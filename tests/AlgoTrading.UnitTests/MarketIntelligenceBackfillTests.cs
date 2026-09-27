using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using AlgoTrading.Infrastructure.Services.MarketFactors;
using AlgoTrading.Infrastructure.Services.MarketIntelligence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using static AlgoTrading.UnitTests.MarketIntelligenceTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The history backfill: which days it asks for, in what order, and that a
/// run stopped at any point is resumed by the next without asking for a day
/// twice. The server is small and shared with live trading, so a backfill
/// that re-downloaded what it has, or never finished because of holidays it
/// could not tell from gaps, would cost real resources every night.
/// </summary>
public class MarketIntelligenceBackfillTests
{
    /// <summary>A dataset held in memory, with scripted answers per day.</summary>
    private sealed class FakeDataset
    {
        public HashSet<DateOnly> Present { get; } = [];
        public HashSet<DateOnly> NoFile { get; } = [];
        public HashSet<DateOnly> Broken { get; } = [];
        public List<DateOnly> Asked { get; } = [];

        public DailyDataset Build(string name = "test") => new(name, new DateOnly(2026, 9, 1),
            (from, to, _) => Task.FromResult<IReadOnlySet<DateOnly>>(Present.Where(d => d >= from && d <= to).ToHashSet()),
            (day, _) =>
            {
                Asked.Add(day);
                if (NoFile.Contains(day)) return Task.FromResult(DayFetchOutcome.NotPublished);
                if (Broken.Contains(day)) return Task.FromResult(DayFetchOutcome.Failed($"{day:yyyy-MM-dd}: HTTP 500"));
                Present.Add(day);
                return Task.FromResult(DayFetchOutcome.Stored);
            });
    }

    private static DailyBackfillRunner Runner(TradingDbContext db, MarketIntelligenceStatus status, DateTime now, params DateOnly[] holidays) =>
        new(new ProcessSettingsStore(db), new Calendar(holidays), status, NullLogger<DailyBackfillRunner>.Instance) { Clock = () => now };

    private static readonly DateOnly From = new(2026, 9, 1);
    private static readonly DateOnly To = new(2026, 9, 25);
    private static readonly DateTime Now = Ist(2026, 9, 27, 20, 0);

