using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.UseCases.Instruments;
using AlgoTrading.Contracts.Instruments;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// Exposes endpoints to search the instrument universe, import CSV masters, and resolve derivative expiries and chains.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class InstrumentsController : ControllerBase
{
    private readonly TradingDbContext _dbContext;
    private readonly ImportInstrumentsFromFileUseCase _importInstrumentsFromFileUseCase;
    private readonly IDerivativesInstrumentService _derivativesInstrumentService;

    public InstrumentsController(
        TradingDbContext dbContext,
        ImportInstrumentsFromFileUseCase importInstrumentsFromFileUseCase,
        IDerivativesInstrumentService derivativesInstrumentService)
    {
        _dbContext = dbContext;
        _importInstrumentsFromFileUseCase = importInstrumentsFromFileUseCase;
        _derivativesInstrumentService = derivativesInstrumentService;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        var rows = await _dbContext.Instruments
            .AsNoTracking()
            .OrderBy(x => x.Symbol)
            .ToListAsync(cancellationToken);

        return Ok(rows);
    }


    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
            return BadRequest(new { message = "query is required" });

        var rows = await _dbContext.Instruments
            .AsNoTracking()
            .Where(x => x.Symbol.Contains(query) || x.Description.Contains(query))
            .OrderBy(x => x.Symbol)
            .Take(50)
            .ToListAsync(cancellationToken);

        return Ok(rows);
    }


    /// <summary>
    /// Bulk-imports an instrument master CSV from a path on the API host.
    /// Admin-only: it reads an arbitrary server-side file and rewrites the
    /// instrument universe every strategy resolves contracts against.
    /// </summary>
    [Microsoft.AspNetCore.Authorization.Authorize(Policy = Security.AuthorizationPolicies.AdminOnly)]
    [HttpPost("import-local")]
    public async Task<IActionResult> ImportLocal(
        [FromQuery] string? filePath,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] ImportInstrumentsRequest? request,
        CancellationToken cancellationToken)
    {
        var pathToUse = filePath ?? request?.FilePath;

        if (string.IsNullOrWhiteSpace(pathToUse))
            return BadRequest(new { message = "filePath is required (either as a query parameter or in the JSON body)" });

        var req = new ImportInstrumentsRequest { FilePath = pathToUse };
        var result = await _importInstrumentsFromFileUseCase.ExecuteAsync(req, cancellationToken);
        return Ok(result);
    }


    // ✅ NEW: Get expiries for an underlying
    [HttpGet("derivatives/expiries")]
    public async Task<IActionResult> GetExpiries(
        [FromQuery] string underlying,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(underlying))
            return BadRequest(new { message = "underlying is required" });

        var result = await _derivativesInstrumentService.GetExpiriesAsync(
            underlying.Trim().ToUpperInvariant(),
            cancellationToken);

        return Ok(result);
    }

    // ✅ NEW: Get option chain
    [HttpGet("derivatives/chain")]
    public async Task<IActionResult> GetOptionChain(
        [FromQuery] string underlying,
        [FromQuery] DateOnly expiry,
        [FromQuery] decimal? fromStrike,
        [FromQuery] decimal? toStrike,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(underlying))
            return BadRequest(new { message = "underlying is required" });

        var result = await _derivativesInstrumentService.GetOptionChainAsync(
            underlying.Trim().ToUpperInvariant(),
            expiry,
            fromStrike,
            toStrike,
            cancellationToken);

        return Ok(result);
    }

    // ✅ NEW: Get one exact option contract
    [HttpGet("derivatives/contract")]
    public async Task<IActionResult> GetExactContract(
        [FromQuery] string underlying,
        [FromQuery] DateOnly expiry,
        [FromQuery] decimal strike,
        [FromQuery] string optionType,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(underlying))
            return BadRequest(new { message = "underlying is required" });

        if (string.IsNullOrWhiteSpace(optionType))
            return BadRequest(new { message = "optionType is required" });

        var result = await _derivativesInstrumentService.GetExactContractAsync(
            underlying.Trim().ToUpperInvariant(),
            expiry,
            strike,
            optionType.Trim().ToUpperInvariant(),
            cancellationToken);

        if (result is null)
            return NotFound(new { message = "Matching contract not found." });

        return Ok(result);
    }

}