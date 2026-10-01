using System.Globalization;
using System.Text;
using AlgoTrading.Api.Services.AiTrader;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services.AiAgents;

/// <summary>
/// The AI's one Telegram message a day, to the desk's system channel: the AI Trader's day and the run reviews
/// written since the last digest. The owner reads the desk on Today and wants one message a day from the AI,
/// never one per item.
/// </summary>
/// <remarks>
/// <para>
/// Due after the NSE close and <see cref="AiSettings.ReviewAfterIst"/> (15:45), by when the AI Trader's shadow
/// book has been squared off at 15:30. While the Trade Reviewer is on it also waits for the reviews it still owes,
/// a failed one with tries left included, for at most <see cref="ReviewWait"/>. A review written after the digest
/// (an MCX run's, after the MCX close) goes in the next one.
/// </para>
/// <para>
/// Once a day: the time it went and the day it was for are kept in system settings before it is sent, so neither a
/// restart nor the next tick sends it again. On a day with no NSE session it goes only if the AI Trader looked that
/// day. Replay decisions and replay shadow positions are never in it. A section with nothing to say is left out, and
/// nothing is sent when neither has anything; such a day is kept as empty.
/// </para>
/// <para>
/// A day missed (the API down from its close to midnight): on the next tick, the latest day of the last
/// <see cref="CatchUpDays"/> that should have had one (an NSE trading day, or a day the AI Trader looked) and did
/// not, and was not found empty, is sent then, titled with its day and "sent late". Older missed days are one line
/// in it. It does not wait for reviews owed, and these alone are no reason to send it. The digest of the day it is
/// sent on still goes after that day's close. Before 1 Oct a missed day's AI Trader section was never sent.
/// </para>
/// </remarks>
public sealed class AiDailyDigest(
    TradingDbContext db,
    AiSettingsStore store,
    AiReportWriter reports,
    IMarketSessionService sessions,
    ISystemNotifier notifier,
    IOptionsMonitor<AiSettings> settings,
    IAiTraderBooks? traderBooks = null) : IAiScheduledAgent
{
    /// <summary>
    /// When the last digest went (UTC). The key the reviewer's own digest used before the digest carried the AI
    /// Trader too: kept, so the first digest after the change neither repeats reviews nor goes twice that day.
    /// </summary>
    public const string SentKey = "ai.reviewer.lastDigestUtc";

    /// <summary>
    /// The IST day (yyyy-MM-dd) the last digest sent was for, on time or late. A late digest goes after its own day,
    /// so the time it went no longer tells which day it was for. When it is absent, the day <see cref="SentKey"/> was
    /// sent on: every digest before 1 Oct went on its own day.
    /// </summary>
    public const string DayKey = "ai.digest.lastDay";

    /// <summary>
    /// The last IST day (yyyy-MM-dd) found with nothing to say once its digest was due, at its own time or by the
    /// catch-up. Such a day is neither sent late nor called missed; a look later that evening still goes that day.
    /// </summary>
    public const string EmptyKey = "ai.digest.lastEmptyDay";

    /// <summary>How far back a missed digest is looked for.</summary>
    public const int CatchUpDays = 7;

    /// <summary>The longest it waits for the reviews still owed; those come in the next day's digest.</summary>
    public static readonly TimeSpan ReviewWait = TimeSpan.FromHours(2);

    /// <summary>Positions listed one by one (it opens at most 10 a day); past these, a count.</summary>
    private const int PositionsShown = 10;

    /// <summary>Deviations listed by title; past these, a count.</summary>
    private const int DeviationsShown = 5;

    public string AgentKey => AiCatalog.DailyDigest;

    public async Task<bool> RunOnceAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var s = settings.CurrentValue;
        if (!s.ReviewDigestToTelegram) return false;

        var day = IstTime.DateOf(nowUtc);
        var marks = await db.SystemSettings.AsNoTracking()
            .Where(x => x.Key == SentKey || x.Key == DayKey || x.Key == EmptyKey)
            .ToDictionaryAsync(x => x.Key, x => x.Value, cancellationToken);
        DateTime? last = marks.TryGetValue(SentKey, out var sentText) && DateTime.TryParse(sentText, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var sent)
            ? sent
            : null;
        // Before the day was kept, every digest went on its own day.
        DateOnly? lastDay = DayOf(marks, DayKey) ?? (last is DateTime l ? IstTime.DateOf(l) : null);
        if (lastDay >= day) return false;

        DateOnly? emptyDay = DayOf(marks, EmptyKey);
        if (new[] { lastDay, emptyDay }.Max() is DateOnly dealtWith)
        {
            var missed = await MissedDaysAsync(dealtWith, day, cancellationToken);
            if (missed.Count > 0 && await SendLateAsync(missed, last, s, nowUtc, cancellationToken)) return true;
        }

        var session = sessions.GetSessionInfo(nowUtc, "NSE", "FO");
        var due = IstTime.FromIst(day.ToDateTime(TradeReviewerAgent.ReviewAfter(s)));
        if (session.IsTradingDay && session.SessionCloseUtc > due) due = session.SessionCloseUtc;
        if (nowUtc < due) return false;

        var state = await store.LoadAsync(cancellationToken);
        var trader = await TraderDayAsync(day, nowUtc, state, s, cancellationToken);
        if (!session.IsTradingDay && trader is not { Looks.Count: > 0 }) return false;

        int owed = await ReviewsOwedAsync(state, s, nowUtc, cancellationToken);
        if (owed > 0 && nowUtc < due + ReviewWait) return false;

        var reviews = await ReviewsSinceAsync(last ?? nowUtc - TimeSpan.FromDays(1), cancellationToken);
        var sections = new[] { trader is null ? null : TraderSection(trader), ReviewSection(reviews, owed) }
            .Where(x => x is not null)
            .ToList();
        if (sections.Count == 0)
        {
            // Kept, so the next day's catch-up neither sends this day late nor calls it missed.
            if (emptyDay != day) await MarkAsync(EmptyKey, day, null, nowUtc, cancellationToken);
            return false;
        }

        // Kept before it is sent: a crash between the two loses one digest, and nothing can send it twice.
        await MarkAsync(DayKey, day, nowUtc, nowUtc, cancellationToken);

        await notifier.NotifyAsync(NotificationCategory.System, NotificationSeverity.Info,
            "AI day, " + DayName(day), string.Join("\n\n", sections),
            cancellationToken: cancellationToken);
        return true;
    }

    /// <summary>Not run by hand: it writes no report, and only its own rule keeps it to one a day.</summary>
    public Task<AiReport?> RunForAsync(string? subjectId, CancellationToken cancellationToken) => Task.FromResult<AiReport?>(null);

    /// <summary>
    /// The days before <paramref name="today"/> and after <paramref name="dealtWith"/> that should have had a digest
    /// and did not: NSE trading days, and days the AI Trader looked. Oldest first, at most <see cref="CatchUpDays"/>
    /// back.
    /// </summary>
    private async Task<List<DateOnly>> MissedDaysAsync(DateOnly dealtWith, DateOnly today, CancellationToken cancellationToken)
    {
        var from = dealtWith.AddDays(1);
        var floor = today.AddDays(-CatchUpDays);
        if (from < floor) from = floor;
        if (from >= today) return [];

        var looked = await db.AiTraderDecisions.AsNoTracking()
            .Where(d => d.Day >= from && d.Day < today && d.ReplaySessionId == null)
            .Select(d => d.Day)
            .Distinct()
            .ToListAsync(cancellationToken);
        var days = new List<DateOnly>();
        for (var d = from; d < today; d = d.AddDays(1))
        {
            if (looked.Contains(d) || sessions.GetSessionInfo(IstTime.MiddayUtc(d), "NSE", "FO").IsTradingDay) days.Add(d);
        }

        return days;
    }

    /// <summary>
    /// The latest missed day's digest, sent now and titled as late, with one line for any missed before it. False
    /// when it has nothing to say: the day is then kept as empty, so it is neither sent later nor called missed.
    /// It does not wait for reviews still owed: it says how many, and they come in the next digest. Reviews owed are
    /// not on their own a reason to send one late (an MCX run stopped at the close is still owed after midnight).
    /// </summary>
    private async Task<bool> SendLateAsync(IReadOnlyList<DateOnly> missed, DateTime? lastSentUtc, AiSettings s, DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var day = missed[^1];
        var earlier = missed.Take(missed.Count - 1).ToList();
        var state = await store.LoadAsync(cancellationToken);
        var trader = await TraderDayAsync(day, nowUtc, state, s, cancellationToken);
        // On, but no look and no position that day: the API was down, most likely, and there is nothing to tell late.
        if (trader is { Looks.Count: 0, Book.Count: 0 }) trader = null;
        var reviews = await ReviewsSinceAsync(lastSentUtc ?? nowUtc - TimeSpan.FromDays(1), cancellationToken);

        if (trader is null && reviews.Count == 0 && earlier.Count == 0)
        {
            await MarkAsync(EmptyKey, day, null, nowUtc, cancellationToken);
            return false;
        }

        int owed = await ReviewsOwedAsync(state, s, nowUtc, cancellationToken);
        var sections = new[] { trader is null ? null : TraderSection(trader), ReviewSection(reviews, owed), MissedLine(earlier) }
            .Where(x => x is not null)
            .ToList();

        // Kept before it is sent, as the day's own: never sent twice, and the next day's own digest still goes.
        await MarkAsync(DayKey, day, nowUtc, nowUtc, cancellationToken);
        await notifier.NotifyAsync(NotificationCategory.System, NotificationSeverity.Info,
            $"AI day, {DayName(day)} (sent late)", string.Join("\n\n", sections),
            cancellationToken: cancellationToken);
        return true;
    }

    /// <summary>"The digests for Mon 28 Sep–Tue 29 Sep were missed." Null when none were.</summary>
    private static string? MissedLine(IReadOnlyList<DateOnly> days) => days.Count switch
    {
        0 => null,
        1 => $"The digest for {DayName(days[0])} was missed.",
        _ => $"The digests for {DayName(days[0])}–{DayName(days[^1])} were missed.",
    };

    /// <summary>
    /// Keeps <paramref name="day"/> under <paramref name="key"/> (<see cref="DayKey"/> or <see cref="EmptyKey"/>) and,
    /// when a digest goes, the time it went (<see cref="SentKey"/>, which the next digest's reviews are counted from).
    /// </summary>
    private async Task MarkAsync(string key, DateOnly day, DateTime? sentUtc, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await SetAsync(key, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), nowUtc, cancellationToken);
        if (sentUtc is DateTime sent) await SetAsync(SentKey, sent.ToString("O", CultureInfo.InvariantCulture), nowUtc, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static DateOnly? DayOf(Dictionary<string, string> marks, string key) =>
        marks.TryGetValue(key, out var text)
        && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : null;

    private async Task SetAsync(string key, string value, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var row = await db.SystemSettings.FirstOrDefaultAsync(x => x.Key == key, cancellationToken);
        if (row is null)
        {
            row = new SystemSetting { Key = key, CreatedUtc = nowUtc };
            db.SystemSettings.Add(row);
        }

        row.Value = value;
        row.UpdatedBy = AgentKey;
        row.UpdatedUtc = nowUtc;
    }

    private static string DayName(DateOnly day) => day.ToString("ddd d MMM", CultureInfo.InvariantCulture);

    /// <summary>
    /// The AI Trader's live day: its looks, its shadow book, and in live mode its live book (its manual book). Null
    /// when it is off and did not look.
    /// </summary>
    private async Task<TraderDay?> TraderDayAsync(DateOnly day, DateTime nowUtc, AiState state, AiSettings s, CancellationToken cancellationToken)
    {
        bool on = state.Agent(AiCatalog.AiTrader) is { Status: "on" };
        var looks = await db.AiTraderDecisions.AsNoTracking()
            .Where(d => d.Day == day && d.ReplaySessionId == null)
            .OrderBy(d => d.ClockUtc).ThenBy(d => d.Id)
            .Select(d => new Look(d.Mode, d.Action, d.Allowed, d.Rule))
            .ToListAsync(cancellationToken);
        if (!on && looks.Count == 0) return null;

        var book = await db.AiTraderShadowPositions.AsNoTracking()
            .Where(p => p.Day == day && p.ReplaySessionId == null)
            .OrderBy(p => p.EntryUtc).ThenBy(p => p.Id)
            .ToListAsync(cancellationToken);
        string mode = looks.Count > 0 ? looks[^1].Mode : s.AiTraderExecute ? AiTraderModes.Live : AiTraderModes.Shadow;
        // Its live book is read only on a day it was live: in shadow mode it places nothing there.
        var live = traderBooks is not null && (mode == AiTraderModes.Live || looks.Any(l => l.Mode == AiTraderModes.Live))
            ? await traderBooks.DayAsync(nowUtc, cancellationToken)
            : null;
        return new TraderDay(day, on, mode, looks, book, live);
    }

    /// <summary>The runs the reviewer is on for and has not settled (none written, or failed with tries left).</summary>
    private async Task<int> ReviewsOwedAsync(AiState state, AiSettings s, DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (state.Agent(AiCatalog.TradeReviewer) is not { Status: "on" }) return 0;
        var ready = await TradeReviewerAgent.ReadyAsync(db, TradeReviewerAgent.ReviewAfter(s), nowUtc, cancellationToken);
        var unsettled = await reports.UnsettledAsync(AiCatalog.TradeReviewer, AiReportSubject.Run,
            ready.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToList(), cancellationToken);
        return unsettled.Count;
    }

    private async Task<List<Review>> ReviewsSinceAsync(DateTime sinceUtc, CancellationToken cancellationToken)
    {
        var rows = await db.AiReports.AsNoTracking()
            .Where(r => r.AgentKey == AiCatalog.TradeReviewer && r.SubjectType == AiReportSubject.Run && r.UpdatedUtc > sinceUtc
                        && r.Status != AiReportStatus.Failed)
            .OrderBy(r => r.Id)
            .Select(r => new { r.SubjectId, r.Title, r.DataJson })
            .ToListAsync(cancellationToken);
        return rows.Select(r => new Review(r.SubjectId, r.Title, AiJson.Object(r.DataJson) is { } d ? AiJson.Str(d, "verdict") : null)).ToList();
    }

    /// <summary>
    /// Its looks counted as the console counts them, the shadow book position by position with the net after
    /// charges, and the rules that refused it most.
    /// </summary>
    private static string TraderSection(TraderDay t)
    {
        var text = new StringBuilder($"AI Trader ({t.Mode} mode{(t.On ? string.Empty : ", now off")})");
        if (t.Looks.Count == 0)
        {
            return text.Append("\nOn, but no looks today.").ToString();
        }

        static bool Acted(Look l) => l.Action is not ("" or AiTraderPlan.None);
        static bool NoAnswer(Look l) => l.Rule is "no-answer" or "unreadable";
        var refused = t.Looks.Where(l => !l.Allowed && !NoAnswer(l)).ToList();
        text.Append($"\nLooks {t.Looks.Count} · proposed {t.Looks.Count(Acted)} · allowed {t.Looks.Count(l => Acted(l) && l.Allowed)} · refused {refused.Count} · no answer {t.Looks.Count(NoAnswer)}");

        if (t.Book.Count > 0)
        {
            decimal charges = t.Book.Sum(p => p.ExitUtc is null
                ? AiTraderShadowBook.Charges(p.Symbol, p.EntryPrice, p.MarkPrice ?? p.EntryPrice, p.Units)
                : p.Charges);
            int open = t.Book.Count(p => p.ExitUtc is null);
            text.Append($"\nShadow book: {t.Book.Count} trade{(t.Book.Count == 1 ? "" : "s")}{(open > 0 ? $" ({open} still open)" : "")}, ")
                .Append($"net {Net(AiTraderShadowBook.Net(t.Book))} after {AiTraderGuard.Rupees(Whole(charges))} charges");
            foreach (var p in t.Book.Take(PositionsShown)) text.Append("\n• ").Append(PositionLine(p));
            if (t.Book.Count > PositionsShown) text.Append($"\n• and {t.Book.Count - PositionsShown} more");
        }
        else if (t.Mode == AiTraderModes.Shadow)
        {
            text.Append("\nShadow book: no trades");
        }

        if (t.Live is { Positions.Count: 0 })
        {
            text.Append("\nLive book: no trades");
        }
        else if (t.Live is { } live)
        {
            int open = live.Positions.Count(p => p.Open);
            text.Append($"\nLive book: {live.Positions.Count} trade{(live.Positions.Count == 1 ? "" : "s")}{(open > 0 ? $" ({open} still open)" : "")}, ")
                .Append($"net {Net(live.Net)} after {AiTraderGuard.Rupees(Whole(live.Charges))} charges");
            foreach (var p in live.Positions.Take(PositionsShown)) text.Append("\n• ").Append(LiveLine(p, t.Day));
            if (live.Positions.Count > PositionsShown) text.Append($"\n• and {live.Positions.Count - PositionsShown} more");
        }

        if (refused.Count > 0)
        {
            var top = refused.GroupBy(l => l.Rule).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(3);
            text.Append("\nTop refusals: ").Append(string.Join(", ", top.Select(g => $"{g.Key} ({g.Count()})")));
        }

        return text.ToString();
    }

    /// <summary>"NIFTY 22650 CE, 1 lot: 11:00 → 11:02, ₹120 → ₹88, stop, −₹2,150".</summary>
    private static string PositionLine(AiTraderShadowPosition p)
    {
        string contract = $"{p.Underlying} {p.Strike.ToString("0.##", CultureInfo.InvariantCulture)} {p.OptionType}, {p.Lots} lot{(p.Lots == 1 ? "" : "s")}";
        if (p.ExitUtc is not DateTime exit)
        {
            return $"{contract}: {Clock(p.EntryUtc)} → open, {AiTraderGuard.Rupees(p.EntryPrice)} → {AiTraderGuard.Rupees(p.MarkPrice ?? p.EntryPrice)}, " +
                   $"not closed (mark at {Clock(p.MarkUtc ?? p.EntryUtc)}), {Net(AiTraderShadowBook.Net([p]))}";
        }

        string how = p.ExitReason switch
        {
            AiTraderShadowBook.ExitedByIt => "its exit",
            AiTraderShadowBook.ReplayEnded => "replay ended",
            _ => p.ExitReason,
        };
        return $"{contract}: {Clock(p.EntryUtc)} → {Clock(exit)}, {AiTraderGuard.Rupees(p.EntryPrice)} → {AiTraderGuard.Rupees(p.ExitPrice ?? p.EntryPrice)}, " +
               $"{how}, {Net(p.NetPnl ?? 0m)}";
    }

    /// <summary>
    /// A live position as a shadow one reads, without how it ended (its manual book does not keep that):
    /// "NIFTY 22650 CE, 1 lot: 09:30 → 09:52, ₹120 → ₹88, −₹2,139", net after its own round trip's charges. One carried
    /// in from an earlier day names that day.
    /// </summary>
    private static string LiveLine(AiTraderLivePosition p, DateOnly day)
    {
        var o = UnderlyingCatalog.ParseOptionSymbol(p.Symbol);
        string name = o is null ? p.Symbol : $"{o.Underlying} {o.Strike.ToString("0.##", CultureInfo.InvariantCulture)} {o.OptionType}";
        string contract = $"{name}, {p.Lots} lot{(p.Lots == 1 ? "" : "s")}";
        string opened = IstTime.DateOf(p.OpenedUtc) == day ? Clock(p.OpenedUtc) : IstTime.ToIst(p.OpenedUtc).ToString("ddd HH:mm", CultureInfo.InvariantCulture);
        string prices = $"{AiTraderGuard.Rupees(p.Entry)} → {AiTraderGuard.Rupees(p.Mark ?? p.Entry)}";
        decimal net = p.Pnl - AiTraderShadowBook.Charges(p.Symbol, p.Entry, p.Mark ?? p.Entry, p.Lots * p.LotSize);
        return p.ClosedUtc is DateTime exit
            ? $"{contract}: {opened} → {Clock(exit)}, {prices}, {Net(net)}"
            : $"{contract}: {opened} → open, {prices}, not closed (mark at {Clock(p.MarkUtc ?? p.OpenedUtc)}), {Net(net)}";
    }

    /// <summary>How many reviews, how many kept to their spec, and the ones that did not. Null when there is nothing to say.</summary>
    private static string? ReviewSection(IReadOnlyList<Review> reviews, int owed)
    {
        var text = new StringBuilder();
        if (reviews.Count > 0)
        {
            int followed = reviews.Count(r => r.Verdict == "followed");
            var deviated = reviews.Where(r => r.Verdict == "deviated").ToList();
            text.Append($"{reviews.Count} run review{(reviews.Count == 1 ? "" : "s")} written: {followed} followed the spec, {deviated.Count} did not");
            int unclear = reviews.Count - followed - deviated.Count;
            if (unclear > 0) text.Append($", {unclear} unclear");
            text.Append('.');
            foreach (var d in deviated.Take(DeviationsShown)) text.Append($"\n• #{d.RunId}: {Escape(d.Title)}");
            if (deviated.Count > DeviationsShown) text.Append($"\n• and {deviated.Count - DeviationsShown} more");
        }

        if (owed > 0)
        {
            if (text.Length > 0) text.Append('\n');
            text.Append($"{owed} run review{(owed == 1 ? " is" : "s are")} not written yet: in the next digest.");
        }

        if (text.Length == 0) return null;
        return text.Append("\nRead them on the AI page, Reports tab.").ToString();
    }

    /// <summary>"+₹2,170" / "−₹2,150" / "₹0": whole rupees with the sign in front.</summary>
    private static string Net(decimal value)
    {
        decimal whole = Whole(value);
        return (whole > 0 ? "+" : string.Empty) + AiTraderGuard.Rupees(whole);
    }

    private static decimal Whole(decimal value) => Math.Round(value, 0, MidpointRounding.AwayFromZero);

    private static string Clock(DateTime utc) => IstTime.ToIst(utc).ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// A model's words in a message Telegram reads as HTML: a bare <c>&lt;</c> or <c>&amp;</c> there makes it
    /// refuse the whole message, and the sender has no plain-text retry.
    /// </summary>
    private static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private sealed record Look(string Mode, string Action, bool Allowed, string Rule);

    /// <param name="Live">Its live book, on a day it was live (and the digest can read it); else null.</param>
    private sealed record TraderDay(DateOnly Day, bool On, string Mode, IReadOnlyList<Look> Looks, IReadOnlyList<AiTraderShadowPosition> Book,
        AiTraderLiveDay? Live);

    private sealed record Review(string RunId, string Title, string? Verdict);
}
