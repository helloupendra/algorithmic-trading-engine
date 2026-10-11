import { describe, expect, it } from 'vitest'
import { SILENCE_DB, VAD_DEFAULTS, createSegmenter, frameDb, framesOf, initialVad, nextFloor, resetVad, vadStep } from './vad'
import type { SegmentEvent, Utterance, VadEvent, VadState } from './vad'

const RATE = 16_000

/** Room noise, deterministic: uniform in ±amp (RMS amp/√3; 0.001 is about -65 dBFS). */
function noise(ms: number, amp = 0.001, seed = 7): Float32Array {
  const out = new Float32Array(Math.round((RATE * ms) / 1000))
  let x = seed
  for (let i = 0; i < out.length; i++) {
    x = (x * 1_103_515_245 + 12_345) % 2_147_483_648
    out[i] = (x / 2_147_483_648 - 0.5) * 2 * amp
  }
  return out
}

/** Something like speech: a 220 Hz voice whose loudness changes every 40 ms (-13 to -29 dBFS), over the room. */
function speech(ms: number, amps: readonly number[] = [0.3, 0.05, 0.2, 0.1]): Float32Array {
  const out = noise(ms)
  const per = Math.round(RATE * 0.04)
  for (let i = 0; i < out.length; i++) out[i] += amps[Math.floor(i / per) % amps.length] * Math.sin((2 * Math.PI * 220 * i) / RATE)
  return out
}

function concat(...parts: Float32Array[]): Float32Array {
  const out = new Float32Array(parts.reduce((n, p) => n + p.length, 0))
  let at = 0
  for (const p of parts) {
    out.set(p, at)
    at += p.length
  }
  return out
}

/** Feeds `signal` in blocks of `block` samples (the worklet sends about 1,024) and gathers what came out. */
function run(signal: Float32Array, block = 1024, segmenter = createSegmenter(RATE)): SegmentEvent[] {
  const events: SegmentEvent[] = []
  for (let at = 0; at < signal.length; at += block) events.push(...segmenter.push(signal.slice(at, at + block)))
  return events
}

const utterances = (events: SegmentEvent[]): Utterance[] => events.flatMap((e) => (e.type === 'utterance' ? [e.utterance] : []))

/** Steps the detector through frames of the given loudness. */
function steps(state: VadState, dbs: number[]): { state: VadState; events: VadEvent[] } {
  const events: VadEvent[] = []
  let s = state
  for (const db of dbs) {
    const r = vadStep(s, db)
    s = r.state
    if (r.event) events.push(r.event)
  }
  return { state: s, events }
}

const times = (n: number, db: number) => Array.from({ length: n }, () => db)
/** Frames that rise and fall as a voice does: -14, -26, -18, -24 dBFS. */
const voiced = (n: number) => Array.from({ length: n }, (_, i) => [-14, -26, -18, -24][i % 4])
/** A detector past its warm-up in a room at -65 dBFS. */
const warm = () => steps(initialVad(), times(framesOf(VAD_DEFAULTS.warmupMs), -65)).state

describe('frameDb', () => {
  it('is the RMS in dBFS, and silence for nothing', () => {
    expect(frameDb(new Float32Array([1, -1, 1, -1]))).toBeCloseTo(0, 6)
    expect(frameDb(new Float32Array([0.1, -0.1]))).toBeCloseTo(-20, 6)
    expect(frameDb(new Float32Array(320))).toBe(SILENCE_DB)
    expect(frameDb(new Float32Array(0))).toBe(SILENCE_DB)
    expect(frameDb(new Float32Array([0, 0, 0.5, -0.5]), 2, 4)).toBeCloseTo(-6.02, 2)
  })
})

describe('nextFloor', () => {
  it('drops fast to a quieter frame, rises slowly to a louder one, and slower still while someone speaks', () => {
    expect(nextFloor(-60, -80, false)).toBeCloseTo(-66, 6)
    expect(nextFloor(-60, -40, false)).toBeCloseTo(-59.6, 6)
    expect(nextFloor(-60, -40, true)).toBeCloseTo(-59.96, 6)
    expect(nextFloor(-99, -200, false)).toBeGreaterThanOrEqual(SILENCE_DB)
  })
})

