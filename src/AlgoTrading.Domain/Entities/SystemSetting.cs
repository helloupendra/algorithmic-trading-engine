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

    /// <summary>The market replay player's pid (market_data/replay/run_replay.py).</summary>
    public const string ReplayPlayerPid = "replay.pid";

    /// <summary>The market replay in progress or last played: its day, speed, runs and how it ended (JSON).</summary>
    public const string ReplaySession = "replay.session";

    /// <summary>
    /// The last start or stop of the Dhan chain recorder and the IST day it was
    /// made, e.g. "true on 2026-09-24". It holds for that day only, so an API
    /// restart in the middle of the session keeps recording what the morning job
    /// switched on; the next day starts from configuration again.
    /// </summary>
    public const string DhanChainPollerEnabled = "dhan.chainpoller.enabled";

    /// <summary>
    /// The IST day the automatic Dhan sign-in stopped itself and why, e.g.
    /// "2026-09-29: Dhan refused the PIN or the code". Never the PIN. While the
    /// stop lived in memory only, the API restart the morning job makes at 08:45,
    /// and every deploy, wiped it, and the next automatic try would have sent the
    /// same wrong PIN again; repeated wrong PINs can lock the Dhan account. It
    /// holds for that day only, and a successful sign-in removes it.
    /// </summary>
    public const string DhanAutoSignInStopped = "dhan.autosignin.stopped";

    /// <summary>
    /// Set once the default candle-pattern rules have been seeded, so rules an
    /// admin deleted stay deleted across restarts.
    /// </summary>
    public const string PatternRulesSeeded = "patterns.rules.seeded";

    /// <summary>
    /// The last session the 08:50 IST forecast job was started for (or found
    /// missed), "yyyy-MM-dd". Written before the job starts, so an API restart
    /// in the middle of it never issues the same morning twice.
    /// </summary>
    public const string ForecastsLastIssuedSession = "forecasts.issue.lastSession";

    /// <summary>
    /// The latest session whose outcomes the scoring job has been started for,
    /// "yyyy-MM-dd". A trading day whose 15:50 IST has passed and is later than
    /// this is a scoring run still owed — at 15:50, or at the next start-up.
    /// </summary>
    public const string ForecastsLastScoredSession = "forecasts.score.lastSession";

    /// <summary>
    /// The IST day, "yyyy-MM-dd", the news scorer's failure was last sent to
    /// the System channel. It runs every ten minutes; a broken scorer is worth
    /// one message a day, not ninety.
    /// </summary>
    public const string NewsScoringLastFailureNotice = "marketintel.newsscore.lastFailureNotice";

    /// <summary>
    /// "marketintel.nofile.&lt;dataset&gt;": the days a market-intelligence
    /// backfill found no file for, once they were old enough that none will
    /// come (holidays the exchange calendar does not hold), as comma-separated
    /// yyyyMMdd. They are not asked for again and do not count as missing.
    /// </summary>
    public static string MarketIntelligenceNoFile(string dataset) => $"marketintel.nofile.{dataset}";

    /// <summary>
    /// "feed.failover.yyyy-MM-dd": the IST day the automatic switch from Dhan to
    /// FYERS was made, and what it saw, e.g. "11:30:05 IST: switched Dhan → FYERS
    /// — NSE silent 212 s (…)". Written before the switch, so an API restart
    /// that day finds it and never switches a second time. Any value blocks the
    /// day's switch; nothing deletes it.
    /// </summary>
    public static string FeedFailover(DateOnly istDay)
        => $"feed.failover.{istDay.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}";

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
