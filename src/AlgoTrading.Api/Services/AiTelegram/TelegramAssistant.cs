using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
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
/// </remarks>
public sealed class TelegramAssistant(
    IServiceScopeFactory scopes,
    IHttpClientFactory http,
    IConfiguration configuration,
    TelegramPairing pairing,
    IOptionsMonitor<AiSettings> settings,
    ILogger<TelegramAssistant> logger,
    TimeProvider? time = null) : BackgroundService
{
    public const string HttpClientName = "telegram-assistant";
    public const string OffsetKey = "ai.telegram.offset";
    public const string OwnerPrefix = "ai.telegram.owner.";
    public const int HistoryMessages = 8;
    public static readonly TimeSpan HistoryIdle = TimeSpan.FromMinutes(30);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<long, (List<AiMessage> Turns, DateTime LastUtc)> _history = new();
    private bool _conflictLogged;

    private string? Token => configuration["Telegram:BotToken"];

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
            $"https://api.telegram.org/bot{Token}/getUpdates?timeout=50&offset={offset}&allowed_updates=%5B%22message%22%5D", cancellationToken);

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
            await SendAsync(chatId, $"Linked to {consoleUser}. Ask anything about the desk: runs, P&L, positions, the option chain, incidents, the docs. /new starts a new conversation.", cancellationToken);
            return;
        }

        if (owner is null)
        {
            // Strangers get one line to /start and nothing else.
            if (text.StartsWith("/start", StringComparison.OrdinalIgnoreCase)) await SendAsync(chatId, "This bot is private.", cancellationToken);
            return;
        }

        if (text.StartsWith("/new", StringComparison.OrdinalIgnoreCase) || text.StartsWith("/start", StringComparison.OrdinalIgnoreCase))
        {
            _history.TryRemove(chatId, out _);
            await SendAsync(chatId, "New conversation. Ask about the desk.", cancellationToken);
            return;
        }

        if (text.StartsWith("/unlink", StringComparison.OrdinalIgnoreCase))
        {
            await UnlinkAsync(userId, cancellationToken);
            _history.TryRemove(chatId, out _);
            await SendAsync(chatId, "Unlinked. This chat no longer reaches the desk.", cancellationToken);
            return;
        }

        await AskAsync(chatId, owner, text.Length <= 4000 ? text : text[..4000], cancellationToken);
    }

    private async Task AskAsync(long chatId, TelegramOwner owner, string question, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var turns = _history.TryGetValue(chatId, out var h) && now - h.LastUtc < HistoryIdle ? h.Turns : [];
        var messages = turns.TakeLast(HistoryMessages).Append(new AiMessage("user", question)).ToList();

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
                AiCatalog.DeskAssistant, null, messages, null, 4096, 0.2, $"tg-{chatId}", "telegram", owner.ConsoleUser, consoleUserId),
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

        _history[chatId] = (messages.Append(new AiMessage("assistant", result.Text)).TakeLast(HistoryMessages).ToList(), _time.GetUtcNow().UtcDateTime);
        string footer = $"— {ShortModel(result.Model)} · {result.Seconds:0} s" +
            (result.Tools.Count > 0 ? $" · {result.Tools.Count} tool call{(result.Tools.Count == 1 ? "" : "s")}" : string.Empty) +
            $" · call #{result.CallId}";
        foreach (string part in TelegramText.Split(result.Text.Trim() + "\n\n" + footer))
        {
            await SendAsync(chatId, part, cancellationToken, html: true);
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

    /// <summary>Sends one message; as HTML when asked, and as plain text if Telegram refuses the HTML.</summary>
    private async Task SendAsync(long chatId, string text, CancellationToken cancellationToken, bool html = false)
    {
        var client = http.CreateClient(HttpClientName);
        if (html)
        {
            using var rich = await client.PostAsJsonAsync($"https://api.telegram.org/bot{Token}/sendMessage",
                new { chat_id = chatId, text = TelegramText.ToHtml(text), parse_mode = "HTML", disable_web_page_preview = true }, cancellationToken);
            if (rich.IsSuccessStatusCode) return;
        }

        using var plain = await client.PostAsJsonAsync($"https://api.telegram.org/bot{Token}/sendMessage",
            new { chat_id = chatId, text, disable_web_page_preview = true }, cancellationToken);
        if (!plain.IsSuccessStatusCode) logger.LogWarning("Telegram assistant: a reply was refused (HTTP {Status})", (int)plain.StatusCode);
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
