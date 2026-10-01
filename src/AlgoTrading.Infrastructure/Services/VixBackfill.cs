using System.Globalization;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.Providers;
using AlgoTrading.Domain.MarketData;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>How complete one session of India VIX candles is, at one resolution.</summary>
public enum VixDayState
{
    /// <summary>Every bar of the session is stored.</summary>
    Complete,

    /// <summary>
    /// A few bars are missing, but the day still counts: at least 90% of the
    /// session (<see cref="HistoryCoverage.CompleteFraction"/>, the coverage
    /// rule every backfill uses), with a bar in the session's first five
    /// minutes and one in its last fifteen. The forecasts read such a day.
    /// </summary>
    Holes,

    /// <summary>Anything less. The forecasts drop the day, and the VIX input of the day after it.</summary>
    Gap,
}

/// <summary>One trading day's India VIX candles at one resolution: what a full session holds, and what is stored.</summary>
public sealed record VixDayCoverage(DateOnly Day, int Minutes, int Expected, int Present, VixDayState State)
{
    public int Missing => Expected - Present;

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{Day:yyyy-MM-dd} {Minutes}m {Present}/{Expected}");
}

/// <summary>
/// The nightly India VIX check: which days it looks at, what a complete day is,
/// and how missing days are grouped into requests. Pure, so every rule is a test.
/// </summary>
/// <remarks>
/// <para>
/// Why it exists. On 1 Oct 2026 the forecasts found India VIX missing for 22,
/// 23 and 24 Sep (incident #204). VIX came only from the live feed's universe:
/// it was not on the watchlist, and unlike the index symbols no broker backfill
/// covered it, so while FYERS carried the desk VIX went unrecorded, and nobody
/// noticed for a week. <c>range.har-vix</c> and the logits read VIX's previous
/// close, so one missing day costs the next day's VIX forecasts and a training
/// row. VIX is on the watchlist now; this check is the net under it.
/// </para>
/// <para>
/// A day is measured bar by bar against its own session from the exchange
/// calendar, so a holiday expects nothing and a special session expects its own
/// hours. Any missing bar is asked for; only a day the forecasts could not use
/// (<see cref="VixDayState.Gap"/>) is worth an alert.
/// </para>
/// </remarks>
public static class VixBackfillPlan
{
    public const string Symbol = OptionChainService.VixSymbol;

    /// <summary>The candle resolutions the nightly archive writes for every symbol, and so the ones checked.</summary>
    public static readonly IReadOnlyList<int> Resolutions = [1, 5, 15];

    /// <summary>About a month of sessions: a gap a person finds a week or two later is still filled by itself.</summary>
    public const int DefaultLookbackTradingDays = 20;

    /// <summary>The most a configuration may ask for; three months of 1-minute bars is still one request a vendor answers.</summary>
    public const int MaxLookbackTradingDays = 60;

    /// <summary>
    /// The first bar must start this soon after the open and the last this soon
    /// before the close: the analysis module's own rule for a 5-minute day
    /// (first bar by 09:20, last from 15:15; <c>analysis/data.py</c>), because
    /// the session's open and close are what the forecasts read.
    /// </summary>
    public static readonly TimeSpan FirstBarWithin = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan LastBarWithin = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The day the check runs through, given the IST days the archive has just
    /// done: the latest of them that was a trading day, or null when none was
    /// (a weekend or a holiday night), in which case nothing runs.
    /// </summary>
    public static DateOnly? CheckThrough(IEnumerable<DateOnly> archivedDays, Func<DateOnly, bool> isTradingDay)
    {
        DateOnly? latest = null;
        foreach (var day in archivedDays)
        {
            if (isTradingDay(day) && (latest is null || day > latest)) latest = day;
        }
        return latest;
    }

    /// <summary>
    /// The last <paramref name="count"/> trading days up to and including
    /// <paramref name="through"/>, oldest first. The search stops after a
    /// generous number of calendar days, so a calendar that knows no trading
    /// day at all returns fewer rather than looping.
    /// </summary>
    public static IReadOnlyList<DateOnly> TradingDaysThrough(DateOnly through, int count, Func<DateOnly, bool> isTradingDay)
    {
        var days = new List<DateOnly>();
        var floor = through.AddDays(-(count * 2 + 30));
        for (var day = through; days.Count < count && day >= floor; day = day.AddDays(-1))
        {
            if (isTradingDay(day)) days.Add(day);
        }
        days.Reverse();
        return days;
    }

