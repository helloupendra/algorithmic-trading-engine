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
