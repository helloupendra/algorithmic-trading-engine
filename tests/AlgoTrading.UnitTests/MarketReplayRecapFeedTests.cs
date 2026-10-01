using System.Net.Http.Json;
using AlgoTrading.Api.Configuration;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Hubs;
using AlgoTrading.Api.Services.Replay;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.UseCases.LiveData;
using AlgoTrading.Contracts.LiveData;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using static AlgoTrading.UnitTests.MarketReplayTests;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The desk replay and a vendor's recap feed (TrueData's evening replay of a session) are never on together:
/// how the API knows a recap feed is running.
/// </summary>
public class MarketReplayRecapFeedTests
{
    private static readonly DateTime Now = new(2026, 10, 3, 14, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_recap_feed_heartbeating_now_is_running()
    {
        var (feeds, locks) = Feeds(Beat("python-dhan-feed", 3), Beat("python-truedata-recap", 10));

        Assert.Equal("python-truedata-recap", await feeds.RunningAsync(default));
        Assert.Empty(locks);
    }

    [Fact]
    public async Task A_recap_heartbeat_gone_quiet_counts_only_while_the_feed_still_holds_its_lock()
    {
        // The API was down: the feed's heartbeats were refused, but it is alive and still refreshes its Redis lock.
        var (quiet, asked) = Feeds(Beat("python-truedata-recap", 300));
        Assert.Null(await quiet.RunningAsync(default));
        Assert.Equal(["feed:truedata:lock"], asked);

        var (held, _) = Feeds(new[] { "feed:truedata:lock" }, Beat("python-truedata-recap", 300));
        Assert.Equal("python-truedata-recap", await held.RunningAsync(default));
    }

    [Fact]
    public async Task A_vendor_back_on_its_live_host_is_not_a_recap_and_live_feeds_never_are()
    {
        var (back, _) = Feeds(new[] { "feed:truedata:lock" }, Beat("python-truedata-recap", 40), Beat("python-truedata-feed", 5));
        Assert.Null(await back.RunningAsync(default));

        var (live, asked) = Feeds(Beat("python-dhan-feed", 2), Beat("python-live-ingestor", 4), Beat("python-truedata-feed", 9));
        Assert.Null(await live.RunningAsync(default));
        Assert.Empty(asked);
    }

    // ---------- the runners' quote calls (replay=true) ----------

    private const string Option = "NSE:NIFTY2610625000CE";
    private static readonly DateOnly DeskDay = new(2026, 9, 30);
    private static readonly DateOnly VendorDay = new(2026, 10, 1);

    [Fact]
    public async Task The_desk_replays_quotes_answer_only_the_recap_runners_of_its_own_day()
    {
        // TrueData's recap writes the live table; the desk replay's book holds another day.
        var (controller, book) = Quotes(live: 300m, replayed: 120m);

        Assert.Equal(120m, await Latest(controller, book, replay: true, DeskDay));       // a desk replay's runner
        Assert.Equal(300m, await Latest(controller, book, replay: true, VendorDay));     // the vendor recap's runner
        Assert.Equal(300m, await Latest(controller, book, replay: false, null));         // a live run, as always
        Assert.Equal(120m, await All(controller, book, DeskDay));
        Assert.Equal(300m, await All(controller, book, VendorDay));

        // A runner started before the day was sent asks with replay=true alone: answered as before.
        Assert.Equal(120m, await Latest(controller, book, replay: true, null));

        // No desk replay on: every recap runner reads the live table, its day given or not.
        book.End();
        Assert.Equal(300m, await Latest(controller, book, replay: true, DeskDay));
        Assert.Equal(300m, await Latest(controller, book, replay: true, null));
    }

    [Fact]
    public async Task The_runners_query_string_is_read_as_the_day_it_replays()
    {
        // Over HTTP, as a runner sends it: the day must bind, or every call would be a 400.
        var (_, book) = Quotes(live: 300m, replayed: 120m);
        var db = NewDb();
        db.LiveQuotesLatest.Add(new LiveQuoteLatest { Symbol = Option, LastTradedPrice = 300m, UpdatedUtc = DateTime.UtcNow,
            ExchangeTimestampUtc = DateTime.UtcNow, SourceKey = "truedata", DataType = "symbolUpdate", RawPayload = "{}" });
        db.SaveChanges();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(LiveDataController).Assembly)
            .ConfigureApplicationPartManager(parts =>
            {
                foreach (var provider in parts.FeatureProviders.OfType<ControllerFeatureProvider>().ToList()) parts.FeatureProviders.Remove(provider);
                parts.FeatureProviders.Add(new OnlyLiveData());
            });
        builder.Services.AddSingleton<ILiveDataService>(new LiveDataService(db, new NoCatalog(), new MarketSessionService(new OpenCalendar())));
        builder.Services.AddSingleton<IMarketReplayBook>(book);
        builder.Services.AddSingleton(RecapClockTests.Inert<IRedisPublisherService>.Create());
        builder.Services.AddSingleton(RecapClockTests.Inert<IProcessSettingsStore>.Create());
        foreach (var useCase in typeof(GetLatestQuoteUseCase).Assembly.GetTypes()
                     .Where(t => t.Namespace == typeof(GetLatestQuoteUseCase).Namespace && t.IsClass && !t.IsAbstract && t.Name.EndsWith("UseCase", StringComparison.Ordinal)))
        {
            builder.Services.AddTransient(useCase);
        }

        builder.Services.AddSingleton<LiveFeedSubscriptions>();
        builder.Services.AddSingleton(sp => new LiveTickDispatcher(sp.GetRequiredService<LiveFeedSubscriptions>(), new RecordingHubContext(),
            Options.Create(new LiveFeedOptions()), NullLogger<LiveTickDispatcher>.Instance));
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        using var client = app.GetTestClient();

        async Task<decimal?> Ltp(string query)
        {
            using var response = await client.GetAsync($"/api/LiveData/latest?symbol={Uri.EscapeDataString(Option)}&{query}");
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<LiveQuoteResponse>())!.LastTradedPrice;
        }

