using System.Buffers.Binary;
using Concentus;

namespace AlgoTrading.Infrastructure.Ai;

/// <summary>
/// Sound as Amazon Transcribe streaming takes it (<see cref="AmazonTranscribeClient"/>): raw 16 kHz mono signed 16-bit
/// little-endian PCM, no header. Made from any PCM WAV: a console recording (already 16 kHz mono 16-bit), a Telegram
/// voice note once <see cref="OggOpusDecoder"/> has made it WAV, or a WAV audio file at any rate, width or channel count.
/// </summary>
/// <remarks>
/// The channels are averaged into one and the rate is changed by the Speex resampler Concentus carries, the one the
/// voice notes already go through. Nothing is written to disk and no process is started.
/// </remarks>
public static class PcmAudio
{
    public const int SampleRate = 16_000;

    /// <summary>Bytes of PCM a second: 16,000 samples of two bytes.</summary>
    public const int BytesPerSecond = SampleRate * 2;

    /// <summary>
    /// The sound of <paramref name="wav"/> as 16 kHz mono 16-bit PCM bytes. Throws <see cref="InvalidDataException"/>,
    /// in plain words, when it is not a PCM WAV the desk can read, holds no sound, or runs past
    /// <paramref name="maxSeconds"/>.
    /// </summary>
    public static byte[] FromWav(byte[] wav, int maxSeconds)
    {
        var header = WavHeader.Read(wav, out int offset);
        if (header.Seconds > maxSeconds) throw new InvalidDataException($"longer than {maxSeconds} s");

        int width = header.BitsPerSample / 8;
        bool readable = header.BitsPerSample % 8 == 0 && (header.IsFloat ? width is 4 or 8 : width is 1 or 2 or 3 or 4);
        if (!readable) throw new InvalidDataException($"its sound is {header.BitsPerSample}-bit{(header.IsFloat ? " float" : string.Empty)}, which the desk cannot read");

        int frame = width * header.Channels;
        int frames = header.DataBytes / frame;
        if (frames == 0) throw new InvalidDataException("no sound in it");

        var data = wav.AsSpan(offset, frames * frame);
        if (header.SampleRate == SampleRate && header.Channels == 1 && width == 2 && !header.IsFloat) return data.ToArray();

        var mono = new short[frames];
        for (int i = 0; i < frames; i++)
        {
            double sum = 0;
            for (int c = 0; c < header.Channels; c++) sum += Sample(data.Slice(i * frame + c * width, width), header.IsFloat);
            mono[i] = ToShort(sum / header.Channels);
        }

        return Bytes(Resample(mono, header.SampleRate, SampleRate));
    }

    /// <summary>Seconds of sound in PCM made by <see cref="FromWav"/>.</summary>
    public static double Seconds(ReadOnlySpan<byte> pcm) => (double)pcm.Length / BytesPerSecond;

    /// <summary>
    /// Mono samples at <paramref name="fromRate"/> as samples at <paramref name="toRate"/>, through the Speex resampler at
    /// its default quality (5); the same samples when the rates match.
    /// </summary>
    public static short[] Resample(short[] input, int fromRate, int toRate)
    {
        if (fromRate == toRate || input.Length == 0) return input;
        var resampler = ResamplerFactory.CreateResampler(1, fromRate, toRate, 5, null);
        var output = new short[(int)((long)input.Length * toRate / fromRate) + 1024];
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

    /// <summary>16-bit samples as little-endian bytes.</summary>
    public static byte[] Bytes(ReadOnlySpan<short> samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), samples[i]);
        return bytes;
    }

    /// <summary>One sample, from -1 to 1: 8-bit WAV is unsigned, wider integers signed, floats as they are.</summary>
    private static double Sample(ReadOnlySpan<byte> bytes, bool isFloat) => (bytes.Length, isFloat) switch
    {
        (4, true) => BinaryPrimitives.ReadSingleLittleEndian(bytes),
        (8, true) => BinaryPrimitives.ReadDoubleLittleEndian(bytes),
        (1, _) => (bytes[0] - 128) / 128.0,
        (2, _) => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768.0,
        (3, _) => ((bytes[0] << 8 | bytes[1] << 16 | bytes[2] << 24) >> 8) / 8388608.0,
        _ => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648.0,
    };

    private static short ToShort(double sample) =>
        double.IsNaN(sample) ? (short)0 : (short)Math.Clamp(Math.Round(sample * 32768.0), short.MinValue, short.MaxValue);
}
