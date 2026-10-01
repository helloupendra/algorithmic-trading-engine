using System.Text.Json.Nodes;
using AlgoTrading.Api.Services.AiSearch;
using AlgoTrading.Api.Services.AiTools;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static AlgoTrading.UnitTests.AiTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The Assistant's search over the desk's docs: how a doc is cut into
/// passages, how the index keeps in step with the files, and what a search
/// returns.
/// </summary>
public sealed class AiSearchTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ai-docs-").FullName;
    private readonly string _dbName = Guid.NewGuid().ToString("N");

    /// <summary>The kit on a named in-memory database, so each DI scope can open its own context on it.</summary>
    private Services Kit(AiSettings? settings = null) => Build(settings, NewDb(_dbName));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // ---------- cutting a doc ----------

    [Fact]
    public void A_doc_is_cut_at_its_sections_each_passage_carrying_its_heading_trail()
    {
        var passages = AiDocChunker.Chunk("modules/risk.md", """
            # Risk
            The guard.
            ## Overall stop
            Measured per day, net of charges.
            ### Scope
            A run can ask for the whole replay.
            ## Kill switch
            Stops every run.
            """);

        Assert.Equal(new[] { "Risk", "Risk › Overall stop", "Risk › Overall stop › Scope", "Risk › Kill switch" }, passages.Select(p => p.Heading));
        Assert.Equal("Measured per day, net of charges.", passages[1].Text);
        Assert.Equal(new[] { 0, 1, 2, 3 }, passages.Select(p => p.Ordinal));
    }

    [Fact]
    public void Comments_are_left_out_and_a_hash_line_inside_code_is_not_a_heading()
    {
        var passages = AiDocChunker.Chunk("strategies/X.md", """
            # X
            <!-- verification SQL: select * from secrets -->
            Before the code.
            ```bash
            # not a heading
            echo hi
            ```
            After the code.
            """);

        var only = Assert.Single(passages);
        Assert.DoesNotContain("verification SQL", only.Text);
        Assert.Contains("# not a heading", only.Text);
        Assert.Equal("X", only.Heading);
    }

    [Fact]
    public void A_long_section_is_split_under_the_passage_limit_and_hashes_are_stable()
    {
        string paragraph = string.Join(" ", Enumerable.Repeat("The desk records every tick and every order.", 30));
        string doc = "# Long\n" + string.Join("\n\n", Enumerable.Repeat(paragraph, 4));

        var first = AiDocChunker.Chunk("a.md", doc);
        var again = AiDocChunker.Chunk("a.md", doc);

        Assert.True(first.Count > 1);
        Assert.All(first, p => Assert.True(p.Text.Length <= AiDocChunker.MaxChars, $"{p.Text.Length} characters"));
        Assert.Equal(first.Select(p => p.Hash), again.Select(p => p.Hash));
    }

    // ---------- keeping the index in step ----------

    [Fact]
    public async Task Only_new_or_changed_passages_are_embedded_and_a_deleted_doc_s_passages_go()
    {
        var ai = Kit();
        Write("modules/risk.md", "# Risk\nThe overall stop is measured per day.\n## Kill switch\nStops every run.");
        Write("modules/chain.md", "# Chain\nWalls sit on their own side of spot.");
        var indexer = Indexer(ai, out _);

        var first = await indexer.IndexAsync(_root, CancellationToken.None);
        int embedsAfterFirst = ai.Provider.Requests.Count;
        var unchanged = await indexer.IndexAsync(_root, CancellationToken.None);
        Write("modules/risk.md", "# Risk\nThe overall stop is measured per day, net of charges.\n## Kill switch\nStops every run.");
        File.Delete(Path.Combine(_root, "modules", "chain.md"));
        var changed = await indexer.IndexAsync(_root, CancellationToken.None);

        Assert.Equal((3, 0), first);
        Assert.Equal((0, 0), unchanged);
        Assert.Equal(1, embedsAfterFirst);
        Assert.Equal((1, 2), changed);
        Assert.Equal(new[] { "modules/risk.md" }, await ai.Db.AiDocChunks.Select(c => c.Path).Distinct().ToListAsync());
        Assert.Equal("passage", JsonNode.Parse(ai.Provider.Requests[0].Body)!["input_type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Each_indexing_run_that_did_something_is_one_row_on_the_calls_tab()
    {
        var ai = Kit();
        Write("a.md", "# A\nOne passage.");

        await Indexer(ai, out _).IndexAsync(_root, CancellationToken.None);

        var call = await ai.Db.AiCalls.SingleAsync();
        Assert.Equal((AiCatalog.DocIndex, "index", AiCallOutcome.Ok, AiCatalog.EmbeddingModel), (call.AgentKey, call.Source, call.Outcome, call.Model));
        Assert.Equal("Embedded 1 passages of 1 docs; removed 0.", call.Summary);
    }

    [Fact]
    public async Task A_provider_failure_is_recorded_and_the_passages_are_tried_again_next_time()
    {
        var ai = Kit();
        Write("a.md", "# A\nOne passage.");
        ai.Provider.EmbeddingsFail = true;
        var indexer = Indexer(ai, out _);

        var failed = await indexer.IndexAsync(_root, CancellationToken.None);
        ai.Provider.EmbeddingsFail = false;
        var retried = await indexer.IndexAsync(_root, CancellationToken.None);

        Assert.Equal((0, 0), failed);
        Assert.Equal((1, 0), retried);
        Assert.Contains(await ai.Db.AiCalls.ToListAsync(), c => c.Outcome == AiCallOutcome.Failed && c.Error.Contains("http 503"));
    }

    [Fact]
    public async Task Without_a_key_or_with_search_off_nothing_is_sent()
    {
        var noKey = Kit(Settings(s => s.ApiKey = ""));
        var off = Kit(Settings(s => s.DocSearchEnabled = false));
        Write("a.md", "# A\nOne passage.");

        Assert.Equal((0, 0), await Indexer(noKey, out _).IndexAsync(_root, CancellationToken.None));
        Assert.Equal((0, 0), await Indexer(off, out _).IndexAsync(_root, CancellationToken.None));
        Assert.Empty(noKey.Provider.Requests);
        Assert.Empty(off.Provider.Requests);
    }

    // ---------- searching ----------

    [Fact]
    public async Task A_search_returns_the_closest_passages_best_first()
    {
        var ai = Kit();
        Write("modules/risk.md", "# Risk\n## Overall stop\noverall stop loss measured per day net of charges");
        Write("modules/chain.md", "# Chain\n## Walls\noption chain walls put wall call wall");
        var indexer = Indexer(ai, out var index);
        await indexer.IndexAsync(_root, CancellationToken.None);

        var hits = await index.SearchAsync("how is the overall stop loss measured", 2, CancellationToken.None);

        Assert.Equal("modules/risk.md", hits[0].Path);
        Assert.Equal("Risk › Overall stop", hits[0].Heading);
        Assert.True(hits[0].Score > hits[1].Score);
        Assert.Equal("query", JsonNode.Parse(ai.Provider.Requests[^1].Body)!["input_type"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_tool_says_so_when_the_docs_are_not_indexed_yet()
    {
        var ai = Kit();
        Indexer(ai, out var index);

        var output = await new SearchDocsTool(index).RunAsync(AiToolArgs.Parse("""{"query":"kill switch"}"""), CancellationToken.None);

        Assert.Equal(0, output.Rows);
        Assert.Contains("not indexed yet", System.Text.Json.JsonSerializer.Serialize(output.Data));
    }

    private void Write(string relative, string text)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private AiDocIndexer Indexer(Services ai, out AiDocIndex index)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => NewDb(_dbName));
        services.AddSingleton(ai.Client);
        var scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        index = new AiDocIndex(scopes, ai.Client);
        return new AiDocIndexer(scopes, index, RecapClockTests.Inert<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>.Create(), ai.Options,
            NullLogger<AiDocIndexer>.Instance);
    }
}
