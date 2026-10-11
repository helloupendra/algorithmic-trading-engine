using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services.AiTelegram;

/// <summary>A voice note or an audio file in a Telegram message, as its metadata describes it.</summary>
/// <param name="Format">What the audio model is sent once it is read: <c>ogg</c> (decoded to WAV first), <c>mp3</c> or <c>wav</c>; null for audio it cannot take.</param>
public sealed record TelegramAudio(string FileId, int Seconds, long? Bytes, string? Format, string Kind)
{
    /// <summary>The message's voice note, else its audio file; null when it has neither.</summary>
    public static TelegramAudio? Of(JsonObject message)
    {
        if (message["voice"] is JsonObject voice) return From(voice, "ogg", "voice note");
        if (message["audio"] is not JsonObject audio) return null;
        string mime = (audio["mime_type"]?.GetValue<string>() ?? string.Empty).ToLowerInvariant();
        string extension = Path.GetExtension(audio["file_name"]?.GetValue<string>() ?? string.Empty).ToLowerInvariant();
        string? format = mime switch
        {
            "audio/ogg" or "audio/opus" or "audio/x-opus+ogg" => "ogg",
            "audio/mpeg" or "audio/mp3" or "audio/mpeg3" => "mp3",
            "audio/wav" or "audio/x-wav" or "audio/wave" or "audio/vnd.wave" => "wav",
            _ => extension switch
            {
                ".ogg" or ".oga" or ".opus" => "ogg",
                ".mp3" => "mp3",
                ".wav" => "wav",
                _ => null,
            },
        };
        return From(audio, format, "audio file");
    }

    private static TelegramAudio? From(JsonObject file, string? format, string kind) =>
        file["file_id"]?.GetValue<string>() is { Length: > 0 } id
            ? new TelegramAudio(id, (int)(Number(file["duration"]) ?? 0), Number(file["file_size"]), format, kind)
            : null;

    private static long? Number(JsonNode? node) =>
        node is not JsonValue value ? null
        : value.TryGetValue(out long l) ? l
        : value.TryGetValue(out int i) ? i
        : value.TryGetValue(out double d) ? (long)d
        : null;
}

/// <summary>What a voice note said, and its call; or why it could not be heard.</summary>
public sealed record VoiceHeard(string? Text, long? CallId, string? Error)
{
    public static VoiceHeard Failed(string why, long? callId = null) => new(null, callId, why);
}

/// <summary>A spoken reply's MP3, and a notice for the owner, either or both absent.</summary>
public sealed record VoiceSpoken(byte[]? Mp3, string? Notice)
{
    public static readonly VoiceSpoken None = new(null, null);
}

