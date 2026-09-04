// src/AlgoTrading.Api/Services/IngestorSupervisor.cs
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Owns the live data ingestor process (fyers_streamer.py): launches it with
/// drained pipes, records its pid durably (system_settings "ingestor.pid"),
/// and — after an API restart — finds the still-running instance by that pid
/// so status reports it and Stop can kill it. Singleton; the pid store is
/// reached through a scope.
/// </summary>
public sealed class IngestorSupervisor
{
    public const string SourceManaged = "managed";
    public const string SourceAdopted = "adopted";
    public const string SourceNone = "none";

    private const int LogBufferCapacity = 500;

    private readonly PythonEngineLocator _engine;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<IngestorSupervisor> _logger;

    // Serializes start attempts so two overlapping POSTs cannot both spawn a
    // process (the second would be untracked and therefore unstoppable).
    private readonly object _startLock = new();
    private Process? _managed;
    private int _managedPid;

    // Recent stdout/stderr lines from the ingestor. The streams MUST be
    // drained: with RedirectStandardOutput and no reader, the pipe buffer
    // fills and the python process blocks on its next print — silently
    // freezing tick capture while status still reports "running".
    private readonly ConcurrentQueue<string> _recentLogs = new();

    public IngestorSupervisor(PythonEngineLocator engine, IServiceScopeFactory scopeFactory, ILogger<IngestorSupervisor> logger)
    {
        _engine = engine;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

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

        var probe = ProcessProbe.Probe(stored.Value, ProcessProbe.IngestorMarker, null, _logger);
        if (probe.IsDead)
        {
            // Stale record from an instance that died without a clean stop.
            await ClearStoredPidAsync(stored.Value, cancellationToken);
            return new Status(false, false, null, SourceNone);
        }

        if (probe.IsUnknown)
        {
            // Alive, but `ps` could not confirm it is the ingestor (spawn
            // failure / timeout under load). The record is kept: dropping it on
            // a transient probe failure would flip the console to "external"
            // and disable Stop until the next heartbeat rewrites the pid.
            _logger.LogWarning("Ingestor pid {Pid} is alive but could not be verified this time; keeping the stored record.", stored.Value);
            return new Status(true, false, stored.Value, SourceAdopted);
        }

        probe.Process?.Dispose();
        return new Status(true, false, stored.Value, SourceAdopted);
    }

    public async Task<StartOutcome> StartAsync(CancellationToken cancellationToken = default)
    {
        var engineDirectory = _engine.EngineDirectory;
        var scriptPath = _engine.ScriptPath("market_data", "live", "fyers_streamer.py");

        if (!File.Exists(scriptPath))
        {
            return new StartOutcome(false, StatusCodes.Status500InternalServerError, $"Script not found at '{scriptPath}'.", null);
        }

        if (ManagedAlive() is { } alivePid)
        {
            return new StartOutcome(false, StatusCodes.Status400BadRequest, $"Ingestor is already running (pid {alivePid}).", alivePid);
        }

        var stored = await ReadStoredPidAsync(cancellationToken);
        if (stored is not null)
        {
            var probe = ProcessProbe.Probe(stored.Value, ProcessProbe.IngestorMarker, null, _logger);
            if (probe.IsAlive)
            {
                probe.Process?.Dispose();
                return new StartOutcome(false, StatusCodes.Status400BadRequest,
                    $"Ingestor already running (pid {stored.Value}, started outside this API instance).", stored.Value);
            }

            if (probe.IsUnknown)
            {
                // Never spawn a second ingestor on an unverified probe: the
                // stored pid is alive and may well be the feed. Ask for a retry.
                return new StartOutcome(false, StatusCodes.Status409Conflict,
                    $"A process with the stored ingestor pid {stored.Value} is alive but could not be verified just now; retry in a moment or stop it first.", stored.Value);
            }

            await ClearStoredPidAsync(stored.Value, cancellationToken);
        }

        Process process;
        int pid;
        lock (_startLock)
        {
            if (ManagedAlive() is { } racedPid)
            {
                return new StartOutcome(false, StatusCodes.Status400BadRequest, $"Ingestor is already running (pid {racedPid}).", racedPid);
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
                _logger.LogInformation("[ingestor] {Line}", e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                AppendLog($"{DateTime.UtcNow:HH:mm:ss} ! {e.Data}");
                _logger.LogWarning("[ingestor:err] {Line}", e.Data);
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
                _logger.LogError(ex, "Failed to start the ingestor.");
                try { process.Dispose(); } catch { /* ignore */ }
                return new StartOutcome(false, StatusCodes.Status500InternalServerError, ex.Message, null);
            }

            _managed = process;
            _managedPid = pid;
            AppendLog($"{DateTime.UtcNow:HH:mm:ss} | ingestor started (pid {pid})");
        }

        _ = Task.Run(() => MonitorExitAsync(process, pid));

        await StoreStoredPidAsync(pid, cancellationToken);
        return new StartOutcome(true, StatusCodes.Status200OK, "Ingestor started", pid);
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
                await ProcessTerminator.StopAsync(managed, managedPid, AppendLog, _logger, "ingestor");
                await ClearStoredPidAsync(managedPid, cancellationToken);
                return new StopOutcome(true, "Ingestor stopped", managedPid, SourceManaged);
            }
        }

