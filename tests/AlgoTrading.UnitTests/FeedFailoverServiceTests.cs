using AlgoTrading.Api.Configuration;
using AlgoTrading.Api.Services;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Domain.ValueObjects;
using AlgoTrading.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The automatic switch from a silent Dhan feed to FYERS. On 24 Sep 2026 the
/// last Dhan tick was 11:27:36 and FYERS took over at 11:34:06 only because a
/// person switched by hand; unattended, 26 runs would have been blind until
/// the 15:30 square-off. It ships as a dry run (owner, 27 Sep), so the dry
/// run's one message and log line are pinned as tightly as the switch.
/// </summary>
public class FeedFailoverServiceTests
{
    // Monday 28 Sep 2026, 11:30:00 IST.
    private static readonly DateTimeOffset Monday1130Ist = new(2026, 9, 28, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Monday = new(2026, 9, 28);

    [Fact]
    public async Task FreshTicks_NoAction()
    {
        var h = new Harness();
        h.Ticks.Ages(nse: 2, bse: 2, mcx: 1);

        await h.ChecksAsync(8);

        Assert.Empty(h.Notifier.Sent);
        Assert.Equal(0, h.Checks.FyersCalls);
        Assert.Equal(0, h.Feeds.DhanStops);
        Assert.DoesNotContain(h.Log.Lines, l => l.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task StaleWithinGrace_NoAction()
    {
        // Dhan was restarted 100 s ago: still connecting and subscribing.
        var h = new Harness();
        h.Feeds.DhanStartedUtc = h.Clock.Now.UtcDateTime.AddSeconds(-100);
        h.Ticks.Ages(nse: 400, bse: 400, mcx: 1);

        await h.ChecksAsync(5);   // up to 160 s

        Assert.Empty(h.Notifier.Sent);
        Assert.Equal(0, h.Checks.FyersCalls);
        Assert.Equal(0, h.Feeds.DhanStops);
    }

    [Fact]
    public async Task StaleOnce_NoAction()
    {
        // One stale check is a blip the feed's own reconnect is still fixing.
        var h = new Harness();
        h.Ticks.Ages(nse: 160, bse: 160, mcx: 1);
        await h.CheckAsync();

        h.Ticks.Ages(nse: 1, bse: 1, mcx: 1);
        await h.ChecksAsync(3);

        Assert.Empty(h.Notifier.Sent);
        Assert.Equal(0, h.Checks.FyersCalls);
        Assert.Contains(h.Log.Lines, l => l.Message.StartsWith("FEED FAILOVER: Dhan ticks stale on one check"));
        Assert.DoesNotContain(h.Log.Lines, l => l.Message.StartsWith("FEED FAILOVER: Dhan feed silent"));
    }

    [Fact]
    public async Task StaleTwice_FyersGood_DryRun_LogsAndAlertsOnce_NoSwitch()
    {
        var h = new Harness(dryRun: true);
        h.Ticks.Ages(nse: 212, bse: 214, mcx: 3);

        await h.ChecksAsync(12);   // three minutes of the same silence

        var sent = Assert.Single(h.Notifier.Sent);
        Assert.Equal(NotificationCategory.Process, sent.Category);   // → the System channel
        Assert.Equal(TelegramChannel.System, AlertSubscriberService.ChannelFor(sent.Category.ToString().ToLowerInvariant()));
        Assert.Contains("dry run", sent.Title);
        Assert.Contains("NSE silent", sent.Message);
        Assert.Contains("DH-901", sent.Message);                      // Dhan's own words
        Assert.Contains("FeedFailover:DryRun=false", sent.Message);

        var wouldSwitch = h.Log.Lines.Where(l => l.Message.StartsWith("FEED FAILOVER (dry run) would switch: ")).ToList();
        var line = Assert.Single(wouldSwitch);
        Assert.Contains("NSE silent", line.Message);
        Assert.Contains("MCX", line.Message);
        Assert.EndsWith("at 11:30:15 IST", line.Message);   // api.log lines carry no time of their own

        Assert.Equal(0, h.Feeds.DhanStops);
        Assert.Equal(0, h.Feeds.FyersStarts);
        Assert.Empty(h.Store.Values);   // a dry run writes nothing
    }

    [Fact]
    public async Task StaleTwice_FyersGood_NotDryRun_SwitchesOnce_OneAlert()
    {
        var h = new Harness(dryRun: false);
        h.Ticks.Ages(nse: 212, bse: 214, mcx: 3);

        await h.ChecksAsync(2);

        Assert.Equal(1, h.Feeds.DhanStops);
        Assert.Equal(1, h.Feeds.FyersStarts);
        Assert.StartsWith("11:30:15 IST: switched Dhan → FYERS", h.Store.Values[SystemSettingKeys.FeedFailover(Monday)]);
        Assert.Empty(h.Notifier.Sent);   // the one alert waits for the verdict on FYERS

        // FYERS ticks 10 s after the switch.
        var switchedAt = h.Clock.Now.UtcDateTime;
        h.Clock.Advance(15);
        h.Ticks.Set(FeedFailoverService.Nse, switchedAt.AddSeconds(10), source: "fyers");
        await h.CheckAsync();

        var alert = Assert.Single(h.Notifier.Sent);
        Assert.Equal(NotificationSeverity.Error, alert.Severity);
        Assert.Equal("Feed switched from Dhan to FYERS", alert.Title);
        Assert.Contains("NSE silent", alert.Message);
        Assert.Contains("DH-901", alert.Message);
        Assert.Contains("10 s after the switch", alert.Message);
        Assert.Contains(h.Log.Lines, l => l.Message.StartsWith("FEED FAILOVER verified: fresh NSE tick 10s after the switch"));

        // Later: FYERS runs, Dhan does not — nothing more, ever, today.
        await h.ChecksAsync(20);
        Assert.Single(h.Notifier.Sent);
        Assert.Equal(1, h.Feeds.DhanStops);
        Assert.Equal(1, h.Feeds.FyersStarts);
        Assert.Equal(0, h.Feeds.DhanStarts);   // never switches back
    }

    [Fact]
    public async Task NotDryRun_NoFyersTickWithin90s_OneAlertSaysSo()
    {
        var h = new Harness(dryRun: false);
        h.Ticks.Ages(nse: 212, bse: 214, mcx: 3);
        await h.ChecksAsync(2);

        await h.ChecksAsync(5);   // 75 s: not yet
        Assert.Empty(h.Notifier.Sent);

        await h.ChecksAsync(3);   // past 90 s
        var alert = Assert.Single(h.Notifier.Sent);
        Assert.Equal(NotificationSeverity.Error, alert.Severity);
        Assert.Contains("no FYERS tick within 90 s", alert.Title);
    }

    [Fact]
    public async Task FyersSignedOut_NoSwitch_AlertsEvery10Min()
    {
        var h = new Harness(dryRun: false);
        h.Checks.Fyers = new FyersSignIn(FyersSignInState.SignedOut, "the FYERS token expired at 29 Sep 06:00 IST");
        h.Ticks.Ages(nse: 212, bse: 214, mcx: 3);

        await h.ChecksAsync(2);
        var first = Assert.Single(h.Notifier.Sent);
        Assert.Equal("Dhan silent and FYERS not signed in — sign in to FYERS", first.Title);
        Assert.Equal(NotificationCategory.Connector, first.Category);   // → the System channel
        Assert.Contains("expired", first.Message);

        // 9 min 45 s later: still one.
        await h.ChecksAsync(39);
        Assert.Single(h.Notifier.Sent);

        // 10 min after the first: the reminder.
        await h.ChecksAsync(1);
        Assert.Equal(2, h.Notifier.Sent.Count);
        Assert.All(h.Notifier.Sent, n => Assert.Equal(first.Title, n.Title));

        Assert.Equal(0, h.Feeds.DhanStops);
        Assert.Equal(0, h.Feeds.FyersStarts);
        Assert.Empty(h.Store.Values);
    }

    [Fact]
    public async Task FyersSignsInDuringTheIncident_SwitchesThen()
    {
        var h = new Harness(dryRun: false);
        h.Checks.Fyers = new FyersSignIn(FyersSignInState.SignedOut, "no FYERS sign-in on record");
        h.Ticks.Ages(nse: 212, bse: 214, mcx: 3);
        await h.ChecksAsync(4);
        Assert.Equal(0, h.Feeds.DhanStops);

        h.Checks.Fyers = new FyersSignIn(FyersSignInState.SignedIn, "signed in (FYERS answered its profile call)");
        await h.CheckAsync();

        Assert.Equal(1, h.Feeds.DhanStops);
        Assert.Equal(1, h.Feeds.FyersStarts);
    }

    [Fact]
    public async Task AlreadySwitchedToday_NoActionAfterRestart()
    {
        // The first API process switches…
        var store = new MemoryStore();
        var first = new Harness(dryRun: false, store: store);
        first.Ticks.Ages(nse: 212, bse: 214, mcx: 3);
        await first.ChecksAsync(2);
        Assert.Equal(1, first.Feeds.DhanStops);

        // …then the API restarts, someone puts Dhan back, and it goes quiet again.
        var second = new Harness(dryRun: false, store: store, start: first.Clock.Now.AddMinutes(30));
        second.Ticks.Ages(nse: 300, bse: 300, mcx: 3);
        await second.ChecksAsync(10);

        Assert.Equal(0, second.Feeds.DhanStops);
        Assert.Equal(0, second.Feeds.FyersStarts);
        Assert.Empty(second.Notifier.Sent);
        Assert.Equal(0, second.Checks.FyersCalls);
        Assert.Single(second.Log.Lines, l => l.Message.StartsWith("FEED FAILOVER: not acting — today's one switch was already made"));
    }

    [Fact]
    public async Task ADayWhoseSwitchCannotBeReadIsNotSwitched()
    {
        var h = new Harness(dryRun: false, store: new MemoryStore { Broken = true });
        h.Ticks.Ages(nse: 212, bse: 214, mcx: 3);

        await h.ChecksAsync(4);

        Assert.Equal(0, h.Feeds.DhanStops);
        Assert.Single(h.Log.Lines, l => l.Message.StartsWith("FEED FAILOVER: not switching — could not read whether today's switch was already made"));
    }

    [Fact]
    public async Task SessionClosed_NoAction()
    {
        var h = new Harness();
        h.Sessions.Open = false;
        h.Ticks.Ages(nse: 3000, bse: 3000, mcx: 1);

        await h.ChecksAsync(8);

        Assert.Empty(h.Notifier.Sent);
        Assert.Equal(0, h.Ticks.Reads);
        Assert.Equal(0, h.Checks.FyersCalls);
    }

    [Fact]
    public async Task BeforeNineTwenty_NoAction()
    {
        // 09:19:00 IST, NSE open since 09:15 and not one tick since.
        var h = new Harness(start: new DateTimeOffset(2026, 9, 28, 3, 49, 0, TimeSpan.Zero));
        h.Ticks.Missing(FeedFailoverService.Nse, FeedFailoverService.Bse);
        h.Ticks.Ages(mcx: 1);

        await h.ChecksAsync(4);   // 09:19:00 … 09:19:45
        Assert.Empty(h.Notifier.Sent);
        Assert.Equal(0, h.Ticks.Reads);

        await h.ChecksAsync(2);   // 09:20:00, 09:20:15: silent since the open, twice
        Assert.Single(h.Notifier.Sent);
        Assert.Contains("no tick since the open", h.Notifier.Sent[0].Message);
    }

    [Fact]
    public async Task NseSilent_McxFresh_StillCounts()
    {
        // MCX still flowing proves the socket is up, not that NSE is: the
        // Dhan loop of 24 Sep was per exchange as often as not.
        var h = new Harness();
        h.Ticks.Ages(nse: 212, bse: 1, mcx: 1);

        await h.ChecksAsync(2);

        var sent = Assert.Single(h.Notifier.Sent);
        Assert.Contains("NSE silent", sent.Message);
        Assert.Contains("MCX", sent.Message);
    }

    [Fact]
    public async Task BseSilentAlone_CountsOnceBseHasTickedToday()
    {
        // 24 Sep, 15:18: SENSEX alone went quiet.
        var h = new Harness();
        h.Ticks.Ages(nse: 1, bse: 240, mcx: 1);   // BSE's last tick was at 11:26, after the open

        await h.ChecksAsync(2);

        var sent = Assert.Single(h.Notifier.Sent);
        Assert.Contains("BSE silent", sent.Message);
    }

    [Fact]
    public async Task NoBseTickAllDay_IsNotASilence()
    {
        // A day with nothing BSE on the list must not read as a dead feed.
        var h = new Harness();
        h.Ticks.Ages(nse: 1, mcx: 1);
        h.Ticks.Missing(FeedFailoverService.Bse);

        await h.ChecksAsync(6);

        Assert.Empty(h.Notifier.Sent);
        Assert.Equal(0, h.Checks.FyersCalls);
    }

    [Fact]
    public async Task Recovered_LogsOnce()
    {
        var h = new Harness();
        h.Ticks.Ages(nse: 212, bse: 1, mcx: 3);
        await h.ChecksAsync(2);

        h.Ticks.Ages(nse: 1, bse: 1, mcx: 1);
        await h.ChecksAsync(6);

        var recovered = Assert.Single(h.Log.Lines, l => l.Message.StartsWith("FEED FAILOVER: recovered after "));
        // NSE's last tick 11:26:28 (212 s before 11:30:00); fresh again at the 11:30:30 check.
        Assert.StartsWith("FEED FAILOVER: recovered after 242s (silent since 11:26:28 IST, fresh at 11:30:30 IST)", recovered.Message);
        Assert.Single(h.Notifier.Sent);   // the dry run's one message; a recovery is logged, not sent
    }

    [Fact]
    public async Task ANewIncidentAfterARecoveryGetsItsOwnMessage()
    {
        var h = new Harness();
        h.Ticks.Ages(nse: 212, bse: 214, mcx: 3);
        await h.ChecksAsync(2);
        h.Ticks.Ages(nse: 1, bse: 1, mcx: 1);
        await h.ChecksAsync(2);
        h.Ticks.Ages(nse: 200, bse: 200, mcx: 1);
        await h.ChecksAsync(2);

        Assert.Equal(2, h.Notifier.Sent.Count);
    }

    [Fact]
    public async Task OnlyWhileDhanIsTheFeed()
    {
        var h = new Harness(dryRun: false);
        h.Ticks.Ages(nse: 500, bse: 500, mcx: 1);

        h.Feeds.FyersRunning = true;   // FYERS already feeds the desk
        await h.ChecksAsync(3);
        h.Feeds.FyersRunning = false;
        h.Feeds.DhanRunning = false;   // no Dhan feed to replace (Sentinel's no-feed-running)
        await h.ChecksAsync(3);

        Assert.Empty(h.Notifier.Sent);
        Assert.Equal(0, h.Feeds.DhanStops);
        Assert.Equal(0, h.Feeds.FyersStarts);
    }

    [Fact]
    public async Task AnUnreadableStreamIsNotASilence()
    {
        var h = new Harness(dryRun: false);
        h.Ticks.Broken = true;

        await h.ChecksAsync(6);

        Assert.Equal(0, h.Feeds.DhanStops);
        Assert.Empty(h.Notifier.Sent);
        Assert.Single(h.Log.Lines, l => l.Message.StartsWith("FEED FAILOVER: could not read the tick stream"));
    }

    [Fact]
    public async Task Disabled_DoesNothing()
    {
        var h = new Harness(dryRun: false, enabled: false);
        h.Ticks.Ages(nse: 500, bse: 500, mcx: 1);

        await h.ChecksAsync(4);

        Assert.Equal(0, h.Ticks.Reads);
        Assert.Equal(0, h.Feeds.DhanStops);
        Assert.Empty(h.Notifier.Sent);
    }

    [Fact]
    public void DryRun_IsTheDefault()
    {
        // Safe to deploy: without a FeedFailover section, nothing is switched.
        Assert.True(new FeedFailoverOptions().DryRun);
        Assert.True(new FeedFailoverOptions().Enabled);
        Assert.Equal("feed.failover.2026-09-28", SystemSettingKeys.FeedFailover(Monday));
    }

    [Fact]
    public void The_container_builds_the_service_as_the_api_registers_it()
    {
        var h = new Harness();
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .AddSingleton<IMarketSessionService>(h.Sessions)
            .AddSingleton<IFeedTickSource>(h.Ticks)
            .AddSingleton<IFeedFailoverFeeds>(h.Feeds)
            .AddSingleton<IFeedFailoverChecks>(h.Checks)
            .AddSingleton<FeedStallAlertGate>()
            .AddHostedService<FeedFailoverService>()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        Assert.Contains(provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>(), s => s is FeedFailoverService);
        Assert.NotNull(provider.GetRequiredService<FeedStallAlertGate>());
    }

    // ------------------------------------------------------------------
    // The tick stream, read the way Sentinel reads it
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("NSE", "NSE")]
    [InlineData("NFO", "NSE")]
    [InlineData("BSE", "BSE")]
    [InlineData("BFO", "BSE")]
    [InlineData("mcx", "MCX")]
    [InlineData("CDS", null)]
    [InlineData("", null)]
    public void Each_exchange_has_its_group(string exchange, string? group)
        => Assert.Equal(group, RedisFeedTickSource.GroupOf(exchange));

    [Fact]
    public void A_ticks_time_is_its_receivedUtc_and_the_newest_entry_wins()
    {
        var newest = new Dictionary<string, FeedTick>();
        var idTime = new DateTime(2026, 9, 28, 5, 57, 40, DateTimeKind.Utc);

        RedisFeedTickSource.Consider(newest, "NSE", "NSE:NIFTY50-INDEX",
            """{"receivedUtc":"2026-09-28T05:57:36.120000+00:00","sourceKey":"dhan"}""", idTime);
        RedisFeedTickSource.Consider(newest, "NSE", "NSE:NIFTYBANK-INDEX",
            """{"receivedUtc":"2026-09-28T05:50:00+00:00","sourceKey":"dhan"}""", idTime.AddMinutes(-8));

        var tick = newest[FeedFailoverService.Nse];
        Assert.Equal(new DateTime(2026, 9, 28, 5, 57, 36, 120, DateTimeKind.Utc), tick.AtUtc);
        Assert.Equal("NSE:NIFTY50-INDEX", tick.Symbol);
        Assert.Equal("dhan", tick.Source);
    }

    [Fact]
    public void A_replay_is_not_a_live_tick()
    {
        var newest = new Dictionary<string, FeedTick>();
        RedisFeedTickSource.Consider(newest, "NSE", "NSE:NIFTY50-INDEX",
            """{"receivedUtc":"2026-09-28T05:59:59+00:00","isReplay":true,"sourceKey":"truedata"}""", DateTime.UtcNow);
        Assert.Empty(newest);
    }

    [Fact]
    public void Without_an_exchange_field_the_symbol_says_which_and_the_entry_id_says_when()
    {
        var newest = new Dictionary<string, FeedTick>();
        var idTime = new DateTime(2026, 9, 28, 5, 59, 0, DateTimeKind.Utc);
        RedisFeedTickSource.Consider(newest, "", "BSE:SENSEX-INDEX", "not json", idTime);
        Assert.Equal(idTime, newest[FeedFailoverService.Bse].AtUtc);
    }

    [Fact]
    public void Stream_ids_are_read_and_stepped_back_like_sentinel_does()
    {
        Assert.Equal(new DateTime(2026, 9, 28, 5, 57, 36, 123, DateTimeKind.Utc),
            RedisFeedTickSource.IdTime("1790575056123-0"));
        Assert.Equal("1790575056123-4", RedisFeedTickSource.PreviousId("1790575056123-5"));
        Assert.Equal("1790575056122-18446744073709551615", RedisFeedTickSource.PreviousId("1790575056123-0"));
        Assert.Null(RedisFeedTickSource.PreviousId("0-0"));
    }

    // ------------------------------------------------------------------
    // FYERS asked for real
    // ------------------------------------------------------------------

    [Fact]
    public void A_profile_answer_is_signed_in()
    {
        var check = FeedFailoverChecks.ReadProfile(200, """{"s":"ok","code":200,"message":"","data":{"fy_id":"XX0000"}}""");
        Assert.Equal(FyersSignInState.SignedIn, check.State);
        Assert.DoesNotContain("XX0000", check.Detail);   // the account id stays out of Telegram
    }

    [Theory]
    [InlineData(401, """{"s":"error","code":-8,"message":"Your token has expired. Please generate a token"}""", "token has expired")]
    [InlineData(200, """{"s":"error","code":-15,"message":"Could not authenticate the user"}""", "Could not authenticate")]
    [InlineData(403, "<html>Forbidden</html>", "HTTP 403")]
    public void A_refused_token_is_signed_out_with_fyers_own_words(int status, string body, string words)
    {
        var check = FeedFailoverChecks.ReadProfile(status, body);
        Assert.Equal(FyersSignInState.SignedOut, check.State);
        Assert.Contains(words, check.Detail);
    }

    [Fact]
    public void A_server_error_is_not_a_verdict_on_the_token()
        => Assert.Equal(FyersSignInState.Unknown, FeedFailoverChecks.ReadProfile(502, "<html>Bad gateway</html>").State);

    [Fact]
    public void Vendor_text_cannot_break_a_telegram_html_message()
        => Assert.Equal("‹b› x and y", FeedFailoverService.Clean("<b>\n x & y", 100));

    // ------------------------------------------------------------------
    // Fakes
    // ------------------------------------------------------------------

    private sealed class Harness
    {
        public Clock Clock { get; }
        public Sessions Sessions { get; } = new();
        public Ticks Ticks { get; }
        public Feeds Feeds { get; } = new();
        public Checks Checks { get; } = new();
        public MemoryStore Store { get; }
        public Notifier Notifier { get; } = new();
        public ListLogger Log { get; } = new();
        private readonly FeedFailoverService _service;

        public Harness(bool dryRun = true, MemoryStore? store = null, DateTimeOffset? start = null, bool enabled = true)
        {
            Clock = new Clock(start ?? Monday1130Ist);
            Ticks = new Ticks(Clock);
            Store = store ?? new MemoryStore();
            Feeds.DhanStartedUtc = new DateTime(2026, 9, 28, 3, 15, 0, DateTimeKind.Utc);   // 08:45 IST
            var services = new ServiceCollection()
                .AddSingleton<IProcessSettingsStore>(Store)
                .AddSingleton<ISystemNotifier>(Notifier)
                .BuildServiceProvider();
            _service = new FeedFailoverService(
                new Monitor(new FeedFailoverOptions { Enabled = enabled, DryRun = dryRun }),
                Sessions, Ticks, Feeds, Checks,
                services.GetRequiredService<IServiceScopeFactory>(),
                Log, Clock);
        }

        public Task CheckAsync() => _service.CheckAsync(CancellationToken.None);

        /// <summary><paramref name="count"/> checks, the first now and each after 15 s; ticks keep their times, so they age.</summary>
        public async Task ChecksAsync(int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (i > 0 || _checked) Clock.Advance(15);
                await CheckAsync();
                _checked = true;
            }
        }

        private bool _checked;
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(double seconds) => Now = Now.AddSeconds(seconds);
    }

    private sealed class Monitor(FeedFailoverOptions value) : IOptionsMonitor<FeedFailoverOptions>
    {
        public FeedFailoverOptions CurrentValue => value;
        public FeedFailoverOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<FeedFailoverOptions, string?> listener) => null;
    }

