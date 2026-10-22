using System.Text.Json;
using System.Text.RegularExpressions;
using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services;
using AlgoTrading.Api.Services.AiActions;
using AlgoTrading.Api.Services.AiVoice;
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
            if (AiSettingsStore.ChainProblem(body.Chain, models.KnownIds(), bedrockOnly: _settings.CurrentValue.Bedrock.Enabled) is string problem) return BadRequest(new { error = problem });
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
            if (AiSettingsStore.ChainProblem(body.Chain, models.KnownIds(), bedrockOnly: _settings.CurrentValue.Bedrock.Enabled) is string problem) return BadRequest(new { error = problem });
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

        // NVIDIA's catalog, then the Claude and open models the desk can ask on Bedrock (it keeps its own list of each).
        var owners = list.Models.ToDictionary(m => m.Id, m => m.OwnedBy, StringComparer.Ordinal);
        foreach (var claude in AiCatalog.BedrockModels) owners[claude.Id] = "anthropic";
        foreach (var open in AiCatalog.BedrockOpenModels) owners[open.Id] = AiCatalog.BedrockOpenModelId(open.Id).Split('.')[0];
        var s = _settings.CurrentValue;
        var ids = inUse.Concat(owners.Keys.Where(id => !inUse.Contains(id)).OrderBy(id => id, StringComparer.Ordinal));

        var models = ids.Select(id => new AiModelDto(
            id,
            owners.TryGetValue(id, out string? owner) && owner.Length > 0 ? owner : AiCatalog.BedrockModelId(id).Split('/', '.')[0],
            inUse.Contains(id),
            state.Tiers.Where(t => t.Chain.Contains(id)).Select(t => t.Def.Key).ToList(),
            state.Agents.Where(a => a.Def.Built && a.Chain.Contains(id)).Select(a => a.Def.Key).ToList(),
            ModelNote(id, state),
            tests.TryGetValue(id, out var test) ? test : null,
            usage.TryGetValue(id, out var used) ? used : new AiModelUsage(0, 0, 0, null),
            AiCatalog.IsEmbeddingModel(id),
            AiCatalog.IsBedrock(id) ? AiCatalog.BedrockModelIds.Contains(id) || id == AiEmbedder.Target(s)
            : AiCatalog.IsBedrockOpen(id) ? AiCatalog.BedrockOpenModelIds.Contains(id)
            : list.Models.Count == 0 || owners.ContainsKey(id))
        {
            Health = inUse.Contains(id) ? ToHealth(_health.State(id, Now())) : null,
            Provider = AiCatalog.ProviderOf(id),
            Name = AiCatalog.BedrockModel(id)?.Name ?? AiCatalog.BedrockOpenModel(id)?.Name,
            Available = AiCatalog.IsBedrock(id) ? s.Bedrock.Enabled : AiCatalog.IsBedrockOpen(id) ? s.BedrockOpen.Enabled : s.KeyConfigured,
            Price = AiGateway.Price(id, s) is { } p ? new AiModelPriceDto(p.Input, p.Output, p.CacheRead, p.CacheWrite, AiModelPrice.Note) : null,
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
            new AiRequestDto(RequestLine(x.Model.Length > 0 ? x.Model : ReadList<string>(x.ChainJson).FirstOrDefault() ?? string.Empty), x.MaxTokens, x.Temperature,
                // A voice note's transcription and a spoken reply are one request and one answer each.
                x.AgentKey is not (AiCatalog.VoiceTranscribe or AiCatalog.VoiceSpeak)))
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
        if (!docs.Embedder.CanEmbed(status.Model))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = $"{status.Model} cannot be asked now: no NVIDIA_API_KEY on the server, or Amazon Bedrock is off." });
        }

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

    // ---------- embeddings --------------------------------------------------

    /// <summary>
    /// Which model the docs and memories are embedded with now, the one the re-embedding would switch to, and how many
    /// vectors each model holds.
    /// </summary>
    [HttpGet("embeddings")]
    public async Task<IActionResult> Embeddings([FromServices] AiEmbedder embedder, CancellationToken cancellationToken)
    {
        var s = _settings.CurrentValue;
        string active = await embedder.ActiveModelAsync(_db, cancellationToken);
        var switched = await _db.SystemSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Key == AiEmbedder.ActiveModelKey, cancellationToken);
        var memories = await _db.AiMemories.AsNoTracking().GroupBy(m => m.EmbeddingModel).Select(g => new { Model = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var passages = await _db.AiDocChunks.AsNoTracking().GroupBy(c => c.Model).Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return Ok(new AiEmbeddingsDto(
            active,
            AiEmbedder.Target(s),
            s.Bedrock.Enabled,
            AiReembedder.Running,
            switched is null ? null : new AiEmbeddingSwitchDto(switched.Value, Utc(switched.UpdatedUtc), switched.UpdatedBy, switched.Reason),
            memories.ToDictionary(m => m.Model.Length > 0 ? m.Model : "(none)", m => m.Count),
            passages.ToDictionary(p => p.Key.Length > 0 ? p.Key : "(none)", p => p.Count)));
    }

    /// <summary>
    /// Embeds every memory and doc passage again with Bedrock's model and, only when all are done, switches search
    /// and memory to it. Runs in the background and answers 202; the result is one row on the Calls tab (agent
    /// <c>reembed</c>).
    /// </summary>
    [HttpPost("embeddings/reembed")]
    public IActionResult Reembed()
    {
        var s = _settings.CurrentValue;
        if (!s.Bedrock.Enabled) return Conflict(new { error = "Amazon Bedrock is off (Ai:Bedrock:Enabled is false): there is nothing to switch to." });
        if (AiReembedder.Running) return Conflict(new { error = "A re-embedding is already running." });
        if (_scopes is null) return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "The API cannot run background work here." });

        string by = Actor();
        var scopes = _scopes;
        var logger = _logger;
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<AiReembedder>().RunAsync(by, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Re-embedding with Bedrock failed");
            }
        });

        return Accepted(new { started = true, model = AiEmbedder.Target(s) });
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

        var detail = new AiReportDetail(ToReportSummary(r, links), r.Body, data);
        if (r.AgentKey == AiCatalog.ComplianceTriage && AlgoTrading.Api.Services.AiAgents.ComplianceTriageAgent.CircularIdOf(r.SubjectId) is long circularId)
        {
            var followUp = await _db.ComplianceFollowUps.AsNoTracking().FirstOrDefaultAsync(f => f.CircularId == circularId, cancellationToken);
            detail = detail with { FollowUp = followUp is null ? null : ToFollowUp(followUp) };
        }

        return Ok(detail);
    }

    // ---------- compliance --------------------------------------------------

    /// <summary>
    /// The circulars and broker notices the Compliance Triage found apply to the desk, newest report first: the dates
    /// each takes effect, what changes on the desk, the alert that went, and whether it was marked handled. Sentinel's
    /// checkup reads it for "Rule changes ahead"; <c>today</c> is the IST day the dates are read against.
    /// </summary>
    [HttpGet("compliance")]
    public async Task<IActionResult> Compliance(CancellationToken cancellationToken)
    {
        var reports = await _db.AiReports.AsNoTracking()
            .Where(r => r.AgentKey == AiCatalog.ComplianceTriage && r.SubjectType == AiReportSubject.Check && r.Status == AiReportStatus.Ok
                        && r.DataJson.Contains("\"applies\":\"yes\""))
            .OrderByDescending(r => r.Id)
            .Take(500)
            .ToListAsync(cancellationToken);
        var applies = reports
            .Select(r => (Report: r, Circular: AlgoTrading.Api.Services.AiAgents.ComplianceTriageAgent.CircularIdOf(r.SubjectId),
                Facts: AlgoTrading.Api.Services.AiAgents.ComplianceTriageAgent.ReadFacts(r.DataJson)))
            .Where(x => x.Circular is not null && x.Facts?.Applies == "yes")
            .ToList();
        var ids = applies.Select(x => x.Circular!.Value).Distinct().ToList();
        var circulars = await _db.RegulatoryCirculars.AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, cancellationToken);
        var followUps = await _db.ComplianceFollowUps.AsNoTracking().Where(f => ids.Contains(f.CircularId)).ToDictionaryAsync(f => f.CircularId, cancellationToken);

        var items = applies
            .Where(x => circulars.ContainsKey(x.Circular!.Value))
            .Select(x =>
            {
                var c = circulars[x.Circular!.Value];
                var f = x.Facts!;
                return new AiComplianceItem(x.Report.Id, c.Id, AlgoTrading.Api.Services.AiAgents.ComplianceAlertText.SourceName(c.Source),
                    c.Number.Length > 0 ? c.Number : null, c.Title, c.PublishedOn?.ToString("yyyy-MM-dd"), c.Url.Length > 0 ? c.Url : null,
                    f.Area, f.Change.Length > 0 ? f.Change : null,
                    f.EffectiveDates.Select(d => new AiEffectiveDateDto(d.Date.ToString("yyyy-MM-dd"), d.What)).ToList(), f.DeskChanges,
                    followUps.TryGetValue(c.Id, out var row) ? ToFollowUp(row) : null);
            })
            .ToList();
        return Ok(new AiComplianceList(IstTime.DateOf(Now()).ToString("yyyy-MM-dd"), items));
    }

    /// <summary>
    /// Marks the circular a Compliance Triage report is about as handled: the desk is ready for it, with what was done.
    /// It then leaves the checkup's "Rule changes ahead", and no alert goes for it if none went yet. Marking it again
    /// replaces the note.
    /// </summary>
    [HttpPost("compliance/{reportId:long}/handled")]
    public async Task<IActionResult> MarkComplianceHandled(long reportId, [FromBody] AiComplianceHandledRequest? body, CancellationToken cancellationToken)
    {
        string note = (body?.Note ?? string.Empty).Trim();
        if (note.Length == 0) return BadRequest(new { error = "Say what was done: a note is required." });
        if (note.Length > MaxHandledNote) return BadRequest(new { error = $"Keep the note to {MaxHandledNote} characters." });

        var report = await _db.AiReports.AsNoTracking().FirstOrDefaultAsync(r => r.Id == reportId, cancellationToken);
        if (report is null) return NotFound(new { error = $"No report {reportId}." });
        if (report.AgentKey != AiCatalog.ComplianceTriage
            || AlgoTrading.Api.Services.AiAgents.ComplianceTriageAgent.CircularIdOf(report.SubjectId) is not long circularId)
        {
            return Conflict(new { error = $"Report {reportId} is not the Compliance Triage's on a circular." });
        }

        if (!await _db.RegulatoryCirculars.AnyAsync(c => c.Id == circularId, cancellationToken)) return NotFound(new { error = $"No circular {circularId}." });

        var now = Now();
        var row = await _db.ComplianceFollowUps.FirstOrDefaultAsync(f => f.CircularId == circularId, cancellationToken);
        if (row is null)
        {
            row = new ComplianceFollowUp { CircularId = circularId, ReportId = reportId, CreatedUtc = now };
            _db.ComplianceFollowUps.Add(row);
        }

        row.HandledUtc = now;
        row.HandledBy = Actor();
        row.HandledNote = note;
        await _db.SaveChangesAsync(cancellationToken);
        HttpContext.Describe($"Marked circular {circularId} handled (Compliance Triage report #{reportId}).", "compliance",
            circularId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Ok(ToFollowUp(row));
    }

    /// <summary>The longest note "Mark handled" keeps.</summary>
    public const int MaxHandledNote = 2000;

    private static AiComplianceFollowUpDto ToFollowUp(ComplianceFollowUp f) => new(
        f.Id, f.CircularId, f.ReportId, Utc(f.AlertedUtc), f.AlertDelivered, Utc(f.HandledUtc),
        f.HandledBy.Length > 0 ? f.HandledBy : null, f.HandledNote.Length > 0 ? f.HandledNote : null);

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
        var def = key is AiCatalog.AssistantCheck or AiCatalog.AssistantExam or AiCatalog.AiTraderReflect
            ? AiCatalog.Agent(key)
            : AiCatalog.Agents.FirstOrDefault(a => a.Key == key);
        if (def is null) return NotFound(new { error = $"No agent {key}." });
        if (key is not (AiCatalog.TradeReviewer or AiCatalog.NewsAnalyst or AiCatalog.IncidentExplainer or AiCatalog.AssistantCheck or AiCatalog.AssistantExam
            or AiCatalog.AiTrader or AiCatalog.AiTraderReflect or AiCatalog.ExecutionReporter
            or AiCatalog.TechnicalAnalyst or AiCatalog.OptionsAnalyst or AiCatalog.MacroAnalyst or AiCatalog.FundamentalAnalyst
            or AiCatalog.DataCurator or AiCatalog.ComplianceTriage or AiCatalog.StrategyResearcher))
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
                : key == AiCatalog.AiTraderReflect
                ? "The AI Trader's lessons run only while the AI Trader is on and Ai:AiTraderLessons is true."
                : $"{def.Name} is switched off." });
        }
        if (!_settings.CurrentValue.ProviderConfigured) return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "No model provider: no NVIDIA_API_KEY on the server, and Amazon Bedrock (Claude and the open models) is off." });

        string? subject = string.IsNullOrWhiteSpace(body?.SubjectId) ? null : body.SubjectId.Trim();
        if (subject is not null && key is AiCatalog.TechnicalAnalyst or AiCatalog.OptionsAnalyst or AiCatalog.MacroAnalyst)
        {
            // A slot analyst writes about the moment it runs: there is nothing else to name.
            return BadRequest(new { error = $"{def.Name} writes about the moment it runs: give no subjectId." });
        }

        if (key is AiCatalog.DataCurator or AiCatalog.StrategyResearcher)
        {
            // A day or a week: "night:2026-10-09", "morning:2026-10-10", "week:2026-10-11", or the date alone.
            if (subject is not null && !System.Text.RegularExpressions.Regex.IsMatch(subject, @"^((night|morning|week):)?\d{4}-\d{2}-\d{2}$"))
            {
                return BadRequest(new { error = key == AiCatalog.DataCurator
                    ? "subjectId is a day (yyyy-MM-dd), night:yyyy-MM-dd or morning:yyyy-MM-dd."
                    : "subjectId is a week's Sunday (yyyy-MM-dd) or week:yyyy-MM-dd." });
            }
        }
        else if (subject is not null && !long.TryParse(subject, out _))
        {
            return BadRequest(new { error = key switch
            {
                AiCatalog.AiTraderReflect => "subjectId is a replay's id.",
                AiCatalog.FundamentalAnalyst => "subjectId is a filing's id.",
                AiCatalog.ExecutionReporter => "subjectId is one of the AI Trader's paper orders' id.",
                AiCatalog.ComplianceTriage => "subjectId is a circular's id.",
                _ => "subjectId is a run or incident id.",
            } });
        }

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
        var orderIds = reports.Where(r => r.SubjectType == AiReportSubject.Order).Select(r => long.TryParse(r.SubjectId, out long v) ? v : 0).Where(v => v > 0).ToList();
        var orders = orderIds.Count == 0 ? [] : await _db.PaperOrders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).Select(o => new { o.Id, o.SimulationRunId }).ToListAsync(cancellationToken);
        var news = newsIds.Count == 0 ? [] : await _db.NewsItems.AsNoTracking().Where(n => newsIds.Contains(n.Id)).Select(n => new { n.Id, n.Link }).ToListAsync(cancellationToken);
        var filings = filingIds.Count == 0 ? [] : await _db.CorporateAnnouncements.AsNoTracking().Where(a => filingIds.Contains(a.Id)).Select(a => new { a.Id, a.AttachmentUrl }).ToListAsync(cancellationToken);
        // The Compliance Triage's reports are about a circular: its page (SEBI) or its document (NSE, BSE).
        var circularIds = reports
            .Where(r => r.AgentKey == AiCatalog.ComplianceTriage && r.SubjectId.StartsWith(AlgoTrading.Api.Services.AiAgents.ComplianceTriageAgent.SubjectPrefix, StringComparison.Ordinal))
            .Select(r => long.TryParse(r.SubjectId[AlgoTrading.Api.Services.AiAgents.ComplianceTriageAgent.SubjectPrefix.Length..], out long v) ? v : 0)
            .Where(v => v > 0)
            .ToList();
        var circulars = circularIds.Count == 0 ? [] : await _db.RegulatoryCirculars.AsNoTracking().Where(c => circularIds.Contains(c.Id)).Select(c => new { c.Id, c.Url }).ToListAsync(cancellationToken);

        foreach (var r in reports)
        {
            if (r.AgentKey == AiCatalog.ComplianceTriage)
            {
                links[(r.SubjectType, r.SubjectId)] = circulars.FirstOrDefault(c => AlgoTrading.Api.Services.AiAgents.ComplianceTriageAgent.Subject(c.Id) == r.SubjectId)?.Url;
                continue;
            }

            links[(r.SubjectType, r.SubjectId)] = r.SubjectType switch
            {
                AiReportSubject.Run => $"/trade/runs/{r.SubjectId}",
                AiReportSubject.Incident => $"/system/incidents?id={r.SubjectId}",
                AiReportSubject.News => news.FirstOrDefault(n => n.Id.ToString() == r.SubjectId)?.Link,
                AiReportSubject.Filing => filings.FirstOrDefault(a => a.Id.ToString() == r.SubjectId)?.AttachmentUrl,
                // The AI Trader's decision is on its panel; a fill is in its manual book.
                AiReportSubject.Decision => $"/ai/agents?agent={AiCatalog.AiTrader}",
                AiReportSubject.Order => orders.FirstOrDefault(o => o.Id.ToString() == r.SubjectId) is { } o ? $"/trade/runs/{o.SimulationRunId}" : null,
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
                    costUsd = result.CostUsd,
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
            CostUsd = result.CostUsd,
        };

        return result.Outcome == AiCallOutcome.Ok ? Ok(answer) : StatusCode(StatusCodes.Status502BadGateway, answer);
    }

    // ---------- actions (Ira, with a PIN) -----------------------------------

    /// <summary>
    /// The signed-in admin's Action PIN as AI → Assistant shows it: whether actions are on, whether a PIN is set (never
    /// the PIN), when a lock after wrong PINs ends, and how long an action waits.
    /// </summary>
    [HttpGet("actions/pin")]
    public async Task<IActionResult> ActionPin([FromServices] IraActions actions, CancellationToken cancellationToken)
    {
        var owner = await actions.OwnerAsync(User.GetUserId(), cancellationToken);
        if (owner is null) return StatusCode(StatusCodes.Status403Forbidden, new { error = NotAnOwner });
        return Ok(await PinStatusAsync(actions, owner, cancellationToken));
    }

    /// <summary>
    /// Sets or resets the signed-in admin's Action PIN, 4 to 8 digits. Signed in already, the console needs no old PIN
    /// (Telegram's <c>/pin old new</c> does). Only a salted hash is kept; the request body is never logged.
    /// </summary>
    [HttpPut("actions/pin")]
    public async Task<IActionResult> SetActionPin([FromBody] AiActionPinRequest? body, [FromServices] IraActions actions, CancellationToken cancellationToken)
    {
        var owner = await actions.OwnerAsync(User.GetUserId(), cancellationToken);
        if (owner is null) return StatusCode(StatusCodes.Status403Forbidden, new { error = NotAnOwner });
        if (!IraPin.IsShape(body?.Pin)) return BadRequest(new { error = "A PIN is 4 to 8 digits." });

        var (change, _, _) = await actions.SetPinAsync(owner, body!.Pin!, null, requireCurrent: false, "the console", cancellationToken);
        HttpContext.Describe(change == IraPinChange.Changed ? "Reset the Action PIN for Ira's actions." : "Set the Action PIN for Ira's actions.", "pin");
        return Ok(await PinStatusAsync(actions, owner, cancellationToken));
    }

    /// <summary>
    /// Confirms an action the Desk Assistant proposed on the console with the owner's PIN, and carries it out once:
    /// 200 with how it ended (executed or failed); 403 a wrong PIN (<c>attemptsLeft</c>); 423 locked after three
    /// (<c>lockedUntilUtc</c>); 409 no longer waiting (expired, cancelled, already done) or no PIN set; 404 not this
    /// admin's console action; 503 actions switched off.
    /// </summary>
    [HttpPost("actions/{id:long}/confirm")]
    public async Task<IActionResult> ConfirmAction(long id, [FromBody] AiActionConfirmRequest? body, [FromServices] IraActions actions, CancellationToken cancellationToken)
    {
        var owner = await actions.OwnerAsync(User.GetUserId(), cancellationToken);
        if (owner is null) return StatusCode(StatusCodes.Status403Forbidden, new { error = NotAnOwner });

        var result = await actions.ConfirmAsync(id, owner, body?.Pin, AiActionChannel.Console, cancellationToken);
        HttpContext.Describe($"Ira's action #{id}: {ActionOutcomeWord(result.Kind)}.", "ai-action", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return ActionResult(result);
    }

    /// <summary>Cancels an action the Desk Assistant proposed on the console while it waits: 200, or 409 when it no longer waits.</summary>
    [HttpPost("actions/{id:long}/cancel")]
    public async Task<IActionResult> CancelAction(long id, [FromServices] IraActions actions, CancellationToken cancellationToken)
    {
        var owner = await actions.OwnerAsync(User.GetUserId(), cancellationToken);
        if (owner is null) return StatusCode(StatusCodes.Status403Forbidden, new { error = NotAnOwner });

        var result = await actions.CancelAsync(id, owner, AiActionChannel.Console, cancellationToken);
        HttpContext.Describe($"Ira's action #{id}: {ActionOutcomeWord(result.Kind)}.", "ai-action", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return ActionResult(result);
    }

    private const string NotAnOwner = "Ira's actions are for an active admin's account.";

    private static async Task<AiActionPinStatus> PinStatusAsync(IraActions actions, IraOwner owner, CancellationToken cancellationToken) =>
        new(actions.Enabled, await actions.HasPinAsync(owner.UserId, cancellationToken), Utc(actions.LockedUntil(owner.UserId)), actions.ConfirmSeconds);

    private IActionResult ActionResult(IraConfirmResult result)
    {
        var view = result.Action is { } a ? ToActionView(a) : null;
        return result.Kind switch
        {
            IraConfirmKind.Executed or IraConfirmKind.Failed or IraConfirmKind.Cancelled => Ok(new { message = result.Message, action = view }),
            IraConfirmKind.WrongPin => StatusCode(StatusCodes.Status403Forbidden, new { error = result.Message, attemptsLeft = result.AttemptsLeft, action = view }),
            IraConfirmKind.Locked => StatusCode(StatusCodes.Status423Locked, new { error = result.Message, lockedUntilUtc = Utc(result.LockedUntilUtc), action = view }),
            IraConfirmKind.NotFound => NotFound(new { error = result.Message }),
            IraConfirmKind.Off => StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = result.Message }),
            _ => Conflict(new { error = result.Message, action = view }),
        };
    }

    private static string ActionOutcomeWord(IraConfirmKind kind) => kind switch
    {
        IraConfirmKind.Executed => "executed",
        IraConfirmKind.Failed => "not done",
        IraConfirmKind.WrongPin => "wrong PIN",
        IraConfirmKind.Locked => "locked after wrong PINs",
        IraConfirmKind.Expired => "expired",
        IraConfirmKind.Cancelled => "cancelled",
        IraConfirmKind.NoPin => "no PIN set",
        IraConfirmKind.Off => "actions switched off",
        IraConfirmKind.NotFound => "not found",
        _ => "no longer waiting",
    };

    private static AiActionView ToActionView(AiAction a) => new(
        a.Id, a.Kind, a.Status, a.Summary, a.Channel, Utc(a.CreatedUtc)!.Value, Utc(a.ExpiresUtc)!.Value, Utc(a.ConfirmedUtc), Utc(a.ExecutedUtc),
        IraActions.ResultMessage(a), a.Error.Length > 0 ? a.Error : null, a.CallId);

    // ---------- voice ("Ira" on the console) --------------------------------

    /// <summary>The types a console recording may come as: WAV, which is what the console records.</summary>
    private static readonly string[] WavTypes = ["audio/wav", "audio/x-wav", "audio/wave", "audio/vnd.wave"];

    /// <summary>A recording shorter than this holds no question.</summary>
    private const double MinVoiceSeconds = 0.3;

    /// <summary>The longest text a spoken reply is made from (only its first sentences are ever said).</summary>
    private const int MaxSpeakChars = 100_000;

    private const string VoiceOff = "Voice is switched off on this desk (Ai:Voice:Enabled). Type the question instead.";

    private const string HandsFreeOff = "Hands-free is switched off on this desk (Ai:Voice:HandsFreePerMinute is 0). Use the mic button, or type the question.";

    /// <summary>
    /// What AI → Assistant needs to know about the voice: the assistant's name as it is now (set with /name on
    /// Telegram), whether voice and spoken replies are on, and how many hands-free utterances a minute the desk takes.
    /// </summary>
    [HttpGet("voice")]
    public async Task<IActionResult> GetVoice([FromServices] AiVoiceService voice, CancellationToken cancellationToken)
    {
        var s = _settings.CurrentValue.Voice;
        return Ok(new AiVoiceInfo(await voice.NameAsync(cancellationToken), s.Enabled, s.SpeakReplies, Math.Max(0, s.HandsFreePerMinute)));
    }

    /// <summary>
    /// A question spoken on AI → Assistant, written down by the audio model (<see cref="AiVoiceService"/>, as Telegram's
    /// voice notes are). The body is the recording as <c>audio/wav</c> (the console records 16 kHz mono 16-bit), at
    /// most <c>Ai:Voice:ConsoleMaxBytes</c> and <c>Ai:Voice:MaxSeconds</c>; it counts toward the asker's AI rate limit
    /// as a question does. The audio is not kept: the call's row (agent <c>voice-transcribe</c>, source
    /// <c>console</c>) holds the transcript. The console then asks the transcript exactly as a typed question.
    /// </summary>
    /// <remarks>
    /// <c>wake=1</c>: a hands-free utterance, which may not have been said to the assistant at all. The answer adds
    /// whether it was (<c>addressed</c>: its name first, after an optional greeting) and the <c>question</c> without the
    /// name; one that was not is still a row, summarised "not addressed: …". These have a budget of their own,
    /// <c>Ai:Voice:HandsFreePerMinute</c> a minute per user (429 with Retry-After past it), instead of the AI rate limit
    /// a question counts toward: six a minute would spend that in five minutes of talk at the desk.
    /// </remarks>
    [HttpPost("voice/transcribe")]
    public async Task<IActionResult> Transcribe(
        [FromServices] AiVoiceService voice,
        [FromQuery] string? conversationId,
        CancellationToken cancellationToken,
        [FromQuery] string? wake = null)
    {
        var s = _settings.CurrentValue.Voice;
        if (!s.Enabled) return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = VoiceOff });
        bool handsFree = wake?.Trim().ToLowerInvariant() is "1" or "true" or "yes";
        if (handsFree && s.HandsFreePerMinute <= 0) return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = HandsFreeOff });

        string type = (Request.ContentType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();
        if (!WavTypes.Contains(type))
        {
            return StatusCode(StatusCodes.Status415UnsupportedMediaType, new
            {
                error = $"Send the recording as audio/wav{(type.Length > 0 ? $", not {type}" : string.Empty)}: the console records WAV, and the desk writes WAV down.",
            });
        }

        string conversation = conversationId?.Trim() ?? string.Empty;
        if (conversation.Length > 0 && !ConversationIdShape.IsMatch(conversation)) return BadRequest(new { error = "conversationId is up to 64 letters, digits, - or _." });

        string tooLarge = $"That recording is larger than {s.ConsoleMaxBytes / (1024.0 * 1024.0):0.#} MB, the most the desk takes. Record a shorter question, or type it.";
        if (Request.ContentLength > s.ConsoleMaxBytes) return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = tooLarge });
        byte[]? wav = await ReadUpToAsync(Request.Body, s.ConsoleMaxBytes, cancellationToken);
        if (wav is null) return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = tooLarge });
        if (wav.Length == 0) return BadRequest(new { error = "Nothing was recorded: the request had no audio." });

        WavHeader header;
        try
        {
            header = WavHeader.Read(wav);
        }
        catch (InvalidDataException ex)
        {
            return BadRequest(new { error = $"That recording could not be read: {ex.Message}. Record it again, or type the question." });
        }

        double seconds = Math.Round(header.Seconds, 2);
        if (seconds > s.MaxSeconds)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new
            {
                error = $"That recording is {Math.Ceiling(seconds):0} s long; the desk listens to {s.MaxSeconds} s at most. Record a shorter question, or type it.",
            });
        }

        if (seconds < MinVoiceSeconds) return BadRequest(new { error = "That recording is too short to hold a question. Speak a little longer, or type it." });

        if (handsFree) return await HandsFreeAsync(voice, wav, seconds, conversation, cancellationToken);

        // The same brake as a typed question's: a stuck button cannot spend the audio model's share either.
        var (lease, refusal) = _limiter.TryAcquire(Actor());
        if (lease is null)
        {
            Response.Headers.RetryAfter = refusal!.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = refusal.Error, retryAfterSeconds = refusal.RetryAfterSeconds });
        }

        VoiceTranscript heard;
        using (lease)
        {
            try
            {
                heard = await voice.TranscribeAsync(
                    new VoiceAudio(wav, "wav", seconds, seconds, "recording", wav.Length),
                    new VoiceCaller(AiVoiceService.Console, Actor(), User.GetUserId(), conversation),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new EmptyResult();
            }
        }

        if (heard.Failure is not null)
        {
            // A provider's trouble, not the desk's: Information, as every AI call's failure is.
            _logger?.LogInformation("Console voice: call {CallId} could not write a recording down: {Outcome}", heard.CallId, heard.Failure);
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                error = $"The audio model could not make out that recording: {heard.Failure} (call #{heard.CallId}). Try again, or type the question.",
                callId = heard.CallId,
            });
        }

        return Ok(new AiVoiceTranscript(heard.Text!, heard.CallId, seconds));
    }

    /// <summary>A hands-free utterance (<c>wake=1</c>), already read and checked: counted on its own budget, written down, and checked for the name.</summary>
    private async Task<IActionResult> HandsFreeAsync(AiVoiceService voice, byte[] wav, double seconds, string conversation, CancellationToken cancellationToken)
    {
        if (!voice.TryStartHandsFree(Actor(), out int wait))
        {
            Response.Headers.RetryAfter = wait.ToString(System.Globalization.CultureInfo.InvariantCulture);
            int cap = _settings.CurrentValue.Voice.HandsFreePerMinute;
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                error = $"Hands-free has sent {cap} utterances to be written down in the last minute, the most the desk takes " +
                        "(Ai:Voice:HandsFreePerMinute): too much speech. It listens again shortly.",
                retryAfterSeconds = wait,
            });
        }

        VoiceTranscript heard;
        try
        {
            heard = await voice.TranscribeAsync(
                new VoiceAudio(wav, "wav", seconds, seconds, "hands-free utterance", wav.Length),
                new VoiceCaller(AiVoiceService.Console, Actor(), User.GetUserId(), conversation),
                wake: true,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new EmptyResult();
        }

        if (heard.Failure is not null)
        {
            _logger?.LogInformation("Console voice: call {CallId} could not write a hands-free utterance down: {Outcome}", heard.CallId, heard.Failure);
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                error = $"The audio model could not make out what was said: {heard.Failure} (call #{heard.CallId}).",
                callId = heard.CallId,
            });
        }

        var wake = heard.Wake ?? VoiceWake.NotAddressed;
        return Ok(new AiVoiceWakeTranscript(heard.Text!, wake.Addressed, wake.Question, heard.CallId, seconds));
    }

    /// <summary>
    /// An answer spoken, as <c>audio/mpeg</c>: its spoken version (<see cref="VoiceText.Spoken"/>: no tables, links or
    /// citations, cut at a sentence to <c>Ai:Voice:MaxSpokenChars</c>) by Amazon Polly, logged as agent
    /// <c>voice-speak</c>, source <c>console</c>, in the conversation of <c>callId</c> when one is given. Polly
    /// without the role's permission is a 503 in words, and is not asked again for an hour, from here or Telegram.
    /// </summary>
    [HttpPost("voice/speak")]
    public async Task<IActionResult> Speak(
        [FromServices] AiVoiceService voice,
        [FromBody] AiVoiceSpeakRequest? body,
        CancellationToken cancellationToken)
    {
        if (!_settings.CurrentValue.Voice.Enabled) return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = VoiceOff });

        string text = body?.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return BadRequest(new { error = "Nothing to speak: text is empty." });
        if (text.Length > MaxSpeakChars) return BadRequest(new { error = $"The text is over {MaxSpeakChars:N0} characters." });

        string conversation = string.Empty;
        if (body!.CallId is long answered)
        {
            string? of = await _db.AiCalls.AsNoTracking().Where(c => c.Id == answered).Select(c => c.ConversationId).FirstOrDefaultAsync(cancellationToken);
            if (of is null) return NotFound(new { error = $"No call #{answered}." });
            conversation = of;
        }

        VoiceSpeech spoken;
        try
        {
            spoken = await voice.SpeakAsync(text, new VoiceCaller(AiVoiceService.Console, Actor(), User.GetUserId(), conversation), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new EmptyResult();
        }

        switch (spoken.Status)
        {
            case VoiceSpeechStatus.Spoken:
                Response.Headers["X-Ai-Call-Id"] = spoken.CallId!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return File(spoken.Mp3!, "audio/mpeg");
            case VoiceSpeechStatus.Off:
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Spoken replies are switched off on this desk (Ai:Voice:SpeakReplies)." });
            case VoiceSpeechStatus.NothingToSay:
                return UnprocessableEntity(new { error = "Nothing in this answer can be said aloud: it is all table or code." });
            case VoiceSpeechStatus.Refused or VoiceSpeechStatus.Paused:
                if (spoken.Status == VoiceSpeechStatus.Refused)
                {
                    _logger?.LogInformation("Console voice: Amazon Polly refused call {CallId} ({Why}); not asked again for an hour", spoken.CallId, spoken.Error);
                }

                int wait = Math.Max(1, (int)Math.Ceiling(((spoken.RetryAfterUtc ?? Now()) - Now()).TotalSeconds));
                Response.Headers.RetryAfter = wait.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    error = $"Spoken replies are off for now: the server's AWS role may not call Amazon Polly (it needs {PollySpeechClient.Permission}; " +
                            $"docs/modules/ai.md, Voice). The desk asks Polly again in {Math.Max(1, (int)Math.Ceiling(wait / 60.0))} min; the answer is above as text.",
                    callId = spoken.CallId,
                    retryAfterSeconds = wait,
                });
            default:
                _logger?.LogInformation("Console voice: call {CallId} could not speak the answer: {Outcome}", spoken.CallId, spoken.Error);
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    error = $"Amazon Polly could not speak the answer: {spoken.Error} (call #{spoken.CallId}). The answer is above as text.",
                    callId = spoken.CallId,
                });
        }
    }

    /// <summary>The request's body, or null when it runs past <paramref name="max"/> bytes (read no further).</summary>
    private static async Task<byte[]?> ReadUpToAsync(Stream body, int max, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > max) return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
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
        AiCatalog.ProviderTerms)
    {
        Bedrock = new AiBedrockDto(s.Bedrock.Enabled, s.Bedrock.Region, AiCatalog.BedrockEndpoint(s.Bedrock.Region),
            new Dictionary<string, string>(s.Bedrock.EffortByTier, StringComparer.OrdinalIgnoreCase)),
        BedrockOpen = new AiBedrockOpenDto(s.BedrockOpen.Enabled, s.Bedrock.Region, AiCatalog.BedrockOpenEndpoint(s.Bedrock.Region)),
    };

    /// <summary>
    /// The request a call made, as its detail shows it: a WebSocket upgrade (GET) for Amazon Transcribe streaming, a POST
    /// to <see cref="ChatEndpoint"/> for every other model.
    /// </summary>
    private string RequestLine(string model) => AiCatalog.IsAmazonTranscribe(model)
        ? $"GET {AmazonTranscribeClient.Endpoint(_settings.CurrentValue.Voice.TranscribeRegion.Trim())}"
        : $"POST {ChatEndpoint(model)}";

    /// <summary>
    /// Where a call's model was asked: Bedrock's Messages API for a <c>bedrock:</c> model, its OpenAI-compatible endpoint
    /// for a <c>bedrock-oss:</c> one, Amazon Polly for a spoken reply's <c>polly:</c> voice, else NVIDIA's.
    /// </summary>
    private string ChatEndpoint(string model) => AiCatalog.IsBedrock(model)
        ? AiCatalog.BedrockEndpoint(_settings.CurrentValue.Bedrock.Region)
        : AiCatalog.IsBedrockOpen(model)
            ? AiCatalog.BedrockOpenEndpoint(_settings.CurrentValue.Bedrock.Region)
            : AiCatalog.IsPolly(model)
                ? AiCatalog.PollyEndpoint(_settings.CurrentValue.Voice.PollyRegion)
                : $"{(_settings.CurrentValue.BaseUrl is { Length: > 0 } b ? b : AiCatalog.DefaultBaseUrl).TrimEnd('/')}/chat/completions";

    private static AiTierDto ToTier(AiTierState t) => new(
        t.Def.Key, t.Def.Label, t.Def.Purpose, t.Chain, t.DefaultChain, t.Overridden, t.Def.Chat, Utc(t.UpdatedUtc), t.UpdatedBy);

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
            _toolbox.For(a.Def).Select(t => new AiToolDto(t.Name, t.Description)).ToList())
        {
            Chat = AiCatalog.CanChat(a.Def),
            ChatTools = AiCatalog.CanChat(a.Def) ? _toolbox.For(a.Def, chat: true).Select(t => new AiToolDto(t.Name, t.Description)).ToList() : [],
        };
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
            // A skipped model was not asked: it is no call to it.
            foreach (var a in ReadAttempts(row.AttemptsJson).Where(a => !AiGateway.IsSkipped(a.Outcome)))
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

    private static AiUsageDto? Usage(AiUsage? u) => u is null
        ? null
        : new AiUsageDto(u.PromptTokens, u.CompletionTokens, u.TotalTokens) { CacheReadTokens = u.CacheReadTokens, CacheWriteTokens = u.CacheWriteTokens };

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

