// src/AlgoTrading.Api/Services/BacktestDataService.cs
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Backtest;
using AlgoTrading.Contracts.MarketData;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace AlgoTrading.Api.Services;

/// <summary>
/// What historical data exists for a backtest (coverage-first, before any
/// picker) and the chunked FYERS backfill that fills the gaps. Sessions are
/// distinct IST calendar days; "live" 1-minute bars are reported but are not
/// replayable — the runner reads the candles table only.
/// </summary>
public sealed class BacktestDataService
{
    private const int ChunkDays = 30;

    public const string OptionPremiumNote =
        "Option premiums are fetched from FYERS history per contract on demand; expired contracts have no history — trades on them will be listed as skipped.";
    public const string BrokerNotLinkedNote =
        "Broker not linked: only contracts already stored can be priced, and no index candles can be backfilled.";

    private readonly TradingDbContext _dbContext;
    private readonly StrategyCatalogService _catalog;
    private readonly ILotSizeResolver _lotSizeResolver;
    private readonly IBrokerSessionStore _brokerSessionStore;
    private readonly IMarketDataService _marketDataService;
    private readonly ILogger<BacktestDataService> _logger;

    public BacktestDataService(
        TradingDbContext dbContext,
        StrategyCatalogService catalog,
        ILotSizeResolver lotSizeResolver,
        IBrokerSessionStore brokerSessionStore,
        IMarketDataService marketDataService,
        ILogger<BacktestDataService> logger)
    {
        _dbContext = dbContext;
        _catalog = catalog;
        _lotSizeResolver = lotSizeResolver;
        _brokerSessionStore = brokerSessionStore;
        _marketDataService = marketDataService;
        _logger = logger;
    }

    // ------------------------------------------------------------------
    // Coverage
    // ------------------------------------------------------------------

