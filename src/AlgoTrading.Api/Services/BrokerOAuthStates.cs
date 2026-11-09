using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace AlgoTrading.Api.Services;

/// <summary>Short-lived, single-use shared-broker sign-ins. A restart safely discards pending flows.</summary>
public sealed class BrokerOAuthStates(TimeProvider? time = null)
{
    public const string CookieName = "openfno_broker_oauth";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    public sealed record Pending(long UserId, Guid SessionId, string ProviderKey, long IssuedAt, DateTimeOffset ExpiresAt, string BrowserHash);
    public sealed record Issued(string State, string BrowserSecret);

    public Issued Issue(long userId, Guid sessionId, string providerKey, long issuedAt, DateTimeOffset tokenExpiresAt)
    {
        var now = _time.GetUtcNow();
        if (tokenExpiresAt <= now) throw new InvalidOperationException("Sign in to the console again before connecting the broker.");
        foreach (var item in _pending.Where(p => p.Value.ExpiresAt <= now)) _pending.TryRemove(item.Key, out _);
        string state = "desk-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        string browser = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var expires = tokenExpiresAt < now + Lifetime ? tokenExpiresAt : now + Lifetime;
        _pending[Hash(state)] = new(userId, sessionId, providerKey, issuedAt, expires, Hash(browser));
        return new(state, browser);
    }

    public Pending? Consume(string? state, string? browserSecret)
    {
        if (state is not { Length: 69 } || !state.StartsWith("desk-", StringComparison.Ordinal) || browserSecret is not { Length: 64 }) return null;
        string key = Hash(state);
        if (!_pending.TryGetValue(key, out var pending) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(pending.BrowserHash), Encoding.ASCII.GetBytes(Hash(browserSecret)))) return null;
        // Check the cookie before removal: another browser cannot consume the owner's pending flow.
        if (!_pending.TryRemove(key, out pending) || pending.ExpiresAt <= _time.GetUtcNow()) return null;
        return pending;
    }

    /// <summary>A console login/logout in this browser invalidates its unfinished broker sign-ins.</summary>
    public void EndBrowserSession(HttpContext context)
    {
        string? secret = context.Request.Cookies[CookieName];
        if (secret is not { Length: 64 }) return;
        string hash = Hash(secret);
        foreach (var item in _pending.Where(p => p.Value.BrowserHash == hash)) _pending.TryRemove(item.Key, out _);
        // Do not send a deletion cookie here: a delayed logout response must not
        // erase a newer flow started in another tab. This secret is now useless
        // and expires naturally, or is replaced by the next Connect request.
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
