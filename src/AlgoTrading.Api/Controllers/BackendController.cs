// src/AlgoTrading.Api/Controllers/BackendController.cs

using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// When this backend process started, so a restart is visible rather than felt.
/// </summary>
/// <remarks>
/// A rebuild takes the API down for a few seconds. Every page polls, so what the
/// operator saw was a red "Failed to fetch" with no cause, and the natural
/// reading of that is "something is broken" rather than "it is coming back".
/// One number fixes it: the moment this process started. The console keeps the
/// first value it sees and compares - when it changes, the backend restarted,
/// and it can say so plainly instead of leaving a scary error on screen.
///
/// Anonymous deliberately. It is the one thing worth asking for while nobody is
/// signed in, because a restart also invalidates nothing else the page can
/// reach, and gating it behind auth would hide exactly the outage it explains.
/// </remarks>
[AllowAnonymous]
[ApiController]
[Route("api/[controller]")]
public class BackendController : ControllerBase
{
    // Captured once, when the class is first touched, from the OS process
    // itself - not a static field set in Program, which a hot reload could
    // leave pointing at an older moment than the process it describes.
    private static readonly DateTime StartedUtc =
        Process.GetCurrentProcess().StartTime.ToUniversalTime();

    /// <summary>Uptime and the moment this process started.</summary>
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var uptime = DateTime.UtcNow - StartedUtc;

        return Ok(new
        {
            startedUtc = StartedUtc,
            uptimeSeconds = (long)uptime.TotalSeconds,
            // A version of the running build, so "did my push actually reach
            // this process" has an answer that does not require a restart log.
            version = typeof(BackendController).Assembly.GetName().Version?.ToString(),
            environment = HttpContext.RequestServices
                .GetService<IWebHostEnvironment>()?.EnvironmentName
        });
    }
}
