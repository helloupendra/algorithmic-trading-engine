namespace AlgoTrading.Domain.Entities;

/// <summary>
/// One scheme's net asset value on one day, as AMFI published it. Table
/// <c>mf_nav</c>, a TimescaleDB hypertable on <see cref="Date"/>, compressed
/// once a chunk is 30 days old, like the desk's tick and bar tables.
/// </summary>
/// <remarks>
/// Most schemes have a NAV on business days only; liquid and overnight funds
/// have one for every calendar day, weekends included. A NAV AMFI writes as
/// "N.A.", as text, or as zero (a written-off segregated portfolio) is not
/// stored: a missing day is a gap, never a made-up number.
/// </remarks>
public class MutualFundNav
{
    /// <summary>AMFI's scheme code (<see cref="MutualFundScheme.SchemeCode"/>).</summary>
    public int SchemeCode { get; set; }

    /// <summary>The day the NAV is for (IST).</summary>
    public DateOnly Date { get; set; }

    /// <summary>The NAV in rupees per unit, exactly as AMFI wrote it.</summary>
    public decimal Nav { get; set; }

    /// <summary><see cref="MutualFundNavSources"/>: which AMFI file the value came from.</summary>
    public string Source { get; set; } = MutualFundNavSources.AmfiDaily;
}

/// <summary>The values of <see cref="MutualFundNav.Source"/>.</summary>
public static class MutualFundNavSources
{
    /// <summary>AMFI's daily file, <c>NAVAll.txt</c>.</summary>
    public const string AmfiDaily = "amfi-daily";

    /// <summary>AMFI's NAV history download, read in 90-day windows.</summary>
    public const string AmfiHistory = "amfi-history";
}
