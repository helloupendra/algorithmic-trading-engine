import { describe, expect, it } from 'vitest'

import {
  NO_FILTERS,
  buildLibrary,
  builtInExit,
  cadenceText,
  dataLabel,
  familyOf,
  familyRoots,
  filtersActive,
  instrumentClass,
  librarySummary,
  matchesFilters,
  oneLiner,
  recentPaperByStrategy,
  sectionOf,
  shortLegs,
  sumRecentPaper,
  timeframeLabel,
  tradeSide,
  usesOptionChain,
} from './strategyLibrary'
import type { LiveRunSummary, StrategyListItem } from './types'

/**
 * The Library page's labels, filters and sections. Legs summaries, facts and
 * descriptions below are copied from the live catalogue and specs, so a
 * rewording there that breaks a chip shows up here first.
 */

const INDICES = ['NIFTY', 'BANKNIFTY', 'FINNIFTY', 'MIDCPNIFTY', 'SENSEX']

let nextId = 1
function strategy(name: string, legsSummary: string, over: Partial<StrategyListItem> = {}): StrategyListItem {
  return {
    id: nextId++,
    name,
    description: '',
    category: 'Neutral',
    supportedUnderlyings: INDICES,
    instrumentKind: 'options',
    legsSummary,
    dataRequirements: [],
    contractRequirements: [],
    defaultParametersJson: '{}',
    defaultLots: 1,
    sourceFile: '',
    createdUtc: '',
    isActive: false,
    startedBy: null,
    startedUtc: null,
    runId: null,
    underlying: null,
    spotSymbol: null,
    lots: null,
    stopLoss: null,
    target: null,
    processId: null,
    lastExit: null,
    activeRuns: [],
    recentExits: [],
    ...over,
  }
}

function run(strategyId: number, netPnl: number, over: Partial<LiveRunSummary> = {}): LiveRunSummary {
  return {
    runId: nextId++,
    userId: 1,
    userName: 'admin',
    strategyId,
    strategyName: 'x',
    category: null,
    underlying: 'NIFTY',
    spotSymbol: 'NSE:NIFTY50-INDEX',
    lots: 1,
    role: null,
    lotSize: 75,
    risk: null,
    status: 'Stopped',
    isActive: false,
    startedUtc: '2026-09-10T04:00:00Z',
    stoppedUtc: '2026-09-10T10:00:00Z',
    stopReason: null,
    stoppedBy: null,
    durationSeconds: 21600,
    netPnl,
    realizedPnl: netPnl,
    unrealizedPnl: 0,
    trades: 1,
    openPositions: 0,
    groups: 1,
    ...over,
  }
}

const activeRun = (underlying: string) => ({
  runId: nextId++,
  underlying,
  spotSymbol: '',
  lots: 1,
  stopLoss: null,
  target: null,
  startedBy: 'admin',
  startedUtc: '2026-09-15T03:50:00Z',
  processId: 1,
})

describe('tradeSide', () => {
  it('reads the verbs of the legs summary', () => {
    expect(tradeSide('Buy ATM CE on an up-break, or Buy ATM PE on a down-break')).toBe('buy')
    expect(tradeSide('Buy one near-ATM CE (bullish) or PE (bearish), chosen by delta')).toBe('buy')
    expect(tradeSide('Sell ATM CE + Sell ATM PE, rolled on every ATM change')).toBe('sell')
    expect(tradeSide('Buy ATM PE + Sell OTM PE')).toBe('mixed')
    expect(tradeSide('Sell 1-2 straddles on adjacent strikes + Buy far OTM CE and PE wings')).toBe('mixed')
  })

  it('is none without a verb, and does not match inside other words', () => {
    expect(tradeSide('')).toBe('none')
    expect(tradeSide(null)).toBe('none')
    expect(tradeSide('Alerts only')).toBe('none')
    expect(tradeSide('Buyback watcher, reseller feed')).toBe('none')
  })
})

