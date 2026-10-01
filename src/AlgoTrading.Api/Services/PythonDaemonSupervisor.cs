// src/AlgoTrading.Api/Services/PythonDaemonSupervisor.cs
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Owns one long-running Python process on behalf of the API: launches it with
/// drained pipes, records its pid durably in system_settings, and — after an
/// API restart — finds the still-running instance by that pid so status reports
/// it and Stop can kill it.
/// </summary>
/// <remarks>
/// Written once and configured per daemon rather than copied. The platform runs
/// several of these (the tick ingestor, the option-chain poller) and every one
/// of them needs the same four things done exactly right: drain both pipes or
/// the child blocks on its next print, hold a start lock or two POSTs spawn two
/// processes, never kill a pid that cannot be verified as ours, and never drop
/// a pid record on a probe that merely failed. Those are the details a second
/// copy gets subtly wrong.
/// <para>
/// Everything that differs between daemons is in <see cref="DaemonDescriptor"/>:
/// what to launch, how to recognise it in a process list, and where its pid is
/// kept.
/// </para>
/// </remarks>
public abstract class PythonDaemonSupervisor
{
    public const string SourceManaged = "managed";
    public const string SourceAdopted = "adopted";
    public const string SourceNone = "none";

    private const int LogBufferCapacity = 500;

    private readonly PythonEngineLocator _engine;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;
    private readonly DaemonDescriptor _daemon;

    // The current marker first, then any the daemon answered to before a rename.
    private readonly string[] _markers;

    // Serializes start attempts so two overlapping POSTs cannot both spawn a
    // process (the second would be untracked and therefore unstoppable).
    private readonly object _startLock = new();
    private Process? _managed;
    private int _managedPid;

    // Recent stdout/stderr lines from the daemon. The streams MUST be
    // drained: with RedirectStandardOutput and no reader, the pipe buffer
    // fills and the python process blocks on its next print — silently
    // freezing tick capture while status still reports "running".
    private readonly ConcurrentQueue<string> _recentLogs = new();

    /// <summary>
    /// The environment variable that pins a launched daemon's log to
    /// <c>logs/engine/&lt;LogName&gt;-&lt;pid&gt;.log</c>, kept from its first
    /// line (core/safe_output.py reads it).
    /// </summary>
    public const string LogNameVariable = "ENGINE_LOG_NAME";

    // The daemon's own log file being followed into api.log, for the process
    // launched or adopted most recently. Guarded by _outputLock.
    private readonly object _outputLock = new();
    private DaemonOutput? _output;

    /// <summary>
    /// When this API process started. A line an adopted daemon wrote before
    /// then may already be in api.log from the instance before the restart;
    /// one written since cannot be.
    /// </summary>
    private static readonly DateTime ApiStartedUtc = ReadApiStart();

