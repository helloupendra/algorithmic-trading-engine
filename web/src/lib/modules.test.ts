import { describe, expect, it } from 'vitest'

import { GRANT_KEYS, WORKSPACES, accessFor, allows, locate, navFor } from './modules'
import type { Access, NavWorkspace } from './modules'

const admin: Access = accessFor({ role: 'Admin' })
/** A trader whose grants the API has not reported (today's /me). */
const traderUnknown: Access = accessFor({ role: 'Trader' })
const traderWith = (...grants: string[]): Access => accessFor({ role: 'Trader', moduleGrants: grants })

const labels = (nav: NavWorkspace[]) => nav.map((w) => w.label)
const pagesOf = (nav: NavWorkspace[], key: string) => nav.find((w) => w.key === key)?.pages.map((p) => p.label) ?? []
const allUrls = (nav: NavWorkspace[]) => nav.flatMap((w) => w.pages.map((p) => p.to))

describe('the registry', () => {
  it('is the six workspaces, in order', () => {
    expect(WORKSPACES.map((w) => w.label)).toEqual(['Desk', 'Markets', 'Trade', 'Research', 'Data', 'System'])
  })

  it('uses only the grant keys the server knows', () => {
    const used = WORKSPACES.flatMap((w) => w.tabs.map((t) => t.requires)).filter((r) => r && r !== 'admin')
    expect(used.every((r) => (GRANT_KEYS as readonly string[]).includes(r!))).toBe(true)
  })

  it('has unique tab keys, homes and page URLs', () => {
    const tabs = WORKSPACES.flatMap((w) => w.tabs)
    expect(new Set(tabs.map((t) => t.key)).size).toBe(tabs.length)
    expect(new Set(tabs.map((t) => t.home)).size).toBe(tabs.length)
    const urls = tabs.flatMap((t) => t.pages.map((p) => p.to))
    expect(new Set(urls).size).toBe(urls.length)
  })

  it('keeps every page at its tab home or under it, and every tab under its workspace', () => {
    for (const ws of WORKSPACES) {
      for (const tab of ws.tabs) {
        expect(tab.home === ws.home || tab.home.startsWith(`${ws.home}/`)).toBe(true)
        for (const page of tab.pages) expect(page.to === tab.home || page.to.startsWith(`${tab.home}/`)).toBe(true)
      }
    }
  })

  it('points the landing of each workspace at one of its own tabs', () => {
    for (const ws of WORKSPACES) {
      if (ws.landing) expect(ws.tabs.map((t) => t.key)).toContain(ws.landing)
    }
  })

  it('keeps every System and Data tab admin-only', () => {
    for (const key of ['system', 'data']) {
      const ws = WORKSPACES.find((w) => w.key === key)!
      expect(ws.tabs.every((t) => t.requires === 'admin')).toBe(true)
    }
  })
})

describe('accessFor and allows', () => {
  it('gives an admin every requirement', () => {
    expect(allows(admin, 'admin')).toBe(true)
    for (const g of GRANT_KEYS) expect(allows(admin, g)).toBe(true)
  })

  it('never gives a trader an admin-only part, whatever the grants', () => {
    expect(allows(traderUnknown, 'admin')).toBe(false)
    expect(allows(traderWith(...GRANT_KEYS), 'admin')).toBe(false)
  })

  it('checks a grant only when the grants are known', () => {
    expect(allows(traderWith('market-data'), 'market-data')).toBe(true)
    expect(allows(traderWith('market-data'), 'strategies')).toBe(false)
    expect(allows(traderWith(), 'strategies')).toBe(false)
    // Unknown is not "missing": the tab stays, and the API answers for itself.
    expect(allows(traderUnknown, 'strategies')).toBe(true)
  })

  it('treats a signed-out user as a trader with nothing known', () => {
    expect(accessFor(null)).toEqual({ isAdmin: false, grants: null })
  })

  it('ignores grants sent for an admin', () => {
    expect(accessFor({ role: 'Admin', moduleGrants: [] }).grants).toBeNull()
  })
})

