using AlgoTrading.Infrastructure.Providers.TrueData;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Symbol grammar is where a second data vendor silently goes wrong: the feed
/// connects, the bars arrive, and they are for the wrong contract.
///
/// Every vendor string below was copied out of TrueData's own master file
/// (api.truedata.in/getAllSymbols) on 2026-09-11, not from the documentation
/// and not invented here.
/// </summary>
public class TrueDataSymbolTests
{
    [Theory]
    [InlineData("NSE:NIFTY50-INDEX", "NIFTY 50")]
    [InlineData("NSE:NIFTYBANK-INDEX", "NIFTY BANK")]
    [InlineData("BSE:SENSEX-INDEX", "SENSEX")]
    public void Index_names_translate_both_ways(string canonical, string vendor)
    {
        Assert.Equal(vendor, TrueDataSymbols.ToVendor(canonical));
        Assert.Equal(canonical, TrueDataSymbols.FromVendor(vendor));
    }

    [Theory]
    [InlineData("NSE:RELIANCE-EQ", "RELIANCE")]
    [InlineData("NSE:SBIN-EQ", "SBIN")]
    public void Equities_lose_only_the_decoration(string canonical, string vendor)
    {
        Assert.Equal(vendor, TrueDataSymbols.ToVendor(canonical));
        Assert.Equal(canonical, TrueDataSymbols.FromVendor(vendor));
    }

    [Theory]
    // 15-09-2026, strike 18200 — TrueData master row 303000973.
    [InlineData("NSE:NIFTY2691518200CE", "NIFTY26091518200CE")]
    // 29-09-2026, strike 28500 — the monthly expiry, which TrueData still spells
    // by its exact day, so the canonical weekly grammar round-trips it.
    [InlineData("NSE:BANKNIFTY2692928500CE", "BANKNIFTY26092928500CE")]
    public void Dated_options_translate_both_ways(string canonical, string vendor)
    {
        Assert.Equal(vendor, TrueDataSymbols.ToVendor(canonical));
        Assert.Equal(canonical, TrueDataSymbols.FromVendor(vendor));
    }

    [Fact]
    public void October_to_December_use_the_single_character_month_coming_back()
    {
        // The canonical grammar spells October as "O". Getting this wrong would
        // land a December contract's prices on an October one.
        Assert.Equal("NSE:NIFTY26O1524000CE", TrueDataSymbols.FromVendor("NIFTY26101524000CE"));
        Assert.Equal("NSE:NIFTY26N1924000PE", TrueDataSymbols.FromVendor("NIFTY26111924000PE"));
        Assert.Equal("NSE:NIFTY26D3124000CE", TrueDataSymbols.FromVendor("NIFTY26123124000CE"));
        Assert.Equal("NIFTY26101524000CE", TrueDataSymbols.ToVendor("NSE:NIFTY26O1524000CE"));
    }

    [Fact]
    public void A_monthly_option_is_declined_rather_than_guessed()
    {
        // "26SEP" says the month and not the day; TrueData needs the day. A
        // guess here is wrong on exactly the days it matters, so the mapping
        // rows from the vendor's master answer instead.
        Assert.Null(TrueDataSymbols.ToVendor("NSE:BANKNIFTY26SEP57500CE"));
    }

    [Theory]
    [InlineData("NIFTY-I")]
    [InlineData("CRUDEOIL-I")]
    [InlineData("BANKNIFTY-II")]
    public void Continuous_futures_are_declined_rather_than_guessed(string vendor)
    {
        // "-I" is whichever contract is nearest today. Which one that is changes
        // on expiry day, so it can only come from the master.
        Assert.Null(TrueDataSymbols.FromVendor(vendor));
    }

    [Theory]
    [InlineData("MCX:CRUDEOIL26SEPFUT")]
    [InlineData("NSE:BANKNIFTY26SEPFUT")]
    public void Canonical_futures_are_declined_too(string canonical)
    {
        Assert.Null(TrueDataSymbols.ToVendor(canonical));
    }

    [Fact]
    public void Nonsense_is_declined_quietly()
    {
        Assert.Null(TrueDataSymbols.ToVendor(null));
        Assert.Null(TrueDataSymbols.ToVendor("   "));
        Assert.Null(TrueDataSymbols.FromVendor("NIFTY26139924000CE"));  // month 13, day 99
    }

    [Theory]
    [InlineData("5", "5min")]
    [InlineData("5m", "5min")]
    [InlineData("1", "1min")]
    [InlineData("15", "15min")]
    [InlineData("D", "eod")]
    [InlineData("1D", "eod")]
    public void Resolutions_become_TrueData_intervals(string resolution, string interval)
    {
        Assert.Equal(interval, TrueDataSymbols.ToInterval(resolution));
    }

    [Fact]
    public void Request_stamps_are_in_IST_because_the_vendor_answers_in_IST()
    {
        // 2026-09-11 03:45 UTC is 09:15 IST, the open. Sending the UTC clock
        // would ask for a window three and a half hours off.
        Assert.Equal("260911T09:15:00", TrueDataSymbols.ToRequestStamp(new DateTime(2026, 9, 11, 3, 45, 0, DateTimeKind.Utc)));
    }
}
