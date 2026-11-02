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
/// Only the raw log is trimmed. The 1-minute bars and the quote snapshots stay,
/// and every tick is copied, verified, to Google Drive each night before it can
/// age out (scripts/archive_to_drive.py).
/// </para>
/// <para>
/// Deleted in bounded batches rather than one statement. A single DELETE over a
/// million rows takes a long lock on a table the live path is writing to, which
/// would cause the outage it is meant to prevent.
/// </para>
/// <para>
/// Production's <c>live_ticks</c> is a hypertable, and its retention is the 90-day
/// policy (<c>LiveTicksRetention90Days</c>), so this sweep does nothing there. It
/// runs only once the catalog has answered that the table is a plain one. A check
/// that fails skips the sweep (fail closed): until 2 Nov 2026 it read as "not a
/// hypertable", and one failed query at the busiest minute of the morning would
/// have deleted days 8 to 90, row by row, ticks the owner keeps on purpose.
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

    /// <summary>One sweep; how many rows it removed.</summary>
    internal async Task<int> SweepAsync(CancellationToken stoppingToken)
    {
        // A hypertable ages out by dropping whole day-chunks (add_retention_policy
        // in the LiveTicksHypertable migration): instant, and no dead rows left
        // behind. Deleting rows from it here would only decompress old chunks to
        // remove what the policy is about to drop anyway.
        bool hypertable;
        try
        {
            hypertable = await IsHypertableAsync(stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "Tick retention skipped this sweep: could not tell whether live_ticks is a hypertable, and no tick is deleted on a guess. It looks again in {Hours:0} h.",
                _options.SweepInterval.TotalHours);
            return 0;
        }

        if (hypertable)
        {
            _logger.LogDebug("live_ticks is a hypertable; retention is TimescaleDB's drop_chunks policy, nothing to sweep.");
            return 0;
        }

        var cutoff = DateTime.UtcNow.AddDays(-_options.RetentionDays);
        int removedTotal = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

            int removed = await DeleteBatchAsync(db, cutoff, _options.BatchSize, stoppingToken);

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

        return removedTotal;
    }

    /// <summary>One bounded DELETE, oldest first; a test without a relational database does it by hand.</summary>
    protected virtual Task<int> DeleteBatchAsync(TradingDbContext db, DateTime cutoffUtc, int batchSize, CancellationToken stoppingToken)
        => db.LiveTicks
            .Where(x => x.ReceivedUtc < cutoffUtc)
            .OrderBy(x => x.ReceivedUtc)
            .Take(batchSize)
            .ExecuteDeleteAsync(stoppingToken);

    /// <summary>What the catalog says. Throws when it cannot say; that is not a "no".</summary>
    protected virtual async Task<bool> IsHypertableAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var n = await db.Database
            .SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM timescaledb_information.hypertables WHERE hypertable_name = 'live_ticks'")
            .FirstOrDefaultAsync(stoppingToken);
        return n > 0;
    }
}

/// <summary>How long raw ticks are kept, and how gently they are removed.</summary>
public sealed class TickRetentionOptions
{
    public const string SectionName = "TickRetention";

    /// <summary>
    /// Days of raw ticks a plain (not hypertable) <c>live_ticks</c> keeps. Zero or less
    /// disables the sweep. 90, as the hypertable's policy: no committed settings file
    /// has this section, so the default is what runs, and until 2 Nov 2026 it was 7,
    /// left from before the owner moved the policy to 90 days.
    /// </summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>Rows per DELETE. Small enough not to hold a long lock.</summary>
    public int BatchSize { get; set; } = 10_000;

    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(6);

    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan BatchPause { get; set; } = TimeSpan.FromMilliseconds(250);

}
