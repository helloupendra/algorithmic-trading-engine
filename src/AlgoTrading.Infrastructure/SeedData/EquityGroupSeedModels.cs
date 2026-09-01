namespace AlgoTrading.Infrastructure.SeedData;

public class EquityGroupSeedItem
{
    public string Name { get; set; } = string.Empty;
    public string Exchange { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
}

public class EquityGroupMemberSeedItem
{
    public string GroupName { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public decimal? Weight { get; set; }
    public DateOnly? EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public bool IsEnabled { get; set; } = true;
}