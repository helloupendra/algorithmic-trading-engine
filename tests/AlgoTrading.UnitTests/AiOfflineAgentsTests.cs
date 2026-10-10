using System.Text.Json.Nodes;
using AlgoTrading.Api.Services;
using AlgoTrading.Api.Services.AiAgents;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Domain.Enums;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static AlgoTrading.UnitTests.AiTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The Phase 4 agents that word what code measured: the Data Curator's checks and triage, the Compliance Triage's
/// circulars, the Strategy Researcher's week. Each is held to the code's facts: ids, quotes and numbers.
/// </summary>
public class AiOfflineAgentsTests
{
    private const string Extract2 = "nvidia/nemotron-3-super-120b-a12b";
    private const string Extract3 = "nvidia/nemotron-3.5-lightning-30b-a3b";

    // Wednesday 30 Sep 2026 is the trading day checked; the night pass runs early on Thursday 1 Oct.
    private static readonly DateOnly Wednesday = new(2026, 9, 30);
    private static readonly DateOnly Thursday = new(2026, 10, 1);

    private static DateTime Ist(DateOnly day, int hour, int minute) => IstTime.FromIst(day.ToDateTime(new TimeOnly(hour, minute)));

    private static IMarketSessionService Sessions(params DateOnly[] holidays) => new MarketSessionService(new MarketIntelligenceTestKit.Calendar(holidays));

    // ---------- the fact check ----------

    [Fact]
    public void A_number_time_or_date_passes_only_when_the_given_text_holds_it()
    {
        const string given = "282 of 375 one-minute candles stored for 2026-09-30 (75.2%), first at 09:15; net -₹1,18,028.50, 76827 NSE/FAOP/76827";

        Assert.Empty(AiFactCheck.Unsupported("282 of 375 bars (75%) on 2026-09-30 from 09:15; ₹118,028.5 lost; NSE/FAOP/76827", given));
        // Counts of the answer's own structure always pass.
        Assert.Empty(AiFactCheck.Unsupported("2 strategies, 3 ideas", given));
        Assert.Equal(new[] { "412", "10:05", "2026-10-01", "76" }, AiFactCheck.Unsupported("412 bars at 10:05 on 2026-10-01, 76%", given));
        // A number inside a symbol is not a number the model wrote.
        Assert.Empty(AiFactCheck.Unsupported("NSE:NIFTY50-INDEX", given));
    }

    [Fact]
    public void A_quote_matches_whatever_its_case_spacing_and_typographic_marks()
    {
        const string source = "Display of “investor awareness message(s)” by stock brokers on their trading apps";

        Assert.True(AiFactCheck.QuoteIn("display of \"investor awareness   message(s)\" by stock brokers", source));
        Assert.False(AiFactCheck.QuoteIn("by algo providers on their trading apps", source));
        Assert.False(AiFactCheck.QuoteIn("stock", source));
    }

    // ---------- the data curator: what the code finds ----------

    [Fact]
    public async Task The_checks_find_each_kind_of_problem_with_the_numbers_they_measured()
    {
        var ai = Build();
        SeedProblems(ai);

        var run = await new DataCuratorChecks(ai.Db, Sessions())
            .RunAsync(DataCuratorChecks.NightPass, Wednesday, Thursday, Ist(Thursday, 0, 30), ["NIFTY", "BANKNIFTY"], CancellationToken.None);

        var byId = run.Findings.ToDictionary(f => f.Id);
        Assert.Contains("archive:2026-09-30", byId.Keys);
        Assert.Contains("vix:2026-09-24", byId.Keys);
        Assert.Contains("300 of 375 1-minute candles stored for 2026-09-30 (80%)", byId["candles:NIFTY:1m:2026-09-30"].Facts);
        Assert.DoesNotContain("candles:NIFTY:5m:2026-09-30", byId.Keys);
        Assert.Contains("NIFTY BANK 0 of 375 minutes", byId["replay:2026-09-30"].Facts);
        Assert.DoesNotContain("NIFTY 50 ", byId["replay:2026-09-30"].Facts);
        Assert.Contains("355 of 375 session minutes", byId["chain:NIFTY:2026-09-30"].Facts);
        Assert.Contains("11:00–11:20 (20 min)", byId["chain:NIFTY:2026-09-30"].Facts);
        // An underlying the poller never captured, that day or the day before, is not a gap.
        Assert.DoesNotContain("chain:BANKNIFTY:2026-09-30", byId.Keys);
        Assert.Contains("12:00", byId["feed:stopped:dhan:2026-09-30"].Facts);
        Assert.Contains("token expired", byId["feed:error:fyers"].Facts);
        Assert.Contains("NSE:RELIANCE-EQ", byId["quotes:2026-09-30"].Facts);
        Assert.DoesNotContain("NSE:NIFTY50-INDEX", byId["quotes:2026-09-30"].Facts);
        Assert.Contains("NSE:NIFTY26092922500CE", byId["watchlist:2026-10-01"].Facts);
        Assert.Contains("expects 2026-10-01", byId["expiry:NIFTY:2026-10-01"].Facts);
        Assert.Contains("lists no enabled BANKNIFTY option", byId["expiry:BANKNIFTY:2026-10-01"].Facts);
        Assert.Contains("is not a trading day", byId["expiry:SENSEX:2026-10-01"].Facts);
        Assert.All(run.Findings, f => Assert.All(f.Actions, a => Assert.Contains(a, DataCuratorChecks.Actions.Keys)));
        Assert.All(run.Findings, f => Assert.Contains(f.Kind, DataCuratorChecks.Kinds));
    }

