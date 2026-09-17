using Microsoft.EntityFrameworkCore;
using AlgoTrading.Api.Services;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Providers.Angel;
using AlgoTrading.Infrastructure.Services.MarketFactors;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The Market factors page's readers and shapers: NSE's participant-wise OI file,
/// the FII/DII answer, the F&amp;O bhavcopy's futures rows, NSE IX's GIFT Nifty and
/// Yahoo's chart answer, each against a real answer of 16–17 Sep 2026.
/// </summary>
public class MarketFactorsTests
{
    internal const string ParticipantCsv = """
    ""Participant wise Open Interest (no. of contracts) in Equity Derivatives as on Sep 16, 2026"",,,,,,,,,,,,,,
    Client Type,Future Index Long,Future Index Short,Future Stock Long,Future Stock Short       ,Option Index Call Long,Option Index Put Long,Option Index Call Short,Option Index Put Short,Option Stock Call Long,Option Stock Put Long,Option Stock Call Short,Option Stock Put Short,Total Long Contracts      ,Total Short Contracts
    Client,289936,56634,3445234,228230,3011455,2259579,2713125,2870213,2876957,831918,1453842,1227293,12715079,8549336
    DII,40557,28011,360574,4613178,5539,50325,1963,0,4956,45256,401796,24249,507207,5069197
    FII,47446,335549,3454477,2918400,598953,1136529,887159,556367,208469,401867,463506,202412,5847742,5363394
    Pro,72202,29947,899938,400415,924260,858812,937961,878666,975180,1111455,1746418,936542,4841847,4929948
    TOTAL,450141,450141,8160223,8160223,4540207,4305245,4540207,4305245,4065562,2390496,4065562,2390496,23911875,23911875
    """;

    internal const string CashJson = """
    [{"buyValue":"14105.01","category":"DII","date":"17-Sep-2026","netValue":"3617.75","sellValue":"10487.26"},{"buyValue":"8761.71","category":"FII/FPI","date":"17-Sep-2026","netValue":"-3208.76","sellValue":"11970.47"}]
    """;

    internal const string BhavcopyCsv = """
    TradDt,BizDt,Sgmt,Src,FinInstrmTp,FinInstrmId,ISIN,TckrSymb,SctySrs,XpryDt,FininstrmActlXpryDt,StrkPric,OptnTp,FinInstrmNm,OpnPric,HghPric,LwPric,ClsPric,LastPric,PrvsClsgPric,UndrlygPric,SttlmPric,OpnIntrst,ChngInOpnIntrst,TtlTradgVol,TtlTrfVal,TtlNbOfTxsExctd,SsnId,NewBrdLotQty,Rmks,Rsvd1,Rsvd2,Rsvd3,Rsvd4
    2026-09-16,2026-09-16,FO,NSE,IDF,48704,,NIFTY,,2026-10-27,2026-10-27,,,NIFTY26OCTFUT,23379.90,23450.00,23300.10,23370.60,23372.00,23332.00,23217.60,23370.60,3428880,165880,6308,9586053236.50,4156,F1,65,,,,,
    2026-09-16,2026-09-16,FO,NSE,IDF,68407,,NIFTY,,2026-09-29,2026-09-29,,,NIFTY26SEPFUT,23234.00,23346.00,23198.40,23272.30,23282.00,23223.90,23217.60,23272.30,17746040,-293085,31552,47746039328.00,21574,F1,65,,,,,
    2026-09-16,2026-09-16,FO,NSE,IDO,40001,,NIFTY,,2026-09-22,2026-09-22,23300,CE,NIFTY2692223300CE,120.00,150.00,100.00,130.00,131.00,110.00,23217.60,130.00,1000,100,500,65000.00,50,F1,65,,,,,
    """;

    private const string NseIxJson = """
    {"data":[{"INSTRUMENTTYPE":"FUTIDX","SYMBOL":"NIFTY","EXPIRYDATE":"29-Sep-2026","OPTIONTYPE":"-","STRIKEPRICE":"-","LASTPRICE":"23333.00","DAYCHANGE":"0.00","DAYCHANGE_1":0,"PERCHANGE":"0","CONTRACTSTRADED":52899,"TIMESTMP":"17-Sep-2026 19:26:50","TOKEN_NMBR":1215,"id":"0/NIFTYFUTIDX29-Sep-2026--"},{"INSTRUMENTTYPE":"FUTIDX","SYMBOL":"NIFTY","EXPIRYDATE":"27-Oct-2026","OPTIONTYPE":"-","STRIKEPRICE":"-","LASTPRICE":"23346.50","DAYCHANGE":"-52.00","DAYCHANGE_1":-52,"PERCHANGE":"-.22","CONTRACTSTRADED":423,"TIMESTMP":"17-Sep-2026 19:12:39","TOKEN_NMBR":1010,"id":"1/NIFTYFUTIDX27-Oct-2026--"}]}
    """;

