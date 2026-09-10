/**
 * The trader's own page: who they are, and their own broker.
 *
 * The broker here is theirs, not the platform's. The platform's FYERS account
 * feeds every strategy and is the operator's business; this one is what a
 * trader links so the console can show their funds, holdings, positions and
 * orders — and, once live execution exists, place their orders. The two are
 * kept apart on the server by broker account; nothing on this page can touch
 * the platform session.
 */
import { useEffect, useState } from 'react'
import type { FormEvent, ReactNode } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useAuth } from '../../lib/auth'
import {
  useRemoveTraderBroker,
  useSaveTraderBroker,
  useStrategies,
  useTraderBroker,
  useTraderBrokerResource,
  useTraderBrokerSignOut,
} from '../../lib/queries'
import { api } from '../../lib/api'
import { formatAge, formatDateTime, formatInr, formatInrWhole } from '../../lib/format'
import type { TraderBrokerStatus } from '../../lib/types'
import { Badge, InlineError, Loading, Panel, QueryBoundary } from '../../components/ui'
import './account.css'

/* ------------------------------------------------------------ broker state */

type LinkState = 'none' | 'saved' | 'linked'

function linkState(b: TraderBrokerStatus): LinkState {
  if (b.isAuthenticated) return 'linked'
  return b.configured ? 'saved' : 'none'
}

function BrokerLight({ state, expiresAtUtc }: { state: LinkState; expiresAtUtc: string | null }) {
  const label =
    state === 'linked'
      ? `Linked${expiresAtUtc ? ` · valid until ${new Date(expiresAtUtc).toLocaleTimeString('en-IN', { hour: '2-digit', minute: '2-digit' })}` : ''}`
      : state === 'saved'
        ? 'App saved · sign in needed'
        : 'Not linked'
  return (
    <span className={`acct-light acct-light--${state}`} role="status">
      <span className="acct-light__dot" aria-hidden="true" />
      {label}
    </span>
  )
}

/* ------------------------------------------------------------- the page */

