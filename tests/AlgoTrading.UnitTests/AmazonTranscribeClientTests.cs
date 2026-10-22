using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AlgoTrading.Infrastructure.Ai;
using Amazon.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Amazon Transcribe streaming, written to AWS's WebSocket guide: the presigned URL, the frames sent, the transcript
/// assembled from the events that come back, and every way a stream can fail. Nothing reaches AWS: the streams are a
/// script, or an in-process host speaking the same frames over a real WebSocket.
/// </summary>
public sealed class AmazonTranscribeClientTests
{
    private const string Region = "ap-south-1";

    // ---------- the presigned URL ----------

    [Fact]
    public async Task The_url_is_presigned_for_transcribe_in_mumbai_with_the_roles_session_and_no_secret()
    {
        var signer = new AwsSigV4BedrockSigner(_ => Task.FromResult<AWSCredentials>(new SessionAWSCredentials("AKIDTESTONLY", "not-a-secret/xyz", "session/token+for=tests")));

        var url = await signer.PresignAsync(AmazonTranscribeClient.Url(new AiVoiceSettings()), Region, AmazonTranscribeClient.Service, AmazonTranscribeClient.UrlLifetime, CancellationToken.None);

        Assert.Equal(("transcribestreaming.ap-south-1.amazonaws.com", 8443, "/stream-transcription-websocket"), (url.Host, url.Port, url.AbsolutePath));
        var q = QueryOf(url);
        Assert.Equal(("AWS4-HMAC-SHA256", "300", "host", "session/token+for=tests"), (q["X-Amz-Algorithm"], q["X-Amz-Expires"], q["X-Amz-SignedHeaders"], q["X-Amz-Security-Token"]));
        Assert.Matches(@"^AKIDTESTONLY/\d{8}/ap-south-1/transcribe/aws4_request$", q["X-Amz-Credential"]);
        Assert.Matches("^[0-9a-f]{64}$", q["X-Amz-Signature"]);
        Assert.Equal(("pcm", "16000", "true", "hi-IN,en-IN"), (q["media-encoding"], q["sample-rate"], q["identify-multiple-languages"], q["language-options"]));
        Assert.False(q.ContainsKey("preferred-language"));
        Assert.False(q.ContainsKey("language-code"));
        Assert.DoesNotContain("not-a-secret", url.AbsoluteUri);
    }

    [Fact]
    public async Task The_signature_is_the_one_awss_websocket_guide_computes_by_hand()
    {
        const string secret = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY";
        var signer = new AwsSigV4BedrockSigner(_ => Task.FromResult<AWSCredentials>(new SessionAWSCredentials("AKIDEXAMPLE", secret, "token/with+chars=")));

        var url = await signer.PresignAsync(AmazonTranscribeClient.Url(new AiVoiceSettings()), Region, "transcribe", TimeSpan.FromMinutes(5), CancellationToken.None);

        // The guide's steps: the query sorted and RFC 3986-encoded (the signature left out), "host:<host>:8443" the one
        // signed header, the hash of an empty payload, then the date/region/service/aws4_request key chain.
        var q = QueryOf(url);
        string amzDate = q["X-Amz-Date"];
        string canonicalQuery = string.Join('&', q.Where(p => p.Key != "X-Amz-Signature").OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{Encode(p.Key)}={Encode(p.Value)}"));
        string canonicalRequest = string.Join('\n', "GET", "/stream-transcription-websocket", canonicalQuery,
            "host:transcribestreaming.ap-south-1.amazonaws.com:8443\n", "host", Convert.ToHexStringLower(SHA256.HashData([])));
        string scope = $"{amzDate[..8]}/ap-south-1/transcribe/aws4_request";
        string toSign = string.Join('\n', "AWS4-HMAC-SHA256", amzDate, scope, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));
        byte[] key = Encoding.UTF8.GetBytes("AWS4" + secret);
        foreach (string part in new[] { amzDate[..8], "ap-south-1", "transcribe", "aws4_request" }) key = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(part));

