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
}
