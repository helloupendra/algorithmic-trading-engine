using System.Security.Claims;
using System.Text.Json;
using AlgoTrading.Api.Configuration;
using AlgoTrading.Api.Hubs;
using AlgoTrading.Contracts.LiveData;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using AlgoTrading.Application.Interfaces;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Each browser is sent the prices it asked for, coalesced, and nothing else.
/// </summary>
/// <remarks>
/// Until 28 Sep every tick batch went to every signed-in browser
/// (<c>Clients.All</c>): a Positions page following three contracts was sent
/// every strike of every chain on the feed, forty times a second.
/// </remarks>
public class LiveFeedHubTests
{
    private const string Nifty = "NSE:NIFTY50-INDEX";
    private const string BankNifty = "NSE:NIFTYBANK-INDEX";
    private const string Sbin = "NSE:SBIN-EQ";

    // ------------------------------------------------------------ registry --

    [Fact]
    public void Symbols_are_trimmed_blanks_dropped_and_case_ignored()
    {
        var subs = new LiveFeedSubscriptions();
        subs.Connect("c1", 7, isAdmin: false);

        var result = subs.Subscribe("c1", new[] { Nifty, " nse:nifty50-index ", "", "   ", null, Sbin });

        Assert.Equal(new SubscribeResult(true, 2), result);
        Assert.Equal(1, subs.Unsubscribe("c1", new[] { "nse:sbin-eq" }));
        Assert.Equal(1, subs.Unsubscribe("c1", new[] { "NSE:NOT-FOLLOWED", null }));
        Assert.Equal(0, subs.Unsubscribe("c1", new[] { Nifty.ToLowerInvariant() }));
    }

    [Fact]
    public void A_call_past_the_cap_adds_nothing()
    {
        var subs = new LiveFeedSubscriptions();
        subs.Connect("c1", 7, isAdmin: false);
        var first = Enumerable.Range(0, LiveFeedSubscriptions.MaxSymbolsPerConnection - 1).Select(i => $"NSE:S{i}-EQ").ToArray();
        Assert.Equal(new SubscribeResult(true, 399), subs.Subscribe("c1", first));

        // Two new ones would make 401: neither is added.
        Assert.Equal(new SubscribeResult(false, 399), subs.Subscribe("c1", new[] { "NSE:NEW1-EQ", "NSE:NEW2-EQ" }));
        Assert.Equal(399, subs.CountFor("c1"));

        // One already followed and one new is 400: accepted.
        Assert.Equal(new SubscribeResult(true, 400), subs.Subscribe("c1", new[] { "nse:s0-eq", "NSE:NEW1-EQ" }));
    }

    [Fact]
    public void The_hub_refuses_past_the_cap_with_a_message_and_adds_nothing()
    {
        var subs = new LiveFeedSubscriptions();
        var hub = Hub(subs, "c1", Trader(7));
        subs.Connect("c1", 7, isAdmin: false);
        hub.Subscribe(Enumerable.Range(0, 400).Select(i => $"NSE:S{i}-EQ").ToArray());

        var refused = Assert.Throws<HubException>(() => hub.Subscribe(new[] { Nifty }));

        Assert.Contains("at most 400 symbols", refused.Message);
        Assert.Equal(400, subs.CountFor("c1"));
        Assert.Equal(400, hub.Subscribe(new[] { "NSE:S1-EQ" }));
    }

    [Fact]
    public async Task A_closed_connection_is_forgotten_and_sent_nothing()
    {
        var subs = new LiveFeedSubscriptions();
        var hubContext = new RecordingHubContext();
        var dispatcher = Dispatcher(subs, hubContext);
        var hub = Hub(subs, "c1", Trader(7));
        await hub.OnConnectedAsync();
        hub.Subscribe(new[] { Nifty });

        await hub.OnDisconnectedAsync(null);
        dispatcher.Enqueue(Tick(Nifty, 100m));

        Assert.Equal(0, subs.ConnectionCount);
        Assert.Equal(0, subs.CountFor("c1"));
        Assert.Equal(0, dispatcher.Flush());
        Assert.Empty(hubContext.All);
    }

