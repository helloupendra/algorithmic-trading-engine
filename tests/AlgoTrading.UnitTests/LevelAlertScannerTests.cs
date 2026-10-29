using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.LevelAlerts;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The level-alert scanner against stored quotes and chain captures: a cross
/// fires once between two fresh readings of one session, never on a stale or
/// pre-open quote, never outside the exchange's own hours, and a repeating
/// rule's re-arm survives a restart.
/// </summary>
public class LevelAlertScannerTests
{
    private const string Nifty = "NSE:NIFTY50-INDEX";
    private const string Crude = "MCX:CRUDEOIL26NOVFUT";

    /// <summary>IST wall time on Friday 2026-10-30 → UTC.</summary>
    private static DateTime Ist(int h, int m, int s = 0) => At(new DateOnly(2026, 10, 30), h, m, s);

    private static DateTime At(DateOnly day, int h, int m, int s = 0) =>
        new DateTime(day.Year, day.Month, day.Day, h, m, s, DateTimeKind.Utc).AddMinutes(-330);

    [Fact]
    public async Task A_cross_between_two_fresh_quotes_fires_once_and_a_one_shot_rule_then_stops()
    {
        var desk = new Desk();
        long rule = desk.Rule(desk.Admin, LevelAlertKinds.Price, Nifty, LevelAlertConditions.Above, 24800m, note: "breakout watch");

        desk.Quote(Nifty, 24790m, Ist(10, 0, 0));
        Assert.Empty((await desk.ScanAsync(Ist(10, 0, 2))).Fired);   // the first reading is the baseline

        desk.Quote(Nifty, 24812.35m, Ist(10, 0, 5));
        var outcome = await desk.ScanAsync(Ist(10, 0, 7));

        var recorded = Assert.Single(outcome.Fired);
        Assert.True(recorded.OwnerIsAdmin);
        Assert.Null(recorded.Fire.TelegramNote);   // an admin's rule: delivery decides

        using var db = desk.Db();
        var fire = await db.LevelAlertFires.SingleAsync();
        Assert.Equal((rule, desk.Admin, 24812.35m, 24790m), (fire.RuleId, fire.UserId, fire.Value, fire.PreviousValue));
        Assert.Equal(Ist(10, 0, 5), fire.ValueAsOfUtc);
        Assert.Equal("NIFTY crossed above 24,800", fire.Title);
        Assert.Equal("NIFTY crossed above 24,800: 24,812.35 at 10:00:05 IST, from 24,790 at 10:00:00. Note: breakout watch", fire.Message);

        var row = await db.LevelAlertRules.SingleAsync();
        Assert.Equal((LevelAlertStatus.Fired, 1, false), (row.Status, row.FireCount, row.Armed));

        // Down through it and up again: a fired one-shot rule says nothing more.
        desk.Quote(Nifty, 24790m, Ist(10, 0, 10));
        await desk.ScanAsync(Ist(10, 0, 12));
        desk.Quote(Nifty, 24820m, Ist(10, 0, 15));
        Assert.Empty((await desk.ScanAsync(Ist(10, 0, 17))).Fired);
        Assert.Equal(1, await desk.Db().LevelAlertFires.CountAsync());
    }

    [Fact]
    public async Task A_rule_made_with_the_price_already_past_its_level_waits_for_a_real_cross()
    {
        var desk = new Desk();
        desk.Rule(desk.Admin, LevelAlertKinds.Price, Nifty, LevelAlertConditions.Above, 24800m);

        desk.Quote(Nifty, 25000m, Ist(10, 0, 0));
        await desk.ScanAsync(Ist(10, 0, 2));
        desk.Quote(Nifty, 25010m, Ist(10, 0, 5));
        Assert.Empty((await desk.ScanAsync(Ist(10, 0, 7))).Fired);

        desk.Quote(Nifty, 24800m, Ist(10, 5, 0));
        Assert.Empty((await desk.ScanAsync(Ist(10, 5, 2))).Fired);   // down to the level: a tie is not a side
        desk.Quote(Nifty, 24805m, Ist(10, 5, 5));
        Assert.Single((await desk.ScanAsync(Ist(10, 5, 7))).Fired);
    }

