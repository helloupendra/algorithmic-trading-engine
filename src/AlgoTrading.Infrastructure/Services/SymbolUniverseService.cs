using System;
using System.Collections.Generic;
using System.Text;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.MarketData;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Infrastructure.Services
{
    /// <summary>
    /// Service for querying local symbols and triggering historical data backfills.
    /// Acts as the bridge between local instrument configurations and the broker historical fetcher.
    /// </summary>
    public class SymbolUniverseService : ISymbolUniverseService
    {
        private readonly TradingDbContext _dbContext;
        private readonly IMarketDataService _marketDataService;

        public SymbolUniverseService(
            TradingDbContext dbContext,
            IMarketDataService marketDataService)
        {
            _dbContext = dbContext;
            _marketDataService = marketDataService;
        }

        public async Task<BackfillHistoryResponse> EnsureHistoryCoverageAsync(
            BackfillHistoryRequest request,
            CancellationToken cancellationToken = default)
        {
            // The candles table holds canonical codes ("5", "D"); every lookup and
            // the sync state row use the same spelling the sync writes.
            string resolution = ResolutionCodes.ToCandle(request.Resolution);

            var response = new BackfillHistoryResponse
            {
                Symbol = request.Symbol,
                Resolution = resolution,
                RequestedFromDate = request.FromDate,
                RequestedToDate = request.ToDate,
            };

            var instrument = await _dbContext.Instruments
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Symbol == request.Symbol && x.IsEnabled, cancellationToken);

            response.InstrumentExists = instrument is not null;

            if (instrument is null)
            {
                response.Message = "Instrument not found in local symbol universe.";
                return response;
            }

            var localCandles = await CountLocalCandlesAsync(request, resolution, cancellationToken);

            response.LocalCandlesAvailable = localCandles;


            bool needBackfill = localCandles == 0;

            if (needBackfill)
            {
                var syncRequest = new SyncHistoryRequest
                {
                    Symbol = request.Symbol,
                    Resolution = resolution,
                    DateFormat = request.DateFormat,
                    FromDate = request.FromDate,
                    ToDate = request.ToDate,
                    ContFlag = request.ContFlag
                };

                var fetched = await _marketDataService.SyncHistoryAsync(syncRequest, cancellationToken);
                response.CandlesFetched = fetched.Count;
                response.MissingSlicesFetched.Add($"{request.FromDate:yyyy-MM-dd} -> {request.ToDate:yyyy-MM-dd}");
            }


            response.LocalCandlesAvailable = await CountLocalCandlesAsync(request, resolution, cancellationToken);

            response.FullCoverageAfterBackfill = response.LocalCandlesAvailable > 0;
            response.Message = response.FullCoverageAfterBackfill
                ? "Historical coverage is available lcoally."
                : "Backfill attempted, but local coverage is still incomplete,";

            var state = await _dbContext.SymbolSyncStates
                .FirstOrDefaultAsync(x =>
                    x.Symbol == request.Symbol &&
                    x.Resolution == resolution, cancellationToken);

            if (state is null)
            {
                state = new SymbolSyncState
                {
                    Symbol = request.Symbol,
                    Resolution = resolution
                };
                _dbContext.SymbolSyncStates.Add(state);
            }

            var minTs = await _dbContext.Candles
                .Where(x => x.Symbol == request.Symbol && x.Resolution == resolution)
                .MinAsync(x => (DateTime?)x.TimeStampUtc, cancellationToken);

            var maxTs = await _dbContext.Candles
                .Where(x => x.Symbol == request.Symbol && x.Resolution == resolution)
                .MaxAsync(x => (DateTime?)x.TimeStampUtc, cancellationToken);

            state.EarliestLocalCandleUtc = minTs;
            state.LatestLocalCandleUtc = maxTs;
            state.LastHistoricalSyncUtc = DateTime.UtcNow;
            state.SyncStatus = response.FullCoverageAfterBackfill ? "Synced" : "Partial";
            state.LastError = string.Empty;
            state.UpdatedUtc = DateTime.UtcNow;

            await _dbContext.SaveChangesAsync(cancellationToken);

            return response;
        }

        private Task<int> CountLocalCandlesAsync(BackfillHistoryRequest request, string resolution, CancellationToken cancellationToken)
            => _dbContext.Candles
                .AsNoTracking()
                .Where(x =>
                    x.Symbol == request.Symbol &&
                    x.Resolution == resolution &&
                    x.TimeStampUtc >= request.FromDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) &&
                    x.TimeStampUtc < request.ToDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))
                .CountAsync(cancellationToken);
    }
}
