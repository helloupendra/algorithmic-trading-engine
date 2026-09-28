/**
 * Workspace registry: the single source of truth for what this console is made
 * of, where each part lives and who may see it.
 *
 * The console is six workspaces (Desk, Markets, Trade, Research, Data,
 * System), each holding tabs. A tab declares what it requires: the Admin role, or one of
 * the module grants the API enforces (PlatformModules on the server). The top
 * bar, the tab strip, the phone's bottom bar and the ⌘K palette all read from
 * here, so a part the user may not use is absent everywhere at once rather
 * than greyed out in one place and linked in another. App.tsx serves exactly
 * these URLs, and a page hidden from traders here sits behind the router's
 * Admin guard there; routeMap.test.ts holds the two together.
 *
 * Hiding is a courtesy, never the control: every endpoint checks the grant
 * itself (RequireModule), so a trader who types a URL still gets a 403.
 *
 * One URL per page for every role. The API already scopes what it answers to
 * the caller, so a page that differs by role picks its view, not its address.
 * The URLs the console had before the workspaces redirect here (routeMap.ts).
 */

import type { ComponentType, SVGProps } from 'react'
import type { MeResponse } from './api'
import { IconCandles, IconDashboard, IconDatabase, IconFlask, IconServer, IconSwitch } from '../components/icons'

/** The module keys the server grants to traders (PlatformModules.cs). */
export const GRANT_KEYS = ['strategies', 'backtesting', 'market-data', 'notebook', 'analysis'] as const
export type GrantKey = (typeof GRANT_KEYS)[number]

/** What a tab needs beyond being signed in: the Admin role, or one grant. */
export type Requirement = 'admin' | GrantKey

export type WorkspaceKey = 'desk' | 'markets' | 'trade' | 'research' | 'data' | 'system'

/** The two consoles one registry serves. */
export type Side = 'admin' | 'trader'

export interface PageDef {
  label: string
  /** Its URL, the same for every role. */
  to: string
  /**
   * The one console that has this page, when only one does: the admin's run
   * cards, the admin's backtest pages. Unset, both have it.
   */
  only?: Side
  /** Only this exact path, not the routes under it (they may be pages of their own). */
  exact?: boolean
  /**
   * More routes that light this page up, as path prefixes. A prefix ending in
   * "/" claims only the routes under it, not the path itself.
   */
  owns?: string[]
  /** Extra words ⌘K matches on. */
  keywords?: string[]
}

export interface TabDef {
  key: string
  label: string
  /** The tab's URL: its pages live at it or under it. */
  home: string
  requires?: Requirement
  pages: PageDef[]
}

export interface WorkspaceDef {
  key: WorkspaceKey
  label: string
  home: string
  /** One line: what the workspace is for. */
  description: string
  icon: ComponentType<SVGProps<SVGSVGElement>>
  /** The tab a click on the workspace opens, when the user can see it; otherwise the first tab. */
  landing?: string
  tabs: TabDef[]
}