    [Fact]
    public void The_expiry_rule_s_next_expiry_moves_back_over_a_holiday_and_on_to_next_week_once_passed()
    {
        var rule = new ExpiryRule { Exchange = "NSE", Underlying = "NIFTY", HasWeekly = true, WeeklyExpiryDay = ExpiryDayOfWeek.Tuesday };
        var holiday = new DateOnly(2026, 10, 6);
        bool Trading(DateOnly d) => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && d != holiday;

        Assert.Equal(new DateOnly(2026, 10, 5), DataCuratorChecks.ExpectedExpiry(rule, new DateOnly(2026, 10, 1), Trading));
        Assert.Equal(new DateOnly(2026, 10, 13), DataCuratorChecks.ExpectedExpiry(rule, new DateOnly(2026, 10, 6), Trading));
        var monthly = new ExpiryRule { HasMonthly = true, MonthlyExpiryDay = ExpiryDayOfWeek.Tuesday };
        Assert.Equal(new DateOnly(2026, 10, 27), DataCuratorChecks.ExpectedExpiry(monthly, new DateOnly(2026, 10, 1), Trading));
    }

    // ---------- the data curator: the triage is held to the findings ----------

    private static DataCheckRun TwoFindings() => new(DataCuratorChecks.NightPass, Wednesday, Thursday, ["NIFTY 50 1-minute candles: 300 of 375."],
    [
        new DataFinding("candles:NIFTY:1m:2026-09-30", "candles", "NIFTY 50, 1-minute candles", "300 of 375 1-minute candles stored for 2026-09-30 (80%).",
            ["backtests on NIFTY"], ["rerun-archive", "backfill-from-broker", "none"]),
        new DataFinding("vix:2026-09-24", "vix", "India VIX", "India VIX has a gap on 2026-09-24.", ["range.har-vix"], ["import-dhan-history", "run-vix-check"]),
    ]);

    [Fact]
    public void A_triage_that_keeps_to_the_findings_passes()
    {
        var run = TwoFindings();
        string answer = """
            {"findings":[
              {"id":"candles:NIFTY:1m:2026-09-30","severity":"high","impact":"Backtests on NIFTY drop 2026-09-30: only 300 of 375 bars.","action":"backfill-from-broker","why":"Below the coverage rule."},
              {"id":"vix:2026-09-24","severity":"medium","impact":"range.har-vix skips the day after.","action":"import-dhan-history","why":"Research only."}],
             "summary":"One day of NIFTY candles is short."}
            """;

        var triage = DataCuratorAgent.Triage(answer, run, DataCuratorAgent.Question(run));

        Assert.Empty(triage.Problems);
        Assert.Equal("high", triage.ById["candles:NIFTY:1m:2026-09-30"].Severity);
    }

