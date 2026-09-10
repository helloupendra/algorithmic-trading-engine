/**
 * Trader overview: one screen that answers "can I trade, what am I running, and
 * can I trust these numbers?".
 *
 * Deliberately not on it: broker link state and ingestor heartbeats. Those are
 * the operator's job and a trader can do nothing about either. What a trader
 * does need from the feed — whether the prices are fresh — is said as data age,
 * next to the data.
 */

import { Link } from 'react-router-dom'
import { useAuth } from '../../lib/auth'
import {
  useKillSwitch,
  useMarketPulse,
  useMarketSession,
  useSimulationRuns,
  useStrategies,
} from '../../lib/queries'
import { formatAge, formatDateTime, formatInrWhole, shortSymbol } from '../../lib/format'
import { Badge, Panel, QueryBoundary, StatTile } from '../../components/ui'
import { MarketPulse } from '../../components/MarketPulse'

export function OverviewPage() {
  const { user } = useAuth()
  const session = useMarketSession()
  const killSwitch = useKillSwitch()
  const pulse = useMarketPulse()
  const runs = useSimulationRuns()
  const strategies = useStrategies()

  const lastQuoteUtc = pulse.data?.latestQuoteUtc ?? null

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
          to="/trader/strategies/history"
        />
        <StatTile
          label="Strategies I can run"
          value={strategies.data?.length ?? '…'}
          sub="from my package"
          to="/trader/deploy"
        />
        <StatTile
          label="Price data"
          value={lastQuoteUtc ? formatAge(lastQuoteUtc) : 'none'}
          tone={quotesAreStale ? 'warn' : undefined}
          sub={quotesAreStale ? 'stale — treat prices with care' : 'last saved quote'}
        />
      </div>

      {/* The market first: the same six numbers and twelve names for everyone,
          always on the feed. The trader's own list has its own page. */}
      <MarketPulse />

      <Panel title="Recent runs" actions={<Link to="/trader/strategies/history">My runs →</Link>}>
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
