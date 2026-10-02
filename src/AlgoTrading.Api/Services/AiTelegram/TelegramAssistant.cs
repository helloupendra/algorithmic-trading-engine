using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AlgoTrading.Api.Services.AgentMemory;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services.AiTelegram;

/// <summary>A code the console hands the owner, sent to the bot as "/pair CODE" to link their Telegram account.</summary>
public sealed record TelegramPairingCode(string Code, string ForUser, DateTime ExpiresUtc);

/// <summary>Codes waiting to be sent to the bot. In memory: a code outlives neither ten minutes nor a restart.</summary>
public sealed class TelegramPairing(TimeProvider? time = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, TelegramPairingCode> _codes = new(StringComparer.Ordinal);

    public TelegramPairingCode Create(string forUser)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var old in _codes.Where(kv => kv.Value.ExpiresUtc <= now || kv.Value.ForUser == forUser)) _codes.TryRemove(old.Key, out _);
        string code = RandomNumberGenerator.GetInt32(100_000, 1_000_000).ToString(CultureInfo.InvariantCulture);
        var entry = new TelegramPairingCode(code, forUser, now + Lifetime);
        _codes[code] = entry;
        return entry;
    }

    /// <summary>The console user a live code was made for; the code is used up.</summary>
    public string? Take(string code) =>
        _codes.TryRemove(code.Trim(), out var entry) && entry.ExpiresUtc > _time.GetUtcNow().UtcDateTime ? entry.ForUser : null;
}

/// <summary>A Telegram account linked to a console user, as the settings table keeps it.</summary>
public sealed record TelegramOwner(long TelegramUserId, string ConsoleUser, string TelegramName, DateTime LinkedUtc);

