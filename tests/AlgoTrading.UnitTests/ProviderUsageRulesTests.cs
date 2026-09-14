using AlgoTrading.Application.Providers;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The rules behind each connector page's "What we take from this vendor"
/// panel. The ones that matter most: a failed read is unknown, never off; a
/// running feed on a closed market is idle, not broken; and anything the
/// connector does not declare is not offered.
/// </summary>
public class ProviderUsageRulesTests
{
    // 2026-09-14 18:00 IST, a Monday the exchanges were shut for Ganesh Chaturthi.
    private static readonly DateTime Now = new(2026, 9, 14, 12, 30, 0, DateTimeKind.Utc);

    private static readonly string[] AllSegments = { "CM", "FO", "CD", "MCX" };

    private static readonly UsageMarkets Closed = new(false, "Ganesh Chaturthi", false, "Ganesh Chaturthi");
    private static readonly UsageMarkets Open = new(true, null, true, null);
    private static readonly UsageMarkets NotKnown = new(null, null, null, null);

    private static UsageHeartbeat Beat(int ageSeconds, string status = "Running", int symbols = 412, bool recap = false)
        => new("python-acme-feed", status, Now.AddSeconds(-ageSeconds), symbols, recap, null);

    private static UsageFeedFacts Feed(
        bool? running = true,
        UsageHeartbeat? beat = null,
        bool heartbeatKnown = true,
        long? ticks = 0,
        DateTime? lastTick = null,
        bool hasFeed = true)
        => new(hasFeed, running, heartbeatKnown, beat, ticks, lastTick);

    // ------------------------------------------------------------------
    // The core decision
    // ------------------------------------------------------------------

    [Fact]
    public void Undeclared_data_is_not_offered_whatever_else_is_true()
    {
        Assert.Equal(UsageState.NotOffered, ProviderUsageRules.Classify(false, true, true, true));
    }

    [Fact]
    public void A_failed_read_is_unknown_even_when_the_producer_is_known_to_be_stopped()
    {
        Assert.Equal(UsageState.Unknown, ProviderUsageRules.Classify(true, null, false, true));
    }

    [Fact]
    public void Fresh_data_is_on_even_on_a_closed_market()
    {
        // A replay of the day's session streams while the exchange is shut.
        Assert.Equal(UsageState.On, ProviderUsageRules.Classify(true, true, true, false));
    }

    [Fact]
    public void A_running_producer_with_nothing_to_say_on_a_closed_market_is_idle()
    {
        Assert.Equal(UsageState.Idle, ProviderUsageRules.Classify(true, false, true, false));
    }

    [Fact]
    public void A_stopped_producer_is_off_even_when_the_market_is_closed()
    {
        Assert.Equal(UsageState.Off, ProviderUsageRules.Classify(true, false, false, false));
    }

    [Fact]
    public void Silence_on_an_open_market_is_off()
    {
        Assert.Equal(UsageState.Off, ProviderUsageRules.Classify(true, false, true, true));
    }

    [Fact]
    public void Silence_when_market_hours_are_not_known_is_unknown_not_off()
    {
        Assert.Equal(UsageState.Unknown, ProviderUsageRules.Classify(true, false, true, null));
    }

    [Theory]
    [InlineData(UsageState.On, "on")]
    [InlineData(UsageState.Idle, "idle")]
    [InlineData(UsageState.Off, "off")]
    [InlineData(UsageState.Unknown, "unknown")]
    [InlineData(UsageState.NotOffered, "not-offered")]
    public void States_are_written_as_the_console_expects(UsageState state, string wire)
    {
        Assert.Equal(wire, ProviderUsageRules.Wire(state));
    }

    // ------------------------------------------------------------------
    // Markets
    // ------------------------------------------------------------------

    [Fact]
    public void A_commodity_only_connector_is_judged_by_mcx_alone()
    {
        var nseShutMcxTrading = new UsageMarkets(false, null, true, null);

        Assert.Equal(true, ProviderUsageRules.AnyCoveredMarketOpen(nseShutMcxTrading, new[] { "MCX" }));
        Assert.Equal(false, ProviderUsageRules.AnyCoveredMarketOpen(nseShutMcxTrading, new[] { "CM", "FO" }));
    }

