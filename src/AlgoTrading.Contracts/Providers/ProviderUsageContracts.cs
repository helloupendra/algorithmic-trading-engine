namespace AlgoTrading.Contracts.Providers;

/// <summary>
/// What the platform is taking from one connector right now, item by item.
/// Returned by <c>GET /api/Providers/{key}/usage</c>.
/// </summary>
public class ProviderUsageResponse
{
    public string ProviderKey { get; set; } = string.Empty;

    /// <summary>When these answers were read.</summary>
    public DateTime CheckedUtc { get; set; }

    public ProviderUsageMarketsResponse Markets { get; set; } = new();

    /// <summary>session, liveTicks, quotes, depth, openInterest, greeks, optionChain, history, instruments, orders — in that order.</summary>
    public List<ProviderUsageItemResponse> Items { get; set; } = new();
}

/// <summary>The exchange groups the items are judged against.</summary>
public class ProviderUsageMarketsResponse
{
    public ProviderUsageMarketResponse Nse { get; set; } = new();
    public ProviderUsageMarketResponse Mcx { get; set; } = new();
}

public class ProviderUsageMarketResponse
{
    /// <summary>Null when the session could not be read — not known is not closed.</summary>
    public bool? Open { get; set; }

    /// <summary>Today's holiday, when there is one.</summary>
    public string? Holiday { get; set; }
}

/// <summary>One kind of data and where it stands.</summary>
public class ProviderUsageItemResponse
{
    /// <summary>Stable id: "session", "liveTicks", "quotes", ….</summary>
    public string Id { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    /// <summary>Whether the connector declares this kind of data.</summary>
    public bool Offered { get; set; }

    /// <summary>"on", "idle", "off", "unknown" or "not-offered".</summary>
    public string State { get; set; } = "unknown";

    public string Summary { get; set; } = string.Empty;

    /// <summary>When the newest datum behind this item landed, when there is one.</summary>
    public DateTime? LastUtc { get; set; }
}
