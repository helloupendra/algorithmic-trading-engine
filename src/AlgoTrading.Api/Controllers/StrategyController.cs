using AlgoTrading.Domain.Constants;
// src/AlgoTrading.Api/Controllers/StrategyController.cs
using AlgoTrading.Api.Configuration;
using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.UseCases.LiveData;
using AlgoTrading.Application.UseCases.Simulator;
using AlgoTrading.Contracts.LiveData;
using AlgoTrading.Contracts.Simulator;
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

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// The strategy catalog and the live paper runner: list strategies (with
/// descriptions from the Python engine), start one on a chosen underlying with
/// optional stop-loss / target, stop it (squaring off), and read its
/// position-based live view, activity and runner output.
/// </summary>
// A trader reaches every live-run endpoint here; the grant is checked on the
// endpoint, not merely hidden in the console's navigation.
[RequireModule(PlatformModules.Strategies)]
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
    private readonly ISystemNotifier _notifier;
    private readonly IStrategyAccessService _strategyAccess;
    private readonly StrategyRunControl _runControl;
    private readonly PythonEngineLocator _engine;
    private readonly IPaperTradingService _paperTrading;
    private readonly ILotSizeResolver _lotSizeResolver;
    private readonly PositionViewBuilder _positionViews;
    private readonly LiveRunHistoryBuilder _history;
    private readonly GetPaperOrdersUseCase _getPaperOrders;
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
        LiveRunHistoryBuilder history,
        GetPaperOrdersUseCase getPaperOrders,
        UpsertWatchlistItemUseCase upsertWatchlistItem,
        ISystemNotifier notifier,
        IStrategyAccessService strategyAccess,
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
        _history = history;
        _getPaperOrders = getPaperOrders;
        _upsertWatchlistItem = upsertWatchlistItem;
        _notifier = notifier;
        _strategyAccess = strategyAccess;
        _options = options.Value;
        _logger = logger;
    }

    // ------------------------------------------------------------------
    // Catalog
    // ------------------------------------------------------------------

    /// <summary>
    /// The strategies the caller may run. An admin sees the whole catalog; a
    /// trader sees exactly what their package and overrides allow.
    /// </summary>
    /// <remarks>
    /// Filtering here is a courtesy, so a trader is not shown buttons that would
    /// be refused. The check that actually stops a deploy lives on the deploy
    /// endpoint.
    /// </remarks>
    [HttpGet]
    public async Task<ActionResult<List<StrategyListItemResponse>>> GetAll(CancellationToken cancellationToken)
    {
        var entries = await _catalog.GetAllAsync(cancellationToken);

        var access = await _strategyAccess.GetAccessAsync(User.GetRequiredUserId(), cancellationToken);

        if (!access.IsUnrestricted)
        {
            entries = entries.Where(x => access.AllowsStrategy(x.Name)).ToList();
        }

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
    public async Task<IActionResult> StartStrategy(
        int id, 
        [FromBody] StartStrategyRequest? request, 
        [FromServices] AlgoTrading.Application.Interfaces.IRiskLimitsStore limitsStore,
        CancellationToken cancellationToken)
    {
        var strategy = await _catalog.FindAsync(id, cancellationToken);
        if (strategy is null) return NotFound(new { message = $"Strategy {id} not found." });

        if (!string.IsNullOrWhiteSpace(strategy.Error))
            return BadRequest(new { message = $"{strategy.Name} cannot be started: {strategy.Error}" });

        if (request is null || string.IsNullOrWhiteSpace(request.Underlying))
            return BadRequest(new { message = "underlying is required — pick the index or stock the strategy should trade." });

        var underlying = request.Underlying.Trim().ToUpperInvariant();

        // The same strategy may run on several underlyings at once; only the
        // same strategy on the SAME underlying is refused.
        if (_registry.Find(id, underlying) is not null)
            return Conflict(new { message = AlreadyRunningMessage(strategy.Name, underlying) });

        var riskLimits = limitsStore.GetLimits();
        if (_registry.Count >= riskLimits.MaxConcurrentRuns)
            return StatusCode(StatusCodes.Status429TooManyRequests,
                new { message = $"Concurrent strategy limit reached ({riskLimits.MaxConcurrentRuns})." });

        int lots = request.Lots ?? Math.Max(1, strategy.DefaultLots);
        if (lots < 1)
            return BadRequest(new { message = "lots must be at least 1." });

        if (request.StopLoss.HasValue && request.StopLoss.Value <= 0)
            return BadRequest(new { message = "stopLoss must be a positive rupee amount, or omitted." });

        if (request.Target.HasValue && request.Target.Value <= 0)
            return BadRequest(new { message = "target must be a positive rupee amount, or omitted." });

        if (!RiskRulesDto.TryValidate(request.Risk, out var riskError))
            return BadRequest(new { message = $"risk: {riskError}" });

        var risk = RunRiskRules.Resolve(request.Risk, request.StopLoss, request.Target);

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
            ParametersJson = LiveRunParameters.Merge(strategy.DefaultParametersJson, request.Parameters, lots, risk, underlying),
            InitialCapital = initialCapital,
            CreatedUtc = now,
            StartedUtc = now
        };

        await _dbContext.SimulationRuns.AddAsync(run, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await EnsureSpotOnWatchlistAsync(spotSymbol, cancellationToken);

        var launch = new LaunchSpec(strategy, run.Id, userId, startedBy, underlying, spotSymbol, lots, risk);
        var (error, running) = LaunchRunner(launch);
        if (error is not null || running is null)
        {
            run.Status = "Failed";
            run.LastError = "Runner failed to start.";
            run.CompletedUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return error ?? StatusCode(StatusCodes.Status500InternalServerError, new { message = "Failed to start the runner." });
        }

        // Durable pid so a restarted API can adopt (or stop) this runner.
        await _runControl.RecordRunnerPidAsync(running.RunId, running.ProcessId, startedBy);

        return Ok(StartResponse($"Started {strategy.Name} on {underlying} (paper).", running));
    }

    /// <summary>
    /// Deploys a strategy against an existing LivePaper run created by the trader
    /// wizard (its parametersJson carries the wizard's configuration). Any signed-in
    /// trader may do this: the runner only posts paper signals into the Simulator.
    /// </summary>
    public record DeployRequest(long RunId);

    [HttpPost("{id:int}/deploy")]
    public async Task<IActionResult> Deploy(
        int id, 
        [FromBody] DeployRequest request, 
        [FromServices] AlgoTrading.Application.Interfaces.IRiskLimitsStore limitsStore,
        CancellationToken cancellationToken)
    {
        var run = await _dbContext.SimulationRuns
            .FirstOrDefaultAsync(r => r.Id == request.RunId, cancellationToken);
        if (run is null)
            return NotFound(new { message = $"Simulation run {request.RunId} not found. Create it first via POST /api/Simulator/runs." });

        if (!string.Equals(run.Mode, LivePaperMode, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Only LivePaper runs can be deployed from the console for now — live mode arrives with the execution loop." });

        var strategy = await _catalog.FindAsync(id, cancellationToken);
        if (strategy is null) return NotFound(new { message = $"Strategy {id} not found." });

        if (_registry.Contains(run.Id))
            return Conflict(new { message = $"Run {run.Id} already has a runner behind it." });

        var p = LiveRunParameters.Parse(run.ParametersJson);
        var underlying = p.Underlying
                         ?? UnderlyingCatalog.UnderlyingForSpot(run.Symbol)
                         ?? UnderlyingCatalog.InferUnderlying(run.Symbol);
        if (string.IsNullOrWhiteSpace(underlying))
            return BadRequest(new { message = $"Run {run.Id} has no usable symbol to derive an underlying from." });

        underlying = underlying.Trim().ToUpperInvariant();

        if (_registry.Find(id, underlying) is not null)
            return Conflict(new { message = AlreadyRunningMessage(strategy.Name, underlying) });

        var riskLimits = limitsStore.GetLimits();
        if (_registry.Count >= riskLimits.MaxConcurrentRuns)
            return StatusCode(StatusCodes.Status429TooManyRequests,
                new { message = $"Concurrent strategy limit reached ({riskLimits.MaxConcurrentRuns})." });

        var spotSymbol = string.IsNullOrWhiteSpace(run.Symbol) ? UnderlyingCatalog.SpotSymbolFor(underlying) : run.Symbol;
        int lots = Math.Max(1, p.Lots ?? strategy.DefaultLots);

        // Same guard as Start: without option contracts the runner would only
        // exit with "no expiries", which reads as a crash on the run card.
        if (!await HasFutureOptionContractsAsync(underlying, cancellationToken))
            return BadRequest(new { message = $"No option contracts loaded for {underlying} — import the F&O master first." });

        var userId = User.GetRequiredUserId();
        var startedBy = User.GetUserName() ?? "unknown";

        // What this trader may run. Filtering the strategy list is a courtesy;
        // this check is what actually stops anything, so it happens on the last
        // step before a runner is launched.
        int openRuns = await _dbContext.SimulationRuns
            .CountAsync(
                x => x.UserId == userId && (x.Status == "Running" || x.Status == "Stopping"),
                cancellationToken);

        var decision = await _strategyAccess.CanDeployAsync(
            userId,
            strategy.Name,
            underlying,
            lots,
            run.Mode,
            openRuns,
            cancellationToken);

        if (!decision.Allowed)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = decision.Reason });
        }

        await EnsureSpotOnWatchlistAsync(spotSymbol, cancellationToken);

        var launch = new LaunchSpec(strategy, run.Id, userId, startedBy, underlying, spotSymbol, lots, p.Risk);
        var (error, running) = LaunchRunner(launch);
        if (error is not null || running is null)
        {
            // Same closing as Start: a run whose runner never came up must say so.
            // Left Pending it becomes a row nobody can explain later, and the
            // trader has no reason on screen for why nothing happened.
            run.Status = "Failed";
            run.LastError = "Runner failed to start.";
            run.CompletedUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);

            HttpContext.Describe(
                $"Deploy of {strategy.Name} on {underlying} failed — run #{run.Id} could not start its runner.",
                "run",
                run.Id.ToString());

            return error ?? StatusCode(StatusCodes.Status500InternalServerError, new { message = "Failed to start the runner." });
        }

        run.Status = "Running";
        run.StartedUtc ??= DateTime.UtcNow;
        run.CompletedUtc = null;
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _runControl.RecordRunnerPidAsync(running.RunId, running.ProcessId, startedBy);

        HttpContext.Describe(
            $"Deployed {strategy.Name} on {underlying} — run #{run.Id}, {lots} lot(s).",
            "run",
            run.Id.ToString());

        await _notifier.NotifyAsync(
            NotificationCategory.StrategyRun,
            NotificationSeverity.Success,
            $"{strategy.Name} started on {underlying}",
            $"Run #{run.Id} · {lots} lot(s) · started by {startedBy}.",
            underlying: underlying,
            symbol: spotSymbol,
            simulationRunId: run.Id,
            cancellationToken: cancellationToken);

        return Ok(StartResponse($"Deployed {strategy.Name} on run {run.Id}.", running));
    }

    /// <summary>
    /// Stops one run of a strategy by run id: squares off its open positions at
    /// the last mark (unless flatten=false) and kills the runner. Admin, or the
    /// user who started it. A LivePaper run whose row is still Running/Stopping
    /// with no runner behind it (API restart) is closed as Stopped without a
    /// process to kill, so it never stays stuck.
    /// </summary>
    [HttpPost("runs/{runId:long}/stop")]
    public async Task<IActionResult> StopRun(
        long runId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] StopStrategyRequest? request,
        CancellationToken cancellationToken)
    {
        var running = _registry.Get(runId);
        if (running is null)
        {
            return await StopOrphanRunAsync(runId, request, cancellationToken);
        }

        return await StopRunningAsync(running, request, cancellationToken);
    }

    /// <summary>
    /// Legacy strategy-scoped stop: resolves to the single active run of the
    /// strategy. With several instances running the caller must name the run
    /// (POST /api/Strategy/runs/{runId}/stop).
    /// </summary>
    [HttpPost("{id:int}/stop")]
    public async Task<IActionResult> StopStrategy(
        int id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] StopStrategyRequest? request,
        CancellationToken cancellationToken)
    {
        var runs = _registry.GetByStrategy(id);
        if (runs.Count == 0)
            return BadRequest(new { message = $"Strategy {id} is not currently running from the dashboard." });

        if (runs.Count > 1)
        {
            return BadRequest(new
            {
                message = $"{runs[0].Name} has {runs.Count} running instances ({string.Join(", ", runs.Select(x => x.Underlying))}) — use /api/Strategy/runs/{{runId}}/stop.",
                runIds = runs.Select(x => x.RunId).ToList()
            });
        }

        return await StopRunningAsync(runs[0], request, cancellationToken);
    }

    private async Task<IActionResult> StopRunningAsync(RunningStrategy running, StopStrategyRequest? request, CancellationToken cancellationToken)
    {
        // Admins can stop anything; a trader can stop only what they started.
        var userName = User.GetUserName() ?? "unknown";
        if (!CanStop(running.StartedBy, running.UserId))
        {
            return Forbid();
        }

        bool flatten = request?.Flatten ?? true;
        var result = await _runControl.StopAsync(running.RunId, $"Stopped by {userName}", flatten, userName, cancellationToken);
        if (!result.WasRunning)
            return BadRequest(new { message = $"{running.Name} on {running.Underlying} (run {running.RunId}) is not currently running from the dashboard." });

        HttpContext.Describe(
            $"Stopped {running.Name} on {running.Underlying} — run #{running.RunId}, "
                + (flatten ? $"squared off {result.Flattened} position(s)." : "positions left open."),
            "run",
            running.RunId.ToString());

        await _notifier.NotifyAsync(
            NotificationCategory.StrategyRun,
            NotificationSeverity.Warning,
            $"{running.Name} stopped on {running.Underlying}",
            flatten
                ? $"Run #{running.RunId} stopped by {userName}; squared off {result.Flattened} open position(s)."
                : $"Run #{running.RunId} stopped by {userName}; positions left open.",
            underlying: running.Underlying,
            simulationRunId: running.RunId,
            cancellationToken: cancellationToken);

        return Ok(new
        {
            message = flatten
                ? $"Stopped {running.Name} on {running.Underlying}; squared off {result.Flattened} open position(s)."
                : $"Stopped {running.Name} on {running.Underlying}.",
            flattened = result.Flattened,
            runId = running.RunId,
            underlying = running.Underlying
        });
    }

    /// <summary>
    /// The run is not in the registry: close its row if it is still open (the
    /// API restarted under a live runner), otherwise report that nothing is running.
    /// </summary>
    private async Task<IActionResult> StopOrphanRunAsync(long runId, StopStrategyRequest? request, CancellationToken cancellationToken)
    {
        var run = await _dbContext.SimulationRuns.AsNoTracking()
            .Where(x => x.Id == runId && x.Mode == LivePaperMode)
            .Select(x => new { x.Id, x.Status, x.StrategyName, x.UserId })
            .FirstOrDefaultAsync(cancellationToken);

        if (run is null)
            return NotFound(new { message = $"Strategy run {runId} not found." });

        if (!StrategyRunControl.IsOpenStatus(run.Status))
            return BadRequest(new { message = $"{run.StrategyName} run {runId} is not currently running (status {run.Status})." });

        var startedBy = await _dbContext.AppUsers.AsNoTracking()
            .Where(x => x.Id == run.UserId)
            .Select(x => x.UserName)
            .FirstOrDefaultAsync(cancellationToken);

        if (!CanStop(startedBy, run.UserId))
        {
            return Forbid();
        }

        var userName = User.GetUserName() ?? "unknown";
        bool flatten = request?.Flatten ?? true;
        var result = await _runControl.StopOrphanAsync(runId, $"Stopped by {userName}", flatten, userName);
        if (!result.WasRunning)
            return BadRequest(new { message = $"{run.StrategyName} run {runId} is not currently running." });

        return Ok(new
        {
            message = flatten
                ? $"Closed {run.StrategyName} run {runId} (no runner process was found); squared off {result.Flattened} open position(s)."
                : $"Closed {run.StrategyName} run {runId} (no runner process was found).",
            flattened = result.Flattened,
            runId
        });
    }

    /// <summary>Admins can stop anything; a trader only what they started (by name or by user id).</summary>
    private bool CanStop(string? startedBy, long ownerUserId)
    {
        if (User.IsAdmin()) return true;

        var userName = User.GetUserName();
        if (!string.IsNullOrWhiteSpace(userName) && string.Equals(startedBy, userName, StringComparison.OrdinalIgnoreCase))
            return true;

        return User.GetUserId() == ownerUserId;
    }

    // ------------------------------------------------------------------
    // Risk rules (editable while running) and runner registration
    // ------------------------------------------------------------------

    /// <summary>
    /// Replaces the risk rules of a running run (all three levels). Admin, or
    /// the user who started it. Takes effect on the guard's next sweep; the
    /// run row's parametersJson is rewritten and a RISK_UPDATED signal records
    /// who changed what. 404 when the run is not running.
    /// </summary>
    [HttpPatch("runs/{runId:long}/risk")]
    public async Task<ActionResult<UpdateRunRiskResponse>> UpdateRunRisk(
        long runId,
        [FromBody] UpdateRunRiskRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
            return BadRequest(new { message = "A risk rules body is required ({ overall, group, leg })." });

        if (!RiskRulesDto.TryValidate(request, out var riskError))
            return BadRequest(new { message = riskError });

        var running = _registry.Get(runId);
        if (running is null)
            return NotFound(new { message = $"Strategy run {runId} is not currently running." });

        if (!CanStop(running.StartedBy, running.UserId))
            return Forbid();

        var rules = RunRiskRules.Sanitize(request);
        var userName = User.GetUserName() ?? "unknown";

        // Persist FIRST, then switch the guard over. The registry entry is
        // what the guard enforces and what an adopted run is rebuilt from
        // (ParametersJson); if the save failed after the registry had already
        // been updated, the guard would enforce rules the row does not have
        // and the client would be told the update failed.
        var run = await _dbContext.SimulationRuns.FirstOrDefaultAsync(x => x.Id == runId, cancellationToken);
        if (run is not null)
        {
            run.ParametersJson = RunRiskRules.Rewrite(run.ParametersJson, rules);
        }

        var now = DateTime.UtcNow;
        await _dbContext.SimulationSignals.AddAsync(new SimulationSignal
        {
            SimulationRunId = runId,
            StrategyName = running.Name,
            SignalType = RunRiskRules.RiskUpdatedSignalType,
            TimestampUtc = now,
            GroupId = string.Empty,
            MetadataJson = RunRiskRules.UpdatedMetadata(rules, userName),
            CreatedUtc = now
        }, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var updated = _registry.UpdateRisk(runId, rules);
        if (updated is null)
        {
            // The run ended between the checks above and now; the rules are on
            // the (closed) row, which is harmless, but there is nothing to guard.
            _logger.LogInformation("Risk rules of run {RunId} were persisted but the run is no longer running.", runId);
            return NotFound(new { message = $"Strategy run {runId} is not currently running." });
        }

        _registry.AppendLog(runId, $"risk rules updated by {userName}: {rules.Describe()}");
        _logger.LogInformation("Risk rules of strategy {StrategyId} ({Name}) run {RunId} on {Underlying} updated by {User}: {Rules}",
            running.StrategyId, running.Name, runId, running.Underlying, userName, rules.Describe());

        return Ok(new UpdateRunRiskResponse { RunId = runId, Risk = updated.Risk });
    }

    /// <summary>
    /// The execution runner confirms its own pid once it knows its run id. Any
    /// signed-in user (the runner authenticates as the service account). The
    /// API already recorded the pid at spawn time; a mismatch keeps the pid the
    /// API launched. 404 when the run does not exist or is already closed.
    /// </summary>
    [HttpPost("runs/{runId:long}/runner")]
    public async Task<ActionResult<RunnerRegistrationResponse>> RegisterRunner(
        long runId,
        [FromBody] RunnerRegistrationRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null || request.ProcessId <= 0)
            return BadRequest(new { message = "processId (positive) is required." });

        var run = await _dbContext.SimulationRuns.AsNoTracking()
            .Where(x => x.Id == runId && x.Mode == LivePaperMode)
            .Select(x => new { x.Id, x.Status })
            .FirstOrDefaultAsync(cancellationToken);

        if (run is null)
            return NotFound(new { message = $"Strategy run {runId} not found." });

        if (!StrategyRunControl.IsOpenStatus(run.Status))
            return NotFound(new { message = $"Strategy run {runId} is not running (status {run.Status})." });

        var running = _registry.Get(runId);
        int pidOnRecord = request.ProcessId;
        if (running is not null && running.ProcessId > 0 && running.ProcessId != request.ProcessId)
        {
            _logger.LogWarning("Strategy run {RunId}: runner reported pid {Reported} but the registry launched pid {Launched}; keeping the launched pid.",
                runId, request.ProcessId, running.ProcessId);
            pidOnRecord = running.ProcessId;
        }

        await _runControl.RecordRunnerPidAsync(runId, pidOnRecord, "runner");
        if (running is not null)
        {
            _registry.AppendLog(runId, $"runner confirmed pid {request.ProcessId}" + (request.StartedUtc.HasValue ? $" (started {request.StartedUtc:HH:mm:ss}Z)" : string.Empty));
        }

        return Ok(new RunnerRegistrationResponse { RunId = runId, ProcessId = pidOnRecord, Managed = running is not null });
    }

    // ------------------------------------------------------------------
    // Run history (per user)
    // ------------------------------------------------------------------

    /// <summary>
    /// Every live run, newest first, attached to the user who started it —
    /// stopped by stop-loss, target, market close, a manual stop, a runner exit
    /// or an API restart, they all stay here. A trader always gets their own
    /// runs (the userId filter is ignored); an admin gets everyone's, optionally
    /// one user's. fromDate / toDate are IST calendar days (yyyy-MM-dd) on the
    /// start time; status is Running | Stopped | Failed | Completed | any.
    /// </summary>
    [HttpGet("runs")]
    public async Task<ActionResult<List<LiveRunSummaryResponse>>> ListRuns(
        [FromQuery] long? userId,
        [FromQuery] int? strategyId,
        [FromQuery] string? underlying,
        [FromQuery] string? status,
        [FromQuery] string? fromDate,
        [FromQuery] string? toDate,
        [FromQuery] int take = LiveRunHistoryFilter.DefaultTake,
        [FromQuery] int skip = 0,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseIstDate(fromDate, out var from))
            return BadRequest(new { message = "fromDate must be an IST calendar day in yyyy-MM-dd form." });

        if (!TryParseIstDate(toDate, out var to))
            return BadRequest(new { message = "toDate must be an IST calendar day in yyyy-MM-dd form." });

        if (from.HasValue && to.HasValue && from.Value > to.Value)
            return BadRequest(new { message = "fromDate must not be after toDate." });

        if (take < 1 || take > LiveRunHistoryFilter.MaxTake)
            return BadRequest(new { message = $"take must be between 1 and {LiveRunHistoryFilter.MaxTake}." });

        if (skip < 0)
            return BadRequest(new { message = "skip must be zero or positive." });

        // Ownership comes from the token, never from the query string: a trader
        // sees only their own runs whatever userId they pass.
        long? scopeUserId = User.IsAdmin() ? userId : User.GetRequiredUserId();

        var filter = new LiveRunHistoryFilter(scopeUserId, strategyId, underlying, status, from, to, take, skip);
        var rows = await _history.ListAsync(filter, cancellationToken);
        return Ok(rows);
    }

    /// <summary>
    /// Per-user rollup for the history page header: runs, active runs, net
    /// P&amp;L and the newest start. Admins get every user; a trader gets one
    /// row — their own.
    /// </summary>
    [HttpGet("runs/summary")]
    public async Task<ActionResult<List<LiveRunUserSummaryResponse>>> GetRunsSummary(CancellationToken cancellationToken)
    {
        long? scopeUserId = User.IsAdmin() ? null : User.GetRequiredUserId();
        var rows = await _history.SummarizeAsync(scopeUserId, cancellationToken);
        return Ok(rows);
    }

    /// <summary>
    /// The paper orders of one live run, newest first — the order ledger under
    /// the detail page's position table. Admin, or the user who started the run.
    /// </summary>
    [HttpGet("runs/{runId:long}/orders")]
    public async Task<ActionResult<IReadOnlyList<PaperOrderResponse>>> GetRunOrders(long runId, CancellationToken cancellationToken)
    {
        var run = await _dbContext.SimulationRuns.AsNoTracking()
            .Where(x => x.Id == runId)
            .Select(x => new { x.Id, x.Mode, x.UserId })
            .FirstOrDefaultAsync(cancellationToken);
        if (run is null)
            return NotFound(new { message = $"Strategy run {runId} not found." });

        // Ownership first: a trader learns nothing (not even the mode) about a
        // run they do not own.
        if (!CanRead(run.UserId))
            return Forbid();

        if (!string.Equals(run.Mode, LivePaperMode, StringComparison.OrdinalIgnoreCase))
            return NotFound(new { message = $"Run {runId} is a {run.Mode} run, not a live strategy run — see /api/Backtest/runs/{runId}." });

        var orders = await _getPaperOrders.ExecuteAsync(runId, cancellationToken);
        return Ok(orders);
    }

    /// <summary>Admins read any run; a trader only the runs they own (by user id).</summary>
    private bool CanRead(long ownerUserId)
        => User.IsAdmin() || User.GetUserId() == ownerUserId;

    /// <summary>
    /// Ownership check for the registry-backed routes (logs, signals) that have
    /// no run row in hand: the registry entry while active, the run row
    /// otherwise. An unknown run reads as not readable for a trader.
    /// </summary>
    private async Task<bool> CanReadRunAsync(long runId, CancellationToken cancellationToken)
    {
        if (User.IsAdmin()) return true;

        var callerId = User.GetUserId();
        if (callerId is null) return false;

        var running = _registry.Get(runId);
        if (running is not null) return running.UserId == callerId.Value;

        var ownerId = await _dbContext.SimulationRuns.AsNoTracking()
            .Where(x => x.Id == runId)
            .Select(x => (long?)x.UserId)
            .FirstOrDefaultAsync(cancellationToken);

        return ownerId.HasValue && ownerId.Value == callerId.Value;
    }

    /// <summary>yyyy-MM-dd → IST calendar day; null/blank is "not given". False when malformed.</summary>
    private static bool TryParseIstDate(string? text, out DateOnly? date)
    {
        date = null;
        if (string.IsNullOrWhiteSpace(text)) return true;

        if (DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            date = parsed;
            return true;
        }

        return false;
    }

    // ------------------------------------------------------------------
    // Live view, logs, signals
    // ------------------------------------------------------------------

    /// <summary>
    /// Position-based live view of one run, active or finished. Finished runs
    /// are built from the database (plus the remembered exit reason, when the
    /// run ended since the API started). Admin, or the user who started the
    /// run (403 otherwise).
    /// </summary>
    [HttpGet("runs/{runId:long}/live")]
    public async Task<ActionResult<StrategyLiveViewResponse>> GetRunLive(long runId, CancellationToken cancellationToken)
    {
        var running = _registry.Get(runId);
        var lastExit = running is null ? _registry.GetExitByRun(runId) : null;

        var run = await _dbContext.SimulationRuns.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == runId, cancellationToken);
        if (run is null)
            return NotFound(new { message = $"Strategy run {runId} not found." });

        // Ownership first: a trader learns nothing (not even the mode) about a
        // run they do not own.
        if (!CanRead(run.UserId))
            return Forbid();

        if (!string.Equals(run.Mode, LivePaperMode, StringComparison.OrdinalIgnoreCase))
            return NotFound(new { message = $"Run {runId} is a {run.Mode} run, not a live strategy run — see /api/Backtest/runs/{runId}." });

        int strategyId;
        string name;
        if (running is not null)
        {
            strategyId = running.StrategyId;
            name = running.Name;
        }
        else if (lastExit is not null)
        {
            strategyId = lastExit.StrategyId;
            name = lastExit.Name;
        }
        else
        {
            var strategy = await _catalog.FindByNameAsync(run.StrategyName, cancellationToken);
            strategyId = strategy?.Id ?? StrategyCatalogService.StableId(run.StrategyName);
            name = strategy?.Name ?? run.StrategyName;
        }

        var view = new StrategyLiveViewResponse
        {
            StrategyId = strategyId,
            Name = name,
            IsActive = running is not null
        };

        await FillLiveViewAsync(view, run, running, lastExit, cancellationToken);
        return Ok(view);
    }

    /// <summary>
    /// Legacy strategy-scoped live view: the most recently started active run
    /// of the strategy; with none active, its newest exit or the latest LivePaper
    /// run of that strategy name. Admin, or the user who started that run
    /// (403 otherwise — the same rule as the run-scoped route).
    /// </summary>
    [HttpGet("{id:int}/live")]
    public async Task<ActionResult<StrategyLiveViewResponse>> GetLive(int id, CancellationToken cancellationToken)
    {
        var strategy = await _catalog.FindAsync(id, cancellationToken);
        if (strategy is null) return NotFound(new { message = $"Strategy {id} not found." });

        var running = NewestActiveRun(id);
        var lastExit = running is null ? _registry.GetLastExit(id) : null;

        if (running is not null && !CanRead(running.UserId))
            return Forbid();

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

        // The resolved run belongs to whoever started it; a trader may only
        // read their own (the registry check above covers the active case, this
        // one the newest exit / latest row of the strategy).
        if (!CanRead(run.UserId))
            return Forbid();

        await FillLiveViewAsync(view, run, running, lastExit, cancellationToken);
        return Ok(view);
    }

    /// <summary>
    /// Fills run configuration, spot, P&amp;L, positions and activity of
    /// <paramref name="run"/> into the view. <paramref name="running"/> is the
    /// live registry entry (when active), <paramref name="lastExit"/> the
    /// remembered exit (when it ended since the API started); otherwise
    /// everything comes from the database.
    /// </summary>
    private async Task FillLiveViewAsync(
        StrategyLiveViewResponse view,
        SimulationRun run,
        RunningStrategy? running,
        LastExit? lastExit,
        CancellationToken cancellationToken)
    {
        var p = LiveRunParameters.Parse(run.ParametersJson);

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
            view.Risk = running.Risk;
            view.StartedBy = running.StartedBy;
            view.StartedUtc = running.StartedUtc;
            view.Runner = new StrategyRunnerInfo { ProcessId = running.ProcessId, LastLogUtc = running.LastLogUtc, Adopted = running.Adopted };
        }
        else if (lastExit is not null)
        {
            view.Risk = lastExit.Risk;
            view.StartedBy = lastExit.StartedBy;
            view.StartedUtc = lastExit.StartedUtc;
            view.StoppedUtc = lastExit.AtUtc;
            view.StopReason = lastExit.Reason;
        }
        else
        {
            view.Risk = p.Risk;
            view.StartedBy = await _dbContext.AppUsers.AsNoTracking()
                .Where(x => x.Id == run.UserId)
                .Select(x => x.UserName)
                .FirstOrDefaultAsync(cancellationToken);
            view.StartedUtc = run.StartedUtc ?? run.CreatedUtc;
            view.StoppedUtc = run.CompletedUtc;
        }

        view.StopLoss = view.Risk.OverallStopLoss;
        view.Target = view.Risk.OverallTarget;

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
        view.Groups = built.Groups;

        view.Pnl.Realized = positions.Sum(x => x.RealizedPnl);
        view.Pnl.Unrealized = positions
            .Where(x => string.Equals(x.Status, "Open", StringComparison.OrdinalIgnoreCase))
            .Sum(x => x.UnrealizedPnl);
        view.Pnl.Total = view.Pnl.Realized + view.Pnl.Unrealized;
        view.Pnl.CapitalUsed = built.CapitalUsed;
        view.Pnl.PremiumOutlay = built.PremiumOutlay;
        view.Pnl.PremiumReceived = built.PremiumReceived;

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
            string text;
            if (s.SignalType == RunRiskRules.RiskUpdatedSignalType)
            {
                text = RunRiskRules.DescribeUpdate(s.MetadataJson);
            }
            else
            {
                var reason = ReadMetadataReason(s.MetadataJson);
                text = string.IsNullOrWhiteSpace(reason) ? s.SignalType : reason;
            }

            view.Activity.Add(new LiveActivityResponse
            {
                AtUtc = s.TimestampUtc,
                Type = s.SignalType,
                Text = text,
                GroupId = s.GroupId,
                // RISK_UPDATED rows render client-side from { risk, by } with the
                // same formatter as the Risk chips; Text stays as the fallback.
                MetadataJson = s.SignalType == RunRiskRules.RiskUpdatedSignalType ? s.MetadataJson : null
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
    }

    /// <summary>
    /// Recent runner stdout/stderr of one run (retained for a while after it
    /// finishes). Admin, or the user who started the run.
    /// </summary>
    [HttpGet("runs/{runId:long}/logs")]
    public async Task<IActionResult> GetRunLogs(long runId, [FromQuery] int take = 200, CancellationToken cancellationToken = default)
    {
        if (!await CanReadRunAsync(runId, cancellationToken))
            return Forbid();

        return Ok(_registry.GetLogs(runId, take));
    }

    /// <summary>
    /// Legacy: runner output of the strategy's most recently started active run
    /// (or of its newest exit when nothing is active).
    /// </summary>
    [HttpGet("{id:int}/logs")]
    public async Task<IActionResult> GetLogs(int id, [FromQuery] int take = 200, CancellationToken cancellationToken = default)
    {
        var runId = NewestActiveRun(id)?.RunId ?? _registry.GetLastExit(id)?.RunId;
        if (!runId.HasValue)
            return Ok(Array.Empty<string>());

        if (!await CanReadRunAsync(runId.Value, cancellationToken))
            return Forbid();

        return Ok(_registry.GetLogs(runId.Value, take));
    }

    /// <summary>The runner pushes a copy of each signal here for the dashboard.</summary>
    [HttpPost("runs/{runId:long}/signals")]
    public IActionResult AddRunSignal(long runId, [FromBody] object signal)
    {
        if (_registry.AddSignal(runId, signal))
        {
            return Ok();
        }
        return NotFound(new { message = $"Strategy run {runId} is not currently active." });
    }

    /// <summary>Recent signals of one active run, newest first. Admin, or the user who started the run.</summary>
    [HttpGet("runs/{runId:long}/signals")]
    public async Task<IActionResult> GetRunSignals(long runId, CancellationToken cancellationToken)
    {
        if (!await CanReadRunAsync(runId, cancellationToken))
            return Forbid();

        return Ok(_registry.GetSignals(runId));
    }

    /// <summary>Legacy: posts into the strategy's most recently started active run.</summary>
    [HttpPost("{id:int}/signals")]
    public IActionResult AddSignal(int id, [FromBody] object signal)
    {
        var running = NewestActiveRun(id);
        if (running is not null && _registry.AddSignal(running.RunId, signal))
        {
            return Ok();
        }
        return NotFound(new { message = $"Strategy {id} is not currently active." });
    }

    /// <summary>
    /// Legacy: signals of the strategy's most recently started active run.
    /// Admin, or the user who started that run (403 otherwise).
    /// </summary>
    [HttpGet("{id:int}/signals")]
    public async Task<IActionResult> GetSignals(int id, CancellationToken cancellationToken)
    {
        var running = NewestActiveRun(id);
        if (running is null)
            return Ok(Array.Empty<object>());

        if (!await CanReadRunAsync(running.RunId, cancellationToken))
            return Forbid();

        return Ok(_registry.GetSignals(running.RunId));
    }

    /// <summary>The strategy's most recently started active run, for the legacy strategy-scoped routes.</summary>
    private RunningStrategy? NewestActiveRun(int strategyId)
    {
        var runs = _registry.GetByStrategy(strategyId);
        return runs.Count == 0 ? null : runs[^1];
    }

    private static string AlreadyRunningMessage(string strategyName, string underlying)
        => $"{strategyName} is already running on {underlying} — stop that run or pick another underlying.";

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
        RiskRulesDto Risk);

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
        // A redirected stdout takes the locale encoding on Windows (cp1252), and
        // the runner prints "→", "≤", "₹"... — force UTF-8 on the pipe everywhere.
        processInfo.Environment["PYTHONIOENCODING"] = "utf-8";

        lock (_registry.StartLock)
        {
            if (_registry.Find(id, spec.Underlying) is not null)
            {
                return (Conflict(new { message = AlreadyRunningMessage(spec.Strategy.Name, spec.Underlying) }), null);
            }

            if (_registry.Contains(spec.RunId))
            {
                return (Conflict(new { message = $"Run {spec.RunId} already has a runner behind it." }), null);
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
                    spec.RunId, spec.Underlying, spec.SpotSymbol, spec.Lots, spec.Risk);

                if (!_registry.TryAdd(running))
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                    process.Dispose();
                    return (Conflict(new { message = $"Run {spec.RunId} already has a runner behind it." }), null);
                }

                // A redeployed run is active again; exits of other runs stay listed
                // until the UI dismisses them.
                _registry.ClearLastExit(id, spec.RunId);

                _logger.LogInformation(
                    "Started strategy {StrategyId} ({Name}) pid {Pid} run {RunId} on {Underlying} ({Spot}) x{Lots} risk=[{Risk}] by {User}",
                    id, spec.Strategy.Name, running.ProcessId, spec.RunId, spec.Underlying, spec.SpotSymbol, spec.Lots,
                    spec.Risk.Describe(), spec.StartedBy);

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
        risk = running.Risk,
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
        var activeRuns = _registry.GetByStrategy(entry.Id);
        var recentExits = _registry.GetLastExits(entry.Id);

        // Legacy single-run fields describe the first (oldest) active run; the
        // legacy lastExit is the newest exit.
        var running = activeRuns.Count > 0 ? activeRuns[0] : null;
        var lastExit = recentExits.Count > 0 ? recentExits[0] : null;

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
            ContractRequirements = entry.ContractRequirements.ToList(),
            DefaultParametersJson = entry.DefaultParametersJson,
            DefaultLots = entry.DefaultLots,
            SourceFile = entry.SourceFile,
            CreatedUtc = entry.CreatedUtc,

            ActiveRuns = activeRuns.Select(ToActiveRun).ToList(),
            RecentExits = recentExits.Select(ToLastExit).ToList(),

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
            LastExit = lastExit is null ? null : ToLastExit(lastExit)
        };
    }

    private static StrategyActiveRunResponse ToActiveRun(RunningStrategy running) => new()
    {
        RunId = running.RunId,
        Underlying = running.Underlying,
        SpotSymbol = running.SpotSymbol,
        Lots = running.Lots,
        StopLoss = running.StopLoss,
        Target = running.Target,
        Risk = running.Risk,
        StartedBy = running.StartedBy,
        StartedUtc = running.StartedUtc,
        ProcessId = running.ProcessId,
        Adopted = running.Adopted
    };

    private static StrategyLastExit ToLastExit(LastExit exit) => new()
    {
        RunId = exit.RunId,
        Reason = exit.Reason,
        AtUtc = exit.AtUtc,
        Underlying = exit.Underlying
    };

    /// <summary>metadataJson.reason (any casing), or null.</summary>
    private static string? ReadMetadataReason(string? metadataJson)
        => SignalMetadata.ReadReason(metadataJson);
}