describe('shortLegs', () => {
  it('keeps the legs and drops the rule text around them', () => {
    expect(shortLegs('Sell ATM CE + Sell ATM PE, rolled on every ATM change')).toBe('Sell ATM CE+PE')
    expect(shortLegs('Buy ATM CE + Buy ATM PE, held until the move arrives or the time stop')).toBe('Buy ATM CE+PE')
    expect(shortLegs('Buy ATM CE on an up-break, or Buy ATM PE on a down-break')).toBe('Buy ATM CE/PE')
    expect(shortLegs('Buy one near-ATM CE (bullish) or PE (bearish), chosen by delta')).toBe('Buy one near-ATM CE/PE')
  })

  it('shortens straddle groups and their wings', () => {
    expect(shortLegs('Sell 1-2 straddles on adjacent strikes + Buy far OTM CE and PE wings')).toBe('Sell 1-2 straddles + Buy wings')
    expect(shortLegs('Sell 3 straddles (ATM and both neighbours) + Buy far OTM CE and PE wings')).toBe('Sell 3 straddles + Buy wings')
    expect(shortLegs('Buy 1-3 straddles around ATM (2x lots when 3 are active)')).toBe('Buy 1-3 straddles')
  })

  it('merges only matching CE/PE pairs', () => {
    expect(shortLegs('Sell ATM CE + Sell ATM PE + Buy OTM CE + Buy OTM PE')).toBe('Sell ATM CE+PE + Buy OTM CE+PE')
    expect(shortLegs('Buy ATM PE + Sell OTM PE')).toBe('Buy ATM PE + Sell OTM PE')
    expect(shortLegs('Buy ATM CE')).toBe('Buy ATM CE')
  })

  it('is null when there are no legs', () => {
    expect(shortLegs('')).toBeNull()
    expect(shortLegs(undefined)).toBeNull()
    expect(shortLegs('Alerts only')).toBeNull()
  })
})

describe('instrumentClass', () => {
  it('classifies by the supported underlyings', () => {
    expect(instrumentClass({ supportedUnderlyings: INDICES, instrumentKind: 'options' })).toBe('index')
    expect(instrumentClass({ supportedUnderlyings: ['CRUDEOIL', 'CRUDEOILM'], instrumentKind: 'options' })).toBe('mcx')
    expect(instrumentClass({ supportedUnderlyings: ['RELIANCE', 'NIFTY'], instrumentKind: 'options' })).toBe('stocks')
    expect(instrumentClass({ supportedUnderlyings: [], instrumentKind: 'options' })).toBe('any')
  })

  it('treats an equity instrument kind as stocks whatever the underlyings', () => {
    expect(instrumentClass({ supportedUnderlyings: ['NIFTY'], instrumentKind: 'equity' })).toBe('stocks')
  })
})

describe('spec facts', () => {
  const ghost = { evaluates_on: 'tick', resolution: '5m', data: 'index candles, ticks', built_in_exit: 'false' }
  const chainFlow = { evaluates_on: 'bar', resolution: '5m', data: 'index candles, option candles, option chain OI', built_in_exit: 'false' }
  const fulcrum = { evaluates_on: 'tick', resolution: 'tick (replay: run bar)', data: 'ticks, index candles', built_in_exit: 'false' }
  const fulcrumBuy = { evaluates_on: 'tick', resolution: 'any (no bar of its own)', data: 'ticks, index candles, option candles', built_in_exit: 'true' }

  it('finds the option chain in the data line, or a declared chain requirement', () => {
    expect(usesOptionChain(chainFlow)).toBe(true)
    expect(usesOptionChain({ data: 'ticks, index candles, option chain OI' })).toBe(true)
    expect(usesOptionChain(ghost)).toBe(false)
    expect(usesOptionChain(null, [{ symbolType: 'option_chain', resolution: '1m' }])).toBe(true)
    expect(usesOptionChain(null)).toBe(false)
  })

  it('reads built_in_exit, and says nothing when the spec does not', () => {
    expect(builtInExit(fulcrumBuy)).toBe(true)
    expect(builtInExit(ghost)).toBe(false)
    expect(builtInExit({})).toBeNull()
    expect(builtInExit(null)).toBeNull()
  })

  it('labels the timeframe from the resolution', () => {
    expect(timeframeLabel(ghost)).toBe('5m chart')
    expect(timeframeLabel({ evaluates_on: 'tick', resolution: '15m' })).toBe('15m chart')
    expect(timeframeLabel(fulcrum)).toBe('Every tick')
    expect(timeframeLabel(fulcrumBuy)).toBe('Every tick')
  })

  it('falls back to the catalog only when there are no facts', () => {
    expect(timeframeLabel(null, [{ symbolType: 'index', resolution: '5' }])).toBe('5m chart')
    expect(timeframeLabel(null, [])).toBeNull()
    expect(timeframeLabel({ evaluates_on: 'bar', resolution: 'any' }, [{ symbolType: 'index', resolution: '5' }])).toBeNull()
  })

  it('describes the cadence in words', () => {
    expect(cadenceText(chainFlow)).toBe('Once per closed 5m bar')
    expect(cadenceText(ghost)).toBe('On every tick, reading the 5m chart')
    expect(cadenceText(fulcrum)).toBe('On every tick')
    expect(cadenceText(null)).toBeNull()
  })

  it('picks the one data need worth a chip', () => {
    expect(dataLabel(chainFlow)).toBe('Chain OI')
    expect(dataLabel(fulcrumBuy)).toBe('Candles')
    expect(dataLabel({ data: 'ticks' })).toBe('Ticks')
    expect(dataLabel(null, [{ symbolType: 'index', resolution: '5m' }])).toBe('Candles')
    expect(dataLabel(null, [])).toBeNull()
  })
})