    // ---------------------------------------------------------- dispatcher --

    [Fact]
    public void The_last_tick_of_a_symbol_wins_and_arrives_once()
    {
        var (subs, hubContext, dispatcher) = Desk();
        Follow(subs, "c1", Nifty);

        dispatcher.Enqueue(Tick(Nifty, 100m));
        dispatcher.Enqueue(new[] { Tick(Nifty, 101m), Tick("nse:nifty50-index", 102m) });

        Assert.Equal(1, dispatcher.Flush());
        var message = Assert.Single(hubContext.TicksTo("c1"));
        var tick = Assert.Single(message);
        Assert.Equal(102m, tick.LastTradedPrice);
    }

    // At the open the feed posts from six threads at once, and a batch holding
    // 09:15:07 can reach the API after one holding 09:15:08. The quote table
    // refuses the older price; the push used to send it anyway.

    [Fact]
    public void An_older_tick_that_arrives_second_in_the_same_flush_is_not_pushed()
    {
        var (subs, hubContext, dispatcher) = Desk();
        Follow(subs, "c1", Nifty);

        dispatcher.Enqueue(Tick(Nifty, 146.5m, At(9, 15, 8)));
        dispatcher.Enqueue(Tick(Nifty, 142.0m, At(9, 15, 7)));
        dispatcher.Flush();

        Assert.Equal(146.5m, Assert.Single(Assert.Single(hubContext.TicksTo("c1"))).LastTradedPrice);
    }

    [Fact]
    public void An_older_tick_that_arrives_after_the_newer_one_was_pushed_is_not_pushed()
    {
        var (subs, hubContext, dispatcher) = Desk();
        Follow(subs, "c1", Nifty);

        dispatcher.Enqueue(Tick(Nifty, 146.5m, At(9, 15, 8)));
        Assert.Equal(1, dispatcher.Flush());
        dispatcher.Enqueue(new[] { Tick(Nifty, 142.0m, At(9, 15, 7)) });
        Assert.Equal(0, dispatcher.Flush());

        // A tick at the same second, or later, is news again.
        dispatcher.Enqueue(Tick(Nifty, 147.0m, At(9, 15, 8)));
        dispatcher.Flush();
        Assert.Equal(new decimal?[] { 146.5m, 147.0m }, hubContext.TicksTo("c1").Select(m => Assert.Single(m).LastTradedPrice));
    }

    [Fact]
    public void Only_that_symbol_is_held_back_and_a_replay_or_an_unstamped_tick_never_is()
    {
        var (subs, hubContext, dispatcher) = Desk();
        Follow(subs, "c1", Nifty, BankNifty, Sbin);
        dispatcher.Enqueue(new[] { Tick(Nifty, 25_000m, At(15, 29, 59)), Tick(BankNifty, 57_000m, At(15, 29, 59)) });
        dispatcher.Flush();
        hubContext.Clear();

        // The evening recap replays the session behind the stamps the live
        // session already wrote; refusing it would freeze the price at the close.
        var replay = Tick(Nifty, 24_900m, At(9, 20));
        replay.IsReplay = true;
        var unstamped = Tick(Sbin, 801m, null);
        dispatcher.Enqueue(new[] { replay, Tick(BankNifty, 56_000m, At(9, 20)), unstamped });
        dispatcher.Flush();

        var pushed = Assert.Single(hubContext.TicksTo("c1")).ToDictionary(x => x.Symbol, x => x.LastTradedPrice);
        Assert.Equal(new Dictionary<string, decimal?> { [Nifty] = 24_900m, [Sbin] = 801m }, pushed);

        // The replay's stamp is the one now held, as the quote table holds it.
        dispatcher.Enqueue(Tick(Nifty, 24_910m, At(9, 21)));
        dispatcher.Flush();
        Assert.Equal(24_910m, Assert.Single(hubContext.TicksTo("c1")[1]).LastTradedPrice);
    }

