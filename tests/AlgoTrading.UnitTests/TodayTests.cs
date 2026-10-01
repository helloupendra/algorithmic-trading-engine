using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services;
using AlgoTrading.Api.Services.AiAgents;
using AlgoTrading.Api.Services.Today;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Domain.ValueObjects;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The owner's one page: what needs a look, today's trading, the agents, what they learnt, the system and the
/// decisions, from what the desk already keeps. Nothing on it waits for an answer.
/// </summary>
public sealed class TodayTests
{
    // 13:00 IST on a Thursday: the NSE session open, the day's runs started at noon.
    private static readonly DateOnly Day = new(2026, 10, 1);
    private static readonly DateTime Now = IstTime.FromIst(new DateTime(2026, 10, 1, 13, 0, 0));

    [Fact]
    public async Task Todays_live_runs_add_up_by_account_worst_first_and_recaps_are_only_counted()
    {
        using var desk = new RunnerDesk();
        long fulcrum = Seed(desk, RunnerDesk.AdminId, "BANKNIFTY", "Running", "Fulcrum", net: -86804m);
        long ghost = Seed(desk, RunnerDesk.AdminId, "NIFTY", "Stopped", "Ghost", net: 5000m);
        Seed(desk, RunnerDesk.TraderId, "NIFTY", "Stopped", "Ghost", net: 1200m);
        Seed(desk, RunnerDesk.AdminId, "NIFTY", "Stopped", "Ghost", net: 99999m, recap: true);

        var today = await Build(desk).BuildAsync(CancellationToken.None);

        Assert.Equal(new[] { fulcrum, ghost }, today.Trading.Runs.Where(r => r.UserName == "admin").Select(r => r.RunId));
        var admin = today.Trading.Accounts.Single(a => a.UserName == "admin");
        Assert.Equal((-81804m, 1, 1), (admin.Net, admin.RunsLive, admin.RunsStopped));
        Assert.Equal(1, today.Trading.RecapsToday);
        Assert.DoesNotContain(today.Trading.Runs, r => r.Net == 99999m);
    }

    [Fact]
    public async Task What_needs_a_look_comes_first_and_carries_the_ai_explanation()
    {
        using var desk = new RunnerDesk();
        using (var db = desk.Db())
        {
            db.Incidents.Add(new Incident { Id = 206, Fingerprint = "f", Agent = "trading", Rule = "day-loss", Severity = IncidentSeverity.High,
                Status = IncidentStatus.Open, Title = "admin is down ₹1,03,383 today", Summary = "Fulcrum", FirstSeenUtc = Now, LastSeenUtc = Now });
            db.Incidents.Add(new Incident { Id = 207, Fingerprint = "g", Agent = "health", Rule = "x", Severity = IncidentSeverity.Low,
                Status = IncidentStatus.Open, Title = "A low one", FirstSeenUtc = Now, LastSeenUtc = Now });
            db.AiReports.Add(new AiReport { AgentKey = AiCatalog.IncidentExplainer, SubjectType = AiReportSubject.Incident, SubjectId = "206",
                Status = AiReportStatus.Ok, Title = "Fulcrum's charges ate the day", CreatedUtc = Now, UpdatedUtc = Now });
            db.AiReports.Add(new AiReport { AgentKey = AiCatalog.AssistantCheck, SubjectType = AiReportSubject.Check, SubjectId = "2026-10-01",
                Status = AiReportStatus.Invalid, DataJson = """{"passed":7,"total":12}""", CreatedUtc = Now, UpdatedUtc = Now });
            db.AiMemories.Add(new AiMemory { Id = 15, AgentKey = AiCatalog.DeskAssistant, Kind = AiMemoryKind.Lesson, Status = AiMemoryStatus.Active,
                Text = "Read totals.netPnl.", Source = AiMemorySource.Check, CreatedUtc = Now, UpdatedUtc = Now, ActivatedUtc = Now,
                DecidedBy = AssistantCheckAgent.Verified, DecidedUtc = Now });
            db.AiMemories.Add(new AiMemory { Id = 16, AgentKey = AiCatalog.DeskAssistant, Kind = AiMemoryKind.Lesson, Status = AiMemoryStatus.Rejected,
                Text = "x", Source = AiMemorySource.Check, CreatedUtc = Now, UpdatedUtc = Now, DecidedBy = AssistantCheckAgent.DidNotFix, DecidedUtc = Now });
            db.SaveChanges();
        }

        var today = await Build(desk).BuildAsync(CancellationToken.None);

        Assert.Equal(new[] { "high", "high", "info" }, today.Attention.Select(a => a.Level));
        var incident = today.Attention.Single(a => a.Kind == "incident");
        Assert.Equal(("admin is down ₹1,03,383 today", "AI: Fulcrum's charges ate the day", "/system/incidents?id=206"), (incident.Title, incident.Detail, incident.Link));
        Assert.Contains(today.Attention, a => a.Kind == "check" && a.Title == "The Assistant check passed 7 of 12");
        Assert.Equal(("tested by the check", "did not fix its question"), (today.Learning.LearnedToday.Single().How, today.Learning.DroppedToday.Single().Why));
        Assert.Equal(new TodayCheck(7, 12), today.Learning.CheckToday);
        Assert.Equal(2, today.System.OpenIncidents);
    }

