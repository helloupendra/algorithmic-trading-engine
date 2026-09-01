// src/AlgoTrading.Infrastructure/Services/OptionHistoryBackfillService.cs
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Options;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Infrastructure.Services;

public class OptionHistoryBackfillService : IOptionHistoryBackfillService
{
    private readonly TradingDbContext _dbContext;
    private readonly IExpiryResolverService _expiryResolverService;
    private readonly IFyersHistoryClient _fyersHistoryClient;
    private readonly IHistoricalCandleStore _historicalCandleStore;

    public OptionHistoryBackfillService(
        TradingDbContext dbContext,
        IExpiryResolverService expiryResolverService,
        IFyersHistoryClient fyersHistoryClient,
        IHistoricalCandleStore historicalCandleStore)
    {
        _dbContext = dbContext;
        _expiryResolverService = expiryResolverService;
        _fyersHistoryClient = fyersHistoryClient;
        _historicalCandleStore = historicalCandleStore;
    }

    public async Task<BackfillOptionHistoryResponse> BackfillAsync(
        BackfillOptionHistoryRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

        string exchange = request.Exchange.Trim().ToUpperInvariant();
        string underlying = request.Underlying.Trim().ToUpperInvariant();

        // 1) Resolve expiry
        DateOnly expiryDate = await ResolveExpiryDateAsync(exchange, underlying, request, cancellationToken);

        // 2) Resolve ATM strike
        decimal atmStrike = ResolveAtmStrike(request);

        // 3) Build strike list
        var strikes = BuildStrikeList(atmStrike, request.StrikeStep, request.StrikeCountEachSide);

        // 4) Resolve contracts from instruments
        var contracts = await ResolveContractsAsync(
            exchange,
            underlying,
            expiryDate,
            strikes,
            request.IncludeCalls,
            request.IncludePuts,
            cancellationToken);

        if (contracts.Count == 0)
        {
            return new BackfillOptionHistoryResponse
            {
                Exchange = exchange,
                Underlying = underlying,
                ExpiryDate = expiryDate,
                AtmStrike = atmStrike,
                Resolution = request.Resolution,
                TotalContractsResolved = 0,
                TotalContractsFetched = 0,
                TotalCandlesInserted = 0,
                TotalCandlesUpdated = 0,
                TotalCandlesSkipped = 0,
                Symbols = new List<string>(),
                Message = "No option contracts were resolved for the requested expiry/strike range."
            };
        }

        // 5) Fetch/save history in chunks
        int totalInserted = 0;
        int totalUpdated = 0;
        int totalSkipped = 0;
        int totalFetchedContracts = 0;
        var symbolList = new List<string>();

        foreach (var contract in contracts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            symbolList.Add(contract.Symbol);

            var chunks = SplitIntoChunks(request.FromUtc, request.ToUtc, daysPerChunk: 30);

            foreach (var chunk in chunks)
            {
                var bars = await _fyersHistoryClient.GetHistoryAsync(
                    contract.Symbol,
                    request.Resolution,
                    chunk.FromUtc,
                    chunk.ToUtc,
                    cancellationToken);

                if (bars.Count == 0)
                    continue;

                var upsertResult = await _historicalCandleStore.UpsertAsync(
                    contract.Symbol,
                    request.Resolution,
                    bars,
                    cancellationToken);

                totalInserted += upsertResult.Inserted;
                totalUpdated += upsertResult.Updated;
                totalSkipped += upsertResult.Skipped;
            }

            totalFetchedContracts++;
        }

        return new BackfillOptionHistoryResponse
        {
            Exchange = exchange,
            Underlying = underlying,
            ExpiryDate = expiryDate,
            AtmStrike = atmStrike,
            Resolution = request.Resolution,
            TotalContractsResolved = contracts.Count,
            TotalContractsFetched = totalFetchedContracts,
            TotalCandlesInserted = totalInserted,
            TotalCandlesUpdated = totalUpdated,
            TotalCandlesSkipped = totalSkipped,
            Symbols = symbolList,
            Message = "Option history backfill completed successfully."
        };
    }

