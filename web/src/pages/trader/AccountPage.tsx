/**
 * The trader's own page: who they are, and the broker account they trade with.
 *
 * That account lives at the simulated broker, and an administrator opens it —
 * a trader cannot link one for themselves, and there is nothing here to link.
 * Every number below is read from the broker on each request rather than kept
 * by the console, so the page cannot show a balance the broker disagrees with.
 */
import { useState } from 'react'
import type { ReactNode } from 'react'
import { useAuth } from '../../lib/auth'
import { useStrategies, useTraderSimBroker, useTraderSimBrokerCredentials } from '../../lib/queries'
import { formatDateTime, formatInr, formatInrWhole } from '../../lib/format'
import type { SimBrokerAccountSnapshot, SimBrokerCredentials } from '../../lib/types'
import { Badge, InlineError, Loading, Panel } from '../../components/ui'
import './account.css'

function Row({ k, children }: { k: string; children: ReactNode }) {
  return (
    <div className="acct__row">
      <dt>{k}</dt>
      <dd>{children}</dd>
    </div>
  )
}

function pnlCls(value: number): string {
  return value > 0 ? 'pos' : value < 0 ? 'neg' : ''
}

/** The colour of an order's status: what is working, what is done, what was refused. */
function statusTone(status: string): 'pos' | 'neg' | 'accent' | 'neutral' {
  if (status === 'FILLED') return 'pos'
  if (status === 'REJECTED' || status === 'CANCELLED' || status === 'EXPIRED') return 'neg'
  if (status === 'PARTIALLY_FILLED') return 'accent'
  return 'neutral'
}

/**
 * The credentials, once asked for. Shown in full because they are the trader's
 * own and the broker will not show them again — the warning says as much rather
 * than a blur that only makes them screenshot it.
 */
function Credentials({ data }: { data: SimBrokerCredentials }) {
  return (
    <div className="acct__broker">
      <dl className="acct__sheet">
        <Row k="Client ID">
          <span className="mono">{data.clientId}</span>
        </Row>
        <Row k="App ID">
          <span className="mono">{data.appId}</span>
        </Row>
        <Row k="App secret">
          <span className="mono acct__wrap">{data.appSecret}</span>
        </Row>
        <Row k="TOTP secret">
          <span className="mono acct__wrap">{data.totpSecret}</span>
        </Row>
      </dl>
      <p className="muted small-note">
        Add the TOTP secret to an authenticator app; the six-digit code it shows is what the daily login asks
        for. These four are all an API client needs — and anyone who has them can trade this account, so keep
        them as you would a password.
      </p>
    </div>
  )
}

function KillSwitchNote({ account }: { account: SimBrokerAccountSnapshot }) {
  if (!account.killSwitch?.active) return null
  return (
    <div className="alert alert--error" role="status">
      This account is stopped. Working orders were cancelled and no new order will be accepted
      {account.killSwitch.until ? ` before ${formatDateTime(account.killSwitch.until)}` : ''}. An administrator
      can lift it.
    </div>
  )
}

function Money({ account }: { account: SimBrokerAccountSnapshot }) {
  const funds = account.funds
  if (!funds) return <p className="muted">The broker did not return this account's funds.</p>
  return (
    <dl className="acct__sheet">
      <Row k="Available to trade">{formatInr(funds.available)}</Row>
      <Row k="Cash">{formatInr(funds.cash)}</Row>
      <Row k="Margin held">{formatInr(funds.orderMargin + funds.positionMargin)}</Row>
      <Row k="Booked today">
        <span className={pnlCls(funds.realisedToday)}>{formatInr(funds.realisedToday)}</span>
      </Row>
      <Row k="Unrealised">
        <span className={pnlCls(funds.unrealised)}>{formatInr(funds.unrealised)}</span>
      </Row>
      <Row k="Charges today">{formatInr(funds.chargesToday)}</Row>
      <Row k="Paid in">{formatInrWhole(funds.netDeposits)}</Row>
    </dl>
  )
}

