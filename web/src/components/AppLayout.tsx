/**
 * The signed-in shell, v3: one console for admins and traders.
 *
 * A 44px top bar holds the brand, the seven workspaces (Desk, Markets, Trade,
 * Research, AI, Data, System), the ⌘K search, the live status items and the account
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
import { useLiveFeed } from '../lib/live'
import { ACCOUNT_PAGE, accessFor, locate, nameRoute, navFor, routeTitle, tabGroups } from '../lib/modules'
import type { NavPage, NavWorkspace } from '../lib/modules'
import { isPaletteShortcut } from '../lib/palette'
import { installScrollCues } from '../lib/scrollCues'
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
 * more than one (Backtests' overview, new backtest and runs). A thin rule
 * brackets such a group, and only such a group: between two single-page
 * tabs it would say nothing.
 */
function TabStrip({ workspace, current }: { workspace: NavWorkspace; current: NavPage }) {
  const listRef = useRef<HTMLElement>(null)
  const groups = tabGroups(workspace.pages)

  // On a narrow screen the current tab may sit past the edge: bring it to the
  // middle by scrolling the strip sideways only, never the page. The strip is
  // a .scroll-x, so lib/scrollCues fades whichever edge hides more.
  useEffect(() => {
    const list = listRef.current
    if (!list) return
    const reveal = () => {
      const tab = list.querySelector<HTMLElement>('[aria-current="page"]')
      if (!tab) return
      const left = tab.offsetLeft - list.offsetLeft
      const hidden = left < list.scrollLeft || left + tab.offsetWidth > list.scrollLeft + list.clientWidth
      if (!hidden) return
      const centred = left - (list.clientWidth - tab.offsetWidth) / 2
      list.scrollLeft = Math.max(0, Math.min(centred, list.scrollWidth - list.clientWidth))
    }
    reveal()
    window.addEventListener('resize', reveal)
    return () => window.removeEventListener('resize', reveal)
  }, [current])

  return (
    <nav className="shell__tablist scroll-x" aria-label={`${workspace.label} pages`} ref={listRef}>
      {groups.map((group, g) => (
        <Fragment key={group[0].tab.key}>
          {g > 0 && (group.length > 1 || groups[g - 1].length > 1) && <span className="shell__tabsep" aria-hidden="true" />}
          {group.map((page) => (
            <Link key={page.to} to={page.to} className="shell__tab" aria-current={page === current ? 'page' : undefined}>
              {page.label}
            </Link>
          ))}
        </Fragment>
      ))}
    </nav>
  )
}

export function AppLayout() {
  const { user, isAdmin, logout } = useAuth()
  // The live connection lives as long as the signed-in console: it opens on
  // sign-in without a reload and closes on sign-out.
  useLiveFeed()
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

  // Sideways-scrolling tables and strips fade the edge that hides more.
  useEffect(() => installScrollCues(document), [])

  // One name per route (lib/modules.ts), for the strip when it has no tabs
  // to show and for the browser tab: several consoles open side by side
  // otherwise all read as the site's title.
  const name = nameRoute(pathname, nav)
  const title = routeTitle(name)
  useEffect(() => {
    const previous = document.title
    document.title = `${title} · OpenFNO`
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

      {/* A workspace with one page has nothing to switch between: its strip
          is the page's name, as it is for a page outside the workspaces. */}
      <div className={`shell__tabs${onDesk ? ' shell__tabs--desk' : ''}`}>
        {onDesk ? (
          <DeskTitle slotRef={setStripSlot} />
        ) : here && here.workspace.pages.length > 1 ? (
          <TabStrip workspace={here.workspace} current={here.page} />
        ) : (
          <span className="shell__tabs-title">{name.page}</span>
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
