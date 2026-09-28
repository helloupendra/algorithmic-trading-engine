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
  withLiveTick,
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

describe('withLiveTick', () => {
  const NIFTY = 'NSE:NIFTY50-INDEX'
  // The forming 5-minute candle opened 10:10 IST (04:40Z).
  const candles = [bar('2026-09-28T04:35:00.000Z', 100), bar('2026-09-28T04:40:00.000Z', 102)]
  const at = (iso: string, lastTradedPrice: number) => ({ lastTradedPrice, exchangeTimestampUtc: iso, receivedAtMs: Date.parse(iso) + 80 })

  it('moves the forming candle: its close, and its high and low when the price passes them', () => {
    const up = withLiveTick(candles, at('2026-09-28T04:42:10Z', 104.5), { symbol: NIFTY, resolution: '5' })
    expect(up).toHaveLength(2)
    expect(up[0]).toBe(candles[0])
    expect(up[1]).toMatchObject({ open: 102, close: 104.5, high: 104.5, low: 101 })
    const down = withLiveTick(up, at('2026-09-28T04:43:00Z', 100.5), { symbol: NIFTY, resolution: '5' })
    // The high reached in between stays on the candle.
    expect(down[1]).toMatchObject({ close: 100.5, high: 104.5, low: 100.5 })
  })

  it('opens the next candle when the price is past the forming one, on the same session', () => {
    const next = withLiveTick(candles, at('2026-09-28T04:45:02Z', 103), { symbol: NIFTY, resolution: '5' })
    expect(next).toHaveLength(3)
    expect(next[2]).toEqual({ timeUtc: '2026-09-28T04:45:00.000Z', open: 103, high: 103, low: 103, close: 103, volume: null })
  })

  it('leaves the candles alone for a day chart, an older price, a new session, a pre-open print or no price', () => {
    const same = (tick: ReturnType<typeof at> | undefined, resolution: '1' | '5' | 'D' = '5', symbol = NIFTY) =>
      expect(withLiveTick(candles, tick, { symbol, resolution })).toBe(candles)
    same(at('2026-09-28T04:42:10Z', 104), 'D')
    same(at('2026-09-28T04:39:59Z', 104))
    same(at('2026-09-29T03:46:00Z', 104))
    same({ ...at('2026-09-28T04:42:10Z', 0) })
    same(undefined)
    const opening = [bar('2026-09-28T03:45:00.000Z', 100)]
    expect(withLiveTick(opening, at('2026-09-28T03:40:00Z', 90), { symbol: NIFTY, resolution: '1' })).toBe(opening)
  })

  it('reads the arrival time when the exchange sent no stamp', () => {
    const next = withLiveTick(candles, { lastTradedPrice: 101.5, exchangeTimestampUtc: null, receivedAtMs: Date.parse('2026-09-28T04:41:00Z') }, { symbol: NIFTY, resolution: '5' })
    expect(next[1]).toMatchObject({ close: 101.5 })
  })
})
