using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using AlgoTrading.Api.Configuration;
using AlgoTrading.Api.Services;
using AlgoTrading.Application.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// A daemon's output reaches api.log from the log it keeps of itself, launched
/// or adopted.
/// </summary>
/// <remarks>
/// On 28 Sep the API was restarted twice with the feed running. The feed was
/// adopted by pid; its pipes had belonged to the API that launched it, nothing
/// read them, and everything it said until its next restart was lost — to the
/// operator and to the Sentinel, which reads api.log. The API now pins the
/// daemon's log name (ENGINE_LOG_NAME, core/safe_output.py) and follows
/// logs/engine/&lt;name&gt;-&lt;pid&gt;.log, as it does a strategy runner's.
/// </remarks>
public class DaemonOutputTests : IDisposable
{
    private const string PidKey = "test-daemon-output.pid";
    private const string LogName = "test-feed";

    private readonly string _root = Directory.CreateTempSubdirectory("daemon-output-").FullName;
    private readonly List<Process> _processes = new();
    private readonly PidStore _store = new();
    private readonly CapturingLoggerProvider _log = new();

    private string EngineDirectory => Path.Combine(_root, "src", "engine");

    // PythonEngineLocator.EngineLogDirectory: two levels above the engine.
    private string LogDirectory => Path.Combine(_root, "logs", "engine");

