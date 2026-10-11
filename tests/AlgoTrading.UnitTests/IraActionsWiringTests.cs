using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services;
using AlgoTrading.Api.Services.AiActions;
using AlgoTrading.Api.Services.AiTelegram;
using AlgoTrading.Api.Services.AiTools;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using static AlgoTrading.UnitTests.AiTestKit;
using static AlgoTrading.UnitTests.IraActionsTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Ira's actions where they meet the rest of the desk: which calls the gateway offers the proposal tools, the
/// console's endpoints behind the admin policy, and the Telegram flow, where a typed PIN is deleted, checked and
/// carried out before any model could see it.
/// </summary>
public sealed class IraActionsWiringTests
{
    private const long OwnerChat = 5550001;
    private readonly string _db = Guid.NewGuid().ToString("N");
    private readonly MovableTime _time = new(Monday1030);
    private readonly FakeHands _hands = new();
    private readonly IraPinGuard _guard;

    public IraActionsWiringTests()
    {
        FastPins();
        _guard = new IraPinGuard(_time);
    }

    private IraActions Actions(AiSettings settings, TradingDbContext? db = null) =>
        new(db ?? NewDb(_db), _hands, _guard, new AiTestKit.Monitor(settings), NullLogger<IraActions>.Instance, _time);

    private static IraOwner Upendra => new(Admin, "upendra");

    // ---------- the gateway offers them to the Desk Assistant, in an admin's chat, and to nothing else ----------

    [Fact]
    public async Task Only_the_desk_assistant_in_an_admins_chat_is_offered_the_proposal_tools()
    {
        var settings = Settings();
        var tools = AiToolNames.Desk.Concat(AiToolNames.Actions).Select(n => (IAiTool)new FakeTool(n)).ToArray();
        var ai = Build(settings, NewDb(_db), tools);
        SeedUsers(ai.Db);
        await ai.Store.SetAgentEnabledAsync(AiCatalog.AiTrader, true, "upendra", null);
        ai.Provider.On(Judge1, Enumerable.Range(0, 8).Select(_ => Answer("ok")).ToArray());

        async Task<(List<string> Tools, string Prompt)> Ask(AiAskInput input)
        {
            await ai.Gateway.AskAsync(input, NullAiStreamSink.Instance, CancellationToken.None);
            var body = JsonNode.Parse(ai.Provider.Requests[^1].Body)!;
            var names = body["tools"]?.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>()).ToList() ?? [];
            return (names, body["messages"]![0]!["content"]!.GetValue<string>());
        }

        var console = await Ask(Question("Stop the Ghost run on NIFTY."));
        var telegram = await Ask(Question("Stop the Ghost run on NIFTY.") with { Source = "telegram", ConversationId = $"tg-{OwnerChat}" });
        var trader = await Ask(Question("Stop the Ghost run on NIFTY.") with { UserId = Trader, RequestedBy = "coderforchange" });
        var script = await Ask(Question("Stop the Ghost run on NIFTY.") with { Source = "api" });
        var anotherAgent = await Ask(Question("Stop your run.", AiCatalog.AiTrader));
        settings.Actions.Enabled = false;
        var switchedOff = await Ask(Question("Stop the Ghost run on NIFTY."));

        Assert.Equal(AiToolNames.Desk.Concat(AiToolNames.Actions), console.Tools);
        Assert.Contains(AiCatalog.ActionsPrompt, console.Prompt);
        Assert.Equal(console.Tools, telegram.Tools);
        foreach (var (tools2, prompt) in new[] { trader, script, switchedOff })
        {
            Assert.Equal(AiToolNames.Desk, tools2);
            Assert.DoesNotContain(AiCatalog.ActionsPrompt, prompt);
        }