    [Fact]
    public void Each_connection_gets_only_its_own_symbols_in_the_feeds_spelling()
    {
        var (subs, hubContext, dispatcher) = Desk();
        Follow(subs, "c1", Nifty.ToLowerInvariant());
        Follow(subs, "c2", BankNifty, Sbin);

        dispatcher.Enqueue(new[] { Tick(Nifty, 25_000m), Tick(BankNifty, 57_000m), Tick("NSE:RELIANCE-EQ", 1_400m) });

        Assert.Equal(2, dispatcher.Flush());
        Assert.Equal(new[] { Nifty }, Assert.Single(hubContext.TicksTo("c1")).Select(x => x.Symbol));
        Assert.Equal(new[] { BankNifty }, Assert.Single(hubContext.TicksTo("c2")).Select(x => x.Symbol));
    }

    [Fact]
    public void Subscribe_all_gets_every_symbol_that_changed()
    {
        var (subs, hubContext, dispatcher) = Desk();
        Follow(subs, "c1", Nifty);
        subs.Connect("everything", 1, isAdmin: true);
        subs.SetAll("everything", true);

        dispatcher.Enqueue(new[] { Tick(Nifty, 25_000m), Tick(BankNifty, 57_000m), Tick(Sbin, 800m) });
        dispatcher.Flush();

        Assert.Equal(
            new[] { Nifty, BankNifty, Sbin },
            Assert.Single(hubContext.TicksTo("everything")).Select(x => x.Symbol).OrderBy(x => x, StringComparer.Ordinal));

        subs.SetAll("everything", false);
        dispatcher.Enqueue(Tick(Sbin, 801m));
        dispatcher.Flush();
        Assert.Single(hubContext.TicksTo("everything"));
    }

    [Fact]
    public void Nothing_new_sends_nothing()
    {
        var (subs, hubContext, dispatcher) = Desk();
        Follow(subs, "c1", Nifty);
        Follow(subs, "c2", Sbin);

        dispatcher.Enqueue(Tick(Nifty, 100m));
        dispatcher.Flush();

        Assert.Empty(hubContext.TicksTo("c2"));

        hubContext.Clear();
        Assert.Equal(0, dispatcher.Flush());
        Assert.Empty(hubContext.All);
    }

    [Fact]
    public void A_failing_browser_does_not_stop_the_others_or_its_own_next_flush()
    {
        var (subs, hubContext, dispatcher) = Desk();
        Follow(subs, "bad", Nifty);
        Follow(subs, "good", Nifty);
        bool failing = true;
        hubContext.OnSend = target => failing && target == "client:bad"
            ? throw new IOException("connection reset")
            : Task.CompletedTask;

        dispatcher.Enqueue(Tick(Nifty, 100m));
        Assert.Equal(2, dispatcher.Flush());

        Assert.Single(hubContext.TicksTo("good"));
        Assert.Empty(hubContext.TicksTo("bad"));

        failing = false;
        dispatcher.Enqueue(Tick(Nifty, 101m));
        dispatcher.Flush();
        Assert.Equal(101m, Assert.Single(Assert.Single(hubContext.TicksTo("bad"))).LastTradedPrice);
    }

