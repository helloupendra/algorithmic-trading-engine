import { describe, expect, it } from 'vitest'

import { bsPrice } from './payoff'
import {
  BUILDER_RATE,
  addLeg,
  atmStrike,
  daysToExpiry,
  expiryCloseMs,
  legIv,
  marketPrice,
  presetLegs,
  timeLeftLabel,
  toPayoffLeg,
} from './strategyBuilder'
import type { BuilderLeg } from './strategyBuilder'
import type { OptionChainLeg } from './types'

/**
 * What the builder reads off the chain before any arithmetic: the price a
 * leg is entered at, the IV it is priced with, the strikes a preset lands on
 * and how long the expiry has left. Each is a place a plausible-looking
 * number could be the wrong one.
 */

const quote = (over: Partial<OptionChainLeg> = {}): OptionChainLeg => ({
  symbol: 'NSE:NIFTY26N0324300CE',
  lastTradedPrice: 121.5,
  priceChange: null,
  priceChangePercent: null,
  bidPrice: 120.9,
  askPrice: 121.6,
  volume: null,
  openInterest: null,
  openInterestChange: null,
  openInterestChangePercent: null,
  impliedVolatility: 13.6,
  delta: null,
  buildUp: 'Neutral',
  ...over,
})

describe('the price a leg is entered at', () => {
  it('takes the middle of a two-sided quote', () => {
    expect(marketPrice(quote())).toEqual({ price: 121.25, basis: 'mid' })
  })

  it('falls back to the last trade when a side is missing or the quote is crossed', () => {
    expect(marketPrice(quote({ bidPrice: null }))).toEqual({ price: 121.5, basis: 'ltp' })
    expect(marketPrice(quote({ bidPrice: 0 }))).toEqual({ price: 121.5, basis: 'ltp' })
    expect(marketPrice(quote({ bidPrice: 122, askPrice: 121 }))).toEqual({ price: 121.5, basis: 'ltp' })
  })

  it('has none when the chain has no price at all', () => {
    expect(marketPrice(quote({ bidPrice: null, askPrice: null, lastTradedPrice: null }))).toBeNull()
    expect(marketPrice(null)).toBeNull()
  })
})

describe('the IV a leg is priced with', () => {
  const years = 5 / 365

  it("takes the chain's IV, which comes in percent", () => {
    expect(legIv(quote(), marketPrice(quote()), 'CE', 24_300, 24_350, years, 0)).toEqual({ iv: 0.136, source: 'chain' })
  })

  it('solves one from the market price when the chain has none', () => {
    const price = bsPrice('CE', 24_350, 24_300, years, 0.15, BUILDER_RATE)!
    const iv = legIv(quote({ impliedVolatility: null }), { price, basis: 'mid' }, 'CE', 24_300, 24_350, years, 0)
    expect(iv?.source).toBe('price')
    expect(iv?.iv).toBeCloseTo(0.15, 6)
  })

  it('has none when neither the chain nor the price can give one', () => {
    expect(legIv(quote({ impliedVolatility: 0 }), null, 'CE', 24_300, 24_350, years, 0)).toBeNull()
    // Under its intrinsic value: no volatility explains the price.
    expect(legIv(quote({ impliedVolatility: null }), { price: 10, basis: 'ltp' }, 'CE', 24_300, 24_350, years, 0)).toBeNull()
  })

  it('counts units as lots × lot size', () => {
    const leg: BuilderLeg = { id: 'a', kind: 'PE', side: 'sell', strike: 24_300, lots: 3, entry: null }
    expect(toPayoffLeg(leg, 75, 95, 0.14)).toEqual({ kind: 'PE', side: 'sell', strike: 24_300, quantity: 225, entry: 95, iv: 0.14 })
  })
})

