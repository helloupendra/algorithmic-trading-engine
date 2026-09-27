namespace AlgoTrading.Application.Interfaces;

/// <summary>How loud an event is. Maps to the badge and to the Telegram prefix.</summary>
public enum NotificationSeverity
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>
/// What kind of event this is, so an operator can choose which categories reach
/// Telegram instead of getting everything or nothing.
/// </summary>
public enum NotificationCategory
{
    /// <summary>A strategy run started, stopped, or hit a risk rule.</summary>
    StrategyRun,

    /// <summary>A background process started or stopped — ingestor, alerter.</summary>
    Process,

    /// <summary>Kill switch, limits, exposure breaches.</summary>
    Risk,

    /// <summary>Broker session connected, expired, refused.</summary>
    Connector,

    /// <summary>Anything else the platform wants to say.</summary>
    System,
}

/// <summary>
/// The one way the platform tells its operator something happened.
/// </summary>
/// <remarks>
/// Everything published here lands on the same path the strategy alerter already
/// uses: Redis <c>alerts:new</c> → Telegram (when configured) → the
/// <c>alert_events</c> table. So an event is never delivered without also being
/// recorded, and the console's alert stream is a complete history rather than a
/// selection.
/// <para>
/// Notifying must never break the thing it is reporting on: implementations
/// swallow their own failures and log them, so a Telegram outage cannot stop a
/// strategy from starting.
/// </para>
/// </remarks>
public interface ISystemNotifier
{
    Task NotifyAsync(
        NotificationCategory category,
        NotificationSeverity severity,
        string title,
        string message,
        string? underlying = null,
        string? symbol = null,
        long? simulationRunId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the event exactly as <see cref="NotifyAsync"/> does — the
    /// <c>alert_events</c> row, the console's alert stream — but does not send
    /// it to Telegram.
    /// </summary>
    /// <remarks>
    /// For events that must stay on the record but would drown the channel if
    /// each were sent: on 25 Sep 2026, 26 runners each reported every one of 11
    /// feed blips, stalled and recovered, and 572 messages hit Telegram, which
    /// refused 350 of them. An implementation that cannot hold a message back
    /// sends it: a duplicate is noise, a lost alert is the failure this desk
    /// exists to prevent.
    /// </remarks>
    Task RecordAsync(
        NotificationCategory category,
        NotificationSeverity severity,
        string title,
        string message,
        string? underlying = null,
        string? symbol = null,
        long? simulationRunId = null,
        CancellationToken cancellationToken = default)
        => NotifyAsync(category, severity, title, message, underlying, symbol, simulationRunId, cancellationToken);
}
