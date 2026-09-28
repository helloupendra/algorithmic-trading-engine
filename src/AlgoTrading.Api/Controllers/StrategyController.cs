using AlgoTrading.Domain.Constants;
// src/AlgoTrading.Api/Controllers/StrategyController.cs
using AlgoTrading.Api.Configuration;
using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.Risk;
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
    private readonly RunCharges _runCharges;
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
        RunCharges runCharges,
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
        _runCharges = runCharges;
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

    /// <summary>User id to user name, read once per request (the table is small).</summary>
    private async Task<IReadOnlyDictionary<long, string>> UserNamesAsync(CancellationToken cancellationToken)
        => await _dbContext.AppUsers.AsNoTracking()
            .ToDictionaryAsync(u => u.Id, u => u.UserName, cancellationToken);

    /// <summary>
    /// The strategies the caller may run. An admin sees the whole catalog; a
    /// trader sees exactly what their package and overrides allow.
    /// </summary>
    /// <remarks>
    /// Filtering here is a courtesy, so a trader is not shown buttons that would
    /// be refused. The check that actually stops a run lives on the start
    /// endpoint.
    /// </remarks>
    [HttpGet]
    public async Task<ActionResult<List<StrategyListItemResponse>>> GetAll(CancellationToken cancellationToken)
    {
        var entries = await _catalog.GetAllAsync(cancellationToken);
        var names = await UserNamesAsync(cancellationToken);

        var access = await _strategyAccess.GetAccessAsync(User.GetRequiredUserId(), cancellationToken);

        if (!access.IsUnrestricted)
        {
            entries = entries.Where(x => access.AllowsStrategy(x.Name)).ToList();
        }

        return Ok(entries.Select(e => ToListItem(e, names)).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<StrategyListItemResponse>> GetById(int id, CancellationToken cancellationToken)
    {
        var entry = await _catalog.FindAsync(id, cancellationToken);
        if (entry is null) return NotFound(new { message = $"Strategy {id} not found." });
        return Ok(ToListItem(entry, await UserNamesAsync(cancellationToken)));
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
    /// Launches the Python execution runner on the chosen underlying.
    /// </summary>
    /// <remarks>
    /// Open to traders as well as the operator: the trader's Strategies page
    /// uses the same launch dialog (underlying, lots, risk rules, strikes) as
    /// the admin runner, and this is the one endpoint behind it. What keeps a
    /// trader inside their package is <see cref="IStrategyAccessService.CanDeployAsync"/>,
    /// checked below on the last step before a runner is launched. Admins pass
    /// it by role. The run is owned by whoever started it, or by the trader an
    /// admin names in <c>ownerUserId</c>.
    /// <para>
    /// A refusal leaves no row behind. Everything that can refuse — the
    /// request, the duplicate checks, the caps, the host's memory, the
    /// trader's package — is decided before the run row is inserted; only a
    /// runner that fails to launch after that is recorded, as Failed. Until
    /// 28 Sep the duplicate and desk-wide checks ran after the insert, and
    /// every refusal left a Failed run in the history.
    /// </para>
    /// </remarks>
    [HttpPost("{id:int}/start")]
    public async Task<IActionResult> StartStrategy(
        int id,
        [FromBody] StartStrategyRequest? request,
        [FromServices] AlgoTrading.Application.Interfaces.IRiskLimitsStore limitsStore,
        [FromServices] AlgoTrading.Application.Interfaces.IDerivativesInstrumentService derivatives,
        [FromServices] IHostMemory memory,
        CancellationToken cancellationToken)
    {
        var strategy = await _catalog.FindAsync(id, cancellationToken);
        if (strategy is null) return NotFound(new { message = $"Strategy {id} not found." });

        if (!string.IsNullOrWhiteSpace(strategy.Error))
            return BadRequest(new { message = $"{strategy.Name} cannot be started: {strategy.Error}" });

        if (request is null || string.IsNullOrWhiteSpace(request.Underlying))
            return BadRequest(new { message = "underlying is required — pick the index or stock the strategy should trade." });

        var underlying = request.Underlying.Trim().ToUpperInvariant();

        // Whose account this run belongs to. Normally the caller's; an admin
        // may name another trader, which is how the morning job deploys into
        // every account without holding anyone's password.
        long callerId = User.GetRequiredUserId();
        long userId = callerId;
        string ownerName = User.GetUserName() ?? "unknown";
        if (request.OwnerUserId is long owner && owner != callerId)
        {
            if (!User.IsAdmin())
                return StatusCode(StatusCodes.Status403Forbidden,
                    new { message = "Only an admin can start a run in another trader's account." });

            var target = await _dbContext.AppUsers
                .FirstOrDefaultAsync(u => u.Id == owner, cancellationToken);
            if (target is null)
                return NotFound(new { message = $"There is no user {owner}." });
            if (!target.IsActive)
                return BadRequest(new { message = $"{target.UserName}'s account is disabled." });

            userId = target.Id;
            ownerName = target.UserName;
        }

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

        // What the run prices itself against. For an index that is a fixed spot
        // symbol; for a commodity there is no spot at all, so the near-month
        // future stands in - resolved now and stored on the run, because it is a
        // different contract every month and a run must not silently follow a
        // symbol that expires under it.
        var spotSymbol = UnderlyingCatalog.SpotSymbolFor(underlying);
        if (string.IsNullOrWhiteSpace(spotSymbol))
        {
            spotSymbol = await derivatives.GetNearestFutureSymbolAsync(underlying, cancellationToken)
                         ?? string.Empty;

            if (string.IsNullOrWhiteSpace(spotSymbol))
            {
                return BadRequest(new
                {
                    message = $"{underlying} has no unexpired futures contract in the instrument master, "
                              + "so there is nothing to price it against. Import the master and try again."
                });
            }
        }

        var command = PrepareRunner(strategy, out var commandError);
        if (command is null) return commandError!;

        // Who pressed the button, which is not always whose account it is.
        var startedBy = User.GetUserName() ?? "unknown";

        // From the duplicate checks to the registered runner, one start at a
        // time. Without it two overlapping starts (a double click, the morning
        // job racing the console) could both pass the checks and both launch.
        await _registry.StartGate.WaitAsync(cancellationToken);
        try
        {
            var refusal = await RefuseStartAsync(strategy, underlying, userId, ownerName, lots, limitsStore, memory, cancellationToken);
            if (refusal is not null) return refusal;

            // Past the checks the start is decided, and it is finished even if
            // the caller goes away: a row inserted and then abandoned before its
            // launch would be an open run with no runner behind it.
            var now = DateTime.UtcNow;
            var run = new SimulationRun
            {
                UserId = userId,
                StartedByUserId = callerId,
                StartedByName = startedBy,
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

            await _dbContext.SimulationRuns.AddAsync(run, CancellationToken.None);
            await _dbContext.SaveChangesAsync(CancellationToken.None);

            await EnsureSpotOnWatchlistAsync(spotSymbol, CancellationToken.None);

            var launch = new LaunchSpec(strategy, run.Id, userId, startedBy, underlying, spotSymbol, lots, risk);
            var (error, running) = LaunchRunner(launch, command);
            if (error is not null || running is null)
            {
                run.Status = "Failed";
                run.LastError = "Runner failed to start.";
                run.CompletedUtc = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync(CancellationToken.None);
                _runControl.PublishRunEvent(run.Id, userId, $"Failed: the runner of {strategy.Name} on {underlying} did not start");

                HttpContext.Describe(
                    $"Start of {strategy.Name} on {underlying} failed — run #{run.Id} could not start its runner.",
                    "run",
                    run.Id.ToString());

                return error ?? StatusCode(StatusCodes.Status500InternalServerError, new { message = "Failed to start the runner." });
            }

            // Durable pid so a restarted API can adopt (or stop) this runner.
            await _runControl.RecordRunnerPidAsync(running.RunId, running.ProcessId, startedBy);
            _runControl.PublishRunEvent(run.Id, userId, $"Started {strategy.Name} on {underlying}, {lots} lot(s)");

            HttpContext.Describe(
                $"Started {strategy.Name} on {underlying} — run #{run.Id}, {lots} lot(s)"
                    + (userId == callerId ? "." : $", in {ownerName}'s account."),
                "run",
                run.Id.ToString());

            return Ok(StartResponse($"Started {strategy.Name} on {underlying} (paper).", running));
        }
        finally
        {
            _registry.StartGate.Release();
        }
    }

    /// <summary>
    /// Why this start must not happen, or null. Called under
    /// <see cref="StrategyProcessRegistry.StartGate"/>, so nothing it counts
    /// can change before the run is launched.
    /// </summary>
    private async Task<IActionResult?> RefuseStartAsync(
        StrategyCatalogEntry strategy,
        string underlying,
        long userId,
        string ownerName,
        int lots,
        AlgoTrading.Application.Interfaces.IRiskLimitsStore limitsStore,
        IHostMemory memory,
        CancellationToken cancellationToken)
    {
        // The same strategy may run on several underlyings at once, and two
        // traders may each run it on the same one — their books are separate.
        // Only the same strategy, on the same underlying, in the SAME account
        // is refused: that would double a position by accident.
        if (_registry.Find(strategy.Id, underlying, userId) is not null)
            return Conflict(new { message = AlreadyRunningMessage(strategy.Name, underlying, ownerName) });

        // The registry is not the whole truth: a run the startup reconcile
        // could neither adopt nor close is still open in the database with no
        // entry here, and a second runner beside it would trade the same book.
        var open = await FindOpenRunAsync(strategy.Name, underlying, userId, cancellationToken);
        if (open is { } row)
        {
            return Conflict(new
            {
                message = $"{strategy.Name} run #{row.RunId} on {underlying} is still {row.Status} in {ownerName}'s account "
                          + "with no runner the API knows of — stop it from its run page before starting another.",
                runId = row.RunId
            });
        }

        // The limit is per trader: one account filling the desk must not stop
        // another account from opening its first run of the day.
        var riskLimits = limitsStore.GetLimits();
        if (RunCap.Blocks(riskLimits.MaxConcurrentRuns, _registry.CountFor(userId)))
            return StatusCode(StatusCodes.Status429TooManyRequests,
                new { message = $"Concurrent strategy limit reached ({riskLimits.MaxConcurrentRuns})." });

        // The box's own ceiling, whoever's runs they are.
        if (_registry.Count >= _options.MaxConcurrentProcesses)
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                message = $"The desk already runs {_registry.Count} strategy runners, its limit "
                          + $"(StrategyRunner:MaxConcurrentProcesses = {_options.MaxConcurrentProcesses}). Stop one first."
            });

        // A null reading is "cannot tell" (not Linux), never "none left".
        long floor = (long)_options.MinAvailableMemoryMb * 1024 * 1024;
        if (memory.AvailableBytes() is long available && available < floor)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                message = $"The server has only {available / (1024 * 1024)} MB of memory available; a new runner needs "
                          + $"{_options.MinAvailableMemoryMb} MB free to start without starving the ones already running. "
                          + "Stop a run first."
            });
        }

        // What this trader may run — strategy, underlying, lots, mode and how
        // many runs they already have open. Filtering the list they see is a
        // courtesy; this is what actually stops anything.
        int openRuns = await _dbContext.SimulationRuns
            .CountAsync(
                x => x.UserId == userId && (x.Status == "Running" || x.Status == "Stopping"),
                cancellationToken);
        var decision = await _strategyAccess.CanDeployAsync(
            userId, strategy.Name, underlying, lots, LivePaperMode, openRuns, cancellationToken);
        if (!decision.Allowed)
            return StatusCode(StatusCodes.Status403Forbidden, new { message = decision.Reason });

        return null;
    }

    /// <summary>
    /// The LivePaper run of this strategy on this underlying in this account
    /// whose row is still Running or Stopping, if there is one.
    /// </summary>
    private async Task<(long RunId, string Status)?> FindOpenRunAsync(
        string strategyName, string underlying, long userId, CancellationToken cancellationToken)
    {
        // The underlying lives in ParametersJson, not in a column. An account
        // has a handful of open rows, so they are read and parsed here.
        var rows = await _dbContext.SimulationRuns.AsNoTracking()
            .Where(x => x.UserId == userId
                        && x.Mode == LivePaperMode
                        && x.StrategyName == strategyName
                        && (x.Status == StrategyRunControl.RunStatusRunning || x.Status == StrategyRunControl.RunStatusStopping))
            .Select(x => new { x.Id, x.Status, x.Symbol, x.ParametersJson })
            .ToListAsync(cancellationToken);

        foreach (var row in rows)
        {
            var rowUnderlying = LiveRunParameters.Parse(row.ParametersJson).Underlying
                                ?? UnderlyingCatalog.UnderlyingForSpot(row.Symbol)
                                ?? UnderlyingCatalog.InferUnderlying(row.Symbol);
            if (string.Equals(rowUnderlying?.Trim(), underlying, StringComparison.OrdinalIgnoreCase))
                return (row.Id, row.Status);
        }

        return null;
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
    /// <summary>Body of POST /api/Strategy/runs/{runId}/positions/close.</summary>
    /// <param name="PositionIds">The open positions to square off. At least one.</param>
    /// <param name="Reason">Optional note recorded against the closing signal.</param>
    public sealed record ClosePositionsRequest(List<long>? PositionIds, string? Reason);

    /// <summary>
    /// Squares off SOME of a run's open positions and leaves the run trading.
    /// </summary>
    /// <remarks>
    /// Stopping the run was the only way to get out of a position by hand, and
    /// it is all-or-nothing: it flattens every leg and kills the runner. A
    /// strategy that holds several positions needs the smaller instrument —
    /// close this one, keep the rest, keep trading.
    ///
    /// The squaring-off itself is not new. The risk guard has always closed
    /// individual legs through <see cref="IPaperTradingService.ClosePositionsAsync"/>,
    /// which fills reduce-only under the run's own lock and writes one
    /// CLOSE_GROUP per group, so a manual close and a stop-loss produce exactly
    /// the same rows. Only a way to ask for it was missing.
    ///
    /// Ids that are not open, or belong to another run, are reported rather than
    /// closed — a stale console tab must not square off somebody else's leg.
    /// Admin, or the user who started the run.
    /// </remarks>
    [HttpPost("runs/{runId:long}/positions/close")]
    public async Task<IActionResult> ClosePositions(
        long runId,
        [FromBody] ClosePositionsRequest? request,
        CancellationToken cancellationToken)
    {
        var requested = request?.PositionIds?.Distinct().ToList() ?? new List<long>();
        if (requested.Count == 0)
            return BadRequest(new { message = "positionIds is required - name at least one open position to square off." });

        var run = await _dbContext.SimulationRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == runId, cancellationToken);

        if (run is null)
            return NotFound(new { message = $"Strategy run {runId} not found." });

        var startedBy = await _dbContext.AppUsers.AsNoTracking()
            .Where(x => x.Id == run.UserId)
            .Select(x => x.UserName)
            .FirstOrDefaultAsync(cancellationToken);

        if (!CanStop(startedBy, run.UserId))
            return Forbid();

        // Scoped to this run and to Open on purpose: the id alone is a global
        // key, and trusting it would let one run close another's position.
        var closable = await _dbContext.PaperPositions
            .AsNoTracking()
            .Where(x => x.SimulationRunId == runId && x.Status == "Open" && requested.Contains(x.Id))
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        var skipped = requested.Except(closable).ToList();

        if (closable.Count == 0)
        {
            return BadRequest(new
            {
                message = "None of those positions are open on this run - the console may be showing a stale view.",
                skipped
            });
        }

        var userName = User.GetUserName() ?? "unknown";
        var reason = string.IsNullOrWhiteSpace(request?.Reason)
            ? $"Squared off by {userName}"
            : request!.Reason!.Trim();

        int closed = await _paperTrading.ClosePositionsAsync(runId, closable, reason, userName, cancellationToken);

        _logger.LogInformation(
            "Run {RunId} ({Strategy}): {Closed} position(s) squared off by hand ({By}); {Skipped} skipped.",
            runId, run.StrategyName, closed, userName, skipped.Count);

        return Ok(new
        {
            message = closed == 1
                ? $"Squared off 1 position on {run.StrategyName} run {runId}; the run is still trading."
                : $"Squared off {closed} positions on {run.StrategyName} run {runId}; the run is still trading.",
            runId,
            closed,
            skipped
        });
    }

    /// <summary>Body of PUT /api/Strategy/runs/{runId}/positions/{positionId}/carry-forward.</summary>
    /// <param name="CarryForward">True to hold the position overnight, false for intraday.</param>
    public sealed record CarryForwardRequest(bool? CarryForward);

    /// <summary>
    /// Ticks or unticks "carry forward" on one open position of a run or of the
    /// manual book.
    /// </summary>
    /// <remarks>
    /// The owner's request, 27 Sep: "if I want to carry forward, there should
    /// be a tick there and ticking it is enough. In strategies, even a single
    /// leg." In the manual book an unticked position is squared off at its
    /// exchange's close; in a strategy run a ticked leg moves to the owner's
    /// manual book when the market close stops the run — and only then: the
    /// Stop button, a risk rule and a runner that dies still square off every
    /// leg. See <see cref="PositionCarryForward"/>.
    ///
    /// Admin, or the owner of the run — the same rule as squaring the position
    /// off. Only an open position of a running (non-recap) run; anything else
    /// answers 409 and changes nothing. Each change is a CARRY_FORWARD row on
    /// the run's activity with who and when, besides the activity log's own
    /// record of the request.
    /// </remarks>
    [HttpPut("runs/{runId:long}/positions/{positionId:long}/carry-forward")]
    public async Task<IActionResult> SetCarryForward(
        long runId,
        long positionId,
        [FromBody] CarryForwardRequest? request,
        [FromServices] PositionCarryForward carryForward,
        CancellationToken cancellationToken)
    {
        if (request?.CarryForward is not { } carry)
            return BadRequest(new { message = "carryForward (true or false) is required." });

        var run = await _dbContext.SimulationRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == runId && x.Mode == LivePaperMode, cancellationToken);

        if (run is null)
            return NotFound(new { message = $"Strategy run {runId} not found." });

        var startedBy = _registry.Get(runId)?.StartedBy
                        ?? await _dbContext.AppUsers.AsNoTracking()
                            .Where(x => x.Id == run.UserId)
                            .Select(x => x.UserName)
                            .FirstOrDefaultAsync(cancellationToken);

        if (!CanStop(startedBy, run.UserId))
            return Forbid();

        var userName = User.GetUserName() ?? "unknown";
        var result = await carryForward.SetAsync(run, positionId, carry, userName, cancellationToken);

        if (result.Outcome is not (PositionCarryForward.Outcome.Changed or PositionCarryForward.Outcome.Unchanged))
            return Conflict(new { message = result.Message, runId, positionId });

        if (result.Outcome == PositionCarryForward.Outcome.Changed)
        {
            HttpContext.Describe($"{result.Message} — run #{runId}, position #{positionId}.", "run", runId.ToString());
            _logger.LogInformation("Run {RunId} ({Strategy}) position {PositionId}: {Message}",
                runId, run.StrategyName, positionId, result.Message);
        }

        return Ok(new
        {
            message = result.Message,
            runId,
            positionId,
            carryForward = carry,
            changed = result.Outcome == PositionCarryForward.Outcome.Changed
        });
    }

    /// <summary>True for a manual book that is still open — Running, with no runner by design.</summary>
    private static bool IsOpenManualBook(SimulationRun run)
        => run.StrategyName == ManualOrdersController.BookStrategyName
           && StrategyRunControl.IsOpenStatus(run.Status);

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
        // A recap run's activity is on the replayed session's clock (RecapClock).
        var marketNow = run is null ? null : await RecapClock.NowAsync(_dbContext, run, cancellationToken);
        await _dbContext.SimulationSignals.AddAsync(new SimulationSignal
        {
            SimulationRunId = runId,
            StrategyName = running.Name,
            SignalType = RunRiskRules.RiskUpdatedSignalType,
            TimestampUtc = marketNow ?? now,
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
        _runControl.PublishRunEvent(runId, running.UserId, $"Risk rules changed by {userName}");
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

    /// <summary>
    /// The runner reports that its tick feed has gone dry, or come back.
    /// </summary>
    /// <remarks>
    /// A frozen feed is the dangerous failure: the runner keeps waiting, the
    /// process looks healthy, and the strategy is blind to a market that is
    /// still moving. Only the runner knows what reached it — the API's
    /// <see cref="FeedFailoverService"/> watches the stream as a whole, not
    /// each run's input — so the runner says so and this turns it into an
    /// alert.
    /// <para>
    /// It never stops the run. Squaring off positions because ticks stopped
    /// would be a bigger decision than this endpoint should make on its own,
    /// and the operator now has what they need to make it.
    /// </para>
    /// <para>
    /// Every report is logged against the run and kept as an alert_events row,
    /// but only <see cref="FeedStallAlertGate"/>'s choice reaches Telegram: the
    /// first stall per underlying in ten minutes, after 180 s of silence, and a
    /// recovery only for a stall that was sent. Until 28 Sep every run sent
    /// both halves of every blip — 572 messages on 25 Sep, 350 of them refused.
    /// </para>
    /// </remarks>
    [HttpPost("runs/{runId:long}/feed")]
    public async Task<IActionResult> ReportFeedHealth(
        long runId,
        [FromBody] RunnerFeedHealthRequest? request,
        [FromServices] FeedStallAlertGate gate,
        CancellationToken cancellationToken)
    {
        if (request is null)
            return BadRequest(new { message = "A body is required." });

        var run = await _dbContext.SimulationRuns.AsNoTracking()
            .Where(x => x.Id == runId && x.Mode == LivePaperMode)
            .Select(x => new { x.Id, x.Status, x.StrategyName, x.Symbol, x.UserId })
            .FirstOrDefaultAsync(cancellationToken);

        if (run is null)
            return NotFound(new { message = $"Strategy run {runId} not found." });

        if (!StrategyRunControl.IsOpenStatus(run.Status))
            return NotFound(new { message = $"Strategy run {runId} is not running (status {run.Status})." });

        // The account first, as in every run alert: with the plan in two
        // accounts a stalled feed reports once per run, and without the name
        // each pair read as one run reported twice.
        string? owner = await _dbContext.AppUsers.AsNoTracking()
            .Where(u => u.Id == run.UserId)
            .Select(u => u.UserName)
            .FirstOrDefaultAsync(cancellationToken);
        string tag = string.IsNullOrWhiteSpace(owner) ? string.Empty : $"[{owner}] ";

        string underlying = request.Underlying ?? string.Empty;
        int seconds = Math.Max(0, request.SilentSeconds);

        if (request.IsStalled)
        {
            _registry.AppendLog(runId, $"FEED STALLED — no ticks for {seconds}s");

            bool send = gate.ShouldSendStall(underlying, seconds);
            string title = $"{tag}Feed stalled — {run.StrategyName} on {underlying}";
            string message = $"Run #{runId} has had no ticks for {seconds}s while the market is open. "
                + "The strategy is still running but is not seeing prices."
                + (send
                    ? $" Other {underlying} runs reporting this in the next {FeedStallAlertGate.Window.TotalMinutes:0} minutes are on the Alerts page, not sent."
                    : string.Empty);

            if (send)
            {
                await _notifier.NotifyAsync(NotificationCategory.StrategyRun, NotificationSeverity.Warning, title, message,
                    underlying: underlying, symbol: run.Symbol, simulationRunId: runId, cancellationToken: cancellationToken);
            }
            else
            {
                await _notifier.RecordAsync(NotificationCategory.StrategyRun, NotificationSeverity.Warning, title, message,
                    underlying: underlying, symbol: run.Symbol, simulationRunId: runId, cancellationToken: cancellationToken);
            }

            HttpContext.Describe($"Reported a stalled feed on run #{runId} — {seconds}s without ticks.", "run", runId.ToString());
        }
        else
        {
            _registry.AppendLog(runId, $"feed recovered after {seconds}s");

            string title = $"{tag}Feed recovered — {run.StrategyName} on {underlying}";
            string message = $"Run #{runId} is receiving ticks again after {seconds}s.";

            if (gate.ShouldSendRecovery(underlying))
            {
                await _notifier.NotifyAsync(NotificationCategory.StrategyRun, NotificationSeverity.Success, title, message,
                    underlying: underlying, symbol: run.Symbol, simulationRunId: runId, cancellationToken: cancellationToken);
            }
            else
            {
                await _notifier.RecordAsync(NotificationCategory.StrategyRun, NotificationSeverity.Success, title, message,
                    underlying: underlying, symbol: run.Symbol, simulationRunId: runId, cancellationToken: cancellationToken);
            }

            HttpContext.Describe($"Reported feed recovery on run #{runId} after {seconds}s.", "run", runId.ToString());
        }

        return Ok(new { runId, acknowledged = true });
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
    /// One IST day of live runs' P&amp;L minute by minute (date yyyy-MM-dd,
    /// default today): each run's realized, unrealized, charges and net as
    /// compact arrays, and each account's total at every minute. Written by
    /// the API's minute recorder while runs are live; see
    /// <see cref="RunPnlSeriesResponse"/> for how the totals are summed.
    /// </summary>
    /// <remarks>
    /// Scoped as the run list is: a trader gets their own runs whatever userId
    /// they pass; an admin gets everyone's, or one user's with userId.
    /// </remarks>
    [HttpGet("runs/pnl-series")]
    public async Task<ActionResult<RunPnlSeriesResponse>> GetPnlSeries(
        [FromQuery] string? date,
        [FromQuery] long? userId,
        [FromServices] RunPnlSeriesBuilder series,
        CancellationToken cancellationToken)
    {
        if (!TryParseIstDate(date, out var day))
            return BadRequest(new { message = "date must be an IST calendar day in yyyy-MM-dd form." });

        long? scopeUserId = User.IsAdmin() ? userId : User.GetRequiredUserId();
        return Ok(await series.BuildAsync(day ?? IstTime.DateOf(DateTime.UtcNow), scopeUserId, cancellationToken));
    }

    /// <summary>
    /// The lifetime record of one strategy — every live run it has ever had,
    /// rolled up into counts, P&amp;L, its best and worst run, and breakdowns by
    /// underlying and by how the runs ended. The runs themselves still come from
    /// <c>GET /api/Strategy/runs?strategyId=…</c>; this answers "has this
    /// strategy ever paid?" without paging the whole history first.
    /// </summary>
    /// <remarks>
    /// Scope comes from the token, exactly as the run list's does: a trader's
    /// record is their own runs whatever userId they pass, and an admin gets
    /// every user's or one user's. Live runs only — a backtest is a hypothesis
    /// and a live run is a result, and a record that added the two would claim
    /// something neither number supports.
    /// </remarks>
    [HttpGet("{id:int}/track-record")]
    public async Task<ActionResult<StrategyTrackRecordResponse>> GetTrackRecord(
        int id,
        [FromQuery] long? userId,
        CancellationToken cancellationToken)
    {
        long? scopeUserId = User.IsAdmin() ? userId : User.GetRequiredUserId();

        var record = await _history.SummarizeStrategyAsync(id, scopeUserId, cancellationToken);
        if (record is null)
            return NotFound(new { message = $"Strategy {id} is in neither the catalog nor the run history." });

        return Ok(record);
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
            // A manual book is open while its row says Running; it has no runner
            // process to ask about, so the registry would always call it dead
            // and the console would paint an open book, holding open positions,
            // as Stopped.
            IsActive = running is not null || IsOpenManualBook(run)
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
        var lastExit = running is null ? ExitsVisibleToCaller(id).FirstOrDefault() : null;

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
        if (RecapClock.IsRecap(run.ParametersJson))
        {
            view.Session = RecapClock.RecapSession;
            view.RecapDate = RecapClock.RecapDate(run.ParametersJson)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        view.OwnerUserId = run.UserId;
        view.OwnerName = await _dbContext.AppUsers.AsNoTracking()
            .Where(x => x.Id == run.UserId)
            .Select(x => x.UserName)
            .FirstOrDefaultAsync(cancellationToken);

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
            // Rows from before 28 Sep recorded no starter; the owner is the
            // best that can be said for them.
            view.StartedBy = string.IsNullOrWhiteSpace(run.StartedByName) ? view.OwnerName : run.StartedByName;
            view.StartedUtc = run.StartedUtc ?? run.CreatedUtc;
            view.StoppedUtc = run.CompletedUtc;
        }

        view.CanControl = CanStop(view.StartedBy, run.UserId);
        view.IsManualBook = run.StrategyName == ManualOrdersController.BookStrategyName;
        // The same conditions PUT …/carry-forward checks; who may is CanControl.
        view.CanCarryForward = PositionCarryForward.IsChangeable(run, active: running is not null || IsOpenManualBook(run));

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
        view.Greeks = built.Greeks;

        view.Pnl.Realized = positions.Sum(x => x.RealizedPnl);
        view.Pnl.Unrealized = positions
            .Where(x => string.Equals(x.Status, "Open", StringComparison.OrdinalIgnoreCase))
            .Sum(x => x.UnrealizedPnl);
        view.Pnl.Total = view.Pnl.Realized + view.Pnl.Unrealized;
        // The same charges the run history takes off, so the run page and the
        // history row beside it can no longer disagree about one run's P&L.
        view.Pnl.Charges = await _runCharges.ForRunAsync(run.Id, cancellationToken);
        view.Pnl.Net = view.Pnl.Total - view.Pnl.Charges;
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
        var runId = NewestActiveRun(id)?.RunId ?? ExitsVisibleToCaller(id).FirstOrDefault()?.RunId;
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

    /// <summary>
    /// The strategy's recent exits the caller may see, newest first: every
    /// account's for an admin, a trader's own otherwise. Another account's
    /// exits carry its run ids, underlyings and stop reasons.
    /// </summary>
    private IReadOnlyList<LastExit> ExitsVisibleToCaller(int strategyId)
    {
        if (User.IsAdmin()) return _registry.GetLastExits(strategyId);

        return User.GetUserId() is long me
            ? _registry.GetLastExits(strategyId, me)
            : Array.Empty<LastExit>();
    }

    /// <summary>The strategy's most recently started active run, for the legacy strategy-scoped routes.</summary>
    private RunningStrategy? NewestActiveRun(int strategyId)
    {
        var runs = _registry.GetByStrategy(strategyId);
        return runs.Count == 0 ? null : runs[^1];
    }

    private static string AlreadyRunningMessage(string strategyName, string underlying, string? account = null)
        => account is null
            ? $"{strategyName} is already running on {underlying} — stop that run or pick another underlying."
            : $"{strategyName} is already running on {underlying} in {account}'s account — stop that run or pick another underlying.";

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

    /// <summary>What goes on the runner's command line, settled before any row exists.</summary>
    private sealed record RunnerCommand(string ScriptPath, string StrategyArgument);

    /// <summary>
    /// The runner script and the strategy argument, or the refusal. Checked
    /// before the run row is inserted: a missing script is a refusal, not a
    /// Failed run.
    /// </summary>
    private RunnerCommand? PrepareRunner(StrategyCatalogEntry strategy, out IActionResult? error)
    {
        error = null;
        var scriptPath = _engine.ScriptPath("strategies", "execution_runner.py");
        if (!System.IO.File.Exists(scriptPath))
        {
            _logger.LogError("Strategy runner not found at {ScriptPath}", scriptPath);
            error = StatusCode(StatusCodes.Status500InternalServerError,
                new { message = $"Strategy runner not found at '{scriptPath}'. Set StrategyRunner:EngineDirectory." });
            return null;
        }

        // The strategy name reaches a command line, so allow only characters that
        // appear in a legitimate strategy identifier.
        var strategyArgument = new string(strategy.Name.Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrEmpty(strategyArgument))
        {
            error = BadRequest(new { message = $"Strategy name '{strategy.Name}' contains no usable characters." });
            return null;
        }

        return new RunnerCommand(scriptPath, strategyArgument);
    }

    /// <summary>
    /// Spawns execution_runner.py and registers it. Returns an error result
    /// instead of throwing so the caller can close its run row. The duplicate
    /// and capacity checks are the caller's, made under
    /// <see cref="StrategyProcessRegistry.StartGate"/>; this only launches.
    /// </summary>
    private (IActionResult? Error, RunningStrategy? Running) LaunchRunner(LaunchSpec spec, RunnerCommand command)
    {
        int id = spec.Strategy.Id;
        var engineDirectory = _engine.EngineDirectory;

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
        processInfo.ArgumentList.Add(command.ScriptPath);
        processInfo.ArgumentList.Add("--strategy");
        processInfo.ArgumentList.Add(command.StrategyArgument);
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
                spec.RunId, spec.Underlying, spec.SpotSymbol, spec.Lots, spec.Risk)
            {
                // Where the runner writes its own output from its first line;
                // the console is read from there (core/safe_output.py).
                OutputLogPath = RunnerOutputLog.PathFor(_engine.EngineLogDirectory, spec.RunId, process.Id)
            };

            if (!_registry.TryAdd(running))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                process.Dispose();
                return (Conflict(new { message = $"Run {spec.RunId} already has a runner behind it." }), null);
            }

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

    private StrategyListItemResponse ToListItem(StrategyCatalogEntry entry, IReadOnlyDictionary<long, string> names)
    {
        // A trader sees their own runs of this strategy, an admin sees every
        // one. It mattered little while the whole desk was one account; from
        // the day two accounts run the same plan, an unscoped list would show a
        // trader somebody else's run — which they cannot open (the live view is
        // owner-checked) and should not have been told about.
        var activeRuns = _registry.GetByStrategy(entry.Id);
        var recentExits = ExitsVisibleToCaller(entry.Id);
        if (!User.IsAdmin())
        {
            long? me = User.GetUserId();
            activeRuns = activeRuns.Where(r => r.UserId == me).ToList();
        }

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

            ActiveRuns = activeRuns.Select(r => ToActiveRun(r, names)).ToList(),
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

    private static StrategyActiveRunResponse ToActiveRun(RunningStrategy running, IReadOnlyDictionary<long, string> names) => new()
    {
        RunId = running.RunId,
        OwnerUserId = running.UserId,
        OwnerName = names.TryGetValue(running.UserId, out var owner) ? owner : null,
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
