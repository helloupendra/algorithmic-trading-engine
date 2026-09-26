using System.Collections.Concurrent;
using System.Diagnostics;
using AlgoTrading.Api.Configuration;
using AlgoTrading.Api.Services;
using AlgoTrading.Application.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// A stop that cannot confirm the process died must leave its pid record in
/// place, so status keeps reporting the survivor and Start refuses a second.
/// </summary>
/// <remarks>
/// The record used to be cleared whatever the kill achieved. For the notifier,
/// which the API now restarts on its own when its script changes, that meant
/// an adopted process that outlived its kill read as "not running" and the
/// next minute's check started another beside it (found in review, 27 Sep).
/// Every daemon shares this stop — the feeds, the chain poller — so the rule is
/// pinned here on the base class. No real process survives SIGKILL on demand,
/// so a subclass plays one that does.
/// </remarks>
public class DaemonStopTests : IDisposable
{
    private const string PidKey = "test-daemon.pid";

    private readonly string _engineDirectory = Directory.CreateTempSubdirectory("daemon-stop-").FullName;
    private readonly List<Process> _processes = new();
    private readonly PidStore _store = new();

    public void Dispose()
    {
        foreach (var process in _processes)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            process.Dispose();
        }

        try { Directory.Delete(_engineDirectory, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task An_adopted_process_that_does_not_confirm_its_exit_keeps_its_record_and_blocks_a_second_start()
    {
        var survivor = Sleeper();
        _store.Pids[PidKey] = survivor.Id;
        var daemon = Daemon(killTakes: false);

        var stop = await daemon.StopAsync("test");

        Assert.True(stop.WasRunning);
        Assert.False(stop.ExitConfirmed);
        Assert.Equal(survivor.Id, _store.Pids.GetValueOrDefault(PidKey));
        Assert.True((await daemon.GetStatusAsync()).IsRunning);

        var start = await daemon.StartAsync();
        Assert.False(start.Started, start.Message);
    }

    [Fact]
    public async Task An_adopted_process_that_dies_has_its_record_cleared_as_before()
    {
        var process = Sleeper();
        _store.Pids[PidKey] = process.Id;
        var daemon = Daemon(killTakes: true);

        var stop = await daemon.StopAsync("test");

        Assert.True(stop.ExitConfirmed, stop.Message);
        Assert.False(_store.Pids.ContainsKey(PidKey));
        Assert.True(process.WaitForExit(5_000));
    }

    [Fact]
    public async Task A_managed_process_that_does_not_confirm_its_exit_keeps_its_record_too()
    {
        File.WriteAllText(Path.Combine(_engineDirectory, "daemon.py"), "import time\ntime.sleep(30)\n");
        var daemon = Daemon(killTakes: false);

        var started = await daemon.StartAsync();
        Assert.True(started.Started, started.Message);
        _processes.Add(Process.GetProcessById(started.ProcessId!.Value));

        var stop = await daemon.StopAsync("test");

        Assert.False(stop.ExitConfirmed);
        Assert.Equal(started.ProcessId, _store.Pids.GetValueOrDefault(PidKey));
        Assert.False((await daemon.StartAsync()).Started);
    }

    [Fact]
    public async Task Nothing_running_is_a_confirmed_stop()
    {
        var stop = await Daemon(killTakes: true).StopAsync("test");

        Assert.False(stop.WasRunning);
        Assert.True(stop.ExitConfirmed);
    }

    // ------------------------------------------------------------ helpers

    /// <summary>A daemon recognised by "sleep", so a sleeper stands in for an adopted instance.</summary>
    private sealed class TestDaemon : PythonDaemonSupervisor
    {
        private readonly bool _killTakes;

        public TestDaemon(PythonEngineLocator engine, IServiceScopeFactory scopes, bool killTakes)
            : base(
                new DaemonDescriptor(
                    Name: "test daemon",
                    ScriptParts: new[] { "daemon.py" },
                    ProcessMarker: "sleep",
                    PidSettingKey: PidKey),
                engine, scopes, NullLogger.Instance)
        {
            _killTakes = killTakes;
        }

        protected override Task<bool> TerminateAsync(Process process, int pid, string label)
            => _killTakes ? base.TerminateAsync(process, pid, label) : Task.FromResult(false);
    }

    private TestDaemon Daemon(bool killTakes)
    {
        // Start refuses before anything else when the script is missing.
        var script = Path.Combine(_engineDirectory, "daemon.py");
        if (!File.Exists(script)) File.WriteAllText(script, string.Empty);

        var engine = new PythonEngineLocator(
            Options.Create(new StrategyRunnerOptions
            {
                EngineDirectory = _engineDirectory,
                PythonExecutable = OperatingSystem.IsWindows() ? "python" : "python3",
            }),
            new FakeHostEnvironment());
        var scopes = new ServiceCollection()
            .AddSingleton<IProcessSettingsStore>(_store)
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
        return new TestDaemon(engine, scopes, killTakes);
    }

    private Process Sleeper()
    {
        var info = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c timeout /t 30 /nobreak")
            : new ProcessStartInfo("/bin/sleep", "30");
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.UseShellExecute = false;

        var process = Process.Start(info)!;
        _processes.Add(process);
        return process;
    }

    private sealed class PidStore : IProcessSettingsStore
    {
        public ConcurrentDictionary<string, int> Pids { get; } = new();

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(Pids.TryGetValue(key, out var pid) ? pid.ToString() : null);

        public Task<int?> GetPidAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(Pids.TryGetValue(key, out var pid) ? pid : (int?)null);

        public Task SetAsync(string key, string value, string? updatedBy = null, CancellationToken cancellationToken = default)
        {
            Pids[key] = int.Parse(value);
            return Task.CompletedTask;
        }

        public Task SetPidAsync(string key, int processId, string? updatedBy = null, CancellationToken cancellationToken = default)
        {
            Pids[key] = processId;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(Pids.TryRemove(key, out _));

        public Task<bool> DeleteIfPidAsync(string key, int processId, CancellationToken cancellationToken = default)
            => Task.FromResult(Pids.TryRemove(new KeyValuePair<string, int>(key, processId)));
    }

    private sealed class FakeHostEnvironment : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "AlgoTrading.Api";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Test";
    }
}
