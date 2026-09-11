using AlgoTrading.Infrastructure.Providers.TrueData;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The symbol master is what answers the names the grammar cannot. Every row
/// below was copied from api.truedata.in/getAllSymbols on 2026-09-11, header
/// and all — including the duplicated "symbolalias" column, which is really
/// there and which a positional parser trips over.
/// </summary>
public class TrueDataSymbolMasterTests
{
    private const string Header =
        "symbolid,symbol,series,isin,exchange,lotsize,strike,expiry,metastocksymbol,symbolalias,symbolalias\n";

    private static TrueDataMasterRow Row(string line) =>
        Assert.Single(TrueDataSymbolMaster.ParseCsv(Header + line + "\n"));

    [Fact]
    public void A_weekly_option_maps_to_the_canonical_dated_grammar()
    {
        var row = Row("303000973,NIFTY26091518200CE,CE,,NSE,65,18200,15-09-2026,NIFTY26I618200,NIFTY26091518200CE");

        Assert.Equal(65, row.LotSize);
        Assert.Equal(18200m, row.Strike);
        Assert.Equal(new DateOnly(2026, 9, 15), row.Expiry);
        Assert.Equal("NSE:NIFTY2691518200CE", TrueDataSymbolMaster.ToCanonical(row));
    }

    [Fact]
    public void A_monthly_option_maps_too_which_is_why_the_master_is_imported()
    {
        // The grammar rules decline this one: the canonical monthly name carries
        // a month and no day. The master carries the day, so this is the answer.
        var row = Row("302965676,BANKNIFTY26092928500CE,CE,,NSE,30,28500,29-09-2026,BN,BN");
        Assert.Equal("NSE:BANKNIFTY2692928500CE", TrueDataSymbolMaster.ToCanonical(row));
    }

    [Fact]
    public void A_near_month_future_is_named_by_its_expiry_not_by_its_position()
    {
        // "CRUDEOIL-I" means a different contract after every expiry, so the
        // canonical name comes from the expiry the master reports.
        var row = Row("950000072,CRUDEOIL-I,XX,,MCX,100,0,21-09-2026,CRUDEOIL_I,CRUDEOIL_I");

        Assert.Equal(100, row.LotSize);
        Assert.Equal("MCX:CRUDEOIL26SEPFUT", TrueDataSymbolMaster.ToCanonical(row));
    }

    [Fact]
    public void The_second_and_third_futures_map_to_their_own_months()
    {
        Assert.Equal("MCX:CRUDEOIL26OCTFUT",
            TrueDataSymbolMaster.ToCanonical(Row("950000074,CRUDEOIL-II,XX,,MCX,100,0,19-10-2026,C,C")));
        Assert.Equal("NSE:NIFTY26NOVFUT",
            TrueDataSymbolMaster.ToCanonical(Row("900000600,NIFTY-III,XX,,NSE,65,0,23-11-2026,N,N")));
    }

    [Fact]
    public void October_to_December_options_take_the_single_character_month()
    {
        Assert.Equal("NSE:NIFTY26O1524000CE",
            TrueDataSymbolMaster.ToCanonical(Row("1,NIFTY26101524000CE,CE,,NSE,65,24000,15-10-2026,x,x")));
        Assert.Equal("NSE:NIFTY26D3124000PE",
            TrueDataSymbolMaster.ToCanonical(Row("2,NIFTY26123124000PE,PE,,NSE,65,24000,31-12-2026,x,x")));
    }

    [Fact]
    public void A_BSE_contract_keeps_its_exchange()
    {
        var row = Row("1,SENSEX26091574500CE,CE,,BSE,10,74500,15-09-2026,x,x");
        Assert.Equal("BSE:SENSEX2691574500CE", TrueDataSymbolMaster.ToCanonical(row));
    }

    [Fact]
    public void Cash_rows_have_no_expiry_and_are_left_to_the_grammar()
    {
        // An equity needs no mapping row: "RELIANCE" is derivable both ways, and
        // a row here would be a second, staler answer to a settled question.
        var row = Row("100001262,RELIANCE,EQ,INE002A01018,NSE,1,,,RELIANCE,RELIANCE");
        Assert.Null(row.Expiry);
        Assert.Null(TrueDataSymbolMaster.ToCanonical(row));
    }

    [Fact]
    public void An_index_row_is_skipped_too()
    {
        var row = Row("200000001,NIFTY 50,IN,200000001,NSE,,,,,");
        Assert.Null(TrueDataSymbolMaster.ToCanonical(row));
    }

    [Fact]
    public void A_broken_master_is_no_rows_rather_than_a_crash()
    {
        Assert.Empty(TrueDataSymbolMaster.ParseCsv(""));
        Assert.Empty(TrueDataSymbolMaster.ParseCsv("nonsense"));
        Assert.Empty(TrueDataSymbolMaster.ParseCsv(Header));
    }
}
