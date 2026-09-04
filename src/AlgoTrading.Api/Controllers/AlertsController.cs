using Microsoft.AspNetCore.Mvc;
using StackExchange.Redis;
using System.Text.Json;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using AlgoTrading.Api.Configuration;
using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services;

namespace AlgoTrading.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AlertsController : ControllerBase
    {
        private readonly IConnectionMultiplexer _redis;
        private readonly StrategyRunnerOptions _options;
        private readonly IWebHostEnvironment _environment;
        private readonly ILogger<AlertsController> _logger;
        private readonly PythonEngineLocator _locator;

        private static readonly List<Process> _alerterProcesses = new();
        private static DateTime? _startedUtc;
        private static readonly object _processLock = new object();
        private static readonly System.Collections.Concurrent.ConcurrentQueue<string> _alerterLogs = new();
        private const int MaxLogs = 100;

        public AlertsController(
            IConnectionMultiplexer redis,
            IOptions<StrategyRunnerOptions> options,
            IWebHostEnvironment environment,
            ILogger<AlertsController> logger,
            PythonEngineLocator locator)
        {
            _redis = redis;
            _options = options.Value;
            _environment = environment;
            _logger = logger;
            _locator = locator;
        }

        private static void AppendLog(string message)
        {
            _alerterLogs.Enqueue($"[{DateTime.UtcNow:HH:mm:ss}] {message}");
            while (_alerterLogs.Count > MaxLogs)
            {
                _alerterLogs.TryDequeue(out _);
            }
        }

        [HttpGet("logs")]
        public IActionResult GetLogs()
        {
            return Ok(_alerterLogs.ToArray());
        }

        [HttpGet("status")]
        public IActionResult GetStatus()
        {
            lock (_processLock)
            {
                // Clean up exited processes
                _alerterProcesses.RemoveAll(p => p.HasExited);
                bool isRunning = _alerterProcesses.Count > 0;
                
                return Ok(new
                {
                    IsRunning = isRunning,
                    StartedUtc = isRunning ? _startedUtc : null
                });
            }
        }

        [HttpPost("start")]
        public IActionResult StartAlerter()
        {
            lock (_processLock)
            {
                _alerterProcesses.RemoveAll(p => p.HasExited);
                if (_alerterProcesses.Count > 0)
                {
                    return Conflict(new { message = "Telegram Alerter is already running." });
                }

                _alerterLogs.Clear();
                AppendLog("Starting Telegram Alerter (LogicEngine) for Core Indices...");

                var engineDirectory = _locator.EngineDirectory;
                var scriptPath = Path.Combine(engineDirectory, "strategies", "execution_runner.py");

                var targets = new[]
                {
                    new { Underlying = "BANKNIFTY", Spot = "NSE:NIFTYBANK-INDEX", Port = 8000 },
                    new { Underlying = "NIFTY", Spot = "NSE:NIFTY50-INDEX", Port = 8001 },
                    new { Underlying = "SENSEX", Spot = "BSE:SENSEX-INDEX", Port = 8002 }
                };

                foreach (var target in targets)
                {
                    var processInfo = new ProcessStartInfo
                    {
                        FileName = _locator.PythonExecutable,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        WorkingDirectory = engineDirectory
                    };

                    processInfo.ArgumentList.Add(scriptPath);
                    processInfo.ArgumentList.Add("--strategy-id");
                    processInfo.ArgumentList.Add("LogicEngine");
                    processInfo.ArgumentList.Add("--user-id");
                    processInfo.ArgumentList.Add(User.GetRequiredUserId().ToString());
                    processInfo.ArgumentList.Add("--underlying");
                    processInfo.ArgumentList.Add(target.Underlying);
                    processInfo.ArgumentList.Add("--spot-symbol");
                    processInfo.ArgumentList.Add(target.Spot);
                    processInfo.ArgumentList.Add("--metrics-port");
                    processInfo.ArgumentList.Add("0");
                    
                    processInfo.Environment["PYTHONPATH"] = engineDirectory;
                    processInfo.Environment["PYTHONUNBUFFERED"] = "1";

                    var process = Process.Start(processInfo);
                    if (process is null)
                    {
                        _logger.LogError("Failed to start process for {Underlying}.", target.Underlying);
                        AppendLog($"Failed to start process for {target.Underlying}");
                        continue;
                    }

                    _alerterProcesses.Add(process);

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var tasks = new[]
                            {
                                Task.Run(async () =>
                                {
                                    while (!process.StandardOutput.EndOfStream)
                                    {
                                        var line = await process.StandardOutput.ReadLineAsync();
                                        if (line != null) 
                                        {
                                            _logger.LogInformation("[{Underlying}] {Line}", target.Underlying, line);
                                            AppendLog($"[{target.Underlying}] {line}");
                                        }
                                    }
                                }),
                                Task.Run(async () =>
                                {
                                    while (!process.StandardError.EndOfStream)
                                    {
                                        var line = await process.StandardError.ReadLineAsync();
                                        if (line != null) 
                                        {
                                            _logger.LogError("[{Underlying}] {Line}", target.Underlying, line);
                                            AppendLog($"[{target.Underlying}] ERROR: {line}");
                                        }
                                    }
                                })
                            };

                            await Task.WhenAll(tasks);
                            await process.WaitForExitAsync();

                            _logger.LogInformation("{Underlying} exited with code {ExitCode}.", target.Underlying, process.ExitCode);
                            AppendLog($"[{target.Underlying}] Process exited with code {process.ExitCode}");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error while draining output for {Underlying}.", target.Underlying);
                            AppendLog($"[{target.Underlying}] Exception in runner: {ex.Message}");
                        }
                    });
                }

                _startedUtc = DateTime.UtcNow;
                return Ok(new { message = "Started 3 Telegram Alerter background processes." });
            }
        }

        [HttpPost("stop")]
        public IActionResult StopAlerter()
        {
            lock (_processLock)
            {
                _alerterProcesses.RemoveAll(p => p.HasExited);
                if (_alerterProcesses.Count == 0)
                {
                    return BadRequest(new { message = "Telegram Alerter is not running." });
                }

                foreach (var process in _alerterProcesses)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error killing alerter process.");
                    }
                }
                
                _alerterProcesses.Clear();
                _startedUtc = null;
                AppendLog("Stopped all Telegram Alerter processes.");
                
                return Ok(new { message = "Stopped all Telegram Alerter background processes." });
            }
        }

        [HttpPost("test-e2e")]
        public async Task<IActionResult> TriggerE2ETest([FromBody] E2ETestRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Instrument))
            {
                return BadRequest(new { status = "error", message = "Instrument is required." });
            }

            var pub = _redis.GetSubscriber();
            
            var payload = new
            {
                command = "TEST_E2E_ALERT",
                instrument = request.Instrument
            };

            string jsonPayload = JsonSerializer.Serialize(payload);
            
            await pub.PublishAsync("cmd:python_engine", jsonPayload);

            return Ok(new
            {
                status = "success",
                message = $"Successfully broadcasted E2E alert command for {request.Instrument} to the Python Engine.",
                broadcastedPayload = payload
            });
        }
    }

    public class E2ETestRequest
    {
        public string Instrument { get; set; } = string.Empty;
    }
}
