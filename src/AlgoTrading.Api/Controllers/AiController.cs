using System.Text.Json;
using System.Text.RegularExpressions;
using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// The AI workspace: the desk's hosted-model agents, the models they use,
/// every call they made, and the Assistant's questions.
/// </summary>
/// <remarks>
/// <para>
/// Admin-only, and not only because the answers can name accounts: the
/// models run on NVIDIA's free tier, whose terms cover development, testing,
/// research and evaluation. Serving traders would be production use.
/// </para>
/// <para>
/// Every call goes through <see cref="AiGateway"/>, which holds the agent's
/// switch, the rate limit, the fallback chain and the audit row. This
/// controller shapes requests and answers; the key never appears in either
/// (<c>keyConfigured</c> is all a browser learns).
/// </para>
/// </remarks>
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
[ApiController]
[Route("api/Ai")]
public class AiController : ControllerBase
{
    private const int DefaultTake = 50;
    private const int MaxTake = 200;
    private const int MaxMessages = 40;
    private const int MaxPromptChars = 60_000;
    private const int MaxSystemChars = 8_000;
    private const int DefaultMaxTokens = 4096;
    private const int MaxMaxTokens = 16_384;
    private const double DefaultTemperature = 0.2;

    /// <summary>
    /// A call still "running" after this long never finished (the API
    /// restarted under it): three models at the five-minute ceiling, and some.
    /// </summary>
    public static readonly TimeSpan AbandonedAfter = TimeSpan.FromMinutes(20);