export const WORKSPACES: readonly WorkspaceDef[] = [
  {
    key: 'desk',
    label: 'Desk',
    home: '/desk',
    description: 'Today on one sheet: readiness, runs, P&L, risk and the market around them.',
    icon: IconDashboard,
    tabs: [
      {
        key: 'desk',
        label: 'Desk',
        home: '/desk',
        // The panels check their own grants.
        pages: [{ label: 'Desk', to: '/desk', exact: true, keywords: ['home', 'overview', 'today'] }],
      },
    ],
  },
  {
    key: 'markets',
    label: 'Markets',
    home: '/markets',
    description: 'Read-only market views: the chain, charts, movers, factors and news.',
    icon: IconCandles,
    landing: 'chain',
    tabs: [
      {
        key: 'pulse',
        label: 'Pulse & watchlist',
        home: '/markets',
        requires: 'market-data',
        pages: [
          // Exact: every other Markets tab lives under /markets.
          { label: 'Watchlist', to: '/markets', exact: true, keywords: ['pulse', 'quotes', 'indices'] },
          // MCX is the same feed on another exchange; it becomes the pulse's MCX group.
          { label: 'Commodity', to: '/markets/mcx', only: 'admin', keywords: ['mcx', 'crude', 'gold'] },
        ],
      },
      {
        key: 'chart',
        label: 'Chart',
        home: '/markets/chart',
        requires: 'market-data',
        pages: [{ label: 'Chart', to: '/markets/chart', keywords: ['candles', 'smc', 'smart money', 'structure', 'bos', 'choch'] }],
      },
      {
        key: 'chain',
        label: 'Option chain',
        home: '/markets/chain',
        requires: 'market-data',
        pages: [
          {
            label: 'Option chain',
            to: '/markets/chain',
            keywords: ['strikes', 'greeks', 'open interest', 'oi history', 'levels', 'max pain', 'walls'],
          },
        ],
      },
      {
        key: 'movers',
        label: 'Movers & breadth',
        home: '/markets/movers',
        requires: 'market-data',
        pages: [
          { label: 'Movers', to: '/markets/movers', keywords: ['gainers', 'losers', 'build-up', 'pcr', 'angel', 'futures'] },
        ],
      },
      {
        key: 'factors',
        label: 'Flows & calendar',
        home: '/markets/factors',
        requires: 'market-data',
        pages: [{ label: 'Factors', to: '/markets/factors', keywords: ['fii', 'dii', 'gift', 'global', 'events'] }],
      },
      {
        key: 'news',
        label: 'News & filings',
        home: '/markets/news',
        requires: 'market-data',
        pages: [{ label: 'News', to: '/markets/news', keywords: ['headlines'] }],
      },
      {
        key: 'patterns',
        label: 'Patterns',
        home: '/markets/patterns',
        // Its API is admin-only.
        requires: 'admin',
        pages: [{ label: 'Patterns', to: '/markets/patterns', keywords: ['candle', 'alerts'] }],
      },
    ],
  },
  {
    key: 'trade',
    label: 'Trade',
    home: '/trade',
    description: 'Everything that places or holds a position: runs, the library, history and orders.',
    icon: IconSwitch,
    tabs: [
      {
        key: 'runs',
        label: 'Runs',
        home: '/trade/runs',
        requires: 'strategies',
        // The run cards, with control over every account's runs. A trader
        // starts runs from the library and follows them in History.
        pages: [
          {
            label: 'Runs',
            to: '/trade/runs',
            only: 'admin',
            // As long as History's claim on a run's page, and earlier, so it wins the tie.
            owns: ['/trade/runs/'],
            keywords: ['live runner', 'run cards'],
          },
        ],
      },
      {
        key: 'library',
        label: 'Library',
        home: '/trade/library',
        requires: 'strategies',
        // The admin's catalogue, a trader's page to deploy from; both open the same spec pages.
        pages: [{ label: 'Library', to: '/trade/library', keywords: ['strategies', 'specs', 'how it works', 'deploy', 'launch'] }],
      },
      {
        key: 'history',
        label: 'History',
        home: '/trade/history',
        requires: 'strategies',
        pages: [
          {
            label: 'History',
            to: '/trade/history',
            // A run's own page belongs here for a trader, who has no Runs tab;
            // for an admin the Runs tab claims it first.
            owns: ['/trade/runs/'],
            keywords: ['my runs', 'run history'],
          },
        ],
      },
      {
        key: 'positions',
        label: 'Positions',
        home: '/trade/positions',
        requires: 'strategies',
        // Every open leg across runs and manual books: an admin's every account, a trader's own.
        pages: [{ label: 'Positions', to: '/trade/positions', keywords: ['open legs', 'carry', 'greeks', 'manual book'] }],
      },
      {
        key: 'orders',
        label: 'Orders',
        home: '/trade/orders',
        requires: 'strategies',
        pages: [{ label: 'Orders', to: '/trade/orders', keywords: ['fills', 'order book'] }],
      },
      {
        key: 'ticket',
        label: 'Ticket',
        home: '/trade/ticket',
        requires: 'strategies',
        pages: [{ label: 'Manual order', to: '/trade/ticket', keywords: ['ticket', 'book'] }],
      },
      {
        key: 'risk',
        label: 'Risk',
        home: '/trade/risk',
        requires: 'admin',
        pages: [{ label: 'Risk', to: '/trade/risk', keywords: ['kill switch', 'limits'] }],
      },
    ],
  },
  {
    key: 'research',
    label: 'Research',
    home: '/research',
    description: 'Evidence before money: backtests, forecasts, the filter lab and notebooks.',
    icon: IconFlask,
    tabs: [
      {
        key: 'backtests',
        label: 'Backtests',
        home: '/research/backtests',
        requires: 'backtesting',
        // Admin pages so far: the API lets a trader read runs, but starting one is admin-only.
        pages: [
          { label: 'Backtests', to: '/research/backtests', exact: true, only: 'admin' },
          { label: 'New backtest', to: '/research/backtests/new', only: 'admin' },
          { label: 'Runs', to: '/research/backtests/runs', only: 'admin', keywords: ['backtest results'] },
        ],
      },
      {
        key: 'forecasts',
        label: 'Forecasts',
        home: '/research/forecasts',
        requires: 'analysis',
        pages: [{ label: 'Forecasts', to: '/research/forecasts', only: 'admin', keywords: ['analysis', 'scoreboard'] }],
      },
      {
        key: 'lab',
        label: 'Filter lab',
        home: '/research/lab',
        requires: 'strategies',
        pages: [{ label: 'Filter lab', to: '/research/lab' }],
      },
      {
        key: 'notebook',
        label: 'Notebook',
        home: '/research/notebook',
        requires: 'notebook',
        // A board opens full-window at /notebook/:id, outside the shell.
        pages: [{ label: 'Notebook', to: '/research/notebook', only: 'admin', keywords: ['whiteboards', 'boards'] }],
      },
    ],
  },
  {
    // Its own workspace, not a System tab (owner, 27 Sep): what the desk
    // records and holds is a subject in itself, not plumbing.
    key: 'data',
    label: 'Data',
    home: '/data',
    description: 'What the desk records and holds: coverage, live feeds, history and instruments.',
    icon: IconDatabase,
    tabs: [
      {
        key: 'overview',
        label: 'Overview',
        home: '/data',
        requires: 'admin',
        // Exact: the other Data tabs live under /data.
        pages: [{ label: 'Overview', to: '/data', exact: true, keywords: ['data', 'coverage', 'inventory'] }],
      },
      {
        key: 'feeds',
        label: 'Feeds',
        home: '/data/feeds',
        requires: 'admin',
        pages: [{ label: 'Feeds', to: '/data/feeds', keywords: ['live feeds', 'ingestor', 'watchlist'] }],
      },
      {
        key: 'historical',
        label: 'Historical',
        home: '/data/historical',
        requires: 'admin',
        pages: [{ label: 'Historical', to: '/data/historical', keywords: ['backfill', 'candles'] }],
      },
      {
        key: 'instruments',
        label: 'Instruments',
        home: '/data/instruments',
        requires: 'admin',
        pages: [{ label: 'Instruments', to: '/data/instruments', keywords: ['masters', 'symbols', 'lot size'] }],
      },
    ],
  },
  {
    key: 'system',
    label: 'System',
    home: '/system',
    description: 'Is the platform behaving, and who may use it: health, incidents, the calendar, connectors, people.',
    icon: IconServer,
    tabs: [
      {
        key: 'health',
        label: 'Health',
        home: '/system',
        requires: 'admin',
        pages: [
          {
            label: 'Health',
            to: '/system',
            // Exact: the other System tabs live under /system too.
            exact: true,
            owns: ['/system/checkups'],
            keywords: ['host', 'disk', 'processes', 'checkup', 'sentinel', 'readiness'],
          },
        ],
      },
      {
        key: 'incidents',
        label: 'Incidents',
        home: '/system/incidents',
        requires: 'admin',
        pages: [{ label: 'Incidents', to: '/system/incidents', keywords: ['sentinel'] }],
      },
      {
        key: 'log',
        label: 'Log',
        home: '/system/log',
        requires: 'admin',
        pages: [
          { label: 'Log', to: '/system/log', keywords: ['activity', 'audit', 'alerts', 'deploys', 'deployments', 'risk events'] },
        ],
      },
      {
        key: 'calendar',
        label: 'Calendar',
        home: '/system/calendar',
        requires: 'admin',
        pages: [{ label: 'Calendar', to: '/system/calendar', keywords: ['holidays', 'market calendar'] }],
      },
      {
        key: 'connectors',
        label: 'Connectors',
        home: '/system/connectors',
        requires: 'admin',
        pages: [{ label: 'Connectors', to: '/system/connectors', keywords: ['brokers', 'dhan', 'fyers', 'sign in'] }],
      },
      {
        key: 'people',
        label: 'People',
        home: '/system/people',
        requires: 'admin',
        pages: [{ label: 'People', to: '/system/people', keywords: ['users', 'grants', 'packages', 'invites'] }],
      },
    ],
  },
]