        Assert.Equal(120m, await Ltp("replay=true&recapDate=2026-09-30"));
        Assert.Equal(300m, await Ltp("replay=true&recapDate=2026-10-01"));
        Assert.Equal(120m, await Ltp("replay=true"));
    }

    private sealed class OnlyLiveData : ControllerFeatureProvider
    {
        protected override bool IsController(System.Reflection.TypeInfo typeInfo) => typeInfo.AsType() == typeof(LiveDataController);
    }

    private static (LiveDataController Controller, MarketReplayBook Book) Quotes(decimal live, decimal replayed)
    {
        var db = NewDb();
        db.LiveQuotesLatest.Add(new LiveQuoteLatest { Symbol = Option, LastTradedPrice = live, BidPrice = live - 0.5m, AskPrice = live + 0.5m,
            UpdatedUtc = DateTime.UtcNow, ExchangeTimestampUtc = DateTime.UtcNow, SourceKey = "truedata", DataType = "symbolUpdate", RawPayload = "{}" });
        db.SaveChanges();
        var book = new MarketReplayBook();
        book.Begin(DeskDay);
        book.Apply([new UpsertLiveTickRequest { Symbol = Option, LastTradedPrice = replayed, ExchangeTimestampUtc = IstTime.FromIst(DeskDay.ToDateTime(new TimeOnly(10, 0))) }]);

        var service = new LiveDataService(db, new NoCatalog(), new MarketSessionService(new OpenCalendar()));
        // Only the two quote reads are asked for.
        var controller = new LiveDataController(null!, null!, null!, new GetLatestQuoteUseCase(service), new GetAllLatestQuotesUseCase(service),
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);
        return (controller, book);
    }

    private static async Task<decimal?> Latest(LiveDataController controller, MarketReplayBook book, bool replay, DateOnly? recapDate) =>
        (await controller.GetLatest(Option, default, replay, book, recapDate)) is OkObjectResult { Value: LiveQuoteResponse quote } ? quote.LastTradedPrice : null;

    private static async Task<decimal?> All(LiveDataController controller, MarketReplayBook book, DateOnly recapDate) =>
        (await controller.GetAllLatest(default, replay: true, book, recapDate)) is OkObjectResult { Value: IEnumerable<LiveQuoteResponse> quotes }
            ? quotes.Single(q => q.Symbol == Option).LastTradedPrice
            : null;

    private static LiveIngestorStatus Beat(string source, int secondsAgo) => new()
    {
        SourceName = source, Status = "Running", LastHeartbeatUtc = Now.AddSeconds(-secondsAgo), UpdatedUtc = Now.AddSeconds(-secondsAgo),
    };

    private static (HeartbeatRecapFeeds Feeds, List<string> LocksAsked) Feeds(params LiveIngestorStatus[] beats) => Feeds([], beats);

    private static (HeartbeatRecapFeeds Feeds, List<string> LocksAsked) Feeds(string[] heldLocks, params LiveIngestorStatus[] beats)
    {
        TradingDbContext db = NewDb();
        db.LiveIngestorStatuses.AddRange(beats);
        db.SaveChanges();
        var clock = new FakeClock();
        clock.Set(Now);
        var asked = new List<string>();
        var feeds = new HeartbeatRecapFeeds(db, key =>
        {
            asked.Add(key);
            return Task.FromResult(heldLocks.Contains(key));
        }, clock);
        return (feeds, asked);
    }
}
