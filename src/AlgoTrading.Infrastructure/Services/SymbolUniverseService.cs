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
            var response = new BackfillHistoryResponse
            {
                Symbol = request.Symbol,
                Resolution = request.Resolution,
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

            var localCandles = await _dbContext.Candles
                .AsNoTracking()
                .Where(x =>
                    x.Symbol == request.Symbol &&
                    x.Resolution == request.Resolution &&
                    x.TimeStampUtc >= request.FromDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) &&
                    x.TimeStampUtc < request.ToDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))
                .CountAsync(cancellationToken);

            response.LocalCandlesAvailable = localCandles;


            bool needBackfill = localCandles == 0;

            if (needBackfill)
            {
                var syncRequest = new SyncHistoryRequest
                {
                    Symbol = request.Symbol,
                    Resolution = request.Resolution,
                    DateFormat = request.DateFormat,
                    FromDate = request.FromDate,
                    ToDate = request.ToDate,
                    ContFlag = request.ContFlag
                };

                var fetched = await _marketDataService.SyncHistoryAsync(syncRequest, cancellationToken);
                response.CandelsFetchedFromFyers = fetched.Count;
                response.MissingSlicesFetched.Add($"{request.FromDate:yyyy-MM-dd} -> {request.ToDate:yyyy-MM-dd}");
            }


            response.LocalCandlesAvailable = await _dbContext.Candles
                .AsNoTracking()
                .Where(x =>
                    x.Symbol == request.Symbol &&
                    x.Resolution == request.Resolution &&
                    x.TimeStampUtc >= request.FromDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) &&
                    x.TimeStampUtc < request.ToDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))
                .CountAsync(cancellationToken);

            response.FullCoverageAfterBackfill = response.LocalCandlesAvailable > 0;
            response.Message = response.FullCoverageAfterBackfill
                ? "Historical coverage is available lcoally."
                : "Backfill attempted, but local coverage is still incomplete,";

            var state = await _dbContext.SymbolSyncStates
                .FirstOrDefaultAsync(x =>
                    x.Symbol == request.Symbol &&
                    x.Resolution == request.Resolution, cancellationToken);

            if (state is null)
            {
                state = new SymbolSyncState
                {
                    Symbol = request.Symbol,
                    Resolution = request.Resolution
                };
                _dbContext.SymbolSyncStates.Add(state);
            }

            var minTs = await _dbContext.Candles
                .Where(x => x.Symbol == request.Symbol && x.Resolution == request.Resolution)
                .MinAsync(x => (DateTime?)x.TimeStampUtc, cancellationToken);

            var maxTs = await _dbContext.Candles
                .Where(x => x.Symbol == request.Symbol && x.Resolution == request.Resolution)
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
    }
}
