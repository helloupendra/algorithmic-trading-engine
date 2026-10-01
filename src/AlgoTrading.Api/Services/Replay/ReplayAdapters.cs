using System.Text.Json;
using System.Text.RegularExpressions;
using AlgoTrading.Application.Providers;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
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

/// <summary>
/// A vendor's recap feed running now, read from the feeds' heartbeats. A feed heartbeats every 15 s as
/// "python-&lt;key&gt;-feed", or "python-&lt;key&gt;-recap" while it replays a session (core/live/feed_runner.py,
/// market_data/live/vendors/truedata.py). A vendor whose newest heartbeat is a recap's is running one when that
/// heartbeat is recent, or, when it has gone quiet, while the feed still holds its Redis lock
/// (<c>feed:&lt;key&gt;:lock</c>, refreshed with every heartbeat attempt): the API may have been down, and the
/// feed's heartbeats refused, while it played on.
/// </summary>
public sealed class HeartbeatRecapFeeds : IRecapFeeds
{
    /// <summary>A recap heartbeat this recent shows the feed running by itself (four of its beats).</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(60);

    /// <summary>How far back a feed's newest heartbeat is looked for.</summary>
    public static readonly TimeSpan Remembered = TimeSpan.FromHours(12);

    private static readonly Regex FeedName = new("^python-(.+)-(feed|recap)$", RegexOptions.CultureInvariant);

    private readonly TradingDbContext _db;
    private readonly Func<string, Task<bool>> _lockHeld;
    private readonly TimeProvider _time;

    public HeartbeatRecapFeeds(TradingDbContext db, IConnectionMultiplexer redis)
        : this(db, key => redis.GetDatabase().KeyExistsAsync(key), null)
    {
    }

    /// <param name="lockHeld">Whether a Redis key exists; a test plays it.</param>
    internal HeartbeatRecapFeeds(TradingDbContext db, Func<string, Task<bool>> lockHeld, TimeProvider? time)
    {
        _db = db;
        _lockHeld = lockHeld;
        _time = time ?? TimeProvider.System;
    }

    public async Task<string?> RunningAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var since = now - Remembered;
        var beats = await _db.LiveIngestorStatuses.AsNoTracking()
            .Where(x => x.LastHeartbeatUtc >= since)
            .Select(x => new { x.SourceName, x.LastHeartbeatUtc })
            .ToListAsync(cancellationToken);

        // The newest heartbeat of each vendor speaks for it: the afternoon's live rows stay beside the evening's recap.
        var newest = beats
            .Select(b => (b.SourceName, b.LastHeartbeatUtc, Match: FeedName.Match(b.SourceName)))
            .GroupBy(b => b.Match.Success ? b.Match.Groups[1].Value : b.SourceName, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Feed: g.Key, Beat: g.OrderByDescending(b => b.LastHeartbeatUtc).First()))
            .Where(x => ProviderUsageRules.IsRecapSourceName(x.Beat.SourceName))
            .OrderByDescending(x => x.Beat.LastHeartbeatUtc)
            .ToList();

        foreach (var (feed, beat) in newest)
        {
            if (now - beat.LastHeartbeatUtc <= Fresh) return beat.SourceName;
            if (!beat.Match.Success) continue;
            try
            {
                if (await _lockHeld($"feed:{feed}:lock")) return beat.SourceName;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Not known is not "not running": no desk replay starts on a guess.
                return $"{beat.SourceName} (its Redis lock could not be read)";
            }
        }

        return null;
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
