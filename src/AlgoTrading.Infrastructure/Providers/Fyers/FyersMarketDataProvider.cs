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

        using var response = await httpClient.SendAsync(request, cancellationToken);
        string json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
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
