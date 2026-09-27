using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Security;
using AlgoTrading.Contracts.MarketIntelligence;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using AlgoTrading.Infrastructure.Services.MarketFactors;
using AlgoTrading.Infrastructure.Services.MarketIntelligence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Constants;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static AlgoTrading.UnitTests.MarketIntelligenceTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The read-only API over the recorded tables, and above all its status
/// answer, which the desk checkup reads to decide whether the recorders are
/// alive. What matters there: rows today come from the tables (they survive a
/// restart), a single dead source shows up without marking the whole recorder
/// down, and a backfill's missing count is sessions, not calendar days.
/// </summary>
public class MarketIntelligenceControllerTests
{
    private sealed class FixedTime(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc, TimeSpan.Zero);
    }

    // Mon 28 Sep 2026, 10:00 IST.
    private static readonly DateTime Now = Ist(2026, 9, 28, 10, 0);

    private sealed record Setup(
        MarketIntelligenceController Controller,
        TradingDbContext Db,
        MarketIntelligenceStatus Status,
        MarketIntelligenceQueries Queries,
        IMarketSessionService Sessions,
        TimeProvider Time);

    private static Setup Build(DateTime now, MarketIntelligenceOptions? options = null)
    {
        var db = Db();
        var status = new MarketIntelligenceStatus { StartedUtc = now.AddHours(-2) };
        var sessions = new Sessions();
        var calendar = new Calendar();
        var runner = new DailyBackfillRunner(new ProcessSettingsStore(db), calendar, status, NullLogger<DailyBackfillRunner>.Instance) { Clock = () => now };
        var breadth = new BreadthRecorder(db, new Factory(new FakeWeb()), new NseRequestPacer(TimeSpan.Zero), NullLogger<BreadthRecorder>.Instance);
        var sync = new MarketFactorsSync(db, new Factory(new FakeWeb()), calendar, new MarketFactorsStatus(), NullLogger<MarketFactorsSync>.Instance);
        var queries = new MarketIntelligenceQueries(db, status, runner, breadth, sync, sessions, Options.Create(options ?? new MarketIntelligenceOptions())) { Clock = () => now };
        var time = new FixedTime(now);
        return new Setup(new MarketIntelligenceController(queries, status, sessions, time), db, status, queries, sessions, time);
    }

    private static NewsItem Headline(string title, string category, DateTime firstSeen, string summary = "") => new()
    {
        Source = "test", Category = category, Title = title, Summary = summary, Link = $"https://example.test/{Guid.NewGuid():N}",
        LinkHash = Guid.NewGuid().ToString("N"), FirstSeenUtc = firstSeen,
    };

    private static T Ok<T>(ActionResult<T> result) => Assert.IsType<T>(Assert.IsType<OkObjectResult>(result.Result).Value);

    // ------------------------------------------------------------ who may read

    // Since 28 Sep the reads are for the market-data grant, so the traders'
    // Desk can show them; status and the backfill trigger stay with admins.
    // Driven through the real pipeline (authentication, the platform's
    // policies, RequireModule and the grant table) rather than read off the
    // attributes, because a grant check that is declared and never runs is
    // exactly the failure worth catching.

    private const long AdminId = 1, GrantedTrader = 7, UngrantedTrader = 8;

    public static TheoryData<string> Reads() => new()
    {
        "/api/MarketIntelligence/news",
        "/api/MarketIntelligence/announcements",
        "/api/MarketIntelligence/calendar",
        "/api/MarketIntelligence/global/daily",
        "/api/MarketIntelligence/global/snapshots",
        "/api/MarketIntelligence/breadth",
    };

    [Theory]
    [MemberData(nameof(Reads))]
    public async Task A_trader_with_the_market_data_grant_may_read(string path)
    {
        await using var host = await GrantHost.StartAsync(Build(Now));

        Assert.Equal(HttpStatusCode.OK, await host.GetAsync(path, GrantedTrader));
    }

    [Theory]
    [MemberData(nameof(Reads))]
    public async Task A_trader_without_the_grant_is_refused(string path)
    {
        await using var host = await GrantHost.StartAsync(Build(Now));

        Assert.Equal(HttpStatusCode.Forbidden, await host.GetAsync(path, UngrantedTrader));
        Assert.Equal(HttpStatusCode.Unauthorized, await host.GetAsync(path, userId: null));
    }

    [Fact]
    public async Task Status_and_the_backfill_trigger_stay_admin_only()
    {
        await using var host = await GrantHost.StartAsync(Build(Now));

        Assert.Equal(HttpStatusCode.Forbidden, await host.GetAsync("/api/MarketIntelligence/status", GrantedTrader));
        Assert.Equal(HttpStatusCode.Forbidden, await host.PostAsync("/api/MarketIntelligence/backfill/all", GrantedTrader));
        Assert.Equal(HttpStatusCode.OK, await host.GetAsync("/api/MarketIntelligence/status", AdminId));
        Assert.Equal(HttpStatusCode.OK, await host.GetAsync("/api/MarketIntelligence/news", AdminId));
    }

    [Fact]
    public void The_grant_is_market_data_and_the_admin_endpoints_say_so()
    {
        var type = typeof(MarketIntelligenceController);
        var module = type.GetCustomAttribute<RequireModuleAttribute>();
        Assert.NotNull(module);
        Assert.Equal(PlatformModules.MarketData,
            typeof(RequireModuleAttribute).GetField("_moduleKey", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(module));
        Assert.Null(type.GetCustomAttribute<AuthorizeAttribute>()!.Policy);

        foreach (var action in new[] { nameof(MarketIntelligenceController.Status), nameof(MarketIntelligenceController.Backfill) })
        {
            Assert.Equal(AuthorizationPolicies.AdminOnly, type.GetMethod(action)!.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        }
    }

    /// <summary>
    /// The controller alone, in an in-process host with the API's policies, the
    /// real RequireModule filter and the real grant check over the test's
    /// database. A caller is named by the X-Test-User header.
    /// </summary>
    private sealed class GrantHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly HttpClient _client;

        private GrantHost(WebApplication app)
        {
            _app = app;
            _client = app.GetTestClient();
        }

        public static async Task<GrantHost> StartAsync(Setup setup)
        {
            setup.Db.AppUsers.AddRange(
                new AppUser { Id = AdminId, UserName = "admin", Role = UserRoles.Admin, IsActive = true },
                new AppUser { Id = GrantedTrader, UserName = "trader", Role = UserRoles.Trader, IsActive = true },
                new AppUser { Id = UngrantedTrader, UserName = "rahul", Role = UserRoles.Trader, IsActive = true });
            setup.Db.UserModuleGrants.Add(new UserModuleGrant { UserId = GrantedTrader, ModuleKey = PlatformModules.MarketData, GrantedBy = "admin" });
            await setup.Db.SaveChangesAsync();

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddControllers()
                .AddApplicationPart(typeof(MarketIntelligenceController).Assembly)
                .ConfigureApplicationPartManager(parts =>
                {
                    foreach (var provider in parts.FeatureProviders.OfType<ControllerFeatureProvider>().ToList())
                        parts.FeatureProviders.Remove(provider);
                    parts.FeatureProviders.Add(new Only<MarketIntelligenceController>());
                });
            builder.Services.AddAuthentication(HeaderAuth.Scheme).AddScheme<AuthenticationSchemeOptions, HeaderAuth>(HeaderAuth.Scheme, null);
            builder.Services.AddAuthorizationBuilder().AddPlatformPolicies();
            builder.Services.AddSingleton<IUserAdminService>(new UserAdminService(
                setup.Db, new PasswordHasher<AppUser>(), RecapClockTests.Inert<ITokenValidityService>.Create(), NullLogger<UserAdminService>.Instance));
            builder.Services.AddSingleton(setup.Queries);
            builder.Services.AddSingleton(setup.Status);
            builder.Services.AddSingleton(setup.Sessions);
            builder.Services.AddSingleton(setup.Time);

            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapControllers();
            await app.StartAsync();
            return new GrantHost(app);
        }

        public Task<HttpStatusCode> GetAsync(string path, long? userId) => SendAsync(HttpMethod.Get, path, userId);

        public Task<HttpStatusCode> PostAsync(string path, long? userId) => SendAsync(HttpMethod.Post, path, userId);

        private async Task<HttpStatusCode> SendAsync(HttpMethod method, string path, long? userId)
        {
            using var request = new HttpRequestMessage(method, path);
            if (userId is { } id) request.Headers.Add(HeaderAuth.UserHeader, id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var response = await _client.SendAsync(request);
            return response.StatusCode;
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _app.DisposeAsync();
        }

        private sealed class Only<T> : ControllerFeatureProvider
        {
            protected override bool IsController(TypeInfo typeInfo) => typeInfo.AsType() == typeof(T);
        }
    }

    /// <summary>Signs a request in as the user named by X-Test-User; user 1 holds the Admin role, as its row says.</summary>
    private sealed class HeaderAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Scheme = "Test";
        public const string UserHeader = "X-Test-User";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(UserHeader, out var value) || !long.TryParse(value, out var userId))
                return Task.FromResult(AuthenticateResult.NoResult());

            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString(System.Globalization.CultureInfo.InvariantCulture)) };
            if (userId == AdminId) claims.Add(new Claim(ClaimTypes.Role, UserRoles.Admin));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme)));
        }
    }

    [Fact]
    public async Task Status_counts_today_s_rows_from_the_tables_and_names_each_failing_source()
    {
        var s = Build(Now);
        s.Db.NewsItems.AddRange(
            Headline("a", "india", Now.AddMinutes(-3)),
            Headline("b", "global", Now.AddHours(-9)),               // 01:00 IST today
            Headline("c", "india", Ist(2026, 9, 27, 23, 0)));        // yesterday
        s.Db.MarketQuoteSnapshots.AddRange(Enumerable.Range(0, 17).Select(i => new MarketQuoteSnapshot { Key = $"K{i}", Price = 1, FetchedUtc = Ist(2026, 9, 28, 8, 45), Source = "t" }));
        await s.Db.SaveChangesAsync();

        s.Status.Recorder(MarketIntelligenceNames.News).Finished(Now.AddMinutes(-3), "3 new headline(s)");
        s.Status.Recorder(MarketIntelligenceNames.News).SourceFailed("BBC World", "HTTP 503", Now.AddMinutes(-3));

        var status = Ok(await s.Controller.Status(CancellationToken.None));

        Assert.Equal(Now, status.ServerUtc);
        Assert.Equal(new[] { "news", "announcements", "calendar", "quote-snapshots", "global-daily", "breadth", "news-scoring" }, status.Recorders.Select(r => r.Name));

        var news = status.Recorders.Single(r => r.Name == MarketIntelligenceNames.News);
        Assert.True(news.Enabled);
        Assert.Equal(2, news.RowsToday);
        Assert.Equal(Now.AddMinutes(-3), news.LatestRowUtc);
        Assert.Equal(Now.AddMinutes(-3), news.LastSuccessUtc);
        Assert.Equal(new[] { "BBC World: HTTP 503" }, news.FailingSources);
        Assert.False(news.Overdue);   // one dead feed is not a dead recorder

        Assert.Equal(17, status.Recorders.Single(r => r.Name == MarketIntelligenceNames.QuoteSnapshots).RowsToday);

        // Nothing has succeeded in two hours since 06:00, inside the window: the checkup should hear of it.
        Assert.True(status.Recorders.Single(r => r.Name == MarketIntelligenceNames.Announcements).Overdue);
    }

    [Fact]
    public async Task A_switched_off_recorder_is_never_overdue()
    {
        var s = Build(Now, new MarketIntelligenceOptions { AnnouncementsEnabled = false });

        var status = Ok(await s.Controller.Status(CancellationToken.None));

        var announcements = status.Recorders.Single(r => r.Name == MarketIntelligenceNames.Announcements);
        Assert.False(announcements.Enabled);
        Assert.False(announcements.Overdue);
    }

    [Fact]
    public async Task Status_reports_each_backfill_s_reach_in_sessions()
    {
        var s = Build(Now);
        foreach (var day in new[] { new DateOnly(2026, 9, 23), new DateOnly(2026, 9, 24), new DateOnly(2026, 9, 25) })
            s.Db.MarketBreadthDaily.Add(new MarketBreadthDaily { Exchange = "NSE", Date = day, Advances = 1, Source = "t" });
        s.Db.MarketGlobalDaily.AddRange(
            new MarketGlobalDaily { Symbol = "SPX", Date = new DateOnly(2020, 1, 2), Close = 3257.85m, Source = "t", FetchedUtc = Now },
            new MarketGlobalDaily { Symbol = "SPX", Date = new DateOnly(2026, 9, 25), Close = 7700m, Source = "t", FetchedUtc = Now });
        await s.Db.SaveChangesAsync();
        s.Status.Backfill(MarketIntelligenceNames.Breadth).State = "waiting";

        var status = Ok(await s.Controller.Status(CancellationToken.None));

        var breadth = status.Backfills.Single(b => b.Dataset == MarketIntelligenceNames.Breadth);
        Assert.Equal("waiting", breadth.State);
        Assert.Equal(new DateOnly(2020, 1, 1), breadth.From);
        Assert.Equal(new DateOnly(2026, 9, 23), breadth.FirstDate);
        Assert.Equal(new DateOnly(2026, 9, 25), breadth.LastDate);
        Assert.Equal(3, breadth.Days);

        // Weekdays from 1 Jan 2020 to Fri 25 Sep 2026 (Monday's files are not out at 10:00), less the three stored.
        int sessions = DailyBackfillRunner.Sessions(new DateOnly(2020, 1, 1), new DateOnly(2026, 9, 27), new Calendar()).Count;
        Assert.Equal(sessions - 3, breadth.MissingSessions);

        var participants = status.Backfills.Single(b => b.Dataset == MarketIntelligenceNames.ParticipantOi);
        Assert.Equal(0, participants.Days);
        Assert.Equal(sessions, participants.MissingSessions);

        var global = status.Backfills.Single(b => b.Dataset == MarketIntelligenceNames.GlobalDaily);
        Assert.Null(global.MissingSessions);   // overseas holidays are not NSE's
        Assert.Equal(16, status.GlobalSymbols.Count);
        var spx = status.GlobalSymbols.Single(g => g.Symbol == "SPX");
        Assert.Equal(("^GSPC", 2, new DateOnly(2020, 1, 2)), (spx.SourceSymbol, spx.Rows, spx.FirstDate!.Value));
        Assert.Equal(0, status.GlobalSymbols.Single(g => g.Symbol == "ES").Rows);
    }

    [Fact]
    public async Task News_pages_newest_first_and_filters_by_category_text_and_ist_day()
    {
        var s = Build(Now);
        s.Db.NewsItems.AddRange(
            Headline("RBI Bulletin – September 2026", "india", Ist(2026, 9, 28, 9, 0), "The Reserve Bank released..."),
            Headline("Fed holds rates", "global", Ist(2026, 9, 28, 8, 0)),
            Headline("Rupee opens flat", "india", Ist(2026, 9, 28, 7, 0), "Forex dealers said the rbi was seen..."),
            Headline("Crude slips", "commodities", Ist(2026, 9, 27, 22, 0)),
            Headline("100% up_move", "india", Ist(2026, 9, 27, 12, 0)));
        await s.Db.SaveChangesAsync();

        var page = Ok(await s.Controller.News(null, null, null, null, skip: 0, take: 2));
        Assert.Equal(5, page.Total);
        Assert.Equal(new[] { "RBI Bulletin – September 2026", "Fed holds rates" }, page.Items.Select(i => i.Title));

        var india = Ok(await s.Controller.News(null, null, "india", null, 0, 50));
        Assert.Equal(3, india.Total);

        // Any case, title or summary.
        var rbi = Ok(await s.Controller.News(null, null, null, "rbi", 0, 50));
        Assert.Equal(new[] { "RBI Bulletin – September 2026", "Rupee opens flat" }, rbi.Items.Select(i => i.Title));

        // % and _ are text, not wildcards.
        Assert.Equal(1, Ok(await s.Controller.News(null, null, null, "0% up_", 0, 50)).Total);

        // A date is that IST day, whole.
        var sunday = Ok(await s.Controller.News("2026-09-27", "2026-09-27", null, null, 0, 50));
        Assert.Equal(new[] { "Crude slips", "100% up_move" }, sunday.Items.Select(i => i.Title));

        // What was known before 08:50 on Monday.
        var before = Ok(await s.Controller.News("2026-09-28", "2026-09-28T08:50:00+05:30", null, null, 0, 50));
        Assert.Equal(new[] { "Fed holds rates", "Rupee opens flat" }, before.Items.Select(i => i.Title));
    }

    [Theory]
    [InlineData("2026-09-28T08:50:00", 0, 50)]   // no offset: IST or UTC? refused, not guessed
    [InlineData("yesterday", 0, 50)]
    [InlineData(null, -1, 50)]
    [InlineData(null, 0, 0)]
    [InlineData(null, 0, 501)]
    public async Task A_bad_news_query_is_refused(string? from, int skip, int take)
    {
        var s = Build(Now);
        var result = await s.Controller.News(from, null, null, null, skip, take);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public void An_instant_needs_an_offset_and_a_date_is_an_ist_day()
    {
        Assert.True(MarketIntelligenceController.TryInstant("2026-09-28", endOfDay: false, out var start));
        Assert.Equal(new DateTime(2026, 9, 27, 18, 30, 0, DateTimeKind.Utc), start);
        Assert.True(MarketIntelligenceController.TryInstant("2026-09-28", endOfDay: true, out var end));
        Assert.Equal(new DateTime(2026, 9, 28, 18, 29, 59, DateTimeKind.Utc), end);
        Assert.True(MarketIntelligenceController.TryInstant("2026-09-28T08:50:00+05:30", false, out var at));
        Assert.Equal(new DateTime(2026, 9, 28, 3, 20, 0, DateTimeKind.Utc), at);
        Assert.True(MarketIntelligenceController.TryInstant("2026-09-28T03:20:00Z", false, out var z));
        Assert.Equal(at, z);
        Assert.False(MarketIntelligenceController.TryInstant("2026-09-28T08:50:00", false, out _));
    }

    [Fact]
    public async Task Announcements_filter_by_symbol_in_any_case()
    {
        var s = Build(Now);
        s.Db.CorporateAnnouncements.AddRange(
            NseCorporateParsers.ParseAnnouncements(Fixture("nse_announcements_latest.json"), Ist(2026, 9, 27, 19, 0)));
        await s.Db.SaveChangesAsync();

        var federal = Ok(await s.Controller.Announcements("federalbnk", null, null));
        var row = Assert.Single(federal.Items);
        Assert.Equal("FEDERALBNK", row.Symbol);
        Assert.Equal(new DateTime(2026, 9, 27, 13, 17, 28, DateTimeKind.Utc), row.AnnouncedUtc);

        var mcl = Ok(await s.Controller.Announcements("MCL", null, null));
        Assert.Equal(3, mcl.Total);
        Assert.True(mcl.Items[0].AnnouncedUtc > mcl.Items[1].AnnouncedUtc);
    }

    [Fact]
    public async Task Snapshots_of_a_day_are_that_ist_day_s()
    {
        var s = Build(Now);
        s.Db.MarketQuoteSnapshots.AddRange(
            new MarketQuoteSnapshot { Key = "GIFTNIFTY", Price = 23333m, FetchedUtc = Ist(2026, 9, 28, 0, 10), Source = "nseix" },
            new MarketQuoteSnapshot { Key = "GIFTNIFTY", Price = 23350m, FetchedUtc = Ist(2026, 9, 28, 8, 45), Source = "nseix" },
            new MarketQuoteSnapshot { Key = "SPX", Price = 7700m, FetchedUtc = Ist(2026, 9, 28, 8, 45), Source = "yahoo:^GSPC" },
            new MarketQuoteSnapshot { Key = "GIFTNIFTY", Price = 23300m, FetchedUtc = Ist(2026, 9, 27, 23, 59), Source = "nseix" });
        await s.Db.SaveChangesAsync();

        var gift = Ok(await s.Controller.Snapshots("giftnifty", new DateOnly(2026, 9, 28), CancellationToken.None));
        Assert.Equal(new[] { 23333m, 23350m }, gift.Select(g => g.Price));
        Assert.Equal(3, Ok(await s.Controller.Snapshots(null, null, CancellationToken.None)).Count);
    }

    [Fact]
    public void A_backfill_asked_for_in_market_hours_waits_for_15_40()
    {
        var s = Build(Now);

        var accepted = Assert.IsType<AcceptedResult>(s.Controller.Backfill("all").Result);
        var answer = Assert.IsType<BackfillRequestResponse>(accepted.Value);

        Assert.False(answer.StartsNow);
        Assert.Contains("15:40", answer.Message);
        Assert.Equal(MarketIntelligenceNames.Backfills, answer.Datasets);
        Assert.All(MarketIntelligenceNames.Backfills, d => Assert.True(s.Status.Backfill(d).Requested));
    }

    [Fact]
    public void A_backfill_asked_for_in_the_evening_starts_now_and_an_unknown_one_is_refused()
    {
        var s = Build(Ist(2026, 9, 28, 19, 0));

        var answer = Assert.IsType<BackfillRequestResponse>(Assert.IsType<AcceptedResult>(s.Controller.Backfill("Breadth").Result).Value);
        Assert.True(answer.StartsNow);
        Assert.Equal(new[] { MarketIntelligenceNames.Breadth }, answer.Datasets);
        Assert.False(s.Status.Backfill(MarketIntelligenceNames.ParticipantOi).Requested);

        Assert.IsType<BadRequestObjectResult>(s.Controller.Backfill("news").Result);
    }
}