/** A page outside the workspaces, reached from the avatar menu (traders only: an admin has no account page). */
export const ACCOUNT_PAGE = { label: 'Account', to: '/account' } as const

// ---------- who sees what -----------------------------------------------------

export interface Access {
  isAdmin: boolean
  /**
   * The grants the user holds, or null when the API did not say. /me does not
   * carry grants yet; until it does a grant-gated tab stays visible, as it was
   * in the sidebar, and its page shows the API's refusal. Not knowing is not
   * the same as knowing the grant is missing.
   */
  grants: ReadonlySet<string> | null
}

/** The console-side view of a user's rights. Admins hold every module by role. */
export function accessFor(user: Pick<MeResponse, 'role' | 'moduleGrants'> | null): Access {
  const isAdmin = user?.role === 'Admin'
  const grants = user?.moduleGrants
  return { isAdmin, grants: isAdmin ? null : Array.isArray(grants) ? new Set(grants) : null }
}

export function allows(access: Access, requirement: Requirement | undefined): boolean {
  if (!requirement) return true
  if (access.isAdmin) return true
  if (requirement === 'admin') return false
  return access.grants === null || access.grants.has(requirement)
}

/** A page as one user sees it. */
export interface NavPage {
  label: string
  to: string
  exact: boolean
  owns: readonly string[]
  keywords: readonly string[]
  tab: TabDef
  workspace: WorkspaceKey
}