    [Fact]
    public async Task The_latest_finished_exam_shows_both_sets()
    {
        using var desk = new RunnerDesk();
        using (var db = desk.Db())
        {
            db.AiReports.Add(new AiReport { Id = 40, AgentKey = AiCatalog.AssistantExam, SubjectType = AiReportSubject.Exam, SubjectId = "1",
                SessionDate = new DateOnly(2026, 9, 27), Status = AiReportStatus.Ok, CreatedUtc = Now.AddDays(-4), UpdatedUtc = Now.AddDays(-4),
                DataJson = """{"repeats":3,"all":{"questions":120},"practice":{"passK":0.7},"holdout":{"passK":0.6}}""" });
            db.AiReports.Add(new AiReport { Id = 41, AgentKey = AiCatalog.AssistantExam, SubjectType = AiReportSubject.Exam, SubjectId = "2",
                SessionDate = new DateOnly(2026, 10, 4), Status = AiReportStatus.Ok, CreatedUtc = Now, UpdatedUtc = Now,
                DataJson = """{"repeats":3,"all":{"questions":150},"practice":{"passK":0.86},"holdout":{"passK":null}}""" });
            db.SaveChanges();
        }

        var today = await Build(desk).BuildAsync(CancellationToken.None);

        Assert.Equal(new TodayExam(41, "2026-10-04", 3, 150, 0.86, null), today.Learning.LatestExam);
    }

    [Fact]
    public async Task The_ai_traders_day_counts_its_looks_and_shows_the_last_three()
    {
        using var desk = new RunnerDesk();
        using (var db = desk.Db())
        {
            new AiSettingsStore(db).SetAgentEnabledAsync(AiCatalog.AiTrader, true, "admin", null).GetAwaiter().GetResult();
            void Add(int minutes, string action, bool allowed, string rule) => db.AiTraderDecisions.Add(new AiTraderDecision
            {
                CreatedUtc = Now, ClockUtc = Now.AddMinutes(-minutes), Day = Day, Mode = AiTraderModes.Shadow, BriefHash = "h", Brief = "b",
                Action = action, Underlying = action == "none" ? "" : "NIFTY", Allowed = allowed, Rule = rule, Reason = $"r{minutes}",
            });
            Add(40, "none", true, "ok");
            Add(30, "buy", true, "ok");
            Add(20, "buy", false, "stop");
            Add(10, "", false, "no-answer");
            db.SaveChanges();
        }

        var today = await Build(desk).BuildAsync(CancellationToken.None);

        var trader = today.AiTrader!;
        Assert.Equal(("on", "shadow", 4, 2, 1, 1, 1), (trader.Status, trader.Mode, trader.Decisions, trader.Actions, trader.Allowed, trader.Refused, trader.NoAnswer));
        Assert.Equal(new[] { "r10", "r20", "r30" }, trader.Latest.Select(d => d.Reason));
    }

    [Fact]
    public async Task A_decision_is_logged_newest_first_and_the_same_one_is_replaced()
    {
        using var desk = new RunnerDesk();
        var builder = Build(desk);

        await builder.RecordAsync(new TodayDecision("2026-10-01", "AI Trader setup", "₹5 lakh paper account", "owner", "decided"), "admin", CancellationToken.None);
        await builder.RecordAsync(new TodayDecision("2026-10-01", "Model provider", "Stay on the NVIDIA trial for now", "Claude (default)", "open"), "admin", CancellationToken.None);
        await builder.RecordAsync(new TodayDecision("2026-10-01", "AI Trader setup", "₹5 lakh paper account 'ai-trader'", "owner", "decided"), "admin", CancellationToken.None);

        var today = await builder.BuildAsync(CancellationToken.None);
        Assert.Equal(new[] { "AI Trader setup", "Model provider" }, today.Decisions.Select(d => d.Title));
        Assert.Equal("₹5 lakh paper account 'ai-trader'", today.Decisions[0].Decided);
        Assert.Contains(today.Attention, a => a.Kind == "decision" && a.Title == "Model provider");
    }

