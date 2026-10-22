using System.Buffers.Binary;
using System.Text;

namespace AlgoTrading.Infrastructure.Ai;

/// <summary>
/// AWS's event-stream encoding, the framing Amazon Transcribe streaming speaks over its WebSocket
/// (<see cref="AmazonTranscribeClient"/>): one message is a prelude (its total length and its headers' length,
/// big-endian), the prelude's CRC32, the headers, the payload, and the whole message's CRC32. Both checksums are the
/// gzip CRC32 (<see cref="Crc32"/>); 16 bytes of every message are overhead.
/// </summary>
/// <remarks>
/// Written from AWS's own description ("Event stream encoding", Amazon Transcribe developer guide), and tested against
/// the frame that page prints. The desk only ever writes string headers; it reads every header type the format has,
/// since AWS's frames carry a timestamp and a byte array too.
/// </remarks>
public static class AwsEventStream
{
    /// <summary>The prelude (8 bytes), its CRC (4) and the message CRC (4).</summary>
    public const int Overhead = 16;

    /// <summary>The largest message read: Transcribe's answers are a few kilobytes of JSON.</summary>
    public const int MaxMessageBytes = 1024 * 1024;

    private const byte StringType = 7;

    /// <summary>The headers every audio frame carries, as Transcribe's WebSocket guide lists them.</summary>
    private static readonly (string Name, string Value)[] AudioHeaders =
    [
        (":content-type", "application/octet-stream"),
        (":event-type", "AudioEvent"),
        (":message-type", "event"),
    ];

    /// <summary>One <c>AudioEvent</c> carrying <paramref name="chunk"/>; an empty chunk ends the stream.</summary>
    public static byte[] AudioEvent(ReadOnlySpan<byte> chunk) => Encode(AudioHeaders, chunk);

