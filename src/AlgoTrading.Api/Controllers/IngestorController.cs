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

    // Serializes start attempts so two overlapping POSTs cannot both spawn a
    // process (the second would be untracked and therefore unstoppable).
    private static readonly object _startLock = new();

    // Recent stdout/stderr lines from the ingestor. The streams MUST be
    // drained: with RedirectStandardOutput and no reader, the pipe buffer
    // fills and the python process blocks on its next print — silently
    // freezing tick capture while status still reports "running".
    private const int LogBufferCapacity = 500;
    private static readonly ConcurrentQueue<string> _recentLogs = new();

    private static void AppendLog(string line)
    {
        _recentLogs.Enqueue(line);
        while (_recentLogs.Count > LogBufferCapacity && _recentLogs.TryDequeue(out _)) { }
    }

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
        var engineDirectory = ResolveEngineDirectory();
        var scriptPath = Path.Combine(engineDirectory, "market_data", "live", "fyers_streamer.py");

        if (!System.IO.File.Exists(scriptPath))
        {
            return StatusCode(StatusCodes.Status500InternalServerError, $"Script not found at '{scriptPath}'.");
        }

        lock (_startLock)
        {
            if (_activeProcesses.ContainsKey("fyers"))
            {
                return BadRequest(new { message = "Ingestor is already running." });
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
                // Line-buffered output so log lines arrive as they happen
                // instead of in 8KB blocks.
                processInfo.Environment["PYTHONUNBUFFERED"] = "1";

                var process = new Process { StartInfo = processInfo, EnableRaisingEvents = true };

                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data is null) return;
                    AppendLog($"{DateTime.UtcNow:HH:mm:ss} | {e.Data}");
                    _logger.LogInformation("[ingestor] {Line}", e.Data);
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data is null) return;
                    AppendLog($"{DateTime.UtcNow:HH:mm:ss} ! {e.Data}");
                    _logger.LogWarning("[ingestor:err] {Line}", e.Data);
                };

                if (!process.Start())
                {
                    return StatusCode(500, "Failed to start python process.");
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                _activeProcesses.TryAdd("fyers", process);
                AppendLog($"{DateTime.UtcNow:HH:mm:ss} | ingestor started (pid {process.Id})");

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await process.WaitForExitAsync();
                        AppendLog($"{DateTime.UtcNow:HH:mm:ss} | ingestor exited with code {process.ExitCode}");
                    }
                    finally
                    {
                        _activeProcesses.TryRemove("fyers", out _);
                        process.Dispose();
                    }
                });

                return Ok(new { message = "Ingestor started", processId = process.Id });
            }
            catch (Exception ex)
            {
                _activeProcesses.TryRemove("fyers", out _);
                return StatusCode(500, ex.Message);
            }
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

    /// <summary>Recent stdout/stderr from the ingestor process — the place to
    /// look when a start flips straight back to stopped.</summary>
    [HttpGet("logs")]
    public IActionResult GetLogs([FromQuery] int take = 200)
    {
        var lines = _recentLogs.ToArray();
        return Ok(lines.Skip(Math.Max(0, lines.Length - Math.Clamp(take, 1, LogBufferCapacity))));
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
