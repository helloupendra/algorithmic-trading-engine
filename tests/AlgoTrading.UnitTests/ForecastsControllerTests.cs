using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Domain.Enums;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The Analysis module's API: "written before, scored after", enforced by the
/// server and not by whoever calls it.
/// </summary>
/// <remarks>
/// What is pinned down: the server's own clock stamps a forecast; one for a
/// session that has opened on its exchange is refused, and SENSEX follows BSE,
/// not NSE; a second forecast for the same model version, index and session is
/// refused; only a registered version can forecast; an outcome is accepted
/// once, after the close, and a scorer that raced another cannot overwrite it;
/// the list filters as asked and refuses a filter it does not know.
/// </remarks>
public class ForecastsControllerTests
{
    // Monday 28 Sep 2026, an ordinary NSE and BSE trading day.
    private static readonly DateOnly Monday = new(2026, 9, 28);
    private const string Key = "range.har-vix";
    private const string Version = "2026-09-27.1";

    [Fact]
    public async Task A_forecast_before_the_open_is_stored_with_the_server_s_clock()
    {
        await using var db = NewDb(NewName());
        await Register(db);
        var now = Ist(2026, 9, 28, 8, 50, 4);

        var view = Ok<ForecastView>(await Controller(db, now).Issue(RangeRequest(), CancellationToken.None));

        Assert.True(view.Id > 0);
        Assert.Equal(now, view.IssuedUtc);
        Assert.Equal(DateTimeKind.Utc, view.IssuedUtc.Kind);
        Assert.Equal(Monday, view.SessionDate);
        Assert.Equal("NIFTY", view.Underlying);
        Assert.Equal(1.02, view.Prediction!.Value.GetProperty("median").GetDouble());
        Assert.Null(view.Outcome);
        Assert.Null(view.ScoredUtc);

        var stored = await db.Forecasts.SingleAsync();
        Assert.Equal(now, stored.IssuedUtc);
        Assert.Contains("\"median\"", stored.PredictionJson);
    }

    [Theory]
    [InlineData(9, 14, 59, false)]
    [InlineData(9, 15, 0, true)]
    [InlineData(11, 0, 0, true)]
    public async Task A_forecast_for_a_session_that_has_opened_is_refused(int hour, int minute, int second, bool refused)
    {
        await using var db = NewDb(NewName());
        await Register(db);

        var result = await Controller(db, Ist(2026, 9, 28, hour, minute, second)).Issue(RangeRequest(), CancellationToken.None);

        if (refused)
        {
            Assert.IsType<ConflictObjectResult>(result);
            Assert.Empty(db.Forecasts);
        }
        else
        {
            Assert.IsType<OkObjectResult>(result);
        }
    }

