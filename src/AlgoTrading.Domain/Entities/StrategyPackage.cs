namespace AlgoTrading.Domain.Entities;

/// <summary>
/// A named bundle of strategies plus the limits that come with them.
/// </summary>
/// <remarks>
/// A package that only listed strategies would barely beat a row of checkboxes.
/// The value is that it also carries limits: on this platform every trader runs
/// on the same broker connection and the same capital, so deciding what a trader
/// may run <em>is</em> deciding how much they may risk.
/// <para>
/// Membership is explicit. A newly written strategy joins no package until
/// someone puts it in one — a convenience that silently handed out new
/// strategies would be a risk, not a feature.
/// </para>
/// </remarks>
public class StrategyPackage
{
    public long Id { get; set; }

    /// <summary>Stable lowercase key, e.g. "starter". Immutable once assigned.</summary>
    public string Key { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>A disabled package grants nothing, without being deleted.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Grants every strategy in the catalog, including ones written later.
    /// </summary>
    /// <remarks>
    /// This is the one place a new strategy reaches a trader without anyone
    /// deciding it should — which is exactly the risk explicit membership exists
    /// to avoid. It is here because it is genuinely wanted for a fully trusted
    /// trader, and because the accounts that predate packages had precisely this
    /// access and could not be migrated to an explicit list. The console must
    /// label it plainly wherever it appears.
    /// </remarks>
    public bool IncludesAllStrategies { get; set; }

    /// <summary>Ceiling on lots for a single run. Null means no package ceiling.</summary>
    public int? MaxLotsPerRun { get; set; }

    /// <summary>
    /// How many live runs a holder may keep open. Null means no package ceiling.
    /// Where this, the per-user cap and the platform limit disagree, the tightest wins.
    /// </summary>
    public int? MaxConcurrentRuns { get; set; }

    /// <summary>
    /// Comma-separated underlyings this package allows, e.g. "NIFTY,BANKNIFTY".
    /// Empty means every underlying the strategy itself supports.
    /// </summary>
    public string AllowedUnderlyingsCsv { get; set; } = string.Empty;

    /// <summary>
    /// False keeps the holder on paper trading. Everything is paper today, but
    /// this is the guard that matters most the day live execution lands.
    /// </summary>
    public bool AllowLiveMode { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public ICollection<StrategyPackageItem> Items { get; set; } = new List<StrategyPackageItem>();
}

/// <summary>One strategy inside a package.</summary>
/// <remarks>
/// Keyed by strategy <em>name</em>, not by the catalog id: that id is a hash of
/// the name, so renaming a strategy changes it and would silently break every
/// row pointing at it. Keyed by name, a rename breaks loudly and visibly — the
/// strategy simply stops appearing in the package until someone fixes it.
/// </remarks>
public class StrategyPackageItem
{
    public long Id { get; set; }

    public long StrategyPackageId { get; set; }

    public string StrategyName { get; set; } = string.Empty;

    public StrategyPackage? Package { get; set; }
}

/// <summary>
/// One extra strategy given to one trader, on top of whatever their package
/// holds — the exception that stops an admin from cloning a package for a
/// single addition.
/// </summary>
public class UserStrategyGrant
{
    public long Id { get; set; }

    public long UserId { get; set; }

    public string StrategyName { get; set; } = string.Empty;

    public string GrantedBy { get; set; } = string.Empty;

    public DateTime GrantedUtc { get; set; } = DateTime.UtcNow;

    public AppUser? User { get; set; }
}
