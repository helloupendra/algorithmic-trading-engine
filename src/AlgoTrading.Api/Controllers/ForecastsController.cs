using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Domain.ValueObjects;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Persistence.Configurations;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// The Analysis module: model versions, the forecasts they issue before each
/// session, the outcomes written after it, and the scoreboard that says
/// whether any of it beats a baseline.
/// </summary>
/// <remarks>
/// <para>
/// The contract is docs/modules/analysis.md. What this controller is for is
/// its first rule, <em>written before, scored after</em>: the API stamps a
/// forecast with its own clock, refuses one for a session that has already
/// opened on the underlying's exchange, refuses a second one for the same
/// model version, index and session, and accepts an outcome once and only
/// after that session's close. None of that is left to the caller, because
/// the caller is the thing being tested.
/// </para>
/// <para>
/// Authorization is layered, and ASP.NET requires every layer to pass. The
/// class needs the <c>analysis</c> module, which admins and the Service
/// account hold by role and a trader only by grant. Writes also need the Admin
/// or Service role: the Python models issue and score as the Service account;
/// a trader who may read the scoreboard may not write to it.
/// </para>
/// </remarks>
[ApiController]
[Route("api/Forecasts")]
[Authorize]
[RequireModule(PlatformModules.Analysis)]
public partial class ForecastsController : ControllerBase
{
    private const string Writers = $"{UserRoles.Admin},{UserRoles.Service}";

    private const int DefaultWindowDays = 30;
    private const int DefaultTake = 2000;
    private const int MaxTake = 5000;

    // Any one JSON field. The largest real one is a backtest of a few hundred
    // bytes; the cap is there so a runaway caller cannot put megabytes in a
    // row that every scoreboard load reads.
    private const int MaxJsonLength = 64 * 1024;

    private readonly TradingDbContext _dbContext;
    private readonly IMarketSessionService _sessions;
    private readonly TimeProvider _time;

    public ForecastsController(TradingDbContext dbContext, IMarketSessionService sessions, TimeProvider? time = null)
    {
        _dbContext = dbContext;
        _sessions = sessions;
        _time = time ?? TimeProvider.System;
    }

    // ------------------------------------------------------------------
    // Models
    // ------------------------------------------------------------------

