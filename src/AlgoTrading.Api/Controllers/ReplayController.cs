using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services.Replay;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.LiveData;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// The market replay (<see cref="MarketReplayService"/>): the recorded days, the replay in progress,
/// and its controls, for the admin; and the player's ticks, from the player.
/// </summary>
[Authorize]
[ApiController]
[Route("api/[controller]")]
public class ReplayController(MarketReplayService replay, IMarketReplayBook book) : ControllerBase
{
    /// <summary>The most ticks one batch may carry.</summary>
    public const int MaxTicks = 2000;

    [HttpGet("days")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> Days(CancellationToken cancellationToken) => Ok(await replay.DaysAsync(cancellationToken));

    [HttpGet("status")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> Status(CancellationToken cancellationToken) => Ok(await replay.StatusAsync(cancellationToken));

    [HttpPost("start")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> Start([FromBody] ReplayStartRequest? request, CancellationToken cancellationToken)
    {
        if (request is null) return BadRequest(new { error = "Send date, speed, from and runIds." });
        var result = await replay.StartAsync(request, User.GetUserName() ?? User.Identity?.Name ?? "admin", cancellationToken);
        return result.Session is null
            ? StatusCode(result.StatusCode, new { error = result.Error })
            : StatusCode(StatusCodes.Status202Accepted, result.Session);
    }

    /// <summary>Queues recorded days to replay one after another with the AI Trader (<see cref="MarketReplayService.QueueAsync"/>).</summary>
    [HttpPost("queue")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> Queue([FromBody] ReplayQueueRequest? request, CancellationToken cancellationToken)
    {
        if (request is null) return BadRequest(new { error = "Send dates and speed." });
        var result = await replay.QueueAsync(request, User.GetUserName() ?? User.Identity?.Name ?? "admin", cancellationToken);
        return result.Session is null
            ? StatusCode(result.StatusCode, new { error = result.Error })
            : StatusCode(StatusCodes.Status202Accepted, result.Session);
    }

    /// <summary>Ends the queue; the day playing now plays on unless it is stopped too.</summary>
    [HttpDelete("queue")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> CancelQueue(CancellationToken cancellationToken) =>
        await replay.CancelQueueAsync(User.GetUserName() ?? User.Identity?.Name ?? "admin", cancellationToken) is { } view
            ? Ok(view)
            : NotFound(new { error = "No queue." });

    [HttpPost("stop")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> Stop(CancellationToken cancellationToken) =>
        Ok(await replay.StopAsync(User.GetUserName() ?? User.Identity?.Name ?? "admin", cancellationToken));

    [HttpPost("pause")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> Pause(CancellationToken cancellationToken) => Ok(await replay.SendAsync("pause", cancellationToken));

    [HttpPost("resume")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> Resume(CancellationToken cancellationToken) => Ok(await replay.SendAsync("resume", cancellationToken));

    [HttpGet("logs")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public IActionResult Logs([FromQuery] int lines = 200) => Ok(new { lines = replay.Logs(lines) });

    /// <summary>The player's ticks, for the replay's prices only: nothing is stored.</summary>
    [HttpPost("ticks")]
    [Authorize(Roles = LiveDataController.Writers)]
    public IActionResult Ticks([FromBody] List<UpsertLiveTickRequest>? ticks)
    {
        if (ticks is null || ticks.Count == 0) return BadRequest(new { error = "Send a list of ticks." });
        if (ticks.Count > MaxTicks) return BadRequest(new { error = $"At most {MaxTicks} ticks a batch." });
        if (book.Day is null) return Conflict(new { error = "No market replay is on." });
        return Ok(new { taken = book.Apply(ticks) });
    }
}
