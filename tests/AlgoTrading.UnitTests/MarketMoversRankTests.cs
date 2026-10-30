using AlgoTrading.Contracts.MarketIntel;
using AlgoTrading.Infrastructure.Services;

namespace AlgoTrading.UnitTests;

public class MarketMoversRankTests
{
    private static MoverDto Mover(string symbol, decimal? change) =>
        new(symbol, symbol + ".NS", 100m, 100m, change);

    [Fact]
    public void A_falling_share_is_never_a_gainer_even_when_it_is_the_only_one()
    {
        var (gainers, losers) = MarketIntelService.RankMovers(new[] { Mover("NSE:RELIANCE-EQ", -0.44m) }, 10);

        Assert.Empty(gainers);
        Assert.Equal("NSE:RELIANCE-EQ", Assert.Single(losers).Symbol);
    }

    [Fact]
    public void Gainers_rise_most_first_losers_fall_most_first_and_unchanged_or_unknown_are_in_neither()
    {
        var movers = new[]
        {
            Mover("A", 1.2m), Mover("B", 3.5m), Mover("C", -0.4m), Mover("D", -2.1m), Mover("E", 0m), Mover("F", null),
        };

        var (gainers, losers) = MarketIntelService.RankMovers(movers, 10);

        Assert.Equal(new[] { "B", "A" }, gainers.Select(m => m.Symbol));
        Assert.Equal(new[] { "D", "C" }, losers.Select(m => m.Symbol));
        Assert.Equal(new[] { "B" }, MarketIntelService.RankMovers(movers, 1).Gainers.Select(m => m.Symbol));
    }
}
