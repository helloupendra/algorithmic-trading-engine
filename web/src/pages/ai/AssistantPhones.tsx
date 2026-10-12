/**
 * The Phones box: "Hey Siri, Ira". On AI → Assistant's side panel and in the
 * Ira app's settings sheet (/ira).
 *
 * Each phone gets a token of its own, made here and shown once: the desk keeps
 * only its SHA-256, and it opens POST /api/Ira/ask alone, never the console.
 * An Apple Shortcut dictates the question, posts it with the token, and speaks
 * the answer's short `spoken` line. Revoke works from the next request. The
 * setup guide lists the Shortcut's five actions exactly, with this console's
 * own address; Android needs no token: the Ira app and "Hey Google, open Ira".
 */

import { useId, useState } from 'react'
import type { FormEvent } from 'react'
import { clockTime } from '../../lib/ai'
import { iraAskUrl, phoneLabelProblem, shortcutSteps, useCreatePhone, usePhones, useRevokePhone } from '../../lib/ira'
import type { PhoneTokenMade } from '../../lib/ira'
import { ApiError } from '../../lib/api'
import { Badge, Panel } from '../../components/ui'
import { CopyButton } from './parts'
import { errorText, useNow } from './common'

export function PhonesCard({ className = 'ai-side', showAppLink = true }: { className?: string; showAppLink?: boolean }) {
  const phones = usePhones()
  const create = useCreatePhone()
  const revoke = useRevokePhone()
  const [label, setLabel] = useState('')
  const [adding, setAdding] = useState(false)
  const [made, setMade] = useState<PhoneTokenMade | null>(null)
  const [confirm, setConfirm] = useState<number | null>(null)
  const id = useId()
  const now = useNow(60_000)
  const p = phones.data
  const askUrl = iraAskUrl(typeof window === 'undefined' ? 'https://www.openfno.com' : window.location.origin)
  const problem = phoneLabelProblem(label)
  const full = p != null && p.tokens.length >= p.maxTokens

  const submit = (e: FormEvent) => {
    e.preventDefault()
    if (problem || create.isPending) return
    create.mutate(label, {
      onSuccess: (m) => {
        setMade(m)
        setAdding(false)
        setLabel('')
      },
    })
  }

  return (
    <Panel title={<>Phones</>} className={`ai-phones ${className}`}>
      {phones.isPending ? (
        <p className="faint ai-flush">Asking the API…</p>
      ) : !p ? (
        <p className="small-note warn ai-flush">
          {phones.error instanceof ApiError && phones.error.status === 404
            ? 'This API build has no phone tokens yet: they come with the next deploy.'
            : `The phones could not be read: ${errorText(phones.error)}`}
        </p>
      ) : (
        <>
          <p className="ai-side__line">
            <Badge tone={p.enabled ? 'pos' : 'neutral'}>{p.enabled ? 'on' : 'off'}</Badge>
            <span className="faint">
              {p.tokens.length} of {p.maxTokens} phone{p.maxTokens === 1 ? '' : 's'}
            </span>
          </p>
          <p className="small-note ai-flush">
            "Hey Siri, Ira" runs an Apple Shortcut that sends what you say to the Desk Assistant and speaks its answer. Each phone
            has a token of its own: it is shown once, opens only the Shortcut's endpoint, and the desk keeps only its hash.
          </p>
          {!p.enabled && <p className="small-note ai-flush">Phone asks are switched off on this desk (Ai:Phone:Enabled).</p>}

          {p.tokens.length > 0 && (
            <ul className="ai-owners">
              {p.tokens.map((t) => (
                <li key={t.id} className="ai-owner">
                  <span className="ai-owner__who">
                    <b>{t.label}</b>
                    <span className="faint">
                      {' '}
                      · made {clockTime(t.createdUtc, now)} IST · {t.lastUsedUtc ? `last asked ${clockTime(t.lastUsedUtc, now)} IST` : 'not used yet'}
                    </span>
                  </span>
                  {confirm === t.id ? (
                    <span className="ai-owner__confirm">
                      <span className="small">Its Shortcut stops working.</span>
                      <button
                        type="button"
                        className="btn btn--danger btn--sm"
                        disabled={revoke.isPending}
                        onClick={() => revoke.mutate(t.id, { onSuccess: () => setConfirm(null) })}
                      >
                        {revoke.isPending ? 'Revoking…' : 'Revoke'}
                      </button>
                      <button type="button" className="btn btn--ghost btn--sm" onClick={() => setConfirm(null)}>
                        Cancel
                      </button>
                    </span>
                  ) : (
                    <button type="button" className="btn btn--ghost btn--sm" onClick={() => setConfirm(t.id)}>
                      Revoke
                    </button>
                  )}
                </li>
              ))}
            </ul>
          )}
          {revoke.isError && (
            <div className="alert alert--error ai-flush" role="alert">
              {errorText(revoke.error)}
            </div>
          )}

          {made && (
            <div className="ai-pair ai-phones__made" role="status">
              <span className="ai-action__label">Token for {made.label}</span>
              <code className="ai-phones__token mono" aria-label="The phone token">
                {made.token}
              </code>
              <p className="small-note ai-flush">
                Shown once: copy it into the Shortcut's Authorization header now, as <span className="mono">Bearer</span> and the token.
                The desk cannot show it again.
              </p>
              <div className="ai-pair__tools">
                <CopyButton text={made.token} label="Copy token" className="btn btn--primary btn--sm" />
                <button type="button" className="btn btn--ghost btn--sm" onClick={() => setMade(null)}>
                  Done
                </button>
              </div>
            </div>
          )}

          {adding ? (
            <form className="ai-pinform" onSubmit={submit}>
              <label className="ai-action__label" htmlFor={`${id}-label`}>
                The phone's name
              </label>
              <input
                id={`${id}-label`}
                className="field__input field__input--sm"
                maxLength={60}
                placeholder="Upendra's iPhone"
                autoComplete="off"
                value={label}
                onChange={(e) => setLabel(e.target.value)}
              />
              <div className="ai-pair__tools">
                <button type="submit" className="btn btn--primary btn--sm" disabled={problem != null || create.isPending}>
                  {create.isPending ? 'Making…' : 'Make a token'}
                </button>
                <button type="button" className="btn btn--ghost btn--sm" disabled={create.isPending} onClick={() => setAdding(false)}>
                  Cancel
                </button>
              </div>
            </form>
          ) : (
            !made && (
              <div className="ai-pair__tools">
                <button
                  type="button"
                  className="btn btn--sm"
                  disabled={!p.enabled || full}
                  title={full ? 'Revoke a phone to add another' : undefined}
                  onClick={() => setAdding(true)}
                >
                  Add a phone
                </button>
                {full && <span className="faint small">At the most: revoke one to add another.</span>}
              </div>
            )
          )}
          {create.isError && (
            <div className="alert alert--error ai-flush" role="alert">
              {errorText(create.error)}
            </div>
          )}

          <details className="ai-phones__guide">
            <summary>Set up "Hey Siri, Ira"</summary>
            <ol className="ai-phones__steps">
              {shortcutSteps(askUrl).map((s) => (
                <li key={s.action}>
                  <b>{s.action}</b>
                  <span className="faint"> · {s.detail}</span>
                </li>
              ))}
            </ol>
            <p className="ai-pair__tools ai-flush">
              <CopyButton text={askUrl} label="Copy the address" className="btn btn--ghost btn--sm" />
            </p>
            <p className="small-note ai-flush">
              Then say "Hey Siri, Ira", and ask. An action Ira proposes waits {showAppLink ? <a href="/ira">in the Ira app</a> : 'in this app'}{' '}
              for your PIN; nothing is done from the Shortcut.
            </p>
            <p className="small-note ai-flush">
              Android: open {showAppLink ? <a href="/ira">/ira</a> : '/ira'} in Chrome, add it to the home screen, and say "Hey Google, open
              Ira".
            </p>
          </details>
        </>
      )}
    </Panel>
  )
}
