// src/AlgoTrading.Api/Controllers/StrategyController.cs
using AlgoTrading.Api.Configuration;
using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.UseCases.LiveData;
using AlgoTrading.Contracts.LiveData;
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// The strategy catalog and the live paper runner: list strategies (with
/// descriptions from the Python engine), start one on a chosen underlying with
/// optional stop-loss / target, stop it (squaring off), and read its
/// position-based live view, activity and runner output.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class StrategyController : ControllerBase
{
    private const string LivePaperMode = "LivePaper";
    private const decimal DefaultInitialCapital = 1_000_000m;
    private const int ActivityLimit = 60;

    private readonly TradingDbContext _dbContext;
    private readonly StrategyCatalogService _catalog;
    private readonly StrategyProcessRegistry _registry;
    private readonly StrategyRunControl _runControl;
    private readonly PythonEngineLocator _engine;
    private readonly IPaperTradingService _paperTrading;
    private readonly ILotSizeResolver _lotSizeResolver;
    private readonly PositionViewBuilder _positionViews;
    private readonly UpsertWatchlistItemUseCase _upsertWatchlistItem;
    private readonly StrategyRunnerOptions _options;
    private readonly ILogger<StrategyController> _logger;

    public StrategyController(
        TradingDbContext dbContext,
        StrategyCatalogService catalog,
        StrategyProcessRegistry registry,
        StrategyRunControl runControl,
        PythonEngineLocator engine,
        IPaperTradingService paperTrading,
        ILotSizeResolver lotSizeResolver,
        PositionViewBuilder positionViews,
        UpsertWatchlistItemUseCase upsertWatchlistItem,
        IOptions<StrategyRunnerOptions> options,
        ILogger<StrategyController> logger)
    {
        _dbContext = dbContext;
        _catalog = catalog;
        _registry = registry;
        _runControl = runControl;
        _engine = engine;
        _paperTrading = paperTrading;
        _lotSizeResolver = lotSizeResolver;
        _positionViews = positionViews;
        _upsertWatchlistItem = upsertWatchlistItem;
        _options = options.Value;
        _logger = logger;
    }

    // ------------------------------------------------------------------
    // Catalog
    // ------------------------------------------------------------------

    /// <summary>Every strategy the engine can run, with its current run state. Readable by any signed-in user.</summary>
    [HttpGet]
    public async Task<ActionResult<List<StrategyListItemResponse>>> GetAll(CancellationToken cancellationToken)
    {
        var entries = await _catalog.GetAllAsync(cancellationToken);
        return Ok(entries.Select(ToListItem).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<StrategyListItemResponse>> GetById(int id, CancellationToken cancellationToken)
    {
        var entry = await _catalog.FindAsync(id, cancellationToken);
        if (entry is null) return NotFound(new { message = $"Strategy {id} not found." });
        return Ok(ToListItem(entry));
    }

    /// <summary>
    /// Registers a strategy definition row. Admin-only — the name here is passed to
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

    // ------------------------------------------------------------------
    // Start / stop
    // ------------------------------------------------------------------

    /// <summary>
    /// Launches the Python execution runner on the chosen underlying. Admin-only:
    /// it starts a process on the API host that can place (paper) orders.
    /// </summary>
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    [HttpPost("{id:int}/start")]
    public async Task<IActionResult> StartStrategy(int id, [FromBody] StartStrategyRequest? request, CancellationToken cancellationToken)
    {
        var strategy = await _catalog.FindAsync(id, cancellationToken);
        if (strategy is null) return NotFound(new { message = $"Strategy {id} not found." });

        if (!string.IsNullOrWhiteSpace(strategy.Error))
            return BadRequest(new { message = $"{strategy.Name} cannot be started: {strategy.Error}" });

        if (_registry.Contains(id))
            return Conflict(new { message = $"{strategy.Name} is already running." });

        if (_registry.Count >= _options.MaxConcurrentProcesses)
            return StatusCode(StatusCodes.Status429TooManyRequests,
                new { message = $"Concurrent strategy limit reached ({_options.MaxConcurrentProcesses})." });

        if (request is null || string.IsNullOrWhiteSpace(request.Underlying))
            return BadRequest(new { message = "underlying is required — pick the index or stock the strategy should trade." });

        var underlying = request.Underlying.Trim().ToUpperInvariant();

        int lots = request.Lots ?? Math.Max(1, strategy.DefaultLots);
        if (lots < 1)
            return BadRequest(new { message = "lots must be at least 1." });

        if (request.StopLoss.HasValue && request.StopLoss.Value <= 0)
            return BadRequest(new { message = "stopLoss must be a positive rupee amount, or omitted." });

        if (request.Target.HasValue && request.Target.Value <= 0)
            return BadRequest(new { message = "target must be a positive rupee amount, or omitted." });

        decimal initialCapital = request.InitialCapital ?? DefaultInitialCapital;
        if (initialCapital <= 0)
            return BadRequest(new { message = "initialCapital must be positive." });

        if (!await HasFutureOptionContractsAsync(underlying, cancellationToken))
            return BadRequest(new { message = $"No option contracts loaded for {underlying} — import the F&O master first." });

        if (strategy.SupportedUnderlyings.Count > 0
            && !strategy.SupportedUnderlyings.Contains(underlying, StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogWarning("{Strategy} started on {Underlying}, which is not in its supported list ({Supported}).",
                strategy.Name, underlying, string.Join(", ", strategy.SupportedUnderlyings));
        }

        var spotSymbol = UnderlyingCatalog.SpotSymbolFor(underlying);
        var userId = User.GetRequiredUserId();
        var startedBy = User.GetUserName() ?? "unknown";
        var now = DateTime.UtcNow;

        var run = new SimulationRun
        {
            UserId = userId,
            Mode = LivePaperMode,
            Symbol = spotSymbol,
            Resolution = "1m",
            ReplaySpeed = string.Empty,
            Status = "Running",
            StrategyName = strategy.Name,
            ParametersJson = MergeParameters(strategy.DefaultParametersJson, request.Parameters, lots, request.StopLoss, request.Target, underlying),
            InitialCapital = initialCapital,
            CreatedUtc = now,
            StartedUtc = now
        };

        await _dbContext.SimulationRuns.AddAsync(run, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await EnsureSpotOnWatchlistAsync(spotSymbol, cancellationToken);

        var launch = new LaunchSpec(strategy, run.Id, userId, startedBy, underlying, spotSymbol, lots, request.StopLoss, request.Target);
        var (error, running) = LaunchRunner(launch);
        if (error is not null || running is null)
        {
            run.Status = "Failed";
            run.LastError = "Runner failed to start.";
            run.CompletedUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return error ?? StatusCode(StatusCodes.Status500InternalServerError, new { message = "Failed to start the runner." });
        }

        return Ok(StartResponse($"Started {strategy.Name} on {underlying} (paper).", running));
    }

    /// <summary>
    /// Deploys a strategy against an existing LivePaper run created by the trader
    /// wizard (its parametersJson carries the wizard's configuration). Any signed-in
    /// trader may do this: the runner only posts paper signals into the Simulator.
    /// </summary>
    public record DeployRequest(long RunId);

    [HttpPost("{id:int}/deploy")]
    public async Task<IActionResult> Deploy(int id, [FromBody] DeployRequest request, CancellationToken cancellationToken)
    {
        var run = await _dbContext.SimulationRuns
            .FirstOrDefaultAsync(r => r.Id == request.RunId, cancellationToken);
        if (run is null)
            return NotFound(new { message = $"Simulation run {request.RunId} not found. Create it first via POST /api/Simulator/runs." });

        if (!string.Equals(run.Mode, LivePaperMode, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Only LivePaper runs can be deployed from the console for now — live mode arrives with the execution loop." });

        var strategy = await _catalog.FindAsync(id, cancellationToken);
        if (strategy is null) return NotFound(new { message = $"Strategy {id} not found." });

        if (_registry.Contains(id))
            return Conflict(new { message = $"{strategy.Name} is already running." });

        if (_registry.Count >= _options.MaxConcurrentProcesses)
            return StatusCode(StatusCodes.Status429TooManyRequests,
                new { message = $"Concurrent strategy limit reached ({_options.MaxConcurrentProcesses})." });

        var p = ParseRunParams(run.ParametersJson);
        var underlying = p.Underlying
                         ?? UnderlyingCatalog.UnderlyingForSpot(run.Symbol)
                         ?? UnderlyingCatalog.InferUnderlying(run.Symbol);
        if (string.IsNullOrWhiteSpace(underlying))
            return BadRequest(new { message = $"Run {run.Id} has no usable symbol to derive an underlying from." });

        var spotSymbol = string.IsNullOrWhiteSpace(run.Symbol) ? UnderlyingCatalog.SpotSymbolFor(underlying) : run.Symbol;
        int lots = Math.Max(1, p.Lots ?? strategy.DefaultLots);

        // Same guard as Start: without option contracts the runner would only
        // exit with "no expiries", which reads as a crash on the run card.
        if (!await HasFutureOptionContractsAsync(underlying, cancellationToken))
            return BadRequest(new { message = $"No option contracts loaded for {underlying} — import the F&O master first." });

        var userId = User.GetRequiredUserId();
        var startedBy = User.GetUserName() ?? "unknown";

        await EnsureSpotOnWatchlistAsync(spotSymbol, cancellationToken);

        var launch = new LaunchSpec(strategy, run.Id, userId, startedBy, underlying, spotSymbol, lots, p.StopLoss, p.Target);
        var (error, running) = LaunchRunner(launch);
        if (error is not null || running is null)
        {
            return error ?? StatusCode(StatusCodes.Status500InternalServerError, new { message = "Failed to start the runner." });
        }

        run.Status = "Running";
        run.StartedUtc ??= DateTime.UtcNow;
        run.CompletedUtc = null;
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(StartResponse($"Deployed {strategy.Name} on run {run.Id}.", running));
    }

    /// <summary>
    /// Stops a running strategy: squares off its open positions at the last mark
    /// (unless flatten=false) and kills the runner. Admin, or the user who started it.
    /// </summary>
    [HttpPost("{id:int}/stop")]
    public async Task<IActionResult> StopStrategy(
        int id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] StopStrategyRequest? request,
        CancellationToken cancellationToken)
    {
        var running = _registry.Get(id);
        if (running is null)
            return BadRequest(new { message = $"Strategy {id} is not currently running from the dashboard." });

        // Admins can stop anything; a trader can stop only what they started.
        var userName = User.GetUserName() ?? "unknown";
        if (!User.IsAdmin() && !string.Equals(running.StartedBy, userName, StringComparison.OrdinalIgnoreCase))
        {
            return Forbid();
        }

        bool flatten = request?.Flatten ?? true;
        var result = await _runControl.StopAsync(id, $"Stopped by {userName}", flatten, userName, cancellationToken);
        if (!result.WasRunning)
            return BadRequest(new { message = $"Strategy {id} is not currently running from the dashboard." });

        return Ok(new
        {
            message = flatten
                ? $"Stopped {running.Name}; squared off {result.Flattened} open position(s)."
                : $"Stopped {running.Name}.",
            flattened = result.Flattened
        });
    }

    // ------------------------------------------------------------------
    // Live view, logs, signals
    // ------------------------------------------------------------------

    /// <summary>
    /// Position-based live view of the strategy's current (or most recent) run.
    /// </summary>
    [HttpGet("{id:int}/live")]
    public async Task<ActionResult<StrategyLiveViewResponse>> GetLive(int id, CancellationToken cancellationToken)
    {
        var strategy = await _catalog.FindAsync(id, cancellationToken);
        if (strategy is null) return NotFound(new { message = $"Strategy {id} not found." });

        var running = _registry.Get(id);
        var lastExit = running is null ? _registry.GetLastExit(id) : null;

        var view = new StrategyLiveViewResponse
        {
            StrategyId = id,
            Name = strategy.Name,
            IsActive = running is not null
        };

        SimulationRun? run;
        long? knownRunId = running?.RunId ?? lastExit?.RunId;
        if (knownRunId.HasValue)
        {
            run = await _dbContext.SimulationRuns.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == knownRunId.Value, cancellationToken);
        }
        else
        {
            // Survives an API restart: the latest LivePaper run of this strategy.
            run = await _dbContext.SimulationRuns.AsNoTracking()
                .Where(x => x.StrategyName == strategy.Name && x.Mode == LivePaperMode)
                .OrderByDescending(x => x.CreatedUtc)
                .ThenByDescending(x => x.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (run is null)
        {
            return Ok(view);
        }

        var p = ParseRunParams(run.ParametersJson);

        view.RunId = run.Id;
        view.Underlying = running?.Underlying
                          ?? lastExit?.Underlying
                          ?? p.Underlying
                          ?? UnderlyingCatalog.UnderlyingForSpot(run.Symbol)
                          ?? UnderlyingCatalog.InferUnderlying(run.Symbol);
        view.SpotSymbol = running?.SpotSymbol ?? lastExit?.SpotSymbol ?? run.Symbol;
        view.Lots = running?.Lots ?? lastExit?.Lots ?? p.Lots;

        if (running is not null)
        {
            view.StopLoss = running.StopLoss;
            view.Target = running.Target;
            view.StartedBy = running.StartedBy;
            view.StartedUtc = running.StartedUtc;
            view.Runner = new StrategyRunnerInfo { ProcessId = running.ProcessId, LastLogUtc = running.LastLogUtc };
        }
        else if (lastExit is not null)
        {
            view.StopLoss = lastExit.StopLoss;
            view.Target = lastExit.Target;
            view.StartedBy = lastExit.StartedBy;
            view.StartedUtc = lastExit.StartedUtc;
            view.StoppedUtc = lastExit.AtUtc;
            view.StopReason = lastExit.Reason;
        }
        else
        {
            view.StopLoss = p.StopLoss;
            view.Target = p.Target;
            view.StartedBy = await _dbContext.AppUsers.AsNoTracking()
                .Where(x => x.Id == run.UserId)
                .Select(x => x.UserName)
                .FirstOrDefaultAsync(cancellationToken);
            view.StartedUtc = run.StartedUtc ?? run.CreatedUtc;
            view.StoppedUtc = run.CompletedUtc;
        }

        var underlyingLot = await _lotSizeResolver.ResolveForUnderlyingAsync(view.Underlying ?? string.Empty, cancellationToken);
        view.LotSize = underlyingLot.LotSize;
        view.LotSizeSource = underlyingLot.Source;

        // Positions (marks open ones to market against the latest live quote),
        // decorated by the same builder the backtest results page uses.
        var positions = await _paperTrading.GetPaperPositionsAsync(run.Id, cancellationToken);
        var built = await _positionViews.BuildAsync<LivePositionResponse>(positions, useLiveQuotes: true, view.SpotSymbol, cancellationToken);

        view.SpotLtp = built.SpotLtp;
        view.SpotUpdatedUtc = built.SpotUpdatedUtc;
        view.Positions = built.Positions;

        view.Pnl.Realized = positions.Sum(x => x.RealizedPnl);
        view.Pnl.Unrealized = positions
            .Where(x => string.Equals(x.Status, "Open", StringComparison.OrdinalIgnoreCase))
            .Sum(x => x.UnrealizedPnl);
        view.Pnl.Total = view.Pnl.Realized + view.Pnl.Unrealized;

        // Activity: the run's signals, newest first (RUN_STOPPED rows included).
        var signals = await _dbContext.SimulationSignals.AsNoTracking()
            .Where(x => x.SimulationRunId == run.Id)
            .OrderByDescending(x => x.TimestampUtc)
            .ThenByDescending(x => x.Id)
            .Take(ActivityLimit)
            .Select(x => new { x.TimestampUtc, x.SignalType, x.GroupId, x.MetadataJson })
            .ToListAsync(cancellationToken);

        foreach (var s in signals)
        {
            var reason = ReadMetadataReason(s.MetadataJson);
            view.Activity.Add(new LiveActivityResponse
            {
                AtUtc = s.TimestampUtc,
                Type = s.SignalType,
                Text = string.IsNullOrWhiteSpace(reason) ? s.SignalType : reason,
                GroupId = s.GroupId
            });
        }

        if (view.StopReason is null && running is null)
        {
            var stopped = signals.FirstOrDefault(x => x.SignalType == StrategyRunControl.RunStoppedSignalType);
            if (stopped is not null)
            {
                view.StopReason = ReadMetadataReason(stopped.MetadataJson) ?? StrategyRunControl.RunStoppedSignalType;
                view.StoppedUtc ??= stopped.TimestampUtc;
            }
        }

        return Ok(view);
    }

    /// <summary>Recent runner stdout/stderr (empty when not running).</summary>
    [HttpGet("{id:int}/logs")]
    public IActionResult GetLogs(int id, [FromQuery] int take = 200)
    {
        return Ok(_registry.GetLogs(id, take));
    }

    /// <summary>The runner pushes a copy of each signal here for the dashboard.</summary>
    [HttpPost("{id:int}/signals")]
    public IActionResult AddSignal(int id, [FromBody] object signal)
    {
        if (_registry.AddSignal(id, signal))
        {
            return Ok();
        }
        return NotFound(new { message = $"Strategy {id} is not currently active." });
    }

    [HttpGet("{id:int}/signals")]
    public IActionResult GetSignals(int id)
    {
        return Ok(_registry.GetSignals(id));
    }

    // ------------------------------------------------------------------
    // Launch plumbing
    // ------------------------------------------------------------------

    private sealed record LaunchSpec(
        StrategyCatalogEntry Strategy,
        long RunId,
        long UserId,
        string StartedBy,
        string Underlying,
        string SpotSymbol,
        int Lots,
        decimal? StopLoss,
        decimal? Target);

    /// <summary>
    /// Spawns execution_runner.py and registers it. Returns an error result
    /// instead of throwing so callers can roll back their run row.
    /// </summary>
    private (IActionResult? Error, RunningStrategy? Running) LaunchRunner(LaunchSpec spec)
    {
        int id = spec.Strategy.Id;
        var engineDirectory = _engine.EngineDirectory;
        var scriptPath = _engine.ScriptPath("strategies", "execution_runner.py");

        if (!System.IO.File.Exists(scriptPath))
        {
            _logger.LogError("Strategy runner not found at {ScriptPath}", scriptPath);
            return (StatusCode(StatusCodes.Status500InternalServerError,
                new { message = $"Strategy runner not found at '{scriptPath}'. Set StrategyRunner:EngineDirectory." }), null);
        }

        // The strategy name reaches a command line, so allow only characters that
        // appear in a legitimate strategy identifier.
        var strategyName = new string(spec.Strategy.Name.Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrEmpty(strategyName))
        {
            return (BadRequest(new { message = $"Strategy name '{spec.Strategy.Name}' contains no usable characters." }), null);
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
        processInfo.ArgumentList.Add("--strategy");
        processInfo.ArgumentList.Add(strategyName);
        processInfo.ArgumentList.Add("--strategy-id");
        processInfo.ArgumentList.Add(id.ToString(CultureInfo.InvariantCulture));
        processInfo.ArgumentList.Add("--user-id");
        processInfo.ArgumentList.Add(spec.UserId.ToString(CultureInfo.InvariantCulture));
        processInfo.ArgumentList.Add("--run-id");
        processInfo.ArgumentList.Add(spec.RunId.ToString(CultureInfo.InvariantCulture));
        processInfo.ArgumentList.Add("--underlying");
        processInfo.ArgumentList.Add(spec.Underlying);
        processInfo.ArgumentList.Add("--spot-symbol");
        processInfo.ArgumentList.Add(spec.SpotSymbol);

        // The engine uses absolute package imports and resolves .env relative to
        // its own location, so PYTHONPATH must point at the engine directory.
        processInfo.Environment["PYTHONPATH"] = engineDirectory;
        // Line-buffered output so log lines arrive as they happen, not in 8KB blocks.
        processInfo.Environment["PYTHONUNBUFFERED"] = "1";

        lock (_registry.StartLock)
        {
            if (_registry.Contains(id))
            {
                return (Conflict(new { message = $"{spec.Strategy.Name} is already running." }), null);
            }

            if (_registry.Count >= _options.MaxConcurrentProcesses)
            {
                return (StatusCode(StatusCodes.Status429TooManyRequests,
                    new { message = $"Concurrent strategy limit reached ({_options.MaxConcurrentProcesses})." }), null);
            }

            Process? process = null;
            try
            {
                process = new Process { StartInfo = processInfo, EnableRaisingEvents = true };
                if (!process.Start())
                {
                    process.Dispose();
                    return (StatusCode(StatusCodes.Status500InternalServerError, new { message = "Failed to start python process." }), null);
                }

                var running = new RunningStrategy(
                    id, spec.Strategy.Name, process, spec.StartedBy, spec.UserId, DateTime.UtcNow,
                    spec.RunId, spec.Underlying, spec.SpotSymbol, spec.Lots, spec.StopLoss, spec.Target);

                if (!_registry.TryAdd(running))
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                    process.Dispose();
                    return (Conflict(new { message = $"{spec.Strategy.Name} is already running." }), null);
                }

                _registry.ClearLastExit(id);

                _logger.LogInformation(
                    "Started strategy {StrategyId} ({Name}) pid {Pid} run {RunId} on {Underlying} ({Spot}) x{Lots} SL={StopLoss} T={Target} by {User}",
                    id, spec.Strategy.Name, running.ProcessId, spec.RunId, spec.Underlying, spec.SpotSymbol, spec.Lots,
                    spec.StopLoss, spec.Target, spec.StartedBy);

                return (null, running);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start strategy {StrategyId}.", id);
                try { process?.Dispose(); } catch { /* ignore */ }
                return (StatusCode(StatusCodes.Status500InternalServerError, new { message = ex.Message }), null);
            }
        }
    }

    /// <summary>True when the underlying has at least one unexpired CE/PE contract in the master.</summary>
    private Task<bool> HasFutureOptionContractsAsync(string underlying, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return _dbContext.Instruments
            .AsNoTracking()
            .AnyAsync(x => x.IsEnabled
                        && x.Underlying == underlying
                        && (x.OptionType == "CE" || x.OptionType == "PE")
                        && x.ExpiryDate.HasValue
                        && x.ExpiryDate >= today,
                cancellationToken);
    }

    private static object StartResponse(string message, RunningStrategy running) => new
    {
        message,
        processId = running.ProcessId,
        runId = running.RunId,
        underlying = running.Underlying,
        spotSymbol = running.SpotSymbol,
        lots = running.Lots,
        stopLoss = running.StopLoss,
        target = running.Target,
        startedBy = running.StartedBy
    };

    /// <summary>
    /// The runner reads spot ticks for the underlying from the live feed, so the
    /// spot symbol must be on the watchlist. Failure here is not fatal — the
    /// runner warns on its own when ticks never arrive.
    /// </summary>
    private async Task EnsureSpotOnWatchlistAsync(string spotSymbol, CancellationToken cancellationToken)
    {
        try
        {
            await _upsertWatchlistItem.ExecuteAsync(new UpsertWatchlistItemRequest
            {
                Symbol = spotSymbol,
                DataType = "symbolUpdate",
                IsActive = true,
                Priority = 100
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not add {Symbol} to the live watchlist.", spotSymbol);
        }
    }

    // ------------------------------------------------------------------
    // Mapping helpers
    // ------------------------------------------------------------------

    private StrategyListItemResponse ToListItem(StrategyCatalogEntry entry)
    {
        var running = _registry.Get(entry.Id);
        var lastExit = running is null ? _registry.GetLastExit(entry.Id) : null;

        return new StrategyListItemResponse
        {
            Id = entry.Id,
            Name = entry.Name,
            Description = entry.Description,
            Category = entry.Category,
            SupportedUnderlyings = entry.SupportedUnderlyings.ToList(),
            InstrumentKind = entry.InstrumentKind,
            LegsSummary = entry.LegsSummary,
            DataRequirements = entry.DataRequirements.ToList(),
            DefaultParametersJson = entry.DefaultParametersJson,
            DefaultLots = entry.DefaultLots,
            SourceFile = entry.SourceFile,
            CreatedUtc = entry.CreatedUtc,

            IsActive = running is not null,
            StartedBy = running?.StartedBy,
            StartedUtc = running?.StartedUtc,
            RunId = running?.RunId,
            Underlying = running?.Underlying,
            SpotSymbol = running?.SpotSymbol,
            Lots = running?.Lots,
            StopLoss = running?.StopLoss,
            Target = running?.Target,
            ProcessId = running?.ProcessId,
            LastExit = lastExit is null ? null : new StrategyLastExit
            {
                RunId = lastExit.RunId,
                Reason = lastExit.Reason,
                AtUtc = lastExit.AtUtc,
                Underlying = lastExit.Underlying
            }
        };
    }

    private sealed record RunParams(int? Lots, decimal? StopLoss, decimal? Target, string? Underlying);

    /// <summary>
    /// Reads lots / stop_loss / target / underlying back out of a run's
    /// parametersJson (numbers may arrive as strings from the wizard).
    /// </summary>
    private static RunParams ParseRunParams(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new RunParams(null, null, null, null);

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return new RunParams(null, null, null, null);
            var root = doc.RootElement;

            int? lots = ReadInt(root, "lots") ?? ReadInt(root, "quantity");
            decimal? sl = ReadDecimal(root, "stop_loss") ?? ReadDecimal(root, "stopLoss");
            decimal? target = ReadDecimal(root, "target");
            string? underlying = null;
            if (root.TryGetProperty("underlying", out var u) && u.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(u.GetString()))
            {
                underlying = u.GetString()!.Trim().ToUpperInvariant();
            }

            return new RunParams(
                lots is > 0 ? lots : null,
                sl is > 0 ? sl : null,
                target is > 0 ? target : null,
                underlying);
        }
        catch (JsonException)
        {
            return new RunParams(null, null, null, null);
        }
    }

    private static int? ReadInt(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetInt32(out var n) => n,
            JsonValueKind.Number when el.TryGetDecimal(out var d) => (int)d,
            JsonValueKind.String when int.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s) => s,
            _ => null
        };
    }

    private static decimal? ReadDecimal(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetDecimal(out var d) => d,
            JsonValueKind.String when decimal.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s) => s,
            _ => null
        };
    }

    /// <summary>
    /// defaults ⊕ overrides ⊕ { lots, stop_loss, target, underlying } as one JSON object.
    /// </summary>
    private static string MergeParameters(
        string? defaultsJson,
        Dictionary<string, JsonElement>? overrides,
        int lots,
        decimal? stopLoss,
        decimal? target,
        string underlying)
    {
        JsonObject merged;
        try
        {
            merged = string.IsNullOrWhiteSpace(defaultsJson)
                ? new JsonObject()
                : JsonNode.Parse(defaultsJson) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            merged = new JsonObject();
        }

        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
            {
                if (string.IsNullOrWhiteSpace(key)) continue;
                merged[key] = value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                    ? null
                    : JsonNode.Parse(value.GetRawText());
            }
        }

        merged["lots"] = lots;
        merged["stop_loss"] = stopLoss.HasValue ? JsonValue.Create(stopLoss.Value) : null;
        merged["target"] = target.HasValue ? JsonValue.Create(target.Value) : null;
        merged["underlying"] = underlying;

        return merged.ToJsonString();
    }

    /// <summary>metadataJson.reason (any casing), or null.</summary>
    private static string? ReadMetadataReason(string? metadataJson)
        => SignalMetadata.ReadReason(metadataJson);
}
