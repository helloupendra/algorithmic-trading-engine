using System.Globalization;
using AlgoTrading.Api.Security;
using AlgoTrading.Contracts.MarketFactors;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using AlgoTrading.Infrastructure.Services.MarketFactors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// The Market factors page: what the big participants hold and trade, futures
/// build-up, GIFT Nifty and overseas markets, and the event calendar.
/// </summary>
/// <remarks>
/// Option-chain levels (OI walls, max pain, PCR) are not repeated here: the page
/// reads them from <c>api/OptionChain/view</c>, the same live answer the chain
/// page uses, so the two can never disagree.
/// </remarks>
[ApiController]
[Route("api/[controller]")]
[Authorize]
[RequireModule(PlatformModules.MarketData)]
public class MarketFactorsController : ControllerBase
{
    private readonly MarketFactorsQueries _queries;
    private readonly GlobalCuesService _globalCues;
    private readonly MarketFactorsSync _sync;
    private readonly TradingDbContext _db;

    public MarketFactorsController(MarketFactorsQueries queries, GlobalCuesService globalCues, MarketFactorsSync sync, TradingDbContext db)
    {
        _queries = queries;
        _globalCues = globalCues;
        _sync = sync;
        _db = db;
    }

    /// <summary>FII/DII cash figures and participant-wise index derivatives positions, newest day first.</summary>
    [HttpGet("flows")]
    public async Task<ActionResult<MarketFlowsResponse>> Flows([FromQuery] int days = 20, CancellationToken cancellationToken = default)
        => Ok(await _queries.FlowsAsync(days, cancellationToken));

    /// <summary>Index futures build-up (daily, and live against the last close), plus the day's stock futures by build-up.</summary>
    [HttpGet("futures")]
    public async Task<ActionResult<MarketFuturesResponse>> Futures([FromQuery] int days = 10, CancellationToken cancellationToken = default)
        => Ok(await _queries.FuturesAsync(days, cancellationToken));

    /// <summary>GIFT Nifty and overseas markets; shared by every viewer and refreshed at most every two minutes.</summary>
    [HttpGet("global")]
    public async Task<ActionResult<GlobalCuesResponse>> Global([FromQuery] bool force = false, CancellationToken cancellationToken = default)
    {
        // A forced refresh is an admin's lever, so one viewer cannot empty the cache for everyone.
        bool canForce = force && User.IsInRole(UserRoles.Admin);
        var snapshot = await _globalCues.GetAsync(canForce, cancellationToken);

        var response = new GlobalCuesResponse
        {
            ServerUtc = DateTime.UtcNow,
            FetchedUtc = snapshot.FetchedUtc,
            Gift = ToDto(snapshot.Gift),
            GiftExpiry = snapshot.GiftExpiry,
            GiftContractsTraded = snapshot.GiftContractsTraded,
            Markets = snapshot.Markets.Select(ToDto).ToList(),
            SourceNote = snapshot.SourceNote,
        };

        if (snapshot.GiftExpiry is not null && snapshot.Gift.LastPrice is > 0)
        {
            var (close, date) = await _queries.NseFutureCloseAsync(snapshot.GiftExpiry.Value, cancellationToken);
            if (close is > 0)
            {
                response.NseFutureClose = close;
                response.NseFutureCloseDate = date;
                response.IndicatedGapPoints = snapshot.Gift.LastPrice - close;
                response.IndicatedGapPercent = Math.Round((snapshot.Gift.LastPrice.Value - close.Value) / close.Value * 100m, 2);
            }
        }

        return Ok(response);
    }

    /// <summary>Scheduled events, NSE holidays and index option expiries between two IST dates (default: 7 days back to 30 ahead).</summary>
    [HttpGet("events")]
    public async Task<ActionResult<MarketEventsResponse>> Events([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken cancellationToken)
    {
        var today = IstTime.DateOf(DateTime.UtcNow);
        var start = from ?? today.AddDays(-7);
        var end = to ?? today.AddDays(30);
        if (end < start) return BadRequest(new { message = "'to' is before 'from'." });
        if (end.DayNumber - start.DayNumber > 400) return BadRequest(new { message = "Ask for at most 400 days at a time." });
        return Ok(await _queries.EventsAsync(start, end, cancellationToken));
    }

    [HttpPost("events")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<ActionResult<MarketEventDto>> AddEvent([FromBody] SaveMarketEventRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Title)) return BadRequest(new { message = "A title is required." });
        TimeOnly? time = null;
        if (!string.IsNullOrWhiteSpace(request.TimeIst))
        {
            if (!TimeOnly.TryParseExact(request.TimeIst.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
                return BadRequest(new { message = "Time must be HH:mm (IST)." });
            time = t;
        }

        string title = request.Title.Trim();
        string category = string.IsNullOrWhiteSpace(request.Category) ? "Other" : request.Category.Trim();
        if (await _db.MarketEvents.AnyAsync(e => e.Date == request.Date && e.Category == category && e.Title == title, cancellationToken))
            return Conflict(new { message = "That event is already on the calendar." });

        var row = new MarketEvent
        {
            Date = request.Date,
            TimeIst = time,
            Region = string.IsNullOrWhiteSpace(request.Region) ? "IN" : request.Region.Trim().ToUpperInvariant(),
            Category = category,
            Title = title,
            Importance = Math.Clamp(request.Importance, 1, 3),
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
            Source = string.IsNullOrWhiteSpace(request.Source) ? null : request.Source.Trim(),
            UpdatedBy = User.GetUserName() ?? "admin",
        };
        _db.MarketEvents.Add(row);
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new MarketEventDto
        {
            Id = row.Id,
            Date = row.Date,
            TimeIst = row.TimeIst?.ToString("HH:mm", CultureInfo.InvariantCulture),
            Region = row.Region,
            Category = row.Category,
            Title = row.Title,
            Importance = row.Importance,
            Notes = row.Notes,
            Source = row.Source,
            Kind = "event",
        });
    }

    [HttpDelete("events/{id:long}")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> DeleteEvent(long id, CancellationToken cancellationToken)
    {
        var row = await _db.MarketEvents.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (row is null) return NotFound(new { message = "No such event." });
        _db.MarketEvents.Remove(row);
        await _db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    /// <summary>How the evening fetches last went.</summary>
    [HttpGet("status")]
    public ActionResult<List<MarketFactorsDatasetStatusDto>> Status() => Ok(_queries.Status());

    /// <summary>Fetch the missing NSE days now instead of waiting for the evening job.</summary>
    [HttpPost("sync")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<ActionResult<MarketFactorsSyncResponse>> Sync([FromQuery] int sessions = 20, CancellationToken cancellationToken = default)
    {
        var report = await _sync.RunAsync(Math.Clamp(sessions, 1, 60), cancellationToken);
        return Ok(new MarketFactorsSyncResponse { Lines = report.Lines.ToList(), Status = _queries.Status() });
    }

    private static GlobalCueDto ToDto(GlobalCueItem item) => new()
    {
        Symbol = item.Symbol,
        Name = item.Name,
        Group = item.Group,
        Currency = item.Currency,
        LastPrice = item.LastPrice,
        PreviousClose = item.PreviousClose,
        Change = item.Change,
        ChangePercent = item.ChangePercent,
        AsOfUtc = item.AsOfUtc,
        Error = item.Error,
    };
}
