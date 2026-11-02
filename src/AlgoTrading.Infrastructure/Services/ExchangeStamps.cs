namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// How far a feed's exchange stamp is believed. A stamp orders a contract's ticks
/// and names the minute of its bar, so a wrong one does lasting harm: the latest
/// quote refuses anything older than the stamp it holds, and one stamp from the
/// future freezes that quote until the clock catches up.
/// </summary>
/// <remarks>
/// That was a real path, not a theoretical one: a Dhan feed restarted during the
/// session stamped a contract's 10:00 snapshot 15:30, and every real tick of that
/// contract was then refused for the rest of the day, while the quote's
/// <c>UpdatedUtc</c> stopped moving (found 2 Nov). The feed no longer does that;
/// this is what stops the next vendor surprise doing the same.
/// </remarks>
public static class ExchangeStamps
{
    /// <summary>
    /// How far ahead of our clock a stamp may be before it is called wrong. The
    /// two clocks need not agree to the second; no exchange prints minutes ahead.
    /// </summary>
    public static readonly TimeSpan MaxAhead = TimeSpan.FromMinutes(2);

    public static bool IsAhead(DateTime? stampUtc, DateTime nowUtc)
        => stampUtc is { } stamp && stamp.ToUniversalTime() > nowUtc + MaxAhead;

    /// <summary>
    /// The stamp to act on: none, for one from the future, which then counts as a
    /// tick the feed did not stamp. A replay's clock is its own and is never judged.
    /// </summary>
    public static DateTime? Usable(DateTime? stampUtc, bool isReplay, DateTime nowUtc)
        => !isReplay && IsAhead(stampUtc, nowUtc) ? null : stampUtc;

    /// <summary>
    /// Whether a tick gives way to the quote on record because it is older. A
    /// stored stamp from the future orders nothing, so it never blocks a tick.
    /// </summary>
    public static bool IsOlderThanStored(DateTime? incomingUtc, DateTime? storedUtc, bool isReplay, DateTime nowUtc)
        => !isReplay
           && incomingUtc is { } incoming
           && storedUtc is { } stored
           && incoming.ToUniversalTime() < stored.ToUniversalTime()
           && !IsAhead(storedUtc, nowUtc);

    /// <summary>The stamp a quote keeps once a tick is applied; an unstamped tick does not keep a bad one alive.</summary>
    public static DateTime? Applied(DateTime? incomingUtc, DateTime? storedUtc, DateTime nowUtc)
        => incomingUtc ?? (IsAhead(storedUtc, nowUtc) ? null : storedUtc);
}
