using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services.AiVoice;

/// <summary>Whose voice call it is, as its <c>ai_calls</c> row records it.</summary>
/// <param name="Source"><see cref="AiVoiceService.Telegram"/>, <see cref="AiVoiceService.Console"/>, or <see cref="AiVoiceService.Alert"/> for what Ira says first.</param>
/// <param name="RequestedBy">The console user's name.</param>
/// <param name="UserId">The console user's id; null, it is looked up by <paramref name="RequestedBy"/>.</param>
/// <param name="ConversationId">The conversation the call belongs to: <c>tg-&lt;chat&gt;</c> on Telegram, the page's on the console.</param>
public sealed record VoiceCaller(string Source, string RequestedBy, long? UserId, string ConversationId);

/// <summary>Audio to write down, as the audio model is sent it.</summary>
/// <param name="Sent">The bytes sent: WAV or MP3.</param>
/// <param name="Format"><c>wav</c> or <c>mp3</c>: the endpoint refused <c>ogg</c> (11 Oct 2026).</param>
/// <param name="Seconds">Its length: measured from the WAV, or as the sender declared it for MP3.</param>
/// <param name="DeclaredSeconds">Its length as the sender declared it; the token budget takes the longer of the two.</param>
/// <param name="Kind">What it is, in words: "voice note", "audio file", "recording".</param>
/// <param name="ReceivedBytes">Its size as it arrived, before any decoding.</param>
public sealed record VoiceAudio(byte[] Sent, string Format, double Seconds, double DeclaredSeconds, string Kind, int ReceivedBytes);

/// <summary>What was heard, and its call; with <see cref="Failure"/> (the provider's reason), nothing was heard.</summary>
public sealed record VoiceTranscript(string? Text, long CallId, string? Failure);

/// <summary>What became of a request to speak an answer.</summary>
public enum VoiceSpeechStatus
{
    /// <summary>Polly spoke it: <see cref="VoiceSpeech.Mp3"/> holds the audio.</summary>
    Spoken,

    /// <summary><c>Ai:Voice:Enabled</c> or <c>Ai:Voice:SpeakReplies</c> is off: Polly was not asked.</summary>
    Off,

    /// <summary>Polly refused the role's permission less than <see cref="AiVoiceService.SpeechRetryAfter"/> ago: not asked again yet.</summary>
    Paused,

    /// <summary>Nothing in the answer can be said (all table or code): Polly was not asked.</summary>
    NothingToSay,

    /// <summary>The role may not call Polly (403): the row is <c>refused</c>, and Polly is left alone for an hour.</summary>
    Refused,

    /// <summary>Any other failure: the row is <c>failed</c>.</summary>
    Failed,
}

/// <summary>A spoken answer, or why there is none.</summary>
/// <param name="Characters">The spoken version's length; 0 when nothing was sent to Polly.</param>
/// <param name="CostUsd">Those characters at Polly's list price.</param>
/// <param name="RetryAfterUtc">For <see cref="VoiceSpeechStatus.Refused"/> and <see cref="VoiceSpeechStatus.Paused"/>: when Polly is asked again.</param>
public sealed record VoiceSpeech(VoiceSpeechStatus Status, byte[]? Mp3, long? CallId, string Error, int Characters, decimal CostUsd, DateTime? RetryAfterUtc)
{
    public static VoiceSpeech Not(VoiceSpeechStatus status, DateTime? retryAfterUtc = null) => new(status, null, null, string.Empty, 0, 0m, retryAfterUtc);
}

