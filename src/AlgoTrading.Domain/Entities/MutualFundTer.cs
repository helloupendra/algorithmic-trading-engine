namespace AlgoTrading.Domain.Entities;

/// <summary>
/// A scheme's total expense ratio in effect on one day, as AMFI publishes it for each scheme (both plans in one row).
/// Table <c>mf_ter</c>, a TimescaleDB hypertable on <see cref="Date"/>.
/// </summary>
/// <remarks>
/// AMFI keys TER by the scheme's NSDL code and its name, not by the AMFI scheme code of each plan and option, so a
/// scheme is matched to its TER by name (<see cref="NameKey"/>), and only when the name matches one TER scheme.
/// </remarks>
public class MutualFundTer
{
    /// <summary>AMFI's TER key, e.g. "360O/O/E/FCF/23/02/0006".</summary>
    public string NsdlSchemeCode { get; set; } = string.Empty;

    /// <summary>The day the ratio is in effect for (IST).</summary>
    public DateOnly Date { get; set; }

    /// <summary>The scheme's name as the TER file writes it.</summary>
    public string SchemeName { get; set; } = string.Empty;

    /// <summary>The name reduced to words (lower case, marks dropped), as scheme names are matched.</summary>
    public string NameKey { get; set; } = string.Empty;

    /// <summary>"Open Ended", "Close Ended", "Interval Fund".</summary>
    public string SchemeType { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    /// <summary>The regular plan's total TER, in percent a year (1.95 is 1.95%); null when AMFI gives none.</summary>
    public decimal? RegularTer { get; set; }

    /// <summary>The direct plan's total TER, in percent a year; null when AMFI gives none.</summary>
    public decimal? DirectTer { get; set; }
}
