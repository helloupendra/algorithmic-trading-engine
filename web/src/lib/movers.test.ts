import { describe, expect, it } from 'vitest'

import {
  buildUpCounts,
  buildUpTone,
  filterBuildUp,
  formatOi,
  movePercent,
  pcrEnds,
  pcrSummary,
  sectionCount,
  sectionFrom,
  sortPcr,
} from './movers'
import type { MoverRow, PcrRow } from './movers'

const row = (underlying: string, buildUp: string | null, oi = 10): MoverRow => ({
  symbol: `${underlying}29SEP26FUT`,
  underlying,
  token: 1,
  ltp: 100,
  priceChangePercent: 1,
  openInterest: 1_00_00_000,
  oiChangePercent: oi,
  buildUp,
})

describe('buildUpTone', () => {
  it('colours fresh positions by direction and unwinds as neither', () => {
    expect(buildUpTone('Long build-up')).toBe('pos')
    expect(buildUpTone('Short build-up')).toBe('neg')
    expect(buildUpTone('Short covering')).toBe('warn')
    expect(buildUpTone('Long unwinding')).toBe('warn')
    expect(buildUpTone(null)).toBe('neutral')
  })
})

describe('formatting', () => {
  it('signs a move and says nothing when there is no number', () => {
    expect(movePercent(2.345)).toBe('+2.35%')
    expect(movePercent(-2.3)).toBe('-2.30%')
    expect(movePercent(null)).toBe('—')
  })

  it('reads open interest in lakhs and crores', () => {
    expect(formatOi(1_90_29_075)).toBe('1.90 cr')
    expect(formatOi(13_10_600)).toBe('13.11 L')
    expect(formatOi(4_200)).toBe('4,200')
    expect(formatOi(null)).toBe('—')
  })
})

describe('pcrEnds', () => {
  const rows: PcrRow[] = [
    { symbol: 'A29SEP26FUT', underlying: 'A', pcr: 0.52 },
    { symbol: 'B29SEP26FUT', underlying: 'B', pcr: 1.8 },
    { symbol: 'C29SEP26FUT', underlying: 'C', pcr: 0.95 },
    { symbol: 'D29SEP26FUT', underlying: 'D', pcr: 0 },
  ]

  it('takes both ends and drops the ones with no ratio', () => {
    const { high, low } = pcrEnds(rows, 2)
    expect(high.map((r) => r.underlying)).toEqual(['B', 'C'])
    expect(low.map((r) => r.underlying)).toEqual(['A', 'C'])
  })

  it('survives an empty answer', () => {
    expect(pcrEnds(undefined).high).toEqual([])
  })
})

describe('buildUpCounts', () => {
  it('counts each kind, most common first, ignoring unclassified rows', () => {
    const counts = buildUpCounts([
      row('A', 'Long build-up'),
      row('B', 'Short build-up'),
      row('C', 'Long build-up'),
      row('D', null),
    ])
    expect(counts).toEqual([
      { label: 'Long build-up', count: 2 },
      { label: 'Short build-up', count: 1 },
    ])
  })
})

describe('sections', () => {
  const snapshot = {
    configured: true,
    priceGainers: [row('A', null), row('B', null)],
    priceLosers: [row('C', null)],
    buildUp: [row('D', 'Long build-up'), row('E', 'Short build-up')],
    pcr: [{ symbol: 'X', underlying: 'X', pcr: 1 }],
  }

  it('reads the section from the URL and falls back to the first', () => {
    expect(sectionFrom('buildup')).toBe('buildup')
    expect(sectionFrom('nonsense')).toBe('price')
    expect(sectionFrom(null)).toBe('price')
  })

  it('counts each section for its tab', () => {
    expect(sectionCount(snapshot, 'price')).toBe(3)
    expect(sectionCount(snapshot, 'buildup')).toBe(2)
    expect(sectionCount(snapshot, 'pcr')).toBe(1)
    expect(sectionCount(undefined, 'pcr')).toBe(0)
  })

  it('filters the build-up by reading', () => {
    expect(filterBuildUp(snapshot.buildUp, 'all')).toHaveLength(2)
    expect(filterBuildUp(snapshot.buildUp, 'Short build-up').map((r) => r.underlying)).toEqual(['E'])
    expect(filterBuildUp(undefined, 'all')).toEqual([])
  })
})

describe('put-call ratio table', () => {
  const rows: PcrRow[] = [
    { symbol: 'NHPC29SEP26FUT', underlying: 'NHPC', pcr: 1.22 },
    { symbol: 'IREDA29SEP26FUT', underlying: 'IREDA', pcr: 0.18 },
    { symbol: 'ABB29SEP26FUT', underlying: 'ABB', pcr: 0.95 },
    { symbol: 'BAD29SEP26FUT', underlying: 'BAD', pcr: 0 },
  ]

  it('orders both ways, drops rows with no ratio, and searches by underlying', () => {
    expect(sortPcr(rows, 'high').map((r) => r.underlying)).toEqual(['NHPC', 'ABB', 'IREDA'])
    expect(sortPcr(rows, 'low').map((r) => r.underlying)).toEqual(['IREDA', 'ABB', 'NHPC'])
    expect(sortPcr(rows, 'high', 'ir').map((r) => r.underlying)).toEqual(['IREDA'])
  })

  it('summarises the whole list', () => {
    expect(pcrSummary(rows)).toEqual({ count: 3, median: 0.95, putHeavy: 1, callHeavy: 1 })
    expect(pcrSummary([])).toEqual({ count: 0, median: null, putHeavy: 0, callHeavy: 0 })
  })
})
