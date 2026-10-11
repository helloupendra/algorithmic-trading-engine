/**
 * The microphone, recorded as raw samples with the Web Audio API and handed
 * back as the 16 kHz mono WAV the API takes (wav.ts).
 *
 * Not MediaRecorder: it records webm, ogg or mp4 depending on the browser, and
 * the audio model takes WAV or MP3 only. The samples come from an
 * AudioWorklet (pcmTap.worklet.js) on the audio thread; where a browser has no
 * AudioWorklet, or will not load it, the deprecated ScriptProcessor does the
 * same job on the main thread. Either way the graph ends in a muted gain, so
 * the browser keeps pulling samples and nothing is played back.
 *
 * Browser-only: getUserMedia, AudioContext and a real microphone, which no
 * unit test has. The encoding it hands over to is tested (wav.test.ts).
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

type WebkitWindow = Window & { webkitAudioContext?: typeof AudioContext }

/**
 * Asks for the microphone (the browser may ask the viewer first) and starts
 * recording. Rejects with the browser's own error (NotAllowedError,
 * NotFoundError, …) or MicUnavailable; micErrorText words each.
 */
export async function startMic(): Promise<MicTake> {
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

  const blocks: Float32Array[] = []
  let frames = 0
  let loudness = 0
  let closed = false
  const take = (channels: Float32Array[]) => {
    if (closed || channels.length === 0) return
    const mono = downmix(channels)
    blocks.push(mono)
    frames += mono.length
    loudness = level(mono)
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
  if (context.state === 'suspended') await context.resume().catch(() => undefined)

  const rate = context.sampleRate
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
    get seconds() {
      return frames / rate
    },
    get level() {
      return loudness
    },
    finish() {
      close()
      return recordingToWav(blocks, rate)
    },
    cancel() {
      close()
      blocks.length = 0
    },
  }
}
