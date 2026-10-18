using System.Globalization;
using System.Text.Json;
using AlgoTrading.Api.Services.AiAgents;
using AlgoTrading.Api.Services.AiTelegram;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services.AiVoice;

/// <summary>
/// What Ira keeps between passes, in one <c>system_settings</c> row (<see cref="Key"/>): each source's high-water
/// mark, the day each briefing last went, and each owner's alerts of the last hour.
/// </summary>
public sealed class IraMarks
{
    public const string Key = AiSettingsStore.Prefix + "voice.alerts.marks";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The AI Trader's decisions (<c>ai_trader_decisions</c>).</summary>
    public IraCursor Decisions { get; set; } = new();

    /// <summary>The runs' signals (<c>simulation_signals</c>): the risk guard's stops and group closes.</summary>
    public IraCursor Signals { get; set; } = new();

    /// <summary>Sentinel's incidents (<c>incidents</c>).</summary>
    public IraCursor Incidents { get; set; } = new();

    /// <summary>The circulars the Compliance Triage found apply (<c>compliance_follow_ups</c> with an alert).</summary>
    public IraCursor Compliance { get; set; } = new();

    /// <summary>The IST day (yyyy-MM-dd) the last morning briefing went.</summary>
    public string? Morning { get; set; }

    /// <summary>The IST day (yyyy-MM-dd) the last close briefing went.</summary>
    public string? Close { get; set; }

    /// <summary>Each linked owner's event messages of the last hour and what was folded, by Telegram user id.</summary>
    public Dictionary<string, IraOwnerHour> Owners { get; set; } = new(StringComparer.Ordinal);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>The marks a row holds; null for none, or for one that cannot be read.</summary>
    public static IraMarks? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<IraMarks>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The row's text as stored (read with <see cref="FromJson"/>); null when there is none yet.</summary>
    public static Task<string?> ReadAsync(TradingDbContext db, CancellationToken cancellationToken) =>
        db.SystemSettings.AsNoTracking().Where(s => s.Key == Key).Select(s => s.Value).FirstOrDefaultAsync(cancellationToken);

    public async Task SaveAsync(TradingDbContext db, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var row = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == Key, cancellationToken);
        if (row is null)
        {
            row = new SystemSetting { Key = Key, CreatedUtc = nowUtc };
            db.SystemSettings.Add(row);
        }

        row.Value = ToJson();
        row.UpdatedBy = IraAlertService.By;
        row.Reason = "Ira's alerts: what has been told";
        row.UpdatedUtc = nowUtc;
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>One owner's last hour: when each event message went, and how many were folded since when.</summary>
public sealed class IraOwnerHour
{
    public List<DateTime> Sent { get; set; } = [];

    public int Folded { get; set; }

    public DateTime? FoldedSinceUtc { get; set; }
}

/// <summary>
/// Each linked owner's own switch for Ira's alerts, set with <c>/alerts on</c> and <c>/alerts off</c> on Telegram and
/// kept in <c>system_settings</c> (<c>ai.voice.alerts.owner.&lt;Telegram user id&gt;</c>, <c>on</c> or <c>off</c>). On
/// until he turns it off. Only /alerts writes it, so the alert pass never overwrites his choice.
/// </summary>
public static class IraAlertPrefs
{
    public const string Prefix = AiSettingsStore.Prefix + "voice.alerts.owner.";

    public static string Key(long telegramUserId) => Prefix + telegramUserId.ToString(CultureInfo.InvariantCulture);

