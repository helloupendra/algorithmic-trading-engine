using AlgoTrading.Domain.Constants;
// src/AlgoTrading.Api/Controllers/SimulatorController.cs
using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.UseCases.Simulator;
using AlgoTrading.Contracts.Backtest;
using AlgoTrading.Contracts.Simulator;
using Microsoft.AspNetCore.Mvc;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// Exposes endpoints to manage backtesting and paper trading simulation runs.
/// Allows starting a run, injecting signals, and reviewing paper portfolios and orders.
/// The runs/{id}/equity-snapshots, marks, progress and complete endpoints are
/// the backtest runner's write path (spec §2.4).
/// </summary>
// Simulation runs are how a strategy run is created, so they belong to the
// same grant.
[RequireModule(PlatformModules.Strategies)]
[ApiController]
[Route("api/[controller]")]
public class SimulatorController : ControllerBase
{
    private const int MaxEquitySnapshotBatch = 5000;

    private readonly IPaperTradingService _paperTrading;
    private readonly BacktestProcessRegistry _backtests;
    private readonly CreateSimulationRunUseCase _createSimulationRunUseCase;
    private readonly GetSimulationRunUseCase _getSimulationRunUseCase;
    private readonly GetSimulationRunsUseCase _getSimulationRunsUseCase;
    private readonly StartSimulationRunUseCase _startSimulationRunUseCase;
    private readonly CreateSimulationSignalUseCase _createSimulationSignalUseCase;
    private readonly GetSimulationSignalsUseCase _getSimulationSignalsUseCase;
    private readonly GetPaperOrdersUseCase _getPaperOrdersUseCase;
    private readonly GetPaperPositionsUseCase _getPaperPositionsUseCase;
    private readonly GetSimulationPortfolioUseCase _getSimulationPortfolioUseCase;

    private readonly RefreshSimulationPortfolioUseCase _refreshSimulationPortfolioUseCase;
    private readonly GetSimulationEquityCurveUseCase _getSimulationEquityCurveUseCase;
    private readonly GetSimulationPerformanceUseCase _getSimulationPerformanceUseCase;

    public SimulatorController(
        IPaperTradingService paperTrading,
        BacktestProcessRegistry backtests,
        CreateSimulationRunUseCase createSimulationRunUseCase,
        GetSimulationRunUseCase getSimulationRunUseCase,
        GetSimulationRunsUseCase getSimulationRunsUseCase,
        StartSimulationRunUseCase startSimulationRunUseCase,
        CreateSimulationSignalUseCase createSimulationSignalUseCase,
        GetSimulationSignalsUseCase getSimulationSignalsUseCase,
        GetPaperOrdersUseCase getPaperOrdersUseCase,
        GetPaperPositionsUseCase getPaperPositionsUseCase,
        GetSimulationPortfolioUseCase getSimulationPortfolioUseCase,
        RefreshSimulationPortfolioUseCase refreshSimulationPortfolioUseCase,
        GetSimulationEquityCurveUseCase getSimulationEquityCurveUseCase,
        GetSimulationPerformanceUseCase getSimulationPerformanceUseCase)
    {
        _paperTrading = paperTrading;
        _backtests = backtests;
        _createSimulationRunUseCase = createSimulationRunUseCase;
        _getSimulationRunUseCase = getSimulationRunUseCase;
        _getSimulationRunsUseCase = getSimulationRunsUseCase;
        _startSimulationRunUseCase = startSimulationRunUseCase;
        _createSimulationSignalUseCase = createSimulationSignalUseCase;
        _getSimulationSignalsUseCase = getSimulationSignalsUseCase;
        _getPaperOrdersUseCase = getPaperOrdersUseCase;
        _getPaperPositionsUseCase = getPaperPositionsUseCase;
        _getSimulationPortfolioUseCase = getSimulationPortfolioUseCase;
        _refreshSimulationPortfolioUseCase = refreshSimulationPortfolioUseCase;
        _getSimulationEquityCurveUseCase = getSimulationEquityCurveUseCase;
        _getSimulationPerformanceUseCase = getSimulationPerformanceUseCase;


    }

    [HttpPost("runs")]
    public async Task<IActionResult> CreateRun(
        [FromBody] CreateSimulationRunRequest request,
        CancellationToken cancellationToken)

