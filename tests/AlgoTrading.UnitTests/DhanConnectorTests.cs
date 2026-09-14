using System.Text.Json;
using AlgoTrading.Infrastructure.Providers.Dhan;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The Dhan connector's rules, pinned against answers and files read from the
/// live API on 2026-09-14 (a holiday, with an active data plan).
/// </summary>
public class DhanConnectorTests
{
    private static DateTime Ist(int y, int mo, int d, int h, int mi = 0) =>
        new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Utc).AddMinutes(-330);

    private static long Epoch(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeSeconds();

    // ------------------------------------------------------------------ history

    [Theory]
    [InlineData("1", "1")]
    [InlineData("5m", "5")]
    [InlineData("15", "15")]
    [InlineData("1h", "60")]
    [InlineData("D", null)]
    [InlineData("1D", null)]
    public void Maps_platform_resolutions_to_Dhan_intervals(string resolution, string? expected)
    {
        Assert.Equal(expected, DhanHistory.IntervalFor(resolution));
    }

    [Fact]
    public void Refuses_a_resolution_Dhan_does_not_serve()
    {
        Assert.Throws<NotSupportedException>(() => DhanHistory.IntervalFor("3"));
    }

    [Fact]
    public void Asks_one_minute_early_because_Dhan_excludes_a_bar_on_fromDate()
    {
        var nifty = DhanInstruments.Indices["NSE:NIFTY50-INDEX"];
        var body = JsonSerializer.SerializeToElement(
            DhanHistory.IntradayRequest(nifty, "5", Ist(2026, 9, 11, 9, 15), Ist(2026, 9, 11, 15, 30)));

        Assert.Equal("13", body.GetProperty("securityId").GetString());
        Assert.Equal("IDX_I", body.GetProperty("exchangeSegment").GetString());
        Assert.Equal("2026-09-11 09:14:00", body.GetProperty("fromDate").GetString());
        Assert.Equal("2026-09-11 15:30:00", body.GetProperty("toDate").GetString());
        Assert.False(body.GetProperty("oi").GetBoolean());
    }

    [Fact]
    public void Asks_for_open_interest_on_derivatives()
    {
        var option = new DhanInstrument("NSE_FNO", 47317, "OPTIDX");
        var body = JsonSerializer.SerializeToElement(
            DhanHistory.IntradayRequest(option, "5", Ist(2026, 9, 11, 9, 15), Ist(2026, 9, 11, 15, 30)));
        Assert.True(body.GetProperty("oi").GetBoolean());
    }

    [Fact]
    public void Cuts_a_long_intraday_range_into_90_day_requests()
    {
        var windows = DhanHistory.Windows(new DateTime(2026, 1, 1), new DateTime(2026, 7, 1), intraday: true).ToList();
        Assert.Equal(3, windows.Count);
        Assert.Equal(new DateTime(2026, 1, 1), windows[0].FromUtc);
        Assert.Equal(new DateTime(2026, 7, 1), windows[^1].ToUtc);
    }

    [Fact]
    public void Reads_bars_trims_to_the_range_and_reports_missing_OI_as_unknown()
    {
        // The 09:10 bar is the one the one-minute-early request can bring back.
        var json = JsonDocument.Parse($$"""
            {
              "open": [3.0, 3.3, 4.0], "high": [3.1, 4.2, 4.1], "low": [2.9, 3.2, 3.8],
              "close": [3.0, 4.1, 3.9], "volume": [10, 4996615, 1476345],
              "timestamp": [{{Epoch(Ist(2026, 9, 11, 9, 10))}}, {{Epoch(Ist(2026, 9, 11, 9, 15))}}, {{Epoch(Ist(2026, 9, 11, 9, 20))}}],
              "open_interest": [0, 3265925, 0]
            }
            """);

        var bars = DhanHistory.Parse(json.RootElement, Ist(2026, 9, 11, 9, 15), Ist(2026, 9, 11, 15, 30), intraday: true);

        Assert.Equal(2, bars.Count);
        Assert.Equal(Ist(2026, 9, 11, 9, 15), bars[0].TimestampUtc);
        Assert.Equal(4.1m, bars[0].Close);
        Assert.Equal(4996615m, bars[0].Volume);
        Assert.Equal(3265925L, bars[0].OpenInterest);
        Assert.Null(bars[1].OpenInterest);
    }

    [Fact]
    public void Keeps_daily_bars_by_their_IST_date()
    {
        // Daily bars are stamped 00:00 IST, which is 18:30 UTC the day before.
        var json = JsonDocument.Parse($$"""
            {"open":[24077.55],"high":[24100],"low":[23900],"close":[24055.8],"volume":[334207690],
             "timestamp":[{{Epoch(Ist(2026, 9, 1, 0))}}]}
            """);

        var bars = DhanHistory.Parse(json.RootElement, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc), intraday: false);

        Assert.Single(bars);
        Assert.Equal(24055.8m, bars[0].Close);
    }

    // --------------------------------------------------------------- the chain

    private const string ChainSample = """
        {"data":{"last_price":23398.1,"oc":{
          "23950.000000":{
            "ce":{"average_price":4.24,"greeks":{"delta":0.03142,"theta":-6.88954,"gamma":0.00025,"vega":1.06634},"implied_volatility":19.03,"last_price":3.5,"oi":3060785,"previous_close_price":7.1,"previous_oi":3344705,"previous_volume":22861930,"security_id":47317,"top_ask_price":3.45,"top_ask_quantity":1820,"top_bid_price":3.1,"top_bid_quantity":195,"volume":69015245},
            "pe":{"average_price":584.21,"greeks":{"delta":0,"theta":0,"gamma":0,"vega":0},"implied_volatility":0,"last_price":490.6,"oi":116545,"previous_close_price":511.3,"previous_oi":166075,"previous_volume":135265,"security_id":47318,"top_ask_price":489.45,"top_ask_quantity":325,"top_bid_price":486.2,"top_bid_quantity":65,"volume":180895}},
          "23400.000000":{
            "ce":{"last_price":120,"oi":1000000,"previous_oi":900000,"volume":5,"security_id":1,"implied_volatility":12.5,"greeks":{"delta":0.5,"theta":-10,"gamma":0.001,"vega":12}},
            "pe":{"last_price":118,"oi":4000000,"previous_oi":3000000,"volume":6,"security_id":2,"implied_volatility":13.1,"greeks":{"delta":-0.5,"theta":-9,"gamma":0.001,"vega":12}}}
        }},"status":"success"}
        """;

    [Fact]
    public void Reads_the_option_chain_as_Dhan_sends_it()
    {
        var chain = DhanOptionChain.Parse(JsonDocument.Parse(ChainSample).RootElement, "NIFTY", new DateOnly(2026, 9, 15));

        Assert.Equal(23398.1m, chain.UnderlyingPrice);
        Assert.Equal(new[] { 23400m, 23950m }, chain.Rows.Select(r => r.Strike));

        var call = chain.Rows[1].Call!;
        Assert.Equal(47317, call.SecurityId);
        Assert.Equal(3060785, call.OpenInterest);
        Assert.Equal(-283920, call.OpenInterestChange);
        Assert.Equal(69015245, call.Volume);
        Assert.Equal(0.03142m, call.Greeks!.Delta);
        Assert.Equal(3.1m, call.Bid);
    }

    [Fact]
    public void Reports_greeks_and_IV_Dhan_could_not_price_as_unknown()
    {
        var put = DhanOptionChain.Parse(JsonDocument.Parse(ChainSample).RootElement, "NIFTY", new DateOnly(2026, 9, 15)).Rows[1].Put!;
        Assert.Null(put.Greeks);
        Assert.Null(put.ImpliedVolatility);
        Assert.Equal(490.6m, put.LastPrice);
    }

    [Fact]
    public void Computes_the_chain_figures_the_OI_rules_read()
    {
        var chain = DhanOptionChain.Parse(JsonDocument.Parse(ChainSample).RootElement, "NIFTY", new DateOnly(2026, 9, 15));
        Assert.Equal(23950m, chain.PeakCallOiStrike);
        Assert.Equal(23400m, chain.PeakPutOiStrike);
        Assert.Equal(Math.Round(4116545m / 4060785m, 4), chain.PutCallOiRatio);
    }

    // ------------------------------------------------------------------ errors

    [Fact]
    public void Reads_both_of_Dhans_error_shapes()
    {
        var trading = DhanApiClient.Describe(401, """{"errorType":"Invalid_Authentication","errorCode":"DH-901","errorMessage":"Client ID or user generated access token is invalid or expired."}""");
        Assert.True(trading.IsAuthFailure);
        Assert.Contains("24 hours", trading.Message);

        var data = DhanApiClient.Describe(400, """{"data":{"814":"Invalid Request"},"status":"failed"}""");
        Assert.Equal("814", data.Code);
        Assert.False(data.IsAuthFailure);

        var plan = DhanApiClient.Describe(200, """{"data":{"806":"Data APIs not subscribed"},"status":"failed"}""");
        Assert.True(plan.IsNotSubscribed);
        Assert.Contains("data plan", plan.Message);

        var expired = DhanApiClient.Describe(200, """{"data":{"807":"Access token expired"},"status":"failed"}""");
        Assert.True(expired.IsAuthFailure);
    }

    // -------------------------------------------------------------- instruments

    [Fact]
    public void A_vendor_symbol_round_trips_and_rejects_anything_else()
    {
        var option = new DhanInstrument("NSE_FNO", 47317, "OPTIDX");
        Assert.Equal("NSE_FNO:47317:OPTIDX", option.ToVendorSymbol());
        Assert.Equal(option, DhanInstrument.Parse("NSE_FNO:47317:OPTIDX"));
        Assert.Null(DhanInstrument.Parse("NSE:NIFTY2691523950CE"));
        Assert.Null(DhanInstrument.Parse("NSE_FNO:abc:OPTIDX"));
    }

    // Header and rows copied from api-scrip-master-detailed.csv on 2026-09-14, trimmed after SM_FREEZE_QTY.
    private static readonly string[] MasterSample =
    {
        "EXCH_ID,SEGMENT,SECURITY_ID,ISIN,INSTRUMENT,UNDERLYING_SECURITY_ID,UNDERLYING_SYMBOL,SYMBOL_NAME,DISPLAY_NAME,INSTRUMENT_TYPE,SERIES,LOT_SIZE,SM_EXPIRY_DATE,STRIKE_PRICE,OPTION_TYPE,TICK_SIZE,EXPIRY_FLAG,SM_FREEZE_QTY,",
        "NSE,I,13,NA,INDEX,13,NIFTY,NIFTY,Nifty 50,INDEX,NA,1.0,0001-01-01,,XX,0.0500,N,0,",
        "NSE,D,47317,NA,OPTIDX,26000,NIFTY,NIFTY-Sep2026-23950-CE,NIFTY 15 SEP 23950 CALL,OP,NA,65.0,2026-09-15,23950.00000,CE,5.0000,W,1756,",
        "NSE,D,69787,NA,OPTIDX,26009,BANKNIFTY,BANKNIFTY-Sep2026-56000-CE,BANKNIFTY 29 SEP 56000 CALL,OP,NA,30.0,2026-09-29,56000.00000,CE,5.0000,M,600,",
        "NSE,D,68407,NA,FUTIDX,26000,NIFTY,NIFTY-Sep2026-FUT,NIFTY SEP FUT,FUT,NA,65.0,2026-09-29,-0.01000,XX,10.0000,M,1756,",
        "BSE,D,869699,NA,OPTIDX,1,SENSEX,BSXOPT,SENSEX 17 SEP 74300 CALL,OPTIDX,NA,20.0,2026-09-17,74300.00000,CE,5.0000,W,1000,",
        "MCX,M,565899,NA,FUTCOM,294,CRUDEOIL,CRUDEOIL,CRUDEOIL SEP FUT,FUTCOM,2,1.0,2026-09-21,0.00000,XX,100.0000,M,100,",
        "MCX,M,576398,NA,OPTFUT,294,CRUDEOIL,CRUDEOIL,CRUDEOIL 17 SEP 9500 CALL,OPTFUT,2,1.0,2026-09-17,9500.00000,CE,10.0000,M,100,",
        "NSE,E,2885,INE002A01018,EQUITY,,RELIANCE,RELIANCE INDUSTRIES LTD,Reliance Industries,ES,EQ,1.0,,,,10.0000,NA,0,",
        "BSE,C,2147318,NA,OPTCUR,600,USDINR,USDINR,USDINR 27 DEC 84.25 CALL,OPTCUR,NA,1.0,2024-12-27,84.25000,CE,0.2500,Q,701,",
        "NSE,D,11111,NA,OPTIDX,26000,NIFTY,NIFTY-Sep2026-23000-CE,NIFTY 08 SEP 23000 CALL,OP,NA,65.0,2026-09-08,23000.00000,CE,5.0000,W,1756,",
    };

    private static readonly PlatformInstrument[] Platform =
    {
        new(1, "NSE:NIFTY2691523950CE", "NSE", "FO", "CE", "NIFTY", new DateOnly(2026, 9, 15), 23950.00m, "CE"),
        new(2, "NSE:BANKNIFTY26SEP56000CE", "NSE", "FO", "CE", "BANKNIFTY", new DateOnly(2026, 9, 29), 56000.00m, "CE"),
        new(3, "NSE:NIFTY26SEPFUT", "NSE", "FO", "FUT", "NIFTY", new DateOnly(2026, 9, 29), null, ""),
        new(4, "BSE:SENSEX2691774300CE", "BSE", "FO", "CE", "SENSEX", new DateOnly(2026, 9, 17), 74300.00m, "CE"),
        new(5, "MCX:CRUDEOIL26SEPFUT", "MCX", "COM", "FUT", "CRUDEOIL", new DateOnly(2026, 9, 21), null, ""),
        new(6, "MCX:CRUDEOIL26SEP9500CE", "MCX", "COM", "CE", "CRUDEOIL", new DateOnly(2026, 9, 17), 9500.00m, "CE"),
        new(7, "NSE:RELIANCE-EQ", "NSE", "CM", "EQ", "", null, null, ""),
        new(8, "NSE:NIFTY2691524000CE", "NSE", "FO", "CE", "NIFTY", new DateOnly(2026, 9, 15), 24000.00m, "CE"),
    };

    [Fact]
    public void Reads_the_master_and_skips_expired_and_untraded_segments()
    {
        var rows = DhanInstrumentMaster.Parse(MasterSample, today: new DateOnly(2026, 9, 14)).ToList();

        Assert.DoesNotContain(rows, r => r.SecurityId == 2147318); // currency: not a segment we trade
        Assert.DoesNotContain(rows, r => r.SecurityId == 11111);   // expired 08 Sep
        Assert.Null(rows.Single(r => r.SecurityId == 13).Expiry);  // 0001-01-01 means none
        Assert.Equal(-0.01m, rows.Single(r => r.SecurityId == 68407).Strike);
    }

    [Fact]
    public void Matches_options_futures_MCX_and_equities_by_contract_not_by_name()
    {
        var rows = DhanInstrumentMaster.Parse(MasterSample, today: new DateOnly(2026, 9, 14));
        var result = DhanInstrumentMaster.Match(rows, Platform);

        Assert.Equal("NSE_FNO:47317:OPTIDX", result.ByCanonical["NSE:NIFTY2691523950CE"].Instrument.ToVendorSymbol());
        Assert.Equal("NSE_FNO:69787:OPTIDX", result.ByCanonical["NSE:BANKNIFTY26SEP56000CE"].Instrument.ToVendorSymbol());
        Assert.Equal("NSE_FNO:68407:FUTIDX", result.ByCanonical["NSE:NIFTY26SEPFUT"].Instrument.ToVendorSymbol());
        Assert.Equal("BSE_FNO:869699:OPTIDX", result.ByCanonical["BSE:SENSEX2691774300CE"].Instrument.ToVendorSymbol());
        Assert.Equal("MCX_COMM:565899:FUTCOM", result.ByCanonical["MCX:CRUDEOIL26SEPFUT"].Instrument.ToVendorSymbol());
        Assert.Equal("MCX_COMM:576398:OPTFUT", result.ByCanonical["MCX:CRUDEOIL26SEP9500CE"].Instrument.ToVendorSymbol());
        Assert.Equal("NSE_EQ:2885:EQUITY", result.ByCanonical["NSE:RELIANCE-EQ"].Instrument.ToVendorSymbol());
        Assert.Equal(1, result.ByCanonical["NSE:NIFTY2691523950CE"].InstrumentId);

        // A contract the master does not list is reported, not guessed at.
        Assert.Equal(new[] { "NSE:NIFTY2691524000CE" }, result.Unmatched);
    }

    [Fact]
    public void Refuses_a_master_whose_header_changed()
    {
        var broken = new[] { "EXCH_ID,SEGMENT,SECURITY_ID", "NSE,D,1" };
        Assert.Throws<InvalidOperationException>(() => DhanInstrumentMaster.Parse(broken, new DateOnly(2026, 9, 14)).ToList());
    }
}
