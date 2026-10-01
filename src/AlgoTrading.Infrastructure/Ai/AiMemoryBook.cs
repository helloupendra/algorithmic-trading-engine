using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Ai;

/// <summary>A memory put into a call's system prompt, with its similarity to the question when the memories had to be ranked.</summary>
public sealed record AiRecalled(long Id, string Kind, string Text, string Context, double? Score);

/// <summary>
/// Reads an agent's memories into its system prompt: all of them while they
/// fit, the ones closest to the question once they do not.
/// </summary>
/// <remarks>
/// <para>
/// Only <see cref="AiMemoryStatus.Active"/> memories are read, and only for an
/// agent that has memory (<see cref="AiSettings.MemoryAgents"/>). While they
/// fit <see cref="AiSettings.MemoryBudgetChars"/> and
/// <see cref="AiSettings.MemoryMaxItems"/>, every one is sent and nothing is
/// embedded. Past that the question is embedded and the memories are ranked
/// by cosine similarity, and those below <see cref="AiSettings.MemoryMinScore"/>
/// stay out. When the embedding fails, the newest memories are sent instead:
/// memory must never stop an answer.
/// </para>
/// <para>
/// The memories are told to the model as guidance, not data: a number still
/// comes from the tools, and a note that disagrees with a tool is out of date.
/// Every embedding call is a row on the Calls tab (agent <c>memory</c>).
/// </para>
/// </remarks>
public sealed class AiMemoryBook(
    TradingDbContext db,
    NvidiaChatClient client,
    IOptionsMonitor<AiSettings> settings,
    ILogger? logger = null,
    TimeProvider? time = null)
{
    /// <summary>The longest memory text; a longer one is refused, not cut.</summary>
    public const int MaxText = 600;

    /// <summary>The longest question kept as a memory's context.</summary>
    public const int MaxContext = 400;

    /// <summary>What one memory costs in the prompt beyond its text: its id, kind and quotes.</summary>
    private const int Overhead = 24;

    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public const string Preamble =
        "Memory: notes kept for you from earlier work. The owner wrote or approved each one. They are guidance, not " +
        "data: still read the tools for every number, and if a note disagrees with what a tool shows now, trust the " +
        "tool and say the note may be out of date.";

    /// <summary>
    /// The active memories <paramref name="agentKey"/> carries into this call,
    /// each counted as used. The counts, and any embedding call's audit row,
    /// are saved with the caller's next <c>SaveChanges</c>.
    /// </summary>
    /// <param name="trial">Memories read on this call whatever their status: a lesson tested before it is used.</param>
    public async Task<IReadOnlyList<AiRecalled>> RecallAsync(
        string agentKey, string question, CancellationToken cancellationToken, IReadOnlyList<long>? trial = null)
    {
        var s = settings.CurrentValue;
        if (!s.HasMemory(agentKey)) return [];

        var tried = trial ?? [];
        var active = await db.AiMemories
            .Where(m => m.AgentKey == agentKey && (m.Status == AiMemoryStatus.Active || tried.Contains(m.Id)))
            .ToListAsync(cancellationToken);
        if (active.Count == 0) return [];

        var ordered = active
            .OrderBy(m => KindOrder(m.Kind))
            .ThenByDescending(m => m.ActivatedUtc ?? m.CreatedUtc)
            .ThenByDescending(m => m.Id)
            .ToList();

        IReadOnlyList<(AiMemory Memory, double? Score)> ranked =
            active.Sum(Size) <= s.MemoryBudgetChars && active.Count <= s.MemoryMaxItems
                ? ordered.Select(m => (m, (double?)null)).ToList()
                : await RankAsync(agentKey, ordered, question, s, cancellationToken);

        var picked = new List<(AiMemory Memory, double? Score)>();
        int chars = 0;
        foreach (var (memory, score) in ranked)
        {
            if (picked.Count >= s.MemoryMaxItems) break;
            int size = Size(memory);
            if (chars + size > s.MemoryBudgetChars) continue;
            picked.Add((memory, score));
            chars += size;
        }

        // A counter, not a ledger: two answers at the same moment may count one use.
        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var (memory, _) in picked)
        {
            memory.Uses++;
            memory.LastUsedUtc = now;
        }

        return picked.Select(p => new AiRecalled(p.Memory.Id, p.Memory.Kind, p.Memory.Text, p.Memory.Context,
            p.Score is double sc ? Math.Round(sc, 4) : null)).ToList();
    }

    /// <summary>The system prompt's memory section for <paramref name="recalled"/>.</summary>
    public static string Block(IReadOnlyList<AiRecalled> recalled)
    {
        var text = new StringBuilder(Preamble);
        foreach (var r in recalled)
        {
            text.Append("\n[M").Append(r.Id).Append("] ").Append(r.Kind);
            if (r.Context.Length > 0) text.Append(" (asked: \"").Append(r.Context).Append("\")");
            text.Append(": ").Append(r.Text);
        }

        return text.ToString();
    }

    /// <summary>
    /// Embeds <paramref name="memory"/>'s text as it stands, with an audit row.
    /// A failure leaves it without a vector; it is embedded again when it is
    /// first ranked. Saved with the caller's next <c>SaveChanges</c>.
    /// </summary>
    public async Task EmbedAsync(AiMemory memory, CancellationToken cancellationToken)
    {
        if (!settings.CurrentValue.KeyConfigured) return;
        var clock = Stopwatch.StartNew();
        try
        {
            var (vectors, tokens) = await client.EmbedAsync(AiCatalog.EmbeddingModel, [EmbedText(memory)], "passage", cancellationToken);
            memory.Embedding = vectors[0];
            memory.EmbeddingModel = AiCatalog.EmbeddingModel;
            Audit(memory.AgentKey, $"Embedded memory M{(memory.Id > 0 ? memory.Id : 0)} for {memory.AgentKey}.", null, tokens, clock);
        }
        catch (AiAttemptFailedException ex)
        {
            memory.Embedding = [];
            memory.EmbeddingModel = string.Empty;
            Audit(memory.AgentKey, $"Could not embed a memory for {memory.AgentKey}.", ex.Outcome, null, clock);
            _logger.LogInformation("Agent memory: a memory could not be embedded ({Error}); it is embedded when it is first ranked", ex.Outcome);
        }
    }

    private async Task<IReadOnlyList<(AiMemory Memory, double? Score)>> RankAsync(
        string agentKey, List<AiMemory> ordered, string question, AiSettings s, CancellationToken cancellationToken)
    {
        if (!s.KeyConfigured || string.IsNullOrWhiteSpace(question)) return ordered.Select(m => (m, (double?)null)).ToList();

        var clock = Stopwatch.StartNew();
        var missing = ordered.Where(m => m.Embedding.Length == 0 || m.EmbeddingModel != AiCatalog.EmbeddingModel).ToList();
        int? tokens = 0;
        try
        {
            if (missing.Count > 0)
            {
                var (vectors, used) = await client.EmbedAsync(AiCatalog.EmbeddingModel, missing.Select(EmbedText).ToList(), "passage", cancellationToken);
                for (int i = 0; i < missing.Count; i++)
                {
                    missing[i].Embedding = vectors[i];
                    missing[i].EmbeddingModel = AiCatalog.EmbeddingModel;
                }

                tokens += used ?? 0;
            }

            var (query, queryTokens) = await client.EmbedAsync(AiCatalog.EmbeddingModel, [Cut(question, 2000)], "query", cancellationToken);
            tokens += queryTokens ?? 0;
            var q = AiDocIndex.Unit(query[0]);

            var ranked = ordered
                .Select(m => (Memory: m, Score: Dot(q, AiDocIndex.Unit(m.Embedding))))
                .Where(x => x.Score >= s.MemoryMinScore)
                .OrderByDescending(x => x.Score)
                .Select(x => (x.Memory, (double?)x.Score))
                .ToList();
            Audit(agentKey, $"Ranked {ordered.Count} memories for a {agentKey} question; {ranked.Count} close enough" +
                (missing.Count > 0 ? $"; embedded {missing.Count}." : "."), null, tokens, clock);
            return ranked;
        }
        catch (AiAttemptFailedException ex)
        {
            Audit(agentKey, $"Could not rank {ordered.Count} memories for a {agentKey} question; sent the newest.", ex.Outcome, null, clock);
            _logger.LogInformation("Agent memory: ranking failed ({Error}); the newest memories are sent", ex.Outcome);
            return ordered.Select(m => (m, (double?)null)).ToList();
        }
    }

    private void Audit(string agentKey, string summary, string? error, int? tokens, Stopwatch clock)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        double seconds = Math.Round(clock.Elapsed.TotalSeconds, 2);
        db.AiCalls.Add(new AiCall
        {
            CreatedUtc = now - clock.Elapsed,
            CompletedUtc = now,
            AgentKey = AiCatalog.Memory,
            Tier = "embed",
            Source = "memory",
            RequestedBy = agentKey,
            ChainJson = JsonSerializer.Serialize(new[] { AiCatalog.EmbeddingModel }),
            Model = error is null ? AiCatalog.EmbeddingModel : string.Empty,
            Outcome = error is null ? AiCallOutcome.Ok : AiCallOutcome.Failed,
            Error = error ?? string.Empty,
            Seconds = seconds,
            TotalTokens = tokens,
            PromptTokens = tokens,
            Summary = Cut(summary, 200),
            Answer = summary,
            AttemptsJson = JsonSerializer.Serialize(new[] { new { model = AiCatalog.EmbeddingModel, outcome = error ?? "ok", seconds, httpStatus = (int?)null, round = 1 } }),
        });
    }

    /// <summary>What a memory is embedded as: its text, and the question it came from.</summary>
    public static string EmbedText(AiMemory m) => m.Context.Length > 0 ? $"{m.Text}\n(asked: {m.Context})" : m.Text;

    private static int Size(AiMemory m) => m.Text.Length + m.Context.Length + Overhead;

    /// <summary>Corrections first: the owner said exactly what was wrong. Then notes, then lessons.</summary>
    private static int KindOrder(string kind) => kind switch
    {
        AiMemoryKind.Correction => 0,
        AiMemoryKind.Note => 1,
        _ => 2,
    };

    private static double Dot(float[] a, float[] b)
    {
        double sum = 0;
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) sum += a[i] * b[i];
        return sum;
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];
}