    [Fact]
    public async Task A_forecast_for_a_past_session_is_refused()
    {
        // Back-dating is the first thing the rule exists to stop.
        await using var db = NewDb(NewName());
        await Register(db);

        var result = await Controller(db, Ist(2026, 9, 29, 8, 50)).Issue(RangeRequest(), CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task A_forecast_for_a_future_session_is_accepted()
    {
        await using var db = NewDb(NewName());
        await Register(db);

        var result = await Controller(db, Ist(2026, 9, 25, 20, 0)).Issue(RangeRequest(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task SENSEX_follows_BSE_s_calendar_and_NIFTY_follows_NSE_s()
    {
        // A BSE-only holiday: SENSEX has no session to forecast, NIFTY does.
        await using var db = NewDb(NewName());
        await Register(db);
        var calendar = new FakeCalendar(new MarketHoliday { Exchange = "BSE", Date = Monday, Name = "BSE closed", Closure = MarketClosure.FullDay });
        var controller = Controller(db, Ist(2026, 9, 28, 8, 50), calendar);

        var sensex = await controller.Issue(RangeRequest(underlying: "SENSEX"), CancellationToken.None);
        var nifty = await controller.Issue(RangeRequest(underlying: "NIFTY"), CancellationToken.None);

        var refusal = Assert.IsType<BadRequestObjectResult>(sensex);
        Assert.Contains("BSE", Message(refusal));
        Assert.IsType<OkObjectResult>(nifty);
    }

    [Fact]
    public async Task A_weekend_has_no_session_to_forecast()
    {
        await using var db = NewDb(NewName());
        await Register(db);

        var result = await Controller(db, Ist(2026, 9, 25, 20, 0)).Issue(RangeRequest(session: new DateOnly(2026, 9, 26)), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task A_second_forecast_for_the_same_model_version_index_and_session_is_refused()
    {
        await using var db = NewDb(NewName());
        await Register(db);
        await Register(db, version: "2026-09-27.2");
        var controller = Controller(db, Ist(2026, 9, 28, 8, 50));

        var first = Ok<ForecastView>(await controller.Issue(RangeRequest(), CancellationToken.None));
        var again = await controller.Issue(RangeRequest(median: 0.5), CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(again);
        Assert.Contains(first.Id.ToString(), Message(conflict));
        Assert.Equal(1.02, JsonDocument.Parse((await db.Forecasts.SingleAsync()).PredictionJson).RootElement.GetProperty("median").GetDouble());

        // Another version, or another index, is another forecast.
        Assert.IsType<OkObjectResult>(await controller.Issue(RangeRequest(version: "2026-09-27.2"), CancellationToken.None));
        Assert.IsType<OkObjectResult>(await controller.Issue(RangeRequest(underlying: "BANKNIFTY"), CancellationToken.None));
    }

    [Fact]
    public async Task Only_a_registered_model_version_can_forecast_and_only_its_own_target()
    {
        await using var db = NewDb(NewName());
        await Register(db);
        var controller = Controller(db, Ist(2026, 9, 28, 8, 50));

        var unregistered = await controller.Issue(RangeRequest(version: "2026-09-27.9"), CancellationToken.None);
        var wrongTarget = await controller.Issue(ProbabilityRequest(Key, Version, "trend", 0.4), CancellationToken.None);

        Assert.Contains("not registered", Message(Assert.IsType<BadRequestObjectResult>(unregistered)));
        Assert.IsType<BadRequestObjectResult>(wrongTarget);
        Assert.Empty(db.Forecasts);
    }

    [Theory]
    [InlineData("volatility", "NIFTY")]
    [InlineData("range", "FINNIFTY")]
    [InlineData("range", "ALL")]
    public async Task An_unknown_target_or_underlying_is_refused(string target, string underlying)
    {
        await using var db = NewDb(NewName());
        await Register(db);

        var result = await Controller(db, Ist(2026, 9, 28, 8, 50)).Issue(RangeRequest(target: target, underlying: underlying), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Target_and_underlying_are_stored_in_their_canonical_spelling()
    {
        await using var db = NewDb(NewName());
        await Register(db);

        var view = Ok<ForecastView>(await Controller(db, Ist(2026, 9, 28, 8, 50))
            .Issue(RangeRequest(target: "Range", underlying: "nifty"), CancellationToken.None));

        Assert.Equal("range", view.Target);
        Assert.Equal("NIFTY", view.Underlying);
    }

    [Theory]
    [InlineData(1.2)]
    [InlineData(-0.1)]
    public async Task A_probability_outside_zero_to_one_is_refused(double p)
    {
        await using var db = NewDb(NewName());
        await Register(db, key: "trend.logit", target: "trend");

        var result = await Controller(db, Ist(2026, 9, 28, 8, 50)).Issue(ProbabilityRequest("trend.logit", Version, "trend", p), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task A_forecast_without_its_baseline_or_scored_number_is_refused()
    {
        await using var db = NewDb(NewName());
        await Register(db);
        var controller = Controller(db, Ist(2026, 9, 28, 8, 50));

        var noBaseline = RangeRequest();
        noBaseline.Baseline = null;
        var noMedian = RangeRequest();
        noMedian.Prediction = Json(new { low80 = 0.7, high80 = 1.5 });

        Assert.IsType<BadRequestObjectResult>(await controller.Issue(noBaseline, CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await controller.Issue(noMedian, CancellationToken.None));
        Assert.Empty(db.Forecasts);
    }

    [Fact]
    public async Task An_outcome_before_the_close_is_refused_and_after_it_is_stored_once()
    {
        string name = NewName();
        await using var db = NewDb(name);
        await Register(db);
        long id = Ok<ForecastView>(await Controller(db, Ist(2026, 9, 28, 8, 50)).Issue(RangeRequest(), CancellationToken.None)).Id;

        var early = await Controller(db, Ist(2026, 9, 28, 15, 29, 59)).Score(id, Outcome(0.004, 0.061), CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(early);

        var scoredAt = Ist(2026, 9, 28, 15, 50);
        var view = Ok<ForecastView>(await Controller(db, scoredAt).Score(id, Outcome(0.004, 0.061), CancellationToken.None));
        Assert.Equal(scoredAt, view.ScoredUtc);
        Assert.Equal(1.016, view.Outcome!.Value.GetProperty("range").GetDouble());
        Assert.Equal(0.004, view.Scores!.Value.GetProperty("loss").GetDouble());

        var again = await Controller(db, Ist(2026, 9, 29, 15, 50)).Score(id, Outcome(0.5, 0.061), CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(again);

        await using var check = NewDb(name);
        var stored = await check.Forecasts.SingleAsync();
        Assert.Equal(0.004, stored.Loss);
        Assert.Equal(0.061, stored.BaselineLoss);
        Assert.Equal(scoredAt, stored.ScoredUtc);
    }

    [Fact]
    public async Task An_outcome_that_lands_after_another_scorer_s_is_a_conflict_not_an_overwrite()
    {
        string name = NewName();
        await using var mine = NewDb(name);
        await Register(mine);
        long id = Ok<ForecastView>(await Controller(mine, Ist(2026, 9, 28, 8, 50)).Issue(RangeRequest(), CancellationToken.None)).Id;

        // This context reads the row while it is unscored…
        _ = await mine.Forecasts.SingleAsync(x => x.Id == id);

        // …and another scorer writes the outcome first.
        await using (var other = NewDb(name))
        {
            var row = await other.Forecasts.SingleAsync(x => x.Id == id);
            row.Loss = 0.1;
            row.BaselineLoss = 0.2;
            row.ScoresJson = "{\"loss\":0.1,\"baselineLoss\":0.2}";
            row.OutcomeJson = "{}";
            row.ScoredUtc = Ist(2026, 9, 28, 15, 50);
            await other.SaveChangesAsync();
        }

        var result = await Controller(mine, Ist(2026, 9, 28, 15, 51)).Score(id, Outcome(0.9, 0.2), CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
        await using var check = NewDb(name);
        Assert.Equal(0.1, (await check.Forecasts.SingleAsync()).Loss);
    }

    [Fact]
    public async Task An_outcome_needs_both_losses_and_an_existing_forecast()
    {
        await using var db = NewDb(NewName());
        await Register(db);
        long id = Ok<ForecastView>(await Controller(db, Ist(2026, 9, 28, 8, 50)).Issue(RangeRequest(), CancellationToken.None)).Id;
        var controller = Controller(db, Ist(2026, 9, 28, 16, 0));

        var noBaselineLoss = new ScoreForecastRequest { Outcome = Json(new { range = 1.0 }), Scores = Json(new { loss = 0.1 }) };
        var negative = Outcome(-0.1, 0.2);

        Assert.IsType<BadRequestObjectResult>(await controller.Score(id, noBaselineLoss, CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await controller.Score(id, negative, CancellationToken.None));
        Assert.IsType<NotFoundObjectResult>(await controller.Score(id + 100, Outcome(0.1, 0.2), CancellationToken.None));
        Assert.Null((await db.Forecasts.SingleAsync()).ScoredUtc);
    }

    [Fact]
    public async Task The_list_filters_by_session_target_and_underlying_newest_session_first()
    {
        await using var db = NewDb(NewName());
        await Register(db);
        await Register(db, key: "trend.logit", target: "trend");
        db.Forecasts.AddRange(
            Row(Key, "range", "NIFTY", new DateOnly(2026, 9, 24)),
            Row(Key, "range", "NIFTY", new DateOnly(2026, 9, 25)),
            Row(Key, "range", "SENSEX", new DateOnly(2026, 9, 25)),
            Row("trend.logit", "trend", "NIFTY", new DateOnly(2026, 9, 25)),
            Row(Key, "range", "NIFTY", new DateOnly(2026, 9, 28)));
        await db.SaveChangesAsync();
        var controller = Controller(db, Ist(2026, 9, 28, 9, 30));

        var all = await List(controller);
        Assert.Equal(5, all.Count);
        Assert.Equal(new DateOnly(2026, 9, 28), all[0].SessionDate);
        Assert.Equal(new DateOnly(2026, 9, 24), all[^1].SessionDate);

        var oneDay = await List(controller, from: new DateOnly(2026, 9, 25), to: new DateOnly(2026, 9, 25));
        Assert.Equal(3, oneDay.Count);

        var niftyRange = await List(controller, target: "range", underlying: "NIFTY");
        Assert.Equal(new[] { new DateOnly(2026, 9, 28), new DateOnly(2026, 9, 25), new DateOnly(2026, 9, 24) }, niftyRange.Select(f => f.SessionDate));

        var trend = await List(controller, target: "trend");
        Assert.Equal("trend.logit", Assert.Single(trend).ModelKey);
    }

    [Fact]
    public async Task The_list_defaults_to_the_last_thirty_days()
    {
        await using var db = NewDb(NewName());
        await Register(db);
        db.Forecasts.AddRange(
            Row(Key, "range", "NIFTY", new DateOnly(2026, 8, 20)),
            Row(Key, "range", "NIFTY", new DateOnly(2026, 9, 1)));
        await db.SaveChangesAsync();

        var items = await List(Controller(db, Ist(2026, 9, 28, 9, 30)));

        Assert.Equal(new DateOnly(2026, 9, 1), Assert.Single(items).SessionDate);
    }

    [Theory]
    [InlineData("volatility", null)]
    [InlineData(null, "FINNIFTY")]
    public async Task An_unknown_list_filter_is_refused_rather_than_ignored(string? target, string? underlying)
    {
        await using var db = NewDb(NewName());

        var result = await Controller(db, Ist(2026, 9, 28, 9, 30)).List(null, null, target, underlying);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task A_from_after_to_is_refused()
    {
        await using var db = NewDb(NewName());

        var result = await Controller(db, Ist(2026, 9, 28, 9, 30)).List(new DateOnly(2026, 9, 28), new DateOnly(2026, 9, 1), null, null);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Registering_again_updates_the_backtest_but_never_the_target()
    {
        await using var db = NewDb(NewName());
        var first = Ist(2026, 9, 27, 10, 0);
        var later = Ist(2026, 9, 27, 11, 0);

        Ok<ForecastModelView>(await Controller(db, first).RegisterModel(ModelRequest(backtest: new { configurationsTried = 4 }), CancellationToken.None));
        var updated = Ok<ForecastModelView>(await Controller(db, later).RegisterModel(ModelRequest(backtest: new { configurationsTried = 5 }), CancellationToken.None));

        Assert.Equal(first, updated.RegisteredUtc);
        Assert.Equal(later, updated.UpdatedUtc);
        Assert.Equal(5, updated.Backtest!.Value.GetProperty("configurationsTried").GetInt32());
        Assert.Single(db.ForecastModels);

        var retarget = await Controller(db, later).RegisterModel(ModelRequest(target: "trend"), CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(retarget);

        var models = Assert.IsAssignableFrom<IEnumerable<ForecastModelView>>(
            Assert.IsType<OkObjectResult>(await Controller(db, later).Models(CancellationToken.None)).Value).ToList();
        Assert.Equal("range", Assert.Single(models).Target);
    }

    [Theory]
    [InlineData("", Version, "range", "d")]
    [InlineData("range har", Version, "range", "d")]
    [InlineData(Key, "", "range", "d")]
    [InlineData(Key, Version, "volatility", "d")]
    [InlineData(Key, Version, "range", " ")]
    public async Task A_model_needs_a_key_a_version_a_known_target_and_a_description(string key, string version, string target, string description)
    {
        await using var db = NewDb(NewName());

        var result = await Controller(db, Ist(2026, 9, 27, 10, 0))
            .RegisterModel(new RegisterForecastModelRequest { Key = key, Version = version, Target = target, Description = description }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(db.ForecastModels);
    }

    [Fact]
    public async Task The_scoreboard_endpoint_reads_scores_and_the_backtest_from_the_database()
    {
        await using var db = NewDb(NewName());
        await Controller(db, Ist(2026, 9, 27, 10, 0)).RegisterModel(ModelRequest(backtest: new { holdout = new { skill = 0.073 } }), CancellationToken.None);
        db.Forecasts.AddRange(
            Row(Key, "range", "NIFTY", new DateOnly(2026, 9, 24), loss: 0.1, baselineLoss: 0.2, scores: "{\"metrics\":{\"covered80\":true},\"calibration\":[{\"p\":0.31,\"y\":0}]}"),
            Row(Key, "range", "NIFTY", new DateOnly(2026, 9, 25), loss: 0.3, baselineLoss: 0.2, scores: "{\"metrics\":{\"covered80\":false},\"calibration\":[{\"p\":0.35,\"y\":1}]}"),
            // Issued, not yet scored: on the board, not in the count.
            Row(Key, "range", "SENSEX", new DateOnly(2026, 9, 28)));
        await db.SaveChangesAsync();

        var result = await Controller(db, Ist(2026, 9, 28, 9, 30)).Scoreboard(CancellationToken.None);

        var rows = Assert.IsAssignableFrom<IReadOnlyList<ScoreboardRow>>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(new[] { "ALL", "NIFTY", "SENSEX" }, rows.Select(r => r.Underlying));
        var all = rows[0];
        Assert.Equal(2, all.LiveCount);
        Assert.Equal(0.2, all.MeanLoss!.Value, 12);
        Assert.Equal(0.2, all.MeanBaselineLoss!.Value, 12);
        Assert.Equal(0.5, all.Coverage80);
        var bin = Assert.Single(all.Calibration);
        Assert.Equal((0.3, 0.4, 2), (bin.From, bin.To, bin.N));
        Assert.Equal(0.073, all.Backtest!.Value.GetProperty("holdout").GetProperty("skill").GetDouble());
        Assert.Equal(0, rows[2].LiveCount);
    }

    [Fact]
    public void The_JSON_is_the_contract_s_shape()
    {
        // The Python models and the page are written to docs/modules/analysis.md
        // by other hands; a renamed property here is a silent break there.
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var forecast = ForecastsController.ToView(new Forecast
        {
            Id = 1,
            ModelKey = Key,
            ModelVersion = Version,
            Target = "range",
            Underlying = "NIFTY",
            SessionDate = Monday,
            IssuedUtc = new DateTime(2026, 9, 28, 3, 20, 4, DateTimeKind.Utc),
            PredictionJson = "{\"median\":1.02}",
            BaselineJson = "{\"median\":1.1}",
            InputsJson = "{\"vixPrevClose\":11.9}",
        });

        string json = JsonSerializer.Serialize(forecast, web);

        Assert.Equal(
            "{\"id\":1,\"modelKey\":\"range.har-vix\",\"modelVersion\":\"2026-09-27.1\",\"target\":\"range\",\"underlying\":\"NIFTY\"," +
            "\"sessionDate\":\"2026-09-28\",\"issuedUtc\":\"2026-09-28T03:20:04Z\",\"prediction\":{\"median\":1.02},\"baseline\":{\"median\":1.1}," +
            "\"inputs\":{\"vixPrevClose\":11.9},\"outcome\":null,\"scores\":null,\"scoredUtc\":null}",
            json);

        var row = ForecastScoreboard.Build([new ScoreboardModel(Key, Version, "range", "d", null)], []).Single();
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(row, web));
        Assert.Equal(
            new[]
            {
                "modelKey", "modelVersion", "target", "underlying", "description", "liveCount", "meanLoss", "meanBaselineLoss",
                "skill", "diffCiLow", "diffCiHigh", "status", "statusReason", "coverage80", "calibration", "firstSession",
                "lastSession", "backtest",
            },
            doc.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void The_contract_s_request_bodies_bind()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var issue = JsonSerializer.Deserialize<IssueForecastRequest>(
            """
            { "modelKey": "range.har-vix", "modelVersion": "2026-09-27.1", "target": "range", "underlying": "NIFTY",
              "sessionDate": "2026-09-28", "prediction": { "median": 1.02 }, "baseline": { "median": 1.1 }, "inputs": { "r1": 0.84 } }
            """, web)!;
        Assert.Equal(Monday, issue.SessionDate);
        Assert.Equal(1.02, issue.Prediction!.Value.GetProperty("median").GetDouble());

        var score = JsonSerializer.Deserialize<ScoreForecastRequest>(
            """
            { "outcome": { "range": 1.016, "trendDay": false },
              "scores": { "loss": 0.004, "baselineLoss": 0.061, "metrics": { "covered80": true }, "calibration": [ { "p": 0.31, "y": 0 } ] } }
            """, web)!;
        Assert.Equal(0.061, score.Scores!.Value.GetProperty("baselineLoss").GetDouble());

        var model = JsonSerializer.Deserialize<RegisterForecastModelRequest>(
            """{ "key": "range.har-vix", "version": "2026-09-27.1", "target": "range", "description": "d", "backtest": { "configurationsTried": 4 } }""", web)!;
        Assert.Equal(4, model.Backtest!.Value.GetProperty("configurationsTried").GetInt32());
    }

    [Fact]
    public void Reads_need_the_analysis_module_and_writes_also_need_Admin_or_Service()
    {
        var type = typeof(ForecastsController);
        Assert.Equal(PlatformModules.Analysis, ModuleOf(type));
        Assert.True(PlatformModules.IsKnown("analysis"));
        Assert.NotNull(type.GetCustomAttribute<AuthorizeAttribute>());

        foreach (var action in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            bool write = action.GetCustomAttributes().Any(a => a is HttpPostAttribute);
            bool read = action.GetCustomAttributes().Any(a => a is HttpGetAttribute);
            if (!write && !read) continue;

            var roles = action.GetCustomAttribute<AuthorizeAttribute>()?.Roles;
            if (write)
            {
                Assert.Equal($"{UserRoles.Admin},{UserRoles.Service}", roles);
            }
            else
            {
                Assert.Null(roles);
            }
        }
    }

    // ---------- helpers ----------

    private static string? ModuleOf(Type type)
    {
        var attribute = type.GetCustomAttribute<RequireModuleAttribute>();
        return attribute is null
            ? null
            : (string?)typeof(RequireModuleAttribute).GetField("_moduleKey", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(attribute);
    }

    private static string NewName() => $"forecasts-{Guid.NewGuid():N}";

    private static TradingDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase(name).Options);

    /// <summary>An IST wall-clock time as UTC.</summary>
    internal static DateTime Ist(int y, int mo, int d, int h, int mi, int s = 0)
        => new DateTime(y, mo, d, h, mi, s, DateTimeKind.Utc).AddMinutes(-330);

    private static ForecastsController Controller(TradingDbContext db, DateTime nowUtc, IMarketCalendar? calendar = null)
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "7"), new Claim(ClaimTypes.Name, "engine-service"), new Claim(ClaimTypes.Role, UserRoles.Service) },
            authenticationType: "Test");

        return new ForecastsController(db, new MarketSessionService(calendar ?? new FakeCalendar()), new FixedTime(nowUtc))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
            },
        };
    }

    private static async Task Register(TradingDbContext db, string key = Key, string version = Version, string target = "range")
    {
        db.ForecastModels.Add(new ForecastModel
        {
            Key = key,
            Version = version,
            Target = target,
            Description = "test model",
            RegisteredUtc = Ist(2026, 9, 27, 10, 0),
            UpdatedUtc = Ist(2026, 9, 27, 10, 0),
        });
        await db.SaveChangesAsync();
    }

    private static RegisterForecastModelRequest ModelRequest(string target = "range", object? backtest = null) => new()
    {
        Key = Key,
        Version = Version,
        Target = target,
        Description = "Log-range on 1-, 5- and 22-day mean log ranges.",
        Backtest = backtest is null ? null : Json(backtest),
    };

    private static IssueForecastRequest RangeRequest(
        string target = "range",
        string underlying = "NIFTY",
        string version = Version,
        DateOnly? session = null,
        double median = 1.02) => new()
    {
        ModelKey = Key,
        ModelVersion = version,
        Target = target,
        Underlying = underlying,
        SessionDate = session ?? Monday,
        Prediction = Json(new { median, low80 = 0.68, high80 = 1.55, buckets = new { quiet = 0.31, normal = 0.45, wild = 0.24 } }),
        Baseline = Json(new { median = 1.1, low80 = 0.7, high80 = 1.6 }),
        Inputs = Json(new { vixPrevClose = 11.9, r1 = 0.84 }),
    };

    private static IssueForecastRequest ProbabilityRequest(string key, string version, string target, double p) => new()
    {
        ModelKey = key,
        ModelVersion = version,
        Target = target,
        Underlying = "NIFTY",
        SessionDate = Monday,
        Prediction = Json(new { p }),
        Baseline = Json(new { p = 0.35 }),
    };

    private static ScoreForecastRequest Outcome(double loss, double baselineLoss) => new()
    {
        Outcome = Json(new { open = 24660.1, high = 24790.4, low = 24540.0, close = 24771.2, range = 1.016, bucket = "normal" }),
        Scores = Json(new { loss, baselineLoss, metrics = new { covered80 = true }, calibration = new[] { new { p = 0.31, y = 0 } } }),
    };

    private static Forecast Row(string key, string target, string underlying, DateOnly session, double? loss = null, double? baselineLoss = null, string? scores = null) => new()
    {
        ModelKey = key,
        ModelVersion = Version,
        Target = target,
        Underlying = underlying,
        SessionDate = session,
        IssuedUtc = IstTime.FromIst(session.ToDateTime(new TimeOnly(8, 50))),
        PredictionJson = "{\"median\":1.0}",
        BaselineJson = "{\"median\":1.1}",
        InputsJson = "{}",
        Loss = loss,
        BaselineLoss = baselineLoss,
        ScoresJson = scores,
        OutcomeJson = loss is null ? null : "{}",
        ScoredUtc = loss is null ? null : IstTime.FromIst(session.ToDateTime(new TimeOnly(15, 50))),
    };

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private static async Task<List<ForecastView>> List(
        ForecastsController controller,
        DateOnly? from = null,
        DateOnly? to = null,
        string? target = null,
        string? underlying = null)
    {
        var result = await controller.List(from, to, target, underlying);
        return Assert.IsAssignableFrom<IEnumerable<ForecastView>>(Assert.IsType<OkObjectResult>(result).Value).ToList();
    }

    private static T Ok<T>(IActionResult result) => Assert.IsType<T>(Assert.IsType<OkObjectResult>(result).Value);

    private static string Message(ObjectResult result)
        => JsonSerializer.SerializeToElement(result.Value).GetProperty("message").GetString() ?? string.Empty;

    private sealed class FixedTime(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    internal sealed class FakeCalendar(params MarketHoliday[] holidays) : IMarketCalendar
    {
        public bool IsLoaded => true;
        public MarketHoliday? HolidayOn(string exchange, DateOnly date) => holidays.FirstOrDefault(h => h.Exchange == exchange && h.Date == date);
        public MarketSpecialSession? SpecialSessionOn(string exchange, DateOnly date) => null;
        public bool HasYear(string exchange, int year) => true;
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

/// <summary>
/// The forecast tables as Postgres will have them. The in-memory provider the
/// controller tests use ignores indexes and keys, so the rules the database
/// itself must hold — one forecast per model version, index and session;
/// only a registered version; scored once — are checked on the real model.
/// </summary>
public class ForecastTablesTests
{
    [Fact]
    public void One_forecast_per_model_version_target_underlying_and_session_is_enforced_by_the_database()
    {
        var entity = EntityFor("forecasts");

        var unique = Assert.Single(entity.GetIndexes(), i => i.IsUnique);
        Assert.Equal(
            new[] { nameof(Forecast.ModelKey), nameof(Forecast.ModelVersion), nameof(Forecast.Target), nameof(Forecast.Underlying), nameof(Forecast.SessionDate) },
            unique.Properties.Select(p => p.Name));
        Assert.Null(unique.GetFilter());

        Assert.Contains(entity.GetIndexes(), i => !i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(Forecast.SessionDate) }));
    }

    [Fact]
    public void A_forecast_must_name_a_registered_model_version()
    {
        var models = EntityFor("forecast_models");
        Assert.Equal(new[] { nameof(ForecastModel.Key), nameof(ForecastModel.Version) }, models.FindPrimaryKey()!.Properties.Select(p => p.Name));

        var fk = Assert.Single(EntityFor("forecasts").GetForeignKeys());
        Assert.Equal("forecast_models", fk.PrincipalEntityType.GetTableName());
        Assert.Equal(new[] { nameof(Forecast.ModelKey), nameof(Forecast.ModelVersion) }, fk.Properties.Select(p => p.Name));
        Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior);
    }

    [Fact]
    public void Only_ScoredUtc_is_a_concurrency_token()
    {
        Assert.Equal(
            new[] { nameof(Forecast.ScoredUtc) },
            EntityFor("forecasts").GetProperties().Where(p => p.IsConcurrencyToken).Select(p => p.Name));
    }

    [Fact]
    public void The_JSON_columns_are_unbounded_text()
    {
        var entity = EntityFor("forecasts");
        foreach (var name in new[] { nameof(Forecast.PredictionJson), nameof(Forecast.BaselineJson), nameof(Forecast.InputsJson), nameof(Forecast.OutcomeJson), nameof(Forecast.ScoresJson) })
        {
            Assert.Equal("text", entity.FindProperty(name)!.GetColumnType());
        }

        Assert.Equal("date", EntityFor("forecasts").FindProperty(nameof(Forecast.SessionDate))!.GetColumnType());
    }

    private static IEntityType EntityFor(string table)
    {
        using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseNpgsql("Host=unused").Options);
        var model = db.GetService<IDesignTimeModel>().Model;
        return model.GetEntityTypes().Single(e => e.GetTableName() == table);
    }
}