    [Fact]
    public void One_open_market_is_enough_and_one_unknown_market_blocks_closed()
    {
        Assert.Equal(true, ProviderUsageRules.AnyCoveredMarketOpen(new UsageMarkets(null, null, true, null), AllSegments));
        Assert.Null(ProviderUsageRules.AnyCoveredMarketOpen(new UsageMarkets(false, null, null, null), AllSegments));
    }

    [Fact]
    public void A_shared_holiday_is_named_once()
    {
        Assert.Equal("NSE and MCX closed for Ganesh Chaturthi", ProviderUsageRules.ClosedReason(Closed, AllSegments));
        Assert.Equal(
            "NSE closed for Diwali, MCX closed",
            ProviderUsageRules.ClosedReason(new UsageMarkets(false, "Diwali", false, null), AllSegments));
    }

    // ------------------------------------------------------------------
    // Feeds
    // ------------------------------------------------------------------

    [Fact]
    public void Every_feed_reports_under_its_key_and_the_original_ingestor_keeps_its_name()
    {
        Assert.Equal(
            new[] { "python-acme-feed", "python-acme-recap" },
            ProviderUsageRules.HeartbeatSourceNames("Acme", isLegacyIngestor: false));

        Assert.Equal(
            ProviderUsageRules.LegacyIngestorSourceName,
            ProviderUsageRules.HeartbeatSourceNames("acme", isLegacyIngestor: true)[0]);
    }

    [Fact]
    public void A_heartbeating_feed_the_api_did_not_start_still_counts_as_running()
    {
        Assert.Equal(true, ProviderUsageRules.FeedRunning(Feed(running: false, beat: Beat(3)), Now));
    }

    [Fact]
    public void A_feed_is_stopped_only_when_both_the_process_and_the_heartbeat_say_so()
    {
        Assert.Equal(false, ProviderUsageRules.FeedRunning(Feed(running: false, beat: Beat(600)), Now));
        Assert.Null(ProviderUsageRules.FeedRunning(Feed(running: false, heartbeatKnown: false), Now));
        Assert.Null(ProviderUsageRules.FeedRunning(Feed(running: null, beat: Beat(600)), Now));
    }

    [Fact]
    public void Flowing_ticks_are_on_and_say_how_many()
    {
        var verdict = ProviderUsageRules.LiveTicks(
            true, Feed(beat: Beat(2), ticks: 2310, lastTick: Now.AddSeconds(-1)), Open, AllSegments, Now);

        Assert.Equal(UsageState.On, verdict.State);
        Assert.Equal("412 symbols · last tick 1 s ago · 2,310 ticks in the last minute", verdict.Summary);
        Assert.Equal(Now.AddSeconds(-1), verdict.LastUtc);
    }

    [Fact]
    public void A_running_feed_with_no_ticks_on_a_holiday_is_idle_and_says_why()
    {
        var verdict = ProviderUsageRules.LiveTicks(true, Feed(beat: Beat(4)), Closed, AllSegments, Now);

        Assert.Equal(UsageState.Idle, verdict.State);
        Assert.Contains("no ticks while NSE and MCX closed for Ganesh Chaturthi", verdict.Summary);
    }

    [Fact]
    public void A_running_feed_with_no_ticks_while_the_market_trades_is_off()
    {
        var verdict = ProviderUsageRules.LiveTicks(true, Feed(beat: Beat(4)), Open, AllSegments, Now);

        Assert.Equal(UsageState.Off, verdict.State);
        Assert.StartsWith("running but no ticks in the last minute", verdict.Summary);
    }

    [Fact]
    public void A_tick_count_that_could_not_be_read_is_unknown_never_off()
    {
        var verdict = ProviderUsageRules.LiveTicks(true, Feed(running: false, beat: Beat(900), ticks: null), Open, AllSegments, Now);

        Assert.Equal(UsageState.Unknown, verdict.State);
        Assert.Contains("could not be read", verdict.Summary);
    }

    [Fact]
    public void A_stopped_feed_is_off()
    {
        var verdict = ProviderUsageRules.LiveTicks(true, Feed(running: false, beat: Beat(900, status: "Stopped")), Closed, AllSegments, Now);

        Assert.Equal(UsageState.Off, verdict.State);
        Assert.StartsWith("feed stopped", verdict.Summary);
    }

