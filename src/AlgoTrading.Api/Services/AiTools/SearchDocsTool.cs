using System.Diagnostics;
using System.Text.Json.Nodes;
using AlgoTrading.Infrastructure.Ai;

namespace AlgoTrading.Api.Services.AiTools;

/// <summary><c>search_docs</c>: the passages of the desk's own docs closest to a question.</summary>
public sealed class SearchDocsTool(AiDocIndex index) : IAiTool
{
    private const int MaxText = 900;

    public string Name => AiToolNames.SearchDocs;

    public string Description =>
        "Searches the desk's own written docs (how each module works, its rules, settings and traps, and every " +
        "strategy's spec) and returns the closest passages with their file and section. Use it for how-does-X-work " +
        "and why-does-the-desk-do-Y questions; quote the file and section you rely on.";

    public JsonObject Parameters => AiToolSchema.Object(
        ("query", AiToolSchema.Text("What to look for, in plain words."), true),
        ("limit", AiToolSchema.Integer("Passages to return, 1 to 8. Default 5."), false));

    public async Task<AiToolOutput> RunAsync(AiToolArgs args, CancellationToken cancellationToken)
    {
        string query = args.String("query", 300) ?? throw new AiToolArgumentException("query is required.");
        int limit = args.Int("limit", 1, 8) ?? 5;

        var clock = Stopwatch.StartNew();
        var status = await index.StatusAsync(cancellationToken);
        if (status.Passages == 0)
        {
            return new AiToolOutput(new { query, found = 0, note = "The docs are not indexed yet; they are indexed a few minutes after the API starts." },
                null, 0, "docs not indexed yet");
        }

        IReadOnlyList<AiDocHit> hits;
        try
        {
            hits = await index.SearchAsync(query, limit, cancellationToken);
        }
        catch (AiAttemptFailedException ex)
        {
            throw new AiToolArgumentException($"The search could not embed the query ({ex.Outcome}); try again.");
        }

        var data = new
        {
            query,
            passages = hits.Select(h => new
            {
                file = $"docs/{h.Path}",
                section = h.Heading,
                score = h.Score,
                text = h.Text.Length <= MaxText ? h.Text : h.Text[..MaxText] + "…",
            }).ToList(),
        };

        return new AiToolOutput(data, status.IndexedUtc, hits.Count,
            $"{hits.Count} passages for \"{(query.Length <= 40 ? query : query[..40] + "…")}\" in {clock.Elapsed.TotalSeconds:0.0} s");
    }
}