    private sealed class Sessions : IMarketSessionService
    {
        public bool Open { get; set; } = true;
        public MarketSessionInfo GetSessionInfo(DateTime utcNow, string exchange, string segment) => throw new NotSupportedException();
        public bool IsMarketOpen(DateTime utcNow, string exchange, string segment) => Open;
        public DateTime GetNextMarketOpenUtc(DateTime utcNow, string exchange, string segment) => throw new NotSupportedException();
    }

    /// <summary>The whole stream is "read" (Exhausted), so a group with no tick has none at all.</summary>
    private sealed class Ticks(Clock clock) : IFeedTickSource
    {
        private readonly Dictionary<string, FeedTick> _newest = new();
        public int Reads { get; private set; }
        public bool Broken { get; set; }

        /// <summary>Each group's newest tick this many seconds before now; null leaves a group as it is.</summary>
        public void Ages(int? nse = null, int? bse = null, int? mcx = null)
        {
            var now = clock.Now.UtcDateTime;
            if (nse is { } n) Set(FeedFailoverService.Nse, now.AddSeconds(-n));
            if (bse is { } b) Set(FeedFailoverService.Bse, now.AddSeconds(-b));
            if (mcx is { } m) Set(FeedFailoverService.Mcx, now.AddSeconds(-m));
        }

