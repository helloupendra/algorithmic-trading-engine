using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Services;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Which activity rows of a run's live view carry their signal's metadata: the
/// run story draws a TrendRider entry's levels from its OPEN_GROUP row.
/// </summary>
public class RunStoryActivityTests
{
    private const string TrendRiderOpen =
        "{\"group_id\":\"TR2_20261117T035000_001\",\"direction\":\"BUY\",\"trend_rider\":{\"levels\":\"prev_day\",\"prev_high\":25291.1," +
        "\"prev_low\":25172.1,\"day_open\":25282.05,\"adr\":291.5,\"entry_spot\":25344.75," +
        "\"index_exits\":{\"stop_adr\":0.5,\"trail_arm_adr\":0.5,\"trail_giveback_adr\":0.35}}}";

    [Fact]
    public void An_open_group_carries_its_context()
    {
        Assert.Equal(TrendRiderOpen, StrategyController.ActivityMetadata("OPEN_GROUP", TrendRiderOpen));
    }

    [Fact]
    public void A_risk_update_still_carries_its_rules()
    {
        const string risk = "{\"risk\":{\"leg\":{\"targetPoints\":20}},\"by\":\"admin\"}";
        Assert.Equal(risk, StrategyController.ActivityMetadata(RunRiskRules.RiskUpdatedSignalType, risk));
    }

    [Fact]
    public void Other_rows_and_oversized_or_empty_metadata_carry_nothing()
    {
        Assert.Null(StrategyController.ActivityMetadata("CLOSE_GROUP", "{\"reason\":\"index trail\"}"));
        Assert.Null(StrategyController.ActivityMetadata("RUN_STOPPED", "{}"));
        Assert.Null(StrategyController.ActivityMetadata("OPEN_GROUP", null));
        Assert.Null(StrategyController.ActivityMetadata("OPEN_GROUP", ""));
        var huge = "{\"x\":\"" + new string('a', StrategyController.OpenMetadataLimit) + "\"}";
        Assert.Null(StrategyController.ActivityMetadata("OPEN_GROUP", huge));
    }
}