/// <summary>
/// The assistant's voice ("Ira"), shared by Telegram (<c>TelegramVoice</c>) and the console (<c>api/Ai/voice/*</c>): a
/// question written down, and an answer spoken back. Each caller brings its own audio (Telegram downloads and decodes
/// its voice notes; the console records WAV in the browser) and words its own replies; what is asked, recorded and
/// remembered is here, once.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hearing.</b> The audio model (<see cref="AiVoiceSettings.TranscribeModel"/>, Voxtral Small) is asked once through
/// <see cref="BedrockOpenChatClient.TranscribeAsync"/>, with the instruction and a short list of the desk's own words
/// (<see cref="VoiceText.Instruction"/>): the strategies' names from the catalog, else from the morning plan, and the
/// assistant's name (<see cref="NameAsync"/>). The audio is never kept; the call is an <c>ai_calls</c> row (agent
/// <see cref="AiCatalog.VoiceTranscribe"/>) with the instruction, a line describing the audio, the transcript, the
/// seconds and the tokens.
/// </para>
/// <para>
/// <b>Speaking.</b> The answer's spoken version (<see cref="VoiceText.Spoken"/>) goes to Amazon Polly
/// (<see cref="PollySpeechClient"/>), one row each (agent <see cref="AiCatalog.VoiceSpeak"/>) with the characters and
/// their list price. A role without <c>polly:SynthesizeSpeech</c> is not a fault of the desk: the row is
/// <c>refused</c>, and Polly is not asked again for <see cref="SpeechRetryAfter"/>, from Telegram or the console.
/// </para>
/// <para>
/// <b>The name.</b> What the owner calls the assistant: <c>ai.voice.name</c> in <c>system_settings</c>
/// (<see cref="AiSettingsStore.VoiceNameKey"/>, set with /name on Telegram) over <c>Ai:Voice:Name</c>. Read once and
/// kept until it is set again, so the console's recordings and Telegram's voice notes hear the same name.
/// </para>
/// <para>
/// Nothing here logs above Debug: what a call came to is the caller's to log, in its own words.
/// </para>
/// </remarks>
public sealed class AiVoiceService(
    IServiceScopeFactory scopes,
    BedrockOpenChatClient transcriber,
    PollySpeechClient polly,
    IOptionsMonitor<AiSettings> settings,
    ILogger<AiVoiceService> logger,
    TimeProvider? time = null)
{
    public const string Telegram = "telegram";
    public const string Console = "console";

    /// <summary>The source of a spoken alert or briefing (<see cref="IraAlertService"/>): Ira spoke first, nobody asked.</summary>
    public const string Alert = "alert";

    public const string Tier = "voice";

    /// <summary>How long Polly is left alone after it refused for the missing permission.</summary>
    public static readonly TimeSpan SpeechRetryAfter = TimeSpan.FromHours(1);

    /// <summary>How long the vocabulary hint is kept before the strategies are read again.</summary>
    public static readonly TimeSpan VocabularyLifetime = TimeSpan.FromMinutes(10);

    /// <summary>How long the strategy catalog may take: on a cold start it runs Python for up to 20 s.</summary>
    private static readonly TimeSpan CatalogWait = TimeSpan.FromSeconds(3);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private DateTime _speechBlockedUntilUtc = DateTime.MinValue;
    private volatile Vocabulary? _vocabulary;
    private volatile string? _name;

    private sealed record Vocabulary(IReadOnlyList<string> Names, DateTime ReadUtc);

    public AiVoiceSettings Settings => settings.CurrentValue.Voice;

    /// <summary>
    /// Writes <paramref name="audio"/> down. The call is recorded whether or not the model answered; a request that is
    /// cancelled before the model answers records nothing.
    /// </summary>
    public async Task<VoiceTranscript> TranscribeAsync(VoiceAudio audio, VoiceCaller caller, CancellationToken cancellationToken)
    {
        var s = Settings;
        string instruction = VoiceText.Instruction(await VocabularyAsync(cancellationToken), await NameAsync(cancellationToken));
        string model = AiCatalog.BedrockOpenPrefix + s.TranscribeModel;
        int maxTokens = MaxTokens(Math.Max(audio.Seconds, audio.DeclaredSeconds));
        var started = _time.GetUtcNow().UtcDateTime;
        var clock = Stopwatch.StartNew();

        AiTranscription? heard = null;
        AiAttemptFailedException? failure = null;
        try
        {
            heard = await transcriber.TranscribeAsync(s.TranscribeModel, audio.Sent, audio.Format, instruction, maxTokens, cancellationToken);
        }
        catch (AiAttemptFailedException ex)
        {
            failure = ex;
        }

        double took = Math.Round(clock.Elapsed.TotalSeconds, 2);
        string described = $"[{audio.Kind}, {audio.Seconds:0.#} s, {audio.ReceivedBytes / 1024.0:0.#} KB, sent as {audio.Format}; the audio is not kept]";
        var row = new AiCall
        {
            CreatedUtc = started,
            CompletedUtc = started + clock.Elapsed,
            AgentKey = AiCatalog.VoiceTranscribe,
            Tier = Tier,
            Source = caller.Source,
            RequestedBy = caller.RequestedBy,
            ConversationId = caller.ConversationId,
            ChainJson = JsonSerializer.Serialize(new[] { model }),
            Model = heard is null ? string.Empty : model,
            Outcome = heard is null ? AiCallOutcome.Failed : AiCallOutcome.Ok,
            Error = failure?.Outcome ?? string.Empty,
            Seconds = took,
            PromptTokens = heard?.Usage?.PromptTokens,
            CompletionTokens = heard?.Usage?.CompletionTokens,
            TotalTokens = heard?.Usage?.TotalTokens,
            Rounds = 1,
            MessagesJson = Messages(instruction + "\n" + described),
            Summary = Cut(heard?.Text ?? $"{Capital(audio.Kind)} of {audio.Seconds:0.#} s: not written down", 200),
            Answer = heard?.Text ?? string.Empty,
            FinishReason = Cut(heard?.FinishReason ?? string.Empty, 32),
            MaxTokens = maxTokens,
            Temperature = 0,
            AttemptsJson = Attempts(model, failure?.Outcome ?? "ok", took, failure?.HttpStatus),
        };
        long callId = await RecordAsync(row, caller, cancellationToken);
        return failure is null ? new VoiceTranscript(heard!.Text, callId, null) : new VoiceTranscript(null, callId, failure.Outcome);
    }

    /// <summary>
    /// <paramref name="answer"/>'s spoken version as MP3, when Polly may be asked; else why not. Every request Polly
    /// was sent is a row; one that was not (switched off, paused, nothing to say) is none.
    /// </summary>
    public async Task<VoiceSpeech> SpeakAsync(string answer, VoiceCaller caller, CancellationToken cancellationToken)
    {
        var s = Settings;
        if (!s.Enabled || !s.SpeakReplies) return VoiceSpeech.Not(VoiceSpeechStatus.Off);
        if (SpeechPausedUntilUtc is DateTime until) return VoiceSpeech.Not(VoiceSpeechStatus.Paused, until);

        string text = VoiceText.Spoken(answer, s.MaxSpokenChars);
        if (text.Length == 0) return VoiceSpeech.Not(VoiceSpeechStatus.NothingToSay);

        string model = PollySpeechClient.ModelOf(s);
        var started = _time.GetUtcNow().UtcDateTime;
        var clock = Stopwatch.StartNew();
        byte[]? mp3 = null;
        string outcome = AiCallOutcome.Ok;
        string error = string.Empty;
        int? status = null;
        try
        {
            mp3 = await polly.SynthesizeAsync(text, cancellationToken);
        }
        catch (PollyNotPermittedException ex)
        {
            (outcome, error, status) = (AiCallOutcome.Refused, ex.Message, 403);
        }
        catch (AiAttemptFailedException ex)
        {
            (outcome, error, status) = (AiCallOutcome.Failed, ex.Outcome, ex.HttpStatus);
        }

        double took = Math.Round(clock.Elapsed.TotalSeconds, 2);
        decimal cost = PollySpeechClient.Cost(text.Length, s);
        string said = mp3 is null
            ? $"Not spoken ({text.Length} characters): {error}"
            : $"Spoke {text.Length} characters ({s.PollyVoice}, {s.PollyEngine}): ${cost.ToString("0.######", CultureInfo.InvariantCulture)} " +
              $"at list price (${s.PollyUsdPerMillionChars.ToString("0.##", CultureInfo.InvariantCulture)} per 1M characters)";
        var row = new AiCall
        {
            CreatedUtc = started,
            CompletedUtc = started + clock.Elapsed,
            AgentKey = AiCatalog.VoiceSpeak,
            Tier = Tier,
            Source = caller.Source,
            RequestedBy = caller.RequestedBy,
            ConversationId = caller.ConversationId,
            ChainJson = JsonSerializer.Serialize(new[] { model }),
            Model = mp3 is null ? string.Empty : model,
            Outcome = outcome,
            Error = error,
            Seconds = took,
            Rounds = 1,
            MessagesJson = Messages(text),
            Summary = Cut(said, 200),
            Answer = mp3 is null ? said : $"{said}; {mp3.Length / 1024.0:0.#} KB of MP3.",
            AttemptsJson = Attempts(model, outcome == AiCallOutcome.Ok ? "ok" : error, took, status),
        };
        long callId = await RecordAsync(row, caller, cancellationToken);

        if (outcome == AiCallOutcome.Refused)
        {
            DateTime resume;
            lock (_gate)
            {
                _speechBlockedUntilUtc = resume = _time.GetUtcNow().UtcDateTime + SpeechRetryAfter;
            }

            return new VoiceSpeech(VoiceSpeechStatus.Refused, null, callId, error, text.Length, cost, resume);
        }

        return mp3 is null
            ? new VoiceSpeech(VoiceSpeechStatus.Failed, null, callId, error, text.Length, cost, null)
            : new VoiceSpeech(VoiceSpeechStatus.Spoken, mp3, callId, string.Empty, text.Length, cost, null);
    }

    /// <summary>When Polly is asked again after refusing the role's permission; null when it may be asked now.</summary>
    public DateTime? SpeechPausedUntilUtc
    {
        get
        {
            lock (_gate)
            {
                return _time.GetUtcNow().UtcDateTime < _speechBlockedUntilUtc ? _speechBlockedUntilUtc : null;
            }
        }
    }

    /// <summary>
    /// What the owner calls the assistant: the name he gave it with /name (<see cref="AiSettingsStore.VoiceNameKey"/>),
    /// else <c>Ai:Voice:Name</c>. Read once, then kept until <see cref="SetNameAsync"/> changes it.
    /// </summary>
    public async Task<string> NameAsync(CancellationToken cancellationToken)
    {
        if (_name is { } known) return known;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        string? saved = await db.SystemSettings.AsNoTracking().Where(s => s.Key == AiSettingsStore.VoiceNameKey).Select(s => s.Value)
            .FirstOrDefaultAsync(cancellationToken);
        return _name = VoiceText.IsName(saved) ? saved!.Trim() : Settings.Name;
    }

    /// <summary>Gives the assistant <paramref name="name"/> (already checked with <see cref="VoiceText.IsName"/>), from the next question on.</summary>
    public async Task SetNameAsync(string name, string by, string reason, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var now = _time.GetUtcNow().UtcDateTime;
        var row = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == AiSettingsStore.VoiceNameKey, cancellationToken);
        if (row is null)
        {
            row = new SystemSetting { Key = AiSettingsStore.VoiceNameKey, CreatedUtc = now };
            db.SystemSettings.Add(row);
        }

        row.Value = name;
        row.UpdatedBy = by;
        row.Reason = reason;
        row.UpdatedUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        _name = name;
    }

    /// <summary>Tokens a transcript may use: about six a second of speech, never fewer than 200.</summary>
    public static int MaxTokens(double seconds) => Math.Clamp((int)Math.Ceiling(seconds) * 6, 200, 1000);

    /// <summary>
    /// The strategies' names as they are said, for the instruction: the catalog's while it answers within a few
    /// seconds, else the morning plan's. Kept for <see cref="VocabularyLifetime"/>.
    /// </summary>
    private async Task<IReadOnlyList<string>> VocabularyAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (_vocabulary is { } kept && now - kept.ReadUtc < VocabularyLifetime) return kept.Names;

        var names = new List<string>();
        await using (var scope = scopes.CreateAsyncScope())
        {
            try
            {
                if (scope.ServiceProvider.GetService<StrategyCatalogService>() is { } catalog)
                {
                    names.AddRange((await catalog.GetAllAsync(cancellationToken).WaitAsync(CatalogWait, cancellationToken)).Select(e => e.Name));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug("Voice: the strategy catalog was not read for the vocabulary ({Type})", ex.GetType().Name);
            }

            if (names.Count == 0 && scope.ServiceProvider.GetService<DeskPlanBuilder>() is { } plan && plan.Locate().File is { } file)
            {
                try
                {
                    names.AddRange(MorningPlan.Parse(await File.ReadAllTextAsync(file, cancellationToken)).Lines.Select(l => l.Strategy));
                }
                catch (IOException ex)
                {
                    logger.LogDebug("Voice: the morning plan was not read for the vocabulary ({Type})", ex.GetType().Name);
                }
            }
        }

        var spoken = VoiceText.SpokenNames(names);
        _vocabulary = new Vocabulary(spoken, now);
        return spoken;
    }

    private async Task<long> RecordAsync(AiCall row, VoiceCaller caller, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        row.UserId = caller.UserId
            ?? await db.AppUsers.AsNoTracking().Where(u => u.UserName == caller.RequestedBy).Select(u => (long?)u.Id).FirstOrDefaultAsync(cancellationToken);
        db.AiCalls.Add(row);
        // Saved even if the caller is stopping: the call was made and billed.
        await db.SaveChangesAsync(CancellationToken.None);
        return row.Id;
    }

    private static string Messages(string content) => new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content }).ToJsonString();

    private static string Attempts(string model, string outcome, double seconds, int? httpStatus) =>
        JsonSerializer.Serialize(new[] { new { model, outcome, seconds, httpStatus, round = 1 } });

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
