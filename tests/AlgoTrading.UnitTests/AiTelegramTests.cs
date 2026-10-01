using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AlgoTrading.Api.Services.AgentMemory;
using AlgoTrading.Api.Services.AiAgents;
using AlgoTrading.Api.Services.AiTelegram;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static AlgoTrading.UnitTests.AiTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The Desk Assistant on the desk's Telegram bot: only a linked owner is
/// answered, linking takes a code from the console, and an answer arrives as
/// Telegram HTML with its call number.
/// </summary>
public sealed class AiTelegramTests
{
    private const long Owner = 5550001;
    private const long Stranger = 5550002;
    private readonly string _dbName = Guid.NewGuid().ToString("N");
    private TelegramPairing _pairing = new();

    // ---------- formatting ----------

    [Fact]
    public void Markdown_becomes_telegram_html_with_tables_and_code_in_monospace()
    {
        string html = TelegramText.ToHtml("""
            ## Run 339
            It lost **₹1,10,132.75** net (`get_run`, 19:56 IST) <b>not a tag</b>.
            | Leg | P&L |
            |-----|-----|
            | CE | -2,934 |
            ```
            x < y
            ```
            """);

        Assert.Contains("<b>Run 339</b>", html);
        Assert.Contains("<b>₹1,10,132.75</b>", html);
        Assert.Contains("<code>get_run</code>", html);
        Assert.Contains("&lt;b&gt;not a tag&lt;/b&gt;", html);
        Assert.Contains("<pre>| Leg | P&amp;L |\n| CE | -2,934 |</pre>", html);
        Assert.Contains("<pre>x &lt; y</pre>", html);
    }

    [Fact]
    public void A_long_answer_is_split_into_messages_under_the_limit()
    {
        string text = string.Join("\n", Enumerable.Repeat(new string('x', 500), 20));

        var parts = TelegramText.Split(text);

        Assert.True(parts.Count > 2);
        Assert.All(parts, p => Assert.True(p.Length <= TelegramText.MaxMessage));
    }

    // ---------- who is answered ----------

    [Fact]
    public async Task A_stranger_gets_one_line_to_start_and_nothing_else()
    {
        var (ai, bot, assistant) = Setup();

        await assistant.HandleAsync(Private(Stranger, "/start"), CancellationToken.None);
        await assistant.HandleAsync(Private(Stranger, "What is my P&L?"), CancellationToken.None);

        Assert.Equal(new[] { "This bot is private." }, bot.Texts);
        Assert.Empty(ai.Provider.Requests);
    }

    [Fact]
    public async Task Group_chats_are_ignored()
    {
        var (ai, bot, assistant) = Setup();
        await Link(assistant, Owner);
        bot.Sent.Clear();

        var group = Private(Owner, "How did the runs do?");
        group["chat"]!["type"] = "supergroup";
        await assistant.HandleAsync(group, CancellationToken.None);

        Assert.Empty(bot.Sent);
        Assert.Empty(ai.Provider.Requests);
    }

    [Fact]
    public async Task A_code_from_the_console_links_the_account_once_and_a_wrong_code_does_not()
    {
        var (_, bot, assistant) = Setup(out var pairing);

        await assistant.HandleAsync(Private(Owner, "/pair 000000"), CancellationToken.None);
        var code = pairing.Create("upendra");
        await assistant.HandleAsync(Private(Owner, $"/pair {code.Code}"), CancellationToken.None);
        await assistant.HandleAsync(Private(Stranger, $"/pair {code.Code}"), CancellationToken.None);

        Assert.StartsWith("That code is not valid", bot.Texts[0]);
        Assert.StartsWith("Linked to upendra.", bot.Texts[1]);
        Assert.StartsWith("That code is not valid", bot.Texts[2]);
        var owners = await assistant.OwnersAsync(CancellationToken.None);
        var linked = Assert.Single(owners);
        Assert.Equal((Owner, "upendra", "@owner"), (linked.TelegramUserId, linked.ConsoleUser, linked.TelegramName));
    }

    // ---------- answering ----------