    /// <summary>The start of every bar a session holds at this resolution, in UTC.</summary>
    public static IReadOnlyList<DateTime> ExpectedBarStarts(DateTime openUtc, DateTime closeUtc, int minutes)
    {
        var starts = new List<DateTime>();
        if (minutes <= 0) return starts;
        for (var t = openUtc; t < closeUtc; t = t.AddMinutes(minutes)) starts.Add(t);
        return starts;
    }

    /// <summary>How complete one day is, from the bars it should hold and the stamps stored.</summary>
    public static VixDayCoverage Classify(DateOnly day, int minutes, IReadOnlyList<DateTime> expected, ISet<DateTime> stored)
    {
        int present = 0;
        DateTime? first = null, last = null;
        foreach (var start in expected)
        {
            if (!stored.Contains(start)) continue;
            present++;
            first ??= start;
            last = start;
        }

        VixDayState state;
        if (present == expected.Count)
        {
            state = VixDayState.Complete;
        }
        else
        {
            bool enough = HistoryCoverage.ClassifyDay(present, expected.Count, CoveragePolicy.Continuous) == DayCoverage.Complete;
            bool opens = first is { } f && f <= expected[0] + FirstBarWithin;
            bool closes = last is { } l && l >= expected[^1].AddMinutes(minutes) - LastBarWithin;
            state = enough && opens && closes ? VixDayState.Holes : VixDayState.Gap;
        }

        return new VixDayCoverage(day, minutes, expected.Count, present, state);
    }

    /// <summary>
    /// The days that need a fetch, merged into runs of consecutive trading days
    /// (a weekend or holiday between two of them does not split a run), so one
    /// request covers a stretch rather than one request per day.
    /// </summary>
    /// <param name="tradingDays">The days checked, oldest first.</param>
    public static IReadOnlyList<(DateOnly From, DateOnly To)> FetchRuns(IReadOnlyList<DateOnly> tradingDays, ISet<DateOnly> needing)
    {
        var runs = new List<(DateOnly From, DateOnly To)>();
        DateOnly? from = null, to = null;
        foreach (var day in tradingDays)
        {
            if (needing.Contains(day))
            {
                from ??= day;
                to = day;
                continue;
            }
            if (from is not null) runs.Add((from.Value, to!.Value));
            from = to = null;
        }
        if (from is not null) runs.Add((from.Value, to!.Value));
        return runs;
    }

    /// <summary>
    /// The gap days to alert about tonight: those not already reported on an
    /// earlier night. A gap no vendor can fill would otherwise send the same
    /// message every night for a month.
    /// </summary>
    public static IReadOnlyList<DateOnly> NewGaps(IEnumerable<DateOnly> gapDays, ISet<DateOnly> reported)
        => gapDays.Where(d => !reported.Contains(d)).Distinct().OrderBy(d => d).ToList();

    /// <summary>"2026-09-22,2026-09-23" to dates; anything unreadable is skipped.</summary>
    public static HashSet<DateOnly> ParseDays(string? csv)
    {
        var days = new HashSet<DateOnly>();
        if (string.IsNullOrWhiteSpace(csv)) return days;
        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (DateOnly.TryParseExact(part, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) days.Add(day);
        }
        return days;
    }

    public static string FormatDays(IEnumerable<DateOnly> days)
        => string.Join(",", days.Distinct().OrderBy(d => d).Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
}

/// <summary>What one nightly India VIX check found and did.</summary>
public sealed class VixBackfillResult
{
    public VixBackfillResult(DateOnly through, IReadOnlyList<DateOnly> days)
    {
        Through = through;
        Days = days;
    }

    /// <summary>The last day checked: the archived trading day the check ran after.</summary>
    public DateOnly Through { get; }

    /// <summary>The trading days checked, oldest first.</summary>
    public IReadOnlyList<DateOnly> Days { get; }

    /// <summary>Each day and resolution as found, before anything was fetched.</summary>
    public List<VixDayCoverage> Before { get; } = new();

    /// <summary>Each day and resolution after the fetches.</summary>
    public List<VixDayCoverage> After { get; } = new();

    /// <summary>One line per request: the vendor, the resolution, the days, and what came back or why nothing did.</summary>
    public List<string> Fetches { get; } = new();

    /// <summary>Bars written, all resolutions together.</summary>
    public int BarsFilled { get; set; }

    /// <summary>Days the forecasts still cannot use at some resolution, oldest first.</summary>
    public IReadOnlyList<DateOnly> GapDays
        => After.Where(c => c.State == VixDayState.Gap).Select(c => c.Day).Distinct().OrderBy(d => d).ToList();

