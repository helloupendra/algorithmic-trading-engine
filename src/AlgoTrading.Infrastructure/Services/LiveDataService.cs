using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using AlgoTrading.Application.Interfaces;
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

    public LiveDataService(TradingDbContext dbContext, IMarketTickArchiveQueue marketTickArchiveQueue)
    {
        _dbContext = dbContext;
        _marketTickArchiveQueue = marketTickArchiveQueue;
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
        var existing = await _dbContext.LiveQuotesLatest
            .FirstOrDefaultAsync(x => x.Symbol == request.Symbol, cancellationToken);

        if (existing is null)
        {
            existing = new LiveQuoteLatest
            {
                Symbol = request.Symbol,
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
                UpdatedUtc = DateTime.UtcNow
            };

            await _dbContext.LiveQuotesLatest.AddAsync(existing, cancellationToken);
        }
        else
        {
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
            RawPayload = request.RawPayload
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
            RawPayload = request.RawPayload
        }, cancellationToken);

        if (request.Symbol == "NSE:NIFTYBANK-INDEX")
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
                    RawPayload = request.RawPayload
                },
                cancellationToken);
        }

        // 3) Upsert 1-minute bar
        if (request.LastTradedPrice.HasValue)
        {
            var barStartUtc = new DateTime(
                nowUtc.Year,
                nowUtc.Month,
                nowUtc.Day,
                nowUtc.Hour,
                nowUtc.Minute,
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
                    UpdatedUtc = nowUtc
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
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            semaphore.Release();
        }
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
            UpdatedUtc = row.UpdatedUtc
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
}