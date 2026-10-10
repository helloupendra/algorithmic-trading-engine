using System.Text.Json;
using System.Text.RegularExpressions;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Ai;

/// <summary>A tier as it stands: its chain now, and whether the owner changed it.</summary>
public sealed record AiTierState(AiTierDef Def, IReadOnlyList<string> Chain, bool Overridden, DateTime? UpdatedUtc, string? UpdatedBy)
{
    /// <summary>The chain it walks when the owner has not changed it, with the providers as they are switched now.</summary>
    public IReadOnlyList<string> DefaultChain { get; init; } = Def.DefaultChain;
}

/// <summary>An agent as it stands: on or off, and the chain its calls walk.</summary>
public sealed record AiAgentState(
    AiAgentDef Def,
    bool Enabled,
    IReadOnlyList<string> Chain,
    bool ChainOverridden,
    DateTime? UpdatedUtc,
    string? UpdatedBy,
    string? Reason)
{
    /// <summary><c>planned</c> until it is built; then <c>on</c> or <c>off</c>.</summary>
    public string Status => !Def.Built ? "planned" : Enabled ? "on" : "off";
}

/// <summary>Everything the owner may change about the desk's AI, read in one query.</summary>
public sealed record AiState(IReadOnlyList<AiTierState> Tiers, IReadOnlyList<AiAgentState> Agents)
{
    public AiTierState Tier(string key) => Tiers.First(t => t.Def.Key == key);

    public AiAgentState? Agent(string key) => Agents.FirstOrDefault(a => a.Def.Key == key);
}

/// <summary>
/// What the owner may change about the AI at run time, kept in
/// <c>system_settings</c> under <c>ai.</c>: a tier's chain, and each agent's
/// switch and own chain.
/// </summary>
/// <remarks>
/// <para>
/// A missing row means the default: the tier's chain from <see cref="AiCatalog"/>,
/// an agent as its catalog entry starts (the Desk Assistant on, the scheduled
/// agents off) and walking its tier's chain. A row whose
/// value cannot be read (hand-edited, say) also counts as the default rather
/// than failing every call; the console shows the chain actually used.
/// </para>
/// <para>
/// While Amazon Bedrock is on (<see cref="AiBedrockSettings.Enabled"/>) every
/// chain is Claude (owner, 10 Oct 2026): an untouched tier walks its
/// <see cref="AiTierDef.BedrockChain"/>, and a chain the owner set keeps only
/// its Bedrock models, or the tier's when it has none. The stored rows are
/// not touched, so turning Bedrock off brings the NVIDIA chains back as they
/// were. The embed tier shows the embedding model in use (<see cref="AiEmbedder"/>).
/// </para>
/// <para>
/// While Claude is off and the open models on Bedrock are on
/// (<see cref="AiBedrockOpenSettings.Enabled"/>), an untouched tier walks its
/// <see cref="AiTierDef.OpenChain"/> and then its NVIDIA chain. A chain
/// the owner set is walked as set, and may name <c>bedrock-oss:</c> models.
/// </para>
/// </remarks>
public sealed class AiSettingsStore
{
    public const string Prefix = "ai.";

    /// <summary>At most this many models in one chain: past three the wait on a bad day is already long.</summary>
    public const int MaxChainLength = 5;

    /// <summary>An NVIDIA id (<c>vendor/model</c>) or a Bedrock one (<c>bedrock:anthropic.claude-…</c>, <c>bedrock-oss:moonshotai.kimi-…</c>).</summary>
    private static readonly Regex ModelIdShape = new(
        @"^([A-Za-z0-9][A-Za-z0-9._-]*/[A-Za-z0-9][A-Za-z0-9._:-]*|bedrock(-oss)?:[a-z0-9][a-z0-9._:-]*)$", RegexOptions.CultureInvariant);

    private readonly TradingDbContext _db;
    private readonly TimeProvider _time;
    private readonly IOptionsMonitor<AiSettings>? _settings;

    public AiSettingsStore(TradingDbContext db, TimeProvider? time = null, IOptionsMonitor<AiSettings>? settings = null)
    {
        _db = db;
        _time = time ?? TimeProvider.System;
        _settings = settings;
    }

    public static string TierChainKey(string tier) => $"{Prefix}tier.{tier}.chain";

    public static string AgentEnabledKey(string agent) => $"{Prefix}agent.{agent}.enabled";

    public static string AgentChainKey(string agent) => $"{Prefix}agent.{agent}.chain";

    public async Task<AiState> LoadAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _db.SystemSettings.AsNoTracking()
            .Where(s => s.Key.StartsWith(Prefix))
            .ToDictionaryAsync(s => s.Key, cancellationToken);

        var settings = _settings?.CurrentValue;
        bool bedrock = settings?.Bedrock.Enabled ?? false;
        bool bedrockOpen = settings?.BedrockOpen.Enabled ?? false;
        rows.TryGetValue(AiEmbedder.ActiveModelKey, out var embeddingRow);

        var tiers = AiCatalog.Tiers.Select(def =>
        {
            if (!def.Chat)
            {
                // Not a chain anyone sets: the model the docs and memories are embedded with now.
                string model = settings is null ? AiCatalog.EmbeddingModel : AiEmbedder.Resolve(embeddingRow?.Value, settings);
                return new AiTierState(def, [model], false, null, null);
            }

            rows.TryGetValue(TierChainKey(def.Key), out var row);
            var chain = Effective(ReadChain(row?.Value), bedrock);
            var standard = def.DefaultFor(bedrock, bedrockOpen);
            return chain is null
                ? new AiTierState(def, standard, false, null, null) { DefaultChain = standard }
                : new AiTierState(def, chain, true, row!.UpdatedUtc, row.UpdatedBy) { DefaultChain = standard };
        }).ToList();