    [Fact]
    public async Task Only_missing_sessions_are_asked_for_newest_first()
    {
        await using var db = Db();
        var data = new FakeDataset();
        data.Present.UnionWith([new DateOnly(2026, 9, 24), new DateOnly(2026, 9, 10)]);
        var ganesh = new DateOnly(2026, 9, 14);   // Ganesh Chaturthi, a known holiday

        var report = await Runner(db, new MarketIntelligenceStatus(), Now, ganesh).RunAsync(data.Build(), From, To, () => true, trackProgress: true, CancellationToken.None);

        // 19 weekdays, less the holiday and the two stored days.
        Assert.Equal(16, report.ToFetch);
        Assert.Equal(16, report.Stored);
        Assert.Equal(new DateOnly(2026, 9, 25), data.Asked[0]);
        Assert.Equal(data.Asked.OrderByDescending(d => d), data.Asked);
        Assert.DoesNotContain(ganesh, data.Asked);
        Assert.DoesNotContain(data.Asked, d => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
        Assert.DoesNotContain(new DateOnly(2026, 9, 24), data.Asked);
    }

    [Fact]
    public async Task A_run_stopped_halfway_is_resumed_without_asking_for_a_day_twice()
    {
        await using var db = Db();
        var data = new FakeDataset();
        var status = new MarketIntelligenceStatus();
        var runner = Runner(db, status, Now);

        // 09:00 on a trading day arrives after five days.
        int allowed = 5;
        var first = await runner.RunAsync(data.Build(), From, To, () => allowed-- > 0, trackProgress: true, CancellationToken.None);

        Assert.True(first.Paused);
        Assert.Equal(5, first.Stored);
        Assert.Equal("waiting", status.Backfill("test").State);

        var second = await runner.RunAsync(data.Build(), From, To, () => true, trackProgress: true, CancellationToken.None);

        Assert.Equal(first.ToFetch - 5, second.ToFetch);
        Assert.Equal(data.Asked.Count, data.Asked.Distinct().Count());
        Assert.Equal(19, data.Present.Count);
        Assert.Equal("done", status.Backfill("test").State);

        // A third run has nothing to do and asks for nothing.
        data.Asked.Clear();
        var third = await runner.RunAsync(data.Build(), From, To, () => true, trackProgress: true, CancellationToken.None);
        Assert.Equal(0, third.ToFetch);
        Assert.Empty(data.Asked);
    }

    [Fact]
    public async Task An_old_day_with_no_file_is_remembered_and_a_recent_one_is_asked_again()
    {
        await using var db = Db();
        var data = new FakeDataset();
        var oldHoliday = new DateOnly(2026, 9, 2);    // a holiday the calendar does not hold
        var notYet = new DateOnly(2026, 9, 25);       // two days before "now": NSE may still post it
        data.NoFile.UnionWith([oldHoliday, notYet]);
        var runner = Runner(db, new MarketIntelligenceStatus(), Now);

        await runner.RunAsync(data.Build(), From, To, () => true, trackProgress: true, CancellationToken.None);

        string? remembered = await new ProcessSettingsStore(db).GetAsync(SystemSettingKeys.MarketIntelligenceNoFile("test"));
        Assert.Equal("20260902", remembered);

        data.Asked.Clear();
        var again = await runner.RunAsync(data.Build(), From, To, () => true, trackProgress: true, CancellationToken.None);
        Assert.Equal(new[] { notYet }, data.Asked);
        Assert.Equal(1, again.NotPublished);

        // The remembered day is not missing; the unpublished one is.
        var coverage = await runner.CoverageAsync(data.Build(), From, To, CancellationToken.None);
        Assert.Equal(1, coverage.MissingSessions);
        Assert.Equal(1, coverage.NoFileDays);
        Assert.Equal(new DateOnly(2026, 9, 1), coverage.FirstDate);
        Assert.Equal(new DateOnly(2026, 9, 24), coverage.LastDate);
    }

    [Fact]
    public async Task A_failed_day_is_reported_and_tried_again_next_run()
    {
        await using var db = Db();
        var data = new FakeDataset();
        var broken = new DateOnly(2026, 9, 15);
        data.Broken.Add(broken);
        var status = new MarketIntelligenceStatus();
        var runner = Runner(db, status, Now);

        var first = await runner.RunAsync(data.Build(), From, To, () => true, trackProgress: true, CancellationToken.None);
        Assert.Equal(1, first.Failed);
        Assert.Equal("2026-09-15: HTTP 500", first.LastError);
        Assert.NotEqual("done", status.Backfill("test").State);

        data.Broken.Clear();
        data.Asked.Clear();
        var second = await runner.RunAsync(data.Build(), From, To, () => true, trackProgress: true, CancellationToken.None);
        Assert.Equal(new[] { broken }, data.Asked);
        Assert.Equal("done", status.Backfill("test").State);
        Assert.Equal(0, second.Failed);
    }

    [Fact]
    public async Task The_evening_look_does_not_overwrite_the_history_backfill_s_progress()
    {
        await using var db = Db();
        var status = new MarketIntelligenceStatus();
        status.Backfill("test").State = "waiting";

        await Runner(db, status, Now).RunAsync(new FakeDataset().Build(), To.AddDays(-3), To, () => true, trackProgress: false, CancellationToken.None);

        Assert.Equal("waiting", status.Backfill("test").State);
    }

    [Fact]
    public async Task The_participant_backfill_reads_the_same_archive_for_every_date_and_skips_stored_days()
    {
        // The per-day fetch the backfill uses is the evening sync's own, against the real 2020 file.
        await using var db = Db();
        var day = new DateOnly(2020, 1, 2);
        var web = new FakeWeb().Serve(MarketFactorsSync.ParticipantUrl(day), Fixture("participant_oi_02012020.csv"));
        var sync = new MarketFactorsSync(db, new Factory(web), new Calendar(), new MarketFactorsStatus(), NullLogger<MarketFactorsSync>.Instance) { Pace = TimeSpan.Zero };
        var dataset = MarketIntelligenceDatasets.ParticipantOi(sync);

        var report = await Runner(db, new MarketIntelligenceStatus(), Ist(2020, 1, 10, 20, 0))
            .RunAsync(dataset, new DateOnly(2020, 1, 1), new DateOnly(2020, 1, 3), () => true, trackProgress: true, CancellationToken.None);

        Assert.Equal(1, report.Stored);
        Assert.Equal(2, report.NotPublished);   // 1 Jan and 3 Jan are not served here
        Assert.Equal(5, await db.MarketParticipantOpenInterest.CountAsync(r => r.Date == day));
        Assert.Equal("https://archives.nseindia.com/content/nsccl/fao_participant_oi_02012020.csv", MarketFactorsSync.ParticipantUrl(day));

        web.Requests.Clear();
        await Runner(db, new MarketIntelligenceStatus(), Ist(2020, 1, 10, 20, 0))
            .RunAsync(dataset, new DateOnly(2020, 1, 1), new DateOnly(2020, 1, 3), () => true, trackProgress: true, CancellationToken.None);
        Assert.Empty(web.Requests);   // 2 Jan stored, 1 and 3 Jan remembered as having no file
    }
}
