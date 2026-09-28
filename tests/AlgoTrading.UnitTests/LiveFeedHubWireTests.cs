using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AlgoTrading.Api.Configuration;
using AlgoTrading.Api.Hubs;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.LiveData;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The hub as a browser meets it: a websocket speaking SignalR's JSON protocol
/// to a real server pipeline, with authentication in front of it.
/// </summary>
/// <remarks>
/// The console is written against the method names, argument shapes and
/// payload field names, none of which a test that calls C# methods would
/// notice changing. These go over the wire.
/// </remarks>
public class LiveFeedHubWireTests
{
    private const string Nifty = "NSE:NIFTY50-INDEX";

    [Fact]
    public async Task A_browser_subscribes_and_receives_only_its_prices_in_the_documented_shape()
    {
        await using var host = await WireHost.StartAsync();
        await using var trader = await host.ConnectAsync(WireHost.PlainTrader);
        await using var other = await host.ConnectAsync(WireHost.PlainTrader);

        Assert.Equal(1, (await trader.InvokeAsync("Subscribe", Symbols("nse:nifty50-index", " ", Nifty))).GetInt32());
        Assert.Equal(1, (await other.InvokeAsync("Subscribe", Symbols("NSE:SBIN-EQ"))).GetInt32());

        host.Dispatcher.Enqueue(new UpsertLiveTickRequest
        {
            Symbol = Nifty, LastTradedPrice = 25_010.5m, BidPrice = 25_010m, AskPrice = 25_011m, Volume = 12,
            OpenInterest = 0, ImpliedVolatility = null, RawPayload = "{\"secret\":true}",
            ExchangeTimestampUtc = new DateTime(2026, 9, 28, 4, 0, 0, DateTimeKind.Utc)
        });
        host.Dispatcher.Flush();

        var message = await trader.NextAsync("ReceiveTicks");
        var tick = Assert.Single(message.GetProperty("arguments")[0].EnumerateArray());
        Assert.Equal(
            new[] { "symbol", "lastTradedPrice", "bidPrice", "askPrice", "volume", "openInterest", "impliedVolatility", "exchangeTimestampUtc" },
            tick.EnumerateObject().Select(x => x.Name));
        Assert.Equal(Nifty, tick.GetProperty("symbol").GetString());
        Assert.Equal(25_010.5m, tick.GetProperty("lastTradedPrice").GetDecimal());
        Assert.Equal("2026-09-28T04:00:00Z", tick.GetProperty("exchangeTimestampUtc").GetString());

        Assert.Null(await other.NextOrNullAsync("ReceiveTicks", TimeSpan.FromMilliseconds(300)));

        Assert.Equal(0, (await trader.InvokeAsync("Unsubscribe", Symbols(Nifty))).GetInt32());
    }

    [Fact]
    public async Task Past_the_cap_the_browser_is_told_why()
    {
        await using var host = await WireHost.StartAsync();
        await using var trader = await host.ConnectAsync(WireHost.PlainTrader);

        var many = Enumerable.Range(0, 401).Select(i => $"NSE:S{i}-EQ").ToArray();
        var error = await trader.InvokeErrorAsync("Subscribe", Symbols(many));

        Assert.Contains("at most 400 symbols", error);
        Assert.Equal(0, (await trader.InvokeAsync("Subscribe", Symbols())).GetInt32());
    }

