/**
 * Talking to the desk on AI → Assistant ("Ira"): the mic beside Ask, the
 * recording state in the composer, and spoken replies.
 *
 * The mic records in the browser (lib/micRecorder, 16 kHz mono WAV) and the
 * page sends it to be written down (lib/voice transcribe); the transcript is
 * then asked exactly as a typed question. Two ways to record:
 *   - hold the mic and speak: letting go sends;
 *   - click it (or Enter/Space on it): it records until Send; a second click on
 *     the mic, or Esc, cancels.
 * It stops by itself at two minutes and sends what it has.
 *
 * Spoken replies are the viewer's choice, off by default and kept in this
 * browser: when on, a finished answer is fetched as speech (lib/voice speak)
 * and played. One audio element plays at a time; Stop stops it, and a new
 * question or a new recording stops it too.
 *
 * Hands-free is the viewer's choice too, off by default: on, the page listens
 * without a press and asks whatever starts with the assistant's name
 * ("Ira, …"); the API decides whether it did (voiceHooks useHandsFree). A
 * line in the composer says it is listening, with a level, or why it paused.
 *
 * The phone app (/ira) draws the same controls bigger: its own class names,
 * and `tapSends` on the mic, so a tap starts a take and the next tap asks it
 * (a phone has no Esc, and a Send button would sit away from the thumb).
 */

import { useEffect, useRef, useState } from 'react'
import type { KeyboardEvent as ReactKeyboardEvent, MouseEvent as ReactMouseEvent, PointerEvent as ReactPointerEvent, ReactNode } from 'react'
import { IconHandsFree, IconMic, IconSpeaker, IconStop } from '../../components/icons'
import { HOLD_MS, VOICE_MAX_SECONDS, clockText } from '../../lib/voice'
import type { HandsFree, HandsFreePause, SpokenReplies, VoiceRecorder } from './voiceHooks'

/**
 * The mic beside Ask. A press that is held is push-to-talk: letting go sends.
 * A click starts a take that runs until Send; a second click cancels it (or,
 * with `tapSends`, asks it). The keyboard gets the click behaviour (Enter or
 * Space), and Esc cancels.
 */
export function MicButton({
  recorder,
  disabled,
  why,
  onStart,
  className = 'btn btn--sm ai-mic',
  tapSends = false,
  children,
}: {
  recorder: VoiceRecorder
  disabled: boolean
  why: string | null
  /** Called inside the press that starts a recording, before it starts. */
  onStart: () => void
  /** Replaces the console's small button classes (the phone app's big round mic). */
  className?: string
  /** A tap on the mic while a clicked take records asks it, rather than cancelling it. */
  tapSends?: boolean
  /** Replaces the mic icon. */
  children?: ReactNode
}) {
  const press = useRef<{ at: number; ends: boolean } | null>(null)
  const on = recorder.state !== 'idle'
  const begin = (held: boolean) => {
    onStart()
    recorder.start(held)
  }
  // A second press ends the take: asked when something was recorded and the mic asks on a tap; otherwise dropped.
  const end = () => {
    if (tapSends && recorder.state === 'recording') recorder.send()
    else recorder.cancel()
  }

  const onPointerDown = (e: ReactPointerEvent<HTMLButtonElement>) => {
    if (e.button !== 0 || (disabled && !on)) return
    e.preventDefault()
    e.currentTarget.setPointerCapture?.(e.pointerId)
    press.current = { at: performance.now(), ends: on }
    if (!on) begin(true)
  }
  const onPointerUp = () => {
    const p = press.current
    press.current = null
    if (!p) return
    if (p.ends) end()
    else if (performance.now() - p.at >= HOLD_MS && recorder.heldLongEnough()) recorder.send()
    // A quick press, or a hold the mic was still starting through: it carries on until Send, the mic again, or Esc.
    else recorder.release()
  }
  const onClick = (e: ReactMouseEvent<HTMLButtonElement>) => {
    // Pointer presses are handled above; this is Enter or Space (or a screen reader's click).
    if (e.detail !== 0) return
    if (on) end()
    else if (!disabled) begin(false)
  }
  const onKeyDown = (e: ReactKeyboardEvent<HTMLButtonElement>) => {
    if (e.key === 'Escape' && on) {
      e.preventDefault()
      recorder.cancel()
    }
  }

  const title = on
    ? tapSends
      ? 'Recording: tap to ask it, or let go if you are holding it'
      : 'Recording: Send asks it; click the mic again or press Esc to cancel'
    : (why ?? 'Ask by voice: hold to talk, or click to start recording')
  return (
    <button
      type="button"
      className={className}
      aria-label={on && tapSends ? 'Ask what was recorded' : 'Ask by voice'}
      aria-pressed={on}
      title={title}
      disabled={disabled && !on}
      onPointerDown={onPointerDown}
      onPointerUp={onPointerUp}
      onPointerCancel={() => (press.current = null)}
      onContextMenu={(e) => e.preventDefault()}
      onClick={onClick}
      onKeyDown={onKeyDown}
    >
      {children ?? <IconMic />}
    </button>
  )
}

