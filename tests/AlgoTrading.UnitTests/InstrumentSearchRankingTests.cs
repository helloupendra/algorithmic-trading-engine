using System.Collections.Generic;
using System.Linq;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Domain.Instruments;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Ordering search results by relevance rather than alphabetically.
/// </summary>
/// <remarks>
/// Every symbol below was reported as unfindable, and all of them were in the
/// master the whole time — the results were sorted by symbol and cut at fifty,
/// so a page of ETFs and bonds stood between the query and the answer. "TCS"
/// was the worst of them: enough bonds matched that NSE:TCS-EQ never appeared
/// at all.
/// </remarks>
public class InstrumentSearchRankingTests
{
    private static Instrument Row(string symbol, string description, string type) => new()
    {
        Symbol = symbol,
        Description = description,
        InstrumentType = type,
        Exchange = symbol.Split(':')[0],
    };

    /// <summary>The real rows, as they sit in the instrument master.</summary>
    private static readonly List<Instrument> Master =
    [
        Row("NSE:NIFTY50-INDEX", "NIFTY50-INDEX", "INDEX"),
        Row("NSE:NIFTY500-INDEX", "NIFTY500-INDEX", "INDEX"),
        Row("NSE:NIFTY50DIVPOINT-INDEX", "NIFTY50DIVPOINT-INDEX", "INDEX"),
        Row("NSE:NIFTYBANK-INDEX", "NIFTYBANK-INDEX", "INDEX"),
        Row("NSE:AXISVALUE-EQ", "AXIS NIFTY500 VALUE 50 ETF", "EQ"),
        Row("NSE:EQUAL50-EQ", "MIRAE ASSET NIFTY50 EQUAL WEIGHT ETF", "EQ"),
        Row("NSE:HDFCVALUE-EQ", "HDFC NIFTY50 VALUE 20 ETF", "EQ"),
        Row("NSE:BANKNIFTY1-EQ", "KOTAK NIFTY BANK ETF", "EQ"),
        Row("NSE:BANKNIFTY26AUG31000CE", "BANKNIFTY 25 Aug 26 31000 CE", "CE"),
        Row("NSE:TCS-EQ", "TATA CONSULTANCY SERV LT", "EQ"),
        Row("NSE:750TCSL28-N0", "TCSL 7.50% 2028", "N0"),
        Row("NSE:835TCSL27-N0", "TVS CREDIT 8.35% 2027", "N0"),
        Row("NSE:HDFCBANK-EQ", "HDFC BANK LTD", "EQ"),
        Row("NSE:765HDFC34-N1", "HDFC BANK 7.65% 2034 SR2", "N1"),
        Row("NSE:H1861D46DD-MF", "HDFCAMC - H1861D46DD", "MF"),
        Row("NSE:RELIANCE-EQ", "RELIANCE INDUSTRIES LTD", "EQ"),
        Row("NSE:RCOM-BE", "RELIANCE COMMUNICATIONS L", "BE"),
        Row("NSE:RELCHEMQ-EQ", "RELIANCE CHEMOTEX IND LTD", "EQ"),
        Row("BSE:SENSEX-INDEX", "SENSEX-INDEX", "INDEX"),
        Row("NSE:AXSENSEX-EQ", "AXIS S&P BSE SENSEX ETF", "EQ"),
    ];

    /// <summary>The controller's ordering, run in memory.</summary>
    private static List<string> Search(string query, string? alias = null)
    {
        string q = query.Trim().ToUpperInvariant();
        var rank = InstrumentSearchRanking.RankBy(q, alias).Compile();
        var kind = InstrumentSearchRanking.KindRank().Compile();

        return Master
            .Where(x => x.Symbol.ToUpper().Contains(q)
                        || x.Description.ToUpper().Contains(q)
                        || (alias != null && x.Symbol == alias))
            .OrderBy(rank)
            .ThenBy(kind)
            .ThenBy(x => x.Symbol.Length)
            .ThenBy(x => x.Symbol)
            .Select(x => x.Symbol)
            .ToList();
    }

    [Fact]
    public void The_index_beats_the_funds_that_merely_mention_it()
    {
        // Every NIFTY500 fund's description contains "NIFTY50" as a substring,
        // which is how AXISVALUE and EQUAL50 came to sort above the index.
        Assert.Equal("NSE:NIFTY50-INDEX", Search("NIFTY50").First());
    }

