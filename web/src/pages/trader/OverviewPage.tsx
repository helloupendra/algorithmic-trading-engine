/**
 * Trader overview: one screen that answers "what state is the platform in and
 * what happened in my runs?". Everything on it is stored data — when the
 * market is closed it shows the last saved session with honest freshness tags.
 */

import { Link, useNavigate } from 'react-router-dom'
import { useAuth } from '../../lib/auth'
import {
  useIngestorStatuses,
  useKillSwitch,
  useLatestQuotes,
  useMarketSession,
  useSimulationRuns,
  useWatchlist,
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
  const watchlist = useWatchlist()
  const runs = useSimulationRuns()
  const ingestors = useIngestorStatuses()

  const lastQuoteUtc = quotes.data?.reduce<string | null>(
    (max, q) => (max == null || q.updatedUtc > max ? q.updatedUtc : max),
    null,
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

      <div className="stat-grid">
        <StatTile
          label="Market (NSE cash)"
          value={
            session.data ? (session.data.isMarketOpen ? 'OPEN' : 'CLOSED') : '…'
          }
          tone={session.data?.isMarketOpen ? 'pos' : undefined}
          sub={
            session.data &&
            (session.data.isMarketOpen
              ? `closes ${formatDateTime(session.data.sessionCloseUtc)}`
              : `next open ${formatDateTime(session.data.nextMarketOpenUtc)}`)
          }
        />
        <StatTile
          label="Kill switch"
          value={killSwitch.data ? (killSwitch.data.isActive ? 'ACTIVE' : 'Off') : '…'}
          tone={killSwitch.data?.isActive ? 'neg' : 'pos'}
          sub={
            killSwitch.data?.updatedUtc &&
            `${killSwitch.data.updatedBy ?? ''} · ${formatAge(killSwitch.data.updatedUtc)}`
          }
        />
        <StatTile
          label="Watchlist symbols"
          value={watchlist.data?.length ?? '…'}
          sub={lastQuoteUtc ? `last quote ${formatAge(lastQuoteUtc)}` : undefined}
        />
        <StatTile
          label="Simulation runs"
          value={runs.data?.length ?? '…'}
          sub={runs.data?.[0] && `latest #${runs.data[0].id} · ${runs.data[0].status}`}
        />
        <StatTile
          label="Ingestor"
          value={
            ingestors.data?.length
              ? ingestors.data.every((s) => s.isHealthy)
                ? 'Healthy'
                : 'Stale'
              : '…'
          }
          tone={
            ingestors.data?.length && !ingestors.data.every((s) => s.isHealthy)
              ? 'warn'
              : undefined
          }
          sub={
            ingestors.data?.[0] &&
            `heartbeat ${formatAge(ingestors.data[0].lastHeartbeatUtc)}`
          }
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
