/**
 * The signed-in shell, v2: grouped sidebar navigation plus a sticky topbar
 * that keeps the three live health signals — market session, broker session,
 * ingestor heartbeat — visible on every screen. Navigation is built from the
 * module registry and the user's role.
 */

import { useEffect, useState } from 'react'
import { NavLink, Outlet, useLocation, useNavigate } from 'react-router-dom'
import { useAuth } from '../lib/auth'
import {
  useBackendStatus,
  useBrokerSession,
  useIngestorStatuses,
  useMarketSession,
} from '../lib/queries'
import {
  BACKTESTING_SECTIONS,
  DATA_SECTIONS,
  STRATEGIES_SECTIONS,
  TRADING_SECTIONS,
  SYSTEM_SECTIONS,
} from '../lib/modules'
import {
  IconArrowRight,
  IconCandles,
  IconChevronDown,
  IconChevronRight,
  IconClock,
  IconDashboard,
  IconDatabase,
  IconFlask,
  IconGlobe,
  IconLayers,
  IconLogo,
  IconMenu,
  IconPen,
  IconPlay,
  IconPulse,
  IconSignOut,
  IconX,
} from './icons'

/** "2h 14m", "6m", "48s" — short enough for a chip. */
function formatUptime(seconds: number): string {
  if (seconds < 60) return `${Math.max(0, Math.round(seconds))}s`
  const minutes = Math.floor(seconds / 60)
  if (minutes < 60) return `${minutes}m`
  const hours = Math.floor(minutes / 60)
  const rest = minutes % 60
  return rest === 0 ? `${hours}h` : `${hours}h ${rest}m`
}

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
  const mcxSession = useMarketSession('MCX', 'COM')
  const broker = useBrokerSession()
  const backend = useBackendStatus()
  const ingestors = useIngestorStatuses()

  const market = session.data
  const mcx = mcxSession.data
  const feeds = ingestors.data ?? []
  const healthyFeeds = feeds.filter((f) => f.isHealthy).length

  // Broker links and ingestor heartbeats are the operator's job. A trader can do
  // nothing about either, and a red pill they cannot act on is just noise. What
  // a trader needs to know about the feed — whether the numbers are fresh — is
  // said on their own pages, next to the numbers.
  const showOperatorPills = isAdmin

  return (
    <div className="topbar__status">
      {/* First in the row on purpose: when the backend is down every other chip
          is stale, and this is the one that explains why. */}
      {backend.isDown ? (
        <StatusPill tone="neg" label="Backend down" title="The API is not answering — it may be restarting." />
      ) : backend.restartedAt ? (
        <StatusPill
          tone="warn"
          label={`Backend restarted ${new Date(backend.restartedAt).toLocaleTimeString('en-IN')}`}
          title="A new backend process is running. Refresh if a page looks stale."
        />
      ) : (
        backend.data && (
          <StatusPill
            tone="pos"
            label={`Backend up ${formatUptime(backend.data.uptimeSeconds)}`}
            title={`Started ${new Date(backend.data.startedUtc).toLocaleString('en-IN')}${
              backend.data.environment ? ` · ${backend.data.environment}` : ''
            }`}
          />
        )
      )}
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
      {/* MCX keeps trading for eight hours after NSE stops, so one "market"
          chip could never be right for both. Its close follows New York's
          daylight saving: 23:55 IST in summer, 23:30 in winter. */}
      {mcx && (
        <StatusPill
          tone={mcx.isMarketOpen ? 'pos' : 'idle'}
          label={mcx.isMarketOpen ? 'MCX open' : 'MCX closed'}
          title={
            mcx.isMarketOpen
              ? `Commodity session is live until ${new Date(mcx.sessionCloseUtc).toLocaleTimeString('en-IN')}`
              : `Next open: ${new Date(mcx.nextMarketOpenUtc).toLocaleString('en-IN')}`
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

/**
 * The Notebook module's sidebar group: one entry today, the whiteboard list
 * (a board's own page is reached from there). Declared beside TRADER_NAV
 * rather than in the module registry, which feeds the overview grid a card per
 * module — the registry entry is its own change.
 */
const NOTEBOOK_SECTIONS = [{ route: '/admin/notebook', label: 'Whiteboards', icon: IconPen, end: false }]

const TRADER_NAV = [
  { to: '/trader', label: 'Overview', icon: IconDashboard, end: true },
  { to: '/trader/watchlist', label: 'Watchlist', icon: IconPulse },
  { to: '/trader/charts', label: 'Charts', icon: IconCandles },
  { to: '/trader/news', label: 'Market news', icon: IconGlobe },
  { to: '/trader/movers', label: 'Top movers', icon: IconArrowRight },
  { to: '/trader/option-chain', label: 'Option chain', icon: IconLayers },
  { to: '/trader/positions', label: 'Positions', icon: IconDatabase },
  { to: '/trader/orders', label: 'Orders', icon: IconClock },
  { to: '/trader/trading', label: 'Manual order', icon: IconArrowRight },
  { to: '/trader/trading/lab', label: 'Filter lab', icon: IconFlask },
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

/**
 * A collapsible sidebar group.
 *
 * The admin nav is four groups of five or six links each. Fully expanded that
 * is twenty-odd rows, which on a laptop pushes System below the fold and on a
 * phone filled the entire drawer - so the groups fold, and only the one you are
 * working in needs to be open.
 *
 * Open state is remembered per group, because a nav that re-collapses on every
 * navigation is worse than one that never folded at all. Walking into a group
 * from elsewhere opens it; nothing ever closes a group behind your back.
 */
function NavGroup({
  label,
  sections,
}: {
  label: string
  sections: ReadonlyArray<{
    route: string
    label: string
    icon: React.ComponentType<React.SVGProps<SVGSVGElement>>
    end?: boolean
  }>
}) {
  const location = useLocation()
  const storageKey = `algotrading.nav.${label.toLowerCase()}`
  const holdsCurrentRoute = sections.some((x) => location.pathname.startsWith(x.route))

  const [open, setOpen] = useState(() => {
    try {
      const saved = localStorage.getItem(storageKey)
      if (saved !== null) return saved === '1'
    } catch {
      // Private mode, or storage disabled. Fall through to the route.
    }
    return holdsCurrentRoute
  })

  // Navigating into a group reveals it. Deliberately one-way: this must not
  // close a group the operator opened to look at something.
  useEffect(() => {
    if (holdsCurrentRoute) setOpen(true)
  }, [holdsCurrentRoute])

  useEffect(() => {
    try {
      localStorage.setItem(storageKey, open ? '1' : '0')
    } catch {
      // Remembering is a convenience, not a requirement.
    }
  }, [open, storageKey])

  const groupId = `nav-group-${label.toLowerCase()}`

  return (
    <div className="nav-group">
      <button
        type="button"
        className="nav-group__toggle"
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
        aria-controls={groupId}
      >
        <span className="nav-group__label">{label}</span>
        {/* A dot when the group is folded away but holds the current page, so a
            collapsed sidebar still says where you are. */}
        {!open && holdsCurrentRoute && <span className="nav-group__dot" aria-hidden="true" />}
        <span className="nav-group__chevron" aria-hidden="true">
          {open ? <IconChevronDown /> : <IconChevronRight />}
        </span>
      </button>
      <div id={groupId} className="nav-group__items" hidden={!open}>
        {sections.map((x) => (
          <NavItem key={x.route} to={x.route} label={x.label} icon={x.icon} end={x.end} />
        ))}
      </div>
    </div>
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

      <NavGroup label="Data" sections={DATA_SECTIONS} />
      <NavGroup label="Trading" sections={TRADING_SECTIONS} />
      <NavGroup label="Strategies" sections={STRATEGIES_SECTIONS} />
      <NavGroup label="Backtesting" sections={BACKTESTING_SECTIONS} />
      <NavGroup label="Notebook" sections={NOTEBOOK_SECTIONS} />
      <NavGroup label="System" sections={SYSTEM_SECTIONS} />

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
  ['/admin/data/commodity', 'Data', 'Commodity'],
  ['/admin/data/chain', 'Data', 'Option chain'],
  ['/admin/data/open-interest', 'Data', 'Open interest'],
  ['/admin/data/historical', 'Data', 'Historical'],
  ['/admin/data/instruments', 'Data', 'Instruments & F&O'],
  ['/admin/data', 'Data', 'Overview'],
  ['/admin/strategies/live', 'Strategies', 'Live runner'],
  ['/admin/strategies/history', 'Strategies', 'Run history'],
  ['/admin/strategies/runs/', 'Strategies', 'Run'],
  ['/admin/strategies/library/', 'Strategies', 'How it works'],
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
  ['/admin/system/deployments', 'System', 'Deployments'],
  ['/admin/broker', 'Data', 'Connectors'],
  ['/admin/notebook/', 'Notebook', 'Whiteboard'],
  ['/admin/notebook', 'Notebook', 'Whiteboards'],
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

  // The sidebar is a drawer below 760px. It used to simply stack above the
  // content there, which meant every page on a phone opened on twenty nav links
  // with the actual screen somewhere past the fold.
  const [navOpen, setNavOpen] = useState(false)

  // Tapping a link should navigate AND get out of the way.
  useEffect(() => { setNavOpen(false) }, [location.pathname])

  // A drawer that traps the page behind it must also stop it scrolling, or the
  // content slides around under the overlay while the drawer stays put.
  useEffect(() => {
    if (!navOpen) return
    const previous = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setNavOpen(false) }
    window.addEventListener('keydown', onKey)
    return () => {
      document.body.style.overflow = previous
      window.removeEventListener('keydown', onKey)
    }
  }, [navOpen])

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
    <div className={`shell ${navOpen ? 'shell--nav-open' : ''}`}>
      {/* Only rendered while open, so it cannot swallow taps when closed. */}
      {navOpen && (
        <div
          className="shell__scrim"
          onClick={() => setNavOpen(false)}
          aria-hidden="true"
        />
      )}
      <aside className="shell__sidebar" id="main-nav">
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
          <button
            type="button"
            className="topbar__nav-toggle"
            onClick={() => setNavOpen((v) => !v)}
            aria-label={navOpen ? 'Close navigation' : 'Open navigation'}
            aria-expanded={navOpen}
            aria-controls="main-nav"
          >
            {navOpen ? <IconX /> : <IconMenu />}
          </button>
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
