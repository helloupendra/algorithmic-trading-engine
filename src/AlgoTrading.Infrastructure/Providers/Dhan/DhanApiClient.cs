using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AlgoTrading.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Providers.Dhan;

/// <summary>Dhan's published request limits, as the classes a call belongs to.</summary>
public enum DhanRateClass
{
    /// <summary>Data APIs (history): 5 requests a second.</summary>
    Data,

    /// <summary>Market quote: 1 request a second.</summary>
    Quote,

    /// <summary>Option chain and its expiry list: one request every 3 seconds.</summary>
    OptionChain,
}

/// <summary>
/// Paces calls per <see cref="DhanRateClass"/>, process-wide. A singleton: the
/// limits are per account, so two requests on different scopes still count
/// against the same budget.
/// </summary>
public sealed class DhanRateGate
{
    private static readonly IReadOnlyDictionary<DhanRateClass, TimeSpan> Spacing = new Dictionary<DhanRateClass, TimeSpan>
    {
        // A little slower than the published limit, so clock jitter on either
        // side never turns a paced call into a refused one.
        [DhanRateClass.Data] = TimeSpan.FromMilliseconds(220),
        [DhanRateClass.Quote] = TimeSpan.FromMilliseconds(1100),
        [DhanRateClass.OptionChain] = TimeSpan.FromMilliseconds(3100),
    };

    private readonly Dictionary<DhanRateClass, SemaphoreSlim> _gates =
        Enum.GetValues<DhanRateClass>().ToDictionary(c => c, _ => new SemaphoreSlim(1, 1));

    private readonly Dictionary<DhanRateClass, DateTime> _lastUtc =
        Enum.GetValues<DhanRateClass>().ToDictionary(c => c, _ => DateTime.MinValue);

    public async Task WaitAsync(DhanRateClass rateClass, CancellationToken cancellationToken)
    {
        var gate = _gates[rateClass];
        await gate.WaitAsync(cancellationToken);
        try
        {
            var wait = _lastUtc[rateClass] + Spacing[rateClass] - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken);
            _lastUtc[rateClass] = DateTime.UtcNow;
        }
        finally
        {
            gate.Release();
        }
    }
}

/// <summary>A refusal from Dhan, with its error code when it sent one.</summary>
public sealed class DhanApiException : InvalidOperationException
{
    public DhanApiException(int httpStatus, string? code, string message) : base(message)
    {
        HttpStatus = httpStatus;
        Code = code;
    }

    /// <summary>The HTTP status; 0 when the request was never sent.</summary>
    public int HttpStatus { get; }

    /// <summary>Dhan's code, e.g. "DH-901", or the numeric key of a failed data answer, e.g. "814".</summary>
    public string? Code { get; }

    /// <summary>
    /// The token is missing, expired or revoked, or the client id is wrong:
    /// nothing else will work until it is replaced. DH-901 on the trading APIs;
    /// 807 (expired), 808 (authentication failed), 809 (invalid token) and 810
    /// (invalid client id) on the data APIs.
    /// </summary>
    public bool IsAuthFailure =>
        HttpStatus == 401 || Code is "DH-901" or "807" or "808" or "809" or "810";

    /// <summary>
    /// The account has no active data plan (DH-902 / 806). Distinct from an auth
    /// failure: a fresh token will not fix it, a subscription will.
    /// </summary>
    public bool IsNotSubscribed => Code is "DH-902" or "806";
}

/// <summary>
/// Every call to Dhan's REST API goes through here: the client id and access
/// token as headers, pacing, and one reading of both of Dhan's error shapes.
/// </summary>
public sealed class DhanApiClient
{
    private readonly DhanSettings _settings;
    private readonly IBrokerCredentialsProvider _credentials;
    private readonly IBrokerSessionStore _sessions;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DhanRateGate _gate;
    private readonly ILogger<DhanApiClient> _logger;

    // Dhan's request fields are case-sensitive and not uniform ("securityId" on
    // history, "UnderlyingScrip" on the option chain), so bodies are written
    // with the exact names and serialised without a naming policy.
    private static readonly JsonSerializerOptions BodyJson = new();

    public DhanApiClient(
        IOptions<DhanSettings> settings,
        IBrokerCredentialsProvider credentials,
        IBrokerSessionStore sessions,
        IHttpClientFactory httpClientFactory,
        DhanRateGate gate,
        ILogger<DhanApiClient> logger)
    {
        _settings = settings.Value;
        _credentials = credentials;
        _sessions = sessions;
        _httpClientFactory = httpClientFactory;
        _gate = gate;
        _logger = logger;
    }

    public Task<JsonDocument> PostAsync(string path, object body, DhanRateClass rateClass, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Post, path, body, rateClass, cancellationToken);

