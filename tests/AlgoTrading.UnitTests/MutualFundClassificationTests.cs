using AlgoTrading.Infrastructure.Services.MutualFunds.Analytics;

namespace AlgoTrading.UnitTests;

/// <summary>AMFI's category lines, the benchmark each scheme is compared with, and name keys, on real AMFI strings.</summary>
public class MutualFundClassificationTests
{
    [Theory]
    [InlineData("Open Ended Schemes(Equity Scheme - Large Cap Fund)", "Equity Scheme - Large Cap Fund")]
    [InlineData("Open Ended Schemes(Equity Schemes - Large Cap Fund)", "Equity Scheme - Large Cap Fund")]
    [InlineData("Open Ended Schemes(Equity Scheme - ELSS)", "Equity Scheme - ELSS")]
    [InlineData("Open Ended Schemes(Equity Schemes - ELSS- Tax Saver Fund)", "Equity Scheme - ELSS")]
    [InlineData("Open Ended Schemes(Equity Schemes - Thematic Fund)", "Equity Scheme - Sectoral/Thematic")]
    [InlineData("Open Ended Schemes(Equity Scheme - Sectoral/ Thematic)", "Equity Scheme - Sectoral/Thematic")]
    [InlineData("Open Ended Schemes(Hybrid Schemes - Balanced Advantage Fund/ Dynamic Asset Allocation)", "Hybrid Scheme - Balanced Advantage/Dynamic Asset Allocation")]
    [InlineData("Open Ended Schemes(Hybrid Scheme - Dynamic Asset Allocation or Balanced Advantage)", "Hybrid Scheme - Balanced Advantage/Dynamic Asset Allocation")]
    [InlineData("Open Ended Schemes(Solution Oriented Schemes ** - Retirement Fund)", "Solution Oriented Scheme - Retirement Fund")]
    [InlineData("Open Ended Schemes(Other Scheme - Other  ETFs)", "Other Scheme - Other ETFs")]
    public void Spellings_of_one_category_share_a_peer_group(string raw, string name)
    {
        var category = MutualFundCategories.Parse(raw);
        Assert.Equal(name, category.Name);
        Assert.Equal("Open Ended", category.Type);
        Assert.Equal("open ended|" + name.ToLowerInvariant(), category.PeerKey);
    }

    [Fact]
    public void Open_and_close_ended_schemes_of_one_category_are_not_peers()
    {
        var open = MutualFundCategories.Parse("Open Ended Schemes(Equity Scheme - ELSS)");
        var closed = MutualFundCategories.Parse("Close Ended Schemes(Equity Scheme - ELSS)");
        Assert.NotEqual(open.PeerKey, closed.PeerKey);
        Assert.Equal("Equity Scheme - ELSS (close-ended)", closed.PeerName);
    }

    [Theory]
    [InlineData("Open Ended Schemes(Income)")]
    [InlineData("Open Ended Schemes(Growth)")]
    [InlineData("Close Ended Schemes(ELSS)")]
    [InlineData("")]
    public void Sections_from_before_sebi_s_2017_categories_have_no_peer_group(string raw) =>
        Assert.Null(MutualFundCategories.Parse(raw).PeerKey);

    [Fact]
    public void Debt_categories_renamed_in_substance_stay_apart()
    {
        var old = MutualFundCategories.Parse("Open Ended Schemes(Debt Scheme - Liquid Fund)");
        var renamed = MutualFundCategories.Parse("Open Ended Schemes(Income/Debt Oriented Schemes - Liquid Fund)");
        Assert.NotNull(old.PeerKey);
        Assert.NotNull(renamed.PeerKey);
        Assert.NotEqual(old.PeerKey, renamed.PeerKey);
    }

    [Theory]
    [InlineData("UTI Nifty 50 Index Fund", "Open Ended Schemes(Other Scheme - Index Funds)", "NIFTY 50")]
    [InlineData("Nippon India ETF Nifty 50 BeES", "Open Ended Schemes(Other Scheme - Other  ETFs)", "NIFTY 50")]
    [InlineData("SBI Nifty50 ETF", "Open Ended Schemes(Exchange Traded Funds (ETFs) - Equity ETF)", "NIFTY 50")]
    [InlineData("UTI - Nifty Next 50 Index Fund", "Open Ended Schemes(Other Scheme - Index Funds)", "NIFTY NEXT 50")]
    [InlineData("Motilal Oswal Nifty Midcap 150 Index Fund", "Open Ended Schemes(Index Funds - Equity Funds)", "NIFTY MIDCAP 150")]
    [InlineData("Motilal Oswal Nifty 500 Index Fund", "Open Ended Schemes(Other Scheme - Index Funds)", "NIFTY 500")]
    [InlineData("Kotak Nifty Bank ETF", "Open Ended Schemes(Other Scheme - Other  ETFs)", "NIFTY BANK")]
    [InlineData("Nippon India Nifty Smallcap 250 Index Fund", "Open Ended Schemes(Other Scheme - Index Funds)", "NIFTY SMALLCAP 250")]
    [InlineData("Motilal Oswal Nifty 500 Multicap 50:25:25 Index Fund", "Open Ended Schemes(Other Scheme - Index Funds)", "NIFTY500 MULTICAP 50:25:25")]
    public void An_index_fund_is_compared_with_the_index_its_name_tracks(string name, string category, string index)
    {
        var choice = MutualFundBenchmarks.Choose(name, MutualFundCategories.Parse(category));
        Assert.Equal("index-name", choice.Basis);
        Assert.Equal(index, choice.Index!.Key);
    }