public sealed record AiProviderDto(string Key, string Name, string BaseUrl, bool KeyConfigured, string CatalogUrl, string Terms)
{
    /// <summary>The second provider: whether it is on, where, and Claude's effort per tier. No credential is ever shown: it has none.</summary>
    public AiBedrockDto? Bedrock { get; init; }

    /// <summary>The third provider, the open models on Bedrock: whether it is on, and where. No credential either.</summary>
    public AiBedrockOpenDto? BedrockOpen { get; init; }
}

public sealed record AiBedrockDto(bool Enabled, string Region, string Endpoint, IReadOnlyDictionary<string, string> EffortByTier);

public sealed record AiBedrockOpenDto(bool Enabled, string Region, string Endpoint);

/// <summary>The embedding model in use, the re-embedding's target, and the vectors each model holds (memories, doc passages).</summary>
public sealed record AiEmbeddingsDto(
    string Active, string Target, bool BedrockEnabled, bool Running, AiEmbeddingSwitchDto? Switched,
    IReadOnlyDictionary<string, int> Memories, IReadOnlyDictionary<string, int> Passages);

/// <summary>The last switch the re-embedding made: to which model, when, by whom, with what.</summary>
public sealed record AiEmbeddingSwitchDto(string Model, DateTime? Utc, string? By, string? Reason);

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
    DateTime? UpdatedUtc, string? UpdatedBy, string? Reason, IReadOnlyList<AiToolDto> Tools)
{
    /// <summary>Whether the owner can talk with it on AI → Assistant (and on Telegram): the Desk Assistant, or a built agent with a chat persona.</summary>
    public bool Chat { get; init; }

    /// <summary>What it reads when the owner talks with it; empty when it cannot be talked with.</summary>
    public IReadOnlyList<AiToolDto> ChatTools { get; init; } = [];
}

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

    /// <summary>Who serves it: NVIDIA's NIM API, Amazon Bedrock (Claude) or Amazon Bedrock (open models).</summary>
    public string Provider { get; init; } = AiCatalog.ProviderName;

    /// <summary>Its name, for a model on Bedrock; null when the id says it.</summary>
    public string? Name { get; init; }

    /// <summary>Whether its provider can be asked now (NVIDIA's key is there; Claude or the open models on Bedrock are on). A chain skips it otherwise.</summary>
    public bool Available { get; init; } = true;

    /// <summary>Its list price, for a Bedrock model with a verified price; null for NVIDIA's free tier and when unknown.</summary>
    public AiModelPriceDto? Price { get; init; }
}