    public void Dispose()
    {
        foreach (var process in _processes)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            process.Dispose();
        }

        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task An_adopted_daemon_is_logged_from_its_file_and_only_lines_no_earlier_API_could_have_logged()
    {
        var feed = Sleeper();
        _store.Pids[PidKey] = feed.Id;
        Directory.CreateDirectory(LogDirectory);
        var path = Path.Combine(LogDirectory, $"{LogName}-{feed.Id}.log");
        File.WriteAllLines(path, new[]
        {
            // Before this API started: the API before the restart may have logged it.
            "2026-01-05T03:45:00.000Z ! [dhan] socket closed (1006), reconnecting",
            // Since: written while no API was reading.
            Stamped("[dhan] reconnected, 212 instruments"),
        });
        var daemon = Daemon();

        var status = await daemon.GetStatusAsync();

        Assert.Equal(PythonDaemonSupervisor.SourceAdopted, status.Source);
        Assert.Equal(path, daemon.FollowedLogPath);
        var console = daemon.GetLogs(50);
        Assert.Contains(console, l => l.EndsWith("! [dhan] socket closed (1006), reconnecting", StringComparison.Ordinal));
        Assert.Contains(console, l => l.Contains("adopted test feed pid", StringComparison.Ordinal));
        Assert.DoesNotContain(Lines(), l => l.Contains("socket closed", StringComparison.Ordinal));
        Assert.Contains("[test feed] [dhan] reconnected, 212 instruments", Lines());

        // What it writes from now on is logged as it is written, in the form
        // the pipes wrote: a daemon started before this deploy writes its
        // fallback log unstamped, and that is followed too.
        File.AppendAllText(path, Stamped("[dhan] heartbeat stale 41s", stderr: true) + "\n" + "[dhan] unstamped fallback line\n");
        await Until(() => Lines().Contains("[test feed] [dhan] unstamped fallback line"));

        var stderr = Assert.Single(_log.Entries, e => e.Message.Contains("heartbeat stale", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, stderr.Level);
        Assert.Equal("[test feed:err] [dhan] heartbeat stale 41s", stderr.Message);

        // Asking again does not follow the file a second time.
        await daemon.GetStatusAsync();
        await Task.Delay(1500);
        Assert.Single(Lines(), l => l.Contains("heartbeat stale", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_launched_daemon_is_logged_once_from_its_file_and_its_pipes_only_before_its_log_began(bool readerFallsBehind)
    {
        // On Windows a virtualenv's python.exe is a launcher that starts the
        // interpreter as a child, so the pid in the log's name is not the one
        // the API holds; the desk runs on Linux.
        if (OperatingSystem.IsWindows()) return;
        var python = RepoPython() ?? "python3";
        var engine = RealEngineDirectory();
        Assert.NotNull(engine);

        // A real daemon on the real core/safe_output.py: one line before the
        // log is installed (an import error would look like this), then
        // output the API reads only from the file. Falling behind, the API
        // reads the early line only once the file holds the later ones: what
        // CI on macOS did, when the line was dropped as a duplicate.
        Directory.CreateDirectory(EngineDirectory);
        File.WriteAllText(Path.Combine(EngineDirectory, "daemon.py"), $$"""
            import sys, time
            from pathlib import Path
            print("before safe_output", file=sys.stderr, flush=True)
            time.sleep(0.5)
            sys.path.insert(0, {{PyString(engine!)}})
            import core.safe_output as safe_output
            safe_output.ENGINE_LOG_DIR = Path({{PyString(LogDirectory)}})
            safe_output.install_safe_stdio(name="the-script-asks-for-another-name")
            print("[test] STARTING LIVE FEED", flush=True)
            print("[test] no ticks for 60s", file=sys.stderr, flush=True)
            time.sleep(1.5)
            print("[test] stopping", flush=True)
            """);
        var daemon = Daemon(python);
        if (readerFallsBehind)
        {
            daemon.ReadPipesAfter = pid => Until(() => FileHolds(daemon.LogPathFor(pid)!, "[test] no ticks for 60s"), seconds: 20);
        }

        var started = await daemon.StartAsync();
        Assert.True(started.Started, started.Message);
        var process = Process.GetProcessById(started.ProcessId!.Value);
        _processes.Add(process);

        await Until(() => Lines().Any(l => l.Contains("stopping", StringComparison.Ordinal)), seconds: 20);
        Assert.True(process.WaitForExit(10_000));
        await Task.Delay(500);

        Assert.Equal(Path.Combine(LogDirectory, $"{LogName}-{started.ProcessId}.log"), daemon.LogPathFor(started.ProcessId!.Value));
        Assert.True(File.Exists(daemon.LogPathFor(started.ProcessId!.Value)));
        var lines = Lines();
        Assert.Single(lines, l => l == "[test feed:err] before safe_output");
        Assert.Single(lines, l => l == "[test feed] [test] STARTING LIVE FEED");
        Assert.Single(lines, l => l == "[test feed:err] [test] no ticks for 60s");
        Assert.Single(lines, l => l == "[test feed] [test] stopping");
        Assert.DoesNotContain(lines, l => l.Contains('\u001e'));
        Assert.Contains(daemon.GetLogs(50), l => l.EndsWith("| [test] STARTING LIVE FEED", StringComparison.Ordinal));
        Assert.DoesNotContain(daemon.GetLogs(50), l => l.Contains('\u001e'));
    }

    [Fact]
    public void The_pipe_marker_is_the_one_safe_output_writes()
    {
        // Two copies of one string, in two languages: a difference would make
        // every launched daemon look like one from before the marker.
        var engine = RealEngineDirectory();
        Assert.NotNull(engine);
        var source = File.ReadAllText(Path.Combine(engine!, "core", "safe_output.py"));
        var match = System.Text.RegularExpressions.Regex.Match(source, "^PIPE_MARKER = \"(?<text>[^\"]*)\"\r?$",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        Assert.True(match.Success, "PIPE_MARKER not found in core/safe_output.py");
        Assert.Equal(DaemonPipes.TeeMarker, match.Groups["text"].Value.Replace("\\x1e", "\u001e", StringComparison.Ordinal));
    }

    [Fact]
    public void Feeds_and_the_chain_poller_keep_the_log_names_their_scripts_always_used()
    {
        // A feed started before the API pinned the name is found by pid only
        // if the name is the one run_feed.py gave its fallback log.
        Assert.Equal("ingestor", FeedSupervisor.Describe("fyers", "ingestor", "k").LogName);
        Assert.Equal("dhan-feed", FeedSupervisor.Describe("dhan", "Dhan feed", "k").LogName);
        Assert.Equal("truedata-feed", FeedSupervisor.Describe("truedata", "TrueData feed", "k").LogName);
    }

    // ------------------------------------------------------------ helpers

    private List<string> Lines() => _log.Entries.Select(e => e.Message).ToList();

    private static string Stamped(string text, bool stderr = false)
        => $"{DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)} {(stderr ? '!' : '|')} {text}";

    private static string PyString(string value) => "r'" + value.Replace("'", "\\'") + "'";

    private static async Task Until(Func<bool> condition, int seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "timed out waiting for the daemon's output");
            await Task.Delay(100);
        }
    }

    private static bool FileHolds(string path, string text)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Contains(text, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>A daemon recognised by "sleep", so a sleeper stands in for an adopted instance.</summary>
    private sealed class TestDaemon : PythonDaemonSupervisor
    {
        public TestDaemon(PythonEngineLocator engine, IServiceScopeFactory scopes, ILogger logger)
            : base(
                new DaemonDescriptor(
                    Name: "test feed",
                    ScriptParts: new[] { "daemon.py" },
                    ProcessMarker: OperatingSystem.IsWindows() ? "ping" : "sleep",
                    PidSettingKey: PidKey,
                    LogName: LogName),
                engine, scopes, logger)
        {
        }

        /// <summary>When set, the pipes are read only once this has finished for the launched pid.</summary>
        public Func<int, Task>? ReadPipesAfter { get; set; }

        protected override void BeginReadingPipes(Process process)
        {
            if (ReadPipesAfter is not { } wait)
            {
                base.BeginReadingPipes(process);
                return;
            }

            var pid = process.Id;
            _ = Task.Run(async () =>
            {
                await wait(pid);
                base.BeginReadingPipes(process);
            });
        }
    }

    private TestDaemon Daemon(string? python = null)
    {
        var engine = new PythonEngineLocator(
            Options.Create(new StrategyRunnerOptions
            {
                EngineDirectory = EngineDirectory,
                PythonExecutable = python ?? (OperatingSystem.IsWindows() ? "python" : "python3"),
            }),
            new FakeHostEnvironment());
        var scopes = new ServiceCollection()
            .AddSingleton<IProcessSettingsStore>(_store)
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
        return new TestDaemon(engine, scopes, _log.CreateLogger("daemon"));
    }

    private Process Sleeper()
    {
        var info = TestSleeper.StartInfo();
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.UseShellExecute = false;

        var process = Process.Start(info)!;
        _processes.Add(process);
        return process;
    }

    private static DirectoryInfo? RepoDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "AlgoTrading.PythonEngine")))
        {
            dir = dir.Parent;
        }
        return dir;
    }

