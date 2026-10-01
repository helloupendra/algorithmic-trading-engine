using System.Text.Json;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Services;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Risk;
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Domain.ValueObjects;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// When a run may start, and what counts as trading. A live run starts only
/// while its market's session is still to come or open today. A recap, which
/// replays a past session to test a strategy, may start any time, and its
/// P&amp;L stays out of every live total (1 Oct: twelve recaps of 11 Sep had
/// added ₹1.57 lakh to the admin account's Ghost month).
/// </summary>
public sealed class RecapRunsTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    // ---------- when a live run may start ----------

    [Theory]
    [InlineData(true, 60, null)]                                  // before or in the session
    [InlineData(true, -1, "NSE closed at")]                       // after today's close
    [InlineData(false, 60, "NSE is closed today")]                // a holiday or a weekend
    public void A_live_run_starts_only_while_todays_session_is_to_come(bool tradingDay, int closeInMinutes, string? refusal)
    {
        var sessions = new Session(tradingDay, Now.AddMinutes(closeInMinutes));

        string? why = StrategyController.WhyMarketClosed(sessions, Now, "NIFTY", "NSE:NIFTY50-INDEX");

        if (refusal is null) Assert.Null(why);
        else Assert.StartsWith(refusal, why);
    }

    [Fact]
    public void A_commodity_run_is_judged_by_the_MCX_session_and_SENSEX_by_BSE()
    {
        var sessions = new Session(true, Now.AddHours(1));

        Assert.Null(StrategyController.WhyMarketClosed(sessions, Now, "CRUDEOIL", "MCX:CRUDEOIL26OCTFUT"));
        Assert.Null(StrategyController.WhyMarketClosed(sessions, Now, "SENSEX", "BSE:SENSEX-INDEX"));
        Assert.Equal(new[] { "MCX", "BSE" }, sessions.Asked.Select(a => a.Exchange));
    }

    [Fact]
    public async Task After_the_close_a_live_start_is_refused_and_leaves_no_row()
    {
        using var desk = new RunnerDesk();

        var refused = Assert.IsType<ConflictObjectResult>(await desk.Start("NIFTY", sessions: new Session(true, Now.AddMinutes(-5))));

        Assert.Contains("NSE closed at", JsonSerializer.Serialize(refused.Value));
        Assert.Empty(desk.Runs());
        Assert.Equal(0, desk.Registry.Count);
    }

    [Fact]
    public async Task A_recap_may_start_after_the_close()
    {
        using var desk = new RunnerDesk();
        var recap = new Dictionary<string, JsonElement>
        {
            ["session"] = JsonSerializer.SerializeToElement("recap"),
            ["recap_date"] = JsonSerializer.SerializeToElement("2026-09-30"),
        };

        Assert.IsType<OkObjectResult>(await desk.Start("NIFTY", sessions: new Session(false, Now.AddMinutes(-5)), parameters: recap));

        var run = Assert.Single(desk.Runs());
        Assert.True(RecapClock.IsRecap(run.ParametersJson));
        Assert.Contains(RecapRuns.Marker, run.ParametersJson);   // the marker every report filters on
    }

    // ---------- recaps are not trading ----------

    [Fact]
    public async Task The_run_history_lists_live_runs_unless_asked_for_the_recaps_and_labels_them()
    {
        using var desk = new RunnerDesk();
        long live = desk.SeedRun(RunnerDesk.AdminId, "NIFTY", "Stopped");
        long recap = desk.SeedRun(RunnerDesk.AdminId, "NIFTY", "Stopped");
        using (var db = desk.Db())
        {
            db.SimulationRuns.Single(r => r.Id == recap).ParametersJson = """{"session":"recap","recap_date":"2026-09-11","underlying":"NIFTY"}""";
            db.SaveChanges();
        }

        await using var read = desk.Db();
        var history = History(desk, read);
        var liveRows = await history.ListAsync(Filter(), CancellationToken.None);
        var recapRows = await history.ListAsync(Filter(recaps: true), CancellationToken.None);
        var accounts = await history.SummarizeAsync(RunnerDesk.AdminId, CancellationToken.None);

        Assert.Equal(new[] { live }, liveRows.Select(r => r.RunId));
        var only = Assert.Single(recapRows);
        Assert.Equal((recap, true, "2026-09-11"), (only.RunId, only.IsRecap, only.RecapDate));
        Assert.False(liveRows.Single().IsRecap);
        Assert.Equal(1, Assert.Single(accounts).Runs);
    }

    [Theory]
    [InlineData("Recap")]
    [InlineData(" RECAP ")]
    public void A_recap_asked_for_in_any_case_is_stored_with_the_marker_every_report_filters_on(string asked)
    {
        var overrides = new Dictionary<string, JsonElement>
        {
            ["session"] = JsonSerializer.SerializeToElement(asked),
            ["recap_date"] = JsonSerializer.SerializeToElement("2026-09-30"),
        };

        string stored = LiveRunParameters.Merge("""{"lookback":20}""", overrides, 1, RiskRulesDto.Empty(), "NIFTY");

        Assert.Contains(RecapRuns.Marker, stored);
        Assert.True(RecapClock.IsRecap(stored));
    }

    [Fact]
    public void A_run_stored_with_the_marker_in_another_case_is_a_recap_to_the_reports_as_it_was_to_its_clock()
    {
        // RecapClock (and the runner) read "Recap" as a recap; the reports' SQL match did not, so such a run traded
        // as a test and was counted as live.
        using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase($"recaps-{Guid.NewGuid():N}").Options);
        foreach (var parameters in new[] { """{"lots":1}""", """{"session":"recap"}""", """{"session":"Recap"}""", """{"session": "RECAP"}""", """{"session":"live"}""" })
        {
            db.SimulationRuns.Add(new SimulationRun { Mode = PaperTradingService.LivePaperMode, Symbol = "NSE:NIFTY50-INDEX", Status = "Stopped",
                StrategyName = "GhostTangentCrossings", ParametersJson = parameters, UserId = 1, CreatedUtc = Now });
        }

        db.SaveChanges();

        Assert.All(db.SimulationRuns.OnlyRecaps().ToList(), r => Assert.True(RecapClock.IsRecap(r.ParametersJson)));
        Assert.Equal(3, db.SimulationRuns.OnlyRecaps().Count());
        Assert.All(db.SimulationRuns.WithoutRecaps().ToList(), r => Assert.False(RecapClock.IsRecap(r.ParametersJson)));
        Assert.Equal(2, db.SimulationRuns.WithoutRecaps().Count());

        // And PostgreSQL is sent a match it can run.
        using var pg = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseNpgsql("Host=unused").Options);
        Assert.Contains("lower(", pg.SimulationRuns.WithoutRecaps().ToQueryString());
        Assert.Contains("lower(", pg.SimulationRuns.OnlyRecaps().ToQueryString());
    }

    private static LiveRunHistoryFilter Filter(bool recaps = false) =>
        new(null, null, null, null, null, null, 100, 0, recaps);

    private static LiveRunHistoryBuilder History(RunnerDesk desk, TradingDbContext db)
    {
        var lots = new PositionGreeksTests.FixedLots(65);
        var charges = new RunCharges(db, lots);
        var catalog = new StrategyCatalogService(
            new PythonEngineLocator(Microsoft.Extensions.Options.Options.Create(desk.Options),
                RecapClockTests.Inert<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>.Create()),
            NullLogger<StrategyCatalogService>.Instance);
        return new LiveRunHistoryBuilder(db, desk.Registry, catalog, lots, charges, new RunPnl(db, lots, charges));
    }

    /// <summary>One exchange day: trading or not, closing at a given instant.</summary>
    private sealed class Session(bool tradingDay, DateTime closeUtc) : IMarketSessionService
    {
        public List<(string Exchange, string Segment)> Asked { get; } = [];

        public MarketSessionInfo GetSessionInfo(DateTime utcNow, string exchange, string segment)
        {
            Asked.Add((exchange, segment));
            return new MarketSessionInfo
            {
                Exchange = exchange, Segment = segment, UtcNow = utcNow, IsTradingDay = tradingDay,
                SessionOpenUtc = closeUtc.AddHours(-6.25), SessionCloseUtc = closeUtc,
            };
        }

        public bool IsMarketOpen(DateTime utcNow, string exchange, string segment) => tradingDay && utcNow < closeUtc;

        public DateTime GetNextMarketOpenUtc(DateTime utcNow, string exchange, string segment) => utcNow;
    }
}
