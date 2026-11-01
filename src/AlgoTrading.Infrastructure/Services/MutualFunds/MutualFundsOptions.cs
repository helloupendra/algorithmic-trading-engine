using System.Net;

namespace AlgoTrading.Infrastructure.Services.MutualFunds;

/// <summary>The <c>MutualFunds</c> section of appsettings. See docs/modules/mutual_funds_module.md.</summary>
public sealed class MutualFundsOptions
{
    public const string SectionName = "MutualFunds";

    /// <summary>The daily NAV sync at 23:30 IST (and its 07:00 retry). On by default.</summary>
    public bool SyncEnabled { get; set; } = true;

    /// <summary>
    /// Whether the history backfill may run at all. It never starts by itself:
    /// an admin starts it (<c>POST api/MutualFunds/backfill</c>), and from then
    /// on it resumes after restarts until it finishes or is paused.
    /// </summary>
    public bool BackfillEnabled { get; set; } = true;

    /// <summary>AMFI's daily file. The old www.amfiindia.com address redirects here.</summary>
    public string DailyUrl { get; set; } = "https://portal.amfiindia.com/spages/NAVAll.txt";

    /// <summary>AMFI's NAV history download; <c>tp</c>, <c>frmdt</c> and <c>todt</c> are added.</summary>
    public string HistoryUrl { get; set; } = "https://portal.amfiindia.com/DownloadNAVHistoryReport_Po.aspx";

    /// <summary>mfapi.in, a free mirror of AMFI's history, used only to spot-check what we stored.</summary>
    public string SpotCheckUrl { get; set; } = "https://api.mfapi.in/mf/";

    /// <summary>The pause before every history request, in seconds: AMFI is a public site, not an API with a quota.</summary>
    public double RequestPaceSeconds { get; set; } = 2.5;

    /// <summary>
    /// The fund report's nightly jobs after the NAV sync: the benchmark (TRI) sync, the TER sync and the category
    /// ranking. On by default; they never run 09:00–15:40 IST on trading days.
    /// </summary>
    public bool AnalyticsEnabled { get; set; } = true;

    /// <summary>
    /// Whether the benchmark sync may fetch each index's history back to <see cref="BenchmarkFrom"/> the first time
    /// (about 20 one-year requests an index, paced like the NAV backfill). It starts by itself: it is small.
    /// </summary>
    public bool BenchmarkBackfillEnabled { get; set; } = true;

    /// <summary>How far back the benchmark history goes: a little before AMFI's NAV history (1 Apr 2006).</summary>
    public DateOnly BenchmarkFrom { get; set; } = new(2006, 1, 1);

    /// <summary>niftyindices.com's total-return index history (POST, at most a year a request).</summary>
    public string BenchmarkUrl { get; set; } = "https://www.niftyindices.com/BackPage/getTotalReturnIndexString";

    /// <summary>AMFI's TER disclosure, one month of every scheme as an Excel file; <c>Month=MM-yyyy</c> is added.</summary>
    public string TerUrl { get; set; } = "https://www.amfiindia.com/api/populate-te-rdata-revised?MF_ID=All&strCat=-1&strType=-1&excel=true";

    /// <summary>
    /// The risk-free rate Sharpe and Sortino use, in percent a year, with its source and date: the report states all
    /// three. Null leaves Sharpe and Sortino out, and the report says why.
    /// </summary>
    public double? RiskFreeRatePct { get; set; }

    public string? RiskFreeRateSource { get; set; }
    public DateOnly? RiskFreeRateAsOf { get; set; }

    public const string HttpClientName = "amfi";
    public const string IndicesHttpClientName = "niftyindices";

    /// <summary>
    /// AMFI's history windows are 40–80 MB of text and take AMFI two minutes or
    /// more to produce, so the timeout is long; the answers are gzipped on request.
    /// </summary>
    public static void Configure(HttpClient client)
    {
        client.Timeout = TimeSpan.FromMinutes(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; OpenFNO mutual fund sync)");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/plain");
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
    }

    /// <summary>niftyindices.com answers its own page's requests: a browser's headers, JSON in and out.</summary>
    public static void ConfigureIndices(HttpClient client)
    {
        client.Timeout = TimeSpan.FromMinutes(2);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; OpenFNO mutual fund benchmarks)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        client.DefaultRequestHeaders.Add("Origin", "https://www.niftyindices.com");
        client.DefaultRequestHeaders.Referrer = new Uri("https://www.niftyindices.com/reports/historical-data");
    }

    public static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = true,
    };
}
