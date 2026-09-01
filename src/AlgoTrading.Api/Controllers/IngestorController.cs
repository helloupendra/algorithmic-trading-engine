using AlgoTrading.Api.Configuration;
using AlgoTrading.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AlgoTrading.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
public class IngestorController : ControllerBase
{
    private readonly StrategyRunnerOptions _options;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<IngestorController> _logger;

    private static readonly ConcurrentDictionary<string, Process> _activeProcesses = new();

    public IngestorController(
        IOptions<StrategyRunnerOptions> options,
        IWebHostEnvironment environment,
        ILogger<IngestorController> logger)
    {
        _options = options.Value;
        _environment = environment;
        _logger = logger;
    }

    [HttpPost("start")]
    public IActionResult StartIngestor()
    {
        if (_activeProcesses.ContainsKey("fyers"))
        {
            return BadRequest(new { message = "Ingestor is already running." });
        }

        var engineDirectory = ResolveEngineDirectory();
        var scriptPath = Path.Combine(engineDirectory, "data_ingestion", "fyers_live_stream.py");

        if (!System.IO.File.Exists(scriptPath))
        {
            return StatusCode(StatusCodes.Status500InternalServerError, $"Script not found at '{scriptPath}'.");
        }

        try
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = ResolvePythonExecutable(),
                WorkingDirectory = engineDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            processInfo.ArgumentList.Add(scriptPath);
            processInfo.Environment["PYTHONPATH"] = engineDirectory;

            var process = Process.Start(processInfo);
            if (process is null) return StatusCode(500, "Failed to start python process.");

            _activeProcesses.TryAdd("fyers", process);

            _ = Task.Run(async () =>
            {
                try
                {
                    await process.WaitForExitAsync();
                }
                finally
                {
                    _activeProcesses.TryRemove("fyers", out _);
                }
            });

            return Ok(new { message = "Ingestor started", processId = process.Id });
        }
        catch (Exception ex)
        {
            return StatusCode(500, ex.Message);
        }
    }

    [HttpPost("stop")]
    public IActionResult StopIngestor()
    {
        if (_activeProcesses.TryGetValue("fyers", out var process))
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch { }
            finally
            {
                _activeProcesses.TryRemove("fyers", out _);
            }
        }
        return Ok(new { message = "Ingestor stopped" });
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        return Ok(new { isRunning = _activeProcesses.ContainsKey("fyers") });
    }

    private string ResolveEngineDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_options.EngineDirectory)) return Path.GetFullPath(_options.EngineDirectory);
        return Path.GetFullPath(Path.Combine(_environment.ContentRootPath, "..", "AlgoTrading.PythonEngine"));
    }

    private string ResolvePythonExecutable()
    {
        if (!string.IsNullOrWhiteSpace(_options.PythonExecutable)) return _options.PythonExecutable;
        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var repoRoot = Path.GetFullPath(Path.Combine(_environment.ContentRootPath, "..", ".."));
        var venvPython = isWindows
            ? Path.Combine(repoRoot, ".venv", "Scripts", "python.exe")
            : Path.Combine(repoRoot, ".venv", "bin", "python");
        return System.IO.File.Exists(venvPython) ? venvPython : (isWindows ? "python" : "python3");
    }
}
