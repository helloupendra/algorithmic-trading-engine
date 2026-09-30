using System.Diagnostics;
using System.Text.Json;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services.AiSearch;

/// <summary>
/// Keeps <c>ai_doc_chunks</c> in step with the repo's <c>docs/</c>: at start
/// (after a deploy) and every six hours it cuts each file into passages and
/// embeds only the passages that are new or changed.
/// </summary>
/// <remarks>
/// The docs are the public ones (openfno.com/docs), so sending them to the
/// provider shares nothing private. Each run that embedded or removed
/// anything leaves one row on the Calls tab (agent <c>doc-index</c>) with what
/// it did, the tokens and the time. A failure keeps what was embedded so far;
/// the rest is tried at the next run.
/// </remarks>
public sealed class AiDocIndexer(
    IServiceScopeFactory scopes,
    AiDocIndex index,
    IWebHostEnvironment env,
    IOptionsMonitor<AiSettings> settings,
    ILogger<AiDocIndexer> logger,
    TimeProvider? time = null) : BackgroundService
{
    public const int BatchSize = 48;
    public static readonly TimeSpan Every = TimeSpan.FromHours(6);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), _time, stoppingToken);
            using var timer = new PeriodicTimer(Every, _time);
            do
            {
                try
                {
                    await IndexAsync(DocsRoot(), stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Indexing the docs for search failed; it tries again in {Hours} hours", Every.TotalHours);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The API is stopping.
        }
    }

    public string DocsRoot() => Path.GetFullPath(Path.Combine(env.ContentRootPath, "..", "..", "docs"));

    /// <summary>One pass over <paramref name="root"/>. Public for tests. Answers how many passages it embedded and removed.</summary>
    public async Task<(int Embedded, int Removed)> IndexAsync(string root, CancellationToken cancellationToken)
    {
        var s = settings.CurrentValue;
        if (!s.DocSearchEnabled || !s.KeyConfigured || !Directory.Exists(root)) return (0, 0);

        var passages = Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .SelectMany(f => AiDocChunker.Chunk(Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllText(f)))
            .ToList();

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var client = scope.ServiceProvider.GetRequiredService<NvidiaChatClient>();

        var existing = await db.AiDocChunks.Select(c => new { c.Id, c.Path, c.Ordinal, c.Hash, c.Model }).ToListAsync(cancellationToken);
        string Key(string path, int ordinal, string hash) => $"{path}\n{ordinal}\n{hash}";
        var wanted = passages.ToDictionary(p => Key(p.Path, p.Ordinal, p.Hash));
        var kept = existing.Where(e => e.Model == AiCatalog.EmbeddingModel && wanted.ContainsKey(Key(e.Path, e.Ordinal, e.Hash)))
            .Select(e => Key(e.Path, e.Ordinal, e.Hash)).ToHashSet();
        var removedIds = existing.Where(e => !kept.Contains(Key(e.Path, e.Ordinal, e.Hash))).Select(e => e.Id).ToList();
        var toEmbed = passages.Where(p => !kept.Contains(Key(p.Path, p.Ordinal, p.Hash))).ToList();
        if (toEmbed.Count == 0 && removedIds.Count == 0) return (0, 0);

        if (removedIds.Count > 0)
        {
            db.AiDocChunks.RemoveRange(await db.AiDocChunks.Where(c => removedIds.Contains(c.Id)).ToListAsync(cancellationToken));
            await db.SaveChangesAsync(cancellationToken);
        }

        var clock = Stopwatch.StartNew();
        int embedded = 0, tokens = 0;
        string? error = null;
        foreach (var batch in toEmbed.Chunk(BatchSize))
        {
            try
            {
                var (vectors, used) = await client.EmbedAsync(AiCatalog.EmbeddingModel, batch.Select(p => $"{p.Heading}\n\n{p.Text}").ToList(),
                    "passage", cancellationToken);
                var now = _time.GetUtcNow().UtcDateTime;
                db.AiDocChunks.AddRange(batch.Select((p, i) => new AiDocChunk
                {
                    Path = p.Path, Heading = Cut(p.Heading, 400), Ordinal = p.Ordinal, Text = p.Text, Hash = p.Hash,
                    Embedding = vectors[i], Model = AiCatalog.EmbeddingModel, IndexedUtc = now,
                }));
                await db.SaveChangesAsync(cancellationToken);
                embedded += batch.Length;
                tokens += used ?? 0;
            }
            catch (AiAttemptFailedException ex)
            {
                error = $"stopped after {embedded} of {toEmbed.Count} passages: {ex.Outcome}";
                break;
            }
        }

        index.Invalidate();
        int files = passages.Select(p => p.Path).Distinct().Count();
        string summary = $"Embedded {embedded} passages of {files} docs; removed {removedIds.Count}.";
        db.AiCalls.Add(new AiCall
        {
            CreatedUtc = _time.GetUtcNow().UtcDateTime - clock.Elapsed,
            CompletedUtc = _time.GetUtcNow().UtcDateTime,
            AgentKey = AiCatalog.DocIndex,
            Tier = "embed",
            Source = "index",
            RequestedBy = AiCatalog.DocIndex,
            ChainJson = JsonSerializer.Serialize(new[] { AiCatalog.EmbeddingModel }),
            Model = embedded > 0 ? AiCatalog.EmbeddingModel : string.Empty,
            Outcome = error is null ? AiCallOutcome.Ok : AiCallOutcome.Failed,
            Error = error ?? string.Empty,
            Seconds = Math.Round(clock.Elapsed.TotalSeconds, 2),
            TotalTokens = tokens,
            PromptTokens = tokens,
            Summary = summary,
            Answer = summary,
            AttemptsJson = JsonSerializer.Serialize(new[] { new { model = AiCatalog.EmbeddingModel, outcome = error is null ? "ok" : error, seconds = Math.Round(clock.Elapsed.TotalSeconds, 2), httpStatus = (int?)null, round = 1 } }),
        });
        await db.SaveChangesAsync(CancellationToken.None);
        logger.LogInformation("Docs search index: {Summary}{Error}", summary, error is null ? string.Empty : $" ({error})");
        return (embedded, removedIds.Count);
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
