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

    [Fact]
    public void Crude_options_are_charged_at_MCX_rates()
    {
        // 2 lots of a CRUDEOIL option (100 barrels a lot), bought at 150 and
        // sold at 180: buy turnover 30,000, sell 36,000, two orders. The Python
        // engine gives 98.7317 (unrounded lines).
        var c = OptionCharges.For(30_000m, 36_000m, 2, ChargeSchedule.McxOptions);

        Assert.Equal(40m, c.Brokerage);
        Assert.Equal(18m, c.Stt);          // CTT 0.05% of the sell side
        Assert.Equal(27.59m, c.Exchange);  // MCX 0.0418% of both sides
        Assert.Equal(0.07m, c.Sebi);
        Assert.Equal(0.9m, c.Stamp);       // 0.003% of the buy side
        Assert.Equal(12.18m, c.Gst);
        Assert.Equal(98.74m, c.Total);

        // At the index-option rates it was charged at until 28 Sep: STT alone 54.
        Assert.Equal(54m, OptionCharges.For(30_000m, 36_000m, 2).Stt);
    }

    [Fact]
    public void Crude_futures_have_their_own_rates()
    {
        var c = OptionCharges.For(600_000m, 610_000m, 2, ChargeSchedule.McxFutures);

        Assert.Equal(61m, c.Stt);          // CTT 0.01% of the sell side
        Assert.Equal(25.41m, c.Exchange);  // MCX 0.0021%
        Assert.Equal(12m, c.Stamp);        // 0.002% of the buy side
    }

    [Theory]
    [InlineData("MCX:CRUDEOIL26OCT5500CE", "MCX options")]
    [InlineData("mcx:crudeoil26oct5500pe", "MCX options")]
    [InlineData("MCX:CRUDEOIL26OCTFUT", "MCX futures")]
    [InlineData("MCX:CRUDEOILM26OCTFUT", "MCX futures")]
    [InlineData("NSE:NIFTY26SEP25000CE", "index options")]
    [InlineData("BSE:SENSEX26OCT82000PE", "index options")]
    [InlineData("NSE:RELIANCE26NOV1400CE", "index options")]
    [InlineData("NSE:BAJAJ-AUTO26NOV8000CE", "index options")]
    [InlineData("NSE:NIFTY26NOVFUT", "NSE futures")]
    [InlineData("nse:nifty26novfut", "NSE futures")]
    [InlineData("NSE:RELIANCE26NOVFUT", "NSE futures")]
    [InlineData("NSE:BAJAJ-AUTO26NOVFUT", "NSE futures")]
    [InlineData("BSE:SENSEX26NOVFUT", "BSE futures")]
    [InlineData("NSE:RELIANCE-EQ", "NSE shares (intraday)")]
    [InlineData("nse:reliance-eq", "NSE shares (intraday)")]
    [InlineData("NSE:BAJAJ-AUTO-EQ", "NSE shares (intraday)")]
    [InlineData("NSE:M&M-EQ", "NSE shares (intraday)")]
    [InlineData("NSE:SOMESTOCK-BE", "NSE shares (intraday)")]
    [InlineData("BSE:RELIANCE-A", "BSE shares (intraday)")]
    [InlineData("NSE:NIFTY50-INDEX", "index options")]
    [InlineData("BSE:SENSEX-INDEX", "index options")]
    [InlineData("NSE:BAJAJ-AUTO", "index options")]
    [InlineData("MANUAL", "index options")]
    [InlineData("", "index options")]
    [InlineData(null, "index options")]
    public void Each_symbol_is_charged_under_its_own_schedule(string? symbol, string schedule)
    {
        // The same table as SegmentTests in the engine's tests/test_charges.py.
        Assert.Equal(schedule, ChargeSchedule.ForSymbol(symbol).Name);
    }

    // Until 2 Nov 2026 futures and shares were charged as index options: STT at 0.15%
    // of notional. The figures below are test_charges.py's too, unrounded there.

    [Fact]
    public void A_nifty_future_round_trip()
    {
        // 65 units bought and sold at 25,000: 16,25,000 a side, two orders. Python: 966.2155.
        var c = OptionCharges.For(1_625_000m, 1_625_000m, 2, ChargeSchedule.ForSymbol("NSE:NIFTY26NOVFUT"));

        Assert.Equal(40m, c.Brokerage);
        Assert.Equal(812.5m, c.Stt);        // 0.05% of the sell side
        Assert.Equal(59.48m, c.Exchange);   // NSE 0.00183% of both sides
        Assert.Equal(3.25m, c.Sebi);
        Assert.Equal(32.5m, c.Stamp);       // 0.002% of the buy side
        Assert.Equal(18.49m, c.Gst);
        Assert.Equal(966.22m, c.Total);

        // At the index-option rates it was charged at until now.
        Assert.True(OptionCharges.For(1_625_000m, 1_625_000m, 2).Total > 3_500m);
    }

    [Fact]
    public void A_sensex_future_pays_no_exchange_charge()
    {
        // Python: 882.976.
        var c = OptionCharges.For(1_600_000m, 1_600_000m, 2, ChargeSchedule.ForSymbol("BSE:SENSEX26NOVFUT"));

        Assert.Equal(800m, c.Stt);
        Assert.Equal(0m, c.Exchange);
        Assert.Equal(882.98m, c.Total);
    }

    [Fact]
    public void Shares_are_charged_as_intraday()
    {
        // 1,000 RELIANCE at 1,400: 14,00,000 a side. Python: 543.9368.
        var c = OptionCharges.For(1_400_000m, 1_400_000m, 2, ChargeSchedule.ForSymbol("NSE:RELIANCE-EQ"));

        Assert.Equal(350m, c.Stt);          // 0.025% of the sell side
        Assert.Equal(85.96m, c.Exchange);   // NSE 0.00307% of both sides
        Assert.Equal(42m, c.Stamp);         // 0.003% of the buy side
        Assert.Equal(543.94m, c.Total);
    }

    [Fact]
    public void Bse_shares_take_bses_exchange_charge()
    {
        // Python: 566.404.
        var c = OptionCharges.For(1_400_000m, 1_400_000m, 2, ChargeSchedule.ForSymbol("BSE:RELIANCE-A"));

        Assert.Equal(105m, c.Exchange);     // BSE 0.00375%
        Assert.Equal(566.40m, c.Total);
    }

    [Fact]
    public void The_option_and_mcx_figures_did_not_move()
    {
        Assert.Equal(new ChargeSchedule("index options", 20m, 0.15m, 0.03503m, 0.0001m, 0.003m, 18m), ChargeSchedule.IndexOptions);
        Assert.Equal(new ChargeSchedule("MCX options", 20m, 0.05m, 0.0418m, 0.0001m, 0.003m, 18m), ChargeSchedule.McxOptions);
        Assert.Equal(new ChargeSchedule("MCX futures", 20m, 0.01m, 0.0021m, 0.0001m, 0.002m, 18m), ChargeSchedule.McxFutures);
    }
}
