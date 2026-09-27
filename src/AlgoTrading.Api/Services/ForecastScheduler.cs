using System.Diagnostics;
using System.Globalization;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Services;

namespace AlgoTrading.Api.Services;

/// <summary>What the forecast scheduler can do.</summary>
public enum ForecastJobKind
{
    /// <summary><c>python -m analysis issue --session &lt;date&gt;</c>, between 08:50 IST and the open.</summary>
    Issue,

    /// <summary><c>python -m analysis score</c>, after 15:50 IST or to catch up.</summary>
    Score,

    /// <summary>The issuing window passed with nothing run; say so, once.</summary>
    MissedIssue,
}

/// <summary>A job and the session it is for (for <see cref="ForecastJobKind.Score"/>, the latest session it covers).</summary>
public sealed record ForecastJob(ForecastJobKind Kind, DateOnly Session);

/// <summary>
/// When the forecast jobs are due. Pure, so every timing rule is a test.
/// </summary>
/// <remarks>
/// <para>
/// Issue runs on NSE trading days from 08:50 IST until 09:15, the open. It is
/// not made up later: the API refuses a forecast once the session has opened,
/// so a late run could only fail. A window that passes with nothing run is
/// reported instead (<see cref="ForecastJobKind.MissedIssue"/>), because a day
/// without forecasts that nobody hears about is how the record quietly gets
/// holes.
/// </para>
/// <para>
/// Score is owed whenever a trading day's 15:50 IST has passed and the last
/// scoring run covered an earlier session. That one rule is both the 15:50 run
/// and the catch-up at start-up: an API that was down at 15:50 yesterday owes
/// a run when it comes back, one that scored yesterday owes nothing until
/// today's 15:50. Scoring is safe to run late — outcomes do not change — and
/// one run scores every forecast whose session has closed.
/// </para>
/// <para>
/// Each job runs at most once per session, even across restarts, because the
/// scheduler records it before starting it (see
/// <see cref="SystemSettingKeys.ForecastsLastIssuedSession"/>). A run that
/// fails is reported, not retried: a retry loop would issue into a half-done
/// morning every half minute, and the next day's scoring picks up whatever a
/// failed one left.
/// </para>
/// </remarks>
public static class ForecastSchedule
{
    /// <summary>When the morning job runs, IST: twenty-five minutes before the open.</summary>
    public static readonly TimeSpan IssueAt = new(8, 50, 0);

    /// <summary>The open, IST. After it the API refuses the day's forecasts.</summary>
    public static readonly TimeSpan IssueUntil = new(9, 15, 0);

    /// <summary>When the evening job runs, IST: twenty minutes after the 15:30 close, so the day's candles are in.</summary>
    public static readonly TimeSpan ScoreAt = new(15, 50, 0);

    // Longer than any run of weekends and holidays an Indian exchange has had.
    private const int LookBackDays = 30;

    /// <summary>The job due now, or null. Issuing comes first: it is the one with a deadline.</summary>
    /// <param name="nowUtc">Now.</param>
    /// <param name="isTradingDay">Whether NSE trades on an IST date.</param>
    /// <param name="lastIssued">The last session the issue job was started (or reported missed) for.</param>
    /// <param name="lastScored">The latest session the score job has been started for.</param>
    /// <param name="scoreAllowed">False in the first minutes after start-up, before the API answers requests.</param>
    public static ForecastJob? Next(
        DateTime nowUtc,
        Func<DateOnly, bool> isTradingDay,
        DateOnly? lastIssued,
        DateOnly? lastScored,
        bool scoreAllowed)
    {
        var today = IstTime.DateOf(nowUtc);
        var time = IstTime.ToIst(nowUtc).TimeOfDay;

        if (time >= IssueAt && (lastIssued is null || lastIssued < today) && isTradingDay(today))
        {
            return new ForecastJob(time < IssueUntil ? ForecastJobKind.Issue : ForecastJobKind.MissedIssue, today);
        }

        if (scoreAllowed && LatestScorableSession(nowUtc, isTradingDay) is DateOnly owed && (lastScored is null || lastScored < owed))
        {
            return new ForecastJob(ForecastJobKind.Score, owed);
        }

        return null;
    }

