/**
 * Hands-free listening's ear: what the microphone hears, cut into utterances
 * (one stretch of speech each) so each can be sent to be written down and
 * checked for the assistant's name. Pure functions and a segmenter with no
 * browser in it, so all of it is tested (vad.test.ts).
 *
 * How it decides, frame by frame (20 ms):
 *   - A frame's loudness is its RMS in dBFS (frameDb).
 *   - The noise floor follows the room: it drops at once to a quieter frame
 *     and rises slowly to a louder one, and hardly at all while someone
 *     speaks (nextFloor). The first 400 ms only set it.
 *   - Speech starts after 150 ms of frames 10 dB over the floor (and over
 *     -50 dBFS); a frame within 6 dB of the floor breaks the run.
 *   - It ends after 700 ms of frames within 6 dB of the floor.
 *   - What is kept: 300 ms before the start (pre-roll, so the first syllable
 *     is not clipped), the speech, and 300 ms of the silence after it.
 *   - Less than 0.6 s of speech (a cough, a click, a chair) is dropped, and so
 *     is a stretch whose loudness barely moves (under 2.5 dB of spread): a fan
 *     or an air conditioner, not a voice.
 *   - At 20 s it is sent as it is, and the rest of that stretch is ignored
 *     until the next silence: nobody asks a 20-second question of a voice
 *     assistant, and a meeting or a TV should not cost a call every 20 s.
 */

export interface VadParams {
  /** One frame's length. */
  frameMs: number
  /** Frames over the start threshold before it counts as speech. */
  startMs: number
  /** Frames under the hold threshold that end it. */
  endMs: number
  /** Kept from before the start. */
  preRollMs: number
  /** Kept of the silence after the last voiced frame. */
  tailMs: number
  /** Speech shorter than this (onset to last voiced frame) is dropped. */
  minMs: number
  /** The longest utterance sent, pre-roll included. */
  maxMs: number
  /** Spent only learning the room's floor. */
  warmupMs: number
  /** How far over the floor a frame must be to start speech. */
  startMarginDb: number
  /** How far over the floor a frame must be to keep speech going. */
  holdMarginDb: number
  /** Nothing quieter than this starts speech, however quiet the room. */
  minSpeechDb: number
  /** The share of the way to a quieter frame the floor moves each frame. */
  floorDown: number
  /** The share of the way to a louder frame the floor moves each frame, in silence. */
  floorUp: number
  /** The same while someone speaks: slow, so a long sentence does not raise the floor over itself. */
  floorUpSpeaking: number
  /** Speech whose frames spread less than this (standard deviation, dB) is a steady noise. */
  steadyDb: number
}

export const VAD_DEFAULTS: VadParams = {
  frameMs: 20,
  startMs: 150,
  endMs: 700,
  preRollMs: 300,
  tailMs: 300,
  minMs: 600,
  maxMs: 20_000,
  warmupMs: 400,
  startMarginDb: 10,
  holdMarginDb: 6,
  minSpeechDb: -50,
  floorDown: 0.3,
  floorUp: 0.02,
  floorUpSpeaking: 0.002,
  steadyDb: 2.5,
}

/** Digital silence, in dBFS. */
export const SILENCE_DB = -100

/** A frame's loudness: its RMS in dBFS, SILENCE_DB for nothing at all. */
export function frameDb(samples: Float32Array, from = 0, to = samples.length): number {
  const n = to - from
  if (n <= 0) return SILENCE_DB
  let sum = 0
  for (let i = from; i < to; i++) sum += samples[i] * samples[i]
  const rms = Math.sqrt(sum / n)
  return rms > 0 ? Math.max(SILENCE_DB, 20 * Math.log10(rms)) : SILENCE_DB
}

/** The noise floor after one more frame at `db`: down fast, up slowly, slower still while someone speaks. */
export function nextFloor(floor: number, db: number, speaking: boolean, p: VadParams = VAD_DEFAULTS): number {
  const rate = db < floor ? p.floorDown : speaking ? p.floorUpSpeaking : p.floorUp
  return Math.max(SILENCE_DB, Math.min(0, floor + (db - floor) * rate))
}

/** The frames a span of milliseconds is, at least one. */
export const framesOf = (ms: number, p: VadParams = VAD_DEFAULTS) => Math.max(1, Math.ceil(ms / p.frameMs))

