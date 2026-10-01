import { describe, expect, it } from 'vitest'

import { accessFor, navFor } from './modules'
import {
  isPaletteShortcut,
  matchScore,
  moveActive,
  pageItems,
  paletteGroups,
  runItems,
  signedRupees,
  strategyItems,
  symbolItems,
} from './palette'
import type { Instrument, LiveRunSummary, StrategyListItem } from './types'

const adminNav = navFor(accessFor({ role: 'Admin' }))
const traderNav = navFor(accessFor({ role: 'Trader' }))
const labels = (items: { label: string }[]) => items.map((i) => i.label)

describe('matchScore', () => {
  it('ranks a label that starts with the query above one that only contains it', () => {
    expect(matchScore('chain', 'Option chain')).toBeGreaterThan(0)
    expect(matchScore('opt', 'Option chain')).toBeGreaterThan(matchScore('chain', 'Option chain'))
    expect(matchScore('ion', 'Option chain')).toBeLessThan(matchScore('chain', 'Option chain'))
  })

  it('needs every word, found in the label or the extra words', () => {
    expect(matchScore('markets news', 'News', ['Markets'])).toBeGreaterThan(0)
    expect(matchScore('markets deploy', 'News', ['Markets'])).toBe(0)
  })

  it('ignores case and matches everything on an empty query', () => {
    expect(matchScore('NIFTY', 'nifty option chain')).toBe(matchScore('nifty', 'NIFTY option chain'))
    expect(matchScore('  ', 'anything')).toBe(1)
  })
})

describe('pageItems', () => {
  it('lists every page the user can open, in registry order, on an empty query', () => {
    const items = pageItems(adminNav, '')
    expect(items.length).toBe(adminNav.flatMap((w) => w.pages).length)
    // Today first: an admin's home.
    expect(items[0]).toMatchObject({ label: 'Today', to: '/today' })
    expect(items[1]).toMatchObject({ label: 'Desk', to: '/desk' })
  })

  it('finds a page by a keyword and says where it lives', () => {
    const [hit] = pageItems(adminNav, 'kill switch')
    expect(hit).toMatchObject({ label: 'Risk', detail: 'Trade', to: '/trade/risk' })
    const [oi] = pageItems(adminNav, 'open interest')
    expect(oi.detail).toBe('Markets')
    expect(pageItems(adminNav, '')[0].detail).toBeUndefined()
  })

  it("never offers a trader an admin page, and adds the trader's account", () => {
    const items = pageItems(traderNav, '', [{ label: 'Account', to: '/account' }])
    expect(items.some((i) => i.to.startsWith('/system') || i.to.startsWith('/data'))).toBe(false)
    expect(items.some((i) => i.to === '/trade/risk' || i.to === '/markets/patterns')).toBe(false)
    expect(labels(pageItems(traderNav, 'connectors'))).toEqual([])
    expect(labels(pageItems(traderNav, 'acc', [{ label: 'Account', to: '/account' }]))).toEqual(['Account'])
  })

  it("offers an index's chain directly", () => {
    expect(pageItems(adminNav, 'bank').slice(0, 2)).toMatchObject([
      { label: 'BANKNIFTY option chain', to: '/markets/chain?u=BANKNIFTY' },
      { label: 'BANKEX option chain', to: '/markets/chain?u=BANKEX' },
    ])
    expect(pageItems(traderNav, 'nifty')[0].to).toBe('/markets/chain?u=NIFTY')
    // One letter matches half the list; the shortcut waits for two.
    expect(pageItems(adminNav, 'n').some((i) => i.label.endsWith('option chain'))).toBe(false)
  })

  it('offers no chain shortcut to a user without the chain page', () => {
    const noMarkets = navFor(accessFor({ role: 'Trader', moduleGrants: ['strategies'] }))
    expect(pageItems(noMarkets, 'nifty')).toEqual([])
  })
})

const instrument = (over: Partial<Instrument>): Instrument => ({
  id: 1,
  symbol: 'NSE:NIFTY50-INDEX',
  exchange: 'NSE',
  segment: 'CM',
  description: 'NIFTY 50',
  instrumentType: 'INDEX',
  isin: null,
  lotSize: null,
  tickSize: null,
  expiryDate: null,
  isEnabled: true,
  underlying: null,
  strikePrice: null,
  optionType: null,
  ...over,
})

describe('symbolItems', () => {
  it('opens a symbol on the Markets chart', () => {
    const [hit] = symbolItems([instrument({})])
    expect(hit).toMatchObject({ label: 'NSE:NIFTY50-INDEX', detail: 'NIFTY 50', meta: 'NSE · INDEX' })
    expect(hit.to).toBe('/markets/chart?symbol=NSE%3ANIFTY50-INDEX')
  })

  it('caps the list and says nothing before the search answers', () => {
    const many = Array.from({ length: 20 }, (_, i) => instrument({ id: i, symbol: `NSE:S${i}-EQ` }))
    expect(symbolItems(many)).toHaveLength(8)
    expect(symbolItems(undefined)).toEqual([])
  })
})

