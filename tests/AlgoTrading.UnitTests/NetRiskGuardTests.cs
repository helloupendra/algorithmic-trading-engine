using AlgoTrading.Api.Services;
using AlgoTrading.Contracts.Strategies;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The run-level stop and target are judged on net P&amp;L, after the charges
/// of the fills so far, like the backtest (owner's decision, 27 Sep).
/// </summary>
public class NetRiskGuardTests
{
    private static OverallRiskDto Rules(decimal? stop = null, decimal? target = null) =>
        new() { StopLoss = stop, Target = target };

    [Fact]
    public void A_run_4000_down_with_6000_of_charges_hits_a_5000_stop()
    {
        // Gross −4,000 reads as inside the line; after charges it is −10,000.
        Assert.Null(StrategyRiskGuardService.EvaluateOverallNet(-4_000m, 0m, 0m, Rules(stop: 5_000m), new RiskTrailState()));

        var reason = StrategyRiskGuardService.EvaluateOverallNet(-4_000m, 0m, 6_000m, Rules(stop: 5_000m), new RiskTrailState());

        Assert.NotNull(reason);
        Assert.StartsWith("Stop loss hit", reason);
    }

    [Fact]
    public void A_gross_5000_that_is_1000_down_after_charges_is_not_a_target()
    {
        Assert.Null(StrategyRiskGuardService.EvaluateOverallNet(3_000m, 2_000m, 6_000m, Rules(target: 5_000m), new RiskTrailState()));
        Assert.NotNull(StrategyRiskGuardService.EvaluateOverallNet(9_000m, 2_000m, 6_000m, Rules(target: 5_000m), new RiskTrailState()));
    }

    [Fact]
    public void Net_is_realized_plus_unrealized_less_charges_never_plus()
    {
        Assert.Equal(-10_000m, StrategyRiskGuardService.OverallNet(-4_000m, 0m, 6_000m));
        Assert.Equal(1_500m, StrategyRiskGuardService.OverallNet(1_000m, 500m, -20m));
    }
}
