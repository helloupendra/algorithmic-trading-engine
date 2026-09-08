// src/AlgoTrading.Api/Controllers/StrategyLabController.cs

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services;
using AlgoTrading.Domain.Constants;
using Microsoft.AspNetCore.Mvc;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// A bench for the indicators and the market-context filters: run them over
/// real bars and see what they compute, before either decides an order.
/// </summary>
/// <remarks>
/// It shells out to <c>tools/evaluate_filters.py</c> rather than working the
/// numbers out in C#, and that is the whole point. The gate that runs live is
/// Python; a C# reimplementation would be a second copy of the maths, and the
/// copy is what drifts. Testing the copy would prove nothing about the thing
/// that actually trades.
///
/// Read-only: it books nothing, subscribes nothing and changes no run. The
/// worst it can do is spend a couple of seconds of CPU.
/// </remarks>
[RequireModule(PlatformModules.Strategies)]
[ApiController]
[Route("api/[controller]")]
public class StrategyLabController : ControllerBase
{
    private const int PythonTimeoutMs = 60_000;

    private readonly PythonEngineLocator _engine;
    private readonly ILogger<StrategyLabController> _logger;

    public StrategyLabController(PythonEngineLocator engine, ILogger<StrategyLabController> logger)
    {
        _engine = engine;
        _logger = logger;
    }

    /// <param name="Symbol">Exact broker symbol whose bars to read.</param>
    /// <param name="Resolution">"1m", "5m", "15m" — aggregated on read like everywhere else.</param>
    /// <param name="Direction">"bullish" or "bearish": which way the probe signal points.</param>
    /// <param name="Bars">How many recent candles to replay the verdict over.</param>
    /// <param name="Filters">The same object a run carries in parametersJson.filters.</param>
    /// <param name="FromDate">yyyy-MM-dd. With it the window is read from stored candles.</param>
    /// <param name="FromTime">"09:30" IST — only candles at or after this are judged.</param>
    public sealed record EvaluateRequest(
        string Symbol,
        string? Resolution,
        int? Bars,
        string? FromDate,
        string? ToDate,
        string? FromTime,
        string? ToTime,
        JsonElement? Filters);

    /// <summary>Indicator values, the gate's verdict now, and that verdict over recent bars.</summary>
    [HttpPost("evaluate")]
    public async Task<IActionResult> Evaluate(
        [FromBody] EvaluateRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Symbol))
            return BadRequest(new { message = "symbol is required." });

        var engineDirectory = _engine.EngineDirectory;
        var scriptPath = _engine.ScriptPath("tools", "evaluate_filters.py");

        if (!System.IO.File.Exists(scriptPath))
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = $"The evaluator was not found at '{scriptPath}'." });

        // The request goes in on stdin: `filters` is a nested object, and a
        // command line is a poor and quote-sensitive place to put one.
        var payload = JsonSerializer.Serialize(new
        {
            symbol = request.Symbol.Trim(),
            resolution = string.IsNullOrWhiteSpace(request.Resolution) ? "5m" : request.Resolution,
            bars = Math.Clamp(request.Bars ?? 25, 1, 500),
            take = 500,
            fromDate = request.FromDate,
            toDate = request.ToDate,
            fromTime = request.FromTime,
            toTime = request.ToTime,
            filters = request.Filters
        });

        var psi = new ProcessStartInfo
        {
            FileName = _engine.PythonExecutable,
            WorkingDirectory = engineDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        psi.ArgumentList.Add(scriptPath);
        psi.Environment["PYTHONPATH"] = engineDirectory;
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Could not start python." });

        await process.StandardInput.WriteAsync(payload);
        process.StandardInput.Close();

        // Both pipes are drained concurrently: a full one would block the child.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PythonTimeoutMs);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return StatusCode(StatusCodes.Status504GatewayTimeout,
                new { message = "The evaluator did not finish in time." });
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (string.IsNullOrWhiteSpace(stdout))
        {
            _logger.LogWarning("Filter evaluator produced no output. exit={Exit} stderr={Stderr}",
                process.ExitCode, stderr);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "The evaluator returned nothing.", stderr });
        }

        try
        {
            // Passed through as-is: the shape is the evaluator's to define, and
            // re-modelling it here would mean two places to change per field.
            using var document = JsonDocument.Parse(stdout);
            return Ok(document.RootElement.Clone());
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Filter evaluator returned unreadable output: {Output}", Truncate(stdout));
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "The evaluator returned unreadable output.", output = Truncate(stdout), stderr });
        }
    }

    private static string Truncate(string text) => text.Length <= 600 ? text : text[..600] + "…";
}
