import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import {
  deltaSummary,
  formatDelta,
  formatIv,
  formatThetaPerDay,
  greeksNote,
  ltpAgeNote,
  thetaTone,
  totalsNote,
} from './greeks'
import type { PositionGreeks, RunGreeksTotals } from './types'

/**
 * The words and colours of an open leg's greeks (27 Sep: "the Greeks' effect
 * should show too — like theta shows when you buy"). The colour is the money's,
 * not the greek's: a bought option's theta is a cost and must read red, a
 * written one's income and green — getting that backwards would tell the owner
 * the opposite of what his book is doing overnight.
 */

const leg = (over: Partial<PositionGreeks> = {}): PositionGreeks => ({
  source: 'feed',
  asOfUtc: '2026-09-28T05:00:00Z',
  stale: false,
  ivPercent: 13.62,
  delta: 0.52,
  gamma: 0.0008,
  theta: -12.5,
  vega: 8.2,
  underlyingPrice: null,
  deltaQuantity: 39,
  deltaRupeesPerPoint: 39,
  thetaRupeesPerDay: -937.5,
  vegaRupeesPerIvPoint: 615,
  ...over,
})

const totals = (over: Partial<RunGreeksTotals> = {}): RunGreeksTotals => ({
  thetaRupeesPerDay: -450,
  vegaRupeesPerIvPoint: 1230,
  netDeltaQuantity: 39,
  byUnderlying: [{ underlying: 'NIFTY', deltaQuantity: 39, deltaRupeesPerPoint: 39 }],
  legs: 1,
  unpriced: 0,
  stale: false,
  oldestAsOfUtc: '2026-09-28T05:00:00Z',
  ...over,
})

describe('theta', () => {
  it('reads a bought option as a cost per day, in red', () => {
    expect(formatThetaPerDay(-450)).toBe('−₹450/day')
    expect(thetaTone(-450)).toBe('neg')
  })

  it('reads a written option as income per day, in green', () => {
    expect(formatThetaPerDay(450)).toBe('+₹450/day')
    expect(thetaTone(450)).toBe('pos')
  })

  it('has no colour when it rounds to nothing', () => {
    expect(thetaTone(0.4)).toBe('')
    expect(thetaTone(null)).toBe('')
    expect(formatThetaPerDay(null)).toBe('—')
  })
})

describe('per-leg figures', () => {
  it('signs delta with a true minus and shows IV in percent', () => {
    expect(formatDelta(0.524)).toBe('+0.52')
    expect(formatDelta(-0.45)).toBe('−0.45')
    expect(formatDelta(0)).toBe('0.00')
    expect(formatIv(13.62)).toBe('13.6%')
    expect(formatIv(null)).toBe('—')
  })

  it('names the source of fresh figures', () => {
    expect(greeksNote(leg())).toBe('IV 13.6% · live quote')
    expect(greeksNote(leg({ source: 'computed' }))).toBe('IV 13.6% · computed')
    expect(greeksNote(leg({ source: 'chain' }))).toBe('IV 13.6% · option chain')
  })

  it('never passes stale figures off as now', () => {
    // 15:29 IST on the Friday before: Monday morning, before the first tick.
    expect(greeksNote(leg({ stale: true, asOfUtc: '2026-09-25T09:59:00Z' }))).toBe('IV 13.6% · as of 15:29')
  })

  it('calls a future what it is', () => {
    expect(greeksNote(leg({ source: 'delta-one', ivPercent: null, asOfUtc: null }))).toBe('future / share')
  })
})

describe('totals', () => {
  it('gives delta per underlying and never adds across them', () => {
    const mixed = totals({
      netDeltaQuantity: null,
      byUnderlying: [
        { underlying: 'CRUDEOIL', deltaQuantity: -100, deltaRupeesPerPoint: -100 },
        { underlying: 'NIFTY', deltaQuantity: 37.5, deltaRupeesPerPoint: 37.5 },
      ],
    })
    expect(deltaSummary(mixed)).toBe('Δ −100 CRUDEOIL · Δ +37.5 NIFTY')
  })

  it('says what is left out and how old the sums are', () => {
    const note = totalsNote(totals({ unpriced: 2, stale: true, oldestAsOfUtc: '2026-09-25T09:59:00Z' }))
    expect(note).toBe('vega +₹1,230 per 1% IV · Δ +39 NIFTY · 2 legs not priced · as of 15:29')
  })
})

describe('LTP age', () => {
  beforeEach(() => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-09-28T04:00:00Z')) // Monday 09:30 IST
  })
  afterEach(() => vi.useRealTimers())

  it('says a carried leg is still on Friday’s price', () => {
    expect(ltpAgeNote('2026-09-25T09:59:00Z')).toBe('as of 15:29 · 2d ago')
  })

  it('says nothing about a price that is a few seconds old', () => {
    expect(ltpAgeNote('2026-09-28T03:59:50Z')).toBeNull()
    expect(ltpAgeNote(null)).toBeNull()
  })
})
