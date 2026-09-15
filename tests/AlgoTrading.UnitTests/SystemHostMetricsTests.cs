using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AlgoTrading.Api.Services;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The System page's host numbers: /proc parsing, the disk-growth estimate,
/// and the Drive archive manifest.
/// </summary>
/// <remarks>
/// The /proc samples are verbatim output of a Linux 6.x kernel (a Docker VM),
/// not text typed to fit the parser.
/// </remarks>
public class SystemHostMetricsTests
{
    private const string MemInfo = """
        MemTotal:        4010356 kB
        MemFree:          507816 kB
        MemAvailable:    2241684 kB
        Buffers:           35700 kB
        Cached:          2228412 kB
        SwapCached:            0 kB
        Active:          2461352 kB
        Inactive:         860980 kB
        Active(anon):    1476828 kB
        Inactive(anon):        0 kB
        SwapTotal:       1048572 kB
        SwapFree:        1048572 kB
        Zswap:                 0 kB
        Dirty:                40 kB
        CommitLimit:     3053748 kB
        Committed_AS:    3399696 kB
        VmallocTotal:   135288315904 kB
        HugePages_Total:       0
        HugePages_Free:        0
        Hugepagesize:       2048 kB
        """;

    [Fact]
    public void Memory_used_is_total_minus_available_not_minus_free()
    {
        var m = ProcText.ParseMemInfo(MemInfo)!;

        Assert.Equal(4010356L * 1024, m.TotalBytes);
        Assert.Equal(2241684L * 1024, m.AvailableBytes);
        Assert.Equal((4010356L - 2241684L) * 1024, m.UsedBytes);
        // 44.1%, where total - MemFree would have said a misleading 87%.
        Assert.Equal(44.1, Math.Round(m.UsedPercent, 1));
        Assert.Equal(1048572L * 1024, m.SwapTotalBytes);
        Assert.Equal(0, m.SwapUsedBytes);
        Assert.Equal(0, m.SwapUsedPercent);
    }

    [Fact]
    public void Swap_in_use_is_reported_and_a_box_without_swap_has_no_swap_percent()
    {
        var swapping = ProcText.ParseMemInfo(
            "MemTotal: 3900000 kB\nMemAvailable: 400000 kB\nSwapTotal: 2097148 kB\nSwapFree: 1572861 kB\n")!;
        Assert.Equal((2097148L - 1572861L) * 1024, swapping.SwapUsedBytes);
        Assert.Equal(25.0, Math.Round(swapping.SwapUsedPercent!.Value, 1));

        var none = ProcText.ParseMemInfo("MemTotal: 1000 kB\nMemAvailable: 500 kB\nSwapTotal: 0 kB\nSwapFree: 0 kB\n")!;
        Assert.Null(none.SwapUsedPercent);
    }

