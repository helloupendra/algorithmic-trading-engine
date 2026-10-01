using System.Text.Json;
using StackExchange.Redis;

namespace AlgoTrading.Api.Services.Replay;

/// <summary>The player as <see cref="ReplaySupervisor"/> runs it.</summary>
public sealed class SupervisedReplayPlayer(ReplaySupervisor supervisor) : IReplayPlayer
{
    public async Task<bool> IsRunningAsync(CancellationToken cancellationToken) =>
        (await supervisor.GetStatusAsync(cancellationToken)).IsRunning;

    public async Task<(bool Started, string Message)> StartAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var outcome = await supervisor.StartAsync(args, cancellationToken);
        return (outcome.Started, outcome.Message);
    }

    public async Task StopAsync(string reason, CancellationToken cancellationToken) =>
        await supervisor.StopAsync(reason, cancellationToken);

    public IReadOnlyList<string> Logs(int take) => supervisor.GetLogs(take);
}

/// <summary>The player's status and commands, through Redis.</summary>
public sealed class RedisReplayChannel(IConnectionMultiplexer redis) : IReplayChannel
{
    public const string StatusKey = "replay:status";
    public const string ControlKey = "replay:control";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ReplayPlayerStatus?> ReadStatusAsync(CancellationToken cancellationToken)
    {
        var value = await redis.GetDatabase().StringGetAsync(StatusKey);
        if (value.IsNullOrEmpty) return null;
        try
        {
            return JsonSerializer.Deserialize<ReplayPlayerStatus>(value.ToString(), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task SendAsync(string command, CancellationToken cancellationToken) =>
        redis.GetDatabase().StringSetAsync(ControlKey, command, TimeSpan.FromHours(12));

    public Task ResetAsync(CancellationToken cancellationToken) =>
        redis.GetDatabase().KeyDeleteAsync([StatusKey, ControlKey]);
}

/// <summary>
/// Stops a replay run through the stop pipeline, squared off at the replay's prices. A run whose
/// runner is gone (an API restart) is stopped as an orphan, the same way.
/// </summary>
public sealed class ReplayRunStopper(StrategyRunControl runs) : IReplayRunStopper
{
    public const string By = "market-replay";

    public async Task StopAsync(long runId, string reason, CancellationToken cancellationToken)
    {
        var result = await runs.StopAsync(runId, reason, flatten: true, By, cancellationToken);
        if (!result.WasRunning) await runs.StopOrphanAsync(runId, reason, flatten: true, By);
    }
}

/// <summary>Looks at the replay every few seconds (<see cref="MarketReplayService.TickAsync"/>).</summary>
public sealed class ReplayMonitor(IServiceScopeFactory scopes, ILogger<ReplayMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every);
        try
        {
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<MarketReplayService>().TickAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    // Never let the replay take the API down.
                    logger.LogWarning(ex, "The market replay monitor's look failed; it looks again in {Seconds}s", Every.TotalSeconds);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The API is stopping.
        }
    }
}