/** The composer's recording line: the clock, the level, and what ends it; or the wait while it is written down. */
export function RecordingStrip({
  recorder,
  hearing,
  onStopHearing,
  keyboard,
  tapSends = false,
}: {
  recorder: VoiceRecorder
  hearing: boolean
  onStopHearing: () => void
  /** A keyboard is likely (a fine pointer): Esc is worth naming. */
  keyboard: boolean
  /** The mic asks a clicked take on its next tap (the phone app). */
  tapSends?: boolean
}) {
  if (hearing) {
    return (
      <div className="ai-rec ai-rec--hearing" role="status">
        <span className="ai-rec__spin" aria-hidden="true" />
        <span>Writing it down…</span>
        <button type="button" className="btn btn--ghost btn--sm ai-rec__end" onClick={onStopHearing}>
          Cancel
        </button>
      </div>
    )
  }
  if (recorder.state === 'idle') return null
  const starting = recorder.state === 'starting'
  return (
    <div className="ai-rec" role="status">
      <span className="ai-rec__dot" aria-hidden="true" />
      <span className="ai-rec__what">{starting ? 'Waiting for the microphone…' : 'Recording'}</span>
      {!starting && (
        <>
          <span className="ai-rec__time" aria-hidden="true">
            {clockText(recorder.seconds)} <span className="faint">/ {clockText(VOICE_MAX_SECONDS)}</span>
          </span>
          <span className="ai-rec__meter" aria-hidden="true">
            <span style={{ transform: `scaleX(${recorder.level.toFixed(3)})` }} />
          </span>
        </>
      )}
      <span className="ai-rec__hint faint">
        {recorder.held
          ? `Let go to ask${keyboard ? ' · Esc cancels' : ''}`
          : tapSends
            ? 'Tap the mic to ask it'
            : `Send asks it · ${keyboard ? 'Esc or the mic cancels' : 'the mic again cancels'}`}
      </span>
    </div>
  )
}

/** "Speak replies": the viewer's own choice, off by default. */
export function SpeakToggle({
  on,
  onChange,
  className = 'btn btn--ghost btn--sm ai-speak',
}: {
  on: boolean
  onChange: (on: boolean) => void
  className?: string
}) {
  return (
    <button
      type="button"
      className={className}
      aria-pressed={on}
      aria-label="Speak replies"
      title={on ? 'Answers are read aloud when they finish (in this browser). Click to stop.' : 'Read answers aloud when they finish (in this browser)'}
      onClick={() => onChange(!on)}
    >
      <IconSpeaker />
      <span className="ai-speak__label">Speak replies</span>
    </button>
  )
}

/**
 * "Hands-free": the viewer's own switch, off by default and kept in this
 * browser. On, the page listens for the assistant's name without a press.
 * `why` says why it cannot be turned on here (voice off on the desk, an API
 * without it); null when it can.
 */
export function HandsFreeToggle({
  handsFree,
  name,
  why,
  onTurnOn,
  className = 'btn btn--ghost btn--sm ai-hands',
}: {
  handsFree: HandsFree
  name: string
  why: string | null
  /** Called inside the click that turns it on: a gesture, for playback later. */
  onTurnOn: () => void
  className?: string
}) {
  const on = handsFree.on && handsFree.available
  const title = why ?? (on ? `Listening for "${name}" (in this browser). Click to stop and let go of the microphone.` : `Listen for "${name}, …" without pressing anything (in this browser)`)
  return (
    <button
      type="button"
      className={className}
      aria-pressed={on}
      aria-label="Hands-free"
      title={title}
      disabled={why != null && !on}
      onClick={() => {
        if (on) handsFree.toggle(false)
        else {
          onTurnOn()
          handsFree.toggle(true)
        }
      }}
    >
      <IconHandsFree />
      <span className="ai-hands__label">Hands-free</span>
    </button>
  )
}

