using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// Status, start, stop and output of every live feed, addressed by connector key.
/// </summary>
/// <remarks>
/// The feeds come from <see cref="FeedSupervisorRegistry"/>, which builds the
/// list from the connectors that declare live ticks. That is why this is one
/// controller and not one per vendor: a new vendor's feed is reachable here the
/// day its connector ships, with no endpoint of its own to write.
/// <para>
/// The FYERS feed is also behind <see cref="IngestorController"/>, which the
/// market-open script and the notifier call. Both routes drive the same
/// supervisor instance, so they cannot disagree about whether it is running.
/// </para>
/// </remarks>
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
[ApiController]
[Route("api/[controller]")]
public class FeedsController : ControllerBase
{
    private readonly FeedSupervisorRegistry _feeds;

    public FeedsController(FeedSupervisorRegistry feeds)
    {
        _feeds = feeds;
    }

    /// <summary>
    /// [{ key, displayName, isRunning, managed, processId, source }] for every
    /// feed. source is "managed" (this API launched it), "adopted" (alive from a
    /// previous API instance, found by its stored pid) or "none".
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var rows = new List<object>(_feeds.All.Count);
        foreach (var (key, displayName, supervisor) in _feeds.All)
        {
            var status = await supervisor.GetStatusAsync(cancellationToken);
            rows.Add(new
            {
                key,
                displayName,
                isRunning = status.IsRunning,
                managed = status.Managed,
                processId = status.ProcessId,
                source = status.Source,
            });
        }

        return Ok(rows);
    }

    /// <summary>
    /// Launches the feed. 400 when it is already alive (managed by this API or
    /// adopted from a previous one), 409 when a stored pid is alive but could
    /// not be verified just now.
    /// </summary>
    [HttpPost("{key}/start")]
    public async Task<IActionResult> Start(string key, CancellationToken cancellationToken)
    {
        if (_feeds.Get(key) is not { } supervisor) return UnknownFeed(key);

        var outcome = await supervisor.StartAsync(cancellationToken);
        if (!outcome.Started)
        {
            return StatusCode(outcome.StatusCode, new { message = outcome.Message, processId = outcome.ProcessId });
        }

        return Ok(new { message = outcome.Message, processId = outcome.ProcessId });
    }

    /// <summary>Stops this feed only — the managed instance, or the adopted one found by its stored pid.</summary>
    [HttpPost("{key}/stop")]
    public async Task<IActionResult> Stop(string key, CancellationToken cancellationToken)
    {
        if (_feeds.Get(key) is not { } supervisor) return UnknownFeed(key);

        var userName = User.GetUserName() ?? "unknown";
        var outcome = await supervisor.StopAsync($"Stopped by {userName}", cancellationToken);
        return Ok(new
        {
            message = outcome.Message,
            wasRunning = outcome.WasRunning,
            processId = outcome.ProcessId,
            source = outcome.Source,
        });
    }

    /// <summary>
    /// Recent stdout/stderr from the feed process — the place to look when a
    /// start flips straight back to stopped. Empty for an adopted feed, whose
    /// pipes belonged to the API instance that launched it.
    /// </summary>
    [HttpGet("{key}/logs")]
    public IActionResult Logs(string key, [FromQuery] int take = 200)
    {
        if (_feeds.Get(key) is not { } supervisor) return UnknownFeed(key);

        return Ok(supervisor.GetLogs(take));
    }

    // A JSON body, so the console can tell "no such feed" from the empty-body
    // 404 of a route that does not exist on an older API build.
    private NotFoundObjectResult UnknownFeed(string key)
        => NotFound(new { message = $"No live feed is registered for '{key}'." });
}
