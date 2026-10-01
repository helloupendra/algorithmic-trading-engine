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
/// (an MCX run's, after 23:30) goes in the next one.
/// </para>
/// <para>
/// Once a day: the time it went is kept in system settings before it is sent, so neither a restart nor the next
/// tick sends it again. On a day with no NSE session it goes only if the AI Trader looked that day. Replay
/// decisions and replay shadow positions are never in it. A section with nothing to say is left out, and nothing
/// is sent when neither has anything.
/// </para>
/// </remarks>
public sealed class AiDailyDigest(
    TradingDbContext db,
    AiSettingsStore store,
    AiReportWriter reports,
    IMarketSessionService sessions,
    ISystemNotifier notifier,
    IOptionsMonitor<AiSettings> settings) : IAiScheduledAgent
{
    /// <summary>
    /// When the last digest went (UTC). The key the reviewer's own digest used before the digest carried the AI
    /// Trader too: kept, so the first digest after the change neither repeats reviews nor goes twice that day.
    /// </summary>
    public const string SentKey = "ai.reviewer.lastDigestUtc";

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
        var marker = await db.SystemSettings.FirstOrDefaultAsync(x => x.Key == SentKey, cancellationToken);
        DateTime? last = marker is not null && DateTime.TryParse(marker.Value, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var sent)
            ? sent
            : null;
        if (last is DateTime l && IstTime.DateOf(l) >= day) return false;

        var session = sessions.GetSessionInfo(nowUtc, "NSE", "FO");
        var due = IstTime.FromIst(day.ToDateTime(TradeReviewerAgent.ReviewAfter(s)));
        if (session.IsTradingDay && session.SessionCloseUtc > due) due = session.SessionCloseUtc;
        if (nowUtc < due) return false;

        var state = await store.LoadAsync(cancellationToken);
        var trader = await TraderDayAsync(day, state, s, cancellationToken);
        if (!session.IsTradingDay && trader is not { Looks.Count: > 0 }) return false;

        int owed = await ReviewsOwedAsync(state, s, nowUtc, cancellationToken);
        if (owed > 0 && nowUtc < due + ReviewWait) return false;

        var reviews = await ReviewsSinceAsync(last ?? nowUtc - TimeSpan.FromDays(1), cancellationToken);
        var sections = new[] { trader is null ? null : TraderSection(trader), ReviewSection(reviews, owed) }
            .Where(x => x is not null)
            .ToList();
        if (sections.Count == 0) return false;

        // Kept before it is sent: a crash between the two loses one digest, and nothing can send it twice.
        if (marker is null)
        {
            marker = new SystemSetting { Key = SentKey, CreatedUtc = nowUtc };
            db.SystemSettings.Add(marker);
        }

        marker.Value = nowUtc.ToString("O", CultureInfo.InvariantCulture);
        marker.UpdatedBy = AgentKey;
        marker.UpdatedUtc = nowUtc;
        await db.SaveChangesAsync(cancellationToken);

        await notifier.NotifyAsync(NotificationCategory.System, NotificationSeverity.Info,
            "AI day, " + day.ToString("ddd d MMM", CultureInfo.InvariantCulture), string.Join("\n\n", sections),
            cancellationToken: cancellationToken);
        return true;
    }

    /// <summary>Not run by hand: it writes no report, and only its own rule keeps it to one a day.</summary>
    public Task<AiReport?> RunForAsync(string? subjectId, CancellationToken cancellationToken) => Task.FromResult<AiReport?>(null);

    /// <summary>The AI Trader's live day: its looks and its shadow book. Null when it is off and did not look.</summary>
    private async Task<TraderDay?> TraderDayAsync(DateOnly day, AiState state, AiSettings s, CancellationToken cancellationToken)
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
        return new TraderDay(on, mode, looks, book);
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

    private sealed record TraderDay(bool On, string Mode, IReadOnlyList<Look> Looks, IReadOnlyList<AiTraderShadowPosition> Book);

    private sealed record Review(string RunId, string Title, string? Verdict);
}
