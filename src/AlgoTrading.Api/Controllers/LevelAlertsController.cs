using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services;
using AlgoTrading.Contracts.Alerts;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.LevelAlerts;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// Level alerts: "tell me when NIFTY crosses 24,800", "when NIFTY's PCR goes
/// above 1.3". Each user's own rules, what they last read, and when they fired.
/// </summary>
/// <remarks>
/// <para>
/// Under the market-data grant, like the chain and the watchlist they are set
/// from. A trader reads and changes only their own rules and fires; an admin
/// reads and changes everyone's. Another user's rule answers 404, not 403, so
/// a trader cannot learn which ids exist. The owner always comes from the
/// sign-in token, never from the request.
/// </para>
/// <para>
/// Watching happens in <see cref="LevelAlertService"/>; this controller edits
/// what it watches and reads what it recorded. A change is picked up by the
/// next scan, within five seconds.
/// </para>
/// </remarks>
[RequireModule(PlatformModules.MarketData)]
[ApiController]
[Route("api/[controller]")]
public class LevelAlertsController : ControllerBase
{
    private readonly TradingDbContext _db;
    private readonly LevelAlertState _state;

    public LevelAlertsController(TradingDbContext db, LevelAlertState state)
    {
        _db = db;
        _state = state;
    }

    /// <summary>The caller's rules, newest first; an admin's view holds everyone's unless <paramref name="mine"/>.</summary>
    [HttpGet("rules")]
    public async Task<ActionResult<List<LevelAlertRuleDto>>> GetRules([FromQuery] bool mine = false, CancellationToken cancellationToken = default)
    {
        var rows = await Visible(_db.LevelAlertRules, mine).AsNoTracking()
            .OrderByDescending(r => r.Id)
            .ToListAsync(cancellationToken);
        var owners = await OwnersAsync(rows.Select(r => r.UserId), cancellationToken);
        var readings = ReadingsByKey();
        var today = IstTime.DateOf(DateTime.UtcNow);

        return Ok(rows.Select(r => ToDto(r, owners, readings, today)).ToList());
    }

    [HttpPost("rules")]
    public async Task<ActionResult<LevelAlertRuleDto>> CreateRule(
        [FromBody] SaveLevelAlertRuleRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null) return BadRequest(new { message = "A rule is required." });
        var (rule, errors) = await CheckAsync(request, cancellationToken);
        if (rule is null) return BadRequest(new { message = string.Join(" ", errors), errors });

        long me = User.GetRequiredUserId();
        int held = await _db.LevelAlertRules.CountAsync(r => r.UserId == me, cancellationToken);
        if (held >= LevelAlertScanner.MaxRulesPerUser)
        {
            return BadRequest(new { message = $"You already have {held} level alerts, the most one account may hold. Delete one first." });
        }

        var now = DateTime.UtcNow;
        var row = new LevelAlertRule { UserId = me, CreatedUtc = now };
        Apply(row, rule, request, now);
        _db.LevelAlertRules.Add(row);
        await _db.SaveChangesAsync(cancellationToken);

