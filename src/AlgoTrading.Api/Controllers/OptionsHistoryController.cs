// src/AlgoTrading.Api/Controllers/OptionsHistoryController.cs
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Options;
using Microsoft.AspNetCore.Mvc;

namespace AlgoTrading.Api.Controllers;

[ApiController]
[Route("api/Options/history")]
public class OptionsHistoryController : ControllerBase
{
    private readonly IOptionHistoryBackfillService _optionHistoryBackfillService;

    public OptionsHistoryController(IOptionHistoryBackfillService optionHistoryBackfillService)
    {
        _optionHistoryBackfillService = optionHistoryBackfillService;
    }

    [HttpPost("backfill")]
    public async Task<IActionResult> Backfill(
        [FromBody] BackfillOptionHistoryRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
            return BadRequest(new { message = "Request body is required." });

        if (string.IsNullOrWhiteSpace(request.Exchange))
            return BadRequest(new { message = "Exchange is required." });

        if (string.IsNullOrWhiteSpace(request.Underlying))
            return BadRequest(new { message = "Underlying is required." });

        if (string.IsNullOrWhiteSpace(request.Resolution))
            return BadRequest(new { message = "Resolution is required." });

        if (request.FromUtc >= request.ToUtc)
            return BadRequest(new { message = "FromUtc must be earlier than ToUtc." });

        if (request.StrikeCountEachSide < 0)
            return BadRequest(new { message = "StrikeCountEachSide cannot be negative." });

        if (request.StrikeStep <= 0)
            return BadRequest(new { message = "StrikeStep must be greater than zero." });

        if (!request.IncludeCalls && !request.IncludePuts)
            return BadRequest(new { message = "At least one of IncludeCalls or IncludePuts must be true." });

        try
        {
            var result = await _optionHistoryBackfillService.BackfillAsync(
                request,
                cancellationToken);

            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                message = "Option history backfill failed.",
                detail = ex.Message
            });
        }
    }
}