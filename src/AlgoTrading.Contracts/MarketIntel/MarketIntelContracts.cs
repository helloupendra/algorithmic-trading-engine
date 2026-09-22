namespace AlgoTrading.Contracts.MarketIntel;

/// <summary>One headline from an external market-news feed.</summary>
public record NewsItemDto(
    string Title,
    string Link,
    string Source,
    DateTime? PublishedUtc,
    string? Summary);

/// <summary>News for one category, with the sources that produced it.</summary>
public record NewsResponse(
    string Category,
    DateTime FetchedUtc,
    IReadOnlyList<NewsItemDto> Items);

/// <summary>
/// One category the news section can be asked for. <see cref="Group"/> is how
/// the console files it — <c>markets</c> for the broad feeds, <c>sectors</c>
/// for the industry ones — so a category added on the server shows up in the
/// console without a change there.
/// </summary>
public record NewsCategoryDto(
    string Key,
    string Label,
    string Group);

/// <summary>Group keys of <see cref="NewsCategoryDto.Group"/>.</summary>
public static class NewsCategoryGroups
{
    /// <summary>The broad feeds: India markets, global business, commodities.</summary>
    public const string Markets = "markets";

    /// <summary>One industry each — pharma, banking, IT and the rest.</summary>
    public const string Sectors = "sectors";
}

/// <summary>
/// One symbol's day move, computed from an external quote source.
/// This is market data, not a recommendation.
/// </summary>
public record MoverDto(
    string Symbol,
    string YahooSymbol,
    decimal? LastPrice,
    decimal? PreviousClose,
    decimal? ChangePercent);

/// <summary>Top movers for one category (an equity group).</summary>
public record MoversResponse(
    string Group,
    string DisplayName,
    DateTime FetchedUtc,
    IReadOnlyList<MoverDto> Gainers,
    IReadOnlyList<MoverDto> Losers,
    int SymbolsResolved,
    int SymbolsFailed);
