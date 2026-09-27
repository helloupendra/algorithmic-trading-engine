using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Infrastructure.Services.MarketIntelligence;

/// <summary>
/// Spaces out every request this module makes to NSE, whichever recorder or
/// backfill makes it, so together they stay at about one a second.
/// </summary>
/// <remarks>
/// NSE's website and archives are public pages, not an API with a quota. A
/// burst is the quickest way to get the server's address refused, and that
/// would also stop the market-factor files the desk already depends on.
/// </remarks>
public sealed class NseRequestPacer
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastUtc = DateTime.MinValue;

    public NseRequestPacer(TimeSpan minimumGap) => MinimumGap = minimumGap;

    /// <summary>The least time between two requests; zero in tests.</summary>
    public TimeSpan MinimumGap { get; }

    public async Task WaitAsync(CancellationToken ct)
    {
        if (MinimumGap <= TimeSpan.Zero) return;

        await _gate.WaitAsync(ct);
        try
        {
            var wait = _lastUtc + MinimumGap - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _lastUtc = DateTime.UtcNow;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// Reads NSE's website JSON (<c>www.nseindia.com/api/...</c>), which answers
/// only a caller that looks like the browser that loaded its page: the cookies
/// that page sets, and the page as the referrer.
/// </summary>
/// <remarks>
/// <para>
/// The cookies are kept here, not in the HTTP handler, because the handler
/// factory recycles handlers every few minutes and a handler's cookie jar goes
/// with it. They are refreshed by loading the referrer page again every
/// <see cref="CookieLifetime"/>, and at once when NSE answers 401 or 403, which
/// is how it says the session expired.
/// </para>
/// <para>
/// Same agent string as the archive fetches (<see cref="MarketFactors.MarketFactorsSync.Configure"/>),
/// which is the one checked from the server. From a Mac on 27 Sep 2026 the
/// announcements API also answered without cookies; the priming is for the day
/// NSE's edge stops allowing that, as it has before.
/// </para>
/// </remarks>
public sealed class NseWebClient
{
    public const string HttpClientName = "nse-web";

    /// <summary>NSE's session cookies last longer than this; refreshing early costs one page load.</summary>
    public static readonly TimeSpan CookieLifetime = TimeSpan.FromMinutes(20);

    private readonly IHttpClientFactory _http;
    private readonly NseRequestPacer _pacer;
    private readonly ILogger<NseWebClient> _logger;
    private readonly SemaphoreSlim _primeGate = new(1, 1);
    private CookieContainer _cookies = new();
    private DateTime _primedUtc = DateTime.MinValue;

    public NseWebClient(IHttpClientFactory http, NseRequestPacer pacer, ILogger<NseWebClient> logger)
    {
        _http = http;
        _pacer = pacer;
        _logger = logger;
    }

    /// <summary>The clock the cookie age is read from; replaced in tests.</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>
    /// GETs a JSON endpoint as the page <paramref name="refererPage"/> would.
    /// Returns the status and, when it is 200, the body.
    /// </summary>
    public async Task<(HttpStatusCode Status, string Body)> GetJsonAsync(string url, string refererPage, CancellationToken ct)
    {
        if (Clock() - _primedUtc > CookieLifetime) await PrimeAsync(refererPage, force: false, ct);

        var (status, body) = await SendAsync(url, refererPage, ct);
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            _logger.LogInformation("NSE answered {Status} for {Url}; loading {Page} again for fresh cookies.", (int)status, url, refererPage);
            await PrimeAsync(refererPage, force: true, ct);
            (status, body) = await SendAsync(url, refererPage, ct);
        }

        return (status, body);
    }

    private async Task<(HttpStatusCode, string)> SendAsync(string url, string refererPage, CancellationToken ct)
    {
        await _pacer.WaitAsync(ct);
        var uri = new Uri(url);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Referrer = new Uri(refererPage);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        string cookies = _cookies.GetCookieHeader(uri);
        if (cookies.Length > 0) request.Headers.Add("Cookie", cookies);

        using var response = await _http.CreateClient(HttpClientName).SendAsync(request, ct);
        Keep(response, uri);
        string body = response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : string.Empty;
        return (response.StatusCode, body);
    }

    private async Task PrimeAsync(string page, bool force, CancellationToken ct)
    {
        await _primeGate.WaitAsync(ct);
        try
        {
            // Another caller may have primed while this one waited.
            if (!force && Clock() - _primedUtc <= CookieLifetime) return;

            await _pacer.WaitAsync(ct);
            var uri = new Uri(page);
            _cookies = new CookieContainer();
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml");
            using var response = await _http.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            Keep(response, uri);

            // Set whether it worked or not: a failing page is not retried on
            // every call, and the API call itself says whether NSE let it in.
            _primedUtc = Clock();
            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("NSE page {Page} answered {Status} while fetching cookies.", page, (int)response.StatusCode);
        }
        finally
        {
            _primeGate.Release();
        }
    }

    private void Keep(HttpResponseMessage response, Uri uri)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values)) return;
        foreach (var value in values)
        {
            try
            {
                _cookies.SetCookies(uri, value);
            }
            catch (CookieException)
            {
                // A cookie .NET cannot read is one the API does not need to see.
            }
        }
    }

    /// <summary>
    /// The handler never manages cookies itself (this class does, see above),
    /// and accepts compressed answers: NSE's page is 400 KB uncompressed.
    /// </summary>
    public static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    public static void Configure(HttpClient client)
    {
        client.Timeout = TimeSpan.FromSeconds(40);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    }
}
