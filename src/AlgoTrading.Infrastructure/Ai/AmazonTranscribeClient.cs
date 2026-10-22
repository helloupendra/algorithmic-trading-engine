using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Ai;

/// <summary>What Amazon Transcribe wrote down: the final results joined in order, and the languages it heard them in.</summary>
/// <param name="Languages">The language of each stretch, in order, a repeat folded into one: <c>hi-IN</c>, <c>en-IN</c>.</param>
/// <param name="AudioSeconds">The seconds of sound streamed, which AWS bills.</param>
public sealed record AmazonTranscript(string Text, IReadOnlyList<string> Languages, double AudioSeconds);

/// <summary>
/// Speech written down by Amazon Transcribe streaming, over its WebSocket API: the desk's speech-to-text from 22 Oct
/// 2026, after Bedrock's Mantle endpoint stopped taking Voxtral's audio.
/// </summary>
/// <remarks>
/// <para>
/// One recording is one stream. A presigned <c>wss://transcribestreaming.{region}.amazonaws.com:8443/stream-transcription-websocket</c>
/// URL (SigV4 in the query, service <c>transcribe</c>, valid 5 minutes, the instance role's session token signed in),
/// then the sound as 16 kHz mono 16-bit PCM in AWS event-stream <c>AudioEvent</c> frames of 100 ms (3,200 bytes), sent
/// at up to <see cref="PaceFactor"/> times real time, then an empty frame to end it. Transcribe answers with
/// <c>TranscriptEvent</c> frames; only final results are kept, joined in order. The parameters come from AWS's
/// developer guide ("Setting up a WebSocket stream", "Language identification with streaming transcriptions") and the
/// <c>StartStreamTranscription</c> API reference, and mirror the IRA project's Python client.
/// </para>
/// <para>
/// With two or more languages (<see cref="AiVoiceSettings.TranscribeLanguages"/>, Hindi and English) Transcribe tells
/// them apart itself, and with <see cref="AiVoiceSettings.TranscribeIdentifyMultiple"/> switches between them inside a
/// recording, as Hinglish does. Multi-language identification takes PCM only, which is why the sound is always sent as
/// PCM, never as the voice note's own Ogg/Opus.
/// </para>
/// <para>
/// The role needs <c>transcribe:StartStreamTranscriptionWebSocket</c> in the region asked. Every failure is an
/// <see cref="AiAttemptFailedException"/> worded as the other providers' are; nothing is logged but sizes, timings and
/// outcomes: never the sound, the words heard or the signed URL.
/// </para>
/// </remarks>
public sealed partial class AmazonTranscribeClient(
    IOptionsMonitor<AiSettings> settings,
    IAwsUrlPresigner presigner,
    ITranscribeTransport transport,
    ILogger<AmazonTranscribeClient> logger)
{
    /// <summary>The service a signature is scoped to.</summary>
    public const string Service = "transcribe";

    /// <summary>The permission the server's role needs for streaming over a WebSocket.</summary>
    public const string Permission = "transcribe:StartStreamTranscriptionWebSocket";

    /// <summary>One audio frame: 100 ms of 16 kHz 16-bit mono.</summary>
    public const int ChunkBytes = 3_200;

    /// <summary>How long a presigned URL is good for: AWS's most for Transcribe (<c>X-Amz-Expires</c> ≤ 300).</summary>
    public static readonly TimeSpan UrlLifetime = TimeSpan.FromMinutes(5);

    /// <summary>The time a stream may take beyond the sound's own length.</summary>
    public TimeSpan BaseTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many times faster than real time the sound is sent: a two-minute note goes in 15 s. A recording is sent as a
    /// live stream would be, only faster; 0 sends it as fast as the socket takes it (tests).
    /// </summary>
    public double PaceFactor { get; init; } = 8;

    /// <summary>
    /// After the last frame is sent, how long a silence from Transcribe ends the stream with what it has given: it
    /// normally sends its final results and closes within a second or two.
    /// </summary>
    public TimeSpan QuietWindow { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// <paramref name="pcm"/> (16 kHz mono signed 16-bit little-endian, <see cref="PcmAudio"/>) written down. Throws
    /// <see cref="AiAttemptFailedException"/> on any failure, including no words heard.
    /// </summary>
    public async Task<AmazonTranscript> TranscribeAsync(byte[] pcm, CancellationToken cancellationToken)
    {
        var voice = settings.CurrentValue.Voice;
        double seconds = PcmAudio.Seconds(pcm);
        var clock = Stopwatch.StartNew();
        string outcome = "ok";
        try
        {
            if (pcm.Length < 2 || pcm.Length % 2 != 0) throw new AiAttemptFailedException("no sound to send: the audio is not 16-bit PCM");
            if (seconds > voice.MaxSeconds + 1)
            {
                throw new AiAttemptFailedException($"too long: {seconds:0} s of sound, past the {voice.MaxSeconds} s the desk sends (Ai:Voice:MaxSeconds)");
            }

            var heard = await StreamAsync(pcm, voice, BaseTimeout + TimeSpan.FromSeconds(seconds), cancellationToken);
            if (heard.Text.Length == 0) throw new AiAttemptFailedException("empty answer: Amazon Transcribe heard no words");
            return new AmazonTranscript(heard.Text, heard.Languages, seconds);
        }
        catch (AiAttemptFailedException ex)
        {
            outcome = ex.Outcome;
            throw;
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        catch (Exception ex)
        {
            outcome = $"failed ({ex.GetType().Name})";
            throw;
        }
        finally
        {
            // Sizes, timings and the outcome only: the outcome is the desk's or AWS's words, never the sound or what it said.
            logger.LogInformation("Amazon Transcribe: {Seconds:0.0} s of sound ({Bytes} bytes) in {Took:0.00} s: {Outcome}",
                seconds, pcm.Length, clock.Elapsed.TotalSeconds, outcome);
        }
    }

    private async Task<TranscribeResults> StreamAsync(byte[] pcm, AiVoiceSettings voice, TimeSpan limit, CancellationToken cancellationToken)
    {
        string region = voice.TranscribeRegion.Trim();
        var unsigned = Url(voice);
        using var timer = new CancellationTokenSource(limit);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);
        try
        {
            var signed = await presigner.PresignAsync(unsigned, region, Service, UrlLifetime, linked.Token);
            await using var connection = await transport.ConnectAsync(AsWebSocket(signed), linked.Token);
            return await ExchangeAsync(connection, pcm, linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiAttemptFailedException($"timeout: no answer within {limit.TotalSeconds:0} s");
        }
        catch (TranscribeHandshakeException ex)
        {
            throw Handshake(ex.Status, ex.Reason, region);
        }
        catch (WebSocketException ex)
        {
            throw new AiAttemptFailedException($"unreachable (the stream to Amazon Transcribe broke: {ex.WebSocketErrorCode})");
        }
        catch (HttpRequestException ex)
        {
            throw new AiAttemptFailedException($"unreachable ({ex.HttpRequestError})");
        }
        catch (InvalidDataException ex)
        {
            throw new AiAttemptFailedException($"provider error: an unreadable reply from Amazon Transcribe ({ex.Message})");
        }
    }

    /// <summary>
    /// Sends the sound and reads the answers at the same time. Ends when Transcribe closes the stream, or stays quiet for
    /// <see cref="QuietWindow"/> once all the sound is sent. An exception frame from Transcribe is the error reported,
    /// even when it made a send fail first.
    /// </summary>
    private async Task<TranscribeResults> ExchangeAsync(ITranscribeConnection connection, byte[] pcm, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task sending = SendAsync(connection, pcm, stop.Token);
        Task<byte[]?>? receiving = null;
        var heard = new TranscribeResults();
        try
        {
            while (true)
            {
                receiving ??= connection.ReceiveAsync(stop.Token);
                if (!sending.IsCompleted)
                {
                    if (await Task.WhenAny(receiving, sending) == sending) continue;
                }
                else
                {
                    var quiet = Task.Delay(QuietWindow, stop.Token);
                    if (await Task.WhenAny(receiving, quiet) == quiet)
                    {
                        // A timeout or the caller stopping ends the wait too: that is not a quiet spell, and throws here.
                        await quiet;
                        // All the sound is sent and nothing more came: what was heard is the answer (or the send's failure).
                        await sending;
                        return heard;
                    }
                }

                byte[]? message = await receiving;
                receiving = null;
                if (message is null)
                {
                    // Closed by Transcribe, normally just after the last frame: the send is given a moment to settle.
                    await Task.WhenAny(sending, Task.Delay(SettleTime, stop.Token));
                    if (sending.IsCompletedSuccessfully) return heard;
                    throw new AiAttemptFailedException("provider error: Amazon Transcribe closed the stream before all the sound was sent");
                }

                Read(message, heard);
            }
        }
        finally
        {
            await stop.CancelAsync();
            await Quietly(sending);
            if (receiving is not null) await Quietly(receiving);
        }
    }

    /// <summary>How long a send may take to finish once Transcribe has closed the stream.</summary>
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(1);

    private async Task SendAsync(ITranscribeConnection connection, byte[] pcm, CancellationToken cancellationToken)
    {
        var pause = PaceFactor > 0 ? TimeSpan.FromSeconds(ChunkBytes / (double)PcmAudio.BytesPerSecond / PaceFactor) : TimeSpan.Zero;
        for (int at = 0; at < pcm.Length; at += ChunkBytes)
        {
            await connection.SendAsync(AwsEventStream.AudioEvent(pcm.AsSpan(at, Math.Min(ChunkBytes, pcm.Length - at))), cancellationToken);
            if (pause > TimeSpan.Zero) await Task.Delay(pause, cancellationToken);
        }

        // An empty AudioEvent ends the stream.
        await connection.SendAsync(AwsEventStream.AudioEvent([]), cancellationToken);
    }

    /// <summary>One message from Transcribe: a transcript event kept, an exception thrown in words, anything else skipped.</summary>
    private static void Read(byte[] message, TranscribeResults heard)
    {
        var frame = AwsEventStream.Decode(message);
        switch (frame.Header(":message-type"))
        {
            case "exception":
                // The WebSocket guide names it :exception-type; the HTTP/2 guide :event-type.
                string kind = frame.Header(":exception-type") ?? frame.Header(":event-type") ?? "an exception";
                throw Refusal(kind, MessageOf(frame.Payload));
            case "error":
                throw Refusal(frame.Header(":error-code") ?? "an error", frame.Header(":error-message") ?? MessageOf(frame.Payload));
            case "event" when frame.Header(":event-type") == "TranscriptEvent":
                heard.Add(frame.Payload);
                break;
        }
    }

    /// <summary>
    /// An exception Transcribe sent inside the stream, in the attempts table's words. A limit or a busy service is
    /// capacity (429/503), as for the other providers.
    /// </summary>
    public static AiAttemptFailedException Refusal(string kind, string message)
    {
        string tail = BedrockRefusals.Tail(Flat(message));
        return kind switch
        {
            "BadRequestException" => new($"http 400: Amazon Transcribe refused the stream{tail}", 400),
            "LimitExceededException" => new($"http 429: Amazon Transcribe's limit was reached (too many streams at once, or one too long){tail}", 429),
            "ServiceUnavailableException" => new($"http 503: Amazon Transcribe is unavailable, temporarily{tail}", 503),
            "InternalFailureException" => new($"http 500: Amazon Transcribe failed while processing the sound{tail}", 500),
            "ConflictException" => new($"http 409: another stream took over this session{tail}", 409),
            "UnrecognizedClientException" => new($"http 403: Amazon Transcribe refused the server's AWS credentials{tail}", 403),
            "AccessDeniedException" => new($"http 403: the server's AWS role is not allowed to call Amazon Transcribe (it needs {Permission}){tail}", 403),
            _ => new($"provider error: Amazon Transcribe sent {Flat(kind)}{tail}"),
        };
    }

    /// <summary>The WebSocket upgrade refused, by its HTTP status, in the attempts table's words.</summary>
    public static AiAttemptFailedException Handshake(int? status, string reason, string region) => status switch
    {
        null => new($"unreachable ({Flat(reason)})"),
        401 or 403 => new(
            $"http {status}: Amazon Transcribe refused the stream: the server's AWS role needs {Permission} in {region}, or its signature was refused",
            status),
        429 => new("http 429: throttled by Amazon Transcribe", 429),
        >= 500 => new($"http {status}: Amazon Transcribe is unavailable, temporarily", status),
        _ => new($"http {status}: Amazon Transcribe refused the stream's parameters", status),
    };

    /// <summary>
    /// The unsigned URL for <paramref name="voice"/>'s settings, as an <c>https</c> GET (it becomes <c>wss</c> once signed).
    /// Throws <see cref="AiAttemptFailedException"/>, naming the setting, when the languages or the region make no sense.
    /// </summary>
    public static Uri Url(AiVoiceSettings voice)
    {
        string region = voice.TranscribeRegion.Trim();
        if (!RegionRx().IsMatch(region)) throw new AiAttemptFailedException($"Ai:Voice:TranscribeRegion \"{region}\" is not an AWS region");
        string query = string.Join('&', Query(voice).Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value)}"));
        return new Uri($"https://{Host(region)}{Path}?{query}");
    }

    /// <summary>
    /// The stream's own query parameters, before the signature's: PCM at 16 kHz, then the language, or the languages
    /// Transcribe identifies among (<c>identify-multiple-languages</c> or <c>identify-language</c> with
    /// <c>language-options</c>, and <c>preferred-language</c> with the second only).
    /// </summary>
    public static IReadOnlyList<(string Name, string Value)> Query(AiVoiceSettings voice)
    {
        var languages = Languages(voice);
        var query = new List<(string, string)> { ("media-encoding", "pcm"), ("sample-rate", PcmAudio.SampleRate.ToString(CultureInfo.InvariantCulture)) };
        if (languages.Count == 1)
        {
            query.Add(("language-code", languages[0]));
            return query;
        }

        query.Add((voice.TranscribeIdentifyMultiple ? "identify-multiple-languages" : "identify-language", "true"));
        query.Add(("language-options", string.Join(',', languages)));
        string preferred = voice.TranscribePreferredLanguage.Trim();
        if (!voice.TranscribeIdentifyMultiple && languages.Contains(preferred, StringComparer.Ordinal)) query.Add(("preferred-language", preferred));
        return query;
    }

    /// <summary>What the call's record says was asked, in words.</summary>
    public static string Describe(AiVoiceSettings voice)
    {
        IReadOnlyList<string> languages;
        try
        {
            languages = Languages(voice);
        }
        catch (AiAttemptFailedException)
        {
            languages = [voice.TranscribeLanguages];
        }

        string how = languages.Count == 1 ? $"in {languages[0]}"
            : voice.TranscribeIdentifyMultiple ? $"{string.Join(" or ", languages)}, identified as spoken and switching within a recording (identify-multiple-languages)"
            : $"{string.Join(" or ", languages)}, one identified for the whole recording (identify-language)";
        return $"Amazon Transcribe streaming in {voice.TranscribeRegion.Trim()}: 16 kHz mono PCM, {how}. Final results only, joined in order.";
    }

    /// <summary>What <paramref name="seconds"/> of sound cost at <paramref name="voice"/>'s price: billed by the second, no minimum.</summary>
    public static decimal Cost(double seconds, AiVoiceSettings voice) => (decimal)Math.Ceiling(Math.Max(0, seconds)) * voice.TranscribeUsdPerSecond;

    /// <summary>The streaming endpoint in <paramref name="region"/>, as the call's record names it.</summary>
    public static string Endpoint(string region) => $"wss://{Host(region)}{Path}";

    private const string Path = "/stream-transcription-websocket";

    private static string Host(string region) => $"transcribestreaming.{region}.amazonaws.com:8443";

    private static Uri AsWebSocket(Uri signed) =>
        signed.Scheme == Uri.UriSchemeHttps ? new Uri("wss" + signed.AbsoluteUri[signed.Scheme.Length..]) : signed;

    private static IReadOnlyList<string> Languages(AiVoiceSettings voice)
    {
        var languages = voice.TranscribeLanguages.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (languages.Count == 0 || languages.Any(l => !LanguageRx().IsMatch(l)))
        {
            throw new AiAttemptFailedException($"Ai:Voice:TranscribeLanguages \"{voice.TranscribeLanguages}\" is not a list of language codes such as hi-IN,en-IN");
        }

        return languages;
    }

    /// <summary>The words in an exception's payload: <c>{"Message": "…"}</c> as AWS sends it, or the text itself.</summary>
    private static string MessageOf(byte[] payload)
    {
        string text = Encoding.UTF8.GetString(payload);
        try
        {
            var node = JsonNode.Parse(text);
            return node?["Message"]?.ToString() ?? node?["message"]?.ToString() ?? text;
        }
        catch (JsonException)
        {
            return text;
        }
    }

    /// <summary>On one line, short, with any AWS account number masked.</summary>
    private static string Flat(string text)
    {
        string flat = string.Join(' ', BedrockRefusals.Mask(text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= 160 ? flat : flat[..157] + "...";
    }

    /// <summary>Waits for a send or a read the stream no longer needs, which was cancelled or has already been reported.</summary>
    private static async Task Quietly(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception)
        {
            // Whatever it came to, the stream's outcome is already decided: this must not hide it.
        }
    }

    [GeneratedRegex("^[a-z]{2}(-[a-z]+)+-[0-9]$")]
    private static partial Regex RegionRx();

    [GeneratedRegex("^[a-zA-Z]{2,3}-[a-zA-Z]{2}$")]
    private static partial Regex LanguageRx();
}

/// <summary>
/// The final results of one stream, in the order Transcribe gave them. A partial result is counted and dropped; a final
/// result that comes again under the same id replaces the first.
/// </summary>
public sealed class TranscribeResults
{
    private readonly List<(string? Id, string Text, string? Language)> _finals = [];

    /// <summary>Partial results seen and dropped.</summary>
    public int Partials { get; private set; }

    /// <summary>The finals' words, joined with a space.</summary>
    public string Text => string.Join(' ', _finals.Select(f => f.Text.Trim()).Where(t => t.Length > 0));

    /// <summary>The finals' languages in order, a run of the same one folded into one.</summary>
    public IReadOnlyList<string> Languages
    {
        get
        {
            var languages = new List<string>();
            foreach (var (_, text, language) in _finals)
            {
                if (string.IsNullOrWhiteSpace(text) || string.IsNullOrEmpty(language)) continue;
                if (languages.Count == 0 || languages[^1] != language) languages.Add(language);
            }

            return languages;
        }
    }

    /// <summary>One <c>TranscriptEvent</c>'s JSON payload. Throws <see cref="InvalidDataException"/> when it is not JSON.</summary>
    public void Add(ReadOnlySpan<byte> json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("a transcript event was not JSON");
        }

        if (root?["Transcript"]?["Results"] is not JsonArray results) return;
        foreach (var result in results.OfType<JsonObject>())
        {
            if (result["IsPartial"] is JsonValue partial && partial.TryGetValue(out bool isPartial) && isPartial)
            {
                Partials++;
                continue;
            }

            string text = Str((result["Alternatives"] as JsonArray)?.FirstOrDefault()?["Transcript"]) ?? string.Empty;
            string? id = Str(result["ResultId"]);
            string? language = Str(result["LanguageCode"]);
            int seen = id is null ? -1 : _finals.FindIndex(f => f.Id == id);
            if (seen >= 0) _finals[seen] = (id, text, language);
            else _finals.Add((id, text, language));
        }
    }

    private static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? s) ? s : null;
}

