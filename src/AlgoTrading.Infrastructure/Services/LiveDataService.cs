using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.Providers;
using AlgoTrading.Contracts.LiveData;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using Prometheus;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// Core service for managing real-time data flow. 
/// Handles saving incoming ticks, building 1-minute live bars, updating the latest quotes, and managing the active watchlist.
/// </summary>
public class LiveDataService : ILiveDataService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _symbolLocks = new();
    
    private static readonly Histogram TickProcessingLatency = Metrics.CreateHistogram(
        "algotrading_tick_processing_latency_seconds",
        "Time taken to process a market tick from exchange generation to persistence",
        new HistogramConfiguration { Buckets = Histogram.ExponentialBuckets(0.001, 2, 10) });

    private readonly TradingDbContext _dbContext;
    private readonly IMarketTickArchiveQueue _marketTickArchiveQueue;
    private readonly IProviderCatalog _providerCatalog;

    /// <summary>Lazily resolved once; the catalog is static metadata, not a query.</summary>
    private string? _defaultSourceKey;

    public LiveDataService(
        TradingDbContext dbContext,
        IMarketTickArchiveQueue marketTickArchiveQueue,
        IProviderCatalog providerCatalog)
    {
        _dbContext = dbContext;
        _marketTickArchiveQueue = marketTickArchiveQueue;
        _providerCatalog = providerCatalog;
    }

    /// <summary>
    /// Data lineage for the live feed. The ingestor is the authority; until it
    /// says (the phase that makes the Python feed provider-aware), the API stamps
    /// the single connector that claims a live feed. With more than one candidate
    /// it stamps nothing — an unattributed row is honest, a guessed one is not.
    /// </summary>
    private string ResolveSourceKey(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return requested.Trim();
        }

        if (_defaultSourceKey is null)
        {
            var candidates = _providerCatalog.Descriptors
                .Where(x => x.Capabilities.LiveTicks)
                .Select(x => x.Key)
                .Take(2)
                .ToList();

            _defaultSourceKey = candidates.Count == 1 ? candidates[0] : string.Empty;
        }

        return _defaultSourceKey;
    }
     
    public async Task<IReadOnlyList<LiveWatchlistItem>> GetWatchlistAsync(
        CancellationToken cancellationToken = default)
    {
        var items = await _dbContext.LiveWatchlistItems
            .OrderByDescending(x => x.Priority)
            .ThenBy(x => x.Symbol)
            .ToListAsync(cancellationToken);

        var expiredItems = new List<LiveWatchlistItem>();
        var activeItems = new List<LiveWatchlistItem>();

        foreach (var item in items)
        {
            if (IsExpired(item.Symbol))
            {
                expiredItems.Add(item);
            }
            else
            {
                activeItems.Add(item);
            }
        }

        if (expiredItems.Any())
        {
            _dbContext.LiveWatchlistItems.RemoveRange(expiredItems);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return activeItems;
    }

    private static bool IsExpired(string symbol)
    {
        // Example: NSE:BANKNIFTY26AUG57600CE or BANKNIFTY26AUG57600CE
        var match = Regex.Match(symbol, @"(\d{2})(JAN|FEB|MAR|APR|MAY|JUN|JUL|AUG|SEP|OCT|NOV|DEC)", RegexOptions.IgnoreCase);
        if (!match.Success) return false; // Not a standard option/futures format

        var yearStr = match.Groups[1].Value;
        var monthStr = match.Groups[2].Value;

        if (int.TryParse(yearStr, out int year) && DateTime.TryParseExact(monthStr, "MMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsedMonth))
        {
            year += 2000;
            // A contract expires near the end of the month. To be safe, we consider it expired 
            // if we are in the next month.
            var expiryMonth = new DateTime(year, parsedMonth.Month, 1).AddMonths(1); 
            if (DateTime.UtcNow >= expiryMonth)
            {
                return true;
            }
        }

        return false;
    }

    public async Task<LiveWatchlistItem> UpsertWatchlistItemAsync(
        UpsertWatchlistItemRequest request,
        CancellationToken cancellationToken = default)
    {
        var existing = await _dbContext.LiveWatchlistItems
            .FirstOrDefaultAsync(x => x.Symbol == request.Symbol, cancellationToken);

        if (existing is null)
        {
            existing = new LiveWatchlistItem
            {
                Symbol = request.Symbol,
                DataType = request.DataType,
                IsActive = request.IsActive,
                Priority = request.Priority,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };

            await _dbContext.LiveWatchlistItems.AddAsync(existing, cancellationToken);
        }
        else
        {
            existing.DataType = request.DataType;
            existing.IsActive = request.IsActive;
            existing.Priority = request.Priority;
            existing.UpdatedUtc = DateTime.UtcNow;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public async Task RemoveWatchlistItemAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        var existing = await _dbContext.LiveWatchlistItems
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (existing is null)
            return;

        _dbContext.LiveWatchlistItems.Remove(existing);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<LiveQuoteResponse?> GetLatestQuoteAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        var row = await _dbContext.LiveQuotesLatest
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Symbol == symbol, cancellationToken);

        if (row is null)
            return null;

        return Map(row);
    }

    public async Task<IReadOnlyList<LiveQuoteResponse>> GetAllLatestQuotesAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await _dbContext.LiveQuotesLatest
            .AsNoTracking()
            .OrderBy(x => x.Symbol)
            .ToListAsync(cancellationToken);

        return rows.Select(Map).ToList();
    }

    public async Task UpsertLatestQuoteAsync(
        UpsertLiveQuoteRequest request,
        CancellationToken cancellationToken = default)
    {
        string sourceKey = ResolveSourceKey(request.SourceKey);

        var existing = await _dbContext.LiveQuotesLatest
            .FirstOrDefaultAsync(x => x.Symbol == request.Symbol, cancellationToken);

        if (existing is null)
        {
            existing = new LiveQuoteLatest
            {
                Symbol = request.Symbol,
                SourceKey = sourceKey,
                DataType = request.DataType,
                LastTradedPrice = request.LastTradedPrice,
                Open = request.Open,
                High = request.High,
                Low = request.Low,
                Close = request.Close,
                Volume = request.Volume,
                RawPayload = request.RawPayload,
                OpenInterest = request.OpenInterest,
                ImpliedVolatility = request.ImpliedVolatility,
                Delta = request.Delta,
                Gamma = request.Gamma,
                Theta = request.Theta,
                Vega = request.Vega,
                ExchangeTimestampUtc = request.ExchangeTimestampUtc?.ToUniversalTime(),
                UpdatedUtc = DateTime.UtcNow
            };

            await _dbContext.LiveQuotesLatest.AddAsync(existing, cancellationToken);
        }
        else
        {
            // Refuse to go backwards in time.
            //
            // The per-symbol lock a few lines up serialises writers; it does not
            // order them. Five executor threads feed this, so during a backlog a
            // tick that left the exchange first can arrive second, win the lock
            // and overwrite a newer price — leaving a stale quote on record with
            // UpdatedUtc claiming it is current. A strategy then prices a leg
            // off it and nothing anywhere looks wrong.
            //
            // Only enforced when both sides carry an exchange stamp. Without one
            // there is no order to preserve and last-writer-wins is all there is.
            var incomingExchangeUtc = request.ExchangeTimestampUtc?.ToUniversalTime();
            if (incomingExchangeUtc is not null
                && existing.ExchangeTimestampUtc is not null
                && incomingExchangeUtc < existing.ExchangeTimestampUtc)
            {
                return;
            }

            existing.ExchangeTimestampUtc = incomingExchangeUtc ?? existing.ExchangeTimestampUtc;
            existing.DataType = request.DataType;
            existing.LastTradedPrice = request.LastTradedPrice;
            existing.Open = request.Open;
            existing.High = request.High;
            existing.Low = request.Low;
            existing.Close = request.Close;
            existing.Volume = request.Volume;
            existing.RawPayload = request.RawPayload;
            existing.OpenInterest = request.OpenInterest;
            existing.ImpliedVolatility = request.ImpliedVolatility;
            existing.Delta = request.Delta;
            existing.Gamma = request.Gamma;
            existing.Theta = request.Theta;
            existing.Vega = request.Vega;
            existing.SourceKey = sourceKey;
            existing.UpdatedUtc = DateTime.UtcNow;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task UpsertHeartbeatAsync(
        UpsertHeartbeatRequest request,
        CancellationToken cancellationToken = default)
    {
        var existing = await _dbContext.LiveIngestorStatuses
            .FirstOrDefaultAsync(x => x.SourceName == request.SourceName, cancellationToken);

        string subscribedSymbolsJson = JsonSerializer.Serialize(request.CurrentSubscribedSymbols);

        if (existing is null)
        {
            existing = new LiveIngestorStatus
            {
                SourceName = request.SourceName,
                Status = request.Status,
                LastHeartbeatUtc = request.LastHeartbeatUtc,
                LastWatchlistRefreshUtc = request.LastWatchlistRefreshUtc,
                CurrentSubscribedSymbolsJson = subscribedSymbolsJson,
                LastError = request.LastError,
                UpdatedUtc = DateTime.UtcNow
            };

            await _dbContext.LiveIngestorStatuses.AddAsync(existing, cancellationToken);
        }
        else
        {
            existing.Status = request.Status;
            existing.LastHeartbeatUtc = request.LastHeartbeatUtc;
            existing.LastWatchlistRefreshUtc = request.LastWatchlistRefreshUtc;
            existing.CurrentSubscribedSymbolsJson = subscribedSymbolsJson;
            existing.LastError = request.LastError;
            existing.UpdatedUtc = DateTime.UtcNow;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IngestorStatusResponse?> GetIngestorStatusAsync(
        string sourceName,
        CancellationToken cancellationToken = default)
    {
        var row = await _dbContext.LiveIngestorStatuses
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.SourceName == sourceName, cancellationToken);

        if (row is null)
            return null;

        var status = MapIngestorStatus(row);
        status.ProcessId = await ReadIngestorPidAsync(cancellationToken);
        return status;
    }

    public async Task<IReadOnlyList<IngestorStatusResponse>> GetAllIngestorStatusesAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await _dbContext.LiveIngestorStatuses
            .AsNoTracking()
            .OrderBy(x => x.SourceName)
            .ToListAsync(cancellationToken);

        var statuses = rows.Select(MapIngestorStatus).ToList();
        if (statuses.Count > 0)
        {
            // One ingestor process today; the stored pid applies to every source it reports.
            var pid = await ReadIngestorPidAsync(cancellationToken);
            foreach (var s in statuses) s.ProcessId = pid;
        }
        return statuses;
    }

    /// <summary>The ingestor pid recorded by its launch / heartbeat (system_settings), or null.</summary>
    private async Task<int?> ReadIngestorPidAsync(CancellationToken cancellationToken)
    {
        var raw = await _dbContext.SystemSettings
            .AsNoTracking()
            .Where(x => x.Key == SystemSettingKeys.IngestorPid)
            .Select(x => x.Value)
            .FirstOrDefaultAsync(cancellationToken);

        return int.TryParse(raw, out var pid) && pid > 0 ? pid : null;
    }

    public async Task<IReadOnlyList<StaleQuoteResponse>> GetStaleQuotesAsync(
        int staleAfterSeconds,
        CancellationToken cancellationToken = default)
    {
        var threshold = DateTime.UtcNow.AddSeconds(-staleAfterSeconds);

        var activeSymbols = await _dbContext.LiveWatchlistItems
            .Where(x => x.IsActive)
            .Select(x => x.Symbol)
            .ToListAsync(cancellationToken);

        var rows = await _dbContext.LiveQuotesLatest
            .AsNoTracking()
            .Where(x => x.UpdatedUtc < threshold && activeSymbols.Contains(x.Symbol))
            .OrderBy(x => x.Symbol)
            .ToListAsync(cancellationToken);

        return rows.Select(x => new StaleQuoteResponse
        {
            Symbol = x.Symbol,
            DataType = x.DataType,
            LastTradedPrice = x.LastTradedPrice,
            UpdatedUtc = x.UpdatedUtc,
            AgeSeconds = (int)(DateTime.UtcNow - x.UpdatedUtc).TotalSeconds
        }).ToList();
    }

    // NEW
    public async Task AppendLiveTickAsync(
        UpsertLiveTickRequest request,
        CancellationToken cancellationToken = default)
    {
        var semaphore = _symbolLocks.GetOrAdd(request.Symbol ?? "UNKNOWN", _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);

        try
        {
            var nowUtc = DateTime.UtcNow;
            string sourceKey = ResolveSourceKey(request.SourceKey);

            if (request.ExchangeTimestampUtc.HasValue)
            {
                var latency = (nowUtc - request.ExchangeTimestampUtc.Value).TotalSeconds;
                if (latency > 0)
                {
                    TickProcessingLatency.Observe(latency);
                }
            }

            // Load current latest quote BEFORE updating it, to calculate volume delta
        var existingLatest = await _dbContext.LiveQuotesLatest
            .FirstOrDefaultAsync(x => x.Symbol == request.Symbol, cancellationToken);

        long volumeDelta = 0;
        if (request.Volume.HasValue && existingLatest?.Volume.HasValue == true)
        {
            var diff = request.Volume.Value - existingLatest.Volume.Value;
            if (diff > 0)
                volumeDelta = diff;
        }

        // 1) Append tick row
        var tick = new LiveTick
        {
            Symbol = request.Symbol,
            DataType = request.DataType,
            ReceivedUtc = nowUtc,
            ExchangeTimestampUtc = request.ExchangeTimestampUtc,
            LastTradedPrice = request.LastTradedPrice,
            BidPrice = request.BidPrice,
            AskPrice = request.AskPrice,
            BidSize = request.BidSize,
            AskSize = request.AskSize,
            Open = request.Open,
            High = request.High,
            Low = request.Low,
            PrevClose = request.PrevClose,
            Volume = request.Volume,
            RawPayload = request.RawPayload,
            SourceKey = sourceKey
        };

        await _dbContext.LiveTicks.AddAsync(tick, cancellationToken);

        // 2) Upsert latest quote snapshot
        await UpsertLatestQuoteAsync(new UpsertLiveQuoteRequest
        {
            Symbol = request.Symbol,
            DataType = request.DataType,
            LastTradedPrice = request.LastTradedPrice,
            Open = request.Open,
            High = request.High,
            Low = request.Low,
            Close = request.PrevClose,
            Volume = request.Volume,
            RawPayload = request.RawPayload,
            // Carried through rather than dropped. The snapshot is what every
            // strategy and screen reads; a quote without its greeks is the
            // reason IV read null everywhere while it was being computed fine.
            OpenInterest = request.OpenInterest,
            ImpliedVolatility = request.ImpliedVolatility,
            Delta = request.Delta,
            Gamma = request.Gamma,
            Theta = request.Theta,
            Vega = request.Vega,
            // Lets the snapshot refuse an out-of-order tick.
            ExchangeTimestampUtc = request.ExchangeTimestampUtc,
            SourceKey = sourceKey
        }, cancellationToken);

        // Every symbol goes through the batched archive writer, not just one.
        // The batching machinery (250 rows / 500ms) was built and then gated to
        // NSE:NIFTYBANK-INDEX, so eighteen other symbols each took a synchronous
        // single-row insert instead — the good path served one symbol and the
        // rest took the slow one.
        {
            await _marketTickArchiveQueue.EnqueueAsync(
                new MarketTickArchiveRequest
                {
                    Symbol = request.Symbol,
                    DataType = request.DataType,
                    ExchangeTimestampUtc = request.ExchangeTimestampUtc,
                    LastTradedPrice = request.LastTradedPrice,
                    BidPrice = request.BidPrice,
                    AskPrice = request.AskPrice,
                    BidSize = request.BidSize,
                    AskSize = request.AskSize,
                    Open = request.Open,
                    High = request.High,
                    Low = request.Low,
                    PrevClose = request.PrevClose,
                    Volume = request.Volume,
                    RawPayload = request.RawPayload,
                    SourceKey = sourceKey
                },
                cancellationToken);
        }

        // 3) Upsert 1-minute bar
        //
        // Not for a tick the exchange stamped at midnight. Before its first
        // trade of the day BSE replays the previous close with a date-only
        // stamp (00:00:00 IST); filed by that clock it became a one-price bar at
        // midnight on nine SENSEX contracts every session (2026-09-08). The tick
        // itself is kept above — the price is real, the minute is not.
        if (request.LastTradedPrice.HasValue && !IsDateOnlyStamp(request.ExchangeTimestampUtc))
        {
            // Bucketed by the EXCHANGE's clock, not ours.
            //
            // This used to floor `nowUtc` — the moment the API happened to
            // receive the tick. While the pipeline keeps up the two agree to
            // within a second and nobody notices. When it falls behind they do
            // not: on 2026-09-04 the feed drifted to 31 minutes late, and every
            // one of those ticks was filed under the minute it arrived. The
            // prices were real and the timestamps were invented, so a strategy
            // reading the 1m series saw half an hour of the session compressed
            // into the wrong candles. The same method already reads this field
            // eighty lines above to measure the delay it was then ignoring.
            //
            // Falls back to arrival time when the feed omits an exchange stamp,
            // which is no worse than before.
            var barClockUtc = request.ExchangeTimestampUtc?.ToUniversalTime() ?? nowUtc;

            var barStartUtc = new DateTime(
                barClockUtc.Year,
                barClockUtc.Month,
                barClockUtc.Day,
                barClockUtc.Hour,
                barClockUtc.Minute,
                0,
                DateTimeKind.Utc);

            var existingBar = await _dbContext.LiveBars
                .FirstOrDefaultAsync(x =>
                    x.Symbol == request.Symbol &&
                    x.Resolution == "1m" &&
                    x.BarStartUtc == barStartUtc,
                    cancellationToken);

            if (existingBar is null)
            {
                existingBar = new LiveBar
                {
                    Symbol = request.Symbol,
                    Resolution = "1m",
                    BarStartUtc = barStartUtc,
                    Open = request.LastTradedPrice.Value,
                    High = request.LastTradedPrice.Value,
                    Low = request.LastTradedPrice.Value,
                    Close = request.LastTradedPrice.Value,
                    VolumeDelta = volumeDelta,
                    TickCount = 1,
                    UpdatedUtc = nowUtc,
                    SourceKey = sourceKey
                };

                await _dbContext.LiveBars.AddAsync(existingBar, cancellationToken);
            }
            else
            {
                var ltp = request.LastTradedPrice.Value;

                if (ltp > existingBar.High)
                    existingBar.High = ltp;

                if (ltp < existingBar.Low)
                    existingBar.Low = ltp;

                existingBar.Close = ltp;
                existingBar.VolumeDelta += volumeDelta;
                existingBar.TickCount += 1;
                existingBar.UpdatedUtc = nowUtc;
                existingBar.SourceKey = sourceKey;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Stores many ticks with a fixed number of database round-trips instead of
    /// a fixed number per tick.
    /// </summary>
    /// <remarks>
    /// <see cref="AppendLiveTickAsync"/> costs five round-trips for one price:
    /// it reads <c>live_quotes_latest</c> for the volume delta, reads it a second
    /// time inside <see cref="UpsertLatestQuoteAsync"/>, saves there, reads the
    /// current 1-minute bar, and saves again. Measured on 2026-09-07 that is
    /// ~30ms per tick, and this feed produces 39 a second across 26 symbols — so
    /// the writer sat level with the feed and any extra load (seven live runs
    /// polling and a risk sweep every three seconds) tipped it into a backlog
    /// that never drained. The console showed it as "36s ago"; the database
    /// showed the gap between exchange stamp and stored row growing from 0.9s to
    /// over a minute within five minutes of the open.
    ///
    /// Concurrency was not the answer: posting the same hundred ticks spread over
    /// a hundred symbols took the same time as a hundred ticks on one symbol, so
    /// the per-symbol lock was never the constraint — the per-tick work was.
    ///
    /// This reads every quote the batch touches in one query, every bar it
    /// touches in a second, applies all of it in memory, and saves once. A batch
    /// of fifty costs three round-trips rather than two hundred and fifty.
    ///
    /// Locks are taken for every symbol in the batch up front, in sorted order so
    /// two overlapping batches cannot deadlock, and held until the save: the
    /// single-tick path stages its work under the same lock, and releasing early
    /// would let two writers both find no bar for a minute and both insert one.
    /// </remarks>
    public async Task AppendLiveTicksAsync(
        IReadOnlyList<UpsertLiveTickRequest> requests,
        CancellationToken cancellationToken = default)
    {
        var batch = requests.Where(r => !string.IsNullOrWhiteSpace(r.Symbol)).ToList();
        if (batch.Count == 0) return;

        // Sorted: a consistent acquisition order is what makes overlapping
        // batches safe to hold several locks at once.
        var symbols = batch.Select(r => r.Symbol!).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
        var held = new List<SemaphoreSlim>(symbols.Count);

        try
        {
            foreach (var symbol in symbols)
            {
                var gate = _symbolLocks.GetOrAdd(symbol, _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(cancellationToken);
                held.Add(gate);
            }

            var nowUtc = DateTime.UtcNow;

            foreach (var request in batch)
            {
                if (request.ExchangeTimestampUtc.HasValue)
                {
                    var latency = (nowUtc - request.ExchangeTimestampUtc.Value).TotalSeconds;
                    if (latency > 0) TickProcessingLatency.Observe(latency);
                }
            }

            var quotes = await _dbContext.LiveQuotesLatest
                .Where(x => symbols.Contains(x.Symbol))
                .ToListAsync(cancellationToken);
            var quoteBySymbol = quotes.ToDictionary(x => x.Symbol, StringComparer.Ordinal);

            // Bar identity is (symbol, minute). EF cannot translate a tuple
            // contains, so both sides are filtered server-side and the exact
            // pairs are matched here.
            var minutes = batch
                .Where(r => r.LastTradedPrice.HasValue)
                .Select(r => FloorToMinute(r.ExchangeTimestampUtc?.ToUniversalTime() ?? nowUtc))
                .Distinct()
                .ToList();

            var bars = minutes.Count == 0
                ? new List<LiveBar>()
                : await _dbContext.LiveBars
                    .Where(x => x.Resolution == "1m"
                        && symbols.Contains(x.Symbol)
                        && minutes.Contains(x.BarStartUtc))
                    .ToListAsync(cancellationToken);

            var barByKey = bars.ToDictionary(x => (x.Symbol, x.BarStartUtc));

            foreach (var request in batch)
            {
                string symbol = request.Symbol!;
                string sourceKey = ResolveSourceKey(request.SourceKey);
                quoteBySymbol.TryGetValue(symbol, out var latest);

                long volumeDelta = 0;
                if (request.Volume.HasValue && latest?.Volume.HasValue == true)
                {
                    var diff = request.Volume.Value - latest.Volume.Value;
                    if (diff > 0) volumeDelta = diff;
                }

                _dbContext.LiveTicks.Add(new LiveTick
                {
                    Symbol = symbol,
                    DataType = request.DataType,
                    ReceivedUtc = nowUtc,
                    ExchangeTimestampUtc = request.ExchangeTimestampUtc,
                    LastTradedPrice = request.LastTradedPrice,
                    BidPrice = request.BidPrice,
                    AskPrice = request.AskPrice,
                    BidSize = request.BidSize,
                    AskSize = request.AskSize,
                    Open = request.Open,
                    High = request.High,
                    Low = request.Low,
                    PrevClose = request.PrevClose,
                    Volume = request.Volume,
                    RawPayload = request.RawPayload,
                    SourceKey = sourceKey
                });

                ApplyLatestQuote(request, latest, sourceKey, quoteBySymbol);

                await _marketTickArchiveQueue.EnqueueAsync(
                    new MarketTickArchiveRequest
                    {
                        Symbol = symbol,
                        DataType = request.DataType,
                        ExchangeTimestampUtc = request.ExchangeTimestampUtc,
                        LastTradedPrice = request.LastTradedPrice,
                        BidPrice = request.BidPrice,
                        AskPrice = request.AskPrice,
                        BidSize = request.BidSize,
                        AskSize = request.AskSize,
                        Open = request.Open,
                        High = request.High,
                        Low = request.Low,
                        PrevClose = request.PrevClose,
                        Volume = request.Volume,
                        RawPayload = request.RawPayload,
                        SourceKey = sourceKey
                    },
                    cancellationToken);

                if (request.LastTradedPrice.HasValue)
                {
                    ApplyBar(request, sourceKey, nowUtc, volumeDelta, barByKey);
                }
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            for (int i = held.Count - 1; i >= 0; i--)
            {
                held[i].Release();
            }
        }
    }

    private static DateTime FloorToMinute(DateTime moment) =>
        new(moment.Year, moment.Month, moment.Day, moment.Hour, moment.Minute, 0, DateTimeKind.Utc);

    /// <summary>
    /// The in-memory twin of <see cref="UpsertLatestQuoteAsync"/>, including its
    /// refusal to move a snapshot backwards in time.
    /// </summary>
    private void ApplyLatestQuote(
        UpsertLiveTickRequest request,
        LiveQuoteLatest? existing,
        string sourceKey,
        Dictionary<string, LiveQuoteLatest> quoteBySymbol)
    {
        var incomingExchangeUtc = request.ExchangeTimestampUtc?.ToUniversalTime();

        if (existing is null)
        {
            var created = new LiveQuoteLatest
            {
                Symbol = request.Symbol!,
                SourceKey = sourceKey,
                DataType = request.DataType,
                LastTradedPrice = request.LastTradedPrice,
                BidPrice = request.BidPrice,
                AskPrice = request.AskPrice,
                BidSize = request.BidSize,
                AskSize = request.AskSize,
                Open = request.Open,
                High = request.High,
                Low = request.Low,
                Close = request.PrevClose,
                Volume = request.Volume,
                RawPayload = request.RawPayload,
                OpenInterest = request.OpenInterest,
                ImpliedVolatility = request.ImpliedVolatility,
                Delta = request.Delta,
                Gamma = request.Gamma,
                Theta = request.Theta,
                Vega = request.Vega,
                ExchangeTimestampUtc = incomingExchangeUtc,
                UpdatedUtc = DateTime.UtcNow
            };

            _dbContext.LiveQuotesLatest.Add(created);
            // So a later tick for this symbol in the same batch updates this row
            // instead of adding a second one.
            quoteBySymbol[request.Symbol!] = created;
            return;
        }

        if (incomingExchangeUtc is not null
            && existing.ExchangeTimestampUtc is not null
            && incomingExchangeUtc < existing.ExchangeTimestampUtc)
        {
            return;
        }

        existing.ExchangeTimestampUtc = incomingExchangeUtc ?? existing.ExchangeTimestampUtc;
        existing.DataType = request.DataType;
        existing.LastTradedPrice = request.LastTradedPrice;
        existing.BidPrice = request.BidPrice;
        existing.AskPrice = request.AskPrice;
        existing.BidSize = request.BidSize;
        existing.AskSize = request.AskSize;
        existing.Open = request.Open;
        existing.High = request.High;
        existing.Low = request.Low;
        existing.Close = request.PrevClose;
        existing.Volume = request.Volume;
        existing.RawPayload = request.RawPayload;
        existing.OpenInterest = request.OpenInterest;
        existing.ImpliedVolatility = request.ImpliedVolatility;
        existing.Delta = request.Delta;
        existing.Gamma = request.Gamma;
        existing.Theta = request.Theta;
        existing.Vega = request.Vega;
        existing.SourceKey = sourceKey;
        existing.UpdatedUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// The in-memory twin of the 1-minute bar upsert, bucketed by the exchange's
    /// clock for the same reason the single-tick path is.
    /// </summary>
    private void ApplyBar(
        UpsertLiveTickRequest request,
        string sourceKey,
        DateTime nowUtc,
        long volumeDelta,
        Dictionary<(string, DateTime), LiveBar> barByKey)
    {
        var barClockUtc = request.ExchangeTimestampUtc?.ToUniversalTime() ?? nowUtc;
        var barStartUtc = FloorToMinute(barClockUtc);
        var key = (request.Symbol!, barStartUtc);
        var ltp = request.LastTradedPrice!.Value;

        if (!barByKey.TryGetValue(key, out var bar))
        {
            bar = new LiveBar
            {
                Symbol = request.Symbol!,
                Resolution = "1m",
                BarStartUtc = barStartUtc,
                Open = ltp,
                High = ltp,
                Low = ltp,
                Close = ltp,
                VolumeDelta = volumeDelta,
                TickCount = 1,
                UpdatedUtc = nowUtc,
                SourceKey = sourceKey
            };

            _dbContext.LiveBars.Add(bar);
            barByKey[key] = bar;
            return;
        }

        if (ltp > bar.High) bar.High = ltp;
        if (ltp < bar.Low) bar.Low = ltp;
        bar.Close = ltp;
        bar.VolumeDelta += volumeDelta;
        bar.TickCount += 1;
        bar.UpdatedUtc = nowUtc;
        bar.SourceKey = sourceKey;
    }

    public async Task<IReadOnlyList<LiveTickResponse>> GetRecentTicksAsync(
        string symbol,
        int take,
        CancellationToken cancellationToken = default)
    {
        var rows = await _dbContext.LiveTicks
            .AsNoTracking()
            .Where(x => x.Symbol == symbol)
            .OrderByDescending(x => x.ReceivedUtc)
            .Take(take)
            .ToListAsync(cancellationToken);

        return rows.Select(x => new LiveTickResponse
        {
            Symbol = x.Symbol,
            DataType = x.DataType,
            ReceivedUtc = x.ReceivedUtc,
            ExchangeTimestampUtc = x.ExchangeTimestampUtc,
            LastTradedPrice = x.LastTradedPrice,
            BidPrice = x.BidPrice,
            AskPrice = x.AskPrice,
            BidSize = x.BidSize,
            AskSize = x.AskSize,
            Open = x.Open,
            High = x.High,
            Low = x.Low,
            PrevClose = x.PrevClose,
            Volume = x.Volume
        }).ToList();
    }

    public async Task<IReadOnlyList<LiveBarResponse>> GetRecentBarsAsync(
        string symbol,
        string resolution,
        int take,
        CancellationToken cancellationToken = default)
    {
        // Only 1m bars are ever written; higher minute resolutions (5m/15m —
        // what the shipped strategies declare) are aggregated on read.
        // Before this, any non-1m request silently returned an empty list and
        // strategies ran with no bars at all.
        var minutes = ParseResolutionMinutes(resolution);

        if (minutes <= 1)
        {
            var rows = await _dbContext.LiveBars
                .AsNoTracking()
                .Where(x => x.Symbol == symbol && x.Resolution == "1m")
                .OrderByDescending(x => x.BarStartUtc)
                .Take(take)
                .ToListAsync(cancellationToken);

            return rows.Select(Map1mBar).ToList();
        }

        var oneMinuteRows = await _dbContext.LiveBars
            .AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Resolution == "1m")
            .OrderByDescending(x => x.BarStartUtc)
            .Take(take * minutes)
            .ToListAsync(cancellationToken);

        // Bucket on the UTC clock. 5 and 15 both divide 30, so the buckets
        // land on IST (+05:30) candle boundaries too — 09:15, 09:20, … .
        return oneMinuteRows
            .GroupBy(x => FloorToBucket(x.BarStartUtc, minutes))
            .OrderByDescending(g => g.Key)
            .Take(take)
            .Select(g =>
            {
                var ordered = g.OrderBy(x => x.BarStartUtc).ToList();
                return new LiveBarResponse
                {
                    Symbol = symbol,
                    Resolution = $"{minutes}m",
                    BarStartUtc = g.Key,
                    Open = ordered[0].Open,
                    High = ordered.Max(x => x.High),
                    Low = ordered.Min(x => x.Low),
                    Close = ordered[^1].Close,
                    VolumeDelta = ordered.Sum(x => x.VolumeDelta),
                    TickCount = ordered.Sum(x => x.TickCount),
                    UpdatedUtc = ordered.Max(x => x.UpdatedUtc)
                };
            })
            .ToList();
    }

    private static LiveBarResponse Map1mBar(LiveBar x) => new()
    {
        Symbol = x.Symbol,
        Resolution = x.Resolution,
        BarStartUtc = x.BarStartUtc,
        Open = x.Open,
        High = x.High,
        Low = x.Low,
        Close = x.Close,
        VolumeDelta = x.VolumeDelta,
        TickCount = x.TickCount,
        UpdatedUtc = x.UpdatedUtc
    };

    /// <summary>"1m"/"1" → 1, "5m"/"5" → 5, "15m" → 15; anything else → 1.</summary>
    private static int ParseResolutionMinutes(string resolution)
    {
        var r = (resolution ?? "1m").Trim().ToLowerInvariant().TrimEnd('m');
        return int.TryParse(r, out var minutes) && minutes >= 1 ? minutes : 1;
    }

    private static DateTime FloorToBucket(DateTime barStartUtc, int minutes)
    {
        var totalMinutes = (long)(barStartUtc - barStartUtc.Date).TotalMinutes;
        return barStartUtc.Date.AddMinutes(totalMinutes - totalMinutes % minutes);
    }

    private static LiveQuoteResponse Map(LiveQuoteLatest row)
    {
        return new LiveQuoteResponse
        {
            Symbol = row.Symbol,
            DataType = row.DataType,
            LastTradedPrice = row.LastTradedPrice,
            BidPrice = row.BidPrice,
            AskPrice = row.AskPrice,
            BidSize = row.BidSize,
            AskSize = row.AskSize,
            Open = row.Open,
            High = row.High,
            Low = row.Low,
            Close = row.Close,
            Volume = row.Volume,
            OpenInterest = row.OpenInterest,
            ImpliedVolatility = row.ImpliedVolatility,
            Delta = row.Delta,
            Gamma = row.Gamma,
            Theta = row.Theta,
            Vega = row.Vega,
            UpdatedUtc = row.UpdatedUtc,
            ExchangeTimestampUtc = row.ExchangeTimestampUtc
        };
    }

    private static IngestorStatusResponse MapIngestorStatus(LiveIngestorStatus row)
    {
        List<string> symbols;

        try
        {
            symbols = JsonSerializer.Deserialize<List<string>>(row.CurrentSubscribedSymbolsJson) ?? new();
        }
        catch
        {
            symbols = new();
        }

        var ageSeconds = (DateTime.UtcNow - row.LastHeartbeatUtc).TotalSeconds;
        bool isHealthy = ageSeconds <= 15 &&
                         string.Equals(row.Status, "Running", StringComparison.OrdinalIgnoreCase);

        return new IngestorStatusResponse
        {
            SourceName = row.SourceName,
            Status = row.Status,
            LastHeartbeatUtc = row.LastHeartbeatUtc,
            LastWatchlistRefreshUtc = row.LastWatchlistRefreshUtc,
            CurrentSubscribedSymbols = symbols,
            LastError = row.LastError,
            UpdatedUtc = row.UpdatedUtc,
            IsHealthy = isHealthy
        };
    }

    private static bool IsDateOnlyStamp(DateTime? exchangeUtc)
        => exchangeUtc.HasValue && IstTime.IsMidnightIst(exchangeUtc.Value.ToUniversalTime());
}