describe('oneLiner', () => {
  it('takes the first sentence', () => {
    expect(oneLiner('Intraday momentum on MCX crude. On the 5-minute chart of the near-month future it tracks VWAP.')).toBe(
      'Intraday momentum on MCX crude.',
    )
    // A decimal point is not a sentence end.
    expect(oneLiner('Buys wings about 3.5% away. Then waits.')).toBe('Buys wings about 3.5% away.')
  })

  it('cuts a long sentence at its first clause, else at a word', () => {
    expect(
      oneLiner(
        'Runs a rolling short straddle: sells the ATM call and put, and whenever the ATM strike moves to a different strike it closes the old straddle and opens a fresh one at the new ATM.',
      ),
    ).toBe('Runs a rolling short straddle')
    const long = oneLiner('Keeps one to three short straddles on the strikes around the spot and buys far OTM call/put wings about 3.5% away as protection.', 80)
    expect(long.endsWith('…')).toBe(true)
    expect(long.length).toBeLessThanOrEqual(80)
    expect(long).toBe('Keeps one to three short straddles on the strikes around the spot and buys far…')
  })

  it('is empty for no description', () => {
    expect(oneLiner('')).toBe('')
    expect(oneLiner(null)).toBe('')
  })
})

describe('families', () => {
  const names = ['Fulcrum', 'FulcrumBuy', 'Fulcrum2Straddle20', 'FulcrumMulti50', 'ShortStraddle', 'ShortStrangle', 'IronButterfly']

  it('roots are strategies that start at least two other names', () => {
    expect([...familyRoots(names)]).toEqual(['Fulcrum'])
    // Two strategies sharing a word are not a family when the word is not a strategy.
    expect(familyRoots(['ShortStraddle', 'ShortStrangle']).size).toBe(0)
    // One variant is not a family.
    expect(familyRoots(['Fulcrum', 'FulcrumBuy']).size).toBe(0)
    // A lower-case continuation is a different word, not a variant.
    expect(familyRoots(['Iron', 'Ironclad', 'Ironside']).size).toBe(0)
  })

  it('places a name in the longest matching family', () => {
    const roots = new Set(['Fulcrum', 'FulcrumMulti'])
    expect(familyOf('Fulcrum', roots)).toBe('Fulcrum')
    expect(familyOf('FulcrumMulti50', roots)).toBe('FulcrumMulti')
    expect(familyOf('FulcrumBuy', roots)).toBe('Fulcrum')
    expect(familyOf('ShortStraddle', roots)).toBeNull()
  })
})

