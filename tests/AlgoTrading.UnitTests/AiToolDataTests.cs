using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AlgoTrading.Api.Services;
using AlgoTrading.Api.Services.AiTools;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The desk tools' answers leave the server: they are sent to the model's
/// provider. These tests seed the desk with the secrets it really holds in
/// free text (a traceback with a password in a run's last error, a token in a
/// stop reason, a bearer header in an incident's evidence, the host in a
/// checkup, an account's email) and read every tool's answer as the model
/// would get it.
/// </summary>
public sealed class AiToolDataTests : IDisposable
{
    private const string Password = "hunter2-db-pass";
    private const string Token = "tok_SECRET_9f8e7d";
    // Made up: {"alg":"HS256"}.{"sub":"7"}.signature, to prove a token in a tool's output is masked.
    private const string Bearer = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiI3In0.c2lnbmF0dXJl"; // pragma: allowlist secret
    private const string Host = "ip-172-31-20-148";
    private const string Email = "coder@example.com";
    private const string LogPath = "/home/ubuntu/algorithmic-trading-engine/logs/api.log";

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly RunnerDesk _desk = new();

    public void Dispose() => _desk.Dispose();

    [Fact]
    public async Task No_tool_answer_carries_a_secret_an_email_or_the_host()
    {
        long runId = SeedDesk();
        await using var db = _desk.Db();

        var answers = new List<string>();
        foreach (var (tool, args) in Tools(db, runId))
        {
            var output = await tool.RunAsync(AiToolArgs.Parse(args), CancellationToken.None);
            answers.Add(JsonSerializer.Serialize(output.Data, Wire));
        }

        string all = string.Join('\n', answers);
        Assert.DoesNotContain(Password, all);
        Assert.DoesNotContain(Token, all);
        Assert.DoesNotContain(Bearer, all);
        Assert.DoesNotContain(Host, all);
        Assert.DoesNotContain(Email, all);
        Assert.DoesNotContain(LogPath, all);
        // The masking left the rest of the sentence readable.
        Assert.Contains("stopped on the daily loss limit", all);
    }

    [Fact]
    public async Task A_run_s_answer_has_its_legs_orders_and_net_after_charges()
    {
        long runId = SeedDesk();
        await using var db = _desk.Db();
        var tool = RunToolFor(db);

        var output = await tool.RunAsync(AiToolArgs.Parse($$"""{"runId":{{runId}}}"""), CancellationToken.None);
        var json = JsonDocument.Parse(JsonSerializer.Serialize(output.Data, Wire)).RootElement;

        Assert.Equal("coderforchange", json.GetProperty("run").GetProperty("account").GetString());
        Assert.Equal("NIFTY", json.GetProperty("run").GetProperty("underlying").GetString());
        Assert.Equal(1, json.GetProperty("legs").GetArrayLength());
        Assert.Equal(2, json.GetProperty("orders").GetArrayLength());
        var pnl = json.GetProperty("pnl");
        Assert.Equal(-975m, pnl.GetProperty("realized").GetDecimal());
        Assert.True(pnl.GetProperty("charges").GetDecimal() > 0);
        Assert.Equal(pnl.GetProperty("realized").GetDecimal() - pnl.GetProperty("charges").GetDecimal(), pnl.GetProperty("net").GetDecimal());
    }

    [Fact]
    public async Task The_runs_list_uses_the_desk_s_net_and_names_the_account()
    {
        long runId = SeedDesk();
        await using var db = _desk.Db();
        var tool = new RunsTool(History(db));
        // The run's own IST day, not "today": it starts an hour ago, which is
        // yesterday's IST day between 00:00 and 01:00 IST.
        var day = AlgoTrading.Infrastructure.Services.IstTime.DateOf(db.SimulationRuns.Single(r => r.Id == runId).StartedUtc!.Value);

        var output = await tool.RunAsync(AiToolArgs.Parse($$"""{"date":"{{day:yyyy-MM-dd}}"}"""), CancellationToken.None);
        var json = JsonDocument.Parse(JsonSerializer.Serialize(output.Data, Wire)).RootElement;

        var run = json.GetProperty("runs").EnumerateArray().Single(r => r.GetProperty("runId").GetInt64() == runId);
        Assert.Equal("coderforchange", run.GetProperty("account").GetString());
        Assert.Equal(run.GetProperty("realizedPnl").GetDecimal() - run.GetProperty("charges").GetDecimal(), run.GetProperty("netPnl").GetDecimal());
        Assert.Equal(output.Rows, json.GetProperty("totals").GetProperty("runs").GetInt32());
    }

