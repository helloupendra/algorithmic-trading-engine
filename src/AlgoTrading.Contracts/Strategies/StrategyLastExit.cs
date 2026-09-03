// src/AlgoTrading.Contracts/Strategies/StrategyLastExit.cs
namespace AlgoTrading.Contracts.Strategies;

/// <summary>
/// How the last run of a strategy ended: stop-loss, target, user stop, market close or a runner crash.
/// </summary>
public class StrategyLastExit
{
    public long RunId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTime AtUtc { get; set; }
    public string Underlying { get; set; } = string.Empty;
}
