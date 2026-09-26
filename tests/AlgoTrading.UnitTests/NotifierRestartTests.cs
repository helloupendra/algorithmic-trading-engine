using System.Diagnostics;
using AlgoTrading.Api.Configuration;
using AlgoTrading.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using static AlgoTrading.Api.Services.NotifierStartupService;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The notifier is adopted across API restarts, so it only moves onto new code
/// when its script is newer than the process (on 27 Sep it still ran 17 Sep's).
/// Moving it is only safe on a quiet desk, onto a script that loads, and away
/// from a pid known to be the notifier.
/// </summary>
public class NotifierRestartTests : IDisposable
{
    private static readonly DateTime Started = new(2026, 9, 17, 15, 48, 46, DateTimeKind.Utc);

    private readonly string _dir = Directory.CreateTempSubdirectory("notifier-restart-").FullName;
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

    // ------------------------------------------------------------ older code

    [Fact]
    public void A_script_changed_after_the_process_started_means_older_code()
    {
        Assert.True(RunsOlderCode(new DateTime(2026, 9, 26, 20, 0, 0, DateTimeKind.Utc), Started));
    }

    [Fact]
    public void A_script_from_before_the_start_is_what_it_runs()
    {
        Assert.False(RunsOlderCode(Started.AddDays(-2), Started));
        Assert.False(RunsOlderCode(Started, Started));
    }

    [Fact]
    public void A_missing_script_never_restarts_it()
    {
        // File.GetLastWriteTimeUtc answers 1601-01-01 for a path that does not exist.
        Assert.False(RunsOlderCode(DateTime.FromFileTimeUtc(0), Started));
    }

    // ------------------------------------------------------------ when

    [Fact]
    public void A_live_run_holds_the_restart_until_the_desk_is_quiet()
    {
        // The desk pulls every two minutes at any hour; a push at 11:00 must
        // not restart the notifier in the middle of the session.
        Assert.Equal(RestartVerdict.WaitForQuietDesk, DecideRestart(verifiedNotifier: true, liveRuns: 7));
        Assert.Equal(RestartVerdict.WaitForQuietDesk, DecideRestart(verifiedNotifier: true, liveRuns: 1));
        Assert.Equal(RestartVerdict.Restart, DecideRestart(verifiedNotifier: true, liveRuns: 0));
    }

    [Fact]
    public void A_pid_not_verified_as_the_notifier_is_never_stopped_automatically()
    {
        Assert.Equal(RestartVerdict.Unverified, DecideRestart(verifiedNotifier: false, liveRuns: 0));
        Assert.Equal(RestartVerdict.Unverified, DecideRestart(verifiedNotifier: false, liveRuns: 3));
    }

    // ------------------------------------------------------------ which pid

    [Fact]
    public void A_live_process_that_is_not_the_notifier_is_not_verified_as_it()
    {
        // On Windows ProcessProbe calls any live holder of the stored pid the
        // adopted notifier; this is the status it would report.
        var sleeper = Sleeper();
        var adopted = new PythonDaemonSupervisor.Status(true, false, sleeper.Id, PythonDaemonSupervisor.SourceAdopted);

        Assert.False(Notifier().IsVerified(adopted));
    }

    [Fact]
    public void An_adopted_notifier_is_verified_only_where_its_command_line_can_be_read()
    {
        var notifier = NamedLikeTheNotifier();
        var adopted = new PythonDaemonSupervisor.Status(true, false, notifier.Id, PythonDaemonSupervisor.SourceAdopted);

        // Windows cannot read a command line, so there it is never verified
        // and never stopped without a person.
        Assert.Equal(!OperatingSystem.IsWindows(), Notifier().IsVerified(adopted));
    }

    [Fact]
    public void A_notifier_this_api_launched_is_verified_by_its_handle()
    {
        var managed = new PythonDaemonSupervisor.Status(true, true, Environment.ProcessId, PythonDaemonSupervisor.SourceManaged);
        Assert.True(Notifier().IsVerified(managed));
    }

    [Fact]
    public void Nothing_running_is_never_verified()
    {
        var none = new PythonDaemonSupervisor.Status(false, false, null, PythonDaemonSupervisor.SourceNone);
        Assert.False(Notifier().IsVerified(none));
    }

    // ------------------------------------------------------------ does it load

    [Fact]
    public async Task A_script_that_loads_passes_without_its_main_being_run()
    {
        var ran = Path.Combine(_dir, "main-ran");
        var script = Script("good_notifier.py",
            "import json\n"
            + "if __name__ == '__main__':\n"
            + $"    open(r'{ran}', 'w').close()\n");

        Assert.Null(await LoadErrorAsync(Python, _dir, script, CancellationToken.None));
        Assert.False(File.Exists(ran));
        Assert.False(Directory.Exists(Path.Combine(_dir, "__pycache__")));
    }

    [Fact]
    public async Task A_syntax_error_is_caught_before_the_old_notifier_is_stopped()
    {
        var script = Script("broken_notifier.py", "def main(:\n    pass\n");

        var error = await LoadErrorAsync(Python, _dir, script, CancellationToken.None);

        Assert.NotNull(error);
        Assert.Contains("SyntaxError", error);
    }

    [Fact]
    public async Task An_import_this_interpreter_cannot_satisfy_is_caught_too()
    {
        // A new dependency missing from the desk's venv would crash-loop the
        // new notifier once a minute; py_compile alone would pass it.
        var script = Script("needs_more.py", "import no_such_module_for_the_load_check\n");

        var error = await LoadErrorAsync(Python, _dir, script, CancellationToken.None);

        Assert.NotNull(error);
        Assert.Contains("ModuleNotFoundError", error);
    }

    // ------------------------------------------------------------ helpers

    private static string Python => OperatingSystem.IsWindows() ? "python" : "python3";

    private string Script(string name, string source)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, source);
        return path;
    }

    private NotifierSupervisor Notifier()
    {
        var engine = new PythonEngineLocator(
            Options.Create(new StrategyRunnerOptions { EngineDirectory = _dir, PythonExecutable = Python }),
            new FakeHostEnvironment());
        var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new NotifierSupervisor(engine, scopes, NullLogger<NotifierSupervisor>.Instance);
    }

    private Process Sleeper()
        => Launch(OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c timeout /t 30 /nobreak")
            : new ProcessStartInfo("/bin/sleep", "30"));

    /// <summary>A live process whose command line names the notifier's script.</summary>
    private Process NamedLikeTheNotifier()
    {
        // Windows never reads the command line, so any live process will do.
        if (OperatingSystem.IsWindows()) return Sleeper();

        // "; :" keeps the shell itself alive (a lone command would be exec'd),
        // so ps shows "/bin/sh -c sleep 30; : telegram_notifier".
        var info = new ProcessStartInfo("/bin/sh");
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("sleep 30; :");
        info.ArgumentList.Add(ProcessProbe.NotifierMarker);
        return Launch(info);
    }

    private Process Launch(ProcessStartInfo info)
    {
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.UseShellExecute = false;

        var process = Process.Start(info)!;
        _processes.Add(process);
        return process;
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
