using System.Buffers.Binary;
using AlgoTrading.Infrastructure.Ai;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Any PCM WAV as Amazon Transcribe streaming takes it: 16 kHz mono signed 16-bit little-endian, no header. The
/// recordings are tones made in the test, so what comes out is checked for its length and for the tone still in it.
/// </summary>
public sealed class PcmAudioTests
{
    [Fact]
    public void The_consoles_own_16_khz_mono_wav_is_sent_as_its_samples_unchanged()
    {
        var samples = Tone(16_000, seconds: 1.5);
        byte[] wav = OggOpusDecoder.Wav(samples, 16_000);

        byte[] pcm = PcmAudio.FromWav(wav, maxSeconds: 120);

        Assert.Equal(wav[44..], pcm);
        Assert.Equal(1.5, PcmAudio.Seconds(pcm));
    }

    [Theory]
    [InlineData(44_100, 2, 16, 1)]
    [InlineData(48_000, 1, 16, 1)]
    [InlineData(48_000, 2, 32, 3)]
    [InlineData(22_050, 1, 24, 1)]
    [InlineData(8_000, 1, 8, 1)]
    [InlineData(16_000, 2, 32, 1)]
    [InlineData(32_000, 1, 64, 3)]
    public void A_wav_at_any_rate_width_or_channel_count_becomes_16_khz_mono_with_its_sound(int rate, int channels, int bits, int format)
    {
        byte[] wav = Wav(rate, channels, bits, format, seconds: 1);

        byte[] pcm = PcmAudio.FromWav(wav, maxSeconds: 120);

        Assert.InRange(PcmAudio.Seconds(pcm), 0.95, 1.01);
        Assert.InRange(Tone440(pcm), 6000, 9000);
    }

    [Fact]
    public void An_extensible_header_is_read_by_the_format_it_names()
    {
        byte[] floats = Wav(48_000, 2, 32, 3, seconds: 1, extensible: true);
        byte[] mp3Inside = Wav(48_000, 2, 16, 0x55, seconds: 1, extensible: true);

        var header = WavHeader.Read(floats);

        Assert.Equal((0xFFFE, 3, true), (header.AudioFormat, header.SampleFormat, header.IsFloat));
        Assert.InRange(Tone440(PcmAudio.FromWav(floats, 120)), 6000, 9000);
        Assert.Equal("its sound is compressed (format 85), not PCM", Assert.Throws<InvalidDataException>(() => PcmAudio.FromWav(mp3Inside, 120)).Message);
    }

    [Fact]
    public void A_voice_note_reaches_transcribe_as_the_samples_the_decoder_made()
    {
        byte[] wav = OggOpusDecoder.Wav(Tone(16_000, seconds: 1), 16_000);
        // A LIST chunk before the data, as some recorders write: the sound is found past it.
        byte[] listed = [.. wav[..36], .. "LIST"u8, 4, 0, 0, 0, .. "INFO"u8, .. wav[36..]];

        Assert.Equal(wav[44..], PcmAudio.FromWav(listed, 120));
        WavHeader.Read(listed, out int offset);
        Assert.Equal(56, offset);
    }

    [Fact]
    public void Too_long_empty_or_unreadable_sound_is_refused_in_words()
    {
        Assert.Equal("longer than 2 s", Assert.Throws<InvalidDataException>(() => PcmAudio.FromWav(OggOpusDecoder.Wav(Tone(16_000, 3), 16_000), 2)).Message);
        Assert.Equal("no sound in it", Assert.Throws<InvalidDataException>(() => PcmAudio.FromWav(OggOpusDecoder.Wav([], 16_000), 2)).Message);
        Assert.Equal("its sound is 12-bit, which the desk cannot read",
            Assert.Throws<InvalidDataException>(() => PcmAudio.FromWav(Wav(16_000, 1, 12, 1, seconds: 1), 2)).Message);
        Assert.Equal("not a WAV file", Assert.Throws<InvalidDataException>(() => PcmAudio.FromWav("ID3 an mp3"u8.ToArray(), 2)).Message);
    }

