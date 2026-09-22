using AlgoTrading.Contracts.MarketIntel;

namespace AlgoTrading.Application.Interfaces;

/// <summary>
/// External market intelligence shown on the trader dashboard: news headlines
/// (public RSS feeds) and day movers (public quote data). Everything here is
/// informational market data — never a trading recommendation.
/// </summary>
public interface IMarketIntelService
{
    /// <summary>
    /// The categories news can be asked for, in the order the console shows
    /// them: the broad market feeds, then one per sector. The console reads its
    /// tabs from here rather than holding its own list, so a sector added on
    /// the server appears without a change there.
    /// </summary>
    IReadOnlyList<NewsCategoryDto> GetNewsCategories();

    /// <param name="category">
    /// A key from <see cref="GetNewsCategories"/> — "india", "global",
    /// "commodities", or a sector such as "pharma", "banking" or "it".
    /// </param>
    Task<NewsResponse> GetNewsAsync(string category, CancellationToken cancellationToken = default);

    /// <param name="groupName">An equity group name, e.g. NIFTY50_CONSTITUENTS.</param>
    Task<MoversResponse> GetMoversAsync(string groupName, int top = 10, CancellationToken cancellationToken = default);
}
