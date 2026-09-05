namespace AlgoTrading.Domain.Entities;

/// <summary>How a data vendor delivers its history.</summary>
public enum DataVendorKind
{
    /// <summary>
    /// One CSV file per symbol and resolution in a folder. Any vendor that can
    /// export or FTP files can be added this way with no code at all.
    /// </summary>
    CsvFiles = 0,
}

/// <summary>
/// A data vendor an operator added from the console, as opposed to one this
/// build ships an adapter for.
/// </summary>
/// <remarks>
/// This is the honest half of "add a vendor without writing code". A vendor's
/// <em>live API</em> cannot be configured into existence — every one has its own
/// auth, paging, rate limits and symbol grammar, and pretending otherwise
/// produces a connector that fails in ways nobody can diagnose. Files, on the
/// other hand, are genuinely uniform: point at a folder and the platform can
/// read it. So that is what this supports, and the console says so.
/// </remarks>
public class DataVendor
{
    public long Id { get; set; }

    /// <summary>
    /// Stable key, lowercase. It is written into the SourceKey column of every
    /// row this vendor produces, so it must never change once data exists.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Operator-facing name, e.g. "TrueData exports".</summary>
    public string DisplayName { get; set; } = string.Empty;

    public DataVendorKind Kind { get; set; } = DataVendorKind.CsvFiles;

    /// <summary>Folder holding this vendor's files.</summary>
    public string Directory { get; set; } = string.Empty;

    /// <summary>A disabled vendor stays configured but is never routed to.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Free-text note from whoever added it.</summary>
    public string Notes { get; set; } = string.Empty;

    public string CreatedBy { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