    /// <summary>The latest trading day whose 15:50 IST has passed, looking back a month; null when none is found.</summary>
    public static DateOnly? LatestScorableSession(DateTime nowUtc, Func<DateOnly, bool> isTradingDay)
    {
        var today = IstTime.DateOf(nowUtc);
        var time = IstTime.ToIst(nowUtc).TimeOfDay;

        for (int back = time >= ScoreAt ? 0 : 1; back <= LookBackDays; back++)
        {
            var day = today.AddDays(-back);
            if (isTradingDay(day))
            {
                return day;
            }
        }

        return null;
    }

    /// <summary>
    /// The interpreter arguments for a job: <c>-m analysis issue --session yyyy-MM-dd</c>
    /// or <c>-m analysis score</c>. This is the contract with the Python CLI.
    /// </summary>
    public static IReadOnlyList<string> Arguments(ForecastJob job) => job.Kind switch
    {
        ForecastJobKind.Issue => ["-m", "analysis", "issue", "--session", Iso(job.Session)],
        ForecastJobKind.Score => ["-m", "analysis", "score"],
        _ => throw new ArgumentOutOfRangeException(nameof(job), job.Kind, "Nothing is run for this job."),
    };

    /// <summary>The job as a command line, for logs and messages.</summary>
    public static string Describe(ForecastJob job) => "python " + string.Join(' ', Arguments(job));

    internal static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>
/// Issues the day's forecasts at 08:50 IST and scores them at 15:50 IST, on
/// NSE trading days, by running the Python models (<c>python -m analysis</c>).
/// </summary>
/// <remarks>
/// <para>
/// The API runs the jobs rather than cron because the API already knows the
/// exchange calendar, the interpreter and the engine directory, and because a
/// scheduler that lives with the API is running exactly when the API that has
/// to accept the forecasts is. The timing rules are in
/// <see cref="ForecastSchedule"/>; this class only asks them every half minute
/// and acts.
/// </para>
/// <para>
/// One job at a time: the loop awaits each run before it looks again, so a
/// slow score can delay the morning's issue by its own length but never run
/// beside it. A failure — a non-zero exit, ten minutes without finishing, or
/// a morning window missed — is sent to the System channel as "Forecasts not
/// issued today" or "Forecasts not scored", because the one failure this
/// module cannot survive is a gap in the record that nobody noticed.
/// </para>
/// <para>
/// Set <c>Forecasts:SchedulerEnabled</c> to false to switch it off (a
/// development machine with its own API, for example).
/// </para>
/// </remarks>
public sealed class ForecastScheduler : BackgroundService
{
    private static readonly TimeSpan FirstLook = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    // Scoring reads the day's candles through the API; the API must be up and
    // past its start-up work first.
    private static readonly TimeSpan ScoreAfterStartup = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMarketSessionService _sessions;
    private readonly ForecastJobRunner _runner;
    private readonly ILogger<ForecastScheduler> _logger;
    private readonly bool _enabled;

    public ForecastScheduler(
        IServiceScopeFactory scopeFactory,
        IMarketSessionService sessions,
        ForecastJobRunner runner,
        ILogger<ForecastScheduler> logger,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _sessions = sessions;
        _runner = runner;
        _logger = logger;
        _enabled = !string.Equals(configuration["Forecasts:SchedulerEnabled"], "false", StringComparison.OrdinalIgnoreCase);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("ForecastScheduler is switched off (Forecasts:SchedulerEnabled=false); no forecasts will be issued or scored.");
            return;
        }

        var startedUtc = DateTime.UtcNow;
        _logger.LogInformation(
            "ForecastScheduler is starting: issue at {IssueAt} IST, score at {ScoreAt} IST on NSE trading days; catch-up scoring from {Delay} after start-up.",
            ForecastSchedule.IssueAt, ForecastSchedule.ScoreAt, ScoreAfterStartup);