    private const string YahooJson = """
    {"chart":{"result":[{"meta":{"currency":"USD","symbol":"ES=F","regularMarketTime":1789653039,"regularMarketPrice":7695.0,"chartPreviousClose":7623.0,"previousClose":7623.0,"shortName":"E-Mini S&P 500 Sep 26"},"timestamp":[],"indicators":{"quote":[{}]}}],"error":null}}
    """;

    [Fact]
    public void Participant_file_reads_every_group_and_checks_its_date_and_balance()
    {
        var rows = MarketFactorParsers.ParseParticipantOpenInterest(ParticipantCsv, new DateOnly(2026, 9, 16), "test");

        Assert.Equal(5, rows.Count);
        var fii = rows.Single(r => r.ClientType == "FII");
        Assert.Equal(47446, fii.FutureIndexLong);
        Assert.Equal(335549, fii.FutureIndexShort);
        Assert.Equal(887159, fii.OptionIndexCallShort);
        Assert.Equal(5363394, fii.TotalShort);

        // A file served under the wrong day must not be stored under it.
        Assert.Throws<FormatException>(() =>
            MarketFactorParsers.ParseParticipantOpenInterest(ParticipantCsv, new DateOnly(2026, 9, 17), "test"));

        // A TOTAL row whose longs and shorts disagree means the columns were read wrong.
        var broken = ParticipantCsv.Replace("TOTAL,450141,450141", "TOTAL,450141,450140");
        Assert.Throws<FormatException>(() =>
            MarketFactorParsers.ParseParticipantOpenInterest(broken, new DateOnly(2026, 9, 16), "test"));
    }

    [Fact]
    public void Participant_positions_read_as_net_long_share_and_day_change()
    {
        var day1 = MarketFactorParsers.ParseParticipantOpenInterest(ParticipantCsv, new DateOnly(2026, 9, 16), "test");
        var day2 = day1.Select(r => new MarketParticipantOpenInterest
        {
            Date = new DateOnly(2026, 9, 17),
            ClientType = r.ClientType,
            FutureIndexLong = r.ClientType == "FII" ? 57446 : r.FutureIndexLong,
            FutureIndexShort = r.FutureIndexShort,
        });

        var shaped = MarketFactorsQueries.ShapeParticipants(day1.Concat(day2));

        Assert.Equal(new DateOnly(2026, 9, 17), shaped[0].Date);
        Assert.Equal(new[] { "FII", "DII", "Pro", "Client" }, shaped[0].Groups.Select(g => g.ClientType));
        var fii = shaped[0].Groups[0];
        Assert.Equal(57446 - 335549, fii.FutureIndexNet);
        Assert.Equal(10000, fii.FutureIndexNetChange);
        Assert.Equal(14.6m, fii.FutureIndexLongPercent);
        Assert.Null(shaped[1].Groups[0].FutureIndexNetChange);   // the oldest day has nothing to compare with
    }

    [Fact]
    public void Cash_figures_read_both_categories_in_crore()
    {
        var rows = MarketFactorParsers.ParseCashFlows(CashJson, "test");
        var shaped = MarketFactorsQueries.ShapeCash(rows);

        Assert.Single(shaped);
        Assert.Equal(new DateOnly(2026, 9, 17), shaped[0].Date);
        Assert.Equal(-3208.76m, shaped[0].Fii!.Net);
        Assert.Equal(3617.75m, shaped[0].Dii!.Net);
        Assert.Equal(14105.01m, shaped[0].Dii!.Buy);
    }

    [Fact]
    public void Bhavcopy_keeps_futures_only_and_reads_them_as_a_build_up()
    {
        var rows = MarketFactorParsers.ParseFuturesBhavcopy(BhavcopyCsv, "test");

        Assert.Equal(2, rows.Count);   // the option row is left out
        var sep = rows.Single(r => r.ExpiryDate == new DateOnly(2026, 9, 29));
        Assert.Equal(17746040, sep.OpenInterest);
        Assert.Equal(-293085, sep.OpenInterestChange);
        Assert.Equal(23272.30m, sep.Close);

        var day = MarketFactorsQueries.Day(sep);
        Assert.Equal(0.21m, day.PriceChangePercent);         // 23223.90 -> 23272.30
        Assert.Equal(-1.62m, day.OpenInterestChangePercent); // 18039125 -> 17746040
        Assert.Equal(OiBuildup.ShortCovering, day.BuildUp);   // price up, OI down

        var oct = MarketFactorsQueries.Day(rows.Single(r => r.ExpiryDate == new DateOnly(2026, 10, 27)));
        Assert.Equal(OiBuildup.LongBuildUp, oct.BuildUp);     // price up, OI up
    }

