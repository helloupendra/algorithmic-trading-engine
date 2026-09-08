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

    protected PythonDaemonSupervisor(
        DaemonDescriptor daemon,
        PythonEngineLocator engine,
        IServiceScopeFactory scopeFactory,
        ILogger logger)
    {
        _daemon = daemon;
        _engine = engine;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>What a daemon is: what to run, how to recognise it, where its pid lives.</summary>
    /// <param name="Name">Used in messages and logs — "ingestor", "chain poller".</param>
    /// <param name="ScriptParts">Path to the script, relative to the engine directory.</param>
    /// <param name="ProcessMarker">
    /// A substring of the command line that identifies this daemon. Checked
    /// before anything is killed, so a recycled pid cannot be mistaken for ours.
    /// </param>
    /// <param name="PidSettingKey">Where the pid is recorded so it survives an API restart.</param>
    /// <param name="Args">
    /// Arguments passed after the script path. Each is quoted individually by
    /// ArgumentList, so a value with spaces needs no escaping here.
    /// </param>
    public sealed record DaemonDescriptor(
        string Name,
        string[] ScriptParts,
        string ProcessMarker,
        string PidSettingKey,
        string[]? Args = null);

    /// <summary>Capitalised for the start of a sentence.</summary>
    private string Sentence => char.ToUpperInvariant(_daemon.Name[0]) + _daemon.Name[1..];

    /// <summary>
    /// isRunning is true when this API launched the process and it is alive
    /// (managed) OR a stored pid is alive and is a fyers_streamer (adopted).
    /// </summary>
    public sealed record Status(bool IsRunning, bool Managed, int? ProcessId, string Source);

    public sealed record StartOutcome(bool Started, int StatusCode, string Message, int? ProcessId);

    public sealed record StopOutcome(bool WasRunning, string Message, int? ProcessId, string Source);

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

        var probe = ProcessProbe.Probe(stored.Value, _daemon.ProcessMarker, null, _logger);
        if (probe.IsDead)
        {
            // Stale record from an instance that died without a clean stop.
            await ClearStoredPidAsync(stored.Value, cancellationToken);
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
        return new Status(true, false, stored.Value, SourceAdopted);
    }

    public async Task<StartOutcome> StartAsync(CancellationToken cancellationToken = default)
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
            var probe = ProcessProbe.Probe(stored.Value, _daemon.ProcessMarker, null, _logger);
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
            foreach (var argument in _daemon.Args ?? Array.Empty<string>())
            {
                processInfo.ArgumentList.Add(argument);
            }
            processInfo.Environment["PYTHONPATH"] = engineDirectory;
            // Line-buffered output so log lines arrive as they happen instead of in 8KB blocks.
            processInfo.Environment["PYTHONUNBUFFERED"] = "1";
            // A redirected stdout takes the locale encoding on Windows (cp1252);
            // force UTF-8 on the pipe so a non-ASCII line never trips the child.
            processInfo.Environment["PYTHONIOENCODING"] = "utf-8";

            process = new Process { StartInfo = processInfo, EnableRaisingEvents = true };

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                AppendLog($"{DateTime.UtcNow:HH:mm:ss} | {e.Data}");
                _logger.LogInformation("[{Daemon}] {Line}", _daemon.Name, e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                AppendLog($"{DateTime.UtcNow:HH:mm:ss} ! {e.Data}");
                _logger.LogWarning("[{Daemon}:err] {Line}", _daemon.Name, e.Data);
            };

            try
            {
                if (!process.Start())
                {
                    process.Dispose();
                    return new StartOutcome(false, StatusCodes.Status500InternalServerError, "Failed to start python process.", null);
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                pid = process.Id;
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
    /// the stored pid either way. Safe to call when nothing is running.
    /// </summary>
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
                await ProcessTerminator.StopAsync(managed, managedPid, AppendLog, _logger, _daemon.Name);
                await ClearStoredPidAsync(managedPid, cancellationToken);
                return new StopOutcome(true, $"{Sentence} stopped", managedPid, SourceManaged);
            }
        }

        var stored = await ReadStoredPidAsync(cancellationToken);
        if (stored is null)
        {
            return new StopOutcome(false, $"{Sentence} is not running", null, SourceNone);
        }

        var probe = ProcessProbe.Probe(stored.Value, _daemon.ProcessMarker, null, _logger);
        if (probe.IsDead)
        {
            await ClearStoredPidAsync(stored.Value, cancellationToken);
            return new StopOutcome(false, $"{Sentence} is not running (stale pid record cleared)", null, SourceNone);
        }

        if (probe.IsUnknown)
        {
            // Killing a pid that could not be verified as ours risks a
            // recycled pid; the record stays so the next attempt can verify.
            _logger.LogWarning("Stop of {Daemon} pid {Pid} skipped: the process is alive but could not be verified ({Reason}).", _daemon.Name, stored.Value, reason);
            return new StopOutcome(false,
                $"{Sentence} pid {stored.Value} is alive but could not be verified just now; retry in a moment.",
                stored.Value, SourceAdopted);
        }

        using var handle = probe.Process!;

        AppendLog($"{DateTime.UtcNow:HH:mm:ss} | stopping adopted {_daemon.Name} pid {stored.Value}: {reason}");
        _logger.LogWarning("Stopping adopted {Daemon} pid {Pid} ({Reason}).", _daemon.Name, stored.Value, reason);
        bool exited = await ProcessTerminator.StopAsync(handle, stored.Value, AppendLog, _logger, $"{_daemon.Name} (adopted)");
        await ClearStoredPidAsync(stored.Value, cancellationToken);

        return new StopOutcome(true, exited ? $"{Sentence} stopped (adopted process)" : $"{Sentence} kill signalled; the process has not confirmed its exit", stored.Value, SourceAdopted);
    }

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

            await ClearStoredPidAsync(pid, CancellationToken.None);
            try { process.Dispose(); } catch { /* already gone */ }
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
