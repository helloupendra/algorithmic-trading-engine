using AlgoTrading.Application.Providers;

namespace AlgoTrading.Infrastructure.Providers.Csv;

/// <summary>
/// Reads OHLCV history from files on disk — for instruments this platform has no
/// live feed for, and for anything a vendor will not sell back far enough.
/// </summary>
public static class CsvProvider
{
    /// <summary>Stable key, written into the SourceKey of every row it produces.</summary>
    public const string Key = "csv";

    public static readonly ProviderDescriptor Descriptor = new(
        Key,
        "CSV files",
        ProviderKind.Data,
        ProviderAuthKind.None,
        new ProviderCapabilities
        {
            History = true,
            LiveTicks = false,
            Quotes = false,
            OptionChain = false,
            Orders = false,
            Depth = false,
            OpenInterest = false,
            Greeks = false,

            // Files are named by canonical symbol, so no mapping table is needed.
            UsesCanonicalSymbols = true,

            // Unlike replay, these bars come from outside the platform, so a sync
            // may — and should — persist them.
            ServesFromPlatformStore = false,

            Resolutions = new[] { "1", "2", "3", "5", "10", "15", "20", "30", "60", "120", "240", "D" },
            Segments = new[] { "CM", "FO", "CD", "MCX" },
        })
    {
        // Offline and manually curated: only ahead of replay.
        FallbackRank = 50,
    };
}

/// <summary>Where the CSV connector looks for files.</summary>
public class CsvProviderSettings
{
    /// <summary>
    /// Directory holding one file per symbol and resolution, named
    /// <c>&lt;symbol with ':' and '/' replaced by '_'&gt;__&lt;resolution&gt;.csv</c> —
    /// for example <c>NSE_NIFTYBANK-INDEX__15.csv</c>.
    /// </summary>
    public string Directory { get; set; } = "data/csv";
}
