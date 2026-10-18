using System.Net;
using System.Security.Claims;
using System.Text.Json;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Services.Portfolio;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.MarketData;
using AlgoTrading.Contracts.Portfolio;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Portfolio;
using AlgoTrading.Infrastructure.Providers.Angel;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static AlgoTrading.UnitTests.PortfolioTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Linking a user's own broker account, syncing it and reading the report, through the controller as the console
/// calls it: the token stored encrypted and never returned, a redirect bound to the user and the browser that started
/// it, a pasted token checked before it is saved, and one user's figures out of every other user's reach.
/// </summary>
public class PortfolioLinkTests
{
    private const long Asha = 7;
    private const long Ravi = 8;

    // ---------- FYERS, by redirect ----------

    [Fact]
    public async Task Linking_fyers_stores_the_token_encrypted_and_no_response_carries_it()
    {
        var desk = new Desk();
        desk.Broker
            .On("POST", FyersPortfolioClient.TokenPath, HttpStatusCode.OK, """{"s":"ok","code":200,"message":"","access_token":"fyers-access-token-SECRET"}""")
            .Get(FyersPortfolioClient.ProfilePath, Fixture("fyers_profile.json"));

        var asha = desk.Controller(Asha);
        var start = Assert.IsType<OkObjectResult>((await asha.Start("fyers", default)).Result);
        var url = Assert.IsType<PortfolioStartResponse>(start.Value).Url!;
        string state = Uri.UnescapeDataString(url[(url.IndexOf("&state=", StringComparison.Ordinal) + 7)..]);
        Assert.StartsWith("pf-", state);

        // The same state is set on this browser, HttpOnly, and sent only to the callbacks.
        string setCookie = asha.HttpContext.Response.Headers.SetCookie.ToString();
        Assert.Contains($"pf_link_fyers={state}", setCookie);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/api/Portfolio/callback", setCookie, StringComparison.OrdinalIgnoreCase);

        var back = await desk.Anonymous(cookie: ("fyers", state)).Callback("fyers", state, "the-auth-code", "ok", null, default);

        Assert.Equal("/portfolio?connected=fyers", Assert.IsType<RedirectResult>(back).Url);
        var row = await desk.Db.PortfolioConnections.SingleAsync();
        Assert.Equal((Asha, "fyers", "connected", "••••0071"), (row.UserId, row.Broker, row.Status, row.AccountLabel));
        Assert.DoesNotContain("fyers-access-token-SECRET", row.TokenProtected);
        Assert.Equal(new PortfolioToken("fyers-access-token-SECRET", AppId: "APP-100"), desk.Vault.Unprotect(row.TokenProtected));
        Assert.NotNull(row.ExpiresUtc);

        // The account was read once with the new token, the way FYERS wants it sent.
        Assert.Equal("APP-100:fyers-access-token-SECRET", desk.Broker.Requests.Single(r => r.Path == FyersPortfolioClient.ProfilePath).Headers["Authorization"]);

        // Neither the token nor its encrypted form leaves through the API.
        string listed = Json((await asha.Connections(default)).Result);
        Assert.Contains("\"status\":\"connected\"", listed);
        Assert.DoesNotContain("fyers-access-token-SECRET", listed);
        Assert.DoesNotContain(row.TokenProtected, listed);

        var logged = await desk.Db.ActivityLog.SingleAsync();
        Assert.Equal((Asha, "portfolio", "connect:fyers", true), (logged.UserId, logged.Module, logged.Action, logged.Succeeded));
        Assert.DoesNotContain("SECRET", logged.Summary);
    }

