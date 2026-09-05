namespace AlgoTrading.Domain.Entities;

/// <summary>
/// A connection to one broker. <see cref="UserId"/> is what makes this both the
/// single-account and the multi-trader model at once:
/// <list type="bullet">
/// <item><description><c>null</c> — the shared platform account, which is how the
/// installation behaves today: one login, used by the ingestor and by everyone.</description></item>
/// <item><description>a user id — that trader's own broker account, used only for
/// their own orders.</description></item>
/// </list>
/// Building the column now means per-trader accounts arrive later as a feature,
/// not as a migration of tables that are full of live positions by then.
/// </summary>
public class BrokerAccount
{
    public long Id { get; set; }

    /// <summary>Provider key, e.g. "fyers". Lowercase, matches the descriptor.</summary>
    public string ProviderKey { get; set; } = string.Empty;

    /// <summary>Null for the shared platform account; otherwise the owning trader.</summary>
    public long? UserId { get; set; }

    /// <summary>Operator-facing name, e.g. "Platform (FYERS)".</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>A disabled account is never routed to, and its session is not refreshed.</summary>
    public bool IsEnabled { get; set; } = true;

    public string CreatedBy { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