    [Fact]
    public void Gift_nifty_is_the_nearest_unexpired_future()
    {
        var quote = MarketFactorParsers.ParseGiftNifty(NseIxJson, new DateOnly(2026, 9, 17));
        Assert.NotNull(quote);
        Assert.Equal(new DateOnly(2026, 9, 29), quote!.Expiry);
        Assert.Equal(23333.00m, quote.LastPrice);
        Assert.Equal(new DateTime(2026, 9, 17, 13, 56, 50, DateTimeKind.Utc), quote.AsOfUtc);

        var afterExpiry = MarketFactorParsers.ParseGiftNifty(NseIxJson, new DateOnly(2026, 9, 30));
        Assert.Equal(new DateOnly(2026, 10, 27), afterExpiry!.Expiry);
        Assert.Equal(-0.22m, afterExpiry.ChangePercent);
    }

    [Fact]
    public void Yahoo_chart_gives_price_previous_close_and_time()
    {
        var quote = MarketFactorParsers.ParseYahooChart(YahooJson, "S&P 500 futures");
        Assert.NotNull(quote);
        Assert.Equal(7695.0m, quote!.LastPrice);
        Assert.Equal(7623.0m, quote.PreviousClose);
        Assert.Equal("USD", quote.Currency);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789653039).UtcDateTime, quote.AsOfUtc);
        Assert.Null(MarketFactorParsers.ParseYahooChart("""{"chart":{"result":null,"error":{"code":"Not Found"}}}""", "x"));
    }

    [Fact]
    public void Index_future_symbol_is_matched_by_name_not_by_the_underlying_column()
    {
        Assert.True(MarketFactorsQueries.IsIndexFutureOf("NSE:NIFTY26SEPFUT", "NIFTY"));
        // The master files NIFTY NEXT 50's future under the underlying NIFTY as well.
        Assert.False(MarketFactorsQueries.IsIndexFutureOf("NSE:NIFTYNXT5026SEPFUT", "NIFTY"));
        Assert.True(MarketFactorsQueries.IsIndexFutureOf("NSE:BANKNIFTY26SEPFUT", "BANKNIFTY"));
        Assert.False(MarketFactorsQueries.IsIndexFutureOf("NSE:NIFTY2692223300CE", "NIFTY"));
    }

    [Fact]
    public void Recent_sessions_skip_weekends_holidays_and_today_before_the_evening()
    {
        // Thu 17 Sep 2026, 12:00 IST: today's files cannot exist yet.
        var noon = new DateTime(2026, 9, 17, 6, 30, 0, DateTimeKind.Utc);
        var holiday = new DateOnly(2026, 9, 14);
        var sessions = MarketFactorsSync.RecentSessions(noon, 4, d => d == holiday);
        Assert.Equal(new[] { new DateOnly(2026, 9, 16), new DateOnly(2026, 9, 15), new DateOnly(2026, 9, 11), new DateOnly(2026, 9, 10) }, sessions);

        // 19:00 IST the same day: today is included.
        var evening = new DateTime(2026, 9, 17, 13, 30, 0, DateTimeKind.Utc);
        Assert.Equal(new DateOnly(2026, 9, 17), MarketFactorsSync.RecentSessions(evening, 1, _ => false)[0]);
    }

    [Fact]
    public void Evening_window_is_18_00_to_23_30_ist()
    {
        Assert.False(MarketFactorsSyncService.InEveningWindow(new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc)));  // 17:30 IST
        Assert.True(MarketFactorsSyncService.InEveningWindow(new DateTime(2026, 9, 17, 12, 30, 0, DateTimeKind.Utc)));  // 18:00 IST
        Assert.True(MarketFactorsSyncService.InEveningWindow(new DateTime(2026, 9, 17, 18, 0, 0, DateTimeKind.Utc)));   // 23:30 IST
        Assert.False(MarketFactorsSyncService.InEveningWindow(new DateTime(2026, 9, 17, 18, 30, 0, DateTimeKind.Utc))); // 00:00 IST
    }

    [Fact]
    public void Nse_ix_token_expiry_is_read_from_the_jwt_with_a_minute_to_spare()
    {
        const string token = "eyJhbGciOiJIUzUxMiIsInR5cCI6IkpXVCJ9.eyJhcHAiOiJyZWFjdC1jbGllbnQiLCJpYXQiOjE3ODk2NTM0MTUsImV4cCI6MTc4OTY1NzAxNX0.sig";
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789657015).UtcDateTime.AddMinutes(-1), GlobalCuesService.TokenExpiry(token));
        Assert.Null(GlobalCuesService.TokenExpiry("not-a-jwt"));
    }
}