    [Fact]
    public void The_decision_table_holds_the_longest_decision_the_api_accepts()
    {
        // On 1 Oct the log was one varchar(2000) setting and the sixth decision broke it; the in-memory test
        // database never checks lengths, so this compares the model with the API's limits instead.
        using var desk = new RunnerDesk();
        using var db = desk.Db();
        var entity = db.Model.FindEntityType(typeof(OwnerDecision))!;

        Assert.Equal(OwnerDecision.TitleMax, entity.FindProperty(nameof(OwnerDecision.Title))!.GetMaxLength());
        Assert.Equal(OwnerDecision.DecidedMax, entity.FindProperty(nameof(OwnerDecision.Decided))!.GetMaxLength());
    }

    [Theory]
    [InlineData(true, -60, 120, "open")]
    [InlineData(true, 30, 400, "pre-open")]
    [InlineData(true, -400, -10, "closed")]
    [InlineData(false, -60, 120, "holiday")]
    public async Task Each_market_says_where_its_day_stands(bool tradingDay, int openMinutes, int closeMinutes, string state)
    {
        using var desk = new RunnerDesk();
        var today = await Build(desk, new Session(tradingDay, Now.AddMinutes(openMinutes), Now.AddMinutes(closeMinutes))).BuildAsync(CancellationToken.None);
        Assert.All(today.Markets, m => Assert.Equal(state, m.State));
        Assert.Equal(new[] { "NSE", "MCX" }, today.Markets.Select(m => m.Exchange));
    }

    [Fact]
    public void The_page_is_admin_only()
    {
        var attribute = Assert.Single(typeof(TodayController).GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>());
        Assert.Equal(AuthorizationPolicies.AdminOnly, attribute.Policy);
    }

    // ---------- helpers ----------

    private static long Seed(RunnerDesk desk, long userId, string underlying, string status, string strategy, decimal net, bool recap = false)
    {
        long id = desk.SeedRun(userId, underlying, status, strategy);
        using var db = desk.Db();
        var run = db.SimulationRuns.Single(r => r.Id == id);
        run.StartedUtc = run.CreatedUtc = IstTime.FromIst(new DateTime(2026, 10, 1, 12, 0, 0));
        if (status != "Running") run.CompletedUtc = Now.AddMinutes(-5);
        if (recap) run.ParametersJson = $$"""{"session":"recap","recap_date":"2026-09-30","underlying":"{{underlying}}"}""";
        db.PaperPositions.Add(new PaperPosition
        {
            SimulationRunId = id, StrategyName = strategy, GroupId = "G1", Symbol = $"NSE:{underlying}26OCT25000CE", Direction = "LONG",
            Quantity = 0, AveragePrice = 100m, RealizedPnl = net, Status = "Closed", OpenedUtc = run.StartedUtc.Value, ClosedUtc = Now.AddMinutes(-10),
        });
        db.SaveChanges();
        return id;
    }

    private static TodayBuilder Build(RunnerDesk desk, IMarketSessionService? sessions = null)
    {
        var db = desk.Db();
        var lots = new PositionGreeksTests.FixedLots(65);
        var charges = new RunCharges(db, lots);   // no orders seeded: no charges, so a run's net is its legs' P&L
        var catalog = new StrategyCatalogService(
            new PythonEngineLocator(Microsoft.Extensions.Options.Options.Create(desk.Options),
                RecapClockTests.Inert<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>.Create()),
            NullLogger<StrategyCatalogService>.Instance);
        var history = new LiveRunHistoryBuilder(db, desk.Registry, catalog, lots, charges, new RunPnl(db, lots, charges));
        return new TodayBuilder(db, history, sessions ?? new Session(true, Now.AddHours(-4), Now.AddHours(2.5)), new AiSettingsStore(db),
            RecapClockTests.Inert<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>.Create(), new Fixed(Now));
    }

    private sealed class Fixed(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    private sealed class Session(bool tradingDay, DateTime openUtc, DateTime closeUtc) : IMarketSessionService
    {
        public MarketSessionInfo GetSessionInfo(DateTime utcNow, string exchange, string segment) => new()
        {
            Exchange = exchange, Segment = segment, UtcNow = utcNow, IsTradingDay = tradingDay, SessionOpenUtc = openUtc, SessionCloseUtc = closeUtc,
        };

        public bool IsMarketOpen(DateTime utcNow, string exchange, string segment) => tradingDay && utcNow >= openUtc && utcNow < closeUtc;

        public DateTime GetNextMarketOpenUtc(DateTime utcNow, string exchange, string segment) => openUtc;
    }
}
