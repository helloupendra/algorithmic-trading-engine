using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Portfolio;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services.Portfolio;

/// <summary>
/// Deletes portfolio snapshots older than <see cref="PortfolioSettings.RetentionDays"/> (90), for every user, every
/// six hours. A sync deletes its own user's old rows too; this catches the users who stopped syncing.
/// </summary>
/// <remarks>
/// A user's holdings are theirs, not the desk's research data: unlike the market data the desk keeps forever, these
/// rows are not archived anywhere before they go.
/// </remarks>
public sealed class PortfolioRetentionService(
    IServiceScopeFactory scopes,
    IOptionsMonitor<PortfolioSettings> settings,
    ILogger<PortfolioRetentionService> logger,
    TimeProvider? time = null) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromHours(6);

    private const int Batch = 500;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), _time, stoppingToken);
            using var timer = new PeriodicTimer(Every, _time);
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    int deleted = await DeleteOldAsync(scope.ServiceProvider.GetRequiredService<TradingDbContext>(), stoppingToken);
                    if (deleted > 0) logger.LogInformation("Portfolio retention: deleted {Count} snapshots older than {Days} days", deleted, settings.CurrentValue.RetentionDays);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Portfolio retention failed; it tries again in {Hours} hours", Every.TotalHours);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The API is stopping.
        }
    }

    /// <summary>Deletes every snapshot older than the retention, in batches. Public for tests.</summary>
    public async Task<int> DeleteOldAsync(TradingDbContext db, CancellationToken cancellationToken)
    {
        var cutoff = _time.GetUtcNow().UtcDateTime.AddDays(-Math.Max(1, settings.CurrentValue.RetentionDays));
        int total = 0;
        while (true)
        {
            var old = await db.PortfolioSnapshots.Where(s => s.TakenUtc < cutoff).OrderBy(s => s.Id).Take(Batch).ToListAsync(cancellationToken);
            if (old.Count == 0) return total;
            db.PortfolioSnapshots.RemoveRange(old);
            await db.SaveChangesAsync(cancellationToken);
            total += old.Count;
        }
    }
}
