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
    public int MaxConcurrentProcesses { get; set; } = 10;

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
}