    [Fact]
    public void A_replay_is_named_as_one()
    {
        var verdict = ProviderUsageRules.LiveTicks(
            true, Feed(beat: Beat(1, recap: true), ticks: 50, lastTick: Now), Closed, AllSegments, Now);

        Assert.Equal(UsageState.On, verdict.State);
        Assert.Contains("replaying a recorded session", verdict.Summary);
    }

    [Fact]
    public void Live_ticks_the_connector_declares_but_has_no_feed_for_are_off()
    {
        var verdict = ProviderUsageRules.LiveTicks(true, Feed(hasFeed: false), Open, AllSegments, Now);
        Assert.Equal(UsageState.Off, verdict.State);

        Assert.Equal(UsageState.NotOffered, ProviderUsageRules.LiveTicks(false, Feed(), Open, AllSegments, Now).State);
    }

    // ------------------------------------------------------------------
    // Session
    // ------------------------------------------------------------------

    [Fact]
    public void A_connector_with_no_login_is_on()
    {
        var verdict = ProviderUsageRules.Session(new UsageSessionFacts(ProviderAuthKind.None, null, true, null), Now);
        Assert.Equal(UsageState.On, verdict.State);
    }

    [Fact]
    public void A_self_signing_connector_depends_on_its_saved_credentials()
    {
        Assert.Equal(UsageState.On, ProviderUsageRules.Session(new UsageSessionFacts(ProviderAuthKind.ApiKey, true, true, null), Now).State);
        Assert.Equal(UsageState.Off, ProviderUsageRules.Session(new UsageSessionFacts(ProviderAuthKind.ApiKey, false, true, null), Now).State);
        Assert.Equal(UsageState.Unknown, ProviderUsageRules.Session(new UsageSessionFacts(ProviderAuthKind.ApiKey, null, true, null), Now).State);
    }

    [Fact]
    public void A_daily_token_is_on_until_it_expires_and_says_when()
    {
        // Issued 09:05 IST, expires 06:00 IST tomorrow.
        var token = new UsageToken(true, new DateTime(2026, 9, 14, 3, 35, 0, DateTimeKind.Utc), new DateTime(2026, 9, 15, 0, 30, 0, DateTimeKind.Utc));

        var verdict = ProviderUsageRules.Session(new UsageSessionFacts(ProviderAuthKind.OAuthDaily, true, true, token), Now);

        Assert.Equal(UsageState.On, verdict.State);
        Assert.Equal("connected · signed in 8 h ago · expires 15 Sep 06:00 IST", verdict.Summary);
    }

    [Fact]
    public void An_expired_daily_token_is_off_and_names_the_expiry()
    {
        var token = new UsageToken(true, new DateTime(2026, 9, 13, 3, 35, 0, DateTimeKind.Utc), new DateTime(2026, 9, 14, 0, 30, 0, DateTimeKind.Utc));

        var verdict = ProviderUsageRules.Session(new UsageSessionFacts(ProviderAuthKind.OAuthDaily, true, true, token), Now);

        Assert.Equal(UsageState.Off, verdict.State);
        Assert.Equal("token expired 06:00 IST — sign in again", verdict.Summary);
    }

    [Fact]
    public void Data_arriving_without_a_sign_in_is_not_reported_as_disconnected()
    {
        // Dhan on 2026-09-14: no Connect yet, a token pasted into configuration,
        // and 2,663 ticks a minute. "not connected" would have been false.
        var verdict = ProviderUsageRules.Session(new UsageSessionFacts(ProviderAuthKind.OAuthDaily, true, true, null), Now, dataArriving: true);

        Assert.Equal(UsageState.On, verdict.State);
        Assert.StartsWith("no sign-in today, but data is arriving", verdict.Summary);
        Assert.Equal(UsageState.Off, ProviderUsageRules.Session(new UsageSessionFacts(ProviderAuthKind.OAuthDaily, true, true, null), Now).State);
    }

    [Fact]
    public void A_session_that_could_not_be_read_is_unknown()
    {
        var verdict = ProviderUsageRules.Session(new UsageSessionFacts(ProviderAuthKind.OAuthDaily, true, TokenKnown: false, null), Now);
        Assert.Equal(UsageState.Unknown, verdict.State);
    }

