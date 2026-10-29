import { describe, expect, it } from 'vitest'

import {
  breakevens,
  bsGreeks,
  bsPrice,
  expiryPnl,
  extremes,
  impliedVol,
  intrinsic,
  leftTailSlope,
  netGreeks,
  netPremium,
  normCdf,
  pnlBefore,
  priceGrid,
  priceRange,
  rightTailSlope,
} from './payoff'
import type { PayoffLeg } from './payoff'

/**
 * The strategy builder shows a trader what a position can make and lose
 * before he enters it. A wrong breakeven or a max loss called bounded when it
 * is not is the kind of number that costs money, so each is pinned here:
 * Black-Scholes against published values and against put-call parity, and
 * the expiry readings against positions whose answers can be worked by hand.
 */

const leg = (over: Partial<PayoffLeg> & Pick<PayoffLeg, 'kind' | 'side' | 'strike' | 'entry'>): PayoffLeg => ({
  quantity: 1,
  iv: 0.2,
  ...over,
})

describe('Black-Scholes', () => {
  it("prices Hull's textbook example (S 42, K 40, r 10%, σ 20%, six months)", () => {
    expect(bsPrice('CE', 42, 40, 0.5, 0.2, 0.1)).toBeCloseTo(4.76, 2)
    expect(bsPrice('PE', 42, 40, 0.5, 0.2, 0.1)).toBeCloseTo(0.81, 2)
  })

  it('gives the at-the-money one-year figures and greeks (S = K = 100, r 5%, σ 20%)', () => {
    const call = bsGreeks('CE', 100, 100, 1, 0.2, 0.05)!
    const put = bsGreeks('PE', 100, 100, 1, 0.2, 0.05)!
    expect(call.price).toBeCloseTo(10.4506, 3)
    expect(put.price).toBeCloseTo(5.5735, 3)
    expect(call.delta).toBeCloseTo(0.6368, 4)
    expect(put.delta).toBeCloseTo(-0.3632, 4)
    expect(call.gamma).toBeCloseTo(0.018762, 5)
    expect(put.gamma).toBeCloseTo(call.gamma, 10)
    // Vega per one point of IV, theta per calendar day: the platform's units.
    expect(call.vegaPerPoint).toBeCloseTo(0.37524, 4)
    expect(call.thetaPerDay).toBeCloseTo(-6.414 / 365, 5)
    expect(put.thetaPerDay).toBeCloseTo(-1.6579 / 365, 5)
  })

  it('holds put-call parity, on a spot and on a future (Black-76)', () => {
    for (const [s, k, t, v, r] of [
      [24_350, 24_300, 6 / 365, 0.13, 0.05],
      [24_350, 25_000, 30 / 365, 0.18, 0.065],
      [5_480, 5_300, 12 / 365, 0.35, 0.05],
    ]) {
      const parity = bsPrice('CE', s, k, t, v, r)! - bsPrice('PE', s, k, t, v, r)!
      expect(parity).toBeCloseTo(s - k * Math.exp(-r * t), 6)
      // With a carry equal to the rate the underlying is a future: C − P = (F − K)·e^(−rT).
      const onFuture = bsPrice('CE', s, k, t, v, r, r)! - bsPrice('PE', s, k, t, v, r, r)!
      expect(onFuture).toBeCloseTo((s - k) * Math.exp(-r * t), 6)
    }
  })

  it('keeps the normal CDF symmetric, which is what keeps parity exact', () => {
    for (const x of [0.35, 1.2, 2.5, 4]) expect(normCdf(x) + normCdf(-x)).toBeCloseTo(1, 15)
    expect(normCdf(0)).toBeCloseTo(0.5, 8)
    expect(normCdf(1.96)).toBeCloseTo(0.975, 4)
  })

  it('is worth its intrinsic value at expiry, and has no greeks there', () => {
    expect(bsPrice('CE', 24_500, 24_300, 0, 0.13, 0.05)).toBe(200)
    expect(bsPrice('PE', 24_500, 24_300, 0, 0.13, 0.05)).toBe(0)
    expect(bsGreeks('CE', 24_500, 24_300, 0, 0.13, 0.05)).toBeNull()
    expect(intrinsic('PE', 24_300, 24_000)).toBe(300)
  })

  it('solves the IV back from a price, and refuses a price with no time value in it', () => {
    const price = bsPrice('PE', 24_350, 24_200, 5 / 365, 0.142, 0.05)!
    expect(impliedVol('PE', price, 24_350, 24_200, 5 / 365, 0.05)).toBeCloseTo(0.142, 6)
    // A call trading under what it is worth outright has no volatility that explains it.
    expect(impliedVol('CE', 140, 24_350, 24_200, 5 / 365, 0.05)).toBeNull()
    expect(impliedVol('CE', 0, 24_350, 24_200, 5 / 365, 0.05)).toBeNull()
  })
})

