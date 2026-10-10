using System.Diagnostics;
using System.Text.Json;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Ai;

/// <summary>How a re-embedding ended: every vector made and the desk switched, or nothing changed and why.</summary>
public sealed record AiReembedResult(bool Ok, string Model, int Memories, int Passages, string? Error, long CallId);

/// <summary>
/// The one-off move of search and memory to Bedrock's embedding model (Titan): every memory and every doc passage
/// is embedded again with it, and only when all of them are does the desk switch to it
/// (<see cref="AiEmbedder.ActiveModelKey"/>).
/// </summary>
/// <remarks>
/// <para>
/// All or nothing. The vectors are made first, in memory (the docs are about 1,300 passages, the memories a few
/// dozen); then they, their model's name and the switch are saved in one transaction. A failure part-way changes
/// nothing: search and memory carry on with the model they had, and the call's row says what failed.
/// </para>
/// <para>
/// It runs only while Bedrock is on, and one at a time. It is one row on the Calls tab (agent <c>reembed</c>), with
/// the counts, the tokens and the time.
/// </para>
/// </remarks>
public sealed class AiReembedder(
    TradingDbContext db,
    AiDocIndex index,
    IOptionsMonitor<AiSettings> settings,
    ILogger<AiReembedder>? logger = null,
    TimeProvider? time = null)
{
    /// <summary>Texts handed to the embedder at a time; nothing is saved between batches.</summary>
    public const int BatchSize = 64;

    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    private readonly ILogger _logger = logger ?? NullLogger<AiReembedder>.Instance;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Whether a re-embedding is running now, anywhere in this API.</summary>
    public static bool Running => OneAtATime.CurrentCount == 0;

    public async Task<AiReembedResult> RunAsync(string requestedBy, CancellationToken cancellationToken)
    {
        var s = settings.CurrentValue;
        string model = AiEmbedder.Target(s);
        if (!s.Bedrock.Enabled) return new AiReembedResult(false, model, 0, 0, "Amazon Bedrock is off (Ai:Bedrock:Enabled is false).", 0);
        if (!await OneAtATime.WaitAsync(0, cancellationToken)) return new AiReembedResult(false, model, 0, 0, "A re-embedding is already running.", 0);

        try
        {
            var clock = Stopwatch.StartNew();
            var memories = await db.AiMemories.OrderBy(m => m.Id).ToListAsync(cancellationToken);
            var passages = await db.AiDocChunks.OrderBy(c => c.Id).ToListAsync(cancellationToken);
            var texts = memories.Select(AiMemoryBook.EmbedText).Concat(passages.Select(p => $"{p.Heading}\n\n{p.Text}")).ToList();

            var vectors = new List<float[]>(texts.Count);
            int tokens = 0;
            string? error = null;
            foreach (var batch in texts.Chunk(BatchSize))
            {
                try
                {
                    var (made, used) = await index.Embedder.EmbedAsync(model, batch, "passage", cancellationToken);
                    vectors.AddRange(made);
                    tokens += used ?? 0;
                }
                catch (AiAttemptFailedException ex)
                {
                    error = $"stopped after {vectors.Count} of {texts.Count} texts: {ex.Outcome}; nothing was changed";
                    break;
                }
            }

            var now = _time.GetUtcNow().UtcDateTime;
            if (error is null)
            {
                for (int i = 0; i < memories.Count; i++)
                {
                    memories[i].Embedding = vectors[i];
                    memories[i].EmbeddingModel = model;
                }

                for (int i = 0; i < passages.Count; i++)
                {
                    passages[i].Embedding = vectors[memories.Count + i];
                    passages[i].Model = model;
                    passages[i].IndexedUtc = now;
                }

                var row = await db.SystemSettings.FirstOrDefaultAsync(x => x.Key == AiEmbedder.ActiveModelKey, cancellationToken);
                if (row is null)
                {
                    row = new SystemSetting { Key = AiEmbedder.ActiveModelKey, CreatedUtc = now };
                    db.SystemSettings.Add(row);
                }

                row.Value = model;
                row.UpdatedBy = requestedBy;
                row.Reason = $"Re-embedded {memories.Count} memories and {passages.Count} doc passages.";
                row.UpdatedUtc = now;
            }

            string summary = error is null
                ? $"Re-embedded {memories.Count} memories and {passages.Count} doc passages with {model}; search and memory now use it."
                : $"Re-embedding with {model} failed; search and memory keep the model they had.";
            var call = new AiCall
            {
                CreatedUtc = now - clock.Elapsed,
                CompletedUtc = now,
                AgentKey = AiCatalog.Reembed,
                Tier = "embed",
                Source = "console",
                RequestedBy = requestedBy,
                ChainJson = JsonSerializer.Serialize(new[] { model }),
                Model = error is null ? model : string.Empty,
                Outcome = error is null ? AiCallOutcome.Ok : AiCallOutcome.Failed,
                Error = error ?? string.Empty,
                Seconds = Math.Round(clock.Elapsed.TotalSeconds, 2),
                TotalTokens = tokens,
                PromptTokens = tokens,
                Summary = summary,
                Answer = summary,
                AttemptsJson = JsonSerializer.Serialize(new[]
                {
                    new { model, outcome = error ?? "ok", seconds = Math.Round(clock.Elapsed.TotalSeconds, 2), httpStatus = (int?)null, round = 1 },
                }),
            };
            db.AiCalls.Add(call);

            // The vectors, their model and the switch together, or none of them.
            await db.SaveChangesAsync(CancellationToken.None);
            index.Invalidate();
            _logger.LogInformation("AI re-embedding: {Summary}{Error}", summary, error is null ? string.Empty : $" ({error})");
            return new AiReembedResult(error is null, model, memories.Count, passages.Count, error, call.Id);
        }
        finally
        {
            OneAtATime.Release();
        }
    }
}
