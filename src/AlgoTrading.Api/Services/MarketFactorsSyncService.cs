using AlgoTrading.Infrastructure.Services;
using AlgoTrading.Infrastructure.Services.MarketFactors;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Keeps the Market factors page's daily data current: NSE's participant-wise
/// open interest, the F&amp;O bhavcopy's futures rows and the FII/DII cash figures.
/// </summary>
/// <remarks>
/// NSE publishes all three in the evening. The service looks back over the last
/// 40 sessions a few minutes after the API starts (a new server, or one that was
/// down, catches up by itself), then again every 30 minutes between 18:00 and
/// 23:30 IST, which fills in the day as soon as NSE posts it. Every look covers
/// the same 40 sessions: a run stops at the per-run fetch cap, and a shorter
/// evening look would leave the days the first run did not reach missing for
/// good. Each look fetches only missing days, so a quiet evening costs a few
/// database reads and one request for the day NSE has not posted yet. Set
/// <c>MarketFactors:SyncEnabled</c> to false to switch it off.
/// </remarks>
public class MarketFactorsSyncService : BackgroundService
{
    private static readonly TimeSpan EveningStart = new(18, 0, 0);
    private static readonly TimeSpan EveningEnd = new(23, 30, 0);
    private const int LookbackSessions = 40;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MarketFactorsSyncService> _logger;
    private readonly bool _enabled;

    public MarketFactorsSyncService(IServiceScopeFactory scopeFactory, ILogger<MarketFactorsSyncService> logger, IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _enabled = !string.Equals(configuration["MarketFactors:SyncEnabled"], "false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a scheduled look is due at this IST time of day.</summary>
    public static bool InEveningWindow(DateTime nowUtc)
    {
        var time = IstTime.ToIst(nowUtc).TimeOfDay;
        return time >= EveningStart && time <= EveningEnd;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("MarketFactorsSyncService is switched off (MarketFactors:SyncEnabled=false).");
            return;
        }

        // After migrations, the calendar load and the reconcilers.
        try { await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken); }
        catch (OperationCanceledException) { return; }

        await RunAsync(LookbackSessions, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken); }
            catch (OperationCanceledException) { break; }

            if (InEveningWindow(DateTime.UtcNow)) await RunAsync(LookbackSessions, stoppingToken);
        }
    }

    private async Task RunAsync(int sessions, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<MarketFactorsSync>().RunAsync(sessions, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Market factors sync failed.");
        }
    }
}
