using AlgoTrading.Api.Services;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Domain.ValueObjects;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Providers.Dhan;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AlgoTrading.UnitTests;

public class OptionChainHealthTests
{
    private static readonly DateTime Now = new(2026, 11, 9, 5, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Dhan_is_healthy_with_the_python_backup_stopped()
    {
        using var f = new Fixture();
        await f.Capture("dhan", "NIFTY", Now.AddSeconds(-20));
        var result = await f.Service.ReadAsync(false);
        Assert.Equal("dhan", result.SelectedProvider);
        Assert.Equal("healthy", result.State);
        Assert.Equal("disabled", result.Writers[1].State);
        Assert.False(result.Writers[1].Required);
        Assert.Equal(20, result.Writers[0].CaptureLagSeconds);
    }

    [Fact]
    public async Task Another_provider_or_underlying_cannot_hide_a_stalled_selected_capture()
    {
        using var f = new Fixture(names: "NIFTY,BANKNIFTY");
        await f.Capture("dhan", "NIFTY", Now.AddSeconds(-20));
        await f.Capture("dhan", "BANKNIFTY", Now.AddMinutes(-10));
        await f.Capture("fyers", "BANKNIFTY", Now);
        var result = await f.Service.ReadAsync(false);
        Assert.Equal("stale", result.State);
        Assert.Equal("stale", result.Writers[0].Underlyings.Single(u => u.Underlying == "BANKNIFTY").State);
    }

    [Fact]
    public async Task No_capture_is_stale_while_open_but_idle_after_close()
    {
        using var f = new Fixture();
        Assert.Equal("stale", (await f.Service.ReadAsync(false)).State);
        f.Sessions.Open = false;
        Assert.Equal("idle", (await f.Service.ReadAsync(false)).State);
    }

    [Fact]
    public async Task Switching_off_dhan_selects_fyers_without_restarting_health_service()
    {
        using var f = new Fixture();
        await f.Capture("fyers", "NIFTY", Now.AddSeconds(-5));
        Assert.Equal("dhan", (await f.Service.ReadAsync(false)).SelectedProvider);
        await f.State.SwitchAsync(false, "test");
        var result = await f.Service.ReadAsync(true);
        Assert.Equal("fyers", result.SelectedProvider);
        Assert.Equal("healthy", result.State);
        Assert.Equal("disabled", result.Writers[0].State);
        Assert.Equal("stopped", (await f.Service.ReadAsync(false)).State);
    }

    [Fact]
    public async Task Both_writers_are_a_conflict_even_when_captures_are_fresh()
    {
        using var f = new Fixture();
        await f.Capture("dhan", "NIFTY", Now);
        await f.Capture("fyers", "NIFTY", Now);
        var result = await f.Service.ReadAsync(true);
        Assert.True(result.ConflictingWriters);
        Assert.Equal("conflict", result.State);
    }

    [Fact]
    public async Task Recent_failed_attempt_is_visible_before_capture_becomes_stale()
    {
        using var f = new Fixture();
        await f.Capture("dhan", "NIFTY", Now.AddSeconds(-20));
        f.State.Record(new("NIFTY", Now.AddSeconds(-5), "failed", "no contracts"));
        var result = await f.Service.ReadAsync(false);
        Assert.Equal("degraded", result.State);
        Assert.Equal("failed", result.Writers[0].Underlyings[0].LastOutcome);
    }

    [Fact]
    public async Task Closed_equity_markets_do_not_hide_stalled_open_commodities()
    {
        using var f = new Fixture(names: "NIFTY,CRUDEOIL");
        f.Sessions.OnlyMcx = true;
        var result = await f.Service.ReadAsync(false);
        Assert.Equal("stale", result.State);
        Assert.Equal("idle", result.Writers[0].Underlyings.Single(u => u.Underlying == "NIFTY").State);
        Assert.True(result.Writers[0].Underlyings.Single(u => u.Underlying == "CRUDEOIL").MarketOpen);
    }

    [Fact]
    public async Task Capture_timestamp_query_filters_provider_and_underlying()
    {
        using var f = new Fixture();
        await f.Capture("fyers", "NIFTY", Now.AddMinutes(-5));
        await f.Capture("dhan", "NIFTY", Now);
        await f.Capture("fyers", "BANKNIFTY", Now.AddMinutes(1));
        var chain = new OptionChainService(f.Db);
        Assert.Equal(Now.AddMinutes(-5), await chain.GetLastCaptureUtcAsync(default, "fyers", "NIFTY"));
        Assert.Null(await chain.GetLastCaptureUtcAsync(default, "dhan", "SENSEX"));
        Assert.Null(await chain.GetLastCaptureUtcAsync(default, "fyers", "NIFTY", Now.AddMinutes(-1)));
    }

    [Fact]
    public async Task Empty_underlying_configuration_is_not_healthy()
    {
        using var f = new Fixture(names: "");
        Assert.Equal("unconfigured", (await f.Service.ReadAsync(false)).State);
    }

    [Fact]
    public async Task Recovery_capture_supersedes_an_older_failed_attempt()
    {
        using var f = new Fixture();
        f.State.Record(new("NIFTY", Now.AddSeconds(-20), "failed", "old failure"));
        await f.Capture("dhan", "NIFTY", Now.AddSeconds(-5));
        Assert.Equal("healthy", (await f.Service.ReadAsync(false)).State);
    }

    private sealed class Fixture : IDisposable
    {
        public TradingDbContext Db { get; } = new(new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();
        public Sessions Sessions { get; } = new();
        public DhanChainPollerState State { get; }
        public OptionChainHealthService Service { get; }
        public Fixture(string names = "NIFTY")
        {
            var options = Options.Create(new DhanSettings { ChainPoller = new() { Enabled = true, Underlyings = names } });
            var clock = new Clock();
            State = new(options, _services.GetRequiredService<IServiceScopeFactory>(), NullLogger<DhanChainPollerState>.Instance, clock);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CHAIN_UNDERLYINGS"] = "NIFTY" }).Build();
            Service = new(new OptionChainService(Db), State, options, Sessions, config, clock);
        }
        public async Task Capture(string source, string underlying, DateTime captured)
        {
            Db.OptionChainSnapshots.Add(new() { Underlying = underlying, SourceKey = source, CapturedUtc = captured,
                ExpiryDate = new(2026, 11, 10), Symbol = underlying + "CE", OptionType = "CE" });
            await Db.SaveChangesAsync();
        }
        public void Dispose() { Db.Dispose(); _services.Dispose(); }
    }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Sessions : IMarketSessionService
    {
        public bool Open { get; set; } = true;
        public bool OnlyMcx { get; set; }
        public bool IsMarketOpen(DateTime utcNow, string exchange, string segment) => Open && (!OnlyMcx || exchange == "MCX");
        public MarketSessionInfo GetSessionInfo(DateTime utcNow, string exchange, string segment) => throw new NotSupportedException();
        public DateTime GetNextMarketOpenUtc(DateTime utcNow, string exchange, string segment) => throw new NotSupportedException();
    }
}
