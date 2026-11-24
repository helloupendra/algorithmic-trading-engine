namespace AlgoTrading.Contracts.LiveData;

/// <summary>
/// The recording list's priorities (<c>live_watchlist.Priority</c>). The feed subscribes the higher ones first, and on
/// a socket with a symbol limit (FYERS, 100) a symbol a person asked for never takes the place of one the desk needs.
/// </summary>
public static class WatchlistPriorities
{
    /// <summary>A contract a running strategy resolved.</summary>
    public const int RunContract = 100;

    /// <summary>The desk's own: a carried position, an admin's ticket or list.</summary>
    public const int Desk = 10;

    /// <summary>The market pulse screen's indices, large caps and commodities.</summary>
    public const int MarketPulse = 5;

    /// <summary>
    /// A symbol a trader asked for (a manual-order ticket, their own watchlist). The feed offers these last
    /// (<c>feed_runner.py</c>, <c>REQUESTED_MAX_PRIORITY</c>), so they fill only the room the desk leaves.
    /// </summary>
    public const int PersonRequested = 1;
}