    /// <summary>The owners (Telegram user ids) who turned their alerts off.</summary>
    public static async Task<HashSet<long>> OffAsync(TradingDbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.SystemSettings.AsNoTracking().Where(s => s.Key.StartsWith(Prefix) && s.Value == "off").Select(s => s.Key).ToListAsync(cancellationToken);
        return rows.Select(k => long.TryParse(k[Prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) ? id : 0)
            .Where(id => id != 0)
            .ToHashSet();
    }

    public static async Task<bool> IsOnAsync(TradingDbContext db, long telegramUserId, CancellationToken cancellationToken)
    {
        string key = Key(telegramUserId);
        return await db.SystemSettings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(cancellationToken) != "off";
    }

    public static async Task SetAsync(TradingDbContext db, long telegramUserId, bool on, string by, DateTime nowUtc, CancellationToken cancellationToken)
    {
        string key = Key(telegramUserId);
        var row = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (row is null)
        {
            row = new SystemSetting { Key = key, CreatedUtc = nowUtc };
            db.SystemSettings.Add(row);
        }

        row.Value = on ? "on" : "off";
        row.UpdatedBy = by;
        row.Reason = $"Set from Telegram with /alerts {row.Value}";
        row.UpdatedUtc = nowUtc;
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// Ira speaks first (step 3a, 11 Oct 2026): when something matters, a short message and the same words as a voice
/// note go to each linked owner's private chat on Telegram, without being asked.
/// </summary>
/// <remarks>
/// <para>
/// <b>What.</b> The AI Trader acting live (a buy, an exit, a strategy started or stopped); a live run's risk rules
/// stopping it or closing a group; a Sentinel incident opened at high or critical; a circular or a broker's notice the
/// Compliance Triage found applies to the desk, once (<see cref="IraDesk"/>, <see cref="ComplianceAlerts"/>). And on NSE
/// trading days (the exchange calendar), a morning briefing at <c>MorningAt</c> (09:10 IST) and a close briefing at
/// <c>CloseAt</c> (15:40 IST). All worded by code (<see cref="IraAlertText"/>); no model writes any of it.
/// </para>
/// <para>
/// <b>When.</b> One pass a minute, 90 s after a start (as the scheduled agents), only where the scheduled agents run
/// (<see cref="AiSettings.SchedulerEnabled"/>), while <c>Ai:Voice:Alerts:Enabled</c> is on and the bot can send. A
/// briefing missed by more than <see cref="BriefingWindow"/> (the API down at its time) is not sent late.
/// </para>
/// <para>
/// <b>Never twice, nothing missed.</b> The marks (<see cref="IraMarks"/>) are saved before anything is sent: a crash
/// between the two loses a message, and nothing is told twice. After a restart the marks carry on where they were, and
/// anything first seen more than <see cref="Freshness"/> ago is not told.
/// </para>
/// <para>
/// <b>Not too much.</b> At most <c>MaxPerHour</c> (8) event messages to one owner in an hour; past that they are
/// folded, and the next message he gets (an event, a briefing, or the line alone once there is room) says "And N more
/// alerts since HH:MM IST". Briefings are not counted. <c>/alerts off</c> stops both for that owner only.
/// </para>
/// <para>
/// <b>Voice.</b> The text goes first; then the same words through the assistant's one voice
/// (<see cref="AiVoiceService"/>, source <see cref="AiVoiceService.Alert"/>), once per distinct message, respecting its
/// hour's pause after a Polly refusal. If Polly cannot speak, the text has gone anyway.
/// </para>
/// <para>
/// <b>Quiet failures.</b> Every line it logs is Information: an alert that did not go is not a desk incident, and
/// Sentinel raises none for it. Each message sent is logged.
/// </para>
/// </remarks>
public sealed class IraAlertService(
    IServiceScopeFactory scopes,
    TelegramAssistant telegram,
    AiVoiceService voice,
    IMarketSessionService sessions,
    IOptionsMonitor<AiSettings> settings,
    ILogger<IraAlertService> logger,
    TimeProvider? time = null) : BackgroundService
{
    /// <summary>Who the marks row says wrote it.</summary>
    public const string By = "ira-alerts";

    /// <summary>Nothing first seen longer ago than this is told: after a restart, the last half hour is, and older history is not.</summary>
    public static readonly TimeSpan Freshness = TimeSpan.FromMinutes(30);

    /// <summary>How long after its time a briefing may still go; past it, that day's is skipped.</summary>
    public static readonly TimeSpan BriefingWindow = TimeSpan.FromMinutes(30);

    /// <summary>The close briefing waits this long after a session that closes later than <c>CloseAt</c>.</summary>
    public static readonly TimeSpan AfterClose = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private bool _refusalLogged;

    /// <summary>Whether a pass runs at all: the desk-wide switch, an API that runs the scheduled agents, and a bot that can send.</summary>
    public bool Active
    {
        get
        {
            var s = settings.CurrentValue;
            return s.Voice.Alerts.Enabled && s.SchedulerEnabled && telegram.CanSend;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(AiAgentScheduler.StartDelay, _time, stoppingToken);
            using var timer = new PeriodicTimer(AiAgentScheduler.Tick, _time);
            do
            {
                try
                {
                    await RunOnceAsync(_time.GetUtcNow().UtcDateTime, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    // An alert that did not go is not a desk incident: Information, so Sentinel raises nothing.
                    logger.LogInformation("Ira's alerts: the minute's pass failed ({Type}: {Message}); it runs again next minute", ex.GetType().Name, ex.Message);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The API is stopping.
        }
    }

    /// <summary>One message to one owner, as it will be sent.</summary>
    private sealed record Outgoing(TelegramOwner Owner, string Kind, string Text);

    /// <summary>One minute's pass: the events since the marks, the briefings due, each owner's cap, then the sending. Public for tests.</summary>
    public async Task RunOnceAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (!Active) return;
        var alerts = settings.CurrentValue.Voice.Alerts;
        int max = Math.Max(1, alerts.MaxPerHour);
        var outbox = new List<Outgoing>();

        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            var desk = scope.ServiceProvider.GetRequiredService<IIraDesk>();
            string? stored = await IraMarks.ReadAsync(db, cancellationToken);
            var marks = IraMarks.FromJson(stored) ?? await desk.StartAsync(cancellationToken);
            var events = await desk.EventsAsync(marks, nowUtc, cancellationToken);

            var owners = await telegram.OwnersAsync(cancellationToken);
            var off = await IraAlertPrefs.OffAsync(db, cancellationToken);
            var listening = owners.Where(o => !off.Contains(o.TelegramUserId)).ToList();
            foreach (string gone in marks.Owners.Keys.Where(k => owners.All(o => Id(o) != k)).ToList()) marks.Owners.Remove(gone);

            // Events, oldest first, each owner within his hour's cap; past it, folded into the next message he gets.
            foreach (var e in events)
            {
                foreach (var owner in listening)
                {
                    var hour = HourOf(marks, owner, nowUtc);
                    if (hour.Sent.Count >= max)
                    {
                        hour.Folded++;
                        hour.FoldedSinceUtc ??= e.AtUtc;
                        continue;
                    }

                    outbox.Add(new Outgoing(owner, e.Kind, Fold(e.Text, hour)));
                    hour.Sent.Add(nowUtc);
                }
            }

            // What was folded goes on its own once the hour has room again and no event carried it.
            foreach (var owner in listening)
            {
                var hour = HourOf(marks, owner, nowUtc);
                if (hour.Folded == 0 || hour.FoldedSinceUtc is not DateTime since || hour.Sent.Count >= max) continue;
                outbox.Add(new Outgoing(owner, "folded", IraAlertText.Folded(hour.Folded, since)));
                hour.Sent.Add(nowUtc);
                hour.Folded = 0;
                hour.FoldedSinceUtc = null;
            }

            // The briefings: not counted against the cap, and they carry anything still folded.
            if (listening.Count > 0)
            {
                var session = sessions.GetSessionInfo(nowUtc, "NSE", "FO");
                var day = IstTime.DateOf(nowUtc);
                string dayText = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (session.IsTradingDay)
                {
                    var morningDue = IstTime.FromIst(day.ToDateTime(At(alerts.MorningAt, new TimeOnly(9, 10))));
                    if (marks.Morning != dayText && nowUtc >= morningDue && nowUtc < morningDue + BriefingWindow
                        && await BriefingAsync("morning", () => desk.MorningAsync(nowUtc, cancellationToken), IraAlertText.Morning, cancellationToken) is { } morning)
                    {
                        marks.Morning = dayText;
                        outbox.AddRange(listening.Select(o => new Outgoing(o, "morning", Fold(morning, HourOf(marks, o, nowUtc)))));
                    }

                    var closeDue = IstTime.FromIst(day.ToDateTime(At(alerts.CloseAt, new TimeOnly(15, 40))));
                    if (session.SessionCloseUtc + AfterClose > closeDue) closeDue = session.SessionCloseUtc + AfterClose;
                    if (marks.Close != dayText && nowUtc >= closeDue && nowUtc < closeDue + BriefingWindow
                        && await BriefingAsync("close", () => desk.CloseAsync(nowUtc, cancellationToken), IraAlertText.Close, cancellationToken) is { } closing)
                    {
                        marks.Close = dayText;
                        outbox.AddRange(listening.Select(o => new Outgoing(o, "close", Fold(closing, HourOf(marks, o, nowUtc)))));
                    }
                }
            }

            // Kept before anything is sent: a crash between the two loses a message, and nothing is told twice. A quiet
            // minute that moved nothing writes nothing.
            if (marks.ToJson() != stored) await marks.SaveAsync(db, nowUtc, cancellationToken);
        }

        await SendAsync(outbox, cancellationToken);
    }

    /// <summary>
    /// A briefing's words, or null when its facts could not be read: logged at Information and tried again next minute
    /// while its window lasts, and the events of the pass still go.
    /// </summary>
    private async Task<string?> BriefingAsync<T>(string kind, Func<Task<T>> facts, Func<T, string> words, CancellationToken cancellationToken)
    {
        try
        {
            return words(await facts());
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Ira: the {Kind} briefing's facts could not be read ({Type}: {Message}); trying again next minute", kind, ex.GetType().Name, ex.Message);
            return null;
        }
    }

    /// <summary>The text first, then the same words as a voice note; Polly asked once per distinct message.</summary>
    private async Task SendAsync(IReadOnlyList<Outgoing> outbox, CancellationToken cancellationToken)
    {
        var spoken = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        foreach (var m in outbox)
        {
            long chat = m.Owner.TelegramUserId;
            if (!await telegram.SendAlertTextAsync(chat, m.Text, cancellationToken))
            {
                logger.LogInformation("Ira: a {Kind} alert to {User} was not delivered", m.Kind, m.Owner.ConsoleUser);
                continue;
            }

            if (!spoken.TryGetValue(m.Text, out var mp3))
            {
                mp3 = await SpeakAsync(m, cancellationToken);
                spoken[m.Text] = mp3;
            }

            bool voiced = mp3 is not null && await telegram.SendAlertVoiceAsync(chat, mp3, cancellationToken);
            logger.LogInformation("Ira: told {User} ({Kind}, {How}): {Text}", m.Owner.ConsoleUser, m.Kind, voiced ? "text and voice" : "text only", m.Text);
        }
    }

    private async Task<byte[]?> SpeakAsync(Outgoing m, CancellationToken cancellationToken)
    {
        var caller = new VoiceCaller(AiVoiceService.Alert, m.Owner.ConsoleUser, null, $"tg-{m.Owner.TelegramUserId.ToString(CultureInfo.InvariantCulture)}");
        VoiceSpeech speech;
        try
        {
            speech = await voice.SpeakAsync(m.Text, caller, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Ira: a {Kind} alert could not be spoken ({Type}); it went as text", m.Kind, ex.GetType().Name);
            return null;
        }

        switch (speech.Status)
        {
            case VoiceSpeechStatus.Spoken:
                return speech.Mp3;
            case VoiceSpeechStatus.Refused:
                if (!_refusalLogged) logger.LogInformation("Ira: Amazon Polly refused call {CallId} ({Why}); alerts go as text", speech.CallId, speech.Error);
                _refusalLogged = true;
                return null;
            case VoiceSpeechStatus.Failed:
                logger.LogInformation("Ira: call {CallId} could not speak a {Kind} alert ({Why}); it went as text", speech.CallId, m.Kind, speech.Error);
                return null;
            default:
                // Voice or spoken replies switched off, or Polly paused after a refusal: text only.
                return null;
        }
    }

    /// <summary><paramref name="text"/> with what was folded for this owner, which is then cleared.</summary>
    private static string Fold(string text, IraOwnerHour hour)
    {
        if (hour.Folded == 0) return text;
        string folded = IraAlertText.WithFolded(text, hour.Folded, hour.FoldedSinceUtc);
        hour.Folded = 0;
        hour.FoldedSinceUtc = null;
        return folded;
    }

    /// <summary>The owner's last hour, older sends dropped.</summary>
    private static IraOwnerHour HourOf(IraMarks marks, TelegramOwner owner, DateTime nowUtc)
    {
        if (!marks.Owners.TryGetValue(Id(owner), out var hour))
        {
            hour = new IraOwnerHour();
            marks.Owners[Id(owner)] = hour;
        }

        hour.Sent.RemoveAll(t => DateTime.SpecifyKind(t, DateTimeKind.Utc) <= nowUtc - Hour);
        return hour;
    }

    private static string Id(TelegramOwner owner) => owner.TelegramUserId.ToString(CultureInfo.InvariantCulture);

    private static TimeOnly At(string? text, TimeOnly fallback) =>
        TimeOnly.TryParseExact(text?.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : fallback;
}
