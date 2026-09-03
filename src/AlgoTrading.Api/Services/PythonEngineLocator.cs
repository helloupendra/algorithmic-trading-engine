// src/AlgoTrading.Api/Services/PythonEngineLocator.cs
using AlgoTrading.Api.Configuration;
using Microsoft.Extensions.Options;
using System.Runtime.InteropServices;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Single place that knows where the Python engine and its interpreter live, so
/// the strategy runner, the ingestor and the strategy catalog all launch the same
/// interpreter against the same package directory.
/// </summary>
public sealed class PythonEngineLocator
{
    private readonly StrategyRunnerOptions _options;
    private readonly IWebHostEnvironment _environment;

    public PythonEngineLocator(IOptions<StrategyRunnerOptions> options, IWebHostEnvironment environment)
    {
        _options = options.Value;
        _environment = environment;
    }

    /// <summary>
    /// &lt;contentRoot&gt;/../AlgoTrading.PythonEngine unless StrategyRunner:EngineDirectory is set.
    /// </summary>
    public string EngineDirectory
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_options.EngineDirectory))
            {
                return Path.GetFullPath(_options.EngineDirectory);
            }

            return Path.GetFullPath(Path.Combine(_environment.ContentRootPath, "..", "AlgoTrading.PythonEngine"));
        }
    }

    /// <summary>
    /// The repo-root virtualenv interpreter when it exists, else whatever "python3"
    /// (or "python" on Windows) resolves to on PATH.
    /// </summary>
    public string PythonExecutable
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_options.PythonExecutable))
            {
                return _options.PythonExecutable;
            }

            bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

            // contentRoot is src/AlgoTrading.Api, so the repo root is two levels up.
            var repoRoot = Path.GetFullPath(Path.Combine(_environment.ContentRootPath, "..", ".."));
            var venvPython = isWindows
                ? Path.Combine(repoRoot, ".venv", "Scripts", "python.exe")
                : Path.Combine(repoRoot, ".venv", "bin", "python");

            if (File.Exists(venvPython))
            {
                return venvPython;
            }

            return isWindows ? "python" : "python3";
        }
    }

    /// <summary>Absolute path of a script inside the engine directory.</summary>
    public string ScriptPath(params string[] relativeParts)
        => Path.Combine(new[] { EngineDirectory }.Concat(relativeParts).ToArray());
}