    [Fact]
    public async Task A_redirect_in_a_browser_that_did_not_start_the_link_is_refused()
    {
        // Ravi starts a link and sends Asha FYERS's sign-in page: her account must not become his.
        var desk = new Desk();
        var ravi = desk.Controller(Ravi);
        var url = Assert.IsType<PortfolioStartResponse>(Assert.IsType<OkObjectResult>((await ravi.Start("fyers", default)).Result).Value).Url!;
        string state = Uri.UnescapeDataString(url[(url.IndexOf("&state=", StringComparison.Ordinal) + 7)..]);

        var back = await desk.Anonymous(cookie: null).Callback("fyers", state, "ashas-auth-code", "ok", null, default);

        Assert.StartsWith("/portfolio?error=", Assert.IsType<RedirectResult>(back).Url);
        Assert.Empty(await desk.Db.PortfolioConnections.ToListAsync());
        Assert.Empty(desk.Broker.Requests);   // the code was never even exchanged
    }

    [Fact]
    public void A_forged_reused_expired_or_mismatched_state_is_refused()
    {
        var time = new MutableTime();
        var states = new PortfolioLinkStates(time);

        string ashas = states.Issue(Asha, "fyers");
        string ravis = states.Issue(Ravi, "fyers");

        // Someone else's state arriving with this browser's cookie.
        Assert.Null(states.Consume("fyers", ravis, ashas, brokerEchoesState: true).UserId);
        // A state never issued.
        Assert.Null(states.Consume("fyers", "pf-made-up", "pf-made-up", brokerEchoesState: true).UserId);
        // FYERS always sends the state back: a redirect without it is not FYERS's.
        Assert.Null(states.Consume("fyers", null, ashas, brokerEchoesState: true).UserId);
        // A state issued for another broker.
        string angel = states.Issue(Asha, "angel");
        Assert.Null(states.Consume("fyers", angel, angel, brokerEchoesState: true).UserId);

        // The genuine one works once, then never again.
        Assert.Equal(Asha, states.Consume("fyers", ashas, ashas, brokerEchoesState: true).UserId);
        Assert.Null(states.Consume("fyers", ashas, ashas, brokerEchoesState: true).UserId);

        // Ten minutes is the limit.
        string late = states.Issue(Asha, "fyers");
        time.Advance(TimeSpan.FromMinutes(11));
        Assert.Contains("10 minutes", states.Consume("fyers", late, late, brokerEchoesState: true).Refusal);

        // Angel One may drop the state from its redirect: the cookie alone then binds it.
        string angelOnly = states.Issue(Ravi, "angel");
        Assert.Equal(Ravi, states.Consume("angel", null, angelOnly, brokerEchoesState: false).UserId);
    }

    // ---------- Dhan, by a pasted token ----------

    [Fact]
    public async Task A_pasted_dhan_token_is_checked_with_one_read_and_a_refused_one_is_never_saved()
    {
        var desk = new Desk();
        desk.Broker.On("GET", DhanPortfolioClient.ProfilePath, HttpStatusCode.Unauthorized,
            """{"errorType":"Invalid_Authentication","errorCode":"DH-901","errorMessage":"Client ID or user generated access token is invalid or expired."}""");

        var refused = await desk.Controller(Asha).Token("dhan", Paste("1100000042", "eyJ.expired-token-value.sig"), default);

        var bad = Assert.IsType<BadRequestObjectResult>(refused.Result);
        Assert.Contains("web.dhan.co", Json(bad));
        Assert.DoesNotContain("expired-token-value", Json(bad));
        Assert.Single(desk.Broker.Requests);
        Assert.Empty(await desk.Db.PortfolioConnections.ToListAsync());
    }

    [Fact]
    public async Task A_good_dhan_token_is_saved_after_its_one_read_and_shown_masked()
    {
        var desk = new Desk();
        desk.Broker.Get(DhanPortfolioClient.ProfilePath, Fixture("dhan_profile.json"));
        string token = Jwt(desk.Time.GetUtcNow().UtcDateTime.AddHours(20));

        var saved = await desk.Controller(Asha).Token("dhan", Paste("1100000042", token), default);

        var view = Assert.IsType<PortfolioConnectionView>(Assert.IsType<OkObjectResult>(saved.Result).Value);
        Assert.Equal(("connected", "••••0042", "token"), (view.Status, view.AccountLabel, view.ConnectMode));
        Assert.Equal(desk.Time.GetUtcNow().UtcDateTime.AddHours(20), view.ExpiresUtc!.Value, TimeSpan.FromSeconds(1));
        Assert.DoesNotContain(token, Json(saved.Result));
        Assert.Single(desk.Broker.Requests);
        Assert.Equal(new PortfolioToken(token, ClientId: "1100000042"), desk.Vault.Unprotect((await desk.Db.PortfolioConnections.SingleAsync()).TokenProtected));
    }

