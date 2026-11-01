using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services.MutualFunds;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoTrading.UnitTests;

/// <summary>The reads behind api/MutualFunds: coverage, scheme search and the spot check against mfapi.in.</summary>
public class MutualFundQueriesTests
{
    /// <summary>An IST wall-clock time as UTC.</summary>
    private static DateTime Ist(int year, int month, int day, int hour, int minute = 0) =>
        new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc).AddMinutes(-330);

    [Fact]
    public void The_spot_check_compares_numbers_not_text()
    {
        const string json = """
            {"meta":{"scheme_code":120716},"data":[
              {"date":"30-10-2026","nav":"162.37510"},
              {"date":"29-10-2026","nav":"161.55690"},
              {"date":"28-10-2026","nav":"159.86830"},
              {"date":"27-10-2026","nav":"160.22760"}],"status":"SUCCESS"}
            """;
        var theirs = MutualFundQueries.ParseMfApi(json);
        var ours = new Dictionary<DateOnly, decimal>
        {
            [new DateOnly(2026, 10, 27)] = 160.2276m,
            [new DateOnly(2026, 10, 29)] = 161.5569m,
            [new DateOnly(2026, 10, 30)] = 162.3752m,
            [new DateOnly(2026, 10, 31)] = 162.3751m,
        };

        var result = MutualFundQueries.Compare(120716, "mfapi", ours, theirs);

        Assert.Equal(3, result.Compared);
        Assert.Equal(2, result.Matching);
        var diff = Assert.Single(result.Different);
        Assert.Equal((new DateOnly(2026, 10, 30), 162.3752m, 162.3751m), (diff.Date, diff.Ours, diff.Theirs));
        Assert.Equal(1, result.MissingHere);      // 28 Oct
        Assert.Equal(1, result.MissingThere);     // 31 Oct
    }

    [Fact]
    public async Task Coverage_counts_schemes_and_says_how_far_behind_the_navs_are()
    {
        await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase($"mf-{Guid.NewGuid():N}").Options);
        var oct28 = new DateOnly(2026, 10, 28);
        db.MutualFundSchemes.AddRange(
            new MutualFundScheme { SchemeCode = 120716, SchemeName = "UTI Nifty 50 Index Fund", Amc = "UTI Mutual Fund", Plan = "direct", Option = "growth", Category = "Index", IsActive = true, LastNavDate = oct28 },
            new MutualFundScheme { SchemeCode = 100822, SchemeName = "UTI Nifty 50 Index Fund", Amc = "UTI Mutual Fund", Plan = "regular", Option = "growth", Category = "Index", IsActive = true, LastNavDate = oct28 },
            new MutualFundScheme { SchemeCode = 130932, SchemeName = "Old Fund", Amc = "ICICI Prudential Mutual Fund", Plan = "unknown", Option = "unknown", Category = "Income", IsActive = false, LastNavDate = new DateOnly(2018, 7, 2) });
        db.MutualFundNavs.AddRange(
            new MutualFundNav { SchemeCode = 120716, Date = oct28, Nav = 160m },
            new MutualFundNav { SchemeCode = 100822, Date = oct28, Nav = 158m },
            new MutualFundNav { SchemeCode = 130932, Date = new DateOnly(2018, 7, 2), Nav = 12.4602m });
        await db.SaveChangesAsync();

        var settings = new MemorySettings();
        await MutualFundSettings.SaveSyncAsync(settings,
            new MutualFundSyncResult { Trigger = "evening", Ok = false, Message = "Failed: AMFI did not answer", StartedUtc = Ist(2026, 11, 2, 23, 30) }, default);
        var queries = new MutualFundQueries(db, new MarketReplayTests.TestCalendar(), settings, new MutualFundStatus(),
            Options.Create(new MutualFundsOptions()), new NoHttp())
        {
            Clock = () => Ist(2026, 11, 3, 8, 55),
        };

        var coverage = await queries.CoverageAsync(default);

        Assert.Equal(3, coverage.Schemes);
        Assert.Equal(2, coverage.ActiveSchemes);
        var unknownPlan = coverage.ByPlan.Single(x => x.Key == "unknown");
        Assert.Equal((1, 0), (unknownPlan.Schemes, unknownPlan.Active));
        Assert.Equal(3, coverage.ByPlan.Count);
        Assert.Equal(2, coverage.ByOption.Single(x => x.Key == "growth").Schemes);
        // Most schemes first.
        Assert.Equal("Index", coverage.ByCategory[0].Key);
        Assert.Equal(2, coverage.ByAmc.Single(x => x.Key == "UTI Mutual Fund").Active);
        Assert.Equal(3, coverage.Nav.Rows);
        Assert.Equal(new DateOnly(2018, 7, 2), coverage.Nav.FirstDate);
        Assert.Equal(oct28, coverage.Nav.LastDate);
        Assert.Equal(oct28, coverage.Nav.LatestFullDate);
        // At 08:55 on Tuesday 3 Nov Monday's NAVs are due; 29 Oct, 30 Oct and 2 Nov are missing.
        Assert.Equal(new DateOnly(2026, 11, 2), coverage.Nav.ExpectedDate);
        Assert.Equal(3, coverage.Nav.BusinessDaysBehind);
        Assert.False(coverage.LastSync!.Ok);
        Assert.Equal("idle", coverage.Backfill.State);
        Assert.Equal(0, coverage.Backfill.WindowsLeft);
    }

    [Fact]
    public async Task Schemes_are_found_by_name_code_or_isin_active_first()
    {
        await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase($"mf-{Guid.NewGuid():N}").Options);
        db.MutualFundSchemes.AddRange(
            new MutualFundScheme { SchemeCode = 120716, SchemeName = "UTI Nifty 50 Index Fund", Amc = "UTI Mutual Fund", Plan = "direct", Option = "growth", IsinGrowth = "INF789F01XA0", IsActive = true },
            new MutualFundScheme { SchemeCode = 100822, SchemeName = "UTI Nifty 50 Index Fund", Amc = "UTI Mutual Fund", Plan = "regular", Option = "growth", IsinGrowth = "INF789F01JN2", IsActive = true },
            new MutualFundScheme { SchemeCode = 999999, SchemeName = "Closed Nifty Fund", Amc = "Other Mutual Fund", Plan = "unknown", Option = "unknown", IsActive = false });
        await db.SaveChangesAsync();
        var queries = new MutualFundQueries(db, new MarketReplayTests.TestCalendar(), new MemorySettings(), new MutualFundStatus(),
            Options.Create(new MutualFundsOptions()), new NoHttp());

        var byName = await queries.SchemesAsync("nifty", null, null, null, 10, default);
        // Active first, then by name and code; the closed fund comes last although its name sorts first.
        Assert.Equal(new[] { 100822, 120716, 999999 }, byName.Select(s => s.SchemeCode));

        Assert.Equal(100822, Assert.Single(await queries.SchemesAsync("100822", null, null, null, 10, default)).SchemeCode);
        Assert.Equal(120716, Assert.Single(await queries.SchemesAsync("inf789f01xa0", null, null, null, 10, default)).SchemeCode);
        Assert.Equal(100822, Assert.Single(await queries.SchemesAsync("nifty", "regular", null, null, 10, default)).SchemeCode);
    }

    private sealed class MemorySettings : IProcessSettingsStore
    {
        private readonly Dictionary<string, string> _values = new();

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(_values.GetValueOrDefault(key));

        public Task SetAsync(string key, string value, string? updatedBy = null, CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(_values.Remove(key));
        public Task<int?> GetPidAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetPidAsync(string key, int processId, string? updatedBy = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteIfPidAsync(string key, int processId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("no network in tests");
    }
}
