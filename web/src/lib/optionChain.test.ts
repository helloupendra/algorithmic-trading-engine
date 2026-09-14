import { describe, expect, it } from 'vitest'

import {
  compactIndian,
  compactSigned,
  describeFreshness,
  expiryLabel,
  isCallItm,
  isPutItm,
  signedPercent,
  spotMarkerIndex,
  strikeWindow,
} from './optionChain'
import type { OptionChainHeader } from './types'

/**
 * The advanced option chain's reading rules. The owner's complaint about the
 * old page was that you could not tell where the price was; these are the
 * rules that put the spot line, the ITM shading and the freshness line in the
 * right place.
 */

describe('compactIndian', () => {
  it('reads the way Indian chains are written', () => {
    expect(compactIndian(194_000)).toBe('1.94L')
    expect(compactIndian(44_000)).toBe('44.0K')
    expect(compactIndian(1_949_000)).toBe('19.49L')
    expect(compactIndian(12_345_678)).toBe('1.23Cr')
    expect(compactIndian(950)).toBe('950')
    expect(compactIndian(0)).toBe('0')
  })

  it('keeps the sign and never invents a number for nothing', () => {
    expect(compactIndian(-7_000)).toBe('−7.0K')
    expect(compactSigned(3_000)).toBe('+3.0K')
    expect(compactSigned(-23_000)).toBe('−23.0K')
    expect(compactSigned(0)).toBe('0')
    expect(compactIndian(null)).toBe('—')
    expect(compactSigned(undefined)).toBe('—')
  })

  it('signs percentages with a true minus and no sign on zero', () => {
    expect(signedPercent(0.11)).toBe('+0.11%')
    expect(signedPercent(-5.82)).toBe('−5.82%')
    expect(signedPercent(0.001)).toBe('0.00%')
  })
})

describe('in the money', () => {
  it('shades calls below the spot and puts above it', () => {
    const spot = 57_369.65
    expect(isCallItm(57_300, spot)).toBe(true)
    expect(isCallItm(57_400, spot)).toBe(false)
    expect(isPutItm(57_400, spot)).toBe(true)
    expect(isPutItm(57_300, spot)).toBe(false)
  })

  it('shades nothing when the spot is unknown', () => {
    expect(isCallItm(100, null)).toBe(false)
    expect(isPutItm(100, 0)).toBe(false)
  })
})

describe('spotMarkerIndex', () => {
  const strikes = [57_200, 57_300, 57_400, 57_500]

  it('puts the line between the two strikes the spot lies between', () => {
    // Rows 0 and 1 (57,200 and 57,300) are above the line; the line goes before row 2.
    expect(spotMarkerIndex(strikes, 57_369.65)).toBe(2)
  })

  it('puts a spot sitting on a strike just below that strike', () => {
    expect(spotMarkerIndex(strikes, 57_400)).toBe(3)
  })

  it('pins the line to an edge when the spot is outside the shown strikes', () => {
    expect(spotMarkerIndex(strikes, 57_000)).toBe(0)
    expect(spotMarkerIndex(strikes, 58_000)).toBe(4)
  })

  it('draws no line without a spot or strikes', () => {
    expect(spotMarkerIndex(strikes, null)).toBeNull()
    expect(spotMarkerIndex([], 57_369)).toBeNull()
  })
})

describe('strikeWindow', () => {
  const strikes = Array.from({ length: 101 }, (_, i) => 50_000 + i * 100) // 50,000 … 60,000

  it('shows N strikes either side of the ATM', () => {
    const { start, end } = strikeWindow(strikes, 57_400, 10)
    expect(end - start).toBe(21)
    expect(strikes[start]).toBe(56_400)
    expect(strikes[end - 1]).toBe(58_400)
  })

  it('slides inward at the ends so the row count holds', () => {
    const low = strikeWindow(strikes, 50_100, 10)
    expect(low).toEqual({ start: 0, end: 21 })
    const high = strikeWindow(strikes, 60_000, 20)
    expect(high).toEqual({ start: 60, end: 101 })
  })

  it('centres on the nearest strike when the ATM is not in the list', () => {
    const { start } = strikeWindow(strikes, 57_449, 10)
    expect(strikes[start + 10]).toBe(57_400)
  })

  it('shows everything for All or for a short chain', () => {
    expect(strikeWindow(strikes, 57_400, 'all')).toEqual({ start: 0, end: 101 })
    expect(strikeWindow([1, 2, 3], 2, 10)).toEqual({ start: 0, end: 3 })
  })
})

