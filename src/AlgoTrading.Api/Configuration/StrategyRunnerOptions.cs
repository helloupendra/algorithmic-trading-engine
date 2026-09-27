namespace AlgoTrading.Api.Configuration;

/// <summary>
/// Where the API finds the Python engine when launching a strategy process.
///
/// Both values default to paths derived from the API's own content root, so a
/// fresh clone works on any machine and any OS without configuration. Override
/// them under a "StrategyRunner" section only for non-standard layouts.
/// </summary>
public class StrategyRunnerOptions
{
    public const string SectionName = "StrategyRunner";

    /// <summary>
    /// Python interpreter to run. When empty, the repo-root virtualenv is used if
    /// present (.venv/bin/python, or .venv\Scripts\python.exe on Windows), falling
    /// back to "python3" / "python" on PATH.
    /// </summary>
    public string? PythonExecutable { get; set; }

    /// <summary>
    /// Directory containing the Python engine package. When empty, resolves to
    /// &lt;contentRoot&gt;/../AlgoTrading.PythonEngine.
    /// </summary>
    public string? EngineDirectory { get; set; }

    /// <summary>
    /// Hard ceiling on concurrently running strategy processes, so a runaway
    /// dashboard cannot exhaust the host.
    /// </summary>
    /// <remarks>
    /// Ten was right while one account ran one strategy per index. With every
    /// trading account running its own copy of the morning's plan the honest
    /// number is accounts times runs — five strategies over three indices plus
    /// crude is thirteen a head — so the ceiling is what the machine can carry,
    /// not what one desk used to need.
    /// <para>
    /// It was forty, on an estimate of 150 MB a runner. Measured on 25 Sep with
    /// 26 runners on the 8 GB box: 115–125 MB each, MemAvailable down to
    /// 1.76–1.92 GB and the CPU nearly saturated. Twenty-eight is two accounts'
    /// plans with a little room, and <see cref="MinAvailableMemoryMb"/> refuses
    /// a start before the box gets there anyway. Set
    /// <c>StrategyRunner:MaxConcurrentProcesses</c> to change it.
    /// </para>
    /// </remarks>
    public int MaxConcurrentProcesses { get; set; } = 28;

    /// <summary>
    /// A new runner is refused (429) while the host has less than this much
    /// memory available (MemAvailable, in MB). Only where it can be read —
    /// Linux; elsewhere the check is skipped.
    /// </summary>
    /// <remarks>
    /// The runner cap counts processes, not memory. A runner is 115–125 MB,
    /// and the API, the feeds and the database live on the same box: below
    /// about a gigabyte the next start is what pushes it into swap or the OOM
    /// killer, which takes out a running strategy rather than the new one.
    /// </remarks>
    public int MinAvailableMemoryMb { get; set; } = 1024;

    /// <summary>
    /// Hard ceiling on concurrently running backtest runner processes. A
    /// backtest is CPU-bound and posts thousands of rows, so the default is
    /// deliberately small.
    /// </summary>
    public int MaxConcurrentBacktests { get; set; } = 3;

    /// <summary>
    /// How often the risk guard re-evaluates each running strategy's total P&amp;L
    /// against its stop-loss / target.
    /// </summary>
    public int RiskGuardIntervalSeconds { get; set; } = 3;

    /// <summary>
    /// The oldest mark, in seconds, the risk guard judges a leg or a group on.
    /// A position marked from an older quote is skipped by the leg and group
    /// rules until its quote moves again, and the run's owner is told once.
    /// </summary>
    /// <remarks>
    /// On 24 Sep the feed stalled from 11:27:36 to 11:34:06 and the guard went
    /// on evaluating leg targets for runs 205, 206, 207, 234 and 235 against
    /// prices that had stopped moving: a target "hit" on a frozen price closes
    /// a leg at a price the market is no longer at.
    /// </remarks>
    public int RiskGuardMaxMarkAgeSeconds { get; set; } = 30;
}
