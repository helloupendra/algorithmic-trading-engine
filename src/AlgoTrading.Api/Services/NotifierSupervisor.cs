// src/AlgoTrading.Api/Services/NotifierSupervisor.cs

using System.Diagnostics;
using AlgoTrading.Domain.Entities;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Owns the Telegram notifier process (scripts/telegram_notifier.py).
/// </summary>
/// <remarks>
/// The notifier began life as a sidecar started by hand, because it was added
/// while strategies were running and the backend could not be restarted. That
/// constraint is long gone, and being started by hand was its one real flaw:
/// it is the piece nobody notices is missing. Everything else stays green —
/// the API answers, the ingestor ticks, strategies trade — and the only symptom
/// is a silence that looks exactly like "nothing happened today".
///
/// So it is a supervised daemon now, started with the API by
/// <see cref="NotifierStartupService"/>. All of the behaviour is inherited;
/// this only names the process and its arguments.
/// </remarks>
public sealed class NotifierSupervisor : PythonDaemonSupervisor
{
    private readonly ILogger<NotifierSupervisor> _logger;

    public NotifierSupervisor(
        PythonEngineLocator engine,
        IServiceScopeFactory scopeFactory,
        ILogger<NotifierSupervisor> logger)
        : base(
            new DaemonDescriptor(
                Name: "notifier",
                // The notifier lives in scripts/, not in the engine, and
                // ScriptParts is engine-relative — hence the walk up to the
                // repository root. It resolves its own .env from __file__, so
                // running it from the engine directory is safe.
                ScriptParts: new[] { "..", "..", "scripts", "telegram_notifier.py" },
                ProcessMarker: ProcessProbe.NotifierMarker,
                PidSettingKey: SystemSettingKeys.NotifierPid,
                // --no-forward: this process sends to Telegram itself rather
                // than republishing events other publishers already handled.
                // The startup summary is deliberately NOT suppressed: one short
                // message per API start is the cheapest possible proof that the
                // alert path works, and its absence is the failure this class
                // exists to prevent. Add "--quiet-start" here to silence it.
                Args: new[] { "--no-forward", "--interval", "5" }),
            engine, scopeFactory, logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// True when <paramref name="status"/> is a running notifier known to BE
    /// the notifier: this API launched it, or its command line was read and
    /// names the script.
    /// </summary>
    /// <remarks>
    /// Status alone is enough for a person looking at the console, not for
    /// anything done without one. On Windows <see cref="ProcessProbe"/> cannot
    /// read a command line, so whatever process now holds the stored pid reads
    /// as the adopted notifier: stopping it automatically could kill an
    /// unrelated process tree, and counting it as a running notifier would keep
    /// the backend's start/stop messages off Telegram while nothing sends them.
    /// </remarks>
    public bool IsVerified(Status status)
        => status.IsRunning
           && status.ProcessId is int pid
           && (status.Managed || ProcessProbe.CommandLineNames(pid, Markers, _logger));
}

/// <summary>
/// Starts the notifier with the API, keeps it started, and moves it onto new
/// code when its script changes and the desk is quiet.
/// </summary>
/// <remarks>
/// A plain start-once would leave the same hole open: if the notifier dies at
/// 10am, alerts stop for the rest of the day and nothing says so. The periodic
/// check is safe to repeat because <see cref="PythonDaemonSupervisor.StartAsync"/>
/// refuses when an instance is already alive — managed or adopted from a
/// previous API process — so this never starts a second one.
/// <para>
/// Adoption had its own hole: an API restart adopts the running notifier, so
/// nothing ever started it again after a deploy. On 27 Sep the one running
/// dated from 17 Sep, and the account names added to its titles on 26 Sep had
/// never reached Telegram. So a script newer than the process means a restart.
/// </para>
/// <para>
/// But not at any hour. The desk pulls from GitHub every two minutes whatever
/// the time; only the build and the API restart wait for a quiet desk. As
/// first written (27 Sep), a push at 11:00 would have restarted the notifier
/// within a minute: alerts in the gap lost into the new baseline, that baseline
/// reading every live run's legs at once, and a script that did not load
/// replacing one that worked. So the restart waits until no strategy run is
/// live — after the MCX close, or a weekend — loads the new script before the
/// old process is stopped, and starts the new one only once the old one is
/// confirmed gone.
/// </para>
/// </remarks>
public sealed class NotifierStartupService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LoadCheckTimeout = TimeSpan.FromSeconds(60);

