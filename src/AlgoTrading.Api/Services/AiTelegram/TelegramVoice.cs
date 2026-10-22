using System.Globalization;
using System.Text.Json.Nodes;
using AlgoTrading.Api.Services.AiVoice;
using AlgoTrading.Infrastructure.Ai;

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
/// The voice side of the Telegram assistant ("Ira"): a voice note written down, and an answer spoken back, through the
/// assistant's one voice (<see cref="AiVoiceService"/>), which the console's AI → Assistant uses too.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hearing.</b> A voice note is OGG/Opus, so it is decoded here, in-process, to 16 kHz mono WAV
/// (<see cref="OggOpusDecoder"/>); WAV goes as it is, and so does MP3 for Voxtral (Amazon Transcribe is sent PCM, which
/// the desk cannot make from MP3, so the assistant refuses MP3 before downloading it). The shared voice writes it down
/// and records the call (source <c>telegram</c>, conversation <c>tg-&lt;chat&gt;</c>); this class words what the owner
/// is told when it could not.
/// </para>
/// <para>
/// <b>Speaking.</b> When <see cref="AiVoiceSettings.SpeakReplies"/> is on, the shared voice asks Amazon Polly for the
/// answer's spoken version. A role without <c>polly:SynthesizeSpeech</c> is not a fault of the desk: the answer stays
/// text, the owner is told once per start what is missing, Polly is not asked again for an hour (from anywhere), and
/// the log line is Information, so Sentinel raises nothing.
/// </para>
/// </remarks>
public sealed class TelegramVoice(AiVoiceService voice, ILogger<TelegramVoice> logger)
{
    public const string SpeechNotice =
        "Spoken replies are off for now: the server's AWS role needs the polly:SynthesizeSpeech permission " +
        "(docs/modules/ai.md, Voice). Answers to voice notes come as text until it is added.";

    private readonly object _gate = new();
    private bool _speechNoticeSent;

    /// <summary>The assistant's one voice: its name, its back-off, its records.</summary>
    public AiVoiceService Voice => voice;

    /// <summary>
    /// Writes <paramref name="audio"/> down. The call is recorded whether or not the model answered; audio that cannot
    /// be read asks no model and records nothing.
    /// </summary>
    public async Task<VoiceHeard> HearAsync(byte[] audio, TelegramAudio note, TelegramOwner owner, long chatId, CancellationToken cancellationToken)
    {
        var s = voice.Settings;
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
        var heard = await voice.TranscribeAsync(
            new VoiceAudio(sent, format, seconds, note.Seconds, note.Kind, audio.Length), Caller(owner, chatId), cancellationToken);

        if (heard.Failure is not null)
        {
            // A provider's trouble, not the desk's: Information, as every AI call's failure is.
            logger.LogInformation("Telegram voice: call {CallId} could not write a voice note down: {Outcome}", heard.CallId, heard.Failure);
            return VoiceHeard.Failed($"I could not make out that {note.Kind}: {heard.Failure} (call #{heard.CallId}). Please try again or type the question.", heard.CallId);
        }

        return new VoiceHeard(heard.Text, heard.CallId, null);
    }

    /// <summary>
    /// The spoken version of <paramref name="answer"/>, when replies are spoken and Polly may be asked; with a notice,
    /// the first time, when the role may not call Polly.
    /// </summary>
    public async Task<VoiceSpoken> SpeakAsync(string answer, TelegramOwner owner, long chatId, CancellationToken cancellationToken)
    {
        var spoken = await voice.SpeakAsync(answer, Caller(owner, chatId), cancellationToken);
        switch (spoken.Status)
        {
            case VoiceSpeechStatus.Refused:
                bool tell;
                lock (_gate)
                {
                    tell = !_speechNoticeSent;
                    _speechNoticeSent = true;
                }

                if (tell) logger.LogInformation("Telegram voice: Amazon Polly refused call {CallId} ({Why}); answers stay text", spoken.CallId, spoken.Error);
                return tell ? new VoiceSpoken(null, SpeechNotice) : VoiceSpoken.None;
            case VoiceSpeechStatus.Failed:
                logger.LogInformation("Telegram voice: call {CallId} could not speak the answer: {Outcome}", spoken.CallId, spoken.Error);
                return new VoiceSpoken(null, null);
            case VoiceSpeechStatus.Spoken:
                logger.LogInformation("Telegram voice: spoke {Chars} characters, about ${Cost} at list price (call {CallId})", spoken.Characters, spoken.CostUsd, spoken.CallId);
                return new VoiceSpoken(spoken.Mp3, null);
            default:
                // Switched off, paused after a refusal, or nothing in the answer to say.
                return VoiceSpoken.None;
        }
    }

    private static VoiceCaller Caller(TelegramOwner owner, long chatId) =>
        new(AiVoiceService.Telegram, owner.ConsoleUser, null, $"tg-{chatId.ToString(CultureInfo.InvariantCulture)}");
}
