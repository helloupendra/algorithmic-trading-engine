using System.Globalization;
using AlgoTrading.Api.Services.AiAgents;
using AlgoTrading.Api.Services.AiTrader;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Domain.Enums;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static AlgoTrading.UnitTests.AiTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The AI's one Telegram digest a day: the AI Trader's live day (its looks and shadow book, never a replay's) and
/// the run reviews, sent once after the close, and only when there is something to say.
/// </summary>
public class AiDailyDigestTests
{
    // Wednesday 30 Sep 2026, a trading day, in IST.
    private static DateTime Ist(int hour, int minute) => AiAgentsTests.Ist(hour, minute);

    private static readonly DateOnly Day = new(2026, 9, 30);

    [Fact]
    public async Task One_message_carries_the_AI_Traders_day_and_the_reviews_and_leaves_replays_out()
    {
        var ai = Build(Settings());
        await On(ai, AiCatalog.AiTrader, AiCatalog.TradeReviewer);
        SeedTradersDay(ai);
        long runId = AiAgentsTests.SeedRun(ai, stoppedAt: Ist(15, 31));
        SeedReview(ai, runId, "deviated", "Stop & target moved after entry", Ist(15, 44));
        // A replay of the same day, with its own looks and shadow book: none of it is the live day's.
        ai.Db.AiTraderDecisions.AddRange(Look(Ist(10, 0), "buy", true, "ok", replay: 7), Look(Ist(10, 10), "buy", false, "capital", replay: 7));
        ai.Db.AiTraderShadowPositions.Add(Position(Ist(10, 0), 22650m, "CE", 2, 120m, 160m, Ist(10, 40), AiTraderShadowBook.TargetHit, 70.10m, replay: 7));
        await ai.Db.SaveChangesAsync();
        var telegram = new Recorder();

        Assert.True(await Digest(ai, telegram).RunOnceAsync(Ist(15, 46), default));

        var sent = Assert.Single(telegram.Sent);
        Assert.Equal((NotificationCategory.System, NotificationSeverity.Info, "AI day, Wed 30 Sep"), (sent.Category, sent.Severity, sent.Title));
        Assert.Equal(
            """
            AI Trader (shadow mode)
            Looks 10 · proposed 8 · allowed 4 · refused 4 · no answer 1
            Shadow book: 3 trades, net −₹122 after ₹187 charges
            • NIFTY 22650 CE, 1 lot: 09:30 → 09:52, ₹120 → ₹88, stop, −₹2,139
            • NIFTY 22600 PE, 2 lots: 10:00 → 11:00, ₹81 → ₹95.5, its exit, +₹1,816
            • NIFTY 22700 CE, 1 lot: 12:00 → 15:30, ₹96 → ₹100, close, +₹201
            Top refusals: stop (2), hours (1), size (1)

            1 run review written: 0 followed the spec, 1 did not.
            • #1: Stop &amp; target moved after entry
            Read them on the AI page, Reports tab.
            """.ReplaceLineEndings("\n"),
            sent.Message);
    }

    [Fact]
    public async Task It_goes_once_a_day_and_a_restart_does_not_send_it_again()
    {
        var ai = Build(Settings());
        await On(ai, AiCatalog.AiTrader);
        SeedTradersDay(ai);
        var telegram = new Recorder();

        Assert.True(await Digest(ai, telegram).RunOnceAsync(Ist(15, 46), default));
        Assert.False(await Digest(ai, telegram).RunOnceAsync(Ist(15, 47), default));
        // A new digest over the same database, as after a restart.
        Assert.False(await Digest(ai, telegram).RunOnceAsync(Ist(21, 0), default));
        Assert.Single(telegram.Sent);

        // The next trading day has its own.
        var thursday = IstTime.FromIst(new DateTime(2026, 10, 1, 15, 46, 0));
        ai.Db.AiTraderDecisions.Add(Look(IstTime.FromIst(new DateTime(2026, 10, 1, 9, 20, 0)), "none", true, "ok"));
        await ai.Db.SaveChangesAsync();
        Assert.True(await Digest(ai, telegram).RunOnceAsync(thursday, default));
        Assert.Equal(2, telegram.Sent.Count);
        Assert.Equal("AI day, Thu 1 Oct", telegram.Sent[1].Title);
        Assert.StartsWith("AI Trader (shadow mode)\nLooks 1 · proposed 0", telegram.Sent[1].Message);
    }

    [Fact]
    public async Task It_waits_for_the_session_close_and_the_review_hour_whichever_is_later()
    {
        var ai = Build(Settings(s => s.ReviewAfterIst = "15:00"));
        await On(ai, AiCatalog.AiTrader);
        SeedTradersDay(ai);
        var telegram = new Recorder();

        // 15:00 has passed, but the NSE session runs to 15:30 and the shadow book is squared off then.
        Assert.False(await Digest(ai, telegram).RunOnceAsync(Ist(15, 29), default));
        Assert.True(await Digest(ai, telegram).RunOnceAsync(Ist(15, 30), default));

        var later = Build(Settings());
        await On(later, AiCatalog.AiTrader);
        SeedTradersDay(later);
        Assert.False(await Digest(later, telegram).RunOnceAsync(Ist(15, 44), default));
        Assert.True(await Digest(later, telegram).RunOnceAsync(Ist(15, 45), default));
    }

    [Fact]
    public async Task When_the_reviewer_has_written_its_reviews_the_digest_carries_them_once()
    {
        var ai = Build(Settings());
        await On(ai, AiCatalog.TradeReviewer);
        long runId = AiAgentsTests.SeedRun(ai, stoppedAt: Ist(15, 31));
        // A deviation is asked twice; both reviews find it.
        ai.Provider.On(Judge1, Answer("""{"verdict":"deviated","title":"Entered at 09:47, not 09:20","journal":"Late entry."}"""),
            Answer("""{"verdict":"deviated","title":"Late entry at 09:47","journal":"Entered late."}"""));
        var reviewer = AiAgentsTests.Reviewer(ai, new AiAgentsTests.FixedTime(Ist(15, 47)));
        var telegram = new Recorder();

        // The run is due a review: the digest waits for it.
        Assert.False(await Digest(ai, telegram).RunOnceAsync(Ist(15, 46), default));
        Assert.True(await reviewer.RunOnceAsync(Ist(15, 47), default));
        Assert.True(await Digest(ai, telegram).RunOnceAsync(Ist(15, 48), default));
        Assert.False(await Digest(ai, telegram).RunOnceAsync(Ist(15, 49), default));

        var message = Assert.Single(telegram.Sent).Message;
        Assert.Contains("1 run review written: 0 followed the spec, 1 did not.", message);
        Assert.Contains($"#{runId}: Entered at 09:47, not 09:20", message);
        // The AI Trader was off and never looked: its section is left out.
        Assert.DoesNotContain("AI Trader", message);
    }

    [Fact]
    public async Task A_failed_review_with_tries_left_is_waited_for_and_one_given_up_on_is_not()
    {
        var ai = Build(Settings());
        await On(ai, AiCatalog.TradeReviewer);
        long done = AiAgentsTests.SeedRun(ai, stoppedAt: Ist(15, 31));
        long failing = AiAgentsTests.SeedRun(ai, stoppedAt: Ist(15, 32));
        SeedReview(ai, done, "followed", "Kept to its spec", Ist(15, 46));
        var retry = SeedReview(ai, failing, "", "Run: no review", Ist(15, 47), AiReportStatus.Failed, attempts: 1);
        var telegram = new Recorder();

        Assert.False(await Digest(ai, telegram).RunOnceAsync(Ist(15, 50), default));

        retry.Attempts = ai.Options.CurrentValue.MaxReportAttempts;
        await ai.Db.SaveChangesAsync();
        Assert.True(await Digest(ai, telegram).RunOnceAsync(Ist(16, 20), default));
        Assert.Equal("1 run review written: 1 followed the spec, 0 did not.\nRead them on the AI page, Reports tab.", Assert.Single(telegram.Sent).Message);
    }

    [Fact]
    public async Task It_waits_at_most_two_hours_for_reviews_and_says_which_are_still_owed()
    {
        var ai = Build(Settings());
        await On(ai, AiCatalog.AiTrader, AiCatalog.TradeReviewer);
        SeedTradersDay(ai);
        AiAgentsTests.SeedRun(ai, stoppedAt: Ist(15, 31));
        var telegram = new Recorder();

        Assert.False(await Digest(ai, telegram).RunOnceAsync(Ist(17, 44), default));
        Assert.True(await Digest(ai, telegram).RunOnceAsync(Ist(17, 45), default));

        string message = Assert.Single(telegram.Sent).Message;
        Assert.StartsWith("AI Trader (shadow mode)\n", message);
        Assert.EndsWith("\n\n1 run review is not written yet: in the next digest.\nRead them on the AI page, Reports tab.", message);
    }

    [Fact]
    public async Task Nothing_is_sent_without_something_to_say_and_a_replay_alone_is_nothing()
    {
        var ai = Build(Settings());
        ai.Db.AiTraderDecisions.Add(Look(Ist(10, 0), "buy", true, "ok", replay: 7));
        ai.Db.AiTraderShadowPositions.Add(Position(Ist(10, 0), 22650m, "CE", 1, 120m, 160m, Ist(10, 40), AiTraderShadowBook.TargetHit, 59m, replay: 7));
        await ai.Db.SaveChangesAsync();
        var telegram = new Recorder();

        Assert.False(await Digest(ai, telegram).RunOnceAsync(Ist(15, 46), default));
        Assert.Empty(telegram.Sent);
        Assert.False(await ai.Db.SystemSettings.AnyAsync(s => s.Key == AiDailyDigest.SentKey));

        // A look later that evening ("Run now") is that day's first news: it goes then.
        ai.Db.AiTraderDecisions.Add(Look(Ist(20, 0), "none", true, "ok"));
        await ai.Db.SaveChangesAsync();
        Assert.True(await Digest(ai, telegram).RunOnceAsync(Ist(20, 1), default));
        Assert.StartsWith("AI Trader (shadow mode, now off)\nLooks 1 · proposed 0 · allowed 0 · refused 0 · no answer 0\nShadow book: no trades",
            Assert.Single(telegram.Sent).Message);
    }

    [Fact]
    public async Task On_a_day_with_no_session_it_goes_only_if_the_AI_Trader_looked()
    {
        // Friday 2 Oct 2026, Gandhi Jayanti: a weekday with no NSE session.
        var holiday = new DateOnly(2026, 10, 2);
        DateTime At(int hour, int minute) => IstTime.FromIst(holiday.ToDateTime(new TimeOnly(hour, minute)));
        var ai = Build(Settings());
        await On(ai, AiCatalog.AiTrader, AiCatalog.TradeReviewer);
        SeedReview(ai, 900, "followed", "Kept to its spec", At(0, 5));
        var telegram = new Recorder();

        Assert.False(await Digest(ai, telegram, holiday).RunOnceAsync(At(16, 0), default));

        // "Run now" on the holiday: the rules refuse a buy on a day with no session.
        ai.Db.AiTraderDecisions.Add(Look(At(16, 30), "buy", false, "hours"));
        await ai.Db.SaveChangesAsync();
        Assert.True(await Digest(ai, telegram, holiday).RunOnceAsync(At(16, 31), default));
        var sent = Assert.Single(telegram.Sent);
        Assert.Equal("AI day, Fri 2 Oct", sent.Title);
        Assert.Contains("Looks 1 · proposed 1 · allowed 0 · refused 1 · no answer 0", sent.Message);
        Assert.Contains("Top refusals: hours (1)", sent.Message);
        Assert.Contains("1 run review written: 1 followed the spec, 0 did not.", sent.Message);
    }

    [Fact]
    public async Task On_with_no_looks_says_so_and_an_unsquared_position_shows_its_mark()
    {
        var ai = Build(Settings());
        await On(ai, AiCatalog.AiTrader);
        var telegram = new Recorder();
        Assert.True(await Digest(ai, telegram).RunOnceAsync(Ist(15, 46), default));
        Assert.Equal("AI Trader (shadow mode)\nOn, but no looks today.", Assert.Single(telegram.Sent).Message);

        // Switched off at 14:00 with a position open: the minute check never squared it off.
        var other = Build(Settings());
        other.Db.AiTraderDecisions.Add(Look(Ist(11, 0), "buy", true, "ok"));
        var open = Position(Ist(11, 0), 22650m, "CE", 1, 120m, null, null, string.Empty, 0m);
        open.MarkPrice = 130m;
        open.MarkUtc = Ist(14, 0);
        other.Db.AiTraderShadowPositions.Add(open);
        await other.Db.SaveChangesAsync();
        var later = new Recorder();

        Assert.True(await Digest(other, later).RunOnceAsync(Ist(15, 46), default));
        // As if sold at its mark: the desk's charges on 120 → 130, net of them.
        decimal charges = AiTraderShadowBook.Charges(open.Symbol, 120m, 130m, 65);
        string net = Signed(10m * 65 - charges);
        Assert.Equal(
            "AI Trader (shadow mode, now off)\nLooks 1 · proposed 1 · allowed 1 · refused 0 · no answer 0\n" +
            $"Shadow book: 1 trade (1 still open), net {net} after {AiTraderGuard.Rupees(Math.Round(charges, 0, MidpointRounding.AwayFromZero))} charges\n" +
            $"• NIFTY 22650 CE, 1 lot: 11:00 → open, ₹120 → ₹130, not closed (mark at 14:00), {net}",
            Assert.Single(later.Sent).Message);
    }

    [Fact]
    public async Task ReviewDigestToTelegram_switches_it_off()
    {
        var ai = Build(Settings(s => s.ReviewDigestToTelegram = false));
        await On(ai, AiCatalog.AiTrader);
        SeedTradersDay(ai);
        var telegram = new Recorder();

        Assert.False(await Digest(ai, telegram).RunOnceAsync(Ist(15, 46), default));
        Assert.Empty(telegram.Sent);

        var state = await ai.Store.LoadAsync();
        Assert.False(AiAgentScheduler.IsOn(AiCatalog.DailyDigest, state, Settings(s => s.ReviewDigestToTelegram = false)));
        // On whichever agents are on: it reports on them and asks no model.
        Assert.True(AiAgentScheduler.IsOn(AiCatalog.DailyDigest, state, Settings()));
    }

    [Fact]
    public async Task A_day_missed_while_the_API_was_down_is_sent_late_once_and_the_next_day_still_gets_its_own()
    {
        var ai = Build(Settings());
        await On(ai, AiCatalog.AiTrader);
        SeedSent(ai, At(Tue29Sep, 15, 46));
        // Wednesday's looks and shadow book; the API went down at 15:40, before Wednesday's digest.
        SeedTradersDay(ai);
        var telegram = new Recorder();

        // Thursday 09:00, the API back: Wednesday's digest goes, titled with its day and as late.
        Assert.True(await Digest(ai, telegram).RunOnceAsync(At(Thu01Oct, 9, 0), default));
        var late = Assert.Single(telegram.Sent);
        Assert.Equal("AI day, Wed 30 Sep (sent late)", late.Title);
        Assert.StartsWith("AI Trader (shadow mode)\nLooks 10 · proposed 8 · allowed 4 · refused 4 · no answer 1\nShadow book: 3 trades", late.Message);

        // Never twice: not the next minute, not after a restart.
        Assert.False(await Digest(ai, telegram).RunOnceAsync(At(Thu01Oct, 9, 1), default));
        Assert.False(await Digest(ai, telegram).RunOnceAsync(At(Thu01Oct, 12, 0), default));

        // Thursday's own digest still goes, after its close.
        ai.Db.AiTraderDecisions.Add(Look(At(Thu01Oct, 9, 20), "none", true, "ok"));
        await ai.Db.SaveChangesAsync();
        Assert.False(await Digest(ai, telegram).RunOnceAsync(At(Thu01Oct, 15, 44), default));
        Assert.True(await Digest(ai, telegram).RunOnceAsync(At(Thu01Oct, 15, 46), default));
        Assert.False(await Digest(ai, telegram).RunOnceAsync(At(Thu01Oct, 15, 47), default));
        Assert.Equal(["AI day, Wed 30 Sep (sent late)", "AI day, Thu 1 Oct"], telegram.Sent.Select(n => n.Title));
        Assert.StartsWith("AI Trader (shadow mode)\nLooks 1 · proposed 0", telegram.Sent[1].Message);
    }

    [Fact]
    public async Task Only_the_latest_missed_day_is_sent_late_and_the_older_ones_are_one_line()
    {
        var ai = Build(Settings());
        await On(ai, AiCatalog.AiTrader, AiCatalog.TradeReviewer);
        SeedSent(ai, At(Fri25Sep, 15, 46));
        SeedTradersDay(ai);
        SeedReview(ai, 900, "followed", "Kept to its spec", At(Mon28Sep, 15, 50));
        var telegram = new Recorder();

        // Down from Monday's close to Thursday morning: Monday, Tuesday and Wednesday had no digest.
        Assert.True(await Digest(ai, telegram).RunOnceAsync(At(Thu01Oct, 10, 0), default));
        var late = Assert.Single(telegram.Sent);
        Assert.Equal("AI day, Wed 30 Sep (sent late)", late.Title);
        Assert.Contains("\n\n1 run review written: 1 followed the spec, 0 did not.\nRead them on the AI page, Reports tab.", late.Message);
        Assert.EndsWith("\n\nThe digests for Mon 28 Sep–Tue 29 Sep were missed.", late.Message);

        Assert.False(await Digest(ai, telegram).RunOnceAsync(At(Thu01Oct, 10, 1), default));
        Assert.Single(telegram.Sent);
    }

    [Fact]
    public async Task A_day_with_nothing_to_say_is_neither_sent_late_nor_called_missed()
    {
        var ai = Build(Settings());
        SeedSent(ai, At(Mon28Sep, 15, 46));
        var telegram = new Recorder();

        // Tuesday: nothing to say at the close.
        Assert.False(await Digest(ai, telegram).RunOnceAsync(At(Tue29Sep, 15, 46), default));
        Assert.Empty(telegram.Sent);

        // No digest tick again until Thursday 09:00 (the API down on Tuesday night, and from 15:40 on Wednesday, after
        // the AI Trader's day). Wednesday's goes late; Tuesday, dealt with at its time, is not called missed.
        SeedTradersDay(ai);
        Assert.True(await Digest(ai, telegram).RunOnceAsync(At(Thu01Oct, 9, 0), default));
        var late = Assert.Single(telegram.Sent);
        Assert.Equal("AI day, Wed 30 Sep (sent late)", late.Title);
        Assert.DoesNotContain("missed", late.Message);
    }

    [Fact]
    public async Task Reviews_still_owed_are_not_on_their_own_a_reason_to_send_a_day_late()
    {
        var ai = Build(Settings());
        await On(ai, AiCatalog.TradeReviewer);
        SeedSent(ai, At(Tue29Sep, 15, 46));
        // Wednesday's run, its review not written when the API went down at 15:40: still owed on Thursday morning.
        AiAgentsTests.SeedRun(ai, stoppedAt: Ist(15, 31));
        var telegram = new Recorder();

        Assert.False(await Digest(ai, telegram).RunOnceAsync(At(Thu01Oct, 9, 0), default));
        Assert.False(await Digest(ai, telegram).RunOnceAsync(At(Thu01Oct, 9, 1), default));
        Assert.Empty(telegram.Sent);
    }

    [Fact]
    public async Task Back_after_today_s_close_the_late_digest_goes_first_and_today_s_the_next_minute()
    {
        var ai = Build(Settings());
        await On(ai, AiCatalog.AiTrader);
        SeedSent(ai, At(Tue29Sep, 15, 46));
        SeedTradersDay(ai);
        ai.Db.AiTraderDecisions.Add(Look(At(Thu01Oct, 9, 20), "none", true, "ok"));
        await ai.Db.SaveChangesAsync();
        var telegram = new Recorder();

        Assert.True(await Digest(ai, telegram).RunOnceAsync(At(Thu01Oct, 16, 0), default));
        Assert.True(await Digest(ai, telegram).RunOnceAsync(At(Thu01Oct, 16, 1), default));
        Assert.False(await Digest(ai, telegram).RunOnceAsync(At(Thu01Oct, 16, 2), default));
        Assert.Equal(["AI day, Wed 30 Sep (sent late)", "AI day, Thu 1 Oct"], telegram.Sent.Select(n => n.Title));
    }

    [Fact]
    public async Task With_no_digest_ever_sent_there_is_nothing_to_catch_up()
    {
        var ai = Build(Settings());
        await On(ai, AiCatalog.AiTrader);
        SeedTradersDay(ai);
        var telegram = new Recorder();

        Assert.False(await Digest(ai, telegram).RunOnceAsync(At(Thu01Oct, 9, 0), default));
        Assert.Empty(telegram.Sent);
    }

    // ---------- helpers ----------

    private static readonly DateOnly Fri25Sep = new(2026, 9, 25);
    private static readonly DateOnly Mon28Sep = new(2026, 9, 28);
    private static readonly DateOnly Tue29Sep = new(2026, 9, 29);
    private static readonly DateOnly Wed30Sep = Day;
    private static readonly DateOnly Thu01Oct = new(2026, 10, 1);

    private static DateTime At(DateOnly day, int hour, int minute) => IstTime.FromIst(day.ToDateTime(new TimeOnly(hour, minute)));

    /// <summary>The last digest went at <paramref name="atUtc"/>, as the digest keeps it.</summary>
    private static void SeedSent(Services ai, DateTime atUtc)
    {
        ai.Db.SystemSettings.Add(new SystemSetting { Key = AiDailyDigest.SentKey, Value = atUtc.ToString("O", CultureInfo.InvariantCulture), CreatedUtc = atUtc, UpdatedUtc = atUtc });
        ai.Db.SaveChanges();
    }

    private static AiDailyDigest Digest(Services ai, Recorder telegram, params DateOnly[] holidays) =>
        new(ai.Db, ai.Store, new AiReportWriter(ai.Db, ai.Options), new MarketSessionService(new Calendar(holidays)), telegram, ai.Options);

    private static async Task On(Services ai, params string[] agents)
    {
        foreach (var agent in agents) await ai.Store.SetAgentEnabledAsync(agent, true, "upendra", null);
    }

    /// <summary>
    /// Ten looks in shadow mode (8 actions: 4 allowed, 4 refused; 1 with no answer) and three closed shadow
    /// positions: stopped, exited by its own decision, squared off at the close. The charges are seeded.
    /// </summary>
    private static void SeedTradersDay(Services ai)
    {
        ai.Db.AiTraderDecisions.AddRange(
            Look(Ist(9, 20), "none", true, "ok"),
            Look(Ist(9, 30), "buy", true, "ok"),
            Look(Ist(9, 40), "buy", false, "stop"),
            Look(Ist(9, 50), "", false, "no-answer"),
            Look(Ist(10, 0), "buy", true, "ok"),
            Look(Ist(10, 10), "buy", false, "stop"),
            Look(Ist(10, 20), "buy", false, "size"),
            Look(Ist(11, 0), "exit", true, "ok"),
            Look(Ist(12, 0), "buy", true, "ok"),
            Look(Ist(14, 50), "buy", false, "hours"));
        ai.Db.AiTraderShadowPositions.AddRange(
            Position(Ist(9, 30), 22650m, "CE", 1, 120m, 88m, Ist(9, 52), AiTraderShadowBook.Stopped, 58.80m),
            Position(Ist(10, 0), 22600m, "PE", 2, 81m, 95.5m, Ist(11, 0), AiTraderShadowBook.ExitedByIt, 69.45m),
            Position(Ist(12, 0), 22700m, "CE", 1, 96m, 100m, Ist(15, 30), AiTraderShadowBook.SessionClose, 59.16m));
        ai.Db.SaveChanges();
    }

    private static AiTraderDecision Look(DateTime clockUtc, string action, bool allowed, string rule, long? replay = null) => new()
    {
        CreatedUtc = clockUtc, ClockUtc = clockUtc, Day = IstTime.DateOf(clockUtc), Mode = replay is null ? AiTraderModes.Shadow : AiTraderModes.Replay,
        ReplaySessionId = replay, Action = action, Underlying = action is "buy" or "exit" ? "NIFTY" : string.Empty, Allowed = allowed, Rule = rule,
    };

    /// <summary>A shadow position, closed at <paramref name="exit"/> with these charges, or open when it is null.</summary>
    private static AiTraderShadowPosition Position(DateTime entryUtc, decimal strike, string option, int lots, decimal entry, decimal? exit,
        DateTime? exitUtc, string reason, decimal charges, long? replay = null)
    {
        var p = new AiTraderShadowPosition
        {
            CreatedUtc = entryUtc, DecisionId = 1, Mode = replay is null ? AiTraderModes.Shadow : AiTraderModes.Replay, ReplaySessionId = replay,
            Day = IstTime.DateOf(entryUtc), Symbol = $"NSE:NIFTY26O06{strike:0}{option}", Underlying = "NIFTY", OptionType = option, Strike = strike,
            Expiry = new DateOnly(2026, 10, 6), Lots = lots, LotSize = 65, EntryUtc = entryUtc, EntryPrice = entry, StopLoss = entry * 0.75m,
            Target = entry * 1.4m, MarkPrice = exit ?? entry, MarkUtc = exitUtc ?? entryUtc,
        };
        if (exit is decimal x)
        {
            p.ExitUtc = exitUtc;
            p.ExitPrice = x;
            p.ExitReason = reason;
            p.Charges = charges;
            p.NetPnl = Math.Round((x - entry) * p.Units - charges, 2);
        }

        return p;
    }

    private static AiReport SeedReview(Services ai, long runId, string verdict, string title, DateTime atUtc,
        string status = AiReportStatus.Ok, int attempts = 1)
    {
        var report = new AiReport
        {
            AgentKey = AiCatalog.TradeReviewer, SubjectType = AiReportSubject.Run, SubjectId = runId.ToString(CultureInfo.InvariantCulture),
            SessionDate = Day, Status = status, Attempts = attempts, Title = title,
            DataJson = verdict.Length == 0 ? "{}" : $$"""{"verdict":"{{verdict}}"}""", CreatedUtc = atUtc, UpdatedUtc = atUtc,
        };
        ai.Db.AiReports.Add(report);
        ai.Db.SaveChanges();
        return report;
    }

    private static string Signed(decimal value)
    {
        decimal whole = Math.Round(value, 0, MidpointRounding.AwayFromZero);
        return (whole > 0 ? "+" : string.Empty) + AiTraderGuard.Rupees(whole);
    }

    private sealed record Notice(NotificationCategory Category, NotificationSeverity Severity, string Title, string Message);

    /// <summary>What would have gone to Telegram.</summary>
    private sealed class Recorder : ISystemNotifier
    {
        public List<Notice> Sent { get; } = [];

        public Task NotifyAsync(NotificationCategory category, NotificationSeverity severity, string title, string message,
            string? underlying = null, string? symbol = null, long? simulationRunId = null, CancellationToken cancellationToken = default)
        {
            Sent.Add(new Notice(category, severity, title, message));
            return Task.CompletedTask;
        }
    }

    /// <summary>Weekends and these dates closed; every year loaded.</summary>
    private sealed class Calendar(params DateOnly[] holidays) : IMarketCalendar
    {
        public bool IsLoaded => true;

        public MarketHoliday? HolidayOn(string exchange, DateOnly date) =>
            holidays.Contains(date) ? new MarketHoliday { Exchange = exchange, Date = date, Name = "Holiday", Closure = MarketClosure.FullDay } : null;

        public MarketSpecialSession? SpecialSessionOn(string exchange, DateOnly date) => null;

        public bool HasYear(string exchange, int year) => true;

        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