    [Fact]
    public async Task A_token_that_opens_another_dhan_account_than_the_one_typed_is_refused()
    {
        var desk = new Desk();
        desk.Broker.Get(DhanPortfolioClient.ProfilePath, Fixture("dhan_profile.json"));

        var refused = await desk.Controller(Asha).Token("dhan", Paste("1100009999", Jwt(DateTime.UtcNow.AddHours(5))), default);

        Assert.Contains("opens Dhan account ••••0042, not ••••9999", Json(Assert.IsType<BadRequestObjectResult>(refused.Result)));
        Assert.Empty(await desk.Db.PortfolioConnections.ToListAsync());
    }

    [Fact]
    public async Task Fyers_and_angel_one_take_no_pasted_token()
    {
        var desk = new Desk();

        var refused = await desk.Controller(Asha).Token("fyers", Paste("XA00071", "a-token-from-somewhere-else"), default);

        Assert.IsType<BadRequestObjectResult>(refused.Result);
        Assert.Empty(desk.Broker.Requests);
    }

    // ---------- disconnecting ----------

    [Fact]
    public async Task Disconnecting_with_forget_deletes_that_broker_s_snapshots_and_the_token()
    {
        var desk = new Desk();
        desk.Link(Asha, "fyers");
        desk.Link(Asha, "dhan");
        desk.Link(Ravi, "fyers");
        desk.Snapshot(Asha, "fyers", 2);
        desk.Snapshot(Asha, "dhan", 2, summary: true);
        desk.Snapshot(Ravi, "fyers", 1);
        await desk.Db.SaveChangesAsync();

        var result = await desk.Controller(Asha).Disconnect("fyers", forget: true, default);

        Assert.IsType<NoContentResult>(result);
        Assert.Empty(desk.Db.PortfolioConnections.Where(c => c.UserId == Asha && c.Broker == "fyers"));
        Assert.Empty(desk.Db.PortfolioSnapshots.Where(s => s.UserId == Asha && s.Broker == "fyers"));
        Assert.Equal(2, desk.Db.PortfolioSnapshots.Count(s => s.UserId == Asha && s.Broker == "dhan"));
        // A summary that may have named FYERS's holdings goes with them.
        Assert.All(desk.Db.PortfolioSnapshots.Where(s => s.UserId == Asha), s => Assert.Null(s.SummaryJson));
        // Another user's link and snapshots are untouched.
        Assert.Single(desk.Db.PortfolioSnapshots.Where(s => s.UserId == Ravi));
        Assert.Single(desk.Db.PortfolioConnections.Where(c => c.UserId == Ravi));
    }

    [Fact]
    public async Task Disconnecting_without_forget_keeps_the_history()
    {
        var desk = new Desk();
        desk.Link(Asha, "fyers");
        desk.Snapshot(Asha, "fyers", 3);
        await desk.Db.SaveChangesAsync();

        await desk.Controller(Asha).Disconnect("fyers", forget: false, default);

        Assert.Empty(await desk.Db.PortfolioConnections.ToListAsync());
        Assert.Equal(3, await desk.Db.PortfolioSnapshots.CountAsync());
    }

    // ---------- syncing and reading ----------

