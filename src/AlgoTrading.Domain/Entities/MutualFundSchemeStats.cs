namespace AlgoTrading.Domain.Entities;

/// <summary>
/// One scheme's trailing returns as of one NAV day and its place among its peers by each, computed each night after
/// the NAV sync. Table <c>mf_scheme_stats</c>, one row per scheme, replaced by the next run.
/// </summary>
/// <remarks>
/// Peers are the active schemes of the same SEBI category (AMFI's spellings folded together), the same scheme type
/// (open-ended, close-ended, interval) and the same plan, growth option only, each with a NAV on <see cref="AsOfDate"/>.
/// A rank is 1 for the highest return; schemes with equal returns share a rank. A return the history does not cover
/// is null and the scheme is left out of that period's count. Facts only: a rank by a stated return, never a rating.
/// </remarks>
public class MutualFundSchemeStats
{
    public int SchemeCode { get; set; }

    /// <summary>The NAV day every return in the run ends on.</summary>
    public DateOnly AsOfDate { get; set; }

    /// <summary>The peer group: scheme type and SEBI category, lower case ("open ended|equity scheme - large cap fund").</summary>
    public string PeerKey { get; set; } = string.Empty;

    /// <summary>"direct" or "regular".</summary>
    public string Plan { get; set; } = string.Empty;

    /// <summary>Trailing CAGRs as fractions (0.0738 is 7.38%); null when the stored history does not cover the period.</summary>
    public double? Return1y { get; set; }

    public double? Return3y { get; set; }
    public double? Return5y { get; set; }

    /// <summary>1 for the highest return among the peers with one; null without a return.</summary>
    public int? Rank1y { get; set; }

    public int? Rank3y { get; set; }
    public int? Rank5y { get; set; }

    /// <summary>Peers (this scheme included) with a return for the period.</summary>
    public int? Peers1y { get; set; }

    public int? Peers3y { get; set; }
    public int? Peers5y { get; set; }

    public DateTime ComputedUtc { get; set; }
}