describe('a straddle', () => {
  // NIFTY 24,300, one lot of 75: the call at 120, the put at 95.
  const call = { kind: 'CE' as const, strike: 24_300, entry: 120, quantity: 75 }
  const put = { kind: 'PE' as const, strike: 24_300, entry: 95, quantity: 75 }
  const long = [leg({ ...call, side: 'buy' }), leg({ ...put, side: 'buy' })]
  const short = [leg({ ...call, side: 'sell' }), leg({ ...put, side: 'sell' })]

  it('breaks even at the strike plus and minus the premium, bought or sold', () => {
    expect(breakevens(long)).toEqual([24_085, 24_515])
    expect(breakevens(short)).toEqual([24_085, 24_515])
  })

  it('pays or receives the premium for every unit', () => {
    expect(netPremium(long)).toBe(-215 * 75)
    expect(netPremium(short)).toBe(215 * 75)
  })

  it('bought, loses at most the premium and has no ceiling', () => {
    const { maxProfit, maxLoss } = extremes(long)
    expect(maxProfit).toBe('unlimited')
    expect(maxLoss).toEqual({ value: -215 * 75, at: 24_300 })
  })

  it('sold, makes at most the premium and has no floor on the loss', () => {
    const { maxProfit, maxLoss } = extremes(short)
    expect(maxProfit).toEqual({ value: 215 * 75, at: 24_300 })
    expect(maxLoss).toBe('unlimited')
  })
})

describe('an iron condor', () => {
  // Short 105/95, long 110/90, each wing five points wide, for a credit of 4.
  const condor = [
    leg({ kind: 'CE', side: 'sell', strike: 105, entry: 3, quantity: 50 }),
    leg({ kind: 'CE', side: 'buy', strike: 110, entry: 1, quantity: 50 }),
    leg({ kind: 'PE', side: 'sell', strike: 95, entry: 3, quantity: 50 }),
    leg({ kind: 'PE', side: 'buy', strike: 90, entry: 1, quantity: 50 }),
  ]

  it('makes at most its credit and loses at most the wing less the credit', () => {
    const { maxProfit, maxLoss } = extremes(condor)
    expect(netPremium(condor)).toBe(4 * 50)
    expect(maxProfit).toEqual({ value: 4 * 50, at: 95 })
    expect(maxLoss).toEqual({ value: -(5 - 4) * 50, at: 90 })
    expect(expiryPnl(condor, 200)).toBe(-(5 - 4) * 50)
  })

  it('breaks even at the short strikes plus and minus the credit', () => {
    expect(breakevens(condor)).toEqual([91, 109])
  })

  it('has flat tails: the bought wings cover the sold strikes', () => {
    expect(rightTailSlope(condor)).toBe(0)
    expect(leftTailSlope(condor)).toBe(0)
  })
})

describe('unlimited detection', () => {
  it('reads a naked short call as an unbounded loss and a bought call as an unbounded gain', () => {
    expect(extremes([leg({ kind: 'CE', side: 'sell', strike: 100, entry: 5 })]).maxLoss).toBe('unlimited')
    expect(extremes([leg({ kind: 'CE', side: 'buy', strike: 100, entry: 5 })]).maxProfit).toBe('unlimited')
  })

  it("bounds a short put's loss at a price of zero, and says that is where it lies", () => {
    const { maxProfit, maxLoss } = extremes([leg({ kind: 'PE', side: 'sell', strike: 24_000, entry: 80, quantity: 75 })])
    expect(maxLoss).toEqual({ value: -(24_000 - 80) * 75, at: 0 })
    expect(maxProfit).toEqual({ value: 80 * 75, at: 24_000 })
  })

  it('bounds both sides of a bull call spread', () => {
    const spread = [leg({ kind: 'CE', side: 'buy', strike: 100, entry: 6 }), leg({ kind: 'CE', side: 'sell', strike: 110, entry: 2 })]
    expect(extremes(spread)).toEqual({ maxProfit: { value: 6, at: 110 }, maxLoss: { value: -4, at: 100 } })
    expect(breakevens(spread)).toEqual([104])
  })

  it('catches the naked call inside a ratio spread', () => {
    const ratio = [leg({ kind: 'CE', side: 'buy', strike: 100, entry: 6 }), leg({ kind: 'CE', side: 'sell', strike: 110, entry: 2, quantity: 2 })]
    expect(rightTailSlope(ratio)).toBe(-1)
    expect(extremes(ratio).maxLoss).toBe('unlimited')
    expect(extremes(ratio).maxProfit).toEqual({ value: 8, at: 110 })
    expect(breakevens(ratio)).toEqual([102, 118])
  })

  it('names only the start of a stretch that stays at zero', () => {
    // Bought 100 CE at 5 and sold 105 CE at nothing: −5 below 100, zero from 105 up.
    const flat = [leg({ kind: 'CE', side: 'buy', strike: 100, entry: 5 }), leg({ kind: 'CE', side: 'sell', strike: 105, entry: 0 })]
    expect(breakevens(flat)).toEqual([105])
  })
})