const strategy = (id: number, name: string, activeRuns = 0): StrategyListItem =>
  ({ id, name, category: 'Options buying', supportedUnderlyings: ['NIFTY'], activeRuns: Array.from({ length: activeRuns }) }) as unknown as StrategyListItem

describe('strategyItems', () => {
  const list = [strategy(1, 'Ghost Tangent Crossings', 2), strategy(2, 'Chain Flow Buy'), strategy(3, 'SMC Structure Break')]

  it('matches by name and links to the spec', () => {
    expect(strategyItems(list, 'chain')).toMatchObject([{ label: 'Chain Flow Buy', to: '/trade/library/2' }])
    expect(strategyItems(list, 'ghost')[0]).toMatchObject({ to: '/trade/library/1', meta: '2 live', tone: 'live' })
  })

  it('stays out of the way on an empty query, and on an underlying every strategy trades', () => {
    expect(strategyItems(list, '')).toEqual([])
    expect(strategyItems(list, 'nifty')).toEqual([])
  })
})

const run = (over: Partial<LiveRunSummary>): LiveRunSummary =>
  ({
    runId: 1,
    userName: 'admin',
    strategyName: 'Fulcrum',
    underlying: 'NIFTY',
    status: 'Running',
    isActive: true,
    startedUtc: '2026-09-28T03:50:00Z',
    netPnl: 0,
    ...over,
  }) as LiveRunSummary

describe('runItems', () => {
  const runs = [
    run({ runId: 410, strategyName: 'Fulcrum', underlying: 'SENSEX', status: 'Stopped', isActive: false, netPnl: -5120 }),
    run({ runId: 412, strategyName: 'Fulcrum', underlying: 'NIFTY', netPnl: -8200, startedUtc: '2026-09-28T03:46:00Z' }),
    run({ runId: 415, strategyName: 'Chain Flow Buy', underlying: 'NIFTY', userName: 'coderforchange', netPnl: 1840 }),
  ]

  it('puts running runs first and shows the net', () => {
    const items = runItems(runs, 'admin', 'fulcrum')
    expect(items.map((i) => i.id)).toEqual(['run:412', 'run:410'])
    expect(items[0]).toMatchObject({ meta: '−₹8,200', tone: 'neg', detail: 'admin · running · #412', to: '/trade/runs/412' })
    expect(items[1].detail).toBe('admin · stopped · #410')
  })

  it('finds a run by its number or its owner, and hides the owner from a trader', () => {
    expect(runItems(runs, 'admin', '415')[0].label).toBe('Chain Flow Buy · NIFTY')
    expect(runItems(runs, 'admin', 'coderfor')[0].id).toBe('run:415')
    expect(runItems(runs, 'trader', '415')[0]).toMatchObject({ detail: 'running · #415', to: '/trade/runs/415' })
  })
})

describe('paletteGroups', () => {
  it('keeps a fixed order and drops empty groups', () => {
    const groups = paletteGroups({ symbol: symbolItems([instrument({})]), page: pageItems(adminNav, 'news'), run: [] })
    expect(groups.map((g) => g.label)).toEqual(['Go to', 'Symbols'])
  })
})

describe('moveActive', () => {
  it('wraps at both ends', () => {
    expect(moveActive(4, 1, 5)).toBe(0)
    expect(moveActive(0, -1, 5)).toBe(4)
    expect(moveActive(2, 1, 5)).toBe(3)
  })

  it('enters the list from nothing and stays out of an empty one', () => {
    expect(moveActive(-1, 1, 3)).toBe(0)
    expect(moveActive(-1, -1, 3)).toBe(2)
    expect(moveActive(0, 1, 0)).toBe(-1)
  })
})

describe('isPaletteShortcut', () => {
  const key = (over: Partial<KeyboardEvent>) => ({ key: 'k', metaKey: false, ctrlKey: false, altKey: false, shiftKey: false, ...over })

  it('takes ⌘K and Ctrl-K, whatever the case', () => {
    expect(isPaletteShortcut(key({ metaKey: true }))).toBe(true)
    expect(isPaletteShortcut(key({ ctrlKey: true, key: 'K' }))).toBe(true)
  })

  it('leaves plain k and other chords alone', () => {
    expect(isPaletteShortcut(key({}))).toBe(false)
    expect(isPaletteShortcut(key({ metaKey: true, shiftKey: true }))).toBe(false)
    expect(isPaletteShortcut(key({ ctrlKey: true, key: 'j' }))).toBe(false)
  })
})

describe('signedRupees', () => {
  it('uses the true minus sign and Indian grouping', () => {
    expect([17374.4, -128000, 0].map(signedRupees)).toEqual(['+₹17,374', '−₹1,28,000', '₹0'])
  })
})
