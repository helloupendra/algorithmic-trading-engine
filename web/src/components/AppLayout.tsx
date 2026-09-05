/**
 * The signed-in shell, v2: grouped sidebar navigation plus a sticky topbar
 * that keeps the three live health signals — market session, broker session,
 * ingestor heartbeat — visible on every screen. Navigation is built from the
 * module registry and the user's role.
 */

import { NavLink, Outlet, useLocation, useNavigate } from 'react-router-dom'
import { useAuth } from '../lib/auth'
import { useBrokerSession, useIngestorStatuses, useMarketSession } from '../lib/queries'
import {
  BACKTESTING_SECTIONS,
  DATA_SECTIONS,
  STRATEGIES_SECTIONS,
  SYSTEM_SECTIONS,
} from '../lib/modules'
import {
  IconArrowRight,
  IconCandles,
  IconClock,
  IconDashboard,
  IconDatabase,
  IconFlask,
  IconGlobe,
  IconLayers,
  IconLogo,
  IconPlay,
  IconPulse,
  IconSignOut,
} from './icons'

function StatusPill({
  tone,
  label,
  title,
}: {
  tone: 'pos' | 'neg' | 'warn' | 'live' | 'idle'
  label: string
  title?: string
}) {
  return (
    <span className={`pill ${tone === 'idle' ? '' : `pill--${tone}`}`} title={title}>
      <span className="pill__dot" aria-hidden="true" />
      {label}
    </span>
  )
}

/** Market open/closed, broker connected, ingestor heartbeat — the pulse row. */
function TopbarStatus() {
  const { isAdmin } = useAuth()
  const session = useMarketSession()
  const broker = useBrokerSession()
  const ingestors = useIngestorStatuses()

  const market = session.data
  const feeds = ingestors.data ?? []
  const healthyFeeds = feeds.filter((f) => f.isHealthy).length

  // Broker links and ingestor heartbeats are the operator's job. A trader can do
  // nothing about either, and a red pill they cannot act on is just noise. What
  // a trader needs to know about the feed — whether the numbers are fresh — is
  // said on their own pages, next to the numbers.
  const showOperatorPills = isAdmin

  return (
    <div className="topbar__status">
      {market && (
        <StatusPill
          tone={market.isMarketOpen ? 'pos' : 'idle'}
          label={market.isMarketOpen ? 'NSE open' : 'NSE closed'}
          title={
            market.isMarketOpen
              ? 'Market session is live'
              : `Next open: ${new Date(market.nextMarketOpenUtc).toLocaleString('en-IN')}`
          }
        />
      )}
      {showOperatorPills && broker.data && (
        <StatusPill
          tone={broker.data.isAuthenticated ? 'pos' : 'neg'}
          label={broker.data.isAuthenticated ? 'FYERS linked' : 'FYERS not linked'}
          title="Broker session"
        />
      )}
      {showOperatorPills && feeds.length > 0 && (
        <StatusPill
          tone={healthyFeeds === feeds.length ? 'live' : 'warn'}
          label={
            healthyFeeds === feeds.length
              ? `Feed live (${feeds.length})`
              : `Feed degraded (${healthyFeeds}/${feeds.length})`
          }
          title="Live ingestor heartbeat"
        />
      )}
    </div>
  )
}

const TRADER_NAV = [
  { to: '/trader', label: 'Overview', icon: IconDashboard, end: true },
  { to: '/trader/watchlist', label: 'Watchlist', icon: IconPulse },
  { to: '/trader/charts', label: 'Charts', icon: IconCandles },
  { to: '/trader/news', label: 'Market news', icon: IconGlobe },
  { to: '/trader/movers', label: 'Top movers', icon: IconArrowRight },
  { to: '/trader/option-chain', label: 'Option chain', icon: IconLayers },
  { to: '/trader/positions', label: 'Positions', icon: IconDatabase },
  { to: '/trader/orders', label: 'Orders', icon: IconClock },
  { to: '/trader/strategies', label: 'Strategies', icon: IconFlask, end: true },
  { to: '/trader/deploy', label: 'Deploy', icon: IconPlay },
  { to: '/trader/strategies/history', label: 'My runs', icon: IconClock },
]

function NavItem({
  to,
  label,
  icon: Icon,
  end,
  badge,
}: {
  to: string
  label: string
  icon: React.ComponentType<React.SVGProps<SVGSVGElement>>
  end?: boolean
  badge?: string
}) {
  return (
    <NavLink
      to={to}
      end={end}
      className={({ isActive }) => (isActive ? 'nav-link nav-link--active' : 'nav-link')}
    >
      <span className="nav-link__icon">
        <Icon />
      </span>
      {label}
      {badge && <span className="nav-badge">{badge}</span>}
    </NavLink>
  )
}