    protected PythonDaemonSupervisor(
        DaemonDescriptor daemon,
        PythonEngineLocator engine,
        IServiceScopeFactory scopeFactory,
        ILogger logger)
    {
        _daemon = daemon;
        _markers = new[] { daemon.ProcessMarker }.Concat(daemon.LegacyMarkers ?? Array.Empty<string>()).ToArray();
        _engine = engine;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>What this supervisor runs, how it recognises it and where its pid lives.</summary>
    public DaemonDescriptor Descriptor => _daemon;

    /// <summary>
    /// The markers a stored pid is checked against before it is adopted or
    /// killed: <see cref="DaemonDescriptor.ProcessMarker"/>, then any legacy ones.
    /// </summary>
    public IReadOnlyList<string> Markers => _markers;

    /// <summary>What a daemon is: what to run, how to recognise it, where its pid lives.</summary>
    /// <param name="Name">Used in messages and logs — "ingestor", "chain poller".</param>
    /// <param name="ScriptParts">Path to the script, relative to the engine directory.</param>
    /// <param name="ProcessMarker">
    /// A substring of the command line that identifies this daemon. Checked
    /// before anything is killed, so a recycled pid cannot be mistaken for ours.
    /// The exact rule is <see cref="ProcessProbe.NamesAnyMarker"/>.
    /// </param>
    /// <param name="PidSettingKey">Where the pid is recorded so it survives an API restart.</param>
    /// <param name="Args">
    /// Arguments passed after the script path. Each is quoted individually by
    /// ArgumentList, so a value with spaces needs no escaping here.
    /// </param>
    /// <param name="LegacyMarkers">
    /// Markers the same daemon was recognised by before its script was renamed.
    /// Only adoption reads them: a process launched under the old name before a
    /// deploy is still ours, and must stay stoppable until it is next restarted.
    /// New launches always use <paramref name="ScriptParts"/>.
    /// </param>
    /// <param name="LogName">
    /// The name of the log the daemon keeps of its own output,
    /// <c>logs/engine/&lt;LogName&gt;-&lt;pid&gt;.log</c>, or null for a script
    /// that keeps none (it does not install core/safe_output.py). With a name
    /// the API reads the daemon's output from that file into api.log, launched
    /// or adopted, and the pipes are only drained. Use the name the script has
    /// always given its fallback log, so a daemon started before this was
    /// deployed is still found once adopted.
    /// </param>
    public sealed record DaemonDescriptor(
        string Name,
        string[] ScriptParts,
        string ProcessMarker,
        string PidSettingKey,
        string[]? Args = null,
        string[]? LegacyMarkers = null,
        string? LogName = null);

    /// <summary>Capitalised for the start of a sentence.</summary>
    private string Sentence => char.ToUpperInvariant(_daemon.Name[0]) + _daemon.Name[1..];

    /// <summary>
    /// isRunning is true when this API launched the process and it is alive
    /// (managed) OR a stored pid is alive and its command line names this
    /// daemon's marker (adopted).
    /// </summary>
    public sealed record Status(bool IsRunning, bool Managed, int? ProcessId, string Source);

    public sealed record StartOutcome(bool Started, int StatusCode, string Message, int? ProcessId);

    /// <param name="ExitConfirmed">
    /// False when the process may still be alive: the kill was sent but its
    /// exit was never confirmed, or the pid could not be verified and was left
    /// alone. The pid record is kept then, and nothing may start a second
    /// instance on the strength of this stop.
    /// </param>
    public sealed record StopOutcome(bool WasRunning, string Message, int? ProcessId, string Source, bool ExitConfirmed = true);

    public async Task<Status> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var managed = ManagedAlive();
        if (managed is not null)
        {
            return new Status(true, true, managed.Value, SourceManaged);
        }

        var stored = await ReadStoredPidAsync(cancellationToken);
        if (stored is null)
        {
            return new Status(false, false, null, SourceNone);
        }

        var probe = ProcessProbe.Probe(stored.Value, _markers, null, _logger);
        if (probe.IsDead)
        {
            // Stale record from an instance that died without a clean stop.
            await ClearStoredPidAsync(stored.Value, cancellationToken);
            StopFollowing(stored.Value);
            return new Status(false, false, null, SourceNone);
        }

        if (probe.IsUnknown)
        {
            // Alive, but `ps` could not confirm it is ours (spawn
            // failure / timeout under load). The record is kept: dropping it on
            // a transient probe failure would flip the console to "external"
            // and disable Stop until the next heartbeat rewrites the pid.
            _logger.LogWarning("{Daemon} pid {Pid} is alive but could not be verified this time; keeping the stored record.", _daemon.Name, stored.Value);
            return new Status(true, false, stored.Value, SourceAdopted);
        }

        probe.Process?.Dispose();
        FollowAdopted(stored.Value);
        return new Status(true, false, stored.Value, SourceAdopted);
    }

    public Task<StartOutcome> StartAsync(CancellationToken cancellationToken = default) => StartAsync(null, cancellationToken);

