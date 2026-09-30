using System.Text.Json;
using System.Text.RegularExpressions;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Infrastructure.Ai;

/// <summary>A tier as it stands: its chain now, and whether the owner changed it.</summary>
public sealed record AiTierState(AiTierDef Def, IReadOnlyList<string> Chain, bool Overridden, DateTime? UpdatedUtc, string? UpdatedBy);

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
/// A missing row means the default: the tier's chain from <see cref="AiCatalog"/>,
/// an agent on when it is built and walking its tier's chain. A row whose
/// value cannot be read (hand-edited, say) also counts as the default rather
/// than failing every call; the console shows the chain actually used.
/// </remarks>
public sealed class AiSettingsStore
{
    public const string Prefix = "ai.";

    /// <summary>At most this many models in one chain: past three the wait on a bad day is already long.</summary>
    public const int MaxChainLength = 5;

    private static readonly Regex ModelIdShape = new(@"^[A-Za-z0-9][A-Za-z0-9._-]*/[A-Za-z0-9][A-Za-z0-9._:-]*$", RegexOptions.CultureInvariant);

    private readonly TradingDbContext _db;
    private readonly TimeProvider _time;

    public AiSettingsStore(TradingDbContext db, TimeProvider? time = null)
    {
        _db = db;
        _time = time ?? TimeProvider.System;
    }

    public static string TierChainKey(string tier) => $"{Prefix}tier.{tier}.chain";

    public static string AgentEnabledKey(string agent) => $"{Prefix}agent.{agent}.enabled";

    public static string AgentChainKey(string agent) => $"{Prefix}agent.{agent}.chain";

    public async Task<AiState> LoadAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _db.SystemSettings.AsNoTracking()
            .Where(s => s.Key.StartsWith(Prefix))
            .ToDictionaryAsync(s => s.Key, cancellationToken);

        var tiers = AiCatalog.Tiers.Select(def =>
        {
            rows.TryGetValue(TierChainKey(def.Key), out var row);
            var chain = ReadChain(row?.Value);
            return chain is null
                ? new AiTierState(def, def.DefaultChain, false, null, null)
                : new AiTierState(def, chain, true, row!.UpdatedUtc, row.UpdatedBy);
        }).ToList();

        var agents = AiCatalog.Agents.Select(def =>
        {
            rows.TryGetValue(AgentEnabledKey(def.Key), out var enabledRow);
            rows.TryGetValue(AgentChainKey(def.Key), out var chainRow);

            // A planned agent is off whatever a row says: it has no code to run.
            bool enabled = def.Built && (enabledRow is null || !bool.TryParse(enabledRow.Value, out bool on) || on);
            var own = ReadChain(chainRow?.Value);
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
    public static string? ChainProblem(IReadOnlyList<string>? chain, IReadOnlySet<string> known, bool chat = true)
    {
        if (chain is null || chain.Count == 0) return "A chain needs at least one model.";
        if (chain.Count > MaxChainLength) return $"A chain holds at most {MaxChainLength} models.";

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in chain)
        {
            if (string.IsNullOrWhiteSpace(id) || !ModelIdShape.IsMatch(id)) return $"\"{id}\" is not a model id (vendor/model).";
            if (!seen.Add(id)) return $"{id} is in the chain twice.";
            if (!known.Contains(id)) return $"{id} is not in the provider's model list.";
            if (chat && AiCatalog.IsEmbeddingModel(id)) return $"{id} makes vectors, not answers: it cannot answer a question.";
        }

        return null;
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