    /// <summary>
    /// Register a model version, or update the description and backtest of one
    /// already registered (upsert on key + version).
    /// </summary>
    /// <remarks>
    /// A version's target is fixed once it is registered: its forecasts were
    /// scored with that target's loss, and relabelling them would mix two kinds
    /// of number in one record. A model that changes what it forecasts is a new
    /// version.
    /// </remarks>
    [HttpPost("models")]
    [Authorize(Roles = Writers)]
    public async Task<IActionResult> RegisterModel([FromBody] RegisterForecastModelRequest? request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new { message = "A body is required." });
        }

        if (!IsIdentifier(request.Key, ForecastModelConfiguration.KeyLength))
        {
            return BadRequest(new { message = $"key is required: letters, digits, '.', '_' or '-', at most {ForecastModelConfiguration.KeyLength} characters." });
        }

        if (!IsIdentifier(request.Version, ForecastModelConfiguration.VersionLength))
        {
            return BadRequest(new { message = $"version is required: letters, digits, '.', '_' or '-', at most {ForecastModelConfiguration.VersionLength} characters." });
        }

        string? target = ForecastTargets.Normalize(request.Target);
        if (target is null)
        {
            return BadRequest(new { message = $"target must be one of {string.Join(", ", ForecastTargets.All)}." });
        }

        if (string.IsNullOrWhiteSpace(request.Description))
        {
            return BadRequest(new { message = "description is required: every forecast on the page traces back to it." });
        }

        if (!TryReadObject(request.Backtest, "backtest", optional: true, out string? backtestJson, out string? backtestError))
        {
            return BadRequest(new { message = backtestError });
        }

        string key = request.Key!.Trim();
        string version = request.Version!.Trim();
        var now = Now();

        var model = await _dbContext.ForecastModels.FirstOrDefaultAsync(x => x.Key == key && x.Version == version, cancellationToken);
        if (model is null)
        {
            model = new ForecastModel { Key = key, Version = version, Target = target, RegisteredUtc = now };
            _dbContext.ForecastModels.Add(model);
        }
        else if (model.Target != target)
        {
            return Conflict(new
            {
                message = $"{key} {version} is registered for {model.Target}; a version's target cannot change. Register a new version.",
            });
        }

        model.Description = request.Description.Trim();
        model.BacktestJson = backtestJson;
        model.UpdatedUtc = now;

        await _dbContext.SaveChangesAsync(cancellationToken);

        HttpContext.Describe($"Registered forecast model {key} {version} ({target})", "forecast-model", $"{key}@{version}");
        return Ok(ToView(model));
    }

    /// <summary>Every registered model version, newest registration first, with its backtest as stored.</summary>
    [HttpGet("models")]
    public async Task<IActionResult> Models(CancellationToken cancellationToken)
    {
        var models = await _dbContext.ForecastModels.AsNoTracking()
            .OrderByDescending(x => x.RegisteredUtc)
            .ThenBy(x => x.Key)
            .ThenBy(x => x.Version)
            .ToListAsync(cancellationToken);

        return Ok(models.Select(ToView).ToList());
    }

    // ------------------------------------------------------------------
    // Forecasts
    // ------------------------------------------------------------------

    /// <summary>
    /// Issue one forecast. The API stamps it with its own clock and refuses it
    /// once the session has opened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 409 when the session for <c>sessionDate</c> has already opened on the
    /// underlying's exchange (NSE for NIFTY and BANKNIFTY, BSE for SENSEX), or
    /// when this model version already forecast this target for this index and
    /// session. 400 when the model version is not registered, the target or
    /// underlying is not one forecasts are made for, or the date is not a
    /// trading day on that exchange — there would be no session to score.
    /// </para>
    /// <para>
    /// The prediction and baseline are checked for the one number each target
    /// is scored on (<c>p</c> in [0, 1], or the range's positive <c>median</c>)
    /// and otherwise stored exactly as sent.
    /// </para>
    /// </remarks>
    [HttpPost]
    [Authorize(Roles = Writers)]
    public async Task<IActionResult> Issue([FromBody] IssueForecastRequest? request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new { message = "A body is required." });
        }

        if (!IsIdentifier(request.ModelKey, ForecastModelConfiguration.KeyLength) || !IsIdentifier(request.ModelVersion, ForecastModelConfiguration.VersionLength))
        {
            return BadRequest(new { message = "modelKey and modelVersion are required." });
        }

        string? target = ForecastTargets.Normalize(request.Target);
        if (target is null)
        {
            return BadRequest(new { message = $"target must be one of {string.Join(", ", ForecastTargets.All)}." });
        }

        string? underlying = ForecastUnderlyings.Normalize(request.Underlying);
        if (underlying is null)
        {
            return BadRequest(new { message = $"underlying must be one of {string.Join(", ", ForecastUnderlyings.All)}." });
        }

        if (request.SessionDate is not DateOnly sessionDate)
        {
            return BadRequest(new { message = "sessionDate is required, as the IST date yyyy-MM-dd." });
        }

        if (!TryReadObject(request.Prediction, "prediction", optional: false, out string? predictionJson, out string? error)
            || !TryReadObject(request.Baseline, "baseline", optional: false, out string? baselineJson, out error)
            || !TryReadObject(request.Inputs, "inputs", optional: true, out string? inputsJson, out error))
        {
            return BadRequest(new { message = error });
        }

        if (ScoredNumberProblem(target, request.Prediction!.Value, "prediction") is { } predictionProblem)
        {
            return BadRequest(new { message = predictionProblem });
        }

        if (ScoredNumberProblem(target, request.Baseline!.Value, "baseline") is { } baselineProblem)
        {
            return BadRequest(new { message = baselineProblem });
        }

        string modelKey = request.ModelKey!.Trim();
        string modelVersion = request.ModelVersion!.Trim();

        var model = await _dbContext.ForecastModels.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Key == modelKey && x.Version == modelVersion, cancellationToken);
        if (model is null)
        {
            return BadRequest(new { message = $"Model {modelKey} version {modelVersion} is not registered. Register it with POST /api/Forecasts/models first." });
        }

        if (model.Target != target)
        {
            return BadRequest(new { message = $"{modelKey} {modelVersion} is registered to forecast {model.Target}, not {target}." });
        }

        var session = SessionOf(underlying, sessionDate);
        if (!session.IsTradingDay)
        {
            string why = session.HolidayName is { } holiday ? $" ({holiday})" : string.Empty;
            return BadRequest(new { message = $"{Iso(sessionDate)} is not a trading day on {session.Exchange}{why}; there is no session to forecast." });
        }

        var now = Now();
        if (now >= session.SessionOpenUtc)
        {
            return Conflict(new
            {
                message = $"The {session.Exchange} session of {Iso(sessionDate)} opened at {IstTime.ToIst(session.SessionOpenUtc):HH:mm} IST; a forecast has to be written before the open.",
                sessionOpenUtc = session.SessionOpenUtc,
            });
        }

        var duplicate = await FindAsync(modelKey, modelVersion, target, underlying, sessionDate, cancellationToken);
        if (duplicate is not null)
        {
            return DuplicateConflict(duplicate);
        }

        var forecast = new Forecast
        {
            ModelKey = modelKey,
            ModelVersion = modelVersion,
            Target = target,
            Underlying = underlying,
            SessionDate = sessionDate,
            IssuedUtc = now,
            PredictionJson = predictionJson!,
            BaselineJson = baselineJson!,
            InputsJson = inputsJson ?? "{}",
        };

        _dbContext.Forecasts.Add(forecast);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two issuers raced past the check above; the unique index let one
            // in. The other is told so, as if it had arrived second.
            _dbContext.Entry(forecast).State = EntityState.Detached;
            duplicate = await FindAsync(modelKey, modelVersion, target, underlying, sessionDate, cancellationToken);
            if (duplicate is null)
            {
                throw;
            }

            return DuplicateConflict(duplicate);
        }

        HttpContext.Describe(
            $"Issued forecast #{forecast.Id}: {modelKey} {modelVersion} {target} for {underlying} on {Iso(sessionDate)}",
            "forecast",
            forecast.Id.ToString(CultureInfo.InvariantCulture));
        return Ok(ToView(forecast));
    }

    /// <summary>
    /// Score a forecast: what happened in the session, and the model's and the
    /// baseline's losses. Written once, after the session's close.
    /// </summary>
    /// <remarks>
    /// 409 before the session closes on the underlying's exchange (15:30 IST),
    /// or when the forecast is already scored. <c>scores.loss</c> and
    /// <c>scores.baselineLoss</c> are required numbers; they are copied into
    /// their own columns for the scoreboard, and the whole <c>scores</c> object
    /// is kept as sent.
    /// </remarks>
    [HttpPost("{id:long}/outcome")]
    [Authorize(Roles = Writers)]
    public async Task<IActionResult> Score(long id, [FromBody] ScoreForecastRequest? request, CancellationToken cancellationToken)
    {
        var forecast = await _dbContext.Forecasts.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (forecast is null)
        {
            return NotFound(new { message = $"Forecast {id} not found." });
        }

        if (request is null)
        {
            return BadRequest(new { message = "A body is required." });
        }

        if (!TryReadObject(request.Outcome, "outcome", optional: false, out string? outcomeJson, out string? error)
            || !TryReadObject(request.Scores, "scores", optional: false, out string? scoresJson, out error))
        {
            return BadRequest(new { message = error });
        }

        if (!TryReadLoss(request.Scores!.Value, "loss", out double loss, out error)
            || !TryReadLoss(request.Scores!.Value, "baselineLoss", out double baselineLoss, out error))
        {
            return BadRequest(new { message = error });
        }

        if (forecast.ScoredUtc is DateTime scoredUtc)
        {
            return Conflict(new
            {
                message = $"Forecast {id} was scored at {Utc(scoredUtc):yyyy-MM-dd HH:mm:ss} UTC; an outcome is written once.",
                scoredUtc = Utc(scoredUtc),
            });
        }

        var session = SessionOf(forecast.Underlying, forecast.SessionDate);
        var now = Now();
        if (now < session.SessionCloseUtc)
        {
            return Conflict(new
            {
                message = $"The {session.Exchange} session of {Iso(forecast.SessionDate)} closes at {IstTime.ToIst(session.SessionCloseUtc):HH:mm} IST; the outcome can be written only after it.",
                sessionCloseUtc = session.SessionCloseUtc,
            });
        }

        forecast.OutcomeJson = outcomeJson;
        forecast.ScoresJson = scoresJson;
        forecast.Loss = loss;
        forecast.BaselineLoss = baselineLoss;
        forecast.ScoredUtc = now;

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // ScoredUtc is a concurrency token: another scorer wrote this
            // forecast's outcome between our read and our write, and theirs stands.
            return Conflict(new { message = $"Forecast {id} was scored by another request just now; an outcome is written once." });
        }

        HttpContext.Describe(
            $"Scored forecast #{id}: {forecast.ModelKey} {forecast.Target} for {forecast.Underlying} on {Iso(forecast.SessionDate)}",
            "forecast",
            id.ToString(CultureInfo.InvariantCulture));
        return Ok(ToView(forecast));
    }

    /// <summary>
    /// Forecasts, newest session first.
    /// </summary>
    /// <param name="fromDate">First session (IST date), inclusive. Defaults to 30 days before <paramref name="toDate"/>, or before today.</param>
    /// <param name="toDate">Last session, inclusive. Defaults to no limit, so forecasts issued for upcoming sessions are included.</param>
    /// <param name="target"><c>range</c>, <c>trend</c> or <c>direction</c>.</param>
    /// <param name="underlying"><c>NIFTY</c>, <c>BANKNIFTY</c> or <c>SENSEX</c>.</param>
    /// <param name="take">At most this many rows, up to 5,000.</param>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery(Name = "from")] DateOnly? fromDate,
        [FromQuery(Name = "to")] DateOnly? toDate,
        [FromQuery] string? target,
        [FromQuery] string? underlying,
        [FromQuery] int take = DefaultTake,
        CancellationToken cancellationToken = default)
    {
        // An unknown filter is refused, not ignored: ignored, it would answer
        // with every target's rows under a heading that names one.
        string? wantedTarget = null;
        if (!string.IsNullOrWhiteSpace(target) && (wantedTarget = ForecastTargets.Normalize(target)) is null)
        {
            return BadRequest(new { message = $"target must be one of {string.Join(", ", ForecastTargets.All)}." });
        }

        string? wantedUnderlying = null;
        if (!string.IsNullOrWhiteSpace(underlying) && (wantedUnderlying = ForecastUnderlyings.Normalize(underlying)) is null)
        {
            return BadRequest(new { message = $"underlying must be one of {string.Join(", ", ForecastUnderlyings.All)}." });
        }

        DateOnly from = fromDate ?? (toDate ?? TodayIst()).AddDays(-DefaultWindowDays);
        if (toDate is DateOnly to && to < from)
        {
            return BadRequest(new { message = "from must not be after to." });
        }

        IQueryable<Forecast> query = _dbContext.Forecasts.AsNoTracking().Where(x => x.SessionDate >= from);

        if (toDate is DateOnly until)
        {
            query = query.Where(x => x.SessionDate <= until);
        }

        if (wantedTarget is not null)
        {
            query = query.Where(x => x.Target == wantedTarget);
        }

        if (wantedUnderlying is not null)
        {
            query = query.Where(x => x.Underlying == wantedUnderlying);
        }

        var rows = await query
            .OrderByDescending(x => x.SessionDate)
            .ThenBy(x => x.Underlying)
            .ThenBy(x => x.ModelKey)
            .ThenBy(x => x.ModelVersion)
            .ThenBy(x => x.Id)
            .Take(Math.Clamp(take, 1, MaxTake))
            .ToListAsync(cancellationToken);

        return Ok(rows.Select(ToView).ToList());
    }

    /// <summary>
    /// One row per model version, target and underlying, and an ALL row per
    /// model version: live record, bootstrap interval, verdict, calibration.
    /// See <see cref="ForecastScoreboard"/> for the rules.
    /// </summary>
    [HttpGet("scoreboard")]
    public async Task<IActionResult> Scoreboard(CancellationToken cancellationToken)
    {
        var models = await _dbContext.ForecastModels.AsNoTracking().ToListAsync(cancellationToken);

        var forecasts = await _dbContext.Forecasts.AsNoTracking()
            .Select(x => new { x.ModelKey, x.ModelVersion, x.Target, x.Underlying, x.SessionDate, x.Loss, x.BaselineLoss, x.ScoresJson, x.ScoredUtc })
            .ToListAsync(cancellationToken);

        var rows = ForecastScoreboard.Build(
            models.Select(m => new ScoreboardModel(m.Key, m.Version, m.Target, m.Description, ParseJson(m.BacktestJson))),
            forecasts.Select(f =>
            {
                bool scored = f.ScoredUtc is not null;
                var (covered80, calibration) = ForecastScoreboard.ReadScores(scored ? f.ScoresJson : null);
                return new ScoreboardForecast(
                    f.ModelKey,
                    f.ModelVersion,
                    f.Target,
                    f.Underlying,
                    f.SessionDate,
                    scored ? f.Loss : null,
                    scored ? f.BaselineLoss : null,
                    covered80,
                    calibration);
            }));

        return Ok(rows);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private DateTime Now() => _time.GetUtcNow().UtcDateTime;

    private DateOnly TodayIst() => IstTime.DateOf(Now());

    /// <summary>The session on that IST date on the exchange the underlying trades on.</summary>
    private MarketSessionInfo SessionOf(string underlying, DateOnly date)
        => _sessions.GetSessionInfo(IstTime.MiddayUtc(date), ForecastUnderlyings.ExchangeOf(underlying), "CM");

    private Task<Forecast?> FindAsync(string modelKey, string modelVersion, string target, string underlying, DateOnly sessionDate, CancellationToken cancellationToken)
        => _dbContext.Forecasts.AsNoTracking().FirstOrDefaultAsync(
            x => x.ModelKey == modelKey && x.ModelVersion == modelVersion && x.Target == target
                 && x.Underlying == underlying && x.SessionDate == sessionDate,
            cancellationToken);

    private ConflictObjectResult DuplicateConflict(Forecast existing) => Conflict(new
    {
        message = $"{existing.ModelKey} {existing.ModelVersion} already forecast {existing.Target} for {existing.Underlying} on {Iso(existing.SessionDate)} (forecast {existing.Id}); a forecast is written once.",
        id = existing.Id,
    });

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static bool IsIdentifier(string? value, int maxLength)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        return trimmed.Length > 0 && trimmed.Length <= maxLength && Identifier().IsMatch(trimmed);
    }

    /// <summary>A model key or version: an identifier, not prose.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();

    /// <summary>
    /// A JSON object from the body as text to store. Absent (or null) is fine
    /// only when <paramref name="optional"/>, and then <paramref name="json"/> is null.
    /// </summary>
    internal static bool TryReadObject(JsonElement? value, string name, bool optional, out string? json, out string? error)
    {
        json = null;
        error = null;

        if (value is not { } element || element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            if (optional)
            {
                return true;
            }

            error = $"{name} is required, as a JSON object.";
            return false;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            error = $"{name} must be a JSON object.";
            return false;
        }

        string raw = element.GetRawText();
        if (raw.Length > MaxJsonLength)
        {
            error = $"{name} is {raw.Length:N0} characters; at most {MaxJsonLength:N0} are stored.";
            return false;
        }

        json = raw;
        return true;
    }

    /// <summary>
    /// Why the number a target is scored on is missing or unusable in
    /// <paramref name="forecast"/>, or null when it is fine.
    /// </summary>
    internal static string? ScoredNumberProblem(string target, JsonElement forecast, string name)
    {
        if (target == ForecastTargets.Range)
        {
            return forecast.TryGetProperty("median", out var median) && median.ValueKind == JsonValueKind.Number
                   && median.TryGetDouble(out double m) && double.IsFinite(m) && m > 0
                ? null
                : $"{name}.median must be a positive number: the range forecast is scored on it.";
        }

        return forecast.TryGetProperty("p", out var p) && p.ValueKind == JsonValueKind.Number
               && p.TryGetDouble(out double probability) && probability >= 0 && probability <= 1
            ? null
            : $"{name}.p must be a probability between 0 and 1.";
    }

    private static bool TryReadLoss(JsonElement scores, string name, out double loss, out string? error)
    {
        loss = 0;
        error = null;

        if (scores.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out loss) && double.IsFinite(loss) && loss >= 0)
        {
            return true;
        }

        error = $"scores.{name} is required: a loss, a finite number of zero or more.";
        return false;
    }

    /// <summary>
    /// Stored JSON back as JSON, so the response carries the object and not a
    /// string of it. A value that is somehow not JSON comes back as a string
    /// rather than failing the whole list.
    /// </summary>
    internal static JsonElement? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(json);
        }
    }

    public static ForecastView ToView(Forecast x) => new(
        x.Id,
        x.ModelKey,
        x.ModelVersion,
        x.Target,
        x.Underlying,
        x.SessionDate,
        Utc(x.IssuedUtc),
        ParseJson(x.PredictionJson),
        ParseJson(x.BaselineJson),
        ParseJson(x.InputsJson),
        ParseJson(x.OutcomeJson),
        ParseJson(x.ScoresJson),
        x.ScoredUtc is DateTime scored ? Utc(scored) : null);

    public static ForecastModelView ToView(ForecastModel x) => new(
        x.Key,
        x.Version,
        x.Target,
        x.Description,
        ParseJson(x.BacktestJson),
        Utc(x.RegisteredUtc),
        Utc(x.UpdatedUtc));
}

