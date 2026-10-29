/**
 * The strategy builder's arithmetic: Black-Scholes prices and greeks, the
 * P&L of a set of option legs at expiry and before it, and what can be read
 * off that P&L exactly: the breakevens, the most it can make and the most it
 * can lose.
 *
 * Conventions are the platform's (OptionMath.cs on the server), so a figure
 * here reads the same as one on a run card: volatility a fraction, time in
 * years on a 365-day count, theta per calendar day and vega per one point of
 * IV, both in premium points per unit. An option on a future (MCX) is priced
 * with a carry equal to the rate, which is Black-76.
 *
 * The P&L at expiry is piecewise linear with a kink at each strike, so nothing
 * about it needs sampling: between two strikes it is a straight line, below
 * the lowest its slope is set by the puts, and above the highest by the calls.
 * The breakevens are the zero crossings of those lines, and the extremes sit
 * at a strike, at a price of zero, or out along the right tail, where a net
 * short call leaves the loss without a bound. Only the curve before expiry,
 * which is smooth, is evaluated on a grid, and only to be drawn.
 */

export type OptionKind = 'CE' | 'PE'
export type LegSide = 'buy' | 'sell'

/** One leg as the arithmetic sees it. */
export interface PayoffLeg {
  kind: OptionKind
  side: LegSide
  strike: number
  /** Units, not lots: lots × lot size. */
  quantity: number
  /** Premium per unit: paid on a buy, received on a sell. */
  entry: number
  /** Implied volatility as a fraction (0.136 is 13.6%); null when nothing priced it. */
  iv: number | null
}

/** Where the legs are priced from, before expiry. */
export interface PricingInputs {
  /** Years to expiry, 365-day count; zero or less is expiry itself. */
  years: number
  /** Risk-free rate, a fraction. */
  rate: number
  /** The underlying's continuous yield: 0 for an index priced off its spot, the rate for an option on a future. */
  carry: number
  /** Added to every leg's IV, in IV points (2 is +2%). */
  ivShift?: number
}

/** Per-unit Black-Scholes value and greeks, in the platform's units. */
export interface OptionGreeks {
  price: number
  delta: number
  gamma: number
  /** Premium points per calendar day. */
  thetaPerDay: number
  /** Premium points per one point of IV. */
  vegaPerPoint: number
}

/** The lowest IV a what-if can take a leg to (1%): a shift past zero has no meaning. */
export const MIN_IV = 0.01

/** The widest and narrowest IV the solver searches, as the server's (OptionMath.MaxVolatility). */
const IV_LOW = 0.001
const IV_HIGH = 5

// --- Black-Scholes -------------------------------------------------------------

/**
 * The standard normal CDF, Abramowitz & Stegun 7.1.26 on erf: the server's
 * own (OptionMath.NormalCdf), so the two sides price an option alike. It is
 * symmetric by construction, N(x) + N(−x) = 1 to floating-point rounding for
 * any x but zero (where it is out by 1e-9), which is what keeps put-call
 * parity exact rather than within the approximation's error.
 */
