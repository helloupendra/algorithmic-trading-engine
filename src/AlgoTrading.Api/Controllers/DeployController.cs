using System.Text.Json;
using AlgoTrading.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// What the machine did with the last pushes: pulled, rebuilt, restarted, live.
/// </summary>
/// <remarks>
/// The record is written by scripts/auto-deploy.ps1, which is the thing that
/// actually performs the deploy - so this controller only reads it. Deliberately
/// a file rather than a table: the writer is a PowerShell script on a schedule,
/// and giving it a database connection (and the credentials to use one) to
/// record its own progress would be a worse trade than reading a small JSON.
///
/// Admin-only. It names commits, file counts and the state of the backend.
/// </remarks>
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
[ApiController]
[Route("api/[controller]")]
public class DeployController : ControllerBase
{
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<DeployController> _logger;

    public DeployController(IWebHostEnvironment environment, ILogger<DeployController> logger)
    {
        _environment = environment;
        _logger = logger;
    }

    /// <summary>The most recent deploys, newest first.</summary>
    [HttpGet("history")]
    public IActionResult GetHistory([FromQuery] int limit = 20)
    {
        limit = Math.Clamp(limit, 1, 40);

        var path = ResolveHistoryFile();
        if (path is null)
        {
            // Not an error: a machine that has never taken a push has no history,
            // and the console should say that rather than show a failure.
            return Ok(new { file = (string?)null, entries = Array.Empty<object>() });
        }

        try
        {
            using var stream = System.IO.File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);

            var entries = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().Take(limit).Select(x => x.Clone()).ToList()
                : new List<JsonElement> { document.RootElement.Clone() };

            return Ok(new { file = path, entries });
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // The writer rewrites this file whole; a read landing mid-write sees
            // a truncated document. Worth reporting, not worth a 500 - the next
            // poll a few seconds later will succeed.
            _logger.LogWarning(ex, "Could not read the deploy history at {Path}.", path);
            return Ok(new { file = path, entries = Array.Empty<object>(), unreadable = true });
        }
    }

    /// <summary>
    /// Finds data/deploy-history.json without assuming where the API was started.
    /// </summary>
    /// <remarks>
    /// ContentRootPath is the API project directory under a plain `dotnet run`,
    /// and the repository root under a published layout, so neither alone is
    /// enough. Walking up a few levels covers both without hardcoding either.
    /// </remarks>
    private string? ResolveHistoryFile()
    {
        var seen = new List<string>();
        foreach (var start in new[] { _environment.ContentRootPath, Directory.GetCurrentDirectory() })
        {
            var directory = new DirectoryInfo(start);
            for (int depth = 0; depth < 4 && directory is not null; depth++)
            {
                var candidate = Path.Combine(directory.FullName, "data", "deploy-history.json");
                seen.Add(candidate);
                if (System.IO.File.Exists(candidate)) return candidate;
                directory = directory.Parent;
            }
        }

        _logger.LogDebug("No deploy history found. Looked in: {Paths}", string.Join("; ", seen));
        return null;
    }
}