    [Theory]
    [InlineData("""{"findings":[{"id":"candles:NIFTY:1m:2026-09-30","severity":"high","impact":"x","action":"none"},{"id":"vix:2026-09-24","severity":"low","impact":"y","action":"run-vix-check"},{"id":"feed:made-up","severity":"high","impact":"z","action":"none"}]}""", "did not record")]
    [InlineData("""{"findings":[{"id":"candles:NIFTY:1m:2026-09-30","severity":"high","impact":"x","action":"none"}]}""", "left out vix:2026-09-24")]
    [InlineData("""{"findings":[{"id":"candles:NIFTY:1m:2026-09-30","severity":"urgent","impact":"x","action":"none"},{"id":"vix:2026-09-24","severity":"low","impact":"y","action":"rerun-archive"}]}""", "is not one of import-dhan-history, run-vix-check")]
    [InlineData("""{"findings":[{"id":"candles:NIFTY:1m:2026-09-30","severity":"high","impact":"412 bars are missing since 10:05","action":"none"},{"id":"vix:2026-09-24","severity":"low","impact":"y","action":"run-vix-check"}]}""", "\"412\" is not in the findings")]
    [InlineData("not json at all", "not the JSON object")]
    public void A_triage_that_strays_from_the_findings_fails_its_check(string answer, string problem)
    {
        var run = TwoFindings();

        var triage = DataCuratorAgent.Triage(answer, run, DataCuratorAgent.Question(run));

        Assert.Contains(triage.Problems, p => p.Contains(problem, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_pass_writes_the_triaged_findings_and_a_clean_pass_asks_no_model()
    {
        var ai = BuildOnAll();
        SeedProblems(ai);
        var agent = Curator(ai, new AiAgentsTests.FixedTime(Ist(Thursday, 0, 30)));
        var run = await new DataCuratorChecks(ai.Db, Sessions())
            .RunAsync(DataCuratorChecks.NightPass, Wednesday, Thursday, Ist(Thursday, 0, 30), ["NIFTY", "BANKNIFTY", "SENSEX"], CancellationToken.None);
        var triage = new JsonArray(run.Findings.Select(f => (JsonNode)new JsonObject
        {
            ["id"] = f.Id, ["severity"] = f.Kind == "candles" ? "high" : "low", ["impact"] = $"{f.ReadBy[0]} reads it.", ["action"] = f.Actions[0], ["why"] = "Code says so.",
        }).ToArray());
        ai.Provider.On(Extract1, Answer(new JsonObject { ["findings"] = triage, ["summary"] = "Several gaps." }.ToJsonString()));

        var report = await agent.RunForAsync("night:2026-09-30", CancellationToken.None);

        Assert.Equal(AiReportStatus.Ok, report!.Status);
        Assert.Equal("night:2026-09-30", report.SubjectId);
        Assert.StartsWith("Data checks for Wed 30 Sep (night): ", report.Title);
        var data = JsonNode.Parse(report.DataJson)!.AsObject();
        Assert.True(data["high"]!.GetValue<int>() >= 1);
        Assert.Contains("**High**", report.Body);

        Assert.Single(ai.Provider.Requests);
    }

    [Fact]
    public async Task A_pass_that_finds_nothing_is_written_as_it_is_with_no_model_asked()
    {
        var saturday = new DateOnly(2026, 10, 3);
        var ai = BuildOnAll();
        ai.Db.SystemSettings.Add(new SystemSetting { Key = NightlyArchiveService.LastArchivedDayKey, Value = "2026-10-03" });
        foreach (var (exchange, underlying) in DataCuratorChecks.ExpiryUnderlyings)
        {
            ai.Db.Instruments.Add(new Instrument { Symbol = $"{exchange}:{underlying}26100625000CE", Exchange = exchange, Segment = "FO", Underlying = underlying,
                InstrumentType = "CE", ExpiryDate = new DateOnly(2026, 10, 6), IsEnabled = true });
        }

        await ai.Db.SaveChangesAsync();

        var report = await Curator(ai, new AiAgentsTests.FixedTime(Ist(saturday, 12, 0))).RunForAsync("night:2026-10-03", CancellationToken.None);

        Assert.Equal(AiReportStatus.Ok, report!.Status);
        Assert.EndsWith("nothing wrong", report.Title);
        Assert.Null(report.CallId);
        Assert.Empty(ai.Provider.Requests);
    }

    [Fact]
    public async Task Every_model_refusing_for_capacity_is_not_a_try()
    {
        var ai = BuildOnAll();
        SeedProblems(ai);
        foreach (var model in new[] { Extract1, Extract2, Extract3 }) ai.Provider.On(model, Script.Status(429, """{"detail":"Too many requests"}"""));

        var report = await Curator(ai, new AiAgentsTests.FixedTime(Ist(Thursday, 0, 30))).RunForAsync("night:2026-09-30", CancellationToken.None);

        Assert.Equal((AiReportStatus.Failed, 0), (report!.Status, report.Attempts));
        Assert.Contains("not counted as a try", report.Error);
    }

    // ---------- the data curator: when ----------

    [Theory]
    [InlineData(2026, 9, 30, 23, 55, "night:2026-09-29")] // Wednesday's archive is due at 23:50 and its pass 20 minutes later; Tuesday's is owed
    [InlineData(2026, 10, 1, 0, 11, "night:2026-09-30")]
    [InlineData(2026, 10, 1, 8, 5, "morning:2026-10-01")]
    [InlineData(2026, 10, 1, 9, 5, null)]                 // the session is the live desk's
    [InlineData(2026, 10, 1, 15, 50, "night:2026-09-30")]
    [InlineData(2026, 10, 3, 8, 5, "night:2026-10-02")]   // Saturday: no morning pass, Friday's night one is owed
    public async Task The_night_pass_follows_the_archive_and_the_morning_pass_keeps_to_its_hour(int y, int m, int d, int hour, int minute, string? due)
    {
        var ai = BuildOnAll();
        ai.Db.SystemSettings.Add(new SystemSetting { Key = NightlyArchiveService.LastArchivedDayKey, Value = "2026-10-09" });
        await ai.Db.SaveChangesAsync();
        var now = IstTime.FromIst(new DateTime(y, m, d, hour, minute, 0));

        var pass = await Curator(ai, new AiAgentsTests.FixedTime(now)).NextDueAsync(now, CancellationToken.None);

        Assert.Equal(due, pass?.SubjectId);
    }

    [Fact]
    public async Task On_a_holiday_there_is_no_morning_pass_and_the_night_pass_waits_for_the_archive_at_most_two_hours()
    {
        var gandhi = new DateOnly(2026, 10, 2);
        var ai = BuildOnAll();
        ai.Db.SystemSettings.Add(new SystemSetting { Key = NightlyArchiveService.LastArchivedDayKey, Value = "2026-09-29" });
        await ai.Db.SaveChangesAsync();
        var curator = Curator(ai, new AiAgentsTests.FixedTime(Ist(gandhi, 8, 5)), Sessions(gandhi));

        // 08:05 on a holiday: no morning pass; Thursday's night pass is owed but its archive has not finished.
        Assert.Null(await curator.NextDueAsync(Ist(gandhi, 0, 15), CancellationToken.None));
        var morning = await curator.NextDueAsync(Ist(gandhi, 8, 5), CancellationToken.None);
        Assert.Equal("night:2026-10-01", morning?.SubjectId);
    }

    // ---------- the compliance triage ----------

    private static RegulatoryCircular Circular(long id, string title, string excerpt, DateTime seen) => new()
    {
        Id = id, Source = RegulatoryCircularSource.Nse, Key = $"nse:{id}", Number = $"NSE/FAOP/{id}", Title = title, Department = "Futures & Options",
        PublishedOn = IstTime.DateOf(seen), Url = $"https://nsearchives.nseindia.com/content/circulars/FAOP{id}.pdf", Excerpt = excerpt,
        Relevance = "department: Futures & Options", FirstSeenUtc = seen,
    };

    [Fact]
    public void A_triage_record_passes_only_with_its_quote_in_the_circular_and_a_change_when_it_applies()
    {
        var item = ComplianceTriageAgent.ItemOf(Circular(7, "Pre-Trade risk controls", "All algo orders must carry the exchange-provided Algo ID from 1 April.", DateTime.UtcNow));

        var good = ComplianceTriageAgent.Check(JsonNode.Parse(
            """{"id":"c7","applies":"no","area":"retail algo","why":"Paper orders never reach the exchange.","change":"","quote":"must carry the exchange-provided Algo ID"}""")!.AsObject(), item);
        var madeUp = ComplianceTriageAgent.Check(JsonNode.Parse(
            """{"id":"c7","applies":"no","area":"retail algo","why":"x","quote":"brokers must register every API user"}""")!.AsObject(), item);
        var noChange = ComplianceTriageAgent.Check(JsonNode.Parse(
            """{"id":"c7","applies":"yes","area":"retail algo","why":"x","change":"","quote":"must carry the exchange-provided Algo ID"}""")!.AsObject(), item);

        Assert.Equal(AiReportStatus.Ok, good.Status);
        Assert.StartsWith("Does not apply: Pre-Trade risk controls", good.Title);
        Assert.Equal(AiReportStatus.Invalid, madeUp.Status);
        Assert.Contains("is not in the circular's text", madeUp.Error);
        Assert.Contains("nothing says what would have to change", noChange.Error);
    }

    [Theory]
    [InlineData(2026, 10, 9, 21, 0, false)]  // Friday evening: the week's triage opens on Saturday
    [InlineData(2026, 10, 10, 9, 59, false)]
    [InlineData(2026, 10, 10, 10, 1, true)]
    [InlineData(2026, 10, 12, 18, 0, true)]  // Monday evening: a Saturday the API missed is caught up
    public async Task The_week_s_circulars_are_read_from_Saturday_10_00(int y, int m, int d, int hour, int minute, bool due)
    {
        var ai = BuildOnAll();
        ai.Db.RegulatoryCirculars.Add(Circular(1, "F&O lot sizes revised", "Lot sizes of index derivatives are revised.", IstTime.FromIst(new DateTime(2026, 10, 8, 20, 30, 0))));
        await ai.Db.SaveChangesAsync();
        var now = IstTime.FromIst(new DateTime(y, m, d, hour, minute, 0));

        var batch = await Compliance(ai, now).NextBatchAsync(now, CancellationToken.None);

        Assert.Equal(due, batch.Count == 1);
    }

    [Fact]
    public async Task A_circular_recorded_after_the_saturday_cutoff_waits_for_the_next_week()
    {
        var ai = BuildOnAll();
        ai.Db.RegulatoryCirculars.Add(Circular(2, "Market timings", "Trading hours for equity derivatives.", IstTime.FromIst(new DateTime(2026, 10, 10, 20, 30, 0))));
        await ai.Db.SaveChangesAsync();
        var sunday = IstTime.FromIst(new DateTime(2026, 10, 11, 12, 0, 0));
        var nextSaturday = IstTime.FromIst(new DateTime(2026, 10, 17, 10, 5, 0));

        Assert.Empty(await Compliance(ai, sunday).NextBatchAsync(sunday, CancellationToken.None));
        Assert.Single(await Compliance(ai, nextSaturday).NextBatchAsync(nextSaturday, CancellationToken.None));
    }

    [Fact]
    public async Task A_batch_writes_one_report_per_circular_and_one_left_out_is_invalid()
    {
        var ai = BuildOnAll();
        var seen = IstTime.FromIst(new DateTime(2026, 10, 8, 20, 30, 0));
        ai.Db.RegulatoryCirculars.AddRange(
            Circular(11, "Pre-Trade risk controls for algo orders", "Every algo order must carry an Algo ID issued by the exchange.", seen),
            Circular(12, "Mock trading on Saturday", "", seen));
        await ai.Db.SaveChangesAsync();
        ai.Provider.On(Extract1, Answer("""
            {"circulars":[{"id":"c11","applies":"unclear","area":"retail algo","why":"The desk sends no orders to an exchange.","change":"","quote":"Every algo order must carry an Algo ID"}]}
            """));
        var now = IstTime.FromIst(new DateTime(2026, 10, 10, 10, 5, 0));

        Assert.True(await Compliance(ai, now).RunOnceAsync(now, CancellationToken.None));

        var reports = await ai.Db.AiReports.AsNoTracking().OrderBy(r => r.SubjectId).ToListAsync();
        Assert.Equal(new[] { "circular:11", "circular:12" }, reports.Select(r => r.SubjectId));
        Assert.Equal((AiReportStatus.Ok, AiReportStatus.Invalid), (reports[0].Status, reports[1].Status));
        Assert.Contains("left this circular out", reports[1].Error);
        // The circular with no text was handed over with the note to quote its title.
        Assert.Contains("judge on the title alone", ai.Provider.Requests.Single().Body);
    }

    // ---------- the strategy researcher ----------

    private static BacktestRow Row(long id, string strategy, string from, string to, int trades, decimal net, int tunables = 4, string config = "a") =>
        new(id, strategy, "NIFTY", "5", from, to, StrategyResearchData.Periods(DateOnly.Parse(from), DateOnly.Parse(to)), trades, net,
            trades == 0 ? 0 : Math.Round(net / trades, 2), 50m, tunables, config, "Completed");

    [Fact]
    public void The_checks_flag_a_small_sample_no_holdout_decay_out_of_sample_and_many_configurations()
    {
        var rows = new[]
        {
            Row(1, "Ghost", "2024-01-01", "2025-12-31", 20, 4000m, tunables: 6, config: "a"),
            Row(2, "Ghost", "2026-01-01", "2026-09-30", 5, -500m, tunables: 6, config: "b"),
        };
        var checks = StrategyResearchData.Checks("Ghost", rows, new PaperRow("Ghost", "last 30 days", 10, 8, 40, -2000m, 900m, -50m))
            .ToDictionary(c => c.Check);

        Assert.Equal("concern", checks["sample"].Reading);          // 25 trades
        Assert.Contains("25 closed trades in 2 backtests", checks["sample"].Facts);
        Assert.Equal("concern", checks["tunables"].Reading);        // 25 / 6 a setting
        Assert.Equal("concern", checks["holdout"].Reading);
        Assert.Contains("2020-09-01 to 2023-12-31", checks["holdout"].Facts);
        Assert.Equal("concern", checks["in-vs-out"].Reading);       // ₹200 a trade in, -₹100 out
        Assert.Equal("concern", checks["paper-vs-backtest"].Reading);
        Assert.Equal("fine", checks["configs"].Reading);

        var many = Enumerable.Range(0, 5).Select(i => Row(10 + i, "Ghost", "2020-09-01", "2023-12-31", 100, 1000m, config: $"c{i}")).ToList();
        var later = StrategyResearchData.Checks("Ghost", many, null).ToDictionary(c => c.Check);
        Assert.Equal(("concern", "fine", "unknown"), (later["configs"].Reading, later["holdout"].Reading, later["paper-vs-backtest"].Reading));
    }

    [Fact]
    public void Tunables_are_the_strategy_s_own_settings_and_the_rules_set_on_the_run()
    {
        var (n, config) = StrategyResearchData.Settings("""
            {"fast":9,"slow":21,"lots":2,"underlying":"NIFTY","charges_per_lot":40,"costs":{"slippage":1},
             "filters":{"vix_max":18,"weekdays":["Mon"],"skip_expiry":false},"risk":{"overall":{"stopLoss":5000}}}
            """);
        var (same, sameConfig) = StrategyResearchData.Settings("""{"slow":21,"fast":9,"lots":1,"filters":{"weekdays":["Mon"],"vix_max":18},"risk":{"overall":{"stopLoss":5000}}}""");

        Assert.Equal(5, n);                 // fast, slow, filters.vix_max, filters.weekdays, risk.overall.stopLoss
        Assert.Equal((n, config), (same, sameConfig));
    }

    private static ResearchWeek Week() => new(new DateOnly(2026, 10, 11), DateTime.UtcNow.AddDays(-7), DateTime.UtcNow,
        [Row(1, "Ghost", "2024-01-01", "2025-12-31", 20, 4000m)],
        [new PaperRow("Ghost", "last 30 days", 10, 8, 40, -2000m, 900m, -50m)],
        StrategyResearchData.Checks("Ghost", [Row(1, "Ghost", "2024-01-01", "2025-12-31", 20, 4000m)], new PaperRow("Ghost", "last 30 days", 10, 8, 40, -2000m, 900m, -50m)),
        [new SpecExcerpt("Ghost", "Trades EMA crossings.", null)]);

    [Fact]
    public void A_note_passes_when_it_keeps_to_the_tables_and_proposes_tests_only()
    {
        var week = Week();
        string answer = """
            {"strategies":[{"strategy":"Ghost","checks":[{"check":"sample","verdict":"concern","note":"Only 20 trades."},{"check":"holdout","verdict":"concern","note":"Never run on 2020-23."}],"summary":"₹200 a trade on 20 trades is a coin."}],
             "ideas":[{"strategy":"Ghost","idea":"Run the same rule on the holdout.","test":"NIFTY 5m, 2020-09-01 to 2023-12-31, unchanged settings","why":"20 trades, ₹4,000 net on the design years only."}],
             "summary":"Ghost is untested outside 2024-2025."}
            """;

        var note = StrategyResearcherAgent.ParseNote(answer, week, StrategyResearcherAgent.Question(week));

        Assert.Empty(note.Problems);
        Assert.Single(note.Ideas);
    }

    [Theory]
    [InlineData("""{"strategies":[{"strategy":"Ghost","checks":[],"summary":"It made ₹9,999."}],"ideas":[]}""", "\"9,999\" is not in the tables")]
    [InlineData("""{"strategies":[{"strategy":"Fulcrum","checks":[],"summary":"x"}],"ideas":[]}""", "a strategy the tables do not list")]
    [InlineData("""{"strategies":[{"strategy":"Ghost","checks":[{"check":"vibes","verdict":"fine","note":"x"}],"summary":"x"}],"ideas":[]}""", "is not one of its checks")]
    [InlineData("""{"strategies":[],"ideas":[{"strategy":"Ghost","idea":"Go live with it on Monday.","test":"none","why":"x"}]}""", "live change")]
    [InlineData("""{"strategies":[],"ideas":[{"strategy":"Ghost","idea":"a","test":"b","why":"c"},{"strategy":"Ghost","idea":"a","test":"b","why":"c"},{"strategy":"Ghost","idea":"a","test":"b","why":"c"},{"strategy":"Ghost","idea":"a","test":"b","why":"c"}]}""", "more than 3")]
    public void A_note_that_strays_from_the_tables_fails_its_check(string answer, string problem)
    {
        var week = Week();

        var note = StrategyResearcherAgent.ParseNote(answer, week, StrategyResearcherAgent.Question(week));

        Assert.Contains(note.Problems, p => p.Contains(problem, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(2026, 10, 11, 10, 59, null)]
    [InlineData(2026, 10, 11, 11, 1, "week:2026-10-11")]
    [InlineData(2026, 10, 12, 10, 0, null)]               // Monday, in the session
    [InlineData(2026, 10, 12, 16, 0, "week:2026-10-11")]  // Monday after the close: a missed Sunday is written late
    [InlineData(2026, 10, 18, 11, 1, "week:2026-10-18")]
    public async Task The_note_is_written_on_Sunday_from_11_00_and_never_in_a_session(int y, int m, int d, int hour, int minute, string? due)
    {
        var ai = BuildOnAll();
        var now = IstTime.FromIst(new DateTime(y, m, d, hour, minute, 0));

        var week = await Researcher(ai, now, new FakePaper()).NextDueAsync(now, CancellationToken.None);

        Assert.Equal(due, week is DateOnly w ? StrategyResearcherAgent.Subject(w) : null);
    }

    [Fact]
    public async Task The_week_s_backtests_are_read_with_their_net_and_a_week_with_nothing_asks_no_model()
    {
        var ai = BuildOnAll();
        var sunday = IstTime.FromIst(new DateTime(2026, 10, 11, 11, 30, 0));
        var run = new SimulationRun
        {
            UserId = 1, Mode = "OfflineReplay", Status = "Completed", StrategyName = "Ghost", Symbol = "NSE:NIFTY50-INDEX", Resolution = "5",
            ParametersJson = """{"underlying":"NIFTY","fast":9,"charges_per_lot":10}""", CreatedUtc = sunday.AddDays(-2),
            FromUtc = IstTime.FromIst(new DateTime(2024, 1, 1, 9, 15, 0)), ToUtc = IstTime.FromIst(new DateTime(2025, 12, 31, 15, 30, 0)),
        };
        ai.Db.SimulationRuns.Add(run);
        await ai.Db.SaveChangesAsync();
        ai.Db.PaperPositions.AddRange(
            new PaperPosition { SimulationRunId = run.Id, Symbol = "A", Status = "Closed", RealizedPnl = 300m },
            new PaperPosition { SimulationRunId = run.Id, Symbol = "B", Status = "Closed", RealizedPnl = -100m });
        ai.Db.PaperOrders.Add(new PaperOrder { SimulationRunId = run.Id, Symbol = "A", Status = "Filled", Quantity = 2 });
        await ai.Db.SaveChangesAsync();
        var data = new StrategyResearchData(ai.Db, new FakePaper(), RecapClockTests.Inert<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>.Create())
        {
            SpecsFolder = Path.Combine(Path.GetTempPath(), "no-specs-" + Guid.NewGuid().ToString("N")),
        };

        var rows = await data.BacktestsAsync(sunday.AddDays(-7), sunday, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal((2, 180m, 90m, 1), (row.Trades, row.Net, row.PerTrade, row.Tunables));
        Assert.Equal(new[] { "design" }, row.Periods);

        var empty = BuildOnAll();
        var note = await Researcher(empty, sunday, new FakePaper()).RunForAsync(null, CancellationToken.None);
        Assert.Equal(AiReportStatus.Ok, note!.Status);
        Assert.Contains("no backtest and no paper run", note.Title);
        Assert.Empty(empty.Provider.Requests);
    }

    // ---------- what each reads in a chat ----------

    private static async Task<System.Text.Json.JsonElement> Read(IAiTool tool, string args) =>
        System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize((await tool.RunAsync(AiToolArgs.Parse(args), default)).Data,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))).RootElement;

    [Fact]
    public async Task The_curator_reads_its_passes_with_their_high_findings_and_one_in_full_masked()
    {
        using var db = NewDb();
        var now = Ist(Thursday, 9, 0);
        db.AiReports.Add(new AiReport { Id = 5, AgentKey = AiCatalog.DataCurator, SubjectType = AiReportSubject.Check, SubjectId = "night:2026-09-30",
            SessionDate = Wednesday, Status = AiReportStatus.Ok, Title = "Data checks for Wed 30 Sep (night): 1 high, 0 medium, 0 low",
            CreatedUtc = now, UpdatedUtc = now,
            DataJson = """
                {"pass":"night","checkedDay":"2026-09-30","high":1,"medium":0,"low":0,"checked":["Feed heartbeats of the last 7 days (2 feeds)."],
                 "findings":[{"id":"feed:error:fyers","subject":"Feed fyers","facts":"Feed fyers said: cannot read /Users/upendra/.fyers/token","severity":"high",
                   "impact":"Live runs priced from it stop.","action":"restart-feed","actionMeans":"Restart the feed (Data → Feeds) and check its token"}]}
                """ });
        db.SaveChanges();
        var tool = new AlgoTrading.Api.Services.AiTools.DataChecksTool(db, new AiAgentsTests.FixedTime(now));

        var list = await Read(tool, "{}");
        var one = await Read(tool, """{"reportId":5}""");

        var row = Assert.Single(list.GetProperty("checks").EnumerateArray());
        Assert.Equal((5, 1), (row.GetProperty("reportId").GetInt32(), row.GetProperty("highFindings").GetArrayLength()));
        var finding = Assert.Single(one.GetProperty("findings").EnumerateArray());
        Assert.Equal(("high", "restart-feed"), (finding.GetProperty("severity").GetString(), finding.GetProperty("action").GetString()));
        Assert.DoesNotContain("/Users/upendra", finding.GetProperty("facts").GetString());
    }

    [Fact]
    public async Task The_compliance_triage_reads_the_circulars_by_its_verdict_and_one_with_its_text_and_quote()
    {
        using var db = NewDb();
        var now = IstTime.FromIst(new DateTime(2026, 10, 10, 12, 0, 0));
        db.RegulatoryCirculars.AddRange(
            Circular(7, "Algo ID on every API order", "Every algo order must carry an Algo ID.", now.AddDays(-2)),
            Circular(8, "Listing of ABC", "ABC is listed.", now.AddDays(-2)),
            Circular(9, "Mock trading", "", now.AddDays(-1)));
        db.AiReports.Add(new AiReport { AgentKey = AiCatalog.ComplianceTriage, SubjectType = AiReportSubject.Check, SubjectId = "circular:7", Status = AiReportStatus.Ok,
            Title = "Applies: Algo ID on every API order", CreatedUtc = now, UpdatedUtc = now,
            DataJson = """{"applies":"yes","area":"retail algo","why":"x","change":"Tag orders.","quote":"Every algo order must carry an Algo ID"}""" });
        db.AiReports.Add(new AiReport { AgentKey = AiCatalog.ComplianceTriage, SubjectType = AiReportSubject.Check, SubjectId = "circular:8", Status = AiReportStatus.Ok,
            Title = "Does not apply: Listing of ABC", CreatedUtc = now, UpdatedUtc = now, DataJson = """{"applies":"no","area":"other","why":"y","quote":"ABC is listed"}""" });
        db.SaveChanges();
        var tool = new AlgoTrading.Api.Services.AiTools.CircularsTool(db, new AiAgentsTests.FixedTime(now));

        var applies = await Read(tool, """{"applies":"yes"}""");
        var untriaged = await Read(tool, """{"applies":"untriaged"}""");
        var one = await Read(tool, """{"id":7}""");

        Assert.Equal(7, Assert.Single(applies.GetProperty("circulars").EnumerateArray()).GetProperty("id").GetInt32());
        Assert.Equal(9, Assert.Single(untriaged.GetProperty("circulars").EnumerateArray()).GetProperty("id").GetInt32());
        Assert.Equal(("Every algo order must carry an Algo ID.", "Tag orders."),
            (one.GetProperty("text").GetString(), one.GetProperty("triage").GetProperty("change").GetString()));
    }

    [Fact]
    public async Task The_researcher_reads_its_notes_and_one_week_with_its_tables()
    {
        using var db = NewDb();
        var now = IstTime.FromIst(new DateTime(2026, 10, 12, 18, 0, 0));
        db.AiReports.Add(new AiReport { Id = 9, AgentKey = AiCatalog.StrategyResearcher, SubjectType = AiReportSubject.Check, SubjectId = "week:2026-10-11",
            SessionDate = new DateOnly(2026, 10, 11), Status = AiReportStatus.Ok, Title = "Research note, week to Sun 11 Oct: 1 strategy",
            CreatedUtc = now, UpdatedUtc = now,
            DataJson = """{"week":"2026-10-11","summary":"Ghost is untested on the holdout.","ideas":[{"strategy":"Ghost","idea":"Run it on 2020-23."}],"backtests":[{"runId":1,"trades":20}],"checks":[]}""" });
        db.SaveChanges();
        var tool = new AlgoTrading.Api.Services.AiTools.ResearchNotesTool(db, new AiAgentsTests.FixedTime(now));

        var list = await Read(tool, "{}");
        var week = await Read(tool, """{"week":"2026-10-11"}""");

        var note = Assert.Single(list.GetProperty("notes").EnumerateArray());
        Assert.Equal("Ghost: Run it on 2020-23.", Assert.Single(note.GetProperty("ideas").EnumerateArray()).GetString());
        Assert.Contains("\"trades\":20", week.GetProperty("backtests").GetString());
        await Assert.ThrowsAsync<AiToolArgumentException>(() => tool.RunAsync(AiToolArgs.Parse("""{"week":"2026-10-04"}"""), default));
    }

    // ---------- helpers ----------

    private static Services BuildOnAll()
    {
        var ai = Build();
        foreach (var key in new[] { AiCatalog.DataCurator, AiCatalog.ComplianceTriage, AiCatalog.StrategyResearcher })
        {
            ai.Store.SetAgentEnabledAsync(key, true, "upendra", null).GetAwaiter().GetResult();
        }

        return ai;
    }

    private static DataCuratorAgent Curator(Services ai, TimeProvider clock, IMarketSessionService? sessions = null)
    {
        sessions ??= Sessions();
        return new DataCuratorAgent(ai.Db, ai.Gateway, new AiReportWriter(ai.Db, ai.Options, clock), new DataCuratorChecks(ai.Db, sessions), sessions,
            new ConfigurationBuilder().Build(), new AiSchedulerState(), ai.Options, NullLogger<DataCuratorAgent>.Instance, clock);
    }

    private static ComplianceTriageAgent Compliance(Services ai, DateTime now) =>
        new(ai.Db, ai.Gateway, new AiReportWriter(ai.Db, ai.Options, new AiAgentsTests.FixedTime(now)), Sessions(), new AiSchedulerState(), ai.Options,
            NullLogger<ComplianceTriageAgent>.Instance, new AiAgentsTests.FixedTime(now));

    private static StrategyResearcherAgent Researcher(Services ai, DateTime now, IPaperResults paper)
    {
        var data = new StrategyResearchData(ai.Db, paper, RecapClockTests.Inert<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>.Create())
        {
            SpecsFolder = Path.Combine(Path.GetTempPath(), "no-specs"),
        };
        return new StrategyResearcherAgent(ai.Gateway, new AiReportWriter(ai.Db, ai.Options, new AiAgentsTests.FixedTime(now)), data, Sessions(),
            new AiSchedulerState(), ai.Options, NullLogger<StrategyResearcherAgent>.Instance, new AiAgentsTests.FixedTime(now));
    }

    /// <summary>No paper runs, or the ones given.</summary>
    internal sealed class FakePaper(params LiveRunSummaryResponse[] runs) : IPaperResults
    {
        public Task<IReadOnlyList<LiveRunSummaryResponse>> RunsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LiveRunSummaryResponse>>(runs);
    }

    /// <summary>
    /// One of each problem on Wednesday 30 Sep: the archive a day behind, an old VIX gap, NIFTY's 1-minute candles at
    /// 300 of 375, only NIFTY in live_bars, a 20-minute hole in the NIFTY chain, a feed that stopped at noon and one in
    /// error, a stock that did not tick, an expired contract still recorded, and three expiry mismatches for Thursday.
    /// </summary>
    private static void SeedProblems(Services ai)
    {
        var db = ai.Db;
        db.SystemSettings.Add(new SystemSetting { Key = NightlyArchiveService.LastArchivedDayKey, Value = "2026-09-29" });
        db.SystemSettings.Add(new SystemSetting { Key = SystemSettingKeys.VixGapsReported, Value = "2026-09-24" });

        var open = Ist(Wednesday, 9, 15);
        for (int i = 0; i < 300; i++) db.Candles.Add(new Candle { Symbol = "NSE:NIFTY50-INDEX", Resolution = "1", TimeStampUtc = open.AddMinutes(i), SourceKey = "live" });
        for (int i = 0; i < 75; i++) db.Candles.Add(new Candle { Symbol = "NSE:NIFTY50-INDEX", Resolution = "5", TimeStampUtc = open.AddMinutes(5 * i), SourceKey = "live" });
        for (int i = 0; i < 375; i++) db.LiveBars.Add(new LiveBar { Symbol = "NSE:NIFTY50-INDEX", Resolution = "1m", BarStartUtc = open.AddMinutes(i) });

        db.Instruments.Add(new Instrument { Symbol = "NSE:NIFTY26100622500CE", Exchange = "NSE", Segment = "FO", Underlying = "NIFTY", InstrumentType = "CE",
            ExpiryDate = new DateOnly(2026, 10, 6), IsEnabled = true });
        db.Instruments.Add(new Instrument { Symbol = "BSE:SENSEX2610038000CE", Exchange = "BSE", Segment = "FO", Underlying = "SENSEX", InstrumentType = "CE",
            ExpiryDate = new DateOnly(2026, 10, 3), IsEnabled = true });
        db.Instruments.Add(new Instrument { Symbol = "NSE:NIFTY26092922500CE", Exchange = "NSE", Segment = "FO", Underlying = "NIFTY", InstrumentType = "CE",
            ExpiryDate = new DateOnly(2026, 9, 29), IsEnabled = true });
        db.ExpiryRules.Add(new ExpiryRule { Exchange = "NSE", Underlying = "NIFTY", HasWeekly = true, WeeklyExpiryDay = ExpiryDayOfWeek.Thursday, IsEnabled = true });
        for (int i = 0; i < 375; i++)
        {
            var at = open.AddMinutes(i).AddSeconds(5);
            var ist = IstTime.ToIst(at);
            if (ist.Hour == 11 && ist.Minute < 20) continue;
            db.OptionChainSnapshots.Add(new OptionChainSnapshot { Underlying = "NIFTY", ExpiryDate = new DateOnly(2026, 10, 6), StrikePrice = 22500, OptionType = "CE",
                Symbol = "NSE:NIFTY26100622500CE", CapturedUtc = at, SourceKey = "dhan" });
        }

        db.LiveIngestorStatuses.Add(new LiveIngestorStatus { SourceName = "dhan", Status = "Stopped", LastHeartbeatUtc = Ist(Wednesday, 12, 0) });
        db.LiveIngestorStatuses.Add(new LiveIngestorStatus { SourceName = "fyers", Status = "Error", LastHeartbeatUtc = Ist(Wednesday, 15, 31), LastError = "token expired" });

        db.LiveWatchlistItems.AddRange(
            new LiveWatchlistItem { Symbol = "NSE:NIFTY50-INDEX", IsActive = true },
            new LiveWatchlistItem { Symbol = "NSE:RELIANCE-EQ", IsActive = true },
            new LiveWatchlistItem { Symbol = "NSE:NIFTY26092922500CE", IsActive = true });
        db.LiveQuotesLatest.AddRange(
            new LiveQuoteLatest { Symbol = "NSE:NIFTY50-INDEX", ExchangeTimestampUtc = Ist(Wednesday, 15, 29), UpdatedUtc = Ist(Wednesday, 15, 29) },
            new LiveQuoteLatest { Symbol = "NSE:RELIANCE-EQ", ExchangeTimestampUtc = Ist(new DateOnly(2026, 9, 29), 15, 29), UpdatedUtc = Ist(new DateOnly(2026, 9, 29), 15, 29) });
        db.SaveChanges();
    }
}
