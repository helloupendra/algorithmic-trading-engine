/**
 * The microphone's raw samples, handed to the page (micRecorder.ts) from the
 * audio thread. A plain .js file served from the console's own origin: the
 * content-security policy lets only same-origin scripts run, a worklet
 * included, so it is imported with ?url&no-inline and never becomes a data:
 * URL.
 *
 * Blocks of 128 frames are gathered into about 1,024 (21 ms at 48 kHz) before
 * each message, so the main thread is not woken 375 times a second. Each
 * message is the block's channels, as Float32Arrays, transferred.
 */

/* global AudioWorkletProcessor, registerProcessor */

const BATCH = 1024

class PcmTap extends AudioWorkletProcessor {
  constructor() {
    super()
    this.parts = []
    this.frames = 0
  }

  process(inputs) {
    const input = inputs[0]
    if (input && input.length > 0 && input[0].length > 0) {
      this.parts.push(input.map((channel) => channel.slice(0)))
      this.frames += input[0].length
      if (this.frames >= BATCH) this.flush()
    }
    return true
  }

  flush() {
    const channels = this.parts[0].length
    const out = []
    for (let c = 0; c < channels; c++) {
      const joined = new Float32Array(this.frames)
      let at = 0
      for (const part of this.parts) {
        const channel = part[c] ?? part[0]
        joined.set(channel, at)
        at += channel.length
      }
      out.push(joined)
    }
    this.parts = []
    this.frames = 0
    this.port.postMessage(out, out.map((c) => c.buffer))
  }
}

registerProcessor('openfno-pcm-tap', PcmTap)
