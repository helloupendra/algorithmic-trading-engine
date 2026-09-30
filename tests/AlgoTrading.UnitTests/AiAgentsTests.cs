using System.Text.Json;
using System.Text.Json.Nodes;
using AlgoTrading.Api.Services.AiAgents;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static AlgoTrading.UnitTests.AiTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The Phase 3 agents the desk runs on its own: when each one's work is due,
/// how its answer is checked before it becomes a report, and the scheduler
/// that runs them.
/// </summary>
public class AiAgentsTests
{
    // Wednesday 30 Sep 2026, in IST.
    private static DateTime Ist(int hour, int minute) => IstTime.FromIst(new DateTime(2026, 9, 30, hour, minute, 0));

    // ---------- the trade reviewer: when a run is due ----------

    [Theory]
    [InlineData(15, 40, false)] // stopped 15:31, but before 15:45
    [InlineData(15, 46, true)]
    public async Task An_NSE_run_is_reviewed_after_15_45(int hour, int minute, bool due)
    {
        var ai = Build();
        long runId = SeedRun(ai, stoppedAt: Ist(15, 31));

        long? next = await Reviewer(ai).NextDueAsync(Ist(hour, minute), CancellationToken.None);

        Assert.Equal(due ? runId : null, next);
    }

    [Fact]
    public async Task A_run_is_reviewed_only_once_it_has_been_stopped_ten_minutes()
    {
        var ai = Build();
        long runId = SeedRun(ai, stoppedAt: Ist(23, 31), started: Ist(9, 0));
        var reviewer = Reviewer(ai);

        Assert.Null(await reviewer.NextDueAsync(Ist(23, 35), CancellationToken.None));
        Assert.Equal(runId, await reviewer.NextDueAsync(Ist(23, 42), CancellationToken.None));
    }

    [Fact]
    public async Task Manual_books_alert_runs_and_running_runs_are_not_reviewed()
    {
        var ai = Build();
        SeedRun(ai, stoppedAt: Ist(15, 31), strategy: "Manual");
        SeedRun(ai, stoppedAt: Ist(15, 31), parameters: """{"role":"alerts"}""");
        SeedRun(ai, stoppedAt: null, status: "Running");

        Assert.Null(await Reviewer(ai).NextDueAsync(Ist(16, 0), CancellationToken.None));
    }

    [Fact]
    public async Task A_failed_review_is_tried_again_later_and_given_up_after_three_tries()
    {
        var ai = Build();
        long runId = SeedRun(ai, stoppedAt: Ist(15, 31));
        ai.Db.AiReports.Add(new AiReport
        {
            AgentKey = AiCatalog.TradeReviewer, SubjectType = AiReportSubject.Run, SubjectId = runId.ToString(),
            Status = AiReportStatus.Failed, Attempts = 1, CreatedUtc = DateTime.UtcNow.AddHours(-1), UpdatedUtc = DateTime.UtcNow.AddHours(-1),
        });
        await ai.Db.SaveChangesAsync();
        var reviewer = Reviewer(ai);

        Assert.Equal(runId, await reviewer.NextDueAsync(Ist(16, 0), CancellationToken.None));

        var report = await ai.Db.AiReports.SingleAsync();
        report.Attempts = 3;
        await ai.Db.SaveChangesAsync();
        Assert.Null(await reviewer.NextDueAsync(Ist(16, 0), CancellationToken.None));
    }

    // ---------- the trade reviewer: the review ----------

