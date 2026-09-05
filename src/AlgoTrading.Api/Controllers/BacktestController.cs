using AlgoTrading.Domain.Constants;
// src/AlgoTrading.Api/Controllers/BacktestController.cs
using AlgoTrading.Api.Configuration;
using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Backtest;
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Globalization;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// Backtesting: what historical data exists (coverage-first), backfill of the
/// gaps, and the lifecycle of OfflineReplay runs driven by the Python backtest
/// runner (tools/backtest_runner.py). Any signed-in user may read; start,
/// stop, delete and backfill are admin-only because they launch processes on
/// the API host and call the broker.
/// </summary>
[RequireModule(PlatformModules.Backtesting)]
[ApiController]
[Route("api/[controller]")]
public class BacktestController : ControllerBase
{
    private const string OfflineReplayMode = "OfflineReplay";
    private const decimal DefaultInitialCapital = 1_000_000m;
    private const int DefaultLogTake = 200;

    private readonly TradingDbContext _dbContext;
    private readonly StrategyCatalogService _catalog;
    private readonly BacktestProcessRegistry _registry;
    private readonly BacktestRunControl _runControl;
    private readonly PythonEngineLocator _engine;
    private readonly BacktestDataService _data;
    private readonly BacktestRunViewBuilder _views;
    private readonly ILotSizeResolver _lotSizeResolver;
    private readonly StrategyRunnerOptions _options;
    private readonly ILogger<BacktestController> _logger;

    public BacktestController(
        TradingDbContext dbContext,
        StrategyCatalogService catalog,
        BacktestProcessRegistry registry,
        BacktestRunControl runControl,
        PythonEngineLocator engine,
        BacktestDataService data,
        BacktestRunViewBuilder views,
        ILotSizeResolver lotSizeResolver,
        IOptions<StrategyRunnerOptions> options,
        ILogger<BacktestController> logger)
    {
        _dbContext = dbContext;
        _catalog = catalog;
        _registry = registry;
        _runControl = runControl;
        _engine = engine;
        _data = data;
        _views = views;
        _lotSizeResolver = lotSizeResolver;
        _options = options.Value;
        _logger = logger;
    }

    // ------------------------------------------------------------------
    // Coverage & backfill
    // ------------------------------------------------------------------

    /// <summary>What data exists for the underlying, per resolution, before anything is picked.</summary>
    [HttpGet("coverage")]
    public async Task<ActionResult<BacktestCoverageResponse>> GetCoverage(
        [FromQuery] string? underlying,
        [FromQuery] int? strategyId,
        [FromQuery] string? resolution,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(underlying))
            return BadRequest(new { message = "underlying is required — e.g. ?underlying=BANKNIFTY." });

        if (!string.IsNullOrWhiteSpace(resolution) && !ResolutionCodes.IsAllowed(resolution))
            return BadRequest(new { message = $"resolution must be one of {string.Join(", ", ResolutionCodes.Allowed)}." });