/// <summary>US dollars per million tokens; <c>note</c> says whose price it is.</summary>
public sealed record AiModelPriceDto(decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite, string Note);

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

public sealed record AiReportDetail(AiReportSummary Report, string Body, JsonElement? Data)
{
    /// <summary>A Compliance Triage report's circular: its alert and handled mark; null when it has neither, or for any other report.</summary>
    public AiComplianceFollowUpDto? FollowUp { get; init; }
}

/// <summary>A circular's one compliance alert and the owner's handled mark.</summary>
public sealed record AiComplianceFollowUpDto(
    long Id, long CircularId, long? ReportId, DateTime? AlertedUtc, bool AlertDelivered, DateTime? HandledUtc, string? HandledBy, string? Note);

/// <summary>A day something in a circular takes effect, yyyy-MM-dd, and what.</summary>
public sealed record AiEffectiveDateDto(string Date, string What);

/// <summary>A circular that applies to the desk, as its latest good triage reads it.</summary>
public sealed record AiComplianceItem(
    long ReportId, long CircularId, string Source, string? Number, string Title, string? PublishedOn, string? Url, string Area, string? Change,
    IReadOnlyList<AiEffectiveDateDto> EffectiveDates, IReadOnlyList<string> DeskChanges, AiComplianceFollowUpDto? FollowUp);

