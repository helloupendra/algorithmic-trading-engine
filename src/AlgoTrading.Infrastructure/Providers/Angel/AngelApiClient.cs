using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Text.Json;
using AlgoTrading.Infrastructure.Providers.Angel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Providers.Angel;

/// <summary>What one Angel call came back with, in words the console can show.</summary>
public sealed record AngelResult(bool Ok, string Message, JsonElement? Data = null, long ElapsedMs = 0)
{
    /// <summary>The vendor refused for pace alone: the same call works after a wait.</summary>
    public bool RateLimited { get; init; }
}

/// <summary>
/// Angel One SmartAPI over HTTP: sign in, then ask for prices.
/// </summary>
/// <remarks>
/// SmartAPI answers a refusal with HTTP 200 and <c>status:false</c>, so every
/// call is unwrapped and the vendor's own message is carried up. The session
/// (client code + PIN + TOTP) is held in memory for the trading day; a refusal
/// clears it so the next call signs in again rather than repeating a dead token.
///
/// <para>Nothing here runs on a timer and nothing writes to the database. It is
/// used by the Connectors page and by whatever is built on top later.</para>
/// </remarks>
public sealed class AngelApiClient
{
    private const string LoginPath = "/rest/auth/angelbroking/user/v1/loginByPassword";
    private const string ProfilePath = "/rest/secure/angelbroking/user/v1/getProfile";
    private const string LtpPath = "/rest/secure/angelbroking/order/v1/getLtpData";
    private const string CandlePath = "/rest/secure/angelbroking/historical/v1/getCandleData";

    private static readonly SemaphoreSlim LoginGate = new(1, 1);
    private static readonly SemaphoreSlim PaceGate = new(1, 1);
    private static string? _jwt;
    private static DateTimeOffset _jwtAt;
    private static DateTimeOffset _lastCall;

    /// <summary>
    /// Smallest gap between two SmartAPI calls from this process.
    /// </summary>
    /// <remarks>
    /// SmartAPI answers a burst with HTTP 403 and a plain-text body — not JSON,
    /// not a status flag: "Access denied because of exceeding access rate"
    /// (2026-09-16, building the movers screen). Pacing costs a second; being
    /// refused costs the panel.
    /// </remarks>
    public static TimeSpan MinCallGap { get; } = TimeSpan.FromMilliseconds(1200);