export interface NavWorkspace {
  key: WorkspaceKey
  label: string
  icon: ComponentType<SVGProps<SVGSVGElement>>
  /** Where a click on the workspace goes. */
  to: string
  /** The pages it shows, in order; `tab.key` changes mark the group boundaries. */
  pages: NavPage[]
}

/** Whether a page is part of this user's console: its tab's requirement, and its console when only one has it. */
function shows(access: Access, tab: TabDef, page: PageDef): boolean {
  return allows(access, tab.requires) && (!page.only || page.only === (access.isAdmin ? 'admin' : 'trader'))
}

/**
 * The workspaces and pages this user can reach. A tab with no page left and a
 * workspace with no tab left are dropped, never shown empty.
 */
export function navFor(access: Access): NavWorkspace[] {
  return WORKSPACES.flatMap((ws) => {
    const pages = ws.tabs.flatMap((tab) =>
      tab.pages
        .filter((p) => shows(access, tab, p))
        .map((p) => ({ label: p.label, to: p.to, exact: p.exact ?? false, owns: p.owns ?? [], keywords: p.keywords ?? [], tab, workspace: ws.key })),
    )
    if (pages.length === 0) return []
    const landing = pages.find((p) => p.tab.key === ws.landing) ?? pages[0]
    return [{ key: ws.key, label: ws.label, icon: ws.icon, to: landing.to, pages }]
  })
}

/** Whether `path` is `prefix` or a route under it, on a segment boundary. */
function under(path: string, prefix: string): boolean {
  return path === prefix || path.startsWith(prefix.endsWith('/') ? prefix : `${prefix}/`)
}

/**
 * The workspace and page the current URL belongs to: the longest matching
 * page URL or owned prefix wins, so /research/backtests/runs beats
 * /research/backtests and a run's page lights up the tab it belongs to.
 * Null for a route no visible page claims (the account page, say).
 */
export function locate(pathname: string, nav: readonly NavWorkspace[]): { workspace: NavWorkspace; page: NavPage } | null {
  const path = pathname.length > 1 ? pathname.replace(/\/+$/, '') : pathname
  let best: { workspace: NavWorkspace; page: NavPage; length: number } | null = null
  for (const workspace of nav) {
    for (const page of workspace.pages) {
      const base = page.to.split('?')[0]
      const candidates = page.exact ? page.owns : [base, ...page.owns]
      const length = Math.max(
        page.exact && path === base ? base.length : -1,
        ...candidates.map((prefix) => (under(path, prefix) ? prefix.length : -1)),
      )
      if (length >= 0 && (!best || length > best.length)) best = { workspace, page, length }
    }
  }
  return best ? { workspace: best.workspace, page: best.page } : null
}
