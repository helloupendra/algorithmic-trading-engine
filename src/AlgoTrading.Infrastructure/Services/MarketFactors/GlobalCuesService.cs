using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Infrastructure.Services.MarketFactors;

/// <summary>One line of the global cues panel.</summary>
public sealed class GlobalCueItem
{
    public string Symbol { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Group { get; init; } = string.Empty;
    public string? Currency { get; set; }
    public decimal? LastPrice { get; set; }
    public decimal? PreviousClose { get; set; }
    public decimal? Change { get; set; }
    public decimal? ChangePercent { get; set; }
    public DateTime? AsOfUtc { get; set; }
    public string? Error { get; set; }
}

/// <summary>GIFT Nifty and the overseas markets an Indian desk reads before and during the session.</summary>
public sealed class GlobalCuesSnapshot
{
    public DateTime FetchedUtc { get; init; }
    public GlobalCueItem Gift { get; init; } = new();
    public DateOnly? GiftExpiry { get; set; }
    public long? GiftContractsTraded { get; set; }
    public IReadOnlyList<GlobalCueItem> Markets { get; init; } = [];

    /// <summary>Said on the page, because neither source is an official data feed.</summary>
    public string SourceNote { get; init; } = string.Empty;
}

/// <summary>
/// Reads GIFT Nifty from NSE IX and overseas markets from Yahoo Finance, and
/// keeps one snapshot for a couple of minutes.
/// </summary>
/// <remarks>
/// Neither is a data feed the platform pays for. NSE IX's website fetches its
/// own prices from <c>/api/market-rate</c> with a short-lived token from
/// <c>/api/generate-token</c>; Yahoo's chart endpoint is public and unofficial.
/// Both can change without notice, so every line carries its own error rather
/// than one failure blanking the panel, and the snapshot is shared by every
/// viewer so opening the page never multiplies the requests.
/// </remarks>
public sealed class GlobalCuesService
{
    public const string HttpClientName = "global-cues";
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(2);

    public static readonly (string Symbol, string Name, string Group)[] Markets =
    [
        ("ES=F", "S&P 500 futures", "United States"),
        ("NQ=F", "Nasdaq 100 futures", "United States"),
        ("YM=F", "Dow Jones futures", "United States"),
        ("^N225", "Nikkei 225", "Asia"),
        ("^HSI", "Hang Seng", "Asia"),
        ("BZ=F", "Brent crude", "Commodities"),
        ("GC=F", "Gold", "Commodities"),
        ("USDINR=X", "US dollar / rupee", "Currency and rates"),
        ("DX-Y.NYB", "US dollar index", "Currency and rates"),
        ("^TNX", "US 10-year yield", "Currency and rates"),
    ];

    private readonly IHttpClientFactory _http;
    private readonly ILogger<GlobalCuesService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GlobalCuesSnapshot? _snapshot;
    private string? _nseIxToken;
    private DateTime _nseIxTokenExpiresUtc;

    public GlobalCuesService(IHttpClientFactory http, ILogger<GlobalCuesService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<GlobalCuesSnapshot> GetAsync(bool force, CancellationToken ct)
    {
        if (!force && _snapshot is not null && DateTime.UtcNow - _snapshot.FetchedUtc < CacheFor) return _snapshot;

        await _gate.WaitAsync(ct);
        try
        {
            if (!force && _snapshot is not null && DateTime.UtcNow - _snapshot.FetchedUtc < CacheFor) return _snapshot;

            var gift = new GlobalCueItem { Symbol = "GIFT NIFTY", Name = "GIFT Nifty (near-month future)", Group = "India" };
            DateOnly? giftExpiry = null;
            long? giftContracts = null;
            try
            {
                var quote = await GiftNiftyAsync(ct);
                if (quote is null)
                {
                    gift.Error = "NSE IX returned no NIFTY future.";
                }
                else
                {
                    gift.LastPrice = quote.LastPrice;
                    gift.Change = quote.DayChange;
                    gift.ChangePercent = quote.ChangePercent;
                    gift.PreviousClose = quote.DayChange is not null ? quote.LastPrice - quote.DayChange : null;
                    gift.AsOfUtc = quote.AsOfUtc;
                    gift.Currency = "INR";
                    giftExpiry = quote.Expiry;
                    giftContracts = quote.ContractsTraded;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                gift.Error = $"NSE IX: {ex.Message}";
                _nseIxToken = null;
            }

            var markets = new List<GlobalCueItem>();
            var client = _http.CreateClient(HttpClientName);
            foreach (var (symbol, name, group) in Markets)
            {
                var item = new GlobalCueItem { Symbol = symbol, Name = name, Group = group };
                try
                {
                    string url = $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(symbol)}?range=1d&interval=15m";
                    using var response = await client.GetAsync(url, ct);
                    if (!response.IsSuccessStatusCode)
                    {
                        item.Error = $"Yahoo answered HTTP {(int)response.StatusCode}.";
                    }
                    else
                    {
                        var q = MarketFactorParsers.ParseYahooChart(await response.Content.ReadAsStringAsync(ct), name);
                        if (q is null)
                        {
                            item.Error = "Yahoo returned no price.";
                        }
                        else
                        {
                            item.Currency = q.Currency;
                            item.LastPrice = q.LastPrice;
                            item.PreviousClose = q.PreviousClose;
                            item.Change = q.PreviousClose is not null ? q.LastPrice - q.PreviousClose : null;
                            item.ChangePercent = q.PreviousClose is > 0 ? (q.LastPrice - q.PreviousClose) / q.PreviousClose * 100m : null;
                            item.AsOfUtc = q.AsOfUtc;
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    item.Error = $"Yahoo: {ex.Message}";
                }

                markets.Add(item);
                await Task.Delay(120, ct);
            }

            _snapshot = new GlobalCuesSnapshot
            {
                FetchedUtc = DateTime.UtcNow,
                Gift = gift,
                GiftExpiry = giftExpiry,
                GiftContractsTraded = giftContracts,
                Markets = markets,
                SourceNote = "GIFT Nifty from NSE IX's website; other markets from Yahoo Finance. Neither is an official data feed: prices can be delayed and the sources can change without notice.",
            };
            int failures = markets.Count(m => m.Error is not null) + (gift.Error is null ? 0 : 1);
            if (failures > 0) _logger.LogWarning("Global cues: {Failures} of {Total} lines failed.", failures, markets.Count + 1);
            return _snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<GiftNiftyQuote?> GiftNiftyAsync(CancellationToken ct)
    {
        var client = _http.CreateClient(HttpClientName);
        if (_nseIxToken is null || DateTime.UtcNow >= _nseIxTokenExpiresUtc)
        {
            using var tokenResponse = await client.GetAsync("https://www.nseix.com/api/generate-token", ct);
            tokenResponse.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(ct));
            _nseIxToken = doc.RootElement.GetProperty("token").GetString();
            _nseIxTokenExpiresUtc = TokenExpiry(_nseIxToken) ?? DateTime.UtcNow.AddMinutes(4);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.nseix.com/api/market-rate?type=derivatives");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _nseIxToken);
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return MarketFactorParsers.ParseGiftNifty(await response.Content.ReadAsStringAsync(ct), IstTime.DateOf(DateTime.UtcNow));
    }

    /// <summary>A JWT's expiry less a minute's margin; null when it cannot be read.</summary>
    public static DateTime? TokenExpiry(string? jwt)
    {
        try
        {
            var parts = (jwt ?? string.Empty).Split('.');
            if (parts.Length < 2) return null;
            string payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            return doc.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var unix)
                ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.AddMinutes(-1)
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void Configure(HttpClient client)
    {
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }
}