describe('presets', () => {
  // NIFTY's ladder, 50 points apart, the money at 24,300.
  const strikes = Array.from({ length: 41 }, (_, i) => 23_300 + 50 * i)

  it('builds a straddle on the money', () => {
    expect(presetLegs('short-straddle', strikes, 24_300)).toEqual([
      { kind: 'CE', side: 'sell', strike: 24_300, lots: 1, entry: null },
      { kind: 'PE', side: 'sell', strike: 24_300, lots: 1, entry: null },
    ])
  })

  it('puts the iron condor wings four and eight strikes out, calls above and puts below', () => {
    const legs = presetLegs('iron-condor', strikes, 24_300)!
    expect(legs.map((l) => `${l.side} ${l.strike} ${l.kind}`)).toEqual([
      'sell 24500 CE',
      'buy 24700 CE',
      'sell 24100 PE',
      'buy 23900 PE',
    ])
  })

  it('builds the spreads in the direction they are named for', () => {
    expect(presetLegs('bull-call', strikes, 24_300)!.map((l) => `${l.side} ${l.strike}`)).toEqual(['buy 24300', 'sell 24500'])
    expect(presetLegs('bear-put', strikes, 24_300)!.map((l) => `${l.side} ${l.strike}`)).toEqual(['buy 24300', 'sell 24100'])
  })

  it('refuses rather than narrows the wings when the chain lists too few strikes', () => {
    expect(presetLegs('iron-condor', strikes.slice(0, 22), 24_300)).toBeNull()
    expect(presetLegs('long-straddle', strikes, 24_325)).toBeNull()
  })

  it("finds the money: the chain's ATM when it is listed, else the strike nearest the spot", () => {
    expect(atmStrike(strikes, 24_300, 24_318)).toBe(24_300)
    expect(atmStrike(strikes, null, 24_332)).toBe(24_350)
    expect(atmStrike(strikes, 30_000, 24_260)).toBe(24_250)
    expect(atmStrike([], 24_300, 24_318)).toBeNull()
  })
})

describe('adding a leg from the chain', () => {
  const one: BuilderLeg = { id: 'a', kind: 'CE', side: 'sell', strike: 24_500, lots: 1, entry: 88 }

  it('adds a lot to the same contract on the same side instead of a second row', () => {
    expect(addLeg([one], { kind: 'CE', side: 'sell', strike: 24_500 }, 'b')).toEqual([{ ...one, lots: 2 }])
  })

  it('adds a row for anything else, following the market', () => {
    const next = addLeg([one], { kind: 'CE', side: 'buy', strike: 24_500 }, 'b')
    expect(next).toHaveLength(2)
    expect(next[1]).toEqual({ id: 'b', kind: 'CE', side: 'buy', strike: 24_500, lots: 1, entry: null })
  })
})

describe('time to expiry', () => {
  it('runs to 15:30 IST on NSE and BSE, and to 23:30 IST on MCX', () => {
    expect(new Date(expiryCloseMs('2026-11-03', 'NSE')).toISOString()).toBe('2026-11-03T10:00:00.000Z')
    expect(new Date(expiryCloseMs('2026-11-05', 'BSE')).toISOString()).toBe('2026-11-05T10:00:00.000Z')
    expect(new Date(expiryCloseMs('2026-11-17', 'MCX')).toISOString()).toBe('2026-11-17T18:00:00.000Z')
  })

  it('counts calendar days, never below zero', () => {
    const close = expiryCloseMs('2026-11-03', 'NSE')
    expect(daysToExpiry(Date.parse('2026-10-30T04:00:00Z'), close)).toBeCloseTo(4.25, 10)
    expect(daysToExpiry(close + 60_000, close)).toBe(0)
  })

  it('says it in days, then hours on the last day', () => {
    expect(timeLeftLabel(4.25)).toBe('4.3 days')
    expect(timeLeftLabel(1)).toBe('1 day')
    expect(timeLeftLabel(0.2)).toBe('5 h')
    expect(timeLeftLabel(0)).toBe('expired')
  })
})
