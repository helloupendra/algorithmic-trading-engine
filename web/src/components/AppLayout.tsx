/**
 * The signed-in shell, v2: grouped sidebar navigation plus a sticky topbar
 * that keeps the live health signals — backend, market sessions, connectors,
 * feeds — visible on every screen. Navigation is built from the
 * module registry and the user's role.
 */

import { useEffect, useRef, useState } from 'react'
import { NavLink, Outlet, useLocation, useNavigate } from 'react-router-dom'
import { useAuth } from '../lib/auth'
import {
  useBackendStatus,
  useFeeds,
  useIngestorStatuses,
  useMarketSession,
  useProviders,
} from '../lib/queries'
import { calendarPulse, connectorsSummary, feedPulses, marketPulses, recapVendors } from '../lib/pulse'
import type { ConnectorState } from '../lib/pulse'
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
  IconPulse,
  IconSignOut,
  IconX,
  IconUsers,
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

const CONNECTOR_DOT: Record<ConnectorState, string> = {
  ready: 'conn-dot--pos',
  'sign-in': 'conn-dot--neg',
  expired: 'conn-dot--neg',
  'not-set-up': 'conn-dot--idle',
}

/**
 * Every broker and data vendor behind one pill. The pill names only what needs
 * a hand (a sign-in, two feeds at once); the list under it says where each
 * connector stands, and each line opens that connector.
 */
function ConnectorsPill({ tradingDay }: { tradingDay: boolean }) {
  const providers = useProviders()
  const feeds = useFeeds()
  const [open, setOpen] = useState(false)
  const boxRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!open) return
    const onPointer = (e: MouseEvent) => {
      if (boxRef.current && !boxRef.current.contains(e.target as Node)) setOpen(false)
    }
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setOpen(false)
    }
    document.addEventListener('mousedown', onPointer)
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('mousedown', onPointer)
      document.removeEventListener('keydown', onKey)
    }
  }, [open])

  const summary = connectorsSummary(providers.data, feeds.data, tradingDay, Date.now())
  if (!summary) return null

  return (
    <div className="conn-pop" ref={boxRef}>
      <button
        type="button"
        className="topbar__pill-link topbar__pill-button"
        aria-expanded={open}
        aria-haspopup="dialog"
        onClick={() => setOpen((v) => !v)}
        title={open ? undefined : summary.pulse.title}
      >
        <StatusPill tone={summary.pulse.tone} label={summary.pulse.label} />
      </button>
      {open && (
        <div className="conn-pop__panel" role="dialog" aria-label="Connectors">
          <div className="conn-pop__head">
            <span>Connectors</span>
            <span className="faint">
              Live data: {summary.liveFeeds.length > 0 ? summary.liveFeeds.join(', ') : 'no feed running'}
            </span>
          </div>
          <ul className="conn-pop__list">
            {summary.lines.map((line) => (
              <li key={line.key}>
                <NavLink to={`/admin/broker/${line.key}`} className="conn-pop__row" onClick={() => setOpen(false)}>
                  <span className={`conn-dot ${CONNECTOR_DOT[line.state]}`} aria-hidden="true" />
                  <span className="conn-pop__name">
                    {line.name} <span className="faint">{line.role}</span>
                  </span>
                  <span className="conn-pop__detail">
                    {line.session}
                    {line.feed && <span className={line.feedRunning ? 'pos' : 'faint'}> · {line.feed}</span>}
                  </span>
                </NavLink>
              </li>
            ))}
          </ul>
          <div className="conn-pop__foot">
            <NavLink to="/admin/broker" onClick={() => setOpen(false)}>
              All connectors
            </NavLink>
            <NavLink to="/admin/data/live" onClick={() => setOpen(false)}>
              Live feeds
            </NavLink>
          </div>
        </div>
      )}
    </div>
  )
}

/**
 * What is trading, and what is feeding it — the pulse row. The rules for which
 * pill appears live in lib/pulse.ts.
 */
function TopbarStatus() {
  const { isAdmin } = useAuth()
  const session = useMarketSession()
  const mcxSession = useMarketSession('MCX', 'COM')
  const backend = useBackendStatus()
  const ingestors = useIngestorStatuses()
  // Broker links and feeds are the operator's job. A trader can do nothing
  // about either, and a red pill they cannot act on is just noise. What a
  // trader needs to know about the feed — whether the numbers are fresh — is
  // said on their own pages, next to the numbers. A replay is the exception:
  // it changes what their recap runs trade on, so "NSE recap" is for everyone.
  const showOperatorPills = isAdmin
  const feedList = useFeeds({ enabled: showOperatorPills })

  // With the API down, a session or broker chip would only be repeating the
  // last answer it got — "MCX open" from ten minutes ago, presented as now.
  // Nothing about the market is known while the backend is unreachable, so
  // nothing about it is shown; the red "Backend down" chip is the whole story.
  const market = backend.isDown ? undefined : session.data
  const mcx = backend.isDown ? undefined : mcxSession.data
  const heartbeats = backend.isDown ? undefined : ingestors.data
  const feeds = backend.isDown ? undefined : feedList.data
  const markets = marketPulses(market, mcx, recapVendors(heartbeats, feeds))
  const calendar = showOperatorPills ? calendarPulse(market, mcx) : null
  const feedPills = showOperatorPills ? feedPulses(feeds, heartbeats, market?.isMarketOpen === true, Date.now()) : []

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
      {markets.map((p) => (
        <StatusPill key={p.key} tone={p.tone} label={p.label} title={p.title} />
      ))}
      {calendar && (
        <NavLink to="/admin/system/calendar" className="topbar__pill-link">
          <StatusPill tone={calendar.tone} label={calendar.label} title={calendar.title} />
        </NavLink>
      )}
      {showOperatorPills && !backend.isDown && (
        // A missing sign-in is an alarm only on a day NSE trades. On a holiday
        // nothing needs a token, and a red pill says the opposite.
        <ConnectorsPill tradingDay={market?.isTradingDay !== false} />
      )}
      {feedPills.map((p) => (
        <NavLink key={`feed-${p.key}`} to="/admin/data/live" className="topbar__pill-link">
          <StatusPill tone={p.tone} label={p.label} title={`${p.title} — open Live feeds`} />
        </NavLink>
      ))}
    </div>
  )
}