    [Fact]
    public async Task A_stale_quote_never_fires()
    {
        const string call = "NSE:NIFTY2611324800CE";
        var desk = new Desk();
        desk.Rule(desk.Admin, LevelAlertKinds.Price, call, LevelAlertConditions.Above, 100m);

        desk.Quote(call, 95m, Ist(10, 0, 0));
        await desk.ScanAsync(Ist(10, 0, 2));

        // The feed hands on a trade from two minutes ago: a price, but not the price now.
        desk.Quote(call, 120m, exchangeUtc: Ist(9, 58, 0), writtenUtc: Ist(10, 0, 6));
        var outcome = await desk.ScanAsync(Ist(10, 0, 7));
        Assert.Empty(outcome.Fired);
        var reading = Assert.Single(outcome.Readings);
        Assert.Equal((LevelReading.Stale, 120m), (reading.State, reading.Value));
        Assert.Equal("the last quote is 2 min old", reading.Detail);

        // Without an exchange stamp, its age is when it was written.
        desk.Quote(call, 120m, exchangeUtc: null, writtenUtc: Ist(10, 0, 6));
        Assert.Empty((await desk.ScanAsync(Ist(10, 1, 30))).Fired);
        Assert.Empty(await desk.Db().LevelAlertFires.ToListAsync());
    }

    [Fact]
    public async Task Outside_every_session_the_scan_reads_nothing_at_all()
    {
        var desk = new Desk();
        desk.Rule(desk.Admin, LevelAlertKinds.Price, Nifty, LevelAlertConditions.Above, 24800m);

        // 08:00 IST: no exchange is open, so the scan must not touch the database.
        var closed = desk.Db();
        closed.Dispose();
        var scanner = new LevelAlertScanner(closed, new LevelAlertReader(closed, desk.Sessions), desk.State, NullLogger<LevelAlertScanner>.Instance);

        var outcome = await scanner.ScanAsync(Ist(8, 0));
        Assert.Empty(outcome.Fired);
    }

    [Fact]
    public async Task Each_symbol_is_read_in_its_own_exchanges_hours_and_a_pre_open_quote_is_not_a_baseline()
    {
        var desk = new Desk();
        desk.Rule(desk.Admin, LevelAlertKinds.Price, Nifty, LevelAlertConditions.Above, 24800m);
        desk.Rule(desk.Admin, LevelAlertKinds.Price, Crude, LevelAlertConditions.Below, 5400m);

        // 09:14: MCX is open since 09:00, NSE is not.
        desk.Quote(Nifty, 24790m, Ist(9, 14, 50));
        var preOpen = await desk.ScanAsync(Ist(9, 14, 55));
        Assert.Equal(LevelReading.Closed, preOpen.Readings.Single(r => r.Target == Nifty).State);

        // At the open the pre-open price is from before the session: not compared, not a baseline.
        var atOpen = await desk.ScanAsync(Ist(9, 15, 5));
        var reading = atOpen.Readings.Single(r => r.Target == Nifty);
        Assert.Equal((LevelReading.Stale, "the last quote is from before the open"), (reading.State, reading.Detail));

        desk.Quote(Nifty, 24810m, Ist(9, 15, 10));
        Assert.Empty((await desk.ScanAsync(Ist(9, 15, 12))).Fired);   // the session's first fresh price: a baseline

        // After NSE's close its quotes are not read, while MCX trades on into the evening.
        desk.Quote(Nifty, 24790m, Ist(15, 30, 1));
        desk.Quote(Crude, 5410m, Ist(15, 30, 1));
        var afterClose = await desk.ScanAsync(Ist(15, 30, 3));
        Assert.Equal(LevelReading.Closed, afterClose.Readings.Single(r => r.Target == Nifty).State);
        Assert.Equal(LevelReading.Live, afterClose.Readings.Single(r => r.Target == Crude).State);

        desk.Quote(Crude, 5395m, Ist(16, 0, 0));
        var evening = await desk.ScanAsync(Ist(16, 0, 2));
        Assert.Equal("CRUDEOIL26NOVFUT crossed below 5,400", Assert.Single(evening.Fired).Fire.Title);
    }

