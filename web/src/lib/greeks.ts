/**
 * How an open leg's greeks read on a run card.
 *
 * 27 Sep, the owner: "if I have bought or sold an option, the Greeks' effect
 * should show too — like theta shows when you buy". The API sends the figures
 * (see PositionGreeks on the server); these helpers only decide the words and
 * the colour, so the table cell and the totals line cannot disagree.
 *
 * Colour follows money, not the greek's sign: a bought option's theta is a
 * cost and reads red, a written one's is income and reads green — the same
 * rule as P&L, because it IS tomorrow's P&L if nothing else moves.
 */

import { formatInrSigned, formatTime, formatAge } from './format'
import type { GreeksSource, PositionGreeks, RunGreeksTotals } from './types'

const MINUS = '−'

/** "+1.25" / "−0.45": a signed decimal with a true minus sign. */
function signed(value: number, digits: number): string {
  const rounded = Number(value.toFixed(digits))
  if (rounded === 0) return (0).toFixed(digits)
  const body = Math.abs(rounded).toLocaleString('en-IN', {
    minimumFractionDigits: digits,
    maximumFractionDigits: digits,
  })
  return `${rounded > 0 ? '+' : MINUS}${body}`
}

/** "−₹937/day" for a bought option, "+₹937/day" for a written one. */
export function formatThetaPerDay(rupees: number | null | undefined): string {
  if (rupees == null) return '—'
  return `${formatInrSigned(rupees)}/day`
}

/** "pos" when time pays the book, "neg" when it costs, "" when flat: the P&L colours. */
export function thetaTone(rupees: number | null | undefined): string {
  if (rupees == null) return ''
  const whole = Math.round(rupees)
  if (whole === 0) return ''
  return whole > 0 ? 'pos' : 'neg'
}

/** A per-unit delta: "+0.52", "−0.45". */
export function formatDelta(delta: number | null | undefined): string {
  if (delta == null) return '—'
  return signed(delta, 2)
}

/** "13.6%"; "—" when unknown. */
export function formatIv(ivPercent: number | null | undefined): string {
  if (ivPercent == null) return '—'
  return `${ivPercent.toFixed(1)}%`
}

/** What each source is called on screen. */
export function sourceLabel(source: GreeksSource | string): string {
  switch (source) {
    case 'feed':
      return 'live quote'
    case 'chain':
      return 'option chain'
    case 'computed':
      return 'computed'
    case 'delta-one':
      return 'delta one'
    default:
      return source
  }
}

/**
 * The muted second line under a leg's delta: "IV 13.6% · live quote", or —
 * when the figures are old — "IV 13.6% · as of 15:29". Stale greeks are shown,
 * never passed off as now.
 */
export function greeksNote(g: PositionGreeks): string {
  if (g.source === 'delta-one') return 'future / share'
  const iv = g.ivPercent != null ? `IV ${formatIv(g.ivPercent)}` : 'IV —'
  if (g.stale && g.asOfUtc) return `${iv} · as of ${formatTime(g.asOfUtc).slice(0, 5)}`
  return `${iv} · ${sourceLabel(g.source)}`
}

/** Tooltip for a leg's greeks: every figure, where it came from and how old it is. */
export function greeksTitle(g: PositionGreeks): string {
  const parts = [
    `Source: ${sourceLabel(g.source)}`,
    g.asOfUtc ? `as of ${formatTime(g.asOfUtc)} (${formatAge(g.asOfUtc)})${g.stale ? ' — stale' : ''}` : null,
    `delta ${formatDelta(g.delta)}, gamma ${g.gamma.toFixed(4)}`,
    `theta ${signed(g.theta, 2)} pts/day, vega ${g.vega.toFixed(2)} pts per 1% IV (per unit)`,
    g.underlyingPrice != null ? `priced off ${g.underlyingPrice.toLocaleString('en-IN')}` : null,
  ]
  return parts.filter(Boolean).join('\n')
}

/**
 * "Δ +39 NIFTY · Δ −100 CRUDEOIL": the book's delta per underlying, in units
 * of each. Never summed across underlyings — a NIFTY point and a crude rupee
 * are different moves.
 */
export function deltaSummary(totals: RunGreeksTotals | null | undefined): string | null {
  if (!totals || totals.byUnderlying.length === 0) return null
  return totals.byUnderlying
    .map((u) => `Δ ${signed(u.deltaQuantity, u.deltaQuantity % 1 === 0 ? 0 : 1)} ${u.underlying}`)
    .join(' · ')
}

/** The totals' second line: vega, delta, and what is left out or old. */
export function totalsNote(totals: RunGreeksTotals): string {
  const parts: string[] = [`vega ${formatInrSigned(totals.vegaRupeesPerIvPoint)} per 1% IV`]
  const delta = deltaSummary(totals)
  if (delta) parts.push(delta)
  if (totals.unpriced > 0) parts.push(`${totals.unpriced} leg${totals.unpriced === 1 ? '' : 's'} not priced`)
  if (totals.stale && totals.oldestAsOfUtc) parts.push(`as of ${formatTime(totals.oldestAsOfUtc).slice(0, 5)}`)
  return parts.join(' · ')
}

/** How old an LTP may be before its row says so. */
export const LTP_STALE_AFTER_MS = 60_000

/**
 * "as of 15:29 · 18h ago" under a leg's LTP when the price is older than a
 * minute — a carried position the feed has not reached yet this morning is
 * still showing yesterday's price, and must say so. Null when fresh.
 */
export function ltpAgeNote(updatedUtc: string | null | undefined, nowMs: number = Date.now()): string | null {
  if (!updatedUtc) return null
  const at = new Date(updatedUtc).getTime()
  if (Number.isNaN(at) || nowMs - at <= LTP_STALE_AFTER_MS) return null
  return `as of ${formatTime(updatedUtc).slice(0, 5)} · ${formatAge(updatedUtc)}`
}
