using System.Text.RegularExpressions;
using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AlgoTrading.UnitTests;

/// <summary>Runs only once the Python news scorer exists; until then it is reported as skipped, with the reason.</summary>
public sealed class FactWhenNewsScorerExistsAttribute : FactAttribute
{
    public FactWhenNewsScorerExistsAttribute()
    {
        if (!File.Exists(NewsTablesContractTests.ScorerPath()))
        {
            Skip = $"{NewsTablesContractTests.ScorerFile} is not written yet (the Python news scorer); "
                   + "this check reads its SQL against news_items and corporate_announcements once it exists.";
        }
    }
}

/// <summary>
/// The scoring columns of <c>news_items</c> and <c>corporate_announcements</c>
/// as the Python scorer's own SQL sees them.
/// </summary>
/// <remarks>
/// <para>
/// The recorders insert the rows through EF; the scorer
/// (<c>analysis/news.py</c>, run as <c>python -m analysis news-score</c>)
/// fills <c>Sentiment</c>, <c>Importance</c>, <c>Symbols</c>, <c>Topics</c>,
/// <c>ScoredUtc</c> and <c>ScoreModel</c> with hand-written SQL. The in-memory
/// provider ignores table and column names, so a rename here would pass every
/// other test and show up first as a scorer that fails every ten minutes. As
/// for Sentinel's tables (<see cref="IncidentsTableContractTests"/>), the real
/// Npgsql model is built, without a connection, and checked.
/// </para>
/// <para>
/// The first group pins the contract the scorer was written against. The
/// second reads the scorer's source, so it waits for that file to exist and
/// is skipped, saying so, until it does.
/// </para>
/// </remarks>
public class NewsTablesContractTests
{
    public const string ScorerFile = "src/AlgoTrading.PythonEngine/analysis/news.py";

    private static readonly string[] Tables = ["news_items", "corporate_announcements"];

    /// <summary>The columns the scorer writes, and the only ones it may write.</summary>
    private static readonly string[] ScoringColumns =
    [
        nameof(IScoredText.Sentiment), nameof(IScoredText.Importance), nameof(IScoredText.Symbols),
        nameof(IScoredText.Topics), nameof(IScoredText.ScoredUtc), nameof(IScoredText.ScoreModel),
    ];

    [Theory]
    [InlineData("news_items")]
    [InlineData("corporate_announcements")]
    public void The_scoring_columns_have_the_contract_s_names_types_and_defaults(string table)
    {
        var entity = IncidentsTableContractTests.EntityFor(IncidentsTableContractTests.Model(), table);
        Assert.NotNull(entity);
        var store = StoreObjectIdentifier.Table(table);
        IProperty Column(string name) => entity!.GetProperties().Single(p => p.GetColumnName(store) == name);

        Assert.Equal("numeric(4,3)", Column("Sentiment").GetColumnType());
        Assert.True(Column("Sentiment").IsNullable);
        Assert.Equal("smallint", Column("Importance").GetColumnType());
        Assert.True(Column("Importance").IsNullable);
        Assert.Equal("timestamp with time zone", Column("ScoredUtc").GetColumnType());
        Assert.True(Column("ScoredUtc").IsNullable);

        // NOT NULL with an empty default, so an unscored row needs no value and the scorer's UPDATE may leave one out.
        foreach (string text in new[] { "Symbols", "Topics" })
        {
            Assert.Equal("text", Column(text).GetColumnType());
            Assert.False(Column(text).IsNullable);
            Assert.Equal("", Column(text).GetDefaultValue());
        }

        Assert.Equal("character varying(80)", Column("ScoreModel").GetColumnType());
        Assert.False(Column("ScoreModel").IsNullable);
        Assert.Equal("", Column("ScoreModel").GetDefaultValue());

        // The work queue: "ScoredUtc" IS NULL.
        Assert.Contains(entity!.GetIndexes(), i => i.Properties.Select(p => p.Name).SequenceEqual(["ScoredUtc"]));
    }

