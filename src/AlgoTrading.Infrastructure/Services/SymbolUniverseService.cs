using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.MarketData;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Domain.MarketData;
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

            // Read the sync row first: it remembers which dates the broker has
            // already said it holds nothing for, and those must not be counted
            // as gaps or the same holidays are re-requested on every run.
            var state = await _dbContext.SymbolSyncStates
                .FirstOrDefaultAsync(x =>
                    x.Symbol == request.Symbol &&
                    x.Resolution == resolution, cancellationToken);

            var knownEmpty = ParseDates(state?.KnownEmptyDatesCsv);

            // An option only prints when it trades, so bar counts cannot be
            // held against a full session for one.
            var policy = HistoryCoverage.PolicyFor(request.Symbol);

            // Today counts only once its session has closed. Asking the broker
            // for a session that has not happened is not an empty answer — FYERS
            // returns "Something went wrong", which reads like a bad symbol.
            var lastCompleted = LastCompletedSession();

            var barsByDate = await BarsByDateAsync(request, resolution, cancellationToken);
            response.LocalCandlesAvailable = barsByDate.Values.Sum();

            // Coverage is measured a trading day at a time. The old rule — "at
            // least one candle exists in the range" — reported a symbol holding
            // one day out of twenty as fully covered, so it was never completed
            // and every backtest over it crossed the hole in silence.
            var gaps = HistoryCoverage.FindGaps(
                request.FromDate, request.ToDate, resolution, barsByDate, knownEmpty, policy, lastCompleted);

            foreach (var gap in gaps)
            {
                var syncRequest = new SyncHistoryRequest
                {
                    Symbol = request.Symbol,
                    Resolution = resolution,
                    DateFormat = request.DateFormat,
                    FromDate = gap.From,
                    ToDate = gap.To,
                    ContFlag = request.ContFlag
                };

                var fetched = await _marketDataService.SyncHistoryAsync(syncRequest, cancellationToken);
                response.CandlesFetched += fetched.Count;
                response.MissingSlicesFetched.Add(gap.ToString());
            }

            if (gaps.Count > 0)
            {
                barsByDate = await BarsByDateAsync(request, resolution, cancellationToken);
                response.LocalCandlesAvailable = barsByDate.Values.Sum();

                // Anything still empty after being asked for is not a gap the
                // broker can fill — a market holiday, or a contract that had
                // not begun trading. Recording it is what lets coverage ever
                // reach "complete".
                foreach (var day in HistoryCoverage.ExpectedTradingDays(request.FromDate, request.ToDate, lastCompleted))
                {
                    if (!barsByDate.ContainsKey(day)) knownEmpty.Add(day);
                }
            }

            var remaining = HistoryCoverage.FindGaps(
                request.FromDate, request.ToDate, resolution, barsByDate, knownEmpty, policy, lastCompleted);

            var expectedDays = HistoryCoverage
                .ExpectedTradingDays(request.FromDate, request.ToDate, lastCompleted)
                .Where(d => !knownEmpty.Contains(d))
                .ToList();

            response.TradingDaysExpected = expectedDays.Count;
            response.TradingDaysCovered = expectedDays.Count(d =>
                HistoryCoverage.ClassifyDay(
                    barsByDate.TryGetValue(d, out int n) ? n : 0,
                    HistoryCoverage.ExpectedBarsPerDay(resolution), policy) == DayCoverage.Complete);

            response.RemainingGaps = remaining.Select(g => g.ToString()).ToList();
            response.FullCoverageAfterBackfill = remaining.Count == 0;
            response.Message = response.FullCoverageAfterBackfill
                ? $"Full coverage: {response.TradingDaysCovered} of {response.TradingDaysExpected} trading days."
                : $"Incomplete: {response.TradingDaysCovered} of {response.TradingDaysExpected} trading days covered; "
                  + $"still missing {string.Join(", ", response.RemainingGaps)}.";

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
            state.KnownEmptyDatesCsv = FormatDates(knownEmpty);
            state.UpdatedUtc = DateTime.UtcNow;

            await _dbContext.SaveChangesAsync(cancellationToken);

            return response;
        }

        /// <summary>
        /// Bars held locally for each date in the range. Dates with none are
        /// absent rather than zero, which is what the coverage check expects.
        /// </summary>
        private async Task<Dictionary<DateOnly, int>> BarsByDateAsync(
            BackfillHistoryRequest request, string resolution, CancellationToken cancellationToken)
        {
            var rows = await _dbContext.Candles
                .AsNoTracking()
                .Where(x =>
                    x.Symbol == request.Symbol &&
                    x.Resolution == resolution &&
                    x.TimeStampUtc >= request.FromDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) &&
                    x.TimeStampUtc < request.ToDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))
                .GroupBy(x => x.TimeStampUtc.Date)
                .Select(g => new { Date = g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken);

            return rows.ToDictionary(r => DateOnly.FromDateTime(r.Date), r => r.Count);
        }

        /// <summary>
        /// The last day whose trading session has finished, in IST.
        /// </summary>
        /// <remarks>
        /// Before 15:30 IST today's session is still running (or has not begun),
        /// so the newest day that can be complete is yesterday.
        /// </remarks>
        private static DateOnly LastCompletedSession()
        {
            var ist = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, IndiaTimeZone);
            var today = DateOnly.FromDateTime(ist.Date);
            return ist.TimeOfDay >= new TimeSpan(15, 30, 0) ? today : today.AddDays(-1);
        }

        private static readonly TimeZoneInfo IndiaTimeZone = ResolveIndiaTimeZone();

        private static TimeZoneInfo ResolveIndiaTimeZone()
        {
            foreach (var id in new[] { "Asia/Kolkata", "India Standard Time" })
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
                catch (TimeZoneNotFoundException) { }
                catch (InvalidTimeZoneException) { }
            }
            // Fixed offset rather than a throw: IST has no daylight saving, so
            // the fallback is exact, and a missing tz database must not stop a
            // backfill.
            return TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromMinutes(330), "IST", "IST");
        }

        private static HashSet<DateOnly> ParseDates(string? csv)
        {
            var set = new HashSet<DateOnly>();
            if (string.IsNullOrWhiteSpace(csv)) return set;

            foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (DateOnly.TryParse(part, out var date)) set.Add(date);
            }
            return set;
        }

        private static string FormatDates(IEnumerable<DateOnly> dates)
            => string.Join(",", dates.Distinct().OrderBy(d => d).Select(d => d.ToString("yyyy-MM-dd")));
    }
}
