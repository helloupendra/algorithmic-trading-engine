using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services;
using AlgoTrading.Api.Services.AiActions;
using AlgoTrading.Api.Services.AiPhone;
using AlgoTrading.Api.Services.AiTools;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Xunit;
using static AlgoTrading.UnitTests.AiTestKit;
using static AlgoTrading.UnitTests.IraActionsTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// "Hey Siri, Ira": the phone token (made once, kept only as a SHA-256, revoked at once, accepted on POST api/Ira/ask
/// alone), the ask through the gateway with source phone, the spoken line for an action proposed, the AI rate limit,
/// the keep-alive of a slow answer, and the phone app's files from wwwroot. Nothing here reaches AWS, NVIDIA or a
/// phone: the models are scripted, the console's sign-in is a JWT signed with a test key.
/// </summary>
public sealed class IraPhoneTests
{
    private readonly string _db = Guid.NewGuid().ToString("N");
    private readonly MovableTime _time = new(Monday1030);
    private readonly FakeHands _hands = new();
    private readonly IraPinGuard _guard;

    public IraPhoneTests()
    {
        FastPins();
        _guard = new IraPinGuard(_time);
    }

    private IraPhoneTokens Tokens(AiSettings? settings = null) =>
        new(NewDb(_db), new AiTestKit.Monitor(settings ?? Settings()), NullLogger<IraPhoneTokens>.Instance, _time);

    private void Seed()
    {
        using var db = NewDb(_db);
        SeedUsers(db);
    }

    // ---------- the token ----------

