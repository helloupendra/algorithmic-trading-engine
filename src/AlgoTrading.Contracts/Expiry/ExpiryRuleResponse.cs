// src/AlgoTrading.Contracts/Expiry/ExpiryRuleResponse.cs
namespace AlgoTrading.Contracts.Expiry;

public class ExpiryRuleResponse
{
    public string Exchange { get; set; } = string.Empty;
    public string Underlying { get; set; } = string.Empty;

    public bool HasWeekly { get; set; }
    public bool HasMonthly { get; set; }
    public bool HasQuarterly { get; set; }
    public bool HasSemiAnnual { get; set; }

    public string? WeeklyExpiryDay { get; set; }
    public string? MonthlyExpiryDay { get; set; }
    public string? QuarterlyExpiryDay { get; set; }
    public string? SemiAnnualExpiryDay { get; set; }

    public string HolidayShiftRule { get; set; } = string.Empty;
    public string PreferredExpiryType { get; set; } = string.Empty;
}