    // Loads the script as a module, the way the tests import it, without
    // running main(). A syntax error and an import this interpreter cannot
    // satisfy both fail here; either would otherwise crash-loop the new
    // process once a minute while the working one was already gone.
    private const string LoadCheck =
        "import importlib.util, os, sys; p = sys.argv[1]; sys.path.insert(0, os.path.dirname(p)); "
        + "s = importlib.util.spec_from_file_location('notifier_load_check', p); "
        + "m = importlib.util.module_from_spec(s); sys.modules[s.name] = m; s.loader.exec_module(m)";

    private readonly NotifierSupervisor _supervisor;
    private readonly PythonEngineLocator _engine;
    private readonly StrategyProcessRegistry _runs;
    private readonly ILogger<NotifierStartupService> _logger;

    // The script version (its write time) and verdict last logged as "not
    // yet", so a restart that waits all afternoon says so once, not every minute.
    private (DateTime Written, RestartVerdict Verdict)? _waitLogged;

    // The script version that did not load. It is not tried again until the
    // file changes.
    private DateTime? _failedToLoad;

    public NotifierStartupService(
        NotifierSupervisor supervisor,
        PythonEngineLocator engine,
        StrategyProcessRegistry runs,
        ILogger<NotifierStartupService> logger)
    {
        _supervisor = supervisor;
        _engine = engine;
        _runs = runs;
        _logger = logger;
    }

    /// <summary>What to do with a notifier that runs older code than its script.</summary>
    public enum RestartVerdict
    {
        /// <summary>Stop it and start the new code.</summary>
        Restart,

        /// <summary>Leave it: its pid is not verified as the notifier, and a stop could kill another program.</summary>
        Unverified,

        /// <summary>Leave it until no strategy run is live.</summary>
        WaitForQuietDesk,
    }

    public static RestartVerdict DecideRestart(bool verifiedNotifier, int liveRuns)
        => !verifiedNotifier ? RestartVerdict.Unverified
            : liveRuns > 0 ? RestartVerdict.WaitForQuietDesk
            : RestartVerdict.Restart;

    /// <summary>True when the script was written after the process started: it runs older code.</summary>
    public static bool RunsOlderCode(DateTime scriptWrittenUtc, DateTime processStartedUtc) =>
        scriptWrittenUtc > processStartedUtc;