export function AccountPage() {
  const { user } = useAuth()
  const strategies = useStrategies()
  const broker = useTraderBroker()
  const save = useSaveTraderBroker()
  const signOut = useTraderBrokerSignOut()
  const remove = useRemoveTraderBroker()

  // The broker's redirect lands here with the outcome in the address.
  const [search, setSearch] = useSearchParams()
  const [notice, setNotice] = useState<{ tone: 'pos' | 'neg'; text: string } | null>(null)
  useEffect(() => {
    const outcome = search.get('broker')
    if (outcome == null) return
    const reason = search.get('reason')
    setNotice(
      outcome === '1'
        ? { tone: 'pos', text: 'FYERS linked. Your funds, positions and orders are below.' }
        : { tone: 'neg', text: reason ?? 'The broker sign-in did not complete.' },
    )
    setSearch({}, { replace: true })
    void broker.refetch()
  }, [search, setSearch, broker])

  const [editing, setEditing] = useState(false)
  const [clientId, setClientId] = useState('')
  const [secretKey, setSecretKey] = useState('')
  const [pin, setPin] = useState('')
  const [signingIn, setSigningIn] = useState<string | null>(null)

  const status = broker.data
  const state: LinkState = status ? linkState(status) : 'none'
  const showForm = status ? editing || state === 'none' : false

  function submit(e: FormEvent) {
    e.preventDefault()
    save.mutate(
      { clientId: clientId.trim(), secretKey: secretKey.trim(), tradingPin: pin.trim() || undefined },
      {
        onSuccess: () => {
          setEditing(false)
          setSecretKey('')
          setPin('')
        },
      },
    )
  }

  async function signIn() {
    setSigningIn(null)
    try {
      const { authUrl } = await api.get<{ authUrl: string }>('/api/Trader/broker/auth-url')
      // The broker signs the trader in on its own site and sends them back to
      // this page through the server's callback.
      window.location.assign(authUrl)
    } catch (err) {
      setSigningIn(err instanceof Error ? err.message : String(err))
    }
  }

  return (
    <div className="page acct">
      <header className="page__header">
        <div>
          <h1 className="page__title">Account</h1>
          <p className="page__subtitle">Your profile, your package, and your own broker.</p>
        </div>
      </header>

      {notice && (
        <div className={`alert ${notice.tone === 'pos' ? 'alert--success' : 'alert--error'}`} role="status">
          {notice.text}
        </div>
      )}

      <div className="acct__grid">
        <Panel title="Profile">
          <dl className="acct__sheet">
            <Row k="Username">{user?.userName ?? '—'}</Row>
            <Row k="Email">{user?.email ?? '—'}</Row>
            <Row k="Role">
              <Badge tone={user?.role === 'Admin' ? 'accent' : 'neutral'}>{user?.role ?? '—'}</Badge>
            </Row>
            <Row k="Allocated capital">{formatInrWhole(user?.totalCapital ?? 0)}</Row>
            <Row k="Strategies in my package">
              {strategies.data ? String(strategies.data.length) : strategies.isError ? 'none granted yet' : '…'}
            </Row>
            <Row k="Member since">{user?.createdUtc ? formatDateTime(user.createdUtc) : '—'}</Row>
            <Row k="Last sign-in">{user?.lastLoginUtc ? formatDateTime(user.lastLoginUtc) : '—'}</Row>
          </dl>
        </Panel>

        <Panel
          title="My broker"
          actions={status ? <BrokerLight state={state} expiresAtUtc={status.expiresAtUtc} /> : undefined}
        >
          {broker.isError && <InlineError error={broker.error} />}
          {save.isError && <InlineError error={save.error} />}
          {signOut.isError && <InlineError error={signOut.error} />}
          {remove.isError && <InlineError error={remove.error} />}
          {signingIn && <InlineError error={new Error(signingIn)} />}
          {(save.isSuccess || signOut.isSuccess || remove.isSuccess) && (
            <div className="alert alert--success">
              {save.data?.message ?? signOut.data?.message ?? remove.data?.message}
            </div>
          )}

          {!status ? (
            <Loading label="Loading…" />
          ) : showForm ? (
            <form className="acct__form" onSubmit={submit}>
              <p className="muted">
                Create an app at <b>myapi.fyers.in</b> and give it this redirect URL, exactly:
              </p>
              <code className="acct__callback">{status.callbackUrl}</code>
              <label className="field">
                <span className="field__label">App ID</span>
                <input
                  className="field__input mono"
                  value={clientId}
                  onChange={(e) => setClientId(e.target.value)}
                  placeholder="XXXXXXXXXX-100"
                  autoComplete="off"
                  required
                />
              </label>
              <label className="field">
                <span className="field__label">Secret key</span>
                <input
                  className="field__input mono"
                  type="password"
                  value={secretKey}
                  onChange={(e) => setSecretKey(e.target.value)}
                  autoComplete="new-password"
                  required
                />
              </label>
              <label className="field">
                <span className="field__label">Trading PIN (optional)</span>
                <input
                  className="field__input mono"
                  type="password"
                  inputMode="numeric"
                  value={pin}
                  onChange={(e) => setPin(e.target.value)}
                  autoComplete="off"
                  placeholder="only if you want the token renewed without you"
                />
              </label>
              <p className="muted small-note">
                The secret and PIN are encrypted before they are stored and are never shown again.
              </p>
              <div className="toolbar">
                <button type="submit" className="btn btn--primary" disabled={save.isPending}>
                  {save.isPending ? 'Saving…' : 'Save'}
                </button>
                {status.configured && (
                  <button type="button" className="btn" onClick={() => setEditing(false)}>
                    Cancel
                  </button>
                )}
              </div>
            </form>
          ) : (
            <div className="acct__broker">
              <dl className="acct__sheet">
                <Row k="Broker">{status.providerName}</Row>
                <Row k="App ID">
                  <span className="mono">{status.clientId ?? '—'}</span>
                </Row>
                <Row k="Redirect URL">
                  <span className="mono acct__wrap">{status.redirectUri ?? '—'}</span>
                </Row>
                <Row k="Trading PIN">{status.hasTradingPin ? 'saved' : 'not saved'}</Row>
                <Row k="Session">
                  {state === 'linked'
                    ? `signed in ${status.signedInUtc ? formatAge(status.signedInUtc) : ''} · expires ${
                        status.expiresAtUtc ? formatDateTime(status.expiresAtUtc) : '06:00 IST'
                      }`
                    : 'not signed in — FYERS expires every session at 06:00 IST, so sign in each morning'}
                </Row>
              </dl>
              <div className="toolbar">
                {state !== 'linked' && (
                  <button type="button" className="btn btn--primary" onClick={signIn}>
                    Sign in to FYERS
                  </button>
                )}
                {state === 'linked' && (
                  <button type="button" className="btn" onClick={() => signOut.mutate()} disabled={signOut.isPending}>
                    {signOut.isPending ? 'Signing out…' : 'Sign out of FYERS'}
                  </button>
                )}
                <button type="button" className="btn btn--ghost" onClick={() => setEditing(true)}>
                  Change app credentials
                </button>
                <button
                  type="button"
                  className="btn btn--ghost btn--danger"
                  disabled={remove.isPending}
                  onClick={() => {
                    if (window.confirm('Remove your broker app and sign out? Your platform account is not affected.')) remove.mutate()
                  }}
                >
                  Remove
                </button>
              </div>
            </div>
          )}
        </Panel>
      </div>

      {status && state === 'linked' && <BrokerBooks />}
    </div>
  )
}

