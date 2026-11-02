using AlgoTrading.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The file that tells the desk a migration is running (2 Nov 2026 review, F4). The desk's side of it is
/// scripts/tests/api-migration.test.sh.
/// </summary>
public sealed class StartupMigrationMarkerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 18, 30, 0, TimeSpan.Zero);
    private static readonly string[] Pending = ["20261102000000_First", "20261102000001_Second"];

    private readonly string _folder = Directory.CreateTempSubdirectory("migration-marker-").FullName;

    private string MarkerPath => Path.Combine(_folder, "api-migrating");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static string Field(string text, string name) =>
        text.Split('\n').Single(line => line.StartsWith(name + "=", StringComparison.Ordinal))[(name.Length + 1)..];

    [Fact]
    public void Begin_writes_the_fields_the_desk_reads()
    {
        StartupMigrationMarker? marker = StartupMigrationMarker.Begin(MarkerPath, Pending, NullLogger.Instance, Now);

        Assert.NotNull(marker);
        string text = File.ReadAllText(MarkerPath);
        Assert.Equal(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), Field(text, "pid"));
        Assert.Equal(Now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), Field(text, "started"));
        Assert.Equal("2", Field(text, "pending"));
        Assert.False(File.Exists(MarkerPath + ".tmp"), "the temporary file is moved into place, not left beside it");
    }

    [Fact]
    public void Done_removes_the_file()
    {
        StartupMigrationMarker.Begin(MarkerPath, Pending, NullLogger.Instance, Now)!.Done();

        Assert.False(File.Exists(MarkerPath));
    }

    [Fact]
    public void Begin_replaces_a_file_a_killed_process_left_behind()
    {
        File.WriteAllText(MarkerPath, "pid=1\nstarted=1\npending=9\n");

        StartupMigrationMarker.Begin(MarkerPath, Pending, NullLogger.Instance, Now);

        Assert.Equal("2", Field(File.ReadAllText(MarkerPath), "pending"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Without_a_path_there_is_no_file_and_no_marker(string? path)
    {
        Assert.Null(StartupMigrationMarker.Begin(path, Pending, NullLogger.Instance, Now));
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public void A_file_that_cannot_be_written_does_not_stop_the_migration()
    {
        string nowhere = Path.Combine(_folder, "no-such-folder", "api-migrating");

        Assert.Null(StartupMigrationMarker.Begin(nowhere, Pending, NullLogger.Instance, Now));
    }

    [Fact]
    public void Program_marks_only_around_the_migration_and_keeps_the_mark_when_it_throws()
    {
        string program = File.ReadAllText(Path.Combine(RepoRoot(), "src", "AlgoTrading.Api", "Program.cs"));

        int begin = program.IndexOf("StartupMigrationMarker.Begin(", StringComparison.Ordinal);
        int migrate = program.IndexOf("Database.MigrateAsync()", StringComparison.Ordinal);
        int done = program.IndexOf("migrationMarker?.Done()", StringComparison.Ordinal);
        Assert.True(begin > 0 && begin < migrate && migrate < done, "Begin, then MigrateAsync, then Done");
        // No finally: the file left by a throw is how the desk tells a failed migration from any other down API.
        Assert.DoesNotContain("finally", program[migrate..done], StringComparison.Ordinal);
    }

    [Fact]
    public void The_desk_hands_the_API_the_path_this_class_reads()
    {
        string desk = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "lib", "desk-common.sh"));

        Assert.Contains($"export {StartupMigrationMarker.PathVariable}=", desk, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
