using AlgoTrading.Infrastructure.Services.MutualFunds;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Keeps every mutual fund scheme's NAV current from AMFI's daily file: at
/// 23:30 IST every day, again at 07:00 IST when the evening run failed or
/// AMFI was late, and an hour after any failure. The schedule is
/// <see cref="MutualFundSchedule.Due"/>; the work is <see cref="MutualFundNavSync"/>.
/// </summary>
/// <remarks>
/// The first look is a few minutes after the API starts, so a new server (or
/// one that was down at 23:30) catches up by itself; after that it looks every
/// five minutes, which costs one settings read. The last result is kept in
/// <c>system_settings</c>, so a restart neither repeats a sync nor forgets a
/// failure. Set <c>MutualFunds:SyncEnabled</c> to false to switch it off.
/// </remarks>
public sealed class MutualFundNavSyncService : BackgroundService
{
    private static readonly TimeSpan Look = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<MutualFundNavSyncService> _logger;
    private readonly bool _enabled;

    public MutualFundNavSyncService(IServiceScopeFactory scopes, ILogger<MutualFundNavSyncService> logger, IOptions<MutualFundsOptions> options)
    {
        _scopes = scopes;
        _logger = logger;
        _enabled = options.Value.SyncEnabled;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("MutualFundNavSyncService is switched off (MutualFunds:SyncEnabled=false).");
            return;
        }

        try
        {
            // After migrations, the calendar load and the market-factor sync's first look.
            await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await LookAsync(stoppingToken);
                await Task.Delay(Look, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task LookAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var sync = scope.ServiceProvider.GetRequiredService<MutualFundNavSync>();
            var last = await sync.LastResultAsync(ct);
            if (MutualFundSchedule.Due(DateTime.UtcNow, last, sync.IsBusinessDay) is { } trigger)
                await sync.RunAsync(trigger, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // RunAsync reports AMFI's and the database's faults itself; this is the settings store failing.
            _logger.LogWarning(ex, "Mutual fund NAV sync could not look at its schedule: {Error}", ex.Message);
        }
    }
}
