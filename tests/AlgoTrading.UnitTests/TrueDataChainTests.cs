using AlgoTrading.Infrastructure.Providers.TrueData;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The TrueData option chain, parsed from the vendor's own bytes.
///
/// Both rows below were taken verbatim from
/// greeks.truedata.in/api/getOptionChainwithGreeks for NIFTY 15-09-2026 on
/// 2026-09-11. One is the at-the-money strike, priced and traded; the other is
/// a far strike whose call side never traded, which is where a parser that
/// treats an empty cell as zero starts inventing data.
/// </summary>
public class TrueDataChainTests
{
    private const string RealCsv =
        "symbol,expiry,calltimestamp,callVol,callltp,callPClose,callbid,callbidqty,callask,callaskqty,callOI,callpOI,cdelta,ctheta,cvega,cgamma,crho,civ,strike,pdelta,ptheta,pvega,pgamma,prho,piv,putbid,putbidqty,putask,putaskqty,putOI,putPOI,putLTP,putPClose,putVol,puttimestamp\n" +
        "NIFTY,15-09-2026,11-09-2026 15:40:00,387587655,133.6,126,133.65,780,135,2145,6543875,5703360,0.6021,-11.98747,9.47394,0.00155,153.28247,0.10118,23400,-0.39759,-11.94754,9.47203,0.00156,-102.95637,0.10086,70.55,520,71.25,910,9096880,7986485,70.4,84.2,279680570,11-09-2026 15:40:00\n" +
        "NIFTY,15-09-2026,,0,0,0,,,,,0,0,,,,,,,21700,-0.00556,-1.44213,0.38996,2E-05,-1.44204,0.29572,1.25,15210,1.3,19890,6019455,1625715,1.3,0.95,40697800,11-09-2026 15:40:00\n";

    private static TrueDataOptionChain Chain() =>
        TrueDataChainCsv.Parse(RealCsv, "NIFTY", new DateOnly(2026, 9, 15));

    [Fact]
    public void Both_sides_of_a_traded_strike_are_read()
    {
        var atm = Assert.Single(Chain().Rows, r => r.Strike == 23400m);

        Assert.Equal(133.6m, atm.Call.LastPrice);
        Assert.Equal(133.65m, atm.Call.Bid);
        Assert.Equal(780, atm.Call.BidQuantity);
        Assert.Equal(135m, atm.Call.Ask);
        Assert.Equal(6543875, atm.Call.OpenInterest);
        Assert.Equal(387587655, atm.Call.Volume);

        Assert.Equal(70.4m, atm.Put.LastPrice);
        Assert.Equal(9096880, atm.Put.OpenInterest);
    }

    [Fact]
    public void Greeks_come_from_the_vendor_for_both_sides()
    {
        var atm = Assert.Single(Chain().Rows, r => r.Strike == 23400m);

        Assert.Equal(0.6021m, atm.Call.Greeks!.Delta);
        Assert.Equal(-11.98747m, atm.Call.Greeks.Theta);
        Assert.Equal(0.00155m, atm.Call.Greeks.Gamma);
        Assert.Equal(0.10118m, atm.Call.Greeks.ImpliedVolatility);

        // A put delta is negative, and reading it as positive would flip every
        // hedge computed from it.
        Assert.Equal(-0.39759m, atm.Put.Greeks!.Delta);
    }

    [Fact]
    public void An_unpriced_side_reports_no_greeks_rather_than_six_zeroes()
    {
        var far = Assert.Single(Chain().Rows, r => r.Strike == 21700m);

        // The call side of this strike never traded: every greek cell is empty.
        Assert.Null(far.Call.Greeks);
        // The put side did, so it keeps its own.
        Assert.NotNull(far.Put.Greeks);
        Assert.Equal(0.29572m, far.Put.Greeks!.ImpliedVolatility);
    }

    [Fact]
    public void Scientific_notation_survives_because_gamma_arrives_in_it()
    {
        var far = Assert.Single(Chain().Rows, r => r.Strike == 21700m);
        Assert.Equal(0.00002m, far.Put.Greeks!.Gamma);
    }

    [Fact]
    public void Open_interest_change_is_the_move_not_the_level()
    {
        var atm = Assert.Single(Chain().Rows, r => r.Strike == 23400m);

        // Calls added 840,515 and puts added 1,110,395 since the previous close.
        Assert.Equal(6543875 - 5703360, atm.Call.OpenInterestChange);
        Assert.Equal(9096880 - 7986485, atm.Put.OpenInterestChange);
    }

    [Fact]
    public void The_timestamp_is_the_exchange_clock_converted_not_relabelled()
    {
        var atm = Assert.Single(Chain().Rows, r => r.Strike == 23400m);
        // 15:40 in Mumbai is 10:10 UTC.
        Assert.Equal(new DateTime(2026, 9, 11, 10, 10, 0, DateTimeKind.Utc), atm.Call.TimestampUtc);
    }

    [Fact]
    public void The_chain_answers_the_questions_the_OI_rules_ask()
    {
        var chain = Chain();

        // Of these two strikes, 23400 carries the most call OI and 23400 the
        // most put OI as well; the far strike's 6.0m puts lose to 9.1m.
        Assert.Equal(23400m, chain.PeakCallOiStrike);
        Assert.Equal(23400m, chain.PeakPutOiStrike);

        // (9,096,880 + 6,019,455) / 6,543,875
        Assert.Equal(2.3100m, chain.PutCallOiRatio);
    }

    [Fact]
    public void A_chain_with_no_call_interest_has_no_ratio_rather_than_a_zero()
    {
        var empty = TrueDataChainCsv.Parse(
            "symbol,expiry,callOI,strike,putOI\nNIFTY,15-09-2026,0,23400,100\n",
            "NIFTY", new DateOnly(2026, 9, 15));

        Assert.Null(empty.PutCallOiRatio);
        Assert.Null(empty.PeakCallOiStrike);
    }

    [Fact]
    public void An_empty_or_broken_answer_is_an_empty_chain_not_a_crash()
    {
        Assert.Empty(TrueDataChainCsv.Parse("", "NIFTY", new DateOnly(2026, 9, 15)).Rows);
        Assert.Empty(TrueDataChainCsv.Parse("nonsense", "NIFTY", new DateOnly(2026, 9, 15)).Rows);
    }
}