    /// <summary>
    /// Loads <paramref name="scriptPath"/> under <paramref name="python"/>
    /// without running it: null when it loads, else why not (the last line
    /// Python printed, usually the exception).
    /// </summary>
    public static async Task<string?> LoadErrorAsync(string python, string engineDirectory, string scriptPath, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo
        {
            FileName = python,
            WorkingDirectory = engineDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(LoadCheck);
        info.ArgumentList.Add(scriptPath);
        // As the supervisor launches it, less the bytecode: a check leaves nothing behind.
        info.Environment["PYTHONPATH"] = engineDirectory;
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        info.Environment["PYTHONDONTWRITEBYTECODE"] = "1";

        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return $"{python} could not be run: {ex.Message}";
        }

        if (process is null) return $"{python} could not be run";

        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var error = process.StandardError.ReadToEndAsync(CancellationToken.None);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(LoadCheckTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                cancellationToken.ThrowIfCancellationRequested();
                return $"it did not finish loading within {LoadCheckTimeout.TotalSeconds:0} s";
            }

            await output;
            var stderr = await error;
            if (process.ExitCode == 0) return null;

            return stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault()
                   ?? $"{python} exited with code {process.ExitCode}";
        }
    }

    private string ScriptPath => _engine.ScriptPath(_supervisor.Descriptor.ScriptParts);

    /// <summary>The script's write time when it is newer than process <paramref name="pid"/>; otherwise null.</summary>
    private DateTime? NewerScript(int pid)
    {
        try
        {
            var written = File.GetLastWriteTimeUtc(ScriptPath);
            using var process = Process.GetProcessById(pid);
            return RunsOlderCode(written, process.StartTime.ToUniversalTime()) ? written : null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException
                                       or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // Gone, or not ours to read: the next minute's check decides.
            return null;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let a bad minute end the supervision loop.
                _logger.LogError(ex, "Could not check or start the Telegram notifier.");
            }

            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task CheckAsync(CancellationToken stoppingToken)
    {
        var status = await _supervisor.GetStatusAsync(stoppingToken);
        if (status.IsRunning && status.ProcessId is int pid && NewerScript(pid) is DateTime written)
        {
            if (!await MoveToNewCodeAsync(status, pid, written, stoppingToken))
            {
                // Still on the old code, or stopped without a confirmed exit:
                // a start now could put a second notifier beside the first.
                return;
            }

            status = await _supervisor.GetStatusAsync(stoppingToken);
        }

        if (!status.IsRunning)
        {
            var outcome = await _supervisor.StartAsync(stoppingToken);
            if (outcome.Started)
            {
                _logger.LogInformation("Telegram notifier started (pid {Pid}).", outcome.ProcessId);
            }
            else
            {
                // Not an error on its own: a racing start, or an
                // instance this API did not launch, both land here.
                _logger.LogWarning("Telegram notifier not started: {Message}", outcome.Message);
            }
        }
    }

    /// <summary>
    /// Stops a notifier that runs older code, when that is safe. True only once
    /// the old process is confirmed gone and the new code may start.
    /// </summary>
    private async Task<bool> MoveToNewCodeAsync(PythonDaemonSupervisor.Status status, int pid, DateTime written, CancellationToken stoppingToken)
    {
        int liveRuns = _runs.Count;
        var verdict = DecideRestart(_supervisor.IsVerified(status), liveRuns);
        if (verdict != RestartVerdict.Restart)
        {
            if (_waitLogged != (written, verdict))
            {
                _waitLogged = (written, verdict);
                if (verdict == RestartVerdict.WaitForQuietDesk)
                {
                    _logger.LogInformation(
                        "Telegram notifier pid {Pid} runs older code than its script; it restarts on the new code once no strategy run is live ({Runs} live now).",
                        pid, liveRuns);
                }
                else
                {
                    _logger.LogWarning(
                        "Telegram notifier pid {Pid} runs older code than its script, but the pid cannot be verified as the notifier, so it is not stopped automatically. Stop it by hand; a new one starts within a minute.",
                        pid);
                }
            }

            return false;
        }

        if (_failedToLoad == written) return false;

        var loadError = await LoadErrorAsync(_engine.PythonExecutable, _engine.EngineDirectory, ScriptPath, stoppingToken);
        if (loadError is not null)
        {
            _failedToLoad = written;
            _logger.LogError(
                "Telegram notifier: the new script does not load ({Error}); pid {Pid} keeps running the old code until the script is fixed.",
                loadError, pid);
            return false;
        }

        _logger.LogInformation("Telegram notifier pid {Pid} runs older code than its script and no run is live; restarting it.", pid);
        var stop = await _supervisor.StopAsync("its script changed; restarting it on the new code", stoppingToken);
        if (!stop.ExitConfirmed)
        {
            _logger.LogError("Telegram notifier pid {Pid} did not confirm its exit ({Message}); not starting a second one.", pid, stop.Message);
            return false;
        }

        _waitLogged = null;
        return true;
    }
}