    [Fact]
    public void An_old_kernel_without_MemAvailable_falls_back_to_free_plus_caches()
    {
        var m = ProcText.ParseMemInfo("MemTotal: 1000 kB\nMemFree: 100 kB\nBuffers: 50 kB\nCached: 250 kB\n")!;
        Assert.Equal(400L * 1024, m.AvailableBytes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not meminfo at all")]
    [InlineData("MemFree: 100 kB\n")]
    public void Unreadable_meminfo_is_null_not_zero(string? text)
    {
        Assert.Null(ProcText.ParseMemInfo(text));
    }

    [Fact]
    public void Cpu_busy_share_comes_from_the_difference_between_two_stat_samples()
    {
        const string earlier = "cpu  42260 0 22469 5525688 5267 0 6554 0 0 0\ncpu0 5833 0 3135 688644 666 0 3970 0 0 0\n";
        const string later = "cpu  42560 0 22569 5526288 5367 0 6554 0 0 0\ncpu0 5900 0 3150 688700 670 0 3970 0 0 0\n";

        var a = ProcText.ParseCpuTimes(earlier)!.Value;
        var b = ProcText.ParseCpuTimes(later)!.Value;

        // idle includes iowait: 5525688 + 5267.
        Assert.Equal(5525688UL + 5267UL, a.Idle);
        Assert.Equal(42260UL + 22469 + 5525688 + 5267 + 6554, a.Total);

        // +300 user +100 system = 400 busy; +600 idle +100 iowait = 700 idle; 400 / 1100.
        Assert.Equal(36.4, Math.Round(ProcText.CpuBusyPercent(a, b)!.Value, 1));
    }

    [Fact]
    public void Cpu_share_is_null_when_no_time_passed_or_the_counters_went_backwards()
    {
        var a = new ProcText.CpuTimes(100, 1000);
        Assert.Null(ProcText.CpuBusyPercent(a, a));
        Assert.Null(ProcText.CpuBusyPercent(a, new ProcText.CpuTimes(50, 900)));
        Assert.Null(ProcText.ParseCpuTimes("intr 12345 0 0\nctxt 999\n"));
    }

    [Fact]
    public void Load_average_and_uptime_parse_the_kernels_own_lines()
    {
        var load = ProcText.ParseLoadAverage("0.08 0.14 0.12 1/357 11112\n")!;
        Assert.Equal(0.08, load.One);
        Assert.Equal(0.14, load.Five);
        Assert.Equal(0.12, load.Fifteen);

        Assert.Equal(7022.53, ProcText.ParseUptimeSeconds("7022.53 55256.88\n"));
        Assert.Null(ProcText.ParseUptimeSeconds("uptime"));
        Assert.Null(ProcText.ParseLoadAverage("0.08"));
    }

    [Fact]
    public void Disk_percent_matches_df_which_leaves_roots_reserved_blocks_out()
    {
        // 38 GB filesystem, 24 GB used, 12 GB available to ordinary users,
        // 2 GB reserved for root: df says 24 / (24 + 12) = 66.7%, not 24 / 38.
        const long gb = 1L << 30;
        double? pct = ProcText.DiskUsedPercent(38 * gb, 14 * gb, 12 * gb);
        Assert.Equal(66.7, Math.Round(pct!.Value, 1));
        Assert.Null(ProcText.DiskUsedPercent(0, 0, 0));
    }

    [Fact]
    public void One_day_chunks_land_on_their_day_and_longer_ones_are_spread()
    {
        var chunks = new[]
        {
            new StorageGrowth.ChunkSpan(new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc), 6_000),
            new StorageGrowth.ChunkSpan(new DateTime(2026, 9, 11, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc), 4_000),
            // Two days in one chunk: half each.
            new StorageGrowth.ChunkSpan(new DateTime(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc), 1_000),
        };

        var byDay = StorageGrowth.BytesPerDay(chunks);

        Assert.Equal(6_000, byDay[new DateOnly(2026, 9, 14)]);
        Assert.Equal(4_000, byDay[new DateOnly(2026, 9, 11)]);
        Assert.Equal(500, byDay[new DateOnly(2026, 9, 8)]);
        Assert.Equal(500, byDay[new DateOnly(2026, 9, 9)]);
    }

    [Fact]
    public void Daily_growth_averages_the_last_three_recording_days_and_skips_the_weekend()
    {
        const long mb = 1024 * 1024;
        var today = new DateOnly(2026, 9, 15); // Tuesday
        var byDay = new Dictionary<DateOnly, long>
        {
            [new DateOnly(2026, 9, 14)] = 6_000 * mb, // Mon
            [new DateOnly(2026, 9, 13)] = 0,          // Sun
            [new DateOnly(2026, 9, 12)] = 10,         // Sat: a stray write, under the floor
            [new DateOnly(2026, 9, 11)] = 3_000 * mb, // Fri
            [new DateOnly(2026, 9, 10)] = 3_000 * mb, // Thu
            [new DateOnly(2026, 9, 9)] = 90_000 * mb, // Wed: outside the sample of three
            [new DateOnly(2026, 9, 15)] = 99_000 * mb, // today, not closed yet
        };

        var estimate = StorageGrowth.EstimateDaily(byDay, today)!;

        Assert.Equal(4_000 * mb, estimate.BytesPerDay);
        Assert.Equal(new[] { new DateOnly(2026, 9, 14), new DateOnly(2026, 9, 11), new DateOnly(2026, 9, 10) }, estimate.Days);
    }

    [Fact]
    public void No_recorded_day_means_no_estimate_rather_than_a_disk_that_never_fills()
    {
        Assert.Null(StorageGrowth.EstimateDaily(new Dictionary<DateOnly, long>(), new DateOnly(2026, 9, 15)));
        Assert.Null(StorageGrowth.DaysUntilFull(10_000, null));
        Assert.Null(StorageGrowth.DaysUntilFull(10_000, 0));
        Assert.Null(StorageGrowth.DaysUntilFull(null, 100));
    }

    [Fact]
    public void Days_until_full_is_free_space_over_the_daily_rate()
    {
        const long gb = 1L << 30;
        // The 2026-09-15 morning: 8.5 GB free at ~6 GB a day is under a day and a half.
        Assert.Equal(8.5 / 6, StorageGrowth.DaysUntilFull((long)(8.5 * gb), 6 * gb)!.Value, 6);
    }

    [Fact]
    public async Task The_id_search_finds_the_first_row_of_a_day_across_gaps()
    {
        // Ids 1..100 on the 14th, 101..199 deleted, 200..300 on the 15th.
        var rows = Enumerable.Range(1, 100).Select(i => ((long)i, new DateTime(2026, 9, 14, 5, 0, 0, DateTimeKind.Utc)))
            .Concat(Enumerable.Range(200, 101).Select(i => ((long)i, new DateTime(2026, 9, 15, 5, 0, 0, DateTimeKind.Utc))))
            .ToList();
        int probes = 0;
        Task<(long Id, DateTime AtUtc)?> FirstFrom(long id)
        {
            probes++;
            foreach (var r in rows)
            {
                if (r.Item1 >= id) return Task.FromResult<(long, DateTime)?>(r);
            }
            return Task.FromResult<(long, DateTime)?>(null);
        }

        var boundary = new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(200, await StorageGrowth.FirstIdAtOrAfterAsync(1, 300, boundary, FirstFrom));
        Assert.True(probes <= 12, $"{probes} probes for 300 ids");

        // Before every row: the first id. After every row: max + 1.
        Assert.Equal(1, await StorageGrowth.FirstIdAtOrAfterAsync(1, 300, boundary.AddDays(-5), FirstFrom));
        Assert.Equal(301, await StorageGrowth.FirstIdAtOrAfterAsync(1, 300, boundary.AddDays(5), FirstFrom));
    }

    [Fact]
    public void The_archive_summary_names_the_newest_verified_day_and_counts_days_still_unproven()
    {
        var lines = new[]
        {
            """{"table":"live_ticks","day":"2026-09-11","rows":123,"bytes":456,"verified":true,"archived_utc":"2026-09-12T00:10:00+00:00","remote":"r"}""",
            // A failed attempt, then a verified retry: not outstanding.
            """{"table":"live_ticks","day":"2026-09-14","rows":9,"bytes":9,"verified":false,"archived_utc":"2026-09-15T00:10:00+00:00"}""",
            """{"table":"live_ticks","day":"2026-09-14","rows":9,"bytes":9,"verified":true,"archived_utc":"2026-09-15T00:20:00+00:00"}""",
            """{"table":"option_chain_snapshots","day":"2026-09-14","rows":5,"bytes":5,"verified":false,"archived_utc":"2026-09-15T00:30:00+00:00"}""",
            "",
            """{"table":"live_bars","day":"2026-09-1""",
        };

        var summary = ArchiveManifest.Summarize(lines);

        var ticks = summary.Tables.Single(t => t.Table == "live_ticks");
        Assert.Equal("2026-09-14", ticks.LastVerifiedDay);
        Assert.Equal(2, ticks.VerifiedDays);
        Assert.Equal(0, ticks.UnverifiedDays);
        Assert.Equal(new DateTime(2026, 9, 15, 0, 20, 0, DateTimeKind.Utc), ticks.LastArchivedUtc);

        var chain = summary.Tables.Single(t => t.Table == "option_chain_snapshots");
        Assert.Null(chain.LastVerifiedDay);
        Assert.Equal(1, chain.UnverifiedDays);

        Assert.Equal(1, summary.UnverifiedDays);
        Assert.Equal(1, summary.UnreadableLines);
    }

    [Fact]
    public void Policy_intervals_are_read_from_timescales_job_config()
    {
        Assert.Equal("2 days", TimescalePolicy.ReadInterval("""{"hypertable_id": 4, "compress_after": "2 days"}""", "compress_after"));
        Assert.Equal("90 days", TimescalePolicy.ReadInterval("""{"drop_after": "90 days", "hypertable_id": 4}""", "drop_after"));
        Assert.Null(TimescalePolicy.ReadInterval("""{"hypertable_id": 4}""", "drop_after"));
        Assert.Null(TimescalePolicy.ReadInterval(null, "drop_after"));
        Assert.Null(TimescalePolicy.ReadInterval("{broken", "drop_after"));
    }
}