    [Fact]
    public async Task A_review_hands_the_model_the_run_and_its_spec_and_keeps_its_verdict()
    {
        var ai = Build(tools: [new FakeTool(AiToolNames.Run, _ => new { run = new { runId = 1, strategy = "Ghost" }, pnl = new { net = -3672.5 } }),
            new FakeTool(AiToolNames.StrategySpec, _ => new { name = "Ghost", spec = "Sell the straddle at 09:20; stop at -3,500." })]);
        long runId = SeedRun(ai, stoppedAt: Ist(15, 31));
        ai.Provider.On(Judge1, Answer("""
            ```json
            {"verdict":"followed","title":"Run lost ₹3,672 but kept its stop","followed":["entry at 09:20","overall stop at -3,500"],
             "deviations":[],"staleFills":1,"marketContext":"NIFTY rallied 0.8%.","lesson":"Test a wider stop on trend days.",
             "journal":"The straddle was sold on time and stopped by its rule."}
            ```
            """));

        var report = await Reviewer(ai).RunForAsync(runId.ToString(), CancellationToken.None);

        Assert.Equal(AiReportStatus.Ok, report!.Status);
        Assert.Equal("Run lost ₹3,672 but kept its stop", report.Title);
        Assert.Contains("followed the spec", report.Body);
        Assert.Contains("Fills at a stale quote:** 1", report.Body);
        Assert.Equal("followed", JsonNode.Parse(report.DataJson)!["verdict"]!.GetValue<string>());
        var sent = ai.Provider.Requests.Single().Body;
        Assert.Contains("Sell the straddle at 09:20", sent);
        Assert.Contains("-3672.5", sent);
        var call = await ai.Db.AiCalls.SingleAsync();
        Assert.Equal("schedule", call.Source);
        Assert.Equal(AiCatalog.TradeReviewer, call.RequestedBy);
    }

    [Fact]
    public async Task A_review_not_in_the_asked_shape_is_kept_as_invalid_with_its_text()
    {
        var ai = Build();
        long runId = SeedRun(ai, stoppedAt: Ist(15, 31));
        ai.Provider.On(Judge1, Answer("The run lost money because the market went up."));

        var report = await Reviewer(ai).RunForAsync(runId.ToString(), CancellationToken.None);

        Assert.Equal(AiReportStatus.Invalid, report!.Status);
        Assert.Equal("The run lost money because the market went up.", report.Body);
    }

    [Fact]
    public async Task When_the_reviews_are_done_one_digest_goes_to_Telegram_and_not_twice()
    {
        var ai = Build();
        long runId = SeedRun(ai, stoppedAt: Ist(15, 31));
        ai.Provider.On(Judge1, Answer("""{"verdict":"deviated","title":"Entered at 09:47, not 09:20","journal":"Late entry."}"""));
        var notifier = new Notifier();
        // Reports are stamped at 15:50 IST; the reviewer's ticks come at 16:00, 16:01 and 16:02.
        var reviewer = Reviewer(ai, notifier, new FixedTime(Ist(15, 50)));

        Assert.True(await reviewer.RunOnceAsync(Ist(16, 0), CancellationToken.None));   // reviews the run
        Assert.False(await reviewer.RunOnceAsync(Ist(16, 1), CancellationToken.None));  // nothing due: digest
        Assert.False(await reviewer.RunOnceAsync(Ist(16, 2), CancellationToken.None));  // nothing new: no digest

        var message = Assert.Single(notifier.Messages);
        Assert.Contains("1 run review written: 0 followed the spec, 1 did not", message);
        Assert.Contains($"#{runId}: Entered at 09:47, not 09:20", message);
    }

    // ---------- the news analyst ----------

    [Fact]
    public async Task The_news_batch_takes_recent_unread_items_filings_first_and_never_the_backfill()
    {
        var ai = Build();
        var now = DateTime.UtcNow;
        ai.Db.NewsItems.AddRange(
            News(1, "RBI holds the repo rate at 6.5%", now.AddHours(-2)),
            News(2, "Old 2020 headline", new DateTime(2020, 3, 1, 5, 0, 0, DateTimeKind.Utc), firstSeen: now.AddHours(-1)));
        ai.Db.CorporateAnnouncements.Add(new CorporateAnnouncement
        {
            Id = 7, Symbol = "TCS", Company = "TCS", Subject = "Order win", Details = "An order worth Rs 1,200 crore.",
            AnnouncedUtc = now.AddHours(-1), FirstSeenUtc = now.AddHours(-1), UniqueKey = "k7",
        });
        await ai.Db.SaveChangesAsync();

        var batch = await News(ai).NextBatchAsync(now, CancellationToken.None);

        Assert.Equal(new[] { "f7", "n1" }, batch.Select(i => i.Id));
    }

