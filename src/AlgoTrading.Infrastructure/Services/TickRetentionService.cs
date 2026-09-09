// src/AlgoTrading.Infrastructure/Services/TickRetentionService.cs
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// Trims the raw tick log so the disk cannot fill up under it.
/// </summary>
/// <remarks>
/// <c>live_ticks</c> takes one row per tick, each carrying the broker's full
/// JSON payload, and nothing ever removed them: a million rows and 1.2 GB had
/// accumulated by the time anyone looked. A full disk does not degrade this
/// platform, it stops it — ingestion, the API and the runners all write to the
/// same database.
/// <para>
/// Only the raw log is trimmed. The 1-minute bars, the quote snapshots and the
/// compressed <c>market_ticks</c> archive are the record worth keeping; this
/// table is a debugging aid whose value falls off within days.
/// </para>
/// <para>
/// Deleted in bounded batches rather than one statement. A single DELETE over a
/// million rows takes a long lock on a table the live path is writing to, which
/// would cause the outage it is meant to prevent.
/// </para>
/// </remarks>
public class TickRetentionService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TickRetentionService> _logger;
    private readonly TickRetentionOptions _options;

    public TickRetentionService(
        IServiceScopeFactory scopeFactory,
        ILogger<TickRetentionService> logger,
        TickRetentionOptions options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.RetentionDays <= 0)
        {
            _logger.LogInformation("Tick retention is disabled (RetentionDays <= 0); live_ticks will grow without bound.");
            return;
        }

        // Never on the way up: a restart during market hours must not spend its
        // first seconds deleting rows while the feed is trying to connect.
        try
        {
            await Task.Delay(_options.StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(_options.SweepInterval);

        do
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A failed sweep is a disk that fills a little sooner. It is
                // never a reason to take the service down.
                _logger.LogError(ex, "Tick retention sweep failed; will retry at the next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SweepAsync(CancellationToken stoppingToken)
    {
        // A hypertable ages out by dropping whole day-chunks (add_retention_policy
        // in the LiveTicksHypertable migration): instant, and no dead rows left
        // behind. Deleting rows from it here would only decompress old chunks to
        // remove what the policy is about to drop anyway.
        if (await IsHypertableAsync(stoppingToken))
        {
            _logger.LogDebug("live_ticks is a hypertable; retention is TimescaleDB's drop_chunks policy, nothing to sweep.");
            return;
        }

        var cutoff = DateTime.UtcNow.AddDays(-_options.RetentionDays);
        int removedTotal = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

            int removed = await db.LiveTicks
                .Where(x => x.ReceivedUtc < cutoff)
                .OrderBy(x => x.ReceivedUtc)
                .Take(_options.BatchSize)
                .ExecuteDeleteAsync(stoppingToken);

            removedTotal += removed;

            if (removed < _options.BatchSize) break;

            // Breathe, so the live writers get the table back between batches.
            await Task.Delay(_options.BatchPause, stoppingToken);
        }

        if (removedTotal > 0)
        {
            _logger.LogInformation(
                "Tick retention removed {Count} live_ticks rows older than {Days} days.",
                removedTotal, _options.RetentionDays);
        }
    }

    private async Task<bool> IsHypertableAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            var n = await db.Database
                .SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM timescaledb_information.hypertables WHERE hypertable_name = 'live_ticks'")
                .FirstOrDefaultAsync(stoppingToken);
            return n > 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not tell whether live_ticks is a hypertable; sweeping rows.");
            return false;
        }
    }
}

/// <summary>How long raw ticks are kept, and how gently they are removed.</summary>
public sealed class TickRetentionOptions
{
    public const string SectionName = "TickRetention";

    /// <summary>Days of raw ticks to keep. Zero or less disables the sweep.</summary>
    public int RetentionDays { get; set; } = 7;

    /// <summary>Rows per DELETE. Small enough not to hold a long lock.</summary>
    public int BatchSize { get; set; } = 10_000;

    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(6);

    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan BatchPause { get; set; } = TimeSpan.FromMilliseconds(250);

}