function Row({ k, children }: { k: string; children: ReactNode }) {
  return (
    <div className="acct__row">
      <dt>{k}</dt>
      <dd>{children}</dd>
    </div>
  )
}

/* --------------------------------------------------- the trader's own books */

type Json = Record<string, unknown>

function num(v: unknown): number | null {
  if (typeof v === 'number' && Number.isFinite(v)) return v
  if (typeof v === 'string' && v.trim() !== '' && !Number.isNaN(Number(v))) return Number(v)
  return null
}
function str(v: unknown): string {
  return v == null || v === '' ? '—' : String(v)
}
function money(v: unknown): string {
  const n = num(v)
  return n == null ? '—' : formatInr(n)
}
function pnlCls(v: unknown): string {
  const n = num(v)
  return n == null ? '' : n > 0 ? 'pos' : n < 0 ? 'neg' : ''
}
function rows(data: Json | undefined, key: string): Json[] {
  const v = data?.[key]
  return Array.isArray(v) ? (v as Json[]) : []
}

/** The broker's own answer was an error; say what it said. */
function BrokerError({ data }: { data: Json | undefined }) {
  if (!data || data.s !== 'error') return null
  return <InlineError error={new Error(str(data.message) || `FYERS error ${str(data.code)}`)} />
}

function BrokerBooks() {
  const [tab, setTab] = useState<'funds' | 'positions' | 'orders' | 'holdings' | 'trades'>('funds')
  const funds = useTraderBrokerResource<Json>('funds', true)
  const positions = useTraderBrokerResource<Json>('positions', tab === 'positions')
  const orders = useTraderBrokerResource<Json>('orders', tab === 'orders')
  const holdings = useTraderBrokerResource<Json>('holdings', tab === 'holdings')
  const trades = useTraderBrokerResource<Json>('trades', tab === 'trades')

  const tabs: { key: typeof tab; label: string }[] = [
    { key: 'funds', label: 'Funds' },
    { key: 'positions', label: 'Positions' },
    { key: 'orders', label: 'Orders' },
    { key: 'holdings', label: 'Holdings' },
    { key: 'trades', label: 'Trades' },
  ]

  return (
    <Panel
      title="My broker account"
      actions={
        <div className="seg" role="group" aria-label="Broker book">
          {tabs.map((t) => (
            <button
              key={t.key}
              type="button"
              aria-pressed={tab === t.key}
              className={`seg__btn ${tab === t.key ? 'is-active' : ''}`}
              onClick={() => setTab(t.key)}
            >
              {t.label}
            </button>
          ))}
        </div>
      }
    >
      <p className="muted small-note" style={{ marginTop: 0 }}>
        Read from FYERS with your own sign-in, as the broker reports it. Nothing here is the platform's paper book.
      </p>

      {tab === 'funds' && (
        <QueryBoundary query={funds}>
          {(data) => (
            <>
              <BrokerError data={data} />
              <div className="tablewrap">
                <table className="table">
                  <thead>
                    <tr>
                      <th>Limit</th>
                      <th className="r">Equity</th>
                      <th className="r">Commodity</th>
                    </tr>
                  </thead>
                  <tbody>
                    {rows(data, 'fund_limit').map((f, i) => (
                      <tr key={i}>
                        <td>{str(f.title)}</td>
                        <td className="r mono">{money(f.equityAmount)}</td>
                        <td className="r mono">{money(f.commodityAmount)}</td>
                      </tr>
                    ))}
                    {rows(data, 'fund_limit').length === 0 && (
                      <tr>
                        <td colSpan={3} className="muted">
                          No fund limits returned.
                        </td>
                      </tr>
                    )}
                  </tbody>
                </table>
              </div>
            </>
          )}
        </QueryBoundary>
      )}

      {tab === 'positions' && (
        <QueryBoundary query={positions}>
          {(data) => (
            <>
              <BrokerError data={data} />
              <Book
                items={rows(data, 'netPositions')}
                empty="No open positions at the broker."
                columns={[
                  ['Symbol', (p) => <span className="mono">{str(p.symbol)}</span>],
                  ['Side', (p) => (num(p.side) === 1 ? 'BUY' : num(p.side) === -1 ? 'SELL' : str(p.side))],
                  ['Qty', (p) => str(p.netQty), 'r'],
                  ['Avg', (p) => money(p.netAvg), 'r'],
                  ['LTP', (p) => money(p.ltp), 'r'],
                  ['P&L', (p) => <span className={pnlCls(p.pl)}>{money(p.pl)}</span>, 'r'],
                  ['Product', (p) => str(p.productType)],
                ]}
              />
              {data?.overall != null && typeof data.overall === 'object' && (
                <p className="muted small-note">
                  Overall P&amp;L{' '}
                  <b className={pnlCls((data.overall as Json).pl_total)}>{money((data.overall as Json).pl_total)}</b>
                  {' · '}realised {money((data.overall as Json).pl_realized)} · unrealised {money((data.overall as Json).pl_unrealized)}
                </p>
              )}
            </>
          )}
        </QueryBoundary>
      )}

      {tab === 'orders' && (
        <QueryBoundary query={orders}>
          {(data) => (
            <>
              <BrokerError data={data} />
              <Book
                items={rows(data, 'orderBook')}
                empty="No orders today at the broker."
                columns={[
                  ['Time', (o) => str(o.orderDateTime)],
                  ['Symbol', (o) => <span className="mono">{str(o.symbol)}</span>],
                  ['Side', (o) => (num(o.side) === 1 ? 'BUY' : num(o.side) === -1 ? 'SELL' : str(o.side))],
                  ['Qty', (o) => `${str(o.filledQty)} / ${str(o.qty)}`, 'r'],
                  ['Price', (o) => money(num(o.tradedPrice) || o.limitPrice), 'r'],
                  ['Status', (o) => str(o.orderStatus === 2 ? 'Filled' : o.orderStatus === 1 ? 'Cancelled' : o.orderStatus === 6 ? 'Pending' : o.orderStatus === 5 ? 'Rejected' : o.orderStatus)],
                  ['Message', (o) => str(o.message)],
                ]}
              />
            </>
          )}
        </QueryBoundary>
      )}

      {tab === 'holdings' && (
        <QueryBoundary query={holdings}>
          {(data) => (
            <>
              <BrokerError data={data} />
              <Book
                items={rows(data, 'holdings')}
                empty="No holdings at the broker."
                columns={[
                  ['Symbol', (h) => <span className="mono">{str(h.symbol)}</span>],
                  ['Qty', (h) => str(h.quantity), 'r'],
                  ['Cost', (h) => money(h.costPrice), 'r'],
                  ['LTP', (h) => money(h.ltp), 'r'],
                  ['Value', (h) => money(h.marketVal), 'r'],
                  ['P&L', (h) => <span className={pnlCls(h.pl)}>{money(h.pl)}</span>, 'r'],
                ]}
              />
            </>
          )}
        </QueryBoundary>
      )}

      {tab === 'trades' && (
        <QueryBoundary query={trades}>
          {(data) => (
            <>
              <BrokerError data={data} />
              <Book
                items={rows(data, 'tradeBook')}
                empty="No trades today at the broker."
                columns={[
                  ['Time', (t) => str(t.orderDateTime)],
                  ['Symbol', (t) => <span className="mono">{str(t.symbol)}</span>],
                  ['Side', (t) => (num(t.side) === 1 ? 'BUY' : num(t.side) === -1 ? 'SELL' : str(t.side))],
                  ['Qty', (t) => str(t.tradedQty), 'r'],
                  ['Price', (t) => money(t.tradePrice), 'r'],
                  ['Value', (t) => money(t.tradeValue), 'r'],
                ]}
              />
            </>
          )}
        </QueryBoundary>
      )}
    </Panel>
  )
}

function Book({
  items,
  columns,
  empty,
}: {
  items: Json[]
  columns: [string, (row: Json) => ReactNode, string?][]
  empty: string
}) {
  if (items.length === 0) return <p className="muted">{empty}</p>
  return (
    <div className={`tablewrap${items.length > 8 ? ' tablewrap--rows8' : ''}`}>
      <table className="table">
        <thead>
          <tr>
            {columns.map(([h, , cls]) => (
              <th key={h} className={cls}>
                {h}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {items.map((row, i) => (
            <tr key={i}>
              {columns.map(([h, render, cls]) => (
                <td key={h} className={cls}>
                  {render(row)}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}