    public async Task<BacktestCoverageResponse> GetCoverageAsync(
        string underlying,
        int? strategyId,
        string? resolution,
        CancellationToken cancellationToken)
    {
        underlying = underlying.Trim().ToUpperInvariant();
        var spot = UnderlyingCatalog.SpotSymbolFor(underlying);

        var lot = await _lotSizeResolver.ResolveForUnderlyingAsync(underlying, cancellationToken);
        bool brokerLinked = await IsBrokerLinkedAsync(cancellationToken);

        var required = new List<string>();
        StrategyCatalogEntry? strategy = null;
        if (strategyId.HasValue)
        {
            strategy = await _catalog.FindAsync(strategyId.Value, cancellationToken);
            if (strategy is not null)
            {
                required.AddRange(strategy.DataRequirements
                    .Where(r => !string.IsNullOrWhiteSpace(r.Resolution))
                    .Select(r => ResolutionCodes.ToStrategy(r.Resolution)));
            }
        }
        if (!string.IsNullOrWhiteSpace(resolution))
        {
            required.Add(ResolutionCodes.ToStrategy(resolution));
        }
        required = required.Distinct(StringComparer.Ordinal).ToList();
        var requiredCanonical = required.Select(ResolutionCodes.ToCandle).ToHashSet(StringComparer.Ordinal);

        // Count / min / max / distinct IST day are aggregated in SQL (a year of
        // 1m candles is ~94k rows; only four summary rows are needed).
        var candleAggregates = await AggregateCandlesAsync(spot, cancellationToken);
        var byResolution = new Dictionary<string, RangeSummary>(StringComparer.Ordinal);
        foreach (var agg in candleAggregates)
        {
            var code = ResolutionCodes.ToCandle(agg.Resolution);
            var summary = agg.ToSummary();
            byResolution[code] = byResolution.TryGetValue(code, out var existing)
                ? RangeSummary.Merge(existing, summary)
                : summary;
        }

        var live = (await AggregateLiveBarsAsync(spot, cancellationToken))?.ToSummary();

        var rows = new List<BacktestResolutionCoverage>();
        foreach (var code in ResolutionCodes.Allowed)
        {
            var row = new BacktestResolutionCoverage
            {
                Resolution = code,
                Label = ResolutionCodes.Label(code),
                Required = requiredCanonical.Contains(code),
                Backfillable = brokerLinked
            };

            if (byResolution.TryGetValue(code, out var s) && s.BarCount > 0)
            {
                row.BarCount = s.BarCount;
                row.FirstUtc = s.FirstUtc;
                row.LastUtc = s.LastUtc;
                row.Sessions = s.Sessions;
                row.Source = "backfill";
            }
            else if (code == "1" && live is not null)
            {
                row.BarCount = live.BarCount;
                row.FirstUtc = live.FirstUtc;
                row.LastUtc = live.LastUtc;
                row.Sessions = live.Sessions;
                row.Source = "live";
            }

            rows.Add(row);
        }

        var optionCoverage = await GetOptionCoverageAsync(underlying, cancellationToken);

        var notes = new List<string>();
        var lotText = lot.Source == LotSizeInfo.SourceMaster ? "from the instrument master" : $"({lot.Source})";
        notes.Add($"Lot size {lot.LotSize} {lotText} applies to the whole range; historical lot-size changes are not modelled.");
        notes.Add(OptionPremiumNote);
        if (!brokerLinked)
        {
            notes.Add(BrokerNotLinkedNote);
        }
        if (rows.All(r => r.Source != "backfill"))
        {
            notes.Add(brokerLinked
                ? $"No stored {underlying} index candles at any resolution — backfill first."
                : $"No stored {underlying} index candles at any resolution, and the broker is not linked to backfill them.");
        }
        if (rows.Any(r => r.Source == "live"))
        {
            notes.Add("Live 1m bars come from ingestion sessions and cover only the hours the ingestor ran; they are not replayable — backfill 1m candles to use that resolution.");
        }
        foreach (var req in rows.Where(r => r.Required && r.Source != "backfill"))
        {
            notes.Add($"The strategy needs {req.Label} bars and none are stored for {underlying}.");
        }
        if (strategy is not null && !string.IsNullOrWhiteSpace(strategy.Error))
        {
            notes.Add($"{strategy.Name} cannot be loaded: {strategy.Error}");
        }

        return new BacktestCoverageResponse
        {
            Underlying = underlying,
            SpotSymbol = spot,
            LotSize = lot.LotSize,
            LotSizeSource = lot.Source,
            Resolutions = rows,
            RequiredResolutions = required,
            OptionCandles = optionCoverage,
            BrokerLinked = brokerLinked,
            Notes = notes
        };
    }