    [Fact]
    public async Task A_busy_run_is_summarised_and_its_lists_come_by_section_and_window()
    {
        long runId = _desk.SeedRun(RunnerDesk.TraderId, "NIFTY", "Stopped");
        var start = new DateTime(2026, 9, 30, 4, 0, 0, DateTimeKind.Utc); // 09:30 IST
        using (var seed = _desk.Db())
        {
            var run = seed.SimulationRuns.Single(r => r.Id == runId);
            run.StartedUtc = start;
            for (int i = 0; i < 40; i++)
            {
                seed.PaperPositions.Add(new PaperPosition
                {
                    SimulationRunId = runId, StrategyName = "Ghost", GroupId = $"G{i}", Symbol = "NSE:NIFTY26OCT25000CE", Direction = "SHORT",
                    Quantity = 0, AveragePrice = 100m, RealizedPnl = i % 2 == 0 ? -100m * i : 50m * i, Status = "Closed",
                    OpenedUtc = start.AddMinutes(i * 5), ClosedUtc = start.AddMinutes(i * 5 + 3),
                });
                seed.PaperOrders.Add(new PaperOrder { SimulationRunId = runId, StrategyName = "Ghost", GroupId = $"G{i}", Symbol = "NSE:NIFTY26OCT25000CE", Side = "SELL", Quantity = 1, Status = "Filled", FillPrice = 100m, CreatedUtc = start.AddMinutes(i * 5) });
                seed.PaperOrders.Add(new PaperOrder { SimulationRunId = runId, StrategyName = "Ghost", GroupId = $"G{i}", Symbol = "NSE:NIFTY26OCT25000CE", Side = "BUY", Quantity = 1, Status = "Filled", FillPrice = 101m, CreatedUtc = start.AddMinutes(i * 5 + 3) });
            }

            seed.SaveChanges();
        }

        await using var db = _desk.Db();
        var tool = RunToolFor(db);

        var summary = JsonDocument.Parse(JsonSerializer.Serialize((await tool.RunAsync(AiToolArgs.Parse($$"""{"runId":{{runId}}}"""), CancellationToken.None)).Data, Wire)).RootElement;
        var orders = JsonDocument.Parse(JsonSerializer.Serialize((await tool.RunAsync(
            AiToolArgs.Parse($$"""{"runId":{{runId}},"section":"orders","from":"10:00","to":"10:30","limit":5}"""), CancellationToken.None)).Data, Wire)).RootElement;

        Assert.False(summary.TryGetProperty("legs", out _));
        Assert.Equal(40, summary.GetProperty("counts").GetProperty("legs").GetInt32());
        Assert.Equal(80, summary.GetProperty("counts").GetProperty("orders").GetInt32());
        Assert.Equal(5, summary.GetProperty("worstLegs").GetArrayLength());
        Assert.Equal(-3800m, summary.GetProperty("worstLegs")[0].GetProperty("pnl").GetDecimal());
        Assert.True(summary.GetProperty("realizedByHour").GetArrayLength() >= 3);
        Assert.Contains("section", summary.GetProperty("more").GetString());

        Assert.Equal(5, orders.GetProperty("rows").GetArrayLength());
        Assert.Equal(13, orders.GetProperty("inWindow").GetInt32()); // 10:00–10:30 IST: 7 opens and 6 closes
        Assert.StartsWith("2026-09-30 10:0", orders.GetProperty("rows")[0].GetProperty("at").GetString());
    }