    [Fact]
    public async Task Each_extracted_record_is_checked_against_its_source_and_stored()
    {
        var ai = Build();
        var now = DateTime.UtcNow;
        ai.Db.NewsItems.AddRange(
            News(1, "Infosys Q2 net profit rises 8% to Rs 6,506 crore", now.AddHours(-1)),
            News(2, "Sensex ends flat", now.AddHours(-1)),
            News(3, "Crude falls 2%", now.AddHours(-1)));
        await ai.Db.SaveChangesAsync();
        ai.Provider.On(Extract1, Answer("""
            {"items":[
              {"id":"n1","event":"results","direction":"positive","symbols":["infy"],"confidence":0.9,"summary":"Infosys profit up 8%",
               "numbers":[{"what":"net profit","value":6506,"unit":"crore","quote":"Rs 6,506 crore"}]},
              {"id":"n2","event":"none","direction":"neutral","symbols":[],"confidence":0.8,"summary":"Flat close",
               "numbers":[{"what":"change","value":0.4,"unit":"%","quote":"up 0.4%"}]}
            ]}
            """));

        Assert.True(await News(ai).RunOnceAsync(now, CancellationToken.None));

        var reports = await ai.Db.AiReports.OrderBy(r => r.SubjectId).ToListAsync();
        Assert.Equal(new[] { AiReportStatus.Ok, AiReportStatus.Invalid, AiReportStatus.Invalid }, reports.Select(r => r.Status));
        Assert.Equal("INFY", JsonNode.Parse(reports[0].DataJson)!["symbols"]![0]!.GetValue<string>());
        Assert.Contains("\"up 0.4%\" is not in the item's text", reports[1].Error);
        Assert.Equal("The answer left this item out.", reports[2].Error);
    }