describe('expiryLabel', () => {
  it('names the day and how far away it is', () => {
    expect(expiryLabel('2026-09-29', '2026-09-14')).toBe('29 Sep 2026 (+15 days)')
    expect(expiryLabel('2026-09-15', '2026-09-14')).toBe('15 Sep 2026 (+1 day)')
    expect(expiryLabel('2026-09-14', '2026-09-14')).toBe('14 Sep 2026 (today)')
    expect(expiryLabel('2026-09-08', '2026-09-14')).toBe('8 Sep 2026 (expired)')
  })
})

describe('describeFreshness', () => {
  const base: OptionChainHeader = {
    mode: 'live',
    serverUtc: '2026-09-15T05:11:00Z',
    exchange: 'NSE',
    marketOpen: true,
    spot: null,
    spotIsFuture: false,
    future: null,
    vix: null,
    atTheMoneyStrike: null,
    maxPainStrike: null,
    putCallRatio: null,
    putCallRatioOfChange: null,
    supportStrike: null,
    supportOpenInterest: null,
    resistanceStrike: null,
    resistanceOpenInterest: null,
    totalCallOpenInterest: 0,
    totalPutOpenInterest: 0,
    totalCallOpenInterestChange: 0,
    totalPutOpenInterestChange: 0,
    atTheMoneyIv: null,
    daysToExpiry: null,
    lotSize: null,
    lotSizeSource: null,
    spotBetweenLower: null,
    spotBetweenUpper: null,
    snapshotCapturedUtc: '2026-09-15T05:10:30Z',
    snapshotSourceKey: 'dhan',
    liveOverlayUtc: '2026-09-15T05:10:58Z',
    liveSourceKey: 'dhan',
    liveLegs: 22,
    totalLegs: 400,
    freshSeconds: 120,
  }
  const at = (iso: string) => Date.parse(iso)

  it('says live, how long ago and from where', () => {
    const f = describeFreshness(base, at('2026-09-15T05:11:00Z'))
    expect(f.tone).toBe('live')
    expect(f.text).toMatch(/^live · updated 2 s ago · Dhan/)
  })

  it('never calls a live answer live once its newest quote has aged out', () => {
    const f = describeFreshness(base, at('2026-09-15T05:14:00Z'))
    expect(f.tone).toBe('stale')
    expect(f.text).not.toMatch(/^live/)
  })

  it('shows a snapshot with its time and age when no quote is fresh', () => {
    const f = describeFreshness({ ...base, mode: 'snapshot', liveOverlayUtc: null, liveSourceKey: null }, at('2026-09-15T05:11:30Z'))
    expect(f.tone).toBe('snapshot')
    expect(f.text).toBe('snapshot 10:40 IST (1 min old) · Dhan · no live quotes')
  })

  it('warns when the snapshot is several minutes old during the session', () => {
    const f = describeFreshness({ ...base, mode: 'snapshot', liveOverlayUtc: null }, at('2026-09-15T05:25:00Z'))
    expect(f.tone).toBe('stale')
    expect(f.text).toMatch(/recorder may be behind/)
  })

  it('says the market is closed and when the last capture was', () => {
    const f = describeFreshness({ ...base, marketOpen: false, snapshotCapturedUtc: '2026-09-14T12:54:18Z' }, at('2026-09-14T16:00:00Z'))
    expect(f.tone).toBe('closed')
    expect(f.text).toBe('market closed — last capture 14 Sep 18:24 IST · Dhan')
  })

  it('labels a replay as a replay', () => {
    const f = describeFreshness({ ...base, mode: 'replay', marketOpen: false }, at('2026-09-15T08:00:00Z'))
    expect(f.tone).toBe('replay')
    expect(f.text).toMatch(/^replay · snapshot 15 Sep 10:40 IST/)
  })
})