    [Fact]
    public async Task Subscribe_all_answers_false_to_a_plain_trader_and_true_to_an_admin()
    {
        await using var host = await WireHost.StartAsync();
        await using var trader = await host.ConnectAsync(WireHost.PlainTrader);
        await using var admin = await host.ConnectAsync(WireHost.AdminId);

        Assert.False((await trader.InvokeAsync("SubscribeAll")).GetBoolean());
        Assert.True((await admin.InvokeAsync("SubscribeAll")).GetBoolean());

        host.Dispatcher.Enqueue(new UpsertLiveTickRequest { Symbol = "MCX:CRUDEOIL26OCTFUT", LastTradedPrice = 5_400m });
        host.Dispatcher.Flush();

        Assert.NotNull(await admin.NextOrNullAsync("ReceiveTicks", TimeSpan.FromSeconds(5)));
        Assert.Null(await trader.NextOrNullAsync("ReceiveTicks", TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task A_desk_event_reaches_its_owner_and_every_admin_once_and_nobody_else()
    {
        await using var host = await WireHost.StartAsync();
        await using var owner = await host.ConnectAsync(WireHost.Owner);
        await using var ownersOtherTab = await host.ConnectAsync(WireHost.Owner);
        await using var admin = await host.ConnectAsync(WireHost.AdminId);
        await using var stranger = await host.ConnectAsync(WireHost.PlainTrader);
        // Every OnConnectedAsync, group joins included, has run once a call has come back.
        foreach (var tab in new[] { owner, ownersOtherTab, admin, stranger })
            await tab.InvokeAsync("Unsubscribe", Symbols());

        await host.DeskEvents.SendAsync(new DeskEvent(DeskEventKinds.Fill, 42, WireHost.Owner, Nifty,
            new DateTime(2026, 9, 28, 4, 30, 0, DateTimeKind.Utc), "SELL 2 at 99.50"));

        foreach (var tab in new[] { owner, ownersOtherTab, admin })
        {
            var message = await tab.NextAsync("DeskEvent");
            var deskEvent = message.GetProperty("arguments")[0];
            Assert.Equal(
                new[] { "kind", "runId", "userId", "symbol", "atUtc", "detail" },
                deskEvent.EnumerateObject().Select(x => x.Name));
            Assert.Equal("fill", deskEvent.GetProperty("kind").GetString());
            Assert.Equal(WireHost.Owner, deskEvent.GetProperty("userId").GetInt64());
            Assert.Equal("2026-09-28T04:30:00Z", deskEvent.GetProperty("atUtc").GetString());
        }
        Assert.Null(await stranger.NextOrNullAsync("DeskEvent", TimeSpan.FromMilliseconds(300)));

        // The admin's own run: in both groups, told once.
        await host.DeskEvents.SendAsync(new DeskEvent(DeskEventKinds.Run, 43, WireHost.AdminId, null, DateTime.UtcNow, "Stopped: Stopped by admin"));
        Assert.Equal(43, (await admin.NextAsync("DeskEvent")).GetProperty("arguments")[0].GetProperty("runId").GetInt64());
        Assert.Null(await admin.NextOrNullAsync("DeskEvent", TimeSpan.FromMilliseconds(300)));
        Assert.Null(await owner.NextOrNullAsync("DeskEvent", TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task A_caller_without_a_sign_in_is_refused()
    {
        await using var host = await WireHost.StartAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => host.ConnectAsync(userId: null));
    }

    // TEMPORARY with the hub's legacy branch (28 Sep 2026): a console built
    // before 28 Sep connects without ?v=2 and never subscribes.

    [Fact]
    public async Task A_console_from_before_28_Sep_still_gets_the_whole_feed_where_its_account_may()
    {
        await using var host = await WireHost.StartAsync();
        await using var oldAdmin = await host.ConnectAsync(WireHost.AdminId, legacy: true);
        await using var oldGranted = await host.ConnectAsync(WireHost.GrantedTrader, legacy: true);
        await using var oldPlain = await host.ConnectAsync(WireHost.PlainTrader, legacy: true);
        await using var newAdmin = await host.ConnectAsync(WireHost.AdminId);
        // Every OnConnectedAsync has run once a call has come back.
        foreach (var tab in new[] { oldAdmin, oldGranted, oldPlain, newAdmin })
            await tab.InvokeAsync("Unsubscribe", Symbols());

        host.Dispatcher.Enqueue(new UpsertLiveTickRequest { Symbol = Nifty, LastTradedPrice = 25_010.5m });
        host.Dispatcher.Flush();

        foreach (var tab in new[] { oldAdmin, oldGranted })
        {
            var tick = Assert.Single((await tab.NextAsync("ReceiveTicks")).GetProperty("arguments")[0].EnumerateArray());
            Assert.Equal(Nifty, tick.GetProperty("symbol").GetString());
            Assert.Equal(25_010.5m, tick.GetProperty("lastTradedPrice").GetDecimal());
        }
        Assert.Null(await oldPlain.NextOrNullAsync("ReceiveTicks", TimeSpan.FromMilliseconds(300)));
        Assert.Null(await newAdmin.NextOrNullAsync("ReceiveTicks", TimeSpan.FromMilliseconds(100)));
    }

    // Until 28 Sep the token decided role:admin once, at connect, and nothing
    // looked again: a demoted or disabled admin's open console kept every
    // account's desk events for as long as the socket stayed up.

    [Fact]
    public async Task A_connection_is_closed_when_its_token_expires()
    {
        await using var host = await WireHost.StartAsync();
        await using var tab = await host.ConnectAsync(WireHost.AdminId, expiresIn: TimeSpan.FromSeconds(1));
        await using var other = await host.ConnectAsync(WireHost.AdminId);

        Assert.True(await tab.ClosedWithinAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, (await other.InvokeAsync("Unsubscribe", Symbols())).GetInt32());
    }

    [Fact]
    public async Task An_admin_demoted_since_the_token_was_issued_gets_no_one_elses_desk_events()
    {
        await using var host = await WireHost.StartAsync();
        await using var demoted = await host.ConnectAsync(WireHost.DemotedAdmin);
        await using var admin = await host.ConnectAsync(WireHost.AdminId);
        foreach (var tab in new[] { demoted, admin })
            await tab.InvokeAsync("Unsubscribe", Symbols());

        await host.DeskEvents.SendAsync(new DeskEvent(DeskEventKinds.Fill, 42, WireHost.Owner, Nifty, DateTime.UtcNow, "SELL 2 at 99.50"));

        Assert.NotNull(await admin.NextOrNullAsync("DeskEvent", TimeSpan.FromSeconds(5)));
        Assert.Null(await demoted.NextOrNullAsync("DeskEvent", TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task A_disabled_account_is_closed_at_connect()
    {
        await using var host = await WireHost.StartAsync();

        HubWire? gone = null;
        try
        {
            gone = await host.ConnectAsync(WireHost.Disabled);
        }
        catch (InvalidOperationException)
        {
            // Closed before the handshake reply was read: refused all the same.
        }

        if (gone is not null)
        {
            Assert.True(await gone.ClosedWithinAsync(TimeSpan.FromSeconds(5)));
            await gone.DisposeAsync();
        }
    }

    /// <summary>
    /// One argument, the array. Passed bare, a string[] would BE the params
    /// array (covariance) and the hub would be sent one argument per symbol.
    /// </summary>
    internal static object?[] Symbols(params string[] symbols) => new object?[] { symbols };

    /// <summary>The hub on an in-process server, signed in by a request header.</summary>
    internal sealed class WireHost : IAsyncDisposable
    {
        public const long AdminId = 1;
        public const long Owner = 7;
        public const long PlainTrader = 8;
        public const long Disabled = 9;
        public const long GrantedTrader = 11;

        /// <summary>A Trader in the row, whose token was issued while it was an admin.</summary>
        public const long DemotedAdmin = 10;

        private readonly WebApplication _app;
        private readonly TradingDbContext _db;

        private WireHost(WebApplication app, TradingDbContext db)
        {
            _app = app;
            _db = db;
        }

        public LiveTickDispatcher Dispatcher => _app.Services.GetRequiredService<LiveTickDispatcher>();

        public SignalRDeskEventPublisher DeskEvents => (SignalRDeskEventPublisher)_app.Services.GetRequiredService<IDeskEventPublisher>();

        /// <param name="configure">Extra registrations.</param>
        public static async Task<WireHost> StartAsync(Action<IServiceCollection>? configure = null)
        {
            var options = new DbContextOptionsBuilder<TradingDbContext>()
                .UseInMemoryDatabase($"live-feed-wire-{Guid.NewGuid():N}")
                .Options;
            var db = new TradingDbContext(options);
            db.AppUsers.AddRange(
                new AppUser { Id = AdminId, UserName = "admin", Role = UserRoles.Admin, IsActive = true },
                new AppUser { Id = 7, UserName = "coderforchange", Role = UserRoles.Trader, IsActive = true },
                new AppUser { Id = PlainTrader, UserName = "mallory", Role = UserRoles.Trader, IsActive = true },
                new AppUser { Id = Disabled, UserName = "gone", Role = UserRoles.Admin, IsActive = false },
                new AppUser { Id = DemotedAdmin, UserName = "demoted", Role = UserRoles.Trader, IsActive = true },
                new AppUser { Id = GrantedTrader, UserName = "granted", Role = UserRoles.Trader, IsActive = true });
            db.UserModuleGrants.Add(new UserModuleGrant { UserId = GrantedTrader, ModuleKey = PlatformModules.MarketData, GrantedBy = "admin" });
            await db.SaveChangesAsync();

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddSignalR();
            builder.Services.AddAuthentication(HeaderAuth.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, HeaderAuth>(HeaderAuth.SchemeName, null);
            builder.Services.AddAuthorization();
            // One context per hub call, as in the API: tabs connect at once.
            builder.Services.AddScoped(_ => new TradingDbContext(options));
            builder.Services.AddScoped<IUserAdminService>(sp => new UserAdminService(
                sp.GetRequiredService<TradingDbContext>(), new PasswordHasher<AppUser>(), RecapClockTests.Inert<ITokenValidityService>.Create(),
                NullLogger<UserAdminService>.Instance));
            builder.Services.Configure<LiveFeedOptions>(_ => { });
            builder.Services.AddSingleton<LiveFeedSubscriptions>();
            builder.Services.AddSingleton<LiveTickDispatcher>();
            builder.Services.AddSingleton<IDeskEventPublisher, SignalRDeskEventPublisher>();
            configure?.Invoke(builder.Services);

            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapLiveFeedHub();
            await app.StartAsync();
            return new WireHost(app, db);
        }

        /// <param name="expiresIn">When the sign-in expires, as a token's would; none by default.</param>
        /// <param name="legacy">Connect as a console built before 28 Sep does: without <c>?v=2</c>.</param>
        public async Task<HubWire> ConnectAsync(long? userId, TimeSpan? expiresIn = null, bool legacy = false)
        {
            var client = _app.GetTestServer().CreateWebSocketClient();
            client.ConfigureRequest = request =>
            {
                if (userId is { } id) request.Headers[HeaderAuth.UserHeader] = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (expiresIn is { } span)
                    request.Headers[HeaderAuth.ExpiresHeader] = DateTimeOffset.UtcNow.Add(span).ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            };

            // Straight to the websocket, as the console's client does with skipNegotiation.
            string query = legacy ? "" : $"?{LiveFeedHub.ProtocolQueryKey}={LiveFeedHub.CurrentProtocol}";
            var socket = await client.ConnectAsync(new Uri($"ws://localhost{LiveFeedHubSetup.Path}{query}"), CancellationToken.None);
            var wire = new HubWire(socket);
            await wire.HandshakeAsync();
            return wire;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.DisposeAsync();
            await _db.DisposeAsync();
        }
    }

    /// <summary>One browser: SignalR's JSON protocol over a raw websocket, messages ended by 0x1E.</summary>
    internal sealed class HubWire(WebSocket socket) : IAsyncDisposable
    {
        private const char RecordSeparator = '\u001e';
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

        private readonly List<JsonElement> _unread = new();
        private readonly StringBuilder _buffer = new();
        private int _nextId;

        public async Task HandshakeAsync()
        {
            await SendAsync("{\"protocol\":\"json\",\"version\":1}");
            var reply = await ReadAsync(Patience) ?? throw new InvalidOperationException("No handshake reply.");
            if (reply.TryGetProperty("error", out var error))
                throw new InvalidOperationException($"Handshake refused: {error.GetString()}");
        }

        /// <summary>Calls a hub method and returns its result.</summary>
        public async Task<JsonElement> InvokeAsync(string target, object?[]? arguments = null)
        {
            var completion = await CompleteAsync(target, arguments ?? Array.Empty<object?>());
            if (completion.TryGetProperty("error", out var error))
                throw new InvalidOperationException($"{target} failed: {error.GetString()}");
            return completion.TryGetProperty("result", out var result) ? result.Clone() : default;
        }

        /// <summary>Calls a hub method that must fail, and returns the error the browser is shown.</summary>
        public async Task<string> InvokeErrorAsync(string target, object?[] arguments)
        {
            var completion = await CompleteAsync(target, arguments);
            Assert.True(completion.TryGetProperty("error", out var error), $"{target} did not fail.");
            return error.GetString()!;
        }

        /// <summary>Whether the server closes this connection within <paramref name="within"/>.</summary>
        public async Task<bool> ClosedWithinAsync(TimeSpan within)
        {
            var until = DateTime.UtcNow + within;
            while (true)
            {
                var left = until - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) return false;
                try
                {
                    // 7 is the protocol's close message; the socket closing follows it.
                    if (await ReadAsync(left) is { } message && message.TryGetProperty("type", out var type) && type.GetInt32() == 7)
                        return true;
                }
                catch (Exception ex) when (ex is InvalidOperationException or WebSocketException or IOException)
                {
                    return true;
                }
            }
        }

        /// <summary>The next message sent to the client method <paramref name="target"/>.</summary>
        public async Task<JsonElement> NextAsync(string target)
            => await NextOrNullAsync(target, Patience) ?? throw new TimeoutException($"No {target} within {Patience.TotalSeconds:0} s.");

        public async Task<JsonElement?> NextOrNullAsync(string target, TimeSpan within)
        {
            var until = DateTime.UtcNow + within;
            while (true)
            {
                int index = _unread.FindIndex(x => IsInvocationOf(x, target));
                if (index >= 0)
                {
                    var found = _unread[index];
                    _unread.RemoveAt(index);
                    return found;
                }

                var left = until - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) return null;
                if (await ReadAsync(left) is { } message) _unread.Add(message);
            }
        }

        private async Task<JsonElement> CompleteAsync(string target, object?[] arguments)
        {
            string id = (++_nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
            await SendAsync(JsonSerializer.Serialize(new { type = 1, invocationId = id, target, arguments }));

            var until = DateTime.UtcNow + Patience;
            while (DateTime.UtcNow < until)
            {
                var message = await ReadAsync(until - DateTime.UtcNow);
                if (message is not { } m) break;
                if (m.GetProperty("type").GetInt32() == 3 && m.GetProperty("invocationId").GetString() == id) return m;
                _unread.Add(m);
            }
            throw new TimeoutException($"No completion for {target}.");
        }

        private static bool IsInvocationOf(JsonElement message, string target)
            => message.GetProperty("type").GetInt32() == 1
               && message.TryGetProperty("target", out var t) && t.GetString() == target;

        private Task SendAsync(string json)
            => socket.SendAsync(Encoding.UTF8.GetBytes(json + RecordSeparator), WebSocketMessageType.Text, true, CancellationToken.None);

        /// <summary>The next protocol message other than a ping, or null when none comes in time.</summary>
        private async Task<JsonElement?> ReadAsync(TimeSpan within)
        {
            using var timeout = new CancellationTokenSource(within);
            var chunk = new byte[16 * 1024];
            while (true)
            {
                string text = _buffer.ToString();
                int end = text.IndexOf(RecordSeparator);
                if (end >= 0)
                {
                    _buffer.Remove(0, end + 1);
                    var message = JsonDocument.Parse(text[..end]).RootElement.Clone();
                    // 6 is a ping; the handshake reply is {} with no type.
                    if (message.TryGetProperty("type", out var type) && type.GetInt32() == 6) continue;
                    return message;
                }

                WebSocketReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(chunk, timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                    throw new InvalidOperationException("The server closed the connection.");
                _buffer.Append(Encoding.UTF8.GetString(chunk, 0, result.Count));
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
            catch (Exception)
            {
                // The host may already be gone.
            }
            socket.Dispose();
        }
    }

    /// <summary>
    /// Signs a request in as the user named by X-Test-User. Users 1, 9 and 10
    /// carry the Admin claim, as tokens issued while they were admins would;
    /// X-Test-Expires (Unix ms) sets when the sign-in expires, as a JWT's exp does.
    /// </summary>
    private sealed class HeaderAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Test";
        public const string UserHeader = "X-Test-User";
        public const string ExpiresHeader = "X-Test-Expires";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(UserHeader, out var value) || !long.TryParse(value, out var userId))
                return Task.FromResult(AuthenticateResult.NoResult());

            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString(System.Globalization.CultureInfo.InvariantCulture)) };
            if (userId is WireHost.AdminId or WireHost.Disabled or WireHost.DemotedAdmin) claims.Add(new Claim(ClaimTypes.Role, UserRoles.Admin));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));

            var properties = new AuthenticationProperties();
            if (Request.Headers.TryGetValue(ExpiresHeader, out var expires) && long.TryParse(expires, out var unixMs))
                properties.ExpiresUtc = DateTimeOffset.FromUnixTimeMilliseconds(unixMs);

            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, properties, SchemeName)));
        }
    }
}