/// <summary>The body of <c>POST /api/Forecasts/models</c>.</summary>
public sealed class RegisterForecastModelRequest
{
    public string? Key { get; set; }
    public string? Version { get; set; }
    public string? Target { get; set; }
    public string? Description { get; set; }

    /// <summary>The walk-forward backtest, stored and returned as sent.</summary>
    public JsonElement? Backtest { get; set; }
}

/// <summary>The body of <c>POST /api/Forecasts</c>.</summary>
public sealed class IssueForecastRequest
{
    public string? ModelKey { get; set; }
    public string? ModelVersion { get; set; }
    public string? Target { get; set; }
    public string? Underlying { get; set; }

    /// <summary>The IST date of the session, yyyy-MM-dd.</summary>
    public DateOnly? SessionDate { get; set; }

    public JsonElement? Prediction { get; set; }
    public JsonElement? Baseline { get; set; }
    public JsonElement? Inputs { get; set; }
}

/// <summary>The body of <c>POST /api/Forecasts/{id}/outcome</c>.</summary>
public sealed class ScoreForecastRequest
{
    public JsonElement? Outcome { get; set; }
    public JsonElement? Scores { get; set; }
}

/// <summary>A forecast as the page and the Python scorer read it; the JSON fields are objects, not strings.</summary>
public sealed record ForecastView(
    long Id,
    string ModelKey,
    string ModelVersion,
    string Target,
    string Underlying,
    DateOnly SessionDate,
    DateTime IssuedUtc,
    JsonElement? Prediction,
    JsonElement? Baseline,
    JsonElement? Inputs,
    JsonElement? Outcome,
    JsonElement? Scores,
    DateTime? ScoredUtc);

/// <summary>A registered model version.</summary>
public sealed record ForecastModelView(
    string Key,
    string Version,
    string Target,
    string Description,
    JsonElement? Backtest,
    DateTime RegisteredUtc,
    DateTime UpdatedUtc);