describe('vadStep', () => {
  it('only learns the room while warming up', () => {
    const { state, events } = steps(initialVad(), times(framesOf(VAD_DEFAULTS.warmupMs), -65))
    expect(events).toEqual([])
    expect(state.phase).toBe('idle')
    expect(state.floor).toBeCloseTo(-65, 6)
  })

  it('starts after 150 ms over the threshold, and a frame back at the floor starts the count again', () => {
    const start = framesOf(VAD_DEFAULTS.startMs)
    expect(start).toBe(8)
    expect(steps(warm(), times(start - 1, -20)).events).toEqual([])
    expect(steps(warm(), [...times(start - 1, -20), -65, ...times(start - 1, -20)]).events).toEqual([])
    // A frame between the two thresholds neither counts nor breaks the run.
    const dipped = steps(warm(), [...times(4, -20), -52, ...times(4, -20)])
    expect(dipped.events).toEqual([{ type: 'start', run: 9 }])
    expect(steps(warm(), times(start, -20)).events).toEqual([{ type: 'start', run: 8 }])
  })

  it('never starts on a sound quieter than -50 dBFS, however quiet the room', () => {
    const silent = steps(initialVad(), times(framesOf(VAD_DEFAULTS.warmupMs), SILENCE_DB)).state
    expect(steps(silent, times(50, -52)).events).toEqual([])
  })

  it('ends after 700 ms of quiet and keeps 300 ms of it', () => {
    const { state, events } = steps(warm(), [...voiced(60), ...times(framesOf(VAD_DEFAULTS.endMs), -65)])
    expect(events).toEqual([{ type: 'start', run: 8 }, { type: 'utterance', frames: 60 + 15, voicedMs: 1200, cut: false }])
    expect(state.phase).toBe('idle')
  })

  it('carries on through a pause shorter than 700 ms', () => {
    const { events } = steps(warm(), [...voiced(40), ...times(30, -65), ...voiced(40), ...times(35, -65)])
    expect(events.filter((e) => e.type !== 'start')).toEqual([{ type: 'utterance', frames: 110 + 15, voicedMs: 2200, cut: false }])
  })

  it('drops less than 0.6 s of sound, and a sound that never changes', () => {
    expect(steps(warm(), [...voiced(25), ...times(35, -65)]).events[1]).toEqual({ type: 'drop', reason: 'short', voicedMs: 500 })
    expect(steps(warm(), [...times(100, -30), ...times(35, -65)]).events[1]).toEqual({ type: 'drop', reason: 'steady', voicedMs: 2000 })
  })

  it('sends 20 s at most, then ignores the rest of that stretch until a silence', () => {
    const max = framesOf(VAD_DEFAULTS.maxMs - VAD_DEFAULTS.preRollMs)
    const long = steps(warm(), voiced(max + 200))
    expect(long.events).toEqual([{ type: 'start', run: 8 }, { type: 'utterance', frames: max, voicedMs: max * 20, cut: true }])
    expect(long.state.phase).toBe('skip')
    const after = steps(long.state, [...times(35, -65), ...voiced(60), ...times(35, -65)])
    expect(after.events.map((e) => e.type)).toEqual(['start', 'utterance'])
  })

  it('forgets a half-heard utterance on a reset, and keeps the floor', () => {
    const half = steps(warm(), voiced(30)).state
    expect(half.phase).toBe('speech')
    const reset = resetVad(half)
    expect(reset.phase).toBe('idle')
    expect(reset.floor).toBe(half.floor)
    expect(steps(reset, times(35, -65)).events).toEqual([])
  })
})

describe('createSegmenter', () => {
  it('cuts one utterance out of a room, with 300 ms before it and 300 ms after', () => {
    const said = speech(1200)
    const events = run(concat(noise(1000), said, noise(1500)))

    expect(events.map((e) => e.type)).toEqual(['start', 'utterance'])
    const [u] = utterances(events)
    expect(u.sampleRate).toBe(RATE)
    expect(u.cut).toBe(false)
    expect(u.voicedMs).toBeGreaterThanOrEqual(1160)
    // 1.2 s of speech, 0.3 s before and 0.3 s after, give or take a frame at either end.
    expect(u.seconds).toBeGreaterThan(1.75)
    expect(u.seconds).toBeLessThan(1.85)
    // It starts in the room and the speech is in it, whole.
    expect(frameDb(u.samples, 0, 320)).toBeLessThan(-55)
    const at = Math.round(0.3 * RATE)
    expect(frameDb(u.samples, at, at + said.length)).toBeCloseTo(frameDb(said), 0)
  })

  it('gives the same utterance however the blocks are cut', () => {
    const signal = concat(noise(800), speech(1500), noise(1200))
    const a = utterances(run(signal, 1024))
    const b = utterances(run(signal, 128))
    const c = utterances(run(signal, 4096))
    expect(a).toHaveLength(1)
    expect(b[0].samples).toEqual(a[0].samples)
    expect(c[0].samples).toEqual(a[0].samples)
  })

  it('sends two utterances a second apart separately, and one with a short pause in it as one', () => {
    expect(utterances(run(concat(noise(800), speech(1000), noise(1000), speech(1000), noise(1000))))).toHaveLength(2)
    expect(utterances(run(concat(noise(800), speech(1000), noise(400), speech(1000), noise(1000))))).toHaveLength(1)
  })

  it('drops a cough and a fan', () => {
    const cough = run(concat(noise(800), speech(200), noise(1000)))
    expect(cough.filter((e) => e.type !== 'start')).toEqual([{ type: 'drop', reason: 'short', voicedMs: expect.any(Number) }])
    const fan = run(concat(noise(800), noise(30_000, 0.05, 11)))
    expect(utterances(fan)).toEqual([])
    expect(fan.some((e) => e.type === 'drop' && e.reason === 'steady')).toBe(true)
  })

  it('cuts a long stretch at 20 s and hears the next utterance after a silence', () => {
    const events = run(concat(noise(800), speech(26_000), noise(1000), speech(1000), noise(1000)))
    const sent = utterances(events)
    expect(sent).toHaveLength(2)
    expect(sent[0].cut).toBe(true)
    expect(sent[0].seconds).toBeCloseTo(20, 1)
    expect(sent[1].cut).toBe(false)
  })

  it('forgets what it was hearing on a reset', () => {
    const s = createSegmenter(RATE)
    run(concat(noise(800), speech(700)), 1024, s)
    expect(s.speaking).toBe(true)
    s.reset()
    expect(s.speaking).toBe(false)
    expect(run(concat(speech(300), noise(1200)), 1024, s).filter((e) => e.type === 'utterance')).toEqual([])
  })
})
