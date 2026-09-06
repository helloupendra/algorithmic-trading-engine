using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.Providers;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Providers.Fyers;

/// <summary>
/// The data side of the FYERS connector: historical bars, and nothing else. It
/// does not touch the database — fetching and persisting are separate jobs, so a
/// second vendor never has to re-implement the upsert logic.
/// </summary>
public class FyersMarketDataProvider : IMarketDataProvider
{
    private readonly IBrokerCredentialsProvider _credentials;
    private readonly IBrokerSessionStore _brokerSessionStore;
    private readonly ISymbolMapper _symbolMapper;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly FyersSettings _settings;
    private readonly ILogger<FyersMarketDataProvider> _logger;

    public FyersMarketDataProvider(
        IOptions<FyersSettings> settings,
        IBrokerCredentialsProvider credentials,
        IBrokerSessionStore brokerSessionStore,
        ISymbolMapper symbolMapper,
        IHttpClientFactory httpClientFactory,
        ILogger<FyersMarketDataProvider> logger)
    {
        _settings = settings.Value;
        _credentials = credentials;
        _brokerSessionStore = brokerSessionStore;
        _symbolMapper = symbolMapper;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public ProviderDescriptor Descriptor => FyersProvider.Descriptor;

    public async Task<IReadOnlyList<ProviderHistoryBar>> GetHistoryAsync(
        string canonicalSymbol,
        string resolution,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var session = await _brokerSessionStore.GetForProviderAsync(FyersProvider.Key, cancellationToken);

        if (session is null || !session.IsAuthenticated || string.IsNullOrWhiteSpace(session.AccessToken))
        {
            throw new InvalidOperationException("No valid FYERS session found. Please authenticate first.");
        }

        // Identity for this connector, but the call is made anyway: the seam is
        // only real if every adapter translates at its own boundary.
        string vendorSymbol = await _symbolMapper.ToVendorAsync(canonicalSymbol, FyersProvider.Key, cancellationToken);

        // FYERS takes the canonical codes ("1", "5", "15", "D"); any spelling
        // the caller used ("5m", "1M") is normalised here.
        string vendorResolution = ResolutionCodes.ToCandle(resolution);

        var queryParams = new Dictionary<string, string?>
        {
            ["symbol"] = vendorSymbol,
            ["resolution"] = vendorResolution,
            ["date_format"] = "0", // epoch seconds
            ["range_from"] = new DateTimeOffset(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc)).ToUnixTimeSeconds().ToString(),
            ["range_to"] = new DateTimeOffset(DateTime.SpecifyKind(toUtc, DateTimeKind.Utc)).ToUnixTimeSeconds().ToString(),
            ["cont_flag"] = "1",
        };

        string url = QueryHelpers.AddQueryString(
            $"{_settings.DataApiBaseUrl.TrimEnd('/')}/data/history",
            queryParams);

        var creds = await _credentials.GetAsync(FyersProvider.Key, cancellationToken: cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", $"{creds.ClientId}:{session.AccessToken}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var httpClient = _httpClientFactory.CreateClient(FyersProvider.Key);

        // FYERS caps history requests per second, and a chain backfill is
        // hundreds of contracts. A refusal there says "ask again shortly", not
        // "this data does not exist" — so it is waited out rather than raised,
        // which would abandon a sweep partway through and leave the gap the
        // sweep existed to fill.
        HttpResponseMessage? response = null;
        string json = string.Empty;

        try
        {
            for (int attempt = 0; ; attempt++)
            {
                response?.Dispose();

                using var attemptRequest = CloneRequest(request);
                response = await httpClient.SendAsync(attemptRequest, cancellationToken);
                json = await response.Content.ReadAsStringAsync(cancellationToken);

                if (response.StatusCode != HttpStatusCode.TooManyRequests ||
                    attempt >= RateLimitRetries)
                {
                    break;
                }

                var wait = RetryDelay(response, attempt);
                _logger.LogDebug(
                    "FYERS rate-limited history for {Symbol}; waiting {Delay} before retry {Attempt}.",
                    vendorSymbol, wait, attempt + 1);
                await Task.Delay(wait, cancellationToken);
            }

            if (!response!.IsSuccessStatusCode)
            {
                ThrowClassified(vendorSymbol, response.StatusCode, ExtractMessage(json) ?? json);
            }

            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            string status = root.TryGetProperty("s", out var statusProp)
                ? statusProp.GetString() ?? string.Empty
                : string.Empty;

            int code = root.TryGetProperty("code", out var codeProp) && codeProp.ValueKind == JsonValueKind.Number
                ? codeProp.GetInt32()
                : 0;

            // "No candles in this window" is an answer, not a failure. FYERS says it
            // with s="no_data" and code=200, and it is the ordinary reply for a
            // strike that has not traded, or a range before the contract listed.
            // Raising it aborts a backfill that is working exactly as intended:
            // sweeping a chain of strikes, most of which are quiet.
            if (IsNoData(status, code))
            {
            _logger.LogDebug(
                "FYERS history for {Symbol} ({Resolution}) {From:o}..{To:o} holds no candles.",
                vendorSymbol, vendorResolution, fromUtc, toUtc);
            return Array.Empty<ProviderHistoryBar>();
        }

        if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase) || code != 200)
        {
            ThrowClassified(vendorSymbol, response.StatusCode, ExtractMessage(json) ?? json);
        }

        if (!root.TryGetProperty("candles", out var candles) || candles.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ProviderHistoryBar>();
        }

        var bars = new List<ProviderHistoryBar>();

        foreach (var candle in candles.EnumerateArray())
        {
            if (candle.ValueKind != JsonValueKind.Array || candle.GetArrayLength() < 6)
                continue;

            bars.Add(new ProviderHistoryBar
            {
                TimestampUtc = DateTimeOffset.FromUnixTimeSeconds(candle[0].GetInt64()).UtcDateTime,
                Open = Convert.ToDecimal(candle[1].GetDouble()),
                High = Convert.ToDecimal(candle[2].GetDouble()),
                Low = Convert.ToDecimal(candle[3].GetDouble()),
                Close = Convert.ToDecimal(candle[4].GetDouble()),
                Volume = Convert.ToDecimal(candle[5].GetDouble()),

                // FYERS history carries no open interest; the descriptor says so
                // rather than letting a caller read a fabricated zero.
                OpenInterest = null,
            });
        }

            _logger.LogDebug(
                "FYERS history for {Symbol} ({Resolution}) {From:o}..{To:o} returned {Count} bars.",
                vendorSymbol, vendorResolution, fromUtc, toUtc, bars.Count);

            return bars;
        }
        finally
        {
            response?.Dispose();
        }
    }

    /// <summary>How many times a rate-limited history call is retried.</summary>
    private const int RateLimitRetries = 5;

    /// <summary>
    /// How long to wait before retrying a rate-limited call: the broker's own
    /// Retry-After when it sends one, otherwise a doubling back-off.
    /// </summary>
    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        var after = response.Headers.RetryAfter;
        if (after?.Delta is { } delta && delta > TimeSpan.Zero) return delta;
        if (after?.Date is { } date)
        {
            var until = date - DateTimeOffset.UtcNow;
            if (until > TimeSpan.Zero) return until;
        }

        return TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt));
    }

    /// <summary>
    /// A fresh copy of the request. An HttpRequestMessage cannot be sent twice,
    /// so a retry needs its own.
    /// </summary>
    private static HttpRequestMessage CloneRequest(HttpRequestMessage source)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri);
        foreach (var header in source.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return clone;
    }

    /// <summary>
    /// Whether the broker answered "there is nothing in that window".
    /// </summary>
    /// <remarks>
    /// The transport succeeded and the request was valid; the range simply
    /// holds no trades. Treated as an empty result so a chain sweep does not
    /// stop at its first quiet strike.
    /// </remarks>
    internal static bool IsNoData(string status, int code)
    {
        // The observed reply is {"candles":[],"message":"","s":"no_data"} — with
        // no "code" field at all, so requiring code==200 here never matched and
        // every quiet strike was raised as a failure. The status alone is
        // definitive: an auth or argument failure comes back as s="error".
        if (status.Equals("no_data", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("nodata", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // A 200 with no status at all is the same answer, less clearly put.
        return status.Length == 0 && code is 200 or 0;
    }

    /// <summary>
    /// Separates "this symbol is not tradable here" from "the call failed".
    /// An expired option contract must skip one contract; a transport or auth
    /// failure must abort the run rather than silently look like empty history.
    /// </summary>
    private static void ThrowClassified(string symbol, HttpStatusCode statusCode, string message)
    {
        bool symbolRejected =
            statusCode == HttpStatusCode.UnprocessableEntity ||
            message.Contains("invalid symbol", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("symbol not found", StringComparison.OrdinalIgnoreCase);

        if (symbolRejected)
        {
            throw new ProviderSymbolRejectedException(FyersProvider.Key, symbol, message);
        }

        throw new InvalidOperationException(
            $"FYERS history API failed for {symbol}. HTTP {(int)statusCode}: {message}");
    }

    private static string? ExtractMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("message", out var message)
                ? message.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