    private static readonly Regex ConversationIdShape = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);

    private readonly TradingDbContext _db;
    private readonly AiSettingsStore _store;
    private readonly AiGateway _gateway;
    private readonly AiModelCatalog _catalog;
    private readonly AiRateLimiter _limiter;
    private readonly AiToolbox _toolbox;
    private readonly AiModelHealth _health;
    private readonly IOptionsMonitor<AiSettings> _settings;
    private readonly IServiceScopeFactory? _scopes;
    private readonly ILogger<AiController>? _logger;
    private readonly TimeProvider _time;

    public AiController(
        TradingDbContext db,
        AiSettingsStore store,
        AiGateway gateway,
        AiModelCatalog catalog,
        AiRateLimiter limiter,
        AiToolbox toolbox,
        AiModelHealth health,
        IOptionsMonitor<AiSettings> settings,
        IServiceScopeFactory? scopes = null,
        ILogger<AiController>? logger = null,
        TimeProvider? time = null)
    {
        _db = db;
        _store = store;
        _gateway = gateway;
        _catalog = catalog;
        _limiter = limiter;
        _toolbox = toolbox;
        _health = health;
        _settings = settings;
        _scopes = scopes;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    // ---------- overview ----------------------------------------------------

    /// <summary>The provider, the tiers, today's numbers and the agents at a glance.</summary>
    [HttpGet("overview")]
    public async Task<IActionResult> Overview(CancellationToken cancellationToken)
    {
        var state = await _store.LoadAsync(cancellationToken);
        var now = Now();
        var today = await TodayRowsAsync(now, cancellationToken);
        var s = _settings.CurrentValue;

        var lastOk = await _db.AiCalls.AsNoTracking()
            .Where(x => x.Outcome == AiCallOutcome.Ok)
            .OrderByDescending(x => x.Id)
            .Select(x => new { x.Id, x.CompletedUtc, x.Model })
            .FirstOrDefaultAsync(cancellationToken);
        var lastError = await _db.AiCalls.AsNoTracking()
            .Where(x => x.Outcome == AiCallOutcome.Failed)
            .OrderByDescending(x => x.Id)
            .Select(x => new { x.Id, x.CompletedUtc, x.CreatedUtc, x.Error })
            .FirstOrDefaultAsync(cancellationToken);

        return Ok(new AiOverview(
            Provider(s),
            new AiLimits(s.PerUserPer10Min, s.GlobalPerMinute, s.MaxConcurrent, _limiter.UsedLastMinute, _limiter.InFlight, AiCatalog.ProviderLimitNote),
            state.Tiers.Select(ToTier).ToList(),
            TodayTotals(today, now),
            ByModel(today),
            lastOk is null ? null : new AiLastOk(Utc(lastOk.CompletedUtc), lastOk.Model, lastOk.Id),
            lastError is null ? null : new AiLastError(Utc(lastError.CompletedUtc ?? lastError.CreatedUtc), lastError.Error, lastError.Id),
            new AiAgentCounts(
                state.Agents.Count,
                state.Agents.Count(a => a.Def.Built),
                state.Agents.Count(a => a.Status == "on"),
                state.Agents.Count(a => a.Status == "off"),
                state.Agents.Count(a => a.Status == "planned")))
        {
            Health = state.Tiers.Where(t => t.Def.Chat).SelectMany(t => t.Chain).Distinct().Select(m => ToHealth(_health.State(m, now))).ToList(),
        });
    }

    /// <summary>What the desk has learnt about each model it has asked lately: answering, failed, or cooling at the back of its chains.</summary>
    [HttpGet("health")]
    public IActionResult Health()
    {
        var now = Now();
        return Ok(_health.Snapshot(now).Select(ToHealth).ToList());
    }

    private static AiModelHealthDto ToHealth(AiModelHealthState h) => new(
        h.Model, h.State, Utc(h.CoolingUntilUtc), h.ConsecutiveFailures, h.LastFailure, Utc(h.LastFailureUtc), h.LastOkSeconds,
        Utc(h.LastOkUtc), Utc(h.LastProbeUtc));

    // ---------- agents ------------------------------------------------------

    /// <summary>Every agent, built or planned, with its chain, switch and recent use; and the parts that are not AI.</summary>
    [HttpGet("agents")]
    public async Task<IActionResult> Agents(CancellationToken cancellationToken)
    {
        var state = await _store.LoadAsync(cancellationToken);
        var now = Now();
        var today = await TodayRowsAsync(now, cancellationToken);
        var last = await LastCallsAsync(cancellationToken);

        return Ok(new AiAgentList(
            state.Agents.Select(a => ToAgent(a, today, last)).ToList(),
            AiCatalog.RuleBased.Select(r => new AiRuleBasedDto(r.Name, r.What, r.Where, r.Model)).ToList()));
    }

    /// <summary>Switches an agent on or off, or gives it its own chain.</summary>
    [HttpPut("agents/{key}")]
    public async Task<IActionResult> UpdateAgent(string key, [FromBody] AiAgentUpdate body, CancellationToken cancellationToken)
    {
        var def = AiCatalog.Agents.FirstOrDefault(a => a.Key == key);
        if (def is null) return NotFound(new { error = $"No agent {key}." });
        if (!def.Built) return Conflict(new { error = $"{def.Name} is not built yet (planned for phase {def.Phase}): there is nothing to switch on." });

        string? reason = Reason(body.Reason);
        if (body.Chain is not null && body.ResetChain)
        {
            return BadRequest(new { error = "Give a chain or reset it, not both." });
        }

        if (body.Chain is not null)
        {
            var models = await _catalog.GetAsync(cancellationToken: cancellationToken);
            if (AiSettingsStore.ChainProblem(body.Chain, models.KnownIds()) is string problem) return BadRequest(new { error = problem });
        }

        string by = Actor();
        if (body.Enabled is bool enabled) await _store.SetAgentEnabledAsync(key, enabled, by, reason, cancellationToken);
        if (body.Chain is not null) await _store.SetAgentChainAsync(key, body.Chain, by, reason, cancellationToken);
        if (body.ResetChain) await _store.SetAgentChainAsync(key, null, by, reason, cancellationToken);

        var state = await _store.LoadAsync(cancellationToken);
        var today = await TodayRowsAsync(Now(), cancellationToken);
        var last = await LastCallsAsync(cancellationToken);
        return Ok(ToAgent(state.Agent(key)!, today, last));
    }

    /// <summary>Sets a tier's chain, or back to its default.</summary>
    [HttpPut("tiers/{tier}")]
    public async Task<IActionResult> UpdateTier(string tier, [FromBody] AiTierUpdate body, CancellationToken cancellationToken)
    {
        var def = AiCatalog.Tier(tier);
        if (def is null) return NotFound(new { error = $"No tier {tier}." });
        if (!def.Chat) return BadRequest(new { error = "The embedding tier is not used yet: nothing searches with it." });

        if (body.Chain is { Count: > 0 })
        {
            var models = await _catalog.GetAsync(cancellationToken: cancellationToken);
            if (AiSettingsStore.ChainProblem(body.Chain, models.KnownIds()) is string problem) return BadRequest(new { error = problem });
        }

        await _store.SetTierChainAsync(def.Key, body.Chain, Actor(), Reason(body.Reason), cancellationToken);
        var state = await _store.LoadAsync(cancellationToken);
        return Ok(ToTier(state.Tier(def.Key)));
    }

    // ---------- models ------------------------------------------------------

    /// <summary>The provider's catalog, the models in use first, with each one's last test and today's use.</summary>
    /// <param name="refresh">Read the provider's list now rather than the one up to ten minutes old.</param>
    [HttpGet("models")]
    public async Task<IActionResult> Models([FromQuery] bool refresh = false, CancellationToken cancellationToken = default)
    {
        var list = await _catalog.GetAsync(refresh, cancellationToken);
        var state = await _store.LoadAsync(cancellationToken);
        var today = await TodayRowsAsync(Now(), cancellationToken);
        var tests = await LastTestsAsync(cancellationToken);
        var usage = AttemptUsage(today);

        // In use: every model a chat tier's chain or a built agent's chain names, in chain order.
        var inUse = new List<string>();
        foreach (var tier in state.Tiers)
        {
            foreach (string id in tier.Chain) if (!inUse.Contains(id)) inUse.Add(id);
        }

        foreach (var agent in state.Agents.Where(a => a.Def.Built))
        {
            foreach (string id in agent.Chain) if (!inUse.Contains(id)) inUse.Add(id);
        }

        var owners = list.Models.ToDictionary(m => m.Id, m => m.OwnedBy, StringComparer.Ordinal);
        var ids = inUse.Concat(list.Models.Select(m => m.Id).Where(id => !inUse.Contains(id)).OrderBy(id => id, StringComparer.Ordinal));

        var models = ids.Select(id => new AiModelDto(
            id,
            owners.TryGetValue(id, out string? owner) && owner.Length > 0 ? owner : id.Split('/')[0],
            inUse.Contains(id),
            state.Tiers.Where(t => t.Chain.Contains(id)).Select(t => t.Def.Key).ToList(),
            state.Agents.Where(a => a.Def.Built && a.Chain.Contains(id)).Select(a => a.Def.Key).ToList(),
            ModelNote(id, state),
            tests.TryGetValue(id, out var test) ? test : null,
            usage.TryGetValue(id, out var used) ? used : new AiModelUsage(0, 0, 0, null),
            AiCatalog.IsEmbeddingModel(id),
            list.Models.Count == 0 || owners.ContainsKey(id))
        {
            Health = inUse.Contains(id) ? ToHealth(_health.State(id, Now())) : null,
        }).ToList();

        return Ok(new AiModelsDto(
            Utc(list.FetchedUtc),
            list.Source,
            list.Error,
            models,
            AiCatalog.LocalModels.Select(m => new AiLocalModelDto(m.Id, m.Kind, m.Where, m.UsedBy)).ToList()));
    }

    /// <summary>One tiny question to one model, to see that it answers and how fast. Logged like any call.</summary>
    [HttpPost("models/test")]
    public async Task<IActionResult> TestModel([FromBody] AiModelTestRequest body, CancellationToken cancellationToken)
    {
        string model = (body.Model ?? string.Empty).Trim();
        var list = await _catalog.GetAsync(cancellationToken: cancellationToken);
        if (!list.KnownIds().Contains(model)) return BadRequest(new { error = $"{model} is not in the provider's model list." });
        if (AiCatalog.IsEmbeddingModel(model)) return BadRequest(new { error = $"{model} makes vectors, not answers: a chat test cannot reach it." });

        var result = await _gateway.AskAsync(new AiAskInput(
            AiCatalog.ModelTest,
            null,
            [new AiMessage("user", "Reply with the word OK and nothing else.")],
            string.Empty,
            512,
            0,
            string.Empty,
            "console",
            Actor(),
            User.GetUserId(),
            [model]), NullAiStreamSink.Instance, cancellationToken);

        if (result.RefusalStatus is int status) return Refused(result, status);

        return Ok(new AiModelTestResult(
            result.Outcome == AiCallOutcome.Ok,
            model,
            result.Seconds,
            result.Text.Trim(),
            result.Outcome == AiCallOutcome.Ok ? null : result.Attempts.LastOrDefault()?.Outcome ?? result.Error,
            result.CallId));
    }

    // ---------- calls -------------------------------------------------------

    /// <summary>The newest calls first, optionally one agent's, one outcome's, one model's or one source's (console, telegram, check...).</summary>
    [HttpGet("calls")]
    public async Task<IActionResult> Calls(
        [FromQuery] string? agent = null,
        [FromQuery] string? outcome = null,
        [FromQuery] string? model = null,
        [FromQuery] int take = DefaultTake,
        [FromQuery] long? beforeId = null,
        [FromQuery] string? source = null,
        CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, MaxTake);
        var query = _db.AiCalls.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(source)) query = query.Where(x => x.Source == source);
        if (!string.IsNullOrWhiteSpace(agent)) query = query.Where(x => x.AgentKey == agent);
        if (!string.IsNullOrWhiteSpace(outcome)) query = query.Where(x => x.Outcome == outcome);
        if (!string.IsNullOrWhiteSpace(model)) query = query.Where(x => x.Model == model || x.ChainJson.Contains("\"" + model + "\""));
        if (beforeId is long before) query = query.Where(x => x.Id < before);

        var rows = await query
            .OrderByDescending(x => x.Id)
            .Take(take + 1)
            .Select(x => new CallRow(x.Id, x.CreatedUtc, x.CompletedUtc, x.AgentKey, x.Tier, x.Source, x.RequestedBy, x.Model,
                x.Outcome, x.AttemptsJson, x.Seconds, x.PromptTokens, x.CompletionTokens, x.TotalTokens, x.Summary, x.Error, x.ConversationId,
                x.ToolCalls, x.Rounds, x.FeedbackScore, x.MemoryIdsJson))
            .ToListAsync(cancellationToken);

        var now = Now();
        var page = rows.Take(take).Select(r => ToSummary(r, now)).ToList();
        return Ok(new AiCallPage(page, rows.Count > take ? page[^1].Id : null));
    }

    /// <summary>One call in full: what was sent, every model tried, the reasoning and the answer.</summary>
    [HttpGet("calls/{id:long}")]
    public async Task<IActionResult> Call(long id, CancellationToken cancellationToken)
    {
        var x = await _db.AiCalls.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (x is null) return NotFound(new { error = $"No call {id}." });

        var summary = ToSummary(new CallRow(x.Id, x.CreatedUtc, x.CompletedUtc, x.AgentKey, x.Tier, x.Source, x.RequestedBy, x.Model,
            x.Outcome, x.AttemptsJson, x.Seconds, x.PromptTokens, x.CompletionTokens, x.TotalTokens, x.Summary, x.Error, x.ConversationId,
            x.ToolCalls, x.Rounds, x.FeedbackScore, x.MemoryIdsJson), Now());

        return Ok(new AiCallDetail(
            summary.Id, summary.Utc, summary.CompletedUtc, summary.AgentKey, summary.AgentName, summary.Tier, summary.Source,
            summary.RequestedBy, summary.Model, summary.Outcome, summary.AttemptCount, summary.Fallbacks, summary.Seconds,
            summary.PromptTokens, summary.CompletionTokens, summary.TotalTokens, summary.Summary, summary.Error, summary.ConversationId,
            summary.ToolCalls, summary.Rounds,
            x.SystemPrompt,
            ReadMessages(x.MessagesJson),
            x.Answer,
            x.Reasoning,
            x.FinishReason,
            ReadList<string>(x.ChainJson),
            ReadAttempts(x.AttemptsJson),
            ReadTools(x.ToolsJson),
            new AiRequestDto($"POST {ChatEndpoint()}", x.MaxTokens, x.Temperature, true))
        {
            MemoryIds = AiGateway.MemoryIds(x.MemoryIdsJson),
            Feedback = x.FeedbackScore is int score
                ? new AiFeedbackDto(score, x.FeedbackNote, x.FeedbackBy, Utc(x.FeedbackUtc) ?? Utc(x.CreatedUtc)!.Value)
                : null,
        });
    }

    // ---------- search ------------------------------------------------------

    /// <summary>The docs passages closest to <paramref name="q"/>, as the Assistant's <c>search_docs</c> finds them, and what the index holds.</summary>
    [HttpGet("search")]
    public async Task<IActionResult> Search(
        [FromServices] AiDocIndex docs,
        [FromQuery] string? q = null,
        [FromQuery] int limit = 5,
        CancellationToken cancellationToken = default)
    {
        var status = await docs.StatusAsync(cancellationToken);
        var statusDto = new AiDocIndexStatusDto(status.Files, status.Passages, Utc(status.IndexedUtc), status.Model);
        if (string.IsNullOrWhiteSpace(q)) return Ok(new AiSearchResult(statusDto, q, []));
        if (q.Length > 300) return BadRequest(new { error = "The query is longer than 300 characters." });
        if (!_settings.CurrentValue.KeyConfigured) return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "No NVIDIA_API_KEY on the server." });

        try
        {
            var hits = await docs.SearchAsync(q.Trim(), Math.Clamp(limit, 1, 20), cancellationToken);
            return Ok(new AiSearchResult(statusDto, q.Trim(), hits.Select(h => new AiSearchHit($"docs/{h.Path}", h.Heading, h.Score, h.Text)).ToList()));
        }
        catch (AiAttemptFailedException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = $"The query could not be embedded: {ex.Outcome}" });
        }
    }

    // ---------- telegram ----------------------------------------------------

    /// <summary>The Assistant on Telegram: whether it runs, and which Telegram accounts are linked to which console users.</summary>
    [HttpGet("telegram")]
    public async Task<IActionResult> Telegram(
        [FromServices] AlgoTrading.Api.Services.AiTelegram.TelegramAssistant telegram,
        CancellationToken cancellationToken)
    {
        var owners = await telegram.OwnersAsync(cancellationToken);
        return Ok(new AiTelegramStatus(telegram.Enabled, _settings.CurrentValue.TelegramAssistantEnabled, await telegram.BotUsernameAsync(cancellationToken),
            owners.Select(o => new AiTelegramOwnerDto(o.TelegramUserId.ToString(System.Globalization.CultureInfo.InvariantCulture), o.TelegramName, o.ConsoleUser, o.LinkedUtc)).ToList()));
    }

    /// <summary>A six-digit code for the signed-in admin, valid ten minutes: sent to the bot as "/pair CODE", it links that Telegram account.</summary>
    [HttpPost("telegram/pair")]
    public async Task<IActionResult> PairTelegram(
        [FromServices] AlgoTrading.Api.Services.AiTelegram.TelegramPairing pairing,
        [FromServices] AlgoTrading.Api.Services.AiTelegram.TelegramAssistant telegram,
        CancellationToken cancellationToken)
    {
        var code = pairing.Create(Actor());
        string? bot = await telegram.BotUsernameAsync(cancellationToken);
        string to = bot is null ? "the desk's Telegram bot" : $"@{bot}";
        return Ok(new AiTelegramPairCode(code.Code, DateTime.SpecifyKind(code.ExpiresUtc, DateTimeKind.Utc), bot,
            $"Send /pair {code.Code} to {to} in a private chat within 10 minutes."));
    }

    /// <summary>Unlinks a Telegram account: its chat no longer reaches the desk.</summary>
    [HttpDelete("telegram/owners/{telegramUserId:long}")]
    public async Task<IActionResult> UnlinkTelegram(
        long telegramUserId,
        [FromServices] AlgoTrading.Api.Services.AiTelegram.TelegramAssistant telegram,
        CancellationToken cancellationToken)
    {
        return await telegram.UnlinkAsync(telegramUserId, cancellationToken) ? NoContent() : NotFound(new { error = "No such linked account." });
    }

    // ---------- reports -----------------------------------------------------

    /// <summary>What the scheduled agents wrote, newest first: all, one agent's, one subject's (a run's review), one status, one IST day.</summary>
    [HttpGet("reports")]
    public async Task<IActionResult> Reports(
        [FromQuery] string? agent = null,
        [FromQuery] string? subjectType = null,
        [FromQuery] string? subjectId = null,
        [FromQuery] string? status = null,
        [FromQuery] DateOnly? date = null,
        [FromQuery] int take = DefaultTake,
        [FromQuery] long? beforeId = null,
        CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, MaxTake);
        var query = _db.AiReports.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(agent)) query = query.Where(r => r.AgentKey == agent);
        if (!string.IsNullOrWhiteSpace(subjectType)) query = query.Where(r => r.SubjectType == subjectType);
        if (!string.IsNullOrWhiteSpace(subjectId)) query = query.Where(r => r.SubjectId == subjectId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(r => r.Status == status);
        if (date is DateOnly day) query = query.Where(r => r.SessionDate == day);
        if (beforeId is long before) query = query.Where(r => r.Id < before);

        var rows = await query.OrderByDescending(r => r.Id).Take(take + 1).ToListAsync(cancellationToken);
        var page = rows.Take(take).ToList();
        var links = await SubjectLinksAsync(page, cancellationToken);
        return Ok(new AiReportPage(page.Select(r => ToReportSummary(r, links)).ToList(), rows.Count > take ? page[^1].Id : null));
    }

    /// <summary>One report in full: its body and the structured data the agent returned.</summary>
    [HttpGet("reports/{id:long}")]
    public async Task<IActionResult> Report(long id, CancellationToken cancellationToken)
    {
        var r = await _db.AiReports.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (r is null) return NotFound(new { error = $"No report {id}." });
        var links = await SubjectLinksAsync([r], cancellationToken);
        JsonElement? data = null;
        try
        {
            data = JsonDocument.Parse(r.DataJson).RootElement.Clone();
        }
        catch (JsonException)
        {
            // Kept as written; the body still reads.
        }

        return Ok(new AiReportDetail(ToReportSummary(r, links), r.Body, data));
    }

    /// <summary>
    /// Each scheduled agent's reports over the last days, by status: the
    /// News Analyst's share of valid records is the roadmap's "JSON validity
    /// above 98%".
    /// </summary>
    [HttpGet("reports/stats")]
    public async Task<IActionResult> ReportStats([FromQuery] int days = 7, CancellationToken cancellationToken = default)
    {
        days = Math.Clamp(days, 1, 60);
        var since = IstTime.DateOf(Now()).AddDays(-(days - 1));
        var rows = await _db.AiReports.AsNoTracking()
            .Where(r => r.SessionDate >= since)
            .GroupBy(r => new { r.AgentKey, r.SessionDate, r.Status })
            .Select(g => new { g.Key.AgentKey, g.Key.SessionDate, g.Key.Status, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var agents = rows.GroupBy(r => r.AgentKey).Select(g =>
        {
            int ok = g.Where(r => r.Status == AiReportStatus.Ok).Sum(r => r.Count);
            int invalid = g.Where(r => r.Status == AiReportStatus.Invalid).Sum(r => r.Count);
            int failed = g.Where(r => r.Status == AiReportStatus.Failed).Sum(r => r.Count);
            return new AiReportAgentStats(
                g.Key,
                AiCatalog.AgentName(g.Key),
                ok + invalid + failed,
                ok,
                invalid,
                failed,
                ok + invalid == 0 ? null : Math.Round(100.0 * ok / (ok + invalid), 1),
                g.GroupBy(r => r.SessionDate).OrderBy(d => d.Key)
                    .Select(d => new AiReportDay(d.Key?.ToString("yyyy-MM-dd") ?? string.Empty,
                        d.Where(r => r.Status == AiReportStatus.Ok).Sum(r => r.Count),
                        d.Where(r => r.Status == AiReportStatus.Invalid).Sum(r => r.Count),
                        d.Where(r => r.Status == AiReportStatus.Failed).Sum(r => r.Count)))
                    .ToList());
        }).OrderBy(a => a.AgentKey).ToList();

        return Ok(new AiReportStats(since.ToString("yyyy-MM-dd"), days, agents));
    }

    /// <summary>
    /// Runs a scheduled agent now, in the background: on one subject (a run
    /// id, an incident id) or its next due work. Answers 202 at once; the
    /// report and its call appear on their tabs when the model has answered.
    /// </summary>
    [HttpPost("agents/{key}/run")]
    public async Task<IActionResult> RunAgent(string key, [FromBody] AiAgentRunRequest? body, CancellationToken cancellationToken)
    {
        if (_scopes is null) return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Scheduled agents are not available on this host." });
        var def = key is AiCatalog.AssistantCheck or AiCatalog.AssistantExam ? AiCatalog.Agent(key) : AiCatalog.Agents.FirstOrDefault(a => a.Key == key);
        if (def is null) return NotFound(new { error = $"No agent {key}." });
        if (key is not (AiCatalog.TradeReviewer or AiCatalog.NewsAnalyst or AiCatalog.IncidentExplainer or AiCatalog.AssistantCheck or AiCatalog.AssistantExam))
        {
            return Conflict(new { error = $"{def.Name} is not a scheduled agent." });
        }

        var state = await _store.LoadAsync(cancellationToken);
        if (!AlgoTrading.Api.Services.AiAgents.AiAgentScheduler.IsOn(key, state, _settings.CurrentValue))
        {
            return Conflict(new { error = key == AiCatalog.AssistantCheck
                ? "The assistant check runs only while the Desk Assistant is on and Ai:AssistantCheckEnabled is true."
                : key == AiCatalog.AssistantExam
                ? "The assistant exam runs only while the Desk Assistant is on and Ai:ExamEnabled is true."
                : $"{def.Name} is switched off." });
        }
        if (!_settings.CurrentValue.KeyConfigured) return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "No NVIDIA_API_KEY on the server." });

        string? subject = string.IsNullOrWhiteSpace(body?.SubjectId) ? null : body.SubjectId.Trim();
        if (subject is not null && !long.TryParse(subject, out _)) return BadRequest(new { error = "subjectId is a run or incident id." });

        var scopes = _scopes;
        var logger = _logger;
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var agent = scope.ServiceProvider.GetServices<AlgoTrading.Api.Services.AiAgents.IAiScheduledAgent>().First(a => a.AgentKey == key);
                await agent.RunForAsync(subject, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Running AI agent {Agent} on request failed", key);
            }
        });

        return Accepted(new { started = true, agent = key, subjectId = subject });
    }

    /// <summary>Where each report's subject lives: a console page for a run or an incident, the source for news and filings.</summary>
    private async Task<Dictionary<(string, string), string?>> SubjectLinksAsync(IReadOnlyList<AiReport> reports, CancellationToken cancellationToken)
    {
        var links = new Dictionary<(string, string), string?>();
        var newsIds = reports.Where(r => r.SubjectType == AiReportSubject.News).Select(r => long.TryParse(r.SubjectId, out long v) ? v : 0).Where(v => v > 0).ToList();
        var filingIds = reports.Where(r => r.SubjectType == AiReportSubject.Filing).Select(r => long.TryParse(r.SubjectId, out long v) ? v : 0).Where(v => v > 0).ToList();
        var news = newsIds.Count == 0 ? [] : await _db.NewsItems.AsNoTracking().Where(n => newsIds.Contains(n.Id)).Select(n => new { n.Id, n.Link }).ToListAsync(cancellationToken);
        var filings = filingIds.Count == 0 ? [] : await _db.CorporateAnnouncements.AsNoTracking().Where(a => filingIds.Contains(a.Id)).Select(a => new { a.Id, a.AttachmentUrl }).ToListAsync(cancellationToken);

        foreach (var r in reports)
        {
            links[(r.SubjectType, r.SubjectId)] = r.SubjectType switch
            {
                AiReportSubject.Run => $"/trade/runs/{r.SubjectId}",
                AiReportSubject.Incident => $"/system/incidents?id={r.SubjectId}",
                AiReportSubject.News => news.FirstOrDefault(n => n.Id.ToString() == r.SubjectId)?.Link,
                AiReportSubject.Filing => filings.FirstOrDefault(a => a.Id.ToString() == r.SubjectId)?.AttachmentUrl,
                _ => null,
            };
        }

        return links;
    }

    private static AiReportSummary ToReportSummary(AiReport r, Dictionary<(string, string), string?> links) => new(
        r.Id,
        r.AgentKey,
        AiCatalog.AgentName(r.AgentKey),
        r.SubjectType,
        r.SubjectId,
        r.SessionDate?.ToString("yyyy-MM-dd"),
        Utc(r.CreatedUtc)!.Value,
        Utc(r.UpdatedUtc)!.Value,
        r.Status,
        r.Attempts,
        r.CallId,
        r.Model.Length > 0 ? r.Model : null,
        r.Title,
        r.Error.Length > 0 ? r.Error : null,
        links.GetValueOrDefault((r.SubjectType, r.SubjectId)) is { Length: > 0 } link ? link : null);

    // ---------- asking ------------------------------------------------------

    /// <summary>
    /// Asks and streams the answer as server-sent events. Refusals come back
    /// as plain JSON before any event: 400, 409 agent off, 429 limit, 503 no key.
    /// </summary>
    [HttpPost("ask/stream")]
    public async Task<IActionResult> AskStream([FromBody] AiAskRequest body, CancellationToken cancellationToken)
    {
        if (Validate(body, out var input) is IActionResult bad) return bad;

        await using var stream = new AiEventStream(Response, cancellationToken);
        AiAskResult result;
        try
        {
            result = await _gateway.AskAsync(input!, stream, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new EmptyResult();
        }

        if (!stream.Started)
        {
            return result.RefusalStatus is int status ? Refused(result, status) : StatusCode(500, new { error = result.Error });
        }

        // Stopped, or the connection went mid-answer: nobody is reading.
        if (cancellationToken.IsCancellationRequested || result.Outcome == AiCallOutcome.Cancelled) return new EmptyResult();

        try
        {
            if (result.Outcome == AiCallOutcome.Ok)
            {
                await stream.SendAsync("done", new
                {
                    callId = result.CallId,
                    model = result.Model,
                    seconds = result.Seconds,
                    finishReason = result.FinishReason,
                    usage = Usage(result.Usage),
                    fallbacks = result.Fallbacks,
                    toolCalls = result.Tools.Count,
                    rounds = result.Rounds,
                    memoryIds = result.MemoryIds,
                });
            }
            else
            {
                await stream.SendAsync("error", new { callId = result.CallId, error = result.Error });
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // The browser left after the answer was recorded.
        }

        return new EmptyResult();
    }

    /// <summary>
    /// Asks and waits for the whole answer. For scripts; behind Cloudflare a
    /// request over 100 seconds is cut, so the console uses the stream.
    /// </summary>
    [HttpPost("ask")]
    public async Task<IActionResult> Ask([FromBody] AiAskRequest body, CancellationToken cancellationToken)
    {
        if (Validate(body, out var input) is IActionResult bad) return bad;

        var result = await _gateway.AskAsync(input! with { Source = "api" }, NullAiStreamSink.Instance, cancellationToken);
        if (result.RefusalStatus is int status) return Refused(result, status);

        var answer = new AiAnswer(result.CallId, result.Outcome, result.Model, result.Text, result.Reasoning, result.Seconds,
            result.FinishReason, Usage(result.Usage), result.Fallbacks,
            result.Attempts.Select(a => new AiAttemptDto(a.Model, a.Outcome, a.Seconds, a.HttpStatus, a.Round)).ToList(), result.Error,
            result.Tools.Select(t => new AiToolCallDto(t.Round, t.Id, t.Name, t.Arguments, t.Ok, t.Error, t.Seconds, t.Rows,
                t.AsOfUtc is DateTime at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : null, t.Summary, t.ResultChars, t.Result)).ToList(),
            result.Rounds)
        {
            MemoryIds = result.MemoryIds,
        };

        return result.Outcome == AiCallOutcome.Ok ? Ok(answer) : StatusCode(StatusCodes.Status502BadGateway, answer);
    }

    // ---------- shaping -----------------------------------------------------

    /// <summary>Checks a question's shape and turns it into the gateway's input, or answers 400.</summary>
    private IActionResult? Validate(AiAskRequest? body, out AiAskInput? input)
    {
        input = null;
        if (body?.Messages is not { Count: > 0 } messages) return BadRequest(new { error = "Ask something: messages is empty." });
        if (messages.Count > MaxMessages) return BadRequest(new { error = $"At most {MaxMessages} messages; start a new conversation." });

        foreach (var m in messages)
        {
            if (m.Role is not ("user" or "assistant")) return BadRequest(new { error = "Each message's role is user or assistant." });
            if (string.IsNullOrWhiteSpace(m.Content)) return BadRequest(new { error = "A message is empty." });
        }

        if (messages[^1].Role != "user") return BadRequest(new { error = "The last message must be the question (role user)." });
        if (messages.Sum(m => m.Content!.Length) > MaxPromptChars) return BadRequest(new { error = $"The conversation is over {MaxPromptChars:N0} characters; start a new one." });
        if (body.System is { Length: > MaxSystemChars }) return BadRequest(new { error = $"The system prompt is over {MaxSystemChars:N0} characters." });

        string agentKey = string.IsNullOrWhiteSpace(body.Agent) ? AiCatalog.DeskAssistant : body.Agent.Trim();
        if (AiCatalog.Agents.All(a => a.Key != agentKey)) return BadRequest(new { error = $"No agent {agentKey}." });

        string? tier = string.IsNullOrWhiteSpace(body.Tier) ? null : body.Tier.Trim().ToLowerInvariant();
        if (tier is not null && AiCatalog.Tier(tier) is not { Chat: true }) return BadRequest(new { error = $"No chat tier {tier}: judge, analyst or extract." });

        int maxTokens = body.MaxTokens ?? DefaultMaxTokens;
        if (maxTokens is < 1 or > MaxMaxTokens) return BadRequest(new { error = $"maxTokens is 1 to {MaxMaxTokens:N0}." });

        double temperature = body.Temperature ?? DefaultTemperature;
        if (temperature is < 0 or > 1.5 || double.IsNaN(temperature)) return BadRequest(new { error = "temperature is 0 to 1.5." });

        string conversation = body.ConversationId?.Trim() ?? string.Empty;
        if (conversation.Length > 0 && !ConversationIdShape.IsMatch(conversation)) return BadRequest(new { error = "conversationId is up to 64 letters, digits, - or _." });

        input = new AiAskInput(
            agentKey,
            tier,
            messages.Select(m => new AiMessage(m.Role!, m.Content!)).ToList(),
            string.IsNullOrWhiteSpace(body.System) ? null : body.System,
            maxTokens,
            temperature,
            conversation,
            "console",
            Actor(),
            User.GetUserId());
        return null;
    }

    private IActionResult Refused(AiAskResult result, int status)
    {
        if (status == StatusCodes.Status429TooManyRequests && result.RetryAfterSeconds > 0)
        {
            Response.Headers.RetryAfter = result.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return StatusCode(status, new { error = result.Error, callId = result.CallId, retryAfterSeconds = result.RetryAfterSeconds });
    }

    private AiProviderDto Provider(AiSettings s) => new(
        AiCatalog.ProviderKey,
        AiCatalog.ProviderName,
        (s.BaseUrl is { Length: > 0 } b ? b : AiCatalog.DefaultBaseUrl).TrimEnd('/'),
        s.KeyConfigured,
        AiCatalog.ProviderCatalogUrl,
        AiCatalog.ProviderTerms);

    private string ChatEndpoint() =>
        $"{(_settings.CurrentValue.BaseUrl is { Length: > 0 } b ? b : AiCatalog.DefaultBaseUrl).TrimEnd('/')}/chat/completions";

    private static AiTierDto ToTier(AiTierState t) => new(
        t.Def.Key, t.Def.Label, t.Def.Purpose, t.Chain, t.Def.DefaultChain, t.Overridden, t.Def.Chat, Utc(t.UpdatedUtc), t.UpdatedBy);

    private AiAgentDto ToAgent(AiAgentState a, IReadOnlyList<TodayRow> today, IReadOnlyDictionary<string, AiLastCallDto> last)
    {
        var mine = today.Where(r => r.AgentKey == a.Def.Key).ToList();
        var tier = AiCatalog.Tier(a.Def.Tier);
        return new AiAgentDto(
            a.Def.Key,
            a.Def.Number,
            a.Def.Name,
            a.Def.Job,
            a.Def.UseCase,
            a.Def.Schedule,
            a.Def.Phase,
            a.Status,
            a.Def.Built,
            a.Enabled,
            a.Def.Tier,
            tier?.Label ?? a.Def.Tier,
            a.Chain,
            a.ChainOverridden,
            a.Def.Reads,
            a.Def.Limits,
            last.TryGetValue(a.Def.Key, out var l) ? l : null,
            null,
            new AiAgentToday(mine.Count, mine.Count(r => r.Outcome == AiCallOutcome.Ok), mine.Count(r => r.Outcome == AiCallOutcome.Failed),
                mine.Sum(r => r.TotalTokens ?? 0)),
            Utc(a.UpdatedUtc),
            a.UpdatedBy,
            a.Reason,
            _toolbox.For(a.Def).Select(t => new AiToolDto(t.Name, t.Description)).ToList());
    }

    private static AiCallSummary ToSummary(CallRow r, DateTime now)
    {
        var attempts = ReadAttempts(r.AttemptsJson);
        bool abandoned = r.Outcome == AiCallOutcome.Running && now - r.CreatedUtc > AbandonedAfter;
        return new AiCallSummary(
            r.Id,
            Utc(r.CreatedUtc)!.Value,
            Utc(r.CompletedUtc),
            r.AgentKey,
            AiCatalog.AgentName(r.AgentKey),
            r.Tier,
            r.Source,
            r.RequestedBy,
            r.Model.Length > 0 ? r.Model : null,
            abandoned ? AiCallOutcome.Failed : r.Outcome,
            attempts.Count,
            AiGateway.CountFallbacks(attempts.Select(a => a.Outcome)),
            r.Seconds,
            r.PromptTokens,
            r.CompletionTokens,
            r.TotalTokens,
            r.Summary,
            abandoned ? "Never finished: the API stopped while the call was running." : r.Error,
            r.ConversationId,
            r.ToolCalls,
            r.Rounds)
        {
            Feedback = r.FeedbackScore,
            MemoryCount = AiGateway.MemoryIds(r.MemoryIdsJson).Count,
        };
    }

    private static string ModelNote(string id, AiState state)
    {
        var parts = new List<string>();
        foreach (var tier in state.Tiers)
        {
            int at = -1;
            for (int i = 0; i < tier.Chain.Count; i++)
            {
                if (tier.Chain[i] == id) { at = i; break; }
            }

            if (at == 0) parts.Add($"first model of the {tier.Def.Label} tier");
            else if (at > 0) parts.Add($"fallback {at} of the {tier.Def.Label} tier");
        }

        foreach (var agent in state.Agents.Where(a => a.Def.Built && a.ChainOverridden && a.Chain.Contains(id)))
        {
            parts.Add($"in {agent.Def.Name}'s own chain");
        }

        if (parts.Count == 0) return string.Empty;
        string text = string.Join("; ", parts);
        return char.ToUpperInvariant(text[0]) + text[1..] + ".";
    }

    // ---------- reading the log ---------------------------------------------

    private sealed record CallRow(
        long Id, DateTime CreatedUtc, DateTime? CompletedUtc, string AgentKey, string Tier, string Source, string RequestedBy,
        string Model, string Outcome, string AttemptsJson, double Seconds, int? PromptTokens, int? CompletionTokens,
        int? TotalTokens, string Summary, string Error, string ConversationId, int ToolCalls, int Rounds,
        int? FeedbackScore = null, string MemoryIdsJson = "[]");

    private sealed record TodayRow(
        long Id, string AgentKey, string Model, string Outcome, double Seconds, int? PromptTokens, int? CompletionTokens,
        int? TotalTokens, string AttemptsJson, DateTime CreatedUtc);

    private async Task<IReadOnlyList<TodayRow>> TodayRowsAsync(DateTime now, CancellationToken cancellationToken)
    {
        var since = IstTime.StartOfDayUtc(IstTime.DateOf(now));
        return await _db.AiCalls.AsNoTracking()
            .Where(x => x.CreatedUtc >= since)
            .Select(x => new TodayRow(x.Id, x.AgentKey, x.Model, x.Outcome, x.Seconds, x.PromptTokens, x.CompletionTokens,
                x.TotalTokens, x.AttemptsJson, x.CreatedUtc))
            .ToListAsync(cancellationToken);
    }

    /// <summary>Each agent's newest call.</summary>
    private async Task<IReadOnlyDictionary<string, AiLastCallDto>> LastCallsAsync(CancellationToken cancellationToken)
    {
        var ids = await _db.AiCalls.AsNoTracking()
            .GroupBy(x => x.AgentKey)
            .Select(g => g.Max(x => x.Id))
            .ToListAsync(cancellationToken);

        var now = Now();
        var rows = await _db.AiCalls.AsNoTracking()
            .Where(x => ids.Contains(x.Id))
            .Select(x => new { x.Id, x.AgentKey, x.CreatedUtc, x.Outcome, x.Model })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(
            x => x.AgentKey,
            x => new AiLastCallDto(
                x.Id,
                Utc(x.CreatedUtc)!.Value,
                x.Outcome == AiCallOutcome.Running && now - x.CreatedUtc > AbandonedAfter ? AiCallOutcome.Failed : x.Outcome,
                x.Model.Length > 0 ? x.Model : null));
    }

    /// <summary>Each model's newest health test in the last week, by the model it tested.</summary>
    private async Task<IReadOnlyDictionary<string, AiModelTestDto>> LastTestsAsync(CancellationToken cancellationToken)
    {
        var since = Now().AddDays(-7);
        var rows = await _db.AiCalls.AsNoTracking()
            .Where(x => x.AgentKey == AiCatalog.ModelTest && x.CreatedUtc >= since && x.Outcome != AiCallOutcome.Running)
            .OrderByDescending(x => x.Id)
            .Take(500)
            .Select(x => new { x.Id, x.CreatedUtc, x.Outcome, x.Seconds, x.Error, x.AttemptsJson, x.ChainJson })
            .ToListAsync(cancellationToken);

        var tests = new Dictionary<string, AiModelTestDto>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            string? model = ReadList<string>(row.ChainJson).FirstOrDefault();
            if (model is null || tests.ContainsKey(model)) continue;
            bool ok = row.Outcome == AiCallOutcome.Ok;
            string? error = ok ? null : ReadAttempts(row.AttemptsJson).LastOrDefault()?.Outcome ?? row.Error;
            tests[model] = new AiModelTestDto(Utc(row.CreatedUtc)!.Value, ok, row.Seconds, error, row.Id);
        }

        return tests;
    }

    private static AiToday TodayTotals(IReadOnlyList<TodayRow> rows, DateTime now)
    {
        var done = rows.Where(r => r.Outcome == AiCallOutcome.Ok).Select(r => r.Seconds).OrderBy(s => s).ToList();
        return new AiToday(
            rows.Count,
            done.Count,
            rows.Count(r => r.Outcome == AiCallOutcome.Failed || (r.Outcome == AiCallOutcome.Running && now - r.CreatedUtc > AbandonedAfter)),
            rows.Count(r => r.Outcome == AiCallOutcome.Refused),
            rows.Count(r => r.Outcome == AiCallOutcome.Cancelled),
            rows.Count(r => r.Outcome == AiCallOutcome.Running && now - r.CreatedUtc <= AbandonedAfter),
            rows.Sum(r => r.PromptTokens ?? 0),
            rows.Sum(r => r.CompletionTokens ?? 0),
            rows.Sum(r => r.TotalTokens ?? 0),
            done.Count == 0 ? null : Math.Round(done.Average(), 2),
            done.Count == 0 ? null : Percentile(done, 0.95),
            rows.Sum(r => AiGateway.CountFallbacks(ReadAttempts(r.AttemptsJson).Select(a => a.Outcome))));
    }

    /// <summary>Per model, from every attempt today: so a model that keeps failing over shows its failures, not only its answers.</summary>
    private static IReadOnlyDictionary<string, AiModelUsage> AttemptUsage(IReadOnlyList<TodayRow> rows)
    {
        var usage = new Dictionary<string, (int Calls, int Ok, int Failed, List<double> Seconds)>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            foreach (var a in ReadAttempts(row.AttemptsJson))
            {
                if (!usage.TryGetValue(a.Model, out var u)) u = (0, 0, 0, []);
                u.Calls++;
                if (a.Outcome == "ok")
                {
                    u.Ok++;
                    u.Seconds.Add(a.Seconds);
                }
                else if (a.Outcome != "cancelled")
                {
                    u.Failed++;
                }

                usage[a.Model] = u;
            }
        }

        return usage.ToDictionary(
            kv => kv.Key,
            kv => new AiModelUsage(kv.Value.Calls, kv.Value.Ok, kv.Value.Failed,
                kv.Value.Seconds.Count == 0 ? null : Math.Round(kv.Value.Seconds.Average(), 2)));
    }

    private static IReadOnlyList<AiModelToday> ByModel(IReadOnlyList<TodayRow> rows)
    {
        var usage = AttemptUsage(rows);
        return usage
            .Select(kv => new AiModelToday(kv.Key, kv.Value.Calls, kv.Value.Ok, kv.Value.Failed,
                rows.Where(r => r.Model == kv.Key).Sum(r => r.TotalTokens ?? 0), kv.Value.AvgSeconds))
            .OrderByDescending(m => m.Calls)
            .ThenBy(m => m.Model, StringComparer.Ordinal)
            .ToList();
    }

    private static double Percentile(List<double> sorted, double p)
    {
        int index = (int)Math.Ceiling(p * sorted.Count) - 1;
        return Math.Round(sorted[Math.Clamp(index, 0, sorted.Count - 1)], 2);
    }

    private static IReadOnlyList<AiAttemptDto> ReadAttempts(string json)
    {
        try
        {
            var root = JsonDocument.Parse(json).RootElement;
            if (root.ValueKind != JsonValueKind.Array) return [];
            var list = new List<AiAttemptDto>();
            foreach (var a in root.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object) continue;
                list.Add(new AiAttemptDto(
                    a.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : string.Empty,
                    a.TryGetProperty("outcome", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString()! : string.Empty,
                    a.TryGetProperty("seconds", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0,
                    a.TryGetProperty("httpStatus", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetInt32() : null,
                    a.TryGetProperty("round", out var rd) && rd.ValueKind == JsonValueKind.Number ? rd.GetInt32() : 1));
            }

            return list;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<AiToolCallDto> ReadTools(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<AiToolCallDto>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<AiMessageDto> ReadMessages(string json)
    {
        try
        {
            var root = JsonDocument.Parse(json).RootElement;
            if (root.ValueKind != JsonValueKind.Array) return [];
            return root.EnumerateArray()
                .Where(m => m.ValueKind == JsonValueKind.Object)
                .Select(m => new AiMessageDto(
                    m.TryGetProperty("role", out var r) ? r.GetString() ?? string.Empty : string.Empty,
                    m.TryGetProperty("content", out var c) ? c.GetString() ?? string.Empty : string.Empty))
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return [];
        }
    }

    private static IReadOnlyList<T> ReadList<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<T>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static AiUsageDto? Usage(AiUsage? u) => u is null ? null : new AiUsageDto(u.PromptTokens, u.CompletionTokens, u.TotalTokens);

    private DateTime Now() => _time.GetUtcNow().UtcDateTime;

    /// <summary>The signed-in user's name, cut to the column's 100 characters.</summary>
    private string Actor()
    {
        string by = User.GetUserName() ?? User.Identity?.Name ?? "admin";
        return by.Length > 100 ? by[..100] : by;
    }

    private static string? Reason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return null;
        string r = reason.Trim();
        return r.Length > 300 ? r[..300] : r;
    }

    private static DateTime? Utc(DateTime? value) =>
        value is null ? null : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);
}

// ---------- wire shapes (camelCase on the wire; docs/ai_module.md) ----------

public sealed record AiProviderDto(string Key, string Name, string BaseUrl, bool KeyConfigured, string CatalogUrl, string Terms);

public sealed record AiLimits(int PerUserPer10Min, int GlobalPerMinute, int MaxConcurrent, int UsedLastMinute, int InFlight, string ProviderNote);

public sealed record AiTierDto(
    string Key, string Label, string Purpose, IReadOnlyList<string> Chain, IReadOnlyList<string> DefaultChain,
    bool Overridden, bool Chat, DateTime? UpdatedUtc, string? UpdatedBy);

public sealed record AiToday(
    int Calls, int Ok, int Failed, int Refused, int Cancelled, int Running,
    int PromptTokens, int CompletionTokens, int TotalTokens, double? AvgSeconds, double? P95Seconds, int Fallbacks);

public sealed record AiModelToday(string Model, int Calls, int Ok, int Failed, int TotalTokens, double? AvgSeconds);

public sealed record AiLastOk(DateTime? Utc, string Model, long CallId);

public sealed record AiLastError(DateTime? Utc, string Error, long CallId);

public sealed record AiAgentCounts(int Total, int Built, int On, int Off, int Planned);

public sealed record AiOverview(
    AiProviderDto Provider, AiLimits Limits, IReadOnlyList<AiTierDto> Tiers, AiToday Today, IReadOnlyList<AiModelToday> ByModel,
    AiLastOk? LastOk, AiLastError? LastError, AiAgentCounts Agents)
{
    /// <summary>The health of every model a chat tier names.</summary>
    public IReadOnlyList<AiModelHealthDto> Health { get; init; } = [];
}

/// <summary>A model's health: <c>healthy</c>, <c>failed</c> (last attempt), <c>cooling</c> (asked after the healthy ones until <c>coolingUntilUtc</c>), <c>unknown</c>.</summary>
public sealed record AiModelHealthDto(
    string Model, string State, DateTime? CoolingUntilUtc, int ConsecutiveFailures, string? LastFailure, DateTime? LastFailureUtc,
    double? LastOkSeconds, DateTime? LastOkUtc, DateTime? LastProbeUtc);

public sealed record AiLastCallDto(long Id, DateTime Utc, string Outcome, string? Model);

public sealed record AiAgentToday(int Calls, int Ok, int Failed, int TotalTokens);

public sealed record AiAgentDto(
    string Key, int Number, string Name, string Job, string UseCase, string Schedule, string Phase, string Status,
    bool Built, bool Enabled, string Tier, string TierLabel, IReadOnlyList<string> Chain, bool ChainOverridden,
    string Reads, string Limits, AiLastCallDto? LastCall, DateTime? NextRunUtc, AiAgentToday Today,
    DateTime? UpdatedUtc, string? UpdatedBy, string? Reason, IReadOnlyList<AiToolDto> Tools);

/// <summary>A desk tool an agent may ask for.</summary>
public sealed record AiToolDto(string Name, string Description);

public sealed record AiRuleBasedDto(string Name, string What, string Where, string Model);

public sealed record AiAgentList(IReadOnlyList<AiAgentDto> Agents, IReadOnlyList<AiRuleBasedDto> RuleBased);

public sealed record AiAgentUpdate(bool? Enabled, List<string>? Chain, bool ResetChain, string? Reason);

public sealed record AiTierUpdate(List<string>? Chain, string? Reason);

public sealed record AiModelTestDto(DateTime Utc, bool Ok, double Seconds, string? Error, long CallId);

public sealed record AiModelUsage(int Calls, int Ok, int Failed, double? AvgSeconds);

public sealed record AiModelDto(
    string Id, string OwnedBy, bool InUse, IReadOnlyList<string> Tiers, IReadOnlyList<string> Agents, string Note,
    AiModelTestDto? LastTest, AiModelUsage Today, bool Embedding, bool Listed)
{
    /// <summary>Its health, for a model in use; null for the rest of the catalog.</summary>
    public AiModelHealthDto? Health { get; init; }
}

public sealed record AiLocalModelDto(string Id, string Kind, string Where, string UsedBy);

public sealed record AiModelsDto(DateTime? FetchedUtc, string Source, string? Error, IReadOnlyList<AiModelDto> Models, IReadOnlyList<AiLocalModelDto> Local);

public sealed record AiModelTestRequest(string? Model);

public sealed record AiModelTestResult(bool Ok, string Model, double Seconds, string Answer, string? Error, long CallId);

public sealed record AiCallSummary(
    long Id, DateTime Utc, DateTime? CompletedUtc, string AgentKey, string AgentName, string Tier, string Source,
    string RequestedBy, string? Model, string Outcome, int AttemptCount, int Fallbacks, double Seconds,
    int? PromptTokens, int? CompletionTokens, int? TotalTokens, string Summary, string Error, string ConversationId,
    int ToolCalls, int Rounds)
{
    /// <summary>The owner's verdict on the answer: 1, -1, or null.</summary>
    public int? Feedback { get; init; }

    /// <summary>How many memories the call was given.</summary>
    public int MemoryCount { get; init; }
}

public sealed record AiCallPage(IReadOnlyList<AiCallSummary> Calls, long? NextBeforeId);

public sealed record AiMessageDto(string Role, string Content);

public sealed record AiAttemptDto(string Model, string Outcome, double Seconds, int? HttpStatus, int Round = 1);

/// <summary>One desk tool call of a question: what was asked, what it found, and the text the model was given back.</summary>
public sealed record AiToolCallDto(
    int Round, string Id, string Name, string Arguments, bool Ok, string? Error, double Seconds, int Rows,
    DateTime? AsOfUtc, string Summary, int ResultChars, string Result);

public sealed record AiRequestDto(string Endpoint, int MaxTokens, double Temperature, bool Stream);

/// <summary>A call in full: the summary's fields, then what was sent and what came back.</summary>
public sealed record AiCallDetail(
    long Id, DateTime Utc, DateTime? CompletedUtc, string AgentKey, string AgentName, string Tier, string Source,
    string RequestedBy, string? Model, string Outcome, int AttemptCount, int Fallbacks, double Seconds,
    int? PromptTokens, int? CompletionTokens, int? TotalTokens, string Summary, string Error, string ConversationId,
    int ToolCalls, int Rounds,
    string System, IReadOnlyList<AiMessageDto> Messages, string Answer, string Reasoning,
    string FinishReason, IReadOnlyList<string> Chain, IReadOnlyList<AiAttemptDto> Attempts, IReadOnlyList<AiToolCallDto> Tools,
    AiRequestDto Request)
{
    /// <summary>The memories the call was given, in the order its prompt lists them.</summary>
    public IReadOnlyList<long> MemoryIds { get; init; } = [];

    public AiFeedbackDto? Feedback { get; init; }
}

/// <summary>The owner's verdict on an answer, and what it should have said with a 👎.</summary>
public sealed record AiFeedbackDto(int Score, string Note, string By, DateTime Utc);

public sealed record AiReportSummary(
    long Id, string AgentKey, string AgentName, string SubjectType, string SubjectId, string? SessionDate,
    DateTime CreatedUtc, DateTime UpdatedUtc, string Status, int Attempts, long? CallId, string? Model, string Title,
    string? Error, string? Link);

public sealed record AiReportPage(IReadOnlyList<AiReportSummary> Reports, long? NextBeforeId);

public sealed record AiReportDetail(AiReportSummary Report, string Body, JsonElement? Data);

public sealed record AiReportDay(string Date, int Ok, int Invalid, int Failed);

public sealed record AiReportAgentStats(
    string AgentKey, string AgentName, int Total, int Ok, int Invalid, int Failed, double? ValidPercent, IReadOnlyList<AiReportDay> Days);

public sealed record AiReportStats(string Since, int Days, IReadOnlyList<AiReportAgentStats> Agents);

public sealed record AiAgentRunRequest(string? SubjectId);

public sealed record AiDocIndexStatusDto(int Files, int Passages, DateTime? IndexedUtc, string Model);

public sealed record AiTelegramOwnerDto(string TelegramUserId, string TelegramName, string ConsoleUser, DateTime LinkedUtc);

/// <summary><c>running</c>: enabled, a bot token and a model key are all there; <c>enabled</c>: the setting alone.</summary>
public sealed record AiTelegramStatus(bool Running, bool Enabled, string? BotUsername, IReadOnlyList<AiTelegramOwnerDto> Owners);

public sealed record AiTelegramPairCode(string Code, DateTime ExpiresUtc, string? BotUsername, string Instruction);

public sealed record AiSearchHit(string File, string Section, double Score, string Text);

public sealed record AiSearchResult(AiDocIndexStatusDto Index, string? Query, IReadOnlyList<AiSearchHit> Hits);

public sealed record AiAskMessage(string? Role, string? Content);

public sealed record AiAskRequest(
    List<AiAskMessage>? Messages, string? Tier, string? System, int? MaxTokens, double? Temperature, string? ConversationId, string? Agent);

public sealed record AiUsageDto(int? PromptTokens, int? CompletionTokens, int? TotalTokens);

public sealed record AiAnswer(
    long CallId, string Outcome, string Model, string Text, string Reasoning, double Seconds, string FinishReason,
    AiUsageDto? Usage, int Fallbacks, IReadOnlyList<AiAttemptDto> Attempts, string Error, IReadOnlyList<AiToolCallDto> Tools, int Rounds)
{
    /// <summary>The memories the answer was given.</summary>
    public IReadOnlyList<long> MemoryIds { get; init; } = [];
}