    [Fact]
    public async Task A_period_is_read_in_one_call_with_the_money_by_day_and_the_trades()
    {
        var today = AlgoTrading.Infrastructure.Services.IstTime.DateOf(DateTime.UtcNow);
        DateTime At(int daysAgo) => AlgoTrading.Infrastructure.Services.IstTime.FromIst(today.AddDays(-daysAgo).ToDateTime(new TimeOnly(10, 0)));
        long Seed(string strategy, string underlying, int daysAgo, params decimal[] legs)
        {
            long id = _desk.SeedRun(RunnerDesk.TraderId, underlying, "Stopped", strategy);
            using var db = _desk.Db();
            var run = db.SimulationRuns.Single(r => r.Id == id);
            run.StartedUtc = run.CreatedUtc = At(daysAgo);
            run.CompletedUtc = At(daysAgo).AddHours(5);
            for (int i = 0; i < legs.Length; i++)
            {
                db.PaperPositions.Add(new PaperPosition
                {
                    SimulationRunId = id, StrategyName = strategy, GroupId = $"G{i}", Symbol = $"NSE:{underlying}26OCT25000CE", Direction = "SHORT",
                    Quantity = 0, AveragePrice = 100m, RealizedPnl = legs[i], Status = "Closed",
                    OpenedUtc = At(daysAgo).AddMinutes(10 * i), ClosedUtc = At(daysAgo).AddMinutes(10 * i + 30),
                });
            }

            db.SaveChanges();
            return id;
        }

        Seed("Ghost", "NIFTY", 0, 300m, -100m);
        Seed("Ghost", "NIFTY", 1, -500m);
        Seed("Ghost", "BANKNIFTY", 2, 200m, 200m, -50m);
        Seed("Fulcrum", "NIFTY", 1, -900m);
        await using var db2 = _desk.Db();

        var output = await new StrategyHistoryTool(History(db2), db2).RunAsync(
            AiToolArgs.Parse($$"""{"strategy":"ghost","from":"{{today.AddDays(-5):yyyy-MM-dd}}"}"""), CancellationToken.None);
        var json = JsonDocument.Parse(JsonSerializer.Serialize(output.Data, Wire)).RootElement;

        var totals = json.GetProperty("totals");
        Assert.Equal(3, totals.GetProperty("runs").GetInt32());
        Assert.Equal(3, totals.GetProperty("daysTraded").GetInt32());
        Assert.Equal(50m, totals.GetProperty("gross").GetDecimal()); // 200 − 500 + 350; Fulcrum is left out
        Assert.Equal(3, json.GetProperty("byDay").GetArrayLength());
        Assert.Equal(2, json.GetProperty("byUnderlying").GetArrayLength());
        var trades = json.GetProperty("trades");
        Assert.Equal(6, trades.GetProperty("closedLegs").GetInt32());
        Assert.Equal(50.0, trades.GetProperty("winRate").GetDouble()); // 300, 200, 200 of six
        Assert.Equal(1.08m, trades.GetProperty("profitFactor").GetDecimal()); // 700 / 650 = 1.0769
        Assert.Equal(30.0, trades.GetProperty("averageHoldMinutes").GetDouble());
        Assert.Equal(-500m, trades.GetProperty("largestLoss").GetDecimal());
    }