    [Fact]
    public async Task A_slow_browser_gets_one_message_at_a_time_and_then_the_latest()
    {
        var (subs, hubContext, dispatcher) = Desk();
        Follow(subs, "slow", Nifty);
        Follow(subs, "fast", Nifty);
        var release = new TaskCompletionSource();
        hubContext.OnSend = target => target == "client:slow" ? release.Task : Task.CompletedTask;

        dispatcher.Enqueue(Tick(Nifty, 100m));
        Assert.Equal(2, dispatcher.Flush());
        dispatcher.Enqueue(Tick(Nifty, 101m));
        Assert.Equal(1, dispatcher.Flush());                      // the slow one's first is still being written
        dispatcher.Enqueue(Tick(Nifty, 102m));
        Assert.Equal(1, dispatcher.Flush());

        release.SetResult();
        var until = DateTime.UtcNow.AddSeconds(5);
        while (hubContext.TicksTo("slow").Count < 2 && DateTime.UtcNow < until)
        {
            dispatcher.Flush();
            await Task.Delay(10);
        }

        Assert.Equal(new decimal?[] { 100m, 101m, 102m }, hubContext.TicksTo("fast").Select(m => Assert.Single(m).LastTradedPrice));
        Assert.Equal(new decimal?[] { 100m, 102m }, hubContext.TicksTo("slow").Select(m => Assert.Single(m).LastTradedPrice));
    }

    [Fact]
    public void Enqueue_skips_blank_symbols_and_never_throws()
    {
        var (subs, hubContext, dispatcher) = Desk();
        subs.Connect("everything", 1, isAdmin: true);
        subs.SetAll("everything", true);

        dispatcher.Enqueue(new UpsertLiveTickRequest[] { Tick("", 1m), Tick("  ", 2m), null!, Tick(Nifty, 3m) });
        dispatcher.Enqueue((IEnumerable<UpsertLiveTickRequest>)null!);
        dispatcher.Flush();

        Assert.Equal(new[] { Nifty }, Assert.Single(hubContext.TicksTo("everything")).Select(x => x.Symbol));
    }