    [Fact]
    public void A_whole_base_name_beats_a_longer_one_that_starts_with_it()
        => Assert.True(Search("NIFTY50").IndexOf("NSE:NIFTY50-INDEX")
                     < Search("NIFTY50").IndexOf("NSE:NIFTY50DIVPOINT-INDEX"));

    [Fact]
    public void An_alias_finds_an_index_the_query_cannot_spell()
    {
        // The words are reversed: nothing in "BANKNIFTY" is a substring of
        // "NIFTYBANK", so no amount of ranking alone would ever reach it.
        Assert.Equal("NSE:NIFTYBANK-INDEX", Search("BANKNIFTY", "NSE:NIFTYBANK-INDEX").First());
    }

    [Fact]
    public void Case_does_not_matter()
    {
        foreach (var spelling in new[] { "banknifty", "BANKNIFTY", "BankNifty" })
        {
            Assert.Equal("NSE:NIFTYBANK-INDEX", Search(spelling, "NSE:NIFTYBANK-INDEX").First());
        }
    }

    [Fact]
    public void A_stock_outranks_the_bonds_named_after_it()
    {
        // NSE:TCS-EQ was not merely low: enough bonds matched that it fell past
        // the fiftieth row and out of the response.
        Assert.Equal("NSE:TCS-EQ", Search("TCS").First());
    }

    [Fact]
    public void A_stock_outranks_bonds_and_mutual_funds()
        => Assert.Equal("NSE:HDFCBANK-EQ", Search("HDFC").First());

    [Fact]
    public void An_exact_stock_beats_another_company_starting_with_the_same_letters()
        => Assert.Equal("NSE:RELIANCE-EQ", Search("RELIANCE").First());

    [Fact]
    public void Options_sort_last_so_a_chain_cannot_fill_the_results()
    {
        var results = Search("BANKNIFTY", "NSE:NIFTYBANK-INDEX");
        Assert.True(results.IndexOf("NSE:BANKNIFTY26AUG31000CE") > results.IndexOf("NSE:BANKNIFTY1-EQ"));
    }

    [Fact]
    public void A_bse_index_is_reachable_once_its_master_is_imported()
    {
        // This one was never a ranking problem: BSE_CM.csv was not downloaded
        // by setup.sh, so BSE:SENSEX-INDEX did not exist locally at all.
        Assert.Equal("BSE:SENSEX-INDEX", Search("SENSEX").First());
    }

    [Theory]
    [InlineData("INDEX", 0)]
    [InlineData("EQ", 1)]
    [InlineData("FUT", 2)]
    [InlineData("N0", 3)]
    [InlineData("MF", 3)]
    [InlineData("CE", 4)]
    [InlineData("PE", 4)]
    public void Tradable_kinds_rank_above_the_rest(string type, int expected)
    {
        var kind = InstrumentSearchRanking.KindRank().Compile();
        Assert.Equal(expected, kind(Row("X:Y", "d", type)));
    }
}

public class InstrumentSearchTokensTests
{
    [Fact]
    public void A_strike_typed_the_way_people_say_it_is_three_tokens()
    {
        Assert.Equal(new[] { "NIFTY", "23500", "PE" }, AlgoTrading.Domain.Instruments.InstrumentSearchRanking.Tokens("nifty 23500 pe"));
    }

    [Fact]
    public void One_word_is_one_token_and_blanks_are_none()
    {
        Assert.Equal(new[] { "HDFCBANK" }, AlgoTrading.Domain.Instruments.InstrumentSearchRanking.Tokens("  hdfcbank "));
        Assert.Empty(AlgoTrading.Domain.Instruments.InstrumentSearchRanking.Tokens("   "));
        Assert.Empty(AlgoTrading.Domain.Instruments.InstrumentSearchRanking.Tokens(null));
    }

    [Fact]
    public void Commas_separate_too()
    {
        Assert.Equal(new[] { "BANKNIFTY", "56500", "PE" }, AlgoTrading.Domain.Instruments.InstrumentSearchRanking.Tokens("BANKNIFTY,56500 PE"));
    }
}
