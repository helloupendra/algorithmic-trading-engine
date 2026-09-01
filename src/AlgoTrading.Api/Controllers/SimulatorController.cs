// src/AlgoTrading.Api/Controllers/SimulatorController.cs
using AlgoTrading.Application.UseCases.Simulator;
using AlgoTrading.Contracts.Simulator;
using Microsoft.AspNetCore.Mvc;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// Exposes endpoints to manage backtesting and paper trading simulation runs.
/// Allows starting a run, injecting signals, and reviewing paper portfolios and orders.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class SimulatorController : ControllerBase
{
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

        return Ok(result);
    }

    [HttpGet("runs")]
    public async Task<IActionResult> GetRuns([FromQuery] long? userId, CancellationToken cancellationToken)
    {
        var result = await _getSimulationRunsUseCase.ExecuteAsync(userId, cancellationToken);
        return Ok(result);
    }


    [HttpPost("runs/{id:long}/start")]
    public async Task<IActionResult> StartRun(long id, CancellationToken cancellationToken)
    {
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
        var result = await _getSimulationSignalsUseCase.ExecuteAsync(id, cancellationToken);
        return Ok(result);
    }

    [HttpGet("runs/{id:long}/orders")]
    public async Task<IActionResult> GetPaperOrders(long id, CancellationToken cancellationToken)
    {
        var result = await _getPaperOrdersUseCase.ExecuteAsync(id, cancellationToken);
        return Ok(result);
    }

    [HttpGet("runs/{id:long}/positions")]
    public async Task<IActionResult> GetPaperPositions(long id, CancellationToken cancellationToken)
    {
        var result = await _getPaperPositionsUseCase.ExecuteAsync(id, cancellationToken);
        return Ok(result);
    }

    [HttpGet("runs/{id:long}/portfolio")]
    public async Task<IActionResult> GetPortfolio(long id, CancellationToken cancellationToken)
    {
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
        var result = await _getSimulationEquityCurveUseCase.ExecuteAsync(id, cancellationToken);
        return Ok(result);
    }

    [HttpGet("runs/{id:long}/performance")]
    public async Task<IActionResult> GetPerformance(long id, CancellationToken cancellationToken)
    {
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

}