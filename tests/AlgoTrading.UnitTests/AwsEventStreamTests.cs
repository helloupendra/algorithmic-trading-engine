using System.Buffers.Binary;
using System.Text;
using AlgoTrading.Infrastructure.Ai;
using Amazon.Runtime.EventStreams;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// AWS's event-stream framing, which Amazon Transcribe streaming speaks: checked against the frame AWS prints in its
/// own guide, against bytes made independently with Python's zlib, and against AWSSDK.Core's own encoder.
/// </summary>
public sealed class AwsEventStreamTests
{
    /// <summary>
    /// The TranscriptEvent frame printed in "Setting up a streaming transcription" (Amazon Transcribe developer guide),
    /// as base64: a <c>:date</c> timestamp header, a <c>:chunk-signature</c> byte-array header, and no payload.
    /// </summary>
    private const string AwsSample = "AAAAUwAAAEP1RHpYBTpkYXRlCAAAAWiXUkMLEDpjaHVuay1zaWduYXR1cmUGACCt6Zy+uymwEK2SrLp/zVBI5eGn83jdBwCaRUBJA+eaDafqjqI=";

    /// <summary>An AudioEvent of the bytes 01 02 01 02 01 02 01 02, encoded by Python's struct and zlib.crc32.</summary>
    private const string GoldenAudio =
        "0000007000000058f940e6600d3a636f6e74656e742d747970650700186170706c69636174696f6e2f6f637465742d73747265616d0b3a6576656e742d" +
        "7479706507000a417564696f4576656e740d3a6d6573736167652d747970650700056576656e7401020102010201020710472e";

    /// <summary>The empty AudioEvent that ends a stream, made the same way.</summary>
    private const string GoldenEnd =
        "0000006800000058a9d03a230d3a636f6e74656e742d747970650700186170706c69636174696f6e2f6f637465742d73747265616d0b3a6576656e742d" +
        "7479706507000a417564696f4576656e740d3a6d6573736167652d747970650700056576656e745cc64095";

    [Fact]
    public void The_crc_is_gzips_crc32() =>
        Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));

    [Fact]
    public void Awss_own_printed_frame_passes_both_checksums_and_reads_every_header()
    {
        byte[] frame = Convert.FromBase64String(AwsSample);

        var message = AwsEventStream.Decode(frame);

        Assert.Equal(83, BinaryPrimitives.ReadInt32BigEndian(frame));
        Assert.Equal(Crc32.Compute(frame.AsSpan(0, 8)), BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(8)));
        Assert.Equal(Crc32.Compute(frame.AsSpan(0, frame.Length - 4)), BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(frame.Length - 4)));
        // The same instant the guide prints beside it: ":date: 2019-01-29T01:56:17.291Z".
        Assert.Equal(DateTimeOffset.Parse("2019-01-29T01:56:17.291Z", System.Globalization.CultureInfo.InvariantCulture), message.Headers[":date"]);
        Assert.Equal(32, Assert.IsType<byte[]>(message.Headers[":chunk-signature"]).Length);
        Assert.Empty(message.Payload);
    }

    [Fact]
    public void An_audio_event_and_the_end_of_the_stream_are_byte_for_byte_what_zlib_makes()
    {
        Assert.Equal(GoldenAudio, Convert.ToHexStringLower(AwsEventStream.AudioEvent([1, 2, 1, 2, 1, 2, 1, 2])));
        Assert.Equal(GoldenEnd, Convert.ToHexStringLower(AwsEventStream.AudioEvent([])));
    }

    [Fact]
    public void An_audio_event_reads_back_with_the_three_headers_the_websocket_guide_lists()
    {
        byte[] chunk = [.. Enumerable.Range(0, 3200).Select(i => (byte)i)];

        var message = AwsEventStream.Decode(AwsEventStream.AudioEvent(chunk));

        Assert.Equal(("event", "AudioEvent", "application/octet-stream"), (message.Header(":message-type"), message.Header(":event-type"), message.Header(":content-type")));
        Assert.Equal(3, message.Headers.Count);
        Assert.Equal(chunk, message.Payload);
    }

    [Fact]
    public void The_sdks_own_reader_takes_our_frames_and_we_take_the_sdks_with_every_header_type()
    {
        // Ours, read by AWSSDK.Core.
        var theirs = EventStreamMessage.FromBuffer(AwsEventStream.AudioEvent([9, 8, 7]), 0, AwsEventStream.AudioEvent([9, 8, 7]).Length);
        Assert.Equal("AudioEvent", theirs.Headers[":event-type"].AsString());
        Assert.Equal(new byte[] { 9, 8, 7 }, theirs.Payload);

        // Theirs, read by us: one header of each type the format has but the UUID, which AWSSDK.Core writes in .NET's
        // own mixed byte order (Guid.ToByteArray) where the format's other SDKs write RFC 4122's; Transcribe sends none.
        var at = new DateTime(2026, 10, 22, 12, 0, 0, 123, DateTimeKind.Utc);
        var headers = new List<IEventStreamHeader>
        {
            Header(":message-type", h => h.SetString("event")),
            Header("yes", h => h.SetBool(true)),
            Header("no", h => h.SetBool(false)),
            Header("byte", h => h.SetSByte(-5)),
            Header("short", h => h.SetInt16(-300)),
            Header("int", h => h.SetInt32(70_000)),
            Header("long", h => h.SetInt64(5_000_000_000)),
            Header("bytes", h => h.SetByteBuf([1, 2, 3])),
            Header("when", h => h.SetTimestamp(at)),
        };
        byte[] sdk = new EventStreamMessage(headers, Encoding.UTF8.GetBytes("""{"ok":true}""")).ToByteArray();

        var ours = AwsEventStream.Decode(sdk);

        Assert.Equal("event", ours.Header(":message-type"));
        Assert.Equal((true, false, (sbyte)-5, (short)-300, 70_000, 5_000_000_000L), (
            (bool)ours.Headers["yes"], (bool)ours.Headers["no"], (sbyte)ours.Headers["byte"], (short)ours.Headers["short"], (int)ours.Headers["int"], (long)ours.Headers["long"]));
        Assert.Equal(new byte[] { 1, 2, 3 }, ours.Headers["bytes"]);
        Assert.Equal(new DateTimeOffset(at), ours.Headers["when"]);
        Assert.Equal("""{"ok":true}""", Encoding.UTF8.GetString(ours.Payload));
    }

    [Fact]
    public void A_damaged_or_cut_frame_is_refused_in_words()
    {
        byte[] good = AwsEventStream.AudioEvent([1, 2, 3, 4]);
        byte[] payloadHit = (byte[])good.Clone();
        payloadHit[^6] ^= 0xFF;
        byte[] preludeHit = (byte[])good.Clone();
        preludeHit[3] ^= 0x01;

        Assert.Equal("its message checksum is wrong", Assert.Throws<InvalidDataException>(() => AwsEventStream.Decode(payloadHit)).Message);
        Assert.Equal("its prelude checksum is wrong", Assert.Throws<InvalidDataException>(() => AwsEventStream.Decode(preludeHit)).Message);
        Assert.StartsWith("it says it is", Assert.Throws<InvalidDataException>(() => AwsEventStream.Decode(good.AsSpan(0, good.Length - 1))).Message);
        Assert.Contains("too short", Assert.Throws<InvalidDataException>(() => AwsEventStream.Decode(good.AsSpan(0, 10))).Message);
    }

    private static EventStreamHeader Header(string name, Action<EventStreamHeader> set)
    {
        var header = new EventStreamHeader(name);
        set(header);
        return header;
    }
}