describe('before expiry', () => {
  const s = 24_350
  const p = { years: 6 / 365, rate: 0.05, carry: 0 }
  const fair = (kind: 'CE' | 'PE', strike: number, iv: number) => bsPrice(kind, s, strike, p.years, iv, p.rate)!

  it('is flat at the spot when every leg was entered at the model price', () => {
    const legs = [
      leg({ kind: 'CE', side: 'sell', strike: 24_500, entry: fair('CE', 24_500, 0.12), iv: 0.12, quantity: 75 }),
      leg({ kind: 'PE', side: 'sell', strike: 24_200, entry: fair('PE', 24_200, 0.14), iv: 0.14, quantity: 75 }),
    ]
    expect(pnlBefore(legs, s, p)).toBeCloseTo(0, 6)
  })

  it('meets the expiry line when no time is left', () => {
    const legs = [leg({ kind: 'CE', side: 'buy', strike: 24_300, entry: 120, quantity: 75 })]
    expect(pnlBefore(legs, 24_600, { ...p, years: 0 })).toBe(expiryPnl(legs, 24_600))
  })

  it('draws no curve when a leg has no IV to price it with', () => {
    const legs = [leg({ kind: 'CE', side: 'buy', strike: 24_300, entry: 120 }), leg({ kind: 'PE', side: 'buy', strike: 24_300, entry: 95, iv: null })]
    expect(pnlBefore(legs, s, p)).toBeNull()
    expect(netGreeks(legs, s, p)).toBeNull()
  })

  it('nets the greeks in rupees: per unit × lots × lot size × side', () => {
    const one = bsGreeks('CE', s, 24_300, p.years, 0.13, p.rate)!
    const bought = netGreeks([leg({ kind: 'CE', side: 'buy', strike: 24_300, entry: 120, iv: 0.13, quantity: 2 * 75 })], s, p)!
    expect(bought.delta).toBeCloseTo(150 * one.delta, 9)
    expect(bought.thetaPerDay).toBeCloseTo(150 * one.thetaPerDay, 9)
    expect(bought.thetaPerDay).toBeLessThan(0)
    const sold = netGreeks([leg({ kind: 'CE', side: 'sell', strike: 24_300, entry: 120, iv: 0.13, quantity: 2 * 75 })], s, p)!
    expect(sold.vegaPerPoint).toBeCloseTo(-bought.vegaPerPoint, 9)
    expect(sold.thetaPerDay).toBeGreaterThan(0)
  })

  it('gives a vega and a theta that match the curve moved by one IV point and one day', () => {
    const legs = [
      leg({ kind: 'CE', side: 'sell', strike: 24_300, entry: 120, iv: 0.13, quantity: 75 }),
      leg({ kind: 'PE', side: 'sell', strike: 24_300, entry: 95, iv: 0.135, quantity: 75 }),
    ]
    const g = netGreeks(legs, s, p)!
    const base = pnlBefore(legs, s, p)!
    const upOnePoint = pnlBefore(legs, s, { ...p, ivShift: 1 })!
    const aDayOn = pnlBefore(legs, s, { ...p, years: p.years - 1 / 365 })!
    expect((upOnePoint - base) / g.vegaPerPoint).toBeGreaterThan(0.98)
    expect((upOnePoint - base) / g.vegaPerPoint).toBeLessThan(1.02)
    // Theta is the instant rate; a whole day of a six-day straddle decays a little faster than it.
    expect((aDayOn - base) / g.thetaPerDay).toBeGreaterThan(0.95)
    expect((aDayOn - base) / g.thetaPerDay).toBeLessThan(1.15)
  })

  it('never takes an IV below 1%, however far the what-if shifts it', () => {
    const legs = [leg({ kind: 'CE', side: 'buy', strike: 24_300, entry: 120, iv: 0.13 })]
    expect(pnlBefore(legs, s, { ...p, ivShift: -40 })).toBe(pnlBefore(legs, s, { ...p, ivShift: -13 }))
  })
})

describe("the chart's prices", () => {
  it('centres on the spot, at least 5% either side, wide enough for every strike', () => {
    expect(priceRange(24_000, [])).toEqual({ lo: 22_800, hi: 25_200 })
    const wide = priceRange(24_000, [leg({ kind: 'CE', side: 'sell', strike: 26_000, entry: 5 })])
    expect(wide.hi).toBeGreaterThan(26_000)
    expect(wide.lo).toBe(24_000 - (wide.hi - 24_000))
    expect(priceRange(100, [leg({ kind: 'PE', side: 'buy', strike: 190, entry: 90 })]).lo).toBe(0)
  })

  it('draws through every strike and breakeven inside the range, in order', () => {
    const xs = priceGrid(100, 200, [125.5, 150, 250], 4)
    expect(xs).toEqual([100, 125, 125.5, 150, 175, 200])
  })
})