/// <summary>The circulars that apply, with the IST day their dates are read against.</summary>
public sealed record AiComplianceList(string Today, IReadOnlyList<AiComplianceItem> Circulars);

/// <summary>What was done about a circular, for "Mark handled".</summary>
public sealed record AiComplianceHandledRequest(string? Note);

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

/// <summary>A console recording written down: the transcript, its <c>voice-transcribe</c> call, and the recording's length.</summary>
public sealed record AiVoiceTranscript(string Text, long CallId, double Seconds);

/// <summary>
/// A hands-free utterance written down: what was heard, whether it was said to the assistant, the question without the
/// name (empty when not addressed, or when only the name was said), its <c>voice-transcribe</c> call and its length.
/// </summary>
public sealed record AiVoiceWakeTranscript(string Text, bool Addressed, string Question, long CallId, double Seconds);

/// <summary>The voice as AI → Assistant shows it: the assistant's name, the desk's switches, and the hands-free budget.</summary>
public sealed record AiVoiceInfo(string Name, bool Enabled, bool SpeakReplies, int HandsFreePerMinute);

/// <summary>The signed-in admin's Action PIN: whether actions are on, whether a PIN is set (never the PIN), a lock's end, an action's wait.</summary>
public sealed record AiActionPinStatus(bool Enabled, bool Set, DateTime? LockedUntilUtc, int ConfirmSeconds);

