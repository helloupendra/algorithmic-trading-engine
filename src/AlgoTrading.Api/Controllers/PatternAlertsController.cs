using AlgoTrading.Api.Security;
using AlgoTrading.Contracts.Patterns;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Patterns;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// Candle-pattern alerts: the rules, today's alerts, the candles forming now and
/// the scanner's health. Admin-only, like the rest of the Alerts section.
/// </summary>
/// <remarks>
/// Recognition and recording happen in <see cref="CandlePatternAlertService"/>;
/// this controller reads what it recorded and edits what it watches. A rule
/// change is picked up by the next scan, within twenty seconds.
/// </remarks>
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
[ApiController]
[Route("api/[controller]")]
public class PatternAlertsController : ControllerBase
{
    private readonly TradingDbContext _db;

    public PatternAlertsController(TradingDbContext db)
    {
        _db = db;
    }

    /// <summary>Patterns with their definitions, symbol groups and timeframes.</summary>
    [HttpGet("catalog")]
    public ActionResult<PatternCatalogResponse> Catalog() => Ok(new PatternCatalogResponse
    {
        Patterns = CandlePatternCatalog.All.Select(p => new PatternInfoDto
        {
            Key = p.Key,
            Name = p.Name,
            Direction = CandlePatternCatalog.DirectionKey(p.Direction),
            Bars = p.Bars,
            Suggests = p.Suggests,
            Definition = p.Definition,
            PortedFrom = p.PortedFrom,
        }).ToList(),
        Groups = PatternSymbolGroups.Fixed.Select(g => new PatternGroupDto { Key = g.Key, Label = g.Label, Description = g.Description }).ToList(),
        Timeframes = SessionBarAggregator.SupportedTimeframes.ToList(),
    });

    [HttpGet("rules")]
    public async Task<ActionResult<List<PatternRuleDto>>> GetRules(
        [FromServices] PatternWatchPlanner planner,
        CancellationToken cancellationToken)
    {
        var rows = await _db.CandlePatternRules.AsNoTracking().OrderBy(r => r.Id).ToListAsync(cancellationToken);
        var today = IstTime.DateOf(DateTime.UtcNow);
        var result = new List<PatternRuleDto>();
        foreach (var row in rows)
        {
            // Resolved as if enabled, so a paused rule still shows what it would watch.
            var parsed = CandlePatternRules.Parse(row) with { IsEnabled = true };
            var plan = await planner.PlanAsync([parsed], today, cancellationToken);
            result.Add(ToDto(row, plan.Symbols));
        }

        return Ok(result);
    }

    [HttpPost("rules")]
    public async Task<ActionResult<PatternRuleDto>> CreateRule(
        [FromBody] SavePatternRuleRequest? request,
        [FromServices] PatternWatchPlanner planner,
        CancellationToken cancellationToken)
    {
        if (request is null) return BadRequest(new { message = "A rule is required." });
        var errors = Validate(request);
        if (errors.Count > 0) return BadRequest(new { message = string.Join(" ", errors), errors });

        var now = DateTime.UtcNow;
        var row = new CandlePatternRule { CreatedUtc = now };
        Apply(row, request, now);
        _db.CandlePatternRules.Add(row);
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(await ToDtoAsync(row, planner, cancellationToken));
    }

    [HttpPut("rules/{id:long}")]
    public async Task<ActionResult<PatternRuleDto>> UpdateRule(
        long id,
        [FromBody] SavePatternRuleRequest? request,
        [FromServices] PatternWatchPlanner planner,
        CancellationToken cancellationToken)
    {
        if (request is null) return BadRequest(new { message = "A rule is required." });
        var row = await _db.CandlePatternRules.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (row is null) return NotFound(new { message = $"Rule {id} not found." });

        var errors = Validate(request);
        if (errors.Count > 0) return BadRequest(new { message = string.Join(" ", errors), errors });

        Apply(row, request, DateTime.UtcNow);
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(await ToDtoAsync(row, planner, cancellationToken));
    }