/** The microphone's level, redrawn ten times a second from the hook's reading, without rendering the page. */
function LiveMeter({ read }: { read: () => number }) {
  const bar = useRef<HTMLSpanElement>(null)
  useEffect(() => {
    const id = window.setInterval(() => {
      if (bar.current) bar.current.style.transform = `scaleX(${read().toFixed(3)})`
    }, 100)
    return () => window.clearInterval(id)
  }, [read])
  return (
    <span className="ai-rec__meter ai-hf__meter" aria-hidden="true">
      <span ref={bar} style={{ transform: 'scaleX(0)' }} />
    </span>
  )
}

/** Seconds until `at`, as a clock, kept current. */
function Countdown({ at }: { at: number }) {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const id = window.setInterval(() => setNow(Date.now()), 1_000)
    return () => window.clearInterval(id)
  }, [])
  return <>{clockText(Math.ceil(Math.max(0, at - now) / 1000))}</>
}

function pausedText(pause: HandsFreePause, name: string): string {
  switch (pause) {
    case 'speaking':
      return `Paused while ${name} speaks`
    case 'answering':
      return 'Paused while the answer comes'
    case 'recording':
      return 'Paused while you use the mic'
    case 'blocked':
      return 'Paused: this agent cannot be asked right now'
    case 'hidden':
      return 'Paused while this tab is hidden'
    case 'cap':
      return 'Paused: too much speech'
    case 'resuming':
      return `Listening for ${name}`
  }
}

/**
 * Hands-free's line in the composer while it is on: what it is doing, in
 * words a screen reader is told (aria-live), and the microphone's level. A
 * moment's note when something was heard but not said to the assistant. Off,
 * it shows only why it turned itself off, if it did.
 */
export function HandsFreeStrip({ handsFree, name }: { handsFree: HandsFree; name: string }) {
  const { status, pause, note } = handsFree
  if (status === 'off') {
    if (!handsFree.problem) return null
    return (
      <p className="small-note warn ai-flush ai-voice-problem" role="alert">
        <span>{handsFree.problem}</span>
        <button type="button" className="btn btn--ghost btn--sm" onClick={handsFree.dismiss}>
          Dismiss
        </button>
      </p>
    )
  }

  const live = status === 'listening' || status === 'hearing' || status === 'checking' || pause === 'resuming'
  const what =
    status === 'starting'
      ? 'Waiting for the microphone…'
      : status === 'gesture'
        ? 'The browser holds the microphone until you click'
        : pause
          ? pausedText(pause, name)
          : `Listening for ${name}`
  const doing = status === 'hearing' ? 'hearing you…' : status === 'checking' ? `checking for “${name}”…` : null
  return (
    <div className={`ai-hf ${live ? 'is-live' : 'is-paused'}`} role="status" aria-live="polite">
      <span className="ai-hf__dot" aria-hidden="true" />
      <span className="ai-hf__what">{what}</span>
      {pause === 'cap' && handsFree.resumeAt != null && (
        <span className="ai-hf__wait faint" aria-hidden="true">
          · back in <Countdown at={handsFree.resumeAt} />
        </span>
      )}
      {status === 'gesture' && (
        <button type="button" className="btn btn--sm" onClick={handsFree.resume}>
          Start listening
        </button>
      )}
      {live && <LiveMeter read={handsFree.readLevel} />}
      {doing && <span className="ai-hf__doing faint">{doing}</span>}
      {note && <span className={`ai-hf__note ai-hf__note--${note.kind}`}>{note.text}</span>}
      {!note && status === 'listening' && <span className="ai-hf__hint faint">Say “{name}, …”</span>}
    </div>
  )
}

/** The line that says a reply is being fetched or spoken, with its stop; or why it was not. */
export function PlaybackBar({ replies }: { replies: SpokenReplies }) {
  if (replies.state === 'idle') {
    if (!replies.problem) return null
    return (
      <p className="small-note warn ai-flush ai-voice-problem" role="alert">
        <span>{replies.problem}</span>
        <button type="button" className="btn btn--ghost btn--sm" onClick={replies.dismiss}>
          Dismiss
        </button>
      </p>
    )
  }
  return (
    <div className="ai-playback" role="status">
      <IconSpeaker aria-hidden="true" />
      <span>
        {replies.state === 'fetching'
          ? 'Getting the spoken reply…'
          : replies.state === 'playing'
            ? 'Speaking the answer'
            : 'The browser held the spoken reply back until you press Play.'}
      </span>
      {replies.state === 'blocked' && (
        <button type="button" className="btn btn--sm" onClick={replies.resume}>
          Play
        </button>
      )}
      <button type="button" className="btn btn--ghost btn--sm ai-playback__stop" onClick={replies.stop}>
        <IconStop /> Stop
      </button>
    </div>
  )
}
