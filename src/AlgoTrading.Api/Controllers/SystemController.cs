using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// The machine the platform runs on: its disk, memory and CPU, the database on
/// it and how fast that grows, and the state of the Drive archive.
/// </summary>
/// <remarks>
/// Separate from <see cref="BackendController"/>, which is anonymous on
/// purpose (it explains an outage to a signed-out console). This one names the
/// instance, its tables and their sizes, so it is Admin-only - and an action
/// cannot be made admin-only inside a controller that allows anonymous callers.
/// </remarks>
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
[ApiController]
[Route("api/[controller]")]
public class SystemController : ControllerBase
{
    private readonly SystemHostService _host;

    public SystemController(SystemHostService host)
    {
        _host = host;
    }

    /// <summary>
    /// { generatedUtc, cacheSeconds, host, ec2, api, database, growth, archive }.
    /// Any field that cannot be read on this host is null - /proc on a Mac,
    /// the instance on a machine that is not EC2 - never a made-up value.
    /// Cached for a few seconds, so polling it is cheap.
    /// </summary>
    [HttpGet("host")]
    public async Task<ActionResult<SystemHostReport>> GetHost(CancellationToken cancellationToken)
    {
        return Ok(await _host.GetAsync(cancellationToken));
    }
}
