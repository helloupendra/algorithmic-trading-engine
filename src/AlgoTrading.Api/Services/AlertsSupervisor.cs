// src/AlgoTrading.Api/Services/AlertsSupervisor.cs
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Runs the LogicEngine alerter — one execution_runner per chain underlying,
/// Telegram alerts only, never a position.
/// </summary>
/// <remarks>
/// The supervisor owns the run row each alerter reports into. It used to let
/// the runner create its own run, which stayed Pending forever: the runner's
/// pid registration was refused (a Pending run is not "open"), nothing ever
/// closed the row, and the three processes outlived the session until the
/// next reboot. Now the row is created Running here, the runner is given its
/// id, and the row is closed by whatever ends the process — a Stop, an exit,
/// or the 15:30 market close.
/// </remarks>
public sealed class AlertsSupervisor
{
    public const string SourceManaged = "managed";
    public const string StrategyName = "LogicEngine";
    /// <summary>What the history page shows instead of lots for an alerter run.</summary>
    public const string RoleAlerts = "alerts";
    public const string SourceAdopted = "adopted";
    public const string SourceNone = "none";

    private const int LogBufferCapacity = 100;

    private readonly PythonEngineLocator _engine;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AlertsSupervisor> _logger;

    private readonly object _startLock = new();

    private readonly ConcurrentDictionary<string, Process> _managed = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _managedPids = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _recentLogs = new(StringComparer.OrdinalIgnoreCase);
    // The run row each managed alerter reports into (underlying -> run id).
    private readonly ConcurrentDictionary<string, long> _runIds = new(StringComparer.OrdinalIgnoreCase);
    // One close at a time: a Stop and the exit it causes reach CloseRunAsync
    // within the same millisecond, and both would find the run still open.
    private readonly SemaphoreSlim _closeGate = new(1, 1);

    public AlertsSupervisor(PythonEngineLocator engine, IServiceScopeFactory scopeFactory, ILogger<AlertsSupervisor> logger)
    {
        _engine = engine;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public sealed record ProcessStatus(string Underlying, int? ProcessId, string Source, DateTime? StartedUtc);
    public sealed record Status(bool IsRunning, bool Managed, IReadOnlyList<ProcessStatus> Processes);

    public sealed record StartOutcome(bool Started, int StatusCode, string Message, IReadOnlyList<string> FailedTargets);
    public sealed record StopOutcome(bool WasRunning, string Message);

    public async Task<Status> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var targets = await GetTargetsAsync(cancellationToken);
        var processes = new List<ProcessStatus>();
        bool anyManaged = false;
        bool anyRunning = false;

        foreach (var target in targets)
        {
            var underlying = target.Underlying;
            var managedPid = ManagedAlive(underlying);

            if (managedPid is not null)
            {
                processes.Add(new ProcessStatus(underlying, managedPid.Value, SourceManaged, null));
                anyManaged = true;
                anyRunning = true;
                continue;
            }

            var stored = await ReadStoredPidAsync(underlying, cancellationToken);
            if (stored is null)
            {
                processes.Add(new ProcessStatus(underlying, null, SourceNone, null));
                continue;
            }

            var probe = ProcessProbe.Probe(stored.Value, ProcessProbe.StrategyRunnerMarker, null, _logger);
            if (probe.IsDead)
            {
                await ClearStoredPidAsync(underlying, stored.Value, cancellationToken);
                processes.Add(new ProcessStatus(underlying, null, SourceNone, null));
                continue;
            }

            probe.Process?.Dispose();
            processes.Add(new ProcessStatus(underlying, stored.Value, SourceAdopted, null));
            anyRunning = true;
        }

        return new Status(anyRunning, anyManaged, processes);
    }

