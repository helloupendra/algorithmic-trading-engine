namespace AlgoTrading.Contracts.LiveData;

/// <summary>A recording-list row that is no longer worth a subscription, and why.</summary>
public class StaleWatchlistItem
{
    public long Id { get; set; }
    public string Symbol { get; set; } = string.Empty;
    /// <summary>"expired" (the contract's expiry is behind us) or "silent" (no tick this session while the feed flows).</summary>
    public string Reason { get; set; } = string.Empty;
    /// <summary>The sentence the page shows.</summary>
    public string Detail { get; set; } = string.Empty;
    public string? ExpiryDate { get; set; }
    public DateTime? LastTickUtc { get; set; }
}

public class StaleWatchlistResponse
{
    /// <summary>True when some watchlist symbol ticked in the last five minutes — the only time "silent" means anything.</summary>
    public bool FeedAlive { get; set; }
    /// <summary>Start of the session the "silent" test is measured from (IST day, 09:00), or null when no session has started today.</summary>
    public DateTime? SessionStartUtc { get; set; }
    public List<StaleWatchlistItem> Items { get; set; } = new();
}

public class PruneWatchlistRequest
{
    /// <summary>Restrict the prune to these rows; empty removes every stale row.</summary>
    public List<long>? Ids { get; set; }
}

public class PruneWatchlistResponse
{
    public List<StaleWatchlistItem> Removed { get; set; } = new();
    public string Message { get; set; } = string.Empty;
}
