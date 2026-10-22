/**
 * A spoken question as the API takes it: 16 kHz mono 16-bit PCM WAV, made in
 * the browser.
 *
 * MediaRecorder would hand back webm, ogg or mp4 depending on the browser, and
 * the API reads WAV only: it sends the sound on to Amazon Transcribe as raw
 * 16 kHz PCM (or, behind a setting, the WAV itself to Voxtral on Bedrock).
 * So the page captures raw samples with the Web Audio API (micRecorder.ts) and
 * these pure functions make the file: the channels mixed down to one, the rate
 * taken to 16 kHz, each sample clipped to 16 bits, and the 44-byte header in
 * front. Sixteen kilohertz is what speech models are trained on and keeps two
 * minutes under 4 MB (32 KB a second).
 */

/** The rate the API is sent: what speech models take. */
export const WAV_RATE = 16_000

/** The canonical PCM header's length. */
export const WAV_HEADER_BYTES = 44

/** The channels of one block averaged into one; a single channel is returned as it is. */
export function downmix(channels: readonly Float32Array[]): Float32Array {
  if (channels.length === 0) return new Float32Array(0)
  if (channels.length === 1) return channels[0]
  const length = Math.min(...channels.map((c) => c.length))
  const mono = new Float32Array(length)
  for (const channel of channels) {
    for (let i = 0; i < length; i++) mono[i] += channel[i]
  }
  for (let i = 0; i < length; i++) mono[i] /= channels.length
  return mono
}

/** Blocks joined end to end. */
export function concat(blocks: readonly Float32Array[]): Float32Array {
  const out = new Float32Array(blocks.reduce((n, b) => n + b.length, 0))
  let at = 0
  for (const b of blocks) {
    out.set(b, at)
    at += b.length
  }
  return out
}

/**
 * `input`, sampled at `fromRate`, as samples at `toRate`. Down (48 kHz to
 * 16 kHz, the usual case), each output sample is the mean of the input samples
 * its span covers: a box filter, crude but enough to keep most of what lies
 * above 8 kHz from folding back into the voice. Up, linear interpolation.
 */
export function resample(input: Float32Array, fromRate: number, toRate: number): Float32Array {
  if (!(fromRate > 0) || !(toRate > 0)) throw new RangeError(`sample rates must be positive (${fromRate} → ${toRate})`)
  if (fromRate === toRate || input.length === 0) return input.slice()
  const ratio = fromRate / toRate
  const length = Math.round(input.length / ratio)
  const out = new Float32Array(length)
  if (ratio > 1) {
    for (let i = 0; i < length; i++) {
      const start = Math.floor(i * ratio)
      const end = Math.min(input.length, Math.max(start + 1, Math.floor((i + 1) * ratio)))
      let sum = 0
      for (let j = start; j < end; j++) sum += input[j]
      out[i] = sum / (end - start)
    }
  } else {
    const last = input.length - 1
    for (let i = 0; i < length; i++) {
      const at = i * ratio
      const i0 = Math.min(last, Math.floor(at))
      const i1 = Math.min(last, i0 + 1)
      const frac = at - i0
      out[i] = input[i0] * (1 - frac) + input[i1] * frac
    }
  }
  return out
}

/** Floats in [-1, 1] as 16-bit samples. Anything louder is clipped, never wrapped; a NaN is silence. */
export function toPcm16(samples: Float32Array): Int16Array {
  const out = new Int16Array(samples.length)
  for (let i = 0; i < samples.length; i++) {
    const s = Number.isNaN(samples[i]) ? 0 : Math.max(-1, Math.min(1, samples[i]))
    out[i] = s < 0 ? Math.round(s * 0x8000) : Math.round(s * 0x7fff)
  }
  return out
}

/** 16-bit mono samples as a WAV file's bytes: the 44-byte RIFF header, then the samples, little-endian. */
export function encodeWav(samples: Int16Array, sampleRate: number = WAV_RATE): ArrayBuffer {
  const data = samples.length * 2
  const buffer = new ArrayBuffer(WAV_HEADER_BYTES + data)
  const view = new DataView(buffer)
  const ascii = (at: number, text: string) => {
    for (let i = 0; i < text.length; i++) view.setUint8(at + i, text.charCodeAt(i))
  }
  ascii(0, 'RIFF')
  view.setUint32(4, 36 + data, true)
  ascii(8, 'WAVE')
  // The fmt chunk: its size, PCM, mono, the rate, bytes a second, bytes a frame, bits a sample.
  ascii(12, 'fmt ')
  view.setUint32(16, 16, true)
  view.setUint16(20, 1, true)
  view.setUint16(22, 1, true)
  view.setUint32(24, sampleRate, true)
  view.setUint32(28, sampleRate * 2, true)
  view.setUint16(32, 2, true)
  view.setUint16(34, 16, true)
  ascii(36, 'data')
  view.setUint32(40, data, true)
  for (let i = 0; i < samples.length; i++) view.setInt16(WAV_HEADER_BYTES + i * 2, samples[i], true)
  return buffer
}

/** A recording's mono blocks, captured at `inputRate`, as the WAV the API takes. */
export function recordingToWav(blocks: readonly Float32Array[], inputRate: number): ArrayBuffer {
  return encodeWav(toPcm16(resample(concat(blocks), inputRate, WAV_RATE)), WAV_RATE)
}

/** Seconds of sound in a WAV made by encodeWav (0 for anything shorter than its header). */
export function wavSeconds(wav: ArrayBuffer): number {
  if (wav.byteLength < WAV_HEADER_BYTES) return 0
  const view = new DataView(wav)
  const byteRate = view.getUint32(28, true)
  return byteRate > 0 ? view.getUint32(40, true) / byteRate : 0
}

/**
 * A block's loudness for a level meter: 0 at -60 dBFS or quieter, 1 at full
 * scale, from its RMS. Decibels, because speech spends most of its time far
 * below full scale and a linear meter barely moves.
 */
export function level(samples: Float32Array): number {
  if (samples.length === 0) return 0
  let sum = 0
  for (let i = 0; i < samples.length; i++) sum += samples[i] * samples[i]
  const rms = Math.sqrt(sum / samples.length)
  if (!(rms > 0)) return 0
  const db = 20 * Math.log10(rms)
  return Math.max(0, Math.min(1, (db + 60) / 60))
}
