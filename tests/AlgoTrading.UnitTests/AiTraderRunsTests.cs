using AlgoTrading.Api.Services;
using AlgoTrading.Api.Services.AiTrader;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.ValueObjects;
using AlgoTrading.Infrastructure.Services;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The AI Trader's strategy runs through the desk's own start (<see cref="StrategyStartService"/>, the path the Start
/// button takes) and stop: in its own account only, with the grants its account holds, refused where a person's start
/// would be. Real runner processes, as in the start endpoint's tests (<see cref="RunnerDesk"/>).
/// </summary>
public class AiTraderRunsTests
{
    [Fact]
    public async Task It_starts_an_allowed_strategy_in_its_own_account_through_the_same_checks_as_a_person()
    {
        using var desk = new RunnerDesk("TrendRiderV2BE", "TrendRiderV2OB");
        await using var db = desk.Db();
        var account = await AiTraderAccount.EnsureAsync(db, default);
        // The real package check: its account's grants are what let it through.
        var runs = Runs(desk, db, new StrategyAccessService(db, desk.Limits), sessions: null);

        var started = await runs.StartAsync(account, "TrendRiderV2BE", "NIFTY", 1, default);

        Assert.True(started.Started, started.Message);
        var run = Assert.Single(desk.Runs());
        Assert.Equal((run.Id, account.Id, account.Id, AiTraderAccount.UserName, "TrendRiderV2BE", "Running"),
            (started.RunId!.Value, run.UserId, run.StartedByUserId!.Value, run.StartedByName, run.StrategyName, run.Status));
        Assert.Equal(account.Id, desk.Registry.Get(run.Id)!.UserId);
        // Its TrendRider runs trade with their day rules off, like the desk's (owner, 25 Nov 2026).
        Assert.Contains("\"day_filters\":\"off\"", run.ParametersJson);
    }

    [Fact]
    public void Only_trendrider_scripts_are_started_with_their_day_rules_off()
    {
        Assert.Equal("off", AiTraderRuns.SettingsFor("TrendRiderV2OB")!["day_filters"].GetString());
        Assert.Equal("off", AiTraderRuns.SettingsFor("TrendRider")!["day_filters"].GetString());
        Assert.Null(AiTraderRuns.SettingsFor("GhostTangentCrossingsV2"));
    }

    [Fact]
    public async Task Without_its_grants_a_start_is_refused_by_the_package_check_and_leaves_no_row()
    {
        using var desk = new RunnerDesk("TrendRiderV2BE");
        await using var db = desk.Db();
        var account = await AiTraderAccount.EnsureAsync(db, default);
        db.UserStrategyGrants.RemoveRange(db.UserStrategyGrants.Where(g => g.UserId == account.Id));
        await db.SaveChangesAsync();

        var started = await Runs(desk, db, new StrategyAccessService(db, desk.Limits), sessions: null).StartAsync(account, "TrendRiderV2BE", "NIFTY", 1, default);

        Assert.False(started.Started);
        Assert.Contains("no strategy package", started.Message);
        Assert.Empty(desk.Runs());
    }

    [Fact]
    public async Task On_a_day_with_no_session_a_start_is_refused_as_a_persons_would_be()
    {
        using var desk = new RunnerDesk("TrendRiderV2BE");
        await using var db = desk.Db();
        var account = await AiTraderAccount.EnsureAsync(db, default);

        var started = await Runs(desk, db, access: null, sessions: new NoSession()).StartAsync(account, "TrendRiderV2BE", "NIFTY", 1, default);

        Assert.False(started.Started);
        Assert.Contains("a live run can start only on a trading day", started.Message);
        Assert.Empty(desk.Runs());
    }

    [Fact]
    public async Task It_stops_its_own_run_and_never_another_accounts()
    {
        using var desk = new RunnerDesk("TrendRiderV2BE");
        await using var db = desk.Db();
        var account = await AiTraderAccount.EnsureAsync(db, default);
        var runs = Runs(desk, db, access: null, sessions: null);
        var own = await runs.StartAsync(account, "TrendRiderV2BE", "NIFTY", 1, default);
        Assert.True(own.Started, own.Message);
        Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await desk.Start("BANKNIFTY", RunnerDesk.TraderId));
        long theirs = desk.Runs().Single(r => r.UserId == RunnerDesk.TraderId).Id;

        var refused = await runs.StopAsync(account, theirs, "Stopped by the AI Trader", default);
        var stopped = await runs.StopAsync(account, own.RunId!.Value, "Stopped by the AI Trader", default);

        Assert.False(refused.Stopped);
        Assert.Contains("not one of its running strategy runs", refused.Message);
        Assert.NotNull(desk.Registry.Get(theirs));
        Assert.True(stopped.Stopped, stopped.Message);
        Assert.Null(desk.Registry.Get(own.RunId.Value));
        Assert.Equal("Stopped", desk.Runs().Single(r => r.Id == own.RunId).Status);
    }

    private static AiTraderRuns Runs(RunnerDesk desk, AlgoTrading.Infrastructure.Persistence.TradingDbContext db, IStrategyAccessService? access,
        IMarketSessionService? sessions) =>
        new(desk.Starts(db, access), desk.Catalog, desk.RunControl(db), desk.Registry, desk.Limits,
            RecapClockTests.Inert<IDerivativesInstrumentService>.Create(), desk.Memory, sessions);

    private sealed class NoSession : IMarketSessionService
    {
        public MarketSessionInfo GetSessionInfo(DateTime utcNow, string exchange, string segment) => new()
        {
            Exchange = exchange, Segment = segment, UtcNow = utcNow, IsTradingDay = false, SessionOpenUtc = utcNow, SessionCloseUtc = utcNow,
        };

        public bool IsMarketOpen(DateTime utcNow, string exchange, string segment) => false;

        public DateTime GetNextMarketOpenUtc(DateTime utcNow, string exchange, string segment) => utcNow.AddDays(1);
    }
}