        Assert.DoesNotContain(anotherAgent.Tools, AiToolNames.Actions.Contains);
        Assert.DoesNotContain(AiCatalog.ActionsPrompt, anotherAgent.Prompt);
    }

    [Fact]
    public void The_catalog_keeps_the_proposal_tools_out_of_every_agents_own_tools_and_says_what_the_assistant_may_do()
    {
        foreach (var agent in AiCatalog.Agents)
        {
            Assert.DoesNotContain((agent.Tools ?? []).Concat(agent.ChatTools ?? []), AiToolNames.Actions.Contains);
        }

        var desk = AiCatalog.Agent(AiCatalog.DeskAssistant)!;
        Assert.Contains("never place, change or cancel a broker order", desk.SystemPrompt);
        Assert.Contains("only after the owner confirms it with his PIN", desk.Limits);
        Assert.All(AiCatalog.Agents.Where(a => a.Key is not (AiCatalog.DeskAssistant or AiCatalog.AiTrader)),
            a => Assert.Contains("cannot propose an action", a.Limits));
        Assert.False(AiCatalog.ActionsOffered(true, AiCatalog.TradeReviewer, "console", ownPrompt: true, askerIsAdmin: true));
        Assert.False(AiCatalog.ActionsOffered(true, AiCatalog.DeskAssistant, "console", ownPrompt: false, askerIsAdmin: true));
        Assert.True(AiCatalog.ActionsOffered(true, AiCatalog.DeskAssistant, "telegram", ownPrompt: true, askerIsAdmin: true));
    }

    // ---------- the console's endpoints ----------

    [Fact]
    public async Task The_console_sets_the_pin_confirms_with_it_and_cancels()
    {
        var settings = Settings();
        var ai = Build(settings, NewDb(_db));
        SeedUsers(ai.Db);
        var actions = Actions(settings, ai.Db);
        var controller = ai.Controller();

        var before = Body<AiActionPinStatus>(await controller.ActionPin(actions, CancellationToken.None));
        var badShape = await controller.SetActionPin(new AiActionPinRequest("12"), actions, CancellationToken.None);
        var after = Body<AiActionPinStatus>(await controller.SetActionPin(new AiActionPinRequest(Pin), actions, CancellationToken.None));
        var stop = await Proposed(actions, "c-1");
        var wrong = Assert.IsType<ObjectResult>(await controller.ConfirmAction(stop.Id, new AiActionConfirmRequest("0000"), actions, CancellationToken.None));
        var done = Assert.IsType<OkObjectResult>(await controller.ConfirmAction(stop.Id, new AiActionConfirmRequest(Pin), actions, CancellationToken.None));
        var again = await controller.ConfirmAction(stop.Id, new AiActionConfirmRequest(Pin), actions, CancellationToken.None);
        var start = await Proposed(actions, "c-1");
        var cancelled = Assert.IsType<OkObjectResult>(await controller.CancelAction(start.Id, actions, CancellationToken.None));

        Assert.Equal(new AiActionPinStatus(true, false, null, 120), before);
        Assert.IsType<BadRequestObjectResult>(badShape);
        Assert.True(after.Set);
        Assert.Equal(403, wrong.StatusCode);
        Assert.Equal(2, Json(wrong.Value)["attemptsLeft"]!.GetValue<int>());
        Assert.Equal("Done: stop_run for upendra.", Json(done.Value)["message"]!.GetValue<string>());
        Assert.Equal("executed", Json(done.Value)["action"]!["status"]!.GetValue<string>());
        Assert.IsType<ConflictObjectResult>(again);
        Assert.Equal("cancelled", Json(cancelled.Value)["action"]!["status"]!.GetValue<string>());
        Assert.Single(_hands.Calls);
        // The request's own activity-log row says what came of it; the action's outcome has a row of its own.
        Assert.Equal($"Ira's action #{start.Id}: cancelled.", ActivityLogContext.Read(controller.HttpContext).Summary);
    }

    [Fact]
    public async Task The_console_endpoints_are_the_admins_own()
    {
        var settings = Settings();
        var ai = Build(settings, NewDb(_db));
        SeedUsers(ai.Db);
        var actions = Actions(settings, ai.Db);
        await actions.SetPinAsync(Upendra, Pin, null, requireCurrent: false, "the console", CancellationToken.None);
        await actions.SetPinAsync(new IraOwner(OtherAdmin, "ravi"), Pin, null, requireCurrent: false, "the console", CancellationToken.None);
        var stop = await Proposed(actions, "c-1");
        var telegram = await actions.ProposeAsync(Upendra, new AiToolCaller(AiCatalog.DeskAssistant, "telegram", "upendra", Admin, $"tg-{OwnerChat}", 9),
            new IraProposal(AiActionKind.StopRun, StopJson, "Stop run 512."), CancellationToken.None);
        var ravi = ai.Controller("ravi");
        ravi.ControllerContext.HttpContext.User = Principal(OtherAdmin, "ravi", UserRoles.Admin);

        Assert.IsType<NotFoundObjectResult>(await ravi.ConfirmAction(stop.Id, new AiActionConfirmRequest(Pin), actions, CancellationToken.None));
        // A Telegram action is confirmed in its chat, not from the console.
        Assert.IsType<NotFoundObjectResult>(await ai.Controller().ConfirmAction(telegram.Id, new AiActionConfirmRequest(Pin), actions, CancellationToken.None));
        Assert.Empty(_hands.Calls);
    }

    [Fact]
    public async Task Over_http_the_actions_answer_an_admin_and_refuse_a_trader_and_a_stranger()
    {
        var settings = Settings();
        var ai = Build(settings, NewDb(_db));
        SeedUsers(ai.Db);
        var actions = Actions(settings, ai.Db);
        await actions.SetPinAsync(Upendra, Pin, null, requireCurrent: false, "the console", CancellationToken.None);
        var stop = await Proposed(actions, "c-1");
        await using var host = await AiHost.StartAsync(ai, services => AddActions(services, _hands, _guard, ai.Options, _time), _db);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendAsync(HttpMethod.Get, "/api/Ai/actions/pin", null)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendAsync(HttpMethod.Get, "/api/Ai/actions/pin", Trader)).Status);
        var traderConfirm = await host.SendAsync(HttpMethod.Post, $"/api/Ai/actions/{stop.Id}/confirm", Trader, new { pin = Pin });
        Assert.Equal(HttpStatusCode.Forbidden, traderConfirm.Status);
        Assert.Equal(string.Empty, traderConfirm.Body);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendAsync(HttpMethod.Put, "/api/Ai/actions/pin", Trader, new { pin = "1111" })).Status);
        Assert.Empty(_hands.Calls);

        var pin = await host.SendAsync(HttpMethod.Get, "/api/Ai/actions/pin", Admin);
        Assert.Equal(HttpStatusCode.OK, pin.Status);
        Assert.True(JsonNode.Parse(pin.Body)!["set"]!.GetValue<bool>());
        var wrong = await host.SendAsync(HttpMethod.Post, $"/api/Ai/actions/{stop.Id}/confirm", Admin, new { pin = "0000" });
        Assert.Equal(HttpStatusCode.Forbidden, wrong.Status);
        Assert.Equal(2, JsonNode.Parse(wrong.Body)!["attemptsLeft"]!.GetValue<int>());
        var right = await host.SendAsync(HttpMethod.Post, $"/api/Ai/actions/{stop.Id}/confirm", Admin, new { pin = Pin });
        Assert.Equal(HttpStatusCode.OK, right.Status);
        Assert.Equal("executed", JsonNode.Parse(right.Body)!["action"]!["status"]!.GetValue<string>());
        Assert.Single(_hands.Calls);
    }

    // ---------- Telegram ----------

    [Fact]
    public async Task On_telegram_a_proposal_asks_for_the_pin_and_the_typed_pin_is_deleted_checked_and_carried_out_without_a_model()
    {
        var (ai, bot, assistant, _) = await Telegram();
        await assistant.HandleAsync(Private(OwnerChat, $"/pin {Pin}", 11), CancellationToken.None);
        Assert.Equal("11", bot.Sent.Single(s => s["method"] == "deleteMessage")["message_id"]);
        Assert.StartsWith("PIN set, and your message deleted.", bot.Texts[^1]);
        ai.Provider.On(Judge1,
            Tools((AiToolNames.ProposeAgentSwitch, """{"agent":"news-analyst","on":true}""")),
            Answer("Switching the News Analyst on: confirm it with your PIN."),
            Answer("It is on."));
        bot.Sent.Clear();

        await assistant.HandleAsync(Private(OwnerChat, "Switch the news analyst on.", 12), CancellationToken.None);

        await using var db = NewDb(_db);
        var action = await db.AiActions.SingleAsync();
        Assert.Equal((AiActionChannel.Telegram, OwnerChat, AiActionStatus.Pending), (action.Channel, action.ChatId!.Value, action.Status));
        Assert.Contains("Switching the News Analyst on", bot.Texts[0]);
        Assert.Equal($"Action #{action.Id}: Switch the News Analyst on, as AI → Agents does.\nIt waits until 10:32 IST. Type your PIN to confirm, or /cancel.", bot.Texts[1]);
        int asked = ai.Provider.Requests.Count;
        bot.Sent.Clear();

        await assistant.HandleAsync(Private(OwnerChat, Pin, 13), CancellationToken.None);

        // Deleted first, then checked and carried out, and no model was asked.
        Assert.Equal(new[] { "deleteMessage", "sendMessage" }, bot.Sent.Select(s => s["method"]));
        Assert.Equal(("13", OwnerChat.ToString(System.Globalization.CultureInfo.InvariantCulture)), (bot.Sent[0]["message_id"], bot.Sent[0]["chat_id"]));
        Assert.Equal("Done: agent_switch for upendra.", bot.Texts.Single());
        Assert.Equal(asked, ai.Provider.Requests.Count);
        Assert.Equal(AiActionKind.AgentSwitch, Assert.Single(_hands.Calls).Kind);
        await using (var read = NewDb(_db)) Assert.Equal(AiActionStatus.Executed, (await read.AiActions.SingleAsync()).Status);

        // The conversation knows what was done.
        await assistant.HandleAsync(Private(OwnerChat, "Is it on now?", 14), CancellationToken.None);
        Assert.Contains("Done: agent_switch for upendra.", ai.Provider.Requests[^1].Body);

        // The PIN is in no call: not the messages, the prompt, the tools or an answer.
        await using var calls = NewDb(_db);
        Assert.All(await calls.AiCalls.ToListAsync(), c =>
            Assert.DoesNotContain(Pin, c.MessagesJson + c.SystemPrompt + c.ToolsJson + c.Answer + c.Summary));
        Assert.DoesNotContain(calls.ActivityLog, e => (e.Summary ?? string.Empty).Contains(Pin));
    }

    [Fact]
    public async Task On_telegram_three_wrong_pins_lock_and_every_one_is_deleted()
    {
        var (_, bot, assistant, actions) = await Telegram();
        await actions.SetPinAsync(Upendra, Pin, null, requireCurrent: false, "Telegram", CancellationToken.None);
        await PendingInChat(actions);
        bot.Sent.Clear();

        await assistant.HandleAsync(Private(OwnerChat, "0000", 21), CancellationToken.None);
        await assistant.HandleAsync(Private(OwnerChat, "1111", 22), CancellationToken.None);
        await assistant.HandleAsync(Private(OwnerChat, "2222", 23), CancellationToken.None);
        await assistant.HandleAsync(Private(OwnerChat, Pin, 24), CancellationToken.None);

        Assert.Equal(new[] { "21", "22", "23", "24" }, bot.Sent.Where(s => s["method"] == "deleteMessage").Select(s => s["message_id"]));
        Assert.Equal("Wrong PIN: 2 tries left before actions lock for 15 minutes. Nothing was done.", bot.Texts[0]);
        Assert.Equal("Wrong PIN: 1 try left before actions lock for 15 minutes. Nothing was done.", bot.Texts[1]);
        Assert.Equal("Wrong PIN. Actions are locked until 10:45 IST after 3 wrong PINs. Nothing was done.", bot.Texts[2]);
        Assert.Equal("Actions are locked until 10:45 IST after 3 wrong PINs. Nothing was done.", bot.Texts[3]);
        Assert.Empty(_hands.Calls);
    }

    [Fact]
    public async Task On_telegram_cancel_drops_the_action_and_a_late_or_stray_pin_runs_nothing()
    {
        var (ai, bot, assistant, actions) = await Telegram();
        await actions.SetPinAsync(Upendra, Pin, null, requireCurrent: false, "Telegram", CancellationToken.None);
        var cancelled = await PendingInChat(actions);
        bot.Sent.Clear();

        await assistant.HandleAsync(Private(OwnerChat, "/cancel", 31), CancellationToken.None);
        await assistant.HandleAsync(Private(OwnerChat, Pin, 32), CancellationToken.None);
        var expired = await PendingInChat(actions);
        _time.Advance(TimeSpan.FromMinutes(3));
        await assistant.HandleAsync(Private(OwnerChat, Pin, 33), CancellationToken.None);
        await assistant.HandleAsync(Private(OwnerChat, Pin, 34), CancellationToken.None);

        Assert.Equal($"Cancelled action #{cancelled.Id}: nothing was done.", bot.Texts[0]);
        Assert.Equal("No action is waiting for a PIN. I deleted your message.", bot.Texts[1]);
        Assert.Equal($"Action #{expired.Id} expired at 10:32 IST: nothing was done. Ask again.", bot.Texts[2]);
        Assert.Equal($"No action is waiting for a PIN: Action #{expired.Id} expired at 10:32 IST: nothing was done. Ask again. I deleted your message.", bot.Texts[3]);
        // /cancel holds no PIN, so it stays; every message that may hold one is deleted.
        Assert.Equal(new[] { "32", "33", "34" }, bot.Sent.Where(s => s["method"] == "deleteMessage").Select(s => s["message_id"]));
        Assert.Empty(_hands.Calls);
        Assert.Empty(ai.Provider.Requests);
    }

    [Fact]
    public async Task On_telegram_a_pin_changes_only_with_the_old_one_and_the_message_is_deleted()
    {
        var (_, bot, assistant, actions) = await Telegram();

        await assistant.HandleAsync(Private(OwnerChat, "/pin", 41), CancellationToken.None);
        await assistant.HandleAsync(Private(OwnerChat, "/pin 12", 42), CancellationToken.None);
        await assistant.HandleAsync(Private(OwnerChat, $"/pin {Pin}", 43), CancellationToken.None);
        await assistant.HandleAsync(Private(OwnerChat, "/pin 5555", 44), CancellationToken.None);
        await assistant.HandleAsync(Private(OwnerChat, "/pin 0000 5555", 45), CancellationToken.None);
        await assistant.HandleAsync(Private(OwnerChat, $"/pin {Pin} 5555", 46), CancellationToken.None);

        Assert.StartsWith("No Action PIN yet. Set one with /pin 1234", bot.Texts[0]);
        Assert.StartsWith("A PIN is 4 to 8 digits.", bot.Texts[1]);
        Assert.StartsWith("PIN set", bot.Texts[2]);
        Assert.StartsWith("A PIN is already set: change it with /pin <old> <new>.", bot.Texts[3]);
        Assert.Equal("Wrong PIN: 2 tries left before actions lock for 15 minutes. The PIN is unchanged.", bot.Texts[4]);
        Assert.Equal("PIN changed, and your message deleted.", bot.Texts[5]);
        Assert.Equal(new[] { "41", "42", "43", "44", "45", "46" }, bot.Sent.Where(s => s["method"] == "deleteMessage").Select(s => s["message_id"]));
        await using var db = NewDb(_db);
        Assert.True(IraPin.Verify("5555", (await db.SystemSettings.SingleAsync(s => s.Key == IraActions.PinKey(Admin))).Value));
    }

    // ---------- helpers ----------

    private const string StopJson = """{"runId":512,"strategy":"GhostTangentCrossings","underlying":"NIFTY","accountUserId":1,"account":"upendra"}""";

    private static Task<AiAction> Proposed(IraActions actions, string conversation) =>
        actions.ProposeAsync(Upendra, new AiToolCaller(AiCatalog.DeskAssistant, "console", "upendra", Admin, conversation, 7),
            new IraProposal(AiActionKind.StopRun, StopJson, "Stop run 512 (Ghost Tangent Crossings on NIFTY, upendra's account)."), CancellationToken.None);

    private static Task<AiAction> PendingInChat(IraActions actions) =>
        actions.ProposeAsync(Upendra, new AiToolCaller(AiCatalog.DeskAssistant, "telegram", "upendra", Admin, TelegramAssistant.ActionConversation(OwnerChat), 5),
            new IraProposal(AiActionKind.AgentSwitch, """{"agent":"news-analyst","agentName":"News Analyst","on":true}""", "Switch the News Analyst on."), CancellationToken.None);

    private async Task<(Services Ai, Bot Bot, TelegramAssistant Assistant, IraActions Actions)> Telegram()
    {
        var settings = Settings();
        var toolActions = Actions(settings);
        var resolver = new IraActionResolver(NewDb(_db), null!, Registry(), new AiSettingsStore(NewDb(_db)), null, _time);
        var ai = Build(settings, NewDb(_db),
            new ProposeStopRunTool(toolActions, resolver, _time), new ProposeAgentSwitchTool(toolActions, resolver, _time));
        SeedUsers(ai.Db);
        var bot = new Bot();
        var services = new ServiceCollection();
        services.AddScoped(_ => NewDb(_db));
        services.AddScoped(_ => ai.Gateway);
        AddActions(services, _hands, _guard, ai.Options, _time);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Telegram:BotToken"] = "123:TEST" }).Build();
        var pairing = new TelegramPairing();
        var assistant = new TelegramAssistant(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), new Factory(bot), config,
            pairing, ai.Options, NullLogger<TelegramAssistant>.Instance);
        await assistant.HandleAsync(Private(OwnerChat, $"/pair {pairing.Create("upendra").Code}", 1), CancellationToken.None);
        bot.Sent.Clear();
        return (ai, bot, assistant, Actions(settings));
    }

    private static JsonObject Private(long user, string text, long messageId) => new()
    {
        ["message_id"] = messageId,
        ["chat"] = new JsonObject { ["id"] = user, ["type"] = "private" },
        ["from"] = new JsonObject { ["id"] = user, ["username"] = "owner", ["first_name"] = "X" },
        ["text"] = text,
    };

    private static T Body<T>(IActionResult result) => Assert.IsType<T>(Assert.IsType<OkObjectResult>(result).Value);

    private static JsonNode Json(object? value) => JsonSerializer.SerializeToNode(value, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static ClaimsPrincipal Principal(long id, string name, string role) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, name), new Claim(ClaimTypes.NameIdentifier, id.ToString(System.Globalization.CultureInfo.InvariantCulture)), new Claim(ClaimTypes.Role, role)], "test"));

    /// <summary>
    /// The AI controller alone in an in-process host with the API's own policies: a caller is named by the X-Test-User
    /// header (1 holds the Admin role, as its row says; 7 is a trader); none is anonymous.
    /// </summary>
    private sealed class AiHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly HttpClient _client;

        private AiHost(WebApplication app)
        {
            _app = app;
            _client = app.GetTestClient();
        }

        public static async Task<AiHost> StartAsync(Services ai, Action<IServiceCollection> more, string dbName)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddControllers()
                .AddApplicationPart(typeof(AiController).Assembly)
                .ConfigureApplicationPartManager(parts =>
                {
                    foreach (var provider in parts.FeatureProviders.OfType<ControllerFeatureProvider>().ToList()) parts.FeatureProviders.Remove(provider);
                    parts.FeatureProviders.Add(new OnlyAi());
                });
            builder.Services.AddAuthentication(HeaderAuth.SchemeName).AddScheme<AuthenticationSchemeOptions, HeaderAuth>(HeaderAuth.SchemeName, null);
            builder.Services.AddAuthorizationBuilder().AddPlatformPolicies();
            builder.Services.AddScoped(_ => NewDb(dbName));
            builder.Services.AddSingleton(ai.Store);
            builder.Services.AddSingleton(ai.Gateway);
            builder.Services.AddSingleton(ai.Catalog);
            builder.Services.AddSingleton(ai.Limiter);
            builder.Services.AddSingleton(ai.Toolbox);
            builder.Services.AddSingleton(ai.Health);
            builder.Services.AddSingleton<IOptionsMonitor<AiSettings>>(ai.Options);
            more(builder.Services);

            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapControllers();
            await app.StartAsync();
            return new AiHost(app);
        }

        public async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpMethod method, string path, long? userId, object? body = null)
        {
            using var request = new HttpRequestMessage(method, path);
            if (userId is { } id) request.Headers.Add(HeaderAuth.UserHeader, id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await _client.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _app.DisposeAsync();
        }

        private sealed class OnlyAi : ControllerFeatureProvider
        {
            protected override bool IsController(TypeInfo typeInfo) => typeInfo.AsType() == typeof(AiController);
        }
    }

    private sealed class HeaderAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Test";
        public const string UserHeader = "X-Test-User";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(UserHeader, out var value) || !long.TryParse(value, out var userId))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, userId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new(ClaimTypes.Name, userId == Admin ? "upendra" : "coderforchange"),
            };
            if (userId == Admin) claims.Add(new Claim(ClaimTypes.Role, UserRoles.Admin));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName)));
        }
    }
}