    // ------------------------------------------------------------------
    // Quotes, depth, open interest, greeks, chain
    // ------------------------------------------------------------------

    [Fact]
    public void Quotes_count_fresh_rows_against_all_rows_from_the_source()
    {
        var quotes = new UsageQuoteFacts(Total: 10, Fresh: 3, FreshWithDepth: 2, FreshWithOpenInterest: 0, FreshWithGreeks: 0, NewestUtc: Now.AddSeconds(-5));

        var verdict = ProviderUsageRules.Quotes(true, quotes, true, true, "", Now);

        Assert.Equal(UsageState.On, verdict.State);
        Assert.Equal("3 of 10 quotes updated in the last 2 min · newest 5 s ago", verdict.Summary);
    }

    [Fact]
    public void Quotes_that_could_not_be_read_are_unknown()
    {
        Assert.Equal(UsageState.Unknown, ProviderUsageRules.Quotes(true, null, false, true, "", Now).State);
        Assert.Equal(UsageState.Unknown, ProviderUsageRules.Depth(true, null, false, true, "").State);
    }

    [Fact]
    public void Quotes_without_bid_ask_sizes_do_not_count_as_depth()
    {
        var quotes = new UsageQuoteFacts(10, 3, 0, 0, 0, Now);

        var verdict = ProviderUsageRules.Depth(true, quotes, true, true, "");

        Assert.Equal(UsageState.Off, verdict.State);
        Assert.Equal("3 fresh quotes, none with bid/ask sizes", verdict.Summary);
    }

    [Fact]
    public void Open_interest_on_either_quotes_or_the_chain_counts()
    {
        var quotes = new UsageQuoteFacts(10, 3, 0, 0, 0, Now);
        var chain = new UsageChainFacts(true, Now.AddMinutes(-1), 80, new[] { "NIFTY" }, 160, 0);

        var verdict = ProviderUsageRules.OpenInterest(true, quotes, chain, true, "");

        Assert.Equal(UsageState.On, verdict.State);
        Assert.Equal("160 chain rows in the last 10 min carry open interest", verdict.Summary);
    }

    [Fact]
    public void No_open_interest_is_unknown_when_the_chain_read_did_not_cover_the_window()
    {
        var quotes = new UsageQuoteFacts(10, 3, 0, 0, 0, Now);
        var partial = new UsageChainFacts(Covered: false, null, 0, Array.Empty<string>(), 0, 0);

        Assert.Equal(UsageState.Unknown, ProviderUsageRules.OpenInterest(true, quotes, partial, true, "").State);
        Assert.Equal(UsageState.Unknown, ProviderUsageRules.Greeks(true, null, partial with { Covered = true }, true, "").State);
    }

    [Fact]
    public void Undeclared_greeks_stay_not_offered_but_say_when_they_are_arriving_anyway()
    {
        var chain = new UsageChainFacts(true, Now, 80, new[] { "NIFTY" }, 0, 80);

        var verdict = ProviderUsageRules.Greeks(false, null, chain, true, "");

        Assert.Equal(UsageState.NotOffered, verdict.State);
        Assert.Equal("not declared by this connector, yet 80 chain rows in the last 10 min from it carry greeks", verdict.Summary);
    }

    [Fact]
    public void The_chain_reports_its_latest_capture()
    {
        var chain = new UsageChainFacts(true, Now.AddSeconds(-20), 164, new[] { "BANKNIFTY", "NIFTY" }, 0, 0);

        var verdict = ProviderUsageRules.OptionChain(true, chain, true, "", Now);

        Assert.Equal(UsageState.On, verdict.State);
        Assert.Equal("last capture 20 s ago · 164 rows in it · BANKNIFTY, NIFTY", verdict.Summary);
    }

    [Fact]
    public void An_empty_chain_window_is_idle_on_a_holiday_and_unknown_when_the_read_fell_short()
    {
        var none = new UsageChainFacts(true, null, 0, Array.Empty<string>(), 0, 0);

        Assert.Equal(UsageState.Idle, ProviderUsageRules.OptionChain(true, none, false, "NSE closed", Now).State);
        Assert.Equal(UsageState.Off, ProviderUsageRules.OptionChain(true, none, true, "", Now).State);
        Assert.Equal(UsageState.Unknown, ProviderUsageRules.OptionChain(true, none with { Covered = false }, true, "", Now).State);
    }

