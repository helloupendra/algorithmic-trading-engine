using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.Providers;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Providers.TrueData;

/// <summary>
/// The data side of the TrueData connector: historical bars, and nothing else.
/// It does not touch the database — fetching and persisting are separate jobs.
/// </summary>
public class TrueDataMarketDataProvider : IMarketDataProvider
{
    private readonly TrueDataTokenStore _tokens;
    private readonly ISymbolMapper _symbolMapper;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TrueDataSettings _settings;
    private readonly ILogger<TrueDataMarketDataProvider> _logger;

    public TrueDataMarketDataProvider(
        IOptions<TrueDataSettings> settings,
        TrueDataTokenStore tokens,
        ISymbolMapper symbolMapper,
        IHttpClientFactory httpClientFactory,
        ILogger<TrueDataMarketDataProvider> logger)
    {
        _settings = settings.Value;
        _tokens = tokens;
        _symbolMapper = symbolMapper;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public ProviderDescriptor Descriptor => TrueDataProvider.Descriptor;

    public async Task<IReadOnlyList<ProviderHistoryBar>> GetHistoryAsync(
        string canonicalSymbol,
        string resolution,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string vendorSymbol = await ResolveVendorSymbolAsync(canonicalSymbol, cancellationToken);

        string url = QueryHelpers.AddQueryString(
            $"{_settings.HistoryBaseUrl.TrimEnd('/')}/getbars",
            new Dictionary<string, string?>
            {
                ["symbol"] = vendorSymbol,
                // Both stamps are the exchange's clock, because every timestamp
                // the vendor returns is too.
                ["from"] = TrueDataSymbols.ToRequestStamp(fromUtc),
                ["to"] = TrueDataSymbols.ToRequestStamp(toUtc),
                ["interval"] = TrueDataSymbols.ToInterval(resolution),
                ["response"] = "json",
            });

        string body = await SendAsync(url, cancellationToken);
        return Parse(body, canonicalSymbol, vendorSymbol);
    }

    /// <summary>
    /// The vendor's name for this instrument: a mapping row when one exists,
    /// otherwise the grammar rules.
    /// </summary>
    /// <remarks>
    /// Rows win. They were written from TrueData's own master file, so they are
    /// the only thing that can answer for a future ("CRUDEOIL-I" is whichever
    /// contract is nearest today) or a monthly option (whose canonical name
    /// carries a month but not the expiry day).
    /// </remarks>
    private async Task<string> ResolveVendorSymbolAsync(string canonicalSymbol, CancellationToken cancellationToken)
    {
        string mapped = await _symbolMapper.ToVendorAsync(canonicalSymbol, TrueDataProvider.Key, cancellationToken);

        // The mapper hands back the canonical string when it has no row, which
        // for this vendor means "no answer" rather than "the same name".
        if (!string.Equals(mapped, canonicalSymbol, StringComparison.OrdinalIgnoreCase))
            return mapped;

        string? lexical = TrueDataSymbols.ToVendor(canonicalSymbol);
        if (!string.IsNullOrWhiteSpace(lexical)) return lexical;

        throw new ProviderSymbolRejectedException(
            TrueDataProvider.Key,
            canonicalSymbol,
            "no TrueData name is known for this instrument — import the symbol master so the mapping exists");
    }

    private async Task<string> SendAsync(string url, CancellationToken cancellationToken)
    {
        var http = _httpClientFactory.CreateClient(TrueDataProvider.Key);

        // Two attempts, and only for a rejected token. Everything else is the
        // answer: retrying a refusal just turns one wrong result into several.
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            string token = await _tokens.GetTokenAsync(cancellationToken);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await http.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 1)
            {
                // A token can die mid-backfill. This class can fix that itself,
                // so it does, rather than failing a sweep that would then leave
                // exactly the gap it was run to fill.
                _logger.LogInformation("TrueData rejected the token; signing in again.");
                _tokens.Invalidate();
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"TrueData history failed ({(int)response.StatusCode}): {Trim(body)}");
            }

            return body;
        }

        throw new InvalidOperationException("TrueData rejected the token twice in a row.");
    }

    /// <summary>
    /// {"status":"Success","Records":[[time, open, high, low, close, volume, oi], …]}
    /// with the time in IST and no offset on it.
    /// </summary>
    private IReadOnlyList<ProviderHistoryBar> Parse(string body, string canonicalSymbol, string vendorSymbol)
    {
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        string status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
        if (!status.Equals("Success", StringComparison.OrdinalIgnoreCase))
        {
            // TrueData answers a symbol it does not list with a status, not an
            // HTTP error. That is one symbol's problem, so the caller can skip
            // it and keep the sweep going.
            throw new ProviderSymbolRejectedException(
                TrueDataProvider.Key, canonicalSymbol, $"{status}: {Trim(body)}");
        }

        if (!root.TryGetProperty("Records", out var records) || records.ValueKind != JsonValueKind.Array)
            return Array.Empty<ProviderHistoryBar>();

        var bars = new List<ProviderHistoryBar>(records.GetArrayLength());
        int malformed = 0;

        foreach (var row in records.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 5) { malformed++; continue; }

            if (!DateTime.TryParse(
                    row[0].GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var ist))
            {
                malformed++;
                continue;
            }

            long oi = row.GetArrayLength() >= 7 ? ReadLong(row[6]) : 0;

            bars.Add(new ProviderHistoryBar
            {
                TimestampUtc = IstTime.FromIst(ist),
                Open = ReadDecimal(row[1]),
                High = ReadDecimal(row[2]),
                Low = ReadDecimal(row[3]),
                Close = ReadDecimal(row[4]),
                Volume = row.GetArrayLength() >= 6 ? ReadDecimal(row[5]) : 0m,

                // An index has no open interest and the feed sends zero for it.
                // Zero is indistinguishable from "every position closed", which
                // is a claim worth not making: it is reported as unknown.
                OpenInterest = oi > 0 ? oi : null,
            });
        }

        if (malformed > 0)
        {
            _logger.LogWarning(
                "TrueData returned {Malformed} unreadable row(s) for {Vendor} ({Canonical}); the rest were kept.",
                malformed, vendorSymbol, canonicalSymbol);
        }

        return bars;
    }

    private static decimal ReadDecimal(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.TryGetDecimal(out var d) ? d : 0m,
        JsonValueKind.String => decimal.TryParse(e.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var p) ? p : 0m,
        _ => 0m,
    };

    private static long ReadLong(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : 0L,
        JsonValueKind.String => long.TryParse(e.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var p) ? p : 0L,
        _ => 0L,
    };

    private static string Trim(string body) =>
        string.IsNullOrWhiteSpace(body) ? "no message" : (body.Length > 200 ? body[..200] : body);
}