    [Fact]
    public async Task A_sync_reads_each_broker_and_a_refused_token_marks_only_that_link_expired()
    {
        var desk = new Desk();
        desk.Link(Asha, "fyers");
        desk.Link(Asha, "dhan");
        await desk.Db.SaveChangesAsync();
        desk.Broker
            .Get(FyersPortfolioClient.HoldingsPath, Fixture("fyers_holdings.json"))
            .Get(FyersPortfolioClient.PositionsPath, Fixture("fyers_positions.json"))
            .Get(FyersPortfolioClient.FundsPath, Fixture("fyers_funds.json"))
            .On("GET", DhanPortfolioClient.HoldingsPath, HttpStatusCode.Unauthorized, """{"errorCode":"DH-901","errorMessage":"expired"}""");

        var synced = Assert.IsType<PortfolioSyncResponse>(Assert.IsType<OkObjectResult>((await desk.Controller(Asha).Sync(default)).Result).Value);

        Assert.Equal([("fyers", true), ("dhan", false)], synced.Results.Select(r => (r.Broker, r.Ok)));
        var links = await desk.Db.PortfolioConnections.ToDictionaryAsync(c => c.Broker);
        Assert.Equal(("connected", null), (links["fyers"].Status, links["fyers"].LastError));
        Assert.Equal("expired", links["dhan"].Status);
        Assert.Contains("web.dhan.co", links["dhan"].LastError);

        var snapshot = await desk.Db.PortfolioSnapshots.SingleAsync();
        Assert.Equal((Asha, "fyers"), (snapshot.UserId, snapshot.Broker));

        var report = Assert.IsType<PortfolioReport>(Assert.IsType<OkObjectResult>((await desk.Controller(Asha).Report(default)).Result).Value);
        Assert.Equal(3, report.Totals.HoldingsCount);
        Assert.Equal(118050m, report.Totals.Current);          // 20 × 2520 + 15 × 3550 + 10 × 1440
        Assert.Equal(2, report.Positions.Count);
        Assert.Equal(1, report.Derivatives.OpenPositions);
        Assert.Contains(report.Sources, s => s.Broker == "fyers" && s.Ok);
        Assert.Contains(report.DataNotes, n => n.StartsWith("Dhan has not been read yet: Dhan no longer accepts this access token"));
        Assert.Null(report.Summary);
        Assert.Equal(PortfolioReportBuilder.Disclaimer, report.Disclaimer);
    }

    [Fact]
    public async Task A_second_sync_within_a_minute_is_refused_with_when_to_try()
    {
        var desk = new Desk();
        var asha = desk.Controller(Asha);

        Assert.IsType<OkObjectResult>((await asha.Sync(default)).Result);
        var again = Assert.IsType<ObjectResult>((await desk.Controller(Asha).Sync(default)).Result);

        Assert.Equal(StatusCodes.Status429TooManyRequests, again.StatusCode);
        // Another user is not held up by it.
        Assert.IsType<OkObjectResult>((await desk.Controller(Ravi).Sync(default)).Result);
    }

    [Fact]
    public async Task A_sync_fetches_missing_daily_closes_for_completed_sessions_only_largest_holding_first()
    {
        var desk = new Desk();
        desk.Link(Asha, "fyers");
        desk.Db.Instruments.AddRange(
            new Instrument { Symbol = "NSE:RELIANCE-EQ", Exchange = "NSE", Segment = "CM", Isin = "INE002A01018", Description = "RELIANCE INDUSTRIES LTD" },
            new Instrument { Symbol = "BSE:RELIANCE-A", Exchange = "BSE", Segment = "CM", Isin = "INE002A01018", Description = "RELIANCE INDUSTRIES LTD" },
            new Instrument { Symbol = "NSE:TCS-EQ", Exchange = "NSE", Segment = "CM", Isin = "INE467B01029", Description = "TATA CONSULTANCY SERV LT" });
        await desk.Db.SaveChangesAsync();
        desk.Broker
            .Get(FyersPortfolioClient.HoldingsPath, Fixture("fyers_holdings.json"))
            .Get(FyersPortfolioClient.PositionsPath, Fixture("fyers_positions.json"))
            .Get(FyersPortfolioClient.FundsPath, Fixture("fyers_funds.json"));

        await desk.Controller(Asha).Sync(default);

        // Friday 11:30 IST: Friday's close is not final, so nothing after Thursday is asked for. NIFTY 50 first (the
        // benchmark), then by value: TCS 53,250, RELIANCE 50,400. INFY is not in the instrument master.
        Assert.Equal(["NSE:NIFTY50-INDEX", "NSE:TCS-EQ", "NSE:RELIANCE-EQ"], desk.History.Asked.Select(r => r.Symbol));
        Assert.All(desk.History.Asked, r => Assert.Equal((new DateOnly(2026, 10, 15), "D"), (r.ToDate, r.Resolution)));

        var held = JsonSerializer.Deserialize<List<BrokerHolding>>((await desk.Db.PortfolioSnapshots.SingleAsync()).HoldingsJson, PortfolioReportService.SnapshotJson)!;
        Assert.Equal(("NSE:RELIANCE-EQ", "RELIANCE INDUSTRIES LTD"), (held[0].CanonicalSymbol, held[0].Name));
        Assert.Null(held.Single(h => h.Isin == "INE009A01021").CanonicalSymbol);
    }