function AdminNav() {
  // Every module now lives inside the group it belongs to — Connectors with the
  // data it supplies, Users with the rest of platform administration. A generic
  // "Modules" heading said nothing: every entry in this sidebar is one.

  return (
    <>
      <div className="nav-group">
        <NavItem to="/admin" label="Overview" icon={IconDashboard} end />
      </div>

      <div className="nav-group">
        <div className="nav-group__label">Data</div>
        {DATA_SECTIONS.map((s) => (
          <NavItem key={s.route} to={s.route} label={s.label} icon={s.icon} end={s.end} />
        ))}
      </div>

      <div className="nav-group">
        <div className="nav-group__label">Strategies</div>
        {STRATEGIES_SECTIONS.map((s) => (
          <NavItem key={s.route} to={s.route} label={s.label} icon={s.icon} end={s.end} />
        ))}
      </div>

      <div className="nav-group">
        <div className="nav-group__label">Backtesting</div>
        {BACKTESTING_SECTIONS.map((s) => (
          <NavItem key={s.route} to={s.route} label={s.label} icon={s.icon} end={s.end} />
        ))}
      </div>

      <div className="nav-group">
        <div className="nav-group__label">System</div>
        {SYSTEM_SECTIONS.map((s) => (
          <NavItem key={s.route} to={s.route} label={s.label} icon={s.icon} end={s.end} />
        ))}
      </div>

    </>
  )
}

function TraderNav() {
  return (
    <div className="nav-group">
      <div className="nav-group__label">Trading</div>
      {TRADER_NAV.map((item) => (
        <NavItem key={item.to} to={item.to} label={item.label} icon={item.icon} end={item.end} />
      ))}
    </div>
  )
}

/** Section title for the topbar, from the deepest matching route. */
const ROUTE_TITLES: Array<[prefix: string, crumb: string | null, title: string]> = [
  ['/admin/data/live', 'Data', 'Live feeds'],
  ['/admin/data/historical', 'Data', 'Historical'],
  ['/admin/data/instruments', 'Data', 'Instruments & F&O'],
  ['/admin/data', 'Data', 'Overview'],
  ['/admin/strategies/live', 'Strategies', 'Live runner'],
  ['/admin/strategies/history', 'Strategies', 'Run history'],
  ['/admin/strategies/runs/', 'Strategies', 'Run'],
  ['/admin/strategies/library', 'Strategies', 'Library'],
  ['/admin/strategies', 'Strategies', 'Overview'],
  ['/admin/backtesting/runs/', 'Backtesting', 'Run'],
  ['/admin/backtesting/runs', 'Backtesting', 'Runs'],
  ['/admin/backtesting/new', 'Backtesting', 'New backtest'],
  ['/admin/backtesting', 'Backtesting', 'Overview'],
  ['/admin/users/packages', 'System', 'Strategy packages'],
  ['/admin/users', 'System', 'Users & access'],
  ['/admin/system/risk', 'System', 'Risk & kill switch'],
  ['/admin/system/alerts', 'System', 'Alerts'],
  ['/admin/system/logs', 'System', 'Activity log'],
  ['/admin/broker', 'Data', 'Connectors'],
  ['/admin/system', 'System', 'Overview'],
  ['/admin', null, 'Overview'],
  ['/trader/strategies/history', 'Trading', 'My runs'],
  ['/trader/strategies/runs/', 'Trading', 'Live run'],
  ['/trader', null, 'Trading'],
]

export function AppLayout() {
  const { user, isAdmin, logout } = useAuth()
  const navigate = useNavigate()
  const location = useLocation()

  const match = ROUTE_TITLES.find(([prefix]) => location.pathname.startsWith(prefix))
  const initials = (user?.userName ?? '?').slice(0, 2).toUpperCase()

  async function handleSignOut() {
    // Leave the guarded area FIRST, then drop the session. Clearing the user
    // while an admin/trader route is still mounted makes RequireAuth render its
    // own <Navigate to="/login">, which races the homepage navigation below and
    // could win — landing the user on the sign-in page instead of "/".
    navigate('/', { replace: true })
    await logout()
  }

  return (
    <div className="shell">
      <aside className="shell__sidebar">
        <div className="shell__brand">
          <span className="shell__brand-mark" aria-hidden="true">
            <IconLogo />
          </span>
          <span>
            AlgoTrading
            <small>Console</small>
          </span>
        </div>

        <nav aria-label="Main">{isAdmin ? <AdminNav /> : <TraderNav />}</nav>

        <div className="shell__user">
          <span className="shell__avatar" aria-hidden="true">
            {initials}
          </span>
          <div className="shell__user-meta">
            <div className="shell__user-name">{user?.userName}</div>
            <div className="shell__user-role" data-role={user?.role}>
              {user?.role}
            </div>
          </div>
          <button
            type="button"
            className="btn btn--ghost btn--sm"
            onClick={handleSignOut}
            title="Sign out"
            aria-label="Sign out"
          >
            <IconSignOut style={{ width: 15, height: 15 }} />
          </button>
        </div>
      </aside>

      <div className="shell__body">
        <header className="topbar">
          <span className="topbar__title">
            {match?.[1] && <span className="topbar__crumb">{match[1]} / </span>}
            {match?.[2] ?? 'Console'}
          </span>
          <TopbarStatus />
        </header>

        <main className="shell__main">
          <Outlet />
        </main>
      </div>
    </div>
  )
}
