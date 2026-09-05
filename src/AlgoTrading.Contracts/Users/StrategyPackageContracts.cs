namespace AlgoTrading.Contracts.Users;

/// <summary>A strategy package as the admin panel shows it.</summary>
public class StrategyPackageResponse
{
    public long Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }

    /// <summary>Covers the whole catalog, including strategies written later.</summary>
    public bool IncludesAllStrategies { get; set; }

    public int? MaxLotsPerRun { get; set; }
    public int? MaxConcurrentRuns { get; set; }
    public IReadOnlyList<string> AllowedUnderlyings { get; set; } = Array.Empty<string>();
    public bool AllowLiveMode { get; set; }

    /// <summary>Strategy names in the package. Empty when it covers everything.</summary>
    public IReadOnlyList<string> Strategies { get; set; } = Array.Empty<string>();

    /// <summary>How many accounts hold this package.</summary>
    public int HolderCount { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
}

public class SaveStrategyPackageRequest
{
    /// <summary>Only used on create; the key never changes afterwards.</summary>
    public string Key { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public bool IncludesAllStrategies { get; set; }
    public int? MaxLotsPerRun { get; set; }
    public int? MaxConcurrentRuns { get; set; }
    public IReadOnlyList<string> AllowedUnderlyings { get; set; } = Array.Empty<string>();
    public bool AllowLiveMode { get; set; }
}

public class SetPackageStrategiesRequest
{
    public IReadOnlyList<string> StrategyNames { get; set; } = Array.Empty<string>();
}

public class SetStrategyGrantsRequest
{
    public IReadOnlyList<string> StrategyNames { get; set; } = Array.Empty<string>();
}
