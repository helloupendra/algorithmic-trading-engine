using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AlgoTrading.Api.Configuration;
using AlgoTrading.Api.Services;
using AlgoTrading.Api.Services.AiTelegram;
using AlgoTrading.Api.Services.AiTrader;
using AlgoTrading.Api.Services.AiVoice;
using AlgoTrading.Api.Services.Today;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Domain.Enums;
using AlgoTrading.Domain.ValueObjects;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static AlgoTrading.UnitTests.AiTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Ira speaks first: the AI Trader acting live, a run's risk rules stopping it or closing a group, a high or critical
/// Sentinel incident, and the two briefings of a trading day, each as one short message and the same words as a voice
/// note to the linked owners' private chats. Built by code from the desk's records. Nothing here reaches AWS or
/// Telegram: Polly and the bot are scripted.
/// </summary>
public sealed class IraAlertTests : IDisposable
{
    private const long Owner = 5550001;
    private const long Partner = 5550002;

    // Monday 12 Oct 2026, 11:00 IST: an NSE trading day.
    private static readonly DateOnly Day = new(2026, 10, 12);
    private static readonly DateTime Now = Ist(11, 0);
    private static readonly byte[] Mp3 = [0xFF, 0xFB, 0x90, 0x64, 1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly PositionGreeksTests.FixedLots Lots = new(65);

    private const string PollyRefusal =
        """{"message":"User: the desk's role in account 123456789012 is not authorized to perform: polly:SynthesizeSpeech because no identity-based policy allows the polly:SynthesizeSpeech action"}""";

    private readonly string _dbName = Guid.NewGuid().ToString("N");
    private readonly List<Process> _processes = [];
    private readonly string _root = Directory.CreateTempSubdirectory("ira-").FullName;

    public void Dispose()
    {
        foreach (var p in _processes)
        {
            try
            {
                if (!p.HasExited) p.Kill();
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            p.Dispose();
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder left behind is harmless.
        }
    }

    // ======================================================== the AI Trader

    [Fact]
    public async Task The_ai_trader_acting_live_is_told_with_what_it_did_and_its_own_reason_cut_short()
    {
        using (var db = Db())
        {
            Decision(db, Now.AddMinutes(-20), AiTraderPlan.Buy,
                """{"contract":{"symbol":"NSE:NIFTY26OCT25100CE"},"placed":{"bookRunId":40,"orderId":501,"positionId":601,"groupId":"g1","symbol":"NSE:NIFTY26OCT25100CE","lots":2,"lotSize":65,"price":120.5,"priceBasis":"ask","stopLoss":95,"target":170}}""",
                "NIFTY broke above the morning high with call OI building. It also held VWAP for an hour.");
            db.PaperOrders.Add(new PaperOrder { Id = 502, SimulationRunId = 40, Symbol = "NSE:NIFTY26OCT25100CE", Side = "SELL", Quantity = 2, FillPrice = 88.5m, Status = "Filled" });
            Decision(db, Now.AddMinutes(-15), AiTraderPlan.Exit,
                """{"exit":{"bookRunId":40,"positionId":601,"symbol":"NSE:NIFTY26OCT25100CE","orderId":502,"price":88.5,"bidSeen":88.6}}""",
                "Momentum faded below VWAP");
            Decision(db, Now.AddMinutes(-10), AiTraderPlan.StartStrategy,
                """{"started":{"runId":512,"strategy":"GhostTangentCrossings","underlying":"BANKNIFTY","lots":1}}""",
                "A trend day is setting up on BANKNIFTY.");
            db.SimulationRuns.Add(new SimulationRun { Id = 512, UserId = 99, Mode = StrategyRunControl.LivePaperMode, Status = "Stopped", StrategyName = "GhostTangentCrossings",
                Symbol = "NSE:NIFTYBANK-INDEX", ParametersJson = """{"underlying":"BANKNIFTY"}""" });
            Decision(db, Now.AddMinutes(-5), AiTraderPlan.StopStrategy, """{"stopped":{"runId":512,"flattened":2}}""",
                "The trend day did not come, and the range held between the two walls of open interest that the options desk had named this morning before the open.");
            db.SaveChanges();
        }

        var told = await Events(Now);

        Assert.Equal(
        [
            "The AI Trader bought 2 lots of NIFTY 25100 CE at ₹120.50, with a stop at ₹95 and a target at ₹170. Its reason: NIFTY broke above the morning high with call OI building.",
            "The AI Trader exited 2 lots of NIFTY 25100 CE at ₹88.50. Its reason: Momentum faded below VWAP.",
            "The AI Trader started Ghost Tangent Crossings on BANKNIFTY with 1 lot, run 512. Its reason: A trend day is setting up on BANKNIFTY.",
            "The AI Trader stopped run 512, Ghost Tangent Crossings on BANKNIFTY, and squared off 2 positions. Its reason: The trend day did not come, and the range held between the two walls of open interest that the options desk had named this morning before…",
        ], told.Select(e => e.Text));
        Assert.All(told, e => Assert.Equal(IraDesk.AiTraderKind, e.Kind));
        Assert.All(told, e => Assert.True(e.Text.Length <= IraAlertText.MaxChars));
    }

    [Fact]
    public async Task Shadow_replay_refused_and_old_decisions_are_not_told()
    {
        const string placed = """{"placed":{"symbol":"NSE:NIFTY26OCT25100CE","lots":1,"price":100,"stopLoss":80,"target":140}}""";
        using (var db = Db())
        {
            Decision(db, Now.AddMinutes(-5), AiTraderPlan.Buy, placed, "Shadow.", mode: AiTraderModes.Shadow);
            Decision(db, Now.AddMinutes(-5), AiTraderPlan.Buy, placed, "A replay.", mode: AiTraderModes.Replay, replaySessionId: 3);
            Decision(db, Now.AddMinutes(-5), AiTraderPlan.Buy, "{}", "Refused.", executed: false, allowed: false);
            Decision(db, Now.AddMinutes(-5), AiTraderPlan.None, "{}", "Nothing to do.", executed: false);
            Decision(db, Now.AddMinutes(-31), AiTraderPlan.Buy, placed, "Placed more than half an hour ago.");
            db.SaveChanges();
        }

        Assert.Empty(await Events(Now));
    }

    [Fact]
    public async Task A_buy_waiting_for_the_validator_holds_the_mark_and_is_told_once_it_is_placed()
    {
        Link(Owner, "upendra");
        var rig = Start();
        await rig.Service.RunOnceAsync(Now, CancellationToken.None);
        long waiting;
        using (var db = Db())
        {
            waiting = Decision(db, Now.AddSeconds(10), AiTraderPlan.Buy, """{"contract":{"symbol":"NSE:NIFTY26OCT25100CE"}}""", "Breakout.",
                executed: false, error: AiTraderAgent.AwaitingChecks);
            db.SaveChanges();
        }

        await rig.Service.RunOnceAsync(Now.AddMinutes(1), CancellationToken.None);
        Assert.Empty(rig.Bot.Texts);

        // A later decision is told while the earlier one still waits.
        using (var db = Db())
        {
            db.PaperOrders.Add(new PaperOrder { Id = 777, SimulationRunId = 40, Symbol = "NSE:BANKNIFTY26OCT57000PE", Side = "SELL", Quantity = 1, FillPrice = 210m, Status = "Filled" });
            Decision(db, Now.AddSeconds(70), AiTraderPlan.Exit,
                """{"exit":{"symbol":"NSE:BANKNIFTY26OCT57000PE","orderId":777,"price":210}}""", "Target in sight.");
            db.SaveChanges();
        }

        await rig.Service.RunOnceAsync(Now.AddMinutes(2), CancellationToken.None);
        Assert.Equal(["The AI Trader exited 1 lot of BANKNIFTY 57000 PE at ₹210. Its reason: Target in sight."], rig.Bot.TextsTo(Owner));

        using (var db = Db())
        {
            var row = db.AiTraderDecisions.Single(d => d.Id == waiting);
            row.Executed = true;
            row.Error = string.Empty;
            row.ResultJson = """{"placed":{"symbol":"NSE:NIFTY26OCT25100CE","lots":1,"price":101.25,"stopLoss":80,"target":140}}""";
            db.SaveChanges();
        }

        await rig.Service.RunOnceAsync(Now.AddMinutes(3), CancellationToken.None);
        await rig.Service.RunOnceAsync(Now.AddMinutes(4), CancellationToken.None);

        Assert.Equal(
        [
            "The AI Trader exited 1 lot of BANKNIFTY 57000 PE at ₹210. Its reason: Target in sight.",
            "The AI Trader bought 1 lot of NIFTY 25100 CE at ₹101.25, with a stop at ₹80 and a target at ₹140. Its reason: Breakout.",
        ], rig.Bot.TextsTo(Owner));
    }

    // ======================================================== risk rules

    [Fact]
    public async Task Risk_rules_stopping_a_live_run_or_closing_a_group_are_told_with_the_run_s_net_after_charges()
    {
        long stopped, grouped;
        using (var db = Db())
        {
            Users(db);
            stopped = Run(db, 1, "GhostTangentCrossings", "NIFTY", "Stopped");
            Closed(db, stopped, "G1", "NSE:NIFTY26OCT25100CE", -5000m);
            // Two fills: the run's charges, which its net is after.
            db.PaperOrders.Add(new PaperOrder { SimulationRunId = stopped, Symbol = "NSE:NIFTY26OCT25100CE", Side = "BUY", Quantity = 2, FillPrice = 100m, Status = "Filled" });
            db.PaperOrders.Add(new PaperOrder { SimulationRunId = stopped, Symbol = "NSE:NIFTY26OCT25100CE", Side = "SELL", Quantity = 2, FillPrice = 61.5m, Status = "Filled" });
            Signal(db, stopped, StrategyRunControl.RunStoppedSignalType, "", new { reason = "Stop loss hit: P&L −5,210.00 ≤ −5,000.00", by = "risk-guard" }, Now.AddMinutes(-4));

            grouped = Run(db, 7, "Fulcrum", "BANKNIFTY", "Running");
            Closed(db, grouped, "G1", "NSE:BANKNIFTY26OCT57000CE", 1240m);
            Signal(db, grouped, "CLOSE_GROUP", "G1", new { reason = "Group target hit: G1 P&L 1,240.00 ≥ 1,000.00", by = "risk-guard", system = true }, Now.AddMinutes(-3));

            // Not told: a leg's own stop, a person's stop, a recap, a backtest.
            Signal(db, grouped, "CLOSE_GROUP", "G2", new { reason = "Leg stop-loss hit: BANKNIFTY 57000 CE −21.4 pts (−2.6%) ≤ −20 pts", by = "risk-guard", system = true }, Now.AddMinutes(-2));
            long byHand = Run(db, 1, "Ghost", "NIFTY", "Stopped");
            Signal(db, byHand, StrategyRunControl.RunStoppedSignalType, "", new { reason = "Stopped by upendra", by = "upendra" }, Now.AddMinutes(-2));
            long recap = Run(db, 1, "Ghost", "NIFTY", "Stopped", parameters: """{"session":"recap","recap_date":"2026-10-09","underlying":"NIFTY"}""");
            Signal(db, recap, StrategyRunControl.RunStoppedSignalType, "", new { reason = "Stop loss hit: P&L −900.00 ≤ −500.00", by = "risk-guard" }, Now.AddMinutes(-2));
            long backtest = Run(db, 1, "Ghost", "NIFTY", "Stopped", mode: "Backtest");
            Signal(db, backtest, StrategyRunControl.RunStoppedSignalType, "", new { reason = "Target hit: P&L 900.00 ≥ 500.00", by = "risk-guard" }, Now.AddMinutes(-2));
            db.SaveChanges();
        }

        decimal charges;
        using (var db = Db()) charges = await new RunCharges(db, Lots).ForRunAsync(stopped, CancellationToken.None);
        Assert.Equal(68.28m, Math.Round(charges, 2));

        var told = await Events(Now);

        Assert.Equal(
        [
            "Risk rules stopped Ghost Tangent Crossings on NIFTY in admin's account: its overall stop loss was hit. The run ended ₹5,068 down after charges.",
            "Risk rules closed group G1 of Fulcrum on BANKNIFTY in coderforchange's account: its group target was hit. The run is ₹1,240 up after charges so far.",
        ], told.Select(e => e.Text));
        Assert.All(told, e => Assert.Equal(IraDesk.RiskKind, e.Kind));
    }

    [Theory]
    [InlineData("Stop loss hit: P&L −5,210.00 ≤ −5,000.00", "stop loss")]
    [InlineData("Trailing stop hit: P&L ₹1,200 fell ₹800 from peak ₹2,000 (trail ₹800)", "trailing stop")]
    [InlineData("Target hit: P&L 6,020.00 ≥ 6,000.00", "target")]
    [InlineData("Group stop-loss hit: G1 P&L −1,240.00 ≤ −1,000.00", "stop loss")]
    [InlineData("Group trailing stop hit: G2 P&L ₹300 fell ₹500 from peak ₹800 (trail ₹500)", "trailing stop")]
    [InlineData("Group target hit: G1 P&L 1,240.00 ≥ 1,000.00", "target")]
    public void A_risk_reason_names_its_rule(string reason, string rule) => Assert.Equal(rule, IraAlertText.RuleOf(reason));

    // ======================================================== Sentinel

    [Fact]
    public async Task A_high_or_critical_incident_is_told_by_its_title_and_the_first_line_of_its_summary()
    {
        using (var db = Db())
        {
            Incident(db, IncidentSeverity.Critical, "Dhan feed silent", Now.AddMinutes(-3), "No tick for 5 minutes since 10:52 IST.\nEvidence: Recv-Q 950 KB");
            Incident(db, IncidentSeverity.High, "admin is down ₹1,03,383 today", Now.AddMinutes(-2), null);
            Incident(db, IncidentSeverity.Medium, "A medium one", Now.AddMinutes(-1), "Not told.");
            db.SaveChanges();
        }

        var told = await Events(Now);

        Assert.Equal(
        [
            "Sentinel opened a critical incident: Dhan feed silent. No tick for 5 minutes since 10:52 IST.",
            "Sentinel opened a high incident: admin is down ₹1,03,383 today.",
        ], told.Select(e => e.Text));
    }

    // ======================================================== the briefings' words

    [Fact]
    public async Task The_morning_briefing_reads_the_latest_ok_analyst_reports_and_the_morning_plan()
    {
        var at = Ist(9, 10);
        long live;
        using (var db = Db())
        {
            Users(db);
            Report(db, AiCatalog.MacroAnalyst, Ist(8, 46), AiReportStatus.Ok, """{"bias":"lean bullish","sizeModifier":0.8,"reason":"x"}""");
            Report(db, AiCatalog.TechnicalAnalyst, Ist(8, 21), AiReportStatus.Ok,
                """{"indices":[{"underlying":"NIFTY","bias":"bullish","grade":"B"},{"underlying":"BANKNIFTY","bias":"neutral","grade":"C"}]}""");
            Report(db, AiCatalog.TechnicalAnalyst, Ist(9, 0), AiReportStatus.Invalid,
                """{"indices":[{"underlying":"NIFTY","bias":"bearish","grade":"A"}]}""");
            Report(db, AiCatalog.OptionsAnalyst, Ist(8, 31), AiReportStatus.Ok,
                """{"indices":[{"underlying":"NIFTY","stance":"range-bound"},{"underlying":"BANKNIFTY","stance":"bearish"},{"underlying":"SENSEX","stance":"neutral"}]}""");
            live = Run(db, 1, "GhostTangentCrossings", "NIFTY", "Running");
            db.SaveChanges();
        }

        string planFile = Path.Combine(_root, "morning-plan.txt");
        await File.WriteAllTextAsync(planFile, """
            accounts: admin coderforchange
            GhostTangentCrossings  NIFTY,BANKNIFTY  2
            Fulcrum                NIFTY            1  -  @admin
            """);
        var registry = new StrategyProcessRegistry(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<StrategyProcessRegistry>.Instance);
        Register(registry, live, 1, "GhostTangentCrossings", "NIFTY");

        using var reading = Db();
        var plan = new DeskPlanBuilder(reading, registry, new Environment(_root), Microsoft.Extensions.Options.Options.Create(new DeskOptions { PlanFile = planFile }));
        string text = IraAlertText.Morning(await Desk(reading, plan: plan).MorningAsync(at, CancellationToken.None));

        Assert.Equal(
            "Good morning. Macro: lean bullish, size 0.80. Technical: NIFTY bullish B, BANKNIFTY neutral C; SENSEX not graded. " +
            "Options: NIFTY range-bound, BANKNIFTY bearish, SENSEX neutral. Morning plan: 1 of 3 runs started for admin, 0 of 2 for coderforchange.",
            text);
        Assert.True(text.Length <= IraAlertText.MaxChars);
    }

    [Fact]
    public async Task The_morning_briefing_says_plainly_what_is_missing()
    {
        using var db = Db();

        string text = IraAlertText.Morning(await Desk(db).MorningAsync(Ist(9, 10), CancellationToken.None));

        Assert.Equal("Good morning. No Macro report yet today. No Technical report yet today. No Options report yet today. The morning plan could not be read.", text);
    }

    [Fact]
    public async Task The_close_briefing_reads_today_s_own_account_and_run_figures()
    {
        using var desk = new RunnerDesk();
        var at = Ist(15, 40);
        SeedToday(desk, RunnerDesk.AdminId, "BANKNIFTY", "Fulcrum", -86804m, at);
        SeedToday(desk, RunnerDesk.AdminId, "NIFTY", "Ghost", 5000m, at);
        SeedToday(desk, RunnerDesk.TraderId, "NIFTY", "Ghost", 1200m, at);
        SeedToday(desk, RunnerDesk.AdminId, "NIFTY", "Ghost", 99999m, at, recap: true);

        var db = desk.Db();
        var today = TodayFor(desk, db, at);
        string text = IraAlertText.Close(await new IraDesk(db, new RunPnl(db, Lots, new RunCharges(db, Lots)), today).CloseAsync(at, CancellationToken.None));

        Assert.Equal(
            "The market has closed. Day net after charges: admin ₹81,804 down, coderforchange ₹1,200 up. Best run: Ghost on NIFTY for admin, ₹5,000 up. " +
            "Worst run: Fulcrum on BANKNIFTY for admin, ₹86,804 down. The AI Trader was off.",
            text);
    }

    [Fact]
    public void The_close_briefing_says_the_ai_trader_s_day_in_each_mode()
    {
        var run = new IraRunNet(1, "Ghost", "NIFTY", "admin", 2500.4m);
        IraClose Day(IraTraderDay? trader) => new([new IraAccountNet("admin", 2500.4m)], run, run, 1, trader);

        Assert.Equal(
            "The market has closed. Day net after charges: admin ₹2,500 up. The only run: Ghost on NIFTY for admin, ₹2,500 up. The AI Trader acted twice; its book is ₹1,240 up after charges.",
            IraAlertText.Close(Day(new IraTraderDay(AiTraderModes.Live, 2, 1, 1240.4m))));
        Assert.EndsWith("The AI Trader made no trade.", IraAlertText.Close(Day(new IraTraderDay(AiTraderModes.Live, 0, 0, 0m))));
        Assert.EndsWith("The AI Trader, in shadow mode, made 3 paper trades, ₹450 down after charges.",
            IraAlertText.Close(Day(new IraTraderDay(AiTraderModes.Shadow, 3, 3, -450m))));
        Assert.Equal("The market has closed. No live runs today. The AI Trader was off.", IraAlertText.Close(new IraClose([], null, null, 0, null)));
    }

    [Fact]
    public void Money_reads_in_whole_rupees_with_indian_grouping_and_a_long_message_ends_at_a_sentence()
    {
        Assert.Equal(("₹1,03,383 down", "₹1,240 up", "flat"), (IraAlertText.Signed(-103383.4m), IraAlertText.Signed(1239.5m), IraAlertText.Signed(0.4m)));
        Assert.Equal(("₹120.50", "₹95", "₹12,34,567"), (IraAlertText.Rupees(120.5m), IraAlertText.Rupees(95m), IraAlertText.Rupees(1234567m)));
        Assert.Equal("Fulcrum 2 Straddle Buy 20", IraAlertText.Name("Fulcrum2StraddleBuy20"));

        string longer = string.Join(' ', Enumerable.Range(1, 30).Select(i => $"Sentence number {i} is here."));
        string fitted = IraAlertText.Fit(longer);
        Assert.True(fitted.Length <= IraAlertText.MaxChars);
        Assert.EndsWith(".", fitted);
        Assert.StartsWith(fitted, longer);
    }

    // ======================================================== the marks

    [Fact]
    public async Task The_mark_survives_a_restart_and_history_older_than_half_an_hour_is_not_told()
    {
        Link(Owner, "upendra");
        using (var db = Db())
        {
            // There before Ira ever ran: never told.
            Incident(db, IncidentSeverity.Critical, "Before Ira", Now.AddMinutes(-5), null);
            db.SaveChanges();
        }

        var first = Start();
        await first.Service.RunOnceAsync(Now, CancellationToken.None);
        Assert.Empty(first.Bot.Texts);

        using (var db = Db())
        {
            Incident(db, IncidentSeverity.High, "Feed slow", Now.AddSeconds(30), null);
            db.SaveChanges();
        }

        await first.Service.RunOnceAsync(Now.AddMinutes(1), CancellationToken.None);
        Assert.Equal(["Sentinel opened a high incident: Feed slow."], first.Bot.TextsTo(Owner));

        // The API is down from 11:02 to 11:50; two incidents open meanwhile.
        using (var db = Db())
        {
            Incident(db, IncidentSeverity.Critical, "Opened at 11:05", Now.AddMinutes(5), null);
            Incident(db, IncidentSeverity.High, "Opened at 11:45", Now.AddMinutes(45), null);
            db.SaveChanges();
        }

        var second = Start();
        await second.Service.RunOnceAsync(Now.AddMinutes(50), CancellationToken.None);
        await second.Service.RunOnceAsync(Now.AddMinutes(51), CancellationToken.None);

        Assert.Equal(["Sentinel opened a high incident: Opened at 11:45."], second.Bot.TextsTo(Owner));
        using var check = Db();
        var marks = IraMarks.FromJson(check.SystemSettings.Single(s => s.Key == IraMarks.Key).Value)!;
        Assert.Equal((4L, 4L, 0), (marks.Incidents.Through, marks.Incidents.Seen, marks.Incidents.Told.Count));
    }

    [Fact]
    public void A_cursor_never_passes_a_row_it_has_not_seen_for_a_pass()
    {
        var cursor = IraCursor.At(10);

        // Row 12 is read; row 11 was still committing.
        Assert.Equal([12L], cursor.Pass([(12, IraVerdict.Tell)], truncated: false, newest: 12));
        Assert.Equal((10L, 12L), (cursor.Through, cursor.Seen));
        Assert.Equal([12L], cursor.Told);

        // A pass later both are there: 11 is told, 12 is not told again, and the mark moves to 12.
        Assert.Equal([11L], cursor.Pass([(11, IraVerdict.Tell), (12, IraVerdict.Tell)], truncated: false, newest: 12));
        Assert.Equal(12L, cursor.Through);
        Assert.Empty(cursor.Told);

        // A row waited for holds the mark below it.
        Assert.Empty(cursor.Pass([(13, IraVerdict.Wait), (14, IraVerdict.Skip)], truncated: false, newest: 14));
        Assert.Empty(cursor.Pass([(13, IraVerdict.Wait), (14, IraVerdict.Skip)], truncated: false, newest: 14));
        Assert.Equal(12L, cursor.Through);
        Assert.Equal([14L], cursor.Told);
        Assert.Equal([13L], cursor.Pass([(13, IraVerdict.Tell), (14, IraVerdict.Skip)], truncated: false, newest: 14));
        Assert.Equal(14L, cursor.Through);

        // A batch cut short never moves the mark past its last row.
        var busy = IraCursor.At(0);
        busy.Pass([(1, IraVerdict.Skip)], truncated: false, newest: 900);
        busy.Pass([(5, IraVerdict.Skip), (6, IraVerdict.Skip)], truncated: true, newest: 900);
        Assert.Equal(6L, busy.Through);
    }

    // ======================================================== the cap

    [Fact]
    public async Task Past_the_hourly_cap_events_fold_into_and_n_more_on_the_next_message()
    {
        Link(Owner, "upendra");
        var rig = Start(tweak: s => s.Voice.Alerts.MaxPerHour = 2);
        await rig.Service.RunOnceAsync(Now.AddMinutes(-1), CancellationToken.None);
        using (var db = Db())
        {
            foreach (string title in new[] { "One", "Two", "Three", "Four" }) Incident(db, IncidentSeverity.High, title, Now.AddSeconds(-30), null);
            db.SaveChanges();
        }

        await rig.Service.RunOnceAsync(Now, CancellationToken.None);
        Assert.Equal(["Sentinel opened a high incident: One.", "Sentinel opened a high incident: Two."], rig.Bot.TextsTo(Owner));

        using (var db = Db())
        {
            Incident(db, IncidentSeverity.High, "Five", Now.AddMinutes(29), null);
            db.SaveChanges();
        }

        await rig.Service.RunOnceAsync(Now.AddMinutes(30), CancellationToken.None);
        Assert.Equal(2, rig.Bot.TextsTo(Owner).Count);

        using (var db = Db())
        {
            Incident(db, IncidentSeverity.Critical, "Six", Now.AddMinutes(60), null);
            db.SaveChanges();
        }

        await rig.Service.RunOnceAsync(Now.AddMinutes(61), CancellationToken.None);
        Assert.Equal("Sentinel opened a critical incident: Six. And 3 more alerts since 10:59 IST: see Today on the console.", rig.Bot.TextsTo(Owner)[^1]);
        Assert.Equal(3, rig.Bot.TextsTo(Owner).Count);
    }

    [Fact]
    public async Task What_was_folded_is_said_on_its_own_once_the_hour_has_room_and_briefings_are_not_counted()
    {
        Link(Owner, "upendra");
        var rig = Start(tweak: s => s.Voice.Alerts.MaxPerHour = 1, desk: db => new FixedBriefings(Desk(db), Morning, Closing));
        await rig.Service.RunOnceAsync(Ist(9, 0), CancellationToken.None);
        using (var db = Db())
        {
            Incident(db, IncidentSeverity.High, "One", Ist(9, 0).AddSeconds(30), null);
            Incident(db, IncidentSeverity.High, "Two", Ist(9, 0).AddSeconds(40), null);
            db.SaveChanges();
        }

        await rig.Service.RunOnceAsync(Ist(9, 1), CancellationToken.None);
        // The briefing goes past the cap, and carries what was folded.
        await rig.Service.RunOnceAsync(Ist(9, 10), CancellationToken.None);
        using (var db = Db())
        {
            Incident(db, IncidentSeverity.High, "Three", Ist(9, 20), null);
            db.SaveChanges();
        }

        await rig.Service.RunOnceAsync(Ist(9, 21), CancellationToken.None);
        await rig.Service.RunOnceAsync(Ist(10, 2), CancellationToken.None);

        Assert.Equal(
        [
            "Sentinel opened a high incident: One.",
            MorningText + " And 1 more alert since 09:00 IST: see Today on the console.",
            "And 1 more alert since 09:20 IST: see Today on the console.",
        ], rig.Bot.TextsTo(Owner));
    }

    // ======================================================== /alerts

    [Fact]
    public async Task Alerts_off_stops_events_and_briefings_for_that_owner_only()
    {
        Link(Owner, "upendra");
        Link(Partner, "coderforchange");
        var rig = Start(desk: db => new FixedBriefings(Desk(db), Morning, Closing));

        await rig.Telegram.HandleAsync(Message(Partner, "/alerts off"), CancellationToken.None);
        await rig.Telegram.HandleAsync(Message(Partner, "/alerts"), CancellationToken.None);
        await rig.Telegram.HandleAsync(Message(Owner, "/alerts"), CancellationToken.None);
        await rig.Telegram.HandleAsync(Message(Owner, "/alerts maybe"), CancellationToken.None);
        Assert.Equal([IraAlertText.AlertsOff, IraAlertText.AlertsOff], rig.Bot.TextsTo(Partner));
        Assert.Equal(
        [
            "Alerts are on. I speak first when the AI Trader acts live, when risk rules stop a run or close a group, when Sentinel opens a high or " +
            "critical incident, and when a new circular applies to the desk, at most 8 an hour. On NSE trading days I also brief you at 09:10 and " +
            "15:40 IST. /alerts off stops them.",
            IraAlertText.AlertsUsage,
        ], rig.Bot.TextsTo(Owner));
        rig.Bot.Clear();

        await rig.Service.RunOnceAsync(Ist(9, 8), CancellationToken.None);
        using (var db = Db())
        {
            Incident(db, IncidentSeverity.Critical, "Dhan feed silent", Ist(9, 9), null);
            db.SaveChanges();
        }

        await rig.Service.RunOnceAsync(Ist(9, 10), CancellationToken.None);
        Assert.Equal(["Sentinel opened a critical incident: Dhan feed silent.", MorningText], rig.Bot.TextsTo(Owner));
        Assert.Empty(rig.Bot.TextsTo(Partner));

        await rig.Telegram.HandleAsync(Message(Partner, "/alerts on"), CancellationToken.None);
        using (var db = Db())
        {
            Incident(db, IncidentSeverity.High, "Feed back", Ist(9, 11), null);
            db.SaveChanges();
        }

        await rig.Service.RunOnceAsync(Ist(9, 12), CancellationToken.None);
        Assert.Equal("Sentinel opened a high incident: Feed back.", rig.Bot.TextsTo(Owner)[^1]);
        Assert.Equal([rig.Bot.TextsTo(Partner)[0], "Sentinel opened a high incident: Feed back."], rig.Bot.TextsTo(Partner));
        Assert.StartsWith("Alerts are on.", rig.Bot.TextsTo(Partner)[0]);
    }

    [Fact]
    public async Task With_the_desk_wide_switch_off_nothing_is_sent_and_alerts_says_so()
    {
        Link(Owner, "upendra");
        var rig = Start(tweak: s => s.Voice.Alerts.Enabled = false, desk: db => new FixedBriefings(Desk(db), Morning, Closing));

        await rig.Service.RunOnceAsync(Ist(9, 10), CancellationToken.None);
        await rig.Telegram.HandleAsync(Message(Owner, "/alerts"), CancellationToken.None);

        Assert.Equal([IraAlertText.AlertsOn(rig.Options.CurrentValue.Voice.Alerts) + IraAlertText.DeskOff], rig.Bot.TextsTo(Owner));
        using var db = Db();
        Assert.False(db.SystemSettings.Any(s => s.Key == IraMarks.Key));
    }

    // ======================================================== the briefings' days

    [Fact]
    public async Task The_briefings_go_once_on_a_trading_day_and_never_on_a_weekend_or_a_holiday()
    {
        Link(Owner, "upendra");
        var calendar = new HolidayCalendar(new DateOnly(2026, 10, 20));
        var rig = Start(sessions: new MarketSessionService(calendar), desk: db => new FixedBriefings(Desk(db), Morning, Closing));

        // Monday 12 Oct: one morning briefing at 09:10 (not again at 09:11), one at the close.
        await rig.Service.RunOnceAsync(Ist(9, 9), CancellationToken.None);
        await rig.Service.RunOnceAsync(Ist(9, 10), CancellationToken.None);
        await rig.Service.RunOnceAsync(Ist(9, 11), CancellationToken.None);
        await rig.Service.RunOnceAsync(Ist(15, 39), CancellationToken.None);
        await rig.Service.RunOnceAsync(Ist(15, 40), CancellationToken.None);
        await rig.Service.RunOnceAsync(Ist(15, 41), CancellationToken.None);
        Assert.Equal([MorningText, ClosingText], rig.Bot.TextsTo(Owner));

        // Saturday 17 Oct, and Tuesday 20 Oct, a holiday in the calendar: nothing.
        foreach (var day in new[] { new DateOnly(2026, 10, 17), new DateOnly(2026, 10, 18), new DateOnly(2026, 10, 20) })
        {
            await rig.Service.RunOnceAsync(At(day, 9, 10), CancellationToken.None);
            await rig.Service.RunOnceAsync(At(day, 15, 40), CancellationToken.None);
        }

        Assert.Equal(2, rig.Bot.TextsTo(Owner).Count);

        // Wednesday 21 Oct: the morning briefing missed by more than half an hour is not sent late.
        await rig.Service.RunOnceAsync(At(new DateOnly(2026, 10, 21), 9, 41), CancellationToken.None);
        Assert.Equal(2, rig.Bot.TextsTo(Owner).Count);
    }

    // ======================================================== the voice

    [Fact]
    public async Task Each_message_is_text_then_the_same_words_spoken_once_for_every_owner()
    {
        Link(Owner, "upendra");
        Link(Partner, "coderforchange");
        var rig = Start();
        await rig.Service.RunOnceAsync(Now, CancellationToken.None);
        using (var db = Db())
        {
            Incident(db, IncidentSeverity.High, "Feed slow", Now.AddSeconds(30), null);
            db.SaveChanges();
        }

        await rig.Service.RunOnceAsync(Now.AddMinutes(1), CancellationToken.None);

        Assert.Equal(["text:5550001", "voice:5550001", "text:5550002", "voice:5550002"], rig.Bot.Order);
        Assert.All(rig.Bot.Voices, v => Assert.Equal(Mp3, v.Audio));
        // Polly was asked once, with the words of the text.
        var spoke = Assert.Single(rig.Aws.Spoken);
        Assert.Equal("Sentinel opened a high incident: Feed slow.", spoke);
        using var db2 = Db();
        var call = Assert.Single(db2.AiCalls.Where(c => c.AgentKey == AiCatalog.VoiceSpeak));
        Assert.Equal((AiVoiceService.Alert, AiCallOutcome.Ok), (call.Source, call.Outcome));
        Assert.Contains(rig.Log.Entries, e => e.Level == LogLevel.Information && e.Message.StartsWith("Ira: told upendra (incident, text and voice)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Polly_refusing_still_sends_the_text_and_nothing_is_logged_above_information()
    {
        Link(Owner, "upendra");
        var aws = new Aws();
        aws.Speech.Enqueue((403, Encoding.UTF8.GetBytes(PollyRefusal)));
        var rig = Start(aws: aws);
        await rig.Service.RunOnceAsync(Now, CancellationToken.None);
        using (var db = Db())
        {
            Incident(db, IncidentSeverity.Critical, "Dhan feed silent", Now.AddSeconds(30), null);
            db.SaveChanges();
        }

        await rig.Service.RunOnceAsync(Now.AddMinutes(1), CancellationToken.None);
        using (var db = Db())
        {
            Incident(db, IncidentSeverity.High, "Still silent", Now.AddMinutes(2), null);
            db.SaveChanges();
        }

        await rig.Service.RunOnceAsync(Now.AddMinutes(3), CancellationToken.None);

        Assert.Equal(["Sentinel opened a critical incident: Dhan feed silent.", "Sentinel opened a high incident: Still silent."], rig.Bot.TextsTo(Owner));
        Assert.Empty(rig.Bot.Voices);
        // Asked once; then left alone for the hour, as the shared voice's back-off says.
        Assert.Single(rig.Aws.Spoken);
        using var check = Db();
        var call = Assert.Single(check.AiCalls.Where(c => c.AgentKey == AiCatalog.VoiceSpeak));
        Assert.Equal((AiVoiceService.Alert, AiCallOutcome.Refused), (call.Source, call.Outcome));
        Assert.DoesNotContain(rig.Log.Entries, e => e.Level > LogLevel.Information);
        Assert.Single(rig.Log.Entries, e => e.Message.StartsWith("Ira: Amazon Polly refused", StringComparison.Ordinal));
    }

    // ======================================================== fixtures

    private static readonly IraMorning Morning = new(new IraMacro("lean bullish", "0.80"),
        [new IraIndexView("NIFTY", "bullish B"), new IraIndexView("BANKNIFTY", "neutral C"), new IraIndexView("SENSEX", "bullish B")],
        [new IraIndexView("NIFTY", "range-bound"), new IraIndexView("BANKNIFTY", "bearish"), new IraIndexView("SENSEX", "neutral")],
        [new IraPlanAccount("admin", 13, 13), new IraPlanAccount("coderforchange", 10, 10)]);

    private const string MorningText =
        "Good morning. Macro: lean bullish, size 0.80. Technical: NIFTY bullish B, BANKNIFTY neutral C, SENSEX bullish B. " +
        "Options: NIFTY range-bound, BANKNIFTY bearish, SENSEX neutral. Morning plan: 13 of 13 runs started for admin, 10 of 10 for coderforchange.";

    private static readonly IraClose Closing = new(
        [new IraAccountNet("admin", -12340m), new IraAccountNet("coderforchange", 2100m)],
        new IraRunNet(2, "GhostTangentCrossings", "NIFTY", "admin", 5000m),
        new IraRunNet(1, "Fulcrum", "BANKNIFTY", "admin", -17340m),
        3,
        new IraTraderDay(AiTraderModes.Live, 2, 1, 1240m));

    private const string ClosingText =
        "The market has closed. Day net after charges: admin ₹12,340 down, coderforchange ₹2,100 up. Best run: Ghost Tangent Crossings on NIFTY for admin, ₹5,000 up. " +
        "Worst run: Fulcrum on BANKNIFTY for admin, ₹17,340 down. The AI Trader acted twice; its book is ₹1,240 up after charges.";

    [Fact]
    public void The_fixture_briefings_fit_in_one_spoken_message()
    {
        Assert.Equal(MorningText, IraAlertText.Morning(Morning));
        Assert.Equal(ClosingText, IraAlertText.Close(Closing));
        Assert.True(MorningText.Length <= IraAlertText.MaxChars);
        Assert.True(ClosingText.Length <= IraAlertText.MaxChars);
    }

    private static DateTime Ist(int hour, int minute) => At(Day, hour, minute);

    private static DateTime At(DateOnly day, int hour, int minute) => IstTime.FromIst(day.ToDateTime(new TimeOnly(hour, minute)));

    private TradingDbContext Db() => NewDb(_dbName);

    private static IraDesk Desk(TradingDbContext db, TodayBuilder? today = null, DeskPlanBuilder? plan = null) =>
        new(db, new RunPnl(db, Lots, new RunCharges(db, Lots)), today, plan);

    /// <summary>The events a desk with no marks yet has to tell at <paramref name="nowUtc"/>.</summary>
    private async Task<IReadOnlyList<IraEvent>> Events(DateTime nowUtc)
    {
        using var db = Db();
        return await Desk(db).EventsAsync(new IraMarks(), nowUtc, CancellationToken.None);
    }

    private sealed record Rig(IraAlertService Service, TelegramAssistant Telegram, Bot Bot, Aws Aws, AiTestKit.Monitor Options, ListLogger Log);

    private Rig Start(IMarketSessionService? sessions = null, Func<TradingDbContext, IIraDesk>? desk = null, Action<AiSettings>? tweak = null, Aws? aws = null)
    {
        var options = new AiTestKit.Monitor(Settings(tweak));
        var services = new ServiceCollection();
        services.AddScoped(_ => NewDb(_dbName));
        services.AddScoped(sp => desk?.Invoke(sp.GetRequiredService<TradingDbContext>()) ?? (IIraDesk)Desk(sp.GetRequiredService<TradingDbContext>()));
        var scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var bot = new Bot();
        aws ??= new Aws();
        var signer = new FakeSigner();
        // Ira speaking first never writes speech down: Transcribe is a script that would answer nothing.
        var voice = new AiVoiceService(scopes, new BedrockOpenChatClient(new Factory(aws), options, signer),
            TranscribeTestKit.Client(options, new TranscribeTestKit.ScriptedTranscribe()), new PollySpeechClient(new Factory(aws), options, signer),
            options, NullLogger<AiVoiceService>.Instance);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Telegram:BotToken"] = "123:TEST" }).Build();
        var telegram = new TelegramAssistant(scopes, new Factory(bot), config, new TelegramPairing(), options, NullLogger<TelegramAssistant>.Instance,
            voice: new TelegramVoice(voice, NullLogger<TelegramVoice>.Instance));
        var log = new ListLogger();
        var service = new IraAlertService(scopes, telegram, voice, sessions ?? new Session(), options, log);
        return new Rig(service, telegram, bot, aws, options, log);
    }

    private void Link(long telegramUserId, string consoleUser)
    {
        using var db = Db();
        db.SystemSettings.Add(new SystemSetting
        {
            Key = TelegramAssistant.OwnerPrefix + telegramUserId.ToString(CultureInfo.InvariantCulture), Value = consoleUser, Reason = "@" + consoleUser,
            UpdatedBy = consoleUser, CreatedUtc = Now.AddDays(-1), UpdatedUtc = Now.AddDays(-1),
        });
        db.SaveChanges();
    }

    private static JsonObject Message(long user, string text)
    {
        var message = new JsonObject { ["text"] = text };
        message["chat"] = JsonNode.Parse($$"""{"id":{{user}},"type":"private"}""");
        message["from"] = JsonNode.Parse($$"""{"id":{{user}},"username":"u{{user}}","first_name":"X"}""");
        return message;
    }

    private static long Decision(TradingDbContext db, DateTime createdUtc, string action, string resultJson, string reason, string mode = AiTraderModes.Live,
        long? replaySessionId = null, bool executed = true, bool allowed = true, string error = "")
    {
        var row = new AiTraderDecision
        {
            CreatedUtc = createdUtc, ClockUtc = createdUtc, Day = IstTime.DateOf(createdUtc), Mode = mode, ReplaySessionId = replaySessionId, Action = action,
            Underlying = "NIFTY", Reason = reason, Allowed = allowed, Rule = allowed ? "ok" : "size", Executed = executed, ResultJson = resultJson, Error = error,
        };
        db.AiTraderDecisions.Add(row);
        db.SaveChanges();
        return row.Id;
    }

    private static void Users(TradingDbContext db)
    {
        db.AppUsers.Add(new AppUser { Id = 1, UserName = "admin", Role = UserRoles.Admin, IsActive = true });
        db.AppUsers.Add(new AppUser { Id = 7, UserName = "coderforchange", Role = UserRoles.Trader, IsActive = true });
        db.SaveChanges();
    }

    private static long Run(TradingDbContext db, long userId, string strategy, string underlying, string status, string mode = StrategyRunControl.LivePaperMode,
        string? parameters = null)
    {
        var run = new SimulationRun
        {
            UserId = userId, Mode = mode, Status = status, StrategyName = strategy, Symbol = UnderlyingCatalog.SpotSymbolFor(underlying),
            ParametersJson = parameters ?? $$"""{"underlying":"{{underlying}}"}""", CreatedUtc = Now.AddHours(-2), StartedUtc = Now.AddHours(-2),
        };
        db.SimulationRuns.Add(run);
        db.SaveChanges();
        return run.Id;
    }

    private static void Closed(TradingDbContext db, long runId, string group, string symbol, decimal realized) =>
        db.PaperPositions.Add(new PaperPosition
        {
            SimulationRunId = runId, StrategyName = "x", GroupId = group, Symbol = symbol, Direction = "LONG", Quantity = 0, AveragePrice = 100m,
            RealizedPnl = realized, Status = "Closed", OpenedUtc = Now.AddHours(-1), ClosedUtc = Now.AddMinutes(-5),
        });

    private static void Signal(TradingDbContext db, long runId, string type, string group, object metadata, DateTime createdUtc)
    {
        db.SimulationSignals.Add(new SimulationSignal
        {
            SimulationRunId = runId, StrategyName = "x", SignalType = type, GroupId = group, TimestampUtc = createdUtc, CreatedUtc = createdUtc,
            MetadataJson = JsonSerializer.Serialize(metadata),
        });
        db.SaveChanges();
    }

    private static void Incident(TradingDbContext db, string severity, string title, DateTime firstSeenUtc, string? summary)
    {
        db.Incidents.Add(new Incident
        {
            Fingerprint = $"test:{Guid.NewGuid():N}", Agent = "feeds", Rule = "test", Severity = severity, Status = IncidentStatus.Open, Title = title, Summary = summary,
            FirstSeenUtc = firstSeenUtc, LastSeenUtc = firstSeenUtc,
        });
        db.SaveChanges();
    }

    private static void Report(TradingDbContext db, string agent, DateTime atUtc, string status, string dataJson) =>
        db.AiReports.Add(new AiReport
        {
            AgentKey = agent, SubjectType = AiReportSubject.Check, SubjectId = $"{Day:yyyy-MM-dd} {IstTime.ToIst(atUtc):HH:mm}", SessionDate = Day, Status = status,
            Title = agent, DataJson = dataJson, CreatedUtc = atUtc, UpdatedUtc = atUtc,
        });

    /// <summary>A registry entry around a real process that stays alive, as the registry requires.</summary>
    private void Register(StrategyProcessRegistry registry, long runId, long userId, string strategy, string underlying)
    {
        var info = TestSleeper.StartInfo();
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.UseShellExecute = false;
        var process = Process.Start(info)!;
        _processes.Add(process);
        Assert.True(registry.TryAdd(new RunningStrategy(
            StrategyCatalogService.StableId(strategy), strategy, process, "admin", userId, DateTime.UtcNow, runId,
            underlying, $"NSE:{underlying}-INDEX", 2, new RiskRulesDto())));
    }

    private static void SeedToday(RunnerDesk desk, long userId, string underlying, string strategy, decimal net, DateTime atUtc, bool recap = false)
    {
        long id = desk.SeedRun(userId, underlying, "Stopped", strategy);
        using var db = desk.Db();
        var run = db.SimulationRuns.Single(r => r.Id == id);
        run.StartedUtc = run.CreatedUtc = Ist(12, 0);
        run.CompletedUtc = atUtc.AddMinutes(-15);
        if (recap) run.ParametersJson = $$"""{"session":"recap","recap_date":"2026-10-09","underlying":"{{underlying}}"}""";
        db.PaperPositions.Add(new PaperPosition
        {
            SimulationRunId = id, StrategyName = strategy, GroupId = "G1", Symbol = $"NSE:{underlying}26OCT25000CE", Direction = "LONG",
            Quantity = 0, AveragePrice = 100m, RealizedPnl = net, Status = "Closed", OpenedUtc = run.StartedUtc.Value, ClosedUtc = atUtc.AddMinutes(-20),
        });
        db.SaveChanges();
    }

    private static TodayBuilder TodayFor(RunnerDesk desk, TradingDbContext db, DateTime atUtc)
    {
        var charges = new RunCharges(db, Lots);
        var catalog = new StrategyCatalogService(
            new PythonEngineLocator(Microsoft.Extensions.Options.Options.Create(desk.Options), RecapClockTests.Inert<IWebHostEnvironment>.Create()),
            NullLogger<StrategyCatalogService>.Instance);
        var history = new LiveRunHistoryBuilder(db, desk.Registry, catalog, Lots, charges, new RunPnl(db, Lots, charges));
        return new TodayBuilder(db, history, new Session(), new AiSettingsStore(db), RecapClockTests.Inert<IWebHostEnvironment>.Create(), new FixedClock(atUtc));
    }

    /// <summary>Real events, fixed briefings: for the tests about when and to whom, not what they say.</summary>
    private sealed class FixedBriefings(IIraDesk events, IraMorning morning, IraClose close) : IIraDesk
    {
        public Task<IraMarks> StartAsync(CancellationToken cancellationToken) => events.StartAsync(cancellationToken);

        public Task<IReadOnlyList<IraEvent>> EventsAsync(IraMarks marks, DateTime nowUtc, CancellationToken cancellationToken) =>
            events.EventsAsync(marks, nowUtc, cancellationToken);

        public Task<IraMorning> MorningAsync(DateTime nowUtc, CancellationToken cancellationToken) => Task.FromResult(morning);

        public Task<IraClose> CloseAsync(DateTime nowUtc, CancellationToken cancellationToken) => Task.FromResult(close);
    }

    /// <summary>Every day an NSE trading day, 09:15 to 15:30 IST.</summary>
    private sealed class Session : IMarketSessionService
    {
        public MarketSessionInfo GetSessionInfo(DateTime utcNow, string exchange, string segment)
        {
            var day = IstTime.DateOf(utcNow);
            return new MarketSessionInfo
            {
                Exchange = exchange, Segment = segment, UtcNow = utcNow, IsTradingDay = true,
                SessionOpenUtc = At(day, 9, 15), SessionCloseUtc = At(day, 15, 30),
            };
        }

        public bool IsMarketOpen(DateTime utcNow, string exchange, string segment) => true;

        public DateTime GetNextMarketOpenUtc(DateTime utcNow, string exchange, string segment) => utcNow;
    }

    /// <summary>The exchange calendar with one NSE holiday.</summary>
    private sealed class HolidayCalendar(DateOnly holiday) : IMarketCalendar
    {
        public bool IsLoaded => true;

        public MarketHoliday? HolidayOn(string exchange, DateOnly date) =>
            date == holiday ? new MarketHoliday { Exchange = exchange, Date = date, Name = "Dussehra", Closure = MarketClosure.FullDay } : null;

        public MarketSpecialSession? SpecialSessionOn(string exchange, DateOnly date) => null;

        public bool HasYear(string exchange, int year) => true;

        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FixedClock(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    private sealed class Environment(string contentRoot) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = contentRoot;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "AlgoTrading.Api";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRoot;
        public string EnvironmentName { get; set; } = "Test";
    }

    /// <summary>Amazon Polly, scripted: each request's text is kept; nothing leaves the test.</summary>
    private sealed class Aws : HttpMessageHandler
    {
        public Queue<(int Status, byte[] Body)> Speech { get; } = new();

        public List<string> Spoken { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.RequestUri!.Host.StartsWith("polly.", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            Spoken.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!["Text"]!.GetValue<string>());
            var (status, bytes) = Speech.Count > 0 ? Speech.Dequeue() : (200, Mp3);
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(status == 200 ? "audio/mpeg" : "application/json");
            return new HttpResponseMessage((HttpStatusCode)status) { Content = content };
        }
    }

    /// <summary>api.telegram.org, scripted: every text and voice note sent, in order.</summary>
    private sealed class Bot : HttpMessageHandler
    {
        private readonly object _gate = new();

        public List<(long Chat, string Text)> Texts { get; } = [];

        public List<(long Chat, byte[] Audio)> Voices { get; } = [];

        public List<string> Order { get; } = [];

        public List<string> TextsTo(long chat)
        {
            lock (_gate) return Texts.Where(t => t.Chat == chat).Select(t => t.Text).ToList();
        }

        public void Clear()
        {
            lock (_gate)
            {
                Texts.Clear();
                Voices.Clear();
                Order.Clear();
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            string method = path[(path.LastIndexOf('/') + 1)..];
            if (request.Content is MultipartFormDataContent form)
            {
                long chat = 0;
                byte[] audio = [];
                foreach (var part in form)
                {
                    string name = part.Headers.ContentDisposition!.Name!.Trim('"');
                    if (name == "chat_id") chat = long.Parse(await part.ReadAsStringAsync(cancellationToken), CultureInfo.InvariantCulture);
                    if (name == "voice") audio = await part.ReadAsByteArrayAsync(cancellationToken);
                }

                lock (_gate)
                {
                    Voices.Add((chat, audio));
                    Order.Add($"voice:{chat}");
                }

                return Ok("""{"message_id":1}""");
            }

            if (method == "sendMessage")
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                long chat = body["chat_id"]!.GetValue<long>();
                lock (_gate)
                {
                    Texts.Add((chat, body["text"]!.GetValue<string>()));
                    Order.Add($"text:{chat}");
                }
            }

            return Ok("""{"message_id":1}""");
        }

        private static HttpResponseMessage Ok(string result) =>
            new(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"result\":" + result + "}", Encoding.UTF8, "application/json") };
    }

    /// <summary>Keeps every log entry, to show what is logged and that nothing is above Information.</summary>
    private sealed class ListLogger : ILogger<IraAlertService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
