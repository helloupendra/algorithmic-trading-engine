using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace AlgoTrading.Api.Services.Portfolio;

/// <summary>Whom a broker's redirect belongs to, or why it is refused.</summary>
public sealed record PortfolioLinkCheck(long? UserId, string? Refusal)
{
    public static PortfolioLinkCheck Bound(long userId) => new(userId, null);

    public static PortfolioLinkCheck Refused(string why) => new(null, why);
}

/// <summary>
/// The one-time states that tie a broker's redirect back to the user who pressed Connect, in the browser they
/// pressed it in. A singleton: the pending sign-ins live in memory for ten minutes.
/// </summary>
/// <remarks>
/// <para>
/// The callback is anonymous by necessity (the broker's redirect carries no token of ours), so the state is the only
/// thing that says whose account is being linked. Two bindings, both required:
/// </para>
/// <list type="bullet">
/// <item>the state is a random value issued to the signed-in user for one broker, used once, gone after ten minutes;</item>
/// <item>the same value is set as an HttpOnly cookie on the browser that started the link, and the callback must
/// carry it. Without this, someone could start a link on their own account and send another person the broker's
/// sign-in page: that person's broker account would be linked to the sender's OpenFNO login, and the sender would
/// read their holdings. With it, a redirect that arrives in any other browser is refused.</item>
/// </list>
/// <para>
/// The state the broker sends back must equal the cookie. FYERS always returns it; Angel One's publisher login has
/// been reported to drop it, so for Angel One a missing state is accepted on the cookie alone, never a different one.
/// A restart forgets pending links; the user presses Connect again.
/// </para>
/// </remarks>
public sealed class PortfolioLinkStates(TimeProvider? time = null)
{
    public const string Prefix = "pf-";

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>The cookie's path: it is sent to the callbacks and nowhere else.</summary>
    public const string CookiePath = "/api/Portfolio/callback";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    private sealed record Pending(long UserId, string Broker, DateTime ExpiresUtc);

    /// <summary>The cookie that carries a pending link's state for one broker.</summary>
    public static string CookieName(string broker) => $"pf_link_{broker}";

    /// <summary>Whether a state from a broker's redirect is a portfolio link's (and not the platform's own sign-in).</summary>
    public static bool IsPortfolioState(string? state) => state?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    /// <summary>A new state for <paramref name="userId"/> linking <paramref name="broker"/>.</summary>
    public string Issue(long userId, string broker)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var stale in _pending.Where(p => p.Value.ExpiresUtc <= now).Select(p => p.Key).ToList()) _pending.TryRemove(stale, out _);

        string state = Prefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        _pending[state] = new Pending(userId, broker, now + Lifetime);
        return state;
    }

    /// <summary>
    /// The user a redirect belongs to, consuming its state; or why it is refused.
    /// </summary>
    /// <param name="fromBroker">The state in the broker's redirect.</param>
    /// <param name="fromCookie">The state in this browser's cookie.</param>
    /// <param name="brokerEchoesState">The broker always returns the state, so its absence is a forgery.</param>
    public PortfolioLinkCheck Consume(string broker, string? fromBroker, string? fromCookie, bool brokerEchoesState)
    {
        const string again = "Press Connect again on the Portfolio page.";
        if (string.IsNullOrWhiteSpace(fromCookie) || !IsPortfolioState(fromCookie))
        {
            return PortfolioLinkCheck.Refused($"This sign-in was not started in this browser, or it took longer than {Lifetime.TotalMinutes:0} minutes. {again}");
        }

        if (string.IsNullOrWhiteSpace(fromBroker))
        {
            if (brokerEchoesState) return PortfolioLinkCheck.Refused($"The broker's answer carried no sign-in state. {again}");
        }
        else if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(fromBroker), Encoding.UTF8.GetBytes(fromCookie)))
        {
            // Someone else's state in this browser: the pending link stays for its owner to finish.
            return PortfolioLinkCheck.Refused($"This sign-in does not match the one started in this browser. {again}");
        }

        if (!_pending.TryRemove(fromCookie, out var pending))
        {
            return PortfolioLinkCheck.Refused($"This sign-in has expired or was already used. {again}");
        }

        if (pending.ExpiresUtc <= _time.GetUtcNow().UtcDateTime)
        {
            return PortfolioLinkCheck.Refused($"This sign-in took longer than {Lifetime.TotalMinutes:0} minutes. {again}");
        }

        return pending.Broker == broker
            ? PortfolioLinkCheck.Bound(pending.UserId)
            : PortfolioLinkCheck.Refused($"This sign-in was started for another broker. {again}");
    }
}

/// <summary>Once a minute per user: a sync reads every linked account, and the brokers limit their APIs.</summary>
public sealed class PortfolioSyncGate(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<long, DateTime> _last = new();
    private readonly object _lock = new();

    /// <summary>True and the slot taken; false with how long to wait.</summary>
    public bool TryEnter(long userId, TimeSpan interval, out TimeSpan retryAfter)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        lock (_lock)
        {
            if (_last.TryGetValue(userId, out var last) && now - last < interval)
            {
                retryAfter = interval - (now - last);
                return false;
            }

            _last[userId] = now;
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }
}
