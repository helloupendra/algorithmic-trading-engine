// src/AlgoTrading.Api/Services/FeedFailoverPorts.cs

namespace AlgoTrading.Api.Services;

// What FeedFailoverService needs from the outside world, each behind a small
// interface so its rules can be tested with a fake clock, a fake tick stream
// and fake feeds. The production implementations are in FeedFailoverAdapters.cs.

/// <summary>The newest live tick of one exchange group.</summary>
/// <param name="AtUtc">The feed's receivedUtc, or the stream entry's time when it has none.</param>
/// <param name="Symbol">The tick's symbol, for the log.</param>
/// <param name="Source">The connector that sent it ("dhan", "fyers"), for the log.</param>
public sealed record FeedTick(DateTime AtUtc, string Symbol, string Source);

/// <summary>What the head of the tick stream says, per exchange group.</summary>
/// <param name="Newest">Group ("NSE", "BSE", "MCX") → its newest live tick among the entries read.</param>
/// <param name="OldestReadUtc">The time of the oldest entry read: a group not seen is at least this old.</param>
/// <param name="Exhausted">The whole stream was read, so a group not seen has no tick at all.</param>
/// <param name="Scanned">Entries read.</param>
public sealed record FeedTickReading(
    IReadOnlyDictionary<string, FeedTick> Newest,
    DateTime? OldestReadUtc,
    bool Exhausted,
    int Scanned);

/// <summary>The newest live tick per exchange group.</summary>
public interface IFeedTickSource
{
    /// <summary>
    /// Reads back from the newest entry until every group that can trigger a
    /// switch has been seen, or the entries are older than
    /// <paramref name="horizon"/>, or a bound on the read is reached.
    /// </summary>
    Task<FeedTickReading> ReadAsync(DateTime nowUtc, TimeSpan horizon, CancellationToken cancellationToken);
}

/// <summary>The Dhan feed process, as its supervisor sees it.</summary>
/// <param name="StartedUtc">When the process started; null when that cannot be read.</param>
public sealed record DhanFeedProcess(bool Running, int? ProcessId, DateTime? StartedUtc);

/// <summary>What a switch did to the FYERS side.</summary>
/// <param name="FeedRunning">The FYERS feed was started (or was already running).</param>
/// <param name="Summary">One line: the feed's and the chain poller's answers.</param>
public sealed record FyersStartOutcome(bool FeedRunning, string Summary);

/// <summary>The feeds the failover reads and, when it is not a dry run, switches.</summary>
public interface IFeedFailoverFeeds
{
    Task<DhanFeedProcess> DhanAsync(CancellationToken cancellationToken);

    /// <summary>True while the FYERS feed (the "ingestor") runs.</summary>
    Task<bool> FyersRunningAsync(CancellationToken cancellationToken);

    /// <summary>Stops the Dhan feed only — its option chain recorder keeps running. One line saying what happened.</summary>
    Task<string> StopDhanAsync(string reason, CancellationToken cancellationToken);

    /// <summary>Starts the FYERS feed, then the FYERS chain poller.</summary>
    Task<FyersStartOutcome> StartFyersAsync(CancellationToken cancellationToken);
}

public enum FyersSignInState
{
    /// <summary>FYERS answered a real call with the token the FYERS feed would use.</summary>
    SignedIn,

    /// <summary>No token, an expired one, or FYERS refused it.</summary>
    SignedOut,

    /// <summary>FYERS could not be asked (network, not configured).</summary>
    Unknown,
}

/// <param name="Detail">Why, in words, for the log and the message. Never a token.</param>
public sealed record FyersSignIn(FyersSignInState State, string Detail);

/// <summary>The vendor-side questions: is FYERS usable, and what does Dhan say about itself.</summary>
public interface IFeedFailoverChecks
{
    /// <summary>A real call to FYERS with the session the FYERS feed would be handed.</summary>
    Task<FyersSignIn> CheckFyersAsync(CancellationToken cancellationToken);

    /// <summary>Dhan's own account of itself: its /profile answer and the feed's last heartbeat. Never throws.</summary>
    Task<string> DescribeDhanAsync(CancellationToken cancellationToken);
}
