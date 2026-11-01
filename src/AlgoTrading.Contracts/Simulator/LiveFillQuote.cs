// src/AlgoTrading.Contracts/Simulator/LiveFillQuote.cs
namespace AlgoTrading.Contracts.Simulator;

/// <summary>
/// What a live order for one contract would fill at right now, priced by the rules a strategy's live leg is filled by,
/// with nothing booked; or why it would not fill.
/// </summary>
/// <param name="Price">The fill price; null when the order would be refused (<paramref name="Refusal"/> says why).</param>
/// <param name="Rule">How it was priced: <c>ask</c>, <c>bid</c>, <c>ltp-plus-half-spread</c>, <c>ltp-less-half-spread</c>.</param>
/// <param name="Note">The same in a sentence, as the order row would keep it.</param>
/// <param name="Refusal">
/// Why there is no fill, in words that follow "Not filled:": no live quote, a quote too old while the contract's market
/// is open, or a limit the book has not reached.
/// </param>
/// <param name="QuoteUtc">When the quote it read was written; null with no quote.</param>
public sealed record LiveFillQuote(
    decimal? Price,
    string? Rule,
    string? Note,
    string? Refusal,
    decimal? Bid,
    decimal? Ask,
    decimal? Ltp,
    DateTime? QuoteUtc);