        var agents = AiCatalog.Agents.Select(def =>
        {
            rows.TryGetValue(AgentEnabledKey(def.Key), out var enabledRow);
            rows.TryGetValue(AgentChainKey(def.Key), out var chainRow);

            // A planned agent is off whatever a row says: it has no code to run.
            // Without a readable row, a built agent is as its catalog entry starts.
            bool enabled = def.Built && (enabledRow is not null && bool.TryParse(enabledRow.Value, out bool on) ? on : def.StartsOn);
            var own = Effective(ReadChain(chainRow?.Value), bedrock);
            var tierChain = tiers.FirstOrDefault(t => t.Def.Key == def.Tier)?.Chain ?? [];

            var newest = new[] { enabledRow, chainRow }.Where(r => r is not null).OrderByDescending(r => r!.UpdatedUtc).FirstOrDefault();
            return new AiAgentState(def, enabled, own ?? tierChain, own is not null, newest?.UpdatedUtc, newest?.UpdatedBy, newest?.Reason);
        }).ToList();

        return new AiState(tiers, agents);
    }

    /// <summary>Sets a tier's chain, or goes back to the default when <paramref name="chain"/> is null or empty.</summary>
    public Task SetTierChainAsync(string tier, IReadOnlyList<string>? chain, string by, string? reason, CancellationToken cancellationToken = default)
        => chain is null || chain.Count == 0
            ? RemoveAsync(TierChainKey(tier), cancellationToken)
            : UpsertAsync(TierChainKey(tier), JsonSerializer.Serialize(chain), by, reason, cancellationToken);

    public Task SetAgentEnabledAsync(string agent, bool enabled, string by, string? reason, CancellationToken cancellationToken = default)
        => UpsertAsync(AgentEnabledKey(agent), enabled ? "true" : "false", by, reason, cancellationToken);

    /// <summary>Gives an agent its own chain, or back to its tier's when <paramref name="chain"/> is null or empty.</summary>
    public Task SetAgentChainAsync(string agent, IReadOnlyList<string>? chain, string by, string? reason, CancellationToken cancellationToken = default)
        => chain is null || chain.Count == 0
            ? RemoveAsync(AgentChainKey(agent), cancellationToken)
            : UpsertAsync(AgentChainKey(agent), JsonSerializer.Serialize(chain), by, reason, cancellationToken);

    /// <summary>
    /// Why a chain cannot be used, or null when it can: one to
    /// <see cref="MaxChainLength"/> distinct model ids, each one the desk knows
    /// (<paramref name="known"/>: the provider's list, or the default chains
    /// when that list is out of reach), and no embedding model in a chat chain.
    /// </summary>
    /// <param name="bedrockOnly">Bedrock is on: every chain is Claude, so an NVIDIA model cannot be set.</param>
    public static string? ChainProblem(IReadOnlyList<string>? chain, IReadOnlySet<string> known, bool chat = true, bool bedrockOnly = false)
    {
        if (chain is null || chain.Count == 0) return "A chain needs at least one model.";
        if (chain.Count > MaxChainLength) return $"A chain holds at most {MaxChainLength} models.";

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in chain)
        {
            if (string.IsNullOrWhiteSpace(id) || !ModelIdShape.IsMatch(id))
            {
                return $"\"{id}\" is not a model id (vendor/model, or bedrock: or bedrock-oss: and a Bedrock model id).";
            }

            if (!seen.Add(id)) return $"{id} is in the chain twice.";
            if (!known.Contains(id)) return $"{id} is not in the provider's model list.";
            if (chat && AiCatalog.IsEmbeddingModel(id)) return $"{id} makes vectors, not answers: it cannot answer a question.";
            if (bedrockOnly && !AiCatalog.IsBedrock(id))
            {
                string whose = AiCatalog.IsBedrockOpen(id) ? "an open model, not Claude" : "NVIDIA's";
                return $"Amazon Bedrock is on, so every chain is Claude on Bedrock: {id} is {whose}.";
            }
        }

        return null;
    }

    /// <summary>
    /// A stored chain as it is walked: while Bedrock is on, only its Bedrock models (null when it names none, so the
    /// tier's own chain is used); otherwise as stored.
    /// </summary>
    private static IReadOnlyList<string>? Effective(IReadOnlyList<string>? stored, bool bedrock)
    {
        if (stored is null || !bedrock) return stored;
        var claude = stored.Where(AiCatalog.IsBedrock).ToList();
        return claude.Count > 0 ? claude : null;
    }

    /// <summary>A stored chain, or null when there is none or it cannot be read.</summary>
    private static IReadOnlyList<string>? ReadChain(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var chain = JsonSerializer.Deserialize<List<string>>(json);
            return chain is { Count: > 0 } && chain.All(m => !string.IsNullOrWhiteSpace(m)) ? chain : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task UpsertAsync(string key, string value, string by, string? reason, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var row = await _db.SystemSettings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (row is null)
        {
            row = new SystemSetting { Key = key, CreatedUtc = now };
            _db.SystemSettings.Add(row);
        }

        row.Value = value;
        row.UpdatedBy = by;
        row.Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        row.UpdatedUtc = now;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        var row = await _db.SystemSettings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (row is null) return;
        _db.SystemSettings.Remove(row);
        await _db.SaveChangesAsync(cancellationToken);
    }
}
