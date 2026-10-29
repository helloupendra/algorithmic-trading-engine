/**
 * Markets → Strategy builder, as data: the legs a trader puts together off
 * the option chain, the presets built around the money, what each leg is
 * priced at and with which IV, and how long the expiry has left. The page
 * draws these and lib/payoff.ts does the arithmetic, so each rule here is
 * pinned by strategyBuilder.test.ts rather than by a screenshot.
 *
 * Analysis only. Nothing here places an order, and the page says so.
 */

import { impliedVol } from './payoff'
import type { LegSide, OptionKind, PayoffLeg } from './payoff'
import type { OptionChain, OptionChainLeg } from './types'

/**
 * The rate the builder prices at, 5%: the rate the platform's live greeks
 * are on (OptionMath.LiveRiskFreeRate, the default of the feed's enricher
 * and the chain poller), so a leg's theta here reads the same as the same
 * leg's on a run card. The backtests' 6.5% is not used: a live figure that
 * changed with the page it was read on would look like the market moving.
 */
export const BUILDER_RATE = 0.05

/** One leg as the trader built it. */
export interface BuilderLeg {
  id: string
  kind: OptionKind
  side: LegSide
  strike: number
  lots: number
  /** A price the trader typed; null follows the market (mid, else LTP). */
  entry: number | null
}

/** The most lots one leg takes: a typo of 5000 should not draw a chart in crores. */
export const MAX_LOTS = 500

/** The chain's side of one strike, or null when the chain lists none. */
export function chainLeg(chain: OptionChain, strike: number, kind: OptionKind): OptionChainLeg | null {
  const row = chain.strikes.find((s) => s.strikePrice === strike)
  return (kind === 'CE' ? row?.call : row?.put) ?? null
}

/** What a leg costs now, and how the price was read. */
export interface MarketPrice {
  price: number
  /** "mid" of a two-sided quote, else the last trade. */
  basis: 'mid' | 'ltp'
}

/**
 * The price a leg is entered at unless the trader types one: the middle of
 * the best bid and ask when both are there and not crossed, else the last
 * trade. A last trade can be minutes old on a far strike; a live two-sided
 * quote is the market now. Null when the chain has neither.
 */
export function marketPrice(leg: OptionChainLeg | null): MarketPrice | null {
  if (!leg) return null
  const { bidPrice: bid, askPrice: ask, lastTradedPrice: ltp } = leg
  if (bid != null && ask != null && bid > 0 && ask >= bid) return { price: Math.round(((bid + ask) / 2) * 100) / 100, basis: 'mid' }
  if (ltp != null && ltp > 0) return { price: ltp, basis: 'ltp' }
  return null
}

/** A leg's IV for the curve before expiry and the greeks, and where it came from. */
export interface LegIv {
  /** A fraction: 0.136 is 13.6%. */
  iv: number
  /** "chain": the chain's own IV. "price": solved here from the market price, the chain having none. */
  source: 'chain' | 'price'
}

/**
 * The chain's IV for a leg (it is sent in percent), or, when the chain has
 * none for that strike, the IV solved from the leg's market price at the
 * builder's rate. Null when neither exists: that leg cannot be priced before
 * expiry, and the page says so rather than drawing a curve without it.
 */
export function legIv(
  leg: OptionChainLeg | null,
  price: MarketPrice | null,
  kind: OptionKind,
  strike: number,
  underlying: number,
  years: number,
  carry: number,
): LegIv | null {
  const percent = leg?.impliedVolatility
  if (percent != null && percent > 0) return { iv: percent / 100, source: 'chain' }
  if (!price) return null
  const solved = impliedVol(kind, price.price, underlying, strike, years, BUILDER_RATE, carry)
  return solved != null ? { iv: solved, source: 'price' } : null
}

/** A builder leg in the arithmetic's terms: units are lots × lot size. */
export function toPayoffLeg(leg: BuilderLeg, lotSize: number, entry: number, iv: number | null): PayoffLeg {
  return { kind: leg.kind, side: leg.side, strike: leg.strike, quantity: leg.lots * lotSize, entry, iv }
}

// --- presets --------------------------------------------------------------------

export type PresetKey = 'long-straddle' | 'short-straddle' | 'short-strangle' | 'iron-condor' | 'bull-call' | 'bear-put'

/** One leg of a preset: a side and a kind, `offset` strikes from the money (above for +, below for −). */
interface PresetLeg {
  side: LegSide
  kind: OptionKind
  offset: number
}

/**
 * How far from the money the wings sit, in listed strikes rather than
 * points: four strikes is 200 points on NIFTY's 50-point ladder and 400 on
 * BANKNIFTY's 100, which is where these are commonly written. Every strike
 * can be moved afterwards.
 */
export const WING = 4

