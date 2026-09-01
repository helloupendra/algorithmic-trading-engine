using AlgoTrading.Contracts.MarketIntel;

namespace AlgoTrading.Application.Interfaces;

/// <summary>
/// External market intelligence shown on the trader dashboard: news headlines
/// (public RSS feeds) and day movers (public quote data). Everything here is
/// informational market data — never a trading recommendation.
/// </summary>
public interface IMarketIntelService
{
    /// <param name="category">"india", "global" or "commodities".</param>
    Task<NewsResponse> GetNewsAsync(string category, CancellationToken cancellationToken = default);

    /// <param name="groupName">An equity group name, e.g. NIFTY50_CONSTITUENTS.</param>
    Task<MoversResponse> GetMoversAsync(string groupName, int top = 10, CancellationToken cancellationToken = default);
}