    /// <summary>
    /// Starts the daemon with <paramref name="extraArgs"/> after the descriptor's own: a daemon whose
    /// run is chosen at start, such as the market replay's day and speed.
    /// </summary>
    public async Task<StartOutcome> StartAsync(IReadOnlyList<string>? extraArgs, CancellationToken cancellationToken = default)
    {
        var engineDirectory = _engine.EngineDirectory;
        var scriptPath = _engine.ScriptPath(_daemon.ScriptParts);

        if (!File.Exists(scriptPath))
        {
            return new StartOutcome(false, StatusCodes.Status500InternalServerError, $"Script not found at '{scriptPath}'.", null);
        }

        if (ManagedAlive() is { } alivePid)
        {
            return new StartOutcome(false, StatusCodes.Status400BadRequest, $"{Sentence} is already running (pid {alivePid}).", alivePid);
        }

        var stored = await ReadStoredPidAsync(cancellationToken);
        if (stored is not null)
        {
            var probe = ProcessProbe.Probe(stored.Value, _markers, null, _logger);
            if (probe.IsAlive)
            {
                probe.Process?.Dispose();
                return new StartOutcome(false, StatusCodes.Status400BadRequest,
                    $"{Sentence} already running (pid {stored.Value}, started outside this API instance).", stored.Value);
            }

            if (probe.IsUnknown)
            {
                // Never spawn a second instance on an unverified probe: the
                // stored pid is alive and may well be the feed. Ask for a retry.
                return new StartOutcome(false, StatusCodes.Status409Conflict,
                    $"A process with the stored {_daemon.Name} pid {stored.Value} is alive but could not be verified just now; retry in a moment or stop it first.", stored.Value);
            }

            await ClearStoredPidAsync(stored.Value, cancellationToken);
        }

        Process process;
        int pid;
        lock (_startLock)
        {
            if (ManagedAlive() is { } racedPid)
            {
                return new StartOutcome(false, StatusCodes.Status400BadRequest, $"{Sentence} is already running (pid {racedPid}).", racedPid);
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

            processInfo.ArgumentList.Add(scriptPath);
            foreach (var argument in (_daemon.Args ?? Array.Empty<string>()).Concat(extraArgs ?? Array.Empty<string>()))
            {
                processInfo.ArgumentList.Add(argument);
            }
            processInfo.Environment["PYTHONPATH"] = engineDirectory;
            // Line-buffered output so log lines arrive as they happen instead of in 8KB blocks.
            processInfo.Environment["PYTHONUNBUFFERED"] = "1";
            // A redirected stdout takes the locale encoding on Windows (cp1252);
            // force UTF-8 on the pipe so a non-ASCII line never trips the child.
            processInfo.Environment["PYTHONIOENCODING"] = "utf-8";
            if (_daemon.LogName is { } logName)
            {
                // The daemon keeps its own log from its first line, under the
                // name the API reads it by. See FollowOutput for why.
                processInfo.Environment[LogNameVariable] = logName;
            }

            process = new Process { StartInfo = processInfo, EnableRaisingEvents = true };

            // Set once the pid is known, before the pipes are read.
            DaemonOutput? output = null;
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null || (output is not null && output.FileHasIt())) return;
                AppendLog($"{DateTime.UtcNow:HH:mm:ss} | {e.Data}");
                _logger.LogInformation("[{Daemon}] {Line}", _daemon.Name, e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null || (output is not null && output.FileHasIt())) return;
                AppendLog($"{DateTime.UtcNow:HH:mm:ss} ! {e.Data}");
                _logger.LogWarning("[{Daemon}:err] {Line}", _daemon.Name, e.Data);
            };

            try
            {
                var launchedUtc = DateTime.UtcNow;
                if (!process.Start())
                {
                    process.Dispose();
                    return new StartOutcome(false, StatusCodes.Status500InternalServerError, "Failed to start python process.", null);
                }

                pid = process.Id;
                // A second of slack: the child stamps its lines on its own
                // clock, read a moment after this one.
                output = FollowOutput(pid, adopted: false, logFromUtc: launchedUtc.AddSeconds(-1));
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start the {Daemon}.", _daemon.Name);
                try { process.Dispose(); } catch { /* ignore */ }
                return new StartOutcome(false, StatusCodes.Status500InternalServerError, ex.Message, null);
            }

            _managed = process;
            _managedPid = pid;
            AppendLog($"{DateTime.UtcNow:HH:mm:ss} | {_daemon.Name} started (pid {pid})");
        }

        _ = Task.Run(() => MonitorExitAsync(process, pid));