/// <summary>One open stream to Amazon Transcribe: whole binary messages each way. One sender and one reader at a time.</summary>
public interface ITranscribeConnection : IAsyncDisposable
{
    Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken);

    /// <summary>The next whole binary message; null once Transcribe has closed the stream.</summary>
    Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken);
}

/// <summary>Opens streams to Amazon Transcribe: a WebSocket on the server, a script in the tests.</summary>
public interface ITranscribeTransport
{
    /// <summary>
    /// A stream open on <paramref name="url"/>. Throws <see cref="TranscribeHandshakeException"/> when the upgrade is
    /// refused (with its HTTP status) or the service cannot be reached (without one).
    /// </summary>
    Task<ITranscribeConnection> ConnectAsync(Uri url, CancellationToken cancellationToken);
}

/// <summary>The WebSocket upgrade failed: refused with <see cref="Status"/>, or never answered (null).</summary>
public sealed class TranscribeHandshakeException(int? status, string reason)
    : Exception(status is null ? $"Amazon Transcribe could not be reached ({reason})" : $"Amazon Transcribe refused the stream with HTTP {status}")
{
    public int? Status { get; } = status;

    public string Reason { get; } = reason;
}

/// <summary>
/// Streams over a real WebSocket: <see cref="ClientWebSocket"/> on the server, or any <see cref="WebSocket"/> a test
/// opens on an in-process host.
/// </summary>
public sealed class WebSocketTranscribeTransport : ITranscribeTransport
{
    private readonly Func<Uri, CancellationToken, Task<WebSocket>> _connect;

