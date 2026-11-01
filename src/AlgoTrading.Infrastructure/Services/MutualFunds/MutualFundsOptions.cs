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

    public const string HttpClientName = "amfi";

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

    public static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = true,
    };
}
