using System.Net;
using System.Text;
using System.Text.Json;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.ValueObjects;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Providers.Dhan;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Dhan's option chain recorded into the platform's chain history, the switch
/// that keeps it recording across a restart, and the universe the Dhan feed
/// streams beyond the watchlist.
/// </summary>
public class DhanChainPollerTests
{
    private static readonly DateOnly Expiry = new(2026, 9, 15);

    // ------------------------------------------------------------- chain → rows

    [Fact]
    public void A_chain_becomes_snapshot_rows_under_the_platforms_symbols()
    {
        var chain = new DhanOptionChain("NIFTY", Expiry, 24010.5m, new[]
        {
            new DhanChainRow(24000.000000m,
                Side(lastPrice: 120m, previousClose: 100m, oi: 5000, previousOi: 4000, iv: 14.2m, greeks: new DhanGreeks(0.52m, -9.1m, 0.0011m, 12.3m)),
                Side(lastPrice: 95m, previousClose: 110m, oi: 7000, previousOi: null, iv: null, greeks: null)),
            // Listed by Dhan after the platform's instrument download: no symbol yet.
            new DhanChainRow(24050m, Side(lastPrice: 90m), null),
        });
        var contracts = new Dictionary<(decimal, string), string>
        {
            [(24000.00m, "CE")] = "NSE:NIFTY2691524000CE",
            [(24000.00m, "PE")] = "NSE:NIFTY2691524000PE",
        };

        var (rows, unmatched) = DhanChainRows.Build(chain, contracts);

        Assert.Equal(2, rows.Count);
        Assert.Equal(1, unmatched);

        var call = rows.Single(r => r.OptionType == "CE");
        Assert.Equal("NSE:NIFTY2691524000CE", call.Symbol);
        Assert.Equal("NIFTY", call.Underlying);
        Assert.Equal(24010.5m, call.SpotPrice);
        Assert.Equal(20m, call.PriceChange);
        Assert.Equal(4000, call.PreviousDayOpenInterest);
        Assert.Equal(14.2m, call.ImpliedVolatility);
        Assert.Equal(0.52m, call.Delta);
        Assert.Equal("dhan", call.SourceKey);

        var put = rows.Single(r => r.OptionType == "PE");
        Assert.Equal(-15m, put.PriceChange);
        Assert.Null(put.PreviousDayOpenInterest);
        Assert.Null(put.Delta);
    }

    [Fact]
    public void The_nearest_expiry_on_its_own_day_is_todays()
    {
        var expiries = new[] { new DateOnly(2026, 9, 22), new DateOnly(2026, 9, 15), new DateOnly(2026, 9, 8) };

        Assert.Equal(new DateOnly(2026, 9, 15), DhanChainRows.NearestExpiry(expiries, new DateOnly(2026, 9, 15)));
        Assert.Equal(new DateOnly(2026, 9, 22), DhanChainRows.NearestExpiry(expiries, new DateOnly(2026, 9, 16)));
        Assert.Null(DhanChainRows.NearestExpiry(expiries, new DateOnly(2026, 9, 23)));
    }

    // ------------------------------------------------------------------ recorder

