import { describe, expect, it } from 'vitest'

import {
  RANGES_FOR,
  chartLayer,
  coverageByResolution,
  hasResolution,
  isPreOpen,
  lastSessions,
  rangeFromDate,
  rollUp,
  stitch,
  structureTimeframes,
} from './chart'
import type { Candle } from './chart'
import type { CoverageRow } from './queries'

const row = (resolution: string, source: CoverageRow['source'], toUtc = '2026-09-25T09:59:00Z', symbol = 'NSE:NIFTY50-INDEX'): CoverageRow => ({
  symbol,
  resolution,
  fromUtc: '2026-09-01T03:45:00Z',
  toUtc,
  barCount: 100,
  source,
})

const bar = (timeUtc: string, close = 100, volume: number | null = 10): Candle => ({
  timeUtc,
  open: close,
  high: close + 1,
  low: close - 1,
  close,
  volume,
})

describe('chartLayer', () => {
  it('draws SMC only when the URL asks for it', () => {
    expect(chartLayer('smc')).toBe('smc')
    expect(chartLayer(null)).toBe('price')
    expect(chartLayer('candles')).toBe('price')
  })

  it('still opens SMC from a link saved before the layer was renamed', () => {
    expect(chartLayer('structure')).toBe('smc')
  })
})

describe('coverage', () => {
  it("reads the live bars' 1m and the stored 1 as the same resolution, for the symbol asked", () => {
    const by = coverageByResolution([row('1', 'backfill'), row('1m', 'live'), row('D', 'backfill'), row('5', 'backfill', undefined, 'NSE:SBIN-EQ')], 'NSE:NIFTY50-INDEX')
    expect(by['1']).toHaveLength(2)
    expect(by.D).toHaveLength(1)
    expect(by['5']).toBeUndefined()
  })

  it('offers every intraday resolution to a symbol recording live, and the day only with daily candles', () => {
    const live = coverageByResolution([row('1m', 'live')], 'NSE:NIFTY50-INDEX')
    expect(hasResolution(live, '15')).toBe(true)
    expect(hasResolution(live, 'D')).toBe(false)
    const stored = coverageByResolution([row('5', 'backfill')], 'NSE:NIFTY50-INDEX')
    expect(hasResolution(stored, '5')).toBe(true)
    expect(hasResolution(stored, '1')).toBe(false)
  })

  it('never offers a range that is noise at the resolution', () => {
    expect(RANGES_FOR['1']).not.toContain('1Y')
    expect(RANGES_FOR.D).not.toContain('1D')
  })
})

describe('rangeFromDate', () => {
  const by = coverageByResolution([row('5', 'backfill', '2026-09-25T09:59:00Z'), row('1m', 'live', '2026-09-26T04:00:00Z')], 'NSE:NIFTY50-INDEX')

  it('counts back from the last bar the symbol has, not from the clock', () => {
    // 1D asks for 8 calendar days, ending on the newest bar (the live one, on the 26th).
    expect(rangeFromDate(by, '5', '1D', '2026-09-28T12:00:00Z')).toBe('2026-09-19')
  })

  it('falls back to the clock with nothing stored, and asks for everything on All', () => {
    expect(rangeFromDate({}, '5', '1D', '2026-09-28T12:00:00Z')).toBe('2026-09-21')
    expect(rangeFromDate(by, 'D', 'ALL', '2026-09-28T12:00:00Z')).toBeUndefined()
  })
})

describe('candles', () => {
  it('drops the pre-open auction on NSE and BSE only', () => {
    expect(isPreOpen('NSE:NIFTY50-INDEX', '2026-09-25T03:30:00Z')).toBe(true)
    expect(isPreOpen('NSE:NIFTY50-INDEX', '2026-09-25T03:45:00Z')).toBe(false)
    expect(isPreOpen('MCX:CRUDEOIL26OCTFUT', '2026-09-25T03:30:00Z')).toBe(false)
  })

  it('takes the last full session for 1D, skipping a day of two stray bars', () => {
    const full = Array.from({ length: 12 }, (_, i) => bar(`2026-09-25T04:${String(i).padStart(2, '0')}:00Z`))
    const stray = [bar('2026-09-28T03:46:00Z'), bar('2026-09-28T03:47:00Z')]
    expect(lastSessions([...full, ...stray], 1)).toHaveLength(12)
    expect(lastSessions([...full, ...stray], 2)).toHaveLength(14)
  })

  it('rolls live minutes into buckets the way the server does', () => {
    const minutes = [bar('2026-09-25T04:00:00Z', 100), bar('2026-09-25T04:01:00Z', 104), bar('2026-09-25T04:05:00Z', 102)]
    const five = rollUp(minutes, 5)
    expect(five).toHaveLength(2)
    expect(five[0]).toMatchObject({ timeUtc: '2026-09-25T04:00:00.000Z', open: 100, close: 104, high: 105, low: 99, volume: 20 })
  })

  it('lays live bars after the last stored candle only', () => {
    const stored = [bar('2026-09-25T04:00:00Z'), bar('2026-09-25T04:01:00Z')]
    const live = [bar('2026-09-25T04:01:00Z', 999), bar('2026-09-25T04:02:00Z', 101)]
    const out = stitch(stored, live, { symbol: 'NSE:NIFTY50-INDEX', resolution: '1', range: '5D' })
    expect(out.map((c) => c.close)).toEqual([100, 100, 101])
  })
})

describe('structureTimeframes', () => {
  it('carries the timeframes above the chart, highest first', () => {
    expect(structureTimeframes('5')).toBe('1D,15m,5m')
    expect(structureTimeframes('15')).toBe('1D,15m')
    expect(structureTimeframes('D')).toBe('1D')
  })
})
