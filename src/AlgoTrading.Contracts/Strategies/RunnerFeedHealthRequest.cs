// src/AlgoTrading.Contracts/Strategies/RunnerFeedHealthRequest.cs

namespace AlgoTrading.Contracts.Strategies;

/// <summary>
/// What a runner says about its own tick feed.
/// </summary>
/// <remarks>
/// The runner is the only thing that can see this. A stalled feed does not stop
/// the process, close the socket or fail a heartbeat — it simply stops
/// delivering ticks, and everything downstream looks healthy while the strategy
/// goes blind.
/// </remarks>
public class RunnerFeedHealthRequest
{
    /// <summary>True when ticks have stopped; false when they came back.</summary>
    public bool IsStalled { get; set; }

    /// <summary>How long the feed has been (or was) silent.</summary>
    public int SilentSeconds { get; set; }

    /// <summary>The underlying this run trades, for the alert's subject line.</summary>
    public string? Underlying { get; set; }
}
