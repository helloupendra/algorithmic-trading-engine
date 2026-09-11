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
    public const string KillSwitchActive = "risk.killswitch.active";
    
    public const string MaxOrdersPerMinute = "risk.limits.maxOrdersPerMinute";
    public const string MaxDailyLoss = "risk.limits.maxDailyLoss";
    public const string MaxConcurrentRuns = "risk.limits.maxConcurrentRuns";
    public const string MaxRunsPerUser = "risk.limits.maxRunsPerUser";

    /// <summary>
    /// OS process id of the FYERS live feed. Written at launch and confirmed by
    /// its heartbeat; deleted on a clean stop/exit. Older than
    /// <see cref="FeedPid"/> and kept under this name so a feed running across
    /// the deploy that introduced per-vendor keys is still found.
    /// </summary>
    public const string IngestorPid = "ingestor.pid";

    /// <summary>The option-chain poller — the only source of open interest.</summary>
    public const string ChainPollerPid = "chain-poller.pid";

    /// <summary>The Telegram notifier — the sidecar that turns run activity into alerts.</summary>
    public const string NotifierPid = "notifier.pid";

    private const string FeedPidPrefix = "feed.";
    private const string StrategyRunPidPrefix = "strategyrun.";
    private const string BacktestRunPidPrefix = "backtestrun.";
    private const string PidSuffix = ".pid";

    /// <summary>
    /// "feed.&lt;providerKey&gt;.pid": a vendor's live feed process id, so the API
    /// can adopt it after a restart. One key per vendor, never shared: a stop
    /// aimed at one feed must not be able to find another feed's pid.
    /// </summary>
    public static string FeedPid(string providerKey) => $"{FeedPidPrefix}{providerKey}{PidSuffix}";

    /// <summary>
    /// The pid key a vendor's feed process is recorded under: FYERS keeps
    /// <see cref="IngestorPid"/>, every other vendor <see cref="FeedPid"/>. The
    /// supervisors and the heartbeat both read it from here, so the place a feed
    /// reports its pid and the place its supervisor looks can never drift apart.
    /// </summary>
    public static string PidForFeed(string providerKey) =>
        string.Equals(providerKey, "fyers", StringComparison.OrdinalIgnoreCase)
            ? IngestorPid
            : FeedPid(providerKey.Trim().ToLowerInvariant());

    /// <summary>"strategyrun.&lt;runId&gt;.pid": the execution runner's process id for a LivePaper run.</summary>
    public static string StrategyRunPid(long runId) => $"{StrategyRunPidPrefix}{runId}{PidSuffix}";

    /// <summary>"backtestrun.&lt;runId&gt;.pid": the backtest runner's process id for an OfflineReplay run.</summary>
    public static string BacktestRunPid(long runId) => $"{BacktestRunPidPrefix}{runId}{PidSuffix}";
}
