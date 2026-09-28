using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The tables the nightly Drive archive copies, as its own SQL names them.
/// </summary>
/// <remarks>
/// <c>scripts/archive_to_drive.py</c> lists each table with the column that
/// places a row in a day, and reads, exports, verifies and (when asked) frees
/// each day with hand-written SQL on that column. A table or column renamed
/// here would pass every other test and show up first as an archive that
/// fails every morning, or copies nothing. So the real Npgsql model is built
/// (no connection is opened) and checked against the script's list.
/// </remarks>
public class ArchiveTablesContractTests
{
    private const string ScriptFile = "scripts/archive_to_drive.py";

    [Fact]
    public void Every_archived_table_has_its_day_column_as_a_utc_timestamp()
    {
        var tables = ArchivedTables();
        var model = IncidentsTableContractTests.Model();

        // Guard against the test passing because the list moved or changed shape.
        Assert.Contains(("live_ticks", "ReceivedUtc"), tables);
        Assert.True(tables.Count >= 4, $"read only {tables.Count} table(s) from {ScriptFile}");

        foreach (var (table, column) in tables)
        {
            var entity = IncidentsTableContractTests.EntityFor(model, table);
            Assert.True(entity is not null, $"{ScriptFile} archives {table}, which the model does not map");

            var store = StoreObjectIdentifier.Table(table);
            var property = entity!.GetProperties().SingleOrDefault(p => p.GetColumnName(store) == column);
            Assert.True(property is not null, $"{ScriptFile} places {table} rows in a day by \"{column}\", which it does not have");

            // The script reads the day as ("column" at time zone 'UTC')::date:
            // right only for a timestamp that carries its zone.
            Assert.Equal("timestamp with time zone", property!.GetColumnType());
        }
    }

    [Fact]
    public void The_live_runs_pnl_minutes_are_archived()
    {
        // Every datum is kept, and nothing is deleted that is not on Drive.
        Assert.Contains(("run_pnl_minutes", "AtUtc"), ArchivedTables());
    }

    /// <summary>The (table, day column) pairs of the script's TABLES.</summary>
    private static List<(string Table, string Column)> ArchivedTables()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, ScriptFile)))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);

        var source = File.ReadAllText(Path.Combine(dir!.FullName, ScriptFile));
        var block = Regex.Match(source, @"^TABLES: Dict\[str, str\] = \{(.*?)^\}", RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.True(block.Success, $"no TABLES dict in {ScriptFile}");

        return Regex.Matches(block.Groups[1].Value, "\"([a-z_]+)\"\\s*:\\s*\"([A-Za-z]+)\"")
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value))
            .ToList();
    }
}
