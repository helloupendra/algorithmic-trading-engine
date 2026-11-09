using AlgoTrading.Application.Interfaces;

namespace AlgoTrading.Api.Hubs;

/// <summary>Also closes sockets when a different API process revokes their persisted session.</summary>
public sealed class LiveSessionMonitor(LiveFeedSubscriptions subscriptions, IServiceScopeFactory scopes, ILogger<LiveSessionMonitor> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    public async Task CheckAsync(CancellationToken ct)
    {
        var connected = subscriptions.Snapshot().Where(x => x.SessionId != null && x.UserId != null)
            .DistinctBy(x => x.SessionId).ToList();
        if (connected.Count == 0) return;
        using var scope = scopes.CreateScope();
        var validity = scope.ServiceProvider.GetRequiredService<ITokenValidityService>();
        foreach (var connection in connected)
            if (!await validity.IsSessionAcceptableAsync(connection.UserId!.Value, connection.SessionId!.Value, connection.IssuedAtUtc, ct))
                subscriptions.CloseConnectionsOfSession(connection.SessionId.Value);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                try { await CheckAsync(stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Could not revalidate live login sessions."); }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
