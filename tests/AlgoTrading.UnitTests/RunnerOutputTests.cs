using System.Diagnostics;
using System.Text;
using AlgoTrading.Api.Services;
using AlgoTrading.Contracts.Strategies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// A run's console, read from the log the runner keeps of itself.
/// </summary>
/// <remarks>
/// On 24 Sep all 13 live runs were adopted after an API restart. An adopted
/// runner has no pipes, so the console showed only the lines the API wrote
/// itself; the runners' warnings and "SIGNAL REFUSED" lines were nowhere, and
/// an exit read "exit code unknown". From 28 Sep a runner writes every line to
/// logs/engine/runner-&lt;run&gt;-&lt;pid&gt;.log from its first one, stamped, and
/// ends with an EXIT line (core/safe_output.py); the API reads that file into
/// the console of every run, launched or adopted.
/// </remarks>
public class RunnerOutputTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("runner-output-").FullName;
    private readonly List<Process> _processes = new();

    public void Dispose()
    {
        foreach (var process in _processes)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            process.Dispose();
        }

        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ------------------------------------------------------------- the file

    [Fact]
    public void A_stamped_line_keeps_its_time_and_stream()
    {
        var line = RunnerOutputLog.Parse("2026-09-28T03:45:02.456Z ! SIGNAL REFUSED by the API: run is Stopping");

        Assert.True(line.IsStderr);
        Assert.Equal(new DateTime(2026, 9, 28, 3, 45, 2, 456, DateTimeKind.Utc), line.AtUtc);
        Assert.Equal(DateTimeKind.Utc, line.AtUtc!.Value.Kind);
        Assert.Equal("SIGNAL REFUSED by the API: run is Stopping", line.Text);
        Assert.Equal("03:45:02 ! SIGNAL REFUSED by the API: run is Stopping", RunnerOutputLog.ToConsole(line, DateTime.UtcNow));

        // A runner from before 28 Sep wrote the file only after its pipe died, unstamped.
        var old = RunnerOutputLog.Parse("[NIFTY] waiting for ticks");
        Assert.Null(old.AtUtc);
        Assert.False(old.IsStderr);
    }

    [Fact]
    public void The_EXIT_line_says_how_the_runner_ended()
    {
        Assert.Equal(new RunnerOutputLog.Exit(1, "uncaught KeyError: 'ltp'"),
            RunnerOutputLog.ParseExit("EXIT code=1 reason=uncaught KeyError: 'ltp'"));
        Assert.Equal(new RunnerOutputLog.Exit(0, "signal SIGTERM"), RunnerOutputLog.ParseExit("EXIT code=0 reason=signal SIGTERM"));
        Assert.Null(RunnerOutputLog.ParseExit("[RUNNER] EXIT code=1 reason=quoted"));
    }

    // ------------------------------------------------------------- the tail

    [Fact]
    public void The_first_read_seeds_the_last_lines_and_later_reads_take_what_was_appended()
    {
        var path = Log("runner-215-4242.log", Enumerable.Range(1, 350).Select(i => Stamped($"line {i}")));
        var seen = new List<RunnerOutputLog.Line>();
        using var tail = new RunnerLogTail(path, seen.Add, seedLines: 300);

        Assert.Equal(300, tail.Poll());
        Assert.Equal("line 51", seen[0].Text);
        Assert.Equal("line 350", seen[^1].Text);

        // A line still being written waits for its newline.
        File.AppendAllText(path, Stamped("line 351") + "\n" + Stamped("line 352", stderr: true) + "\n2026-09-28T04:00:00.000Z | half");
        Assert.Equal(2, tail.Poll());
        Assert.True(seen[^1].IsStderr);
        Assert.Equal("line 352", seen[^1].Text);

        File.AppendAllText(path, " a line\n");
        Assert.Equal(1, tail.Poll());
        Assert.Equal("half a line", seen[^1].Text);
        Assert.Equal(0, tail.Poll());
    }

    [Fact]
    public void A_file_that_is_not_there_yet_is_read_from_its_start_once_it_is()
    {
        var path = Path.Combine(_dir, "engine", "runner-216-4243.log");
        var seen = new List<RunnerOutputLog.Line>();
        using var tail = new RunnerLogTail(path, seen.Add);

        // A launched runner has not printed yet when the registry first looks.
        Assert.Equal(0, tail.Poll());

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Stamped("[CONFIG] strategy=Fulcrum run_id=216") + "\n" + Stamped("[STATUS] NIFTY spot=25010") + "\n");
        Assert.Equal(2, tail.Poll());
        Assert.Equal("[CONFIG] strategy=Fulcrum run_id=216", seen[0].Text);
    }

    [Fact]
    public void A_file_cut_short_is_read_again_from_its_start()
    {
        var path = Log("runner-217-1.log", new[] { Stamped("one"), Stamped("two") });
        var seen = new List<RunnerOutputLog.Line>();
        using var tail = new RunnerLogTail(path, seen.Add);
        tail.Poll();

        File.WriteAllText(path, Stamped("again") + "\n");
        Assert.Equal(1, tail.Poll());
        Assert.Equal("again", seen[^1].Text);
    }

    [Fact]
    public void Utf8_is_decoded_whole_lines_at_a_time()
    {
        var path = Path.Combine(_dir, "runner-218-1.log");
        var bytes = Encoding.UTF8.GetBytes(Stamped("risk ₹2,500 → group") + "\n");
        // Written in two halves that split the rupee sign's three bytes.
        int cut = Array.IndexOf(bytes, (byte)0xE2) + 1;
        File.WriteAllBytes(path, bytes[..cut]);

        var seen = new List<RunnerOutputLog.Line>();
        using var tail = new RunnerLogTail(path, seen.Add);
        Assert.Equal(0, tail.Poll());

        using (var append = new FileStream(path, FileMode.Append)) append.Write(bytes, cut, bytes.Length - cut);
        Assert.Equal(1, tail.Poll());
        Assert.Equal("risk ₹2,500 → group", seen[0].Text);
    }

    // ------------------------------------------------------ in the registry

    [Fact]
    public async Task An_adopted_runners_console_is_seeded_from_its_log_and_follows_it()
    {
        var path = Log("runner-300-1.log", new[]
        {
            Stamped("[CONFIG] strategy=Fulcrum run_id=300", at: "2026-09-28T03:46:00.000Z"),
            Stamped("SIGNAL REFUSED by the API: 409", stderr: true, at: "2026-09-28T05:10:00.000Z")
        });
        var registry = NewRegistry();
        var entry = Entry(300, Sleeper(), path, adopted: true);
        Assert.True(registry.TryAdd(entry));

        var logs = registry.GetLogs(300, 300);
        Assert.Equal("03:46:00 | [CONFIG] strategy=Fulcrum run_id=300", logs[0]);
        Assert.Equal("05:10:00 ! SIGNAL REFUSED by the API: 409", logs[1]);
        Assert.Contains(StrategyProcessRegistry.AdoptedLogLine, logs[2]);
        Assert.Contains("runner-300-1.log", logs[2]);
        Assert.Equal("SIGNAL REFUSED by the API: 409", entry.LastStderrLine);

        // Polled every second from there on.
        File.AppendAllText(path, Stamped("[STATUS] NIFTY spot=25010") + "\n");
        Assert.True(await Eventually(() => registry.GetLogs(300, 300).Any(x => x.EndsWith("| [STATUS] NIFTY spot=25010"))),
            "the appended line reached the console");
    }

    [Fact]
    public async Task A_launched_runners_console_comes_from_its_log_not_twice_from_its_pipes()
    {
        var path = Path.Combine(_dir, "runner-301-2.log");
        var registry = NewRegistry();

        // A child that prints to its pipes and writes its log, as a real runner does.
        var child = Echo("from the pipe");
        File.WriteAllText(path, Stamped("from the file") + "\n");
        Assert.True(registry.TryAdd(Entry(301, child, path, adopted: false)));

        Assert.True(await Eventually(() => registry.GetLogs(301, 300).Any(x => x.EndsWith("| from the file"))));
        await Task.Delay(500);
        Assert.DoesNotContain(registry.GetLogs(301, 300), x => x.Contains("from the pipe"));
    }

    [Fact]
    public async Task An_adopted_runner_that_exits_is_explained_by_its_EXIT_line()
    {
        // Not our child: the OS keeps its exit code from us, as with any runner
        // adopted after a restart. Windows hands over any process's code.
        if (OperatingSystem.IsWindows()) return;

        var path = Log("runner-302-3.log", new[]
        {
            Stamped("Traceback (most recent call last):", stderr: true),
            Stamped("KeyError: 'ltp'", stderr: true),
            Stamped("EXIT code=1 reason=uncaught KeyError: 'ltp'")
        });
        var registry = NewRegistry();
        Assert.True(registry.TryAdd(Entry(302, Orphan(seconds: 1), path, adopted: true)));

        Assert.True(await Eventually(() => registry.GetExitByRun(302) is not null, TimeSpan.FromSeconds(20)), "the exit was noticed");
        Assert.Equal("Runner exited (code 1): uncaught KeyError: 'ltp'", registry.GetExitByRun(302)!.Reason);
        Assert.Contains(registry.GetLogs(302, 300), x => x.EndsWith("runner exited with code 1 (from its EXIT line)"));
    }

    // -------------------------------------------------------------- helpers

    private static string Stamped(string text, bool stderr = false, string at = "2026-09-28T04:00:00.000Z")
        => $"{at} {(stderr ? '!' : '|')} {text}";

    private string Log(string name, IEnumerable<string> lines)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    private static StrategyProcessRegistry NewRegistry()
        => new(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<StrategyProcessRegistry>.Instance);

    private static RunningStrategy Entry(long runId, Process process, string logPath, bool adopted)
        => new(7, "Fulcrum", process, "admin", 1, DateTime.UtcNow, runId, "NIFTY", "NSE:NIFTY50-INDEX", 2, new RiskRulesDto())
        {
            Adopted = adopted,
            OutputLogPath = logPath
        };

    private Process Sleeper() => Start(TestSleeper.StartInfo());

    /// <summary>A child that prints one line to stdout and then stays alive.</summary>
    private Process Echo(string line)
    {
        var info = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd", $"/c echo {line} & ping -n 31 127.0.0.1 >nul")
            : new ProcessStartInfo("/bin/sh", $"-c \"echo '{line}'; sleep 30\"");
        return Start(info);
    }

    /// <summary>
    /// A live process that is not this one's child: a shell starts it in the
    /// background and exits, so it is handed to init, as a runner whose API
    /// restarted is.
    /// </summary>
    private Process Orphan(int seconds)
    {
        var shell = Start(new ProcessStartInfo("/bin/sh", $"-c \"sleep {seconds + 1} >/dev/null 2>&1 & echo $!\""));
        int pid = int.Parse(shell.StandardOutput.ReadLine()!.Trim());
        shell.WaitForExit();
        var orphan = Process.GetProcessById(pid);
        _processes.Add(orphan);
        return orphan;
    }

    private Process Start(ProcessStartInfo info)
    {
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.UseShellExecute = false;
        var process = Process.Start(info)!;
        _processes.Add(process);
        return process;
    }

    private static async Task<bool> Eventually(Func<bool> condition, TimeSpan? within = null)
    {
        var until = DateTime.UtcNow + (within ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }
        return condition();
    }
}
