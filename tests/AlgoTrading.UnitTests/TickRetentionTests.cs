using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.UnitTests;

/// <summary>
/// live_ticks is a hypertable, kept 90 days by TimescaleDB's policy; the owner wants
/// every datum kept. This sweep deletes rows only from a table the catalog says is
/// plain. On 2 Nov 2026 the review found that a failed catalog query read as "plain".
/// </summary>
public class TickRetentionTests
{
    [Fact]
    public async Task A_catalog_check_that_fails_deletes_nothing_and_says_so_at_warning()
    {
        using var desk = new Table();
        desk.Seed(ageDays: 200, count: 3);
        desk.Seed(ageDays: 8, count: 3);
        var log = new ListLogger();

        // The real check. The in-memory provider has no raw SQL, so it throws, as a catalog
        // query does on an exhausted pool or a timeout; this is the review's probe turned round.
        var sweeper = desk.Sweeper(new TickRetentionOptions { RetentionDays = 7 }, log);
        int removed = await sweeper.SweepAsync(CancellationToken.None);

        Assert.Equal(0, removed);
        Assert.Empty(sweeper.Batches);
        Assert.Equal(6, desk.Count());
        var line = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Contains("skipped this sweep", line.Text);
        Assert.NotNull(line.Exception);
    }

    [Fact]
    public async Task A_hypertable_is_left_to_its_policy()
    {
        using var desk = new Table();
        desk.Seed(ageDays: 200, count: 3);

        var sweeper = desk.Sweeper(new TickRetentionOptions(), hypertable: true);

        Assert.Equal(0, await sweeper.SweepAsync(CancellationToken.None));
        Assert.Empty(sweeper.Batches);
        Assert.Equal(3, desk.Count());
    }

    [Fact]
    public async Task A_plain_table_keeps_what_the_policy_would_keep_and_loses_only_what_is_older()
    {
        using var desk = new Table();
        desk.Seed(ageDays: 91, count: 25);
        desk.Seed(ageDays: 89, count: 4);
        desk.Seed(ageDays: 8, count: 2);

        var sweeper = desk.Sweeper(new TickRetentionOptions { BatchSize = 10, BatchPause = TimeSpan.Zero }, hypertable: false);

        Assert.Equal(25, await sweeper.SweepAsync(CancellationToken.None));
        Assert.Equal(new[] { 10, 10, 5 }, sweeper.Batches);
        Assert.Equal(6, desk.Count());
    }

    [Fact]
    public void The_default_cannot_cut_deeper_than_the_hypertables_policy()
    {
        // No committed settings file has a TickRetention section, so the default is what runs.
        string root = PortfolioTestKit.RepoRoot();
        var settings = Directory.GetFiles(Path.Combine(root, "src", "AlgoTrading.Api"), "appsettings*.json");
        Assert.NotEmpty(settings);
        Assert.All(settings, file => Assert.DoesNotContain(TickRetentionOptions.SectionName, File.ReadAllText(file)));

        string policy = File.ReadAllText(Path.Combine(root, "src", "AlgoTrading.Infrastructure", "Persistence", "Migrations", "20260910093000_LiveTicksRetention90Days.cs"));
        Assert.Contains($"drop_after => INTERVAL '{new TickRetentionOptions().RetentionDays} days'", policy);
    }

    private sealed class Table : IDisposable
    {
        private readonly InMemoryDatabaseRoot _root = new();
        private readonly string _name = $"tick-retention-{Guid.NewGuid():N}";
        private readonly ServiceProvider _provider;

        public Table()
        {
            var services = new ServiceCollection();
            services.AddDbContext<TradingDbContext>(o => o.UseInMemoryDatabase(_name, _root));
            _provider = services.BuildServiceProvider();
        }

        private TradingDbContext Db()
            => new(new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase(_name, _root).Options);

        public void Seed(int ageDays, int count)
        {
            using var db = Db();
            for (int i = 0; i < count; i++)
            {
                db.LiveTicks.Add(new LiveTick { Symbol = "NSE:NIFTY50-INDEX", ReceivedUtc = DateTime.UtcNow.AddDays(-ageDays).AddSeconds(-i) });
            }

            db.SaveChanges();
        }

        public int Count()
        {
            using var db = Db();
            return db.LiveTicks.Count();
        }

        public Sweeper Sweeper(TickRetentionOptions options, ListLogger? log = null, bool? hypertable = null)
            => new(_provider.GetRequiredService<IServiceScopeFactory>(), log ?? new ListLogger(), options, hypertable);

        public void Dispose() => _provider.Dispose();
    }

    /// <summary>
    /// The service with its DELETE done row by row, as the in-memory provider has no bulk
    /// delete, and the catalog's answer given when <paramref name="hypertable"/> is set.
    /// </summary>
    private sealed class Sweeper(IServiceScopeFactory scopes, ILogger<TickRetentionService> log, TickRetentionOptions options, bool? hypertable)
        : TickRetentionService(scopes, log, options)
    {
        public List<int> Batches { get; } = new();

        protected override Task<bool> IsHypertableAsync(CancellationToken stoppingToken)
            => hypertable is { } answer ? Task.FromResult(answer) : base.IsHypertableAsync(stoppingToken);

        protected override async Task<int> DeleteBatchAsync(TradingDbContext db, DateTime cutoffUtc, int batchSize, CancellationToken stoppingToken)
        {
            var rows = await db.LiveTicks.Where(x => x.ReceivedUtc < cutoffUtc).OrderBy(x => x.ReceivedUtc).Take(batchSize).ToListAsync(stoppingToken);
            db.LiveTicks.RemoveRange(rows);
            await db.SaveChangesAsync(stoppingToken);
            Batches.Add(rows.Count);
            return rows.Count;
        }
    }

    private sealed class ListLogger : ILogger<TickRetentionService>
    {
        public List<(LogLevel Level, string Text, Exception? Exception)> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add((logLevel, formatter(state, exception), exception));
    }
}