describe('sections and grouping', () => {
  const fulcrum = strategy('Fulcrum', 'Sell ATM CE + Sell ATM PE, rolled on every ATM change', { category: 'Adjustment' })
  const fulcrumBuy = strategy('FulcrumBuy', 'Buy ATM CE + Buy ATM PE, held until the move arrives', { category: 'Adjustment' })
  const multiBuy50 = strategy('FulcrumMultiBuy50', 'Buy 1-3 straddles on the strikes around ATM', { category: 'Adjustment' })
  const multi50 = strategy('FulcrumMulti50', 'Sell 1-3 straddles on the strikes around ATM + Buy far OTM CE and PE wings', { category: 'Adjustment' })
  const ghost = strategy('GhostTangentCrossings', 'Buy ATM CE on an up-break, or Buy ATM PE on a down-break', { category: 'Directional' })
  const chainFlow = strategy('ChainFlowBuy', 'Buy one near-ATM CE (bullish) or PE (bearish), chosen by delta', { category: 'Directional' })
  const crude = strategy('CrudeMomentum', 'Buy ATM CE', { category: 'Bullish', supportedUnderlyings: ['CRUDEOIL', 'CRUDEOILM'] })
  const straddle = strategy('ShortStraddle', 'Sell ATM CE + Sell ATM PE')
  const bull = strategy('BullCallSpread', 'Buy ATM CE + Sell OTM CE', { category: 'Bullish' })
  const example = strategy('ExampleStraddle', 'Sell ATM CE + Sell ATM PE', { category: 'Example' })
  const logic = strategy('LogicEngine', '', { category: 'Alerts', supportedUnderlyings: ['BANKNIFTY', 'NIFTY', 'SENSEX'] })
  const all = [fulcrum, fulcrumBuy, multiBuy50, multi50, ghost, chainFlow, crude, straddle, bull, example, logic]
  const roots = familyRoots(all.map((s) => s.name))

  it('puts each strategy in one section', () => {
    expect(sectionOf(ghost)).toBe('buying')
    expect(sectionOf(straddle)).toBe('selling')
    expect(sectionOf(bull)).toBe('hedged')
    expect(sectionOf(multi50)).toBe('hedged')
    // Commodities before side: a crude buyer is found under Commodities.
    expect(sectionOf(crude)).toBe('commodities')
    // Examples and alerters are set apart whatever their legs.
    expect(sectionOf(example)).toBe('other')
    expect(sectionOf(logic)).toBe('other')
  })

  it('collapses a family per section and keeps a lone member as a card', () => {
    const sections = buildLibrary(all, roots)
    expect(sections.map((s) => s.key)).toEqual(['buying', 'selling', 'hedged', 'commodities', 'other'])

    const buying = sections[0]
    expect(buying.count).toBe(4)
    const family = buying.items.find((i) => i.kind === 'family')
    expect(family?.kind === 'family' && family.members.map((m) => m.name)).toEqual(['FulcrumBuy', 'FulcrumMultiBuy50'])

    // Fulcrum itself is the only family member among the sellers: a plain card.
    const selling = sections[1]
    expect(selling.items.every((i) => i.kind === 'single')).toBe(true)
    expect(selling.items.map((i) => (i.kind === 'single' ? i.strategy.name : ''))).toEqual(['Fulcrum', 'ShortStraddle'])
  })

  it('orders running first, then recent use, then name', () => {
    const running = { ...straddle, activeRuns: [activeRun('NIFTY')] }
    const recent = recentPaperByStrategy([run(fulcrum.id, 100), run(fulcrum.id, 200)])
    const sections = buildLibrary([fulcrum, running, ghost, chainFlow], roots, recent)
    const selling = sections.find((s) => s.key === 'selling')!
    expect(selling.items.map((i) => (i.kind === 'single' ? i.strategy.name : ''))).toEqual(['ShortStraddle', 'Fulcrum'])
    const buying = sections.find((s) => s.key === 'buying')!
    expect(buying.items.map((i) => (i.kind === 'single' ? i.strategy.name : ''))).toEqual(['ChainFlowBuy', 'GhostTangentCrossings'])
  })

  it('drops empty sections', () => {
    expect(buildLibrary([ghost], roots).map((s) => s.key)).toEqual(['buying'])
    expect(buildLibrary([], roots)).toEqual([])
  })

  it('summarises the catalogue', () => {
    const running = { ...ghost, activeRuns: [activeRun('NIFTY'), activeRun('SENSEX')] }
    expect(librarySummary([running, straddle, bull, logic])).toEqual({ total: 4, buy: 1, sell: 1, mixed: 1, none: 1, running: 1 })
  })
})

