import { describe, expect, it } from 'vitest'
import { WAV_HEADER_BYTES, WAV_RATE, concat, downmix, encodeWav, level, recordingToWav, resample, toPcm16, wavSeconds } from './wav'

const ascii = (view: DataView, at: number, n: number) =>
  Array.from({ length: n }, (_, i) => String.fromCharCode(view.getUint8(at + i))).join('')

/** A sine of `hz` at `rate`, `seconds` long, at `amplitude` of full scale. */
function tone(hz: number, rate: number, seconds: number, amplitude = 0.5): Float32Array {
  const out = new Float32Array(Math.round(rate * seconds))
  for (let i = 0; i < out.length; i++) out[i] = amplitude * Math.sin((2 * Math.PI * hz * i) / rate)
  return out
}

/** How strongly `hz` is in `samples` (its amplitude, by one DFT bin). */
function strength(samples: ArrayLike<number>, hz: number, rate: number): number {
  let re = 0
  let im = 0
  for (let i = 0; i < samples.length; i++) {
    re += samples[i] * Math.cos((2 * Math.PI * hz * i) / rate)
    im += samples[i] * Math.sin((2 * Math.PI * hz * i) / rate)
  }
  return (2 * Math.hypot(re, im)) / samples.length
}

describe('encodeWav', () => {
  it('writes the 44-byte PCM header the API reads: RIFF/WAVE, mono, 16 kHz, 16-bit, and the data size', () => {
    const wav = encodeWav(new Int16Array([1, -1, 32767, -32768]), WAV_RATE)
    const view = new DataView(wav)

    expect(wav.byteLength).toBe(WAV_HEADER_BYTES + 8)
    expect(ascii(view, 0, 4)).toBe('RIFF')
    expect(view.getUint32(4, true)).toBe(36 + 8)
    expect(ascii(view, 8, 4)).toBe('WAVE')
    expect(ascii(view, 12, 4)).toBe('fmt ')
    expect(view.getUint32(16, true)).toBe(16)
    expect(view.getUint16(20, true)).toBe(1) // PCM
    expect(view.getUint16(22, true)).toBe(1) // mono
    expect(view.getUint32(24, true)).toBe(16_000)
    expect(view.getUint32(28, true)).toBe(32_000) // bytes a second
    expect(view.getUint16(32, true)).toBe(2) // bytes a frame
    expect(view.getUint16(34, true)).toBe(16)
    expect(ascii(view, 36, 4)).toBe('data')
    expect(view.getUint32(40, true)).toBe(8)
    expect([0, 1, 2, 3].map((i) => view.getInt16(44 + i * 2, true))).toEqual([1, -1, 32767, -32768])
  })

  it('makes an empty file a valid one, with no sound', () => {
    const wav = encodeWav(new Int16Array(0))
    expect(wav.byteLength).toBe(44)
    expect(wavSeconds(wav)).toBe(0)
  })
})

describe('toPcm16', () => {
  it('clips anything louder than full scale instead of wrapping it, and reads NaN as silence', () => {
    expect(Array.from(toPcm16(new Float32Array([2, -2, 1, -1, 0, 0.5, -0.5, NaN])))).toEqual([
      32767, -32768, 32767, -32768, 0, 16384, -16384, 0,
    ])
  })
})

describe('downmix and concat', () => {
  it('averages the channels into one', () => {
    expect(Array.from(downmix([new Float32Array([1, 0.5, -1]), new Float32Array([0, 0.5, 1])]))).toEqual([0.5, 0.5, 0])
  })

  it('returns a single channel as it is, and nothing for no channels', () => {
    const mono = new Float32Array([0.25])
    expect(downmix([mono])).toBe(mono)
    expect(downmix([]).length).toBe(0)
  })

  it('joins blocks end to end', () => {
    expect(Array.from(concat([new Float32Array([1, 2]), new Float32Array([]), new Float32Array([3])]))).toEqual([1, 2, 3])
  })
})

describe('resample', () => {
  it('takes 48 kHz to 16 kHz with the length a third and the voice kept', () => {
    const input = tone(440, 48_000, 1)
    const out = resample(input, 48_000, 16_000)

    expect(out.length).toBe(16_000)
    expect(strength(out, 440, 16_000)).toBeCloseTo(0.5, 1)
  })

  it('takes 44.1 kHz to 16 kHz', () => {
    const out = resample(tone(300, 44_100, 2), 44_100, 16_000)
    expect(out.length).toBe(32_000)
    expect(strength(out, 300, 16_000)).toBeCloseTo(0.5, 1)
  })

  it('keeps much of a tone above 8 kHz from folding back into the voice', () => {
    // 15 kHz at 48 kHz would alias to 1 kHz at 16 kHz; the box filter keeps most of it out.
    const out = resample(tone(15_000, 48_000, 1), 48_000, 16_000)
    expect(strength(out, 1_000, 16_000)).toBeLessThan(0.2)
  })

  it('interpolates up, and copies at the same rate', () => {
    const up = resample(new Float32Array([0, 1]), 8_000, 16_000)
    expect(Array.from(up)).toEqual([0, 0.5, 1, 1])
    const same = new Float32Array([0.1, 0.2])
    expect(resample(same, 16_000, 16_000)).not.toBe(same)
    expect(Array.from(resample(same, 16_000, 16_000))).toEqual(Array.from(same))
  })

  it('refuses a rate that makes no sense', () => {
    expect(() => resample(new Float32Array(4), 0, 16_000)).toThrow(RangeError)
  })
})

describe('recordingToWav', () => {
  it('turns two seconds of 48 kHz blocks into two seconds of 16 kHz WAV, 64,044 bytes', () => {
    const all = tone(440, 48_000, 2)
    const blocks = [all.slice(0, 1000), all.slice(1000, 50_000), all.slice(50_000)]

    const wav = recordingToWav(blocks, 48_000)

    expect(wav.byteLength).toBe(44 + 2 * 32_000)
    expect(wavSeconds(wav)).toBe(2)
    expect(new DataView(wav).getUint32(24, true)).toBe(16_000)
    const samples = new Int16Array(wav.slice(44))
    expect(strength(samples, 440, 16_000) / 32767).toBeCloseTo(0.5, 1)
  })

  it('keeps two minutes under the console limit of 4 MB', () => {
    expect(44 + 120 * 32_000).toBeLessThan(4 * 1024 * 1024)
  })
})

describe('level', () => {
  it('reads silence as 0, full scale as 1, and speech-like levels in between on a decibel scale', () => {
    expect(level(new Float32Array(128))).toBe(0)
    expect(level(new Float32Array(0))).toBe(0)
    expect(level(new Float32Array(128).fill(1))).toBe(1)
    // -20 dBFS: two thirds of the way up a 60 dB meter.
    expect(level(new Float32Array(128).fill(0.1))).toBeCloseTo(2 / 3, 5)
    expect(level(new Float32Array(128).fill(0.0001))).toBe(0)
  })
})
