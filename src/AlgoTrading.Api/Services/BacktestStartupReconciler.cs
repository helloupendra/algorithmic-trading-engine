// src/AlgoTrading.Api/Services/BacktestStartupReconciler.cs
namespace AlgoTrading.Api.Services;

/// <summary>
/// The backtest registry is in-memory, so every runner process is gone after
/// an API restart while its SimulationRun row may still say Running. Such a
/// row can never finish on its own (no runner will post /complete), keeps the
/// UI polling and, without this, could neither be stopped nor deleted. At
/// startup every OfflineReplay run left Running/Pending is squared off at its
/// last mark and marked Failed with a clear LastError.
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
            int closed = await control.FailOrphanedRunsAsync(RestartReason, cancellationToken);
            if (closed > 0)
            {
                _logger.LogWarning("Closed {Count} orphaned backtest run(s) left Running by a previous API process.", closed);
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
