// src/AlgoTrading.Api/Services/StrategyStartService.cs
using System.Diagnostics;
using System.Globalization;
using AlgoTrading.Api.Configuration;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.Risk;
using AlgoTrading.Application.UseCases.LiveData;
using AlgoTrading.Contracts.LiveData;
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services;

/// <summary>Who asks for a start: the caller's id and name, and whether they are an admin.</summary>
/// <param name="UserName">Who pressed the button; recorded on the run as who started it.</param>
public sealed record StrategyStarter(long UserId, string? UserName, bool IsAdmin);

/// <summary>How a start ended: started, or refused (and how an HTTP caller says so).</summary>
public enum StrategyStartKind
{
    Started,
    NotFound,
    BadRequest,
    Forbidden,
    Conflict,
    TooMany,
    ServerError,
}

/// <summary>
/// A start's outcome. <c>Message</c> is the refusal's words (or the start's); <c>RunId</c> names the open run a
/// conflict found, or the run whose runner failed to launch (<c>LaunchFailed</c>).
/// </summary>
public sealed record StrategyStartOutcome(
    StrategyStartKind Kind,
    string Message,
    long? RunId = null,
    RunningStrategy? Running = null,
    string? Strategy = null,
    string? Underlying = null,
    int Lots = 0,
    long OwnerUserId = 0,
    string? OwnerName = null,
    bool LaunchFailed = false)
{
    public bool Started => Kind == StrategyStartKind.Started && Running is not null;

    public static StrategyStartOutcome Refused(StrategyStartKind kind, string message, long? runId = null) => new(kind, message, runId);
}

