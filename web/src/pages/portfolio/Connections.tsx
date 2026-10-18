/**
 * The linked accounts: one row a broker, the first-run panel when none is
 * linked, and the dialogs that link and unlink one.
 *
 * Linking is read-only and never asks for the broker's password, PIN or TOTP:
 * the user signs in on the broker's own page (a redirect), or pastes an
 * access token the broker issued (a form). The form's inputs are read once on
 * submit and the form is cleared; no token is ever held in React state.
 */

import { useEffect, useRef, useState } from 'react'
import type { FormEvent, ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { formatAge } from '../../lib/format'
import {
  PORTFOLIO_BROKERS,
  connectionView,
  formatStamp,
  isSecretField,
  submitPortfolioToken,
  useDisconnect,
  useInvalidatePortfolio,
} from '../../lib/portfolio'
import type { PortfolioConnection, TokenField } from '../../lib/portfolio'
import { Badge, InlineError, Panel } from '../../components/ui'
import { IconX } from '../../components/icons'

/** What linking means, in the words the owner asked for. */
export function ReadOnlyNote({ className = '' }: { className?: string }) {
  return (
    <p className={`pf-readonly ${className}`}>
      <b>Read-only.</b> A link can read your holdings, positions and funds; it cannot place, change or cancel an order. We
      never ask for your broker password, PIN or TOTP: you sign in on the broker's own page, or paste an access token the
      broker issued, which the server keeps encrypted.
    </p>
  )
}

const FYERS_NOTE = 'A FYERS link lasts the trading day. Reconnect each morning to refresh it.'

/** How a broker is linked, in a few words, for the first-run panel. */
function howItLinks(c: PortfolioConnection): string {
  return c.connectMode === 'redirect' ? `Sign in on ${c.name}'s own page` : `Paste an access token from ${c.name}`
}

/** The line after the account label: when it last synced, and when the link ends. */
function linkMeta(c: PortfolioConnection, nowMs: number): ReactNode[] {
  const view = connectionView(c, nowMs)
  const parts: ReactNode[] = []
  if (view.status === 'not_connected') {
    if (c.lastSyncUtc) parts.push(<span key="kept" title={formatStamp(c.lastSyncUtc)}>snapshots kept, last read {formatStamp(c.lastSyncUtc)}</span>)
    return parts
  }
  parts.push(
    c.lastSyncUtc ? (
      <span key="sync" title={`Last synced ${formatStamp(c.lastSyncUtc)} IST`}>
        synced {formatAge(c.lastSyncUtc)}
      </span>
    ) : (
      <span key="sync">not synced yet</span>
    ),
  )
  if (c.expiresUtc) {
    parts.push(
      <span key="exp" className={view.status === 'expired' ? 'warn' : undefined}>
        {view.status === 'expired' ? 'expired' : 'link until'} {formatStamp(c.expiresUtc)}
      </span>,
    )
  }
  return parts
}

function ConnectionRow({
  c,
  nowMs,
  busy,
  onConnect,
  onDisconnect,
}: {
  c: PortfolioConnection
  nowMs: number
  busy: boolean
  onConnect: (c: PortfolioConnection) => void
  onDisconnect: (c: PortfolioConnection) => void
}) {
  const view = connectionView(c, nowMs)
  const meta = linkMeta(c, nowMs)
  return (
    <li className={`pf-link pf-link--${view.status}`}>
      <div className="pf-link__name">{c.name}</div>
      <div className="pf-link__state">
        <Badge tone={view.tone}>{view.label}</Badge>
      </div>
      <div className="pf-link__acct mono" title={c.accountLabel ? 'Account, as the broker masks it' : undefined}>
        {c.accountLabel ?? <span className="faint">—</span>}
      </div>
      <div className="pf-link__meta">
        {meta.map((m, i) => (
          <span key={i}>
            {i > 0 && <span className="pf-dot" aria-hidden="true"> · </span>}
            {m}
          </span>
        ))}
      </div>
      <div className="pf-link__actions">
        {view.action && (
          <button
            type="button"
            className={`btn btn--sm${view.action === 'Reconnect' ? ' btn--primary' : ''}`}
            onClick={() => onConnect(c)}
            disabled={busy}
            aria-label={`${view.action} ${c.name}`}
          >
            {busy ? 'Opening…' : view.action}
          </button>
        )}
        {view.canDisconnect && (
          <button type="button" className="btn btn--sm btn--ghost" onClick={() => onDisconnect(c)} aria-label={`Disconnect ${c.name}`}>
            Disconnect
          </button>
        )}
      </div>
      {/* Linked but failing (the last sync was refused) says so here too, not only in the report. */}
      {view.status !== 'not_connected' && c.lastError && (
        <p className={`pf-link__note ${view.status === 'error' ? 'pf-link__note--err' : 'pf-link__note--warn'}`}>
          {view.status === 'connected' ? `Last sync failed: ${c.lastError}` : c.lastError}
        </p>
      )}
      {c.broker === 'fyers' && <p className="pf-link__note">{FYERS_NOTE}</p>}
    </li>
  )
}

export function ConnectionsPanel({
  connections,
  nowMs,
  pendingBroker,
  onConnect,
  onDisconnect,
  sync,
}: {
  connections: PortfolioConnection[]
  nowMs: number
  pendingBroker: string | null
  onConnect: (c: PortfolioConnection) => void
  onDisconnect: (c: PortfolioConnection) => void
  /** The Sync now control, in the panel's head. */
  sync: ReactNode
}) {
  return (
    <Panel title="Linked accounts" actions={sync} className="pf-links">
      <ul className="pf-links__list">
        {connections.map((c) => (
          <ConnectionRow
            key={c.broker}
            c={c}
            nowMs={nowMs}
            busy={pendingBroker === c.broker}
            onConnect={onConnect}
            onDisconnect={onDisconnect}
          />
        ))}
      </ul>
      <ReadOnlyNote />
    </Panel>
  )
}

/** Nothing linked yet: what the section does, and a Connect button a broker. */
export function FirstRunPanel({
  connections,
  pendingBroker,
  onConnect,
}: {
  connections: PortfolioConnection[]
  pendingBroker: string | null
  onConnect: (c: PortfolioConnection) => void
}) {
  // The server's list, in the order the owner names them; a broker it adds later goes last.
  const order = (b: string) => {
    const i = (PORTFOLIO_BROKERS as readonly string[]).indexOf(b)
    return i === -1 ? PORTFOLIO_BROKERS.length : i
  }
  const shown = [...connections].sort((a, b) => order(a.broker) - order(b.broker))
  return (
    <section className="panel pf-first" aria-labelledby="pf-first-title">
      <h2 className="pf-first__title" id="pf-first-title">
        Link a broker account to see your portfolio
      </h2>
      <p className="pf-first__lead">
        Portfolio reads your own demat or broker account and lays out the facts in one place: what you own, what it is
        worth, your P&amp;L, and how it is spread across sectors, brokers and company sizes.
      </p>
      <ul className="pf-first__brokers">
        {shown.map((c) => (
          <li key={c.broker} className="pf-first__broker">
            <div>
              <b>{c.name}</b>
              <span className="pf-first__how">{howItLinks(c)}</span>
            </div>
            <button type="button" className="btn" onClick={() => onConnect(c)} disabled={pendingBroker === c.broker}>
              {pendingBroker === c.broker ? 'Opening…' : `Connect ${c.name}`}
            </button>
          </li>
        ))}
      </ul>
      {shown.some((c) => c.broker === 'fyers') && <p className="pf-first__note">{FYERS_NOTE}</p>}
      <ReadOnlyNote />
    </section>
  )
}

// ---------- dialogs -----------------------------------------------------------

/**
 * A modal's keyboard rules: the control marked data-autofocus (else the card)
 * takes focus on open, Tab stays inside the card, Escape closes it, and focus
 * goes back to the button that opened it.
 */
function useDialog(onClose: () => void) {
  const cardRef = useRef<HTMLDivElement>(null)
  const closeRef = useRef(onClose)
  useEffect(() => {
    closeRef.current = onClose
  }, [onClose])
  useEffect(() => {
    const before = document.activeElement as HTMLElement | null
    const card = cardRef.current
    ;(card?.querySelector<HTMLElement>('[data-autofocus]') ?? card)?.focus()
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.preventDefault()
        closeRef.current()
        return
      }
      if (e.key !== 'Tab' || !card) return
      const focusable = [...card.querySelectorAll<HTMLElement>('button, input, select, textarea, a[href]')].filter(
        (el) => !el.hasAttribute('disabled'),
      )
      if (focusable.length === 0) return
      const first = focusable[0]
      const last = focusable[focusable.length - 1]
      const at = document.activeElement
      if (e.shiftKey && (at === first || !card.contains(at))) {
        e.preventDefault()
        last.focus()
      } else if (!e.shiftKey && (at === last || !card.contains(at))) {
        e.preventDefault()
        first.focus()
      }
    }
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('keydown', onKey)
      before?.focus?.()
    }
  }, [])
  return cardRef
}

