// src/AlgoTrading.Contracts/Strategies/StrategyDataRequirement.cs
namespace AlgoTrading.Contracts.Strategies;

/// <summary>
/// A data feed a strategy needs to run, e.g. { symbolType: "index", resolution: "5m" }.
/// </summary>
public class StrategyDataRequirement
{
    public string SymbolType { get; set; } = string.Empty;
    public string Resolution { get; set; } = string.Empty;
}
