import { describe, expect, it } from 'vitest'

import appSource from '../App.tsx?raw'
import { HOMES_OUTSIDE_WORKSPACES, ROUTE_MOVES, ROUTES_THAT_STAY, movedUrl } from './routeMap'
import { WORKSPACES } from './modules'

/**
 * The redirect table is only useful if it is complete: a route it forgets is a
 * bookmark, a Telegram link or a checkup link that dies when the URLs move.
 * These read the routes straight out of App.tsx, so adding a route without
 * placing it fails here, not in production.
 */

const appRoutes = [...appSource.matchAll(/<Route\b[^>]*?\bpath="([^"]+)"/g)].map((m) => m[1])

const tabHomes = WORKSPACES.flatMap((ws) => [ws.home, ...ws.tabs.map((t) => t.home)])

/** `target` is `home`, a route under it, or `home` with a query. */
function landsIn(target: string, home: string): boolean {
  const path = target.split('?')[0]
  return path === home || path.startsWith(`${home}/`)
}

describe('route map', () => {
  it('reads the routes out of App.tsx', () => {
    // A regex that stopped matching would make every other check pass vacuously.
    expect(appRoutes.length).toBeGreaterThan(60)
    expect(appRoutes).toContain('/admin/strategies/runs/:runId')
    expect(new Set(appRoutes).size).toBe(appRoutes.length)
  })

  it('places every route App.tsx serves', () => {
    const placed = new Set([...Object.keys(ROUTE_MOVES), ...ROUTES_THAT_STAY])
    expect(appRoutes.filter((r) => !placed.has(r))).toEqual([])
  })

  it('has no entry for a route that no longer exists', () => {
    const routes = new Set(appRoutes)
    expect([...Object.keys(ROUTE_MOVES), ...ROUTES_THAT_STAY].filter((r) => !routes.has(r))).toEqual([])
  })

  it('moves every route into a workspace, or to a home named as outside them', () => {
    const stray = Object.entries(ROUTE_MOVES).filter(
      ([, to]) => !tabHomes.some((home) => landsIn(to, home)) && !HOMES_OUTSIDE_WORKSPACES.includes(to.split('?')[0]),
    )
    expect(stray).toEqual([])
  })

  it('only fills a :param the old route has', () => {
    const missing = Object.entries(ROUTE_MOVES).flatMap(([from, to]) =>
      [...to.matchAll(/:(\w+)/g)].map((m) => m[1]).filter((name) => !from.includes(`:${name}`)).map((name) => `${from} → ${to} (:${name})`),
    )
    expect(missing).toEqual([])
  })

  it('sends each tab page to the home of its own tab', () => {
    const wrong = WORKSPACES.flatMap((ws) =>
      ws.tabs.flatMap((tab) =>
        tab.pages.flatMap((page) =>
          [page.admin, page.trader]
            .filter((url): url is string => !!url)
            .filter((url) => !landsIn(ROUTE_MOVES[url] ?? '', tab.home))
            .map((url) => `${url} → ${ROUTE_MOVES[url]} (tab ${tab.key}, home ${tab.home})`),
        ),
      ),
    )
    expect(wrong).toEqual([])
  })

  it('never points a tab at a route App.tsx does not serve', () => {
    const routes = new Set(appRoutes)
    const dead = WORKSPACES.flatMap((ws) => ws.tabs.flatMap((t) => t.pages.flatMap((p) => [p.admin, p.trader])))
      .filter((url): url is string => !!url)
      .filter((url) => !routes.has(url))
    expect(dead).toEqual([])
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
    expect(movedUrl('/admin/data/commodity')).toBe('/markets?group=mcx')
  })

  it('tolerates a trailing slash and refuses what it does not know', () => {
    expect(movedUrl('/admin/')).toBe('/desk')
    expect(movedUrl('/admin/nowhere')).toBeNull()
    expect(movedUrl('/login')).toBeNull()
  })
})