    [Fact]
    public void The_resampler_keeps_the_length_in_time_both_ways()
    {
        Assert.InRange(PcmAudio.Resample(Tone(48_000, 2), 48_000, 16_000).Length, 31_500, 32_000);
        Assert.InRange(PcmAudio.Resample(Tone(8_000, 2), 8_000, 16_000).Length, 31_500, 32_000);
        var same = Tone(16_000, 1);
        Assert.Same(same, PcmAudio.Resample(same, 16_000, 16_000));
    }

    /// <summary>A 440 Hz tone of amplitude 8,000 (of 32,767).</summary>
    private static short[] Tone(int rate, double seconds)
    {
        var samples = new short[(int)(rate * seconds)];
        for (int i = 0; i < samples.Length; i++) samples[i] = (short)(8000 * Math.Sin(2 * Math.PI * 440 * i / rate));
        return samples;
    }

    /// <summary>The same tone as a WAV file of any rate, channel count, width and format (1 integer, 3 float, or another).</summary>
    private static byte[] Wav(int rate, int channels, int bits, int format, double seconds, bool extensible = false)
    {
        int width = (bits + 7) / 8;
        int frames = (int)(rate * seconds);
        var data = new byte[frames * channels * width];
        for (int i = 0; i < frames; i++)
        {
            double v = 8000.0 / 32768 * Math.Sin(2 * Math.PI * 440 * i / rate);
            for (int c = 0; c < channels; c++)
            {
                var at = data.AsSpan((i * channels + c) * width, width);
                if (format == 3 && bits == 32) BinaryPrimitives.WriteSingleLittleEndian(at, (float)v);
                else if (format == 3) BinaryPrimitives.WriteDoubleLittleEndian(at, v);
                else if (bits == 8) at[0] = (byte)(128 + Math.Round(v * 127));
                else if (bits == 24)
                {
                    int s = (int)Math.Round(v * 8388607);
                    at[0] = (byte)s;
                    at[1] = (byte)(s >> 8);
                    at[2] = (byte)(s >> 16);
                }
                else if (bits == 32) BinaryPrimitives.WriteInt32LittleEndian(at, (int)Math.Round(v * int.MaxValue));
                else BinaryPrimitives.WriteInt16LittleEndian(at, (short)Math.Round(v * 32767));
            }
        }

        int fmtSize = extensible ? 40 : 16;
        var wav = new byte[12 + 8 + fmtSize + 8 + data.Length];
        var span = wav.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], wav.Length - 8);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], fmtSize);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..], (ushort)(extensible ? 0xFFFE : format));
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], rate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], rate * channels * width);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], (short)(channels * width));
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], (short)bits);
        if (extensible)
        {
            BinaryPrimitives.WriteInt16LittleEndian(span[36..], 22);
            BinaryPrimitives.WriteInt16LittleEndian(span[38..], (short)bits);
            BinaryPrimitives.WriteInt32LittleEndian(span[40..], 0);
            // The sub-format GUID: its first two bytes are the format; the rest is the fixed KSDATAFORMAT tail.
            BinaryPrimitives.WriteUInt16LittleEndian(span[44..], (ushort)format);
            byte[] tail = [0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71];
            tail.CopyTo(span[46..]);
        }

        int dataAt = 20 + fmtSize;
        "data"u8.CopyTo(span[dataAt..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[(dataAt + 4)..], data.Length);
        data.CopyTo(span[(dataAt + 8)..]);
        return wav;
    }

    /// <summary>The amplitude of the 440 Hz tone in 16 kHz PCM bytes: sound, not silence.</summary>
    private static double Tone440(byte[] pcm)
    {
        int n = pcm.Length / 2;
        double re = 0, im = 0;
        for (int i = 0; i < n; i++)
        {
            short sample = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2));
            re += sample * Math.Cos(2 * Math.PI * 440 * i / 16_000.0);
            im += sample * Math.Sin(2 * Math.PI * 440 * i / 16_000.0);
        }

        return 2 * Math.Sqrt(re * re + im * im) / n;
    }
}
