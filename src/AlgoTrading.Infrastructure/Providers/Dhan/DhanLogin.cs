using System.Text.Json;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.Providers;
using AlgoTrading.Domain.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Providers.Dhan;

/// <summary>What a completed Dhan sign-in produced.</summary>
public sealed record DhanSignIn(string ClientId, DateTime ExpiresUtc);

/// <summary>Why a sign-in produced no token.</summary>
public enum DhanSignInFailure
{
    /// <summary>A value it needs is missing or malformed. Retrying changes nothing until someone fixes it.</summary>
    NotSetUp,

    /// <summary>
    /// Dhan answered, and said no. Never retried by a machine: a PIN or code
    /// that is wrong now is wrong every time, and repeated wrong PINs can lock
    /// the account.
    /// </summary>
    Refused,

    /// <summary>Dhan was not reached, or failed on its side. Worth another try later.</summary>
    Unreachable,
}

/// <summary>A sign-in that did not produce a token, and why (<see cref="Failure"/>).</summary>
public sealed class DhanSignInException(string message, DhanSignInFailure failure) : InvalidOperationException(message)
{
    public DhanSignInFailure Failure { get; } = failure;
}

/// <summary>
/// Dhan's two ways to the day's 24-hour access token, both saved as the
/// connector's session:
/// <list type="bullet">
/// <item>the API-key browser flow behind Connect: generate a consent with the
/// API key and secret → the operator signs in on Dhan's site → Dhan redirects to
/// the callback with a tokenId → the tokenId is consumed for the token;</item>
/// <item>the PIN + TOTP sign-in (<see cref="SignInWithTotpAsync"/>), which needs
/// no browser and is what lets the desk sign itself in each morning.</item>
/// </list>
/// </summary>
/// <remarks>
/// Verified against Dhan's documentation and the live consent endpoint on
/// 2026-09-14. A consent can be generated 25 times a day, so a Connect that is
/// pressed and abandoned costs one of them and nothing else. The PIN + TOTP
/// call is from the same documentation, read again on 2026-09-27.
/// </remarks>
public sealed class DhanLoginFlow : IProviderLoginFlow
{
    /// <summary>
    /// The HTTP client for the PIN + TOTP call. Registered without request
    /// logging: Dhan takes the PIN in the query string, and a request line logged
    /// at Information would write it into api.log.
    /// </summary>
    public const string SignInClient = "dhan-sign-in";

    private readonly IOptionsMonitor<DhanSettings> _settingsMonitor;
    private readonly IBrokerCredentialsProvider _credentials;
    private readonly IBrokerSessionStore _sessions;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DhanLoginFlow> _logger;
    private readonly TimeProvider _time;

    public DhanLoginFlow(
        IOptionsMonitor<DhanSettings> settings,
        IBrokerCredentialsProvider credentials,
        IBrokerSessionStore sessions,
        IHttpClientFactory httpClientFactory,
        ILogger<DhanLoginFlow> logger,
        TimeProvider? time = null)
    {
        _settingsMonitor = settings;
        _credentials = credentials;
        _sessions = sessions;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    // Read on every call rather than once: appsettings.Local.json is reloaded
    // when it changes, so a PIN added to .env and regenerated is picked up
    // without restarting the API in the middle of a session.
    private DhanSettings Settings => _settingsMonitor.CurrentValue;

    public string ProviderKey => DhanProvider.Key;

    public async Task<string> GetLoginUrlAsync(CancellationToken cancellationToken = default)
    {
        var (clientId, apiKey, apiSecret) = await AppCredentialsAsync(cancellationToken);

        using var root = await PostAsync(
            $"/app/generate-consent?client_id={Uri.EscapeDataString(clientId)}", apiKey, apiSecret, cancellationToken);

        string consentId = root.RootElement.TryGetProperty("consentAppId", out var id) ? id.GetString() ?? string.Empty : string.Empty;
        if (string.IsNullOrWhiteSpace(consentId))
            throw new InvalidOperationException("Dhan generated no consent; check the API key and secret.");

        return $"{Settings.AuthBaseUrl.TrimEnd('/')}/login/consentApp-login?consentAppId={Uri.EscapeDataString(consentId)}";
    }

    /// <summary>
    /// Turns the tokenId from Dhan's redirect into a session. Refuses a sign-in
    /// to any Dhan account other than the configured client id: the callback is
    /// open to the browser, so the account it accepts must be pinned.
    /// </summary>
    public async Task<DhanSignIn> CompleteAsync(string tokenId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tokenId))
            throw new InvalidOperationException("Dhan's redirect carried no tokenId.");