    [Theory]
    [InlineData("""{"id":"n1","event":"rumour","direction":"positive","confidence":0.5}""", "not one of the listed events")]
    [InlineData("""{"id":"n1","event":"other","direction":"up","confidence":0.5}""", "direction")]
    [InlineData("""{"id":"n1","event":"other","direction":"neutral","confidence":7}""", "confidence")]
    public void A_record_outside_the_asked_lists_is_invalid(string record, string expected)
    {
        var (status, _, _, _, error) = NewsAnalystAgent.Check(JsonNode.Parse(record)!.AsObject(), "Some headline");

        Assert.Equal(AiReportStatus.Invalid, status);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void A_quote_matches_across_case_spacing_and_typographic_quotes()
    {
        var record = JsonNode.Parse("""{"event":"guidance","direction":"positive","confidence":0.6,"numbers":[{"what":"growth","value":12,"unit":"%","quote":"growth of  12%"}]}""")!.AsObject();

        var (status, _, _, _, error) = NewsAnalystAgent.Check(record, "The company expects “Growth of 12%” next year");

        Assert.True(status == AiReportStatus.Ok, error);
    }

    [Fact]
    public async Task The_news_analyst_waits_its_interval_between_batches()
    {
        var ai = Build();
        ai.Db.NewsItems.Add(News(1, "Headline", DateTime.UtcNow.AddHours(-1)));
        await ai.Db.SaveChangesAsync();
        ai.Provider.On(Extract1, Answer("""{"items":[]}"""));
        var state = new AiSchedulerState();
        var news = News(ai, state);
        var now = DateTime.UtcNow;

        Assert.True(await news.RunOnceAsync(now, CancellationToken.None));
        Assert.False(await news.RunOnceAsync(now.AddMinutes(5), CancellationToken.None));
        Assert.Single(ai.Provider.Requests);
    }

    // ---------- the incident explainer ----------

    [Fact]
    public async Task Only_live_incidents_of_medium_or_worse_from_the_last_day_are_explained()
    {
        var ai = Build();
        var now = DateTime.UtcNow;
        ai.Db.Incidents.AddRange(
            Incident(1, IncidentSeverity.Low, IncidentStatus.Open, now.AddHours(-1)),
            Incident(2, IncidentSeverity.High, IncidentStatus.Resolved, now.AddHours(-1)),
            Incident(3, IncidentSeverity.Medium, IncidentStatus.Open, now.AddDays(-3)),
            Incident(4, IncidentSeverity.Critical, IncidentStatus.Acknowledged, now.AddHours(-2)));
        await ai.Db.SaveChangesAsync();

        Assert.Equal(4, await Explainer(ai).NextDueAsync(now, CancellationToken.None));
    }

    [Fact]
    public async Task An_explanation_reads_the_masked_evidence_and_is_stored_in_three_parts()
    {
        var ai = Build();
        ai.Db.Incidents.Add(Incident(9, IncidentSeverity.High, IncidentStatus.Open, DateTime.UtcNow.AddMinutes(-5),
            evidence: """["feed silent 120s","db password=hunter2"]"""));
        await ai.Db.SaveChangesAsync();
        ai.Provider.On(AnalystFirst, Answer("""{"title":"Dhan feed went quiet","what":"No ticks for 2 minutes.","why":"The feed's socket stalled.","do":"Restart the Dhan feed from Data → Feeds.","urgency":"now","confidence":0.7}"""));

        var report = await Explainer(ai).RunForAsync("9", CancellationToken.None);

        Assert.Equal(AiReportStatus.Ok, report!.Status);
        Assert.Contains("**What to do** (now). Restart the Dhan feed", report.Body);
        Assert.DoesNotContain("hunter2", ai.Provider.Requests.Single().Body);
    }

    // ---------- the reports endpoints ----------

    [Fact]
    public async Task Reports_list_by_subject_and_the_news_validity_is_counted_per_agent()
    {
        var ai = Build();
        var day = IstTime.DateOf(DateTime.UtcNow);
        ai.Db.AiReports.AddRange(
            Report(AiCatalog.TradeReviewer, AiReportSubject.Run, "412", AiReportStatus.Ok, day),
            Report(AiCatalog.TradeReviewer, AiReportSubject.Run, "413", AiReportStatus.Ok, day),
            Report(AiCatalog.NewsAnalyst, AiReportSubject.News, "1", AiReportStatus.Ok, day),
            Report(AiCatalog.NewsAnalyst, AiReportSubject.News, "2", AiReportStatus.Ok, day),
            Report(AiCatalog.NewsAnalyst, AiReportSubject.News, "3", AiReportStatus.Ok, day),
            Report(AiCatalog.NewsAnalyst, AiReportSubject.News, "4", AiReportStatus.Invalid, day),
            Report(AiCatalog.NewsAnalyst, AiReportSubject.News, "5", AiReportStatus.Failed, day));
        await ai.Db.SaveChangesAsync();
        var controller = ai.Controller();

        var one = Body<AlgoTrading.Api.Controllers.AiReportPage>(await controller.Reports(agent: AiCatalog.TradeReviewer, subjectId: "412"));
        var stats = Body<AlgoTrading.Api.Controllers.AiReportStats>(await controller.ReportStats(7, CancellationToken.None));

        var review = Assert.Single(one.Reports);
        Assert.Equal("/trade/runs/412", review.Link);
        var news = stats.Agents.Single(a => a.AgentKey == AiCatalog.NewsAnalyst);
        Assert.Equal((5, 3, 1, 1), (news.Total, news.Ok, news.Invalid, news.Failed));
        Assert.Equal(75.0, news.ValidPercent); // failures are not counted against validity: no answer was checked
    }

    private static AiReport Report(string agent, string subjectType, string subjectId, string status, DateOnly day) => new()
    {
        AgentKey = agent, SubjectType = subjectType, SubjectId = subjectId, Status = status, SessionDate = day,
        CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow, Title = $"{subjectType} {subjectId}", Attempts = 1,
    };

    private static T Body<T>(Microsoft.AspNetCore.Mvc.IActionResult result) =>
        Assert.IsType<T>(Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result).Value);

    // ---------- the scheduler ----------

    [Fact]
    public async Task The_scheduler_runs_each_switched_on_agent_and_survives_one_that_throws()
    {
        var ai = Build();
        await ai.Store.SetAgentEnabledAsync(AiCatalog.IncidentExplainer, false, "upendra", null);
        var throws = new StubAgent(AiCatalog.TradeReviewer, fail: true);
        var off = new StubAgent(AiCatalog.IncidentExplainer);
        var runs = new StubAgent(AiCatalog.NewsAnalyst);

        await Scheduler(ai, throws, off, runs).RunTickAsync(CancellationToken.None);

        Assert.Equal(1, throws.Calls);
        Assert.Equal(0, off.Calls);
        Assert.Equal(1, runs.Calls);
    }

