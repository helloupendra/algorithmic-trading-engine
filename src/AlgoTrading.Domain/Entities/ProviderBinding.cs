namespace AlgoTrading.Domain.Entities;

/// <summary>
/// Which connector serves which job, and in what order. One row per
/// (capability, segment, provider); the lowest <see cref="Priority"/> wins and
/// the rest are the failover chain.
/// </summary>
/// <remarks>
/// Empty table means "use the only provider that claims the capability", which is
/// exactly how a fresh install behaves before anyone opens the console.
/// </remarks>
public class ProviderBinding
{
    public long Id { get; set; }

    /// <summary>Name of the <c>ProviderCapability</c> value, e.g. "History".</summary>
    public string Capability { get; set; } = string.Empty;

    /// <summary>Segment this binding covers ("CM", "FO", "MCX"), or null for all of them.</summary>
    public string? Segment { get; set; }

    /// <summary>Provider key, e.g. "fyers".</summary>
    public string ProviderKey { get; set; } = string.Empty;

    /// <summary>Lower wins. 0 is the primary.</summary>
    public int Priority { get; set; }

    public bool IsEnabled { get; set; } = true;

    public string UpdatedBy { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
