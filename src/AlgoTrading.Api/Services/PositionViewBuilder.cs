// src/AlgoTrading.Api/Services/PositionViewBuilder.cs
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Simulator;
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Decorates paper positions into the position-based rows the live runner and
/// the backtest results page both show: decoded contract, BUY/SELL side, lots x
/// lot size, entry, mark and P&amp;L. A closed leg is the same row with lots 0.
/// Live views mark open rows against LiveQuotesLatest; replays never do.
/// </summary>
public sealed class PositionViewBuilder
{
    private readonly TradingDbContext _dbContext;
    private readonly ILotSizeResolver _lotSizeResolver;

    public PositionViewBuilder(TradingDbContext dbContext, ILotSizeResolver lotSizeResolver)
    {
        _dbContext = dbContext;
        _lotSizeResolver = lotSizeResolver;
    }

    public sealed record Result<T>(
        List<T> Positions,
        decimal? SpotLtp,
        DateTime? SpotUpdatedUtc,
        IReadOnlyDictionary<string, LotSizeInfo> LotSizes) where T : LivePositionResponse;

    /// <summary>
    /// Builds the rows (open first, then newest first). With
    /// <paramref name="useLiveQuotes"/> the open rows' Ltp comes from the latest
    /// live quote (falling back to the stored mark) and the spot LTP is looked
    /// up too; otherwise Ltp is the stored LastMarkPrice and no quote is read.
    /// <paramref name="lotSizeOverride"/> applies one lot size to every row
    /// (a backtest books every contract with the run's frozen lot size).
    /// </summary>
    public async Task<Result<T>> BuildAsync<T>(
        IReadOnlyList<PaperPositionResponse> positions,
        bool useLiveQuotes,
        string? spotSymbol,
        CancellationToken cancellationToken,
        int? lotSizeOverride = null) where T : LivePositionResponse, new()
    {
        var symbols = positions.Select(x => x.Symbol).Distinct(StringComparer.Ordinal).ToList();

        var quoteBySymbol = new Dictionary<string, (decimal? Ltp, DateTime UpdatedUtc)>(StringComparer.Ordinal);
        decimal? spotLtp = null;
        DateTime? spotUpdatedUtc = null;

        if (useLiveQuotes)
        {
            var quoteSymbols = symbols.ToList();
            if (!string.IsNullOrWhiteSpace(spotSymbol)) quoteSymbols.Add(spotSymbol);

            if (quoteSymbols.Count > 0)
            {
                var quotes = await _dbContext.LiveQuotesLatest.AsNoTracking()
                    .Where(x => quoteSymbols.Contains(x.Symbol))
                    .Select(x => new { x.Symbol, x.LastTradedPrice, x.UpdatedUtc })
                    .ToListAsync(cancellationToken);

                foreach (var q in quotes)
                {
                    quoteBySymbol.TryAdd(q.Symbol, (q.LastTradedPrice, q.UpdatedUtc));
                }
            }

            if (!string.IsNullOrWhiteSpace(spotSymbol) && quoteBySymbol.TryGetValue(spotSymbol, out var spotQuote))
            {
                spotLtp = spotQuote.Ltp;
                spotUpdatedUtc = spotQuote.UpdatedUtc;
            }
        }

        var instruments = symbols.Count == 0
            ? new List<InstrumentLite>()
            : await _dbContext.Instruments.AsNoTracking()
                .Where(x => symbols.Contains(x.Symbol))
                .Select(x => new InstrumentLite(x.Symbol, x.Underlying, x.StrikePrice, x.OptionType, x.ExpiryDate))
                .ToListAsync(cancellationToken);
        var instrumentBySymbol = instruments
            .GroupBy(x => x.Symbol, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var lotSizes = await _lotSizeResolver.ResolveManyAsync(symbols, cancellationToken);

        var rows = new List<T>(positions.Count);
        foreach (var pos in positions)
        {
            bool isOpen = string.Equals(pos.Status, "Open", StringComparison.OrdinalIgnoreCase);
            int lotSize = lotSizeOverride is > 0
                ? lotSizeOverride.Value
                : lotSizes.TryGetValue(pos.Symbol, out var ls) ? ls.LotSize : 1;
            instrumentBySymbol.TryGetValue(pos.Symbol, out var inst);

            decimal? ltp = null;
            DateTime? ltpUpdatedUtc = null;
            if (isOpen)
            {
                if (useLiveQuotes && quoteBySymbol.TryGetValue(pos.Symbol, out var quote) && quote.Ltp.HasValue)
                {
                    ltp = quote.Ltp;
                    ltpUpdatedUtc = quote.UpdatedUtc;
                }
                else
                {
                    ltp = pos.LastMarkPrice;
                    ltpUpdatedUtc = useLiveQuotes ? null : pos.UpdatedUtc;
                }
            }

            int lots = isOpen ? pos.Quantity : 0;

            rows.Add(new T
            {
                Id = pos.Id,
                GroupId = pos.GroupId,
                Symbol = pos.Symbol,
                Contract = BuildContract(pos.Symbol, inst),
                Side = string.Equals(pos.Direction, "LONG", StringComparison.OrdinalIgnoreCase) ? "BUY" : "SELL",
                Lots = lots,
                LotSize = lotSize,
                Quantity = lots * lotSize,
                Status = isOpen ? "Open" : "Closed",
                EntryPrice = pos.AveragePrice,
                Ltp = ltp,
                LtpUpdatedUtc = ltpUpdatedUtc,
                Pnl = isOpen ? pos.UnrealizedPnl : pos.RealizedPnl,
                OpenedUtc = pos.OpenedUtc,
                ClosedUtc = pos.ClosedUtc
            });
        }

        rows = rows
            .OrderBy(x => x.Status == "Open" ? 0 : 1)
            .ThenByDescending(x => x.OpenedUtc)
            .ThenByDescending(x => x.Id)
            .ToList();

        return new Result<T>(rows, spotLtp, spotUpdatedUtc, lotSizes);
    }

    public sealed record InstrumentLite(string Symbol, string Underlying, decimal? StrikePrice, string OptionType, DateOnly? ExpiryDate);

    /// <summary>
    /// Decoded contract for display: the instrument master wins, the FYERS
    /// symbol grammar fills in for contracts the master no longer holds.
    /// </summary>
    public static ContractInfo BuildContract(string symbol, InstrumentLite? inst)
    {
        var parsed = UnderlyingCatalog.ParseOptionSymbol(symbol);

        var underlying = !string.IsNullOrWhiteSpace(inst?.Underlying)
            ? inst!.Underlying.Trim().ToUpperInvariant()
            : parsed?.Underlying ?? UnderlyingCatalog.InferUnderlying(symbol);

        var strike = inst?.StrikePrice is > 0 ? inst.StrikePrice : parsed?.Strike;
        var optionType = !string.IsNullOrWhiteSpace(inst?.OptionType)
            ? inst!.OptionType.Trim().ToUpperInvariant()
            : parsed?.OptionType ?? string.Empty;
        var expiry = inst?.ExpiryDate ?? parsed?.Expiry;

        bool looksLikeOption = strike.HasValue && (optionType == "CE" || optionType == "PE");

        return new ContractInfo
        {
            Underlying = underlying,
            Strike = strike,
            OptionType = optionType,
            ExpiryDate = expiry,
            Label = looksLikeOption
                ? UnderlyingCatalog.ContractLabel(underlying, strike, optionType, expiry)
                : symbol
        };
    }
}
