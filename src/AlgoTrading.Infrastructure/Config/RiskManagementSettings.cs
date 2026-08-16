// src/AlgoTrading.Infrastructure/Config/RiskManagementSettings.cs
namespace AlgoTrading.Infrastructure.Config;

public class RiskManagementSettings
{
    public int MaxOrdersPerMinute { get; set; } = 50;
    public decimal MaxDailyLoss { get; set; } = -50000.0m;
}