        public void Set(string group, DateTime atUtc, string source = "dhan")
            => _newest[group] = new FeedTick(atUtc, $"{group}:TEST", source);

        public void Missing(params string[] groups)
        {
            foreach (var g in groups) _newest.Remove(g);
        }

        public Task<FeedTickReading> ReadAsync(DateTime nowUtc, TimeSpan horizon, CancellationToken cancellationToken)
        {
            Reads++;
            if (Broken) throw new InvalidOperationException("It was not possible to connect to the redis server(s).");
            return Task.FromResult(new FeedTickReading(new Dictionary<string, FeedTick>(_newest), null, Exhausted: true, Scanned: 100));
        }
    }

    private sealed class Feeds : IFeedFailoverFeeds
    {
        public bool DhanRunning { get; set; } = true;
        public bool FyersRunning { get; set; }
        public DateTime? DhanStartedUtc { get; set; }
        public int DhanStops { get; private set; }
        public int DhanStarts { get; private set; }
        public int FyersStarts { get; private set; }

        public Task<DhanFeedProcess> DhanAsync(CancellationToken cancellationToken)
            => Task.FromResult(DhanRunning ? new DhanFeedProcess(true, 4242, DhanStartedUtc) : new DhanFeedProcess(false, null, null));

        public Task<bool> FyersRunningAsync(CancellationToken cancellationToken) => Task.FromResult(FyersRunning);

