/**
 * The state behind talking to the desk on AI → Assistant (AssistantVoice.tsx
 * draws it): the microphone as a recorder, and spoken replies through one
 * audio element. Hooks only, so the components file holds only components.
 */

import { useCallback, useEffect, useRef, useState } from 'react'
import { startMic } from '../../lib/micRecorder'
import type { MicTake } from '../../lib/micRecorder'
import { VOICE_MAX_SECONDS, VoiceRefusal, micErrorText, speak, voiceFailureText } from '../../lib/voice'
import { isAbort } from '../../lib/ai'
import { encodeWav } from '../../lib/wav'

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
