// src/AlgoTrading.Contracts/Simulator/CarryForwardUpdate.cs
namespace AlgoTrading.Contracts.Simulator;

/// <summary>What a request to change one position's carry-forward tick did.</summary>
public enum CarryForwardUpdate
{
    /// <summary>The tick was changed and the change recorded on the run's activity.</summary>
    Changed,

    /// <summary>The tick already had that value; nothing was written.</summary>
    Unchanged,

    /// <summary>The position is not an open position of that run (closed, carried, or someone else's).</summary>
    PositionNotOpen,

    /// <summary>The run is being stopped or has ended; nothing will carry its positions now.</summary>
    RunNotRunning
}
