using System.Diagnostics;
using AlgoTrading.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// A real process that stays alive for about half a minute, for tests that
/// need something to stop, adopt or watch.
/// </summary>
/// <remarks>
/// On Windows this was <c>timeout /t 30</c>, which needs a console: started the
/// way a CI runner starts it, with no console and its output redirected, it
/// prints "ERROR: Input redirection is not supported" and exits at once. A test
/// that needed the process alive then saw a runner that had died on its own
/// (CarryForwardTests read "Runner exited" where it expected the risk guard's
/// stop). <c>ping</c> waits the same way with or without a console.
/// </remarks>
internal static class TestSleeper
{
    // On Linux Process.Start can return before exec replaces the inherited test-host
    // command line. Adoption must observe the requested child, not that transient image.
    public static void WaitForCommandLine(Process process, string marker)
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.True(SpinWait.SpinUntil(() => !process.HasExited &&
            ProcessProbe.CommandLineNames(process.Id, [marker], NullLogger.Instance), TimeSpan.FromSeconds(5)),
            $"Child {process.Id} did not publish its expected command line ({marker}).");
    }

    public static ProcessStartInfo StartInfo() => OperatingSystem.IsWindows()
        ? new ProcessStartInfo("ping", "-n 31 127.0.0.1")
        : new ProcessStartInfo("/bin/sleep", "30");
}
