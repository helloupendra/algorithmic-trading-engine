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

    /// <summary>
    /// OS process id of the live data ingestor (fyers_streamer). Written at
    /// launch and confirmed by its heartbeat; deleted on a clean stop/exit.
    /// </summary>
    public const string IngestorPid = "ingestor.pid";

    private const string StrategyRunPidPrefix = "strategyrun.";
    private const string BacktestRunPidPrefix = "backtestrun.";
    private const string PidSuffix = ".pid";

    /// <summary>"strategyrun.&lt;runId&gt;.pid": the execution runner's process id for a LivePaper run.</summary>
    public static string StrategyRunPid(long runId) => $"{StrategyRunPidPrefix}{runId}{PidSuffix}";

    /// <summary>"backtestrun.&lt;runId&gt;.pid": the backtest runner's process id for an OfflineReplay run.</summary>
    public static string BacktestRunPid(long runId) => $"{BacktestRunPidPrefix}{runId}{PidSuffix}";
}
