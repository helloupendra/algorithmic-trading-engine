using AlgoTrading.Domain.Constants;
using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.UseCases.Instruments;
using AlgoTrading.Contracts.Instruments;
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using AlgoTrading.Domain.Instruments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// Exposes endpoints to search the instrument universe, import CSV masters, and resolve derivative expiries and chains.
/// </summary>
// Option chains and instrument search are market views.
[RequireModule(PlatformModules.MarketData)]
[ApiController]
[Route("api/[controller]")]
public class InstrumentsController : ControllerBase
{
    private const string FnoUnderlyingsCacheKey = InstrumentMasterService.FnoUnderlyingsCacheKey;
    private static readonly TimeSpan FnoUnderlyingsCacheTtl = TimeSpan.FromMinutes(5);

    private readonly TradingDbContext _dbContext;
    private readonly ImportInstrumentsFromFileUseCase _importInstrumentsFromFileUseCase;
    private readonly IDerivativesInstrumentService _derivativesInstrumentService;
    private readonly ILotSizeResolver _lotSizeResolver;
    private readonly IMemoryCache _cache;

    public InstrumentsController(
        TradingDbContext dbContext,
        ImportInstrumentsFromFileUseCase importInstrumentsFromFileUseCase,
        IDerivativesInstrumentService derivativesInstrumentService,
        ILotSizeResolver lotSizeResolver,
        IMemoryCache cache)
    {
        _dbContext = dbContext;
        _importInstrumentsFromFileUseCase = importInstrumentsFromFileUseCase;
        _derivativesInstrumentService = derivativesInstrumentService;
        _lotSizeResolver = lotSizeResolver;
        _cache = cache;
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
    public async Task<IActionResult> Search(
        [FromQuery] string query,
        [FromQuery] string? type,
        [FromQuery] bool includeExpired,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
            return BadRequest(new { message = "query is required" });

        // Upper-cased on both sides: PostgreSQL's LIKE is case-sensitive, so
        // "banknifty" used to return nothing at all while "BANKNIFTY" returned
        // fifty rows.
        string q = query.Trim().ToUpperInvariant();

        // "BANKNIFTY" is what every strategy calls it; the instrument is listed
        // as NSE:NIFTYBANK-INDEX, which no substring of the query can reach.
        string? alias = UnderlyingCatalog.IsIndex(q) ? UnderlyingCatalog.SpotSymbolFor(q) : null;

        var dbQuery = _dbContext.Instruments
            .AsNoTracking()
            .Where(x =>
                x.Symbol.ToUpper().Contains(q) ||
                x.Description.ToUpper().Contains(q) ||
                (alias != null && x.Symbol == alias));

        // Expired contracts stay in the master - a backfill still needs them -
        // but they must not be offered to anything trading TODAY. The manual
        // ticket showed a SENSEX put that expired five days earlier, waited for
        // a tick that could never come, and explained the blank prices as "add
        // it to the watchlist". 802 F&O rows were in that state.
        if (!includeExpired)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5));
            dbQuery = dbQuery.Where(x => x.ExpiryDate == null || x.ExpiryDate >= today);
        }

        if (!string.IsNullOrEmpty(type))
        {
            if (type == "EQ") dbQuery = dbQuery.Where(x => x.InstrumentType == "EQ" || x.InstrumentType == null);
            else if (type == "FUT") dbQuery = dbQuery.Where(x => x.InstrumentType != null && x.InstrumentType.Contains("FUT"));
            // OptionType defaults to "" and is never null in the table, so the old
            // `!= null` here matched every instrument in the master — the Options
            // filter returned bonds, funds and indices alike.
            else if (type == "OPT") dbQuery = dbQuery.Where(x => (x.InstrumentType != null && (x.InstrumentType.Contains("OPT") || x.InstrumentType == "CE" || x.InstrumentType == "PE")) || (x.OptionType != null && x.OptionType != ""));
            else if (type == "INDEX") dbQuery = dbQuery.Where(x => (x.InstrumentType != null && x.InstrumentType.Contains("INDEX")) || (x.Segment != null && x.Segment.Contains("INDEX")));
        }

        // Ordered by how well each row answers the query, and only then cut to
        // fifty. Cutting first — which is what alphabetical order amounted to —
        // dropped NSE:TCS-EQ off the end of a page of bonds.
        var rows = await dbQuery
            .OrderBy(InstrumentSearchRanking.RankBy(q, alias))
            .ThenBy(InstrumentSearchRanking.KindRank())
            // Nearest expiry first among a name's contracts: "crudeoil" used to
            // list DEC, NOV, OCT and only then the SEP future that trades today.
            .ThenBy(x => x.ExpiryDate)
            .ThenBy(x => x.Symbol.Length)
            .ThenBy(x => x.Symbol)
            .Take(50)
            .ToListAsync(cancellationToken);

        return Ok(rows);
    }


    /// <summary>
    /// Bulk-imports an instrument master CSV from a path on the API host.
    /// Admin-only: it reads an arbitrary server-side file and rewrites the
    /// instrument universe every strategy resolves contracts against.
    /// </summary>
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
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

        // The F&O universe just changed: drop the cached underlyings list so the
        // launch dialog shows the imported contracts immediately, not in 5 minutes.
        _cache.Remove(FnoUnderlyingsCacheKey);

        return Ok(result);
    }


    /// <summary>
    /// The FYERS symbol masters: file on this host, rows in the database per
    /// exchange and segment, last refresh, and whether a refresh is running.
    /// </summary>
    [HttpGet("masters")]
    public Task<InstrumentMastersResponse> GetMasters([FromServices] InstrumentMasterService masters, CancellationToken cancellationToken)
        => masters.GetStatusAsync(cancellationToken);

    /// <summary>
    /// Downloads the latest masters from FYERS and imports them, in the
    /// background (the page polls GET masters). Admin-only: it rewrites the
    /// instrument universe every strategy resolves contracts against.
    /// 202 when started, 409 when a refresh is already running.
    /// </summary>
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    [HttpPost("masters/refresh")]
    public IActionResult RefreshMasters([FromServices] InstrumentMasterService masters, [FromQuery] string? only)
    {
        var names = string.IsNullOrWhiteSpace(only)
            ? null
            : only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var by = User.Identity?.Name ?? "admin";
        if (!masters.TryStart(by, names))
            return Conflict(new { message = "A masters refresh is already running." });
        return Accepted(new { message = "Refresh started.", masters = names ?? InstrumentMasterService.Known.Select(k => k.Name).ToArray() });
    }

    /// <summary>
    /// Every underlying with at least one unexpired option contract, with the
    /// facts the launch dialog shows before the user picks one: spot symbol, lot
    /// size (and where it came from), strike step, expiries and contract count.
    /// Index underlyings first in catalog order, then stocks alphabetically.
    /// Cached for five minutes; a master import evicts the entry, and an empty
    /// result is never cached (the very next import must be visible at once).
    /// </summary>
    [HttpGet("derivatives/underlyings")]
    public async Task<ActionResult<List<FnoUnderlyingResponse>>> GetFnoUnderlyings(CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(FnoUnderlyingsCacheKey, out List<FnoUnderlyingResponse>? cached) && cached is not null)
        {
            return Ok(cached);
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // One grouped query: distinct (underlying, exchange, expiry, strike) with
        // contract counts — small enough to shape in memory, and it avoids a
        // per-underlying round trip for expiries and strike steps.
        var rows = await _dbContext.Instruments
            .AsNoTracking()
            .Where(x => x.IsEnabled
                        && x.Underlying != ""
                        && (x.OptionType == "CE" || x.OptionType == "PE")
                        && x.ExpiryDate.HasValue
                        && x.ExpiryDate >= today)
            .GroupBy(x => new { x.Underlying, x.Exchange, x.ExpiryDate, x.StrikePrice })
            .Select(g => new
            {
                g.Key.Underlying,
                g.Key.Exchange,
                g.Key.ExpiryDate,
                g.Key.StrikePrice,
                Count = g.Count(),
                LotSize = g.Max(x => x.LotSize)
            })
            .ToListAsync(cancellationToken);

        var result = new List<FnoUnderlyingResponse>();

        foreach (var group in rows.GroupBy(x => x.Underlying.Trim().ToUpperInvariant()))
        {
            var underlying = group.Key;
            var expiries = group
                .Select(x => x.ExpiryDate!.Value)
                .Distinct()
                .OrderBy(x => x)
                .ToList();
            if (expiries.Count == 0) continue;

            var nextExpiry = expiries[0];

            var nextExpiryRows = group.Where(x => x.ExpiryDate == nextExpiry).ToList();
            var strikes = nextExpiryRows
                .Where(x => x.StrikePrice.HasValue && x.StrikePrice > 0)
                .Select(x => x.StrikePrice!.Value)
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            decimal strikeStep = 0m;
            for (int i = 1; i < strikes.Count; i++)
            {
                var diff = strikes[i] - strikes[i - 1];
                if (diff > 0 && (strikeStep == 0m || diff < strikeStep)) strikeStep = diff;
            }
            if (strikeStep <= 0m) strikeStep = UnderlyingCatalog.FallbackStrikeStep(underlying);

            var masterLot = nextExpiryRows.Select(x => x.LotSize).Where(x => x is > 0).Max();
            int lotSize;
            string lotSizeSource;
            if (masterLot is > 0)
            {
                lotSize = masterLot.Value;
                lotSizeSource = "master";
            }
            else
            {
                var resolved = await _lotSizeResolver.ResolveForUnderlyingAsync(underlying, cancellationToken);
                lotSize = resolved.LotSize;
                lotSizeSource = resolved.Source;
            }

            var exchange = group
                .GroupBy(x => x.Exchange)
                .OrderByDescending(g => g.Sum(x => x.Count))
                .Select(g => g.Key)
                .FirstOrDefault() ?? string.Empty;

            result.Add(new FnoUnderlyingResponse
            {
                Underlying = underlying,
                Exchange = exchange,
                SpotSymbol = UnderlyingCatalog.SpotSymbolFor(underlying),
                LotSize = lotSize,
                LotSizeSource = lotSizeSource,
                StrikeStep = strikeStep,
                NextExpiry = nextExpiry.ToString("yyyy-MM-dd"),
                Expiries = expiries.Take(8).Select(x => x.ToString("yyyy-MM-dd")).ToList(),
                OptionContracts = group.Sum(x => x.Count)
            });
        }

        result = result
            .OrderBy(x => UnderlyingCatalog.SortRank(x.Underlying))
            .ThenBy(x => x.Underlying, StringComparer.Ordinal)
            .ToList();

        if (result.Count > 0)
        {
            _cache.Set(FnoUnderlyingsCacheKey, result, FnoUnderlyingsCacheTtl);
        }

        return Ok(result);
    }

    // Get expiries for an underlying
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