    private static string? RealEngineDirectory()
        => RepoDirectory() is { } repo ? Path.Combine(repo.FullName, "src", "AlgoTrading.PythonEngine") : null;

    /// <summary>
    /// The virtualenv interpreter, looked for above the repo as well (a git
    /// worktree sits inside the checkout that holds .venv). Launched directly,
    /// so the pid the API sees is the pid in the log's name.
    /// </summary>
    private static string? RepoPython()
    {
        for (var dir = RepoDirectory(); dir is not null; dir = dir.Parent)
        {
            var candidate = OperatingSystem.IsWindows()
                ? Path.Combine(dir.FullName, ".venv", "Scripts", "python.exe")
                : Path.Combine(dir.FullName, ".venv", "bin", "python");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private sealed class PidStore : IProcessSettingsStore
    {
        public ConcurrentDictionary<string, int> Pids { get; } = new();

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(Pids.TryGetValue(key, out var pid) ? pid.ToString(CultureInfo.InvariantCulture) : null);

        public Task<int?> GetPidAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(Pids.TryGetValue(key, out var pid) ? pid : (int?)null);

        public Task SetAsync(string key, string value, string? updatedBy = null, CancellationToken cancellationToken = default)
        {
            Pids[key] = int.Parse(value, CultureInfo.InvariantCulture);
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
