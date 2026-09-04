using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// Start / stop / status of the live data ingestor. The process itself is
/// owned by <see cref="IngestorSupervisor"/>, which also recognises an
/// instance launched by a previous API process (by its stored pid) so status
/// and Stop keep working across an API restart.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
public class IngestorController : ControllerBase
{
    private readonly IngestorSupervisor _supervisor;

    public IngestorController(IngestorSupervisor supervisor)
    {
        _supervisor = supervisor;
    }

    /// <summary>
    /// Launches fyers_streamer.py. 400 when an instance is already alive —
    /// managed by this API, or adopted from a previous one.
    /// </summary>
    [HttpPost("start")]
    public async Task<IActionResult> StartIngestor(CancellationToken cancellationToken)
    {
        var outcome = await _supervisor.StartAsync(cancellationToken);
        if (!outcome.Started)
        {
            return StatusCode(outcome.StatusCode, new { message = outcome.Message, processId = outcome.ProcessId });
        }

        return Ok(new { message = outcome.Message, processId = outcome.ProcessId });
    }

    /// <summary>Stops the managed instance, or the adopted one found by its stored pid.</summary>
    [HttpPost("stop")]
    public async Task<IActionResult> StopIngestor(CancellationToken cancellationToken)
    {
        var userName = User.GetUserName() ?? "unknown";
        var outcome = await _supervisor.StopAsync($"Stopped by {userName}", cancellationToken);
        return Ok(new
        {
            message = outcome.Message,
            wasRunning = outcome.WasRunning,
            processId = outcome.ProcessId,
            source = outcome.Source
        });
    }

    /// <summary>
    /// { isRunning, managed, processId, source } — source is "managed" (this
    /// API launched it), "adopted" (alive from a previous API instance) or "none".
    /// </summary>
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken cancellationToken)
    {
        var status = await _supervisor.GetStatusAsync(cancellationToken);
        return Ok(new
        {
            isRunning = status.IsRunning,
            managed = status.Managed,
            processId = status.ProcessId,
            source = status.Source
        });
    }

    /// <summary>Recent stdout/stderr from the ingestor process — the place to
    /// look when a start flips straight back to stopped.</summary>
    [HttpGet("logs")]
    public IActionResult GetLogs([FromQuery] int take = 200)
    {
        return Ok(_supervisor.GetLogs(take));
    }
}