        HttpContext.Describe($"Level alert created: {LevelAlertText.Describe(row.Kind, row.Target, row.Condition, row.Level)}", "level-alert", row.Id.ToString());
        return Ok(await ToDtoAsync(row, cancellationToken));
    }

    [HttpPut("rules/{id:long}")]
    public async Task<ActionResult<LevelAlertRuleDto>> UpdateRule(
        long id,
        [FromBody] SaveLevelAlertRuleRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null) return BadRequest(new { message = "A rule is required." });
        var row = await Visible(_db.LevelAlertRules, mine: false).FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (row is null) return NotFound(new { message = $"Level alert {id} not found." });

        var (rule, errors) = await CheckAsync(request, cancellationToken);
        if (rule is null) return BadRequest(new { message = string.Join(" ", errors), errors });

        Apply(row, rule, request, DateTime.UtcNow);
        await _db.SaveChangesAsync(cancellationToken);

        HttpContext.Describe($"Level alert {row.Status}: {LevelAlertText.Describe(row.Kind, row.Target, row.Condition, row.Level)}", "level-alert", row.Id.ToString());
        return Ok(await ToDtoAsync(row, cancellationToken));
    }

    /// <summary>Deletes the rule. What it fired stays in the history.</summary>
    [HttpDelete("rules/{id:long}")]
    public async Task<IActionResult> DeleteRule(long id, CancellationToken cancellationToken)
    {
        var row = await Visible(_db.LevelAlertRules, mine: false).FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (row is null) return NotFound(new { message = $"Level alert {id} not found." });

        _db.LevelAlertRules.Remove(row);
        await _db.SaveChangesAsync(cancellationToken);

        HttpContext.Describe($"Level alert deleted: {LevelAlertText.Describe(row.Kind, row.Target, row.Condition, row.Level)}", "level-alert", id.ToString());
        return NoContent();
    }

    /// <summary>What fired, newest first: the caller's own, or for an admin everyone's unless <paramref name="mine"/>.</summary>
    [HttpGet("fired")]
    public async Task<ActionResult<List<LevelAlertFireDto>>> GetFired(
        [FromQuery] bool mine = false,
        [FromQuery] long? ruleId = null,
        [FromQuery] int limit = 200,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        var query = Visible(_db.LevelAlertFires, mine).AsNoTracking();
        if (ruleId is { } rid) query = query.Where(f => f.RuleId == rid);

        var rows = await query.OrderByDescending(f => f.FiredUtc).ThenByDescending(f => f.Id).Take(limit).ToListAsync(cancellationToken);
        var owners = await OwnersAsync(rows.Select(f => f.UserId), cancellationToken);

        return Ok(rows.Select(f => new LevelAlertFireDto
        {
            Id = f.Id,
            RuleId = f.RuleId,
            UserId = f.UserId,
            OwnerUserName = owners.GetValueOrDefault(f.UserId)?.UserName,
            FiredUtc = AsUtc(f.FiredUtc),
            Kind = f.Kind,
            Target = f.Target,
            DisplayName = LevelAlertText.Subject(f.Kind, f.Target),
            Condition = f.Condition,
            Level = f.Level,
            Value = f.Value,
            ValueAsOfUtc = AsUtc(f.ValueAsOfUtc),
            PreviousValue = f.PreviousValue,
            PreviousAsOfUtc = AsUtc(f.PreviousAsOfUtc),
            Title = f.Title,
            Message = f.Message,
            DeliveredToTelegram = f.DeliveredToTelegram,
            TelegramNote = f.TelegramNote,
            Seen = f.SeenUtc is not null,
        }).ToList());
    }

    /// <summary>Marks the caller's own new fires as seen; an admin looking at a trader's never marks them for the trader.</summary>
    [HttpPost("fired/seen")]
    public async Task<ActionResult<object>> MarkSeen(CancellationToken cancellationToken)
    {
        long me = User.GetRequiredUserId();
        var unseen = await _db.LevelAlertFires.Where(f => f.UserId == me && f.SeenUtc == null).Take(1000).ToListAsync(cancellationToken);
        var now = DateTime.UtcNow;
        foreach (var fire in unseen) fire.SeenUtc = now;
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new { marked = unseen.Count });
    }

    /// <summary>The top bar's count: the caller's own fires not yet seen, and today's.</summary>
    [HttpGet("summary")]
    public async Task<ActionResult<LevelAlertSummaryDto>> GetSummary(CancellationToken cancellationToken)
    {
        long me = User.GetRequiredUserId();
        var since = IstTime.StartOfDayUtc(IstTime.DateOf(DateTime.UtcNow));
        var mine = _db.LevelAlertFires.AsNoTracking().Where(f => f.UserId == me);

        var newest = await mine.Where(f => f.SeenUtc == null)
            .OrderByDescending(f => f.FiredUtc)
            .Select(f => new { f.FiredUtc, f.Title })
            .FirstOrDefaultAsync(cancellationToken);

        return Ok(new LevelAlertSummaryDto
        {
            Unseen = await mine.CountAsync(f => f.SeenUtc == null, cancellationToken),
            FiredToday = await mine.CountAsync(f => f.FiredUtc >= since, cancellationToken),
            NewestUtc = newest is null ? null : AsUtc(newest.FiredUtc),
            NewestTitle = newest?.Title,
        });
    }

    /// <summary>The service's health, and what it is watching: for a trader, only the targets of their own rules.</summary>
    [HttpGet("status")]
    public async Task<ActionResult<LevelAlertStatusDto>> GetStatus(
        [FromServices] TelegramSender telegram,
        CancellationToken cancellationToken)
    {
        var snap = _state.Snapshot;
        var watching = snap.Readings.AsEnumerable();
        if (!User.IsAdmin())
        {
            long me = User.GetRequiredUserId();
            var keys = (await _db.LevelAlertRules.AsNoTracking().Where(r => r.UserId == me)
                    .Select(r => new { r.Kind, r.Target }).ToListAsync(cancellationToken))
                .Select(r => LevelAlertReader.KeyOf(r.Kind, r.Target))
                .ToHashSet(StringComparer.Ordinal);
            watching = watching.Where(r => keys.Contains(LevelAlertReader.KeyOf(r.Kind, r.Target)));
        }

        return Ok(new LevelAlertStatusDto
        {
            Enabled = snap.Enabled,
            IntervalSeconds = (int)LevelAlertService.Interval.TotalSeconds,
            PcrIntervalSeconds = (int)LevelAlertScanner.PcrInterval.TotalSeconds,
            PriceFreshSeconds = (int)LevelCross.PriceFreshFor.TotalSeconds,
            PcrFreshSeconds = (int)LevelCross.PcrFreshFor.TotalSeconds,
            StartedUtc = snap.StartedUtc,
            LastScanUtc = snap.LastScanUtc,
            LastScanMilliseconds = snap.LastScanMilliseconds is { } ms ? Math.Round(ms, 1) : null,
            LastErrorUtc = snap.LastErrorUtc,
            LastError = snap.LastError,
            ActiveRules = User.IsAdmin() ? snap.ActiveRules : 0,
            MaxRulesPerUser = LevelAlertScanner.MaxRulesPerUser,
            DefaultPriceRearmPercent = LevelCross.DefaultPriceRearmPercent,
            DefaultPcrRearmPoints = LevelCross.DefaultPcrRearmPoints,
            Watching = watching.Select(ToReadingDto).ToList(),
            Skipped = User.IsAdmin() ? snap.Skipped.ToList() : [],
            TelegramConfigured = telegram.IsConfigured,
            TelegramSystemChatConfigured = telegram.IsSystemChatConfigured,
            TelegramMaxMessages = LevelAlertService.MaxMessages,
            TelegramWindowMinutes = (int)LevelAlertService.MessageWindow.TotalMinutes,
            TelegramMessagesSent = snap.TelegramMessagesSent,
            TelegramMessagesSuppressed = snap.TelegramMessagesSuppressed,
            TelegramMessagesFailed = snap.TelegramMessagesFailed,
            LastTelegramProblem = snap.LastTelegramProblem,
        });
    }

    /// <summary>
    /// What a target reads now, read the way the service reads it, for the
    /// form: the user sees the number their level will be compared with, and
    /// whether there is one at all, before they save.
    /// </summary>
    [HttpGet("now")]
    public async Task<ActionResult<LevelAlertReadingDto>> GetNow(
        [FromQuery] string? kind,
        [FromQuery] string? target,
        [FromServices] LevelAlertReader reader,
        CancellationToken cancellationToken)
    {
        var k = LevelAlertRules.NormalizeKind(kind);
        var t = k is null ? null : LevelAlertRules.NormalizeTarget(k, target);
        if (k is null || t is null) return BadRequest(new { message = "Name a price symbol (NSE:NIFTY50-INDEX) or a chain's underlying (NIFTY)." });

        var nowUtc = DateTime.UtcNow;
        var session = reader.SessionOf(k, t, nowUtc);
        LevelReading reading;
        if (k == LevelAlertKinds.Price)
        {
            var prices = await reader.PricesAsync(new List<string> { t }, new Dictionary<string, LevelSession> { [t] = session }, nowUtc, cancellationToken);
            reading = prices[t];
        }
        else
        {
            reading = await reader.PcrAsync(t, session, nowUtc, known: null, valueWhenStale: true, cancellationToken);
        }

        return Ok(ToReadingDto(reading));
    }

    // ------------------------------------------------------------------

    private sealed record CheckedRule(string Kind, string Target, string Condition, decimal Level);

    /// <summary>The request normalised, or every reason it cannot be saved.</summary>
    private async Task<(CheckedRule? Rule, IReadOnlyList<string> Errors)> CheckAsync(SaveLevelAlertRuleRequest r, CancellationToken cancellationToken)
    {
        var errors = LevelAlertRules.Validate(r.Kind, r.Target, r.Condition, r.Level, r.Repeat, r.RearmBy, r.Note, r.ExpiresOn,
            IstTime.DateOf(DateTime.UtcNow));
        if (errors.Count > 0) return (null, errors);

        var kind = LevelAlertRules.NormalizeKind(r.Kind)!;
        var target = LevelAlertRules.NormalizeTarget(kind, r.Target)!;

        // Only what the platform knows: a typo would otherwise wait for a quote that never comes.
        bool known = kind == LevelAlertKinds.Price
            ? await _db.Instruments.AnyAsync(i => i.Symbol == target, cancellationToken)
              || await _db.LiveQuotesLatest.AnyAsync(q => q.Symbol == target, cancellationToken)
            : UnderlyingCatalog.IsIndex(target) || UnderlyingCatalog.IsCommodity(target)
              || await _db.OptionChainSnapshots.AnyAsync(x => x.Underlying == target, cancellationToken);
        if (!known)
        {
            return (null, [kind == LevelAlertKinds.Price
                ? $"{target} is not a symbol the platform knows: it is neither in the instrument master nor quoted by a feed."
                : $"{target} has no option chain on the platform: it is not an index or commodity the recorder follows, and none was ever captured."]);
        }

        return (new CheckedRule(kind, target, LevelAlertRules.NormalizeCondition(r.Condition)!, r.Level!.Value), []);
    }

    private void Apply(LevelAlertRule row, CheckedRule rule, SaveLevelAlertRuleRequest r, DateTime nowUtc)
    {
        // A different level, or a rule coming back from paused or fired, starts
        // armed. A save that only changes the note keeps a repeating rule's
        // re-arm state, so sitting on its level it still fires once.
        bool changed = row.Kind != rule.Kind || row.Target != rule.Target || row.Condition != rule.Condition || row.Level != rule.Level;
        bool resumed = r.Active && row.Status != LevelAlertStatus.Active;

        row.Kind = rule.Kind;
        row.Target = rule.Target;
        row.Condition = rule.Condition;
        row.Level = rule.Level;
        row.Repeat = r.Repeat;
        row.RearmBy = r.Repeat
            ? r.RearmBy ?? (rule.Kind == LevelAlertKinds.Pcr ? LevelCross.DefaultPcrRearmPoints : LevelCross.DefaultPriceRearmPercent)
            : null;
        row.Note = string.IsNullOrWhiteSpace(r.Note) ? null : r.Note.Trim();
        row.ExpiresOn = r.ExpiresOn;
        row.Notify = r.Notify;
        row.Status = r.Active ? LevelAlertStatus.Active : LevelAlertStatus.Paused;
        if (changed || resumed || !r.Repeat) row.Armed = true;
        row.UpdatedUtc = nowUtc;
    }

    /// <summary>The rows the caller may see: their own, or for an admin everyone's unless they ask for their own.</summary>
    private IQueryable<LevelAlertRule> Visible(IQueryable<LevelAlertRule> rules, bool mine)
    {
        if (User.IsAdmin() && !mine) return rules;
        long me = User.GetRequiredUserId();
        return rules.Where(r => r.UserId == me);
    }

    private IQueryable<LevelAlertFire> Visible(IQueryable<LevelAlertFire> fires, bool mine)
    {
        if (User.IsAdmin() && !mine) return fires;
        long me = User.GetRequiredUserId();
        return fires.Where(f => f.UserId == me);
    }

    private sealed record Owner(string UserName, string Role);

    /// <summary>Owners' names and roles, for an admin's view and for whether Telegram applies.</summary>
    private async Task<Dictionary<long, Owner>> OwnersAsync(IEnumerable<long> ids, CancellationToken cancellationToken)
    {
        var distinct = ids.Distinct().ToList();
        return await _db.AppUsers.AsNoTracking().Where(u => distinct.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => new Owner(u.UserName, u.Role), cancellationToken);
    }

    private Dictionary<string, LevelReading> ReadingsByKey() =>
        _state.Snapshot.Readings.ToDictionary(r => LevelAlertReader.KeyOf(r.Kind, r.Target), StringComparer.Ordinal);

    private async Task<LevelAlertRuleDto> ToDtoAsync(LevelAlertRule row, CancellationToken cancellationToken) =>
        ToDto(row, await OwnersAsync([row.UserId], cancellationToken), ReadingsByKey(), IstTime.DateOf(DateTime.UtcNow));

    private static LevelAlertRuleDto ToDto(
        LevelAlertRule row,
        IReadOnlyDictionary<long, Owner> owners,
        IReadOnlyDictionary<string, LevelReading> readings,
        DateOnly today)
    {
        var reading = readings.GetValueOrDefault(LevelAlertReader.KeyOf(row.Kind, row.Target));
        var owner = owners.GetValueOrDefault(row.UserId);
        bool expired = row.Status != LevelAlertStatus.Fired && row.ExpiresOn is { } last && last < today;

        return new LevelAlertRuleDto
        {
            Id = row.Id,
            UserId = row.UserId,
            OwnerUserName = owner?.UserName,
            Kind = row.Kind,
            Target = row.Target,
            DisplayName = LevelAlertText.Subject(row.Kind, row.Target),
            Condition = row.Condition,
            Level = row.Level,
            Description = LevelAlertText.Describe(row.Kind, row.Target, row.Condition, row.Level),
            Repeat = row.Repeat,
            RearmBy = row.RearmBy,
            RearmUnit = row.Kind == LevelAlertKinds.Pcr ? "points" : "percent",
            Note = row.Note,
            ExpiresOn = row.ExpiresOn,
            Notify = row.Notify,
            TelegramApplies = owner?.Role == UserRoles.Admin,
            Status = expired ? LevelAlertStatus.Expired : row.Status,
            Armed = row.Armed,
            LastFiredUtc = row.LastFiredUtc is { } f ? AsUtc(f) : null,
            FireCount = row.FireCount,
            CreatedUtc = AsUtc(row.CreatedUtc),
            UpdatedUtc = AsUtc(row.UpdatedUtc),
            Now = reading is null ? null : ToReadingDto(reading),
            Side = reading?.Value is { } v ? LevelCross.SideOf(v, row.Level) : null,
        };
    }

    private static LevelAlertReadingDto ToReadingDto(LevelReading r) => new()
    {
        Kind = r.Kind,
        Target = r.Target,
        DisplayName = LevelAlertText.Subject(r.Kind, r.Target),
        Exchange = r.Exchange,
        InSession = r.InSession,
        Value = r.Value,
        AsOfUtc = r.AsOfUtc is { } at ? AsUtc(at) : null,
        State = r.State,
        Detail = r.Detail,
        Context = r.Context,
    };

    // Serialised with its Z only when the kind says UTC; a value read back from the database comes out Unspecified.
    private static DateTime AsUtc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