    [Fact]
    public async Task A_round_stores_the_chain_through_the_option_chain_module()
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, IstTime.Zone));
        var expiry = today.AddDays(1);
        await using var db = Db();
        db.Instruments.AddRange(
            Option("NSE:NIFTYTESTCE", "NIFTY", expiry, 24000m, "CE"),
            Option("NSE:NIFTYTESTPE", "NIFTY", expiry, 24000m, "PE"));
        await db.SaveChangesAsync();

        var handler = new Routes
        {
            ["/optionchain/expirylist"] = $$"""{"data":["{{expiry:yyyy-MM-dd}}"],"status":"success"}""",
            ["/optionchain"] = """
                {"data":{"last_price":24010.5,"oc":{"24000.000000":{
                  "ce":{"last_price":120,"previous_close_price":100,"oi":5000,"previous_oi":4000,"volume":900,"implied_volatility":14.2,
                        "greeks":{"delta":0.52,"theta":-9.1,"gamma":0.0011,"vega":12.3},"top_bid_price":119.5,"top_ask_price":120.5,"security_id":1},
                  "pe":{"last_price":95,"previous_close_price":110,"oi":7000,"previous_oi":6500,"volume":800,"implied_volatility":13.1,
                        "greeks":{"delta":-0.47,"theta":-8.0,"gamma":0.0011,"vega":12.0},"security_id":2}}}},"status":"success"}
                """,
        };
        var state = State();
        var recorder = Recorder(db, handler, state, marketOpen: true);

        var outcomes = await recorder.RecordAsync(new[] { "NIFTY" }, onlyOpenMarkets: true, CancellationToken.None);

        var outcome = Assert.Single(outcomes);
        Assert.Equal("recorded", outcome.State);
        Assert.Equal(2, outcome.Rows);
        Assert.Equal(expiry, outcome.Expiry);

        var stored = await db.OptionChainSnapshots.OrderBy(s => s.OptionType).ToListAsync();
        Assert.Equal(2, stored.Count);
        Assert.All(stored, s => Assert.Equal("dhan", s.SourceKey));
        Assert.Equal(4000, stored[0].OpenInterestAtOpen);
        Assert.Equal(-0.47m, stored[1].Delta);
        Assert.Same(outcome, state.Outcomes.Single());
    }

    [Fact]
    public async Task An_MCX_chain_is_priced_on_its_future_not_on_the_chains_own_figure()
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, IstTime.Zone));
        await using var db = Db();
        db.Instruments.AddRange(
            new Instrument { Symbol = "MCX:CRUDEOILTESTFUT", Exchange = "MCX", Segment = "COM", InstrumentType = "FUT", Underlying = "CRUDEOIL", ExpiryDate = today.AddDays(7), IsEnabled = true },
            new Instrument { Symbol = "MCX:CRUDEOILTEST9950CE", Exchange = "MCX", Segment = "COM", InstrumentType = "CE", OptionType = "CE", Underlying = "CRUDEOIL", ExpiryDate = today.AddDays(3), StrikePrice = 9950m, IsEnabled = true });
        db.InstrumentVendorSymbols.Add(new InstrumentVendorSymbol { ProviderKey = "dhan", CanonicalSymbol = "MCX:CRUDEOILTESTFUT", VendorSymbol = "MCX_COMM:565899:FUTCOM" });
        db.LiveQuotesLatest.Add(new LiveQuoteLatest { Symbol = "MCX:CRUDEOILTESTFUT", LastTradedPrice = 9971m, UpdatedUtc = DateTime.UtcNow, SourceKey = "dhan" });
        await db.SaveChangesAsync();

        var handler = new Routes
        {
            ["/optionchain/expirylist"] = $$"""{"data":["{{today.AddDays(3):yyyy-MM-dd}}"],"status":"success"}""",
            // The figure Dhan sent on 2026-09-14 while the future traded 9,971.
            ["/optionchain"] = """{"data":{"last_price":9577,"oc":{"9950.000000":{"ce":{"last_price":302.3,"oi":1797,"security_id":7}}}},"status":"success"}""",
        };
        var recorder = Recorder(db, handler, State(), marketOpen: true);

        var outcome = Assert.Single(await recorder.RecordAsync(new[] { "CRUDEOIL" }, onlyOpenMarkets: true, CancellationToken.None));

        Assert.Equal("recorded", outcome.State);
        Assert.Equal(9971m, outcome.Spot);
        Assert.Equal(9971m, (await db.OptionChainSnapshots.SingleAsync()).SpotPrice);
    }

    [Fact]
    public async Task A_closed_market_is_not_asked_and_says_why()
    {
        await using var db = Db();
        var handler = new Routes();
        var recorder = Recorder(db, handler, State(), marketOpen: false);

        var outcome = Assert.Single(await recorder.RecordAsync(new[] { "SENSEX" }, onlyOpenMarkets: true, CancellationToken.None));

        Assert.Equal("idle", outcome.State);
        Assert.Equal("BSE is closed", outcome.Detail);
        Assert.Empty(handler.Called);
    }

    [Fact]
    public async Task A_rejected_token_ends_the_round_instead_of_repeating_for_every_underlying()
    {
        await using var db = Db();
        var handler = new Routes(HttpStatusCode.Unauthorized)
        {
            ["/optionchain/expirylist"] = """{"errorType":"Invalid_Authentication","errorCode":"DH-901","errorMessage":"Client ID or user generated access token is invalid or expired."}""",
        };
        var recorder = Recorder(db, handler, State(), marketOpen: true);

        var outcomes = await recorder.RecordAsync(new[] { "NIFTY", "BANKNIFTY" }, onlyOpenMarkets: true, CancellationToken.None);

        var outcome = Assert.Single(outcomes);
        Assert.Equal("auth-failed", outcome.State);
    }

    [Fact]
    public async Task A_lapsed_data_plan_ends_the_round_and_says_so()
    {
        await using var db = Db();
        var handler = new Routes(HttpStatusCode.Unauthorized)
        {
            ["/optionchain/expirylist"] = """{"data":{"806":"Data APIs not Subscribed"},"status":"failed"}""",
        };
        var recorder = Recorder(db, handler, State(), marketOpen: true);

        var outcomes = await recorder.RecordAsync(new[] { "NIFTY", "BANKNIFTY" }, onlyOpenMarkets: true, CancellationToken.None);

        var outcome = Assert.Single(outcomes);
        Assert.Equal("not-subscribed", outcome.State);
        Assert.Contains("a new token will not help", outcome.Detail);
    }

    [Fact]
    public void Configured_lists_are_replaced_not_appended_to()
    {
        var settings = new DhanChainPollerSettings { Underlyings = " nifty, CRUDEOIL ,,NIFTY" };
        Assert.Equal(new[] { "NIFTY", "CRUDEOIL" }, settings.UnderlyingList);
    }

    // ------------------------------------------------------------ later expiries

    // NIFTY from Friday 30 Oct 2026: weeklies on Tuesdays, and the November
    // monthly on Monday the 23rd because the 24th is Guru Nanak Jayanti.
    private static readonly DateOnly[] NiftyExpiries =
    [
        new(2026, 10, 27), new(2026, 11, 3), new(2026, 11, 10), new(2026, 11, 17), new(2026, 11, 23),
        new(2026, 12, 1), new(2026, 12, 8), new(2026, 12, 15), new(2026, 12, 22), new(2026, 12, 29),
        new(2027, 1, 5), new(2027, 1, 12), new(2027, 3, 30),
    ];

    [Fact]
    public void Later_expiries_are_the_next_one_and_the_nearest_monthly()
    {
        // Moved a day early by the holiday, the monthly is still the month's last listed expiry.
        Assert.Equal(new[] { new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 23) },
            DhanChainRows.LaterExpiries(NiftyExpiries, new DateOnly(2026, 10, 30)));

        // On a weekly's own expiry day the nearest is that day's, and both later ones follow it.
        Assert.Equal(new[] { new DateOnly(2026, 11, 17), new DateOnly(2026, 11, 23) },
            DhanChainRows.LaterExpiries(NiftyExpiries, new DateOnly(2026, 11, 10)));
    }

    [Fact]
    public void A_monthly_already_recorded_as_nearest_or_next_is_not_added_twice()
    {
        // The next expiry is the monthly.
        Assert.Equal(new[] { new DateOnly(2026, 11, 23) },
            DhanChainRows.LaterExpiries(NiftyExpiries, new DateOnly(2026, 11, 12)));

        // The nearest is the monthly, on its own expiry day too: only the next weekly is added.
        Assert.Equal(new[] { new DateOnly(2026, 12, 1) },
            DhanChainRows.LaterExpiries(NiftyExpiries, new DateOnly(2026, 11, 19)));
        Assert.Equal(new[] { new DateOnly(2026, 12, 1) },
            DhanChainRows.LaterExpiries(NiftyExpiries, new DateOnly(2026, 11, 23)));

        // December's monthly is the nearest at the turn of the year; January's is not reached for.
        Assert.Equal(new[] { new DateOnly(2027, 1, 5) },
            DhanChainRows.LaterExpiries(NiftyExpiries, new DateOnly(2026, 12, 28)));
    }

    [Fact]
    public void BANKNIFTY_lists_only_monthlies_so_its_later_expiry_is_next_months()
    {
        var banknifty = new[] { new DateOnly(2026, 10, 27), new DateOnly(2026, 11, 23), new DateOnly(2026, 12, 29), new DateOnly(2027, 1, 26) };

        Assert.Equal(new[] { new DateOnly(2026, 12, 29) }, DhanChainRows.LaterExpiries(banknifty, new DateOnly(2026, 10, 30)));
        Assert.Equal(new[] { new DateOnly(2026, 12, 29) }, DhanChainRows.LaterExpiries(banknifty, new DateOnly(2026, 11, 23)));
    }

    [Fact]
    public void SENSEX_expires_on_BSE_Thursdays_and_the_same_rule_holds()
    {
        var sensex = new[] { new DateOnly(2026, 11, 5), new DateOnly(2026, 11, 12), new DateOnly(2026, 11, 19), new DateOnly(2026, 11, 26), new DateOnly(2026, 12, 3) };

        Assert.Equal(new[] { new DateOnly(2026, 11, 12), new DateOnly(2026, 11, 26) },
            DhanChainRows.LaterExpiries(sensex, new DateOnly(2026, 10, 30)));
    }

    [Fact]
    public void An_underlying_with_one_expiry_left_has_no_later_one()
    {
        Assert.Empty(DhanChainRows.LaterExpiries(new[] { new DateOnly(2026, 11, 3) }, new DateOnly(2026, 10, 30)));
        Assert.Empty(DhanChainRows.LaterExpiries(new[] { new DateOnly(2026, 10, 27), new DateOnly(2026, 11, 3) }, new DateOnly(2026, 10, 30)));
        Assert.Empty(DhanChainRows.LaterExpiries(Array.Empty<DateOnly>(), new DateOnly(2026, 10, 30)));

        // Unsorted, with a repeat: the same answer as the clean list.
        Assert.Equal(new[] { new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 23) },
            DhanChainRows.LaterExpiries(NiftyExpiries.Reverse().Append(new DateOnly(2026, 11, 3)), new DateOnly(2026, 10, 30)));
    }

    [Fact]
    public void A_later_expiry_is_due_once_an_interval_counted_in_rounds()
    {
        var every = TimeSpan.FromMinutes(5);
        var round = new DateTime(2026, 10, 30, 3, 45, 0, DateTimeKind.Utc); // 09:15 IST

        Assert.True(DhanLaterExpirySchedule.IsDue(null, round, every));
        Assert.False(DhanLaterExpirySchedule.IsDue(round, round.AddMinutes(1), every));
        Assert.False(DhanLaterExpirySchedule.IsDue(round, round.AddMinutes(4), every));
        // The fifth round started a hair before the fifth minute: still that round, not the sixth.
        Assert.True(DhanLaterExpirySchedule.IsDue(round, round.AddMinutes(5).AddMilliseconds(-40), every));
        Assert.True(DhanLaterExpirySchedule.IsDue(round, round.AddMinutes(5), every));
    }

    [Fact]
    public void The_cadence_is_never_quicker_than_a_round_and_the_deadline_leaves_the_next_round_on_time()
    {
        var minute = TimeSpan.FromSeconds(60);
        Assert.Equal(TimeSpan.FromSeconds(300), DhanLaterExpirySchedule.Every(new DhanChainPollerSettings(), minute));
        Assert.Equal(minute, DhanLaterExpirySchedule.Every(new DhanChainPollerSettings { LaterExpiryIntervalSeconds = 10 }, minute));

        var round = new DateTime(2026, 10, 30, 3, 45, 0, DateTimeKind.Utc);
        var deadline = DhanLaterExpirySchedule.Deadline(round, minute);
        Assert.Equal(round.AddSeconds(58), deadline);
        Assert.True(DhanLaterExpirySchedule.CanStart(round.AddSeconds(30), deadline, DhanLaterExpirySchedule.Reserve));
        Assert.True(DhanLaterExpirySchedule.CanStart(round.AddSeconds(48), deadline, DhanLaterExpirySchedule.Reserve));
        Assert.False(DhanLaterExpirySchedule.CanStart(round.AddSeconds(49), deadline, DhanLaterExpirySchedule.Reserve));
    }

    [Fact]
    public void The_default_later_expiry_settings_need_no_server_change()
    {
        var settings = new DhanChainPollerSettings();
        Assert.Equal(new[] { "NIFTY", "BANKNIFTY", "SENSEX" }, settings.LaterExpiryUnderlyingList);
        Assert.Equal(300, settings.LaterExpiryIntervalSeconds);
        Assert.Empty(new DhanChainPollerSettings { LaterExpiryUnderlyings = "" }.LaterExpiryUnderlyingList);
    }

    [Fact]
    public async Task Later_expiries_are_recorded_after_the_nearest_and_then_wait_their_interval()
    {
        var (db, handler, today, expiries) = await NiftyWeekliesAsync();
        await using var _ = db;
        var state = State();
        var recorder = Recorder(db, handler, state, marketOpen: true, QuickGate());
        var every = TimeSpan.FromMinutes(5);
        var round = DateTime.UtcNow;

        var nearest = await recorder.RecordAsync(new[] { "NIFTY" }, onlyOpenMarkets: true, CancellationToken.None);
        var later = await recorder.RecordLaterExpiriesAsync(new[] { "NIFTY" }, nearest, round, round.AddMinutes(1), every, CancellationToken.None);

        var expected = DhanChainRows.LaterExpiries(expiries, today);
        Assert.NotEmpty(expected);
        Assert.Equal(expected, later.Select(o => o.Expiry!.Value));
        Assert.All(later, o => Assert.Equal("recorded", o.State));
        Assert.Equal(expected.Prepend(expiries[0]).Order(),
            (await db.OptionChainSnapshots.Select(x => x.ExpiryDate).Distinct().ToListAsync()).Order());
        // Kept apart from the nearest's line, which stays one per underlying.
        Assert.Equal(expiries[0], Assert.Single(state.Outcomes).Expiry);
        Assert.Equal(expected, state.LaterOutcomes.Select(o => o.Expiry!.Value));

        // A round later the nearest is asked again; the later ones are not.
        int chainsSoFar = handler.Chains.Count;
        var nextRound = await recorder.RecordLaterExpiriesAsync(new[] { "NIFTY" }, nearest, round.AddMinutes(1), round.AddMinutes(2), every, CancellationToken.None);
        Assert.Empty(nextRound);
        Assert.Equal(chainsSoFar, handler.Chains.Count);

        // Five rounds on, they are due again.
        var fifth = await recorder.RecordLaterExpiriesAsync(new[] { "NIFTY" }, nearest, round.AddMinutes(5), round.AddMinutes(6), every, CancellationToken.None);
        Assert.Equal(expected, fifth.Select(o => o.Expiry!.Value));
    }

    [Fact]
    public async Task Later_expiries_beyond_the_rounds_chain_budget_wait_for_the_next_round()
    {
        var (db, handler, today, expiries) = await NiftyWeekliesAsync();
        await using var _ = db;
        var state = State();
        var recorder = Recorder(db, handler, state, marketOpen: true, QuickGate());
        var every = TimeSpan.FromMinutes(5);
        var round = DateTime.UtcNow;
        var expected = DhanChainRows.LaterExpiries(expiries, today);
        Assert.NotEmpty(expected);

        var nearest = await recorder.RecordAsync(new[] { "NIFTY" }, onlyOpenMarkets: true, CancellationToken.None);
        int chains = handler.Chains.Count;

        // The nearest ones used the round's whole budget: nothing more is asked of
        // Dhan, and nothing is marked asked, so it is all still due.
        Assert.Empty(await recorder.RecordLaterExpiriesAsync(
            new[] { "NIFTY" }, nearest, round, round.AddMinutes(1), every, CancellationToken.None, maxCalls: 0));
        Assert.Equal(chains, handler.Chains.Count);
        Assert.All(expected, e => Assert.Null(state.LaterAskedInRound("NIFTY", e)));

        // Next round, with room for one chain, exactly one is asked for.
        var next = await recorder.RecordLaterExpiriesAsync(
            new[] { "NIFTY" }, nearest, round.AddMinutes(1), round.AddMinutes(2), every, CancellationToken.None, maxCalls: 1);
        Assert.Equal(expected[0], Assert.Single(next).Expiry);
        Assert.Equal(chains + 1, handler.Chains.Count);
        Assert.Equal(10, new DhanChainPollerSettings().ChainCallsPerRound);
    }

    [Fact]
    public async Task No_later_expiry_is_asked_for_when_the_nearest_was_not_recorded_or_the_underlying_is_not_listed()
    {
        var (db, handler, _, _) = await NiftyWeekliesAsync();
        await using var _db = db;
        var recorder = Recorder(db, handler, State(), marketOpen: true, QuickGate());
        var round = DateTime.UtcNow;
        var nearest = await recorder.RecordAsync(new[] { "NIFTY" }, onlyOpenMarkets: true, CancellationToken.None);
        int chains = handler.Chains.Count;

        // The nearest failed: a later expiry alone would be read as the nearest.
        var failed = new[] { nearest[0] with { State = "failed" } };
        Assert.Empty(await recorder.RecordLaterExpiriesAsync(new[] { "NIFTY" }, failed, round, round.AddMinutes(1), TimeSpan.FromMinutes(5), CancellationToken.None));
        // Recorded, but its underlying is not one of those configured for later expiries.
        Assert.Empty(await recorder.RecordLaterExpiriesAsync(new[] { "SENSEX" }, nearest, round, round.AddMinutes(1), TimeSpan.FromMinutes(5), CancellationToken.None));
        Assert.Equal(chains, handler.Chains.Count);
    }

    [Fact]
    public async Task No_later_capture_starts_without_its_reserve_before_the_next_round_and_it_stays_due()
    {
        var (db, handler, today, expiries) = await NiftyWeekliesAsync();
        await using var _ = db;
        var state = State();
        var recorder = Recorder(db, handler, state, marketOpen: true, QuickGate());
        var nearest = await recorder.RecordAsync(new[] { "NIFTY" }, onlyOpenMarkets: true, CancellationToken.None);
        int chains = handler.Chains.Count;
        var round = DateTime.UtcNow;

        var outcomes = await recorder.RecordLaterExpiriesAsync(
            new[] { "NIFTY" }, nearest, round, DateTime.UtcNow.AddSeconds(9), TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.Empty(outcomes);
        Assert.Equal(chains, handler.Chains.Count);
        Assert.All(DhanChainRows.LaterExpiries(expiries, today), e => Assert.Null(state.LaterAskedInRound("NIFTY", e)));
    }

    [Fact]
    public async Task A_later_capture_still_waiting_on_Dhan_at_the_deadline_is_cut_short()
    {
        var (db, handler, today, expiries) = await NiftyWeekliesAsync();
        await using var _ = db;
        var state = State();
        var recorder = Recorder(db, handler, state, marketOpen: true, QuickGate(), laterReserve: TimeSpan.FromMilliseconds(100));
        var nearest = await recorder.RecordAsync(new[] { "NIFTY" }, onlyOpenMarkets: true, CancellationToken.None);
        var held = DhanChainRows.LaterExpiries(expiries, today)[0];
        handler.HoldExpiry = held.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var round = DateTime.UtcNow;
        var deadline = round.AddMilliseconds(1500);

        var outcomes = await recorder.RecordLaterExpiriesAsync(new[] { "NIFTY" }, nearest, round, deadline, TimeSpan.FromMinutes(5), CancellationToken.None);

        // Back at the deadline, give or take a busy test runner's timers (unanswered, it would wait
        // out HttpClient's 100 s), with nothing stored for it and nothing more asked. Ten seconds of
        // slack: a loaded macOS runner took 2.0 s past it on 29 Oct, still nowhere near 100 s.
        Assert.True(DateTime.UtcNow < deadline.AddSeconds(10), $"returned {(DateTime.UtcNow - deadline).TotalMilliseconds:0} ms after the deadline");
        var cut = Assert.Single(outcomes);
        Assert.Equal("cut-short", cut.State);
        Assert.Equal(held, cut.Expiry);
        Assert.False(await db.OptionChainSnapshots.AnyAsync(x => x.ExpiryDate == held));
        // It counts as asked: a Dhan that keeps hanging costs one call an interval, no more.
        Assert.Equal(round, state.LaterAskedInRound("NIFTY", held));
    }

    [Fact]
    public async Task Later_expiries_take_the_same_chain_slot_as_the_nearest_ones()
    {
        var (db, handler, _, _) = await NiftyWeekliesAsync();
        await using var _ = db;
        var recorder = Recorder(db, handler, State(), marketOpen: true, QuickGate(chainMilliseconds: 300));
        var round = DateTime.UtcNow;

        var nearest = await recorder.RecordAsync(new[] { "NIFTY" }, onlyOpenMarkets: true, CancellationToken.None);
        await recorder.RecordLaterExpiriesAsync(new[] { "NIFTY" }, nearest, round, round.AddMinutes(1), TimeSpan.FromMinutes(5), CancellationToken.None);

        // The nearest's chain, then each later one: never two inside one 300 ms slot (less a
        // Windows timer tick of slack). Without the shared gate they would be a millisecond apart.
        Assert.True(handler.Chains.Count >= 2);
        var sent = handler.Chains.Select(c => c.SentUtc).Order().ToList();
        for (int i = 1; i < sent.Count; i++)
            Assert.True(sent[i] - sent[i - 1] >= TimeSpan.FromMilliseconds(250), $"chains {i - 1} and {i} were {(sent[i] - sent[i - 1]).TotalMilliseconds:0} ms apart");
    }

    // ---------------------------------------------------------------- the switch

    // 11:28 IST on Thursday 2026-09-24: the restart that ended that day's chain.
    private static readonly DateTimeOffset RestartOn24Sep = new(2026, 9, 24, 5, 58, 0, TimeSpan.Zero);

    [Fact]
    public async Task Switched_on_today_it_survives_a_restart()
    {
        var store = new MemoryStore();
        var clock = new Clock(RestartOn24Sep.AddHours(-2.5)); // 08:58 IST, the morning job
        await State(store, clock).SwitchAsync(on: true, "morning job");

        clock.Now = RestartOn24Sep;
        var afterRestart = State(store, clock);
        await afterRestart.RestoreAsync(CancellationToken.None);

        Assert.True(afterRestart.Enabled);
        Assert.Equal("true on 2026-09-24", store.Values[SystemSettingKeys.DhanChainPollerEnabled]);
    }

    [Fact]
    public async Task Switched_on_yesterday_it_starts_as_configured()
    {
        var store = new MemoryStore { Values = { [SystemSettingKeys.DhanChainPollerEnabled] = "true on 2026-09-23" } };

        var afterRestart = State(store, new Clock(RestartOn24Sep));
        await afterRestart.RestoreAsync(CancellationToken.None);

        Assert.False(afterRestart.Enabled);
        Assert.Null(afterRestart.TodaysSwitch);
    }

    [Fact]
    public async Task A_stop_clears_it()
    {
        var store = new MemoryStore();
        var clock = new Clock(RestartOn24Sep);
        var state = State(store, clock);
        await state.SwitchAsync(on: true, "morning job");
        await state.SwitchAsync(on: false, "admin");

        var afterRestart = State(store, clock);
        await afterRestart.RestoreAsync(CancellationToken.None);

        Assert.False(state.Enabled);
        Assert.False(afterRestart.Enabled);
        Assert.Equal("false on 2026-09-24", store.Values[SystemSettingKeys.DhanChainPollerEnabled]);
    }

    [Fact]
    public async Task A_stop_today_holds_across_a_restart_even_where_configuration_says_on()
    {
        var store = new MemoryStore();
        var clock = new Clock(RestartOn24Sep);
        await State(store, clock, configured: true).SwitchAsync(on: false, "admin");

        var afterRestart = State(store, clock, configured: true);
        await afterRestart.RestoreAsync(CancellationToken.None);

        Assert.False(afterRestart.Enabled);
    }

    [Fact]
    public async Task The_switch_ends_with_the_IST_day_restart_or_not()
    {
        var clock = new Clock(new DateTimeOffset(2026, 9, 24, 17, 55, 0, TimeSpan.Zero)); // 23:25 IST
        var state = State(new MemoryStore(), clock);
        await state.SwitchAsync(on: true, "admin");
        Assert.True(state.Enabled);

        clock.Now = clock.Now.AddMinutes(40); // 00:05 IST on the 25th
        Assert.False(state.Enabled);
    }

    [Fact]
    public async Task A_store_that_cannot_be_read_leaves_configuration_in_charge()
    {
        var store = new MemoryStore { Broken = true };
        var configuredOn = State(store, new Clock(RestartOn24Sep), configured: true);
        var configuredOff = State(store, new Clock(RestartOn24Sep), configured: false);

        await configuredOn.RestoreAsync(CancellationToken.None);
        await configuredOff.RestoreAsync(CancellationToken.None);

        Assert.True(configuredOn.Enabled);
        Assert.False(configuredOff.Enabled);
    }

    [Fact]
    public async Task A_save_that_fails_does_not_fail_the_start()
    {
        var state = State(new MemoryStore { Broken = true }, new Clock(RestartOn24Sep));

        await state.SwitchAsync(on: true, "morning job");

        Assert.True(state.Enabled);
    }

    [Fact]
    public async Task A_start_that_arrives_while_the_restart_is_reading_wins()
    {
        var store = new MemoryStore
        {
            Values = { [SystemSettingKeys.DhanChainPollerEnabled] = "false on 2026-09-24" },
            HoldReads = new TaskCompletionSource(),
        };
        var state = State(store, new Clock(RestartOn24Sep));

        var restoring = state.RestoreAsync(CancellationToken.None);
        await state.SwitchAsync(on: true, "morning job");
        store.HoldReads.SetResult();
        await restoring;

        Assert.True(state.Enabled);
    }

    [Fact]
    public async Task Off_while_NSE_trades_and_Dhan_is_signed_in_is_said_once_a_day()
    {
        var clock = new Clock(RestartOn24Sep);
        var notifier = new Notifier();
        var poller = Poller(State(new MemoryStore(), clock), clock, notifier, signedIn: true);

        Assert.True(await poller.WarnIfOffWhileNseTradesAsync(CancellationToken.None));
        Assert.False(await poller.WarnIfOffWhileNseTradesAsync(CancellationToken.None));

        var sent = Assert.Single(notifier.Sent);
        Assert.Equal(NotificationCategory.Process, sent.Category);
    }

    [Fact]
    public async Task Off_is_not_said_when_someone_stopped_it_today_or_Dhan_is_signed_out()
    {
        var clock = new Clock(RestartOn24Sep);
        var notifier = new Notifier();
        var stoppedToday = State(new MemoryStore(), clock);
        await stoppedToday.SwitchAsync(on: false, "admin");

        Assert.False(await Poller(stoppedToday, clock, notifier, signedIn: true).WarnIfOffWhileNseTradesAsync(CancellationToken.None));
        Assert.False(await Poller(State(new MemoryStore(), clock), clock, notifier, signedIn: false).WarnIfOffWhileNseTradesAsync(CancellationToken.None));
        Assert.Empty(notifier.Sent);
    }

    // ------------------------------------------------------------------ universe

    [Fact]
    public void At_the_money_strikes_are_counted_in_listed_strikes()
    {
        var ladder = Enumerable.Range(0, 41).Select(i => 23000m + i * 50m).Reverse();

        var picked = DhanUniverseRules.NearestStrikes(ladder, 24013m, eachSide: 2);

        Assert.Equal(new[] { 23900m, 23950m, 24000m, 24050m, 24100m }, picked);
        // At the edge of the ladder the window is cut, not shifted.
        Assert.Equal(new[] { 23000m, 23050m }, DhanUniverseRules.NearestStrikes(ladder, 22000m, eachSide: 1));
        Assert.Empty(DhanUniverseRules.NearestStrikes(ladder, 0m, eachSide: 2));
    }

    [Fact]
    public void On_expiry_day_the_next_expiry_is_streamed_too()
    {
        var expiries = new[] { new DateOnly(2026, 9, 15), new DateOnly(2026, 9, 22), new DateOnly(2026, 9, 29) };

        Assert.Equal(new[] { new DateOnly(2026, 9, 15), new DateOnly(2026, 9, 22) },
            DhanUniverseRules.OptionExpiries(expiries, new DateOnly(2026, 9, 15)));
        Assert.Equal(new[] { new DateOnly(2026, 9, 22) },
            DhanUniverseRules.OptionExpiries(expiries, new DateOnly(2026, 9, 16)));
    }

    [Fact]
    public void Last_prices_are_read_by_segment_and_security_id()
    {
        // The answer Dhan gave on 2026-09-14, an NSE holiday: nothing for indices.
        using var doc = JsonDocument.Parse("""
            {"data":{"IDX_I":{},"MCX_COMM":{"565899":{"last_price":10007},"568245":{"last_price":277.3}}},"status":"success"}
            """);

        var prices = DhanUniverseBuilder.ReadLastPrices(doc.RootElement);

        Assert.Equal(2, prices.Count);
        Assert.Equal(10007m, prices[("MCX_COMM", 565899)]);
        Assert.Equal(277.3m, prices[("MCX_COMM", 568245)]);
    }

    // ------------------------------------------------------------------ fixtures

    private static DhanChainSide Side(
        decimal? lastPrice = null, decimal? previousClose = null, long? oi = null, long? previousOi = null,
        decimal? iv = null, DhanGreeks? greeks = null) =>
        new(0, lastPrice, previousClose, null, null, null, null, null, oi, previousOi, null, null, iv, greeks);

    private static Instrument Option(string symbol, string underlying, DateOnly expiry, decimal strike, string type) => new()
    {
        Symbol = symbol,
        Exchange = "NSE",
        Segment = "FO",
        InstrumentType = type,
        OptionType = type,
        Underlying = underlying,
        ExpiryDate = expiry,
        StrikePrice = strike,
        IsEnabled = true,
    };

    private static TradingDbContext Db() =>
        new(new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase($"dhan-chain-{Guid.NewGuid():N}").Options);

    private static DhanChainPollerState State(MemoryStore? store = null, TimeProvider? clock = null, bool configured = false)
    {
        var scopes = new ServiceCollection()
            .AddSingleton<IProcessSettingsStore>(store ?? new MemoryStore())
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
        return new DhanChainPollerState(
            Options.Create(new DhanSettings { ChainPoller = { Enabled = configured } }),
            scopes,
            NullLogger<DhanChainPollerState>.Instance,
            clock);
    }

    private static DhanChainPoller Poller(DhanChainPollerState state, Clock clock, Notifier notifier, bool signedIn)
    {
        var dhan = signedIn
            ? new BrokerSession { ProviderKey = "dhan", BrokerName = "Dhan", AccessToken = "token", UpdatedUtc = clock.Now.UtcDateTime.AddHours(-3) }
            : null;
        var scopes = new ServiceCollection()
            .AddSingleton<IBrokerSessionStore>(new DhanSessions(dhan))
            .AddSingleton<ISystemNotifier>(notifier)
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
        return new DhanChainPoller(scopes, state, new Sessions(open: true), Options.Create(new DhanSettings()),
            NullLogger<DhanChainPoller>.Instance, clock);
    }

    private static DhanChainRecorder Recorder(
        TradingDbContext db, Routes handler, DhanChainPollerState state, bool marketOpen, DhanRateGate? gate = null, TimeSpan? laterReserve = null)
    {
        var api = new DhanApiClient(
            Options.Create(new DhanSettings { AccessToken = "token" }),
            new Credentials(),
            new NoSessions(),
            new Factory(handler),
            gate ?? new DhanRateGate(),
            NullLogger<DhanApiClient>.Instance);
        var chain = new DhanOptionChainClient(api, NullLogger<DhanOptionChainClient>.Instance);
        return new DhanChainRecorder(chain, api, new OptionChainService(db), db, new Sessions(marketOpen), state, NullLogger<DhanChainRecorder>.Instance)
        {
            LaterReserve = laterReserve ?? DhanLaterExpirySchedule.Reserve,
        };
    }

    /// <summary>A chain gate quicker than Dhan's, so a test of several chains takes a second, not ten.</summary>
    private static DhanRateGate QuickGate(int chainMilliseconds = 150) => new(new Dictionary<DhanRateClass, TimeSpan>
    {
        [DhanRateClass.Data] = TimeSpan.Zero,
        [DhanRateClass.Quote] = TimeSpan.Zero,
        [DhanRateClass.OptionChain] = TimeSpan.FromMilliseconds(chainMilliseconds),
    });

    /// <summary>
    /// NIFTY listed weekly from tomorrow for ten weeks, a 24,000 CE and PE for
    /// each expiry in the platform's master, and a Dhan that answers every chain
    /// with that one strike.
    /// </summary>
    private static async Task<(TradingDbContext Db, Routes Handler, DateOnly Today, IReadOnlyList<DateOnly> Expiries)> NiftyWeekliesAsync()
    {
        var today = IstTime.DateOf(DateTime.UtcNow);
        var expiries = Enumerable.Range(0, 10).Select(w => today.AddDays(1 + 7 * w)).ToList();
        var db = Db();
        foreach (var expiry in expiries)
        {
            db.Instruments.AddRange(
                Option($"NSE:NIFTYTEST{expiry:yyMMdd}24000CE", "NIFTY", expiry, 24000m, "CE"),
                Option($"NSE:NIFTYTEST{expiry:yyMMdd}24000PE", "NIFTY", expiry, 24000m, "PE"));
        }
        await db.SaveChangesAsync();

        string listed = string.Join(",", expiries.Select(e => $"\"{e:yyyy-MM-dd}\""));
        var handler = new Routes
        {
            ["/optionchain/expirylist"] = $$"""{"data":[{{listed}}],"status":"success"}""",
            ["/optionchain"] = """
                {"data":{"last_price":24010.5,"oc":{"24000.000000":{
                  "ce":{"last_price":120,"previous_close_price":100,"oi":5000,"previous_oi":4000,"volume":900,"implied_volatility":14.2,"security_id":1},
                  "pe":{"last_price":95,"previous_close_price":110,"oi":7000,"previous_oi":6500,"volume":800,"implied_volatility":13.1,"security_id":2}}}},"status":"success"}
                """,
        };
        return (db, handler, today, expiries);
    }

    /// <summary>Answers by path, longest match first, so "/optionchain" never shadows its expiry list.</summary>
    private sealed class Routes(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler, IEnumerable<KeyValuePair<string, string>>
    {
        private readonly Dictionary<string, string> _bodies = new();
        public List<string> Called { get; } = new();

        /// <summary>Every chain request as (when it was sent, the expiry it asked for).</summary>
        public List<(DateTime SentUtc, string Expiry)> Chains { get; } = new();

        /// <summary>A chain request for this expiry ("yyyy-MM-dd") is never answered: it waits until cancelled.</summary>
        public string? HoldExpiry { get; set; }

        public string this[string path] { set => _bodies[path] = value; }

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _bodies.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            Called.Add(path);
            if (path.EndsWith("/optionchain", StringComparison.Ordinal) && request.Content is not null)
            {
                using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
                string expiry = body.RootElement.GetProperty("Expiry").GetString()!;
                lock (Chains) Chains.Add((DateTime.UtcNow, expiry));
                if (expiry == HoldExpiry) await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            var match = _bodies.Keys.Where(k => path.EndsWith(k, StringComparison.Ordinal)).OrderByDescending(k => k.Length).FirstOrDefault();
            return match is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") }
                : new HttpResponseMessage(status) { Content = new StringContent(_bodies[match], Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Credentials : IBrokerCredentialsProvider
    {
        public Task<BrokerCredentials> GetAsync(string providerKey, long? brokerAccountId = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new BrokerCredentials("1100000001", string.Empty, string.Empty, null, "test", null, null));

        public Task SaveAsync(string providerKey, string clientId, string secretKey, string redirectUri, string updatedBy,
            string? tradingPin = null, long? brokerAccountId = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class NoSessions : IBrokerSessionStore
    {
        public Task<BrokerSession?> GetCurrentAsync(CancellationToken cancellationToken = default) => Task.FromResult<BrokerSession?>(null);
        public Task<BrokerSession?> GetForAccountAsync(long brokerAccountId, CancellationToken cancellationToken = default) => Task.FromResult<BrokerSession?>(null);
        public Task ClearAccountAsync(long brokerAccountId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<BrokerSession?> GetForProviderAsync(string providerKey, CancellationToken cancellationToken = default) => Task.FromResult<BrokerSession?>(null);
        public Task SaveAsync(BrokerSession session, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearAsync(string? providerKey = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Sessions(bool open) : IMarketSessionService
    {
        public MarketSessionInfo GetSessionInfo(DateTime utcNow, string exchange, string segment) => throw new NotSupportedException();
        public bool IsMarketOpen(DateTime utcNow, string exchange, string segment) => open;
        public DateTime GetNextMarketOpenUtc(DateTime utcNow, string exchange, string segment) => throw new NotSupportedException();
    }

    private sealed class DhanSessions(BrokerSession? dhan) : IBrokerSessionStore
    {
        public Task<BrokerSession?> GetForProviderAsync(string providerKey, CancellationToken cancellationToken = default)
            => Task.FromResult(providerKey == "dhan" ? dhan : null);
        public Task<BrokerSession?> GetCurrentAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BrokerSession?> GetForAccountAsync(long brokerAccountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ClearAccountAsync(long brokerAccountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveAsync(BrokerSession session, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ClearAsync(string? providerKey = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>
    /// system_settings in memory. <see cref="Broken"/> fails every read and write,
    /// like a database that is down; <see cref="HoldReads"/> keeps a read waiting
    /// after it has taken its value.
    /// </summary>
    private sealed class MemoryStore : IProcessSettingsStore
    {
        public Dictionary<string, string> Values { get; } = new();
        public bool Broken { get; init; }
        public TaskCompletionSource? HoldReads { get; init; }

        public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            if (Broken) throw new InvalidOperationException("the database is down");
            var value = Values.GetValueOrDefault(key);
            if (HoldReads is not null) await HoldReads.Task;
            return value;
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
        public List<(NotificationCategory Category, string Title)> Sent { get; } = new();

        public Task NotifyAsync(NotificationCategory category, NotificationSeverity severity, string title, string message,
            string? underlying = null, string? symbol = null, long? simulationRunId = null, CancellationToken cancellationToken = default)
        {
            Sent.Add((category, title));
            return Task.CompletedTask;
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
