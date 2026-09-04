using AlgoTrading.Contracts.Strategies;

namespace AlgoTrading.Contracts.Risk;

public class RiskExposureResponse
{
    public decimal TotalUnrealizedPnL { get; set; }
    public decimal TotalRealizedPnL { get; set; }
    public int ActiveRunsCount { get; set; }
    public List<ActiveRunExposure> ActiveRuns { get; set; } = new();
}

public class ActiveRunExposure
{
    public long RunId { get; set; }
    public string StrategyName { get; set; } = string.Empty;
    public string Underlying { get; set; } = string.Empty;
    public decimal UnrealizedPnL { get; set; }
    public decimal RealizedPnL { get; set; }
    public RiskRulesDto RiskRules { get; set; } = new();
}