    [Fact]
    public async Task Without_a_key_or_with_the_scheduler_off_no_agent_runs()
    {
        var noKey = Build(Settings(s => s.ApiKey = ""));
        var off = Build(Settings(s => s.SchedulerEnabled = false));
        var a = new StubAgent(AiCatalog.NewsAnalyst);
        var b = new StubAgent(AiCatalog.NewsAnalyst);

        await Scheduler(noKey, a).RunTickAsync(CancellationToken.None);
        await Scheduler(off, b).RunTickAsync(CancellationToken.None);

        Assert.Equal(0, a.Calls + b.Calls);
    }

    // ---------- helpers ----------

    private const string AnalystFirst = "moonshotai/kimi-k3";

    private static long SeedRun(Services ai, DateTime? stoppedAt, string strategy = "Ghost", string parameters = """{"underlying":"NIFTY"}""",
        string status = "Stopped", DateTime? started = null)
    {
        var run = new SimulationRun
        {
            UserId = 1, Mode = "LivePaper", Status = status, StrategyName = strategy, Symbol = "NSE:NIFTY50-INDEX",
            ParametersJson = parameters, CreatedUtc = started ?? Ist(9, 15), StartedUtc = started ?? Ist(9, 15), CompletedUtc = stoppedAt,
        };
        ai.Db.SimulationRuns.Add(run);
        ai.Db.SaveChanges();
        return run.Id;
    }

    private static NewsItem News(long id, string title, DateTime published, DateTime? firstSeen = null) => new()
    {
        Id = id, Source = "test", Category = "markets", Title = title, Summary = string.Empty, Link = $"https://example.com/{id}",
        LinkHash = $"h{id}", PublishedUtc = published, FirstSeenUtc = firstSeen ?? published,
    };

    private static Incident Incident(long id, string severity, string status, DateTime firstSeen, string? evidence = null) => new()
    {
        Id = id, Fingerprint = $"f{id}", Agent = "trading", Rule = "feed_silent", Severity = severity, Status = status,
        Title = $"Incident {id}", Summary = "Feed quiet", EvidenceJson = evidence, FirstSeenUtc = firstSeen, LastSeenUtc = firstSeen,
    };

    private static TradeReviewerAgent Reviewer(Services ai, ISystemNotifier? notifier = null, TimeProvider? reportClock = null) => new(
        ai.Db, ai.Gateway, new AiReportWriter(ai.Db, ai.Options, reportClock), ai.Toolbox, new AiSchedulerState(), notifier ?? new Notifier(), ai.Options,
        NullLogger<TradeReviewerAgent>.Instance);

    private sealed class FixedTime(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    private static NewsAnalystAgent News(Services ai, AiSchedulerState? state = null) => new(
        ai.Db, ai.Gateway, new AiReportWriter(ai.Db, ai.Options), state ?? new AiSchedulerState(), ai.Options, NullLogger<NewsAnalystAgent>.Instance);

    private static IncidentExplainerAgent Explainer(Services ai) => new(
        ai.Db, ai.Gateway, new AiReportWriter(ai.Db, ai.Options), new AiSchedulerState(), NullLogger<IncidentExplainerAgent>.Instance);

    private static AiAgentScheduler Scheduler(Services ai, params IAiScheduledAgent[] agents)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => ai.Store);
        foreach (var agent in agents) services.AddScoped(_ => agent);
        return new AiAgentScheduler(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), ai.Options,
            NullLogger<AiAgentScheduler>.Instance);
    }

    private sealed class StubAgent(string key, bool fail = false) : IAiScheduledAgent
    {
        public int Calls { get; private set; }

        public string AgentKey => key;

        public Task<bool> RunOnceAsync(DateTime nowUtc, CancellationToken cancellationToken)
        {
            Calls++;
            return fail ? throw new InvalidOperationException("boom") : Task.FromResult(true);
        }

        public Task<AiReport?> RunForAsync(string? subjectId, CancellationToken cancellationToken) => Task.FromResult<AiReport?>(null);
    }

    private sealed class Notifier : ISystemNotifier
    {
        public List<string> Messages { get; } = [];

        public Task NotifyAsync(NotificationCategory category, NotificationSeverity severity, string title, string message,
            string? underlying = null, string? symbol = null, long? simulationRunId = null, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }

        public Task RecordAsync(NotificationCategory category, NotificationSeverity severity, string title, string message,
            string? underlying = null, string? symbol = null, long? simulationRunId = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
