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
    /// not what one desk used to need. Each runner is a Python process of
    /// roughly 150 MB; forty is about 6 GB, which is why it is not higher on an
    /// 8 GB box. Set <c>StrategyRunner:MaxConcurrentProcesses</c> to change it.
    /// </remarks>
    public int MaxConcurrentProcesses { get; set; } = 40;

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