/**
 * The Notebook module's sidebar group: one entry today, the whiteboard list
 * (a board's own page is reached from there). Declared beside the trader sections
 * rather than in the module registry, which feeds the overview grid a card per
 * module — the registry entry is its own change.
 */
const NOTEBOOK_SECTIONS = [{ route: '/admin/notebook', label: 'Whiteboards', icon: IconPen, end: false }]

/**
 * The trader's sidebar, in the order a trading day is lived: what is running
 * and what it is making first, then the market to look at, then the tools
 * used now and then. Grouped like the operator's side so both feel like one
 * console — and so the list stays short even as pages are added.
 */
const TRADER_TRADE_SECTIONS = [
  { route: '/trader/deploy', label: 'Strategies', icon: IconFlask, end: false },
  { route: '/trader/strategies/history', label: 'My runs', icon: IconClock, end: false },
  { route: '/trader/positions', label: 'Positions', icon: IconDatabase, end: false },
  { route: '/trader/orders', label: 'Orders', icon: IconClock, end: false },
]

const TRADER_MARKET_SECTIONS = [
  { route: '/trader/watchlist', label: 'Watchlist', icon: IconPulse, end: false },
  { route: '/trader/charts', label: 'Charts', icon: IconCandles, end: false },
  { route: '/trader/structure', label: 'Market structure', icon: IconCandles, end: false },
  { route: '/trader/option-chain', label: 'Option chain', icon: IconLayers, end: false },
  { route: '/trader/movers', label: 'Top movers', icon: IconArrowRight, end: false },
  { route: '/trader/news', label: 'Market news', icon: IconGlobe, end: false },
]

const TRADER_TOOL_SECTIONS = [
  { route: '/trader/trading', label: 'Manual order', icon: IconArrowRight, end: true },
  { route: '/trader/trading/lab', label: 'Filter lab', icon: IconFlask, end: false },
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
  defaultOpen = false,
}: {
  label: string
  sections: ReadonlyArray<{
    route: string
    label: string
    icon: React.ComponentType<React.SVGProps<SVGSVGElement>>
    end?: boolean
  }>
  /** Open on a first visit, before the person has chosen; a group they close stays closed. */
  defaultOpen?: boolean
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
    return holdsCurrentRoute || defaultOpen
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
    <>
      <div className="nav-group">
        <NavItem to="/trader" label="Overview" icon={IconDashboard} end />
      </div>

      <NavGroup label="Trade" sections={TRADER_TRADE_SECTIONS} defaultOpen />
      <NavGroup label="Markets" sections={TRADER_MARKET_SECTIONS} defaultOpen />
      <NavGroup label="Tools" sections={TRADER_TOOL_SECTIONS} />

      <div className="nav-group">
        <NavItem to="/trader/account" label="Account" icon={IconUsers} />
      </div>
    </>
  )
}

/** Section title for the topbar, from the deepest matching route. */
const ROUTE_TITLES: Array<[prefix: string, crumb: string | null, title: string]> = [
  ['/admin/data/live', 'Data', 'Live feeds'],
  ['/admin/data/commodity', 'Data', 'Commodity'],
  ['/admin/data/chain', 'Data', 'Option chain'],
  ['/admin/data/open-interest', 'Data', 'Open interest'],
  ['/admin/data/historical', 'Data', 'Historical'],
  ['/admin/data/structure', 'Data', 'Market structure'],
  ['/admin/data/instruments', 'Data', 'Instruments & F&O'],
  ['/admin/data/movers', 'Data', 'Market movers'],
  ['/admin/data/factors', 'Data', 'Market factors'],
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
  ['/admin/data/patterns', 'Data', 'Pattern alerts'],
  ['/admin/system/calendar', 'System', 'Market calendar'],
  ['/admin/system/logs', 'System', 'Activity log'],
  ['/admin/system/deployments', 'System', 'Deployments'],
  ['/admin/broker', 'System', 'Connectors'],
  ['/admin/notebook/', 'Notebook', 'Whiteboard'],
  ['/admin/notebook', 'Notebook', 'Whiteboards'],
  ['/admin/system', 'System', 'Overview'],
  ['/admin', null, 'Overview'],
  ['/trader/strategies/history', 'Trade', 'My runs'],
  ['/trader/strategies/runs/', 'Trade', 'Live run'],
  ['/trader/strategies/', 'Trade', 'How it works'],
  ['/trader/deploy', 'Trade', 'Strategies'],
  ['/trader/positions', 'Trade', 'Positions'],
  ['/trader/orders', 'Trade', 'Orders'],
  ['/trader/watchlist', 'Markets', 'Watchlist'],
  ['/trader/charts', 'Markets', 'Charts'],
  ['/trader/structure', 'Markets', 'Market structure'],
  ['/trader/option-chain', 'Markets', 'Option chain'],
  ['/trader/movers', 'Markets', 'Top movers'],
  ['/trader/news', 'Markets', 'Market news'],
  ['/trader/trading/lab', 'Tools', 'Filter lab'],
  ['/trader/trading', 'Tools', 'Manual order'],
  ['/trader/account', null, 'Account'],
  ['/trader', null, 'Overview'],
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
          <span className="shell__brand-word">
            open<b>fno</b>
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