function Positions({ account }: { account: SimBrokerAccountSnapshot }) {
  if (account.positions.length === 0) return <p className="muted">No open positions.</p>
  return (
    <div className={`tablewrap${account.positions.length > 8 ? ' tablewrap--rows8' : ''}`}>
      <table className="table">
        <thead>
          <tr>
            <th>Symbol</th>
            <th>Product</th>
            <th className="num">Qty</th>
            <th className="num">Average</th>
            <th className="num">Last</th>
            <th className="num">Unrealised</th>
            <th className="num">Net today</th>
          </tr>
        </thead>
        <tbody>
          {account.positions.map((p) => (
            <tr key={`${p.symbol}-${p.product}`}>
              <td className="mono">{p.symbol}</td>
              <td>{p.product}</td>
              <td className="num">{p.quantity}</td>
              <td className="num">{formatInr(p.averagePrice)}</td>
              <td className="num">{p.lastPrice == null ? '—' : formatInr(p.lastPrice)}</td>
              <td className={`num ${pnlCls(p.unrealised)}`}>{formatInr(p.unrealised)}</td>
              <td className={`num ${pnlCls(p.netToday)}`}>{formatInr(p.netToday)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function Orders({ account }: { account: SimBrokerAccountSnapshot }) {
  if (account.orders.length === 0) return <p className="muted">No orders today.</p>
  return (
    <div className={`tablewrap${account.orders.length > 8 ? ' tablewrap--rows8' : ''}`}>
      <table className="table">
        <thead>
          <tr>
            <th>Time</th>
            <th>Symbol</th>
            <th>Side</th>
            <th className="num">Qty</th>
            <th className="num">Price</th>
            <th>Status</th>
            <th>Why</th>
          </tr>
        </thead>
        <tbody>
          {account.orders.map((o) => (
            <tr key={o.orderId}>
              <td>{new Date(o.placedAt).toLocaleTimeString('en-IN', { hour: '2-digit', minute: '2-digit' })}</td>
              <td className="mono">{o.symbol}</td>
              <td>{o.side}</td>
              <td className="num">
                {o.filledQuantity}/{o.quantity}
              </td>
              <td className="num">{o.averagePrice ?? o.limitPrice ?? '—'}</td>
              <td>
                <Badge tone={statusTone(o.status)}>{o.status.replace('_', ' ').toLowerCase()}</Badge>
              </td>
              <td className="muted">{o.rejectionCode ?? o.message ?? '—'}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

export function AccountPage() {
  const { user } = useAuth()
  const strategies = useStrategies()
  const broker = useTraderSimBroker()
  const reveal = useTraderSimBrokerCredentials()
  const [showing, setShowing] = useState(false)

  const linked = broker.data?.linked === true
  const account = broker.data?.account ?? null

  return (
    <div className="page acct">
      <header className="page__header">
        <div>
          <h1 className="page__title">Account</h1>
          <p className="page__subtitle">Your profile, your package, and your broker account.</p>
        </div>
      </header>

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
          title="My broker account"
          actions={
            linked && account ? (
              <Badge tone={account.killSwitch?.active ? 'neg' : 'pos'}>
                {account.killSwitch?.active ? 'stopped' : account.link.clientId}
              </Badge>
            ) : undefined
          }
        >
          {broker.isError && <InlineError error={broker.error} />}
          {reveal.isError && <InlineError error={reveal.error} />}

          {broker.isLoading ? (
            <Loading label="Loading…" />
          ) : !linked ? (
            <div className="acct__broker">
              <p className="muted">
                {broker.data?.message ??
                  'You do not have an account at the simulated broker yet. An administrator opens one for you.'}
              </p>
              <p className="muted small-note">
                That account is where your orders go: play money, real rules — lot sizes, tick sizes, margin,
                market hours and a daily login, exactly as a live broker enforces them.
              </p>
            </div>
          ) : !account ? (
            <div className="acct__broker">
              <p className="muted">
                Your account exists, but the broker could not be read just now
                {broker.data?.message ? `: ${broker.data.message}` : '.'}
              </p>
            </div>
          ) : (
            <div className="acct__broker">
              <KillSwitchNote account={account} />
              <Money account={account} />
              {account.warnings.length > 0 && (
                <div className="alert alert--error" role="status">
                  {account.warnings.join(' · ')}
                </div>
              )}
              <div className="toolbar">
                <button
                  type="button"
                  className="btn btn--ghost"
                  disabled={reveal.isPending}
                  onClick={() => {
                    if (showing) {
                      setShowing(false)
                      reveal.reset()
                      return
                    }
                    reveal.mutate(undefined, { onSuccess: () => setShowing(true) })
                  }}
                >
                  {reveal.isPending ? 'Fetching…' : showing ? 'Hide API credentials' : 'Show API credentials'}
                </button>
              </div>
              {showing && reveal.data && <Credentials data={reveal.data} />}
            </div>
          )}
        </Panel>
      </div>

      {linked && account && (
        <>
          <Panel title="Positions">
            <Positions account={account} />
          </Panel>
          <Panel title="Orders today">
            <Orders account={account} />
          </Panel>
        </>
      )}
    </div>
  )
}