        try { await Task.Delay(FirstLook, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(startedUtc, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ForecastScheduler tick failed.");
            }

            try { await Task.Delay(CheckInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(DateTime startedUtc, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IProcessSettingsStore>();

        var lastIssued = ParseDate(await store.GetAsync(SystemSettingKeys.ForecastsLastIssuedSession, ct));
        var lastScored = ParseDate(await store.GetAsync(SystemSettingKeys.ForecastsLastScoredSession, ct));

        var now = DateTime.UtcNow;
        var job = ForecastSchedule.Next(now, IsNseTradingDay, lastIssued, lastScored, scoreAllowed: now - startedUtc >= ScoreAfterStartup);
        if (job is null)
        {
            return;
        }

        // Recorded before anything runs. If the API stops halfway through the
        // job, the restart must not start the same morning again: at most once.
        string key = job.Kind == ForecastJobKind.Score
            ? SystemSettingKeys.ForecastsLastScoredSession
            : SystemSettingKeys.ForecastsLastIssuedSession;
        await store.SetAsync(key, ForecastSchedule.Iso(job.Session), nameof(ForecastScheduler), ct);

        var notifier = scope.ServiceProvider.GetRequiredService<ISystemNotifier>();

        if (job.Kind == ForecastJobKind.MissedIssue)
        {
            string at = IstTime.ToIst(now).ToString("HH:mm", CultureInfo.InvariantCulture);
            _logger.LogWarning(
                "Forecasts for {Session} were not issued: the scheduler was not running between 08:50 and 09:15 IST (it is {Now} IST). A forecast cannot be issued once the session is open.",
                job.Session, at);
            await notifier.NotifyAsync(
                NotificationCategory.System,
                NotificationSeverity.Warning,
                "Forecasts not issued today",
                $"The forecast job did not run for {ForecastSchedule.Iso(job.Session)}: the API was not running between 08:50 and 09:15 IST, and a forecast cannot be issued once the session is open. Today has no forecasts; the next are due at 08:50 IST on the next trading day.",
                cancellationToken: ct);
            return;
        }

        var result = await _runner.RunAsync(job, ct);
        if (result.Succeeded)
        {
            return;
        }

        string title = job.Kind == ForecastJobKind.Issue ? "Forecasts not issued today" : "Forecasts not scored";
        await notifier.NotifyAsync(
            NotificationCategory.System,
            NotificationSeverity.Error,
            title,
            $"{ForecastSchedule.Describe(job)} {result.Failure}." + (result.LastLine is { } line ? $" Last output: {line}" : string.Empty),
            cancellationToken: ct);
    }

    private bool IsNseTradingDay(DateOnly date)
        => _sessions.GetSessionInfo(IstTime.MiddayUtc(date), "NSE", "CM").IsTradingDay;

    private static DateOnly? ParseDate(string? value)
        => DateOnly.TryParseExact(value?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
}

/// <summary>How a forecast job ended.</summary>
/// <param name="Failure">What went wrong, as the end of a sentence ("exited with code 1"); null when it succeeded.</param>
/// <param name="LastLine">The last line it printed, secrets masked; for the notification.</param>
public sealed record ForecastRunResult(string? Failure, string? LastLine)
{
    public bool Succeeded => Failure is null;
}

/// <summary>
/// Runs one <c>python -m analysis</c> job to completion: from the engine
/// directory, with the interpreter and environment the strategy runners get,
/// its output drained into the API log, and stopped after a time limit. The
/// forecasts' issue and score jobs use it, and so does the news scorer
/// (<see cref="NewsScoringScheduler"/>).
/// </summary>
/// <remarks>
/// The environment is the runners': <c>PYTHONPATH</c> is the engine
/// directory, output is unbuffered UTF-8, and the engine reads the API's
/// address and the Service account's credentials from the repository's
/// <c>.env</c> itself (<c>core/config.py</c>, <c>core/api_client.py</c>), so
/// no secret passes through a command line or this process's memory.
/// </remarks>
public sealed class ForecastJobRunner
{
    /// <summary>Longer than any honest run: fetching a few hundred sessions and posting a dozen forecasts takes seconds.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    private const int MaxLineLength = 300;

    private readonly PythonEngineLocator _engine;
    private readonly ILogger<ForecastJobRunner> _logger;

    public ForecastJobRunner(PythonEngineLocator engine, ILogger<ForecastJobRunner> logger)
    {
        _engine = engine;
        _logger = logger;
    }

    /// <summary>The engine directory the jobs run from, where the <c>analysis</c> package lives.</summary>
    public string EngineDirectory => _engine.EngineDirectory;

    public Task<ForecastRunResult> RunAsync(ForecastJob job, CancellationToken cancellationToken)
        => RunAsync($"forecasts {job.Kind}", ForecastSchedule.Arguments(job), Timeout, lowPriority: false, cancellationToken);

    /// <summary>
    /// Runs <c>python</c> with <paramref name="arguments"/> (a <c>-m analysis</c>
    /// command) and waits for it.
    /// </summary>
    /// <param name="label">How the job is named in the log, e.g. "forecasts Issue" or "news-score".</param>
    /// <param name="timeout">How long it may run before it is stopped.</param>
    /// <param name="lowPriority">
    /// Run it below normal priority (nice 10 on Linux): for background work
    /// that must never slow down the strategies sharing the machine.
    /// </param>
    public async Task<ForecastRunResult> RunAsync(string label, IReadOnlyList<string> arguments, TimeSpan timeout, bool lowPriority, CancellationToken cancellationToken)
    {
        string command = "python " + string.Join(' ', arguments);
        string engineDirectory = _engine.EngineDirectory;

        // Said plainly rather than left to Python's "No module named analysis".
        string package = Path.Combine(engineDirectory, "analysis");
        if (!Directory.Exists(package))
        {
            _logger.LogError("Analysis job not run: {Command} needs the analysis package at {Path}, and it is not there.", command, package);
            return new ForecastRunResult($"was not run: the analysis package is not at {package}", null);
        }

        var info = new ProcessStartInfo
        {
            FileName = _engine.PythonExecutable,
            WorkingDirectory = engineDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        // As for the runners: absolute package imports from the engine
        // directory, lines as they happen, and UTF-8 on the pipe on every OS.
        info.Environment["PYTHONPATH"] = engineDirectory;
        info.Environment["PYTHONUNBUFFERED"] = "1";
        info.Environment["PYTHONIOENCODING"] = "utf-8";

        string? lastLine = null;
        void Remember(string line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                string masked = IncidentRedaction.Mask(line.Trim());
                lastLine = masked.Length > MaxLineLength ? masked[..MaxLineLength] + "…" : masked;
            }
        }

        using var process = new Process { StartInfo = info };

        // Both pipes drained, always: an undrained pipe fills and the child
        // blocks on its next print, which would read here as a timeout.
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            Remember(e.Data);
            _logger.LogInformation("[{Label}] {Line}", label, e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            Remember(e.Data);
            // Python's logging writes to stderr, so this is the log, not only errors.
            _logger.LogWarning("[{Label}:err] {Line}", label, e.Data);
        };

        var stopwatch = Stopwatch.StartNew();
        _logger.LogInformation("Analysis job starting: {Command} (in {Directory}).", command, engineDirectory);

        try
        {
            if (!process.Start())
            {
                return new ForecastRunResult("could not be started", null);
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Analysis job could not be started: {Command}.", command);
            return new ForecastRunResult($"could not be started ({ex.Message})", null);
        }

        if (lowPriority)
        {
            try
            {
                // Set a moment after the start, which is harmless: the job's
                // first second is imports, not the heavy part.
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
            {
                _logger.LogWarning("Could not lower the priority of {Command}: {Error}. It runs at normal priority.", command, ex.Message);
            }
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);

        try
        {
            // Also waits for both pipes to reach their end, so the last line is in.
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }

            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Analysis job stopped because the API is shutting down: {Command}.", command);
                throw;
            }

            _logger.LogError("Analysis job did not finish in {Minutes} minutes and was stopped: {Command}.", timeout.TotalMinutes, command);
            return new ForecastRunResult($"did not finish in {timeout.TotalMinutes:0} minutes and was stopped", lastLine);
        }

        int exitCode = process.ExitCode;
        if (exitCode == 0)
        {
            _logger.LogInformation("Analysis job finished in {Seconds:0.0} s: {Command}.", stopwatch.Elapsed.TotalSeconds, command);
            return new ForecastRunResult(null, lastLine);
        }

        _logger.LogError("Analysis job exited with code {Code} after {Seconds:0.0} s: {Command}.", exitCode, stopwatch.Elapsed.TotalSeconds, command);
        return new ForecastRunResult($"exited with code {exitCode}", lastLine);
    }
}
