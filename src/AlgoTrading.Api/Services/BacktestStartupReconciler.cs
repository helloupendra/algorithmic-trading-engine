// src/AlgoTrading.Api/Services/BacktestStartupReconciler.cs
namespace AlgoTrading.Api.Services;

/// <summary>
/// The backtest registry is in-memory, so after an API restart a SimulationRun
/// row may still say Running while its runner is either gone or still
/// replaying on its own. At startup every OfflineReplay run left
/// Running/Pending is reconciled: a runner whose stored pid is alive (and is
/// the backtest_runner for that run) is ADOPTED into the registry — it keeps
/// posting progress/marks/complete and can be stopped — and the rest are
/// squared off at their last mark and marked Failed with a clear LastError.
/// </summary>
public sealed class BacktestStartupReconciler : IHostedService
{
    public const string RestartReason = "API restarted while the backtest was running";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BacktestStartupReconciler> _logger;

    public BacktestStartupReconciler(IServiceScopeFactory scopeFactory, ILogger<BacktestStartupReconciler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var control = scope.ServiceProvider.GetRequiredService<BacktestRunControl>();
            var result = await control.ReconcileOrphanedRunsAsync(RestartReason, cancellationToken);
            if (result.Adopted > 0)
            {
                _logger.LogWarning("Adopted {Count} backtest run(s) still replaying from a previous API process.", result.Adopted);
            }
            if (result.Closed > 0)
            {
                _logger.LogWarning("Closed {Count} orphaned backtest run(s) left Running by a previous API process.", result.Closed);
            }
        }
        catch (Exception ex)
        {
            // Never block startup on this: the rows stay stoppable/deletable
            // through the orphan paths in BacktestRunControl and the controller.
            _logger.LogError(ex, "Could not reconcile orphaned backtest runs at startup.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
