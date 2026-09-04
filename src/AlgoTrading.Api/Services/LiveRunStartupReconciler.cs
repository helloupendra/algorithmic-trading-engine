// src/AlgoTrading.Api/Services/LiveRunStartupReconciler.cs
namespace AlgoTrading.Api.Services;

/// <summary>
/// The strategy registry is in-memory, so after an API restart a LivePaper
/// SimulationRun may still say Running while its execution runner is either
/// gone or still trading on its own. Once at startup (after migrations) every
/// such run is reconciled: a runner whose stored pid is alive (and is the
/// execution_runner for that run) is ADOPTED — its card returns, the risk
/// guard watches it again and Stop works; a dead one is closed as Stopped
/// ("API restarted; runner not found") with its positions squared off at the
/// last mark, exactly like the backtest reconciler.
/// </summary>
public sealed class LiveRunStartupReconciler : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LiveRunStartupReconciler> _logger;

    public LiveRunStartupReconciler(IServiceScopeFactory scopeFactory, ILogger<LiveRunStartupReconciler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var control = scope.ServiceProvider.GetRequiredService<StrategyRunControl>();
            var result = await control.ReconcileOrphanedRunsAsync(cancellationToken);
            if (result.Adopted > 0)
            {
                _logger.LogWarning("Adopted {Count} live strategy run(s) still running from a previous API process.", result.Adopted);
            }
            if (result.Closed > 0)
            {
                _logger.LogWarning("Closed {Count} orphaned live strategy run(s) left Running by a previous API process.", result.Closed);
            }
        }
        catch (Exception ex)
        {
            // Never block startup on this: the rows stay stoppable through the
            // orphan path in StrategyRunControl and the controller.
            _logger.LogError(ex, "Could not reconcile orphaned live strategy runs at startup.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
