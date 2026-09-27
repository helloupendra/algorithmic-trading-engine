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

    /// <summary>How long an adopted process gets to leave after SIGTERM.</summary>
    /// <remarks>
    /// At the 15:30 close on 24 Sep, 10 of 13 adopted runners were killed after
    /// "ignoring" SIGTERM for 5 s. A runner releases its Redis lock and joins
    /// its threads on the way out, and an adopted one is not our child, so its
    /// exit is seen by polling rather than reported by the OS. It is given
    /// longer, and watched by pid.
    /// </remarks>
    public static readonly TimeSpan AdoptedGracefulExitTimeout = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan PidPollInterval = TimeSpan.FromMilliseconds(200);

    private const int SigTerm = 15;
    private const int NoSignal = 0;
    private const int NoSuchProcess = 3; // ESRCH, the same on Linux and macOS

    /// <summary>
    /// Terminates <paramref name="process"/>. <paramref name="pid"/> is the id
    /// captured at launch (Process.Id throws once the handle is gone).
    /// <paramref name="log"/> receives the human-readable steps for the run's
    /// output console. Returns true when the process is known to have exited.
    /// </summary>
    /// <param name="adopted">
    /// The process was taken over by pid after an API restart, so it is not
    /// this process's child: its exit is watched with <c>kill(pid, 0)</c> for
    /// up to <see cref="AdoptedGracefulExitTimeout"/>. A child is waited on
    /// directly (kill(pid, 0) would still find it as a zombie until reaped).
    /// </param>
    public static async Task<bool> StopAsync(Process process, int pid, Action<string> log, ILogger logger, string label, bool adopted = false)
    {
        bool watchPid = adopted && !OperatingSystem.IsWindows() && pid > 0;
        try
        {
            if (process.HasExited) return true;

            if (!OperatingSystem.IsWindows() && TrySendSigterm(process, pid, logger, label))
            {
                log("sent SIGTERM to the runner");
                var patience = watchPid ? AdoptedGracefulExitTimeout : GracefulExitTimeout;
                if (watchPid ? await WaitForPidExitAsync(pid, patience) : await WaitForExitAsync(process, patience))
                {
                    return true;
                }
                logger.LogWarning("{Label} runner ignored SIGTERM for {Seconds}s; killing the process tree.",
                    label, patience.TotalSeconds);
                log("runner did not exit on SIGTERM; killing");
            }

            if (process.HasExited) return true;

            // entireProcessTree: the runner may have spawned children that would
            // otherwise keep running after the parent dies.
            process.Kill(entireProcessTree: true);
            bool gone = watchPid
                ? await WaitForPidExitAsync(pid, ForcedExitTimeout)
                : await WaitForExitAsync(process, ForcedExitTimeout);
            if (!gone)
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

    /// <summary>Polls <c>kill(pid, 0)</c> until the pid is gone or <paramref name="timeout"/> passes.</summary>
    public static async Task<bool> WaitForPidExitAsync(int pid, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            if (IsGone(pid)) return true;
            if (DateTime.UtcNow >= until) return false;
            await Task.Delay(PidPollInterval);
        }
    }

    /// <summary>
    /// True once no process has <paramref name="pid"/>. EPERM means one does,
    /// owned by someone else, so only ESRCH counts as gone.
    /// </summary>
    private static bool IsGone(int pid)
    {
        try
        {
            return SysKill(pid, NoSignal) != 0 && Marshal.GetLastPInvokeError() == NoSuchProcess;
        }
        catch (Exception)
        {
            return false; // kill(2) unavailable: never claim an exit that was not seen
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
