/**
 * Ira's actions on AI → Assistant: the confirm card under an answer that
 * proposed one, and the Action PIN box on the side panel.
 *
 * The card shows the sentence code wrote (not the model's words), how long it
 * waits, and a PIN field with Confirm and Cancel. The PIN goes to the API's
 * confirm endpoint alone: it never joins the conversation, so no model sees
 * it. A wrong PIN says how many tries are left; three lock actions for
 * fifteen minutes. Once it ran, the card says what the desk did ("Done: …")
 * or why it did not ("Not done: …").
 */

import { useEffect, useId, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { clockTime } from '../../lib/ai'
import {
  actionKindLabel,
  isPinShape,
  pinProblem,
  secondsLeft,
  useActionPin,
  useCancelAction,
  useConfirmAction,
  useSetActionPin,
  waitText,
} from '../../lib/aiActions'
import type { ActionOutcome, ProposedAction } from '../../lib/aiActions'
import { ApiError } from '../../lib/api'
import { Badge, Panel } from '../../components/ui'
import { errorText, useNow } from './common'

type CardState = 'waiting' | 'done' | 'failed' | 'cancelled' | 'expired' | 'locked' | 'closed' | 'replaced'

function stateOf(outcome: ActionOutcome | null, expired: boolean, replaced: boolean): CardState {
  if (replaced) return 'replaced'
  if (!outcome || outcome.kind === 'wrong') return expired ? 'expired' : 'waiting'
  switch (outcome.kind) {
    case 'ran':
      return outcome.done ? 'done' : 'failed'
    case 'cancelled':
      return 'cancelled'
    case 'locked':
      return 'locked'
    default:
      return outcome.status === 'expired' ? 'expired' : 'closed'
  }
}

const BADGES: Record<CardState, { label: string; tone: 'pos' | 'neg' | 'warn' | 'neutral' | 'accent' }> = {
  waiting: { label: 'needs your PIN', tone: 'warn' },
  done: { label: 'done', tone: 'pos' },
  failed: { label: 'not done', tone: 'neg' },
  cancelled: { label: 'cancelled', tone: 'neutral' },
  expired: { label: 'expired', tone: 'neutral' },
  locked: { label: 'locked', tone: 'neg' },
  closed: { label: 'closed', tone: 'neutral' },
  replaced: { label: 'replaced', tone: 'neutral' },
}

/**
 * One proposed action under the answer that proposed it. Its outcome is the
 * page's (`outcome`, `onOutcome`), so switching conversations and back finds
 * it as it was; the PIN being typed is the card's alone and is cleared once sent.
 */
export function ActionCard({
  action,
  replaced,
  outcome,
  onOutcome,
  focus,
}: {
  action: ProposedAction
  replaced: boolean
  outcome: ActionOutcome | null
  onOutcome: (outcome: ActionOutcome) => void
  /** Take the keyboard when it appears: the newest answer's card, so the PIN can be typed at once. */
  focus: boolean
}) {
  const confirm = useConfirmAction()
  const cancel = useCancelAction()
  const [pin, setPin] = useState('')
  const inputRef = useRef<HTMLInputElement>(null)
  const id = useId()
  const settled = outcome != null && outcome.kind !== 'wrong'
  const now = useNow(1_000, !settled && !replaced)
  const left = secondsLeft(action.expiresUtc, now)
  const expired = left === 0
  const state = stateOf(outcome, expired, replaced)
  const busy = confirm.isPending || cancel.isPending
  const waiting = state === 'waiting'

  // The newest answer's card takes the keyboard once, when it turns up waiting.
  const focused = useRef(false)
  useEffect(() => {
    if (!focus || focused.current || !waiting) return
    focused.current = true
    inputRef.current?.focus({ preventScroll: true })
  }, [focus, waiting])

  const submit = (e: FormEvent) => {
    e.preventDefault()
    if (!isPinShape(pin) || busy) return
    confirm.mutate(
      { id: action.id, pin },
      {
        onSuccess: (o) => {
          onOutcome(o)
          // The PIN does not stay on the page.
          setPin('')
          if (o.kind === 'wrong') inputRef.current?.focus()
        },
      },
    )
  }

  const drop = () => cancel.mutate(action.id, { onSuccess: onOutcome })
  const badge = BADGES[state]
  const expiredAt = action.expiresUtc ? clockTime(action.expiresUtc, now) : null

  return (
    <section className={`ai-action ai-action--${state}`} aria-labelledby={`${id}-head`}>
      <div className="ai-action__head">
        <span id={`${id}-head`} className="ai-action__kind">
          {actionKindLabel(action.kind)} <span className="faint">· action #{action.id}</span>
        </span>
        <Badge tone={badge.tone}>{badge.label}</Badge>
      </div>
      <p className="ai-action__summary">{action.summary}</p>

      {waiting && (
        <form className="ai-action__form" onSubmit={submit}>
          <label className="ai-action__label" htmlFor={`${id}-pin`}>
            Your Action PIN
          </label>
          <div className="ai-action__row">
            <input
              ref={inputRef}
              id={`${id}-pin`}
              className="field__input ai-action__pin mono"
              type="password"
              inputMode="numeric"
              autoComplete="one-time-code"
              pattern="[0-9]*"
              maxLength={8}
              placeholder="4 to 8 digits"
              aria-describedby={`${id}-note`}
              aria-invalid={outcome?.kind === 'wrong' || undefined}
              value={pin}
              disabled={busy}
              onChange={(e) => setPin(e.target.value.replace(/\D/g, '').slice(0, 8))}
            />
            <button type="submit" className="btn btn--primary btn--sm" disabled={!isPinShape(pin) || busy}>
              {confirm.isPending ? 'Confirming…' : 'Confirm'}
            </button>
            <button type="button" className="btn btn--ghost btn--sm" disabled={busy} onClick={drop}>
              {cancel.isPending ? 'Cancelling…' : 'Cancel'}
            </button>
          </div>
          {outcome?.kind === 'wrong' && (
            <p className="ai-action__wrong" role="alert">
              {outcome.message}
            </p>
          )}
          <p id={`${id}-note`} className="small-note ai-flush">
            <span className="mono">{waitText(left)}</span> · Nothing happens until you confirm. The PIN goes to the desk, never to
            the model.
          </p>
        </form>
      )}
      {state === 'done' && outcome?.kind === 'ran' && (
        <div className="alert alert--success ai-flush" role="status">
          {outcome.message}
        </div>
      )}
      {state === 'failed' && outcome?.kind === 'ran' && (
        <div className="alert alert--error ai-flush" role="alert">
          {outcome.message}
        </div>
      )}
      {(state === 'cancelled' || state === 'locked' || state === 'closed') && outcome && (
        <p className={`small-note ai-flush ${state === 'locked' ? 'neg' : ''}`} role="status">
          {outcome.message}
        </p>
      )}
      {state === 'expired' && (
        <p className="small-note ai-flush" role="status">
          {outcome && outcome.kind === 'closed' ? outcome.message : `Expired${expiredAt ? ` at ${expiredAt} IST` : ''}: nothing was done. Ask again.`}
        </p>
      )}
      {state === 'replaced' && <p className="small-note ai-flush">A newer proposal in this answer replaced it: only the last one can be confirmed.</p>}
    </section>
  )
}

/** The side panel's Action PIN: whether one is set, and setting or resetting it (admin only, like the whole page). */
export function ActionPinCard() {
  const status = useActionPin()
  const save = useSetActionPin()
  const [open, setOpen] = useState(false)
  const [pin, setPin] = useState('')
  const [repeat, setRepeat] = useState('')
  const [saved, setSaved] = useState<string | null>(null)
  const id = useId()
  const s = status.data
  const problem = pinProblem(pin, repeat)
  const now = useNow(30_000, s?.lockedUntilUtc != null)
  const locked = s?.lockedUntilUtc != null && Date.parse(s.lockedUntilUtc) > now

  const submit = (e: FormEvent) => {
    e.preventDefault()
    if (problem || save.isPending) return
    save.mutate(pin, {
      onSuccess: (next) => {
        setSaved(next.set && s?.set ? 'PIN reset.' : 'PIN set.')
        setOpen(false)
        setPin('')
        setRepeat('')
      },
    })
  }

  const close = () => {
    setOpen(false)
    setPin('')
    setRepeat('')
  }

  return (
    <Panel title={<>Action PIN</>} className="ai-side">
      {status.isPending ? (
        <p className="faint ai-flush">Asking the API…</p>
      ) : !s ? (
        <p className="small-note warn ai-flush">
          {status.error instanceof ApiError && status.error.status === 404
            ? 'This API build has no Action PIN yet: it comes with the next deploy.'
            : `The PIN's state could not be read: ${errorText(status.error)}`}
        </p>
      ) : (
        <>
          <p className="ai-side__line">
            <Badge tone={!s.enabled ? 'neutral' : s.set ? 'pos' : 'warn'}>{!s.enabled ? 'actions off' : s.set ? 'set' : 'not set'}</Badge>
            {locked && <Badge tone="neg">locked until {clockTime(s.lockedUntilUtc, now)} IST</Badge>}
          </p>
          <p className="small-note ai-flush">
            The Desk Assistant may propose to stop or start a run, square off manual positions or switch an agent. Nothing runs
            until you type this PIN: in the card under its answer, or in the Telegram chat. It never reaches a model; the desk
            keeps only a salted hash. An action waits {Math.round(s.confirmSeconds / 60) || 1} min; three wrong PINs lock
            actions for 15 min.
          </p>
          {!s.enabled && <p className="small-note ai-flush">Actions are switched off on this desk (Ai:Actions:Enabled).</p>}
          {saved && (
            <div className="alert alert--success ai-flush" role="status">
              {saved}
            </div>
          )}
          {open ? (
            <form className="ai-pinform" onSubmit={submit}>
              <label className="ai-action__label" htmlFor={`${id}-new`}>
                {s.set ? 'New PIN' : 'PIN'}
              </label>
              <input
                id={`${id}-new`}
                className="field__input field__input--sm mono"
                type="password"
                inputMode="numeric"
                autoComplete="new-password"
                pattern="[0-9]*"
                maxLength={8}
                placeholder="4 to 8 digits"
                value={pin}
                onChange={(e) => setPin(e.target.value.replace(/\D/g, '').slice(0, 8))}
              />
              <label className="ai-action__label" htmlFor={`${id}-again`}>
                The same again
              </label>
              <input
                id={`${id}-again`}
                className="field__input field__input--sm mono"
                type="password"
                inputMode="numeric"
                autoComplete="new-password"
                pattern="[0-9]*"
                maxLength={8}
                value={repeat}
                onChange={(e) => setRepeat(e.target.value.replace(/\D/g, '').slice(0, 8))}
              />
              {pin && repeat && problem && <p className="small-note warn ai-flush">{problem}</p>}
              <div className="ai-pair__tools">
                <button type="submit" className="btn btn--primary btn--sm" disabled={problem != null || save.isPending}>
                  {save.isPending ? 'Saving…' : s.set ? 'Reset PIN' : 'Set PIN'}
                </button>
                <button type="button" className="btn btn--ghost btn--sm" disabled={save.isPending} onClick={close}>
                  Cancel
                </button>
              </div>
            </form>
          ) : (
            <div className="ai-pair__tools">
              <button
                type="button"
                className="btn btn--sm"
                onClick={() => {
                  setSaved(null)
                  setOpen(true)
                }}
              >
                {s.set ? 'Reset PIN' : 'Set PIN'}
              </button>
              <span className="faint small">On Telegram: /pin 1234 sets it, /pin old new changes it.</span>
            </div>
          )}
          {save.isError && (
            <div className="alert alert--error ai-flush" role="alert">
              {errorText(save.error)}
            </div>
          )}
        </>
      )}
    </Panel>
  )
}
