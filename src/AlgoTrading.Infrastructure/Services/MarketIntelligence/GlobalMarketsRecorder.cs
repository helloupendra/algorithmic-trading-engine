using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services.MarketFactors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Infrastructure.Services.MarketIntelligence;

/// <summary>What one snapshot or daily update did.</summary>
public sealed record GlobalRecordReport(int Stored, int Failed, int Skipped, IReadOnlyList<string> Errors);

/// <summary>
/// Records GIFT Nifty and the overseas markets: a snapshot of every price at
/// the snapshot times, and each market's daily bars.
/// </summary>
/// <remarks>
/// Neither source is a paid feed (see <see cref="GlobalCuesService"/>), so
/// every market is fetched on its own and one failing is a line in the report,
/// logged once until it recovers, never a lost snapshot for the rest.
/// </remarks>
public sealed class GlobalMarketsRecorder
{
    public const string HttpClientName = GlobalCuesService.HttpClientName;

    private readonly TradingDbContext _db;
    private readonly IHttpClientFactory _http;
    private readonly GlobalCuesService _cues;
    private readonly MarketIntelligenceStatus _status;
    private readonly ILogger<GlobalMarketsRecorder> _logger;

    public GlobalMarketsRecorder(TradingDbContext db, IHttpClientFactory http, GlobalCuesService cues,
        MarketIntelligenceStatus status, ILogger<GlobalMarketsRecorder> logger)
    {
        _db = db;
        _http = http;
        _cues = cues;
        _status = status;
        _logger = logger;
    }

    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>The pause between two Yahoo requests; zero in tests.</summary>
    public TimeSpan QuotePause { get; set; } = TimeSpan.FromMilliseconds(150);

