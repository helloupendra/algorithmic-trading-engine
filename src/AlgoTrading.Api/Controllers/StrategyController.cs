using AlgoTrading.Api.Configuration;
using AlgoTrading.Api.Security;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AlgoTrading.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StrategyController : ControllerBase
{
    private readonly TradingDbContext _dbContext;
    private readonly StrategyRunnerOptions _options;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<StrategyController> _logger;

    /// <summary>
    /// Running strategy processes, keyed by strategy id.
    ///
    /// NOTE: this is in-process state. It is lost if the API restarts, which orphans
    /// any running Python process — it keeps trading but can no longer be stopped
    /// from here. Moving run state into the database is tracked as follow-up work;
    /// until then, treat a restart as requiring a manual check for stray processes.
    /// </summary>
    private static readonly ConcurrentDictionary<int, RunningStrategy> _activeProcesses = new();

    private sealed record RunningStrategy(Process Process, string StartedBy, DateTime StartedUtc)
    {
        public List<object> RecentSignals { get; } = new();
    }

    public StrategyController(
        TradingDbContext dbContext,
        IOptions<StrategyRunnerOptions> options,
        IWebHostEnvironment environment,
        ILogger<StrategyController> logger)
    {
        _dbContext = dbContext;
        _options = options.Value;
        _environment = environment;
        _logger = logger;
    }

    private List<StrategyDefinition> GetDiscoveredStrategies()
    {
        var engineDirectory = ResolveEngineDirectory();
        var strategiesPath = Path.Combine(engineDirectory, "strategies");
        var list = new List<StrategyDefinition>();
        
        if (!Directory.Exists(strategiesPath)) return list;

        var excludeFiles = new HashSet<string> { "__init__.py", "base_strategy.py", "execution_runner.py", "logic_engine.py", "contract_selector.py", "price_resolver.py", "list_strategies.py" };
        var pyFiles = Directory.GetFiles(strategiesPath, "*.py", SearchOption.AllDirectories)
            .Where(f => !excludeFiles.Contains(Path.GetFileName(f))).ToList();

        foreach (var file in pyFiles)
        {
            var content = System.IO.File.ReadAllText(file);
            var lines = content.Split('\n');
            string? strategyName = null;
            string? className = null;
            
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (line.StartsWith("class ") && line.Contains("(BaseStrategy)"))
                {
                    var parts = line.Split(' ', '(');
                    if (parts.Length > 1) { className = parts[1].Trim(); }
                }
                if (trimmed.StartsWith("name = \"") || trimmed.StartsWith("name = '"))
                {
                    var quote = trimmed.Contains("\"") ? '"' : '\'';
                    var parts = trimmed.Split(quote);
                    if (parts.Length >= 3)
                    {
                        strategyName = parts[1];
                    }
                }
            }

            if (string.IsNullOrEmpty(strategyName))
            {
                strategyName = className;
            }

            if (string.IsNullOrEmpty(strategyName))
            {
                var nameParts = Path.GetFileNameWithoutExtension(file).Split('_');
                strategyName = string.Join("", nameParts.Select(p => char.ToUpper(p[0]) + p.Substring(1)));
            }

            int stableId = Math.Abs(strategyName.GetHashCode());
            if (stableId == 0) stableId = 1;

            list.Add(new StrategyDefinition
            {
                Id = stableId,
                Name = strategyName,
                Description = $"Discovered from {Path.GetRelativePath(strategiesPath, file).Replace("\\", "/")}",
                DefaultParametersJson = "{}",
                CreatedUtc = System.IO.File.GetCreationTimeUtc(file)
            });
        }
        return list;
    }

    [HttpGet]
    public IActionResult GetAll()
    {
        var strategies = GetDiscoveredStrategies();
        return Ok(strategies.Select(s =>
        {
            _activeProcesses.TryGetValue(s.Id, out var running);
            return new
            {
                s.Id, s.Name, s.Description, s.DefaultParametersJson, s.CreatedUtc,
                IsActive = running is not null, StartedBy = running?.StartedBy, StartedUtc = running?.StartedUtc
            };
        }).ToList());
    }

    [HttpGet("{id}")]
    public IActionResult GetById(int id)
    {
        var strategy = GetDiscoveredStrategies().FirstOrDefault(x => x.Id == id);
        if (strategy == null) return NotFound();
        return Ok(strategy);
    }

    /// <summary>
    /// Registers a new strategy definition. Admin-only — the name here is passed to
    /// the Python runner, so creating one determines what code can be launched.
    /// </summary>
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    [HttpPost]
    public async Task<ActionResult<StrategyDefinition>> Create(StrategyDefinition strategy, CancellationToken cancellationToken)
    {
        _dbContext.Strategies.Add(strategy);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = strategy.Id }, strategy);
    }

    /// <summary>
    /// Deploys a strategy against an existing simulation run (paper trading).
    /// Unlike <c>start</c>, any signed-in trader may do this: the runner posts
    /// signals into the Simulator, which fills them on paper only — no broker
    /// order path exists here. The run carries the user's parametersJson, so
    /// this is what the Deploy wizard calls.
    /// </summary>
    public record DeployRequest(long RunId);

    [HttpPost("{id:int}/deploy")]
    public async Task<IActionResult> Deploy(int id, [FromBody] DeployRequest request, CancellationToken cancellationToken)
    {
        var run = await _dbContext.SimulationRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == request.RunId, cancellationToken);
        if (run is null)
        {
            return NotFound($"Simulation run {request.RunId} not found. Create it first via POST /api/Simulator/runs.");
        }
        if (!string.Equals(run.Mode, "LivePaper", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("Only LivePaper runs can be deployed from the console for now — live mode arrives with the execution loop.");
        }

        var strategy = await _dbContext.Strategies.FindAsync(new object[] { id }, cancellationToken);
        if (strategy == null) return NotFound($"Strategy {id} not found");

        return LaunchRunner(strategy, request.RunId);
    }

    /// <summary>
    /// Launches the Python execution runner for a strategy. Admin-only: it starts a
    /// process on the API host that can place orders.
    /// </summary>
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    [HttpPost("{id}/start")]
    public IActionResult StartStrategy(int id, CancellationToken cancellationToken)
    {
        var strategy = GetDiscoveredStrategies().FirstOrDefault(x => x.Id == id);
        if (strategy == null) return NotFound($"Strategy {id} not found");

        return LaunchRunner(strategy, runId: null);
    }

    /// <summary>
    /// Shared spawn path for start (admin, runner creates its own run) and
    /// deploy (trader, runner attaches to an existing paper run whose
    /// parametersJson carries the wizard's configuration).
    /// </summary>
    private IActionResult LaunchRunner(Domain.Entities.StrategyDefinition strategy, long? runId)
    {
        int id = strategy.Id;

        if (_activeProcesses.ContainsKey(id))
        {
            return Conflict($"Strategy {id} is already running.");
        }

        if (_activeProcesses.Count >= _options.MaxConcurrentProcesses)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests,
                $"Concurrent strategy limit reached ({_options.MaxConcurrentProcesses}).");
        }

        var engineDirectory = ResolveEngineDirectory();
        var scriptPath = Path.Combine(engineDirectory, "strategies", "execution_runner.py");

        if (!System.IO.File.Exists(scriptPath))
        {
            _logger.LogError("Strategy runner not found at {ScriptPath}", scriptPath);
            return StatusCode(StatusCodes.Status500InternalServerError,
                $"Strategy runner not found at '{scriptPath}'. Set StrategyRunner:EngineDirectory.");
        }

        var python = ResolvePythonExecutable();

        // The strategy name reaches a command line, so allow only characters that
        // appear in a legitimate strategy identifier. This prevents a crafted
        // definition name from injecting extra arguments.
        var strategyName = new string(strategy.Name.Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrEmpty(strategyName))
        {
            return BadRequest($"Strategy name '{strategy.Name}' contains no usable characters.");
        }

        var userId = User.GetRequiredUserId();
        var startedBy = User.GetUserName() ?? "unknown";

        try
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = python,
                WorkingDirectory = engineDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // ArgumentList quotes each value for us, so paths containing spaces work
            // on every platform without manual escaping.
            processInfo.ArgumentList.Add(scriptPath);
            processInfo.ArgumentList.Add("--strategy");
            processInfo.ArgumentList.Add(strategyName);
            processInfo.ArgumentList.Add("--strategy-id");
            processInfo.ArgumentList.Add(id.ToString());
            processInfo.ArgumentList.Add("--user-id");
            processInfo.ArgumentList.Add(userId.ToString());
            if (runId.HasValue)
            {
                processInfo.ArgumentList.Add("--run-id");
                processInfo.ArgumentList.Add(runId.Value.ToString());
            }

            // The engine uses absolute package imports and resolves .env relative to
            // its own location, so PYTHONPATH must point at the engine directory.
            processInfo.Environment["PYTHONPATH"] = engineDirectory;

            var process = Process.Start(processInfo);
            if (process is null)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, "Failed to start python process.");
            }

            _activeProcesses.TryAdd(id, new RunningStrategy(process, startedBy, DateTime.UtcNow));

            _ = Task.Run(async () =>
            {
                try
                {
                    var stdout = await process.StandardOutput.ReadToEndAsync();
                    var stderr = await process.StandardError.ReadToEndAsync();
                    await process.WaitForExitAsync();

                    _logger.LogInformation(
                        "Strategy {StrategyId} ({Name}) exited with code {ExitCode}.\nSTDOUT:\n{StdOut}\nSTDERR:\n{StdErr}",
                        id, strategyName, process.ExitCode, stdout, stderr);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while draining output for strategy {StrategyId}.", id);
                }
                finally
                {
                    _activeProcesses.TryRemove(id, out _);
                }
            });

            return Ok(new
            {
                message = $"Started {strategyName}",
                processId = process.Id,
                runId,
                userId,
                startedBy
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start strategy {StrategyId}.", id);
            return StatusCode(StatusCodes.Status500InternalServerError, ex.Message);
        }
    }

    [HttpPost("{id}/stop")]
    public IActionResult StopStrategy(int id)
    {
        if (!_activeProcesses.TryGetValue(id, out var running))
        {
            return BadRequest($"Strategy {id} is not currently running from the dashboard.");
        }

        // Admins can stop anything; a trader can stop only what they deployed.
        bool isAdmin = User.IsInRole("Admin");
        var userName = User.GetUserName();
        if (!isAdmin && !string.Equals(running.StartedBy, userName, StringComparison.OrdinalIgnoreCase))
        {
            return Forbid();
        }

        try
        {
            if (!running.Process.HasExited)
            {
                // entireProcessTree: the runner may have spawned children that would
                // otherwise keep trading after the parent dies.
                running.Process.Kill(entireProcessTree: true);
                running.Process.WaitForExit(5000);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping strategy {StrategyId}.", id);
        }
        finally
        {
            _activeProcesses.TryRemove(id, out _);
        }

        return Ok(new { message = "Strategy stopped successfully." });
    }

    [HttpPost("{id}/signals")]
    public IActionResult AddSignal(int id, [FromBody] object signal)
    {
        if (_activeProcesses.TryGetValue(id, out var running))
        {
            lock (running.RecentSignals)
            {
                running.RecentSignals.Insert(0, signal);
                if (running.RecentSignals.Count > 100) running.RecentSignals.RemoveAt(100);
            }
            return Ok();
        }
        return NotFound($"Strategy {id} is not currently active.");
    }

    [HttpGet("{id}/signals")]
    public IActionResult GetSignals(int id)
    {
        if (_activeProcesses.TryGetValue(id, out var running))
        {
            lock (running.RecentSignals)
            {
                return Ok(running.RecentSignals.ToList());
            }
        }
        return Ok(new List<object>()); // return empty list if not running
    }

    /// <summary>
    /// &lt;contentRoot&gt;/../AlgoTrading.PythonEngine unless configured otherwise.
    /// </summary>
    private string ResolveEngineDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_options.EngineDirectory))
        {
            return Path.GetFullPath(_options.EngineDirectory);
        }

        return Path.GetFullPath(Path.Combine(
            _environment.ContentRootPath, "..", "AlgoTrading.PythonEngine"));
    }

    /// <summary>
    /// The repo-root virtualenv interpreter when it exists, else whatever "python3"
    /// (or "python" on Windows) resolves to on PATH.
    /// </summary>
    private string ResolvePythonExecutable()
    {
        if (!string.IsNullOrWhiteSpace(_options.PythonExecutable))
        {
            return _options.PythonExecutable;
        }

        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        // contentRoot is src/AlgoTrading.Api, so the repo root is two levels up.
        var repoRoot = Path.GetFullPath(Path.Combine(_environment.ContentRootPath, "..", ".."));
        var venvPython = isWindows
            ? Path.Combine(repoRoot, ".venv", "Scripts", "python.exe")
            : Path.Combine(repoRoot, ".venv", "bin", "python");

        if (System.IO.File.Exists(venvPython))
        {
            return venvPython;
        }

        return isWindows ? "python" : "python3";
    }
}
