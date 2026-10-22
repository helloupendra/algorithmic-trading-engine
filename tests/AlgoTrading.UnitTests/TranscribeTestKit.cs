using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AlgoTrading.Infrastructure.Ai;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Amazon Transcribe streaming, scripted for the tests: the frames it sends back, a stand-in for its WebSocket, and a
/// presigner that signs nothing. Nothing here reaches AWS.
/// </summary>
public static class TranscribeTestKit
{
    /// <summary>The desk's client on <paramref name="aws"/>, sending as fast as the script takes it.</summary>
    public static AmazonTranscribeClient Client(IOptionsMonitor<AiSettings> settings, ITranscribeTransport aws) =>
        new(settings, new FakePresigner(), aws, NullLogger<AmazonTranscribeClient>.Instance) { PaceFactor = 0 };

    /// <summary>One result of a TranscriptEvent, as Transcribe writes it.</summary>
    public static JsonObject Result(string id, string text, bool partial, string language) => new()
    {
        ["ResultId"] = id,
        ["StartTime"] = 0.1,
        ["EndTime"] = 1.2,
        ["IsPartial"] = partial,
        ["LanguageCode"] = language,
        ["Alternatives"] = new JsonArray(new JsonObject { ["Transcript"] = text, ["Items"] = new JsonArray() }),
    };

    /// <summary>A TranscriptEvent's JSON payload.</summary>
    public static byte[] Event(params JsonObject[] results) =>
        Encoding.UTF8.GetBytes(new JsonObject { ["Transcript"] = new JsonObject { ["Results"] = new JsonArray(results) } }.ToJsonString());

    /// <summary>A TranscriptEvent frame as Transcribe sends one.</summary>
    public static byte[] Transcript(params JsonObject[] results) => AwsEventStream.Encode(
        [(":content-type", "application/json"), (":event-type", "TranscriptEvent"), (":message-type", "event")], Event(results));

    /// <summary>
    /// A stream's whole answer: for each stretch said, a partial result first and then its final, each in its language.
    /// </summary>
    public static byte[][] Heard(params (string Text, string Language)[] stretches) => stretches
        .SelectMany((s, i) => new[]
        {
            Transcript(Result($"r{i}", s.Text[..Math.Max(1, s.Text.Length / 2)], partial: true, s.Language)),
            Transcript(Result($"r{i}", s.Text, partial: false, s.Language)),
        })
        .ToArray();

    /// <summary>An exception frame with the headers Transcribe's WebSocket guide lists.</summary>
    public static byte[] ExceptionFrame(string kind, string message) => AwsEventStream.Encode(
        [(":content-type", "application/json"), (":exception-type", kind), (":message-type", "exception")],
        Encoding.UTF8.GetBytes(new JsonObject { ["Message"] = message }.ToJsonString()));

    /// <summary>Returns the URL as it was given, with <paramref name="extra"/> appended as a stand-in signature.</summary>
    public sealed class FakePresigner(string extra = "X-Amz-Signature=test") : IAwsUrlPresigner
    {
        public Task<Uri> PresignAsync(Uri url, string region, string service, TimeSpan lifetime, CancellationToken cancellationToken)
        {
            Assert.Equal(("ap-south-1", "transcribe", TimeSpan.FromMinutes(5)), (region, service, lifetime));
            return Task.FromResult(new Uri($"{url.AbsoluteUri}&{extra}"));
        }
    }

    /// <summary>
    /// Amazon Transcribe, scripted: keeps every frame the desk sends, and once the empty frame ends a stream sends that
    /// stream's answer (the next of <see cref="Answers"/>, else <see cref="Replies"/>) and closes, or stays open.
    /// </summary>
    public sealed class ScriptedTranscribe : ITranscribeTransport
    {
        /// <summary>Each stream's answer, in the order the streams open.</summary>
        public Queue<byte[][]> Answers { get; } = new();

        /// <summary>The answer of a stream when <see cref="Answers"/> has none left.</summary>
        public List<byte[]> Replies { get; init; } = [];

        public bool CloseAfterReplies { get; init; } = true;

        /// <summary>Sent at once after the first frame, then the stream closes and every later send fails.</summary>
        public byte[]? RefuseAfterFirstFrame { get; init; }

        /// <summary>Each stream's upgrade is refused with this, while it is set.</summary>
        public TranscribeHandshakeException? Refuse { get; set; }

        /// <summary>The URL each stream was opened on.</summary>
        public List<Uri> Urls { get; } = [];

        public Uri? Url => Urls.LastOrDefault();

        /// <summary>Every frame of every stream, in order.</summary>
        public List<AwsEventMessage> Frames { get; } = [];

        /// <summary>The sound of every stream, joined.</summary>
        public byte[] Sound => Frames.SelectMany(f => f.Payload).ToArray();

        public bool Disposed { get; private set; }

        public Task<ITranscribeConnection> ConnectAsync(Uri url, CancellationToken cancellationToken)
        {
            Urls.Add(url);
            if (Refuse is not null) throw Refuse;
            return Task.FromResult<ITranscribeConnection>(new Connection(this, Answers.Count > 0 ? Answers.Dequeue() : [.. Replies]));
        }

        private sealed class Connection(ScriptedTranscribe aws, byte[][] answer) : ITranscribeConnection
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
                    foreach (var reply in answer) _out.Writer.TryWrite(reply);
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
}
