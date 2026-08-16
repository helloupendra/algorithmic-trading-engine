using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AlgoTrading.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StrategyController : ControllerBase
{
    private readonly TradingDbContext _dbContext;
    
    // Store process instances keyed by strategy ID
    private static readonly ConcurrentDictionary<int, Process> _activeProcesses = new();

    public StrategyController(TradingDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<object>>> GetAll(CancellationToken cancellationToken)
    {
        var strategies = await _dbContext.Strategies.ToListAsync(cancellationToken);
        return strategies.Select(s => new 
        {
            s.Id,
            s.Name,
            s.Description,
            s.DefaultParametersJson,
            s.CreatedUtc,
            IsActive = _activeProcesses.ContainsKey(s.Id)
        }).ToList();
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<StrategyDefinition>> GetById(int id, CancellationToken cancellationToken)
    {
        var strategy = await _dbContext.Strategies.FindAsync(new object[] { id }, cancellationToken);
        if (strategy == null) return NotFound();
        return strategy;
    }

    [HttpPost]
    public async Task<ActionResult<StrategyDefinition>> Create(StrategyDefinition strategy, CancellationToken cancellationToken)
    {
        _dbContext.Strategies.Add(strategy);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = strategy.Id }, strategy);
    }

    [HttpPost("{id}/start")]
    public async Task<IActionResult> StartStrategy(int id, CancellationToken cancellationToken)
    {
        var strategy = await _dbContext.Strategies.FindAsync(new object[] { id }, cancellationToken);
        if (strategy == null) return NotFound($"Strategy {id} not found");

        if (_activeProcesses.ContainsKey(id))
        {
            return BadRequest($"Strategy {id} is already running.");
        }

        try
        {
            var workingDir = "/Users/upendra/Documents/Work/upendra/AlgoTrading/src/AlgoTrading.PythonEngine";
            var scriptPath = Path.Combine(workingDir, "strategies", "execution_runner.py");

            // Extract the simple strategy name, e.g., "Titli (Multi 50)" -> "TitliMulti50"
            // Wait, we need to pass a clean string to the CLI. 
            // The frontend passes ID, let's map it based on strategy name or just pass strategy.Name.
            var strategyNameArgs = strategy.Name;
            
            // "Titli (Multi 50)" might be registered as "TitliMulti50" in python.
            // Let's strip out spaces and parentheses just in case.
            var cleanStrategyName = strategyNameArgs.Replace(" ", "").Replace("(", "").Replace(")", "");

            var processInfo = new ProcessStartInfo
            {
                FileName = "/Library/Frameworks/Python.framework/Versions/3.12/Resources/Python.app/Contents/MacOS/Python",
                Arguments = $"{scriptPath} --strategy {cleanStrategyName} --user-id 1",
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = Process.Start(processInfo);
            if (process != null)
            {
                _activeProcesses.TryAdd(id, process);
                
                // Fire-and-forget logging
                _ = Task.Run(async () =>
                {
                    var stdout = await process.StandardOutput.ReadToEndAsync();
                    var stderr = await process.StandardError.ReadToEndAsync();
                    System.Console.WriteLine($"[Process {id} exited]\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
                    _activeProcesses.TryRemove(id, out _);
                });

                return Ok(new { message = $"Started {cleanStrategyName}", processId = process.Id });
            }
            
            return StatusCode(500, "Failed to start python process.");
        }
        catch (System.Exception ex)
        {
            return StatusCode(500, ex.Message);
        }
    }

    [HttpPost("{id}/stop")]
    public IActionResult StopStrategy(int id)
    {
        if (_activeProcesses.TryGetValue(id, out var process))
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(1000); // give it a sec to die
                }
            }
            catch { /* Ignore errors on kill */ }
            finally
            {
                _activeProcesses.TryRemove(id, out _);
            }

            return Ok(new { message = "Strategy stopped successfully." });
        }

        return BadRequest($"Strategy {id} is not currently running from the dashboard.");
    }
}