describe('filters', () => {
  const chainFlow = strategy('ChainFlowBuy', 'Buy one near-ATM CE (bullish) or PE (bearish), chosen by delta', {
    category: 'Directional',
    description: 'Intraday option BUYING confirmed by the option chain.',
  })
  const crude = strategy('CrudeMomentum', 'Buy ATM CE', { category: 'Bullish', supportedUnderlyings: ['CRUDEOIL'] })
  const straddle = strategy('ShortStraddle', 'Sell ATM CE + Sell ATM PE', { activeRuns: [activeRun('NIFTY')] })
  const chainFacts = { data: 'index candles, option candles, option chain OI' }

  it('passes everything with no filters', () => {
    expect(filtersActive(NO_FILTERS)).toBe(false)
    expect([chainFlow, crude, straddle].every((s) => matchesFilters(s, null, NO_FILTERS))).toBe(true)
  })

  it('searches name, description, legs and underlyings, every word', () => {
    const f = (q: string) => ({ ...NO_FILTERS, q })
    expect(filtersActive(f('crude'))).toBe(true)
    expect(matchesFilters(crude, null, f('crude'))).toBe(true)
    expect(matchesFilters(crude, null, f('CRUDEOIL call'))).toBe(false)
    expect(matchesFilters(chainFlow, null, f('option chain'))).toBe(true)
    expect(matchesFilters(straddle, null, f('sell atm'))).toBe(true)
  })

  it('filters by side, category, instrument, chain and running', () => {
    expect(matchesFilters(straddle, null, { ...NO_FILTERS, side: 'sell' })).toBe(true)
    expect(matchesFilters(crude, null, { ...NO_FILTERS, side: 'sell' })).toBe(false)
    expect(matchesFilters(chainFlow, null, { ...NO_FILTERS, category: 'directional' })).toBe(true)
    expect(matchesFilters(crude, null, { ...NO_FILTERS, instrument: 'mcx' })).toBe(true)
    expect(matchesFilters(straddle, null, { ...NO_FILTERS, instrument: 'mcx' })).toBe(false)
    expect(matchesFilters(chainFlow, chainFacts, { ...NO_FILTERS, chain: true })).toBe(true)
    expect(matchesFilters(chainFlow, null, { ...NO_FILTERS, chain: true })).toBe(false)
    expect(matchesFilters(straddle, null, { ...NO_FILTERS, running: true })).toBe(true)
    expect(matchesFilters(crude, null, { ...NO_FILTERS, running: true })).toBe(false)
  })
})

describe('recent paper P&L', () => {
  it('adds trading runs, counts alert runs without P&L, and includes open P&L of a live run', () => {
    const rows = recentPaperByStrategy([
      run(1, 1200),
      run(1, -500),
      run(1, 300, { isActive: true, unrealizedPnl: -100 }),
      run(2, 0, { role: 'alerts' }),
      run(2, 0, { role: 'alerts' }),
    ])
    expect(rows.get(1)).toEqual({ runs: 3, alertRuns: 0, netPnl: 900 })
    expect(rows.get(2)).toEqual({ runs: 0, alertRuns: 2, netPnl: 0 })
    expect(rows.get(3)).toBeUndefined()
  })

  it('sums a family and stays undefined when no member ran', () => {
    expect(sumRecentPaper([{ runs: 1, alertRuns: 0, netPnl: 50 }, undefined, { runs: 2, alertRuns: 1, netPnl: -80 }])).toEqual({
      runs: 3,
      alertRuns: 1,
      netPnl: -30,
    })
    expect(sumRecentPaper([undefined, undefined])).toBeUndefined()
  })
})