        var stored = await ReadStoredPidAsync(cancellationToken);
        if (stored is null)
        {
            return new StopOutcome(false, "Ingestor is not running", null, SourceNone);
        }

        var probe = ProcessProbe.Probe(stored.Value, ProcessProbe.IngestorMarker, null, _logger);
        if (probe.IsDead)
        {
            await ClearStoredPidAsync(stored.Value, cancellationToken);
            return new StopOutcome(false, "Ingestor is not running (stale pid record cleared)", null, SourceNone);
        }

        if (probe.IsUnknown)
        {
            // Killing a pid that could not be verified as the ingestor risks a
            // recycled pid; the record stays so the next attempt can verify.
            _logger.LogWarning("Stop of ingestor pid {Pid} skipped: the process is alive but could not be verified ({Reason}).", stored.Value, reason);
            return new StopOutcome(false,
                $"Ingestor pid {stored.Value} is alive but could not be verified as the feed just now; retry in a moment.",
                stored.Value, SourceAdopted);
        }

        using var handle = probe.Process!;

        AppendLog($"{DateTime.UtcNow:HH:mm:ss} | stopping adopted ingestor pid {stored.Value}: {reason}");
        _logger.LogWarning("Stopping adopted ingestor pid {Pid} ({Reason}).", stored.Value, reason);
        bool exited = await ProcessTerminator.StopAsync(handle, stored.Value, AppendLog, _logger, "ingestor (adopted)");
        await ClearStoredPidAsync(stored.Value, cancellationToken);

        return new StopOutcome(true, exited ? "Ingestor stopped (adopted process)" : "Ingestor kill signalled; the process has not confirmed its exit", stored.Value, SourceAdopted);
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
            AppendLog($"{DateTime.UtcNow:HH:mm:ss} | ingestor exited with code {code}");
            _logger.LogInformation("Ingestor pid {Pid} exited with code {Code}.", pid, code);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ingestor exit monitor failed for pid {Pid}.", pid);
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
            return await store.GetPidAsync(SystemSettingKeys.IngestorPid, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the stored ingestor pid.");
            return null;
        }
    }

    private async Task StoreStoredPidAsync(int pid, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IProcessSettingsStore>();
            await store.SetPidAsync(SystemSettingKeys.IngestorPid, pid, "api", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist the ingestor pid {Pid}.", pid);
        }
    }

    /// <summary>Clears the record only when it still names <paramref name="pid"/>, so a newer instance's pid is never dropped.</summary>
    private async Task ClearStoredPidAsync(int pid, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IProcessSettingsStore>();
            await store.DeleteIfPidAsync(SystemSettingKeys.IngestorPid, pid, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clear the stored ingestor pid {Pid}.", pid);
        }
    }
}