describe('navFor', () => {
  it('gives an admin all six workspaces, opening Markets on the chain', () => {
    const nav = navFor(admin)
    expect(labels(nav)).toEqual(['Desk', 'Markets', 'Trade', 'Research', 'Data', 'System'])
    expect(nav.find((w) => w.key === 'markets')!.to).toBe('/markets/chain')
    expect(nav[0].to).toBe('/desk')
  })

  it('gives an admin one page per tab, the backtests aside', () => {
    const nav = navFor(admin)
    expect(pagesOf(nav, 'markets')).toEqual(['Watchlist', 'Commodity', 'Chart', 'Option chain', 'Movers', 'Factors', 'News', 'Patterns'])
    expect(pagesOf(nav, 'trade')).toEqual(['Runs', 'Library', 'History', 'Manual order', 'Risk'])
    expect(pagesOf(nav, 'research')).toEqual(['Backtests', 'New backtest', 'Runs', 'Forecasts', 'Filter lab', 'Notebook'])
    expect(pagesOf(nav, 'system')).toEqual(['Health', 'Incidents', 'Log', 'Calendar', 'Connectors', 'People'])
  })

  it('never shows a trader System, Data, connectors, feeds or Sentinel pages', () => {
    const nav = navFor(traderWith(...GRANT_KEYS))
    expect(labels(nav)).not.toContain('System')
    expect(labels(nav)).not.toContain('Data')
    const urls = allUrls(nav)
    expect(urls.filter((u) => u.startsWith('/system') || u.startsWith('/data'))).toEqual([])
    expect(urls).toContain('/desk')
    for (const hidden of ['Connectors', 'Feeds', 'Health', 'Incidents', 'Patterns', 'Risk', 'Commodity', 'Runs']) {
      expect(nav.flatMap((w) => w.pages.map((p) => p.label))).not.toContain(hidden)
    }
  })

  it("shows a trader their pages when the grants are not known", () => {
    const nav = navFor(traderUnknown)
    expect(labels(nav)).toEqual(['Desk', 'Markets', 'Trade', 'Research'])
    expect(pagesOf(nav, 'markets')).toEqual(['Watchlist', 'Chart', 'Option chain', 'Movers', 'Factors', 'News'])
    expect(pagesOf(nav, 'trade')).toEqual(['Library', 'History', 'Positions', 'Orders', 'Manual order'])
    expect(pagesOf(nav, 'research')).toEqual(['Filter lab'])
  })

  it('drops a workspace whose every tab needs a grant the trader lacks', () => {
    const nav = navFor(traderWith('strategies'))
    expect(labels(nav)).toEqual(['Desk', 'Trade', 'Research'])
    expect(labels(navFor(traderWith('market-data')))).toEqual(['Desk', 'Markets'])
  })

  it('leaves a trader with no grants the Desk alone', () => {
    const nav = navFor(traderWith())
    expect(labels(nav)).toEqual(['Desk'])
    expect(nav[0].pages.map((p) => p.to)).toEqual(['/desk'])
  })

  it('gives both consoles the same URL for a page both have', () => {
    const adminUrls = new Set(allUrls(navFor(admin)))
    const shared = allUrls(navFor(traderUnknown)).filter((u) => adminUrls.has(u))
    expect(shared).toEqual(['/desk', '/markets', '/markets/chart', '/markets/chain', '/markets/movers', '/markets/factors', '/markets/news',
      '/trade/library', '/trade/history', '/trade/ticket', '/research/lab'])
  })
})

describe('locate', () => {
  const adminNav = navFor(admin)
  const traderNav = navFor(traderUnknown)
  const at = (nav: NavWorkspace[], path: string) => {
    const hit = locate(path, nav)
    return hit ? `${hit.workspace.label} / ${hit.page.label}` : null
  }

  it('finds the page for its own URL', () => {
    expect(at(adminNav, '/markets/chain')).toBe('Markets / Option chain')
    expect(at(adminNav, '/system/incidents')).toBe('System / Incidents')
    expect(at(traderNav, '/markets/chain')).toBe('Markets / Option chain')
  })

  it('prefers the longest match, and holds an exact page to its own path', () => {
    expect(at(adminNav, '/markets')).toBe('Markets / Watchlist')
    expect(at(adminNav, '/markets/mcx')).toBe('Markets / Commodity')
    expect(at(adminNav, '/research/backtests')).toBe('Research / Backtests')
    expect(at(adminNav, '/research/backtests/new')).toBe('Research / New backtest')
    expect(at(adminNav, '/data')).toBe('Data / Overview')
    expect(at(adminNav, '/data/feeds')).toBe('Data / Feeds')
  })

  it('lights up the tab a detail page belongs to', () => {
    expect(at(adminNav, '/markets/chain/oi')).toBe('Markets / Option chain')
    expect(at(adminNav, '/trade/library/9')).toBe('Trade / Library')
    expect(at(adminNav, '/research/backtests/runs/31')).toBe('Research / Runs')
    expect(at(adminNav, '/system/connectors/dhan')).toBe('System / Connectors')
    expect(at(adminNav, '/system/people/packages')).toBe('System / People')
    expect(at(adminNav, '/system/checkups')).toBe('System / Health')
    expect(at(traderNav, '/trade/positions/runs/3')).toBe('Trade / Positions')
  })

  it("files a run's page under Runs for an admin and under History for a trader", () => {
    expect(at(adminNav, '/trade/runs/412')).toBe('Trade / Runs')
    expect(at(traderNav, '/trade/runs/412')).toBe('Trade / History')
    // The run cards themselves are not a trader's page.
    expect(at(traderNav, '/trade/runs')).toBeNull()
  })

  it('holds the Desk to its exact path', () => {
    expect(at(adminNav, '/desk')).toBe('Desk / Desk')
    expect(at(traderNav, '/desk/')).toBe('Desk / Desk')
    expect(at(adminNav, '/desk/nowhere')).toBeNull()
    // The old URLs only redirect now; nothing claims them.
    expect(at(adminNav, '/admin')).toBeNull()
    expect(at(traderNav, '/account')).toBeNull()
  })

  it('respects segment boundaries', () => {
    expect(at(adminNav, '/data/feeds')).toBe('Data / Feeds')
    expect(at(adminNav, '/data/feedsx')).toBeNull()
  })
})
