/**
 * The signed-in shell: sidebar navigation, user badge, sign out.
 *
 * Navigation is built from the user's role so a trader never sees an admin
 * destination they would only get a 403 from.
 */

import { NavLink, Outlet, useNavigate } from 'react-router-dom'
import { useAuth } from '../lib/auth'

interface NavItem {
  to: string
  label: string
  /** Emoji stand-in until an icon set is chosen. */
  icon: string
  end?: boolean
}

const TRADER_NAV: NavItem[] = [
  { to: '/trader', label: 'Overview', icon: '📊', end: true },
  { to: '/trader/watchlist', label: 'Watchlist', icon: '👁' },
  { to: '/trader/charts', label: 'Charts', icon: '📉' },
  { to: '/trader/news', label: 'Market news', icon: '📰' },
  { to: '/trader/movers', label: 'Top movers', icon: '🚀' },
  { to: '/trader/option-chain', label: 'Option chain', icon: '⛓' },
  { to: '/trader/positions', label: 'Positions', icon: '📈' },
  { to: '/trader/orders', label: 'Orders', icon: '🧾' },
  { to: '/trader/strategies', label: 'Strategies', icon: '🤖' },
  { to: '/trader/deploy', label: 'Deploy', icon: '🎯' },
]

const ADMIN_NAV: NavItem[] = [
  { to: '/admin', label: 'System status', icon: '🖥', end: true },
  { to: '/admin/broker', label: 'Broker (FYERS)', icon: '🔌' },
  { to: '/trader/deploy', label: 'Deploy strategy', icon: '🎯' },
  { to: '/admin/users', label: 'Users', icon: '👥' },
  { to: '/admin/risk', label: 'Risk & kill switch', icon: '🛑' },
  { to: '/admin/strategies', label: 'Strategy control', icon: '🎛' },
  { to: '/admin/instruments', label: 'Instruments', icon: '🗃' },
  { to: '/admin/ingestion', label: 'Data ingestion', icon: '📡' },
  { to: '/admin/live-alerts', label: 'Live Alerts', icon: '⚡' },
]

export function AppLayout() {
  const { user, isAdmin, logout } = useAuth()
  const navigate = useNavigate()

  const navItems = isAdmin ? ADMIN_NAV : TRADER_NAV

  async function handleSignOut() {
    await logout()
    navigate('/login', { replace: true })
  }

  return (
    <div className="shell">
      <aside className="shell__sidebar">
        <div className="shell__brand">
          <span className="shell__brand-mark" aria-hidden="true">
            ▲
          </span>
          <span>AlgoTrading</span>
        </div>

        <nav className="shell__nav" aria-label="Main">
          {navItems.map((item) => (
            <NavLink
              key={item.to}
              to={item.to}
              end={item.end}
              className={({ isActive }) =>
                isActive ? 'shell__nav-link shell__nav-link--active' : 'shell__nav-link'
              }
            >
              <span className="shell__nav-icon" aria-hidden="true">
                {item.icon}
              </span>
              {item.label}
            </NavLink>
          ))}
        </nav>

        {/* An admin can reach the trading screens too; a trader has no admin link. */}
        {isAdmin && (
          <NavLink to="/trader" className="shell__nav-link shell__nav-link--secondary">
            <span className="shell__nav-icon" aria-hidden="true">
              ↔
            </span>
            Trader view
          </NavLink>
        )}

        <div className="shell__user">
          <div className="shell__user-name">{user?.userName}</div>
          <div className="shell__user-role" data-role={user?.role}>
            {user?.role}
          </div>
          <button type="button" className="btn btn--ghost btn--sm" onClick={handleSignOut}>
            Sign out
          </button>
        </div>
      </aside>

      <main className="shell__main">
        <Outlet />
      </main>
    </div>
  )
}
