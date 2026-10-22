using System.Buffers.Binary;

namespace AlgoTrading.Infrastructure.Ai;

/// <summary>
/// What a WAV file's header says: its format and how much sound follows. Read before a console recording is sent to the
/// audio model, so a file that is not WAV, or runs past the limit, is refused in words without asking any model.
/// </summary>
/// <param name="AudioFormat">1 for PCM; 3 for 32-bit float; <c>0xFFFE</c> for the extensible header.</param>
/// <param name="DataBytes">The <c>data</c> chunk's length, as far as the file actually holds it.</param>
public sealed record WavHeader(int AudioFormat, int Channels, int SampleRate, int BitsPerSample, int ByteRate, int DataBytes)
{
    /// <summary>Seconds of sound: the data's bytes over the bytes a second.</summary>
    public double Seconds => ByteRate > 0 ? (double)DataBytes / ByteRate : 0;

    /// <summary>
    /// How each sample is written: 1 for integer PCM, 3 for IEEE float. The same as <see cref="AudioFormat"/>, except
    /// that an extensible header (<c>0xFFFE</c>) is resolved to the format its sub-format names.
    /// </summary>
    public int SampleFormat { get; init; } = 1;

    /// <summary>Whether the samples are IEEE floats rather than integers.</summary>
    public bool IsFloat => SampleFormat == 3;

    /// <summary>
    /// The header of <paramref name="wav"/>, walking its chunks to <c>fmt </c> and <c>data</c> (a browser may put a
    /// <c>LIST</c> chunk between them). Throws <see cref="InvalidDataException"/>, in plain words, when it is not a WAV
    /// file of sound a model can take.
    /// </summary>
    public static WavHeader Read(ReadOnlySpan<byte> wav) => Read(wav, out _);

    /// <summary>The same, with where the sound starts: the <c>data</c> chunk's first byte.</summary>
    public static WavHeader Read(ReadOnlySpan<byte> wav, out int dataOffset)
    {
        if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav[8..12].SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("not a WAV file");
        }

        int at = 12;
        (int Format, int Channels, int Rate, int ByteRate, int Bits, int Samples)? fmt = null;
        while (at + 8 <= wav.Length)
        {
            var id = wav.Slice(at, 4);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(wav[(at + 4)..]);
            int body = at + 8;
            if (id.SequenceEqual("fmt "u8))
            {
                if (size < 16 || body + 16 > wav.Length) throw new InvalidDataException("its format chunk is cut short");
                int format = BinaryPrimitives.ReadUInt16LittleEndian(wav[body..]);
                // An extensible header names the real format in the first two bytes of its sub-format GUID; one too
                // short to hold it is taken as integer PCM.
                int samples = format != 0xFFFE ? format
                    : size >= 40 && body + 26 <= wav.Length ? BinaryPrimitives.ReadUInt16LittleEndian(wav[(body + 24)..])
                    : 1;
                fmt = (
                    format,
                    BinaryPrimitives.ReadUInt16LittleEndian(wav[(body + 2)..]),
                    BinaryPrimitives.ReadInt32LittleEndian(wav[(body + 4)..]),
                    BinaryPrimitives.ReadInt32LittleEndian(wav[(body + 8)..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(wav[(body + 14)..]),
                    samples);
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (fmt is not { } f) throw new InvalidDataException("its sound comes before its format");
                if (f.Samples is not (1 or 3)) throw new InvalidDataException($"its sound is compressed (format {f.Samples}), not PCM");
                if (f.Channels < 1 || f.Rate < 1 || f.ByteRate < 1 || f.Bits < 8) throw new InvalidDataException("its format chunk makes no sense");
                // A recorder that streams writes the data's size as 0 or as "to the end": what the file holds counts.
                int held = wav.Length - body;
                int data = size == 0 || size > held ? held : (int)size;
                dataOffset = body;
                return new WavHeader(f.Format, f.Channels, f.Rate, f.Bits, f.ByteRate, data) { SampleFormat = f.Samples };
            }

            // Chunks are padded to an even length.
            long next = body + size + (size & 1);
            if (next > wav.Length) break;
            at = (int)next;
        }

        throw new InvalidDataException(fmt is null ? "it has no format chunk" : "it has no sound (no data chunk)");
    }
}
