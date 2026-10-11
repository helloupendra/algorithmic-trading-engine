using System.Buffers.Binary;
using Concentus;
using Concentus.Oggfile;

namespace AlgoTrading.Infrastructure.Ai;

/// <summary>
/// A Telegram voice note (OGG/Opus) as 16 kHz mono 16-bit PCM WAV, the form Bedrock's audio model takes: its endpoint
/// refused <c>ogg</c> with a 400 and took <c>wav</c> and <c>mp3</c> (11 Oct 2026).
/// </summary>
/// <remarks>
/// <para>
/// Decoded in-process by Concentus, a managed port of the Opus reference decoder, with Concentus.Oggfile reading the
/// Ogg pages: the server has no ffmpeg, and nothing here starts a process. Nothing is written to disk.
/// </para>
/// <para>
/// The note is decoded at Opus's own 48 kHz (mono, whatever it was recorded in) and then resampled to 16 kHz with the
/// Speex resampler Concentus carries. Asked for 16 kHz directly, Concentus 2.2.2 returned silence for a hybrid-mode
/// stream, which is what a phone's 48 kHz voice note at about 24 kb/s is (found by this class's test, 11 Oct 2026).
/// </para>
/// </remarks>
public static class OggOpusDecoder
{
    public const int SampleRate = 16_000;

    /// <summary>What Opus is decoded at before it is resampled: its own full rate.</summary>
    private const int DecodeRate = 48_000;

    static OggOpusDecoder()
    {
        // The same managed decoder on the server as in the tests, never a native libopus the machine happens to have.
        OpusCodecFactory.AttemptToUseNativeLibrary = false;
    }

    /// <summary>
    /// <paramref name="ogg"/> decoded to a WAV file's bytes. Throws <see cref="InvalidDataException"/> when it is not
    /// Ogg/Opus, holds no sound, or runs past <paramref name="maxSeconds"/>.
    /// </summary>
    public static byte[] ToWav(byte[] ogg, int maxSeconds) => Wav(Resample(Decode(ogg, maxSeconds)), SampleRate);

    /// <summary>The note's samples at <see cref="DecodeRate"/>, mono.</summary>
    private static short[] Decode(byte[] ogg, int maxSeconds)
    {
        var decoder = OpusCodecFactory.CreateDecoder(DecodeRate, 1, null);
        OpusOggReadStream reader;
        try
        {
            reader = new OpusOggReadStream(decoder, new MemoryStream(ogg, writable: false));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidDataException($"not an Ogg/Opus file ({ex.GetType().Name})", ex);
        }

        if (!reader.HasNextPacket) throw new InvalidDataException($"not an Ogg/Opus file ({reader.LastError ?? "no packets"})");

        long limit = (long)maxSeconds * DecodeRate;
        var pcm = new List<short>(DecodeRate * 10);
        try
        {
            while (reader.HasNextPacket)
            {
                // A header page or a lost packet decodes to nothing: skipped.
                short[]? samples = reader.DecodeNextPacket();
                if (samples is null) continue;
                pcm.AddRange(samples);
                if (pcm.Count > limit) throw new InvalidDataException($"longer than {maxSeconds} s");
            }
        }
        catch (Exception ex) when (ex is not InvalidDataException and not OutOfMemoryException)
        {
            throw new InvalidDataException($"the Opus audio could not be decoded ({ex.GetType().Name})", ex);
        }

        if (pcm.Count == 0) throw new InvalidDataException("no sound in it");
        return pcm.ToArray();
    }

    /// <summary>48 kHz samples as 16 kHz ones, through the Speex resampler at its default quality (5).</summary>
    private static short[] Resample(short[] input)
    {
        var resampler = ResamplerFactory.CreateResampler(1, DecodeRate, SampleRate, 5, null);
        var output = new short[input.Length / (DecodeRate / SampleRate) + 1024];
        int read = 0, written = 0;
        while (read < input.Length && written < output.Length)
        {
            int inLength = input.Length - read;
            int outLength = output.Length - written;
            resampler.Process(0, input.AsSpan(read), ref inLength, output.AsSpan(written), ref outLength);
            if (inLength == 0 && outLength == 0) break;
            read += inLength;
            written += outLength;
        }

        return output[..written];
    }

    /// <summary>16-bit mono PCM samples as a WAV file: the 44-byte RIFF header, then the samples, little-endian.</summary>
    public static byte[] Wav(ReadOnlySpan<short> samples, int sampleRate)
    {
        int data = samples.Length * 2;
        var wav = new byte[44 + data];
        var span = wav.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + data);
        "WAVE"u8.CopyTo(span[8..]);
        // The fmt chunk: its size, PCM, mono, the rate, bytes a second, bytes a frame, bits a sample.
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], sampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], data);
        for (int i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(span[(44 + i * 2)..], samples[i]);
        }

        return wav;
    }

    /// <summary>Seconds of sound in a WAV file made by <see cref="Wav"/>.</summary>
    public static double Seconds(byte[] wav) =>
        wav.Length < 44 ? 0 : (double)BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)) / 2 / BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24));
}
