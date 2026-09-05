using AlgoTrading.Application.Providers;
using AlgoTrading.Domain.Entities;

namespace AlgoTrading.Infrastructure.Providers.Csv;

/// <summary>
/// Turns a vendor an operator added into a connector descriptor.
/// </summary>
/// <remarks>
/// There is no fixed "csv" connector any more: each file-based vendor is its own
/// connector with its own key, so its rows are attributable to it by name rather
/// than all landing under one anonymous "csv".
/// </remarks>
public static class CsvVendorDescriptor
{
    /// <summary>
    /// Offline and manually curated, so it ranks behind a live vendor but ahead
    /// of replay, which can only return what the platform already stored.
    /// </summary>
    public const int FallbackRank = 50;

    public static ProviderDescriptor For(DataVendor vendor) => new(
        vendor.Key,
        vendor.DisplayName,
        ProviderKind.Data,
        ProviderAuthKind.None,
        new ProviderCapabilities
        {
            // A folder of OHLCV files: history, and honestly nothing else.
            History = true,
            LiveTicks = false,
            Quotes = false,
            OptionChain = false,
            Orders = false,
            Depth = false,
            OpenInterest = false,
            Greeks = false,

            // Files are named by canonical symbol, so no mapping rows are needed.
            UsesCanonicalSymbols = true,

            // The bars come from outside the platform, so a sync may persist them.
            ServesFromPlatformStore = false,

            Resolutions = new[] { "1", "2", "3", "5", "10", "15", "20", "30", "60", "120", "240", "D" },
            Segments = new[] { "CM", "FO", "CD", "MCX" },
        })
    {
        FallbackRank = FallbackRank,
    };
}
