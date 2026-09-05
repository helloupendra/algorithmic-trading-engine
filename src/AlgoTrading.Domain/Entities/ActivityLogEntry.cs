namespace AlgoTrading.Domain.Entities;

/// <summary>
/// One thing somebody did. The platform's answer to "who changed this, and when".
/// </summary>
/// <remarks>
/// Written for every request that changes something, by whoever made it — admin,
/// trader or the engine's own account. It is deliberately separate from
/// <see cref="RiskEvent"/> and <see cref="AlertEvent"/>: those record what the
/// <em>platform</em> decided, this records what a <em>person</em> asked for.
/// <para>
/// The username and role are copied in rather than joined: an account can be
/// deleted, and the history of what it did must still read.
/// </para>
/// </remarks>
public class ActivityLogEntry
{
    public long Id { get; set; }

    public DateTime OccurredUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Null for an anonymous request — a failed sign-in, an OAuth callback.</summary>
    public long? UserId { get; set; }

    /// <summary>Copied at write time so a deleted account's trail stays readable.</summary>
    public string UserName { get; set; } = "anonymous";

    public string Role { get; set; } = string.Empty;

    /// <summary>Which part of the platform: "strategies", "data", "users"…</summary>
    public string Module { get; set; } = string.Empty;

    /// <summary>What was asked for, e.g. "deploy-run", "save-credentials".</summary>
    public string Action { get; set; } = string.Empty;

    public string Method { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public int StatusCode { get; set; }

    public int DurationMs { get; set; }

    /// <summary>True for 2xx and 3xx. A refusal is still worth recording.</summary>
    public bool Succeeded { get; set; }

    /// <summary>What it acted on: "run", "user", "package", "provider"…</summary>
    public string? TargetType { get; set; }

    public string? TargetId { get; set; }

    /// <summary>
    /// A sentence a person can read, set by the endpoint itself when the path and
    /// status alone would not say enough.
    /// </summary>
    public string? Summary { get; set; }

    /// <summary>
    /// Where the request came from. Useful when an account behaves unexpectedly.
    /// </summary>
    public string? IpAddress { get; set; }
}
