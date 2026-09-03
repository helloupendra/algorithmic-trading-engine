// src/AlgoTrading.Api/Services/ProcessTerminator.cs
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Stops a Python runner the same way from every owner (live strategy, backtest):
/// SIGTERM first (the runner's handler prints "[RUNNER] stopping: SIGTERM" and
/// releases its locks in its finally block), wait for a graceful exit, then
/// SIGKILL the whole process tree if it is still alive. Windows has no SIGTERM,
/// so it goes straight to Kill.
/// </summary>
public static class ProcessTerminator
{
    public static readonly TimeSpan GracefulExitTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan ForcedExitTimeout = TimeSpan.FromSeconds(5);

    private const int SigTerm = 15;

    /// <summary>
    /// Terminates <paramref name="process"/>. <paramref name="pid"/> is the id
    /// captured at launch (Process.Id throws once the handle is gone).
    /// <paramref name="log"/> receives the human-readable steps for the run's
    /// output console. Returns true when the process is known to have exited.
    /// </summary>
    public static async Task<bool> StopAsync(Process process, int pid, Action<string> log, ILogger logger, string label)
    {
        try
        {
            if (process.HasExited) return true;

            if (!OperatingSystem.IsWindows() && TrySendSigterm(process, pid, logger, label))
            {
                log("sent SIGTERM to the runner");
                if (await WaitForExitAsync(process, GracefulExitTimeout))
                {
                    return true;
                }
                logger.LogWarning("{Label} runner ignored SIGTERM for {Seconds}s; killing the process tree.",
                    label, GracefulExitTimeout.TotalSeconds);
                log("runner did not exit on SIGTERM; killing");
            }

            if (process.HasExited) return true;

            // entireProcessTree: the runner may have spawned children that would
            // otherwise keep running after the parent dies.
            process.Kill(entireProcessTree: true);
            if (!await WaitForExitAsync(process, ForcedExitTimeout))
            {
                logger.LogError("{Label} runner pid {Pid} is still alive after SIGKILL.", label, pid);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error stopping {Label} process.", label);
            try { return process.HasExited; } catch { return false; }
        }
    }

    public static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            try { return process.HasExited; } catch { return false; }
        }
    }

    private static bool TrySendSigterm(Process process, int pid, ILogger logger, string label)
    {
        if (pid <= 0)
        {
            try { pid = process.Id; } catch { return false; }
        }

        try
        {
            return SysKill(pid, SigTerm) == 0;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "kill(2) unavailable; falling back to Process.Kill for {Label}.", label);
            return false;
        }
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SysKill(int pid, int signal);
}