    [Fact]
    public void The_point_in_time_stamps_and_the_text_the_scorer_reads_are_where_it_expects_them()
    {
        var model = IncidentsTableContractTests.Model();
        string Type(string table, string column)
        {
            var entity = IncidentsTableContractTests.EntityFor(model, table)!;
            return entity.GetProperties().Single(p => p.GetColumnName(StoreObjectIdentifier.Table(table)) == column).GetColumnType();
        }

        Assert.Equal("bigint", Type("news_items", "Id"));
        Assert.Equal("text", Type("news_items", "Title"));
        Assert.Equal("text", Type("news_items", "Summary"));
        Assert.Equal("character varying(40)", Type("news_items", "Category"));
        Assert.Equal("character varying(64)", Type("news_items", "LinkHash"));
        Assert.Equal("timestamp with time zone", Type("news_items", "FirstSeenUtc"));
        Assert.Equal("timestamp with time zone", Type("news_items", "PublishedUtc"));

        Assert.Equal("bigint", Type("corporate_announcements", "Id"));
        Assert.Equal("character varying(40)", Type("corporate_announcements", "Symbol"));
        Assert.Equal("text", Type("corporate_announcements", "Subject"));
        Assert.Equal("text", Type("corporate_announcements", "Details"));
        Assert.Equal("timestamp with time zone", Type("corporate_announcements", "AnnouncedUtc"));
        Assert.Equal("timestamp with time zone", Type("corporate_announcements", "FirstSeenUtc"));
        Assert.Equal("character varying(64)", Type("corporate_announcements", "UniqueKey"));
    }

    [FactWhenNewsScorerExists]
    public void Every_table_the_scorer_names_exists()
    {
        // Literal SQL ("FROM news_items"), keywords in upper case only, so
        // that Python's own "from x import y" is not read as a table.
        var named = Regex.Matches(ScorerCode(), @"\b(?:FROM|INTO|JOIN|UPDATE)\s+""?([a-z][a-z0-9_]*)""?")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        Assert.Contains("news_items", named);
        Assert.Contains("corporate_announcements", named);
        var model = IncidentsTableContractTests.Model();
        Assert.All(named, table => Assert.True(IncidentsTableContractTests.EntityFor(model, table) is not null, $"news.py names a table the model does not have: {table}"));
    }

    [FactWhenNewsScorerExists]
    public void Every_column_the_scorer_names_exists_with_that_spelling()
    {
        var model = IncidentsTableContractTests.Model();
        var columns = Tables
            .SelectMany(t => IncidentsTableContractTests.EntityFor(model, t)!.GetProperties().Select(p => p.GetColumnName(StoreObjectIdentifier.Table(t))))
            .ToHashSet();

        // The whole file, comments included: stricter than the SQL alone.
        var quoted = Regex.Matches(ReadScorer(), "\"([A-Z][A-Za-z0-9]+)\"").Select(m => m.Groups[1].Value).Distinct().ToList();

        // Guard against the test passing because the file moved or changed shape.
        Assert.Contains("ScoredUtc", quoted);
        var missing = quoted.Where(c => !columns.Contains(c)).ToList();
        Assert.True(missing.Count == 0, "news.py names columns neither table has: " + string.Join(", ", missing));
    }

    [FactWhenNewsScorerExists]
    public void The_scorer_writes_only_the_scoring_columns()
    {
        // Above all it must never touch FirstSeenUtc: that stamp is what makes a
        // replayed forecast honest. A SET list may run over several string
        // literals, so it is read up to its WHERE.
        var sets = Regex.Matches(ScorerCode(), @"\bSET\b(?<set>.*?)\bWHERE\b", RegexOptions.Singleline)
            .Select(m => Regex.Matches(m.Groups["set"].Value, "\"([A-Za-z0-9]+)\"\\s*=").Select(c => c.Groups[1].Value).ToList())
            .ToList();

        Assert.True(sets.Count >= 2,
            $"news.py should write each table with a literal UPDATE ... SET ... WHERE; found {sets.Count}. "
            + "SQL composed at run time cannot be checked from the file.");
        Assert.All(sets, assigned =>
        {
            var outside = assigned.Except(ScoringColumns).ToList();
            Assert.True(outside.Count == 0, "news.py writes columns that are not its own: " + string.Join(", ", outside));
            Assert.Contains("ScoredUtc", assigned);
        });
    }

    internal static string ScorerPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
        {
            dir = dir.Parent;
        }

        return dir is null ? ScorerFile : Path.Combine(dir.FullName, ScorerFile);
    }

    private static string ReadScorer() => File.ReadAllText(ScorerPath());

    /// <summary>
    /// The scorer's source without docstrings and comment lines, whose prose
    /// can say "FROM" or "SET" without being SQL.
    /// </summary>
    private static string ScorerCode()
    {
        string code = Regex.Replace(ReadScorer(), "(\"\"\"|''')[\\s\\S]*?\\1", string.Empty);
        return Regex.Replace(code, @"^\s*#.*$", string.Empty, RegexOptions.Multiline);
    }
}
