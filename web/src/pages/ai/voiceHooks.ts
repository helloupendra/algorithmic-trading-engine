/**
 * The state behind talking to the desk on AI → Assistant (AssistantVoice.tsx
 * draws it): the microphone as a recorder, spoken replies through one audio
 * element, and hands-free listening. Hooks only, so the components file holds
 * only components.
 */

import { useCallback, useEffect, useRef, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { openMic, startMic } from '../../lib/micRecorder'
import type { MicStream, MicTake } from '../../lib/micRecorder'
import {
  VOICE_MAX_SECONDS,
  VoiceRefusal,
  checkWake,
  handsFreeWait,
  micErrorText,
  readHandsFree,
  readVoiceInfo,
  recentSends,
  speak,
  stopsHandsFree,
  transcribe,
  voiceFailureText,
  writeHandsFree,
} from '../../lib/voice'
import type { HandsFreeHold, VoiceInfo } from '../../lib/voice'
import { isAbort } from '../../lib/ai'
import type { HeardMark } from '../../lib/ai'
import { api } from '../../lib/api'
import { createSegmenter } from '../../lib/vad'
import type { Segmenter, Utterance } from '../../lib/vad'
import { encodeWav, level, recordingToWav } from '../../lib/wav'

/** A take held for less than this was too short to be the whole question: a hold that short carries on as a click. */
const MIN_HELD_SECONDS = 0.5

export type RecorderState = 'idle' | 'starting' | 'recording'

export interface VoiceRecorder {
  state: RecorderState
  /** Held down (push-to-talk) rather than clicked. */
  held: boolean
  seconds: number
  level: number
  start: (held: boolean) => void
  send: () => void
  cancel: () => void
  /** Whether enough has been captured for a let-go to send it. */
  heldLongEnough: () => boolean
  release: () => void
}

/**
 * The microphone as a recorder: `onRecorded` gets each finished take as WAV,
 * `onProblem` the reason in words when the mic will not start.
 */
export function useVoiceRecorder(onRecorded: (wav: ArrayBuffer) => void, onProblem: (text: string) => void): VoiceRecorder {
  const [state, setState] = useState<RecorderState>('idle')
  const [held, setHeld] = useState(false)
  const [meter, setMeter] = useState({ seconds: 0, level: 0 })
  const take = useRef<MicTake | null>(null)
  // Each start's number: a microphone that answers after its take was cancelled (or replaced) is let go at once.
  const generation = useRef(0)
  const busy = useRef(false)
  const recorded = useRef(onRecorded)
  const problem = useRef(onProblem)
  useEffect(() => {
    recorded.current = onRecorded
    problem.current = onProblem
  })

  const finish = useCallback((deliver: boolean) => {
    const t = take.current
    take.current = null
    generation.current++
    busy.current = false
    setState('idle')
    setHeld(false)
    setMeter({ seconds: 0, level: 0 })
    if (!t) return
    if (!deliver) {
      t.cancel()
      return
    }
    recorded.current(t.finish())
  }, [])

  const start = useCallback((hold: boolean) => {
    if (busy.current) return
    busy.current = true
    const mine = ++generation.current
    setHeld(hold)
    setState('starting')
    startMic().then(
      (t) => {
        if (mine !== generation.current) {
          t.cancel()
          return
        }
        take.current = t
        setState('recording')
      },
      (error: unknown) => {
        if (mine !== generation.current) return
        busy.current = false
        setState('idle')
        setHeld(false)
        problem.current(micErrorText(error))
      },
    )
  }, [])

  const cancel = useCallback(() => {
    if (busy.current) finish(false)
  }, [finish])

  const send = useCallback(() => {
    if (take.current) finish(true)
  }, [finish])

  // The clock and the meter, ten times a second; at the limit it sends what it has.
  useEffect(() => {
    if (state !== 'recording') return
    const id = window.setInterval(() => {
      const t = take.current
      if (!t) return
      setMeter({ seconds: t.seconds, level: t.level })
      if (t.seconds >= VOICE_MAX_SECONDS) finish(true)
    }, 100)
    return () => window.clearInterval(id)
  }, [state, finish])

  // Esc cancels, wherever the focus is.
  useEffect(() => {
    if (state === 'idle') return
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.preventDefault()
        cancel()
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [state, cancel])

  // Leaving the page lets go of the microphone.
  useEffect(
    () => () => {
      busy.current = false
      generation.current++
      take.current?.cancel()
      take.current = null
    },
    [],
  )

  return {
    state,
    held,
    seconds: meter.seconds,
    level: meter.level,
    start,
    send,
    cancel,
    heldLongEnough: () => (take.current?.seconds ?? 0) >= MIN_HELD_SECONDS,
    release: () => setHeld(false),
  }
}

type PlayState = 'idle' | 'fetching' | 'playing' | 'blocked'

export interface SpokenReplies {
  state: PlayState
  problem: string | null
  /** Called inside a click or key press: lets the one audio element play later, where a browser asks for a gesture first. */
  unlock: () => void
  say: (text: string, callId: number | null) => void
  /** Plays a reply the browser held back, from a click. */
  resume: () => void
  stop: () => void
  dismiss: () => void
  /** Asks again from the next answer, though the API asked for a pause (the viewer turned Speak replies back on). */
  allow: () => void
}

/** A short silent WAV, for unlocking playback inside a gesture. */
let silence: string | null = null
function silentUrl(): string {
  silence ??= URL.createObjectURL(new Blob([encodeWav(new Int16Array(800))], { type: 'audio/wav' }))
  return silence
}

/** Spoken replies: one audio element, reused, so only one reply ever plays. */
export function useSpokenReplies(): SpokenReplies {
  const [state, setState] = useState<PlayState>('idle')
  const [problem, setProblem] = useState<string | null>(null)
  const audio = useRef<HTMLAudioElement | null>(null)
  const url = useRef<string | null>(null)
  const fetching = useRef<AbortController | null>(null)
  const unlocked = useRef(false)
  // Until when the API asked not to be asked (a 503 with Retry-After: Polly refused the role, or replies are off).
  const pausedUntil = useRef(0)

  const element = useCallback(() => {
    if (!audio.current) {
      const a = new Audio()
      a.preload = 'auto'
      a.addEventListener('ended', () => setState('idle'))
      a.addEventListener('error', () => {
        // Only a reply's own audio is watched: the silent unlock clip has no URL of its own here.
        if (!url.current) return
        setState('idle')
        setProblem('The spoken reply could not be played in this browser. The answer is above as text.')
      })
      audio.current = a
    }
    return audio.current
  }, [])

  const stop = useCallback(() => {
    fetching.current?.abort()
    fetching.current = null
    const a = audio.current
    if (a) {
      a.pause()
      a.removeAttribute('src')
      a.load()
    }
    if (url.current) URL.revokeObjectURL(url.current)
    url.current = null
    setState('idle')
    setProblem(null)
  }, [])

  const unlock = useCallback(() => {
    if (unlocked.current) return
    unlocked.current = true
    try {
      const a = element()
      a.muted = true
      a.src = silentUrl()
      void a
        .play()
        .then(() => a.pause())
        .catch(() => undefined)
        .finally(() => {
          a.muted = false
        })
    } catch {
      /* nothing to unlock with: a reply may wait for the Play button */
    }
  }, [element])

  const say = useCallback(
    (text: string, callId: number | null) => {
      stop()
      // The API said when Polly may be asked again: the answers before then stay text, without the same notice each time.
      if (Date.now() < pausedUntil.current) return
      const controller = new AbortController()
      fetching.current = controller
      setState('fetching')
      speak(text, callId, controller.signal).then(
        (mp3) => {
          if (fetching.current !== controller) return
          fetching.current = null
          const a = element()
          url.current = URL.createObjectURL(mp3)
          a.muted = false
          a.src = url.current
          a.play().then(
            () => setState('playing'),
            (error: unknown) => {
              // The browser wants a click first (autoplay rules): the Play button gives it one.
              if ((error as { name?: string } | null)?.name === 'NotAllowedError') setState('blocked')
              else {
                setState('idle')
                setProblem(`The spoken reply could not play: ${error instanceof Error ? error.message : String(error)}. The answer is above as text.`)
              }
            },
          )
        },
        (error: unknown) => {
          if (fetching.current !== controller) return
          fetching.current = null
          setState('idle')
          if (isAbort(error)) return
          // 503: Polly refused the role (the API says for how long) or replies are switched off on the desk (ten minutes).
          if (error instanceof VoiceRefusal && error.status === 503) {
            pausedUntil.current = Date.now() + (error.retryAfterSeconds ?? 600) * 1000
          }
          setProblem(voiceFailureText('speak', error))
        },
      )
    },
    [element, stop],
  )

  const resume = useCallback(() => {
    const a = audio.current
    if (!a?.src) return
    a.play().then(
      () => setState('playing'),
      () => setState('blocked'),
    )
  }, [])

  // Leaving the page stops the voice.
  useEffect(() => () => stop(), [stop])

  return {
    state,
    problem,
    unlock,
    say,
    resume,
    stop,
    dismiss: () => setProblem(null),
    allow: () => {
      pausedUntil.current = 0
    },
  }
}

// ---------- hands-free --------------------------------------------------------

/** The voice as the API describes it (GET /api/Ai/voice): the assistant's name, the switches, the hands-free budget. */
export function useAiVoice() {
  return useQuery({
    queryKey: ['ai', 'voice'],
    queryFn: async () => readVoiceInfo(await api.get<unknown>('/api/Ai/voice')),
    staleTime: 60_000,
  })
}

export type { HandsFreeHold } from '../../lib/voice'

/** Why hands-free is not listening: the page's hold, a hidden tab, too much speech, or the moment after a hold. */
export type HandsFreePause = HandsFreeHold | 'hidden' | 'cap' | 'resuming'

export type HandsFreeStatus = 'off' | 'starting' | 'gesture' | 'paused' | 'listening' | 'hearing' | 'checking'

/** A faint word for a moment: heard but not addressed, the name alone (say the question), or a check that failed. */
export interface HandsFreeNote {
  kind: 'ignored' | 'wake' | 'failed'
  text: string
}

export interface HandsFree {
  /** The viewer's switch, kept in this browser. */
  on: boolean
  /** The desk takes hands-free: voice on, and a budget above 0. */
  available: boolean
  status: HandsFreeStatus
  pause: HandsFreePause | null
  /** For a 'cap' pause: when it listens again, in ms since the epoch. */
  resumeAt: number | null
  note: HandsFreeNote | null
  /** Why it turned itself off, in words. */
  problem: string | null
  /** The microphone's loudness now, 0 to 1 (0 while paused), for a meter that redraws without the page. */
  readLevel: () => number
  toggle: (on: boolean) => void
  /** From a click: lets the audio through where the browser held it back. */
  resume: () => void
  dismiss: () => void
}

export interface HandsFreeOptions {
  /** Null while it is being asked, or when the API does not say. */
  info: VoiceInfo | null
  hold: HandsFreeHold | null
  conversationId: string
  /** A question said to the assistant: asked as the mic's are. */
  onQuestion: (question: string, mark: HeardMark) => void
}

/** How long after a hold ends before it listens again: the tail of a reply, or of an answer's last sound, is not a question. */
const RESUME_GRACE_MS = 600
/** After the name alone ("Ira."), the next utterance within this is the question, name or not. */
const FOLLOW_UP_MS = 8_000
const IGNORED_NOTE_MS = 2_000
const FAILED_NOTE_MS = 4_000
/** Checks that fail in a row (the model, the network) before hands-free turns itself off. */
const MAX_FAILURES = 3
/** Utterances waiting while one is checked; more are dropped. */
const MAX_QUEUED = 2

function handsFreeMicText(error: unknown): string {
  return micErrorText(error).replace('press the mic again', 'turn Hands-free on again').replace(' Type the question instead.', '')
}

/**
 * Hands-free: the microphone held open while it is on, cut into utterances
 * (lib/vad), each sent with the wake check (lib/voice checkWake). One said to
 * the assistant is asked through `onQuestion`, marked as heard; anything else
 * is a faint note for two seconds. It pauses while the page holds it (Ira
 * speaking, an answer coming, the mic in use), while the tab is hidden, and
 * when the minute's budget is spent; it resumes by itself. A refused or
 * missing microphone, voice switched off or an API without hands-free turns
 * it off with the reason.
 */
export function useHandsFree({ info, hold, conversationId, onQuestion }: HandsFreeOptions): HandsFree {
  const [on, setOn] = useState(() => readHandsFree())
  const [problem, setProblem] = useState<string | null>(null)
  const [ready, setReady] = useState(false)
  const [gesture, setGesture] = useState(false)
  const [hidden, setHidden] = useState(false)
  const [resumeAt, setResumeAt] = useState<number | null>(null)
  const [grace, setGrace] = useState(false)
  const [hearing, setHearing] = useState(false)
  const [checking, setChecking] = useState(false)
  const [note, setNote] = useState<HandsFreeNote | null>(null)

  const available = info != null && info.enabled && info.handsFreePerMinute > 0
  const active = on && available

  const mic = useRef<MicStream | null>(null)
  const segmenter = useRef<Segmenter | null>(null)
  const paused = useRef(true)
  const loud = useRef(0)
  const queue = useRef<Utterance[]>([])
  const inFlight = useRef<AbortController | null>(null)
  const sentAt = useRef<number[]>([])
  const followUntil = useRef(0)
  const failures = useRef(0)
  const noteTimer = useRef<number | null>(null)
  const latest = useRef({ conversationId, onQuestion, perMinute: info?.handsFreePerMinute ?? 0 })
  useEffect(() => {
    latest.current = { conversationId, onQuestion, perMinute: info?.handsFreePerMinute ?? 0 }
  })

  useEffect(() => writeHandsFree(on), [on])

  const show = useCallback((next: HandsFreeNote | null, ms = 0) => {
    if (noteTimer.current != null) window.clearTimeout(noteTimer.current)
    noteTimer.current = null
    setNote(next)
    if (next && ms > 0) {
      noteTimer.current = window.setTimeout(() => {
        noteTimer.current = null
        setNote(null)
      }, ms)
    }
  }, [])
  useEffect(
    () => () => {
      if (noteTimer.current != null) window.clearTimeout(noteTimer.current)
    },
    [],
  )

  /** Sends the next waiting utterance, one at a time, within the minute's budget. */
  const pump = useCallback(
    function next() {
      if (inFlight.current || paused.current) return
      const u = queue.current.shift()
      if (!u) return
      const now = Date.now()
      // After the name alone, the next utterance is the question: written down as the mic's are, not checked for the name.
      const followUp = now < followUntil.current
      followUntil.current = 0
      if (!followUp) {
        const wait = handsFreeWait(sentAt.current, now, latest.current.perMinute)
        if (wait > 0) {
          queue.current = []
          setResumeAt(now + wait)
          return
        }
        sentAt.current = [...recentSends(sentAt.current, now), now]
      }

      const controller = new AbortController()
      inFlight.current = controller
      setChecking(true)
      const wav = recordingToWav([u.samples], u.sampleRate)
      const conversation = latest.current.conversationId
      const heard = followUp
        ? transcribe(wav, conversation, controller.signal).then((h) => ({ ...h, addressed: true, question: h.text.trim() }))
        : checkWake(wav, conversation, controller.signal)
      heard
        .then(
          (h) => {
            if (inFlight.current !== controller) return
            failures.current = 0
            if (h.addressed && h.question) {
              show(null)
              latest.current.onQuestion(h.question, { callId: h.callId, seconds: h.seconds, handsFree: true })
            } else if (h.addressed) {
              followUntil.current = Date.now() + FOLLOW_UP_MS
              show({ kind: 'wake', text: 'Yes? Say the question.' }, FOLLOW_UP_MS)
            } else {
              show({ kind: 'ignored', text: 'Heard something, not addressed' }, IGNORED_NOTE_MS)
            }
          },
          (error: unknown) => {
            if (inFlight.current !== controller || isAbort(error)) return
            // The minute's budget, by the API's count: listen again when it says.
            if (error instanceof VoiceRefusal && error.status === 429) {
              queue.current = []
              setResumeAt(Date.now() + (error.retryAfterSeconds ?? 30) * 1000)
              return
            }
            failures.current++
            if (stopsHandsFree(error) || failures.current >= MAX_FAILURES) {
              setProblem(`Hands-free turned off: ${voiceFailureText('hear', error)}`)
              setOn(false)
              return
            }
            show({ kind: 'failed', text: `Could not check that: ${voiceFailureText('hear', error)}` }, FAILED_NOTE_MS)
          },
        )
        .finally(() => {
          if (inFlight.current !== controller) return
          inFlight.current = null
          setChecking(false)
          next()
        })
    },
    [show],
  )

  /** Each block the microphone hands over: the meter's level, and the detector unless paused. */
  const onBlock = useCallback(
    (mono: Float32Array) => {
      const seg = segmenter.current
      if (!seg || paused.current) {
        loud.current = 0
        return
      }
      loud.current = level(mono)
      for (const e of seg.push(mono)) {
        if (e.type === 'start') {
          setHearing(true)
          continue
        }
        setHearing(false)
        if (e.type === 'utterance' && queue.current.length < MAX_QUEUED) {
          queue.current.push(e.utterance)
          pump()
        }
      }
    },
    [pump],
  )

  // On: the microphone, asked for once and held open. Off, or leaving the page: let go of it, and of anything in flight.
  useEffect(() => {
    if (!active) return
    let cancelled = false
    openMic(onBlock).then(
      (m) => {
        if (cancelled) {
          m.close()
          return
        }
        mic.current = m
        segmenter.current = createSegmenter(m.sampleRate)
        setGesture(m.suspended)
        setReady(true)
      },
      (error: unknown) => {
        if (cancelled) return
        setProblem(`Hands-free is off: ${handsFreeMicText(error)}`)
        setOn(false)
      },
    )
    return () => {
      cancelled = true
      mic.current?.close()
      mic.current = null
      segmenter.current = null
      const flying = inFlight.current
      inFlight.current = null
      flying?.abort()
      queue.current = []
      followUntil.current = 0
      loud.current = 0
      setReady(false)
      setGesture(false)
      setHearing(false)
      setChecking(false)
    }
  }, [active, onBlock])

  // The tab hidden: not listening (and nothing heard from another tab's sound is sent).
  useEffect(() => {
    if (!active) return
    const onVisibility = () => setHidden(document.hidden)
    onVisibility()
    document.addEventListener('visibilitychange', onVisibility)
    return () => document.removeEventListener('visibilitychange', onVisibility)
  }, [active])

  // A moment's grace after the page's hold ends, so the end of a spoken reply is not taken for the owner.
  const held = hold != null
  const wasHeld = useRef(held)
  useEffect(() => {
    const ended = wasHeld.current && !held
    wasHeld.current = held
    if (!ended) return
    setGrace(true)
    const id = window.setTimeout(() => setGrace(false), RESUME_GRACE_MS)
    return () => window.clearTimeout(id)
  }, [held])

  // The minute's budget spent: listen again when it allows.
  useEffect(() => {
    if (resumeAt == null) return
    const id = window.setTimeout(() => setResumeAt(null), Math.max(0, resumeAt - Date.now()))
    return () => window.clearTimeout(id)
  }, [resumeAt])

  const resume = useCallback(() => {
    const m = mic.current
    if (!m) return
    void m.resume().then((running) => {
      if (running) setGesture(false)
    })
  }, [])

  // Opened without a click (a reload with hands-free on): the first click or key press anywhere lets the audio through.
  useEffect(() => {
    if (!gesture) return
    window.addEventListener('pointerdown', resume)
    window.addEventListener('keydown', resume)
    return () => {
      window.removeEventListener('pointerdown', resume)
      window.removeEventListener('keydown', resume)
    }
  }, [gesture, resume])

  const pause: HandsFreePause | null = !active ? null : (hold ?? (hidden ? 'hidden' : resumeAt != null ? 'cap' : grace ? 'resuming' : null))
  const quiet = !active || !ready || gesture || pause != null

  // Into a pause or out of one: anything half-heard is dropped, and nothing waits to be sent across it.
  useEffect(() => {
    paused.current = quiet
    segmenter.current?.reset()
    setHearing(false)
    if (quiet) queue.current = []
    else pump()
  }, [quiet, pump])

  const status: HandsFreeStatus = !active
    ? 'off'
    : !ready
      ? 'starting'
      : gesture
        ? 'gesture'
        : pause
          ? 'paused'
          : hearing
            ? 'hearing'
            : checking
              ? 'checking'
              : 'listening'

  const toggle = useCallback(
    (next: boolean) => {
      setProblem(null)
      show(null)
      setOn(next)
    },
    [show],
  )

  return {
    on,
    available,
    status,
    pause,
    resumeAt,
    note,
    problem,
    readLevel: useCallback(() => (paused.current ? 0 : loud.current), []),
    toggle,
    resume,
    dismiss: useCallback(() => setProblem(null), []),
  }
}
