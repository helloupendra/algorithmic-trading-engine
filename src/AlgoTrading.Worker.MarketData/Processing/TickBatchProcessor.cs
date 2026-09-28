// src/AlgoTrading.Worker.MarketData/Processing/TickBatchProcessor.cs
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Worker.MarketData.Configuration;
using AlgoTrading.Worker.MarketData.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Worker.MarketData.Processing;

public class TickBatchProcessor : ITickBatchProcessor
{
    private readonly TradingDbContext _dbContext;
    private readonly ILogger<TickBatchProcessor> _logger;
    private readonly RedisStreamOptions _options;

    public TickBatchProcessor(
        TradingDbContext dbContext,
        ILogger<TickBatchProcessor> logger,
        IOptions<RedisStreamOptions> options)
    {
        _dbContext = dbContext;
        _logger = logger;
        _options = options.Value;
    }

    public async Task ProcessAsync(
        IReadOnlyList<MarketTickStreamMessage> messages,
        CancellationToken cancellationToken = default,
        bool redelivered = false)
    {
        if (messages is null || messages.Count == 0)
            return;

        var normalized = messages
            .Where(x => !string.IsNullOrWhiteSpace(x.Symbol))
            .Select(Normalize)
            .ToList();

        if (normalized.Count == 0)
            return;

        var symbols = normalized
            .Select(x => x.Symbol)
            .Distinct()
            .ToList();

        using var tx = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        // 1) Append raw tick archive (Skip replay ticks!)
        var liveTicks = normalized.Where(x => !x.IsReplay).ToList();
        var alreadyStored = 0;
        if (redelivered && liveTicks.Count > 0)
        {
            (liveTicks, alreadyStored) = await DropAlreadyStoredAsync(liveTicks, symbols, cancellationToken);
        }

        if (liveTicks.Count > 0)
        {
            var tickEntities = liveTicks.Select(x => new MarketTick
            {
                Symbol = x.Symbol,
                DataType = x.DataType,
                ExchangeTimestampUtc = x.ExchangeTimestampUtc,
                LastTradedPrice = x.LastTradedPrice,
                BidPrice = x.BidPrice,
                AskPrice = x.AskPrice,
                BidSize = x.BidSize,
                AskSize = x.AskSize,
                Open = x.Open,
                High = x.High,
                Low = x.Low,
                PrevClose = x.Close, // rename if your schema uses Close directly
                Volume = x.Volume,
                RawPayload = x.RawPayload,
                SourceKey = x.SourceKey ?? string.Empty,
                ReceivedUtc = x.ReceivedUtc ?? DateTime.UtcNow
            }).ToList();

            await _dbContext.MarketTicks.AddRangeAsync(tickEntities, cancellationToken);
        }

        // 2) Upsert latest quote projection — only when this worker is the
        // writer (see RedisStreamOptions.ProjectLatestQuotes).
        var refused = 0;
        if (_options.ProjectLatestQuotes)
        {
            refused = await ProjectLatestQuotesAsync(normalized, symbols, cancellationToken);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Processed tick batch of {Count} messages{Redelivered}{AlreadyStored}{Refused}",
            normalized.Count,
            redelivered ? " (redelivered)" : string.Empty,
            alreadyStored > 0 ? $", {alreadyStored} already archived" : string.Empty,
            refused > 0 ? $", {refused} older than the stored quote" : string.Empty);
    }

    /// <summary>
    /// The ticks of a redelivered batch that are not archived yet.
    /// </summary>
    /// <remarks>
    /// A tick is recognised by its symbol, the moment it was published
    /// (<c>receivedUtc</c>, stamped by the feed, or the stream entry's own clock
    /// when it has none) and its exchange stamp. Delivery is at least once: a
    /// write that committed but whose acknowledgement never reached Redis is
    /// delivered again, and without this it would be archived twice.
    /// </remarks>
    private async Task<(List<MarketTickStreamMessage> Remaining, int AlreadyStored)> DropAlreadyStoredAsync(
        List<MarketTickStreamMessage> ticks,
        List<string> symbols,
        CancellationToken cancellationToken)
    {
        var stamps = ticks
            .Where(x => x.ReceivedUtc.HasValue)
            .Select(x => x.ReceivedUtc!.Value)
            .Distinct()
            .ToList();

        if (stamps.Count == 0)
            return (ticks, 0);

        var stored = await _dbContext.MarketTicks
            .AsNoTracking()
            .Where(x => symbols.Contains(x.Symbol) && stamps.Contains(x.ReceivedUtc))
            .Select(x => new { x.Symbol, x.ReceivedUtc, x.ExchangeTimestampUtc })
            .ToListAsync(cancellationToken);

        if (stored.Count == 0)
            return (ticks, 0);

        var seen = stored
            .Select(x => (x.Symbol, x.ReceivedUtc, x.ExchangeTimestampUtc))
            .ToHashSet();

        var remaining = ticks
            .Where(x => !x.ReceivedUtc.HasValue
                        || !seen.Contains((x.Symbol, x.ReceivedUtc.Value, x.ExchangeTimestampUtc)))
            .ToList();

        return (remaining, ticks.Count - remaining.Count);
    }