    [Fact]
    public async Task An_overnight_gap_through_the_level_is_a_baseline_not_a_cross()
    {
        var thursday = new DateOnly(2026, 10, 29);
        var desk = new Desk();
        desk.Rule(desk.Admin, LevelAlertKinds.Price, Nifty, LevelAlertConditions.Above, 24800m);

        desk.Quote(Nifty, 24790m, At(thursday, 15, 29, 50));
        await desk.ScanAsync(At(thursday, 15, 29, 55));

        // Friday opens above the level. Nothing traded at 24,800, and whether
        // this fired must not depend on whether the API restarted overnight.
        desk.Quote(Nifty, 24900m, Ist(9, 15, 2));
        Assert.Empty((await desk.ScanAsync(Ist(9, 15, 4))).Fired);

        desk.Quote(Nifty, 24790m, Ist(9, 20, 0));
        await desk.ScanAsync(Ist(9, 20, 2));
        desk.Quote(Nifty, 24810m, Ist(9, 20, 5));
        Assert.Single((await desk.ScanAsync(Ist(9, 20, 7))).Fired);
    }

    [Fact]
    public async Task A_repeating_rule_disarms_on_a_fire_and_its_re_arm_survives_a_restart()
    {
        var desk = new Desk();
        long id = desk.Rule(desk.Admin, LevelAlertKinds.Price, Nifty, LevelAlertConditions.Above, 24800m, repeat: true, rearmBy: 0.2m);
        int second = 0;
        async Task<LevelScanOutcome> Tick(decimal price)
        {
            second += 5;
            desk.Quote(Nifty, price, Ist(11, 0, second));
            return await desk.ScanAsync(Ist(11, 0, second + 1));
        }

        await Tick(24790m);
        Assert.Single((await Tick(24810m)).Fired);
        Assert.False((await desk.Db().LevelAlertRules.SingleAsync()).Armed);   // stored, not just remembered

        // Hovering around the level is one alert, not one per wiggle.
        Assert.Empty((await Tick(24795m)).Fired);
        Assert.Empty((await Tick(24812m)).Fired);

        // An API restart forgets the readings; the stored disarm still holds.
        desk.Restart();
        await Tick(24790m);
        Assert.Empty((await Tick(24805m)).Fired);

        // Back by 0.2% of 24,800 (to 24,750.40 or lower) arms it again.
        Assert.Equal(1, (await Tick(24750m)).Rearmed);
        Assert.True((await desk.Db().LevelAlertRules.SingleAsync()).Armed);
        Assert.Single((await Tick(24801m)).Fired);

        var row = await desk.Db().LevelAlertRules.SingleAsync(r => r.Id == id);
        Assert.Equal((LevelAlertStatus.Active, 2), (row.Status, row.FireCount));
    }

    [Fact]
    public async Task A_pcr_rule_fires_on_the_capture_that_takes_the_nearest_expiry_across_its_level()
    {
        var expiry = new DateOnly(2026, 11, 3);
        var desk = new Desk();
        desk.Rule(desk.Admin, LevelAlertKinds.Pcr, "NIFTY", LevelAlertConditions.Above, 1.30m);

        desk.Capture("NIFTY", expiry, Ist(10, 0, 0), callOi: 1000, putOi: 1250);
        desk.Capture("NIFTY", expiry.AddDays(7), Ist(10, 0, 3), callOi: 1000, putOi: 2000);   // next week's, never read
        var first = await desk.ScanAsync(Ist(10, 0, 20));
        var reading = Assert.Single(first.Readings);
        Assert.Equal((LevelReading.Live, 1.25m, "03 Nov expiry"), (reading.State, reading.Value, reading.Context));

        // The next capture, read at the next scan that is due (more than 15 s after the last read).
        desk.Capture("NIFTY", expiry, Ist(10, 1, 0), callOi: 1000, putOi: 1320);
        Assert.Single((await desk.ScanAsync(Ist(10, 1, 2))).Fired);

        var fire = await desk.Db().LevelAlertFires.SingleAsync();
        Assert.Equal((1.32m, 1.25m, Ist(10, 1, 0)), (fire.Value, fire.PreviousValue, fire.ValueAsOfUtc));
        Assert.Equal("NIFTY PCR went above 1.30", fire.Title);
        Assert.Equal("NIFTY PCR went above 1.30: 1.32 in the 10:01 capture (03 Nov expiry), from 1.25 at 10:00.", fire.Message);
    }

