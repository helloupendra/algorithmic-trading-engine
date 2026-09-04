// src/AlgoTrading.Api/Services/ProcessProbe.cs
using System.Diagnostics;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Answers "is the process with this pid still OUR child from before the
/// restart?" for the adoption paths. A bare pid check is not enough: pids are
/// recycled, and killing whatever now owns a stale pid would be a disaster. On
/// non-Windows hosts the command line (<c>ps -o command= -p pid</c>) must name
/// the expected script (and, for runners, the exact <c>--run-id</c>).
/// </summary>
public static class ProcessProbe
{
    public const string IngestorMarker = "fyers_streamer";
    public const string StrategyRunnerMarker = "execution_runner";
    public const string BacktestRunnerMarker = "backtest_runner";

    private static readonly TimeSpan PsTimeout = TimeSpan.FromSeconds(5);

    /// <summary>What a probe found out about a pid.</summary>
    public enum Outcome
    {
        /// <summary>The pid is alive and verified to be the expected process.</summary>
        Alive,

        /// <summary>The pid is gone, has exited, or now belongs to a different (recycled) process.</summary>
        Dead,

        /// <summary>
        /// The pid is alive but its command line could not be read (ps could
        /// not be spawned, timed out under load, or printed nothing) — so it
        /// is neither confirmed nor refuted as the expected process.
        /// </summary>
        Unknown
    }

    /// <summary>
    /// The probe result. <see cref="Process"/> is a live handle the caller owns
    /// only when <see cref="Result"/> is <see cref="Outcome.Alive"/>.
    /// </summary>
    public sealed record ProbeResult(Outcome Result, Process? Process)
    {
        public bool IsAlive => Result == Outcome.Alive;
        public bool IsDead => Result == Outcome.Dead;
        public bool IsUnknown => Result == Outcome.Unknown;
    }

    /// <summary>
    /// A live handle to <paramref name="pid"/> when it is alive and, on
    /// non-Windows hosts, its command line contains <paramref name="marker"/>
    /// (and names <paramref name="runId"/> through <c>--run-id</c> when given);
    /// otherwise null — including when the command line could not be read
    /// (callers that need to tell "dead" from "could not verify" use
    /// <see cref="Probe"/>). The caller owns the returned handle.
    /// </summary>
    public static Process? TryGetAlive(int pid, string marker, long? runId, ILogger logger)
        => Probe(pid, marker, runId, logger).Process;

    /// <summary>
    /// Three-way probe of <paramref name="pid"/>: Alive (handle returned),
    /// Dead (gone, exited or recycled to another program), or Unknown (alive
    /// but the command line could not be read). A durable pid record must only
    /// be dropped on Dead: a transient <c>ps</c> failure is not proof that the
    /// process is gone.
    /// </summary>
    public static ProbeResult Probe(int pid, string marker, long? runId, ILogger logger)
    {
        if (pid <= 0) return new ProbeResult(Outcome.Dead, null);

        Process? process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return new ProbeResult(Outcome.Dead, null); // no such process
        }
        catch (InvalidOperationException)
        {
            return new ProbeResult(Outcome.Dead, null);
        }

        try
        {
            if (process.HasExited)
            {
                process.Dispose();
                return new ProbeResult(Outcome.Dead, null);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read the state of pid {Pid}.", pid);
            process.Dispose();
            return new ProbeResult(Outcome.Unknown, null);
        }

        if (OperatingSystem.IsWindows())
        {
            // No portable command-line read here; the pid being alive is the best signal available.
            return new ProbeResult(Outcome.Alive, process);
        }

        var commandLine = ReadCommandLine(pid, logger);
        if (commandLine is null)
        {
            logger.LogWarning("Could not read the command line of pid {Pid}; it is alive but cannot be verified as the expected {Marker} process.", pid, marker);
            process.Dispose();
            return new ProbeResult(Outcome.Unknown, null);
        }

        if (!commandLine.Contains(marker, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Pid {Pid} is alive but is not a {Marker} process ({CommandLine}); it was recycled.", pid, marker, Truncate(commandLine));
            process.Dispose();
            return new ProbeResult(Outcome.Dead, null);
        }

        if (runId.HasValue && !HasRunIdArgument(commandLine, runId.Value))
        {
            logger.LogWarning("Pid {Pid} is a {Marker} process but not for run {RunId} ({CommandLine}).", pid, marker, runId, Truncate(commandLine));
            process.Dispose();
            return new ProbeResult(Outcome.Dead, null);
        }

        return new ProbeResult(Outcome.Alive, process);
    }

    /// <summary>Full command line of the process on macOS/Linux, or null when it cannot be read.</summary>
    public static string? ReadCommandLine(int pid, ILogger logger)
    {
        if (OperatingSystem.IsWindows()) return null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ps",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("command=");
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add(pid.ToString(System.Globalization.CultureInfo.InvariantCulture));

            using var ps = Process.Start(psi);
            if (ps is null) return null;

            var output = ps.StandardOutput.ReadToEnd();
            if (!ps.WaitForExit((int)PsTimeout.TotalMilliseconds))
            {
                try { ps.Kill(); } catch { /* best effort */ }
                return null;
            }

            var line = output.Trim();
            return line.Length == 0 ? null : line;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "ps failed for pid {Pid}.", pid);
            return null;
        }
    }

    /// <summary>True when the command line carries <c>--run-id &lt;runId&gt;</c> (or <c>--run-id=&lt;runId&gt;</c>) as whole tokens.</summary>
    public static bool HasRunIdArgument(string commandLine, long runId)
    {
        var expected = runId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var tokens = commandLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] == "--run-id" && i + 1 < tokens.Length && tokens[i + 1] == expected) return true;
            if (tokens[i] == $"--run-id={expected}") return true;
        }
        return false;
    }

    private static string Truncate(string text)
        => text.Length <= 160 ? text : text[..160] + "…";
}
