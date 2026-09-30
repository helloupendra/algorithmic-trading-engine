using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoTrading.Infrastructure.Ai;

/// <summary>A passage the search found, with its cosine similarity to the query.</summary>
public sealed record AiDocHit(string Path, string Heading, string Text, double Score);

/// <summary>What the search index holds.</summary>
public sealed record AiDocIndexStatus(int Files, int Passages, DateTime? IndexedUtc, string Model);

/// <summary>
/// The docs' passages and their vectors, in memory, and the search over them:
/// the query is embedded as a query and compared with every passage.
/// </summary>
/// <remarks>
/// The corpus is the repo's <c>docs/</c> (about 2,000 passages of 2,048
/// floats, some 16 MB), so a plain cosine over all of it is milliseconds and
/// needs no vector index. Loaded from <c>ai_doc_chunks</c> on first use and
/// again after the indexer changes it.
/// </remarks>
public sealed class AiDocIndex(IServiceScopeFactory scopes, NvidiaChatClient client)
{
    private sealed record Entry(string Path, string Heading, string Text, float[] Unit);

    private sealed record Loaded(IReadOnlyList<Entry> Entries, DateTime? IndexedUtc);

    private readonly SemaphoreSlim _load = new(1, 1);
    private volatile Loaded? _loaded;

    /// <summary>After the indexer changed the table: the next search reloads.</summary>
    public void Invalidate() => _loaded = null;

    public async Task<AiDocIndexStatus> StatusAsync(CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(cancellationToken);
        return new AiDocIndexStatus(loaded.Entries.Select(e => e.Path).Distinct().Count(), loaded.Entries.Count, loaded.IndexedUtc,
            AiCatalog.EmbeddingModel);
    }

    /// <summary>The <paramref name="limit"/> passages closest to <paramref name="query"/>, best first; none when the index is empty.</summary>
    public async Task<IReadOnlyList<AiDocHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(cancellationToken);
        if (loaded.Entries.Count == 0) return [];

        var (vectors, _) = await client.EmbedAsync(AiCatalog.EmbeddingModel, [query], "query", cancellationToken);
        var q = Unit(vectors[0]);
        return loaded.Entries
            .Select(e => (e, score: Dot(q, e.Unit)))
            .OrderByDescending(x => x.score)
            .Take(limit)
            .Select(x => new AiDocHit(x.e.Path, x.e.Heading, x.e.Text, Math.Round(x.score, 4)))
            .ToList();
    }

    private async Task<Loaded> LoadAsync(CancellationToken cancellationToken)
    {
        if (_loaded is { } ready) return ready;
        await _load.WaitAsync(cancellationToken);
        try
        {
            if (_loaded is { } again) return again;
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            var rows = await db.AiDocChunks.AsNoTracking()
                .Where(c => c.Model == AiCatalog.EmbeddingModel)
                .OrderBy(c => c.Path).ThenBy(c => c.Ordinal)
                .Select(c => new { c.Path, c.Heading, c.Text, c.Embedding, c.IndexedUtc })
                .ToListAsync(cancellationToken);
            var loaded = new Loaded(
                rows.Where(r => r.Embedding.Length > 0).Select(r => new Entry(r.Path, r.Heading, r.Text, Unit(r.Embedding))).ToList(),
                rows.Count == 0 ? null : DateTime.SpecifyKind(rows.Max(r => r.IndexedUtc), DateTimeKind.Utc));
            _loaded = loaded;
            return loaded;
        }
        finally
        {
            _load.Release();
        }
    }

    public static float[] Unit(float[] v)
    {
        double norm = Math.Sqrt(v.Sum(x => (double)x * x));
        return norm == 0 ? v : v.Select(x => (float)(x / norm)).ToArray();
    }

    private static double Dot(float[] a, float[] b)
    {
        double sum = 0;
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) sum += a[i] * b[i];
        return sum;
    }
}
