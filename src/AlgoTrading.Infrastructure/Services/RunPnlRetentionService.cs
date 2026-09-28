// src/AlgoTrading.Infrastructure/Services/RunPnlRetentionService.cs
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// Can trim the live runs' minute-by-minute P&amp;L (<c>run_pnl_minutes</c>)
/// to a window of days, the way <see cref="TickRetentionService"/> trims raw
/// ticks. Off unless <see cref="RunPnlRetentionOptions.RetentionDays"/> is set.
/// </summary>
/// <remarks>
/// <para>
/// The API's minute recorder writes one row per live run per minute, from 28
/// Sep: some 375 rows a session for every NSE run, more for an MCX run into
/// the evening, and a row for every manual book whose figures moved. The table
/// only ever grows.
/// </para>
/// <para>
/// Off by default, because the owner's rule is that every datum is kept and
/// nothing is deleted that is not on Drive. The nightly archive
/// (<c>scripts/archive_to_drive.py</c>) copies each closed day of this table
/// and verifies it; that archive's own drop option is the way to free the
/// disk of verified days. The sweep is for a desk that chooses a window
/// instead: it deletes whether or not a day is on Drive.
/// </para>
/// <para>
/// What a sweep takes away is the intraday curve of an old day, nothing else.
/// A run's figures in the run history, its run page and its charges are worked
/// out from its positions and fills (<c>RunPnl</c>), never read back from
/// these rows; the Desk and the Tracks view ask for one day at a time. A manual
/// book, which gets a row only when it moves, simply gets a fresh one on its
/// next move once its last has aged out.
/// </para>
/// <para>
/// Deleted in bounded batches, oldest first, with a pause between them, for
/// the reason the tick sweep gives: the recorder writes to this table every
/// minute, and one long DELETE would hold it.
/// </para>
/// </remarks>
public class RunPnlRetentionService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RunPnlRetentionService> _logger;
    private readonly RunPnlRetentionOptions _options;

    public RunPnlRetentionService(
        IServiceScopeFactory scopeFactory,
        ILogger<RunPnlRetentionService> logger,
        RunPnlRetentionOptions options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options;
    }

    /// <summary>
    /// The rows the next batch removes: older than <paramref name="cutoffUtc"/>,
    /// oldest first, at most <paramref name="batchSize"/>.
    /// </summary>
    public static IQueryable<RunPnlMinute> Expired(IQueryable<RunPnlMinute> rows, DateTime cutoffUtc, int batchSize)
        => rows.Where(x => x.AtUtc < cutoffUtc).OrderBy(x => x.AtUtc).ThenBy(x => x.Id).Take(batchSize);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.RetentionDays <= 0)
        {
            _logger.LogInformation("Run P&L retention is off (RunPnlRetention:RetentionDays <= 0); every run_pnl_minutes row is kept.");
            return;
        }

        // Never on the way up: a restart in market hours must not spend its
        // first minutes deleting rows while the runners are being adopted.
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
                await SweepAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A failed sweep is a table that stays a little larger. It is
                // never a reason to take the service down.
                _logger.LogError(ex, "Run P&L retention sweep failed; will retry at the next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// One sweep: removes every row older than <see cref="RunPnlRetentionOptions.RetentionDays"/>
    /// before <paramref name="nowUtc"/>, a batch at a time, and says how many went.
    /// </summary>
    public async Task<int> SweepAsync(DateTime nowUtc, CancellationToken stoppingToken)
    {
        if (_options.RetentionDays <= 0) return 0;

        var cutoff = nowUtc.AddDays(-_options.RetentionDays);
        int batchSize = Math.Max(1, _options.BatchSize);
        int removedTotal = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            int removed;
            using (var scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                removed = await DeleteBatchAsync(db, cutoff, batchSize, stoppingToken);
            }

            removedTotal += removed;

            if (removed < batchSize) break;

            // Breathe, so the recorder gets the table back between batches.
            await Task.Delay(_options.BatchPause, stoppingToken);
        }

        if (removedTotal > 0)
        {
            _logger.LogInformation(
                "Run P&L retention removed {Count} run_pnl_minutes rows older than {Days} days (before {Cutoff:yyyy-MM-dd HH:mm} UTC).",
                removedTotal, _options.RetentionDays, cutoff);
        }

        return removedTotal;
    }

    /// <summary>
    /// Deletes one batch of <see cref="Expired"/> rows in a single statement.
    /// Virtual only because the in-memory provider the tests use has no bulk
    /// DELETE; the rows chosen are the same either way.
    /// </summary>
    protected virtual Task<int> DeleteBatchAsync(TradingDbContext db, DateTime cutoffUtc, int batchSize, CancellationToken cancellationToken)
        => Expired(db.RunPnlMinutes, cutoffUtc, batchSize).ExecuteDeleteAsync(cancellationToken);
}

/// <summary>How many days of run P&amp;L minutes are kept, and how gently the rest are removed.</summary>
public sealed class RunPnlRetentionOptions
{
    public const string SectionName = "RunPnlRetention";

    /// <summary>
    /// Days of minutes to keep. Zero or less (the default) keeps every row: the
    /// owner's rule is that nothing is deleted that is not on Drive.
    /// </summary>
    public int RetentionDays { get; set; }

    /// <summary>Rows per DELETE. Small enough not to hold a long lock.</summary>
    public int BatchSize { get; set; } = 10_000;

    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(6);

    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan BatchPause { get; set; } = TimeSpan.FromMilliseconds(250);
}
