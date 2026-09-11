using System.Text.Json;
using AlgoTrading.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Providers.TrueData;

/// <summary>
/// Holds the TrueData REST bearer token and renews it when it is close to dying.
/// </summary>
/// <remarks>
/// Registered as a singleton so one token serves the whole process. That matters
/// for the same reason it mattered in the desk scripts on 2026-09-11: a token
/// fetched per request is a sign-in per request, and a vendor that counts logins
/// will eventually say no in the middle of a backfill.
///
/// <para>Unlike the FYERS token, nothing here needs a person. The username and
/// password are exchanged for a bearer token over REST, so an expiry is a thing
/// this class fixes by itself rather than a thing the morning waits for.</para>
/// </remarks>
public sealed class TrueDataTokenStore
{
    /// <summary>
    /// Renewed this far before the vendor's own expiry, so a long call started
    /// just under the wire does not finish on a dead token.
    /// </summary>
    private static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(5);

    private readonly IBrokerCredentialsProvider _credentials;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TrueDataSettings _settings;
    private readonly ILogger<TrueDataTokenStore> _logger;

    // One renewal at a time: a burst of backfill calls on a cold store would
    // otherwise all miss and sign in together.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTime _expiresUtc = DateTime.MinValue;

    public TrueDataTokenStore(
        IOptions<TrueDataSettings> settings,
        IBrokerCredentialsProvider credentials,
        IHttpClientFactory httpClientFactory,
        ILogger<TrueDataTokenStore> logger)
    {
        _settings = settings.Value;
        _credentials = credentials;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// A usable bearer token, fetching one if the cached token is missing or
    /// nearly expired.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No credentials are saved, or TrueData refused them. Thrown rather than
    /// returning null so a caller cannot mistake "not signed in" for "no data".
    /// </exception>
    public async Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        if (IsFresh()) return _token!;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Another caller may have renewed while this one waited.
            if (IsFresh()) return _token!;

            var creds = await _credentials.GetAsync(TrueDataProvider.Key, cancellationToken: cancellationToken);
            if (string.IsNullOrWhiteSpace(creds.ClientId) || string.IsNullOrWhiteSpace(creds.SecretKey))
            {
                throw new InvalidOperationException(
                    "TrueData has no credentials saved. Add the username and password on the Connectors page.");
            }

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = creds.ClientId,
                ["password"] = creds.SecretKey,
                ["grant_type"] = "password",
            });

            var http = _httpClientFactory.CreateClient(TrueDataProvider.Key);
            using var response = await http.PostAsync(
                $"{_settings.AuthBaseUrl.TrimEnd('/')}/token", content, cancellationToken);

            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // The vendor's own words, trimmed: a wrong password and an
                // expired subscription are different problems and the operator
                // needs to be told which.
                throw new InvalidOperationException(
                    $"TrueData refused the sign-in ({(int)response.StatusCode}): {Describe(body)}");
            }

            using var json = JsonDocument.Parse(body);
            string? token = json.RootElement.TryGetProperty("access_token", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("TrueData returned no access token.");

            int expiresIn = json.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out int seconds)
                ? seconds
                : 3600;

            _token = token;
            _expiresUtc = DateTime.UtcNow.AddSeconds(expiresIn);
            _logger.LogInformation(
                "TrueData token obtained, valid until {ExpiresUtc:u} ({Hours:0.#} h).",
                _expiresUtc, expiresIn / 3600.0);

            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forgets the cached token, so the next call signs in again.</summary>
    public void Invalidate()
    {
        _token = null;
        _expiresUtc = DateTime.MinValue;
    }

    private bool IsFresh() =>
        !string.IsNullOrWhiteSpace(_token) && DateTime.UtcNow < _expiresUtc - RenewBefore;

    /// <summary>The vendor's message when it sent one, otherwise the raw body.</summary>
    private static string Describe(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "no message";
        try
        {
            using var json = JsonDocument.Parse(body);
            foreach (var name in new[] { "error_description", "error", "message" })
            {
                if (json.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString() ?? body;
            }
        }
        catch (JsonException)
        {
            // Not JSON; the body itself is the message.
        }

        return body.Length > 200 ? body[..200] : body;
    }
}