export const PRESETS: ReadonlyArray<{ key: PresetKey; label: string; hint: string; legs: readonly PresetLeg[] }> = [
  {
    key: 'long-straddle',
    label: 'Long straddle',
    hint: 'Buy the ATM call and put: pays on a big move either way',
    legs: [{ side: 'buy', kind: 'CE', offset: 0 }, { side: 'buy', kind: 'PE', offset: 0 }],
  },
  {
    key: 'short-straddle',
    label: 'Short straddle',
    hint: 'Sell the ATM call and put: pays if the price stays put; the loss has no bound',
    legs: [{ side: 'sell', kind: 'CE', offset: 0 }, { side: 'sell', kind: 'PE', offset: 0 }],
  },
  {
    key: 'short-strangle',
    label: 'Short strangle',
    hint: `Sell a call and a put ${WING} strikes either side of the money`,
    legs: [{ side: 'sell', kind: 'CE', offset: WING }, { side: 'sell', kind: 'PE', offset: -WING }],
  },
  {
    key: 'iron-condor',
    label: 'Iron condor',
    hint: `The short strangle with wings bought ${WING} strikes further out: the loss is capped`,
    legs: [
      { side: 'sell', kind: 'CE', offset: WING },
      { side: 'buy', kind: 'CE', offset: 2 * WING },
      { side: 'sell', kind: 'PE', offset: -WING },
      { side: 'buy', kind: 'PE', offset: -2 * WING },
    ],
  },
  {
    key: 'bull-call',
    label: 'Bull call spread',
    hint: `Buy the ATM call, sell one ${WING} strikes up`,
    legs: [{ side: 'buy', kind: 'CE', offset: 0 }, { side: 'sell', kind: 'CE', offset: WING }],
  },
  {
    key: 'bear-put',
    label: 'Bear put spread',
    hint: `Buy the ATM put, sell one ${WING} strikes down`,
    legs: [{ side: 'buy', kind: 'PE', offset: 0 }, { side: 'sell', kind: 'PE', offset: -WING }],
  },
]

/** The strike nearest the spot among those listed: the chain's ATM when it names one that is listed. */
export function atmStrike(strikes: readonly number[], atm: number | null | undefined, spot: number | null | undefined): number | null {
  if (strikes.length === 0) return null
  if (atm != null && strikes.includes(atm)) return atm
  const ref = spot != null && spot > 0 ? spot : strikes[Math.floor(strikes.length / 2)]
  return strikes.reduce((best, s) => (Math.abs(s - ref) < Math.abs(best - ref) ? s : best), strikes[0])
}

/**
 * A preset's legs on this chain, one lot each. Null when the chain does not
 * list a strike the preset needs: a strangle quietly built on narrower wings
 * would be a different trade under the same name.
 */
export function presetLegs(key: PresetKey, strikes: readonly number[], atm: number | null): Omit<BuilderLeg, 'id'>[] | null {
  const preset = PRESETS.find((p) => p.key === key)
  const sorted = [...strikes].sort((a, b) => a - b)
  const at = atm == null ? -1 : sorted.indexOf(atm)
  if (!preset || at < 0) return null
  const legs: Omit<BuilderLeg, 'id'>[] = []
  for (const p of preset.legs) {
    const strike = sorted[at + p.offset]
    if (strike == null) return null
    legs.push({ kind: p.kind, side: p.side, strike, lots: 1, entry: null })
  }
  return legs
}

/**
 * The legs with one more added from the chain: the same contract on the same
 * side takes another lot rather than a second row, as a broker's book would.
 */
export function addLeg(legs: readonly BuilderLeg[], next: Omit<BuilderLeg, 'id' | 'lots' | 'entry'>, id: string): BuilderLeg[] {
  const same = legs.find((l) => l.strike === next.strike && l.kind === next.kind && l.side === next.side)
  if (same) return legs.map((l) => (l === same ? { ...l, lots: Math.min(MAX_LOTS, l.lots + 1) } : l))
  return [...legs, { ...next, id, lots: 1, entry: null }]
}

// --- time ---------------------------------------------------------------------------

/**
 * When an expiry day ends on its exchange, in UTC milliseconds: 15:30 IST for
 * NSE and BSE, when expiring index contracts stop, also from 7 Dec 2026 (a
 * stock option then trades to 15:45, and its time left is understated by a
 * quarter of an hour, never overstated); for MCX the evening close, taken as 23:30 IST, the earlier of
 * its two seasonal closes (23:55 while New York is on winter time), so the
 * time left is never overstated. The server counts the live greeks to the
 * same close (ExpirySettler.ExpiryCloseUtc).
 */
export function expiryCloseMs(expiryIso: string, exchange: string | null | undefined): number {
  const time = (exchange ?? '').toUpperCase() === 'MCX' ? '23:30' : '15:30'
  return Date.parse(`${expiryIso.slice(0, 10)}T${time}:00+05:30`)
}

/** Calendar days from `nowMs` to the expiry close; never negative. */
export function daysToExpiry(nowMs: number, closeMs: number): number {
  return Math.max(0, (closeMs - nowMs) / 86_400_000)
}

/** "6.2 days", "1 day", "5 h", "expired". */
export function timeLeftLabel(days: number): string {
  if (days <= 0) return 'expired'
  if (days < 1) return `${Math.max(1, Math.round(days * 24))} h`
  const rounded = Math.round(days * 10) / 10
  return `${rounded.toLocaleString('en-IN')} ${rounded === 1 ? 'day' : 'days'}`
}
