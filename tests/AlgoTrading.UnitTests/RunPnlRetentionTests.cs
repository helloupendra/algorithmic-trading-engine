using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlgoTrading.UnitTests;

/// <summary>
/// run_pnl_minutes keeps every row unless a window of days is set; with one,
/// the sweep removes what is older, a batch at a time, and nothing inside it.
/// </summary>
/// <remarks>
/// The recorder writes a row per live run per minute (28 Sep). The owner's
/// rule is that every datum is kept and nothing is deleted that is not on
/// Drive, so the sweep ships switched off; the tests that delete set a window.
/// </remarks>
public class RunPnlRetentionTests
{
    private static readonly DateTime Now = new(2026, 12, 30, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Rows_older_than_the_window_go_and_the_window_stays()
    {
        using var desk = new Table();
        desk.Seed(runId: 1, Now.AddDays(-120), minutes: 3);
        desk.Seed(runId: 1, Now.AddDays(-91), minutes: 2);
        desk.Seed(runId: 2, Now.AddDays(-89), minutes: 4);
        desk.Seed(runId: 2, Now.AddMinutes(-5), minutes: 5);

        int removed = await desk.Sweeper(new RunPnlRetentionOptions { RetentionDays = 90 }).SweepAsync(Now, CancellationToken.None);

        Assert.Equal(5, removed);
        var left = desk.Rows();
        Assert.Equal(9, left.Count);
        Assert.All(left, x => Assert.True(x.AtUtc >= Now.AddDays(-90)));
    }

    [Fact]
    public async Task A_large_backlog_goes_in_batches_until_a_short_one()
    {
        using var desk = new Table();
        desk.Seed(runId: 1, Now.AddDays(-200), minutes: 25);
        desk.Seed(runId: 1, Now.AddDays(-1), minutes: 2);
        var sweeper = desk.Sweeper(new RunPnlRetentionOptions { RetentionDays = 90, BatchSize = 10, BatchPause = TimeSpan.Zero });

        int removed = await sweeper.SweepAsync(Now, CancellationToken.None);

        Assert.Equal(25, removed);
        Assert.Equal(new[] { 10, 10, 5 }, sweeper.Batches);    // never more than a batch in one statement
        Assert.Equal(2, desk.Rows().Count);
    }

    [Fact]
    public async Task A_batch_takes_the_oldest_rows_first()
    {
        using var desk = new Table();
        desk.Seed(runId: 1, Now.AddDays(-100), minutes: 3);
        desk.Seed(runId: 2, Now.AddDays(-150), minutes: 3);

        await using var db = desk.Db();
        var batch = await RunPnlRetentionService.Expired(db.RunPnlMinutes, Now.AddDays(-90), 3).ToListAsync();

        Assert.All(batch, x => Assert.Equal(2, x.SimulationRunId));
    }

    [Fact]
    public async Task Zero_days_keeps_everything()
    {
        using var desk = new Table();
        desk.Seed(runId: 1, Now.AddDays(-400), minutes: 3);
        var sweeper = desk.Sweeper(new RunPnlRetentionOptions { RetentionDays = 0 });

        Assert.Equal(0, await sweeper.SweepAsync(Now, CancellationToken.None));
        Assert.Empty(sweeper.Batches);
        Assert.Equal(3, desk.Rows().Count);
    }

    [Fact]
    public async Task What_a_sweep_removed_is_logged()
    {
        using var desk = new Table();
        desk.Seed(runId: 1, Now.AddDays(-100), minutes: 4);
        var log = new ListLogger();
        var sweeper = desk.Sweeper(new RunPnlRetentionOptions { RetentionDays = 90 }, log);

        await sweeper.SweepAsync(Now, CancellationToken.None);
        await sweeper.SweepAsync(Now, CancellationToken.None);    // nothing left: nothing said

        var line = Assert.Single(log.Lines);
        Assert.Contains("removed 4 run_pnl_minutes rows older than 90 days", line);
    }

    [Fact]
    public async Task By_default_nothing_is_deleted()
    {
        // The owner's rule: every datum is kept, and nothing is deleted that
        // is not on Drive. The sweep exists, but only a setting turns it on.
        using var desk = new Table();
        desk.Seed(runId: 1, Now.AddDays(-400), minutes: 3);
        desk.Seed(runId: 1, Now.AddDays(-100), minutes: 2);
        var options = new RunPnlRetentionOptions();
        var sweeper = desk.Sweeper(options);

        Assert.Equal(0, options.RetentionDays);
        Assert.Equal("RunPnlRetention", RunPnlRetentionOptions.SectionName);
        Assert.Equal(0, await sweeper.SweepAsync(Now, CancellationToken.None));
        Assert.Empty(sweeper.Batches);
        Assert.Equal(5, desk.Rows().Count);
    }

    [Fact]
    public void The_committed_settings_keep_everything()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "AlgoTrading.Api", "appsettings.json")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);

        using var settings = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(dir!.FullName, "src", "AlgoTrading.Api", "appsettings.json")));
        var days = settings.RootElement.GetProperty(RunPnlRetentionOptions.SectionName).GetProperty("RetentionDays").GetInt32();

        Assert.Equal(0, days);
    }

    /// <summary>run_pnl_minutes in memory, and the sweep over it.</summary>
    private sealed class Table : IDisposable
    {
        private readonly InMemoryDatabaseRoot _root = new();
        private readonly string _name = $"run-pnl-retention-{Guid.NewGuid():N}";
        private readonly ServiceProvider _provider;

        public Table()
        {
            var services = new ServiceCollection();
            services.AddDbContext<TradingDbContext>(o => o.UseInMemoryDatabase(_name, _root));
            _provider = services.BuildServiceProvider();
        }

        public TradingDbContext Db()
            => new(new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase(_name, _root).Options);

        public void Seed(long runId, DateTime fromUtc, int minutes)
        {
            using var db = Db();
            for (int i = 0; i < minutes; i++)
            {
                db.RunPnlMinutes.Add(new RunPnlMinute { SimulationRunId = runId, AtUtc = fromUtc.AddMinutes(i), Net = i });
            }
            db.SaveChanges();
        }

        public List<RunPnlMinute> Rows()
        {
            using var db = Db();
            return db.RunPnlMinutes.AsNoTracking().OrderBy(x => x.AtUtc).ToList();
        }

        public InMemorySweeper Sweeper(RunPnlRetentionOptions options, ILogger<RunPnlRetentionService>? logger = null)
            => new(_provider.GetRequiredService<IServiceScopeFactory>(), logger ?? NullLogger<RunPnlRetentionService>.Instance, options);

        public void Dispose() => _provider.Dispose();
    }

    /// <summary>
    /// The service with its one DELETE done row by row: the in-memory provider
    /// has no bulk delete. The rows are the ones the real statement removes.
    /// </summary>
    private sealed class InMemorySweeper(IServiceScopeFactory scopes, ILogger<RunPnlRetentionService> logger, RunPnlRetentionOptions options)
        : RunPnlRetentionService(scopes, logger, options)
    {
        public List<int> Batches { get; } = new();

        protected override async Task<int> DeleteBatchAsync(TradingDbContext db, DateTime cutoffUtc, int batchSize, CancellationToken cancellationToken)
        {
            var rows = await Expired(db.RunPnlMinutes, cutoffUtc, batchSize).ToListAsync(cancellationToken);
            db.RunPnlMinutes.RemoveRange(rows);
            await db.SaveChangesAsync(cancellationToken);
            Batches.Add(rows.Count);
            return rows.Count;
        }
    }

    private sealed class ListLogger : ILogger<RunPnlRetentionService>
    {
        public List<string> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
    }
}