export interface VadState {
  /** warmup: learning the floor; idle: waiting for speech; speech: in an utterance; skip: past the limit, waiting for silence. */
  phase: 'warmup' | 'idle' | 'speech' | 'skip'
  /** Frames seen while warming up. */
  seen: number
  floor: number
  /** Idle: frames since a possible start (0: none), how many of them were over the start threshold, and their loudness summed. */
  run: number
  above: number
  runSum: number
  runSumSq: number
  /** Speech: frames since the start, the last voiced one's index, and the frames since it. */
  length: number
  lastVoiced: number
  silence: number
  /** Speech: the loudness of the frames since the start, summed, to the last voiced frame. */
  sum: number
  sumSq: number
  voicedSum: number
  voicedSumSq: number
}

export function initialVad(): VadState {
  return {
    phase: 'warmup',
    seen: 0,
    floor: SILENCE_DB,
    run: 0,
    above: 0,
    runSum: 0,
    runSumSq: 0,
    length: 0,
    lastVoiced: -1,
    silence: 0,
    sum: 0,
    sumSq: 0,
    voicedSum: 0,
    voicedSumSq: 0,
  }
}

/** Ready for the next utterance, the floor kept: after a pause, nothing half-heard carries over. */
export function resetVad(state: VadState): VadState {
  return { ...initialVad(), phase: state.phase === 'warmup' ? 'warmup' : 'idle', seen: state.seen, floor: state.floor }
}

export type VadEvent =
  /** Speech began: the utterance is the pre-roll and the `run` frames since the possible start. */
  | { type: 'start'; run: number }
  /** An utterance to send: its first `frames` frames from the start (pre-roll not counted). `cut` at the limit. */
  | { type: 'utterance'; frames: number; voicedMs: number; cut: boolean }
  /** Speech that is not worth sending. */
  | { type: 'drop'; reason: 'short' | 'steady'; voicedMs: number }

/** The spread (standard deviation) of `n` values from their sum and sum of squares. */
function spread(sum: number, sumSq: number, n: number): number {
  if (n < 2) return 0
  const mean = sum / n
  return Math.sqrt(Math.max(0, sumSq / n - mean * mean))
}

/** How an utterance that ended (or reached the limit) is judged: dropped, or the frames to send. */
function judge(s: VadState, cut: boolean, p: VadParams): VadEvent {
  const voiced = s.lastVoiced + 1
  const voicedMs = voiced * p.frameMs
  if (voicedMs < p.minMs) return { type: 'drop', reason: 'short', voicedMs }
  if (spread(s.voicedSum, s.voicedSumSq, voiced) < p.steadyDb) return { type: 'drop', reason: 'steady', voicedMs }
  return { type: 'utterance', frames: cut ? s.length : Math.min(s.length, voiced + framesOf(p.tailMs, p)), voicedMs, cut }
}

/**
 * One frame at `db` through the detector: the state after it, and what it
 * started or finished, if anything.
 */
export function vadStep(state: VadState, db: number, p: VadParams = VAD_DEFAULTS): { state: VadState; event: VadEvent | null } {
  const s = { ...state }
  const start = Math.max(s.floor + p.startMarginDb, p.minSpeechDb)
  const hold = Math.max(s.floor + p.holdMarginDb, p.minSpeechDb - (p.startMarginDb - p.holdMarginDb))

  if (s.phase === 'warmup') {
    s.floor = s.seen === 0 ? db : s.floor + (db - s.floor) * p.floorDown
    s.seen++
    if (s.seen >= framesOf(p.warmupMs, p)) s.phase = 'idle'
    return { state: s, event: null }
  }

  if (s.phase === 'idle') {
    s.floor = nextFloor(s.floor, db, s.run > 0, p)
    if (db >= start || (db >= hold && s.run > 0)) {
      s.run++
      if (db >= start) s.above++
      s.runSum += db
      s.runSumSq += db * db
    } else {
      s.run = 0
      s.above = 0
      s.runSum = 0
      s.runSumSq = 0
    }
    if (s.above < framesOf(p.startMs, p)) return { state: s, event: null }
    // The run's frames are the utterance's first: its spread is measured from them on.
    const { run, runSum, runSumSq } = s
    return {
      state: {
        ...s,
        phase: 'speech',
        run: 0,
        above: 0,
        runSum: 0,
        runSumSq: 0,
        length: run,
        lastVoiced: run - 1,
        silence: 0,
        sum: runSum,
        sumSq: runSumSq,
        voicedSum: runSum,
        voicedSumSq: runSumSq,
      },
      event: { type: 'start', run },
    }
  }

  s.floor = nextFloor(s.floor, db, true, p)
  if (s.phase === 'skip') {
    s.silence = db >= hold ? 0 : s.silence + 1
    if (s.silence >= framesOf(p.endMs, p)) return { state: resetVad(s), event: null }
    return { state: s, event: null }
  }

  // Speech.
  s.length++
  s.sum += db
  s.sumSq += db * db
  if (db >= hold) {
    s.lastVoiced = s.length - 1
    s.silence = 0
    s.voicedSum = s.sum
    s.voicedSumSq = s.sumSq
  } else {
    s.silence++
  }

  if (s.silence >= framesOf(p.endMs, p)) return { state: resetVad(s), event: judge(s, false, p) }
  if (s.length >= framesOf(p.maxMs - p.preRollMs, p)) return { state: { ...resetVad(s), phase: 'skip' }, event: judge(s, true, p) }
  return { state: s, event: null }
}