/// <summary>
/// A sync run end to end against an in-memory database and a stand-in for NSE
/// that serves the real files of 16–17 Sep 2026: what is stored, what counts as
/// "not published yet", and that a second run fetches nothing it already has.
/// </summary>
public class MarketFactorsSyncRunTests
{
    private sealed class FakeNse : HttpMessageHandler
    {
        public readonly List<string> Requests = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.ToString();
            Requests.Add(url);
            if (url == MarketFactorsSync.ParticipantUrl(new DateOnly(2026, 9, 16)))
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(MarketFactorsFixtures.ParticipantCsv) });
            if (url == MarketFactorsSync.FuturesBhavcopyUrl(new DateOnly(2026, 9, 16)))
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(MarketFactorsFixtures.Zip("BhavCopy.csv", MarketFactorsFixtures.BhavcopyCsv)) });
            if (url == MarketFactorsSync.CashFlowsUrl)
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(MarketFactorsFixtures.CashJson) });
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class NoHolidays : AlgoTrading.Application.Interfaces.IMarketCalendar
    {
        public bool IsLoaded => true;
        public MarketHoliday? HolidayOn(string exchange, DateOnly date) => null;
        public MarketSpecialSession? SpecialSessionOn(string exchange, DateOnly date) => null;
        public bool HasYear(string exchange, int year) => true;
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task A_run_stores_the_published_days_and_a_second_run_asks_only_for_the_missing_ones()
    {
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<AlgoTrading.Infrastructure.Persistence.TradingDbContext>()
            .UseInMemoryDatabase($"factors-{Guid.NewGuid():N}").Options;
        await using var db = new AlgoTrading.Infrastructure.Persistence.TradingDbContext(options);
        var nse = new FakeNse();
        var status = new MarketFactorsStatus();
        var sync = new MarketFactorsSync(db, new Factory(nse), new NoHolidays(), status,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MarketFactorsSync>.Instance)
        {
            // Thu 17 Sep 2026, 19:00 IST: 17, 16 and 15 Sep are the last three sessions.
            Clock = () => new DateTime(2026, 9, 17, 13, 30, 0, DateTimeKind.Utc),
            Pace = TimeSpan.Zero,
        };

        var report = await sync.RunAsync(3, CancellationToken.None);

        Assert.Equal(5, db.MarketParticipantOpenInterest.Count());
        Assert.All(db.MarketParticipantOpenInterest, r => Assert.Equal(new DateOnly(2026, 9, 16), r.Date));
        Assert.Equal(2, db.MarketFuturesDaily.Count());
        Assert.Equal(2, db.MarketCashFlows.Count());
        Assert.Contains("participant OI: 1 day(s) stored, 2 not published, 0 failed", report.Lines[0]);
        Assert.Equal(new DateOnly(2026, 9, 16), status.For(MarketFactorsSync.ParticipantDataset).NewestDay);
        Assert.False(status.For(MarketFactorsSync.ParticipantDataset).LastFailed);

        nse.Requests.Clear();
        await sync.RunAsync(3, CancellationToken.None);

        // 16 Sep is stored, so only 15 and 17 Sep are asked for again; the cash figures are upserted, not doubled.
        Assert.DoesNotContain(MarketFactorsSync.ParticipantUrl(new DateOnly(2026, 9, 16)), nse.Requests);
        Assert.Contains(MarketFactorsSync.ParticipantUrl(new DateOnly(2026, 9, 17)), nse.Requests);
        Assert.Equal(5, db.MarketParticipantOpenInterest.Count());
        Assert.Equal(2, db.MarketCashFlows.Count());
    }
}

/// <summary>Real answers shared by the market-factor tests.</summary>
internal static class MarketFactorsFixtures
{
    public static string ParticipantCsv => MarketFactorsTests.ParticipantCsv;

    public static string BhavcopyCsv => MarketFactorsTests.BhavcopyCsv;

    public static string CashJson => MarketFactorsTests.CashJson;

    public static byte[] Zip(string name, string text)
    {
        using var memory = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(text);
        }
        return memory.ToArray();
    }
}