    [Fact]
    public async Task The_owner_s_question_is_answered_as_the_desk_assistant_with_its_call_number()
    {
        var (ai, bot, assistant) = Setup();
        await Link(assistant, Owner);
        bot.Sent.Clear();
        ai.Provider.On(Judge1, Answer("Run 339 lost **₹1,10,132.75** net."));

        await assistant.HandleAsync(Private(Owner, "Which run lost the most today?"), CancellationToken.None);

        var reply = bot.Sent.Last(s => s["method"] == "sendMessage");
        Assert.Equal("HTML", reply["parse_mode"]);
        Assert.Contains("<b>₹1,10,132.75</b>", reply["text"]);
        Assert.Matches(@"— nemotron-3-ultra-550b-a55b · \d+ s · call #\d+", reply["text"]);
        var call = await ai.Db.AiCalls.SingleAsync();
        Assert.Equal(("telegram", "upendra", AiCatalog.DeskAssistant), (call.Source, call.RequestedBy, call.AgentKey));
    }

    [Fact]
    public async Task A_follow_up_carries_the_conversation_and_new_starts_over()
    {
        var (ai, bot, assistant) = Setup();
        await Link(assistant, Owner);
        ai.Provider.On(Judge1, Answer("Run 339."), Answer("Because of charges."), Answer("Fresh."));

        await assistant.HandleAsync(Private(Owner, "Which run lost the most?"), CancellationToken.None);
        await assistant.HandleAsync(Private(Owner, "Why?"), CancellationToken.None);
        await assistant.HandleAsync(Private(Owner, "/new"), CancellationToken.None);
        await assistant.HandleAsync(Private(Owner, "Hello"), CancellationToken.None);

        var second = JsonNode.Parse(ai.Provider.Requests[1].Body)!["messages"]!.AsArray().Select(m => m!["content"]?.GetValue<string>()).ToList();
        var third = JsonNode.Parse(ai.Provider.Requests[2].Body)!["messages"]!.AsArray().Select(m => m!["content"]?.GetValue<string>()).ToList();
        Assert.Contains("Which run lost the most?", second);
        Assert.Contains("Run 339.", second);
        Assert.DoesNotContain("Which run lost the most?", third);
    }

    [Fact]
    public async Task A_refused_question_says_why()
    {
        var (ai, bot, assistant) = Setup();
        await Link(assistant, Owner);
        await ai.Store.SetAgentEnabledAsync(AiCatalog.DeskAssistant, false, "upendra", "quiet day");
        bot.Sent.Clear();

        await assistant.HandleAsync(Private(Owner, "Anything?"), CancellationToken.None);

        Assert.Contains("switched off", bot.Texts.Single());
    }

    // ---------- reading updates ----------

    [Fact]
    public async Task Updates_are_read_from_the_saved_offset_and_the_next_one_is_kept()
    {
        var (_, bot, assistant) = Setup();
        bot.Updates = """{"ok":true,"result":[{"update_id":41,"message":{"chat":{"id":7,"type":"private"},"from":{"id":5550002},"text":"hi"}},{"update_id":42,"message":{"chat":{"id":7,"type":"private"},"from":{"id":5550002},"text":"/start"}}]}""";

        await assistant.PollOnceAsync(CancellationToken.None);
        bot.Updates = """{"ok":true,"result":[]}""";
        await assistant.PollOnceAsync(CancellationToken.None);

        await using var db = NewDb(_dbName);
        Assert.Equal("43", (await db.SystemSettings.SingleAsync(s => s.Key == TelegramAssistant.OffsetKey)).Value);
        Assert.Contains("offset=43", bot.Polls.Last());
        Assert.Equal(new[] { "This bot is private." }, bot.Texts);
    }

