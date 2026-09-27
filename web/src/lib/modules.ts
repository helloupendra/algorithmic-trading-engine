/**
 * Workspace registry: the single source of truth for what this console is made
 * of and who may see each part of it.
 *
 * The console is five workspaces (Desk, Markets, Trade, Research, System),
 * each holding tabs. A tab declares what it requires: the Admin role, or one of
 * the module grants the API enforces (PlatformModules on the server). The top
 * bar, the tab strip, the phone's bottom bar and the ⌘K palette all read from
 * here, so a part the user may not use is absent everywhere at once rather
 * than greyed out in one place and linked in another.
 *
 * Hiding is a courtesy, never the control: every endpoint checks the grant
 * itself (RequireModule), so a trader who types a URL still gets a 403.
 *
 * URLs have not moved yet. Each tab keeps the pages it will gather, each with
 * today's URL per console, and the `home` it moves to once the URLs change;
 * lib/routeMap.ts holds the redirect table for that step. Until the merges
 * land, a tab that gathers several of today's pages shows each of them.
 */

import type { ComponentType, SVGProps } from 'react'
import type { MeResponse } from './api'
import { IconCandles, IconDashboard, IconFlask, IconServer, IconSwitch } from '../components/icons'

/** The module keys the server grants to traders (PlatformModules.cs). */
export const GRANT_KEYS = ['strategies', 'backtesting', 'market-data', 'notebook', 'analysis'] as const
export type GrantKey = (typeof GRANT_KEYS)[number]

/** What a tab needs beyond being signed in: the Admin role, or one grant. */
export type Requirement = 'admin' | GrantKey

export type WorkspaceKey = 'desk' | 'markets' | 'trade' | 'research' | 'system'

/** One of today's pages. */
export interface PageDef {
  label: string
  /** Today's URL in each console. A console without one does not show the page. */
  admin?: string
  trader?: string
  /** Only this exact path, not the routes under it: the two home pages sit above everything. */
  exact?: boolean
  /** More of today's routes that belong to this page (its detail pages), as path prefixes. */
  owns?: string[]
  /** Extra words ⌘K matches on. */
  keywords?: string[]
}

