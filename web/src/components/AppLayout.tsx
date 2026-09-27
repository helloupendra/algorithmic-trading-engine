/**
 * The signed-in shell, v3: one console for admins and traders.
 *
 * A 44px top bar holds the brand, the six workspaces (Desk, Markets, Trade,
 * Research, Data, System), the ⌘K search, the live status items and the account
 * menu. Under it, a 38px strip of the current workspace's tabs; on a phone
 * the workspaces move to a bottom bar. Which workspaces and tabs exist comes
 * from lib/modules.ts and the user's role and grants, and the current one from
 * the URL, so the shell has no navigation state of its own.
 *
 * Signed-in only: RequireAuth wraps this route in App.tsx, and RequireRole
 * guards the admin pages beneath it.
 */

import { Fragment, useEffect, useMemo, useRef, useState } from 'react'
import { Link, Outlet, useLocation, useNavigate } from 'react-router-dom'
import { useAuth } from '../lib/auth'
import { ACCOUNT_PAGE, accessFor, locate, navFor } from '../lib/modules'
import type { NavPage, NavWorkspace } from '../lib/modules'
import { isPaletteShortcut } from '../lib/palette'
import { IconLogo, IconSearch } from './icons'
import { StatusItems } from './shell/StatusItems'
import { useIstDate } from './shell/useIstDate'
import { CommandPalette } from './shell/CommandPalette'
import { UserMenu } from './shell/UserMenu'
import type { ShellOutlet } from './shell/stripSlot'
import './shell/shell.css'

const IS_MAC = typeof navigator !== 'undefined' && /Mac|iPhone|iPad/.test(navigator.userAgent)

/**
 * The Desk has one page, so its strip is a title and the date, not a tab,
 * then a slot the Desk fills with its own switches (phase, account).
 */
function DeskTitle({ slotRef }: { slotRef: (el: HTMLDivElement | null) => void }) {
  const date = useIstDate()
  return (
    <>
      <span className="shell__tabs-title">Desk</span>
      <span className="shell__tabs-meta">{date}</span>
      <div className="shell__tabs-tools" ref={slotRef} />
    </>
  )
}

/**
 * The current workspace's pages, one per tab but for the few tabs that hold
 * more than one (Backtests' overview, new backtest and runs); a thin rule
 * separates one tab's pages from the next.
 */
function TabStrip({ workspace, current }: { workspace: NavWorkspace; current: NavPage }) {
  const listRef = useRef<HTMLElement>(null)

  // On a narrow screen the current tab may sit past the edge: bring it in by
  // scrolling the strip sideways only, never the page.
  useEffect(() => {
    const list = listRef.current
    const tab = list?.querySelector<HTMLElement>('[aria-current="page"]')
    if (!list || !tab) return
    const left = tab.offsetLeft - list.offsetLeft
    if (left < list.scrollLeft || left + tab.offsetWidth > list.scrollLeft + list.clientWidth) {
      list.scrollLeft = Math.max(0, left - 24)
    }
  }, [current])

  return (
    <nav className="shell__tablist" aria-label={`${workspace.label} pages`} ref={listRef}>
      {workspace.pages.map((page, i) => (
        <Fragment key={page.to}>
          {i > 0 && workspace.pages[i - 1].tab.key !== page.tab.key && <span className="shell__tabsep" aria-hidden="true" />}
          <Link to={page.to} className="shell__tab" aria-current={page === current ? 'page' : undefined}>
            {page.label}
          </Link>
        </Fragment>
      ))}
    </nav>
  )
}

export function AppLayout() {
  const { user, isAdmin, logout } = useAuth()
  const navigate = useNavigate()
  const { pathname } = useLocation()
  const access = useMemo(() => accessFor(user), [user])
  const nav = useMemo(() => navFor(access), [access])
  const here = locate(pathname, nav)
  const [paletteOpen, setPaletteOpen] = useState(false)
  const [stripSlot, setStripSlot] = useState<HTMLElement | null>(null)
  const onDesk = here?.workspace.key === 'desk'

  const extras = useMemo(
    () => (isAdmin ? [] : [{ label: ACCOUNT_PAGE.label, to: ACCOUNT_PAGE.to, keywords: ['broker', 'capital', 'profile'] }]),
    [isAdmin],
  )
  const onAccount = !isAdmin && pathname === ACCOUNT_PAGE.to

  // ⌘K / Ctrl-K from anywhere in the console, even from inside a field.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (!isPaletteShortcut(e)) return
      e.preventDefault()
      setPaletteOpen((v) => !v)
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])

  // Back and forward close it too, not only a pick from the list.
  useEffect(() => setPaletteOpen(false), [pathname])

  // The browser tab names the page: several consoles open side by side
  // otherwise all read as the site's title.
  const title = here
    ? here.page.label === here.workspace.label
      ? here.page.label
      : `${here.page.label} · ${here.workspace.label}`
    : onAccount
      ? ACCOUNT_PAGE.label
      : null
  useEffect(() => {
    const previous = document.title
    document.title = title ? `${title} · OpenFNO` : 'OpenFNO console'
    return () => {
      document.title = previous
    }
  }, [title])

  async function handleSignOut() {
    // Leave the guarded area FIRST, then drop the session. Clearing the user
    // while an admin/trader route is still mounted makes RequireAuth render its
    // own <Navigate to="/login">, which races the homepage navigation below and
    // could win — landing the user on the sign-in page instead of "/".
    navigate('/', { replace: true })
    await logout()
  }

  const desk = nav[0]?.to ?? '/'
  const workspaceLinks = (withIcons: boolean) =>
    nav.map((ws) => {
      const Icon = ws.icon
      return (
        <Link key={ws.key} to={ws.to} className="shell__ws-link" aria-current={here?.workspace.key === ws.key ? 'true' : undefined}>
          {withIcons && <Icon aria-hidden="true" />}
          {ws.label}
        </Link>
      )
    })

  return (
    <div className="shell">
      <header className="shell__top">
        <Link to={desk} className="shell__brand" aria-label="OpenFNO: the Desk">
          <span className="shell__brand-mark" aria-hidden="true">
            <IconLogo />
          </span>
          <span className="shell__brand-word">
            open<b>fno</b>
          </span>
        </Link>
        <nav className="shell__ws" aria-label="Workspaces">
          {workspaceLinks(false)}
        </nav>
        <div className="shell__grow" />
        <button
          type="button"
          className="shell__search"
          onClick={() => setPaletteOpen(true)}
          aria-label="Search symbols, runs and pages"
          aria-keyshortcuts="Meta+K Control+K"
        >
          <IconSearch aria-hidden="true" />
          <span>Search symbols, runs, pages</span>
          <kbd>{IS_MAC ? '⌘K' : 'Ctrl K'}</kbd>
        </button>
        <StatusItems />
        <UserMenu user={user} isAdmin={isAdmin} onSignOut={handleSignOut} />
      </header>

      <div className={`shell__tabs${onDesk ? ' shell__tabs--desk' : ''}`}>
        {onDesk ? (
          <DeskTitle slotRef={setStripSlot} />
        ) : here ? (
          <TabStrip workspace={here.workspace} current={here.page} />
        ) : (
          title && <span className="shell__tabs-title">{title}</span>
        )}
      </div>

      <main className="shell__main">
        <Outlet context={{ stripSlot } satisfies ShellOutlet} />
      </main>

      <nav className="shell__bottom" aria-label="Workspaces">
        {workspaceLinks(true)}
      </nav>

      {paletteOpen && <CommandPalette nav={nav} access={access} extras={extras} onClose={() => setPaletteOpen(false)} />}
    </div>
  )
}
