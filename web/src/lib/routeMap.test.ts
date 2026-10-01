import { describe, expect, it } from 'vitest'

import appSource from '../App.tsx?raw'
import { HOMES_OUTSIDE_WORKSPACES, ROUTES_THAT_STAY, ROUTE_MOVES, matchRoute, movedUrl } from './routeMap'
import { GRANT_KEYS, WORKSPACES, accessFor, locate, navFor } from './modules'

/**
 * The console's URLs are only as good as three promises: the router serves
 * exactly the registry's pages, a page the registry hides from traders is
 * behind the router's Admin guard, and no URL the console ever gave out goes
 * dead. These read the routes straight out of App.tsx and the links straight
 * out of the source, so breaking a promise fails here, not in production.
 */

const appRoutes = [...appSource.matchAll(/<Route\b[^>]*?\bpath="([^"]+)"/g)].map((m) => m[1])

/** The routes inside the router's Admin guards. */
const adminRoutes = new Set(
  [...appSource.matchAll(/<Route element=\{<RequireRole role="Admin" \/>\}>([\s\S]*?)<\/Route>/g)].flatMap((block) =>
    [...block[1].matchAll(/\bpath="([^"]+)"/g)].map((m) => m[1]),
  ),
)

const adminNav = navFor(accessFor({ role: 'Admin' }))
/** A trader holding every grant: what the registry would ever show a trader. */
const traderNav = navFor(accessFor({ role: 'Trader', moduleGrants: [...GRANT_KEYS] }))

const pageUrls = WORKSPACES.flatMap((ws) => ws.tabs.flatMap((t) => t.pages.map((p) => p.to)))

/** A concrete URL for a route pattern: every :param filled with a plausible id. */
const sample = (route: string) => route.replace(/:\w+/g, '7')