/** One utterance, as the microphone gave it: mono samples at its own rate. */
export interface Utterance {
  samples: Float32Array
  sampleRate: number
  seconds: number
  voicedMs: number
  /** Reached the limit, so it stops mid-speech. */
  cut: boolean
}

export type SegmentEvent =
  | { type: 'start' }
  | { type: 'utterance'; utterance: Utterance }
  | { type: 'drop'; reason: 'short' | 'steady'; voicedMs: number }

export interface Segmenter {
  /** Feeds a block of mono samples; returns what began or ended in it. */
  push(block: Float32Array): SegmentEvent[]
  /** Forgets anything half-heard (the floor stays): for a pause. */
  reset(): void
  /** Whether it is inside an utterance now. */
  readonly speaking: boolean
  /** The room's noise floor, in dBFS. */
  readonly floor: number
}

function join(frames: readonly Float32Array[]): Float32Array {
  const out = new Float32Array(frames.reduce((n, f) => n + f.length, 0))
  let at = 0
  for (const f of frames) {
    out.set(f, at)
    at += f.length
  }
  return out
}

/**
 * The microphone's blocks (any size) cut into frames, run through vadStep,
 * and gathered into utterances with their pre-roll.
 */
export function createSegmenter(sampleRate: number, p: VadParams = VAD_DEFAULTS): Segmenter {
  const frameSize = Math.max(1, Math.round((sampleRate * p.frameMs) / 1000))
  const preRoll = framesOf(p.preRollMs, p)
  let state = initialVad()
  let rest = new Float32Array(0)
  // Before a possible start: the last pre-roll's frames. During one: its frames. In an utterance: all of it.
  let ring: Float32Array[] = []
  let pending: Float32Array[] = []
  let utterance: Float32Array[] = []
  let pre = 0

  const frame = (f: Float32Array, out: SegmentEvent[]) => {
    const was = state.phase
    const { state: next, event } = vadStep(state, frameDb(f), p)
    state = next
    if (was === 'warmup' || was === 'idle') {
      if (event?.type === 'start') {
        pending.push(f)
        utterance = [...ring, ...pending]
        pre = ring.length
        ring = []
        pending = []
        out.push({ type: 'start' })
      } else if (state.run > 0) {
        pending.push(f)
      } else {
        ring.push(...pending, f)
        pending = []
        if (ring.length > preRoll) ring = ring.slice(ring.length - preRoll)
      }
      return
    }
    if (was === 'skip') return
    utterance.push(f)
    if (!event) return
    if (event.type === 'utterance') {
      const samples = join(utterance.slice(0, pre + event.frames))
      out.push({ type: 'utterance', utterance: { samples, sampleRate, seconds: samples.length / sampleRate, voicedMs: event.voicedMs, cut: event.cut } })
    } else if (event.type === 'drop') {
      out.push({ type: 'drop', reason: event.reason, voicedMs: event.voicedMs })
    }
    utterance = []
    pre = 0
  }

  return {
    push(block) {
      const out: SegmentEvent[] = []
      let data = block
      if (rest.length > 0) {
        data = new Float32Array(rest.length + block.length)
        data.set(rest)
        data.set(block, rest.length)
      }
      let at = 0
      for (; at + frameSize <= data.length; at += frameSize) frame(data.slice(at, at + frameSize), out)
      rest = data.slice(at)
      return out
    },
    reset() {
      state = resetVad(state)
      rest = new Float32Array(0)
      ring = []
      pending = []
      utterance = []
      pre = 0
    },
    get speaking() {
      return state.phase === 'speech'
    },
    get floor() {
      return state.floor
    },
  }
}
