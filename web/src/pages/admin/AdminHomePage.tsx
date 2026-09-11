/**
 * Admin home — the console front door. One card per module (from the module
 * registry) plus the live health strip.
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
  const mcxSession = useMarketSession('MCX', 'COM')
  const broker = useBrokerSession()
  const process = useIngestorProcessStatus()
  const ingestors = useIngestorStatuses()
  const watchlist = useWatchlist()
  const killSwitch = useKillSwitch()

  const feeds = ingestors.data ?? []
  const healthy = feeds.filter((f) => f.isHealthy).length
  const marketOpen = session.data?.isMarketOpen ?? false
  const mcxOpen = mcxSession.data?.isMarketOpen ?? false
  const ksActive = killSwitch.data?.isActive ?? false
  const feedRunning = (process.data?.isRunning ?? false) || healthy > 0

  // These tiles are the first thing read each morning, so none of them may
  // answer before it has been told. Until a query returns, the tile says so:
  // a "Stopped" feed or a "Not linked" broker that is really just a request in
  // flight is the kind of thing that sends you restarting something healthy.
  const unknown = '—'

  // Only the hour is worth showing here; the exact close is in the chip's title
  // on the topbar. MCX ends at 23:55 IST in summer and 23:30 in winter.
  const mcxCloseLabel = mcxSession.data
    ? new Date(mcxSession.data.sessionCloseUtc).toLocaleTimeString('en-IN', {
        hour: '2-digit',
        minute: '2-digit',
      })
    : null

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
          label="Equity market"
          value={session.isPending ? unknown : marketOpen ? 'Open' : 'Closed'}
          tone={marketOpen ? 'pos' : undefined}
          sub="NSE cash session"
        />
        {/* Its own tile rather than a second line on the equity one: after 15:30
            the two disagree for eight hours, which is exactly the stretch when
            reading one and assuming the other is how you misjudge the desk. */}
        <StatTile
          label="Commodity market"
          value={mcxSession.isPending ? unknown : mcxOpen ? 'Open' : 'Closed'}
          tone={mcxOpen ? 'pos' : undefined}
          sub={mcxOpen && mcxCloseLabel ? `MCX until ${mcxCloseLabel}` : 'MCX session'}
          to="/admin/data/commodity"
        />
        <StatTile
          label="Live feed"
          value={process.isPending && ingestors.isPending ? unknown : feedRunning ? 'Running' : 'Stopped'}
          tone={feedRunning ? (healthy === feeds.length && feeds.length > 0 ? 'pos' : 'warn') : undefined}
          sub={feeds.length > 0 ? `${healthy}/${feeds.length} sources healthy` : 'no heartbeat yet'}
          to="/admin/data/live"
        />
        <StatTile
          label="Broker"
          value={broker.isPending ? unknown : broker.data?.isAuthenticated ? 'Linked' : 'Not linked'}
          tone={broker.isPending ? undefined : broker.data?.isAuthenticated ? 'pos' : 'neg'}
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
          value={killSwitch.isPending ? unknown : ksActive ? 'ACTIVE' : 'Off'}
          tone={ksActive ? 'neg' : undefined}
          sub={killSwitch.isPending ? 'checking…' : ksActive ? 'all trading halted' : 'trading allowed'}
          to="/admin/system/risk"
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
                {/* Only a module that cannot be opened yet needs a tag. Which
                    design generation a working module was built on is our
                    business, not something to label the front door with. */}
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
    </div>
  )
}
