using System.Security.Claims;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Alerts;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.LevelAlerts;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The level-alert endpoints as the console calls them: the owner comes from
/// the token, a trader never sees or touches another's rules or fires (and is
/// told 404, not 403), an admin sees everyone's, and only what the platform
/// quotes can be watched.
/// </summary>
public class LevelAlertsControllerTests
{
    private const string Nifty = "NSE:NIFTY50-INDEX";

    private static SaveLevelAlertRuleRequest Price(
        string target = Nifty, string condition = "above", decimal level = 24800m,
        bool repeat = false, decimal? rearmBy = null, string? note = null, bool active = true) =>
        new() { Kind = "price", Target = target, Condition = condition, Level = level, Repeat = repeat, RearmBy = rearmBy, Note = note, Active = active };

    [Fact]
    public async Task A_trader_cannot_see_edit_or_delete_another_traders_rule()
    {
        var desk = new Desk();
        var created = Ok(await desk.Controller(desk.Asha).CreateRule(Price(" nse:nifty50-index "), default));
        Assert.Equal((desk.Asha, Nifty, "NIFTY crosses above 24,800"), (created.UserId, created.Target, created.Description));

        var ravi = desk.Controller(desk.Ravi);
        Assert.Empty(Ok(await ravi.GetRules(cancellationToken: default)));
        Assert.IsType<NotFoundObjectResult>((await ravi.UpdateRule(created.Id, Price(level: 1m), default)).Result);
        Assert.IsType<NotFoundObjectResult>(await ravi.DeleteRule(created.Id, default));

        var row = await desk.Db.LevelAlertRules.AsNoTracking().SingleAsync();
        Assert.Equal((desk.Asha, 24800m), (row.UserId, row.Level));
    }

    [Fact]
    public async Task A_trader_reads_only_their_own_fires_and_marks_only_their_own_seen()
    {
        var desk = new Desk();
        desk.Fire(desk.Asha, "NIFTY crossed above 24,800");
        desk.Fire(desk.Ravi, "BANKNIFTY crossed below 51,000");
        desk.Fire(desk.Admin, "NIFTY PCR went above 1.30");

        var asha = desk.Controller(desk.Asha);
        Assert.Equal(new[] { "NIFTY crossed above 24,800" }, Ok(await asha.GetFired(cancellationToken: default)).Select(f => f.Title));
        Assert.Equal(1, Ok(await asha.GetSummary(default)).Unseen);

        await asha.MarkSeen(default);
        Assert.Equal(0, Ok(await asha.GetSummary(default)).Unseen);
        Assert.Equal(1, Ok(await desk.Controller(desk.Ravi).GetSummary(default)).Unseen);

        // An admin reads everyone's, or their own; reading a trader's never marks it seen for them.
        var admin = desk.Controller(desk.Admin, UserRoles.Admin);
        Assert.Equal(3, Ok(await admin.GetFired(cancellationToken: default)).Count);
        Assert.Equal(new[] { "NIFTY PCR went above 1.30" }, Ok(await admin.GetFired(mine: true, cancellationToken: default)).Select(f => f.Title));
        await admin.MarkSeen(default);
        Assert.Equal(1, Ok(await desk.Controller(desk.Ravi).GetSummary(default)).Unseen);
    }

    [Fact]
    public async Task An_admin_sees_and_edits_everyones_rules_and_only_an_admins_rule_reaches_telegram()
    {
        var desk = new Desk();
        var ashas = Ok(await desk.Controller(desk.Asha).CreateRule(Price(), default));
        var admin = desk.Controller(desk.Admin, UserRoles.Admin);
        var own = Ok(await admin.CreateRule(new SaveLevelAlertRuleRequest { Kind = "pcr", Target = "nifty", Condition = "above", Level = 1.3m }, default));

        var all = Ok(await admin.GetRules(cancellationToken: default));
        Assert.Equal(new[] { own.Id, ashas.Id }, all.Select(r => r.Id));
        Assert.Equal("asha", all.Single(r => r.Id == ashas.Id).OwnerUserName);
        Assert.False(all.Single(r => r.Id == ashas.Id).TelegramApplies);
        Assert.True(all.Single(r => r.Id == own.Id).TelegramApplies);
        Assert.Equal(new[] { own.Id }, Ok(await admin.GetRules(true, default)).Select(r => r.Id));

        // An admin pausing a trader's rule keeps it the trader's.
        var paused = Ok(await admin.UpdateRule(ashas.Id, Price(active: false), default));
        Assert.Equal((desk.Asha, LevelAlertStatus.Paused), (paused.UserId, paused.Status));
    }

    [Fact]
    public async Task Only_a_symbol_the_platform_knows_and_a_chain_it_records_can_be_watched()
    {
        var desk = new Desk();
        var asha = desk.Controller(desk.Asha);

        var typo = Assert.IsType<BadRequestObjectResult>((await asha.CreateRule(Price("NSE:NIFTY50-INDX"), default)).Result);
        Assert.Contains("not a symbol the platform knows", Message(typo));

        var touch = Assert.IsType<BadRequestObjectResult>((await asha.CreateRule(
            new SaveLevelAlertRuleRequest { Kind = "pcr", Target = "NIFTY", Condition = "touch", Level = 1.3m }, default)).Result);
        Assert.Contains("touch is for prices", Message(touch));

        var unknownChain = Assert.IsType<BadRequestObjectResult>((await asha.CreateRule(
            new SaveLevelAlertRuleRequest { Kind = "pcr", Target = "ZOMATO", Condition = "above", Level = 1m }, default)).Result);
        Assert.Contains("no option chain", Message(unknownChain));

        // A symbol a feed quotes is known even before the instrument master has it.
        desk.Db.LiveQuotesLatest.Add(new LiveQuoteLatest { Symbol = "NSE:SBIN-EQ", LastTradedPrice = 800m, UpdatedUtc = DateTime.UtcNow, SourceKey = "dhan" });
        await desk.Db.SaveChangesAsync();
        Assert.IsType<OkObjectResult>((await asha.CreateRule(Price("NSE:SBIN-EQ", "below", 790m), default)).Result);
    }