/// <summary>
/// The voice side of the Telegram assistant ("Ira"): a voice note written down, and an answer spoken back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hearing.</b> A voice note is OGG/Opus, which Bedrock's audio model refuses, so it is decoded in-process to 16 kHz
/// mono WAV (<see cref="OggOpusDecoder"/>); MP3 and WAV go as they are. The model (<see cref="AiVoiceSettings.TranscribeModel"/>,
/// Voxtral Small) is asked once through <see cref="BedrockOpenChatClient.TranscribeAsync"/>, with the instruction and a
/// short list of the desk's own words (<see cref="VoiceText.Instruction"/>): the strategies' names from the catalog,
/// else from the morning plan. The audio is never kept; the call is an <c>ai_calls</c> row (agent
/// <see cref="AiCatalog.VoiceTranscribe"/>) with the instruction, the transcript, the seconds and the tokens.
/// </para>
/// <para>
/// <b>Speaking.</b> The answer's spoken version (<see cref="VoiceText.Spoken"/>) goes to Amazon Polly
/// (<see cref="PollySpeechClient"/>), one row each (agent <see cref="AiCatalog.VoiceSpeak"/>) with the characters and
/// their list price. A role without <c>polly:SynthesizeSpeech</c> is not a fault of the desk: the answer stays text, the
/// owner is told once per start what is missing, Polly is not asked again for an hour, and the log line is
/// Information, so Sentinel raises nothing.
/// </para>
/// </remarks>
public sealed class TelegramVoice(
    IServiceScopeFactory scopes,
    BedrockOpenChatClient transcriber,
    PollySpeechClient polly,
    IOptionsMonitor<AiSettings> settings,
    ILogger<TelegramVoice> logger,
    TimeProvider? time = null)
{
    /// <summary>How long Polly is left alone after it refused for the missing permission.</summary>
    public static readonly TimeSpan SpeechRetryAfter = TimeSpan.FromHours(1);

    /// <summary>How long the vocabulary hint is kept before the strategies are read again.</summary>
    public static readonly TimeSpan VocabularyLifetime = TimeSpan.FromMinutes(10);

    /// <summary>How long the strategy catalog may take: on a cold start it runs Python for up to 20 s.</summary>
    private static readonly TimeSpan CatalogWait = TimeSpan.FromSeconds(3);

    public const string Tier = "voice";

    public const string SpeechNotice =
        "Spoken replies are off for now: the server's AWS role needs the polly:SynthesizeSpeech permission " +
        "(docs/modules/ai.md, Voice). Answers to voice notes come as text until it is added.";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private DateTime _speechBlockedUntilUtc = DateTime.MinValue;
    private bool _speechNoticeSent;
    private (IReadOnlyList<string> Names, DateTime ReadUtc)? _vocabulary;

    public AiVoiceSettings Settings => settings.CurrentValue.Voice;

    /// <summary>
    /// Writes <paramref name="audio"/> down. The call is recorded whether or not the model answered; audio that cannot
    /// be read asks no model and records nothing.
    /// </summary>
    public async Task<VoiceHeard> HearAsync(byte[] audio, TelegramAudio note, TelegramOwner owner, long chatId, string name, CancellationToken cancellationToken)
    {
        var s = Settings;
        byte[] sent;
        try
        {
            sent = note.Format == "ogg" ? OggOpusDecoder.ToWav(audio, s.MaxSeconds) : audio;
        }
        catch (InvalidDataException ex)
        {
            logger.LogInformation("Telegram voice: a {Kind} could not be read ({Why})", note.Kind, ex.Message);
            return VoiceHeard.Failed($"I could not read that {note.Kind} ({ex.Message}). Please send it again or type the question.");
        }

        string format = note.Format == "ogg" ? "wav" : note.Format!;
        double seconds = note.Format == "ogg" ? OggOpusDecoder.Seconds(sent) : note.Seconds;
        string instruction = VoiceText.Instruction(await VocabularyAsync(cancellationToken), name);
        string model = AiCatalog.BedrockOpenPrefix + s.TranscribeModel;
        int maxTokens = MaxTokens(Math.Max(seconds, note.Seconds));
        var started = _time.GetUtcNow().UtcDateTime;
        var clock = Stopwatch.StartNew();

        AiTranscription? heard = null;
        AiAttemptFailedException? failure = null;
        try
        {
            heard = await transcriber.TranscribeAsync(s.TranscribeModel, sent, format, instruction, maxTokens, cancellationToken);
        }
        catch (AiAttemptFailedException ex)
        {
            failure = ex;
        }

        double took = Math.Round(clock.Elapsed.TotalSeconds, 2);
        string described = $"[{note.Kind}, {seconds:0.#} s, {audio.Length / 1024.0:0.#} KB, sent as {format}; the audio is not kept]";
        var row = new AiCall
        {
            CreatedUtc = started,
            CompletedUtc = started + clock.Elapsed,
            AgentKey = AiCatalog.VoiceTranscribe,
            Tier = Tier,
            Source = "telegram",
            RequestedBy = owner.ConsoleUser,
            ConversationId = $"tg-{chatId.ToString(CultureInfo.InvariantCulture)}",
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
            Summary = Cut(heard?.Text ?? $"{Capital(note.Kind)} of {seconds:0.#} s: not written down", 200),
            Answer = heard?.Text ?? string.Empty,
            FinishReason = Cut(heard?.FinishReason ?? string.Empty, 32),
            MaxTokens = maxTokens,
            Temperature = 0,
            AttemptsJson = Attempts(model, failure?.Outcome ?? "ok", took, failure?.HttpStatus),
        };
        long callId = await RecordAsync(row, owner, cancellationToken);

        if (failure is not null)
        {
            // A provider's trouble, not the desk's: Information, as every AI call's failure is.
            logger.LogInformation("Telegram voice: call {CallId} could not write a voice note down: {Outcome}", callId, failure.Outcome);
            return VoiceHeard.Failed($"I could not make out that {note.Kind}: {failure.Outcome} (call #{callId}). Please try again or type the question.", callId);
        }

        return new VoiceHeard(heard!.Text, callId, null);
    }

    /// <summary>
    /// The spoken version of <paramref name="answer"/>, when replies are spoken and Polly may be asked; with a notice,
    /// the first time, when the role may not call Polly.
    /// </summary>
    public async Task<VoiceSpoken> SpeakAsync(string answer, TelegramOwner owner, long chatId, CancellationToken cancellationToken)
    {
        var s = Settings;
        if (!s.SpeakReplies) return VoiceSpoken.None;
        lock (_gate)
        {
            if (_time.GetUtcNow().UtcDateTime < _speechBlockedUntilUtc) return VoiceSpoken.None;
        }

        string text = VoiceText.Spoken(answer, s.MaxSpokenChars);
        if (text.Length == 0) return VoiceSpoken.None;

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
            Source = "telegram",
            RequestedBy = owner.ConsoleUser,
            ConversationId = $"tg-{chatId.ToString(CultureInfo.InvariantCulture)}",
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
        long callId = await RecordAsync(row, owner, cancellationToken);

        if (outcome == AiCallOutcome.Refused)
        {
            bool tell;
            lock (_gate)
            {
                _speechBlockedUntilUtc = _time.GetUtcNow().UtcDateTime + SpeechRetryAfter;
                tell = !_speechNoticeSent;
                _speechNoticeSent = true;
            }

            if (tell) logger.LogInformation("Telegram voice: Amazon Polly refused call {CallId} ({Why}); answers stay text", callId, error);
            return tell ? new VoiceSpoken(null, SpeechNotice) : VoiceSpoken.None;
        }

        if (mp3 is null) logger.LogInformation("Telegram voice: call {CallId} could not speak the answer: {Outcome}", callId, error);
        else logger.LogInformation("Telegram voice: spoke {Chars} characters, about ${Cost} at list price (call {CallId})", text.Length, cost, callId);
        return new VoiceSpoken(mp3, null);
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
                logger.LogDebug("Telegram voice: the strategy catalog was not read for the vocabulary ({Type})", ex.GetType().Name);
            }

            if (names.Count == 0 && scope.ServiceProvider.GetService<DeskPlanBuilder>() is { } plan && plan.Locate().File is { } file)
            {
                try
                {
                    names.AddRange(MorningPlan.Parse(await File.ReadAllTextAsync(file, cancellationToken)).Lines.Select(l => l.Strategy));
                }
                catch (IOException ex)
                {
                    logger.LogDebug("Telegram voice: the morning plan was not read for the vocabulary ({Type})", ex.GetType().Name);
                }
            }
        }

        var spoken = VoiceText.SpokenNames(names);
        _vocabulary = (spoken, now);
        return spoken;
    }

    private async Task<long> RecordAsync(AiCall row, TelegramOwner owner, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        row.UserId = await db.AppUsers.AsNoTracking().Where(u => u.UserName == owner.ConsoleUser).Select(u => (long?)u.Id).FirstOrDefaultAsync(cancellationToken);
        db.AiCalls.Add(row);
        // Saved even if the poll is stopping: the call was made and billed.
        await db.SaveChangesAsync(CancellationToken.None);
        return row.Id;
    }

    private static string Messages(string content) => new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content }).ToJsonString();

    private static string Attempts(string model, string outcome, double seconds, int? httpStatus) =>
        JsonSerializer.Serialize(new[] { new { model, outcome, seconds, httpStatus, round = 1 } });

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