    public async Task<StartOutcome> StartAsync(long userId, CancellationToken cancellationToken = default)
    {
        var targets = await GetTargetsAsync(cancellationToken);
        if (targets.Count == 0)
        {
            return new StartOutcome(false, StatusCodes.Status400BadRequest, "No alert targets configured.", Array.Empty<string>());
        }

        var engineDirectory = _engine.EngineDirectory;
        var scriptPath = _engine.ScriptPath("strategies", "execution_runner.py");

        if (!File.Exists(scriptPath))
        {
            return new StartOutcome(false, StatusCodes.Status500InternalServerError, $"Script not found at '{scriptPath}'.", targets.Select(t => t.Underlying).ToArray());
        }

        var failedTargets = new List<string>();
        bool anyStarted = false;

        lock (_startLock)
        {
            foreach (var target in targets)
            {
                var underlying = target.Underlying;
                if (ManagedAlive(underlying) is not null)
                {
                    continue; // Already running
                }

                long runId;
                try
                {
                    runId = CreateRunRowAsync(userId, underlying, target.Spot).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Could not create the run row for alerter {Underlying}.", underlying);
                    failedTargets.Add(underlying);
                    continue;
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
                processInfo.ArgumentList.Add("--strategy");
                processInfo.ArgumentList.Add("LogicEngine");
                processInfo.ArgumentList.Add("--strategy-id");
                processInfo.ArgumentList.Add(StrategyCatalogService.StableId("LogicEngine").ToString());
                processInfo.ArgumentList.Add("--user-id");
                processInfo.ArgumentList.Add(userId.ToString());
                processInfo.ArgumentList.Add("--run-id");
                processInfo.ArgumentList.Add(runId.ToString());
                processInfo.ArgumentList.Add("--underlying");
                processInfo.ArgumentList.Add(underlying);
                processInfo.ArgumentList.Add("--spot-symbol");
                processInfo.ArgumentList.Add(target.Spot);
                processInfo.ArgumentList.Add("--metrics-port");
                processInfo.ArgumentList.Add("0");

                processInfo.Environment["PYTHONPATH"] = engineDirectory;
                processInfo.Environment["PYTHONUNBUFFERED"] = "1";
                processInfo.Environment["PYTHONIOENCODING"] = "utf-8";

                Process process = new Process { StartInfo = processInfo, EnableRaisingEvents = true };

                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data is null) return;
                    AppendLog(underlying, $"{DateTime.UtcNow:HH:mm:ss} | {e.Data}");
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data is null) return;
                    AppendLog(underlying, $"{DateTime.UtcNow:HH:mm:ss} ! {e.Data}");
                };

                try
                {
                    if (!process.Start())
                    {
                        process.Dispose();
                        failedTargets.Add(underlying);
                        CloseRunAsync(underlying, runId, "Alerter process did not start", "api").GetAwaiter().GetResult();
                        continue;
                    }

                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    int pid = process.Id;

                    _managed[underlying] = process;
                    _managedPids[underlying] = pid;
                    _runIds[underlying] = runId;
                    anyStarted = true;

                    _ = Task.Run(() => MonitorExitAsync(underlying, process, pid));
                    // Store PID safely without blocking the lock for long
                    _ = StoreStoredPidAsync(underlying, pid, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to start alerter for {Underlying}.", underlying);
                    failedTargets.Add(underlying);
                    try { process.Dispose(); } catch { }
                    CloseRunAsync(underlying, runId, $"Alerter failed to start: {ex.Message}", "api").GetAwaiter().GetResult();
                }
            }
        }

        if (failedTargets.Count > 0 && anyStarted)
        {
            return new StartOutcome(true, StatusCodes.Status207MultiStatus, "Started partially.", failedTargets);
        }
        else if (failedTargets.Count > 0 && !anyStarted)
        {
            return new StartOutcome(false, StatusCodes.Status400BadRequest, "Failed to start any targets.", failedTargets);
        }