        Assert.Equal(Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(toSign))), q["X-Amz-Signature"]);
    }

    [Fact]
    public void The_languages_are_identified_within_a_recording_or_once_or_fixed_by_the_settings()
    {
        var many = new AiVoiceSettings();
        var one = new AiVoiceSettings { TranscribeIdentifyMultiple = false };
        var fixedHindi = new AiVoiceSettings { TranscribeLanguages = "hi-IN" };
        var preferredElsewhere = new AiVoiceSettings { TranscribeIdentifyMultiple = false, TranscribePreferredLanguage = "ta-IN" };

        Assert.Equal(
            new[] { ("media-encoding", "pcm"), ("sample-rate", "16000"), ("identify-multiple-languages", "true"), ("language-options", "hi-IN,en-IN") },
            AmazonTranscribeClient.Query(many));
        Assert.Equal(
            new[] { ("media-encoding", "pcm"), ("sample-rate", "16000"), ("identify-language", "true"), ("language-options", "hi-IN,en-IN"), ("preferred-language", "hi-IN") },
            AmazonTranscribeClient.Query(one));
        Assert.Equal(new[] { ("media-encoding", "pcm"), ("sample-rate", "16000"), ("language-code", "hi-IN") }, AmazonTranscribeClient.Query(fixedHindi));
        Assert.DoesNotContain(AmazonTranscribeClient.Query(preferredElsewhere), p => p.Name == "preferred-language");
        Assert.StartsWith("Ai:Voice:TranscribeLanguages \"hindi, english\" is not", Assert.Throws<AiAttemptFailedException>(() =>
            AmazonTranscribeClient.Query(new AiVoiceSettings { TranscribeLanguages = "hindi, english" })).Outcome);
        Assert.StartsWith("Ai:Voice:TranscribeRegion \"mumbai\"", Assert.Throws<AiAttemptFailedException>(() =>
            AmazonTranscribeClient.Url(new AiVoiceSettings { TranscribeRegion = "mumbai" })).Outcome);
    }

    [Fact]
    public void A_seconds_streaming_is_priced_at_mumbais_rate_by_the_whole_second()
    {
        var voice = new AiVoiceSettings();

        Assert.Equal(0.0001667m, voice.TranscribeUsdPerSecond);
        Assert.Equal(0.0005001m, AmazonTranscribeClient.Cost(2.2, voice));
        Assert.Equal(0.020004m, AmazonTranscribeClient.Cost(120, voice));
        Assert.Equal(0m, AmazonTranscribeClient.Cost(0, voice));
    }

    // ---------- the transcript ----------

    [Fact]
    public void Only_final_results_are_kept_in_order_across_events_and_a_language_switch()
    {
        var heard = new TranscribeResults();

        heard.Add(Event(Result("r1", "कल सुबह", partial: true, "hi-IN")));
        heard.Add(Event(Result("r1", "कल सुबह नौ बजे", partial: false, "hi-IN")));
        heard.Add(Event(Result("r2", "meeting", partial: true, "en-IN"), Result("r2", "meeting hai", partial: true, "en-IN")));
        heard.Add(Event(Result("r2", "meeting hai", partial: false, "en-IN"), Result("r3", "NIFTY", partial: false, "en-IN")));
        heard.Add(Event(Result("r4", "का क्या हाल है?", partial: false, "hi-IN")));
        heard.Add(Encoding.UTF8.GetBytes("""{"Transcript":{"Results":[]}}"""));

        Assert.Equal("कल सुबह नौ बजे meeting hai NIFTY का क्या हाल है?", heard.Text);
        Assert.Equal(new[] { "hi-IN", "en-IN", "hi-IN" }, heard.Languages);
        Assert.Equal(3, heard.Partials);
    }

    [Fact]
    public void A_final_that_comes_again_under_its_id_replaces_the_first_and_empty_finals_add_nothing()
    {
        var heard = new TranscribeResults();

        heard.Add(Event(Result("r1", "aaj ka", partial: false, "en-IN")));
        heard.Add(Event(Result("r2", "  ", partial: false, "en-IN")));
        heard.Add(Event(Result("r1", "aaj ka P&L", partial: false, "en-IN")));

        Assert.Equal("aaj ka P&L", heard.Text);
        Assert.Equal(new[] { "en-IN" }, heard.Languages);
        Assert.Equal("a transcript event was not JSON", Assert.Throws<InvalidDataException>(() => heard.Add("not json"u8)).Message);
    }

    // ---------- the stream ----------

    [Fact]
    public async Task A_recording_goes_as_100_ms_frames_then_an_empty_one_and_the_finals_come_back_joined()
    {
        var aws = new ScriptedTranscribe
        {
            Replies =
            [
                Transcript(Result("r1", "इरा, आज का", partial: true, "hi-IN")),
                Transcript(Result("r1", "इरा, आज का P&L", partial: false, "hi-IN")),
                Transcript(Result("r2", "kya hai?", partial: false, "en-IN")),
            ],
        };
        byte[] pcm = Pcm(seconds: 1.05);

        var heard = await Client(aws).TranscribeAsync(pcm, CancellationToken.None);

        Assert.Equal(("इरा, आज का P&L kya hai?", 1.05), (heard.Text, heard.AudioSeconds));
        Assert.Equal(new[] { "hi-IN", "en-IN" }, heard.Languages);
        // Ten full frames of 3,200 bytes, the rest (1,600), then the empty frame that ends the stream.
        Assert.Equal(Enumerable.Repeat(3200, 10).Append(1600).Append(0), aws.Frames.Select(f => f.Payload.Length));
        Assert.All(aws.Frames, f => Assert.Equal(("event", "AudioEvent", "application/octet-stream"), (f.Header(":message-type"), f.Header(":event-type"), f.Header(":content-type"))));
        Assert.Equal(pcm, aws.Frames.SelectMany(f => f.Payload).ToArray());
        Assert.StartsWith("wss://transcribestreaming.ap-south-1.amazonaws.com:8443/stream-transcription-websocket?", aws.Url!.AbsoluteUri);
        Assert.True(aws.Disposed);
    }

    [Fact]
    public async Task A_stream_that_stays_open_after_its_finals_ends_after_a_quiet_spell()
    {
        var aws = new ScriptedTranscribe { Replies = [Transcript(Result("r1", "aaj ki khabar", partial: false, "en-IN"))], CloseAfterReplies = false };

        var heard = await Client(aws, quiet: TimeSpan.FromMilliseconds(200)).TranscribeAsync(Pcm(0.5), CancellationToken.None);

        Assert.Equal("aaj ki khabar", heard.Text);
    }

    [Theory]
    [InlineData("BadRequestException", "http 400: Amazon Transcribe refused the stream: Unsupported language: [account]", 400)]
    [InlineData("LimitExceededException", "http 429: Amazon Transcribe's limit was reached (too many streams at once, or one too long): Unsupported language: [account]", 429)]
    [InlineData("ServiceUnavailableException", "http 503: Amazon Transcribe is unavailable, temporarily: Unsupported language: [account]", 503)]
    [InlineData("InternalFailureException", "http 500: Amazon Transcribe failed while processing the sound: Unsupported language: [account]", 500)]
    [InlineData("ConflictException", "http 409: another stream took over this session: Unsupported language: [account]", 409)]
    [InlineData("UnrecognizedClientException", "http 403: Amazon Transcribe refused the server's AWS credentials: Unsupported language: [account]", 403)]
    [InlineData("AccessDeniedException", "http 403: the server's AWS role is not allowed to call Amazon Transcribe (it needs transcribe:StartStreamTranscriptionWebSocket): Unsupported language: [account]", 403)]
    [InlineData("SomethingNewException", "provider error: Amazon Transcribe sent SomethingNewException: Unsupported language: [account]", null)]
    public async Task An_exception_from_transcribe_is_worded_like_the_other_providers_refusals(string kind, string outcome, int? status)
    {
        var aws = new ScriptedTranscribe { Replies = [ExceptionFrame(kind, "Unsupported language: 123456789012")] };

        var ex = await Assert.ThrowsAsync<AiAttemptFailedException>(() => Client(aws).TranscribeAsync(Pcm(0.3), CancellationToken.None));

        Assert.Equal((outcome, status), (ex.Outcome, ex.HttpStatus));
    }

    [Fact]
    public async Task An_exception_sent_mid_stream_is_the_error_even_when_the_next_send_fails()
    {
        // Transcribe refuses after the first frame and closes: the desk's next send fails, but the refusal is what is said.
        var aws = new ScriptedTranscribe { RefuseAfterFirstFrame = ExceptionFrame("BadRequestException", "Your stream is too big") };

        var ex = await Assert.ThrowsAsync<AiAttemptFailedException>(() => Client(aws).TranscribeAsync(Pcm(2), CancellationToken.None));

        Assert.Equal("http 400: Amazon Transcribe refused the stream: Your stream is too big", ex.Outcome);
    }

    [Theory]
    [InlineData(403, "http 403: Amazon Transcribe refused the stream: the server's AWS role needs transcribe:StartStreamTranscriptionWebSocket in ap-south-1, or its signature was refused")]
    [InlineData(400, "http 400: Amazon Transcribe refused the stream's parameters")]
    [InlineData(429, "http 429: throttled by Amazon Transcribe")]
    [InlineData(503, "http 503: Amazon Transcribe is unavailable, temporarily")]
    [InlineData(null, "unreachable (NameResolutionError)")]
    public async Task A_refused_upgrade_names_the_permission_or_the_reason(int? status, string outcome)
    {
        var aws = new ScriptedTranscribe { Refuse = new TranscribeHandshakeException(status, "NameResolutionError") };

        var ex = await Assert.ThrowsAsync<AiAttemptFailedException>(() => Client(aws).TranscribeAsync(Pcm(0.3), CancellationToken.None));

        Assert.Equal(outcome, ex.Outcome);
        Assert.Equal(status, ex.HttpStatus);
    }

    [Fact]
    public async Task Silence_a_stream_that_never_answers_a_damaged_reply_and_no_credentials_each_say_so()
    {
        var silent = new ScriptedTranscribe { Replies = [Transcript(Result("r1", "", partial: false, "hi-IN"))] };
        var hung = new ScriptedTranscribe { Replies = [], CloseAfterReplies = false };
        var damaged = new ScriptedTranscribe { Replies = [[0, 0, 0, 20, 0, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]] };
        var unsigned = new ScriptedTranscribe();

        var none = await Assert.ThrowsAsync<AiAttemptFailedException>(() => Client(silent).TranscribeAsync(Pcm(0.5), CancellationToken.None));
        var late = await Assert.ThrowsAsync<AiAttemptFailedException>(() => Client(hung, timeout: TimeSpan.FromSeconds(1)).TranscribeAsync(Pcm(0.1), CancellationToken.None));
        var garbled = await Assert.ThrowsAsync<AiAttemptFailedException>(() => Client(damaged).TranscribeAsync(Pcm(0.1), CancellationToken.None));
        var noKeys = await Assert.ThrowsAsync<AiAttemptFailedException>(() => Client(unsigned, presigner: new NoCredentials()).TranscribeAsync(Pcm(0.1), CancellationToken.None));

        Assert.Equal("empty answer: Amazon Transcribe heard no words", none.Outcome);
        Assert.Equal("timeout: no answer within 1 s", late.Outcome);
        Assert.Equal("provider error: an unreadable reply from Amazon Transcribe (its prelude checksum is wrong)", garbled.Outcome);
        Assert.StartsWith("unreachable (AWS: could not sign the request:", noKeys.Outcome);
        Assert.Null(unsigned.Url);
    }

    [Fact]
    public async Task Sound_past_the_limit_or_not_16_bit_is_refused_before_anything_is_signed()
    {
        var aws = new ScriptedTranscribe();
        var client = Client(aws, maxSeconds: 2);

        var tooLong = await Assert.ThrowsAsync<AiAttemptFailedException>(() => client.TranscribeAsync(Pcm(3.5), CancellationToken.None));
        var odd = await Assert.ThrowsAsync<AiAttemptFailedException>(() => client.TranscribeAsync([1, 2, 3], CancellationToken.None));

        Assert.Equal("too long: 4 s of sound, past the 2 s the desk sends (Ai:Voice:MaxSeconds)", tooLong.Outcome);
        Assert.Equal("no sound to send: the audio is not 16-bit PCM", odd.Outcome);
        Assert.Null(aws.Url);
    }

    [Fact]
    public async Task A_caller_that_stops_is_not_a_failure_of_the_provider()
    {
        var aws = new ScriptedTranscribe { Replies = [], CloseAfterReplies = false };
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(aws).TranscribeAsync(Pcm(0.2), stop.Token));
    }

    [Fact]
    public async Task The_log_holds_sizes_timings_and_the_outcome_never_the_words_or_the_signature()
    {
        var log = new ListLogger();
        var aws = new ScriptedTranscribe { Replies = [Transcript(Result("r1", "aaj ka P&L kya hai", partial: false, "en-IN"))] };
        var client = new AmazonTranscribeClient(new AiTestKit.Monitor(AiTestKit.Settings()), new FakePresigner("X-Amz-Signature=deadbeef&X-Amz-Security-Token=tok"), aws, log)
        {
            PaceFactor = 0,
        };

        await client.TranscribeAsync(Pcm(1), CancellationToken.None);

        var line = Assert.Single(log.Lines);
        Assert.Equal("Amazon Transcribe: 1.0 s of sound (32000 bytes) in", line[..50]);
        Assert.EndsWith(": ok", line);
        Assert.DoesNotContain("P&L", line);
        Assert.DoesNotContain("deadbeef", line);
        Assert.DoesNotContain("tok", line);
    }

    // ---------- over a real WebSocket ----------

    [Fact]
    public async Task Over_a_real_websocket_split_messages_are_joined_text_is_skipped_and_the_close_ends_the_stream()
    {
        int audioBytes = 0, frames = 0;
        await using var host = await Host(async socket =>
        {
            var buffer = new byte[64 * 1024];
            while (true)
            {
                // Each frame the desk sends is one whole WebSocket message.
                var received = await socket.ReceiveAsync(buffer, CancellationToken.None);
                var frame = AwsEventStream.Decode(buffer.AsSpan(0, received.Count));
                frames++;
                audioBytes += frame.Payload.Length;
                if (frame.Payload.Length == 0) break;
            }

            byte[] partial = Transcript(Result("r1", "Ira aaj", partial: true, "en-IN"));
            byte[] final = Transcript(Result("r1", "Ira, aaj ka P&L?", partial: false, "en-IN"));
            await socket.SendAsync(partial, WebSocketMessageType.Binary, true, CancellationToken.None);
            await socket.SendAsync("not part of the protocol"u8.ToArray(), WebSocketMessageType.Text, true, CancellationToken.None);
            // One event in three pieces, as a WebSocket may deliver it.
            await socket.SendAsync(final.AsMemory(0, 10), WebSocketMessageType.Binary, false, CancellationToken.None);
            await socket.SendAsync(final.AsMemory(10, 30), WebSocketMessageType.Binary, false, CancellationToken.None);
            await socket.SendAsync(final.AsMemory(40), WebSocketMessageType.Binary, true, CancellationToken.None);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        });

        var heard = await Client(host.Transport).TranscribeAsync(Pcm(0.5), CancellationToken.None);

        Assert.Equal("Ira, aaj ka P&L?", heard.Text);
        Assert.Equal((16_000, 6), (audioBytes, frames));
    }

    [Fact]
    public async Task Over_a_real_websocket_an_exception_frame_then_a_close_is_the_error()
    {
        await using var host = await Host(async socket =>
        {
            var buffer = new byte[64 * 1024];
            await socket.ReceiveAsync(buffer, CancellationToken.None);
            await socket.SendAsync(ExceptionFrame("LimitExceededException", "You have reached your limit of concurrent streams."), WebSocketMessageType.Binary, true, CancellationToken.None);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        });

        var ex = await Assert.ThrowsAsync<AiAttemptFailedException>(() => Client(host.Transport).TranscribeAsync(Pcm(2), CancellationToken.None));

        Assert.Equal(("http 429: Amazon Transcribe's limit was reached (too many streams at once, or one too long): You have reached your limit of concurrent streams.", 429),
            (ex.Outcome, ex.HttpStatus));
    }

    // ---------- helpers ----------

    private static AmazonTranscribeClient Client(
        ITranscribeTransport transport, TimeSpan? quiet = null, TimeSpan? timeout = null, int maxSeconds = 120, IAwsUrlPresigner? presigner = null) =>
        new(new AiTestKit.Monitor(AiTestKit.Settings(s => s.Voice.MaxSeconds = maxSeconds)), presigner ?? new FakePresigner(), transport, new ListLogger())
        {
            PaceFactor = 0,
            QuietWindow = quiet ?? TimeSpan.FromSeconds(5),
            BaseTimeout = timeout ?? TimeSpan.FromSeconds(30),
        };

    /// <summary>Silence of <paramref name="seconds"/> as 16 kHz 16-bit PCM, its bytes numbered so the frames can be checked.</summary>
    private static byte[] Pcm(double seconds) => [.. Enumerable.Range(0, (int)(seconds * PcmAudio.BytesPerSecond)).Select(i => (byte)i)];

    private static JsonObject Result(string id, string text, bool partial, string language) => new()
    {
        ["ResultId"] = id,
        ["StartTime"] = 0.1,
        ["EndTime"] = 1.2,
        ["IsPartial"] = partial,
        ["LanguageCode"] = language,
        ["Alternatives"] = new JsonArray(new JsonObject { ["Transcript"] = text, ["Items"] = new JsonArray() }),
    };

    private static byte[] Event(params JsonObject[] results) =>
        Encoding.UTF8.GetBytes(new JsonObject { ["Transcript"] = new JsonObject { ["Results"] = new JsonArray(results) } }.ToJsonString());

    /// <summary>A TranscriptEvent frame as Transcribe sends one.</summary>
    private static byte[] Transcript(params JsonObject[] results) => AwsEventStream.Encode(
        [(":content-type", "application/json"), (":event-type", "TranscriptEvent"), (":message-type", "event")], Event(results));

    /// <summary>An exception frame with the headers the WebSocket guide lists.</summary>
    private static byte[] ExceptionFrame(string kind, string message) => AwsEventStream.Encode(
        [(":content-type", "application/json"), (":exception-type", kind), (":message-type", "exception")],
        Encoding.UTF8.GetBytes(new JsonObject { ["Message"] = message }.ToJsonString()));

    private static Dictionary<string, string> QueryOf(Uri url) => url.Query.TrimStart('?').Split('&')
        .Select(p => p.Split('=', 2))
        .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]), StringComparer.Ordinal);

    /// <summary>RFC 3986 encoding, as SigV4 wants it: only A-Z a-z 0-9 - _ . ~ stay as they are.</summary>
    private static string Encode(string value) => string.Concat(Encoding.UTF8.GetBytes(value).Select(b =>
        b is (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z') or (>= (byte)'0' and <= (byte)'9') or (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~'
            ? ((char)b).ToString()
            : $"%{b:X2}"));

    /// <summary>An in-process host whose one WebSocket endpoint runs <paramref name="serve"/>; no port is opened.</summary>
    private static async Task<TestHost> Host(Func<WebSocket, Task> serve)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.UseWebSockets();
        app.Map("/stream-transcription-websocket", async (HttpContext context) =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await serve(socket);
        });
        await app.StartAsync();
        var client = app.GetTestServer().CreateWebSocketClient();
        return new TestHost(app, new WebSocketTranscribeTransport((uri, ct) => client.ConnectAsync(uri, ct)));
    }

    private sealed record TestHost(WebApplication App, WebSocketTranscribeTransport Transport) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    /// <summary>Returns the URL as it was given, with <paramref name="extra"/> appended as a stand-in signature.</summary>
    private sealed class FakePresigner(string extra = "X-Amz-Signature=test") : IAwsUrlPresigner
    {
        public Task<Uri> PresignAsync(Uri url, string region, string service, TimeSpan lifetime, CancellationToken cancellationToken)
        {
            Assert.Equal((Region, "transcribe", TimeSpan.FromMinutes(5)), (region, service, lifetime));
            return Task.FromResult(new Uri($"{url.AbsoluteUri}&{extra}"));
        }
    }

    /// <summary>The real signer on a machine with no AWS credentials.</summary>
    private sealed class NoCredentials : IAwsUrlPresigner
    {
        private readonly AwsSigV4BedrockSigner _signer = new(_ => throw new AmazonClientException("Failed to resolve AWS credentials"));

        public Task<Uri> PresignAsync(Uri url, string region, string service, TimeSpan lifetime, CancellationToken cancellationToken) =>
            _signer.PresignAsync(url, region, service, lifetime, cancellationToken);
    }

    /// <summary>
    /// Amazon Transcribe, scripted: keeps every frame the desk sends, and once the empty frame ends the stream sends its
    /// <see cref="Replies"/> and closes (or stays open).
    /// </summary>
    private sealed class ScriptedTranscribe : ITranscribeTransport
    {
        public List<byte[]> Replies { get; init; } = [];

        public bool CloseAfterReplies { get; init; } = true;

        /// <summary>Sent at once after the first frame, then the stream closes and every later send fails.</summary>
        public byte[]? RefuseAfterFirstFrame { get; init; }

        public TranscribeHandshakeException? Refuse { get; init; }

        public Uri? Url { get; private set; }

        public List<AwsEventMessage> Frames { get; } = [];

        public bool Disposed { get; private set; }

        public Task<ITranscribeConnection> ConnectAsync(Uri url, CancellationToken cancellationToken)
        {
            Url = url;
            if (Refuse is not null) throw Refuse;
            return Task.FromResult<ITranscribeConnection>(new Connection(this));
        }

        private sealed class Connection(ScriptedTranscribe aws) : ITranscribeConnection
        {
            private readonly Channel<byte[]?> _out = Channel.CreateUnbounded<byte[]?>();
            private bool _closed;

            public Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
            {
                if (_closed) throw new WebSocketException(WebSocketError.InvalidState);
                var frame = AwsEventStream.Decode(message.Span);
                aws.Frames.Add(frame);
                if (aws.RefuseAfterFirstFrame is { } refusal)
                {
                    _closed = true;
                    _out.Writer.TryWrite(refusal);
                    _out.Writer.TryWrite(null);
                }
                else if (frame.Payload.Length == 0)
                {
                    foreach (var reply in aws.Replies) _out.Writer.TryWrite(reply);
                    if (aws.CloseAfterReplies) _out.Writer.TryWrite(null);
                }

                return Task.CompletedTask;
            }

            public async Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken) => await _out.Reader.ReadAsync(cancellationToken);

            public ValueTask DisposeAsync()
            {
                aws.Disposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class ListLogger : ILogger<AmazonTranscribeClient>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }
}
