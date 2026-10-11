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
 */

import { useRef } from 'react'
import type { KeyboardEvent as ReactKeyboardEvent, MouseEvent as ReactMouseEvent, PointerEvent as ReactPointerEvent } from 'react'
import { IconMic, IconSpeaker, IconStop } from '../../components/icons'
import { HOLD_MS, VOICE_MAX_SECONDS, clockText } from '../../lib/voice'
import type { SpokenReplies, VoiceRecorder } from './voiceHooks'

/**
 * The mic beside Ask. A press that is held is push-to-talk: letting go sends.
 * A click starts a take that runs until Send; a second click cancels it. The
 * keyboard gets the click behaviour (Enter or Space), and Esc cancels.
 */
export function MicButton({
  recorder,
  disabled,
  why,
  onStart,
}: {
  recorder: VoiceRecorder
  disabled: boolean
  why: string | null
  /** Called inside the press that starts a recording, before it starts. */
  onStart: () => void
}) {
  const press = useRef<{ at: number; cancels: boolean } | null>(null)
  const on = recorder.state !== 'idle'
  const begin = (held: boolean) => {
    onStart()
    recorder.start(held)
  }

  const onPointerDown = (e: ReactPointerEvent<HTMLButtonElement>) => {
    if (e.button !== 0 || (disabled && !on)) return
    e.preventDefault()
    e.currentTarget.setPointerCapture?.(e.pointerId)
    press.current = { at: performance.now(), cancels: on }
    if (!on) begin(true)
  }
  const onPointerUp = () => {
    const p = press.current
    press.current = null
    if (!p) return
    if (p.cancels) recorder.cancel()
    else if (performance.now() - p.at >= HOLD_MS && recorder.heldLongEnough()) recorder.send()
    // A quick press, or a hold the mic was still starting through: it carries on until Send, the mic again, or Esc.
    else recorder.release()
  }
  const onClick = (e: ReactMouseEvent<HTMLButtonElement>) => {
    // Pointer presses are handled above; this is Enter or Space (or a screen reader's click).
    if (e.detail !== 0) return
    if (on) recorder.cancel()
    else if (!disabled) begin(false)
  }
  const onKeyDown = (e: ReactKeyboardEvent<HTMLButtonElement>) => {
    if (e.key === 'Escape' && on) {
      e.preventDefault()
      recorder.cancel()
    }
  }

  const title = on
    ? 'Recording: Send asks it; click the mic again or press Esc to cancel'
    : (why ?? 'Ask by voice: hold to talk, or click to start recording')
  return (
    <button
      type="button"
      className="btn btn--sm ai-mic"
      aria-label="Ask by voice"
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
      <IconMic />
    </button>
  )
}

/** The composer's recording line: the clock, the level, and what ends it; or the wait while it is written down. */
export function RecordingStrip({
  recorder,
  hearing,
  onStopHearing,
  keyboard,
}: {
  recorder: VoiceRecorder
  hearing: boolean
  onStopHearing: () => void
  /** A keyboard is likely (a fine pointer): Esc is worth naming. */
  keyboard: boolean
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
          : `Send asks it · ${keyboard ? 'Esc or the mic cancels' : 'the mic again cancels'}`}
      </span>
    </div>
  )
}

/** "Speak replies": the viewer's own choice, off by default. */
export function SpeakToggle({ on, onChange }: { on: boolean; onChange: (on: boolean) => void }) {
  return (
    <button
      type="button"
      className="btn btn--ghost btn--sm ai-speak"
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