    /// <summary>A message with <paramref name="headers"/> (each a string) and <paramref name="payload"/>.</summary>
    public static byte[] Encode(IReadOnlyList<(string Name, string Value)> headers, ReadOnlySpan<byte> payload)
    {
        int headerBytes = 0;
        foreach (var (name, value) in headers)
        {
            int nameBytes = Encoding.UTF8.GetByteCount(name);
            int valueBytes = Encoding.UTF8.GetByteCount(value);
            if (nameBytes is 0 or > byte.MaxValue) throw new ArgumentException($"a header name is 1 to 255 bytes: {name}", nameof(headers));
            if (valueBytes > ushort.MaxValue) throw new ArgumentException($"header {name}'s value is over 65,535 bytes", nameof(headers));
            headerBytes += 1 + nameBytes + 1 + 2 + valueBytes;
        }

        int total = Overhead + headerBytes + payload.Length;
        var message = new byte[total];
        var span = message.AsSpan();
        BinaryPrimitives.WriteInt32BigEndian(span, total);
        BinaryPrimitives.WriteInt32BigEndian(span[4..], headerBytes);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], Crc32.Compute(span[..8]));

        int at = 12;
        foreach (var (name, value) in headers)
        {
            int nameBytes = Encoding.UTF8.GetBytes(name, span[(at + 1)..]);
            span[at] = (byte)nameBytes;
            at += 1 + nameBytes;
            span[at++] = StringType;
            int valueBytes = Encoding.UTF8.GetBytes(value, span[(at + 2)..]);
            BinaryPrimitives.WriteUInt16BigEndian(span[at..], (ushort)valueBytes);
            at += 2 + valueBytes;
        }

        payload.CopyTo(span[at..]);
        BinaryPrimitives.WriteUInt32BigEndian(span[(total - 4)..], Crc32.Compute(span[..(total - 4)]));
        return message;
    }

    /// <summary>
    /// The headers and payload of one whole message, after both checksums are checked. A string header's value is a
    /// string; a timestamp a <see cref="DateTimeOffset"/>; a byte array a <c>byte[]</c>; the others as their .NET
    /// type. Throws <see cref="InvalidDataException"/>, in plain words, for a message that is cut short, damaged or
    /// malformed.
    /// </summary>
    public static AwsEventMessage Decode(ReadOnlySpan<byte> message)
    {
        if (message.Length < Overhead) throw new InvalidDataException($"an event-stream message of {message.Length} bytes is too short");
        int total = BinaryPrimitives.ReadInt32BigEndian(message);
        int headerBytes = BinaryPrimitives.ReadInt32BigEndian(message[4..]);
        if (BinaryPrimitives.ReadUInt32BigEndian(message[8..]) != Crc32.Compute(message[..8])) throw new InvalidDataException("its prelude checksum is wrong");
        if (total != message.Length) throw new InvalidDataException($"it says it is {total} bytes but {message.Length} came");
        if (headerBytes < 0 || headerBytes > total - Overhead) throw new InvalidDataException("its headers' length makes no sense");
        if (BinaryPrimitives.ReadUInt32BigEndian(message[(total - 4)..]) != Crc32.Compute(message[..(total - 4)])) throw new InvalidDataException("its message checksum is wrong");

        var headers = new Dictionary<string, object>(StringComparer.Ordinal);
        var block = message.Slice(12, headerBytes);
        int at = 0;
        while (at < block.Length)
        {
            int nameBytes = block[at];
            if (nameBytes == 0 || at + 1 + nameBytes + 1 > block.Length) throw new InvalidDataException("a header is cut short");
            string name = Encoding.UTF8.GetString(block.Slice(at + 1, nameBytes));
            at += 1 + nameBytes;
            byte type = block[at++];
            headers[name] = ReadValue(block, ref at, type, name);
        }

        return new AwsEventMessage(headers, message.Slice(12 + headerBytes, total - Overhead - headerBytes).ToArray());
    }

    private static object ReadValue(ReadOnlySpan<byte> block, ref int at, byte type, string name)
    {
        ReadOnlySpan<byte> Take(ReadOnlySpan<byte> from, ref int i, int count)
        {
            if (i + count > from.Length) throw new InvalidDataException($"header {name} is cut short");
            var bytes = from.Slice(i, count);
            i += count;
            return bytes;
        }

        switch (type)
        {
            case 0: return true;
            case 1: return false;
            case 2: return (sbyte)Take(block, ref at, 1)[0];
            case 3: return BinaryPrimitives.ReadInt16BigEndian(Take(block, ref at, 2));
            case 4: return BinaryPrimitives.ReadInt32BigEndian(Take(block, ref at, 4));
            case 5: return BinaryPrimitives.ReadInt64BigEndian(Take(block, ref at, 8));
            case 6 or StringType:
                int length = BinaryPrimitives.ReadUInt16BigEndian(Take(block, ref at, 2));
                var value = Take(block, ref at, length);
                return type == StringType ? Encoding.UTF8.GetString(value) : value.ToArray();
            case 8: return DateTimeOffset.FromUnixTimeMilliseconds(BinaryPrimitives.ReadInt64BigEndian(Take(block, ref at, 8)));
            case 9: return new Guid(Take(block, ref at, 16), bigEndian: true);
            default: throw new InvalidDataException($"header {name} has an unknown value type ({type})");
        }
    }
}

/// <summary>One event-stream message, read: its headers by name, and its payload.</summary>
public sealed record AwsEventMessage(IReadOnlyDictionary<string, object> Headers, byte[] Payload)
{
    /// <summary>A string header's value; null when it is missing or not a string.</summary>
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value as string : null;
}

/// <summary>
/// CRC-32 as gzip and zlib compute it (polynomial <c>0xEDB88320</c>, reflected, initial and final value all ones):
/// the checksum AWS's event-stream encoding uses for both of its CRCs.
/// </summary>
public static class Crc32
{
    private static readonly uint[] Table = MakeTable();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    private static uint[] MakeTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }

        return table;
    }
}