function Dialog({ titleId, title, onClose, children }: { titleId: string; title: string; onClose: () => void; children: ReactNode }) {
  const cardRef = useDialog(onClose)
  return createPortal(
    <div className="modal" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className="modal__card modal__card--sm" role="dialog" aria-modal="true" aria-labelledby={titleId} tabIndex={-1} ref={cardRef}>
        <div className="modal__body">
          <div className="modal__head">
            <h2 className="modal__title" id={titleId}>
              {title}
            </h2>
            <button type="button" className="btn btn--ghost btn--sm" onClick={onClose} aria-label="Close" title="Close (Esc)">
              <IconX style={{ width: 14, height: 14 }} />
            </button>
          </div>
          {children}
        </div>
      </div>
    </div>,
    document.body,
  )
}

/**
 * The token form. Its inputs are uncontrolled: the values are read from the
 * form once on submit, sent, and the form is cleared whatever the answer, so
 * no token outlives the request in the page.
 */
export function TokenDialog({
  connection,
  fields,
  onClose,
  onLinked,
}: {
  connection: PortfolioConnection
  fields: TokenField[]
  onClose: () => void
  onLinked: (name: string) => void
}) {
  const invalidate = useInvalidatePortfolio()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const name = connection.name

  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const form = e.currentTarget
    const data = new FormData(form)
    const values: Record<string, string> = {}
    for (const f of fields) values[f.key] = String(data.get(f.key) ?? '').trim()
    const empty = fields.find((f) => !values[f.key])
    if (empty) {
      setError(`${empty.label} is empty.`)
      form.querySelector<HTMLInputElement>(`[name="${CSS.escape(empty.key)}"]`)?.focus()
      return
    }
    setBusy(true)
    setError(null)
    try {
      await submitPortfolioToken(connection.broker, values)
      form.reset()
      void invalidate()
      onLinked(name)
    } catch (err) {
      form.reset()
      setError(err instanceof Error ? err.message.split('\n')[0] : 'The broker could not be linked.')
      form.querySelector<HTMLInputElement>('input')?.focus()
    } finally {
      setBusy(false)
    }
  }

  return (
    <Dialog titleId="pf-token-title" title={`Link ${name}`} onClose={onClose}>
      <form className="pf-form" onSubmit={submit} autoComplete="off" noValidate>
        <p className="muted pf-flush">
          Paste what {name} issued for your account. It is sent once over an encrypted connection and kept encrypted on
          the server; this page does not keep it. We never ask for your {name} password, PIN or TOTP.
        </p>
        {fields.length === 0 ? (
          <p className="alert alert--error">The server did not say which fields {name} needs.</p>
        ) : (
          fields.map((f, i) => {
            const id = `pf-field-${f.key}`
            return (
              <div className="field" key={f.key}>
                <label className="field__label" htmlFor={id}>
                  {f.label}
                </label>
                <input
                  id={id}
                  name={f.key}
                  className="field__input"
                  type={isSecretField(f.key) ? 'password' : 'text'}
                  autoComplete={isSecretField(f.key) ? 'new-password' : 'off'}
                  autoCapitalize="off"
                  autoCorrect="off"
                  spellCheck={false}
                  data-1p-ignore="true"
                  data-lpignore="true"
                  aria-describedby={f.help ? `${id}-help` : undefined}
                  data-autofocus={i === 0 ? '' : undefined}
                />
                {f.help && (
                  <span className="field__help" id={`${id}-help`}>
                    {f.help}
                  </span>
                )}
              </div>
            )
          })
        )}
        {error && (
          <div className="alert alert--error" role="alert">
            {error}
          </div>
        )}
        <div className="modal__foot">
          <button type="button" className="btn" onClick={onClose}>
            Cancel
          </button>
          <button type="submit" className="btn btn--primary" disabled={busy || fields.length === 0}>
            {busy ? 'Linking…' : `Link ${name}`}
          </button>
        </div>
      </form>
    </Dialog>
  )
}