    [Theory]
    [InlineData("DSP Nifty 50 Equal Weight Index Fund")]
    [InlineData("ICICI Prudential Nifty 100 Low Volatility 30 ETF")]
    [InlineData("Motilal Oswal Nifty Midcap 150 Momentum 50 Index Fund")]
    [InlineData("UTI Nifty200 Momentum 30 Index Fund")]
    [InlineData("Kotak Nifty PSU Bank ETF")]
    [InlineData("Nippon India ETF Nifty Private Bank")]
    [InlineData("Nippon India ETF Gold BeES")]
    [InlineData("ICICI Prudential Nifty50 Value 20 ETF")]
    public void An_index_fund_on_an_index_not_collected_has_no_benchmark(string name)
    {
        var choice = MutualFundBenchmarks.Choose(name, MutualFundCategories.Parse("Open Ended Schemes(Other Scheme - Index Funds)"));
        Assert.Null(choice.Index);
        Assert.Equal("none", choice.Basis);
        Assert.StartsWith("Benchmark not collected yet", choice.Note, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Open Ended Schemes(Equity Scheme - Large Cap Fund)", "NIFTY 100")]
    [InlineData("Open Ended Schemes(Equity Schemes - Large & Mid Cap Fund)", "NIFTY LARGEMIDCAP 250")]
    [InlineData("Open Ended Schemes(Equity Scheme - Mid Cap Fund)", "NIFTY MIDCAP 150")]
    [InlineData("Open Ended Schemes(Equity Scheme - Small Cap Fund)", "NIFTY SMALLCAP 250")]
    [InlineData("Open Ended Schemes(Equity Scheme - Multi Cap Fund)", "NIFTY500 MULTICAP 50:25:25")]
    [InlineData("Open Ended Schemes(Equity Scheme - Flexi Cap Fund)", "NIFTY 500")]
    [InlineData("Open Ended Schemes(Equity Schemes - ELSS- Tax Saver Fund)", "NIFTY 500")]
    public void An_active_equity_fund_is_compared_with_its_category_s_usual_benchmark(string category, string index)
    {
        var choice = MutualFundBenchmarks.Choose("Mirae Asset Large Cap Fund", MutualFundCategories.Parse(category));
        Assert.Equal("category", choice.Basis);
        Assert.Equal(index, choice.Index!.Key);
        Assert.Contains("the fund's own SID may name another", choice.Note, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Open Ended Schemes(Debt Scheme - Liquid Fund)", "Benchmark not collected yet")]
    [InlineData("Open Ended Schemes(Hybrid Scheme - Aggressive Hybrid Fund)", "Benchmark not collected yet")]
    [InlineData("Open Ended Schemes(Equity Scheme - Sectoral/ Thematic)", "No benchmark: sectoral")]
    [InlineData("Open Ended Schemes(Income)", "No benchmark: AMFI lists")]
    public void Other_schemes_have_no_benchmark_and_say_why(string category, string start)
    {
        var choice = MutualFundBenchmarks.Choose("HDFC Liquid Fund", MutualFundCategories.Parse(category));
        Assert.Null(choice.Index);
        Assert.StartsWith(start, choice.Note, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Axis Children's Fund", "axis childrens fund")]
    [InlineData("Axis Children’s Fund", "axis childrens fund")]
    [InlineData("ICICI Prudential Banking & PSU Debt Fund", "icici prudential banking and psu debt fund")]
    [InlineData("SBI Nifty50 ETF", "sbi nifty 50 etf")]
    [InlineData("  360 ONE  FLEXICAP FUND ", "360 one flexicap fund")]
    public void Names_reduce_to_a_key(string name, string key) => Assert.Equal(key, MutualFundNames.Key(name));
}