const served = (url: string) => appRoutes.some((route) => route !== '*' && matchRoute(route, url.split(/[?#]/)[0]) !== null)

/** A workspace's own address where no page lives (/trade, /research): it only opens the first tab. */
const workspaceAddresses = WORKSPACES.map((w) => w.home).filter((home) => !pageUrls.includes(home))

/** The routes that are pages in a workspace. */
const consoleRoutes = appRoutes.filter(
  (r) => !ROUTES_THAT_STAY.includes(r) && !HOMES_OUTSIDE_WORKSPACES.includes(r) && !workspaceAddresses.includes(r),
)

/**
 * Every route App.tsx served before the workspaces (27 Sep 2026). A redirect
 * for each must outlive the page it once served: Telegram messages, checkup
 * items and bookmarks name them.
 */
const PRE_WORKSPACE_ROUTES = [
  '/admin', '/trader', '/admin/notebook/:id',
  '/trader/watchlist', '/trader/charts', '/trader/structure', '/trader/news', '/trader/movers', '/trader/market-movers',
  '/trader/option-chain', '/trader/positions', '/trader/orders', '/trader/trading', '/trader/trading/lab', '/trader/strategies',
  '/trader/deploy', '/trader/account', '/trader/strategies/:id/how-it-works', '/trader/runs/:id', '/trader/strategies/history',
  '/trader/strategies/runs/:runId',
  '/admin/data', '/admin/data/live', '/admin/data/commodity', '/admin/data/chain', '/admin/data/open-interest',
  '/admin/data/patterns', '/admin/data/movers', '/admin/data/factors', '/admin/data/news', '/admin/data/historical',
  '/admin/data/structure', '/admin/data/instruments',
  '/admin/system', '/admin/users', '/admin/users/packages', '/admin/system/risk', '/admin/risk', '/admin/trading',
  '/admin/trading/lab', '/admin/strategies', '/admin/strategies/live', '/admin/strategies/history',
  '/admin/strategies/runs/:runId', '/admin/strategies/library', '/admin/strategies/library/:id',
  '/admin/backtesting', '/admin/backtesting/new', '/admin/backtesting/runs', '/admin/backtesting/runs/:id',
  '/admin/analysis', '/admin/notebook', '/admin/system/alerts', '/admin/system/patterns', '/admin/system/calendar',
  '/admin/system/logs', '/admin/system/deployments', '/admin/incidents', '/admin/checkup', '/admin/live-alerts',
  '/admin/broker', '/admin/broker/:providerKey', '/admin/ingestion', '/admin/instruments',
]

describe('the router and the registry', () => {
  it('reads the routes out of App.tsx', () => {
    // A regex that stopped matching would make every other check pass vacuously.
    expect(appRoutes.length).toBeGreaterThan(40)
    expect(appRoutes).toContain('/trade/runs/:runId')
    expect(adminRoutes.size).toBeGreaterThan(20)
    expect(new Set(appRoutes).size).toBe(appRoutes.length)
  })

  it("serves every page the registry lists, and every workspace's own address", () => {
    expect(pageUrls.filter((url) => !appRoutes.includes(url))).toEqual([])
    expect(workspaceAddresses).toEqual(['/trade', '/research'])
    expect(workspaceAddresses.filter((url) => !appRoutes.includes(url))).toEqual([])
  })

  it('serves nothing in the console that no registry page claims', () => {
    const stray = consoleRoutes.filter((r) => !locate(sample(r), adminNav) && !locate(sample(r), traderNav))
    expect(stray).toEqual([])
  })

  it('guards exactly the routes the registry hides from traders', () => {
    const wrong = consoleRoutes.filter((r) => adminRoutes.has(r) === (locate(sample(r), traderNav) !== null))
    expect(wrong).toEqual([])
    // The two homes outside the workspaces: a board is an admin page, the account a trader's.
    expect(adminRoutes.has('/notebook/:id')).toBe(true)
    expect(adminRoutes.has('/account')).toBe(false)
  })

  it('serves no old URL as a page of its own', () => {
    expect(appRoutes.filter((r) => r.startsWith('/admin') || r.startsWith('/trader'))).toEqual([])
  })
})

describe('the redirect table', () => {
  it('mounts a redirect for every old route', () => {
    expect(appSource).toContain('Object.keys(ROUTE_MOVES).map')
    expect(PRE_WORKSPACE_ROUTES.filter((r) => !(r in ROUTE_MOVES))).toEqual([])
  })

  it('sends every old route to a route App.tsx serves', () => {
    const dead = Object.entries(ROUTE_MOVES).filter(([, to]) => !served(sample(to)))
    expect(dead).toEqual([])
  })

  it('lands every old tab page in a workspace tab, or on a home named as outside them', () => {
    const stray = Object.entries(ROUTE_MOVES).filter(([, to]) => {
      const path = sample(to.split('?')[0])
      return !HOMES_OUTSIDE_WORKSPACES.includes(to.split('?')[0]) && !locate(path, adminNav) && !locate(path, traderNav)
    })
    expect(stray).toEqual([])
  })

  it('only fills a :param the old route has', () => {
    const missing = Object.entries(ROUTE_MOVES).flatMap(([from, to]) =>
      [...to.matchAll(/:(\w+)/g)].map((m) => m[1]).filter((name) => !from.includes(`:${name}`)).map((name) => `${from} → ${to} (:${name})`),
    )
    expect(missing).toEqual([])
  })
})

describe('links in the source', () => {
  // Every .ts and .tsx file but the tests and this table, as text.
  const files = import.meta.glob(['../**/*.{ts,tsx}', '!../**/*.test.ts', '!../lib/routeMap.ts'], {
    query: '?raw',
    import: 'default',
    eager: true,
  }) as Record<string, string>

  /**
   * Console paths written in a string or a template, with each ${…} read as a
   * value. A placeholder is an example of what to type (a folder on the server), not a link.
   */
  const links = Object.entries(files).flatMap(([file, text]) =>
    [...text.matchAll(/(?<!placeholder=)(['"`])(\/(?:today|desk|markets|trade|research|ai|data|system|account|notebook|admin|trader)\b[^'"`\s]*)\1/g)].map(
      (m) => ({ file, url: m[2].replace(/\$\{[^}]*\}/g, '7') }),
    ),
  )

  it('finds the links', () => {
    expect(links.length).toBeGreaterThan(60)
  })

  it('points every link at a route App.tsx serves, never at an old URL', () => {
    const bad = links.filter(({ url }) => !served(url) || movedUrl(url.split(/[?#]/)[0]) !== null)
    expect(bad).toEqual([])
  })
})

describe('movedUrl', () => {
  it('fills params by name and keeps the query string', () => {
    expect(movedUrl('/admin/strategies/runs/412', '?tab=orders')).toBe('/trade/runs/412?tab=orders')
    expect(movedUrl('/trader/strategies/7/how-it-works')).toBe('/trade/library/7')
    expect(movedUrl('/admin/checkup', '?id=46')).toBe('/system/checkups?id=46')
  })

  it("merges the target's own query over the old one", () => {
    expect(movedUrl('/admin/system/alerts', '?source=x&limit=50')).toBe('/system/log?source=alerts&limit=50')
    expect(movedUrl('/admin/data/structure', '?symbol=NSE%3ANIFTY50-INDEX')).toBe('/markets/chart?symbol=NSE%3ANIFTY50-INDEX&layer=smc')
  })

  it("keeps a broker's sign-in result on its way to the connector", () => {
    expect(movedUrl('/admin/broker/dhan', '?connected=0&reason=Token%20expired')).toBe(
      '/system/connectors/dhan?connected=0&reason=Token+expired',
    )
  })

  it('tolerates a trailing slash and refuses what it does not know', () => {
    expect(movedUrl('/admin/')).toBe('/desk')
    expect(movedUrl('/admin/nowhere')).toBeNull()
    expect(movedUrl('/login')).toBeNull()
    expect(movedUrl('/desk')).toBeNull()
    expect(movedUrl('/system/log')).toBeNull()
  })

  it('opens both old home pages on the Desk', () => {
    expect(movedUrl('/admin')).toBe('/desk')
    expect(movedUrl('/trader')).toBe('/desk')
  })
})