    [Fact]
    public async Task The_chains_are_read_at_most_every_fifteen_seconds_and_a_stale_capture_never_fires()
    {
        var expiry = new DateOnly(2026, 11, 3);
        var desk = new Desk();
        desk.Rule(desk.Admin, LevelAlertKinds.Pcr, "NIFTY", LevelAlertConditions.Below, 1.00m);

        desk.Capture("NIFTY", expiry, Ist(10, 0, 0), callOi: 1000, putOi: 1100);
        await desk.ScanAsync(Ist(10, 0, 5));

        // A capture across the level arrives, but the chains were read 5 s ago: the last reading stands.
        desk.Capture("NIFTY", expiry, Ist(10, 0, 8), callOi: 1000, putOi: 900);
        var early = await desk.ScanAsync(Ist(10, 0, 10));
        Assert.Empty(early.Fired);
        Assert.Equal(1.10m, Assert.Single(early.Readings).Value);

        // The recorder then stops; by the next due read its newest capture is four minutes old.
        var late = await desk.ScanAsync(Ist(10, 4, 10));
        Assert.Empty(late.Fired);
        Assert.Equal(LevelReading.Stale, Assert.Single(late.Readings).State);
    }

    [Fact]
    public async Task A_traders_rule_is_for_the_console_and_every_rule_on_a_symbol_shares_one_reading()
    {
        var desk = new Desk();
        desk.Rule(desk.Trader, LevelAlertKinds.Price, Nifty, LevelAlertConditions.Above, 24800m);
        desk.Rule(desk.Admin, LevelAlertKinds.Price, Nifty, LevelAlertConditions.Touch, 24805m, notify: false);
        desk.Rule(desk.Admin, LevelAlertKinds.Price, Nifty, LevelAlertConditions.Below, 24700m);

        desk.Quote(Nifty, 24790m, Ist(10, 0, 0));
        await desk.ScanAsync(Ist(10, 0, 2));
        desk.Quote(Nifty, 24810m, Ist(10, 0, 5));
        var outcome = await desk.ScanAsync(Ist(10, 0, 7));

        Assert.Single(outcome.Readings);
        Assert.Equal(2, outcome.Fired.Count);
        var trader = outcome.Fired.Single(f => f.Fire.UserId == desk.Trader);
        Assert.Equal((false, "A trader's rule: shown in the console only."), (trader.OwnerIsAdmin, trader.Fire.TelegramNote));
        var admin = outcome.Fired.Single(f => f.Fire.UserId == desk.Admin);
        Assert.Equal((LevelAlertConditions.Touch, "Telegram is off for this rule."), (admin.Fire.Condition, admin.Fire.TelegramNote));
    }

    [Fact]
    public async Task An_expired_or_paused_rule_or_one_whose_owner_is_disabled_is_not_watched()
    {
        var desk = new Desk();
        long gone = desk.User("gone", UserRoles.Trader, active: false);
        desk.Rule(desk.Admin, LevelAlertKinds.Price, Nifty, LevelAlertConditions.Above, 24800m, expiresOn: new DateOnly(2026, 10, 29));
        desk.Rule(desk.Admin, LevelAlertKinds.Price, Nifty, LevelAlertConditions.Above, 24800m, status: LevelAlertStatus.Paused);
        desk.Rule(gone, LevelAlertKinds.Price, Nifty, LevelAlertConditions.Above, 24800m);
        desk.Rule(desk.Trader, LevelAlertKinds.Price, Nifty, LevelAlertConditions.Above, 24800m, expiresOn: new DateOnly(2026, 10, 30));

        desk.Quote(Nifty, 24790m, Ist(10, 0, 0));
        await desk.ScanAsync(Ist(10, 0, 2));
        desk.Quote(Nifty, 24810m, Ist(10, 0, 5));
        var outcome = await desk.ScanAsync(Ist(10, 0, 7));

        Assert.Equal(1, outcome.ActiveRules);   // the last day is still a day it may fire
        Assert.Equal(desk.Trader, Assert.Single(outcome.Fired).Fire.UserId);
    }

    [Fact]
    public void The_telegram_message_escapes_the_owners_note()
    {
        var fire = new LevelAlertFire { Message = "NIFTY crossed above 24,800: … Note: <b>buy</b> & hold" };
        Assert.Equal("<b>Level alert</b>\nNIFTY crossed above 24,800: … Note: &lt;b&gt;buy&lt;/b&gt; &amp; hold", LevelAlertText.TelegramMessage([fire]));
    }