        var result = await _data.GetCoverageAsync(underlying, strategyId, resolution, cancellationToken);
        return Ok(result);
    }

    /// <summary>Pulls the spot symbol's candles from FYERS in ≤ 30-day chunks per resolution.</summary>
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    [HttpPost("backfill")]
    public async Task<ActionResult<BacktestBackfillResponse>> Backfill([FromBody] BacktestBackfillRequest? request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Underlying))
            return BadRequest(new { message = "underlying is required." });

        if (request.Resolutions is null || request.Resolutions.Count == 0)
            return BadRequest(new { message = "resolutions is required — e.g. [\"5\", \"1\"]." });

        var bad = request.Resolutions.FirstOrDefault(r => !ResolutionCodes.IsAllowed(r));
        if (bad is not null)
            return BadRequest(new { message = $"resolution '{bad}' is not supported; use one of {string.Join(", ", ResolutionCodes.Allowed)}." });

        if (!TryParseDate(request.FromDate, out var fromDate))
            return BadRequest(new { message = "fromDate must be yyyy-MM-dd." });
        if (!TryParseDate(request.ToDate, out var toDate))
            return BadRequest(new { message = "toDate must be yyyy-MM-dd." });

        var today = IstTime.DateOf(DateTime.UtcNow);
        if (toDate < fromDate)
            return BadRequest(new { message = "toDate must be on or after fromDate." });
        if (fromDate > today)
            return BadRequest(new { message = "fromDate cannot be in the future." });
        if (toDate > today) toDate = today;

        try
        {
            var result = await _data.BackfillAsync(request.Underlying, request.Resolutions, fromDate, toDate, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    // ------------------------------------------------------------------
    // Runs
    // ------------------------------------------------------------------

    /// <summary>Validates the request, creates the OfflineReplay run and spawns the backtest runner.</summary>
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    [HttpPost("runs")]
    public async Task<IActionResult> StartRun([FromBody] StartBacktestRequest? request, CancellationToken cancellationToken)
    {
        if (request is null)
            return BadRequest(new { message = "A request body is required." });

        if (request.StrategyId <= 0)
            return BadRequest(new { message = "strategyId is required." });

        var strategy = await _catalog.FindAsync(request.StrategyId, cancellationToken);
        if (strategy is null)
            return NotFound(new { message = $"Strategy {request.StrategyId} not found." });

        if (!string.IsNullOrWhiteSpace(strategy.Error))
            return BadRequest(new { message = $"{strategy.Name} cannot be backtested: {strategy.Error}" });

        if (string.IsNullOrWhiteSpace(request.Underlying))
            return BadRequest(new { message = "underlying is required — pick the index or stock the strategy should trade." });

        var underlying = request.Underlying.Trim().ToUpperInvariant();

        if (string.IsNullOrWhiteSpace(request.Resolution) || !ResolutionCodes.IsAllowed(request.Resolution))
            return BadRequest(new { message = $"resolution must be one of {string.Join(", ", ResolutionCodes.Allowed)}." });
        var resolution = ResolutionCodes.ToCandle(request.Resolution);

        if (!TryParseDate(request.FromDate, out var fromDate))
            return BadRequest(new { message = "fromDate is required (yyyy-MM-dd)." });
        if (!TryParseDate(request.ToDate, out var toDate))
            return BadRequest(new { message = "toDate is required (yyyy-MM-dd)." });

        var today = IstTime.DateOf(DateTime.UtcNow);
        if (toDate < fromDate)
            return BadRequest(new { message = "toDate must be on or after fromDate." });
        if (toDate > today)
            return BadRequest(new { message = "toDate cannot be after today." });

        int lots = request.Lots ?? Math.Max(1, strategy.DefaultLots);
        if (lots < 1)
            return BadRequest(new { message = "lots must be at least 1." });

        if (request.StopLoss.HasValue && request.StopLoss.Value <= 0)
            return BadRequest(new { message = "stopLoss must be a positive rupee amount, or omitted." });

        if (request.Target.HasValue && request.Target.Value <= 0)
            return BadRequest(new { message = "target must be a positive rupee amount, or omitted." });

        if (!RiskRulesDto.TryValidate(request.Risk, out var riskError))
            return BadRequest(new { message = $"risk: {riskError}" });

        // The engine enforces every level during the replay; the API only stores them.
        var risk = RunRiskRules.Resolve(request.Risk, request.StopLoss, request.Target);

        decimal chargesPerLot = request.ChargesPerLot ?? 0m;
        if (chargesPerLot < 0)
            return BadRequest(new { message = "chargesPerLot cannot be negative." });

        decimal initialCapital = request.InitialCapital ?? DefaultInitialCapital;
        if (initialCapital <= 0)
            return BadRequest(new { message = "initialCapital must be positive." });

        var eod = BacktestRunParameters.NormalizeEodSquareOff(request.EodSquareOffIst, out var eodError);
        if (eod is null)
            return BadRequest(new { message = eodError });

        if (!await HasOptionContractsAsync(underlying, cancellationToken))
            return BadRequest(new { message = $"No option contracts loaded for {underlying} — import the F&O master first." });

        var spotSymbol = UnderlyingCatalog.SpotSymbolFor(underlying);
        var fromUtc = IstTime.StartOfDayUtc(fromDate);
        var toUtc = IstTime.EndOfDayUtc(toDate);

        int bars = await _data.CountIndexCandlesAsync(spotSymbol, resolution, fromUtc, toUtc, cancellationToken);
        if (bars == 0)
        {
            return BadRequest(new
            {
                message = $"No {underlying} {ResolutionCodes.Label(resolution)} candles between {fromDate:yyyy-MM-dd} and {toDate:yyyy-MM-dd} — backfill first."
            });
        }

        // Every index resolution the strategy declares must be stored too;
        // otherwise the runner would feed it an empty series bar after bar and
        // the run would "complete" with zero trades (the runner refuses as well).
        foreach (var requiredResolution in RequiredIndexResolutions(strategy, resolution))
        {
            int stored = await _data.CountIndexCandlesAsync(spotSymbol, requiredResolution, fromUtc, toUtc, cancellationToken);
            if (stored == 0)
            {
                return BadRequest(new
                {
                    message = $"{strategy.Name} needs {ResolutionCodes.Label(requiredResolution)} index candles and none are stored for {underlying} between {fromDate:yyyy-MM-dd} and {toDate:yyyy-MM-dd} — backfill {ResolutionCodes.Label(requiredResolution)} first."
                });
            }
        }

        if (_registry.Count >= _options.MaxConcurrentBacktests)
            return StatusCode(StatusCodes.Status429TooManyRequests,
                new { message = $"Concurrent backtest limit reached ({_options.MaxConcurrentBacktests}); wait for a run to finish or stop one." });

        if (strategy.SupportedUnderlyings.Count > 0
            && !strategy.SupportedUnderlyings.Contains(underlying, StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogWarning("{Strategy} backtested on {Underlying}, which is not in its supported list ({Supported}).",
                strategy.Name, underlying, string.Join(", ", strategy.SupportedUnderlyings));
        }

        var userId = User.GetRequiredUserId();
        var startedBy = User.GetUserName() ?? "unknown";
        var now = DateTime.UtcNow;

        // The current lot size is frozen into the run so the runner's ledger,
        // the paper engine and the views all book with the same number.
        var lot = await _lotSizeResolver.ResolveForUnderlyingAsync(underlying, cancellationToken);

        var run = new SimulationRun
        {
            UserId = userId,
            Mode = OfflineReplayMode,
            Symbol = spotSymbol,
            Resolution = resolution,
            FromUtc = fromUtc,
            ToUtc = toUtc,
            ReplaySpeed = "max",
            Status = BacktestRunControl.RunStatusPending,
            StrategyName = strategy.Name,
            ParametersJson = BacktestRunParameters.Merge(
                strategy.DefaultParametersJson, request.Parameters, lots, risk,
                underlying, resolution, eod, chargesPerLot, lot.LotSize, lot.Source),
            InitialCapital = initialCapital,
            CreatedUtc = now
        };

        await _dbContext.SimulationRuns.AddAsync(run, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var (error, running, capReached) = LaunchRunner(strategy, run, userId, startedBy, underlying, spotSymbol, lots, risk.OverallStopLoss, risk.OverallTarget);
        if (capReached)
        {
            // Lost the race for the last slot: nothing was launched, so the row
            // must not survive as a "Failed" run nobody started.
            _dbContext.SimulationRuns.Remove(run);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return error ?? StatusCode(StatusCodes.Status429TooManyRequests,
                new { message = $"Concurrent backtest limit reached ({_options.MaxConcurrentBacktests})." });
        }
        if (error is not null || running is null)
        {
            run.Status = BacktestRunControl.RunStatusFailed;
            run.LastError = "Backtest runner failed to start.";
            run.CompletedUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return error ?? StatusCode(StatusCodes.Status500InternalServerError, new { message = "Failed to start the backtest runner." });
        }

        run.Status = BacktestRunControl.RunStatusRunning;
        run.StartedUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Durable pid so a restarted API can adopt (or stop) this runner.
        await _runControl.RecordRunnerPidAsync(run.Id, running.ProcessId, startedBy);

        return Ok(new
        {
            runId = run.Id,
            message = $"Started backtest of {strategy.Name} on {underlying} ({ResolutionCodes.Label(resolution)}, {fromDate:yyyy-MM-dd} → {toDate:yyyy-MM-dd}, {bars} bars)."
        });
    }

    /// <summary>Stops a running backtest: SIGTERM/kill the runner, square off at last mark, mark Stopped.</summary>
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    [HttpPost("runs/{id:long}/stop")]
    public async Task<IActionResult> StopRun(long id, CancellationToken cancellationToken)
    {
        var userName = User.GetUserName() ?? "unknown";
        var result = await _runControl.StopAsync(id, $"Stopped by {userName}", userName, cancellationToken);
        if (!result.WasRunning)
            return BadRequest(new { message = $"Backtest run {id} is not running." });

        return Ok(new
        {
            message = result.Flattened > 0
                ? $"Stopped backtest run {id}; squared off {result.Flattened} open position(s) at last mark."
                : $"Stopped backtest run {id}."
        });
    }

    /// <summary>Deletes a finished run and all of its signals, orders, positions and equity snapshots.</summary>
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    [HttpDelete("runs/{id:long}")]
    public async Task<IActionResult> DeleteRun(long id, CancellationToken cancellationToken)
    {
        var run = await _dbContext.SimulationRuns
            .FirstOrDefaultAsync(x => x.Id == id && x.Mode == OfflineReplayMode, cancellationToken);
        if (run is null)
            return NotFound(new { message = $"Backtest run {id} not found." });

        // Only a run with a live runner behind it is protected. A row left
        // Running with no registry entry (API restart, monitor failure) has no
        // process to stop, so refusing would make it undeletable for good.
        if (_registry.Contains(id))
            return BadRequest(new { message = $"Backtest run {id} is running — stop it before deleting." });

        if (run.Status is BacktestRunControl.RunStatusRunning or BacktestRunControl.RunStatusPending)
        {
            _logger.LogWarning("Deleting backtest run {RunId}, which is marked {Status} but has no runner process (orphaned).", id, run.Status);
        }

        await using var tx = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        await _dbContext.SimulationEquitySnapshots.Where(x => x.SimulationRunId == id).ExecuteDeleteAsync(cancellationToken);
        await _dbContext.PaperPositions.Where(x => x.SimulationRunId == id).ExecuteDeleteAsync(cancellationToken);
        await _dbContext.PaperOrders.Where(x => x.SimulationRunId == id).ExecuteDeleteAsync(cancellationToken);
        await _dbContext.SimulationSignals.Where(x => x.SimulationRunId == id).ExecuteDeleteAsync(cancellationToken);
        _dbContext.SimulationRuns.Remove(run);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await tx.CommitAsync(cancellationToken);

        _logger.LogInformation("Deleted backtest run {RunId} ({Strategy}) by {User}.", id, run.StrategyName, User.GetUserName());
        return NoContent();
    }

    /// <summary>OfflineReplay runs, newest first, at most 200.</summary>
    [HttpGet("runs")]
    public async Task<ActionResult<List<BacktestRunSummaryResponse>>> GetRuns(CancellationToken cancellationToken)
        => Ok(await _views.ListAsync(cancellationToken));

    /// <summary>The full results view of one run.</summary>
    [HttpGet("runs/{id:long}")]
    public async Task<ActionResult<BacktestRunViewResponse>> GetRun(long id, CancellationToken cancellationToken)
    {
        var view = await _views.BuildAsync(id, cancellationToken);
        if (view is null)
            return NotFound(new { message = $"Backtest run {id} not found." });
        return Ok(view);
    }

    /// <summary>
    /// Recent runner stdout/stderr: the live ring buffer while the process runs,
    /// its final snapshot for the most recently finished runs, else empty.
    /// </summary>
    [HttpGet("runs/{id:long}/logs")]
    public IActionResult GetLogs(long id, [FromQuery] int take = DefaultLogTake)
        => Ok(_registry.GetLogs(id, take));

    // ------------------------------------------------------------------
    // Launch plumbing
    // ------------------------------------------------------------------

    /// <summary>
    /// Index resolutions the strategy declares (canonical codes) other than the
    /// driver, which StartRun has already checked.
    /// </summary>
    private static IEnumerable<string> RequiredIndexResolutions(StrategyCatalogEntry strategy, string driverResolution)
        => strategy.DataRequirements
            .Where(r => string.Equals(r.SymbolType, "index", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(r.Resolution))
            .Select(r => ResolutionCodes.ToCandle(r.Resolution))
            .Where(r => !string.Equals(r, driverResolution, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal);

    /// <summary>
    /// Spawns tools/backtest_runner.py --run-id N and registers it. Returns an
    /// error result instead of throwing so the caller can mark the run Failed;
    /// <c>CapReached</c> means nothing was launched because the concurrency cap
    /// was hit under the start lock.
    /// </summary>
    private (IActionResult? Error, RunningBacktest? Running, bool CapReached) LaunchRunner(
        StrategyCatalogEntry strategy,
        SimulationRun run,
        long userId,
        string startedBy,
        string underlying,
        string spotSymbol,
        int lots,
        decimal? stopLoss,
        decimal? target)
    {
        var engineDirectory = _engine.EngineDirectory;
        var scriptPath = _engine.ScriptPath("tools", "backtest_runner.py");

        if (!System.IO.File.Exists(scriptPath))
        {
            _logger.LogError("Backtest runner not found at {ScriptPath}", scriptPath);
            return (StatusCode(StatusCodes.Status500InternalServerError,
                new { message = $"Backtest runner not found at '{scriptPath}'. Set StrategyRunner:EngineDirectory." }), null, false);
        }

        var processInfo = new ProcessStartInfo
        {
            FileName = _engine.PythonExecutable,
            WorkingDirectory = engineDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // ArgumentList quotes each value, so paths with spaces work on every platform.
        processInfo.ArgumentList.Add(scriptPath);
        processInfo.ArgumentList.Add("--run-id");
        processInfo.ArgumentList.Add(run.Id.ToString(CultureInfo.InvariantCulture));

        // The engine uses absolute package imports and resolves .env relative to
        // its own location, so PYTHONPATH must point at the engine directory.
        processInfo.Environment["PYTHONPATH"] = engineDirectory;
        // Line-buffered output so log lines arrive as they happen, not in 8KB blocks.
        processInfo.Environment["PYTHONUNBUFFERED"] = "1";
        // A redirected stdout takes the locale encoding on Windows (cp1252); the
        // runner prints "→", "≤", "₹"... — force UTF-8 on the pipe everywhere.
        processInfo.Environment["PYTHONIOENCODING"] = "utf-8";

        lock (_registry.StartLock)
        {
            if (_registry.Count >= _options.MaxConcurrentBacktests)
            {
                return (StatusCode(StatusCodes.Status429TooManyRequests,
                    new { message = $"Concurrent backtest limit reached ({_options.MaxConcurrentBacktests})." }), null, true);
            }

            Process? process = null;
            try
            {
                process = new Process { StartInfo = processInfo, EnableRaisingEvents = true };
                if (!process.Start())
                {
                    process.Dispose();
                    return (StatusCode(StatusCodes.Status500InternalServerError, new { message = "Failed to start python process." }), null, false);
                }

                var running = new RunningBacktest(
                    run.Id, strategy.Id, strategy.Name, process, startedBy, userId, DateTime.UtcNow,
                    underlying, spotSymbol, lots, stopLoss, target,
                    ResolutionCodes.ToCandle(run.Resolution), run.FromUtc ?? DateTime.MinValue, run.ToUtc ?? DateTime.MinValue);

                if (!_registry.TryAdd(running))
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                    process.Dispose();
                    return (Conflict(new { message = $"Backtest run {run.Id} is already registered." }), null, false);
                }

                _logger.LogInformation(
                    "Started backtest run {RunId} ({Name}) pid {Pid} on {Underlying} ({Spot}) @{Resolution} x{Lots} SL={StopLoss} T={Target} by {User}",
                    run.Id, strategy.Name, running.ProcessId, underlying, spotSymbol, running.Resolution, lots, stopLoss, target, startedBy);

                return (null, running, false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start backtest run {RunId}.", run.Id);
                try { process?.Dispose(); } catch { /* ignore */ }
                return (StatusCode(StatusCodes.Status500InternalServerError, new { message = ex.Message }), null, false);
            }
        }
    }

    /// <summary>True when the underlying has at least one CE/PE contract in the master (any expiry).</summary>
    private Task<bool> HasOptionContractsAsync(string underlying, CancellationToken cancellationToken)
        => _dbContext.Instruments
            .AsNoTracking()
            .AnyAsync(x => x.IsEnabled
                        && x.Underlying == underlying
                        && (x.OptionType == "CE" || x.OptionType == "PE")
                        && x.ExpiryDate.HasValue,
                cancellationToken);

    private static bool TryParseDate(string? text, out DateOnly date)
        => DateOnly.TryParseExact((text ?? string.Empty).Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}