    [Fact]
    public async Task A_token_is_shown_once_and_only_its_sha256_is_kept()
    {
        Seed();
        var tokens = Tokens();

        var (made, error) = await tokens.CreateAsync(Admin, "upendra", "  Upendra's iPhone ", CancellationToken.None);

        Assert.Null(error);
        string secret = made!.Secret;
        Assert.StartsWith(IraPhoneTokens.Prefix, secret);
        Assert.Equal(IraPhoneTokens.Length, secret.Length);
        Assert.True(IraPhoneTokens.IsShape(secret));
        Assert.Equal("Upendra's iPhone", made.Token.Label);

        await using var db = NewDb(_db);
        var row = await db.AiPhoneTokens.SingleAsync();
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret))), row.TokenHash);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(row));
        Assert.DoesNotContain(secret[IraPhoneTokens.Prefix.Length..], JsonSerializer.Serialize(row));

        // Listed again, it is its row only: the token itself is never shown a second time.
        var listed = await tokens.ListAsync(Admin, CancellationToken.None);
        Assert.Equal(made.Token, Assert.Single(listed));
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(listed));

        var (again, _) = await tokens.CreateAsync(Admin, "upendra", "iPad", CancellationToken.None);
        Assert.NotEqual(secret, again!.Secret);
    }

    [Fact]
    public async Task A_token_opens_until_it_is_revoked_and_only_for_an_active_admin()
    {
        Seed();
        var tokens = Tokens(Settings(s => s.Phone.MaxTokensPerUser = 2));
        var (made, _) = await tokens.CreateAsync(Admin, "upendra", "iPhone", CancellationToken.None);
        string secret = made!.Secret;

        var caller = await tokens.AuthenticateAsync(secret, CancellationToken.None);
        Assert.Equal(new IraPhoneCaller(made.Token.Id, "iPhone", Admin, "upendra"), caller);
        await using (var db = NewDb(_db)) Assert.Equal(Monday1030, (await db.AiPhoneTokens.SingleAsync()).LastUsedUtc);

        // Not a token, a token of the right shape that was never made, a token with one character changed.
        Assert.Null(await tokens.AuthenticateAsync("ira_short", CancellationToken.None));
        Assert.Null(await tokens.AuthenticateAsync(IraPhoneTokens.NewSecret(), CancellationToken.None));
        Assert.Null(await tokens.AuthenticateAsync(secret[..^1] + (secret[^1] == 'A' ? 'B' : 'A'), CancellationToken.None));

        // Revoked: refused from the next use, and not listed.
        Assert.NotNull(await tokens.RevokeAsync(Admin, made.Token.Id, CancellationToken.None));
        Assert.Null(await tokens.AuthenticateAsync(secret, CancellationToken.None));
        Assert.Empty(await tokens.ListAsync(Admin, CancellationToken.None));
        Assert.Null(await tokens.RevokeAsync(Admin, made.Token.Id, CancellationToken.None));

        // Another admin cannot revoke it; a disabled or demoted owner's token opens nothing.
        var (second, _) = await tokens.CreateAsync(Admin, "upendra", "iPad", CancellationToken.None);
        Assert.Null(await tokens.RevokeAsync(OtherAdmin, second!.Token.Id, CancellationToken.None));
        await using (var db = NewDb(_db))
        {
            (await db.AppUsers.SingleAsync(u => u.Id == Admin)).Role = UserRoles.Trader;
            await db.SaveChangesAsync();
        }

        Assert.Null(await tokens.AuthenticateAsync(second.Secret, CancellationToken.None));

        // The cap: two working tokens at most here.
        await using (var db = NewDb(_db))
        {
            (await db.AppUsers.SingleAsync(u => u.Id == Admin)).Role = UserRoles.Admin;
            await db.SaveChangesAsync();
        }

        Assert.NotNull((await tokens.CreateAsync(Admin, "upendra", "Work phone", CancellationToken.None)).Made);
        var (none, refused) = await tokens.CreateAsync(Admin, "upendra", "Third", CancellationToken.None);
        Assert.Null(none);
        Assert.Contains("Revoke one first", refused);
    }

    // ---------- over HTTP, with the console's JWT beside it ----------

    [Fact]
    public async Task A_phone_token_is_accepted_on_the_ask_alone_and_the_console_sign_in_does_not_open_the_ask()
    {
        Seed();
        var ai = Build(Settings(), NewDb(_db));
        ai.Provider.On(Judge1, Answer("Two runs are live: both up after charges."));
        await using var host = await PhoneHost.StartAsync(ai, _db, _time);
        string admin = PhoneHost.Jwt(Admin, "upendra", UserRoles.Admin);

        var made = await host.SendAsync(HttpMethod.Post, "/api/Ira/phones", admin, new { label = "Upendra's iPhone" });
        Assert.Equal(HttpStatusCode.OK, made.Status);
        string secret = JsonNode.Parse(made.Body)!["token"]!.GetValue<string>();

        var answered = await host.SendAsync(HttpMethod.Post, "/api/Ira/ask", secret, new { text = "How are the runs doing?" });
        Assert.Equal(HttpStatusCode.OK, answered.Status);
        var json = JsonNode.Parse(answered.Body)!;
        Assert.Equal("Two runs are live: both up after charges.", json["text"]!.GetValue<string>());
        Assert.Equal("Two runs are live: both up after charges.", json["spoken"]!.GetValue<string>());
        Assert.True(json["callId"]!.GetValue<long>() > 0);

        // Anywhere else the phone token is no credential at all: the AI endpoints, the Phones box, a console ask.
        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Get, "/api/Ai/voice"), (HttpMethod.Get, "/api/Ira/phones"), (HttpMethod.Get, "/api/Ira/actions"),
                     (HttpMethod.Post, "/api/Ai/ask"), (HttpMethod.Get, "/api/Ai/actions/pin"),
                 })
        {
            var other = await host.SendAsync(method, path, secret, method == HttpMethod.Post ? new { messages = new[] { new { role = "user", content = "hi" } } } : null);
            Assert.True(other.Status == HttpStatusCode.Unauthorized, $"{method} {path} answered {(int)other.Status} to a phone token");
        }

        // And the console's own sign-in does not open the phone's endpoint.
        var console = await host.SendAsync(HttpMethod.Post, "/api/Ira/ask", admin, new { text = "How are the runs doing?" });
        Assert.Equal(HttpStatusCode.Unauthorized, console.Status);
        Assert.Contains("\"spoken\"", console.Body);

        // Nothing at all: a 401 with words Siri can say.
        var none = await host.SendAsync(HttpMethod.Post, "/api/Ira/ask", null, new { text = "Hello?" });
        Assert.Equal(HttpStatusCode.Unauthorized, none.Status);
        Assert.Equal(IraPhoneAuthentication.RefusedSpoken, JsonNode.Parse(none.Body)!["spoken"]!.GetValue<string>());

        // A trader cannot make one.
        var trader = await host.SendAsync(HttpMethod.Post, "/api/Ira/phones", PhoneHost.Jwt(Trader, "coderforchange", UserRoles.Trader), new { label = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, trader.Status);
    }

    [Fact]
    public async Task A_revoke_works_at_once_and_the_token_is_never_logged()
    {
        Seed();
        var ai = Build(Settings(), NewDb(_db));
        ai.Provider.On(Judge1, Answer("Yes."));
        await using var host = await PhoneHost.StartAsync(ai, _db, _time);
        string admin = PhoneHost.Jwt(Admin, "upendra", UserRoles.Admin);

        var made = JsonNode.Parse((await host.SendAsync(HttpMethod.Post, "/api/Ira/phones", admin, new { label = "iPhone" })).Body)!;
        string secret = made["token"]!.GetValue<string>();
        long id = made["id"]!.GetValue<long>();

        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Post, "/api/Ira/ask", secret, new { text = "Anything open?" })).Status);
        var listed = await host.SendAsync(HttpMethod.Get, "/api/Ira/phones", admin);
        Assert.DoesNotContain(secret, listed.Body);
        Assert.NotNull(JsonNode.Parse(listed.Body)!["tokens"]![0]!["lastUsedUtc"]);

        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Delete, $"/api/Ira/phones/{id}", admin)).Status);
        var after = await host.SendAsync(HttpMethod.Post, "/api/Ira/ask", secret, new { text = "Anything open?" });
        Assert.Equal(HttpStatusCode.Unauthorized, after.Status);
        Assert.Single(ai.Provider.Requests);

        Assert.Contains(host.Logs.Entries, e => e.Message.Contains("made phone token", StringComparison.Ordinal));
        Assert.Contains(host.Logs.Entries, e => e.Message.Contains("revoked phone token", StringComparison.Ordinal));
        Assert.Contains(host.Logs.Entries, e => e.Message.Contains("Ira phone: upendra asked the Desk Assistant from iPhone", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Logs.Entries, e => e.Message.Contains(secret[IraPhoneTokens.Prefix.Length..], StringComparison.Ordinal));
    }

    // ---------- the ask ----------

    [Fact]
    public async Task The_ask_runs_through_the_gateway_as_the_owner_with_source_phone_and_keeps_the_conversation()
    {
        Seed();
        var ai = Build(Settings(), NewDb(_db));
        ai.Provider.On(Judge1,
            Answer("**NIFTY** is up 0.4% at 25,120 (`get_quotes`, 10:30 IST).\n\n| Index | Last |\n|---|---|\n| NIFTY | 25,120 |"),
            Answer("BANKNIFTY is down 0.2%."));
        await using var host = await PhoneHost.StartAsync(ai, _db, _time);
        string secret = await host.TokenAsync();

        var first = JsonNode.Parse((await host.SendAsync(HttpMethod.Post, "/api/Ira/ask", secret, new { text = "How is NIFTY doing?" })).Body)!;
        var second = JsonNode.Parse((await host.SendAsync(HttpMethod.Post, "/api/Ira/ask", secret, new { text = "And BANKNIFTY?", agent = "desk-assistant" })).Body)!;

        // The table and the citation are for the eye: Siri says the sentence, and where the rest is.
        Assert.Equal($"NIFTY is up 0.4% at 25,120. {IraPhoneAsk.DetailsOnConsole}", first["spoken"]!.GetValue<string>());
        Assert.StartsWith("**NIFTY**", first["text"]!.GetValue<string>());

        await using var db = NewDb(_db);
        var calls = await db.AiCalls.OrderBy(c => c.Id).ToListAsync();
        Assert.Equal(2, calls.Count);
        Assert.All(calls, c => Assert.Equal((AiCatalog.PhoneSource, "upendra", (long?)Admin, AiCatalog.DeskAssistant), (c.Source, c.RequestedBy, c.UserId, c.AgentKey)));
        Assert.StartsWith(IraPhoneConversations.Prefix, calls[0].ConversationId);
        Assert.Equal(calls[0].ConversationId, calls[1].ConversationId);
        Assert.Equal(second["callId"]!.GetValue<long>(), calls[1].Id);

        // The follow-up was asked knowing the first question and its answer, and the prompt says it came from the phone.
        var asked = JsonNode.Parse(ai.Provider.Requests[^1].Body)!["messages"]!.AsArray();
        Assert.Contains(asked, m => m!["content"]!.GetValue<string>() == "How is NIFTY doing?");
        Assert.Equal("And BANKNIFTY?", asked[^1]!["content"]!.GetValue<string>());
        Assert.Contains("from his phone", asked[0]!["content"]!.GetValue<string>());
        Assert.Contains("He may call you Ira.", asked[0]!["content"]!.GetValue<string>());

        // A question without text, or to an agent nobody talks with, is refused with words Siri can say.
        var empty = await host.SendAsync(HttpMethod.Post, "/api/Ira/ask", secret, new { text = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, empty.Status);
        Assert.Equal("I did not get a question. Try again.", JsonNode.Parse(empty.Body)!["spoken"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Post, "/api/Ira/ask", secret, new { text = "Hi", agent = "nobody" })).Status);
    }

    [Fact]
    public async Task An_action_proposed_from_the_phone_is_spoken_as_proposed_waits_for_the_pin_and_is_confirmed_in_the_ira_app()
    {
        Seed();
        var settings = Settings();
        var toolActions = new IraActions(NewDb(_db), _hands, _guard, new AiTestKit.Monitor(settings), NullLogger<IraActions>.Instance, _time);
        await toolActions.SetPinAsync(new IraOwner(Admin, "upendra"), Pin, null, requireCurrent: false, "the console", CancellationToken.None);
        var resolver = new IraActionResolver(NewDb(_db), null!, Registry(), new AiSettingsStore(NewDb(_db)), null, _time);
        var ai = Build(settings, NewDb(_db), new ProposeAgentSwitchTool(toolActions, resolver, _time));
        ai.Provider.On(Judge1,
            Tools((AiToolNames.ProposeAgentSwitch, """{"agent":"news-analyst","on":true}""")),
            Answer("Proposed: switch the News Analyst on. Confirm it with your PIN under this answer."));
        await using var host = await PhoneHost.StartAsync(ai, _db, _time, services => AddActions(services, _hands, _guard, ai.Options, _time));
        string secret = await host.TokenAsync();
        string admin = PhoneHost.Jwt(Admin, "upendra", UserRoles.Admin);

        var answered = await host.SendAsync(HttpMethod.Post, "/api/Ira/ask", secret, new { text = "Switch the news analyst on." });

        Assert.Equal(HttpStatusCode.OK, answered.Status);
        var json = JsonNode.Parse(answered.Body)!;
        await using (var db = NewDb(_db))
        {
            var action = await db.AiActions.SingleAsync();
            Assert.Equal((AiActionStatus.Pending, AiActionChannel.Console), (action.Status, action.Channel));
            Assert.StartsWith(IraPhoneConversations.Prefix, action.ConversationId);
            Assert.Equal(action.Id, json["actionId"]!.GetValue<long>());
            Assert.Equal("Switch the News Analyst on, as AI → Agents does.", action.Summary);
        }

        Assert.Equal("I have proposed: Switch the News Analyst on, as AI Agents does. Confirm it with your PIN in the Ira app.",
            json["spoken"]!.GetValue<string>());
        Assert.Empty(_hands.Calls);

        // The Ira app finds it, and the console's confirm card carries it out with the PIN.
        var waiting = JsonNode.Parse((await host.SendAsync(HttpMethod.Get, "/api/Ira/actions", admin)).Body)!.AsArray();
        long id = Assert.Single(waiting)!["id"]!.GetValue<long>();
        Assert.Equal(json["actionId"]!.GetValue<long>(), id);

        var confirmed = await host.SendAsync(HttpMethod.Post, $"/api/Ai/actions/{id}/confirm", admin, new { pin = Pin });
        Assert.Equal(HttpStatusCode.OK, confirmed.Status);
        Assert.Equal(AiActionKind.AgentSwitch, Assert.Single(_hands.Calls).Kind);
        Assert.Empty(JsonNode.Parse((await host.SendAsync(HttpMethod.Get, "/api/Ira/actions", admin)).Body)!.AsArray());
    }

    [Fact]
    public async Task The_phone_shares_the_console_rate_limit_and_hears_how_long_to_wait()
    {
        Seed();
        var ai = Build(Settings(s => s.PerUserPer10Min = 1), NewDb(_db));
        ai.Provider.On(Judge1, Answer("One."), Answer("Two."));
        await using var host = await PhoneHost.StartAsync(ai, _db, _time);
        string secret = await host.TokenAsync();

        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Post, "/api/Ira/ask", secret, new { text = "First?" })).Status);
        var second = await host.SendAsync(HttpMethod.Post, "/api/Ira/ask", secret, new { text = "Second?" });

        Assert.Equal(HttpStatusCode.TooManyRequests, second.Status);
        Assert.True(second.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        var json = JsonNode.Parse(second.Body)!;
        Assert.StartsWith("That is a lot of questions in a short time. Try again in ", json["spoken"]!.GetValue<string>());
        Assert.Contains("questions in the last ten minutes", json["error"]!.GetValue<string>());

        // The same bucket as the console's: the owner's next typed question is refused too.
        var (lease, refusal) = ai.Limiter.TryAcquire("upendra");
        Assert.Null(lease);
        Assert.NotNull(refusal);
        await using var db = NewDb(_db);
        Assert.Equal(AiCallOutcome.Refused, (await db.AiCalls.OrderBy(c => c.Id).LastAsync()).Outcome);
        Assert.Single(ai.Provider.Requests);
    }

    [Fact]
    public async Task A_slow_answer_is_kept_alive_with_spaces_before_its_json()
    {
        Seed();
        var ai = Build(Settings(), NewDb(_db));
        var slow = Assert.IsType<SseScript>(Answer("Done thinking.")) with { Delay = TimeSpan.FromMilliseconds(400) };
        ai.Provider.On(Judge1, slow);
        await using var host = await PhoneHost.StartAsync(ai, _db, _time);
        string secret = await host.TokenAsync();

        var before = IraPhoneResponse.KeepAliveEvery;
        IraPhoneResponse.KeepAliveEvery = TimeSpan.FromMilliseconds(40);
        try
        {
            var answered = await host.SendAsync(HttpMethod.Post, "/api/Ira/ask", secret, new { text = "Think hard." });

            Assert.Equal(HttpStatusCode.OK, answered.Status);
            Assert.Equal("application/json", answered.ContentType);
            Assert.StartsWith("  ", answered.Body);
            Assert.Equal("Done thinking.", JsonNode.Parse(answered.Body)!["spoken"]!.GetValue<string>());
        }
        finally
        {
            IraPhoneResponse.KeepAliveEvery = before;
        }
    }

    [Fact]
    public void The_spoken_lines_are_short_and_plain()
    {
        Assert.Equal("I have proposed: Stop run 512 (Ghost on NIFTY). Confirm it with your PIN in the Ira app.",
            IraPhoneAsk.ProposedSpoken("Stop run 512 (Ghost on NIFTY)."));
        Assert.Equal("a minute", IraPhoneAsk.WaitWords(45 + 15));
        Assert.Equal("40 seconds", IraPhoneAsk.WaitWords(40));
        Assert.Equal("3 minutes", IraPhoneAsk.WaitWords(150));
        Assert.StartsWith("The answer is all table", IraPhoneAsk.AnswerSpoken("| a | b |\n|---|---|\n| 1 | 2 |", 600));
        Assert.Contains("switched off", IraPhoneAsk.RefusedSpoken(409, "Desk Assistant", 0));
        Assert.Equal("ira_x", IraPhoneTokens.FromAuthorization("Bearer ira_x"));
        Assert.Null(IraPhoneTokens.FromAuthorization("Bearer eyJhbGciOi.x.y"));
        Assert.Null(IraPhoneTokens.FromAuthorization(null));
    }

    // ---------- the phone app's files ----------

    [Fact]
    public async Task The_manifest_and_the_service_worker_are_served_as_themselves_and_ira_gets_its_own_shell()
    {
        string root = Path.Combine(Path.GetTempPath(), "ira-site-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "<!doctype html><title>console</title>");
            await File.WriteAllTextAsync(Path.Combine(root, "ira.html"), "<!doctype html><title>Ira</title>");
            await File.WriteAllTextAsync(Path.Combine(root, "ira.webmanifest"), """{"name":"Ira","start_url":"/ira"}""");
            await File.WriteAllTextAsync(Path.Combine(root, "ira-sw.js"), "self.addEventListener('fetch', () => {})");

            await using (var site = await SiteHost.StartAsync(root))
            {
                Assert.Equal(("application/manifest+json", "Ira"), await site.GetAsync("/ira.webmanifest", body => JsonNode.Parse(body)!["name"]!.GetValue<string>()));
                Assert.Equal("text/javascript", (await site.GetAsync("/ira-sw.js", b => b)).Type);
                Assert.Equal(("text/html", "<!doctype html><title>Ira</title>"), await site.GetAsync("/ira", b => b));
                Assert.Equal(("text/html", "<!doctype html><title>Ira</title>"), await site.GetAsync("/ira/settings", b => b));
                Assert.Equal(("text/html", "<!doctype html><title>console</title>"), await site.GetAsync("/desk", b => b));
                // Missing, they are a 404, never the console's HTML read as a manifest, a script or JSON.
                Assert.Equal(HttpStatusCode.NotFound, await site.StatusAsync("/other.webmanifest"));
                Assert.Equal(HttpStatusCode.NotFound, await site.StatusAsync("/api/nothing"));
            }

            // A wwwroot built before the phone app had its own shell: /ira still opens, on the console's.
            File.Delete(Path.Combine(root, "ira.html"));
            File.Delete(Path.Combine(root, "ira-sw.js"));
            await using (var site = await SiteHost.StartAsync(root))
            {
                Assert.Equal(("text/html", "<!doctype html><title>console</title>"), await site.GetAsync("/ira", b => b));
                Assert.Equal(HttpStatusCode.NotFound, await site.StatusAsync("/ira-sw.js"));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---------- hosts ----------

    /// <summary>
    /// The AI, Ira and phone controllers in an in-process host with the API's own policies and schemes: the console's
    /// JWT as the default (signed with a test key, leaving phone tokens alone as Program.cs does) and the phone token's
    /// scheme beside it.
    /// </summary>
    private sealed class PhoneHost : IAsyncDisposable
    {
        private static readonly SymmetricSecurityKey Key = new(Encoding.UTF8.GetBytes("ira-phone-tests-signing-key-0123456789-abcdef"));

        private readonly WebApplication _app;
        private readonly HttpClient _client;

        private PhoneHost(WebApplication app, LogSink logs)
        {
            _app = app;
            _client = app.GetTestClient();
            Logs = logs;
        }

        public LogSink Logs { get; }

        public static string Jwt(long id, string name, string role) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new Claim(JwtRegisteredClaimNames.UniqueName, name),
                new Claim(ClaimTypes.Role, role),
            ],
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(Key, SecurityAlgorithms.HmacSha256)));

        public static async Task<PhoneHost> StartAsync(Services ai, string dbName, TimeProvider time, Action<IServiceCollection>? more = null)
        {
            var logs = new LogSink();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);
            builder.Logging.SetMinimumLevel(LogLevel.Trace);
            builder.Services.AddControllers()
                .AddApplicationPart(typeof(AiController).Assembly)
                .ConfigureApplicationPartManager(parts =>
                {
                    foreach (var provider in parts.FeatureProviders.OfType<ControllerFeatureProvider>().ToList()) parts.FeatureProviders.Remove(provider);
                    parts.FeatureProviders.Add(new OnlyThese());
                });
            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(o =>
                {
                    o.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = false,
                        ValidateAudience = false,
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = Key,
                    };
                    o.Events = new JwtBearerEvents
                    {
                        OnMessageReceived = c =>
                        {
                            if (IraPhoneAuthentication.CarriesPhoneToken(c.Request)) c.NoResult();
                            return Task.CompletedTask;
                        },
                    };
                })
                .AddIraPhoneTokens();
            builder.Services.AddAuthorizationBuilder().AddPlatformPolicies();
            builder.Services.AddSingleton(time);
            builder.Services.AddScoped(_ => NewDb(dbName));
            builder.Services.AddSingleton(ai.Store);
            builder.Services.AddSingleton(ai.Gateway);
            builder.Services.AddSingleton(ai.Catalog);
            builder.Services.AddSingleton(ai.Limiter);
            builder.Services.AddSingleton(ai.Toolbox);
            builder.Services.AddSingleton(ai.Health);
            builder.Services.AddSingleton<IOptionsMonitor<AiSettings>>(ai.Options);
            builder.Services.AddScoped<IraPhoneTokens>();
            builder.Services.AddSingleton<IraPhoneConversations>();
            builder.Services.AddScoped<IraPhoneAsk>();
            more?.Invoke(builder.Services);

            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapControllers();
            await app.StartAsync();
            return new PhoneHost(app, logs);
        }

        /// <summary>A phone token for the owner, made through the console's endpoint.</summary>
        public async Task<string> TokenAsync()
        {
            var made = await SendAsync(HttpMethod.Post, "/api/Ira/phones", Jwt(Admin, "upendra", UserRoles.Admin), new { label = "iPhone" });
            Assert.Equal(HttpStatusCode.OK, made.Status);
            return JsonNode.Parse(made.Body)!["token"]!.GetValue<string>();
        }

        public async Task<(HttpStatusCode Status, string Body, HttpResponseHeaders Headers, string? ContentType)> SendAsync(
            HttpMethod method, string path, string? bearer, object? body = null)
        {
            using var request = new HttpRequestMessage(method, path);
            if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await _client.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(), response.Headers, response.Content.Headers.ContentType?.MediaType);
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _app.DisposeAsync();
        }

        private sealed class OnlyThese : ControllerFeatureProvider
        {
            protected override bool IsController(TypeInfo typeInfo) =>
                typeInfo.AsType() == typeof(AiController) || typeInfo.AsType() == typeof(IraController) || typeInfo.AsType() == typeof(IraPhoneController);
        }
    }

    /// <summary>A wwwroot of the test's own, served as Program.cs serves the console's: static files, then the fallbacks.</summary>
    private sealed class SiteHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly HttpClient _client;

        private SiteHost(WebApplication app)
        {
            _app = app;
            _client = app.GetTestClient();
        }

        public static async Task<SiteHost> StartAsync(string root)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test", ContentRootPath = root, WebRootPath = root });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            var app = builder.Build();
            // In Program.cs's order: files first, then routing, so a real file is never answered by a fallback.
            app.UseDefaultFiles();
            app.UseStaticFiles();
            app.UseRouting();
            ConsoleSite.MapConsoleFallbacks(app);
            await app.StartAsync();
            return new SiteHost(app);
        }

        public async Task<(string? Type, T Body)> GetAsync<T>(string path, Func<string, T> read)
        {
            using var response = await _client.GetAsync(path);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {path} answered {(int)response.StatusCode}");
            return (response.Content.Headers.ContentType?.MediaType, read(await response.Content.ReadAsStringAsync()));
        }

        public async Task<HttpStatusCode> StatusAsync(string path)
        {
            using var response = await _client.GetAsync(path);
            return response.StatusCode;
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _app.DisposeAsync();
        }
    }

    /// <summary>Every line the host logs, at every level, to look for the token in.</summary>
    private sealed class LogSink : ILoggerProvider
    {
        public List<(string Category, LogLevel Level, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Writer(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Writer(LogSink sink, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                string message = formatter(state, exception) + (exception is null ? string.Empty : " " + exception);
                lock (sink.Entries) sink.Entries.Add((category, logLevel, message));
            }
        }
    }
}
