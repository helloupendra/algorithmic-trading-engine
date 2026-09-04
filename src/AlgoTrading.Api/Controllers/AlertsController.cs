using Microsoft.AspNetCore.Mvc;
using StackExchange.Redis;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services;
using AlgoTrading.Contracts.Alerts;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using AlgoTrading.Domain.Entities;

namespace AlgoTrading.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AlertsController : ControllerBase
    {
        private readonly IConnectionMultiplexer _redis;
        private readonly AlertsSupervisor _supervisor;
        private readonly ILogger<AlertsController> _logger;

        public AlertsController(
            IConnectionMultiplexer redis,
            AlertsSupervisor supervisor,
            ILogger<AlertsController> logger)
        {
            _redis = redis;
            _supervisor = supervisor;
            _logger = logger;
        }

        [HttpGet("logs")]
        [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
        public IActionResult GetLogs([FromQuery] string underlying = "BANKNIFTY")
        {
            return Ok(_supervisor.GetLogs(underlying, 100));
        }

        [HttpGet("status")]
        [Authorize]
        public async Task<IActionResult> GetStatus(CancellationToken cancellationToken = default)
        {
            var status = await _supervisor.GetStatusAsync(cancellationToken);
            return Ok(status);
        }

        [HttpPost("start")]
        [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
        public async Task<IActionResult> StartAlerter(CancellationToken cancellationToken = default)
        {
            var outcome = await _supervisor.StartAsync(User.GetRequiredUserId(), cancellationToken);
            return StatusCode(outcome.StatusCode, new { message = outcome.Message, failedTargets = outcome.FailedTargets });
        }

        [HttpPost("stop")]
        [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
        public async Task<IActionResult> StopAlerter(CancellationToken cancellationToken = default)
        {
            var outcome = await _supervisor.StopAsync("API stop requested", cancellationToken);
            if (!outcome.WasRunning)
            {
                return BadRequest(new { message = outcome.Message });
            }
            return Ok(new { message = outcome.Message });
        }

        [HttpPost("test-e2e")]
        [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
        public async Task<IActionResult> TriggerE2ETest(
            [FromBody] E2ETestRequest request,
            [FromServices] TradingDbContext dbContext,
            CancellationToken cancellationToken = default)
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

            // Item 5.7: POST /api/Alerts/test-e2e writes an alert_events row.
            var alert = new AlertEvent
            {
                OccurredUtc = DateTime.UtcNow,
                Source = "test-e2e",
                Underlying = request.Instrument,
                Severity = "info",
                Title = "E2E Test Triggered",
                Message = $"API initiated an E2E test for {request.Instrument}.",
                MetadataJson = jsonPayload,
                DeliveredToTelegram = false
            };
            dbContext.AlertEvents.Add(alert);
            await dbContext.SaveChangesAsync(cancellationToken);

            return Ok(new
            {
                status = "success",
                message = $"Successfully broadcasted E2E alert command for {request.Instrument} to the Python Engine.",
                broadcastedPayload = payload
            });
        }

        [HttpGet("events")]
        [Authorize]
        public async Task<IActionResult> GetAlertEvents(
            [FromServices] TradingDbContext dbContext,
            [FromQuery] int limit = 100,
            CancellationToken cancellationToken = default)
        {
            limit = Math.Clamp(limit, 1, 500);

            var events = await dbContext.AlertEvents
                .AsNoTracking()
                .OrderByDescending(x => x.OccurredUtc)
                .Take(limit)
                .Select(x => new AlertEventDto
                {
                    Id = x.Id,
                    OccurredUtc = x.OccurredUtc,
                    Source = x.Source,
                    Underlying = x.Underlying,
                    Symbol = x.Symbol,
                    Severity = x.Severity,
                    Title = x.Title,
                    Message = x.Message,
                    MetadataJson = x.MetadataJson,
                    DeliveredToTelegram = x.DeliveredToTelegram,
                    SimulationRunId = x.SimulationRunId
                })
                .ToListAsync(cancellationToken);

            return Ok(events);
        }
    }

    public class E2ETestRequest
    {
        public string Instrument { get; set; } = string.Empty;
    }
}
