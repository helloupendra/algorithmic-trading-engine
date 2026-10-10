using System.Globalization;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Services.Replay;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Domain.Enums;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services.AiAgents;

/// <summary>One data problem the code found: what was measured, what reads that data, and the fixes allowed for it.</summary>
/// <param name="Id">Stable for the same problem on the same day, so a model's answer can be held to the list.</param>
/// <param name="Kind">One of <see cref="DataCuratorChecks.Kinds"/>.</param>
/// <param name="Subject">What it is about, in a few words ("NIFTY 50, 1-minute candles").</param>
/// <param name="Facts">What the check measured, in words with its numbers: the only numbers the model may cite.</param>
/// <param name="ReadBy">What on the desk reads that data, so "what breaks" names something real.</param>
/// <param name="Actions">The fixes allowed for it, keys of <see cref="DataCuratorChecks.Actions"/>.</param>
public sealed record DataFinding(string Id, string Kind, string Subject, string Facts, IReadOnlyList<string> ReadBy, IReadOnlyList<string> Actions);

/// <summary>What one pass checked and found.</summary>
/// <param name="Pass">night (after the archive of <paramref name="CheckedDay"/>) or morning (before the open of <paramref name="Today"/>).</param>
/// <param name="CheckedDay">The trading day whose recorded data was checked.</param>
/// <param name="Today">The IST day the pass ran for, whose expiries were checked.</param>
/// <param name="Checked">Each check that ran, in words, findings or not.</param>
public sealed record DataCheckRun(string Pass, DateOnly CheckedDay, DateOnly Today, IReadOnlyList<string> Checked, IReadOnlyList<DataFinding> Findings);

/// <summary>
/// The Data Curator's checks: plain code over what the desk recorded, each bounded to one day and a handful of
/// symbols, on indexes that exist. The model never looks at the data; it triages these findings.
/// </summary>
/// <remarks>
/// <para>
/// What is checked, for the trading day just finished (the night pass) or the one before today (the morning pass):
/// </para>
/// <list type="bullet">
/// <item>the archive's marker (<c>archive.candles.lastDay</c>) has reached the day;</item>
/// <item>the index candles at 1 and 5 minutes against the day's session, by the backfills' coverage rule
/// (<see cref="VixBackfillPlan.Classify"/>: 90% and a bar near the open and the close);</item>
/// <item>the 1-minute live bars of the indices a replay plays (<see cref="MarketReplayService.CoverageSymbols"/>), as
/// the Replay days list counts them;</item>
/// <item>India VIX days the nightly check could not fill (<see cref="SystemSettingKeys.VixGapsReported"/>);</item>
/// <item>the option chain's captures minute by minute, for the poller's underlyings and the expiry it captured most;</item>
/// <item>the feeds' heartbeats: none during the session, one that stopped mid-session, one reporting an error;</item>
/// <item>recorded indices, stocks and futures whose quote did not move in the session;</item>
/// <item>contracts still recorded after their expiry;</item>
/// <item>for today: the instrument master's next index option expiry, against the exchange calendar and the
/// expiry rules.</item>
/// </list>
/// <para>
/// Nothing here scans a table: each query names its symbol (or underlying and expiry) and a one-day window, on the
/// indexes those tables have for exactly these reads. The live desk shares the box.
/// </para>
/// </remarks>
public sealed class DataCuratorChecks(TradingDbContext db, IMarketSessionService sessions)
{
    public const string NightPass = "night";
    public const string MorningPass = "morning";

    /// <summary>The kinds of finding, as the report and the console group them.</summary>
    public static readonly IReadOnlyList<string> Kinds =
        ["archive", "candles", "replay", "vix", "chain", "feed", "quotes", "watchlist", "expiry"];

