/**
 * The microphone, as raw samples from the Web Audio API: a recording handed
 * back as the 16 kHz mono WAV the API takes (startMic, wav.ts), or an open
 * stream of blocks for hands-free listening (openMic, vad.ts).
 *
 * Not MediaRecorder: it records webm, ogg or mp4 depending on the browser, and
 * the audio model takes WAV or MP3 only. The samples come from an
 * AudioWorklet (pcmTap.worklet.js) on the audio thread; where a browser has no
 * AudioWorklet, or will not load it, the deprecated ScriptProcessor does the
 * same job on the main thread. Either way the graph ends in a muted gain, so
 * the browser keeps pulling samples and nothing is played back.
 *
 * The browser is asked for echo cancellation, noise suppression and automatic
 * gain: the first keeps a spoken reply coming out of the speakers from being
 * heard as the owner (hands-free also pauses while it plays).
 *
 * Browser-only: getUserMedia, AudioContext and a real microphone, which no
 * unit test has. The encoding and the listening it hands over to are tested
 * (wav.test.ts, vad.test.ts).
 */

import workletUrl from './pcmTap.worklet.js?url&no-inline'
import { MicUnavailable } from './voice'
import { downmix, level, recordingToWav } from './wav'

/** One recording under way. */
export interface MicTake {
  /** Seconds captured so far. */
  readonly seconds: number
  /** The latest block's loudness, 0 to 1 (wav.ts level). */
  readonly level: number
  /** Stops the microphone and returns what was said, as WAV. */
  finish(): ArrayBuffer
  /** Stops the microphone and throws the recording away. */
  cancel(): void
}

/** The microphone held open, handing each block of mono samples to its listener. */
export interface MicStream {
  /** The device's rate, which the blocks are at. */
  readonly sampleRate: number
  /** The browser holds the audio back until a click or a key press: the stream was opened without one (a reload with hands-free on). */
  readonly suspended: boolean
  /** Tries to let the audio through; true once it flows. Call it from a click or a key press. */
  resume(): Promise<boolean>
  /** Lets go of the microphone. */
  close(): void
}

type WebkitWindow = Window & { webkitAudioContext?: typeof AudioContext }

/**
 * A suspended context resumed, waiting `ms` at most: without a click or key
 * press behind it, resume() may not settle until there is one.
 */
async function resumeSoon(context: AudioContext, ms = 400): Promise<void> {
  if (context.state !== 'suspended') return
  await Promise.race([context.resume().catch(() => undefined), new Promise((r) => window.setTimeout(r, ms))])
}

/**
 * Asks for the microphone (the browser may ask the viewer first) and hands
 * each block it hears to `onBlock`, mono, at the device's rate, until close.
 * Rejects with the browser's own error (NotAllowedError, NotFoundError, …) or
 * MicUnavailable; micErrorText words each.
 */
export async function openMic(onBlock: (mono: Float32Array) => void): Promise<MicStream> {
  const media = typeof navigator === 'undefined' ? undefined : navigator.mediaDevices
  if (!media?.getUserMedia) throw new MicUnavailable()
  const stream = await media.getUserMedia({
    audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true },
  })

  const Context = window.AudioContext ?? (window as WebkitWindow).webkitAudioContext
  let context: AudioContext
  try {
    if (!Context) throw new MicUnavailable()
    // The device's own rate: Firefox will not join a microphone to a context at another rate. wav.ts resamples.
    context = new Context()
  } catch (error) {
    stream.getTracks().forEach((t) => t.stop())
    throw error
  }

  let closed = false
  const take = (channels: Float32Array[]) => {
    if (closed || channels.length === 0) return
    onBlock(downmix(channels))
  }

  const source = context.createMediaStreamSource(stream)
  const mute = context.createGain()
  mute.gain.value = 0
  mute.connect(context.destination)
  let tap: AudioNode
  try {
    await context.audioWorklet.addModule(workletUrl)
    const node = new AudioWorkletNode(context, 'openfno-pcm-tap', { numberOfInputs: 1, numberOfOutputs: 1, outputChannelCount: [1] })
    node.port.onmessage = (e: MessageEvent<Float32Array[]>) => take(e.data)
    tap = node
  } catch {
    const processor = context.createScriptProcessor(4096, Math.max(1, Math.min(2, source.channelCount)), 1)
    processor.onaudioprocess = (e) => {
      const channels: Float32Array[] = []
      for (let c = 0; c < e.inputBuffer.numberOfChannels; c++) channels.push(new Float32Array(e.inputBuffer.getChannelData(c)))
      take(channels)
    }
    tap = processor
  }
  source.connect(tap)
  tap.connect(mute)
  // A context made after an await may start suspended (Safari): the press that started it was the gesture.
  await resumeSoon(context)

  const close = () => {
    if (closed) return
    closed = true
    try {
      source.disconnect()
      tap.disconnect()
      mute.disconnect()
    } catch {
      /* already torn down */
    }
    stream.getTracks().forEach((t) => t.stop())
    void context.close().catch(() => undefined)
  }

  return {
    sampleRate: context.sampleRate,
    get suspended() {
      return !closed && context.state === 'suspended'
    },
    async resume() {
      if (closed) return false
      await resumeSoon(context, 1_000)
      return context.state === 'running'
    },
    close,
  }
}

/**
 * Asks for the microphone and starts recording a question. Rejects as openMic
 * does.
 */
export async function startMic(): Promise<MicTake> {
  const blocks: Float32Array[] = []
  let frames = 0
  let loudness = 0
  const mic = await openMic((mono) => {
    blocks.push(mono)
    frames += mono.length
    loudness = level(mono)
  })

  return {
    get seconds() {
      return frames / mic.sampleRate
    },
    get level() {
      return loudness
    },
    finish() {
      mic.close()
      return recordingToWav(blocks, mic.sampleRate)
    },
    cancel() {
      mic.close()
      blocks.length = 0
    },
  }
}
