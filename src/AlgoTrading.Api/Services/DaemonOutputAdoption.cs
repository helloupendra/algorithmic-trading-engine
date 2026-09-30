// src/AlgoTrading.Api/Services/DaemonOutputAdoption.cs

namespace AlgoTrading.Api.Services;

/// <summary>
/// At start-up, asks every feed and the chain poller for its status once, so a
/// daemon left running by the previous API instance is adopted — and its log
/// file followed into api.log — straight away.
/// </summary>
/// <remarks>
/// A supervisor adopts lazily, on the first status question. The console and
/// the failover loop ask the feeds often, but nothing asks about the chain
/// poller until someone opens its page, and after the two restarts of 28 Sep
/// its output would have stayed unread all day. One pass is enough: every
/// later status question adopts as well.
/// </remarks>
public sealed class DaemonOutputAdoption : BackgroundService
{
    private readonly FeedSupervisorRegistry _feeds;
    private readonly ChainPollerSupervisor _poller;
    private readonly ILogger<DaemonOutputAdoption> _logger;

    public DaemonOutputAdoption(
        FeedSupervisorRegistry feeds,
        ChainPollerSupervisor poller,
        ILogger<DaemonOutputAdoption> logger)
    {
        _feeds = feeds;
        _poller = poller;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var daemons = _feeds.All.Select(f => f.Supervisor).Append(_poller).ToList();
        foreach (var daemon in daemons)
        {
            try
            {
                await daemon.GetStatusAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not check {Daemon} for adoption at start-up.", daemon.Descriptor.Name);
            }
        }
    }
}