/// <summary>A new Action PIN, 4 to 8 digits.</summary>
public sealed record AiActionPinRequest(string? Pin);

/// <summary>The PIN that confirms an action.</summary>
public sealed record AiActionConfirmRequest(string? Pin);

/// <summary>An action as the console's confirm card shows it once it has an answer: its state, and what was done.</summary>
public sealed record AiActionView(
    long Id, string Kind, string Status, string Summary, string Channel, DateTime CreatedUtc, DateTime ExpiresUtc,
    DateTime? ConfirmedUtc, DateTime? ExecutedUtc, string? Result, string? Error, long? CallId);

/// <summary>An answer to speak; <c>callId</c>, the answer's own call, puts the spoken reply's row in its conversation.</summary>
public sealed record AiVoiceSpeakRequest(string? Text, long? CallId);

public sealed record AiUsageDto(int? PromptTokens, int? CompletionTokens, int? TotalTokens)
{
    /// <summary>Of the prompt tokens, those read from Claude's prompt cache; null for a provider that does not say.</summary>
    public int? CacheReadTokens { get; init; }

    /// <summary>Of the prompt tokens, those written to it.</summary>
    public int? CacheWriteTokens { get; init; }
}

public sealed record AiAnswer(
    long CallId, string Outcome, string Model, string Text, string Reasoning, double Seconds, string FinishReason,
    AiUsageDto? Usage, int Fallbacks, IReadOnlyList<AiAttemptDto> Attempts, string Error, IReadOnlyList<AiToolCallDto> Tools, int Rounds)
{
    /// <summary>The memories the answer was given.</summary>
    public IReadOnlyList<long> MemoryIds { get; init; } = [];

    /// <summary>What it cost in US dollars at list price; null when NVIDIA's free tier answered.</summary>
    public decimal? CostUsd { get; init; }
}