    public Task<JsonDocument> GetAsync(string path, DhanRateClass rateClass, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Get, path, null, rateClass, cancellationToken);

    private async Task<JsonDocument> SendAsync(
        HttpMethod method, string path, object? body, DhanRateClass rateClass, CancellationToken cancellationToken)
    {
        var (clientId, token) = await ResolveTokenAsync(cancellationToken);

        await _gate.WaitAsync(rateClass, cancellationToken);

        using var request = new HttpRequestMessage(method, $"{_settings.ApiBaseUrl.TrimEnd('/')}{path}");
        request.Headers.TryAddWithoutValidation("access-token", token.Value);
        request.Headers.TryAddWithoutValidation("client-id", clientId);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body, BodyJson), Encoding.UTF8, "application/json");
        }

        var http = _httpClientFactory.CreateClient(DhanProvider.Key);
        using var response = await http.SendAsync(request, cancellationToken);
        string text = await response.Content.ReadAsStringAsync(cancellationToken);
        int status = (int)response.StatusCode;

        if (!response.IsSuccessStatusCode)
        {
            var failure = Describe(status, text);
            _logger.LogWarning("Dhan {Method} {Path} failed: {Message}", method, path, failure.Message);
            throw failure;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "null" : text);
        }
        catch (JsonException)
        {
            throw new DhanApiException(status, null, $"Dhan answered {path} with something that is not JSON: {Trim(text)}");
        }

        // A 200 can still carry {"status":"failed"}: the data APIs report a bad
        // request that way as well as with a 400.
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String &&
            string.Equals(s.GetString(), "failed", StringComparison.OrdinalIgnoreCase))
        {
            document.Dispose();
            throw Describe(status, text);
        }

        return document;
    }

    /// <summary>Where the token in use came from, and until when it is good.</summary>
    public sealed record DhanToken(string Value, string Source, DateTime? ExpiresUtc);

    /// <summary>
    /// The client id and the token to call Dhan with: the session from the daily
    /// sign-in while it is valid, otherwise a token pasted into configuration.
    /// </summary>
    public async Task<(string ClientId, DhanToken Token)> ResolveTokenAsync(CancellationToken cancellationToken = default)
    {
        var creds = await _credentials.GetAsync(DhanProvider.Key, cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(creds.ClientId))
        {
            throw new DhanApiException(0, null,
                "Dhan has no client id. Add it to the Dhan section of appsettings.Local.json (ClientId) or on the Connectors page.");
        }

        var session = await _sessions.GetForProviderAsync(DhanProvider.Key, cancellationToken);
        if (session is { IsAuthenticated: true } && !string.IsNullOrWhiteSpace(session.AccessToken))
        {
            return (creds.ClientId.Trim(), new DhanToken(session.AccessToken, "sign-in", session.ExpiresAtUtc));
        }

        if (!string.IsNullOrWhiteSpace(_settings.AccessToken))
        {
            return (creds.ClientId.Trim(), new DhanToken(_settings.AccessToken.Trim(), "configuration", null));
        }

        throw new DhanApiException(401, null,
            "Dhan is not signed in. Press Connect on the Dhan connector page (once a day), or set Dhan:AccessToken.");
    }

    /// <summary>
    /// Reads either of Dhan's error shapes: {"errorType","errorCode","errorMessage"},
    /// or {"data":{"814":"Invalid Request"},"status":"failed"}.
    /// </summary>
    internal static DhanApiException Describe(int status, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("errorCode", out var code))
            {
                string c = code.ToString();
                string type = root.TryGetProperty("errorType", out var t) ? t.ToString() : string.Empty;
                string message = root.TryGetProperty("errorMessage", out var m) ? m.ToString() : string.Empty;
                return Build(status, c, $"{c} {type}: {message}".Trim());
            }

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in data.EnumerateObject())
                {
                    return Build(status, property.Name, $"{property.Name}: {property.Value}");
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON; the body itself is the message.
        }

        return Build(status, null, $"Dhan request failed ({status}): {Trim(body)}");
    }

    private static DhanApiException Build(int status, string? code, string message)
    {
        var failure = new DhanApiException(status, code, message);
        if (failure.IsAuthFailure)
        {
            return new DhanApiException(status, code,
                $"Dhan rejected the access token or client id ({message}). A console token lasts 24 hours; generate a new one.");
        }

        if (failure.IsNotSubscribed)
        {
            return new DhanApiException(status, code,
                $"The Dhan account has no active data plan ({message}). Market data needs the Data APIs subscription.");
        }

        return failure;
    }

    private static string Trim(string body) =>
        string.IsNullOrWhiteSpace(body) ? "no message" : (body.Length > 200 ? body[..200] : body);
}