    [Fact]
    public async Task Another_reader_of_the_bot_is_waited_out_not_crashed_on()
    {
        var (_, bot, assistant) = Setup();
        bot.PollStatus = HttpStatusCode.Conflict;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => assistant.PollOnceAsync(cts.Token));
    }

    // ---------- teaching it from the phone ----------

    [Fact]
    public async Task Remember_saves_a_note_memory_lists_it_and_forget_retires_it()
    {
        var (ai, bot, assistant) = Setup();
        await Link(assistant, Owner);
        bot.Sent.Clear();

        await assistant.HandleAsync(Private(Owner, "/remember Weekly NIFTY options expire on Tuesday."), CancellationToken.None);
        await using var db = NewDb(_dbName);
        var note = await db.AiMemories.SingleAsync();
        await assistant.HandleAsync(Private(Owner, "/memory"), CancellationToken.None);
        await assistant.HandleAsync(Private(Owner, $"/forget M{note.Id}"), CancellationToken.None);

        Assert.Equal(("telegram", "upendra", AiMemoryKind.Note), (note.Via, note.CreatedBy, note.Kind));
        Assert.StartsWith($"Saved as note M{note.Id}.", bot.Texts[0]);
        Assert.Contains($"M{note.Id} (note): Weekly NIFTY options expire on Tuesday.", bot.Texts[1]);
        Assert.StartsWith($"M{note.Id} is retired", bot.Texts[2]);
        await using var after = NewDb(_dbName);
        Assert.Equal(AiMemoryStatus.Retired, (await after.AiMemories.SingleAsync()).Status);
        Assert.DoesNotContain(ai.Provider.Requests, r => r.Path.EndsWith("/chat/completions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_answer_carries_thumbs_and_a_thumbs_down_asks_what_it_should_have_said()
    {
        var (ai, bot, assistant) = Setup();
        await Link(assistant, Owner);
        ai.Provider.On(Judge1, Answer("The desk made ₹2,22,414 today."));
        await assistant.HandleAsync(Private(Owner, "How much did the desk make today?"), CancellationToken.None);
        var answer = bot.Sent.Last(s => s["method"] == "sendMessage");
        long callId = (await ai.Db.AiCalls.SingleAsync()).Id;
        Assert.Contains($"fb:-1:{callId}", answer["reply_markup"]);
        bot.Sent.Clear();

        await assistant.HandleButtonAsync(Press(Owner, $"fb:-1:{callId}", messageId: 7), CancellationToken.None);
        var prompt = bot.Sent.Single(s => s["method"] == "sendMessage");
        long promptId = long.Parse(prompt["message_id_sent"]);
        await assistant.HandleAsync(Reply(Owner, "Always give net after charges, not gross.", promptId), CancellationToken.None);

        await using var db = NewDb(_dbName);
        var call = await db.AiCalls.SingleAsync(c => c.Id == callId);
        var correction = await db.AiMemories.SingleAsync();
        Assert.Contains("force_reply", prompt["reply_markup"]);
        Assert.Contains(bot.Sent, s => s["method"] == "editMessageReplyMarkup" && s["reply_markup"].Contains(" noted"));
        Assert.Equal((-1, "Always give net after charges, not gross."), (call.FeedbackScore, call.FeedbackNote));
        Assert.Equal((AiMemoryKind.Correction, AiMemoryStatus.Active, callId, "telegram"), (correction.Kind, correction.Status, correction.SourceCallId, correction.Via));
        Assert.StartsWith($"Saved as correction M{correction.Id}.", bot.Texts.Last());
    }

    [Fact]
    public async Task A_strangers_button_changes_nothing()
    {
        var (ai, bot, assistant) = Setup();
        await Link(assistant, Owner);
        ai.Provider.On(Judge1, Answer("ok"));
        await assistant.HandleAsync(Private(Owner, "Anything?"), CancellationToken.None);
        long callId = (await ai.Db.AiCalls.SingleAsync()).Id;
        bot.Sent.Clear();

        await assistant.HandleButtonAsync(Press(Stranger, $"fb:1:{callId}", messageId: 7), CancellationToken.None);

        await using var db = NewDb(_dbName);
        Assert.Null((await db.AiCalls.SingleAsync()).FeedbackScore);
        Assert.Equal(new[] { "answerCallbackQuery" }, bot.Sent.Select(s => s["method"]));
    }

    [Fact]
    public async Task The_days_tested_lessons_reach_each_owner_in_one_message_with_nothing_to_answer()
    {
        var (ai, bot, assistant) = Setup();
        await Link(assistant, Owner);
        bot.Sent.Clear();
        var used = new AiMemory { Id = 15, AgentKey = AiCatalog.DeskAssistant, Status = AiMemoryStatus.Active, Text = "Read totals.netPnl for the day's total.",
            DecidedBy = AssistantCheckAgent.Verified };
        var dropped = new AiMemory { Id = 16, AgentKey = AiCatalog.DeskAssistant, Status = AiMemoryStatus.Rejected, Text = "x",
            DecidedBy = AssistantCheckAgent.DidNotFix };

        await assistant.LessonsProposedAsync([used, dropped], CancellationToken.None);

        var sent = bot.Sent.Single(s => s["method"] == "sendMessage");
        Assert.Equal(Owner.ToString(System.Globalization.CultureInfo.InvariantCulture), sent["chat_id"]);
        Assert.Contains("M15: Read totals.netPnl for the day's total.", sent["text"]);
        Assert.Contains("M16: did not fix its question", sent["text"]);
        Assert.Contains("Nothing to answer", sent["text"]);
        Assert.False(sent.ContainsKey("reply_markup"));
    }

    // ---------- helpers ----------

    private (Services Ai, FakeBot Bot, TelegramAssistant Assistant) Setup() => Setup(out _);

    private (Services Ai, FakeBot Bot, TelegramAssistant Assistant) Setup(out TelegramPairing pairing)
    {
        var ai = Build(null, NewDb(_dbName));
        var bot = new FakeBot();
        var services = new ServiceCollection();
        services.AddScoped(_ => NewDb(_dbName));
        services.AddScoped(_ => ai.Gateway);
        services.AddScoped(sp => new AiMemoryService(sp.GetRequiredService<TradingDbContext>(),
            new AiMemoryBook(sp.GetRequiredService<TradingDbContext>(), ai.Client, ai.Options), ai.Options));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Telegram:BotToken"] = "123:TEST" }).Build();
        pairing = _pairing = new TelegramPairing();
        var assistant = new TelegramAssistant(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), new Factory(bot), config,
            pairing, ai.Options, NullLogger<TelegramAssistant>.Instance);
        return (ai, bot, assistant);
    }

    /// <summary>Links <paramref name="user"/> the way the owner does: a code from the console, sent to the bot.</summary>
    private async Task Link(TelegramAssistant assistant, long user) =>
        await assistant.HandleAsync(Private(user, $"/pair {_pairing.Create("upendra").Code}"), CancellationToken.None);

    private static JsonObject Private(long user, string text) => new()
    {
        ["chat"] = new JsonObject { ["id"] = user, ["type"] = "private" },
        ["from"] = new JsonObject { ["id"] = user, ["username"] = user == Owner ? "owner" : "someone", ["first_name"] = "X" },
        ["text"] = text,
    };

    private static JsonObject Reply(long user, string text, long toMessageId)
    {
        var message = Private(user, text);
        message["reply_to_message"] = new JsonObject { ["message_id"] = toMessageId };
        return message;
    }

    private static JsonObject Press(long user, string data, long messageId) => new()
    {
        ["id"] = "press-1",
        ["from"] = new JsonObject { ["id"] = user },
        ["data"] = data,
        ["message"] = new JsonObject { ["message_id"] = messageId, ["chat"] = new JsonObject { ["id"] = user, ["type"] = "private" } },
    };

    /// <summary>
    /// api.telegram.org: records every method called, answers getUpdates from
    /// <see cref="Updates"/>, and gives each sent message an id (kept on the
    /// record as <c>message_id_sent</c>).
    /// </summary>
    private sealed class FakeBot : HttpMessageHandler
    {
        private long _nextMessageId = 100;

        public List<Dictionary<string, string>> Sent { get; } = [];
        public List<string> Polls { get; } = [];
        public string Updates { get; set; } = """{"ok":true,"result":[]}""";
        public HttpStatusCode PollStatus { get; set; } = HttpStatusCode.OK;

        public List<string> Texts => Sent.Where(s => s["method"] == "sendMessage").Select(s => s["text"]).ToList();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            string method = path[(path.LastIndexOf('/') + 1)..];
            if (method == "getUpdates")
            {
                Polls.Add(request.RequestUri.Query);
                return new HttpResponseMessage(PollStatus) { Content = new StringContent(Updates, Encoding.UTF8, "application/json") };
            }

            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            var entry = body.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? string.Empty);
            entry["method"] = method;
            long id = Interlocked.Increment(ref _nextMessageId);
            entry["message_id_sent"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Sent.Add(entry);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"result\":{\"message_id\":" + id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}") };
        }
    }
}