        await StoreStoredPidAsync(pid, cancellationToken);
        return new StartOutcome(true, StatusCodes.Status200OK, $"{Sentence} started", pid);
    }

    /// <summary>
    /// Stops the managed process (SIGTERM, then the tree is killed) or, after
    /// an API restart, the adopted one found through its stored pid. Clears
    /// the stored pid once the exit is confirmed, and only then. Safe to call
    /// when nothing is running.
    /// </summary>
    /// <remarks>
    /// The record used to be cleared whether or not the process died. A kill
    /// that did not take then read as "not running" on the next status check,
    /// and the next start put a second instance beside the first — found in
    /// review on 27 Sep 2026, when the notifier began restarting itself on
    /// new code. Kept, the record lets status report the survivor and Start
    /// refuse; the exit monitor (managed) or the next probe (adopted) clears
    /// it once the process is really gone.
    /// </remarks>
    public async Task<StopOutcome> StopAsync(string reason, CancellationToken cancellationToken = default)
    {
        Process? managed;
        int managedPid;
        lock (_startLock)
        {
            managed = _managed;
            managedPid = _managedPid;
        }

        if (managed is not null)
        {
            bool alive;
            try { alive = !managed.HasExited; }
            catch { alive = false; }

            if (alive)
            {
                AppendLog($"{DateTime.UtcNow:HH:mm:ss} | stopping: {reason}");
                bool stopped = await TerminateAsync(managed, managedPid, _daemon.Name);
                if (!stopped)
                {
                    _logger.LogError("{Daemon} pid {Pid} did not confirm its exit; keeping its pid record.", _daemon.Name, managedPid);
                    return new StopOutcome(true, $"{Sentence} kill signalled; the process has not confirmed its exit", managedPid, SourceManaged, ExitConfirmed: false);
                }

                await ClearStoredPidAsync(managedPid, cancellationToken);
                return new StopOutcome(true, $"{Sentence} stopped", managedPid, SourceManaged);
            }
        }

        var stored = await ReadStoredPidAsync(cancellationToken);
        if (stored is null)
        {
            return new StopOutcome(false, $"{Sentence} is not running", null, SourceNone);
        }

        var probe = ProcessProbe.Probe(stored.Value, _markers, null, _logger);
        if (probe.IsDead)
        {
            await ClearStoredPidAsync(stored.Value, cancellationToken);
            StopFollowing(stored.Value);
            return new StopOutcome(false, $"{Sentence} is not running (stale pid record cleared)", null, SourceNone);
        }

        if (probe.IsUnknown)
        {
            // Killing a pid that could not be verified as ours risks a
            // recycled pid; the record stays so the next attempt can verify.
            _logger.LogWarning("Stop of {Daemon} pid {Pid} skipped: the process is alive but could not be verified ({Reason}).", _daemon.Name, stored.Value, reason);
            return new StopOutcome(false,
                $"{Sentence} pid {stored.Value} is alive but could not be verified just now; retry in a moment.",
                stored.Value, SourceAdopted, ExitConfirmed: false);
        }

        using var handle = probe.Process!;

        AppendLog($"{DateTime.UtcNow:HH:mm:ss} | stopping adopted {_daemon.Name} pid {stored.Value}: {reason}");
        _logger.LogWarning("Stopping adopted {Daemon} pid {Pid} ({Reason}).", _daemon.Name, stored.Value, reason);
        bool exited = await TerminateAsync(handle, stored.Value, $"{_daemon.Name} (adopted)");
        if (!exited)
        {
            _logger.LogError("Adopted {Daemon} pid {Pid} did not confirm its exit; keeping its pid record.", _daemon.Name, stored.Value);
            return new StopOutcome(true, $"{Sentence} kill signalled; the process has not confirmed its exit", stored.Value, SourceAdopted, ExitConfirmed: false);
        }

        await ClearStoredPidAsync(stored.Value, cancellationToken);
        StopFollowing(stored.Value);
        return new StopOutcome(true, $"{Sentence} stopped (adopted process)", stored.Value, SourceAdopted);
    }

    /// <summary>
    /// Ends <paramref name="process"/> through <see cref="ProcessTerminator"/>;
    /// true once its exit is confirmed.
    /// </summary>
    /// <remarks>
    /// Virtual for one reason: a test has to play a process that will not die,
    /// and no real process can be made to survive SIGKILL on demand.
    /// </remarks>
    protected virtual Task<bool> TerminateAsync(Process process, int pid, string label)
        => ProcessTerminator.StopAsync(process, pid, AppendLog, _logger, label);

    /// <summary>Recent stdout/stderr — the place to look when a start flips straight back to stopped.</summary>
    public IReadOnlyList<string> GetLogs(int take)
    {
        var lines = _recentLogs.ToArray();
        return lines.Skip(Math.Max(0, lines.Length - Math.Clamp(take, 1, LogBufferCapacity))).ToList();
    }

    private int? ManagedAlive()
    {
        lock (_startLock)
        {
            if (_managed is null) return null;
            try
            {
                return _managed.HasExited ? null : _managedPid;
            }
            catch
            {
                return null;
            }
        }
    }

    private async Task MonitorExitAsync(Process process, int pid)
    {
        try
        {
            await process.WaitForExitAsync();
            int code;
            try { code = process.ExitCode; } catch { code = -1; }
            AppendLog($"{DateTime.UtcNow:HH:mm:ss} | {_daemon.Name} exited with code {code}");
            _logger.LogInformation("{Daemon} pid {Pid} exited with code {Code}.", _daemon.Name, pid, code);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Daemon} exit monitor failed for pid {Pid}.", _daemon.Name, pid);
        }
        finally
        {
            lock (_startLock)
            {
                if (ReferenceEquals(_managed, process))
                {
                    _managed = null;
                    _managedPid = 0;
                }
            }

            // One last read, so the daemon's final lines reach api.log.
            StopFollowing(pid);
            await ClearStoredPidAsync(pid, CancellationToken.None);
            try { process.Dispose(); } catch { /* already gone */ }
        }
    }

    // ------------------------------------------------------------------
    // The daemon's own log file
    // ------------------------------------------------------------------

    /// <summary>Where a daemon with <see cref="DaemonDescriptor.LogName"/> keeps its output, for one pid.</summary>
    public string? LogPathFor(int pid)
        => _daemon.LogName is { } name
            ? Path.Combine(_engine.EngineLogDirectory, $"{name}-{pid.ToString(System.Globalization.CultureInfo.InvariantCulture)}.log")
            : null;

    /// <summary>The file being followed into api.log now, if any.</summary>
    public string? FollowedLogPath
    {
        get
        {
            lock (_outputLock) return _output?.Tail.Path;
        }
    }

    /// <summary>
    /// Follows the daemon's own log file into api.log and the recent-lines
    /// buffer (<see cref="GetLogs"/>), replacing whatever was followed before.
    /// Null when the daemon keeps no file.
    /// </summary>
    /// <remarks>
    /// The pipes of a launched daemon belong to the API that launched it. On
    /// 28 Sep the API was restarted twice with the feed running; the feed was
    /// adopted by pid, nothing read what it printed, and until it was next
    /// restarted its warnings were in no log at all — the Sentinel, which
    /// reads api.log, could not see them either. The file outlives the API:
    /// read from it, the same lines reach api.log before and after a restart,
    /// in the same "[name]" / "[name:err]" form the pipes wrote.
    /// <para>
    /// A line is logged when it was appended after the first read, or stamped
    /// at or after <paramref name="logFromUtc"/>: for a launch, the launch; for
    /// an adoption, this API's own start, since anything older may have been
    /// logged by the API before the restart, and the Sentinel would raise its
    /// errors again. Older lines still fill the buffer the console shows.
    /// </para>
    /// </remarks>
    private DaemonOutput? FollowOutput(int pid, bool adopted, DateTime logFromUtc)
    {
        if (LogPathFor(pid) is not { } path) return null;

        DaemonOutput output;
        DaemonOutput? previous;
        lock (_outputLock)
        {
            if (_output is { } current && current.Pid == pid) return current;
            previous = _output;
            DaemonOutput? created = null;
            var tail = new RunnerLogTail(
                path,
                (line, seeded) => OnFileLine(created!, line, seeded),
                // A launched daemon's file is new: all of it is news. An
                // adopted one's start is only history for the console.
                seedLines: adopted ? LogBufferCapacity : int.MaxValue);
            created = new DaemonOutput(pid, tail, logFromUtc);
            output = created;
            _output = output;
        }

        previous?.Tail.Stop();

        if (adopted)
        {
            AppendLog($"{DateTime.UtcNow:HH:mm:ss} | adopted {_daemon.Name} pid {pid}: output continues from {Path.GetFileName(path)}");
            _logger.LogInformation("Adopted {Daemon} pid {Pid}: its output is read from {Path}.", _daemon.Name, pid, path);
            output.Tail.Poll();
        }

        output.Tail.Start(StrategyProcessRegistry.OutputPollInterval);
        return output;
    }

    /// <summary>An adopted daemon, verified as ours: follow its file unless already doing so.</summary>
    private void FollowAdopted(int pid)
    {
        lock (_outputLock)
        {
            if (_output is { } current && current.Pid == pid) return;
        }

        FollowOutput(pid, adopted: true, logFromUtc: ApiStartedUtc);
    }

    /// <summary>Stops following <paramref name="pid"/>'s file after one last read. A newer process's file is left alone.</summary>
    private void StopFollowing(int pid)
    {
        DaemonOutput? output;
        lock (_outputLock)
        {
            output = _output;
            if (output is null || output.Pid != pid) return;
            _output = null;
        }

        output.Tail.Stop();
    }

    private void OnFileLine(DaemonOutput output, RunnerOutputLog.Line line, bool seeded)
    {
        AppendLog(RunnerOutputLog.ToConsole(line, DateTime.UtcNow));

        bool news = !seeded || (line.AtUtc is { } at && at >= output.LogFromUtc);
        if (!news) return;

        if (line.IsStderr)
        {
            _logger.LogWarning("[{Daemon}:err] {Line}", _daemon.Name, line.Text);
        }
        else
        {
            _logger.LogInformation("[{Daemon}] {Line}", _daemon.Name, line.Text);
        }
    }

    private static DateTime ReadApiStart()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            return self.StartTime.ToUniversalTime();
        }
        catch
        {
            return DateTime.UtcNow;
        }
    }

    /// <summary>One daemon process's log file and how it is being read.</summary>
    private sealed class DaemonOutput
    {
        private volatile bool _fileSeen;

        public DaemonOutput(int pid, RunnerLogTail tail, DateTime logFromUtc)
        {
            Pid = pid;
            Tail = tail;
            LogFromUtc = logFromUtc;
        }

        public int Pid { get; }
        public RunnerLogTail Tail { get; }
        public DateTime LogFromUtc { get; }

        /// <summary>
        /// True once the file exists: every line the pipe carries from then on
        /// is in it too (the tee writes the file before the pipe), so the pipe
        /// is only drained and nothing is logged twice. Until then the pipe is
        /// the only copy — an import error before core/safe_output.py was
        /// installed, or a file that could not be opened — and it is logged
        /// as before.
        /// </summary>
        public bool FileHasIt()
        {
            if (_fileSeen) return true;
            if (!File.Exists(Tail.Path)) return false;
            _fileSeen = true;
            return true;
        }
    }

    private void AppendLog(string line)
    {
        _recentLogs.Enqueue(line);
        while (_recentLogs.Count > LogBufferCapacity && _recentLogs.TryDequeue(out _)) { }
    }

    // ------------------------------------------------------------------
    // Durable pid record
    // ------------------------------------------------------------------

    private async Task<int?> ReadStoredPidAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IProcessSettingsStore>();
            return await store.GetPidAsync(_daemon.PidSettingKey, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the stored {Daemon} pid.", _daemon.Name);
            return null;
        }
    }

    private async Task StoreStoredPidAsync(int pid, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IProcessSettingsStore>();
            await store.SetPidAsync(_daemon.PidSettingKey, pid, "api", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist the {Daemon} pid {Pid}.", _daemon.Name, pid);
        }
    }

    /// <summary>Clears the record only when it still names <paramref name="pid"/>, so a newer instance's pid is never dropped.</summary>
    private async Task ClearStoredPidAsync(int pid, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IProcessSettingsStore>();
            await store.DeleteIfPidAsync(_daemon.PidSettingKey, pid, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clear the stored {Daemon} pid {Pid}.", _daemon.Name, pid);
        }
    }
}