    /// <summary>One in-memory database, a session service on an empty holiday calendar, and the service's memory.</summary>
    private sealed class Desk
    {
        private readonly string _name = $"levels-{Guid.NewGuid():N}";

        public IMarketSessionService Sessions { get; } = new MarketSessionService(new Calendar());
        public LevelAlertState State { get; private set; } = new();
        public long Admin { get; }
        public long Trader { get; }

        public Desk()
        {
            Admin = User("admin", UserRoles.Admin);
            Trader = User("asha", UserRoles.Trader);
        }

        /// <summary>A new context on the same database, as each scan gets its own scope in the API.</summary>
        public TradingDbContext Db() => new(new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase(_name).Options);

        public async Task<LevelScanOutcome> ScanAsync(DateTime nowUtc)
        {
            using var db = Db();
            return await new LevelAlertScanner(db, new LevelAlertReader(db, Sessions), State, NullLogger<LevelAlertScanner>.Instance).ScanAsync(nowUtc);
        }

        /// <summary>The API restarting: everything in memory is gone, the database stays.</summary>
        public void Restart() => State = new LevelAlertState();

        public long User(string name, string role, bool active = true)
        {
            using var db = Db();
            var user = new AppUser { UserName = name, Email = $"{name}@example.test", Role = role, IsActive = active };
            db.AppUsers.Add(user);
            db.SaveChanges();
            return user.Id;
        }

        public long Rule(
            long userId, string kind, string target, string condition, decimal level,
            bool repeat = false, decimal? rearmBy = null, string? note = null, bool notify = true,
            DateOnly? expiresOn = null, string status = LevelAlertStatus.Active)
        {
            using var db = Db();
            var rule = new LevelAlertRule
            {
                UserId = userId, Kind = kind, Target = target, Condition = condition, Level = level,
                Repeat = repeat, RearmBy = rearmBy, Note = note, Notify = notify, ExpiresOn = expiresOn, Status = status,
            };
            db.LevelAlertRules.Add(rule);
            db.SaveChanges();
            return rule.Id;
        }

        /// <summary>The feed's tick for a symbol: the exchange's stamp, written by the API a moment later.</summary>
        public void Quote(string symbol, decimal ltp, DateTime exchangeUtc) => Quote(symbol, ltp, exchangeUtc, exchangeUtc.AddMilliseconds(200));

        public void Quote(string symbol, decimal ltp, DateTime? exchangeUtc, DateTime writtenUtc)
        {
            using var db = Db();
            var row = db.LiveQuotesLatest.FirstOrDefault(q => q.Symbol == symbol);
            if (row is null) db.LiveQuotesLatest.Add(row = new LiveQuoteLatest { Symbol = symbol, SourceKey = "dhan" });
            row.LastTradedPrice = ltp;
            row.ExchangeTimestampUtc = exchangeUtc;
            row.UpdatedUtc = writtenUtc;
            db.SaveChanges();
        }

        /// <summary>One recorder capture: a single strike carrying the chain's whole OI each side.</summary>
        public void Capture(string underlying, DateOnly expiry, DateTime capturedUtc, long callOi, long putOi)
        {
            using var db = Db();
            foreach (var (type, oi) in new[] { ("CE", callOi), ("PE", putOi) })
            {
                db.OptionChainSnapshots.Add(new OptionChainSnapshot
                {
                    Underlying = underlying, ExpiryDate = expiry, StrikePrice = 24800m, OptionType = type,
                    Symbol = $"NSE:{underlying}{expiry:yyMdd}24800{type}", CapturedUtc = capturedUtc, SpotPrice = 24800m,
                    OpenInterest = oi, SourceKey = "dhan",
                });
            }

            db.SaveChanges();
        }
    }

    private sealed class Calendar : IMarketCalendar
    {
        public bool IsLoaded => true;
        public MarketHoliday? HolidayOn(string exchange, DateOnly date) => null;
        public MarketSpecialSession? SpecialSessionOn(string exchange, DateOnly date) => null;
        public bool HasYear(string exchange, int year) => true;
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
