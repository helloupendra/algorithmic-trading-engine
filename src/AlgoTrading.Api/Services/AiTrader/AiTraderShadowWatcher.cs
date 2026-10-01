using AlgoTrading.Api.Services.AiAgents;
using AlgoTrading.Api.Services.Replay;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Infrastructure.Ai;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services.AiTrader;

/// <summary>
/// The shadow book's minute check (<see cref="AiTraderShadowCheck"/>), every minute whatever the AI Trader's switch.
/// </summary>
/// <remarks>
/// <para>
/// The check rode on the agent's own tick, and the scheduler runs an agent only while it is on: switched off with a
/// position open, the position was neither marked, stopped, taken nor squared off at 15:30, and closed on a later
/// day at a stale mark. A position it opened is watched until it closes, as a placed order would be. The agent no
/// longer checks: one check a minute, here.
/// </para>
/// <para>
/// It waits for the API to settle as the scheduler does (<see cref="AiAgentScheduler.StartDelay"/>), so nothing is
/// read while the API starts, and runs only where the scheduled agents may (<see cref="AiSettings.SchedulerEnabled"/>).
/// It asks no model, so it needs no key.
/// </para>
/// </remarks>
public sealed class AiTraderShadowWatcher(
    IServiceScopeFactory scopes,
    IOptionsMonitor<AiSettings> settings,
    ILogger<AiTraderShadowWatcher> logger,
    TimeProvider? time = null) : BackgroundService
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(AiAgentScheduler.StartDelay, _time, stoppingToken);
            using var timer = new PeriodicTimer(AiAgentScheduler.Tick, _time);
            do
            {
                try
                {
                    await RunTickAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    // An unhandled exception in a hosted service stops the host. A position keeps its last mark and is
                    // checked again next minute.
                    logger.LogWarning(ex, "AI Trader: the shadow book's minute check failed; it runs again next minute");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The API is stopping.
        }
    }

    /// <summary>One minute's check, in a scope (so a database context) of its own. Public for tests.</summary>
    public async Task RunTickAsync(CancellationToken cancellationToken)
    {
        if (!settings.CurrentValue.SchedulerEnabled) return;
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AiTraderShadowCheck>().RunAsync(_time.GetUtcNow().UtcDateTime, cancellationToken);
    }
}

/// <summary>
/// One minute check of the shadow books (<see cref="AiTraderShadowBook"/>), under <see cref="AiTraderAgent.BookGate"/>
/// so it never acts on a position a look is judging: an ended replay's positions closed at their last marks, the
/// playing replay's (one the AI Trader decides in) checked at the replay's clock, and the live shadow book at
/// <c>nowUtc</c>.
/// </summary>
public sealed class AiTraderShadowCheck(AiTraderShadowBook shadow, IReplaySessions replays, IMarketReplayBook? replayBook = null)
{
    public async Task RunAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var replay = await replays.LoadAsync(cancellationToken);
        await AiTraderAgent.BookGate.WaitAsync(cancellationToken);
        try
        {
            await shadow.EndReplaysAsync(replay is not null && MarketReplayService.IsActive(replay.State) ? replay.Id : null, cancellationToken);
            if (AiTraderAgent.Replaying(replay, replayBook) && replayBook?.ClockUtc is DateTime clock)
            {
                await shadow.CheckAsync(clock, replay.Id, cancellationToken);
            }

            await shadow.CheckAsync(nowUtc, null, cancellationToken);
        }
        finally
        {
            AiTraderAgent.BookGate.Release();
        }
    }
}