    // ------------------------------------------------------------------
    // History, instruments, orders
    // ------------------------------------------------------------------

    [Fact]
    public void History_follows_the_routing_chain()
    {
        var bars = new UsageCandleFacts(new DateTime(2026, 9, 11, 9, 59, 0, DateTimeKind.Utc), "5", 20_000);

        var primary = ProviderUsageRules.History(true, 0, "Acme", bars, Now);
        Assert.Equal(UsageState.On, primary.State);
        Assert.Equal("primary source for history · newest stored bar 11 Sep 15:29 IST (5 min)", primary.Summary);

        var fallback = ProviderUsageRules.History(true, 2, "Acme", bars, Now);
        Assert.Equal(UsageState.Idle, fallback.State);
        Assert.StartsWith("fallback #2 — asked only when Acme cannot answer", fallback.Summary);

        Assert.Equal(UsageState.Off, ProviderUsageRules.History(true, -1, "Acme", bars, Now).State);
        Assert.Equal(UsageState.Unknown, ProviderUsageRules.History(true, null, null, bars, Now).State);
        Assert.Equal(UsageState.NotOffered, ProviderUsageRules.History(false, 0, "Acme", bars, Now).State);
    }

    [Fact]
    public void Stored_bars_that_could_not_be_read_do_not_change_the_routing_answer()
    {
        var verdict = ProviderUsageRules.History(true, 0, "Acme", null, Now);

        Assert.Equal(UsageState.On, verdict.State);
        Assert.EndsWith("stored bars could not be read", verdict.Summary);
    }

    [Fact]
    public void Instruments_matter_only_to_a_connector_with_its_own_symbols()
    {
        Assert.Equal(UsageState.NotOffered, ProviderUsageRules.Instruments(true, null, Now).State);
        Assert.Equal(UsageState.Unknown, ProviderUsageRules.Instruments(false, null, Now).State);
        Assert.Equal(UsageState.Off, ProviderUsageRules.Instruments(false, new UsageInstrumentFacts(0, null), Now).State);

        var on = ProviderUsageRules.Instruments(false, new UsageInstrumentFacts(104_114, Now.AddDays(-2)), Now);
        Assert.Equal(UsageState.On, on.State);
        Assert.Equal("104,114 symbols mapped · updated 2 d ago", on.Summary);
    }

    [Fact]
    public void Offered_orders_are_idle_because_nothing_is_placed_through_a_connector_today()
    {
        Assert.Equal(UsageState.Idle, ProviderUsageRules.Orders(true).State);
        Assert.Equal(UsageState.NotOffered, ProviderUsageRules.Orders(false).State);
    }

    // ------------------------------------------------------------------
    // Wording
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(4, "4 s ago")]
    [InlineData(125, "2 min ago")]
    [InlineData(3 * 3600 + 5, "3 h ago")]
    [InlineData(2 * 86400 + 5, "2 d ago")]
    [InlineData(-30, "just now")]
    public void Ages_read_the_way_a_person_says_them(int secondsAgo, string expected)
    {
        Assert.Equal(expected, ProviderUsageRules.Age(Now.AddSeconds(-secondsAgo), Now));
    }

    [Fact]
    public void Clock_times_are_ist_and_carry_a_date_only_when_it_is_not_today()
    {
        Assert.Equal("17:30 IST", ProviderUsageRules.Clock(Now.AddHours(-0.5), Now));
        Assert.Equal("15 Sep 06:00 IST", ProviderUsageRules.Clock(new DateTime(2026, 9, 15, 0, 30, 0, DateTimeKind.Utc), Now));
    }

    [Theory]
    [InlineData("1", "1 min")]
    [InlineData("15", "15 min")]
    [InlineData("D", "daily")]
    [InlineData("1m", "1m")]
    public void Resolutions_are_labelled(string stored, string label)
    {
        Assert.Equal(label, ProviderUsageRules.ResolutionLabel(stored));
    }
}
