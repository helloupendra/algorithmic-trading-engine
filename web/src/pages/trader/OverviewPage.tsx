/**
 * Trader overview: one screen that answers "can I trade, what am I running, and
 * can I trust these numbers?".
 *
 * Deliberately not on it: broker link state and ingestor heartbeats. Those are
 * the operator's job and a trader can do nothing about either. What a trader
 * does need from the feed — whether the prices are fresh — is said as data age,
 * next to the data.
 */

import { Link, useNavigate } from 'react-router-dom'
import { useAuth } from '../../lib/auth'
import {
  useKillSwitch,
  useLatestQuotes,
  useMarketSession,
  useSimulationRuns,
  useStrategies,
} from '../../lib/queries'
import {
  formatAge,
  formatDateTime,
  formatInrWhole,
  formatPrice,
  pnlClass,
  quoteChange,
  shortSymbol,
} from '../../lib/format'
import { Badge, Panel, QueryBoundary, StatTile } from '../../components/ui'

export function OverviewPage() {
  const navigate = useNavigate()
  const { user } = useAuth()
  const session = useMarketSession()
  const killSwitch = useKillSwitch()
  const quotes = useLatestQuotes()
  const runs = useSimulationRuns()
  const strategies = useStrategies()

  const lastQuoteUtc = quotes.data?.reduce<string | null>(
    (max, q) => (max == null || q.updatedUtc > max ? q.updatedUtc : max),
    null,
  )

  // "Can I trust these prices?" is the only feed question a trader needs, and it
  // is answered by the age of the data itself rather than by a process's health.
  const quotesAreStale =
    lastQuoteUtc != null && Date.now() - new Date(lastQuoteUtc).getTime() > 5 * 60 * 1000

  const myLiveRuns = (runs.data ?? []).filter(
    (r) => r.status === 'Running' || r.status === 'Stopping',
  )

  return (
    <div className="page">
      <header className="page__header">
        <h1 className="page__title">Overview</h1>
        <p className="page__subtitle">
          Signed in as {user?.userName} · allocated capital{' '}
          {formatInrWhole(user?.totalCapital ?? 0)}
        </p>
      </header>

      {killSwitch.data?.isActive && (
        <div className="alert alert--error" role="alert">
          <b>Trading is halted platform-wide.</b> An admin pulled the kill switch
          {killSwitch.data.reason ? ` — “${killSwitch.data.reason}”` : '.'} New runs will be refused
          until it is released.
        </div>
      )}

      <div className="stat-grid">
        <StatTile
          label="Market (NSE cash)"
          value={session.data ? (session.data.isMarketOpen ? 'OPEN' : 'CLOSED') : '…'}
          tone={session.data?.isMarketOpen ? 'pos' : undefined}
          sub={
            session.data &&
            (session.data.isMarketOpen
              ? `closes ${formatDateTime(session.data.sessionCloseUtc)}`
              : `next open ${formatDateTime(session.data.nextMarketOpenUtc)}`)
          }
        />
        <StatTile
          label="My capital"
          value={formatInrWhole(user?.totalCapital ?? 0)}
          sub="allocated to this account"
        />
        <StatTile
          label="My live runs"
          value={myLiveRuns.length}
          tone={myLiveRuns.length > 0 ? 'accent' : undefined}
          sub={myLiveRuns.length > 0 ? 'open right now' : 'nothing running'}
          to="/trader/strategies"
        />
        <StatTile
          label="Strategies I can run"
          value={strategies.data?.length ?? '…'}
          sub="from my package"
          to="/trader/strategies"
        />
        <StatTile
          label="Price data"
          value={lastQuoteUtc ? formatAge(lastQuoteUtc) : 'none'}
          tone={quotesAreStale ? 'warn' : undefined}
          sub={quotesAreStale ? 'stale — treat prices with care' : 'last saved quote'}
        />
      </div>

      <Panel
        title="Last saved quotes"
        actions={<Link to="/trader/watchlist">Watchlist →</Link>}
      >
        <QueryBoundary query={quotes} empty="No quotes stored yet — start the ingestor during market hours.">
          {(data) => (
            <div className="tablewrap">
              <table className="table table--hover">
                <thead>
                  <tr>
                    <th>Symbol</th>
                    <th className="r">LTP</th>
                    <th className="r">Change</th>
                    <th className="r">Open</th>
                    <th className="r">High</th>
                    <th className="r">Low</th>
                    <th className="r">Volume</th>
                    <th className="r">As of</th>
                  </tr>
                </thead>
                <tbody>
                  {data.map((q) => {
                    const chg = quoteChange(q.lastTradedPrice, q.close)
                    return (
                      <tr
                        key={q.symbol}
                        onClick={() => navigate(`/trader/charts?symbol=${encodeURIComponent(q.symbol)}`)}
                      >
                        <td className="mono">{shortSymbol(q.symbol)}</td>
                        <td className="r mono">{formatPrice(q.lastTradedPrice)}</td>
                        <td className={`r mono ${pnlClass(chg?.abs)}`}>
                          {chg ? `${chg.abs >= 0 ? '+' : ''}${chg.abs.toFixed(2)} (${chg.pct.toFixed(2)}%)` : '—'}
                        </td>
                        <td className="r mono">{formatPrice(q.open)}</td>
                        <td className="r mono">{formatPrice(q.high)}</td>
                        <td className="r mono">{formatPrice(q.low)}</td>
                        <td className="r mono">{q.volume?.toLocaleString('en-IN') ?? '—'}</td>
                        <td className="r muted">{formatAge(q.updatedUtc)}</td>
                      </tr>
                    )
                  })}
                </tbody>
              </table>
            </div>
          )}
        </QueryBoundary>
      </Panel>

      <Panel title="Recent runs" actions={<Link to="/trader/strategies">All strategies →</Link>}>
        <QueryBoundary query={runs} empty="No simulation runs yet.">
          {(data) => (
            <div className="tablewrap">
              <table className="table">
                <thead>
                  <tr>
                    <th>Run</th>
                    <th>Strategy</th>
                    <th>Mode</th>
                    <th>Symbol</th>
                    <th>Status</th>
                    <th className="r">Capital</th>
                    <th className="r">Created</th>
                    <th />
                  </tr>
                </thead>
                <tbody>
                  {data.slice(0, 5).map((run) => (
                    <tr key={run.id}>
                      <td className="mono">#{run.id}</td>
                      <td>{run.strategyName}</td>
                      <td>{run.mode}</td>
                      <td className="mono">{shortSymbol(run.symbol)}</td>
                      <td>
                        <Badge tone={run.status === 'Running' ? 'pos' : run.status === 'Failed' ? 'neg' : 'neutral'}>
                          {run.status}
                        </Badge>
                      </td>
                      <td className="r mono">{formatInrWhole(run.initialCapital)}</td>
                      <td className="r muted">{formatDateTime(run.createdUtc)}</td>
                      <td className="r">
                        <Link to={`/trader/runs/${run.id}`}>Open →</Link>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </QueryBoundary>
      </Panel>
    </div>
  )
}