    /// <summary>The pause between two history requests, which are larger.</summary>
    public TimeSpan HistoryPause { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>GIFT Nifty, from NSE IX; replaced in tests, which have no NSE IX.</summary>
    public Func<CancellationToken, Task<GiftNiftyQuote?>>? GiftNiftySource { get; set; }

    /// <summary>
    /// One snapshot: GIFT Nifty first (it is the number the 08:45 slot exists
    /// for), then every global key. All rows share one FetchedUtc, so "the
    /// snapshot before 08:50" is one set, and it is the moment the last price
    /// came in: no price is ever stamped earlier than the desk had it.
    /// </summary>
    public async Task<GlobalRecordReport> SnapshotAsync(CancellationToken ct)
    {
        var health = _status.Recorder(MarketIntelligenceNames.QuoteSnapshots);
        var startedUtc = Clock();
        health.Attempted(startedUtc);
        var rows = new List<MarketQuoteSnapshot>();
        var errors = new List<string>();

        try
        {
            var gift = await (GiftNiftySource ?? _cues.GiftNiftyNowAsync)(ct);
            if (gift is null) throw new InvalidOperationException("NSE IX listed no NIFTY future");
            rows.Add(new MarketQuoteSnapshot
            {
                Key = GlobalMarketKeys.GiftNifty,
                Price = gift.LastPrice,
                PreviousClose = gift.DayChange is decimal change ? gift.LastPrice - change : null,
                ChangePct = gift.ChangePercent,
                AsOfUtc = gift.AsOfUtc,
                Source = "nseix",
            });
            health.LogSourceRecovery(_logger, GlobalMarketKeys.GiftNifty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errors.Add($"{GlobalMarketKeys.GiftNifty}: {ex.Message}");
            health.LogSourceFailure(_logger, GlobalMarketKeys.GiftNifty, ex.Message, Clock());
        }

        var client = _http.CreateClient(HttpClientName);
        foreach (var market in GlobalMarketKeys.All)
        {
            try
            {
                using var response = await client.GetAsync(GlobalMarketKeys.QuoteUrl(market.YahooSymbol), ct);
                if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
                var quote = MarketFactorParsers.ParseYahooChart(await response.Content.ReadAsStringAsync(ct), market.Name)
                    ?? throw new FormatException("no price in the answer");

                rows.Add(new MarketQuoteSnapshot
                {
                    Key = market.Key,
                    Price = quote.LastPrice,
                    PreviousClose = quote.PreviousClose,
                    ChangePct = YahooDailyBars.ChangePct(quote.LastPrice, quote.PreviousClose),
                    AsOfUtc = quote.AsOfUtc,
                    Source = $"yahoo:{market.YahooSymbol}",
                });
                health.LogSourceRecovery(_logger, market.Key);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"{market.Key}: {ex.Message}");
                health.LogSourceFailure(_logger, market.Key, ex.Message, Clock());
            }

            if (QuotePause > TimeSpan.Zero) await Task.Delay(QuotePause, ct);
        }

        var fetchedUtc = Clock();
        foreach (var row in rows) row.FetchedUtc = fetchedUtc;
        _db.MarketQuoteSnapshots.AddRange(rows);
        await _db.SaveChangesAsync(ct);

        int total = GlobalMarketKeys.All.Count + 1;
        health.Finished(Clock(), $"{rows.Count} of {total} price(s) at {IstTime.ShortStamp(startedUtc)} IST",
            rows.Count == 0 ? "every source failed" : null);
        return new GlobalRecordReport(rows.Count, errors.Count, 0, errors);
    }

    /// <summary>
    /// Brings every market's daily bars up to date: the last ten days again
    /// for a market that has rows (a revised close replaces the stored one),
    /// or its whole history from 2020 for one that has none, when
    /// <paramref name="allowHistory"/> says the backfill may run now.
    /// </summary>
    /// <remarks>
    /// Only bars that are surely over are written
    /// (<see cref="MarketIntelligenceSchedule.LatestFinalOverseasDate"/>), so a
    /// half-formed day never sits in the table.
    /// </remarks>
    public async Task<GlobalRecordReport> UpdateDailyAsync(bool allowHistory, CancellationToken ct)
    {
        var health = _status.Recorder(MarketIntelligenceNames.GlobalDaily);
        var now = Clock();
        health.Attempted(now);
        var lastFinal = MarketIntelligenceSchedule.LatestFinalOverseasDate(now);

        var newest = await _db.MarketGlobalDaily.AsNoTracking()
            .GroupBy(x => x.Symbol)
            .Select(g => new { Symbol = g.Key, Last = g.Max(x => x.Date) })
            .ToDictionaryAsync(x => x.Symbol, x => x.Last, ct);

        int stored = 0, skipped = 0;
        var errors = new List<string>();
        var client = _http.CreateClient(HttpClientName);
        bool first = true;
        foreach (var market in GlobalMarketKeys.All)
        {
            DateOnly from;
            if (newest.TryGetValue(market.Key, out var last))
            {
                if (last >= lastFinal) { skipped++; continue; }
                from = last.AddDays(-10);
            }
            else if (allowHistory)
            {
                from = GlobalMarketKeys.HistoryFrom;
            }
            else
            {
                // Its history waits for the backfill window.
                skipped++;
                continue;
            }

            if (!first && HistoryPause > TimeSpan.Zero) await Task.Delay(HistoryPause, ct);
            first = false;

            try
            {
                using var response = await client.GetAsync(GlobalMarketKeys.ChartUrl(market.YahooSymbol, from, now), ct);
                if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
                var bars = YahooDailyBars.Parse(await response.Content.ReadAsStringAsync(ct))
                    .Where(b => b.Date >= from && b.Date >= GlobalMarketKeys.HistoryFrom && b.Date <= lastFinal)
                    .ToList();

                stored += await UpsertAsync(market, bars, Clock(), ct);
                health.LogSourceRecovery(_logger, market.Key);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _db.ChangeTracker.Clear();
                errors.Add($"{market.Key}: {ex.Message}");
                health.LogSourceFailure(_logger, market.Key, ex.Message, Clock());
            }
        }

        health.Finished(Clock(), $"{stored} bar(s) written, {errors.Count} market(s) failed, {skipped} up to date or waiting",
            errors.Count > 0 && errors.Count == GlobalMarketKeys.All.Count - skipped ? "every market failed" : null);
        return new GlobalRecordReport(stored, errors.Count, skipped, errors);
    }

    private async Task<int> UpsertAsync(GlobalMarket market, IReadOnlyList<DailyBar> bars, DateTime fetchedUtc, CancellationToken ct)
    {
        if (bars.Count == 0) return 0;

        var from = bars[0].Date;
        var existing = await _db.MarketGlobalDaily
            .Where(x => x.Symbol == market.Key && x.Date >= from)
            .ToDictionaryAsync(x => x.Date, ct);

        foreach (var bar in bars)
        {
            if (!existing.TryGetValue(bar.Date, out var row))
            {
                row = new MarketGlobalDaily { Symbol = market.Key, Date = bar.Date };
                _db.MarketGlobalDaily.Add(row);
            }

            row.Open = bar.Open;
            row.High = bar.High;
            row.Low = bar.Low;
            row.Close = bar.Close;
            row.Volume = bar.Volume;
            row.Source = $"yahoo:{market.YahooSymbol}";
            row.FetchedUtc = fetchedUtc;
        }

        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        return bars.Count;
    }
}