    [Theory]
    [InlineData("""{"period":"next_year"}""", "period is")]
    [InlineData("""{"from":"2026-09-30","to":"2026-09-01"}""", "from is after to")]
    public async Task A_period_the_tool_cannot_read_is_answered_in_words(string args, string expected)
    {
        await using var db = _desk.Db();
        var ex = await Assert.ThrowsAsync<AiToolArgumentException>(() => new StrategyHistoryTool(History(db), db).RunAsync(AiToolArgs.Parse(args), CancellationToken.None));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public async Task A_run_that_does_not_exist_is_answered_in_words()
    {
        SeedDesk();
        await using var db = _desk.Db();

        var ex = await Assert.ThrowsAsync<AiToolArgumentException>(() => RunToolFor(db).RunAsync(AiToolArgs.Parse("""{"runId":99999}"""), CancellationToken.None));

        Assert.Contains("get_runs lists them", ex.Message);
    }

    // ---------- the desk ----------

    private long SeedDesk()
    {
        long runId = _desk.SeedRun(RunnerDesk.TraderId, "NIFTY", "Stopped");
        using var db = _desk.Db();

        var trader = db.AppUsers.Single(u => u.Id == RunnerDesk.TraderId);
        trader.Email = Email;

        var run = db.SimulationRuns.Single(r => r.Id == runId);
        run.LastError = $"Traceback: psycopg.OperationalError password={Password} at {LogPath}";
        run.CompletedUtc = DateTime.UtcNow.AddMinutes(-5);

        db.PaperPositions.Add(new PaperPosition
        {
            SimulationRunId = runId, StrategyName = "Ghost", GroupId = "G1", Symbol = "NSE:NIFTY26OCT25000CE", Direction = "SHORT",
            Quantity = 0, AveragePrice = 100m, LastMarkPrice = 115m, RealizedPnl = -975m, Status = "Closed",
            OpenedUtc = DateTime.UtcNow.AddMinutes(-50), ClosedUtc = DateTime.UtcNow.AddMinutes(-10),
        });
        db.PaperOrders.AddRange(
            new PaperOrder { SimulationRunId = runId, StrategyName = "Ghost", GroupId = "G1", Symbol = "NSE:NIFTY26OCT25000CE", Side = "SELL", Quantity = 1, Status = "Filled", FillPrice = 100m, CreatedUtc = DateTime.UtcNow.AddMinutes(-50), FilledUtc = DateTime.UtcNow.AddMinutes(-50) },
            new PaperOrder { SimulationRunId = runId, StrategyName = "Ghost", GroupId = "G1", Symbol = "NSE:NIFTY26OCT25000CE", Side = "BUY", Quantity = 1, Status = "Filled", FillPrice = 115m, CreatedUtc = DateTime.UtcNow.AddMinutes(-10), FilledUtc = DateTime.UtcNow.AddMinutes(-10) });
        db.SimulationSignals.Add(new SimulationSignal
        {
            SimulationRunId = runId, StrategyName = "Ghost", SignalType = "RUN_STOPPED", TimestampUtc = DateTime.UtcNow.AddMinutes(-5),
            MetadataJson = JsonSerializer.Serialize(new { reason = $"stopped on the daily loss limit; api token={Token}" }),
        });

        db.Incidents.Add(new Incident
        {
            Fingerprint = "f1", Agent = "logs", Rule = "api_errors", Severity = IncidentSeverity.High, Status = IncidentStatus.Open,
            Title = "API errors", Summary = "Unhandled exception in the API", Location = LogPath,
            EvidenceJson = JsonSerializer.Serialize(new[] { $"GET /api/x Authorization: Bearer {Bearer}", $"db password={Password}" }),
            FirstSeenUtc = DateTime.UtcNow.AddHours(-1), LastSeenUtc = DateTime.UtcNow,
        });

        db.DeskCheckups.Add(new DeskCheckup
        {
            Slot = DeskCheckupSlot.OnRequest, Status = DeskCheckupStatus.Done, Verdict = DeskCheckupVerdict.Ok,
            Headline = "All clear", Host = Host, RequestedBy = "admin", RequestedUtc = DateTime.UtcNow.AddMinutes(-3),
            StartedUtc = DateTime.UtcNow.AddMinutes(-3), CompletedUtc = DateTime.UtcNow.AddMinutes(-2),
            ItemsJson = $$"""[{"key":"api","area":"Server","title":"API","state":"ok","detail":"Answering on {{Host}}, token={{Token}}","action":""}]""",
        });

        db.SaveChanges();
        return runId;
    }

    private IEnumerable<(IAiTool Tool, string Args)> Tools(TradingDbContext db, long runId)
    {
        yield return (new RunsTool(History(db)), "{}");
        yield return (new StrategyHistoryTool(History(db), db), """{"period":"last_7_days"}""");
        yield return (RunToolFor(db), $$"""{"runId":{{runId}}}""");
        yield return (new IncidentsTool(db), """{"which":"recent","days":3}""");
        yield return (new CheckupTool(db), "{}");
        yield return (new ForecastsTool(db), "{}");
    }

    private RunTool RunToolFor(TradingDbContext db)
    {
        var lots = new PositionGreeksTests.FixedLots(65);
        var charges = new RunCharges(db, lots);
        return new RunTool(db, new PositionViewBuilder(db, lots, new PositionGreeksBuilder(db, PositionGreeksTests.Sessions())), new RunPnl(db, lots, charges));
    }

    private LiveRunHistoryBuilder History(TradingDbContext db)
    {
        var lots = new PositionGreeksTests.FixedLots(65);
        var charges = new RunCharges(db, lots);
        var catalog = new StrategyCatalogService(
            new PythonEngineLocator(Microsoft.Extensions.Options.Options.Create(_desk.Options),
                RecapClockTests.Inert<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>.Create()),
            NullLogger<StrategyCatalogService>.Instance);
        return new LiveRunHistoryBuilder(db, _desk.Registry, catalog, lots, charges, new RunPnl(db, lots, charges));
    }
}