    private async Task<BacktestOptionCoverage> GetOptionCoverageAsync(string underlying, CancellationToken cancellationToken)
    {
        var rows = await (
                from c in _dbContext.Candles.AsNoTracking()
                join i in _dbContext.Instruments.AsNoTracking() on c.Symbol equals i.Symbol
                where i.Underlying == underlying && (i.OptionType == "CE" || i.OptionType == "PE")
                group c by new { c.Symbol, i.ExpiryDate } into g
                select new
                {
                    g.Key.Symbol,
                    g.Key.ExpiryDate,
                    First = g.Min(x => x.TimeStampUtc),
                    Last = g.Max(x => x.TimeStampUtc)
                })
            .ToListAsync(cancellationToken);

        if (rows.Count == 0) return new BacktestOptionCoverage();

        return new BacktestOptionCoverage
        {
            Symbols = rows.Select(x => x.Symbol).Distinct(StringComparer.Ordinal).Count(),
            FirstUtc = rows.Min(x => x.First),
            LastUtc = rows.Max(x => x.Last),
            Expiries = rows
                .Where(x => x.ExpiryDate.HasValue)
                .Select(x => x.ExpiryDate!.Value)
                .Distinct()
                .OrderBy(x => x)
                .Select(x => x.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .ToList()
        };
    }

    public async Task<bool> IsBrokerLinkedAsync(CancellationToken cancellationToken)
    {
        try
        {
            var session = await _brokerSessionStore.GetCurrentAsync(cancellationToken);
            return session is not null && session.IsAuthenticated;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the broker session; treating the broker as not linked.");
            return false;
        }
    }

    /// <summary>Index candles for the spot symbol at the canonical resolution inside [fromUtc, toUtc].</summary>
    public Task<int> CountIndexCandlesAsync(string spotSymbol, string resolution, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken)
    {
        var canonical = ResolutionCodes.ToCandle(resolution);
        return _dbContext.Candles.AsNoTracking()
            .CountAsync(x => x.Symbol == spotSymbol && x.Resolution == canonical && x.TimeStampUtc >= fromUtc && x.TimeStampUtc <= toUtc, cancellationToken);
    }

    // ------------------------------------------------------------------
    // Backfill
    // ------------------------------------------------------------------

    /// <summary>
    /// Pulls the underlying's spot candles from FYERS in ≤ 30-day chunks per
    /// resolution, skipping chunks whose every session already has candles.
    /// Throws InvalidOperationException (→ 400) when the broker session is missing.
    /// </summary>
    public async Task<BacktestBackfillResponse> BackfillAsync(
        string underlying,
        IReadOnlyList<string> resolutions,
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken)
    {
        if (!await IsBrokerLinkedAsync(cancellationToken))
        {
            throw new InvalidOperationException("No valid broker session — link the broker (Data → Broker) before backfilling.");
        }

        underlying = underlying.Trim().ToUpperInvariant();
        var spot = UnderlyingCatalog.SpotSymbolFor(underlying);
        var today = IstTime.DateOf(DateTime.UtcNow);

        var response = new BacktestBackfillResponse();
        var chunks = SplitIntoChunks(fromDate, toDate);

        foreach (var resolution in resolutions.Select(ResolutionCodes.ToCandle).Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = new BacktestBackfillResolutionResult { Resolution = resolution, Chunks = chunks.Count };

            var stamps = await _dbContext.Candles.AsNoTracking()
                .Where(x => x.Symbol == spot && x.Resolution == resolution
                            && x.TimeStampUtc >= IstTime.StartOfDayUtc(fromDate)
                            && x.TimeStampUtc <= IstTime.EndOfDayUtc(toDate))
                .Select(x => x.TimeStampUtc)
                .ToListAsync(cancellationToken);
            var presentDays = stamps.Select(IstTime.DateOf).ToHashSet();

            foreach (var (chunkFrom, chunkTo) in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int expected = CountWeekdays(chunkFrom, chunkTo < today ? chunkTo : today);
                int present = presentDays.Count(d => d >= chunkFrom && d <= chunkTo);

                // Nothing to fetch for a chunk entirely in the future, or one where
                // every weekday already has candles (exchange holidays keep a chunk
                // "incomplete", which only costs one extra FYERS call).
                if (expected == 0 || present >= expected)
                {
                    result.SkippedChunks++;
                    continue;
                }

                var fetched = await _marketDataService.SyncHistoryAsync(new SyncHistoryRequest
                {
                    Symbol = spot,
                    Resolution = resolution,
                    DateFormat = 1,
                    FromDate = chunkFrom,
                    ToDate = chunkTo,
                    ContFlag = 1
                }, cancellationToken);

                result.CandlesFetched += fetched.Count;
                _logger.LogInformation("Backfilled {Count} {Symbol} candles at {Resolution} for {From}..{To}.",
                    fetched.Count, spot, resolution, chunkFrom, chunkTo);
            }

            response.PerResolution.Add(result);
        }

        int totalFetched = response.PerResolution.Sum(x => x.CandlesFetched);
        int totalSkipped = response.PerResolution.Sum(x => x.SkippedChunks);
        var labels = string.Join(", ", response.PerResolution.Select(x => ResolutionCodes.Label(x.Resolution)));
        response.Message = totalFetched > 0
            ? $"Fetched {totalFetched} {underlying} candles ({labels}) for {fromDate:yyyy-MM-dd} → {toDate:yyyy-MM-dd}; {totalSkipped} chunk(s) were already covered."
            : $"Nothing new for {underlying} ({labels}) in {fromDate:yyyy-MM-dd} → {toDate:yyyy-MM-dd}: {totalSkipped} chunk(s) already covered, FYERS returned no further candles.";

        return response;
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    internal sealed record RangeSummary(long BarCount, DateTime FirstUtc, DateTime LastUtc, int Sessions)
    {
        /// <summary>
        /// Two spellings of one resolution ("5" and "5M") folding into one row;
        /// sessions are summed, which can only over-count when both spellings
        /// hold the same day — a transitional state the backfill never creates.
        /// </summary>
        public static RangeSummary Merge(RangeSummary a, RangeSummary b)
            => new(a.BarCount + b.BarCount,
                   a.FirstUtc < b.FirstUtc ? a.FirstUtc : b.FirstUtc,
                   a.LastUtc > b.LastUtc ? a.LastUtc : b.LastUtc,
                   a.Sessions + b.Sessions);
    }

    /// <summary>One SQL aggregate row per stored resolution of a symbol (unmapped query type).</summary>
    public sealed class CandleAggregateRow
    {
        public string Resolution { get; set; } = string.Empty;
        public long BarCount { get; set; }
        public DateTime FirstUtc { get; set; }
        public DateTime LastUtc { get; set; }
        public int Sessions { get; set; }

        internal RangeSummary ToSummary()
            => new(BarCount, DateTime.SpecifyKind(FirstUtc, DateTimeKind.Utc), DateTime.SpecifyKind(LastUtc, DateTimeKind.Utc), Sessions);
    }

    // The IST calendar day of a UTC timestamp: shift by +05:30 and take the date.
    private const string IstDaySql = "((\"{0}\" AT TIME ZONE 'UTC') + INTERVAL '330 minutes')::date";

    private Task<List<CandleAggregateRow>> AggregateCandlesAsync(string spot, CancellationToken cancellationToken)
    {
        var istDay = string.Format(CultureInfo.InvariantCulture, IstDaySql, "TimeStampUtc");
        var sql = $@"SELECT ""Resolution"" AS ""Resolution"",
                            COUNT(*)::bigint AS ""BarCount"",
                            MIN(""TimeStampUtc"") AS ""FirstUtc"",
                            MAX(""TimeStampUtc"") AS ""LastUtc"",
                            COUNT(DISTINCT {istDay})::int AS ""Sessions""
                     FROM candles
                     WHERE ""Symbol"" = {{0}}
                     GROUP BY ""Resolution""";
        return _dbContext.Database.SqlQueryRaw<CandleAggregateRow>(sql, spot).ToListAsync(cancellationToken);
    }

    private async Task<CandleAggregateRow?> AggregateLiveBarsAsync(string spot, CancellationToken cancellationToken)
    {
        var istDay = string.Format(CultureInfo.InvariantCulture, IstDaySql, "BarStartUtc");
        var sql = $@"SELECT ""Resolution"" AS ""Resolution"",
                            COUNT(*)::bigint AS ""BarCount"",
                            MIN(""BarStartUtc"") AS ""FirstUtc"",
                            MAX(""BarStartUtc"") AS ""LastUtc"",
                            COUNT(DISTINCT {istDay})::int AS ""Sessions""
                     FROM live_bars
                     WHERE ""Symbol"" = {{0}} AND ""Resolution"" = {{1}}
                     GROUP BY ""Resolution""";
        var rows = await _dbContext.Database
            .SqlQueryRaw<CandleAggregateRow>(sql, spot, ResolutionCodes.LiveBarResolution)
            .ToListAsync(cancellationToken);
        return rows.FirstOrDefault(r => r.BarCount > 0);
    }

    private static List<(DateOnly From, DateOnly To)> SplitIntoChunks(DateOnly from, DateOnly to)
    {
        var chunks = new List<(DateOnly, DateOnly)>();
        var current = from;
        while (current <= to)
        {
            var end = current.AddDays(ChunkDays - 1);
            if (end > to) end = to;
            chunks.Add((current, end));
            current = end.AddDays(1);
        }
        return chunks;
    }

    private static int CountWeekdays(DateOnly from, DateOnly to)
    {
        int n = 0;
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (d.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday) n++;
        }
        return n;
    }
}
