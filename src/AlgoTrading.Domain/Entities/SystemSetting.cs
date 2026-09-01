namespace AlgoTrading.Domain.Entities;

/// <summary>
/// A durable key/value flag for platform-wide state that must survive a restart.
///
/// This exists because safety-critical switches cannot live in process memory: an
/// API restart would silently reset them to their default. The global kill switch
/// is the motivating case — if an operator halts trading and the process recycles,
/// trading must stay halted until someone explicitly re-enables it.
/// </summary>
public class SystemSetting
{
    /// <summary>
    /// Primary key.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Unique setting name. Use the constants on <see cref="SystemSettingKeys"/>.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Raw value. Booleans are stored as "true"/"false".
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Username of whoever last changed the value, for the admin audit trail.
    /// </summary>
    public string? UpdatedBy { get; set; }

    /// <summary>
    /// Optional operator note explaining why the value was changed.
    /// </summary>
    public string? Reason { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Well-known <see cref="SystemSetting.Key"/> values.
/// </summary>
public static class SystemSettingKeys
{
    /// <summary>
    /// When "true", every order is rejected platform-wide.
    /// </summary>
    public const string KillSwitchActive = "risk.killswitch.active";
}
