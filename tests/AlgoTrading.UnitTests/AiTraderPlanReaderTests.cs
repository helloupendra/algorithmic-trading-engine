using AlgoTrading.Api.Services.AiTrader;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>The model's answer read as a plan: the shape checked, nothing in it trusted.</summary>
public class AiTraderPlanReaderTests
{
    [Fact]
    public void A_buy_in_a_fenced_block_is_read_field_by_field()
    {
        var (plan, error) = AiTraderPlanReader.Read("""
            Here is my plan:
            ```json
            {"action":"buy","underlying":"nifty","option":"ce","strike":"ATM+1","lots":"2","stopLoss":88.5,"target":"150",
             "reason":"Spot above VWAP and EMA20 > EMA50; call OI falling at 22700.","confidence":0.62}
            ```
            """);

        Assert.Null(error);
        Assert.Equal((AiTraderPlan.Buy, "NIFTY", "CE", "ATM+1", 2, 88.5m, 150m, 0.62),
            (plan!.Action, plan.Underlying, plan.Option, plan.Strike, plan.Lots, plan.StopLoss, plan.Target, plan.Confidence));
    }

    [Theory]
    [InlineData("""{"action":"sell","reason":"x"}""", "is not none, buy")]
    [InlineData("""{"action":"buy"}""", "needs its reason")]
    [InlineData("""{"action":"none","reason":"Quiet.","confidence":3}""", "confidence")]
    [InlineData("I would wait.", "not the JSON object")]
    public void An_answer_that_is_not_a_plan_says_why(string answer, string why)
    {
        var (plan, error) = AiTraderPlanReader.Read(answer);
        Assert.Null(plan);
        Assert.Contains(why, error);
    }

    [Fact]
    public void Start_and_stop_strategy_and_exit_carry_their_ids()
    {
        var (exit, _) = AiTraderPlanReader.Read("""{"action":"exit","position_id":41,"reason":"Target near."}""");
        var (stop, _) = AiTraderPlanReader.Read("""{"action":"stop strategy","runId":"7","reason":"Chop."}""");
        Assert.Equal(41, exit!.PositionId);
        Assert.Equal((AiTraderPlan.StopStrategy, 7L), (stop!.Action, stop.RunId));
    }

    [Theory]
    [InlineData("ATM", 22650)]
    [InlineData("atm+1", 22700)]
    [InlineData("ATM-2", 22550)]
    [InlineData("22800", 22800)]
    [InlineData(null, 22650)]
    public void A_strike_is_named_around_the_money_or_as_a_number(string? strike, int expected) =>
        Assert.Equal(expected, AiTraderPlanReader.StrikeOf(strike, 22650m, 50m));

    [Theory]
    [InlineData("ATM+20")]
    [InlineData("deep OTM")]
    public void A_strike_it_cannot_place_is_none(string strike) =>
        Assert.Null(AiTraderPlanReader.StrikeOf(strike, 22650m, 50m));
}