    private static void ValidateRequest(BackfillOptionHistoryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Exchange))
            throw new InvalidOperationException("Exchange is required.");

        if (string.IsNullOrWhiteSpace(request.Underlying))
            throw new InvalidOperationException("Underlying is required.");

        if (string.IsNullOrWhiteSpace(request.Resolution))
            throw new InvalidOperationException("Resolution is required.");

        if (request.FromUtc >= request.ToUtc)
            throw new InvalidOperationException("FromUtc must be earlier than ToUtc.");

        if (request.StrikeCountEachSide < 0)
            throw new InvalidOperationException("StrikeCountEachSide cannot be negative.");

        if (request.StrikeStep <= 0)
            throw new InvalidOperationException("StrikeStep must be greater than zero.");

        if (!request.IncludeCalls && !request.IncludePuts)
            throw new InvalidOperationException("At least one of IncludeCalls or IncludePuts must be true.");
    }

    private async Task<DateOnly> ResolveExpiryDateAsync(
        string exchange,
        string underlying,
        BackfillOptionHistoryRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ExpiryDate.HasValue)
            return request.ExpiryDate.Value;

        var resolved = await _expiryResolverService.ResolvePreferredExpiryAsync(
            exchange,
            underlying,
            DateTime.UtcNow,
            cancellationToken);

        if (resolved is null)
            throw new InvalidOperationException(
                $"Could not resolve preferred expiry for exchange='{exchange}', underlying='{underlying}'.");

        return resolved.ExpiryDate;
    }

    private static decimal ResolveAtmStrike(BackfillOptionHistoryRequest request)
    {
        if (request.AtmStrike.HasValue)
            return request.AtmStrike.Value;

        if (!request.UnderlyingPrice.HasValue)
            throw new InvalidOperationException(
                "Either AtmStrike or UnderlyingPrice must be provided in this first version.");

        var price = request.UnderlyingPrice.Value;
        var step = request.StrikeStep;

        return Math.Ceiling(price / step) * step;
    }

    private static List<decimal> BuildStrikeList(
        decimal atmStrike,
        decimal strikeStep,
        int strikeCountEachSide)
    {
        var strikes = new List<decimal>();

        for (int i = -strikeCountEachSide; i <= strikeCountEachSide; i++)
        {
            strikes.Add(atmStrike + (i * strikeStep));
        }

        return strikes
            .Distinct()
            .OrderBy(x => x)
            .ToList();
    }

    private async Task<List<ResolvedOptionContract>> ResolveContractsAsync(
        string exchange,
        string underlying,
        DateOnly expiryDate,
        IReadOnlyList<decimal> strikes,
        bool includeCalls,
        bool includePuts,
        CancellationToken cancellationToken)
    {
        var optionTypes = new List<string>();

        if (includeCalls)
            optionTypes.Add("CE");

        if (includePuts)
            optionTypes.Add("PE");

        var rows = await _dbContext.Instruments
            .AsNoTracking()
            .Where(x =>
                x.IsEnabled &&
                x.Exchange == exchange &&
                x.Underlying == underlying &&
                x.ExpiryDate == expiryDate &&
                x.StrikePrice.HasValue &&
                strikes.Contains(x.StrikePrice.Value) &&
                optionTypes.Contains(x.OptionType))
            .Select(x => new ResolvedOptionContract
            {
                Symbol = x.Symbol,
                StrikePrice = x.StrikePrice!.Value,
                OptionType = x.OptionType
            })
            .OrderBy(x => x.StrikePrice)
            .ThenBy(x => x.OptionType)
            .ToListAsync(cancellationToken);

        return rows;
    }

    private static List<DateRangeChunk> SplitIntoChunks(
        DateTime fromUtc,
        DateTime toUtc,
        int daysPerChunk)
    {
        var chunks = new List<DateRangeChunk>();

        var current = fromUtc;

        while (current < toUtc)
        {
            var next = current.AddDays(daysPerChunk);
            if (next > toUtc)
                next = toUtc;

            chunks.Add(new DateRangeChunk
            {
                FromUtc = current,
                ToUtc = next
            });

            current = next;
        }

        return chunks;
    }

    private sealed class ResolvedOptionContract
    {
        public string Symbol { get; set; } = string.Empty;
        public decimal StrikePrice { get; set; }
        public string OptionType { get; set; } = string.Empty;
    }

    private sealed class DateRangeChunk
    {
        public DateTime FromUtc { get; set; }
        public DateTime ToUtc { get; set; }
    }
}
