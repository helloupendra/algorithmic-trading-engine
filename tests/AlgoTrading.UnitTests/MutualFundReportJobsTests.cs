using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services.MutualFunds;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlgoTrading.UnitTests;

/// <summary>The fund report's nightly jobs: when they run, what they read from niftyindices.com and AMFI, and the ranking.</summary>
public class MutualFundReportJobsTests
{
    /// <summary>An IST wall-clock time as UTC.</summary>
    private static DateTime Ist(int year, int month, int day, int hour, int minute = 0) =>
        new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc).AddMinutes(-330);

    private static MutualFundJobRun Ran(DateTime startedUtc, bool ok = true, DateOnly? reached = null) =>
        new() { StartedUtc = startedUtc, FinishedUtc = startedUtc.AddMinutes(1), Ok = ok, Reached = reached };

    // ---------- schedule ----------

    [Fact]
    public void Nightly_jobs_run_after_23_45_ist_and_retry_in_the_morning_only_when_short()
    {
        var oct30 = new DateOnly(2026, 10, 30);
        Assert.Equal("first", MutualFundAnalyticsSchedule.NightlyDue(Ist(2026, 11, 2, 10, 0), null));
        // Ran at 23:45 on Friday: nothing more until Saturday 23:45.
        var friday = Ran(Ist(2026, 10, 30, 23, 46), reached: oct30);
        Assert.Null(MutualFundAnalyticsSchedule.NightlyDue(Ist(2026, 10, 31, 8, 0), friday, oct30));
        Assert.Equal("evening", MutualFundAnalyticsSchedule.NightlyDue(Ist(2026, 10, 31, 23, 50), friday, oct30));
        // A run that fell short of the day due is tried again at 07:15.
        var short_ = Ran(Ist(2026, 11, 2, 23, 46), reached: oct30);
        Assert.Null(MutualFundAnalyticsSchedule.NightlyDue(Ist(2026, 11, 3, 7, 0), short_, new DateOnly(2026, 11, 2)));
        Assert.Equal("morning", MutualFundAnalyticsSchedule.NightlyDue(Ist(2026, 11, 3, 7, 20), short_, new DateOnly(2026, 11, 2)));
        // A failure is tried again an hour later.
        var failed = Ran(Ist(2026, 11, 3, 0, 10), ok: false);
        Assert.Null(MutualFundAnalyticsSchedule.NightlyDue(Ist(2026, 11, 3, 0, 50), failed));
        Assert.Equal("retry", MutualFundAnalyticsSchedule.NightlyDue(Ist(2026, 11, 3, 1, 15), failed));
    }

    [Fact]
    public void The_ranking_runs_for_a_newer_nav_day_and_at_most_every_six_hours_while_the_backfill_fills_older_years()
    {
        var oct30 = new DateOnly(2026, 10, 30);
        var nov2 = new DateOnly(2026, 11, 2);
        Assert.Null(MutualFundAnalyticsSchedule.StatsDue(Ist(2026, 11, 3, 0, 0), null, null, null, null));
        Assert.Equal("first", MutualFundAnalyticsSchedule.StatsDue(Ist(2026, 11, 3, 0, 0), null, oct30, null, null));

        var ranked = Ran(Ist(2026, 11, 2, 0, 0), reached: oct30);
        Assert.Equal("evening", MutualFundAnalyticsSchedule.StatsDue(Ist(2026, 11, 3, 0, 0), ranked, nov2, null, null));
        Assert.Null(MutualFundAnalyticsSchedule.StatsDue(Ist(2026, 11, 2, 3, 0), ranked, oct30, new DateOnly(2020, 1, 1), new DateOnly(2021, 1, 1)));
        Assert.Equal("backfill", MutualFundAnalyticsSchedule.StatsDue(Ist(2026, 11, 2, 6, 30), ranked, oct30, new DateOnly(2020, 1, 1), new DateOnly(2021, 1, 1)));
        Assert.Null(MutualFundAnalyticsSchedule.StatsDue(Ist(2026, 11, 2, 6, 30), ranked, oct30, new DateOnly(2021, 1, 1), new DateOnly(2021, 1, 1)));
        Assert.Equal("retry", MutualFundAnalyticsSchedule.StatsDue(Ist(2026, 11, 2, 1, 30), Ran(Ist(2026, 11, 2, 0, 0), ok: false), oct30, null, null));
    }

    // ---------- state ----------

    [Fact]
    public async Task The_state_is_kept_in_small_parts_and_only_changed_parts_are_written()
    {
        var settings = new CountingSettings();
        string longMessage = new('x', 5000);
        await MutualFundAnalyticsSettings.UpdateAsync(settings, s =>
        {
            s.Benchmarks = new MutualFundJobRun { Message = MutualFundAnalyticsSettings.Clip(longMessage), Ok = true };
            foreach (var index in AlgoTrading.Infrastructure.Services.MutualFunds.Analytics.MutualFundBenchmarks.Indices)
                s.BenchmarkHistory[index.Key] = new MutualFundBenchmarkHistory { Cursor = new DateOnly(2006, 1, 1), Complete = true, Note = "no values before 1 Apr 2005" };
            s.Ter = new MutualFundJobRun { Message = MutualFundAnalyticsSettings.Clip(longMessage) };
            s.Stats = new MutualFundJobRun { Message = MutualFundAnalyticsSettings.Clip(longMessage) };
            s.Requested.Add(MutualFundJobs.Stats);
        }, default);

        // system_settings.Value is varchar(2000).
        Assert.All(settings.Values.Values, v => Assert.True(v.Length < 2000, $"{v.Length} characters"));

        settings.Writes.Clear();
        await MutualFundAnalyticsSettings.UpdateAsync(settings, s => s.Requested.Remove(MutualFundJobs.Stats), default);
        Assert.Equal([MutualFundAnalyticsSettings.RequestedKey], settings.Writes);

        var read = await MutualFundAnalyticsSettings.ReadAsync(settings, default);
        Assert.Empty(read.Requested);
        Assert.True(read.BenchmarkHistory["NIFTY 50"].Complete);
        Assert.Equal(601, read.Ter!.Message.Length);
    }

    // ---------- niftyindices.com ----------

    [Fact]
    public void Tri_answers_are_read_oldest_first_and_other_indices_rows_are_skipped()
    {
        const string json = """
            [{"RequestNumber":"TRI1","Index Name":"Nifty 50","Date":"30 Oct 2026","TotalReturnsIndex":"35043.45","NTR_Value":"30426.79"},
             {"RequestNumber":"TRI1","Index Name":"NIFTY 50","Date":"29 Oct 2026","TotalReturnsIndex":"34866.61"},
             {"RequestNumber":"TRI1","Index Name":"Nifty Next 50","Date":"29 Oct 2026","TotalReturnsIndex":"99999"},
             {"RequestNumber":"TRI1","Index Name":"Nifty 50","Date":"28 Oct 2026","TotalReturnsIndex":"-"}]
            """;

        var values = MutualFundBenchmarkSync.Parse(json, "NIFTY 50");

        Assert.Equal([(new DateOnly(2026, 10, 29), 34866.61m), (new DateOnly(2026, 10, 30), 35043.45m)], values);
        Assert.Empty(MutualFundBenchmarkSync.Parse("[]", "NIFTY 50"));
        Assert.Single(MutualFundBenchmarkSync.Parse("""{"d":"[{\"Index Name\":\"Nifty Bank\",\"Date\":\"01 Apr 2005\",\"TotalReturnsIndex\":\"5000.1\"}]"}""", "NIFTY BANK"));
    }

    [Fact]
    public void An_error_page_is_a_failure_not_an_empty_history()
    {
        var error = Assert.Throws<FormatException>(() =>
            MutualFundBenchmarkSync.Parse("""{"Message":"There was an error processing the request.","StackTrace":"","ExceptionType":""}""", "NIFTY 50"));
        Assert.Contains("There was an error", error.Message, StringComparison.Ordinal);
        Assert.Throws<FormatException>(() => MutualFundBenchmarkSync.Parse("<html>maintenance</html>", "NIFTY 50"));
    }

    // ---------- AMFI's TER ----------

    [Fact]
    public void The_ter_workbook_is_read_by_its_headers()
    {
        using var file = File.OpenRead(Path.Combine(Path.GetDirectoryName(MutualFundAnalyticsReferenceTests.Folder())!, "TER_102026_sample.xlsx"));

        var rows = MutualFundTerSync.Parse(file);

        // A trimmed copy of AMFI's October 2026 file: footnotes skipped, every data row read.
        Assert.Equal(87, rows.Count);
        var uti = rows.Single(r => r.NameKey == "uti nifty 50 index fund" && r.Date == new DateOnly(2026, 10, 30));
        Assert.Equal(("UTIM/O/O/EIN/00/03/0013", 0.37m, 0.25m), (uti.NsdlSchemeCode, uti.RegularTer, uti.DirectTer));
        Assert.Equal("Open Ended", uti.SchemeType);
        // The file escapes the apostrophe; the key drops it, as scheme names are matched.
        Assert.Contains(rows, r => r.SchemeName == "Axis Children's Fund" && r.NameKey == "axis childrens fund");
        // "NSDL Code not allocated" is shared by schemes: those rows are keyed by name instead.
        Assert.Contains(rows, r => r.NsdlSchemeCode == "name:uti unit linked insurance plan");
        Assert.Contains(rows, r => r.NsdlSchemeCode == "name:lic mf unit linked insurance scheme ulis");
        Assert.DoesNotContain(rows, r => r.SchemeName.StartsWith("Disclaimer", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("46325", 2026, 10, 30)]
    [InlineData("2026-10-30", 2026, 10, 30)]
    [InlineData("2026-10-30T00:00:00.000Z", 2026, 10, 30)]
    [InlineData("30-Oct-2026", 2026, 10, 30)]
    public void Ter_dates_read_as_excel_serials_or_text(string text, int y, int m, int d) =>
        Assert.Equal(new DateOnly(y, m, d), MutualFundTerSync.ParseDate(text));

    [Fact]
    public void Not_a_workbook_is_a_failure()
    {
        using var html = new MemoryStream("<html>busy</html>"u8.ToArray());
        Assert.Throws<InvalidDataException>(() => MutualFundTerSync.Parse(html));
    }

    [Fact]
    public void The_first_days_of_a_month_also_read_the_month_before()
    {
        Assert.Equal([new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1)], MutualFundTerSync.MonthsFor(new DateOnly(2026, 11, 5)));
        Assert.Equal([new DateOnly(2026, 11, 1)], MutualFundTerSync.MonthsFor(new DateOnly(2026, 11, 6)));
        Assert.Equal("11-2026", MutualFundTerSync.MonthParameter(new DateOnly(2026, 11, 1)));
    }

    // ---------- the ranking ----------

    [Fact]
    public void Equal_returns_share_a_rank_and_schemes_without_one_do_not_count()
    {
        var ranks = MutualFundStatsJob.Rank([(1, "a", 0.10), (2, "a", 0.12), (3, "a", 0.10), (4, "a", 0.05), (5, "a", null), (6, "b", 0.01)]);

        Assert.Equal((1, 4), ranks[2]);
        Assert.Equal((2, 4), ranks[1]);
        Assert.Equal((2, 4), ranks[3]);
        Assert.Equal((4, 4), ranks[4]);
        Assert.False(ranks.ContainsKey(5));
        Assert.Equal((1, 1), ranks[6]);
    }

    [Fact]
    public async Task The_ranking_uses_the_report_s_coverage_rules_and_ranks_within_a_peer_group_and_plan()
    {
        await using var db = Db();
        var asOf = new DateOnly(2026, 10, 30);
        void Scheme(int code, string plan, string category, DateOnly first, string option = "growth", bool active = true) =>
            db.MutualFundSchemes.Add(new MutualFundScheme
            {
                SchemeCode = code, SchemeName = $"Fund {code}", Amc = "AMC", Plan = plan, Option = option, Category = category,
                FirstNavDate = first, LastNavDate = asOf, IsActive = active,
            });
        void Nav(int code, DateOnly date, decimal nav) => db.MutualFundNavs.Add(new MutualFundNav { SchemeCode = code, Date = date, Nav = nav });

        const string largeCap = "Open Ended Schemes(Equity Scheme - Large Cap Fund)";
        const string largeCapRenamed = "Open Ended Schemes(Equity Schemes - Large Cap Fund)";
        var oneYear = new DateOnly(2025, 10, 30);
        Scheme(1, "direct", largeCap, new DateOnly(2020, 1, 1));
        Scheme(2, "direct", largeCapRenamed, new DateOnly(2020, 1, 1));        // same peer group, AMFI's other spelling
        Scheme(3, "direct", largeCap, new DateOnly(2025, 11, 3));             // too young for a year
        Scheme(4, "regular", largeCap, new DateOnly(2020, 1, 1));             // another plan: its own group
        Scheme(5, "direct", largeCap, new DateOnly(2020, 1, 1), option: "idcw");
        Scheme(6, "direct", "Open Ended Schemes(Income)", new DateOnly(2020, 1, 1));
        Scheme(7, "direct", largeCap, new DateOnly(2020, 1, 1));              // a gap: first NAV after the anniversary is 9 days on
        foreach (int code in new[] { 1, 2, 3, 4, 5, 6, 7 }) Nav(code, asOf, 120m);
        Nav(1, oneYear, 100m);
        Nav(2, new DateOnly(2025, 11, 3), 110m);                             // 30 Oct 2025 missing: 4 days on, within 7
        Nav(3, new DateOnly(2025, 11, 3), 100m);
        Nav(4, oneYear, 100m);
        Nav(5, oneYear, 100m);
        Nav(6, oneYear, 100m);
        Nav(7, new DateOnly(2025, 11, 8), 100m);
        await db.SaveChangesAsync();

        var rows = await new MutualFundStatsJob(db, NullLogger<MutualFundStatsJob>.Instance).ComputeAsync(asOf, DateTime.UtcNow, default);

        Assert.Equal([1, 2, 3, 4, 7], rows.Select(r => r.SchemeCode).OrderBy(c => c));
        var r1 = rows.Single(r => r.SchemeCode == 1);
        Assert.Equal(0.2, r1.Return1y!.Value, 9);
        Assert.Equal((1, 2), (r1.Rank1y, r1.Peers1y));
        var r2 = rows.Single(r => r.SchemeCode == 2);
        Assert.Equal(r1.PeerKey, r2.PeerKey);
        Assert.Equal(Math.Pow(120.0 / 110, 365.0 / 361) - 1, r2.Return1y!.Value, 9);
        Assert.Equal((2, 2), (r2.Rank1y, r2.Peers1y));
        Assert.Null(rows.Single(r => r.SchemeCode == 3).Return1y);
        Assert.Null(rows.Single(r => r.SchemeCode == 7).Return1y);
        Assert.Equal((1, 1), (rows.Single(r => r.SchemeCode == 4).Rank1y, rows.Single(r => r.SchemeCode == 4).Peers1y));
        Assert.All(rows, r => Assert.Null(r.Return3y));
    }

    internal static TradingDbContext Db() =>
        new(new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase($"mf-{Guid.NewGuid():N}").Options);

    internal sealed class CountingSettings : IProcessSettingsStore
    {
        public Dictionary<string, string> Values { get; } = new();
        public List<string> Writes { get; } = new();

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(Values.GetValueOrDefault(key));

        public Task SetAsync(string key, string value, string? updatedBy = null, CancellationToken cancellationToken = default)
        {
            Values[key] = value;
            Writes.Add(key);
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(Values.Remove(key));
        public Task<int?> GetPidAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetPidAsync(string key, int processId, string? updatedBy = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteIfPidAsync(string key, int processId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