    [Fact]
    public void A_pushed_price_has_exactly_the_eight_fields_the_console_reads()
    {
        var tick = Tick(Nifty, 100m);
        tick.RawPayload = "{\"the broker's whole message\":true}";

        // SignalR's JSON protocol: the web defaults (camelCase), which the pinned names must not depend on.
        var json = JsonSerializer.SerializeToElement(LiveTickPush.From(tick), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(
            new[] { "symbol", "lastTradedPrice", "bidPrice", "askPrice", "volume", "openInterest", "impliedVolatility", "exchangeTimestampUtc" },
            json.EnumerateObject().Select(x => x.Name));
    }

    [Fact]
    public void The_push_interval_has_a_floor()
    {
        var subs = new LiveFeedSubscriptions();
        var dispatcher = new LiveTickDispatcher(subs, new RecordingHubContext(),
            Options.Create(new LiveFeedOptions { PushIntervalMs = 0 }), NullLogger<LiveTickDispatcher>.Instance);

        Assert.Equal(TimeSpan.FromMilliseconds(LiveFeedOptions.MinPushIntervalMs), dispatcher.PushInterval);
        Assert.Equal(TimeSpan.FromMilliseconds(250), Dispatcher(subs, new RecordingHubContext()).PushInterval);
    }

    // ---------------------------------------------------- subscribe to all --

    [Theory]
    [InlineData(AdminId, true)]
    [InlineData(GrantedTrader, true)]
    [InlineData(PlainTrader, false)]
    [InlineData(DisabledGrantedTrader, false)]
    public async Task Subscribe_all_is_for_admins_and_the_market_data_grant(long userId, bool allowed)
    {
        await using var db = Users();
        var subs = new LiveFeedSubscriptions();
        var hub = Hub(subs, "c1", userId == AdminId ? Admin(userId) : Trader(userId), db);
        await hub.OnConnectedAsync();

        Assert.Equal(allowed, await hub.SubscribeAll());
        Assert.Equal(allowed, subs.IsAll("c1"));

        Assert.True(hub.UnsubscribeAll());
        Assert.False(subs.IsAll("c1"));
    }

    // ------------------------------------------------------------- helpers --

    private const long AdminId = 1;
    private const long GrantedTrader = 7;
    private const long PlainTrader = 8;
    private const long DisabledGrantedTrader = 9;

    private static (LiveFeedSubscriptions, RecordingHubContext, LiveTickDispatcher) Desk()
    {
        var subs = new LiveFeedSubscriptions();
        var hubContext = new RecordingHubContext();
        return (subs, hubContext, Dispatcher(subs, hubContext));
    }

    private static LiveTickDispatcher Dispatcher(LiveFeedSubscriptions subs, RecordingHubContext hubContext)
        => new(subs, hubContext, Options.Create(new LiveFeedOptions()), NullLogger<LiveTickDispatcher>.Instance);

    private static void Follow(LiveFeedSubscriptions subs, string connectionId, params string[] symbols)
    {
        subs.Connect(connectionId, 7, isAdmin: false);
        Assert.True(subs.Subscribe(connectionId, symbols).Accepted);
    }

    private static UpsertLiveTickRequest Tick(string symbol, decimal ltp)
        => Tick(symbol, ltp, new DateTime(2026, 9, 28, 4, 0, 0, DateTimeKind.Utc));

    private static UpsertLiveTickRequest Tick(string symbol, decimal ltp, DateTime? exchangeUtc) => new()
    {
        Symbol = symbol,
        LastTradedPrice = ltp,
        BidPrice = ltp - 0.05m,
        AskPrice = ltp + 0.05m,
        Volume = 1_000,
        ExchangeTimestampUtc = exchangeUtc
    };

    /// <summary>IST wall time on 28 Sep 2026, in UTC.</summary>
    private static DateTime At(int h, int m, int s = 0) => new DateTime(2026, 9, 28, h, m, s, DateTimeKind.Utc).AddMinutes(-330);

    private static LiveFeedHub Hub(LiveFeedSubscriptions subs, string connectionId, ClaimsPrincipal user, TradingDbContext? db = null)
    {
        var users = db is null
            ? RecapClockTests.Inert<IUserAdminService>.Create()
            : new UserAdminService(db, new PasswordHasher<AppUser>(), RecapClockTests.Inert<ITokenValidityService>.Create(),
                NullLogger<UserAdminService>.Instance);

        return new LiveFeedHub(subs, users)
        {
            Context = new TestCallerContext(connectionId, user),
            Groups = new RecordingHubContext().Groups
        };
    }

    private static TradingDbContext Users()
    {
        var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase($"live-feed-users-{Guid.NewGuid():N}")
            .Options);
        db.AppUsers.AddRange(
            new AppUser { Id = AdminId, UserName = "admin", Role = UserRoles.Admin, IsActive = true },
            new AppUser { Id = GrantedTrader, UserName = "coderforchange", Role = UserRoles.Trader, IsActive = true },
            new AppUser { Id = PlainTrader, UserName = "mallory", Role = UserRoles.Trader, IsActive = true },
            new AppUser { Id = DisabledGrantedTrader, UserName = "gone", Role = UserRoles.Trader, IsActive = false });
        db.UserModuleGrants.AddRange(
            new UserModuleGrant { UserId = GrantedTrader, ModuleKey = PlatformModules.MarketData, GrantedBy = "admin" },
            new UserModuleGrant { UserId = PlainTrader, ModuleKey = PlatformModules.Strategies, GrantedBy = "admin" },
            new UserModuleGrant { UserId = DisabledGrantedTrader, ModuleKey = PlatformModules.MarketData, GrantedBy = "admin" });
        db.SaveChanges();
        return db;
    }

    internal static ClaimsPrincipal Trader(long id) => new(new ClaimsIdentity(
        new[] { new Claim(ClaimTypes.NameIdentifier, id.ToString()), new Claim(ClaimTypes.Name, $"user{id}") }, "Test"));

    internal static ClaimsPrincipal Admin(long id) => new(new ClaimsIdentity(
        new[]
        {
            new Claim(ClaimTypes.NameIdentifier, id.ToString()), new Claim(ClaimTypes.Name, "admin"),
            new Claim(ClaimTypes.Role, UserRoles.Admin)
        }, "Test"));
}