        return new StartOutcome(true, StatusCodes.Status200OK, "Started all targets successfully.", Array.Empty<string>());
    }

    public async Task<StopOutcome> StopAsync(string reason, CancellationToken cancellationToken = default)
    {
        var targets = await GetTargetsAsync(cancellationToken);
        bool anyWasRunning = false;

        foreach (var target in targets)
        {
            var underlying = target.Underlying;

            Process? managed = null;
            int managedPid = 0;
            lock (_startLock)
            {
                if (_managed.TryGetValue(underlying, out var proc) && _managedPids.TryGetValue(underlying, out var pid))
                {
                    managed = proc;
                    managedPid = pid;
                }
            }

            if (managed is not null)
            {
                bool alive;
                try { alive = !managed.HasExited; } catch { alive = false; }

                if (alive)
                {
                    anyWasRunning = true;
                    await ProcessTerminator.StopAsync(managed, managedPid, (l) => AppendLog(underlying, l), _logger, $"alerter-{underlying}");
                    await ClearStoredPidAsync(underlying, managedPid, cancellationToken);
                }
            }
            await CloseRunAsync(underlying, null, reason, "api");

            var stored = await ReadStoredPidAsync(underlying, cancellationToken);
            if (stored is not null)
            {
                var probe = ProcessProbe.Probe(stored.Value, ProcessProbe.StrategyRunnerMarker, null, _logger);
                if (probe.IsAlive)
                {
                    anyWasRunning = true;
                    using var handle = probe.Process!;
                    await ProcessTerminator.StopAsync(handle, stored.Value, (l) => AppendLog(underlying, l), _logger, $"alerter-{underlying} (adopted)");
                }
                await ClearStoredPidAsync(underlying, stored.Value, cancellationToken);
            }
        }

        return new StopOutcome(anyWasRunning, "Stop command executed for all targets.");
    }

    public IReadOnlyList<string> GetLogs(string underlying, int take)
    {
        if (_recentLogs.TryGetValue(underlying, out var queue))
        {
            var lines = queue.ToArray();
            return lines.Skip(Math.Max(0, lines.Length - Math.Clamp(take, 1, LogBufferCapacity))).ToList();
        }
        return Array.Empty<string>();
    }

    private int? ManagedAlive(string underlying)
    {
        lock (_startLock)
        {
            if (_managed.TryGetValue(underlying, out var process) && _managedPids.TryGetValue(underlying, out var pid))
            {
                try { return process.HasExited ? null : pid; } catch { return null; }
            }
            return null;
        }
    }

    private async Task MonitorExitAsync(string underlying, Process process, int pid)
    {
        try
        {
            await process.WaitForExitAsync();
            int code;
            try { code = process.ExitCode; } catch { code = -1; }
            AppendLog(underlying, $"{DateTime.UtcNow:HH:mm:ss} | alerter exited with code {code}");
            _logger.LogInformation("Alerter {Underlying} pid {Pid} exited with code {Code}.", underlying, pid, code);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Alerter exit monitor failed for {Underlying} pid {Pid}.", underlying, pid);
        }
        finally
        {
            lock (_startLock)
            {
                if (_managed.TryGetValue(underlying, out var existingProc) && ReferenceEquals(existingProc, process))
                {
                    _managed.TryRemove(underlying, out _);
                    _managedPids.TryRemove(underlying, out _);
                }
            }
            await ClearStoredPidAsync(underlying, pid, CancellationToken.None);
            try { process.Dispose(); } catch { }
            await CloseRunAsync(underlying, null, "Alerter process exited", "alerter");
        }
    }

    // --- the run row -------------------------------------------------------

    private async Task<long> CreateRunRowAsync(long userId, string underlying, string spotSymbol)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var now = DateTime.UtcNow;
        var run = new SimulationRun
        {
            UserId = userId,
            Mode = StrategyRunControl.LivePaperMode,
            Symbol = spotSymbol,
            Resolution = "1m",
            StrategyName = StrategyName,
            Status = StrategyRunControl.RunStatusRunning,
            ParametersJson = System.Text.Json.JsonSerializer.Serialize(new { underlying, role = RoleAlerts }),
            InitialCapital = 0m,
            CreatedUtc = now,
            StartedUtc = now,
        };
        db.SimulationRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    /// <summary>
    /// Marks the alerter's run Stopped. With no id on hand (an adopted process,
    /// or a Stop after an API restart) the open LogicEngine run for the target
    /// is looked up; there is at most one per underlying.
    /// </summary>
    private async Task CloseRunAsync(string underlying, long? runId, string reason, string by)
    {
        await _closeGate.WaitAsync();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            var control = scope.ServiceProvider.GetRequiredService<StrategyRunControl>();
            if (runId is null && _runIds.TryRemove(underlying, out var known)) runId = known;
            // Only a run that is still open is closed: a Stop and the exit it
            // causes both arrive here, and the second must not write a second
            // RUN_STOPPED.
            var open = await db.SimulationRuns.AsNoTracking()
                .Where(x => x.Mode == StrategyRunControl.LivePaperMode && x.StrategyName == StrategyName
                            && (x.Status == StrategyRunControl.RunStatusRunning || x.Status == "Pending")
                            && (runId != null ? x.Id == runId.Value : x.ParametersJson.Contains($"\"underlying\":\"{underlying}\"")))
                .Select(x => x.Id).ToListAsync();
            foreach (var id in open)
                await control.RecordRunStoppedAsync(id, StrategyName, reason, by, lastError: null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not close the alerter run for {Underlying}.", underlying);
        }
        finally
        {
            _closeGate.Release();
        }
    }

    private void AppendLog(string underlying, string line)
    {
        var queue = _recentLogs.GetOrAdd(underlying, _ => new ConcurrentQueue<string>());
        queue.Enqueue(line);
        while (queue.Count > LogBufferCapacity && queue.TryDequeue(out _)) { }
    }

    private async Task<int?> ReadStoredPidAsync(string underlying, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IProcessSettingsStore>();
        return await store.GetPidAsync($"alerts.pid.{underlying}", cancellationToken);
    }

    private async Task StoreStoredPidAsync(string underlying, int pid, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IProcessSettingsStore>();
        await store.SetPidAsync($"alerts.pid.{underlying}", pid, "api", cancellationToken);
    }

    private async Task ClearStoredPidAsync(string underlying, int pid, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IProcessSettingsStore>();
        await store.DeleteIfPidAsync($"alerts.pid.{underlying}", pid, cancellationToken);
    }

    private record TargetConfig(string Underlying, string Spot);

    private async Task<IReadOnlyList<TargetConfig>> GetTargetsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IProcessSettingsStore>();
        var targetsStr = await store.GetAsync("alerts.targets", cancellationToken);

        if (!string.IsNullOrWhiteSpace(targetsStr))
        {
            var targets = new List<TargetConfig>();
            var pairs = targetsStr.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var pair in pairs)
            {
                var parts = pair.Split('|');
                if (parts.Length == 2)
                {
                    targets.Add(new TargetConfig(parts[0].Trim(), parts[1].Trim()));
                }
            }
            if (targets.Count > 0) return targets;
        }

        // Default seed targets
        return new[]
        {
            new TargetConfig("BANKNIFTY", "NSE:NIFTYBANK-INDEX"),
            new TargetConfig("NIFTY", "NSE:NIFTY50-INDEX"),
            new TargetConfig("SENSEX", "BSE:SENSEX-INDEX")
        };
    }
}