    [Fact]
    public async Task A_user_cannot_read_another_user_s_report_or_history()
    {
        var desk = new Desk();
        desk.Link(Asha, "fyers");
        desk.Snapshot(Asha, "fyers", 1);
        await desk.Db.SaveChangesAsync();

        // Ravi has no link of his own: Asha's figures are not his, whatever he asks.
        Assert.IsType<NotFoundObjectResult>((await desk.Controller(Ravi).Report(default)).Result);
        var history = Assert.IsAssignableFrom<IReadOnlyList<PortfolioHistoryPoint>>(Assert.IsType<OkObjectResult>((await desk.Controller(Ravi).History(90, default)).Result).Value);
        Assert.Empty(history);
        var listed = Assert.IsType<PortfolioConnectionsResponse>(Assert.IsType<OkObjectResult>((await desk.Controller(Ravi).Connections(default)).Result).Value);
        Assert.All(listed.Brokers, b => Assert.Equal("not_connected", b.Status));

        // Asha reads her own.
        var report = Assert.IsType<PortfolioReport>(Assert.IsType<OkObjectResult>((await desk.Controller(Asha).Report(default)).Result).Value);
        Assert.Equal(1, report.Totals.HoldingsCount);
        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<PortfolioHistoryPoint>>(Assert.IsType<OkObjectResult>((await desk.Controller(Asha).History(90, default)).Result).Value));
    }

    [Fact]
    public async Task No_snapshot_yet_is_a_404_with_what_to_do()
    {
        var desk = new Desk();
        desk.Link(Asha, "fyers");
        await desk.Db.SaveChangesAsync();

        var missing = Assert.IsType<NotFoundObjectResult>((await desk.Controller(Asha).Report(default)).Result);

        Assert.Contains("Sync", Json(missing));
    }

    [Fact]
    public async Task Every_broker_is_listed_with_how_it_links()
    {
        var desk = new Desk();

        var listed = Assert.IsType<PortfolioConnectionsResponse>(Assert.IsType<OkObjectResult>((await desk.Controller(Asha).Connections(default)).Result).Value);

        Assert.Equal([("fyers", "redirect"), ("dhan", "token"), ("angel", "redirect")], listed.Brokers.Select(b => (b.Broker, b.ConnectMode)));
        Assert.Equal(["clientId", "accessToken"], listed.Brokers[1].TokenFields.Select(f => f.Key));
        Assert.Contains("Never paste your password, PIN or TOTP", listed.Brokers[1].TokenFields[1].Help);
    }

    [Fact]
    public async Task An_admin_sees_how_many_users_linked_each_broker_and_nothing_else()
    {
        var desk = new Desk();
        desk.Link(Asha, "fyers");
        desk.Link(Ravi, "fyers", expiresUtc: desk.Time.GetUtcNow().UtcDateTime.AddHours(-1));
        desk.Link(Ravi, "dhan");
        desk.Snapshot(Asha, "fyers", 1);
        await desk.Db.SaveChangesAsync();

        var counts = Assert.IsAssignableFrom<IReadOnlyList<PortfolioBrokerCount>>(
            Assert.IsType<OkObjectResult>((await desk.Controller(1, "Admin").AdminCounts(default)).Result).Value);

        Assert.Equal(new PortfolioBrokerCount("fyers", 1, 1, 0), counts[0]);
        Assert.Equal(new PortfolioBrokerCount("dhan", 1, 0, 0), counts[1]);
        Assert.DoesNotContain("RELIANCE", Json(counts));
    }

    [Fact]
    public async Task Snapshots_older_than_ninety_days_are_deleted()
    {
        var desk = new Desk();
        var now = desk.Time.GetUtcNow().UtcDateTime;
        desk.Db.PortfolioSnapshots.AddRange(
            new PortfolioSnapshot { UserId = Asha, Broker = "fyers", TakenUtc = now.AddDays(-91) },
            new PortfolioSnapshot { UserId = Ravi, Broker = "dhan", TakenUtc = now.AddDays(-120) },
            new PortfolioSnapshot { UserId = Asha, Broker = "fyers", TakenUtc = now.AddDays(-89) });
        await desk.Db.SaveChangesAsync();

        var retention = new PortfolioRetentionService(null!, new Monitor<PortfolioSettings>(new PortfolioSettings()),
            NullLogger<PortfolioRetentionService>.Instance, desk.Time);

        Assert.Equal(2, await retention.DeleteOldAsync(desk.Db, default));
        Assert.Equal(now.AddDays(-89), (await desk.Db.PortfolioSnapshots.SingleAsync()).TakenUtc);
    }

    // ---------- the desk ----------

    private static PortfolioTokenRequest Paste(string clientId, string token) =>
        new(new Dictionary<string, string> { ["clientId"] = clientId, ["accessToken"] = token });

    private static string Json(object? value) => JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    /// <summary>The portfolio services wired as the API wires them, over an in-memory database and a fake broker.</summary>
    private sealed class Desk
    {
        public MutableTime Time { get; } = new();
        public TradingDbContext Db { get; } = new(new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase($"portfolio-{Guid.NewGuid():N}").Options);
        public FakeBroker Broker { get; } = new();
        public RecordingHistory History { get; } = new();
        public PortfolioTokenVault Vault { get; } = new(new EphemeralDataProtectionProvider());

        private readonly PortfolioLinkStates _states;
        private readonly PortfolioSyncGate _gate;

        public Desk()
        {
            _states = new PortfolioLinkStates(Time);
            _gate = new PortfolioSyncGate(Time);
        }

        public PortfolioController Controller(long userId, string role = "Trader") =>
            Build(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Name, $"user{userId}"), new Claim(ClaimTypes.Role, role)], "test")),
                null);

        /// <summary>The broker's redirect: no bearer token, only this browser's cookie, if it has one.</summary>
        public PortfolioController Anonymous((string Broker, string State)? cookie) => Build(new ClaimsPrincipal(new ClaimsIdentity()), cookie);

        public void Link(long userId, string broker, DateTime? expiresUtc = null) => Db.PortfolioConnections.Add(new PortfolioConnection
        {
            UserId = userId,
            Broker = broker,
            Status = PortfolioConnectionStatus.Connected,
            AccountLabel = "••••0001",
            TokenProtected = Vault.Protect(new PortfolioToken($"token-{userId}-{broker}", AppId: "APP-100", ClientId: "1100000042")),
            ConnectedUtc = Time.GetUtcNow().UtcDateTime.AddHours(-1),
            ExpiresUtc = expiresUtc ?? Time.GetUtcNow().UtcDateTime.AddHours(12),
        });

        public void Snapshot(long userId, string broker, int count, bool summary = false)
        {
            for (int i = 0; i < count; i++)
            {
                Db.PortfolioSnapshots.Add(new PortfolioSnapshot
                {
                    UserId = userId,
                    Broker = broker,
                    TakenUtc = Time.GetUtcNow().UtcDateTime.AddHours(-i - 1),
                    HoldingsJson = JsonSerializer.Serialize(new[]
                    {
                        new BrokerHolding { Symbol = "NSE:RELIANCE-EQ", Exchange = "NSE", Isin = "INE002A01018", Quantity = 10, AvgPrice = 2400, Ltp = 2500 },
                    }, PortfolioReportService.SnapshotJson),
                    SummaryKey = summary ? "1" : null,
                    SummaryJson = summary ? """{"text":"The portfolio is worth ₹25,000.","model":"m","generatedUtc":"2026-10-16T10:00:00Z"}""" : null,
                });
            }
        }

        private PortfolioController Build(ClaimsPrincipal user, (string Broker, string State)? cookie)
        {
            var settings = new Monitor<PortfolioSettings>(new PortfolioSettings
            {
                Fyers = { AppId = "APP-100", SecretKey = "app-secret", RedirectUri = "https://openfno.test/api/Portfolio/callback/fyers" },
                Angel = { ApiKey = "angel-key" },
            });
            var factory = new Factory(Broker);
            var fyers = new FyersPortfolioClient(factory, new Credentials(), settings);
            var dhan = new DhanPortfolioClient(factory);
            var angel = new AngelPortfolioClient(factory, settings, Options.Create(new AngelSettings()), new AngelPortfolioPacer(TimeSpan.Zero));
            var sessions = new PortfolioSessions(new PortfolioReportTests.FakeCalendar());
            var scopes = new ServiceCollection().AddSingleton<IMarketDataService>(History).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
            var prices = new PortfolioPrices(Db, scopes, sessions, settings, NullLogger<PortfolioPrices>.Instance);
            var reports = new PortfolioReportService(Db, prices, sessions, settings, Time);
            var links = new PortfolioLinkService(Db, fyers, dhan, angel, Vault, _states, NullLogger<PortfolioLinkService>.Instance, Time);
            var sync = new PortfolioSyncService(Db, [fyers, dhan, angel], Vault, prices, reports, new PortfolioSummaryWriter(new NoModel(), Time),
                _gate, settings, NullLogger<PortfolioSyncService>.Instance, Time);

            var context = new DefaultHttpContext { User = user };
            context.Request.Scheme = "https";
            context.Request.Path = "/api/Portfolio/callback/fyers";
            if (cookie is { } c) context.Request.Headers.Cookie = $"{PortfolioLinkStates.CookieName(c.Broker)}={c.State}";

            return new PortfolioController(links, sync, reports, new ConfigurationBuilder().Build())
            {
                ControllerContext = new ControllerContext { HttpContext = context },
            };
        }
    }

    /// <summary>The desk's history connector, with nothing to give: it records what it was asked for.</summary>
    private sealed class RecordingHistory : IMarketDataService
    {
        public List<SyncHistoryRequest> Asked { get; } = [];

        public Task<IReadOnlyList<CandleResponse>> SyncHistoryAsync(SyncHistoryRequest request, CancellationToken cancellationToken = default)
        {
            Asked.Add(request);
            return Task.FromResult<IReadOnlyList<CandleResponse>>([]);
        }

        public Task<IReadOnlyList<CandleResponse>> GetStoredHistoryAsync(GetStoredCandlesRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CandleResponse>>([]);
    }

    private sealed class NoModel : IPortfolioSummaryModel
    {
        public Task<PortfolioModelAnswer> AskAsync(long userId, IReadOnlyList<Infrastructure.Ai.AiMessage> messages, CancellationToken cancellationToken) =>
            Task.FromResult(new PortfolioModelAnswer(false, string.Empty, string.Empty, "No Amazon Bedrock model is switched on."));
    }

    internal sealed class MutableTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 16, 6, 0, 0, TimeSpan.Zero); // Friday 11:30 IST

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