export function DisconnectDialog({
  connection,
  onClose,
  onDone,
}: {
  connection: PortfolioConnection
  onClose: () => void
  onDone: (message: string) => void
}) {
  const disconnect = useDisconnect()
  const [forget, setForget] = useState(false)
  const name = connection.name

  function confirm(e: FormEvent) {
    e.preventDefault()
    disconnect.mutate(
      { broker: connection.broker, forget },
      {
        onSuccess: () =>
          onDone(forget ? `${name} is unlinked and its saved snapshots are deleted.` : `${name} is unlinked. Its saved snapshots are kept.`),
      },
    )
  }

  return (
    <Dialog titleId="pf-unlink-title" title={`Disconnect ${name}?`} onClose={onClose}>
      <form className="pf-form" onSubmit={confirm}>
        <p className="muted pf-flush">
          The link to {name} is removed and the access token the server holds for it is deleted. You can link it again at
          any time.
        </p>
        <label className="pf-check">
          <input type="checkbox" checked={forget} onChange={(e) => setForget(e.target.checked)} />
          <span>
            Also delete this broker's saved snapshots
            <span className="pf-check__help">
              {forget ? `${name}'s holdings leave the report and the history.` : 'Left unticked, the snapshots already saved are kept.'}
            </span>
          </span>
        </label>
        {disconnect.isError && <InlineError error={disconnect.error} />}
        <div className="modal__foot">
          <button type="button" className="btn" onClick={onClose} data-autofocus="">
            Cancel
          </button>
          <button type="submit" className="btn btn--danger" disabled={disconnect.isPending}>
            {disconnect.isPending ? 'Disconnecting…' : `Disconnect ${name}`}
          </button>
        </div>
      </form>
    </Dialog>
  )
}