    [Fact]
    public async Task A_repeating_rule_gets_the_default_re_arm_and_a_save_that_only_changes_the_note_keeps_it_disarmed()
    {
        var desk = new Desk();
        var asha = desk.Controller(desk.Asha);
        var rule = Ok(await asha.CreateRule(Price(repeat: true), default));
        Assert.Equal((LevelCross.DefaultPriceRearmPercent, "percent"), (rule.RearmBy, rule.RearmUnit));

        var row = await desk.Db.LevelAlertRules.SingleAsync();
        row.Armed = false;   // the service fired it a moment ago
        await desk.Db.SaveChangesAsync();

        var noted = Ok(await asha.UpdateRule(rule.Id, Price(repeat: true, rearmBy: 0.2m, note: "round number"), default));
        Assert.False(noted.Armed);
        var moved = Ok(await asha.UpdateRule(rule.Id, Price(level: 24900m, repeat: true, rearmBy: 0.2m), default));
        Assert.True(moved.Armed);
    }

    [Fact]
    public async Task One_account_holds_at_most_the_limit()
    {
        var desk = new Desk();
        for (int i = 0; i < LevelAlertScanner.MaxRulesPerUser; i++)
        {
            desk.Db.LevelAlertRules.Add(new LevelAlertRule { UserId = desk.Asha, Kind = "price", Target = Nifty, Condition = "above", Level = 24000m + i });
        }

        await desk.Db.SaveChangesAsync();
        var refused = Assert.IsType<BadRequestObjectResult>((await desk.Controller(desk.Asha).CreateRule(Price(), default)).Result);
        Assert.Contains("the most one account may hold", Message(refused));
        Assert.IsType<OkObjectResult>((await desk.Controller(desk.Ravi).CreateRule(Price(), default)).Result);
    }

    [Fact]
    public async Task A_trader_sees_only_what_their_own_rules_watch()
    {
        var desk = new Desk();
        desk.Db.LevelAlertRules.Add(new LevelAlertRule { UserId = desk.Asha, Kind = "price", Target = Nifty, Condition = "above", Level = 24800m });
        desk.Db.LevelAlertRules.Add(new LevelAlertRule { UserId = desk.Ravi, Kind = "price", Target = "NSE:SBIN-EQ", Condition = "below", Level = 790m });
        await desk.Db.SaveChangesAsync();
        desk.State.Observed(DateTime.UtcNow, 2,
        [
            new LevelReading("price", Nifty, "NSE", true, 24810m, DateTime.UtcNow, LevelReading.Live, null),
            new LevelReading("price", "NSE:SBIN-EQ", "NSE", true, 801m, DateTime.UtcNow, LevelReading.Live, null),
        ], []);

        var status = Ok(await desk.Controller(desk.Asha).GetStatus(desk.Telegram, default));
        Assert.Equal(new[] { Nifty }, status.Watching.Select(w => w.Target));
        Assert.Equal(0, status.ActiveRules);   // the desk's count is the operator's business

        var rule = Assert.Single(Ok(await desk.Controller(desk.Asha).GetRules(cancellationToken: default)));
        Assert.Equal((24810m, "above"), (rule.Now?.Value, rule.Side));
    }

    private static T Ok<T>(ActionResult<T> result) => Assert.IsType<T>(Assert.IsType<OkObjectResult>(result.Result).Value);

    private static string Message(ObjectResult result) =>
        result.Value!.GetType().GetProperty("message")!.GetValue(result.Value)!.ToString()!;

    private sealed class Desk
    {
        public TradingDbContext Db { get; } = new(new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase($"level-api-{Guid.NewGuid():N}").Options);
        public LevelAlertState State { get; } = new();
        public TelegramSender Telegram { get; } = new(new PortfolioTestKit.Factory(new PortfolioTestKit.FakeBroker()),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), Microsoft.Extensions.Logging.Abstractions.NullLogger<TelegramSender>.Instance);
        public long Admin { get; }
        public long Asha { get; }
        public long Ravi { get; }

        public Desk()
        {
            Admin = User("owner", UserRoles.Admin);
            Asha = User("asha", UserRoles.Trader);
            Ravi = User("ravi", UserRoles.Trader);
            Db.Instruments.Add(new Instrument { Symbol = Nifty, Exchange = "NSE", Segment = "CM", InstrumentType = "INDEX", IsEnabled = true });
            Db.SaveChanges();
        }

        private long User(string name, string role)
        {
            var user = new AppUser { UserName = name, Email = $"{name}@example.test", Role = role };
            Db.AppUsers.Add(user);
            Db.SaveChanges();
            return user.Id;
        }

        public void Fire(long userId, string title)
        {
            Db.LevelAlertFires.Add(new LevelAlertFire
            {
                RuleId = 1, UserId = userId, FiredUtc = DateTime.UtcNow, Kind = "price", Target = Nifty, Condition = "above",
                Level = 24800m, Value = 24810m, PreviousValue = 24790m, Title = title, Message = title,
            });
            Db.SaveChanges();
        }

        public LevelAlertsController Controller(long userId, string role = UserRoles.Trader) => new(Db, State)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Name, $"user{userId}"), new Claim(ClaimTypes.Role, role)], "test")),
                },
            },
        };
    }
}