    /// <summary>Day and resolution pairs still short of a full session but usable.</summary>
    public IReadOnlyList<VixDayCoverage> Holes => After.Where(c => c.State == VixDayState.Holes).ToList();
}

/// <summary>
/// Fills India VIX candles the nightly archive left short: for the last
/// trading days (20 by default) and every resolution the archive writes, it
/// finds the bars a session should hold and does not, and asks the history
/// vendors for exactly those.
/// </summary>
/// <remarks>
/// <para>
/// Light by design, because it runs on the box the live desk shares: three
/// queries of one symbol's timestamps, and on a night with nothing missing no
/// request at all. Missing days are merged into runs, one request per run and
/// resolution, to each vendor only while bars are still missing.
/// </para>
/// <para>
/// Only missing bars are written. A stored bar — the archive's own from the
/// live feed, or an earlier backfill's — is never replaced, so a rerun writes
/// nothing and a night's check can be run again at any time.
/// </para>
/// <para>
/// The vendors are the history router's chain (FYERS, then the others by rank,
/// or the bindings on the Data Sources page), minus a connector that serves
/// the platform's own table. One that throws is noted and the next is asked.
/// Telling anyone is the caller's job (<c>VixBackfillReport</c> in the API).
/// </para>
/// </remarks>
public sealed class VixBackfillService
{
    private readonly TradingDbContext _db;
    private readonly IProviderRouter _router;
    private readonly IHistoricalCandleStore _store;
    private readonly IMarketSessionService _sessions;
    private readonly ILogger<VixBackfillService> _logger;

    public VixBackfillService(
        TradingDbContext db,
        IProviderRouter router,
        IHistoricalCandleStore store,
        IMarketSessionService sessions,
        ILogger<VixBackfillService> logger)
    {
        _db = db;
        _router = router;
        _store = store;
        _sessions = sessions;
        _logger = logger;
    }

    /// <summary>NSE trades on this IST date (the exchange calendar's answer, holidays and special sessions included).</summary>
    public bool IsTradingDay(DateOnly day) => _sessions.GetSessionInfo(IstTime.MiddayUtc(day), "NSE", "CM").IsTradingDay;

    /// <summary>
    /// Whether <paramref name="day"/>'s NSE session is over at <paramref name="nowUtc"/>:
    /// an earlier day, or today once its own close has passed (a special
    /// session's, such as Muhurat trading in the evening, included). A day with
    /// no session has nothing to wait for.
    /// </summary>
    public bool SessionClosed(DateOnly day, DateTime nowUtc)
    {
        var today = IstTime.DateOf(nowUtc);
        if (day != today) return day < today;

        var session = _sessions.GetSessionInfo(IstTime.MiddayUtc(day), "NSE", "CM");
        return !session.IsTradingDay || session.SessionCloseUtc <= nowUtc;
    }

    /// <summary>Checks the last <paramref name="lookbackTradingDays"/> trading days through <paramref name="through"/> and fills what it can.</summary>
    public async Task<VixBackfillResult> RunAsync(DateOnly through, int lookbackTradingDays, CancellationToken cancellationToken = default)
    {
        int count = Math.Clamp(lookbackTradingDays, 1, VixBackfillPlan.MaxLookbackTradingDays);
        var days = VixBackfillPlan.TradingDaysThrough(through, count, IsTradingDay);
        var result = new VixBackfillResult(through, days);
        if (days.Count == 0) return result;

        var sessions = days.ToDictionary(d => d, Session);
        IReadOnlyList<IMarketDataProvider>? vendors = null;

        foreach (int minutes in VixBackfillPlan.Resolutions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expected = days.ToDictionary(
                d => d, d => VixBackfillPlan.ExpectedBarStarts(sessions[d].OpenUtc, sessions[d].CloseUtc, minutes));
            var stored = await StoredStampsAsync(minutes, days[0], days[^1], cancellationToken);

            var before = days.Select(d => VixBackfillPlan.Classify(d, minutes, expected[d], stored)).ToList();
            result.Before.AddRange(before);

            var needing = before.Where(c => c.State != VixDayState.Complete).Select(c => c.Day).ToHashSet();
            if (needing.Count > 0)
            {
                vendors ??= await VendorsAsync(cancellationToken);
                foreach (var (from, to) in VixBackfillPlan.FetchRuns(days, needing))
                {
                    var missing = days
                        .Where(d => d >= from && d <= to && needing.Contains(d))
                        .SelectMany(d => expected[d])
                        .Where(t => !stored.Contains(t))
                        .ToHashSet();
                    await FillAsync(minutes, from, to, missing, stored, vendors, result, cancellationToken);
                }
            }

            result.After.AddRange(days.Select(d => VixBackfillPlan.Classify(d, minutes, expected[d], stored)));
        }

        return result;
    }

