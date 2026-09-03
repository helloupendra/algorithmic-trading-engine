// src/AlgoTrading.Contracts/Strategies/StopStrategyRequest.cs
namespace AlgoTrading.Contracts.Strategies;

/// <summary>
/// Optional body of POST /api/Strategy/{id}/stop.
/// </summary>
public class StopStrategyRequest
{
    /// <summary>Square off every open position at the last mark before killing the runner. Default true.</summary>
    public bool? Flatten { get; set; }
}