export interface TabDef {
  key: string
  /** The tab's name once its pages are merged. */
  label: string
  /** Where the tab lives once URLs move; the redirect table points here. */
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
        // Until /desk exists each console keeps its own home page.
        pages: [{ label: 'Desk', admin: '/admin', trader: '/trader', exact: true, keywords: ['home', 'overview'] }],
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
          { label: 'Watchlist', trader: '/trader/watchlist', keywords: ['pulse', 'quotes'] },
          // MCX is the same feed on another exchange; it becomes the pulse's MCX group.
          { label: 'Commodity', admin: '/admin/data/commodity', keywords: ['mcx', 'crude', 'gold'] },
        ],
      },
      {
        key: 'chart',
        label: 'Chart',
        home: '/markets/chart',
        requires: 'market-data',
        pages: [
          { label: 'Charts', trader: '/trader/charts', keywords: ['candles'] },
          { label: 'Structure', admin: '/admin/data/structure', trader: '/trader/structure', keywords: ['smc', 'bos', 'choch'] },
        ],
      },
      {
        key: 'chain',
        label: 'Option chain',
        home: '/markets/chain',
        requires: 'market-data',
        pages: [
          { label: 'Option chain', admin: '/admin/data/chain', trader: '/trader/option-chain', keywords: ['strikes', 'greeks'] },
          { label: 'Open interest', admin: '/admin/data/open-interest', keywords: ['oi'] },
        ],
      },
      {
        key: 'movers',
        label: 'Movers & breadth',
        home: '/markets/movers',
        requires: 'market-data',
        pages: [
          // Angel One's market-wide screens: price, OI build-up, PCR.
          { label: 'Movers', admin: '/admin/data/movers', trader: '/trader/market-movers', keywords: ['build-up', 'pcr', 'angel'] },
          // Gainers and losers inside an index. Two pages both called "movers"
          // sat side by side in the trader nav; this one says what it ranks.
          { label: 'Index movers', trader: '/trader/movers', keywords: ['gainers', 'losers'] },
        ],
      },
      {
        key: 'factors',
        label: 'Flows & calendar',
        home: '/markets/factors',
        requires: 'market-data',
        pages: [{ label: 'Factors', admin: '/admin/data/factors', keywords: ['fii', 'dii', 'gift', 'global', 'events'] }],
      },
      {
        key: 'news',
        label: 'News & filings',
        home: '/markets/news',
        requires: 'market-data',
        pages: [{ label: 'News', admin: '/admin/data/news', trader: '/trader/news', keywords: ['headlines'] }],
      },
      {
        key: 'patterns',
        label: 'Patterns',
        home: '/markets/patterns',
        // Its API is admin-only.
        requires: 'admin',
        pages: [{ label: 'Patterns', admin: '/admin/data/patterns', keywords: ['candle', 'alerts'] }],
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
        pages: [
          { label: 'Overview', admin: '/admin/strategies' },
          { label: 'Live runner', admin: '/admin/strategies/live', owns: ['/admin/strategies/runs'], keywords: ['run cards'] },
        ],
      },
      {
        key: 'library',
        label: 'Library',
        home: '/trade/library',
        requires: 'strategies',
        pages: [
          { label: 'Library', admin: '/admin/strategies/library', keywords: ['specs', 'how it works'] },
          // A trader's strategies page is where they deploy from; its "How it
          // works" pages live under /trader/strategies/:id.
          { label: 'Strategies', trader: '/trader/deploy', owns: ['/trader/strategies'], keywords: ['deploy', 'launch'] },
        ],
      },
      {
        key: 'history',
        label: 'History',
        home: '/trade/history',
        requires: 'strategies',
        pages: [
          {
            label: 'History',
            admin: '/admin/strategies/history',
            trader: '/trader/strategies/history',
            owns: ['/trader/strategies/runs'],
            keywords: ['my runs', 'run history'],
          },
        ],
      },
      {
        key: 'positions',
        label: 'Positions',
        home: '/trade/positions',
        requires: 'strategies',
        // The v1 Simulator pages, until positions across every book exist.
        pages: [{ label: 'Positions', trader: '/trader/positions', owns: ['/trader/runs'] }],
      },
      {
        key: 'orders',
        label: 'Orders',
        home: '/trade/orders',
        requires: 'strategies',
        pages: [{ label: 'Orders', trader: '/trader/orders' }],
      },
      {
        key: 'ticket',
        label: 'Ticket',
        home: '/trade/ticket',
        requires: 'strategies',
        pages: [{ label: 'Manual order', admin: '/admin/trading', trader: '/trader/trading', exact: true, keywords: ['ticket', 'book'] }],
      },
      {
        key: 'risk',
        label: 'Risk',
        home: '/trade/risk',
        requires: 'admin',
        pages: [{ label: 'Risk', admin: '/admin/system/risk', keywords: ['kill switch', 'limits'] }],
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
        pages: [
          { label: 'Backtests', admin: '/admin/backtesting', exact: true },
          { label: 'New backtest', admin: '/admin/backtesting/new' },
          { label: 'Runs', admin: '/admin/backtesting/runs', keywords: ['backtest results'] },
        ],
      },
      {
        key: 'forecasts',
        label: 'Forecasts',
        home: '/research/forecasts',
        requires: 'analysis',
        pages: [{ label: 'Forecasts', admin: '/admin/analysis', keywords: ['analysis', 'scoreboard'] }],
      },
      {
        key: 'lab',
        label: 'Filter lab',
        home: '/research/lab',
        requires: 'strategies',
        pages: [{ label: 'Filter lab', admin: '/admin/trading/lab', trader: '/trader/trading/lab' }],
      },
      {
        key: 'notebook',
        label: 'Notebook',
        home: '/research/notebook',
        requires: 'notebook',
        pages: [{ label: 'Notebook', admin: '/admin/notebook', keywords: ['whiteboards', 'boards'] }],
      },
    ],
  },
  {
    key: 'system',
    label: 'System',
    home: '/system',
    description: 'Is the platform behaving, and who may use it: health, incidents, data plumbing, people.',
    icon: IconServer,
    tabs: [
      {
        key: 'health',
        label: 'Health',
        home: '/system',
        requires: 'admin',
        pages: [
          { label: 'Health', admin: '/admin/system', exact: true, keywords: ['host', 'disk', 'processes'] },
          { label: 'Checkup', admin: '/admin/checkup', keywords: ['sentinel', 'readiness'] },
        ],
      },
      {
        key: 'incidents',
        label: 'Incidents',
        home: '/system/incidents',
        requires: 'admin',
        pages: [{ label: 'Incidents', admin: '/admin/incidents', keywords: ['sentinel'] }],
      },
      {
        key: 'log',
        label: 'Log',
        home: '/system/log',
        requires: 'admin',
        pages: [
          { label: 'Activity', admin: '/admin/system/logs', keywords: ['activity log', 'audit'] },
          { label: 'Alerts', admin: '/admin/system/alerts', keywords: ['alerter'] },
          { label: 'Deploys', admin: '/admin/system/deployments', keywords: ['deployments'] },
        ],
      },
      {
        key: 'data',
        label: 'Data',
        home: '/system/data',
        requires: 'admin',
        pages: [
          { label: 'Data', admin: '/admin/data', exact: true, keywords: ['coverage'] },
          { label: 'Feeds', admin: '/admin/data/live', keywords: ['live feeds', 'ingestor', 'watchlist'] },
          { label: 'Historical', admin: '/admin/data/historical', keywords: ['backfill', 'candles'] },
          { label: 'Instruments', admin: '/admin/data/instruments', keywords: ['masters', 'f&o'] },
          { label: 'Calendar', admin: '/admin/system/calendar', keywords: ['holidays', 'market calendar'] },
        ],
      },
      {
        key: 'connectors',
        label: 'Connectors',
        home: '/system/connectors',
        requires: 'admin',
        pages: [{ label: 'Connectors', admin: '/admin/broker', keywords: ['brokers', 'dhan', 'fyers', 'sign in'] }],
      },
      {
        key: 'people',
        label: 'People',
        home: '/system/people',
        requires: 'admin',
        pages: [{ label: 'Users', admin: '/admin/users', keywords: ['people', 'grants', 'packages', 'invites'] }],
      },
    ],
  },
]

/** Pages outside the workspaces, reached from the avatar menu. */
export const ACCOUNT_PAGE = { label: 'Account', trader: '/trader/account' } as const

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

/** A page as one user sees it: its URL in their console. */
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

/**
 * The workspaces and pages this user can reach today. A page needs a URL in
 * the user's console and the tab's requirement; a tab with no page left and a
 * workspace with no tab left are dropped, never shown empty.
 */
export function navFor(access: Access): NavWorkspace[] {
  const side = access.isAdmin ? 'admin' : 'trader'
  return WORKSPACES.flatMap((ws) => {
    const pages = ws.tabs.flatMap((tab) =>
      allows(access, tab.requires)
        ? tab.pages.flatMap((p) => {
            const to = p[side]
            return to
              ? [{ label: p.label, to, exact: p.exact ?? false, owns: p.owns ?? [], keywords: p.keywords ?? [], tab, workspace: ws.key }]
              : []
          })
        : [],
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
 * page URL or owned prefix wins, so /admin/strategies/live beats
 * /admin/strategies and a run's page lights up the tab it was opened from.
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