    private (DateTime OpenUtc, DateTime CloseUtc) Session(DateOnly day)
    {
        var info = _sessions.GetSessionInfo(IstTime.MiddayUtc(day), "NSE", "CM");
        return (DateTime.SpecifyKind(info.SessionOpenUtc, DateTimeKind.Utc), DateTime.SpecifyKind(info.SessionCloseUtc, DateTimeKind.Utc));
    }

    /// <summary>The bar starts stored for VIX at one resolution over the days checked: timestamps only.</summary>
    private async Task<HashSet<DateTime>> StoredStampsAsync(int minutes, DateOnly first, DateOnly last, CancellationToken ct)
    {
        string resolution = ResolutionCodes.ToCandle(minutes.ToString(CultureInfo.InvariantCulture));
        var fromUtc = IstTime.StartOfDayUtc(first);
        var toUtc = IstTime.EndOfDayUtc(last);
        var stamps = await _db.Candles.AsNoTracking()
            .Where(c => c.Symbol == VixBackfillPlan.Symbol && c.Resolution == resolution && c.TimeStampUtc >= fromUtc && c.TimeStampUtc <= toUtc)
            .Select(c => c.TimeStampUtc)
            .ToListAsync(ct);
        return stamps.Select(t => DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToHashSet();
    }

    private async Task<IReadOnlyList<IMarketDataProvider>> VendorsAsync(CancellationToken ct)
    {
        var chain = await _router.ResolveDataChainAsync(ProviderCapability.History, cancellationToken: ct);
        // A connector that reads the platform's own candles has nothing this
        // table lacks, and writing its bars back would re-stamp real rows.
        return chain.Where(p => !p.Descriptor.Capabilities.ServesFromPlatformStore).ToList();
    }

    private async Task FillAsync(
        int minutes, DateOnly from, DateOnly to, HashSet<DateTime> missing, HashSet<DateTime> stored,
        IReadOnlyList<IMarketDataProvider> vendors, VixBackfillResult result, CancellationToken ct)
    {
        string resolution = ResolutionCodes.ToCandle(minutes.ToString(CultureInfo.InvariantCulture));
        string span = from == to
            ? from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{from:yyyy-MM-dd}..{to:yyyy-MM-dd}");

        if (vendors.Count == 0)
        {
            result.Fetches.Add($"{minutes}m {span}: no history vendor is available");
            return;
        }

        // Whole IST days as UTC instants; only the missing session bars are kept from the answer.
        var fromUtc = IstTime.StartOfDayUtc(from);
        var toUtc = IstTime.EndOfDayUtc(to);

        foreach (var vendor in vendors)
        {
            if (missing.Count == 0) break;
            string key = vendor.Descriptor.Key;
            int asked = missing.Count;

            IReadOnlyList<ProviderHistoryBar> bars;
            try
            {
                bars = await vendor.GetHistoryAsync(VixBackfillPlan.Symbol, resolution, fromUtc, toUtc, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Fetches.Add($"{key} {minutes}m {span}: failed ({ex.Message})");
                _logger.LogInformation("India VIX backfill: {Vendor} could not serve {Minutes}m for {Span}: {Message}", key, minutes, span, ex.Message);
                continue;
            }

            var fill = bars
                .Where(b => missing.Contains(DateTime.SpecifyKind(b.TimestampUtc, DateTimeKind.Utc)))
                .GroupBy(b => b.TimestampUtc)
                .Select(g => g.First())
                .ToList();

            if (fill.Count > 0)
            {
                try
                {
                    await _store.UpsertAsync(VixBackfillPlan.Symbol, resolution, fill, key, ct);
                }
                catch (DbUpdateException ex)
                {
                    // Someone else wrote one of these bars in between (a backtest's
                    // history sync). Drop what this context holds and let the next
                    // night's look see what is there.
                    _db.ChangeTracker.Clear();
                    result.Fetches.Add($"{key} {minutes}m {span}: {fill.Count} bars not saved ({ex.InnerException?.Message ?? ex.Message})");
                    continue;
                }

                foreach (var bar in fill)
                {
                    var start = DateTime.SpecifyKind(bar.TimestampUtc, DateTimeKind.Utc);
                    missing.Remove(start);
                    stored.Add(start);
                }
                result.BarsFilled += fill.Count;
            }

            result.Fetches.Add($"{key} {minutes}m {span}: {bars.Count} bars returned, {fill.Count} of {asked} missing filled");
        }
    }
}
