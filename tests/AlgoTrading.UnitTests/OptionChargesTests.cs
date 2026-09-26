using AlgoTrading.Application.Risk;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The C# charges must equal the Python engine's (core/charges.py), or a live
/// run and its backtest would be charged differently.
/// </summary>
public class OptionChargesTests
{
    [Fact]
    public void A_day_of_Fulcrum_is_charged_what_the_engine_charges_it()
    {
        // 344 round trips of 2 NIFTY lots (130 units) at about ₹108 a unit —
        // run 259 on 25 Sep. Reference from:
        //   CostModel().charges(buy_turnover=14040*344, sell_turnover=14064*344, orders=688)
        var c = OptionCharges.For(14040m * 344, 14064m * 344, 688);

        Assert.Equal(13760.00m, c.Brokerage);
        Assert.Equal(7257.02m, c.Stt);
        Assert.Equal(3386.62m, c.Exchange);
        Assert.Equal(9.67m, c.Sebi);
        Assert.Equal(144.89m, c.Stamp);
        Assert.Equal(3088.13m, c.Gst);
        Assert.Equal(27646.33m, c.Total, 0);  // within a rupee of 27,646.34: the parts are rounded first
    }

    [Fact]
    public void Stt_is_on_the_sell_side_and_stamp_on_the_buy_side()
    {
        var buyOnly = OptionCharges.For(100000m, 0m, 1);
        var sellOnly = OptionCharges.For(0m, 100000m, 1);

        Assert.Equal(0m, buyOnly.Stt);
        Assert.Equal(150m, sellOnly.Stt);
        Assert.Equal(3m, buyOnly.Stamp);
        Assert.Equal(0m, sellOnly.Stamp);
    }

    [Fact]
    public void Nothing_traded_costs_nothing()
    {
        Assert.Equal(0m, OptionCharges.For(0m, 0m, 0).Total);
    }

    [Fact]
    public void Nonsense_inputs_do_not_produce_negative_charges()
    {
        Assert.True(OptionCharges.For(-5m, -5m, -2).Total >= 0m);
    }
}
