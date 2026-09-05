/**
 * Admin system status: everything an operator checks first — market session,
 * kill switch, broker session, ingestor health, users — with one line each.
 */

import { Link } from 'react-router-dom'
import {
  useBrokerSession,
  useIngestorStatuses,
  useKillSwitch,
  useLatestQuotes,
  useMarketSession,
  useSimulationRuns,
  useUsers,
  useWatchlist,
} from '../../lib/queries'
import { formatAge, formatDateTime } from '../../lib/format'
import { Badge, Panel, StatTile } from '../../components/ui'
import { LiveQuotesMonitor } from '../../components/LiveQuotesMonitor'

function ChecklistItem({
  done,
  label,
  hint,
  to,
}: {
  done: boolean
  label: string
  hint: string
  to: string
}) {
  return (
    <Link className="checklist__item" to={to}>
      <span className={`checklist__state ${done ? 'checklist__state--done' : 'checklist__state--todo'}`}>
        {done ? '✓' : '!'}
      </span>
      <span className="checklist__body">
        <span className="checklist__label">{label}</span>
        <br />
        <span className="checklist__hint">{hint}</span>
      </span>
      <span className="checklist__go">{done ? 'view →' : 'fix →'}</span>
    </Link>
  )
}

export function AdminOverviewPage() {
  const session = useMarketSession()
  const killSwitch = useKillSwitch()
  const broker = useBrokerSession()
  const ingestors = useIngestorStatuses()
  const users = useUsers()
  const watchlist = useWatchlist()
  const quotes = useLatestQuotes()
  const runs = useSimulationRuns()

  const ingestorHealthy = ingestors.data?.every((s) => s.isHealthy)

  return (
    <div className="page">
      <header className="page__header">
        <h1 className="page__title">System status</h1>
        <p className="page__subtitle">The operator's first look: is everything up, and is anything on fire?</p>
      </header>

      <div className="stat-grid">
        <StatTile
          label="Market (NSE)"
          value={session.data ? (session.data.isMarketOpen ? 'OPEN' : 'CLOSED') : '…'}
          tone={session.data?.isMarketOpen ? 'pos' : undefined}
          sub={session.data && `next open ${formatDateTime(session.data.nextMarketOpenUtc)}`}
        />
        <StatTile
          label="Kill switch"
          value={killSwitch.data ? (killSwitch.data.isActive ? 'ACTIVE' : 'Off') : '…'}
          tone={killSwitch.data?.isActive ? 'neg' : 'pos'}
          to="/admin/system/risk"
          sub="manage →"
        />
        <StatTile
          label="FYERS session"
          value={broker.data ? (broker.data.isAuthenticated ? 'Authenticated' : 'Expired') : '…'}
          tone={broker.data?.isAuthenticated ? 'pos' : 'warn'}
          to="/admin/broker"
          sub={
            broker.data?.isAuthenticated
              ? broker.data?.updatedUtc && `updated ${formatAge(broker.data.updatedUtc)}`
              : 'reconnect →'
          }
        />
        <StatTile
          label="Ingestor"
          value={ingestors.data?.length ? (ingestorHealthy ? 'Healthy' : 'Stale') : '…'}
          tone={ingestors.data?.length && !ingestorHealthy ? 'warn' : 'pos'}
          to="/admin/ingestion"
          sub={ingestors.data?.[0] && `heartbeat ${formatAge(ingestors.data[0].lastHeartbeatUtc)}`}
        />
        <StatTile label="Users" value={users.data?.length ?? '…'} to="/admin/users" sub="manage →" />
        <StatTile
          label="Watchlist / quotes"
          value={`${watchlist.data?.length ?? '…'} / ${quotes.data?.length ?? '…'}`}
          sub="subscribed / with data"
          to="/admin/instruments"
        />
        <StatTile label="Simulation runs" value={runs.data?.length ?? '…'} to="/admin/strategies" sub="strategy control →" />
      </div>

      <Panel title="Go-live checklist">
        <p className="muted">
          Paper trading needs these in order — each row shows its live state; click to fix.
        </p>
        <div className="checklist">
          <ChecklistItem
            done={!!broker.data?.isAuthenticated}
            label="Connect FYERS"
            hint={
              broker.data?.isAuthenticated
                ? 'Session authenticated'
                : 'Session expired — market data and orders need a fresh login'
            }
            to="/admin/broker"
          />
          <ChecklistItem
            done={!!ingestors.data?.length && !!ingestorHealthy}
            label="Start the live ingestor"
            hint={
              ingestorHealthy
                ? 'Heartbeat healthy — ticks are flowing'
                : `Stale — last heartbeat ${
                    ingestors.data?.[0] ? formatAge(ingestors.data[0].lastHeartbeatUtc) : 'unknown'
                  } ago`
            }
            to="/admin/ingestion"
          />
          <ChecklistItem
            done={killSwitch.data ? !killSwitch.data.isActive : false}
            label="Kill switch off"
            hint={killSwitch.data?.isActive ? 'Trading is halted' : 'Trading enabled'}
            to="/admin/system/risk"
          />
          <ChecklistItem
            done={!!session.data?.isMarketOpen}
            label="Market open"
            hint={
              session.data?.isMarketOpen
                ? 'NSE session is live'
                : `Opens ${session.data ? formatDateTime(session.data.nextMarketOpenUtc) : '…'}`
            }
            to="/trader/deploy"
          />
        </div>
      </Panel>

      <Panel title="Quick actions">
        <div className="chip-row">
          <Link className="btn btn--sm" to="/admin/broker">Connect FYERS →</Link>
          <Link className="btn btn--sm" to="/admin/system/risk">Kill switch →</Link>
          <Link className="btn btn--sm" to="/admin/ingestion">Ingestion &amp; backfill →</Link>
          <Link className="btn btn--sm" to="/admin/strategies">Start a strategy →</Link>
          <Link className="btn btn--sm" to="/admin/users">Manage users →</Link>
          <Link className="btn btn--sm" to="/trader">Trader view →</Link>
        </div>
      </Panel>

      <Panel title="Data freshness">
        <p className="muted">
          Market data on this installation is whatever the last live session saved.
          {quotes.data?.length
            ? ` The newest stored quote is ${formatAge(
                quotes.data.reduce((m, q) => (q.updatedUtc > m ? q.updatedUtc : m), quotes.data[0].updatedUtc),
              )}.`
            : ' No quotes stored yet.'}{' '}
          Start the Python ingestor during market hours to refresh it; everything on these screens
          updates live when it runs.
        </p>
        {killSwitch.data?.updatedUtc && (
          <p className="muted">
            Kill switch last touched by <b>{killSwitch.data.updatedBy}</b> (
            {formatDateTime(killSwitch.data.updatedUtc)}) — “{killSwitch.data.reason}”.{' '}
            <Badge tone={killSwitch.data.isActive ? 'neg' : 'pos'}>
              {killSwitch.data.isActive ? 'halted' : 'trading enabled'}
            </Badge>
          </p>
        )}
      </Panel>

      <LiveQuotesMonitor />
    </div>
  )
}