export function normCdf(x: number): number {
  const sign = x < 0 ? -1 : 1
  const z = Math.abs(x) / Math.SQRT2
  const t = 1 / (1 + 0.3275911 * z)
  const y = 1 - ((((1.061405429 * t - 1.453152027) * t + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.exp(-z * z)
  return 0.5 * (1 + sign * y)
}

function normPdf(x: number): number {
  return Math.exp(-0.5 * x * x) / Math.sqrt(2 * Math.PI)
}

/** What an option is worth at expiry, per unit. */
export function intrinsic(kind: OptionKind, strike: number, underlying: number): number {
  return kind === 'CE' ? Math.max(underlying - strike, 0) : Math.max(strike - underlying, 0)
}

/**
 * Per-unit price and greeks under Black-Scholes-Merton. Null when the inputs
 * define none: no time left, no volatility, or a price that is not one.
 */
export function bsGreeks(
  kind: OptionKind,
  underlying: number,
  strike: number,
  years: number,
  vol: number,
  rate: number,
  carry = 0,
): OptionGreeks | null {
  if (!(underlying > 0) || !(strike > 0) || !(vol > 0) || !(years > 0)) return null
  const sqrtT = Math.sqrt(years)
  const d1 = (Math.log(underlying / strike) + (rate - carry + 0.5 * vol * vol) * years) / (vol * sqrtT)
  const d2 = d1 - vol * sqrtT
  const carryDiscount = Math.exp(-carry * years)
  const rateDiscount = Math.exp(-rate * years)
  const pdf = normPdf(d1)
  const decay = (-underlying * carryDiscount * pdf * vol) / (2 * sqrtT)

  let price: number
  let delta: number
  let thetaAnnual: number
  if (kind === 'CE') {
    price = underlying * carryDiscount * normCdf(d1) - strike * rateDiscount * normCdf(d2)
    delta = carryDiscount * normCdf(d1)
    thetaAnnual = decay - rate * strike * rateDiscount * normCdf(d2) + carry * underlying * carryDiscount * normCdf(d1)
  } else {
    price = strike * rateDiscount * normCdf(-d2) - underlying * carryDiscount * normCdf(-d1)
    delta = carryDiscount * (normCdf(d1) - 1)
    thetaAnnual = decay + rate * strike * rateDiscount * normCdf(-d2) - carry * underlying * carryDiscount * normCdf(-d1)
  }

  return {
    price,
    delta,
    gamma: (carryDiscount * pdf) / (underlying * vol * sqrtT),
    thetaPerDay: thetaAnnual / 365,
    vegaPerPoint: (underlying * carryDiscount * pdf * sqrtT) / 100,
  }
}

/** The per-unit price before expiry, and the intrinsic value at it (no volatility is needed there). */
export function bsPrice(
  kind: OptionKind,
  underlying: number,
  strike: number,
  years: number,
  vol: number,
  rate: number,
  carry = 0,
): number | null {
  if (years <= 0) return intrinsic(kind, strike, underlying)
  return bsGreeks(kind, underlying, strike, years, vol, rate, carry)?.price ?? null
}

/**
 * The volatility (a fraction) at which Black-Scholes prices the option at
 * `price`. Null when none can: the price is at or under what the option is
 * worth with no time value, or past what it could ever be worth. Bisection,
 * as on the server: a price is monotonic in volatility, so halving the
 * bracket always converges where Newton's step can run away on a far strike.
 */
export function impliedVol(
  kind: OptionKind,
  price: number,
  underlying: number,
  strike: number,
  years: number,
  rate: number,
  carry = 0,
): number | null {
  if (!(price > 0) || !(underlying > 0) || !(strike > 0) || !(years > 0)) return null
  const at = (vol: number) => bsGreeks(kind, underlying, strike, years, vol, rate, carry)!.price
  let low = IV_LOW
  let high = IV_HIGH
  if (price <= at(low) || price >= at(high)) return null
  for (let i = 0; i < 60; i++) {
    const mid = 0.5 * (low + high)
    if (at(mid) > price) high = mid
    else low = mid
  }
  return 0.5 * (low + high)
}

// --- a position ------------------------------------------------------------------

/** +1 for a bought leg, −1 for a sold one. */
export function sideSign(side: LegSide): number {
  return side === 'buy' ? 1 : -1
}

/** Premium for the whole position in rupees: positive when it is received (a credit), negative when paid. */
export function netPremium(legs: readonly PayoffLeg[]): number {
  return legs.reduce((sum, l) => sum - sideSign(l.side) * l.entry * l.quantity, 0)
}

/** The position's P&L in rupees if the underlying settles at `underlying` on expiry. */
export function expiryPnl(legs: readonly PayoffLeg[], underlying: number): number {
  return legs.reduce((sum, l) => sum + sideSign(l.side) * (intrinsic(l.kind, l.strike, underlying) - l.entry) * l.quantity, 0)
}

/** A leg's IV after a what-if shift, never under MIN_IV. */
function shifted(iv: number, shift: number | undefined): number {
  return Math.max(MIN_IV, iv + (shift ?? 0) / 100)
}

/**
 * The position's P&L in rupees at `underlying` before expiry, each leg marked
 * at its Black-Scholes value. Null when a leg has no IV to price it with,
 * since a curve that left a leg out would be a different position's.
 */
export function pnlBefore(legs: readonly PayoffLeg[], underlying: number, p: PricingInputs): number | null {
  if (p.years <= 0) return expiryPnl(legs, underlying)
  let sum = 0
  for (const l of legs) {
    if (l.iv == null || !(l.iv > 0)) return null
    const value = bsPrice(l.kind, underlying, l.strike, p.years, shifted(l.iv, p.ivShift), p.rate, p.carry)
    if (value == null) return null
    sum += sideSign(l.side) * (value - l.entry) * l.quantity
  }
  return sum
}

/** The position's greeks in rupees: per-unit figures × quantity × side. */
export interface NetGreeks {
  /** Rupees per one point of the underlying (equally, the units of underlying the position is long). */
  delta: number
  /** How far `delta` moves for a one-point move. */
  gamma: number
  /** Rupees per calendar day, everything else held. */
  thetaPerDay: number
  /** Rupees per one point of IV on every leg. */
  vegaPerPoint: number
}

/**
 * The position's greeks at `underlying` now. Null when a leg has no IV, or
 * when there is no time left (at expiry the greeks are not defined).
 */
export function netGreeks(legs: readonly PayoffLeg[], underlying: number, p: PricingInputs): NetGreeks | null {
  if (legs.length === 0 || p.years <= 0) return null
  const total: NetGreeks = { delta: 0, gamma: 0, thetaPerDay: 0, vegaPerPoint: 0 }
  for (const l of legs) {
    if (l.iv == null || !(l.iv > 0)) return null
    const g = bsGreeks(l.kind, underlying, l.strike, p.years, shifted(l.iv, p.ivShift), p.rate, p.carry)
    if (!g) return null
    const units = sideSign(l.side) * l.quantity
    total.delta += units * g.delta
    total.gamma += units * g.gamma
    total.thetaPerDay += units * g.thetaPerDay
    total.vegaPerPoint += units * g.vegaPerPoint
  }
  return total
}

// --- read off the expiry line ------------------------------------------------------

/** The slope of the expiry P&L above the highest strike: rupees per point, the net units of calls held. */
export function rightTailSlope(legs: readonly PayoffLeg[]): number {
  return legs.reduce((sum, l) => sum + (l.kind === 'CE' ? sideSign(l.side) * l.quantity : 0), 0)
}

/** The slope below the lowest strike: minus the net units of puts held (a bought put gains as the price falls). */
export function leftTailSlope(legs: readonly PayoffLeg[]): number {
  return legs.reduce((sum, l) => sum - (l.kind === 'PE' ? sideSign(l.side) * l.quantity : 0), 0)
}

/** Where the expiry line can turn: a price of zero and every strike, ascending. */
function kinks(legs: readonly PayoffLeg[]): number[] {
  return [...new Set([0, ...legs.map((l) => l.strike)])].sort((a, b) => a - b)
}

/** What counts as zero rupees for this position: a billionth of its size, so rounding never makes or hides a crossing. */
function tolerance(legs: readonly PayoffLeg[]): number {
  return 1e-9 * Math.max(1, legs.reduce((sum, l) => sum + l.quantity * Math.max(l.strike, l.entry, 1), 0))
}

/**
 * Every underlying price at which the position breaks even on expiry,
 * ascending. Exact: the zero crossings of the straight lines between the
 * kinks, and of the right tail beyond the last. A stretch where the P&L is
 * exactly zero gives its two ends.
 */
export function breakevens(legs: readonly PayoffLeg[]): number[] {
  if (legs.length === 0) return []
  const eps = tolerance(legs)
  const xs = kinks(legs)
  const vs = xs.map((x) => expiryPnl(legs, x))
  const zero = (i: number) => Math.abs(vs[i]) <= eps
  const roots: number[] = []
  for (let i = 0; i < xs.length; i++) {
    if (zero(i)) {
      // Inside a flat stretch at zero, a kink is neither of its ends.
      const inside = i > 0 && zero(i - 1) && (i + 1 < xs.length ? zero(i + 1) : rightTailSlope(legs) === 0)
      if (!inside) roots.push(xs[i])
    } else if (i + 1 < xs.length && Math.abs(vs[i + 1]) > eps && Math.sign(vs[i]) !== Math.sign(vs[i + 1])) {
      roots.push(xs[i] + ((xs[i + 1] - xs[i]) * vs[i]) / (vs[i] - vs[i + 1]))
    }
  }
  const last = vs[vs.length - 1]
  const slope = rightTailSlope(legs)
  if (Math.abs(last) > eps && slope !== 0 && Math.sign(slope) !== Math.sign(last)) {
    roots.push(xs[xs.length - 1] - last / slope)
  }
  // A price of zero is where a contract stops existing, not a breakeven anyone trades to.
  return roots.filter((r, i) => r > 0 && (i === 0 || r - roots[i - 1] > 1e-9))
}

/** One extreme of the expiry P&L: rupees and the price where it is first reached, or no bound at all. */
export type Extreme = { value: number; at: number } | 'unlimited'

/**
 * The most the position can make and the most it can lose by expiry. Exact
 * for the same reason as the breakevens: a straight-line P&L peaks at a kink
 * or runs away along the right tail, so the tail's slope alone says whether
 * either side is unbounded. The left side always ends, at a price of zero;
 * `at` is zero only when nothing short of zero reaches the extreme, so a
 * short put's worst case reads as what it is and an iron condor's does not.
 */
export function extremes(legs: readonly PayoffLeg[]): { maxProfit: Extreme; maxLoss: Extreme } {
  if (legs.length === 0) return { maxProfit: { value: 0, at: 0 }, maxLoss: { value: 0, at: 0 } }
  const eps = tolerance(legs)
  let best = { value: -Infinity, at: 0 }
  let worst = { value: Infinity, at: 0 }
  // The strikes first, ascending, and zero last: it wins only by being strictly further out.
  const [zero, ...strikes] = kinks(legs)
  for (const x of [...strikes, zero]) {
    const v = expiryPnl(legs, x)
    if (v > best.value + eps) best = { value: v, at: x }
    if (v < worst.value - eps) worst = { value: v, at: x }
  }
  const slope = rightTailSlope(legs)
  return {
    maxProfit: slope > 0 ? 'unlimited' : best,
    maxLoss: slope < 0 ? 'unlimited' : worst,
  }
}

// --- the chart's prices ------------------------------------------------------------

/**
 * The underlying prices the chart spans: centred on the spot, wide enough to
 * hold every strike and breakeven with room past them (and `extra`, the
 * what-if price), and never less than 5% either side. Never below zero.
 */
export function priceRange(
  spot: number,
  legs: readonly PayoffLeg[],
  marks: readonly number[] = [],
  extra: readonly number[] = [],
): { lo: number; hi: number } {
  const far = Math.max(0, ...[...legs.map((l) => l.strike), ...marks].map((x) => Math.abs(x - spot) * 1.25), ...extra.map((x) => Math.abs(x - spot) * 1.1))
  const half = Math.max(spot * 0.05, far)
  return { lo: Math.max(0, spot - half), hi: spot + half }
}

/**
 * The prices the curves are drawn through: `steps` even steps across the
 * range, and every strike and breakeven inside it, so the expiry line's
 * corners are drawn where they are and not where a step happened to fall.
 */
export function priceGrid(lo: number, hi: number, exact: readonly number[], steps = 200): number[] {
  const xs: number[] = []
  for (let i = 0; i <= steps; i++) xs.push(lo + ((hi - lo) * i) / steps)
  for (const x of exact) if (x > lo && x < hi) xs.push(x)
  return [...new Set(xs)].sort((a, b) => a - b)
}
