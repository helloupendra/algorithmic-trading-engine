// src/AlgoTrading.Api/Controllers/ExpiryController.cs
using AlgoTrading.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace AlgoTrading.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ExpiryController : ControllerBase
{
    private readonly IExpiryResolverService _expiryResolverService;

    public ExpiryController(IExpiryResolverService expiryResolverService)
    {
        _expiryResolverService = expiryResolverService;
    }

    [HttpGet("rule")]
    public async Task<IActionResult> GetRule(
        [FromQuery] string exchange,
        [FromQuery] string underlying,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(exchange))
            return BadRequest(new { message = "exchange is required." });

        if (string.IsNullOrWhiteSpace(underlying))
            return BadRequest(new { message = "underlying is required." });

        var result = await _expiryResolverService.GetRuleAsync(
            exchange.Trim().ToUpperInvariant(),
            underlying.Trim().ToUpperInvariant(),
            cancellationToken);

        if (result is null)
            return NotFound(new { message = "Expiry rule not found." });

        return Ok(result);
    }

    [HttpGet("available")]
    public async Task<IActionResult> GetAvailable(
        [FromQuery] string exchange,
        [FromQuery] string underlying,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(exchange))
            return BadRequest(new { message = "exchange is required." });

        if (string.IsNullOrWhiteSpace(underlying))
            return BadRequest(new { message = "underlying is required." });

        var result = await _expiryResolverService.GetAvailableExpiriesAsync(
            exchange.Trim().ToUpperInvariant(),
            underlying.Trim().ToUpperInvariant(),
            cancellationToken);

        return Ok(result);
    }

    [HttpGet("resolve")]
    public async Task<IActionResult> Resolve(
        [FromQuery] string exchange,
        [FromQuery] string underlying,
        [FromQuery] string? expiryType,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(exchange))
            return BadRequest(new { message = "exchange is required." });

        if (string.IsNullOrWhiteSpace(underlying))
            return BadRequest(new { message = "underlying is required." });

        var ex = exchange.Trim().ToUpperInvariant();
        var ul = underlying.Trim().ToUpperInvariant();

        try
        {
            if (string.IsNullOrWhiteSpace(expiryType))
            {
                var preferred = await _expiryResolverService.ResolvePreferredExpiryAsync(
                    ex,
                    ul,
                    DateTime.UtcNow,
                    cancellationToken);

                if (preferred is null)
                    return NotFound(new { message = "No expiry could be resolved." });

                return Ok(preferred);
            }

            var result = await _expiryResolverService.ResolveExactExpiryAsync(
                ex,
                ul,
                expiryType.Trim(),
                DateTime.UtcNow,
                cancellationToken);

            if (result is null)
                return NotFound(new { message = "No expiry could be resolved." });

            return Ok(result);
        }
        catch (InvalidOperationException ex2)
        {
            return BadRequest(new { message = ex2.Message });
        }
    }
}