        public Task<string> StopDhanAsync(string reason, CancellationToken cancellationToken)
        {
            DhanStops++;
            DhanRunning = false;
            return Task.FromResult("Dhan feed: Dhan feed stopped");
        }

        public Task<FyersStartOutcome> StartFyersAsync(CancellationToken cancellationToken)
        {
            FyersStarts++;
            FyersRunning = true;
            return Task.FromResult(new FyersStartOutcome(true, "FYERS feed: Ingestor started; FYERS chain poller: Chain poller started"));
        }
    }

    private sealed class Checks : IFeedFailoverChecks
    {
        public FyersSignIn Fyers { get; set; } = new(FyersSignInState.SignedIn, "signed in (FYERS answered its profile call)");
        public int FyersCalls { get; private set; }

        public Task<FyersSignIn> CheckFyersAsync(CancellationToken cancellationToken)
        {
            FyersCalls++;
            return Task.FromResult(Fyers);
        }

        public Task<string> DescribeDhanAsync(CancellationToken cancellationToken)
            => Task.FromResult("/profile refused: DH-901 Invalid access token; feed heartbeat Stalled at 11:29:50 IST");
    }

    private sealed class MemoryStore : IProcessSettingsStore
    {
        public Dictionary<string, string> Values { get; } = new();
        public bool Broken { get; init; }

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            if (Broken) throw new InvalidOperationException("the database is down");
            return Task.FromResult(Values.GetValueOrDefault(key));
        }

        public Task SetAsync(string key, string value, string? updatedBy = null, CancellationToken cancellationToken = default)
        {
            if (Broken) throw new InvalidOperationException("the database is down");
            Values[key] = value;
            return Task.CompletedTask;
        }

        public Task<int?> GetPidAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetPidAsync(string key, int processId, string? updatedBy = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteIfPidAsync(string key, int processId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Notifier : ISystemNotifier
    {
        public List<(NotificationCategory Category, NotificationSeverity Severity, string Title, string Message)> Sent { get; } = new();

        public Task NotifyAsync(NotificationCategory category, NotificationSeverity severity, string title, string message,
            string? underlying = null, string? symbol = null, long? simulationRunId = null, CancellationToken cancellationToken = default)
        {
            Sent.Add((category, severity, title, message));
            return Task.CompletedTask;
        }
    }

    private sealed class ListLogger : ILogger<FeedFailoverService>
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add((logLevel, formatter(state, exception)));
    }
}