/// <summary>
/// The one way a live strategy runner starts: the checks, the run row, the runner process and its registration.
/// <c>POST /api/Strategy/{id}/start</c> (<see cref="Controllers.StrategyController.StartStrategy"/>) and the AI Trader's
/// strategy starts both come here, so a start the AI asks for is refused exactly where a person's would be.
/// </summary>
/// <remarks>
/// <para>
/// A refusal leaves no row behind. Everything that can refuse (the request, the market's hours, the duplicate checks,
/// the caps, the host's memory, the owner's package) is decided before the run row is inserted; only a runner that
/// fails to launch after that is recorded, as Failed. Until 28 Sep the duplicate and desk-wide checks ran after the
/// insert, and every refusal left a Failed run in the history.
/// </para>
/// <para>
/// From the duplicate checks to the registered runner, one start at a time (<see cref="StrategyProcessRegistry.StartGate"/>):
/// two overlapping starts (a double click, the morning job racing the console, the AI Trader beside a person) cannot
/// both pass the checks and both launch.
/// </para>
/// </remarks>
public sealed class StrategyStartService(
    TradingDbContext db,
    StrategyCatalogService catalog,
    StrategyProcessRegistry registry,
    StrategyRunControl runControl,
    PythonEngineLocator engine,
    UpsertWatchlistItemUseCase upsertWatchlistItem,
    IStrategyAccessService strategyAccess,
    IOptions<StrategyRunnerOptions> options,
    ILogger logger)
{
    public const string LivePaperMode = "LivePaper";
    public const decimal DefaultInitialCapital = 1_000_000m;

    private readonly StrategyRunnerOptions _options = options.Value;

    /// <summary>
    /// Why a live run on <paramref name="underlying"/> cannot start at <paramref name="nowUtc"/>: its exchange is
    /// closed today, or today's session has ended. Null while today's session is still to come or open.
    /// </summary>
    public static string? WhyMarketClosed(IMarketSessionService sessions, DateTime nowUtc, string underlying, string? spotSymbol)
    {
        string exchange = MarketCloseRules.ExchangeOf(underlying, spotSymbol);
        var info = sessions.GetSessionInfo(nowUtc, exchange, exchange == MarketCloseRules.Mcx ? "COM" : "FO");
        var ist = IstTime.ToIst(nowUtc);
        if (!info.IsTradingDay)
        {
            return $"{exchange} is closed today ({ist:ddd d MMM}): a live run can start only on a trading day. " +
                   "To test the strategy on a past session, start it as a recap.";
        }

        if (nowUtc >= info.SessionCloseUtc)
        {
            return $"{exchange} closed at {IstTime.ToIst(info.SessionCloseUtc):HH:mm} IST today: a live run started now would trade " +
                   "on stale prices. Start it on the next trading day, or as a recap to test it on today's session.";
        }

        return null;
    }

    /// <summary>
    /// Starts strategy <paramref name="strategyId"/> as <paramref name="caller"/> asked, in the caller's account or, for
    /// an admin, the trader named in <see cref="StartStrategyRequest.OwnerUserId"/>.
    /// </summary>
    public async Task<StrategyStartOutcome> StartAsync(
        int strategyId,
        StartStrategyRequest? request,
        StrategyStarter caller,
        IRiskLimitsStore limitsStore,
        IDerivativesInstrumentService derivatives,
        IHostMemory memory,
        IMarketSessionService? sessions,
        CancellationToken cancellationToken)
    {
        var strategy = await catalog.FindAsync(strategyId, cancellationToken);
        if (strategy is null) return StrategyStartOutcome.Refused(StrategyStartKind.NotFound, $"Strategy {strategyId} not found.");

        if (!string.IsNullOrWhiteSpace(strategy.Error))
            return StrategyStartOutcome.Refused(StrategyStartKind.BadRequest, $"{strategy.Name} cannot be started: {strategy.Error}");

        if (request is null || string.IsNullOrWhiteSpace(request.Underlying))
            return StrategyStartOutcome.Refused(StrategyStartKind.BadRequest, "underlying is required — pick the index or stock the strategy should trade.");

        var underlying = request.Underlying.Trim().ToUpperInvariant();

        // Whose account this run belongs to. Normally the caller's; an admin
        // may name another trader, which is how the morning job deploys into
        // every account without holding anyone's password.
        long callerId = caller.UserId;
        long userId = callerId;
        string ownerName = caller.UserName ?? "unknown";
        if (request.OwnerUserId is long owner && owner != callerId)
        {
            if (!caller.IsAdmin)
                return StrategyStartOutcome.Refused(StrategyStartKind.Forbidden, "Only an admin can start a run in another trader's account.");

            var target = await db.AppUsers.FirstOrDefaultAsync(u => u.Id == owner, cancellationToken);
            if (target is null) return StrategyStartOutcome.Refused(StrategyStartKind.NotFound, $"There is no user {owner}.");
            if (!target.IsActive) return StrategyStartOutcome.Refused(StrategyStartKind.BadRequest, $"{target.UserName}'s account is disabled.");

            userId = target.Id;
            ownerName = target.UserName;
        }

        int lots = request.Lots ?? Math.Max(1, strategy.DefaultLots);
        if (lots < 1) return StrategyStartOutcome.Refused(StrategyStartKind.BadRequest, "lots must be at least 1.");

        if (request.StopLoss.HasValue && request.StopLoss.Value <= 0)
            return StrategyStartOutcome.Refused(StrategyStartKind.BadRequest, "stopLoss must be a positive rupee amount, or omitted.");

        if (request.Target.HasValue && request.Target.Value <= 0)
            return StrategyStartOutcome.Refused(StrategyStartKind.BadRequest, "target must be a positive rupee amount, or omitted.");

        if (!RiskRulesDto.TryValidate(request.Risk, out var riskError))
            return StrategyStartOutcome.Refused(StrategyStartKind.BadRequest, $"risk: {riskError}");

        var risk = RunRiskRules.Resolve(request.Risk, request.StopLoss, request.Target);

        decimal initialCapital = request.InitialCapital ?? DefaultInitialCapital;
        if (initialCapital <= 0) return StrategyStartOutcome.Refused(StrategyStartKind.BadRequest, "initialCapital must be positive.");

        if (!await HasFutureOptionContractsAsync(underlying, cancellationToken))
            return StrategyStartOutcome.Refused(StrategyStartKind.BadRequest, $"No option contracts loaded for {underlying} — import the F&O master first.");

        if (strategy.SupportedUnderlyings.Count > 0
            && !strategy.SupportedUnderlyings.Contains(underlying, StringComparer.OrdinalIgnoreCase))
        {
            logger.LogWarning("{Strategy} started on {Underlying}, which is not in its supported list ({Supported}).",
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
            spotSymbol = await derivatives.GetNearestFutureSymbolAsync(underlying, cancellationToken) ?? string.Empty;

            if (string.IsNullOrWhiteSpace(spotSymbol))
            {
                return StrategyStartOutcome.Refused(StrategyStartKind.BadRequest,
                    $"{underlying} has no unexpired futures contract in the instrument master, "
                    + "so there is nothing to price it against. Import the master and try again.");
            }
        }

        // A live run trades a session that has not ended yet today; before the open
        // it waits for it. Started after the close, at a weekend or on a holiday it
        // had only stale quotes and old bars to trade, and on 11-12 Sep such runs
        // booked trades timed before they began. A recap replays a past session on
        // purpose and may start any time (RecapRuns).
        string parametersJson = LiveRunParameters.Merge(strategy.DefaultParametersJson, request.Parameters, lots, risk, underlying);
        if (sessions is not null && !RecapClock.IsRecap(parametersJson)
            && WhyMarketClosed(sessions, DateTime.UtcNow, underlying, spotSymbol) is string closed)
        {
            return StrategyStartOutcome.Refused(StrategyStartKind.Conflict, closed);
        }

        var command = PrepareRunner(strategy, out var commandError);
        if (command is null) return commandError!;

        // Who pressed the button, which is not always whose account it is.
        var startedBy = caller.UserName ?? "unknown";

        // From the duplicate checks to the registered runner, one start at a
        // time. Without it two overlapping starts (a double click, the morning
        // job racing the console) could both pass the checks and both launch.
        await registry.StartGate.WaitAsync(cancellationToken);
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
                ParametersJson = parametersJson,
                InitialCapital = initialCapital,
                CreatedUtc = now,
                StartedUtc = now
            };

            await db.SimulationRuns.AddAsync(run, CancellationToken.None);
            await db.SaveChangesAsync(CancellationToken.None);

            await EnsureSpotOnWatchlistAsync(spotSymbol, CancellationToken.None);

            var launch = new LaunchSpec(strategy, run.Id, userId, startedBy, underlying, spotSymbol, lots, risk);
            var (error, running) = LaunchRunner(launch, command);
            if (error is not null || running is null)
            {
                run.Status = "Failed";
                run.LastError = "Runner failed to start.";
                run.CompletedUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
                runControl.PublishRunEvent(run.Id, userId, $"Failed: the runner of {strategy.Name} on {underlying} did not start");

                var failed = error ?? StrategyStartOutcome.Refused(StrategyStartKind.ServerError, "Failed to start the runner.");
                return failed with
                {
                    RunId = run.Id, Strategy = strategy.Name, Underlying = underlying, Lots = lots, OwnerUserId = userId, OwnerName = ownerName,
                    LaunchFailed = true,
                };
            }

            // Durable pid so a restarted API can adopt (or stop) this runner.
            await runControl.RecordRunnerPidAsync(running.RunId, running.ProcessId, startedBy);
            runControl.PublishRunEvent(run.Id, userId, $"Started {strategy.Name} on {underlying}, {lots} lot(s)");

            return new StrategyStartOutcome(StrategyStartKind.Started, $"Started {strategy.Name} on {underlying} (paper).", run.Id, running,
                strategy.Name, underlying, lots, userId, ownerName);
        }
        finally
        {
            registry.StartGate.Release();
        }
    }

    /// <summary>
    /// Why this start must not happen, or null. Called under
    /// <see cref="StrategyProcessRegistry.StartGate"/>, so nothing it counts
    /// can change before the run is launched.
    /// </summary>
    private async Task<StrategyStartOutcome?> RefuseStartAsync(
        StrategyCatalogEntry strategy,
        string underlying,
        long userId,
        string ownerName,
        int lots,
        IRiskLimitsStore limitsStore,
        IHostMemory memory,
        CancellationToken cancellationToken)
    {
        // The same strategy may run on several underlyings at once, and two
        // traders may each run it on the same one — their books are separate.
        // Only the same strategy, on the same underlying, in the SAME account
        // is refused: that would double a position by accident.
        if (registry.Find(strategy.Id, underlying, userId) is not null)
            return StrategyStartOutcome.Refused(StrategyStartKind.Conflict, AlreadyRunningMessage(strategy.Name, underlying, ownerName));

        // The registry is not the whole truth: a run the startup reconcile
        // could neither adopt nor close is still open in the database with no
        // entry here, and a second runner beside it would trade the same book.
        var open = await FindOpenRunAsync(strategy.Name, underlying, userId, cancellationToken);
        if (open is { } row)
        {
            return StrategyStartOutcome.Refused(StrategyStartKind.Conflict,
                $"{strategy.Name} run #{row.RunId} on {underlying} is still {row.Status} in {ownerName}'s account "
                + "with no runner the API knows of — stop it from its run page before starting another.",
                row.RunId);
        }

        // The limit is per trader: one account filling the desk must not stop
        // another account from opening its first run of the day.
        var riskLimits = limitsStore.GetLimits();
        if (RunCap.Blocks(riskLimits.MaxConcurrentRuns, registry.CountFor(userId)))
            return StrategyStartOutcome.Refused(StrategyStartKind.TooMany, $"Concurrent strategy limit reached ({riskLimits.MaxConcurrentRuns}).");

        // The box's own ceiling, whoever's runs they are.
        if (registry.Count >= _options.MaxConcurrentProcesses)
        {
            return StrategyStartOutcome.Refused(StrategyStartKind.TooMany,
                $"The desk already runs {registry.Count} strategy runners, its limit "
                + $"(StrategyRunner:MaxConcurrentProcesses = {_options.MaxConcurrentProcesses}). Stop one first.");
        }

        // A null reading is "cannot tell" (not Linux), never "none left".
        long floor = (long)_options.MinAvailableMemoryMb * 1024 * 1024;
        if (memory.AvailableBytes() is long available && available < floor)
        {
            return StrategyStartOutcome.Refused(StrategyStartKind.TooMany,
                $"The server has only {available / (1024 * 1024)} MB of memory available; a new runner needs "
                + $"{_options.MinAvailableMemoryMb} MB free to start without starving the ones already running. "
                + "Stop a run first.");
        }

        // What this trader may run — strategy, underlying, lots, mode and how
        // many runs they already have open. Filtering the list they see is a
        // courtesy; this is what actually stops anything.
        int openRuns = await db.SimulationRuns
            .CountAsync(x => x.UserId == userId && (x.Status == "Running" || x.Status == "Stopping"), cancellationToken);
        var decision = await strategyAccess.CanDeployAsync(userId, strategy.Name, underlying, lots, LivePaperMode, openRuns, cancellationToken);
        if (!decision.Allowed) return StrategyStartOutcome.Refused(StrategyStartKind.Forbidden, decision.Reason ?? "Not allowed.");

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
        var rows = await db.SimulationRuns.AsNoTracking()
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

    public static string AlreadyRunningMessage(string strategyName, string underlying, string? account = null)
        => account is null
            ? $"{strategyName} is already running on {underlying} — stop that run or pick another underlying."
            : $"{strategyName} is already running on {underlying} in {account}'s account — stop that run or pick another underlying.";

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
    private RunnerCommand? PrepareRunner(StrategyCatalogEntry strategy, out StrategyStartOutcome? error)
    {
        error = null;
        var scriptPath = engine.ScriptPath("strategies", "execution_runner.py");
        if (!File.Exists(scriptPath))
        {
            logger.LogError("Strategy runner not found at {ScriptPath}", scriptPath);
            error = StrategyStartOutcome.Refused(StrategyStartKind.ServerError,
                $"Strategy runner not found at '{scriptPath}'. Set StrategyRunner:EngineDirectory.");
            return null;
        }

        // The strategy name reaches a command line, so allow only characters that
        // appear in a legitimate strategy identifier.
        var strategyArgument = new string(strategy.Name.Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrEmpty(strategyArgument))
        {
            error = StrategyStartOutcome.Refused(StrategyStartKind.BadRequest, $"Strategy name '{strategy.Name}' contains no usable characters.");
            return null;
        }

        return new RunnerCommand(scriptPath, strategyArgument);
    }

    /// <summary>
    /// Spawns execution_runner.py and registers it. Returns an error instead
    /// of throwing so the caller can close its run row. The duplicate and
    /// capacity checks are the caller's, made under
    /// <see cref="StrategyProcessRegistry.StartGate"/>; this only launches.
    /// </summary>
    private (StrategyStartOutcome? Error, RunningStrategy? Running) LaunchRunner(LaunchSpec spec, RunnerCommand command)
    {
        int id = spec.Strategy.Id;
        var engineDirectory = engine.EngineDirectory;

        var processInfo = new ProcessStartInfo
        {
            FileName = engine.PythonExecutable,
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
                return (StrategyStartOutcome.Refused(StrategyStartKind.ServerError, "Failed to start python process."), null);
            }

            var running = new RunningStrategy(
                id, spec.Strategy.Name, process, spec.StartedBy, spec.UserId, DateTime.UtcNow,
                spec.RunId, spec.Underlying, spec.SpotSymbol, spec.Lots, spec.Risk)
            {
                // Where the runner writes its own output from its first line;
                // the console is read from there (core/safe_output.py).
                OutputLogPath = RunnerOutputLog.PathFor(engine.EngineLogDirectory, spec.RunId, process.Id)
            };

            if (!registry.TryAdd(running))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                process.Dispose();
                return (StrategyStartOutcome.Refused(StrategyStartKind.Conflict, $"Run {spec.RunId} already has a runner behind it."), null);
            }

            logger.LogInformation(
                "Started strategy {StrategyId} ({Name}) pid {Pid} run {RunId} on {Underlying} ({Spot}) x{Lots} risk=[{Risk}] by {User}",
                id, spec.Strategy.Name, running.ProcessId, spec.RunId, spec.Underlying, spec.SpotSymbol, spec.Lots,
                spec.Risk.Describe(), spec.StartedBy);

            return (null, running);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start strategy {StrategyId}.", id);
            try { process?.Dispose(); } catch { /* ignore */ }
            return (StrategyStartOutcome.Refused(StrategyStartKind.ServerError, ex.Message), null);
        }
    }

    /// <summary>True when the underlying has at least one unexpired CE/PE contract in the master.</summary>
    private Task<bool> HasFutureOptionContractsAsync(string underlying, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return db.Instruments
            .AsNoTracking()
            .AnyAsync(x => x.IsEnabled
                        && x.Underlying == underlying
                        && (x.OptionType == "CE" || x.OptionType == "PE")
                        && x.ExpiryDate.HasValue
                        && x.ExpiryDate >= today,
                cancellationToken);
    }

    /// <summary>
    /// The runner reads spot ticks for the underlying from the live feed, so the
    /// spot symbol must be on the watchlist. Failure here is not fatal — the
    /// runner warns on its own when ticks never arrive.
    /// </summary>
    private async Task EnsureSpotOnWatchlistAsync(string spotSymbol, CancellationToken cancellationToken)
    {
        try
        {
            await upsertWatchlistItem.ExecuteAsync(new UpsertWatchlistItemRequest
            {
                Symbol = spotSymbol,
                DataType = "symbolUpdate",
                IsActive = true,
                Priority = 100
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not add {Symbol} to the live watchlist.", spotSymbol);
        }
    }
}
