using System.Text.Json;
using AlgoTrading.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// A strategy that can only trade in a backtest is refused a live start. SignalBuilder
/// could be deployed, look healthy, and never place an order, because the live runner
/// books no bare BUY or SELL but Ghost's.
/// </summary>
public class BacktestOnlyStrategyTests
{
    [Fact]
    public async Task A_live_start_is_refused_with_the_reason_and_no_run_is_left_behind()
    {
        using var desk = new RunnerDesk();
        desk.AddStrategy("Builder", "backtest_only = True");

        var result = await desk.Start("NIFTY", strategyId: StrategyCatalogService.StableId("Builder"));

        var refused = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("runs in backtests only", JsonSerializer.Serialize(refused.Value));
        Assert.Empty(desk.Runs());
    }

    [Fact]
    public async Task Any_other_strategy_starts_as_it_always_has()
    {
        using var desk = new RunnerDesk();
        desk.AddStrategy("Plain", "instrument_kind = \"options\"");

        var result = await desk.Start("NIFTY", strategyId: StrategyCatalogService.StableId("Plain"));

        Assert.IsType<OkObjectResult>(result);
    }
}
