using System.Text.Json;
using AlgoTrading.Infrastructure.Providers.Angel;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The movers screen: Angel's three differently-shaped answers turned into one
/// table, and the four names a desk gives a change in open interest.
/// </summary>
/// <remarks>
/// The payloads below are cut from real SmartAPI answers on 2026-09-16: a
/// price list carries ltp and netChange, an OI list carries opnInterest and
/// netChangeOpnInterest and NO price, and a FULL quote carries percentChange.
/// </remarks>
public class AngelMoversTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private const string PriceGainers = """
    [{"tradingSymbol":"PATANJALI29SEP26FUT","percentChange":8.04,"symbolToken":68757,"ltp":364.0,"netChange":27.1},
     {"tradingSymbol":"POLICYBZR29SEP26FUT","percentChange":4.85,"symbolToken":68768,"ltp":1829.5,"netChange":84.6}]
    """;

    private const string OiGainers = """
    [{"tradingSymbol":"OFSS29SEP26FUT","percentChange":14.91,"symbolToken":68753,"opnInterest":1310600,"netChangeOpnInterest":170100.0},
     {"tradingSymbol":"PAYTM29SEP26FUT","percentChange":14.18,"symbolToken":68758,"opnInterest":19029075,"netChangeOpnInterest":2363500.0}]
    """;

    private const string Quotes = """
    {"fetched":[{"tradingSymbol":"OFSS29SEP26FUT","symbolToken":"68753","ltp":11438.0,"netChange":-222.0,"percentChange":-1.9,"opnInterest":1310700},
                {"tradingSymbol":"PAYTM29SEP26FUT","symbolToken":"68758","ltp":1786.6,"netChange":46.2,"percentChange":2.65,"opnInterest":19025450}],"unfetched":[]}
    """;

    private const string Pcr = """
    [{"pcr":0.52,"tradingSymbol":"329SEP2629SEP26FUT"},{"pcr":0.95,"tradingSymbol":"ABB29SEP26FUT"}]
    """;

    [Theory]
    [InlineData(5.0, 2.0, OiBuildup.LongBuildUp)]
    [InlineData(5.0, -2.0, OiBuildup.ShortBuildUp)]
    [InlineData(-5.0, 2.0, OiBuildup.ShortCovering)]
    [InlineData(-5.0, -2.0, OiBuildup.LongUnwinding)]
    [InlineData(0.0, 3.0, OiBuildup.Flat)]
    [InlineData(4.0, 0.01, OiBuildup.Flat)]
    public void Open_interest_and_price_read_together(double oi, double price, string expected)
    {
        Assert.Equal(expected, OiBuildup.Classify((decimal)oi, (decimal)price));
    }

    [Fact]
    public void A_price_list_keeps_its_price_change_and_names_the_underlying()
    {
        var rows = MoversAssembly.ParseList(Json(PriceGainers), oiList: false);
        Assert.Equal(2, rows.Count);
        Assert.Equal("PATANJALI", rows[0].Underlying);
        Assert.Equal(364.0m, rows[0].Ltp);
        Assert.Equal(8.04m, rows[0].PriceChangePercent);
        Assert.Null(rows[0].OiChangePercent);
    }

    [Fact]
    public void An_oi_list_carries_open_interest_and_no_price()
    {
        var rows = MoversAssembly.ParseList(Json(OiGainers), oiList: true);
        Assert.Equal(14.91m, rows[0].OiChangePercent);
        Assert.Equal(1310600m, rows[0].OpenInterest);
        Assert.Equal(0m, rows[0].PriceChangePercent);   // Angel does not send one
    }

    [Fact]
    public void The_build_up_table_prices_the_oi_rows_from_the_quote_call()
    {
        var oiRows = MoversAssembly.ParseList(Json(OiGainers), oiList: true);
        var quotes = MoversAssembly.ParseQuoteFacts(Json(Quotes));
        var rows = MoversAssembly.BuildUp(oiRows, quotes);

        var ofss = rows.Single(r => r.Underlying == "OFSS");
        Assert.Equal(-1.9m, ofss.PriceChangePercent);
        Assert.Equal(11438.0m, ofss.Ltp);            // the OI list had no price
        Assert.Equal(OiBuildup.ShortBuildUp, ofss.BuildUp);   // OI up, price down

        var paytm = rows.Single(r => r.Underlying == "PAYTM");
        Assert.Equal(OiBuildup.LongBuildUp, paytm.BuildUp);   // OI up, price up

        // Biggest OI move first, so the table opens on what changed most.
        Assert.Equal("OFSS", rows[0].Underlying);
    }

    [Fact]
    public void A_row_whose_price_is_missing_is_kept_and_left_unclassified()
    {
        var oiRows = MoversAssembly.ParseList(Json(OiGainers), oiList: true);
        var rows = MoversAssembly.BuildUp(oiRows, new Dictionary<long, MoversAssembly.QuoteFact>());
        Assert.Equal(2, rows.Count);                 // never silently dropped
        Assert.All(rows, r => Assert.Null(r.BuildUp));
    }

    [Fact]
    public void Put_call_ratios_are_read_per_underlying()
    {
        var rows = MoversAssembly.ParsePcr(Json(Pcr));
        Assert.Equal(2, rows.Count);
        Assert.Equal("ABB", rows[1].Underlying);
        Assert.Equal(0.95m, rows[1].Pcr);
    }

    [Theory]
    [InlineData("PAYTM29SEP26FUT", "PAYTM")]
    [InlineData("NIFTY25SEP26FUT", "NIFTY")]
    [InlineData("M&M29SEP26FUT", "M&M")]
    [InlineData("RELIANCE-EQ", "RELIANCE-EQ")]
    public void The_futures_symbol_is_shown_as_its_underlying(string symbol, string expected)
    {
        Assert.Equal(expected, MoversAssembly.Underlying(symbol));
    }

    [Fact]
    public void An_answer_that_is_not_a_list_is_empty_not_an_exception()
    {
        Assert.Empty(MoversAssembly.ParseList(Json("""{"message":"no"}"""), oiList: false));
        Assert.Empty(MoversAssembly.ParsePcr(Json("null")));
        Assert.Empty(MoversAssembly.ParseQuoteFacts(Json("""{"unfetched":[]}""")));
    }
}
