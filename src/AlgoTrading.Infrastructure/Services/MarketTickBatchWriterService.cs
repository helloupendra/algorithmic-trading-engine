// src/AlgoTrading.Infrastructure/Services/MarketTickBatchWriterService.cs
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Infrastructure.Services;

public class MarketTickBatchWriterService : BackgroundService
{
    private readonly MarketTickArchiveQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MarketTickBatchWriterService> _logger;

    private const int BatchSize = 250;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(500);

    public MarketTickBatchWriterService(
        MarketTickArchiveQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<MarketTickBatchWriterService> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Flushes one batch, and survives it failing.
    /// </summary>
    /// <remarks>
    /// The buffer is cleared either way. Keeping a batch that will not write
    /// means the next flush retries the same rows, fails the same way and grows
    /// without bound — the writer would stay alive and still stop archiving,
    /// which is the worst of both.
    /// </remarks>
    private async Task FlushGuardedAsync(List<MarketTick> buffer, CancellationToken stoppingToken)
    {
        try
        {
            await FlushBatchAsync(buffer, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Dropping a batch of {Count} archive ticks after repeated write failures. " +
                "The writer stays up; live ticks are unaffected.", buffer.Count);
            buffer.Clear();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var buffer = new List<MarketTick>(BatchSize * 2);
        using var timer = new PeriodicTimer(FlushInterval);

        _logger.LogInformation("MarketTickBatchWriterService started.");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await timer.WaitForNextTickAsync(stoppingToken);

                // Drain queue.
                //
                // Every flush is guarded individually. FlushBatchAsync rethrows
                // once its retries are spent, and that exception used to escape
                // both this loop and the `while` above it, into the outer catch
                // — where BackgroundService simply logs and never restarts the
                // service. One bad batch killed the archive writer for the rest
                // of the process's life, and because the queue blocks when full,
                // a dead writer then throttled the live tick path that feeds it.
                // Losing one batch of archive rows is the cheap failure; losing
                // the writer is not.
                while (_queue.Reader.TryRead(out var item))
                {
                    buffer.Add(Map(item));

                    if (buffer.Count >= BatchSize)
                    {
                        await FlushGuardedAsync(buffer, stoppingToken);
                    }
                }

                // Periodic flush of partial batch
                if (buffer.Count > 0)
                {
                    await FlushGuardedAsync(buffer, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("MarketTickBatchWriterService cancellation requested.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MarketTickBatchWriterService failed.");
        }
        finally
        {
            // Final drain on shutdown
            try
            {
                while (_queue.Reader.TryRead(out var item))
                {
                    buffer.Add(Map(item));

                    if (buffer.Count >= BatchSize)
                    {
                        await FlushBatchAsync(buffer, CancellationToken.None);
                    }
                }

                if (buffer.Count > 0)
                {
                    await FlushBatchAsync(buffer, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Final flush failed during MarketTickBatchWriterService shutdown.");
            }

            _logger.LogInformation("MarketTickBatchWriterService stopped.");
        }
    }

    private async Task FlushBatchAsync(List<MarketTick> buffer, CancellationToken cancellationToken)
    {
        if (buffer.Count == 0)
            return;

        var toWrite = buffer.ToList();
        buffer.Clear();

        const int maxRetries = 3;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

                await dbContext.MarketTicks.AddRangeAsync(toWrite, cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);

                _logger.LogDebug("Inserted MarketTick batch of {Count}", toWrite.Count);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to insert MarketTick batch, attempt {Attempt}/{MaxRetries}", attempt, maxRetries);

                if (attempt == maxRetries)
                    throw;

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
    }

    private static MarketTick Map(AlgoTrading.Contracts.LiveData.MarketTickArchiveRequest request)
    {
        return new MarketTick
        {
            Symbol = request.Symbol.Trim().ToUpperInvariant(),
            DataType = string.IsNullOrWhiteSpace(request.DataType)
                ? "symbolUpdate"
                : request.DataType.Trim(),

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
            RawPayload = request.RawPayload ?? string.Empty,

            ReceivedUtc = DateTime.UtcNow
        };
    }
}