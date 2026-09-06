using AlgoTrading.Infrastructure.Services;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The number the whole platform is judged on.
///
/// This existed twice — once private inside PaperTradingService and once
/// hand-rolled in LiveRunHistoryBuilder — and the copies had already drifted on
/// how they compared the direction string. Now there is one, and it is provable
/// without a database.
/// </summary>
public class PaperPnlTests
{
    private const int BankniftyLot = 30;

    [Fact]
    public void A_short_makes_money_when_the_price_falls()
    {
        // Sold at 500, bought back at 400: 100 points × 1 lot × 30.
        Assert.Equal(3000m, PaperPnl.Realized("SHORT", 500m, 400m, 1, BankniftyLot));
    }

    [Fact]
    public void A_short_loses_money_when_the_price_rises()
    {
        Assert.Equal(-3000m, PaperPnl.Realized("SHORT", 500m, 600m, 1, BankniftyLot));
    }

    [Fact]
    public void A_long_makes_money_when_the_price_rises()
    {
        Assert.Equal(3000m, PaperPnl.Realized("LONG", 400m, 500m, 1, BankniftyLot));
    }

    [Fact]
    public void A_long_loses_money_when_the_price_falls()
    {
        Assert.Equal(-3000m, PaperPnl.Realized("LONG", 500m, 400m, 1, BankniftyLot));
    }

    [Theory]
    [InlineData("LONG")]
    [InlineData("long")]
    [InlineData("Long")]
    public void Direction_is_read_case_insensitively(string direction)
    {
        // The two old copies disagreed here: one used == "LONG", the other
        // OrdinalIgnoreCase, so a position stored as "long" was valued as a
        // SHORT by one of them and a LONG by the other.
        Assert.Equal(3000m, PaperPnl.Realized(direction, 400m, 500m, 1, BankniftyLot));
        Assert.True(PaperPnl.IsLong(direction));
    }

    [Fact]
    public void Anything_that_is_not_long_is_treated_as_short()
    {
        Assert.False(PaperPnl.IsLong("SHORT"));
        Assert.False(PaperPnl.IsLong(""));
        Assert.False(PaperPnl.IsLong(null));
    }

    [Fact]
    public void Realized_and_unrealized_are_the_same_sum_against_a_different_price()
    {
        Assert.Equal(
            PaperPnl.Realized("SHORT", 500m, 450m, 2, BankniftyLot),
            PaperPnl.Unrealized("SHORT", 500m, 450m, 2, BankniftyLot));
    }

    [Fact]
    public void Quantity_and_lot_size_both_scale_the_result()
    {
        Assert.Equal(6000m, PaperPnl.Realized("SHORT", 500m, 400m, 2, BankniftyLot));
        Assert.Equal(6500m, PaperPnl.Realized("SHORT", 500m, 400m, 1, 65)); // NIFTY's lot
    }

    [Fact]
    public void A_missing_lot_size_is_treated_as_one_rather_than_zeroing_the_pnl()
    {
        // A zero multiplier would silently report every position as flat.
        Assert.Equal(100m, PaperPnl.Realized("SHORT", 500m, 400m, 1, 0));
        Assert.Equal(100m, PaperPnl.Realized("SHORT", 500m, 400m, 1, -5));
    }

    [Fact]
    public void A_position_closed_at_its_entry_made_nothing()
    {
        Assert.Equal(0m, PaperPnl.Realized("SHORT", 500m, 500m, 3, BankniftyLot));
        Assert.Equal(0m, PaperPnl.Realized("LONG", 500m, 500m, 3, BankniftyLot));
    }

    [Fact]
    public void Fractional_option_prices_survive_as_decimals()
    {
        // 0.05 × 30 = 1.5 — a float would not land on this exactly.
        Assert.Equal(1.5m, PaperPnl.Realized("SHORT", 12.35m, 12.30m, 1, BankniftyLot));
    }
}
