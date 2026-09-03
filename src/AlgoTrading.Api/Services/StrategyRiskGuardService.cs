// src/AlgoTrading.Api/Services/StrategyRiskGuardService.cs
using AlgoTrading.Api.Configuration;
using AlgoTrading.Application.Interfaces;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Enforces each running strategy's stop-loss / target on the run's TOTAL P&amp;L
/// (realized + unrealized) from the API side, so the guard works even when the
/// Python runner is wedged. On trigger: flatten, kill, record the reason.
/// </summary>
public sealed class StrategyRiskGuardService : BackgroundService
{
    private readonly StrategyProcessRegistry _registry;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<StrategyRunnerOptions> _options;
    private readonly ILogger<StrategyRiskGuardService> _logger;

    public StrategyRiskGuardService(
        StrategyProcessRegistry registry,
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<StrategyRunnerOptions> options,
        ILogger<StrategyRiskGuardService> logger)
    {
        _registry = registry;
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StrategyRiskGuardService is starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Risk guard sweep failed.");
            }

            var seconds = Math.Max(1, _options.CurrentValue.RiskGuardIntervalSeconds);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("StrategyRiskGuardService is stopping.");
    }

    private async Task CheckAllAsync(CancellationToken cancellationToken)
    {
        var guarded = _registry.List()
            .Where(x => x.StopLoss.HasValue || x.Target.HasValue)
            .ToList();

        if (guarded.Count == 0) return;

        foreach (var entry in guarded)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var paperTrading = scope.ServiceProvider.GetRequiredService<IPaperTradingService>();

                var summary = await paperTrading.GetPortfolioSummaryAsync(entry.RunId, cancellationToken);
                decimal totalPnl = summary.RealizedPnl + summary.UnrealizedPnl;

                string? reason = null;
                if (entry.StopLoss.HasValue && totalPnl <= -entry.StopLoss.Value)
                {
                    reason = $"Stop loss hit: P&L {totalPnl:0} ≤ −{entry.StopLoss.Value:0}";
                }
                else if (entry.Target.HasValue && totalPnl >= entry.Target.Value)
                {
                    reason = $"Target hit: P&L {totalPnl:0} ≥ {entry.Target.Value:0}";
                }

                if (reason is null) continue;

                _logger.LogWarning("Risk guard tripping strategy {StrategyId} ({Name}): {Reason}", entry.StrategyId, entry.Name, reason);

                var control = scope.ServiceProvider.GetRequiredService<StrategyRunControl>();
                await control.StopAsync(entry.StrategyId, reason, flatten: true, by: "risk-guard", cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Risk guard check failed for strategy {StrategyId} run {RunId}.", entry.StrategyId, entry.RunId);
            }
        }
    }
}