    [HttpDelete("rules/{id:long}")]
    public async Task<IActionResult> DeleteRule(long id, CancellationToken cancellationToken)
    {
        var row = await _db.CandlePatternRules.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (row is null) return NotFound(new { message = $"Rule {id} not found." });

        _db.CandlePatternRules.Remove(row);
        await _db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Pattern alerts recorded since 00:00 IST, newest first. Filters match
    /// exactly: symbol is the canonical symbol, pattern the key, direction
    /// "bullish", "bearish" or "neutral".
    /// </summary>
    [HttpGet("events")]
    public async Task<ActionResult<List<PatternAlertDto>>> GetEvents(
        [FromQuery] string? symbol,
        [FromQuery] int? timeframe,
        [FromQuery] string? pattern,
        [FromQuery] string? direction,
        [FromQuery] int limit = 300,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 1000);
        var since = IstTime.StartOfDayUtc(IstTime.DateOf(DateTime.UtcNow));

        var query = _db.AlertEvents.AsNoTracking()
            .Where(e => e.Source == CandlePatternScanner.Source && e.OccurredUtc >= since);
        if (!string.IsNullOrWhiteSpace(symbol))
        {
            var s = symbol.Trim().ToUpperInvariant();
            query = query.Where(e => e.Symbol == s);
        }

        var rows = await query.OrderByDescending(e => e.OccurredUtc).ThenByDescending(e => e.Id)
            .Take(5000)
            .ToListAsync(cancellationToken);

        var result = rows
            .Select(e => (Row: e, Meta: PatternEventMetadata.TryRead(e.MetadataJson)))
            .Where(x => x.Meta is not null)
            .Where(x => timeframe is null || x.Meta!.Timeframe == timeframe)
            .Where(x => string.IsNullOrWhiteSpace(pattern) || string.Equals(x.Meta!.Pattern, pattern.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrWhiteSpace(direction) || string.Equals(x.Meta!.Direction, direction.Trim(), StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .Select(x => new PatternAlertDto
            {
                Id = x.Row.Id,
                OccurredUtc = x.Row.OccurredUtc,
                Symbol = x.Row.Symbol ?? string.Empty,
                DisplayName = PatternAlertText.DisplayName(x.Row.Symbol ?? string.Empty),
                Timeframe = x.Meta!.Timeframe,
                Pattern = x.Meta.Pattern,
                PatternName = x.Meta.PatternName,
                Direction = x.Meta.Direction,
                BarStartUtc = x.Meta.BarStartUtc,
                BarEndUtc = x.Meta.BarEndUtc,
                Open = x.Meta.Open,
                High = x.Meta.High,
                Low = x.Meta.Low,
                Close = x.Meta.Close,
                MinutesInBar = x.Meta.MinutesInBar,
                MinutesExpected = x.Meta.MinutesExpected,
                Title = x.Row.Title,
                Message = x.Row.Message,
                DeliveredToTelegram = x.Row.DeliveredToTelegram,
                Notify = x.Meta.Notify,
                NotifySkippedReason = x.Meta.NotifySkippedReason,
                Rules = x.Meta.Rules.ToList(),
            })
            .ToList();

        return Ok(result);
    }

    /// <summary>The candle forming on every watched symbol and timeframe, and what it would be if it closed now.</summary>
    [HttpGet("forming")]
    public async Task<ActionResult<PatternFormingResponse>> GetForming(
        [FromServices] CandlePatternScanner scanner,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var forming = await scanner.GetFormingAsync(now, cancellationToken);

        return Ok(new PatternFormingResponse
        {
            AsOfUtc = now,
            Items = forming.Select(f => new PatternFormingDto
            {
                Symbol = f.Symbol,
                DisplayName = PatternAlertText.DisplayName(f.Symbol),
                Timeframe = f.TimeframeMinutes,
                Exchange = f.Exchange,
                InSession = f.ExchangeInSession,
                BarStartUtc = f.BarStartUtc,
                BarEndUtc = f.BarEndUtc,
                Open = f.Bar?.Open,
                High = f.Bar?.High,
                Low = f.Bar?.Low,
                Close = f.Bar?.Close,
                MinutesElapsed = f.MinutesElapsed,
                MinutesExpected = f.MinutesExpected,
                MinutesWithData = f.Bar?.MinutesWithData ?? 0,
                WouldBe = f.WouldBe.Select(ToHit).ToList(),
                LastClosed = f.LastClosed.Select(ToHit).ToList(),
                LastClosedStartUtc = f.LastClosedStartUtc,
            }).ToList(),
        });
    }

    [HttpGet("status")]
    public async Task<ActionResult<PatternScannerStatusResponse>> GetStatus(
        [FromServices] PatternScannerState state,
        [FromServices] TelegramSender telegram,
        CancellationToken cancellationToken)
    {
        var snap = state.Snapshot;
        var outcome = snap.LastOutcome;
        var since = IstTime.StartOfDayUtc(IstTime.DateOf(DateTime.UtcNow));

        var today = await _db.AlertEvents.AsNoTracking()
            .Where(e => e.Source == CandlePatternScanner.Source && e.OccurredUtc >= since)
            .Select(e => new { e.MetadataJson, e.DeliveredToTelegram })
            .ToListAsync(cancellationToken);
        var metas = today.Select(e => PatternEventMetadata.TryRead(e.MetadataJson)).OfType<PatternEventMetadata>().ToList();

        return Ok(new PatternScannerStatusResponse
        {
            Enabled = snap.Enabled,
            IntervalSeconds = (int)CandlePatternAlertService.Settings.Interval.TotalSeconds,
            StartedUtc = snap.StartedUtc,
            LastScanUtc = snap.LastScanUtc,
            LastScanMilliseconds = snap.LastScanMilliseconds is { } ms ? Math.Round(ms, 1) : null,
            LastErrorUtc = snap.LastErrorUtc,
            LastError = snap.LastError,
            RuleCount = outcome?.RuleCount ?? 0,
            WatchCount = outcome?.WatchCount ?? 0,
            ClosedCandlesRead = outcome?.ClosedCandlesRead ?? 0,
            Symbols = (outcome?.Symbols ?? []).Select(s => new PatternSymbolStatusDto
            {
                Symbol = s.Symbol,
                DisplayName = PatternAlertText.DisplayName(s.Symbol),
                Exchange = s.Exchange,
                InSession = s.ExchangeInSession,
                BarsToday = s.BarsToday,
                LastBarUtc = s.LastBarUtc,
                Problem = s.Problem,
            }).ToList(),
            Unresolved = outcome?.Unresolved.ToList() ?? [],
            TelegramConfigured = telegram.IsConfigured,
            TelegramMaxMessages = CandlePatternAlertService.MaxMessages,
            TelegramWindowMinutes = (int)CandlePatternAlertService.MessageWindow.TotalMinutes,
            LastTelegramUtc = snap.LastTelegramUtc,
            TelegramMessagesSent = snap.TelegramMessagesSent,
            TelegramMessagesSuppressed = snap.TelegramMessagesSuppressed,
            TelegramMessagesFailed = snap.TelegramMessagesFailed,
            LastTelegramProblem = snap.LastTelegramProblem,
            AlertsToday = today.Count,
            DeliveredToday = today.Count(e => e.DeliveredToTelegram),
            TodayByPattern = metas.GroupBy(m => m.Pattern).Select(g => new PatternCountDto { Key = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count).ThenBy(x => x.Key, StringComparer.Ordinal).ToList(),
            TodayByTimeframe = metas.GroupBy(m => m.Timeframe).OrderBy(g => g.Key)
                .Select(g => new PatternCountDto { Key = $"{g.Key}m", Count = g.Count() }).ToList(),
        });
    }

    private static List<string> Validate(SavePatternRuleRequest r) =>
        CandlePatternRules.Validate(r.Name, r.Symbols, r.Groups, r.Timeframes, r.Patterns).ToList();

    private void Apply(CandlePatternRule row, SavePatternRuleRequest r, DateTime now)
    {
        row.Name = r.Name!.Trim();
        row.SymbolsCsv = string.Join(',', (r.Symbols ?? []).Select(CandlePatternRules.NormalizeSymbol).OfType<string>().Distinct(StringComparer.Ordinal));
        row.GroupsCsv = string.Join(',', (r.Groups ?? []).Select(PatternSymbolGroups.Normalize).OfType<string>().Distinct(StringComparer.Ordinal));
        row.TimeframesCsv = string.Join(',', (r.Timeframes ?? []).Distinct().Order());
        row.PatternsCsv = string.Join(',', (r.Patterns ?? [])
            .Select(k => CandlePatternCatalog.TryParse(k, out var p) ? (CandlePattern?)p : null)
            .OfType<CandlePattern>().Distinct().Order().Select(CandlePatternCatalog.Key));
        row.IsEnabled = r.IsEnabled;
        row.Notify = r.Notify;
        row.UpdatedBy = User.GetUserName() ?? "admin";
        row.UpdatedUtc = now;
    }

    private static async Task<PatternRuleDto> ToDtoAsync(CandlePatternRule row, PatternWatchPlanner planner, CancellationToken cancellationToken)
    {
        var parsed = CandlePatternRules.Parse(row) with { IsEnabled = true };
        var plan = await planner.PlanAsync([parsed], IstTime.DateOf(DateTime.UtcNow), cancellationToken);
        return ToDto(row, plan.Symbols);
    }

    private static PatternRuleDto ToDto(CandlePatternRule row, IReadOnlyList<string> resolved)
    {
        var parsed = CandlePatternRules.Parse(row);
        return new PatternRuleDto
        {
            Id = row.Id,
            Name = row.Name,
            Symbols = parsed.Symbols.ToList(),
            Groups = parsed.Groups.ToList(),
            Timeframes = parsed.Timeframes.ToList(),
            Patterns = parsed.Patterns.Select(CandlePatternCatalog.Key).ToList(),
            IsEnabled = row.IsEnabled,
            Notify = row.Notify,
            UpdatedBy = row.UpdatedBy,
            UpdatedUtc = row.UpdatedUtc,
            ResolvedSymbols = resolved.ToList(),
        };
    }

    private static PatternHitDto ToHit(PatternHit h)
    {
        var info = CandlePatternCatalog.Info(h.Pattern);
        return new PatternHitDto { Key = info.Key, Name = info.Name, Direction = CandlePatternCatalog.DirectionKey(h.Direction) };
    }
}