    {
        request.UserId = User.GetRequiredUserId();

        if (string.IsNullOrWhiteSpace(request.Mode))
            return BadRequest(new { message = "Mode is required." });

        if (string.IsNullOrWhiteSpace(request.Symbol))
            return BadRequest(new { message = "Symbol is required." });

        if (string.IsNullOrWhiteSpace(request.Resolution))
            return BadRequest(new { message = "Resolution is required." });

        try
        {
            var result = await _createSimulationRunUseCase.ExecuteAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }


    [HttpGet("runs/{id:long}")]
    public async Task<IActionResult> GetRun(long id, CancellationToken cancellationToken)
    {
        var result = await _getSimulationRunUseCase.ExecuteAsync(id, cancellationToken);

        if (result is null)
            return NotFound(new { message = "Simulation run not found." });

        if (!User.IsInRole("Admin") && result.UserId != User.GetRequiredUserId())
            return Forbid();

        return Ok(result);
    }

    [HttpGet("runs")]
    public async Task<IActionResult> GetRuns([FromQuery] long? userId, CancellationToken cancellationToken)
    {
        if (!User.IsInRole("Admin"))
        {
            userId = User.GetRequiredUserId();
        }
        var result = await _getSimulationRunsUseCase.ExecuteAsync(userId, cancellationToken);
        return Ok(result);
    }

    private async Task<bool> IsRunOwnedByCallerAsync(long runId, CancellationToken ct)
    {
        if (User.IsInRole("Admin")) return true;
        var run = await _getSimulationRunUseCase.ExecuteAsync(runId, ct);
        return run != null && run.UserId == User.GetRequiredUserId();
    }

    [HttpPost("runs/{id:long}/start")]
    public async Task<IActionResult> StartRun(long id, CancellationToken cancellationToken)
    {
        if (!await IsRunOwnedByCallerAsync(id, cancellationToken))
            return Forbid();
        try
        {
            var result = await _startSimulationRunUseCase.ExecuteAsync(id, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("signals")]
    public async Task<IActionResult> CreateSignal(
    [FromBody] CreateSimulationSignalRequest request,
    CancellationToken cancellationToken)
    {
        if (request.SimulationRunId <= 0)
            return BadRequest(new { message = "SimulationRunId is required." });

        if (string.IsNullOrWhiteSpace(request.StrategyName))
            return BadRequest(new { message = "StrategyName is required." });

        if (string.IsNullOrWhiteSpace(request.SignalType))
            return BadRequest(new { message = "SignalType is required." });

        try
        {
            var result = await _createSimulationSignalUseCase.ExecuteAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("runs/{id:long}/signals")]
    public async Task<IActionResult> GetSignals(long id, CancellationToken cancellationToken)
    {
        if (!await IsRunOwnedByCallerAsync(id, cancellationToken))
            return Forbid();

        var result = await _getSimulationSignalsUseCase.ExecuteAsync(id, cancellationToken);
        return Ok(result);
    }

    [HttpGet("runs/{id:long}/orders")]
    public async Task<IActionResult> GetPaperOrders(long id, CancellationToken cancellationToken)
    {
        if (!await IsRunOwnedByCallerAsync(id, cancellationToken))
            return Forbid();

        var result = await _getPaperOrdersUseCase.ExecuteAsync(id, cancellationToken);
        return Ok(result);
    }

    [HttpGet("runs/{id:long}/positions")]
    public async Task<IActionResult> GetPaperPositions(long id, CancellationToken cancellationToken)
    {
        if (!await IsRunOwnedByCallerAsync(id, cancellationToken))
            return Forbid();

        var result = await _getPaperPositionsUseCase.ExecuteAsync(id, cancellationToken);
        return Ok(result);
    }

    [HttpGet("runs/{id:long}/portfolio")]
    public async Task<IActionResult> GetPortfolio(long id, CancellationToken cancellationToken)
    {
        if (!await IsRunOwnedByCallerAsync(id, cancellationToken))
            return Forbid();

        try
        {
            var result = await _getSimulationPortfolioUseCase.ExecuteAsync(id, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    [HttpPost("runs/{id:long}/portfolio/refresh")]
    public async Task<IActionResult> RefreshPortfolio(long id, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _refreshSimulationPortfolioUseCase.ExecuteAsync(id, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    [HttpGet("runs/{id:long}/equity-curve")]
    public async Task<IActionResult> GetEquityCurve(long id, CancellationToken cancellationToken)
    {
        if (!await IsRunOwnedByCallerAsync(id, cancellationToken))
            return Forbid();

        var result = await _getSimulationEquityCurveUseCase.ExecuteAsync(id, cancellationToken);
        return Ok(result);
    }

    [HttpGet("runs/{id:long}/performance")]
    public async Task<IActionResult> GetPerformance(long id, CancellationToken cancellationToken)
    {
        if (!await IsRunOwnedByCallerAsync(id, cancellationToken))
            return Forbid();

        try
        {
            var result = await _getSimulationPerformanceUseCase.ExecuteAsync(id, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    // ------------------------------------------------------------------
    // Backtest runner write path (OfflineReplay runs)
    // ------------------------------------------------------------------

    /// <summary>Bulk equity points with HISTORICAL SnapshotUtc; OfflineReplay only; at most 5000 per call.</summary>
    [HttpPost("runs/{id:long}/equity-snapshots")]
    public async Task<IActionResult> AddEquitySnapshots(
        long id,
        [FromBody] List<EquitySnapshotBatchItem>? items,
        CancellationToken cancellationToken)
    {
        if (items is null)
            return BadRequest(new { message = "A JSON array of equity snapshots is required." });

        if (items.Count > MaxEquitySnapshotBatch)
            return BadRequest(new { message = $"At most {MaxEquitySnapshotBatch} equity snapshots per request." });

        try
        {
            int inserted = await _paperTrading.AddEquitySnapshotsAsync(id, items, cancellationToken);
            return Ok(new { inserted });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>Bar-close marks for the run's open positions (LastMarkPrice, UnrealizedPnl, UpdatedUtc = atUtc).</summary>
    [HttpPost("runs/{id:long}/marks")]
    public async Task<IActionResult> ApplyMarks(long id, [FromBody] RunMarksRequest? request, CancellationToken cancellationToken)
    {
        if (request is null || request.Marks is null)
            return BadRequest(new { message = "atUtc and marks are required." });

        try
        {
            int updated = await _paperTrading.ApplyMarksAsync(id, request, cancellationToken);
            return Ok(new { updated });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Runner progress; registry only. 404 when the run is not in the registry.
    /// A body that names the runner's processId confirms the pid on record
    /// (persisted once, so a restarted API can adopt the runner).
    /// </summary>
    [HttpPost("runs/{id:long}/progress")]
    public async Task<IActionResult> ReportProgress(
        long id,
        [FromBody] RunProgressRequest? request,
        [FromServices] BacktestRunControl runControl)
    {
        if (request is null)
            return BadRequest(new { message = "A progress body is required." });

        bool updated = _backtests.UpdateProgress(id, request.Percent, request.BarsProcessed, request.TotalBars,
            request.CurrentUtc, request.Trades, request.Message);
        if (!updated)
            return NotFound(new { message = $"Backtest run {id} is not running." });

        if (request.ProcessId is > 0 && _backtests.ConfirmPid(id, request.ProcessId.Value))
        {
            await runControl.RecordRunnerPidAsync(id, request.ProcessId.Value, "runner");
        }

        return Ok();
    }

    /// <summary>Final verdict from the runner: Status, CompletedUtc, LastError and the BACKTEST_SUMMARY signal.</summary>
    [HttpPost("runs/{id:long}/complete")]
    public async Task<IActionResult> CompleteRun(long id, [FromBody] CompleteRunRequest? request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Status))
            return BadRequest(new { message = "status (\"Completed\" | \"Failed\") is required." });

        try
        {
            await _paperTrading.CompleteRunAsync(id, request, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }

        bool completed = string.Equals(request.Status.Trim(), "Completed", StringComparison.OrdinalIgnoreCase);
        _backtests.AppendLog(id, completed
            ? "runner reported completion"
            : $"runner reported failure: {request.Error}");
        if (completed)
        {
            var current = _backtests.Get(id)?.Progress;
            _backtests.UpdateProgress(id, 100m, current?.TotalBars ?? current?.BarsProcessed ?? 0, current?.TotalBars ?? 0,
                current?.CurrentUtc, current?.Trades ?? 0, "Completed");
        }

        return Ok(new { message = completed ? $"Backtest run {id} completed." : $"Backtest run {id} marked failed." });
    }
}