        var (clientId, apiKey, apiSecret) = await AppCredentialsAsync(cancellationToken);

        using var root = await PostAsync(
            $"/app/consumeApp-consent?tokenId={Uri.EscapeDataString(tokenId)}", apiKey, apiSecret, cancellationToken);
        var r = root.RootElement;

        string token = r.TryGetProperty("accessToken", out var t) ? t.GetString() ?? string.Empty : string.Empty;
        string signedInAs = r.TryGetProperty("dhanClientId", out var c) ? c.ToString() : string.Empty;
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Dhan returned no access token for this sign-in.");

        return await SaveAsync(clientId, signedInAs, token, "the browser sign-in", cancellationToken);
    }

    /// <summary>
    /// The day's token from the account's PIN and a TOTP code, with no browser:
    /// <c>POST /app/generateAccessToken?dhanClientId=…&amp;pin=…&amp;totp=…</c>.
    /// Saved exactly where Connect saves its token, so the feed and every call
    /// pick it up the same way.
    /// </summary>
    /// <exception cref="DhanSignInException">No token: not set up, refused, or Dhan not reached.</exception>
    public Task<DhanSignIn> SignInWithTotpAsync(CancellationToken cancellationToken = default) =>
        SignInWithTotpAsync(lastCodeStep: null, codeSent: null, cancellationToken);

    /// <summary>
    /// <see cref="SignInWithTotpAsync(CancellationToken)"/>, never sending a code
    /// from <paramref name="lastCodeStep"/> (the TOTP step of the code sent last)
    /// and telling <paramref name="codeSent"/> which step it sends, just before it does.
    /// </summary>
    public async Task<DhanSignIn> SignInWithTotpAsync(long? lastCodeStep, Action<long>? codeSent, CancellationToken cancellationToken = default)
    {
        var settings = Settings;
        var creds = await _credentials.GetAsync(DhanProvider.Key, cancellationToken: cancellationToken);
        string clientId = (creds.ClientId ?? string.Empty).Trim();
        string pin = settings.Pin.Trim();
        string secret = settings.TotpSecret.Trim();

        var missing = new List<string>();
        if (clientId.Length == 0) missing.Add("DHAN_CLIENT_ID");
        if (pin.Length == 0) missing.Add("DHAN_PIN");
        if (secret.Length == 0) missing.Add("DHAN_TOTP_SECRET");
        if (missing.Count > 0)
        {
            throw new DhanSignInException(
                $"The automatic Dhan sign-in is not set up: {string.Join(", ", missing)} missing in the server's .env.", DhanSignInFailure.NotSetUp);
        }

        try
        {
            Totp.FromBase32(secret, "DHAN_TOTP_SECRET");
        }
        catch (ArgumentException)
        {
            // Not the parser's own text: it quotes the character it choked on,
            // which is a piece of the secret, and this message goes to Telegram.
            throw new DhanSignInException(
                "DHAN_TOTP_SECRET is not base32 text. Copy it again from Dhan's Setup TOTP page (the text behind the QR code).",
                DhanSignInFailure.NotSetUp);
        }

        // Not a code about to roll over, nor one from the step sent last (WaitBeforeCode).
        var wait = WaitBeforeCode(_time.GetUtcNow(), lastCodeStep);
        if (wait > TimeSpan.Zero) await Task.Delay(wait, _time, cancellationToken);
        var now = _time.GetUtcNow();

        string url = $"{settings.AuthBaseUrl.TrimEnd('/')}/app/generateAccessToken" +
                     $"?dhanClientId={Uri.EscapeDataString(clientId)}&pin={Uri.EscapeDataString(pin)}&totp={Totp.Generate(secret, now)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        int status;
        string body;
        try
        {
            var http = _httpClientFactory.CreateClient(SignInClient);
            // Counted as sent before it goes, even if it never arrives: one that
            // did arrive must not be sent a second time.
            codeSent?.Invoke(CodeStep(now));
            using var response = await http.SendAsync(request, cancellationToken);
            status = (int)response.StatusCode;
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !cancellationToken.IsCancellationRequested)
        {
            // The exception's own text is not repeated: it can carry the request.
            throw new DhanSignInException($"Dhan's sign-in service could not be reached ({ex.GetType().Name}).", DhanSignInFailure.Unreachable);
        }

        string token = string.Empty, signedInAs = string.Empty, reason = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var r = doc.RootElement;
            if (r.ValueKind == JsonValueKind.Object)
            {
                token = r.TryGetProperty("accessToken", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                signedInAs = r.TryGetProperty("dhanClientId", out var c) ? c.ToString() : string.Empty;
                reason = RefusalReason(r);
            }
        }
        catch (JsonException)
        {
            // Not JSON: judged by the status alone below.
        }

        // 408 and 429 say "not now", not "wrong PIN": marking them refused would
        // end the day's automatic tries over a busy minute at Dhan.
        if (status >= 500 || status is 408 or 429)
            throw new DhanSignInException($"Dhan's sign-in service failed ({status}). It will be tried again.", DhanSignInFailure.Unreachable);

        if (status >= 400 || string.IsNullOrWhiteSpace(token))
        {
            // Only fields Dhan uses for a reason are repeated, never the body:
            // it answers the request, and the request carried the PIN.
            throw new DhanSignInException(
                $"Dhan refused the PIN + TOTP sign-in ({status}{(reason.Length > 0 ? $": {reason}" : string.Empty)}). " +
                "Check DHAN_PIN and DHAN_TOTP_SECRET, and that TOTP is enabled for the account's API access.",
                DhanSignInFailure.Refused);
        }

        try
        {
            return await SaveAsync(clientId, signedInAs, token, "PIN + TOTP", cancellationToken);
        }
        catch (Exception ex) when (ex is not DhanSignInException && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // Dhan said yes; the database or the key ring said no. Not a refusal:
            // it used to be one whenever the failure was an InvalidOperationException,
            // which is how EF reports a transient database failure, so a blip
            // stopped the day's tries as if the PIN were wrong. Any other type
            // escaped with no failure recorded and no pause, and the worker would
            // have taken a new token every minute. The exception is logged here,
            // where it can only have come from the save (never near the PIN), and
            // only its type goes on to Telegram.
            _logger.LogError(ex, "Dhan issued a token (PIN + TOTP) but it could not be saved.");
            throw new DhanSignInException(
                $"Dhan issued a token but it could not be saved: {ex.GetType().Name}. It will be tried again.", DhanSignInFailure.Unreachable);
        }
    }

    /// <summary>
    /// How long to wait before reading the code, and so which 30-second TOTP
    /// step it comes from. Pure, so the rule is tested without a clock.
    /// </summary>
    /// <remarks>
    /// Two reasons to wait for the next step, each to one second into it:
    /// <list type="bullet">
    /// <item>the step is in its last seconds: the code can roll over by the time
    /// Dhan checks it, and a refused code is not retried;</item>
    /// <item>the code sent last came from this step. The worker and the morning
    /// job, a moment apart, could each send the same code. A TOTP code is meant
    /// to be accepted once (RFC 6238, section 5.2), so Dhan may refuse the second
    /// as a wrong one, and a refusal stops the day.</item>
    /// </list>
    /// </remarks>
    public static TimeSpan WaitBeforeCode(DateTimeOffset now, long? lastCodeStep)
    {
        long seconds = now.ToUnixTimeSeconds();
        long at = seconds;
        if (seconds % TotpStepSeconds >= 26) at = NextStepStart(seconds / TotpStepSeconds);
        if (lastCodeStep is { } last && at / TotpStepSeconds == last) at = NextStepStart(last);
        return TimeSpan.FromSeconds(at - seconds);

        static long NextStepStart(long step) => (step + 1) * TotpStepSeconds + 1;
    }

    /// <summary>The TOTP step a code read at <paramref name="at"/> belongs to.</summary>
    public static long CodeStep(DateTimeOffset at) => at.ToUnixTimeSeconds() / TotpStepSeconds;

    private const int TotpStepSeconds = 30;

    /// <summary>
    /// Saves the token as the connector's session, refusing one for any Dhan
    /// account other than the configured client id.
    /// </summary>
    private async Task<DhanSignIn> SaveAsync(string clientId, string signedInAs, string token, string how, CancellationToken cancellationToken)
    {
        if (!string.Equals(signedInAs, clientId, StringComparison.Ordinal))
        {
            // A DhanSignInException is still an InvalidOperationException, which
            // is what the browser callback catches.
            throw new DhanSignInException(
                $"Signed in to Dhan as client {Mask(signedInAs)}, but this connector is set up for {Mask(clientId)}. Nothing was saved.",
                DhanSignInFailure.Refused);
        }

        await _sessions.SaveAsync(new BrokerSession
        {
            ProviderKey = DhanProvider.Key,
            BrokerName = "DHAN",
            AccessToken = token,
            RefreshToken = string.Empty,
        }, cancellationToken);

        var expires = BrokerSession.TokenExpiryUtc(DhanProvider.Key, null, _time.GetUtcNow().UtcDateTime)!.Value;
        _logger.LogInformation("Dhan signed in ({How}) for client {Client}; token valid until {Expires:u}.", how, Mask(clientId), expires);
        return new DhanSignIn(clientId, expires);
    }

    /// <summary>The reason in a refusal, from the fields Dhan's error bodies use.</summary>
    private static string RefusalReason(JsonElement root)
    {
        var parts = new List<string>();
        foreach (string name in new[] { "errorCode", "errorMessage", "message", "remarks" })
        {
            if (root.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            {
                string text = v.ToString().Trim();
                if (text.Length > 0 && !parts.Contains(text)) parts.Add(text.Length > 160 ? text[..160] + "…" : text);
            }
        }
        return string.Join(" — ", parts);
    }

    private async Task<(string ClientId, string ApiKey, string ApiSecret)> AppCredentialsAsync(CancellationToken cancellationToken)
    {
        var creds = await _credentials.GetAsync(DhanProvider.Key, cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(creds.ClientId) || string.IsNullOrWhiteSpace(creds.SecretKey) || string.IsNullOrWhiteSpace(Settings.ApiKey))
        {
            throw new InvalidOperationException(
                "Dhan sign-in needs the client id, the API key and the API secret (Dhan section of appsettings.Local.json: ClientId, ApiKey, ApiSecret).");
        }

        return (creds.ClientId.Trim(), Settings.ApiKey.Trim(), creds.SecretKey.Trim());
    }

    private async Task<JsonDocument> PostAsync(string pathAndQuery, string apiKey, string apiSecret, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Settings.AuthBaseUrl.TrimEnd('/')}{pathAndQuery}");
        request.Headers.TryAddWithoutValidation("app_id", apiKey);
        request.Headers.TryAddWithoutValidation("app_secret", apiSecret);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        var http = _httpClientFactory.CreateClient(DhanProvider.Key);
        using var response = await http.SendAsync(request, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // Not DhanApiClient's wording: on these two calls a 401 means the
            // consent or the sign-in was not accepted, not that a data token expired.
            var failure = DhanApiClient.Describe((int)response.StatusCode, body);
            string code = failure.Code is null ? string.Empty : $", {failure.Code}";
            throw new InvalidOperationException(
                $"Dhan did not accept the sign-in ({(int)response.StatusCode}{code}). Press Connect again; if it keeps failing, check the API key and secret.");
        }

        try
        {
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"Dhan's sign-in service answered with something that is not JSON ({(int)response.StatusCode}).");
        }
    }

    /// <summary>"1113706926" → "111…926": enough to tell accounts apart in a message or log.</summary>
    internal static string Mask(string clientId) =>
        clientId.Length <= 6 ? "…" : $"{clientId[..3]}…{clientId[^3..]}";
}
