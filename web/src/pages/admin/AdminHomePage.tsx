/**
 * Admin home — the console front door. One card per module (from the module
 * registry) plus the live health strip. Modules marked v1 still run on the old
 * pages; they get rebuilt one by one, Data first.
 */

import { Link } from 'react-router-dom'
import { MODULES } from '../../lib/modules'
import {
  useBrokerSession,
  useIngestorProcessStatus,
  useIngestorStatuses,
  useKillSwitch,
  useMarketSession,
  useWatchlist,
} from '../../lib/queries'
import { useAuth } from '../../lib/auth'
import { Badge, StatTile } from '../../components/ui'

export function AdminHomePage() {
  const { user } = useAuth()
  const session = useMarketSession()
  const broker = useBrokerSession()
  const process = useIngestorProcessStatus()
  const ingestors = useIngestorStatuses()
  const watchlist = useWatchlist()
  const killSwitch = useKillSwitch()

  const feeds = ingestors.data ?? []
  const healthy = feeds.filter((f) => f.isHealthy).length
  const marketOpen = session.data?.isMarketOpen ?? false
  const ksActive = killSwitch.data?.isActive ?? false

  return (
    <div className="page">
      <header className="page__header">
        <div>
          <h1 className="page__title">Welcome back, {user?.userName}</h1>
          <p className="page__subtitle">Console health at a glance, then pick a module.</p>
        </div>
      </header>

      <div className="stat-grid">
        <StatTile
          label="Market"
          value={marketOpen ? 'Open' : 'Closed'}
          tone={marketOpen ? 'pos' : undefined}
          sub="NSE cash session"
        />
        <StatTile
          label="Live feed"
          value={(process.data?.isRunning || healthy > 0) ? 'Running' : 'Stopped'}
          tone={(process.data?.isRunning || healthy > 0) ? (healthy === feeds.length && feeds.length > 0 ? 'pos' : 'warn') : undefined}
          sub={feeds.length > 0 ? `${healthy}/${feeds.length} sources healthy` : 'no heartbeat yet'}
          to="/admin/data/live"
        />
        <StatTile
          label="Broker"
          value={broker.data?.isAuthenticated ? 'Linked' : 'Not linked'}
          tone={broker.data?.isAuthenticated ? 'pos' : 'neg'}
          sub="FYERS session"
          to="/admin/broker"
        />
        <StatTile
          label="Watchlist"
          value={watchlist.data?.length ?? 0}
          sub="live subscriptions"
          to="/admin/data/live"
        />
        <StatTile
          label="Kill switch"
          value={ksActive ? 'ACTIVE' : 'Off'}
          tone={ksActive ? 'neg' : undefined}
          sub={ksActive ? 'all trading halted' : 'trading allowed'}
          to="/admin/risk"
        />
      </div>

      <div className="module-grid">
        {MODULES.map((m) => {
          const Icon = m.icon
          const disabled = m.status === 'planned'
          const card = (
            <>
              <span className="module-card__icon">
                <Icon />
              </span>
              <span className="module-card__name">
                {m.name}
                {m.status === 'ready' && <Badge tone="accent">v2</Badge>}
                {m.status === 'legacy' && <Badge tone="neutral">v1</Badge>}
                {m.status === 'planned' && <Badge tone="neutral">soon</Badge>}
              </span>
              <p className="module-card__desc">{m.description}</p>
            </>
          )
          return disabled ? (
            <div key={m.key} className="module-card module-card--off">
              {card}
            </div>
          ) : (
            <Link key={m.key} to={m.route} className="module-card">
              {card}
            </Link>
          )
        })}
      </div>

      <p className="small-note">
        Modules tagged v1 still use the previous screens; each will be rebuilt on the v2 design —
        Data is first. Per-trader module access will live under Users once the trader console is
        rebuilt.
      </p>
    </div>
  )
}