    /// <summary>
    /// The latest-quote projection, under the API's rule: a tick never moves a
    /// quote backwards in exchange time, unless it is a replay (which runs behind
    /// the stamps the live session wrote on purpose). Returns how many ticks
    /// were refused as older than the stored quote.
    /// </summary>
    /// <remarks>
    /// This used to overwrite whatever it found and stamp <c>UpdatedUtc</c> with
    /// the exchange's clock. A delayed tick could put an older price on record
    /// marked as current, and a replayed one looked hours stale to every reader
    /// that ages a quote by <c>UpdatedUtc</c>. It also added a second row for a
    /// new symbol that ticked twice in one batch, which the unique index refused
    /// — failing the whole batch, which then sat in the pending list forever.
    /// </remarks>
    private async Task<int> ProjectLatestQuotesAsync(
        List<MarketTickStreamMessage> normalized,
        List<string> symbols,
        CancellationToken cancellationToken)
    {
        var existingLatest = await _dbContext.LiveQuotesLatest
            .Where(x => symbols.Contains(x.Symbol))
            .ToListAsync(cancellationToken);

        var existingBySymbol = existingLatest.ToDictionary(x => x.Symbol, x => x);
        var nowUtc = DateTime.UtcNow;
        var refused = 0;

        foreach (var msg in normalized)
        {
            if (!existingBySymbol.TryGetValue(msg.Symbol, out var latest))
            {
                latest = new LiveQuoteLatest
                {
                    Symbol = msg.Symbol,
                    DataType = string.IsNullOrWhiteSpace(msg.DataType) ? "symbolUpdate" : msg.DataType,
                };
                _dbContext.LiveQuotesLatest.Add(latest);
                existingBySymbol[msg.Symbol] = latest;
            }
            else if (!msg.IsReplay
                     && msg.ExchangeTimestampUtc is not null
                     && latest.ExchangeTimestampUtc is not null
                     && msg.ExchangeTimestampUtc < latest.ExchangeTimestampUtc)
            {
                refused++;
                continue;
            }

            latest.DataType = string.IsNullOrWhiteSpace(msg.DataType) ? latest.DataType : msg.DataType;
            latest.ExchangeTimestampUtc = msg.ExchangeTimestampUtc ?? latest.ExchangeTimestampUtc;
            latest.LastTradedPrice = msg.LastTradedPrice;
            latest.BidPrice = msg.BidPrice;
            latest.AskPrice = msg.AskPrice;
            latest.BidSize = (long?)msg.BidSize;
            latest.AskSize = (long?)msg.AskSize;
            latest.Open = msg.Open;
            latest.High = msg.High;
            latest.Low = msg.Low;
            latest.Close = msg.Close;
            latest.Volume = (long?)msg.Volume;
            latest.OpenInterest = (long?)msg.OpenInterest;
            latest.ImpliedVolatility = msg.ImpliedVolatility;
            latest.Delta = msg.Delta;
            latest.Gamma = msg.Gamma;
            latest.Theta = msg.Theta;
            latest.Vega = msg.Vega;
            latest.SourceKey = msg.SourceKey ?? latest.SourceKey;
            // When it was written, as the API stamps it: readers age a quote by
            // this, and the exchange's clock has its own column above.
            latest.UpdatedUtc = nowUtc;
        }

        return refused;
    }

    private static MarketTickStreamMessage Normalize(MarketTickStreamMessage x)
    {
        x.Symbol = x.Symbol.Trim().ToUpperInvariant();

        if (!string.IsNullOrWhiteSpace(x.Exchange))
            x.Exchange = x.Exchange.Trim().ToUpperInvariant();

        if (!string.IsNullOrWhiteSpace(x.DataType))
            x.DataType = x.DataType.Trim();

        x.ReceivedUtc = EnsureUtc(x.ReceivedUtc);
        x.ExchangeTimestampUtc = EnsureUtc(x.ExchangeTimestampUtc);

        return x;
    }

    public static DateTime? EnsureUtc(DateTime? dt)
    {
        if (!dt.HasValue) return null;
        if (dt.Value.Kind == DateTimeKind.Utc) return dt;

        // The feed writes "+00:00", and System.Text.Json turns a stamp with an
        // offset into LOCAL time. Relabelling that as UTC moved every stamp by
        // the host's offset (5h30 on an IST machine) — and the ordering rule
        // above compares these stamps with the ones the API wrote correctly.
        if (dt.Value.Kind == DateTimeKind.Local) return dt.Value.ToUniversalTime();

        // Unspecified: the feed's clock is UTC, so it is taken as UTC.
        return DateTime.SpecifyKind(dt.Value, DateTimeKind.Utc);
    }
}
