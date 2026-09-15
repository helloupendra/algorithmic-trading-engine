using System.Text.Json;
using AlgoTrading.Infrastructure.Providers.Dhan;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The expired options importer's rules, pinned against answers read from
/// Dhan's <c>/charts/rollingoption</c> on 2026-09-15.
/// </summary>
public class DhanOptionHistoryTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static DateTime Ist(int y, int mo, int d, int h, int mi = 0) =>
        new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Utc).AddMinutes(-330);

    private static DhanRollingSeries Nifty(string type = "CE", int offset = 0, string interval = "5") =>
        new("NIFTY", "WEEK", 1, offset, type, interval);

    // The first three and the last five-minute bars of NIFTY WEEK 1 ATM CALL,
    // 2025-09-01 to 2025-09-05, exactly as Dhan sent them: pe is null for a CALL.
    // The second bar's strike is 24550 while its open (102.45) is the 24500
    // strike's price: coarser bars span a change of ATM.
    private const string CallAnswer = """
        {"data":{"ce":{
          "iv":[12.96,11.94,11.82,7.37],
          "oi":[15695250,12538125,13425675,5051925],
          "strike":[24500.0,24550.0,24550.0,24750.0],
          "spot":[24523.5,24529.15,24530.25,24743.95],
          "open":[72.0,102.45,72.3,96.9],
          "high":[106.0,104.8,102.9,96.9],
          "low":[64.9,65.8,66.15,89.95],
          "close":[101.9,72.7,71.85,91.5],
          "volume":[26204925,17800800,11453325,4251825],
          "timestamp":[1756698300,1756698600,1756698900,1757066100]},
        "pe":null}}
        """;

    // The same week for PUT: ce is null and the bars sit under pe.
    private const string PutAnswer = """
        {"data":{"ce":null,"pe":{
          "iv":[9.64,9.28,8.81],
          "oi":[10748850,5176200,6241950],
          "strike":[24500.0,24550.0,24550.0],
          "spot":[24523.5,24529.15,24530.25],
          "open":[95.45,46.45,65.25],
          "high":[95.45,78.0,70.55],
          "low":[46.0,45.0,42.55],
          "close":[47.35,65.5,62.3],
          "volume":[23059275,14542350,11855250],
          "timestamp":[1756698300,1756698600,1756698900]}}}
        """;

    // 2025-08-15, a holiday: 200 with every array empty.
    private const string HolidayAnswer = """
        {"data": {"ce": {"iv": [], "oi": [], "strike": [], "spot": [], "open": [], "high": [], "low": [], "close": [], "volume": [], "timestamp": []}, "pe": null}}
        """;

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    // ------------------------------------------------------------------ request

    [Theory]
    [InlineData(0, "ATM")]
    [InlineData(1, "ATM+1")]
    [InlineData(3, "ATM+3")]
    [InlineData(-2, "ATM-2")]
    [InlineData(10, "ATM+10")]
    [InlineData(-10, "ATM-10")]
    public void Writes_the_strike_relative_to_ATM(int offset, string expected)
    {
        Assert.Equal(expected, DhanRollingOptions.StrikeArgument(offset));
    }

    [Theory]
    [InlineData(11)]
    [InlineData(-11)]
    public void Refuses_offsets_Dhan_answers_with_silence(int offset)
    {
        // ATM+11 came back 200 with empty arrays: indistinguishable from a holiday.
        Assert.Throws<ArgumentOutOfRangeException>(() => DhanRollingOptions.StrikeArgument(offset));
    }

    [Theory]
    [InlineData("1", "1m")]
    [InlineData("5", "5m")]
    [InlineData("15", "15m")]
    [InlineData("60", "60m")]
    public void Names_the_resolution_after_the_interval(string interval, string resolution)
    {
        Assert.Equal(resolution, DhanRollingOptions.ResolutionFor(interval));
    }

    [Theory]
    [InlineData("25")] // documented, but refused with DH-905
    [InlineData("3")]
    [InlineData("D")]
    public void Refuses_intervals_the_endpoint_does_not_serve(string interval)
    {
        Assert.Throws<NotSupportedException>(() => DhanRollingOptions.NormalizeInterval(interval));
    }

    [Fact]
    public void Accepts_an_interval_written_as_a_resolution()
    {
        Assert.Equal("5", DhanRollingOptions.NormalizeInterval("5m"));
    }

    [Fact]
    public void Cuts_a_range_into_windows_of_at_most_thirty_days()
    {
        var windows = DhanRollingOptions.Windows(D(2025, 8, 1), D(2025, 9, 30));

        Assert.Equal(new[] { (D(2025, 8, 1), D(2025, 8, 30)), (D(2025, 8, 31), D(2025, 9, 29)), (D(2025, 9, 30), D(2025, 9, 30)) }, windows);
        Assert.All(windows, w => Assert.True(w.To.DayNumber - w.From.DayNumber + 1 <= DhanRollingOptions.WindowDays));
        for (int i = 1; i < windows.Count; i++) Assert.Equal(windows[i - 1].To.AddDays(1), windows[i].From);
    }

    [Fact]
    public void A_single_day_is_one_window()
    {
        Assert.Equal(new[] { (D(2025, 9, 1), D(2025, 9, 1)) }, DhanRollingOptions.Windows(D(2025, 9, 1), D(2025, 9, 1)));
    }

    [Theory]
    [InlineData("NIFTY", "NSE_FNO", 13)]
    [InlineData("banknifty", "NSE_FNO", 25)]
    [InlineData("FINNIFTY", "NSE_FNO", 27)]
    [InlineData("MIDCPNIFTY", "NSE_FNO", 442)]
    [InlineData("SENSEX", "BSE_FNO", 51)]
    [InlineData("BANKEX", "BSE_FNO", 69)]
    public void Asks_for_an_index_underlying_under_its_exchange_FnO_segment(string underlying, string segment, long id)
    {
        // SENSEX under NSE_FNO or IDX_I answered 200 with no bars.
        var instrument = DhanRollingOptions.OptionUnderlying(underlying);
        Assert.Equal(segment, instrument.Segment);
        Assert.Equal(id, instrument.SecurityId);
        Assert.Equal("OPTIDX", instrument.InstrumentType);
    }

    [Fact]
    public void Refuses_underlyings_it_has_no_id_for()
    {
        Assert.Throws<NotSupportedException>(() => DhanRollingOptions.OptionUnderlying("RELIANCE"));
    }

    [Fact]
    public void Builds_the_request_the_live_API_accepted()
    {
        var body = JsonSerializer.SerializeToElement(
            DhanRollingOptions.Request(Nifty("PE", -3), D(2025, 9, 1), D(2025, 9, 30)));

        Assert.Equal("NSE_FNO", body.GetProperty("exchangeSegment").GetString());
        Assert.Equal("5", body.GetProperty("interval").GetString());
        Assert.Equal(13, body.GetProperty("securityId").GetInt64());
        Assert.Equal("OPTIDX", body.GetProperty("instrument").GetString());
        Assert.Equal("WEEK", body.GetProperty("expiryFlag").GetString());
        Assert.Equal(1, body.GetProperty("expiryCode").GetInt32());
        Assert.Equal("ATM-3", body.GetProperty("strike").GetString());
        Assert.Equal("PUT", body.GetProperty("drvOptionType").GetString());
        Assert.Equal(
            new[] { "open", "high", "low", "close", "iv", "volume", "strike", "oi", "spot" },
            body.GetProperty("requiredData").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("2025-09-01", body.GetProperty("fromDate").GetString());
        // A day past the window: toDate answered inclusive but is documented exclusive.
        Assert.Equal("2025-10-01", body.GetProperty("toDate").GetString());
    }

    [Fact]
    public void Asks_for_CALL_for_a_CE_series()
    {
        var body = JsonSerializer.SerializeToElement(DhanRollingOptions.Request(Nifty("CE", 2), D(2025, 9, 1), D(2025, 9, 1)));
        Assert.Equal("CALL", body.GetProperty("drvOptionType").GetString());
        Assert.Equal("ATM+2", body.GetProperty("strike").GetString());
        Assert.Equal("2025-09-02", body.GetProperty("toDate").GetString());
    }

    // ------------------------------------------------------------------ response

    [Fact]
    public void Maps_a_call_answer_to_rows()
    {
        var rows = DhanRollingOptions.Parse(Json(CallAnswer), Nifty("CE"), D(2025, 9, 1), D(2025, 9, 5));

        Assert.Equal(4, rows.Count);
        var first = rows[0];
        // Epoch 1756698300 is 2025-09-01 09:15 IST: the bar's start.
        Assert.Equal(Ist(2025, 9, 1, 9, 15), first.BarStartUtc);
        Assert.Equal(DateTimeKind.Utc, first.BarStartUtc.Kind);
        Assert.Equal("NIFTY", first.Underlying);
        Assert.Equal("WEEK", first.ExpiryFlag);
        Assert.Equal(1, first.ExpiryCode);
        Assert.Null(first.ExpiryDate);
        Assert.Equal(0, first.StrikeOffset);
        Assert.Equal("CE", first.OptionType);
        Assert.Equal("5m", first.Resolution);
        Assert.Equal(24500m, first.Strike);
        Assert.Equal(72.0m, first.Open);
        Assert.Equal(106.0m, first.High);
        Assert.Equal(64.9m, first.Low);
        Assert.Equal(101.9m, first.Close);
        Assert.Equal(26204925L, first.Volume);
        Assert.Equal(15695250L, first.OpenInterest);
        Assert.Equal(12.96m, first.ImpliedVolatility);
        Assert.Equal(24523.5m, first.SpotPrice);
        Assert.Equal("dhan", first.SourceKey);

        // The strike moves with the money, bar to bar.
        Assert.Equal(24550m, rows[1].Strike);
        Assert.Equal(Ist(2025, 9, 5, 15, 25), rows[3].BarStartUtc);
        Assert.Equal(24750m, rows[3].Strike);
    }

    [Fact]
    public void Reads_the_put_side_of_a_put_answer()
    {
        var rows = DhanRollingOptions.Parse(Json(PutAnswer), Nifty("PE", 0), D(2025, 9, 1), D(2025, 9, 5));

        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal("PE", r.OptionType));
        Assert.Equal(47.35m, rows[0].Close);
        Assert.Equal(10748850L, rows[0].OpenInterest);
        Assert.Equal(24523.5m, rows[0].SpotPrice);
    }

    [Fact]
    public void Finds_no_call_bars_in_a_put_answer()
    {
        Assert.Empty(DhanRollingOptions.Parse(Json(PutAnswer), Nifty("CE"), D(2025, 9, 1), D(2025, 9, 5)));
    }

    [Fact]
    public void A_holiday_answer_is_no_rows()
    {
        Assert.Empty(DhanRollingOptions.Parse(Json(HolidayAnswer), Nifty("CE"), D(2025, 8, 15), D(2025, 8, 17)));
    }

    [Theory]
    [InlineData("""{"data":{"814":"Invalid Request"},"status":"failed"}""")]
    [InlineData("""{"data":null}""")]
    [InlineData("""[]""")]
    public void An_answer_of_another_shape_is_no_rows(string text)
    {
        Assert.Empty(DhanRollingOptions.Parse(Json(text), Nifty("CE"), D(2025, 9, 1), D(2025, 9, 5)));
    }

    [Fact]
    public void Keeps_only_bars_inside_the_window()
    {
        // The request asks a day past the window; that day's bars are dropped.
        var rows = DhanRollingOptions.Parse(Json(CallAnswer), Nifty("CE"), D(2025, 9, 1), D(2025, 9, 4));
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.True(r.BarStartUtc < Ist(2025, 9, 2, 0, 0)));
    }

    [Fact]
    public void Stores_unknowns_as_null_and_drops_bars_without_prices()
    {
        const string text = """
            {"data":{"ce":{
              "timestamp":[1756698300,1756698600,1756698600,1756698900],
              "open":[72.0,1.0,102.45,72.3],
              "high":[106.0,1.0,104.8,102.9],
              "low":[64.9,1.0,65.8,66.15],
              "close":[101.9,1.0,72.7,null],
              "strike":[24500.0,24550.0,24550.0,24550.0],
              "oi":[0,5,12538125,1],
              "iv":[0,0.0,11.94,1],
              "spot":[0,24529.15,24529.15,1],
              "volume":[0,1,17800800,1]},
            "pe":null}}
            """;
        var rows = DhanRollingOptions.Parse(Json(text), Nifty("CE"), D(2025, 9, 1), D(2025, 9, 1));

        Assert.Equal(2, rows.Count);
        Assert.Null(rows[0].OpenInterest);
        Assert.Null(rows[0].ImpliedVolatility);
        Assert.Null(rows[0].SpotPrice);
        // No trades is a fact, not an unknown.
        Assert.Equal(0L, rows[0].Volume);
        // A repeated stamp keeps its last copy.
        Assert.Equal(102.45m, rows[1].Open);
        Assert.Equal(12538125L, rows[1].OpenInterest);
    }

    // ------------------------------------------------------------------ resume

    [Fact]
    public void Missing_days_are_trading_days_with_no_bars()
    {
        var trading = new[] { D(2025, 8, 14), D(2025, 8, 18), D(2025, 8, 19), D(2025, 9, 1) };
        var present = new HashSet<DateOnly> { D(2025, 8, 14), D(2025, 8, 19) };

        Assert.Equal(new[] { D(2025, 8, 18) }, DhanRollingOptions.MissingDays(trading, present, D(2025, 8, 1), D(2025, 8, 30)));
    }

    [Fact]
    public void Plans_only_windows_with_a_missing_trading_day()
    {
        var ce = Nifty("CE");
        var pe = Nifty("PE");
        var windows = new[] { (D(2025, 8, 1), D(2025, 8, 2)), (D(2025, 8, 15), D(2025, 8, 17)), (D(2025, 9, 1), D(2025, 9, 2)) };
        // 2 Aug is a Saturday and 15-17 Aug a holiday and a weekend.
        var trading = new[] { D(2025, 8, 1), D(2025, 9, 1), D(2025, 9, 2) };
        var present = new Dictionary<DhanRollingSeries, IReadOnlySet<DateOnly>>
        {
            [ce] = new HashSet<DateOnly> { D(2025, 8, 1), D(2025, 9, 1), D(2025, 9, 2) },
            [pe] = new HashSet<DateOnly> { D(2025, 8, 1), D(2025, 9, 1) },
        };

        var plan = DhanOptionHistoryPlan.Build(new[] { ce, pe }, windows, trading, s => present[s]);

        Assert.Equal(6, plan.Total);
        Assert.Equal(3, plan.AlreadyPresent);
        Assert.Equal(2, plan.NoTradingDays);
        var item = Assert.Single(plan.Work);
        Assert.Equal(pe, item.Series);
        Assert.Equal((D(2025, 9, 1), D(2025, 9, 2)), (item.From, item.To));
    }

    [Fact]
    public void The_fallback_calendar_is_every_weekday()
    {
        Assert.Equal(new[] { D(2025, 8, 15), D(2025, 8, 18) }, DhanOptionHistoryPlan.Weekdays(D(2025, 8, 15), D(2025, 8, 18)));
    }

    [Fact]
    public void Runs_join_across_weekends_and_break_at_a_missing_weekday()
    {
        var days = new[] { D(2025, 8, 13), D(2025, 8, 14), D(2025, 8, 18), D(2025, 8, 19), D(2025, 8, 22), D(2025, 8, 25) };

        // 15 Aug (a Friday holiday) breaks the run; 23-24 Aug (a weekend) does not.
        Assert.Equal(
            new[] { (D(2025, 8, 13), D(2025, 8, 14)), (D(2025, 8, 18), D(2025, 8, 19)), (D(2025, 8, 22), D(2025, 8, 25)) },
            DhanRollingOptions.Runs(days));
    }

    // ------------------------------------------------------------------ the import request

    [Theory]
    [InlineData("\"-3..3\"", new[] { -3, -2, -1, 0, 1, 2, 3 })]
    [InlineData("[1,-1,0,1]", new[] { -1, 0, 1 })]
    [InlineData("2", new[] { 2 })]
    [InlineData("\"-10..-8\"", new[] { -10, -9, -8 })]
    [InlineData("null", new[] { 0 })]
    public void Reads_strike_offsets_as_a_list_or_a_range(string json, int[] expected)
    {
        Assert.Equal(expected, DhanRollingOptions.ParseOffsets(JsonDocument.Parse(json).RootElement));
    }

    [Theory]
    [InlineData("\"-11..0\"")]
    [InlineData("[0,12]")]
    [InlineData("\"3..-3\"")]
    [InlineData("\"ATM\"")]
    [InlineData("[]")]
    [InlineData("[1.5]")]
    public void Refuses_strike_offsets_it_cannot_serve(string json)
    {
        Assert.Throws<ArgumentException>(() => DhanRollingOptions.ParseOffsets(JsonDocument.Parse(json).RootElement));
    }

    [Fact]
    public void An_import_request_fills_its_defaults()
    {
        var request = DhanOptionHistoryRequest.Create(
            " nifty ", "2025-08-01", "2025-09-30", JsonDocument.Parse("\"-2..2\"").RootElement,
            optionTypes: null, expiryFlag: null, expiryCode: null, interval: null, todayIst: D(2026, 9, 15));

        Assert.Equal("NIFTY", request.Underlying);
        Assert.Equal("WEEK", request.ExpiryFlag);
        Assert.Equal(1, request.ExpiryCode);
        Assert.Equal("5", request.Interval);
        Assert.Equal(new[] { "CE", "PE" }, request.OptionTypes);
        Assert.Equal(10, request.Series.Count);
    }

    [Fact]
    public void An_import_request_reads_CALL_and_PUT()
    {
        var request = DhanOptionHistoryRequest.Create(
            "SENSEX", "2025-08-01", "2025-08-31", null, new[] { "put", "CALL", "PE" }, "month", 2, "1m", D(2026, 9, 15));

        Assert.Equal(new[] { "CE", "PE" }, request.OptionTypes);
        Assert.Equal("MONTH", request.ExpiryFlag);
        Assert.Equal("1", request.Interval);
    }

    [Theory]
    [InlineData("NIFTY", "2025-09-30", "2025-08-01", "WEEK", 1)]  // from after to
    [InlineData("NIFTY", "2026-09-01", "2026-09-15", "WEEK", 1)]  // today's session is unfinished
    [InlineData("NIFTY", "2025-08-01", "2025-09-30", "DAY", 1)]   // no such flag
    [InlineData("NIFTY", "2025-08-01", "2025-09-30", "WEEK", 0)]  // refused by Dhan as "required"
    [InlineData("NIFTY", "01-08-2025", "2025-09-30", "WEEK", 1)]  // date format
    [InlineData("NIFTY", "2019-01-01", "2025-09-30", "WEEK", 1)]  // over six years
    public void Refuses_an_import_request_that_cannot_be_served(string underlying, string from, string to, string flag, int code)
    {
        Assert.Throws<ArgumentException>(() => DhanOptionHistoryRequest.Create(
            underlying, from, to, null, null, flag, code, "5", D(2026, 9, 15)));
    }

    // ------------------------------------------------------------------ retries

    [Theory]
    [InlineData(429, null, true)]
    [InlineData(500, null, true)]
    [InlineData(400, "DH-904", true)]
    [InlineData(400, "805", true)]
    [InlineData(200, "800", true)]
    [InlineData(400, "DH-905", false)]
    [InlineData(400, "814", false)]
    [InlineData(401, "807", false)]
    [InlineData(400, "DH-901", false)]
    [InlineData(400, "806", false)]
    public void Retries_throttling_and_server_errors_but_not_refusals(int status, string? code, bool transient)
    {
        Assert.Equal(transient, DhanOptionHistoryImporter.IsTransient(new DhanApiException(status, code, "x"), CancellationToken.None));
    }

    [Fact]
    public void Retries_the_network_but_not_its_own_cancellation()
    {
        Assert.True(DhanOptionHistoryImporter.IsTransient(new HttpRequestException("reset"), CancellationToken.None));
        Assert.True(DhanOptionHistoryImporter.IsTransient(new TaskCanceledException("timeout"), CancellationToken.None));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.False(DhanOptionHistoryImporter.IsTransient(new TaskCanceledException(), cancelled.Token));
    }
}