    public WebSocketTranscribeTransport()
        : this(null)
    {
    }

    /// <param name="connect">How a socket is opened; <see cref="ClientWebSocket"/> when null.</param>
    public WebSocketTranscribeTransport(Func<Uri, CancellationToken, Task<WebSocket>>? connect)
    {
        _connect = connect ?? OpenAsync;
    }

    public async Task<ITranscribeConnection> ConnectAsync(Uri url, CancellationToken cancellationToken) =>
        new Connection(await _connect(url, cancellationToken));

    /// <summary>A <see cref="ClientWebSocket"/> on <paramref name="url"/>, keeping the HTTP status of a refused upgrade.</summary>
    private static async Task<WebSocket> OpenAsync(Uri url, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        try
        {
            await socket.ConnectAsync(url, cancellationToken);
            return socket;
        }
        catch (WebSocketException ex)
        {
            int status = (int)socket.HttpStatusCode;
            socket.Dispose();
            throw status is > 0 and not 101
                ? new TranscribeHandshakeException(status, ex.WebSocketErrorCode.ToString())
                : new TranscribeHandshakeException(null, ex.InnerException is HttpRequestException http ? http.HttpRequestError.ToString() : ex.WebSocketErrorCode.ToString());
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private sealed class Connection(WebSocket socket) : ITranscribeConnection
    {
        public Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken) =>
            socket.SendAsync(message, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken).AsTask();

        public async Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken)
        {
            var buffer = new ArrayBufferWriter<byte>(8 * 1024);
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer.GetMemory(8 * 1024), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                buffer.Advance(result.Count);
                if (buffer.WrittenCount > AwsEventStream.MaxMessageBytes) throw new InvalidDataException("a message ran past 1 MB");
                if (!result.EndOfMessage) continue;
                if (result.MessageType == WebSocketMessageType.Binary) return buffer.WrittenSpan.ToArray();
                // A text message is no part of the protocol: skipped.
                buffer.Clear();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timer = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timer.Token);
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
                {
                    // Closing politely is a courtesy: the socket is let go either way.
                }
            }

            socket.Dispose();
        }
    }
}