/// <summary>
/// The Desk Assistant on Telegram, for its owner only: questions sent to the
/// desk's bot in a private chat are answered as on the Assistant tab.
/// </summary>
/// <remarks>
/// <para>
/// Only a linked account is answered. The console hands its admin a
/// six-digit code (AI → Assistant); sent to the bot as <c>/pair CODE</c>
/// within ten minutes, it links that Telegram account to that console user,
/// kept in <c>system_settings</c> (<c>ai.telegram.owner.&lt;id&gt;</c>).
/// Anyone else gets one line to /start and nothing else, and group chats
/// are ignored: the bot also posts the desk's notices to groups.
/// </para>
/// <para>
/// Each question goes through <see cref="AiGateway"/> as the Desk Assistant
/// (source <c>telegram</c>), so it has the Assistant's tools, switch, rate
/// limit and audit row. A chat keeps its last few turns for half an hour so a
/// follow-up works; <c>/new</c> starts over. The bot shows "typing…" while
/// the model thinks, and the answer arrives as Telegram HTML with the model,
/// the seconds and the call number under it.
/// </para>
/// <para>
/// Updates are read by long polling (getUpdates). Only one poller may read a
/// bot: nothing else on the desk reads this one (checked 30 Sep), and the
/// offset is kept in settings so a restart does not answer twice.
/// </para>
/// <para>
/// It is also where the owner teaches the Assistant from the phone
/// (<see cref="AiMemoryService"/>): 👍 and 👎 under every answer, a reply to
/// the bot's "what should it have said?" as a correction, <c>/remember</c>,
/// <c>/forget</c> and <c>/memory</c>, and Approve or Reject on each lesson
/// the daily check proposes. Buttons pressed by anyone but a linked owner do
/// nothing.
/// </para>
/// <para>
/// The owner can talk with the other agents too (<see cref="AgentCommands"/>):
/// <c>/trader …</c>, <c>/reviewer …</c>, <c>/news …</c> or <c>/incident …</c>
/// asks that agent as itself, its chat persona with the tools that read its own
/// work. The chat stays with it, so a follow-up needs no prefix, until
/// <c>/assistant</c>, <c>/new</c> or half an hour of quiet brings back the Desk
/// Assistant. <c>/remember</c> and <c>/memory</c> are the chat's agent's, and a
/// correction is the answered call's agent's.
/// </para>
/// </remarks>
public sealed class TelegramAssistant(
    IServiceScopeFactory scopes,
    IHttpClientFactory http,
    IConfiguration configuration,
    TelegramPairing pairing,
    IOptionsMonitor<AiSettings> settings,
    ILogger<TelegramAssistant> logger,
    TimeProvider? time = null) : BackgroundService, IAiLessonNotifier
{
    public const string HttpClientName = "telegram-assistant";
    public const string OffsetKey = "ai.telegram.offset";
    public const string OwnerPrefix = "ai.telegram.owner.";
    public const int HistoryMessages = 8;
    public static readonly TimeSpan HistoryIdle = TimeSpan.FromMinutes(30);

    /// <summary>The short commands that pick who answers, and what the bot calls each; the Desk Assistant by default.</summary>
    public static readonly IReadOnlyList<(string Command, string Agent, string Name)> AgentCommands =
    [
        ("/assistant", AiCatalog.DeskAssistant, "Assistant"),
        ("/trader", AiCatalog.AiTrader, "AI Trader"),
        ("/reviewer", AiCatalog.TradeReviewer, "Trade Reviewer"),
        ("/news", AiCatalog.NewsAnalyst, "News Analyst"),
        ("/incident", AiCatalog.IncidentExplainer, "Incident Explainer"),
    ];

    private const string AgentHelp = "/trader, /reviewer, /news or /incident before a question asks that agent; /assistant comes back.";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Each chat's agent and its last turns with it: a new agent starts a new conversation.</summary>
    private readonly ConcurrentDictionary<long, (string Agent, List<AiMessage> Turns, DateTime LastUtc)> _history = new();

    /// <summary>The bot's "what should it have said?" prompts by message id: a reply to one is a correction of that call.</summary>
    private readonly ConcurrentDictionary<long, (long CallId, DateTime AskedUtc)> _corrections = new();

    /// <summary>How long a "what should it have said?" prompt takes a reply.</summary>
    public static readonly TimeSpan CorrectionWindow = TimeSpan.FromHours(6);
    private bool _conflictLogged;

    private string? _botUsername;

    private string? Token => configuration["Telegram:BotToken"];

    /// <summary>The bot's @username (from getMe, kept once read), for the console's "send /pair CODE to …"; null when it cannot be read.</summary>
    public async Task<string?> BotUsernameAsync(CancellationToken cancellationToken)
    {
        if (_botUsername is not null || string.IsNullOrWhiteSpace(Token)) return _botUsername;
        try
        {
            using var response = await http.CreateClient(HttpClientName).GetAsync($"https://api.telegram.org/bot{Token}/getMe", cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            _botUsername = root?["result"]?["username"]?.GetValue<string>();
            return _botUsername;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public bool Enabled => settings.CurrentValue.TelegramAssistantEnabled && !string.IsNullOrWhiteSpace(Token) && settings.CurrentValue.KeyConfigured;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), _time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!Enabled)
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), _time, stoppingToken);
                    continue;
                }

                try
                {
                    await PollOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning("Telegram assistant: reading updates failed ({Type}); trying again in 10 s", ex.GetType().Name);
                    await Task.Delay(TimeSpan.FromSeconds(10), _time, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The API is stopping.
        }
    }

    /// <summary>One long poll and the updates it brought. Public for tests.</summary>
    public async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        long offset = await ReadOffsetAsync(cancellationToken);
        var client = http.CreateClient(HttpClientName);
        using var response = await client.GetAsync(
            $"https://api.telegram.org/bot{Token}/getUpdates?timeout=50&offset={offset}&allowed_updates=%5B%22message%22%2C%22callback_query%22%5D", cancellationToken);

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            if (!_conflictLogged) logger.LogWarning("Telegram assistant: another program is reading this bot's updates; waiting");
            _conflictLogged = true;
            await Task.Delay(TimeSpan.FromMinutes(1), _time, cancellationToken);
            return;
        }

        response.EnsureSuccessStatusCode();
        _conflictLogged = false;
        var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (root?["result"] is not JsonArray updates || updates.Count == 0) return;

        long next = offset;
        foreach (var update in updates.OfType<JsonObject>())
        {
            next = Math.Max(next, (update["update_id"]?.GetValue<long>() ?? 0) + 1);
            // Kept before answering: a question that crashes the handler is not asked again and again.
            await SaveOffsetAsync(next, cancellationToken);
            if (update["message"] is JsonObject message) await HandleAsync(message, cancellationToken);
            else if (update["callback_query"] is JsonObject press) await HandleButtonAsync(press, cancellationToken);
        }
    }

    /// <summary>One message to the bot. Public for tests.</summary>
    public async Task HandleAsync(JsonObject message, CancellationToken cancellationToken)
    {
        if (message["chat"]?["type"]?.GetValue<string>() != "private") return;
        long chatId = message["chat"]!["id"]!.GetValue<long>();
        long userId = message["from"]?["id"]?.GetValue<long>() ?? 0;
        string text = (message["text"]?.GetValue<string>() ?? string.Empty).Trim();
        string name = message["from"]?["username"]?.GetValue<string>() is { Length: > 0 } handle ? "@" + handle
            : message["from"]?["first_name"]?.GetValue<string>() ?? userId.ToString(CultureInfo.InvariantCulture);
        if (userId == 0 || text.Length == 0) return;

        var owners = await OwnersAsync(cancellationToken);
        var owner = owners.FirstOrDefault(o => o.TelegramUserId == userId);

        if (text.StartsWith("/pair", StringComparison.OrdinalIgnoreCase))
        {
            string code = text[5..].Trim();
            string? consoleUser = code.Length == 6 ? pairing.Take(code) : null;
            if (consoleUser is null)
            {
                await SendAsync(chatId, "That code is not valid or has expired. Make a new one on openfno.com: AI → Assistant → Link Telegram.", cancellationToken);
                return;
            }

            await LinkAsync(userId, consoleUser, name, cancellationToken);
            await SendAsync(chatId, $"Linked to {consoleUser}. Ask anything about the desk: runs, P&L, positions, the option chain, incidents, the docs. /new starts a new conversation. {AgentHelp}", cancellationToken);
            return;
        }

        if (owner is null)
        {
            // Strangers get one line to /start and nothing else.
            if (text.StartsWith("/start", StringComparison.OrdinalIgnoreCase)) await SendAsync(chatId, "This bot is private.", cancellationToken);
            return;
        }

        // "/trader why did you buy?" asks the AI Trader, and the chat stays with it; "/trader" alone switches to it.
        foreach (var (command, agentKey, agentName) in AgentCommands)
        {
            if (!IsCommand(text, command)) continue;
            string rest = AfterCommand(text, command);
            if (rest.Length == 0)
            {
                _history[chatId] = (agentKey, [], _time.GetUtcNow().UtcDateTime);
                await SendAsync(chatId, agentKey == AiCatalog.DeskAssistant
                    ? "Talking with the Desk Assistant. Ask about the desk."
                    : $"Talking with the {agentName}: questions go to it until /assistant, /new or half an hour of quiet. Ask it about its own work.", cancellationToken);
                return;
            }

            await AskAsync(chatId, owner, agentKey, rest.Length <= 4000 ? rest : rest[..4000], cancellationToken);
            return;
        }

        if (IsCommand(text, "/new") || IsCommand(text, "/start"))
        {
            _history.TryRemove(chatId, out _);
            await SendAsync(chatId, $"New conversation. Ask about the desk. {AgentHelp}", cancellationToken);
            return;
        }

        if (text.StartsWith("/unlink", StringComparison.OrdinalIgnoreCase))
        {
            await UnlinkAsync(userId, cancellationToken);
            _history.TryRemove(chatId, out _);
            await SendAsync(chatId, "Unlinked. This chat no longer reaches the desk.", cancellationToken);
            return;
        }

        // A reply to "what should it have said?" is the correction of that answer.
        if (message["reply_to_message"]?["message_id"]?.GetValue<long>() is long repliedTo
            && _corrections.TryGetValue(repliedTo, out var pending)
            && _time.GetUtcNow().UtcDateTime - pending.AskedUtc < CorrectionWindow
            && !text.StartsWith('/'))
        {
            _corrections.TryRemove(repliedTo, out _);
            await MemoryAsync(chatId, async memory =>
            {
                var (_, saved) = await memory.FeedbackAsync(pending.CallId, -1, text, owner.ConsoleUser, "telegram", cancellationToken);
                return $"Saved as correction M{saved!.Id}. The {NameOf(saved.AgentKey)} reads it from the next question. /forget {saved.Id} takes it out.";
            }, cancellationToken);
            return;
        }

        if (IsCommand(text, "/remember"))
        {
            string note = AfterCommand(text, "/remember");
            if (note.Length == 0)
            {
                await SendAsync(chatId, "Write it after the command, for example: /remember weekly NIFTY options expire on Tuesday.", cancellationToken);
                return;
            }

            // The note is for the agent this chat is talking with.
            string agent = AgentOf(chatId);
            await MemoryAsync(chatId, async memory =>
            {
                var saved = await memory.RememberAsync(agent, note, owner.ConsoleUser, "telegram", cancellationToken);
                return $"Saved as note M{saved.Id}. The {NameOf(agent)} reads it from the next question. /forget {saved.Id} takes it out.";
            }, cancellationToken);
            return;
        }

        if (IsCommand(text, "/forget"))
        {
            string arg = text["/forget".Length..].Trim().TrimStart('M', 'm');
            if (!long.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id))
            {
                await SendAsync(chatId, "Which one? For example /forget 12. /memory lists them.", cancellationToken);
                return;
            }

            await MemoryAsync(chatId, async memory =>
            {
                var retired = await memory.UpdateAsync(id, null, AiMemoryStatus.Retired, owner.ConsoleUser, cancellationToken);
                return $"M{id} is retired: the {NameOf(retired.AgentKey)} no longer reads it. It can be restored on AI → Memory.";
            }, cancellationToken);
            return;
        }

        if (IsCommand(text, "/memory"))
        {
            await SendAsync(chatId, await MemoryListAsync(AgentOf(chatId), cancellationToken), cancellationToken);
            return;
        }

        await AskAsync(chatId, owner, AgentOf(chatId), text.Length <= 4000 ? text : text[..4000], cancellationToken);
    }

    /// <summary>The agent a chat is talking with: the one its last question went to, while the conversation lasts; else the Desk Assistant.</summary>
    private string AgentOf(long chatId) =>
        _history.TryGetValue(chatId, out var h) && _time.GetUtcNow().UtcDateTime - h.LastUtc < HistoryIdle ? h.Agent : AiCatalog.DeskAssistant;

    /// <summary>What the bot calls an agent: "Assistant", "AI Trader".</summary>
    public static string NameOf(string agentKey) =>
        AgentCommands.FirstOrDefault(c => c.Agent == agentKey).Name ?? AiCatalog.AgentName(agentKey);

    /// <summary>What follows a command: "/trader why?" and "/trader@bot why?" both give "why?".</summary>
    private static string AfterCommand(string text, string command)
    {
        string rest = text[command.Length..];
        if (rest.StartsWith('@'))
        {
            int space = rest.IndexOf(' ');
            rest = space < 0 ? string.Empty : rest[space..];
        }

        return rest.Trim();
    }

    private async Task AskAsync(long chatId, TelegramOwner owner, string agentKey, string question, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        // A conversation is with one agent: another agent starts its own.
        var turns = _history.TryGetValue(chatId, out var h) && now - h.LastUtc < HistoryIdle && h.Agent == agentKey ? h.Turns : [];
        var messages = turns.TakeLast(HistoryMessages).Append(new AiMessage("user", question)).ToList();
        _history[chatId] = (agentKey, turns, now);

        using var typing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var typingLoop = TypingAsync(chatId, typing.Token);
        AiAskResult result;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            long? consoleUserId = await db.AppUsers.AsNoTracking().Where(u => u.UserName == owner.ConsoleUser).Select(u => (long?)u.Id).FirstOrDefaultAsync(cancellationToken);
            var gateway = scope.ServiceProvider.GetRequiredService<AiGateway>();
            result = await gateway.AskAsync(new AiAskInput(
                agentKey, null, messages, null, 4096, 0.2, $"tg-{chatId}", "telegram", owner.ConsoleUser, consoleUserId),
                NullAiStreamSink.Instance, cancellationToken);
        }
        finally
        {
            await typing.CancelAsync();
            try { await typingLoop; } catch (OperationCanceledException) { }
        }

        if (result.Outcome != AiCallOutcome.Ok)
        {
            string why = result.RefusalStatus is null ? $"No model answered (call #{result.CallId}): {result.Error}" : result.Error;
            await SendAsync(chatId, why, cancellationToken);
            return;
        }

        _history[chatId] = (agentKey, messages.Append(new AiMessage("assistant", result.Text)).TakeLast(HistoryMessages).ToList(), _time.GetUtcNow().UtcDateTime);
        // Another agent than the Assistant is named, so the owner knows who answered.
        string who = agentKey == AiCatalog.DeskAssistant ? string.Empty : $"{NameOf(agentKey)} · ";
        string footer = $"— {who}{ShortModel(result.Model)} · {result.Seconds:0} s" +
            (result.Tools.Count > 0 ? $" · {result.Tools.Count} tool call{(result.Tools.Count == 1 ? "" : "s")}" : string.Empty) +
            (result.MemoryIds.Count > 0 ? $" · read {result.MemoryIds.Count} memor{(result.MemoryIds.Count == 1 ? "y" : "ies")}" : string.Empty) +
            $" · call #{result.CallId}";
        var parts = TelegramText.Split(result.Text.Trim() + "\n\n" + footer);
        for (int i = 0; i < parts.Count; i++)
        {
            // The verdict buttons go under the last part of the answer.
            await SendAsync(chatId, parts[i], cancellationToken, html: true, markup: i == parts.Count - 1 ? VerdictButtons(result.CallId) : null);
        }
    }

    private static JsonObject VerdictButtons(long callId) => Keyboard(("👍", $"fb:1:{callId}"), ("👎", $"fb:-1:{callId}"));

    private static JsonObject Keyboard(params (string Text, string Data)[] buttons) => new()
    {
        ["inline_keyboard"] = new JsonArray(new JsonArray(buttons.Select(b => (JsonNode)new JsonObject { ["text"] = b.Text, ["callback_data"] = b.Data }).ToArray())),
    };

    private static bool IsCommand(string text, string command) =>
        text.Equals(command, StringComparison.OrdinalIgnoreCase)
        || text.StartsWith(command + " ", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith(command + "@", StringComparison.OrdinalIgnoreCase);

    /// <summary>A button under a message. Public for tests.</summary>
    public async Task HandleButtonAsync(JsonObject press, CancellationToken cancellationToken)
    {
        string pressId = press["id"]?.GetValue<string>() ?? string.Empty;
        long userId = press["from"]?["id"]?.GetValue<long>() ?? 0;
        string data = press["data"]?.GetValue<string>() ?? string.Empty;
        long chatId = press["message"]?["chat"]?["id"]?.GetValue<long>() ?? 0;
        long messageId = press["message"]?["message_id"]?.GetValue<long>() ?? 0;

        var owner = (await OwnersAsync(cancellationToken)).FirstOrDefault(o => o.TelegramUserId == userId);
        if (owner is null || chatId == 0)
        {
            await CallApiAsync("answerCallbackQuery", new JsonObject { ["callback_query_id"] = pressId }, cancellationToken);
            return;
        }

        string[] f = data.Split(':');
        string toast;
        JsonObject? markup = null;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var memory = scope.ServiceProvider.GetRequiredService<AiMemoryService>();
            switch (f)
            {
                case ["fb", var sc, var idText] when int.TryParse(sc, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int score)
                    && long.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long callId):
                    var (answered, _) = await memory.FeedbackAsync(callId, score, null, owner.ConsoleUser, "telegram", cancellationToken);
                    toast = score == 1 ? "Noted: a good answer." : "Noted: a bad answer.";
                    markup = Keyboard((score == 1 ? "👍 noted" : "👎 noted", "noop"));
                    if (score == -1)
                    {
                        long? prompt = await SendAsync(chatId,
                            $"What should it have said? Reply to this message with the right answer or rule (call #{callId}). It becomes a correction the {NameOf(answered.AgentKey)} reads from the next question.",
                            cancellationToken, markup: new JsonObject { ["force_reply"] = true, ["input_field_placeholder"] = "The right answer or rule" });
                        if (prompt is long promptId) _corrections[promptId] = (callId, _time.GetUtcNow().UtcDateTime);
                    }

                    break;
                case ["mem", var verb, var idText] when verb is "a" or "r"
                    && long.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long memoryId):
                    var saved = await memory.UpdateAsync(memoryId, null, verb == "a" ? AiMemoryStatus.Active : AiMemoryStatus.Rejected, owner.ConsoleUser, cancellationToken);
                    toast = verb == "a" ? $"M{saved.Id} approved: the {NameOf(saved.AgentKey)} reads it from now on." : $"M{saved.Id} rejected.";
                    markup = Keyboard((verb == "a" ? $"Approved by {owner.ConsoleUser}" : $"Rejected by {owner.ConsoleUser}", "noop"));
                    break;
                default:
                    toast = string.Empty;
                    break;
            }
        }
        catch (AiMemoryException ex)
        {
            toast = ex.Message;
        }

        await CallApiAsync("answerCallbackQuery", new JsonObject { ["callback_query_id"] = pressId, ["text"] = toast.Length > 190 ? toast[..190] : toast }, cancellationToken);
        if (markup is not null && messageId > 0)
        {
            await CallApiAsync("editMessageReplyMarkup", new JsonObject { ["chat_id"] = chatId, ["message_id"] = messageId, ["reply_markup"] = markup }, cancellationToken);
        }
    }

    /// <summary>
    /// The day's lessons in one message to every linked owner: each was tested by the check before it was
    /// used, so nothing waits for an answer (owner, 1 Oct). /forget takes one out.
    /// </summary>
    public async Task LessonsProposedAsync(IReadOnlyList<AiMemory> lessons, CancellationToken cancellationToken)
    {
        if (!Enabled || lessons.Count == 0) return;
        var used = lessons.Where(l => l.Status == AiMemoryStatus.Active).ToList();
        var dropped = lessons.Where(l => l.Status != AiMemoryStatus.Active).ToList();
        var text = new System.Text.StringBuilder();
        text.Append($"Today's check tested {lessons.Count} lesson{(lessons.Count == 1 ? "" : "s")} for the {AiCatalog.AgentName(lessons[0].AgentKey)}.");
        if (used.Count > 0)
        {
            text.Append("\n\nLearned (fixed the question it came from, broke nothing):");
            foreach (var l in used) text.Append($"\nM{l.Id}: {l.Text}");
        }

        if (dropped.Count > 0)
        {
            text.Append("\n\nDropped:");
            foreach (var l in dropped) text.Append($"\nM{l.Id}: {l.DecidedBy.Replace("check: ", string.Empty)}");
        }

        text.Append("\n\nNothing to answer. /forget N takes a lesson out; all of it is on Today and AI → Memory.");
        foreach (var owner in await OwnersAsync(cancellationToken))
        {
            await SendAsync(owner.TelegramUserId, text.ToString(), cancellationToken);
        }
    }

    private async Task MemoryAsync(long chatId, Func<AiMemoryService, Task<string>> action, CancellationToken cancellationToken)
    {
        string reply;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            reply = await action(scope.ServiceProvider.GetRequiredService<AiMemoryService>());
        }
        catch (AiMemoryException ex)
        {
            reply = ex.Message;
        }

        await SendAsync(chatId, reply, cancellationToken);
    }

    private async Task<string> MemoryListAsync(string agentKey, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var rows = await db.AiMemories.AsNoTracking()
            .Where(m => m.AgentKey == agentKey && (m.Status == AiMemoryStatus.Active || m.Status == AiMemoryStatus.Proposed))
            .OrderByDescending(m => m.Id)
            .ToListAsync(cancellationToken);
        var active = rows.Where(m => m.Status == AiMemoryStatus.Active).ToList();
        int waiting = rows.Count - active.Count;
        string name = NameOf(agentKey);
        if (active.Count == 0 && waiting == 0) return $"The {name} has no memories yet. Teach it with /remember, or 👎 an answer and reply with what it should have said.";

        var lines = active.Take(15).Select(m => $"M{m.Id} ({m.Kind}): {(m.Text.Length <= 160 ? m.Text : m.Text[..159] + "…")}").ToList();
        if (active.Count > 15) lines.Add($"…and {active.Count - 15} more on AI → Memory.");
        if (waiting > 0) lines.Add($"{waiting} lesson{(waiting == 1 ? "" : "s")} waiting for your approval on AI → Memory.");
        return $"The {name} reads {active.Count} memor{(active.Count == 1 ? "y" : "ies")}:\n" + string.Join("\n", lines) + "\n/forget N takes one out.";
    }

    private async Task CallApiAsync(string method, JsonObject body, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.CreateClient(HttpClientName).PostAsJsonAsync($"https://api.telegram.org/bot{Token}/{method}", body, cancellationToken);
            if (!response.IsSuccessStatusCode) logger.LogInformation("Telegram assistant: {Method} was refused (HTTP {Status})", method, (int)response.StatusCode);
        }
        catch (HttpRequestException ex)
        {
            logger.LogInformation("Telegram assistant: {Method} failed ({Error})", method, ex.HttpRequestError);
        }
    }

    private async Task TypingAsync(long chatId, CancellationToken cancellationToken)
    {
        var client = http.CreateClient(HttpClientName);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var _ = await client.PostAsJsonAsync($"https://api.telegram.org/bot{Token}/sendChatAction",
                    new { chat_id = chatId, action = "typing" }, cancellationToken);
            }
            catch (HttpRequestException)
            {
                // Only a courtesy.
            }

            await Task.Delay(TimeSpan.FromSeconds(4), _time, cancellationToken);
        }
    }

    /// <summary>
    /// Sends one message, with buttons or a reply prompt when given; as HTML
    /// when asked, and as plain text if Telegram refuses the HTML. The sent
    /// message's id, or null when it was not sent.
    /// </summary>
    private async Task<long?> SendAsync(long chatId, string text, CancellationToken cancellationToken, bool html = false, JsonObject? markup = null)
    {
        var client = http.CreateClient(HttpClientName);
        if (html)
        {
            var rich = new JsonObject { ["chat_id"] = chatId, ["text"] = TelegramText.ToHtml(text), ["parse_mode"] = "HTML", ["disable_web_page_preview"] = true };
            if (markup is not null) rich["reply_markup"] = markup.DeepClone();
            using var sent = await client.PostAsJsonAsync($"https://api.telegram.org/bot{Token}/sendMessage", rich, cancellationToken);
            if (sent.IsSuccessStatusCode) return await MessageIdAsync(sent, cancellationToken);
        }

        var plain = new JsonObject { ["chat_id"] = chatId, ["text"] = text, ["disable_web_page_preview"] = true };
        if (markup is not null) plain["reply_markup"] = markup.DeepClone();
        using var response = await client.PostAsJsonAsync($"https://api.telegram.org/bot{Token}/sendMessage", plain, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Telegram assistant: a reply was refused (HTTP {Status})", (int)response.StatusCode);
            return null;
        }

        return await MessageIdAsync(response, cancellationToken);
    }

    private static async Task<long?> MessageIdAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))?["result"]?["message_id"]?.GetValue<long>();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<TelegramOwner>> OwnersAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var rows = await db.SystemSettings.AsNoTracking().Where(s => s.Key.StartsWith(OwnerPrefix)).ToListAsync(cancellationToken);
        return rows
            .Select(r => long.TryParse(r.Key[OwnerPrefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out long id)
                ? new TelegramOwner(id, r.Value, r.Reason ?? string.Empty, DateTime.SpecifyKind(r.UpdatedUtc, DateTimeKind.Utc))
                : null)
            .Where(o => o is not null)
            .Select(o => o!)
            .ToList();
    }

    private async Task LinkAsync(long telegramUserId, string consoleUser, string telegramName, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        string key = OwnerPrefix + telegramUserId.ToString(CultureInfo.InvariantCulture);
        var now = _time.GetUtcNow().UtcDateTime;
        var row = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (row is null)
        {
            row = new SystemSetting { Key = key, CreatedUtc = now };
            db.SystemSettings.Add(row);
        }

        row.Value = consoleUser;
        row.UpdatedBy = consoleUser;
        row.Reason = telegramName;
        row.UpdatedUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Telegram assistant: {Telegram} linked to {User}", telegramName, consoleUser);
    }

    public async Task<bool> UnlinkAsync(long telegramUserId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        string key = OwnerPrefix + telegramUserId.ToString(CultureInfo.InvariantCulture);
        var row = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (row is null) return false;
        db.SystemSettings.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<long> ReadOffsetAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        string? value = await db.SystemSettings.AsNoTracking().Where(s => s.Key == OffsetKey).Select(s => s.Value).FirstOrDefaultAsync(cancellationToken);
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long offset) ? offset : 0;
    }

    private async Task SaveOffsetAsync(long offset, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var row = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == OffsetKey, cancellationToken);
        var now = _time.GetUtcNow().UtcDateTime;
        if (row is null)
        {
            row = new SystemSetting { Key = OffsetKey, CreatedUtc = now };
            db.SystemSettings.Add(row);
        }

        row.Value = offset.ToString(CultureInfo.InvariantCulture);
        row.UpdatedUtc = now;
        row.UpdatedBy = "telegram-assistant";
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string ShortModel(string id) => id.Contains('/') ? id[(id.IndexOf('/') + 1)..] : id;
}