    /// <summary>The fixes the model may choose from, and what each means to the owner.</summary>
    public static readonly IReadOnlyDictionary<string, string> Actions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["rerun-archive"] = "Archive the day again: POST /api/Backfill/archive?day=…",
        ["run-vix-check"] = "Run the India VIX check again: POST /api/Backfill/vix?through=…",
        ["backfill-from-broker"] = "Backfill the index candles from the broker (Research → Backtests → Backfill)",
        ["import-dhan-history"] = "Import the days from Dhan's index history (tools/index_history_download.py, then index_history_import.py)",
        ["restart-feed"] = "Restart the feed (Data → Feeds) and check its token",
        ["check-chain-poller"] = "Check the option chain poller ran all session and on the right expiry",
        ["refresh-instrument-master"] = "Refresh the instrument masters (Data → Instruments)",
        ["fix-expiry-rule"] = "Correct the expiry rule's weekday",
        ["check-holiday-calendar"] = "Check the exchange calendar: the day may be a holiday it is missing",
        ["prune-watchlist"] = "Prune expired contracts from the recording list (POST /api/LiveData/watchlist/prune)",
        ["none"] = "Nothing to do: expected, or already healing",
    };

    /// <summary>The index option underlyings whose next expiry is checked, with the exchange that lists them.</summary>
    public static readonly IReadOnlyList<(string Exchange, string Underlying)> ExpiryUnderlyings =
        [("NSE", "NIFTY"), ("NSE", "BANKNIFTY"), ("BSE", "SENSEX")];

    /// <summary>Fewer missing bars than this at 1 minute (2 at 5 minutes) on a usable day is not worth a finding.</summary>
    public const int MinorMissingOneMinute = 5;

    /// <summary>A chain capture gap shorter than this many minutes is not listed.</summary>
    public const int ChainGapMinutes = 3;

    private static readonly string[] CandleSymbols = ["NIFTY", "BANKNIFTY", "SENSEX", "INDIAVIX"];

    private static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["NIFTY"] = "NIFTY 50",
        ["BANKNIFTY"] = "NIFTY BANK",
        ["SENSEX"] = "SENSEX",
        ["INDIAVIX"] = "India VIX",
    };

    /// <summary>Runs every check of a pass. <paramref name="chainUnderlyings"/> are the chain poller's underlyings.</summary>
    public async Task<DataCheckRun> RunAsync(string pass, DateOnly checkedDay, DateOnly today, DateTime nowUtc,
        IReadOnlyList<string> chainUnderlyings, CancellationToken cancellationToken)
    {
        var findings = new List<DataFinding>();
        var checkedList = new List<string>();

        await ArchiveAsync(checkedDay, findings, checkedList, cancellationToken);
        var vixGaps = await VixGapsAsync(findings, checkedList, cancellationToken);
        await CandlesAsync(checkedDay, vixGaps, findings, checkedList, cancellationToken);
        await ReplayMinutesAsync(checkedDay, findings, checkedList, cancellationToken);
        foreach (var underlying in chainUnderlyings) await ChainAsync(underlying, checkedDay, findings, checkedList, cancellationToken);
        await FeedsAsync(checkedDay, nowUtc, findings, checkedList, cancellationToken);
        await StaleQuotesAsync(checkedDay, findings, checkedList, cancellationToken);
        await ExpiredRecordedAsync(today, findings, checkedList, cancellationToken);
        foreach (var (exchange, underlying) in ExpiryUnderlyings) await ExpiryAsync(exchange, underlying, today, findings, checkedList, cancellationToken);

        return new DataCheckRun(pass, checkedDay, today, checkedList, findings);
    }

    // ---------- the archive ----------

    private async Task ArchiveAsync(DateOnly day, List<DataFinding> findings, List<string> checkedList, CancellationToken cancellationToken)
    {
        string? value = await Setting(NightlyArchiveService.LastArchivedDayKey, cancellationToken);
        DateOnly? last = DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        checkedList.Add($"The nightly candle archive's last finished day ({Iso(last)}).");
        if (last >= day) return;

        findings.Add(new DataFinding($"archive:{Iso(day)}", "archive", "The nightly candle archive",
            $"The archive's last finished day is {(last is null ? "not recorded" : Iso(last))}; {Iso(day)} has not been archived, so its candles are only in live_bars.",
            ["backtests and charts reading candles", "the India VIX check, which runs after the archive"],
            ["rerun-archive", "none"]));
    }

    // ---------- India VIX ----------

    private async Task<HashSet<DateOnly>> VixGapsAsync(List<DataFinding> findings, List<string> checkedList, CancellationToken cancellationToken)
    {
        var gaps = VixBackfillPlan.ParseDays(await Setting(SystemSettingKeys.VixGapsReported, cancellationToken));
        checkedList.Add("India VIX days the nightly check could not fill.");
        foreach (var gap in gaps.OrderBy(g => g))
        {
            findings.Add(new DataFinding($"vix:{Iso(gap)}", "vix", "India VIX",
                $"India VIX has a gap on {Iso(gap)} that the nightly check asked the vendors for and could not fill; the forecasts drop that day.",
                ["range.har-vix and the logit forecasts (the day after a gap)", "the AI Trader's brief (India VIX)"],
                ["import-dhan-history", "run-vix-check", "check-holiday-calendar"]));
        }

        return gaps;
    }

    // ---------- candles ----------

    private async Task CandlesAsync(DateOnly day, HashSet<DateOnly> vixGaps, List<DataFinding> findings, List<string> checkedList, CancellationToken cancellationToken)
    {
        foreach (var key in CandleSymbols)
        {
            string symbol = MarketReplayService.CoverageSymbols[key];
            var (open, close, trading) = Session(symbol, day);
            if (!trading) continue;
            foreach (int minutes in new[] { 1, 5 })
            {
                string resolution = ResolutionCodes.ToCandle(minutes.ToString(CultureInfo.InvariantCulture));
                var stored = (await db.Candles.AsNoTracking()
                        .Where(c => c.Symbol == symbol && c.Resolution == resolution && c.TimeStampUtc >= open && c.TimeStampUtc < close)
                        .Select(c => c.TimeStampUtc)
                        .Distinct()
                        .ToListAsync(cancellationToken))
                    .Select(t => DateTime.SpecifyKind(t, DateTimeKind.Utc))
                    .ToHashSet();
                var expected = VixBackfillPlan.ExpectedBarStarts(open, close, minutes);
                var coverage = VixBackfillPlan.Classify(day, minutes, expected, stored);
                checkedList.Add($"{Names[key]} {minutes}-minute candles for {Iso(day)}: {coverage.Present} of {coverage.Expected}.");

                if (coverage.State == VixDayState.Complete) continue;
                if (coverage.State == VixDayState.Holes && coverage.Missing < (minutes == 1 ? MinorMissingOneMinute : 2)) continue;
                // A VIX day the nightly check already reported is one finding, not three.
                if (key == "INDIAVIX" && vixGaps.Contains(day)) continue;

                var present = expected.Where(stored.Contains).ToList();
                string span = present.Count == 0 ? "none stored" : $"first at {Clock(present[0])}, last at {Clock(present[^1])}";
                string state = coverage.State == VixDayState.Gap
                    ? "the day does not count: below 90% of the session, or nothing near its open or close"
                    : "the day still counts, a few bars short";
                findings.Add(new DataFinding($"candles:{key}:{minutes}m:{Iso(day)}", "candles", $"{Names[key]}, {minutes}-minute candles",
                    $"{coverage.Present} of {coverage.Expected} {minutes}-minute candles stored for {Iso(day)} ({Percent(coverage.Present, coverage.Expected)}%), {span}; {state}.",
                    CandleReaders(key),
                    coverage.Present == 0
                        ? ["rerun-archive", "backfill-from-broker", "import-dhan-history", "check-holiday-calendar"]
                        : ["rerun-archive", "backfill-from-broker", "import-dhan-history", "none"]));
            }
        }
    }

    private static IReadOnlyList<string> CandleReaders(string key) => key switch
    {
        "INDIAVIX" => ["range.har-vix and the logit forecasts", "the AI Trader's brief (India VIX)"],
        "NIFTY" => ["backtests on NIFTY", "the 08:50 forecasts", "the AI Trader's similar past moments"],
        _ => [$"backtests on {key}", "the AI Trader's similar past moments"],
    };

    // ---------- what a replay plays ----------

    private async Task ReplayMinutesAsync(DateOnly day, List<DataFinding> findings, List<string> checkedList, CancellationToken cancellationToken)
    {
        var (open, close, trading) = Session(MarketReplayService.CoverageSymbols["NIFTY"], day);
        if (!trading) return;

        var short_ = new List<string>();
        int expected = (int)(close - open).TotalMinutes;
        int worst = expected;
        foreach (var (key, symbol) in MarketReplayService.CoverageSymbols)
        {
            var (o, c, t) = Session(symbol, day);
            if (!t) continue;
            var minutes = (await db.LiveBars.AsNoTracking()
                    .Where(b => b.Symbol == symbol && b.Resolution == "1m" && b.BarStartUtc >= o && b.BarStartUtc < c)
                    .Select(b => b.BarStartUtc)
                    .Distinct()
                    .ToListAsync(cancellationToken))
                .Select(m => DateTime.SpecifyKind(m, DateTimeKind.Utc))
                .ToList();
            int want = (int)(c - o).TotalMinutes;
            checkedList.Add($"Minutes a replay of {Iso(day)} plays for {Names[key]}: {minutes.Count} of {want}.");
            if (want - minutes.Count < MinorMissingOneMinute) continue;

            worst = Math.Min(worst, minutes.Count);
            var gaps = Gaps(o, c, minutes.Select(m => Floor(m)).ToHashSet(), 1).OrderByDescending(g => g.Minutes).Take(3).ToList();
            short_.Add($"{Names[key]} {minutes.Count} of {want} minutes ({Percent(minutes.Count, want)}%)" +
                       (gaps.Count == 0 ? string.Empty : $", longest gaps {string.Join(", ", gaps.Select(g => g.Text))}"));
        }

        if (short_.Count == 0) return;
        findings.Add(new DataFinding($"replay:{Iso(day)}", "replay", $"Recorded minutes of {Iso(day)}",
            $"live_bars for {Iso(day)}: {string.Join("; ", short_)}. A replay of the day plays only what was recorded.",
            ["market replays and recap runs of the day", "the AI Trader's lesson checks on its looks that day", "the Trade Reviewer's market block for the day"],
            worst == 0 ? ["restart-feed", "check-holiday-calendar", "none"] : ["restart-feed", "none"]));
    }

    // ---------- the option chain ----------

    private async Task ChainAsync(string underlying, DateOnly day, List<DataFinding> findings, List<string> checkedList, CancellationToken cancellationToken)
    {
        string exchange = underlying == "SENSEX" ? "BSE" : "NSE";
        var info = sessions.GetSessionInfo(IstTime.MiddayUtc(day), exchange, "FO");
        if (!info.IsTradingDay) return;
        var open = Utc(info.SessionOpenUtc);
        var close = Utc(info.SessionCloseUtc);

        var (expiry, minutes) = await CapturedMinutesAsync(underlying, day, open, close, cancellationToken);
        int want = (int)(close - open).TotalMinutes;
        if (minutes.Count == 0)
        {
            // Never captured the day before either: an underlying the poller does not follow, not a gap.
            var previous = PreviousTradingDay(day, exchange);
            if (previous is not DateOnly before) return;
            var p = sessions.GetSessionInfo(IstTime.MiddayUtc(before), exchange, "FO");
            var (_, earlier) = await CapturedMinutesAsync(underlying, before, Utc(p.SessionOpenUtc), Utc(p.SessionCloseUtc), cancellationToken);
            checkedList.Add($"{underlying} option chain captures on {Iso(day)}: none ({earlier.Count} minutes on {Iso(before)}).");
            if (earlier.Count == 0) return;
            findings.Add(new DataFinding($"chain:{underlying}:{Iso(day)}", "chain", $"{underlying} option chain",
                $"No {underlying} option chain was captured on {Iso(day)}; on {Iso(before)} {earlier.Count} of the session's minutes were.",
                ChainReaders(underlying), ["check-chain-poller", "check-holiday-calendar"]));
            return;
        }

        checkedList.Add($"{underlying} option chain captures on {Iso(day)} ({Iso(expiry)} expiry): {minutes.Count} of {want} minutes.");
        var gaps = Gaps(open, close, minutes, ChainGapMinutes).OrderByDescending(g => g.Minutes).Take(5).ToList();
        if (want - minutes.Count < MinorMissingOneMinute && gaps.All(g => g.Minutes < 5)) return;

        findings.Add(new DataFinding($"chain:{underlying}:{Iso(day)}", "chain", $"{underlying} option chain",
            $"The {underlying} chain ({Iso(expiry)} expiry) was captured in {minutes.Count} of {want} session minutes on {Iso(day)} ({Percent(minutes.Count, want)}%)" +
            (gaps.Count == 0 ? "." : $"; longest gaps {string.Join(", ", gaps.Select(g => g.Text))}."),
            ChainReaders(underlying), ["check-chain-poller", "none"]));
    }

    private static IReadOnlyList<string> ChainReaders(string underlying) =>
        [$"ChainFlowBuy and other chain-reading backtests on {underlying} (the historical chain)", "the option chain's OI history", "the AI Trader's brief (the chain) in replays"];

    /// <summary>
    /// The minutes the chain was captured in, for the expiry captured most that day: the master's two nearest
    /// expiries are tried, each one index range (underlying, expiry, time).
    /// </summary>
    private async Task<(DateOnly? Expiry, HashSet<DateTime> Minutes)> CapturedMinutesAsync(string underlying, DateOnly day, DateTime open, DateTime close,
        CancellationToken cancellationToken)
    {
        var expiries = await db.Instruments.AsNoTracking()
            .Where(i => i.Underlying == underlying && (i.InstrumentType == "CE" || i.InstrumentType == "PE")
                        && i.ExpiryDate >= day && i.ExpiryDate <= day.AddDays(40))
            .Select(i => i.ExpiryDate!.Value)
            .Distinct()
            .OrderBy(e => e)
            .Take(2)
            .ToListAsync(cancellationToken);

        DateOnly? best = null;
        var bestMinutes = new HashSet<DateTime>();
        foreach (var expiry in expiries)
        {
            var stamps = await db.OptionChainSnapshots.AsNoTracking()
                .Where(s => s.Underlying == underlying && s.ExpiryDate == expiry && s.CapturedUtc >= open && s.CapturedUtc < close)
                .Select(s => s.CapturedUtc)
                .Distinct()
                .Take(20_000)
                .ToListAsync(cancellationToken);
            var minutes = stamps.Select(s => Floor(DateTime.SpecifyKind(s, DateTimeKind.Utc))).ToHashSet();
            if (minutes.Count > bestMinutes.Count)
            {
                best = expiry;
                bestMinutes = minutes;
            }

            // The nearest expiry captured for most of the session is the one the poller followed: no second read.
            if (bestMinutes.Count * 2 >= (close - open).TotalMinutes) break;
        }

        return (best, bestMinutes);
    }

    // ---------- feeds ----------

    private async Task FeedsAsync(DateOnly day, DateTime nowUtc, List<DataFinding> findings, List<string> checkedList, CancellationToken cancellationToken)
    {
        var since = nowUtc.AddDays(-7);
        var feeds = await db.LiveIngestorStatuses.AsNoTracking()
            .Where(s => s.LastHeartbeatUtc >= since)
            .Select(s => new { s.SourceName, s.Status, s.LastHeartbeatUtc, s.LastError })
            .ToListAsync(cancellationToken);
        checkedList.Add($"Feed heartbeats of the last 7 days ({feeds.Count} feeds).");
        var info = sessions.GetSessionInfo(IstTime.MiddayUtc(day), "NSE", "CM");
        if (feeds.Count == 0)
        {
            if (info.IsTradingDay)
            {
                findings.Add(new DataFinding($"feed:none:{Iso(day)}", "feed", "The live feeds",
                    "No feed has written a heartbeat in the last 7 days.",
                    ["every live strategy run and the AI Trader (prices)", "live_bars and the night's candles"], ["restart-feed"]));
            }

            return;
        }

        if (info.IsTradingDay)
        {
            var open = Utc(info.SessionOpenUtc);
            var close = Utc(info.SessionCloseUtc);
            var latest = feeds.Max(f => Utc(f.LastHeartbeatUtc));
            if (latest < open)
            {
                findings.Add(new DataFinding($"feed:silent:{Iso(day)}", "feed", "The live feeds",
                    $"No feed's heartbeat was seen in the {Iso(day)} session; the latest of any feed was at {Clock(latest)} on {Iso(IstTime.DateOf(latest))}.",
                    ["every live strategy run and the AI Trader (prices)", "live_bars and the night's candles"], ["restart-feed", "check-holiday-calendar"]));
            }

            foreach (var f in feeds.Where(f => Utc(f.LastHeartbeatUtc) >= open.AddMinutes(15) && Utc(f.LastHeartbeatUtc) < close.AddMinutes(-10)))
            {
                findings.Add(new DataFinding($"feed:stopped:{Slug(f.SourceName)}:{Iso(day)}", "feed", $"Feed {f.SourceName}",
                    $"Feed {f.SourceName} wrote its last heartbeat at {Clock(Utc(f.LastHeartbeatUtc))} on {Iso(day)}, before the {Clock(close)} close, and none since.",
                    ["live runs priced from this feed", "live_bars for its symbols"], ["restart-feed", "none"]));
            }
        }

        foreach (var f in feeds.Where(f => nowUtc - Utc(f.LastHeartbeatUtc) <= TimeSpan.FromDays(1)
                                           && (string.Equals(f.Status, "Error", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(f.LastError))))
        {
            string error = IncidentRedaction.Mask(Cut(f.LastError, 200) ?? "no message");
            findings.Add(new DataFinding($"feed:error:{Slug(f.SourceName)}", "feed", $"Feed {f.SourceName}",
                $"Feed {f.SourceName} is {f.Status} and its last heartbeat ({Clock(Utc(f.LastHeartbeatUtc))} on {Iso(IstTime.DateOf(Utc(f.LastHeartbeatUtc)))}) said: {error}",
                ["live runs priced from this feed", "live_bars for its symbols"], ["restart-feed", "none"]));
        }
    }

    // ---------- quotes ----------

    private async Task StaleQuotesAsync(DateOnly day, List<DataFinding> findings, List<string> checkedList, CancellationToken cancellationToken)
    {
        var nse = sessions.GetSessionInfo(IstTime.MiddayUtc(day), "NSE", "CM");
        if (!nse.IsTradingDay) return;

        // Indices, stocks and futures tick all session; a far strike may not trade all day, so options are left out.
        var symbols = await db.LiveWatchlistItems.AsNoTracking()
            .Where(w => w.IsActive && (w.Symbol.EndsWith("-INDEX") || w.Symbol.EndsWith("-EQ") || w.Symbol.EndsWith("FUT")))
            .Select(w => w.Symbol)
            .Distinct()
            .Take(500)
            .ToListAsync(cancellationToken);
        if (symbols.Count == 0) return;
        var quotes = await db.LiveQuotesLatest.AsNoTracking()
            .Where(q => symbols.Contains(q.Symbol))
            .Select(q => new { q.Symbol, q.UpdatedUtc, q.ExchangeTimestampUtc })
            .ToListAsync(cancellationToken);
        var expired = await ExpiredAsync(symbols, day, cancellationToken);

        var stale = new List<string>();
        foreach (var symbol in symbols.Where(s => !expired.Contains(s)).OrderBy(s => s, StringComparer.Ordinal))
        {
            var (open, _, trading) = Session(symbol, day);
            if (!trading) continue;
            var q = quotes.FirstOrDefault(x => x.Symbol == symbol);
            var at = q is null ? (DateTime?)null : Utc(q.ExchangeTimestampUtc ?? q.UpdatedUtc);
            if (at is null || at < open) stale.Add(symbol);
        }

        checkedList.Add($"Quotes of {symbols.Count} recorded indices, stocks and futures in the {Iso(day)} session.");
        if (stale.Count == 0) return;
        findings.Add(new DataFinding($"quotes:{Iso(day)}", "quotes", "Recorded quotes",
            $"{stale.Count} of {symbols.Count} recorded indices, stocks and futures had no quote in the {Iso(day)} session: " +
            string.Join(", ", stale.Take(10)) + (stale.Count > 10 ? $" and {stale.Count - 10} more" : string.Empty) + ".",
            ["paper fills and marks priced from live_quotes_latest", "the market pulse and the watchlist"],
            ["restart-feed", "prune-watchlist", "none"]));
    }

    private async Task ExpiredRecordedAsync(DateOnly today, List<DataFinding> findings, List<string> checkedList, CancellationToken cancellationToken)
    {
        var recorded = await db.LiveWatchlistItems.AsNoTracking().Where(w => w.IsActive).Select(w => w.Symbol).Distinct().Take(5000).ToListAsync(cancellationToken);
        var expired = (await ExpiredAsync(recorded, today, cancellationToken)).OrderBy(s => s, StringComparer.Ordinal).ToList();
        checkedList.Add($"Expired contracts among the {recorded.Count} recorded symbols.");
        if (expired.Count == 0) return;
        findings.Add(new DataFinding($"watchlist:{Iso(today)}", "watchlist", "The recording list",
            $"{expired.Count} recorded contracts expired before {Iso(today)}: {string.Join(", ", expired.Take(8))}" +
            (expired.Count > 8 ? $" and {expired.Count - 8} more." : "."),
            ["the feed's subscriptions (a dead contract takes a slot)"], ["prune-watchlist", "none"]));
    }

    /// <summary>The symbols among <paramref name="symbols"/> whose contract expired before <paramref name="day"/>, by the instrument master.</summary>
    private async Task<HashSet<string>> ExpiredAsync(IReadOnlyCollection<string> symbols, DateOnly day, CancellationToken cancellationToken)
    {
        if (symbols.Count == 0) return [];
        var list = symbols.ToList();
        return (await db.Instruments.AsNoTracking()
                .Where(i => list.Contains(i.Symbol) && i.ExpiryDate != null && i.ExpiryDate < day)
                .Select(i => i.Symbol)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
    }

    // ---------- expiries ----------

    private async Task ExpiryAsync(string exchange, string underlying, DateOnly today, List<DataFinding> findings, List<string> checkedList,
        CancellationToken cancellationToken)
    {
        var expiries = await db.Instruments.AsNoTracking()
            .Where(i => i.IsEnabled && i.Underlying == underlying && i.Exchange == exchange && (i.InstrumentType == "CE" || i.InstrumentType == "PE")
                        && i.ExpiryDate >= today && i.ExpiryDate <= today.AddDays(60))
            .Select(i => i.ExpiryDate!.Value)
            .Distinct()
            .OrderBy(e => e)
            .Take(3)
            .ToListAsync(cancellationToken);
        string id = $"expiry:{underlying}:{Iso(today)}";
        string[] readers = [$"strategies trading {underlying} options by nearest expiry", "the AI Trader's contract", "the option chain poller"];
        if (expiries.Count == 0)
        {
            checkedList.Add($"{underlying}'s next option expiry in the instrument master: none in 60 days.");
            findings.Add(new DataFinding(id, "expiry", $"{underlying} expiries",
                $"The instrument master lists no enabled {underlying} option expiring between {Iso(today)} and {Iso(today.AddDays(60))}.",
                readers, ["refresh-instrument-master"]));
            return;
        }

        var next = expiries[0];
        checkedList.Add($"{underlying}'s next option expiry in the instrument master: {Iso(next)} ({next.DayOfWeek}).");
        if (!sessions.GetSessionInfo(IstTime.MiddayUtc(next), exchange, "FO").IsTradingDay)
        {
            findings.Add(new DataFinding(id, "expiry", $"{underlying} expiries",
                $"The instrument master's next {underlying} expiry, {Iso(next)} ({next.DayOfWeek}), is not a trading day in the {exchange} calendar.",
                readers, ["refresh-instrument-master", "check-holiday-calendar"]));
            return;
        }

        var rule = await db.ExpiryRules.AsNoTracking()
            .FirstOrDefaultAsync(r => r.IsEnabled && r.Exchange == exchange && r.Underlying == underlying, cancellationToken);
        if (rule is null) return;
        var expected = ExpectedExpiry(rule, today, d => sessions.GetSessionInfo(IstTime.MiddayUtc(d), exchange, "FO").IsTradingDay);
        if (expected is null || expected == next) return;
        string cadence = rule.HasWeekly ? $"weekly on {rule.WeeklyExpiryDay}" : $"monthly, last {rule.MonthlyExpiryDay}";
        findings.Add(new DataFinding(id, "expiry", $"{underlying} expiries",
            $"The instrument master's next {underlying} expiry is {Iso(next)} ({next.DayOfWeek}); the expiry rule ({cadence}) expects {Iso(expected)} ({expected.Value.DayOfWeek}).",
            readers, ["fix-expiry-rule", "refresh-instrument-master"]));
    }

    /// <summary>
    /// The next expiry an expiry rule expects on or after <paramref name="today"/>: the next weekly weekday, or the
    /// last such weekday of the month for a monthly rule, moved back to the trading day before when it is a holiday.
    /// Null when the rule names no weekday.
    /// </summary>
    public static DateOnly? ExpectedExpiry(ExpiryRule rule, DateOnly today, Func<DateOnly, bool> isTradingDay)
    {
        if (rule.HasWeekly && rule.WeeklyExpiryDay is ExpiryDayOfWeek weekly)
        {
            var target = (DayOfWeek)(int)weekly;
            var d = today;
            while (d.DayOfWeek != target) d = d.AddDays(1);
            for (int i = 0; i < 3; i++, d = d.AddDays(7))
            {
                var shifted = Shift(d, isTradingDay);
                if (shifted >= today) return shifted;
            }

            return null;
        }

        if (rule.HasMonthly && rule.MonthlyExpiryDay is ExpiryDayOfWeek monthly)
        {
            var target = (DayOfWeek)(int)monthly;
            var month = new DateOnly(today.Year, today.Month, 1);
            for (int i = 0; i < 3; i++, month = month.AddMonths(1))
            {
                var d = month.AddMonths(1).AddDays(-1);
                while (d.DayOfWeek != target) d = d.AddDays(-1);
                var shifted = Shift(d, isTradingDay);
                if (shifted >= today) return shifted;
            }
        }

        return null;
    }

    private static DateOnly Shift(DateOnly day, Func<DateOnly, bool> isTradingDay)
    {
        var d = day;
        for (int i = 0; i < 7 && !isTradingDay(d); i++) d = d.AddDays(-1);
        return d;
    }

    // ---------- helpers ----------

    /// <summary>The trading day before <paramref name="day"/> on <paramref name="exchange"/>, at most ten days back.</summary>
    public DateOnly? PreviousTradingDay(DateOnly day, string exchange = "NSE")
    {
        for (var d = day.AddDays(-1); d >= day.AddDays(-10); d = d.AddDays(-1))
        {
            if (sessions.GetSessionInfo(IstTime.MiddayUtc(d), exchange, "FO").IsTradingDay) return d;
        }

        return null;
    }

    /// <summary>A symbol's session on the day: MCX's for an MCX symbol, BSE's for a BSE one, NSE's otherwise.</summary>
    private (DateTime Open, DateTime Close, bool Trading) Session(string symbol, DateOnly day)
    {
        int colon = symbol.IndexOf(':');
        string exchange = colon > 0 ? symbol[..colon].ToUpperInvariant() : "NSE";
        var info = exchange switch
        {
            "MCX" => sessions.GetSessionInfo(IstTime.MiddayUtc(day), "MCX", "COM"),
            "BSE" => sessions.GetSessionInfo(IstTime.MiddayUtc(day), "BSE", "CM"),
            _ => sessions.GetSessionInfo(IstTime.MiddayUtc(day), "NSE", "CM"),
        };
        return (Utc(info.SessionOpenUtc), Utc(info.SessionCloseUtc), info.IsTradingDay);
    }

    private sealed record Gap(DateTime From, int Minutes)
    {
        public string Text => $"{Clock(From)}–{Clock(From.AddMinutes(Minutes))} ({Minutes} min)";
    }

    /// <summary>Runs of at least <paramref name="atLeast"/> session minutes with nothing in <paramref name="have"/>.</summary>
    private static List<Gap> Gaps(DateTime open, DateTime close, HashSet<DateTime> have, int atLeast)
    {
        var gaps = new List<Gap>();
        DateTime? start = null;
        for (var m = open; m < close; m = m.AddMinutes(1))
        {
            if (!have.Contains(m))
            {
                start ??= m;
                continue;
            }

            if (start is DateTime s && (m - s).TotalMinutes >= atLeast) gaps.Add(new Gap(s, (int)(m - s).TotalMinutes));
            start = null;
        }

        if (start is DateTime last && (close - last).TotalMinutes >= atLeast) gaps.Add(new Gap(last, (int)(close - last).TotalMinutes));
        return gaps;
    }

    private async Task<string?> Setting(string key, CancellationToken cancellationToken) =>
        await db.SystemSettings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(cancellationToken);

    private static DateTime Floor(DateTime utc) => new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static string Clock(DateTime utc) => IstTime.ToIst(utc).ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string Iso(DateOnly? day) => day?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "none";

    private static string Percent(int part, int whole) =>
        whole == 0 ? "0" : Math.Round(100m * part / whole, 1, MidpointRounding.AwayFromZero).ToString("0.#", CultureInfo.InvariantCulture);

    private static string Slug(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).Take(40).ToArray());

    private static string? Cut(string? text, int max) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Length <= max ? text.Trim() : text[..(max - 1)].Trim() + "…";
}
