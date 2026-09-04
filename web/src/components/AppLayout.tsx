/**
 * The signed-in shell, v2: grouped sidebar navigation plus a sticky topbar
 * that keeps the three live health signals — market session, broker session,
 * ingestor heartbeat — visible on every screen. Navigation is built from the
 * module registry and the user's role.
 */

import { NavLink, Outlet, useLocation, useNavigate } from 'react-router-dom'
import { useAuth } from '../lib/auth'
import { useBrokerSession, useIngestorStatuses, useMarketSession } from '../lib/queries'
import { BACKTESTING_SECTIONS, DATA_SECTIONS, MODULES, STRATEGIES_SECTIONS } from '../lib/modules'
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
  IconSwitch,
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
  const session = useMarketSession()
  const broker = useBrokerSession()
  const ingestors = useIngestorStatuses()

  const market = session.data
  const feeds = ingestors.data ?? []
  const healthyFeeds = feeds.filter((f) => f.isHealthy).length

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
      {broker.data && (
        <StatusPill
          tone={broker.data.isAuthenticated ? 'pos' : 'neg'}
          label={broker.data.isAuthenticated ? 'FYERS linked' : 'FYERS not linked'}
          title="Broker session"
        />
      )}
      {feeds.length > 0 && (
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
  // Modules with their own nav group above are left out of the legacy list.
  const grouped = new Set(['data', 'strategies', 'backtesting'])
  const legacyModules = MODULES.filter((m) => !grouped.has(m.key) && m.status !== 'planned')

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
        <div className="nav-group__label">Modules</div>
        {legacyModules.map((m) => (
          <NavItem
            key={m.key}
            to={m.route}
            label={m.name}
            icon={m.icon}
            badge={m.status === 'legacy' ? 'v1' : undefined}
          />
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
  ['/admin/users', 'Modules', 'Users'],
  ['/admin/risk', 'Modules', 'Risk'],
  ['/admin/strategies', 'Modules', 'Strategies'],
  ['/admin/live-alerts', 'Modules', 'Alerts'],
  ['/admin/broker', 'Modules', 'Broker'],
  ['/admin/system', 'Modules', 'System'],
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
    await logout()
    // Signing out returns to the public homepage; "Open console" there leads
    // back to /login.
    navigate('/', { replace: true })
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

        {isAdmin && (
          <div className="nav-group">
            <NavItem to="/trader" label="Trader view" icon={IconSwitch} />
          </div>
        )}

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