    /// <summary>How long to wait before each retry of a rate-limited call.</summary>
    public static IReadOnlyList<TimeSpan> RateLimitWaits { get; } =
        new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5) };

    private readonly IHttpClientFactory _factory;
    private readonly AngelSettings _settings;
    private readonly ILogger<AngelApiClient> _logger;
    private readonly TimeProvider _time;

    public AngelApiClient(IHttpClientFactory factory, IOptions<AngelSettings> settings,
        ILogger<AngelApiClient> logger, TimeProvider? time = null)
    {
        _factory = factory;
        _settings = settings.Value;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>At most this many instruments in one quote call (SmartAPI's cap).</summary>
    public const int MaxQuoteSymbols = 50;

    /// <summary>A session younger than this is reused; SmartAPI's own token lasts the trading day.</summary>
    public static TimeSpan SessionLifetime { get; } = TimeSpan.FromHours(8);

    public bool HasSession => _jwt is not null && DateTimeOffset.UtcNow - _jwtAt < SessionLifetime;

    public DateTimeOffset? SessionStartedUtc => _jwt is null ? null : _jwtAt;

    /// <summary>Forget the session, so the next call signs in again.</summary>
    public static void ForgetSession()
    {
        _jwt = null;
        _jwtAt = default;
    }

    private HttpClient Client()
    {
        var client = _factory.CreateClient(AngelProvider.Key);
        client.BaseAddress ??= new Uri(_settings.RootUrl.TrimEnd('/'));
        var headers = client.DefaultRequestHeaders;
        headers.Clear();
        headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        headers.Add("X-UserType", "USER");
        headers.Add("X-SourceID", "WEB");
        headers.Add("X-ClientLocalIP", LocalIp());
        headers.Add("X-ClientPublicIP", string.IsNullOrWhiteSpace(_settings.StaticIp) ? LocalIp() : _settings.StaticIp);
        headers.Add("X-MACAddress", MacAddress());
        headers.Add("X-PrivateKey", _settings.ApiKey);
        if (_jwt is not null) headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwt);
        return client;
    }

    /// <summary>Sign in unless a live session is already held.</summary>
    public async Task<AngelResult> LoginAsync(bool force, CancellationToken cancellationToken)
    {
        if (_settings.Missing().Count > 0)
            return new AngelResult(false, "Not configured: set " + string.Join(", ", _settings.Missing()) + ".");

        if (HasSession && !force) return new AngelResult(true, "Already signed in.");

        await LoginGate.WaitAsync(cancellationToken);
        try
        {
            if (HasSession && !force) return new AngelResult(true, "Already signed in.");
            string totp;
            try
            {
                totp = AngelTotp.Generate(_settings.TotpSecret, _time.GetUtcNow());
            }
            catch (ArgumentException ex)
            {
                return new AngelResult(false, ex.Message);
            }

            var body = new { clientcode = _settings.ClientCode, password = _settings.Pin, totp };
            var result = await PostAsync(LoginPath, body, cancellationToken, signedIn: false);
            if (!result.Ok) return result;

            string? jwt = result.Data?.TryGetProperty("jwtToken", out var token) == true ? token.GetString() : null;
            if (string.IsNullOrWhiteSpace(jwt))
                return new AngelResult(false, "Angel accepted the sign-in but returned no token.");

            _jwt = jwt;
            _jwtAt = DateTimeOffset.UtcNow;
            _logger.LogInformation("Angel One session started for {ClientCode}", _settings.ClientCode);
            return new AngelResult(true, "Signed in.", result.Data, result.ElapsedMs);
        }
        finally
        {
            LoginGate.Release();
        }
    }

    public async Task<AngelResult> ProfileAsync(CancellationToken cancellationToken)
    {
        var login = await LoginAsync(false, cancellationToken);
        if (!login.Ok) return login;
        return await GetAsync(ProfilePath, cancellationToken);
    }

    /// <summary>One signed POST to any SmartAPI path, for the screens built on top.</summary>
    public async Task<AngelResult> CallAsync(string path, object body, CancellationToken cancellationToken)
    {
        var login = await LoginAsync(false, cancellationToken);
        if (!login.Ok) return login;
        return await PostAsync(path, body, cancellationToken);
    }

    /// <summary>One signed GET (the profile and put-call ratio endpoints refuse a POST).</summary>
    public async Task<AngelResult> GetAsync(string path, CancellationToken cancellationToken, bool signIn)
    {
        if (signIn)
        {
            var login = await LoginAsync(false, cancellationToken);
            if (!login.Ok) return login;
        }
        return await GetAsync(path, cancellationToken);
    }

    /// <summary>Quotes for up to <see cref="MaxQuoteSymbols"/> instruments, keyed by exchange.</summary>
    public async Task<AngelResult> QuotesAsync(IReadOnlyDictionary<string, IReadOnlyList<string>> tokensByExchange,
        string mode, CancellationToken cancellationToken)
    {
        int total = tokensByExchange.Sum(pair => pair.Value.Count);
        if (total > MaxQuoteSymbols)
            return new AngelResult(false, $"SmartAPI takes at most {MaxQuoteSymbols} instruments per quote call, asked for {total}.");

        var login = await LoginAsync(false, cancellationToken);
        if (!login.Ok) return login;
        return await PostAsync("/rest/secure/angelbroking/market/v1/quote/",
            new { mode = mode.ToUpperInvariant(), exchangeTokens = tokensByExchange }, cancellationToken);
    }

    /// <summary>Last traded price for one instrument, by Angel's exchange and token.</summary>
    public async Task<AngelResult> LtpAsync(string exchange, string tradingSymbol, string token,
        CancellationToken cancellationToken)
    {
        var login = await LoginAsync(false, cancellationToken);
        if (!login.Ok) return login;
        return await PostAsync(LtpPath, new { exchange, tradingsymbol = tradingSymbol, symboltoken = token },
            cancellationToken);
    }

    /// <summary>Candles for one window. The caller keeps to the interval's day limit.</summary>
    public async Task<AngelResult> CandlesAsync(string exchange, string token, string interval,
        DateTime fromIst, DateTime toIst, CancellationToken cancellationToken)
    {
        var login = await LoginAsync(false, cancellationToken);
        if (!login.Ok) return login;
        var body = new
        {
            exchange,
            symboltoken = token,
            interval,
            fromdate = fromIst.ToString("yyyy-MM-dd HH:mm"),
            todate = toIst.ToString("yyyy-MM-dd HH:mm"),
        };
        return await PostAsync(CandlePath, body, cancellationToken);
    }

    // ------------------------------------------------------------- plumbing

    private Task<AngelResult> GetAsync(string path, CancellationToken cancellationToken)
        => SendAsync(new HttpRequestMessage(HttpMethod.Get, path), cancellationToken);

    private Task<AngelResult> PostAsync(string path, object body, CancellationToken cancellationToken,
        bool signedIn = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        return SendAsync(request, cancellationToken);
    }

    /// <summary>One call, paced, and retried while the vendor is only complaining about pace.</summary>
    private async Task<AngelResult> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        AngelResult result = await SendOnceAsync(request, cancellationToken);
        for (int attempt = 0; attempt < RateLimitWaits.Count && result.RateLimited; attempt++)
        {
            await Task.Delay(RateLimitWaits[attempt], cancellationToken);
            var retry = Clone(request);
            if (retry is null) break;
            result = await SendOnceAsync(retry, cancellationToken);
        }
        return result;
    }

    /// <summary>A request can only be sent once, so a retry needs a copy.</summary>
    private static HttpRequestMessage? Clone(HttpRequestMessage request)
    {
        var copy = new HttpRequestMessage(request.Method, request.RequestUri);
        if (request.Content is null) return copy;
        if (request.Content is not System.Net.Http.Json.JsonContent && request.Content is not StringContent) return null;
        string body = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        copy.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        return copy;
    }

    private async Task<AngelResult> SendOnceAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var started = _time.GetTimestamp();
        await PaceGate.WaitAsync(cancellationToken);
        try
        {
            var since = DateTimeOffset.UtcNow - _lastCall;
            if (since < MinCallGap) await Task.Delay(MinCallGap - since, cancellationToken);
            _lastCall = DateTimeOffset.UtcNow;
        }
        finally
        {
            PaceGate.Release();
        }

        try
        {
            using var client = Client();
            using var response = await client.SendAsync(request, cancellationToken);
            long elapsed = (long)_time.GetElapsedTime(started).TotalMilliseconds;
            string text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (text.Length == 0)
                return new AngelResult(false, $"Angel answered HTTP {(int)response.StatusCode} with an empty body.", null, elapsed);

            // A rate refusal is plain text, not JSON, and says so in words.
            if (IsRateLimit((int)response.StatusCode, text))
                return new AngelResult(false, $"Angel is rate-limiting: {text.Trim()[..Math.Min(90, text.Trim().Length)]}",
                    null, elapsed) { RateLimited = true };

            using var document = JsonDocument.Parse(text);
            var root = document.RootElement.Clone();
            bool ok = root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.True;
            if (ok)
            {
                JsonElement? data = root.TryGetProperty("data", out var payload) ? payload.Clone() : null;
                return new AngelResult(true, "OK", data, elapsed);
            }

            string message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "refused" : "refused";
            string code = root.TryGetProperty("errorcode", out var c) ? c.GetString() ?? string.Empty : string.Empty;
            if (code is "AB1010" or "AB8050" or "AB8051") ForgetSession();   // token no longer accepted
            return new AngelResult(false, Explain(message, code), null, elapsed);
        }
        catch (JsonException)
        {
            return new AngelResult(false, "Angel answered with something that is not JSON.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new AngelResult(false, $"Could not reach Angel One: {ex.Message}");
        }
    }

    /// <summary>True when the vendor is refusing for pace alone, which a wait fixes.</summary>
    internal static bool IsRateLimit(int statusCode, string body)
        => (statusCode == 403 || statusCode == 429)
           && body.Contains("rate", StringComparison.OrdinalIgnoreCase);

    /// <summary>The vendor's words, plus the cause when the code has a known one.</summary>
    internal static string Explain(string message, string code)
    {
        string suffix = string.IsNullOrWhiteSpace(code) ? string.Empty : $" [{code}]";
        if (message.Contains("totp", StringComparison.OrdinalIgnoreCase))
            return $"{message}{suffix} — the TOTP secret or this machine's clock is wrong.";
        if (code is "AB1004" || message.Contains("block", StringComparison.OrdinalIgnoreCase))
            return $"{message}{suffix} — the app only answers from the static IP it was registered with.";
        if (code is "AB1010" or "AB8050" or "AB8051")
            return $"{message}{suffix} — the session was rejected; the next call signs in again.";
        return message + suffix;
    }

    private static string LocalIp()
    {
        try
        {
            using var probe = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Dgram, 0);
            probe.Connect("8.8.8.8", 65530);      // no packet leaves; it just picks the route
            return (probe.LocalEndPoint as System.Net.IPEndPoint)?.Address.ToString() ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }

    private static string MacAddress()
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up
                                     && n.NetworkInterfaceType != NetworkInterfaceType.Loopback);
            string raw = nic?.GetPhysicalAddress().ToString() ?? "000000000000";
            return string.Join(":", Enumerable.Range(0, raw.Length / 2).Select(i => raw.Substring(i * 2, 2)));
        }
        catch
        {
            return "00:00:00:00:00:00";
        }
    }
}
