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

  it('has unique tab keys and homes, and one URL per page per console', () => {
    const tabs = WORKSPACES.flatMap((w) => w.tabs)
    expect(new Set(tabs.map((t) => t.key)).size).toBe(tabs.length)
    expect(new Set(tabs.map((t) => t.home)).size).toBe(tabs.length)
    for (const side of ['admin', 'trader'] as const) {
      const urls = tabs.flatMap((t) => t.pages.map((p) => p[side])).filter(Boolean)
      expect(new Set(urls).size).toBe(urls.length)
    }
  })

  it('points the landing of each workspace at one of its own tabs', () => {
    for (const ws of WORKSPACES) {
      if (ws.landing) expect(ws.tabs.map((t) => t.key)).toContain(ws.landing)
    }
  })

  it('keeps every System tab admin-only', () => {
    const system = WORKSPACES.find((w) => w.key === 'system')!
    expect(system.tabs.every((t) => t.requires === 'admin')).toBe(true)
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
    expect(nav.find((w) => w.key === 'markets')!.to).toBe('/admin/data/chain')
    expect(nav[0].to).toBe('/desk')
  })

  it('never shows a trader System, Data, connectors, feeds or Sentinel pages', () => {
    const nav = navFor(traderWith(...GRANT_KEYS))
    expect(labels(nav)).not.toContain('System')
    expect(labels(nav)).not.toContain('Data')
    const urls = allUrls(nav)
    expect(urls.filter((u) => u.startsWith('/admin'))).toEqual([])
    expect(urls).toContain('/desk')
    for (const hidden of ['Connectors', 'Feeds', 'Checkup', 'Incidents', 'Patterns', 'Risk']) {
      expect(nav.flatMap((w) => w.pages.map((p) => p.label))).not.toContain(hidden)
    }
  })

  it("shows a trader today's trader pages when the grants are not known", () => {
    const nav = navFor(traderUnknown)
    expect(labels(nav)).toEqual(['Desk', 'Markets', 'Trade', 'Research'])
    expect(pagesOf(nav, 'markets')).toEqual(['Watchlist', 'Charts', 'Structure', 'Option chain', 'Movers', 'Index movers', 'News'])
    expect(pagesOf(nav, 'trade')).toEqual(['Strategies', 'History', 'Positions', 'Orders', 'Manual order'])
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

  it('shows no tab the admin console lacks a page for (and the reverse)', () => {
    // The Desk is the one URL both consoles share.
    const own = (nav: NavWorkspace[]) => allUrls(nav).filter((u) => u !== '/desk')
    expect(own(navFor(admin)).every((u) => u.startsWith('/admin'))).toBe(true)
    expect(own(navFor(traderUnknown)).every((u) => u.startsWith('/trader'))).toBe(true)
    expect(navFor(admin)[0].to).toBe(navFor(traderUnknown)[0].to)
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
    expect(at(adminNav, '/admin/data/chain')).toBe('Markets / Option chain')
    expect(at(adminNav, '/admin/incidents')).toBe('System / Incidents')
    expect(at(traderNav, '/trader/option-chain')).toBe('Markets / Option chain')
  })

  it('prefers the longest match', () => {
    expect(at(adminNav, '/admin/strategies')).toBe('Trade / Overview')
    expect(at(adminNav, '/admin/strategies/live')).toBe('Trade / Live runner')
    expect(at(adminNav, '/admin/trading/lab')).toBe('Research / Filter lab')
    expect(at(adminNav, '/admin/trading')).toBe('Trade / Manual order')
  })

  it('lights up the tab a detail page belongs to', () => {
    expect(at(adminNav, '/admin/strategies/runs/412')).toBe('Trade / Live runner')
    expect(at(adminNav, '/admin/strategies/library/9')).toBe('Trade / Library')
    expect(at(adminNav, '/admin/backtesting/runs/31')).toBe('Research / Runs')
    expect(at(adminNav, '/admin/broker/dhan')).toBe('System / Connectors')
    expect(at(adminNav, '/admin/users/packages')).toBe('System / Users')
    expect(at(traderNav, '/trader/strategies/7/how-it-works')).toBe('Trade / Strategies')
    expect(at(traderNav, '/trader/strategies/runs/88')).toBe('Trade / History')
    expect(at(traderNav, '/trader/runs/3')).toBe('Trade / Positions')
  })

  it('holds the Desk to its exact path', () => {
    expect(at(adminNav, '/desk')).toBe('Desk / Desk')
    expect(at(traderNav, '/desk/')).toBe('Desk / Desk')
    expect(at(adminNav, '/desk/nowhere')).toBeNull()
    // The old home pages only redirect now; nothing claims them.
    expect(at(adminNav, '/admin')).toBeNull()
    expect(at(traderNav, '/trader/account')).toBeNull()
  })

  it('respects segment boundaries', () => {
    expect(at(adminNav, '/admin/data/live')).toBe('Data / Feeds')
    expect(at(adminNav, '/admin/data/livestream')).toBeNull()
  })
})
