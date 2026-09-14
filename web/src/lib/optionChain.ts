/**
 * Pure helpers for the advanced option chain page: where the spot line goes,
 * which halves are in the money, the strike window, compact Indian notation
 * and the freshness line. Kept out of the component so each rule is pinned by
 * a test rather than by looking at a screenshot at 09:15.
 */

import type { OptionChainHeader, OptionChainStrike } from './types'

export const UNDERLYINGS = [
  'NIFTY',
  'BANKNIFTY',
  'FINNIFTY',
  'MIDCPNIFTY',
  'SENSEX',
  'BANKEX',
  'CRUDEOIL',
  'NATURALGAS',
] as const

const MINUS = '−'

/**
 * Compact Indian-market notation: 1.94Cr, 1.94L, 44.0K, 950.
 * Crore and lakh to two places, thousands to one — the way chains are read.
 */
export function compactIndian(value: number | null | undefined): string {
  if (value == null || !Number.isFinite(value)) return '—'
  const abs = Math.abs(value)
  const sign = value < 0 ? MINUS : ''
  if (abs >= 1e7) return `${sign}${(abs / 1e7).toFixed(2)}Cr`
  if (abs >= 1e5) return `${sign}${(abs / 1e5).toFixed(2)}L`
  if (abs >= 1e3) return `${sign}${(abs / 1e3).toFixed(1)}K`
  return `${sign}${Math.round(abs)}`
}

/** The same, always signed: "+44.0K", "−7.0K", "0". */
export function compactSigned(value: number | null | undefined): string {
  if (value == null || !Number.isFinite(value)) return '—'
  if (Math.round(value) === 0) return '0'
  return value > 0 ? `+${compactIndian(value)}` : compactIndian(value)
}

/** "57,369.65" — Indian grouping, two decimals. */
export function price(value: number | null | undefined, digits = 2): string {
  if (value == null || !Number.isFinite(value)) return '—'
  return value.toLocaleString('en-IN', { minimumFractionDigits: digits, maximumFractionDigits: digits })
}

/** "+0.11%", "−5.82%". */
export function signedPercent(value: number | null | undefined, digits = 2): string {
  if (value == null || !Number.isFinite(value)) return '—'
  const fixed = Math.abs(value).toFixed(digits)
  if (Number(fixed) === 0) return `${fixed}%`
  return `${value > 0 ? '+' : MINUS}${fixed}%`
}

/** "▲0.02%" / "▼0.02%" for the spot marker. */
export function arrowPercent(value: number | null | undefined): string {
  if (value == null || !Number.isFinite(value)) return ''
  const fixed = Math.abs(value).toFixed(2)
  if (Number(fixed) === 0) return `${fixed}%`
  return `${value > 0 ? '▲' : '▼'}${fixed}%`
}

/** "pos" / "neg" / "" for a signed number. */
export function tone(value: number | null | undefined): string {
  if (value == null || !Number.isFinite(value) || value === 0) return ''
  return value > 0 ? 'pos' : 'neg'
}

// --- where the money is ---------------------------------------------------------

/** A call is in the money when its strike is below the spot. */
export function isCallItm(strike: number, spot: number | null | undefined): boolean {
  return spot != null && spot > 0 && strike < spot
}

/** A put is in the money when its strike is above the spot. */
export function isPutItm(strike: number, spot: number | null | undefined): boolean {
  return spot != null && spot > 0 && strike > spot
}

/**
 * Where the spot line goes in an ascending list of strikes: the index of the
 * first row BELOW the line, i.e. the count of strikes at or under the spot.
 * 0 puts the line above every row, `strikes.length` below every row. Null when
 * there is no spot or nothing to place it among.
 */
export function spotMarkerIndex(strikes: readonly number[], spot: number | null | undefined): number | null {
  if (spot == null || !(spot > 0) || strikes.length === 0) return null
  let count = 0
  for (const strike of strikes) {
    if (strike <= spot) count++
    else break
  }
  return count
}

export type WindowSize = 10 | 20 | 'all'

/**
 * The slice [start, end) of an ascending strike list that shows `each` strikes
 * either side of the ATM row. When the ATM is near an end the window slides
 * inward so the same number of rows is still shown where the chain allows.
 */
export function strikeWindow(
  strikes: readonly number[],
  atm: number | null | undefined,
  each: WindowSize,
): { start: number; end: number } {
  const n = strikes.length
  if (each === 'all' || n <= each * 2 + 1) return { start: 0, end: n }

  let centre = atm == null ? -1 : strikes.indexOf(atm)
  if (centre < 0) {
    // No exact ATM in the list: the nearest strike to it, else the middle.
    centre = atm == null
      ? Math.floor(n / 2)
      : strikes.reduce((best, s, i) => (Math.abs(s - atm) < Math.abs(strikes[best] - atm) ? i : best), 0)
  }

  const size = each * 2 + 1
  let start = Math.max(0, centre - each)
  start = Math.min(start, n - size)
  return { start, end: start + size }
}

/** The largest value in a column on one side, so the heaviest row can be marked. */
export function sidePeak(
  strikes: readonly OptionChainStrike[],
  side: 'call' | 'put',
  field: 'openInterest' | 'volume',
): number {
  let peak = 0
  for (const strike of strikes) {
    const value = strike[side]?.[field] ?? 0
    if (value > peak) peak = value
  }
  return peak
}

/** The largest absolute value across both sides, which bars are scaled against. */
export function columnPeak(
  strikes: readonly OptionChainStrike[],
  field: 'openInterest' | 'volume' | 'openInterestChange',
): number {
  let peak = 0
  for (const strike of strikes) {
    for (const leg of [strike.call, strike.put]) {
      const value = Math.abs(leg?.[field] ?? 0)
      if (value > peak) peak = value
    }
  }
  return peak
}

// --- expiry and time ------------------------------------------------------------

const IST_OFFSET_MS = 330 * 60_000
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']

/** "2026-09-14" — the IST calendar day of an instant. */
export function istDate(ms: number): string {
  return new Date(ms + IST_OFFSET_MS).toISOString().slice(0, 10)
}

/** "10:41" — IST wall clock of an instant. */
export function istTime(iso: string | null | undefined, seconds = false): string {
  if (!iso) return '—'
  const ms = Date.parse(iso)
  if (Number.isNaN(ms)) return '—'
  return new Date(ms + IST_OFFSET_MS).toISOString().slice(11, seconds ? 19 : 16)
}

/** "14 Sep 18:24" — IST day and time of an instant. */
export function istStamp(iso: string | null | undefined): string {
  if (!iso) return '—'
  const ms = Date.parse(iso)
  if (Number.isNaN(ms)) return '—'
  const d = new Date(ms + IST_OFFSET_MS).toISOString()
  return `${Number(d.slice(8, 10))} ${MONTHS[Number(d.slice(5, 7)) - 1]} ${d.slice(11, 16)}`
}

/** Whole days between two "yyyy-mm-dd" dates. */
export function daysBetween(fromIso: string, toIso: string): number {
  return Math.round((Date.parse(`${toIso}T00:00:00Z`) - Date.parse(`${fromIso}T00:00:00Z`)) / 86_400_000)
}

/** "29 Sep 2026 (+15 days)", "(today)", "(expired)". */
export function expiryLabel(expiryIso: string, todayIso: string): string {
  const [y, m, d] = expiryIso.split('-').map(Number)
  const name = `${d} ${MONTHS[m - 1]} ${y}`
  const days = daysBetween(todayIso, expiryIso)
  if (days < 0) return `${name} (expired)`
  if (days === 0) return `${name} (today)`
  return `${name} (+${days} ${days === 1 ? 'day' : 'days'})`
}

/** "2 s", "4 min", "3 h", "2 d". */
export function ageText(seconds: number): string {
  const s = Math.max(0, Math.floor(seconds))
  if (s < 60) return `${s} s`
  const m = Math.floor(s / 60)
  if (m < 60) return `${m} min`
  const h = Math.floor(m / 60)
  if (h < 48) return `${h} h`
  return `${Math.floor(h / 24)} d`
}

const SOURCE_LABELS: Record<string, string> = { dhan: 'Dhan', fyers: 'FYERS', truedata: 'TrueData' }

export function sourceLabel(key: string | null | undefined): string {
  if (!key) return 'unknown source'
  return SOURCE_LABELS[key.toLowerCase()] ?? key
}

export type FreshnessTone = 'live' | 'snapshot' | 'stale' | 'closed' | 'replay' | 'empty'

export interface Freshness {
  tone: FreshnessTone
  text: string
}

/** A snapshot this old while the market is open means the recorder is behind. */
export const SNAPSHOT_BEHIND_SECONDS = 180

/**
 * The line under the header that says how old every number on the page is.
 *
 * `nowMs` is the server's clock as the page believes it now (server time at the
 * response plus the time since it arrived), so a browser with a wrong clock
 * cannot make a stale chain read as live. A "live" answer whose newest quote
 * has since aged past the freshness limit — a poll that stopped coming back —
 * is reported as stale, never as live.
 */
export function describeFreshness(header: OptionChainHeader, nowMs: number): Freshness {
  const age = (iso: string | null) => (iso ? (nowMs - Date.parse(iso)) / 1000 : Infinity)
  const snapshot = header.snapshotCapturedUtc
  const source = sourceLabel(header.liveSourceKey ?? header.snapshotSourceKey)

  if (header.mode === 'replay') {
    return { tone: 'replay', text: `replay · snapshot ${istStamp(snapshot)} IST · ${sourceLabel(header.snapshotSourceKey)}` }
  }

  if (!snapshot && !header.liveOverlayUtc) {
    return { tone: 'empty', text: 'no capture recorded for this underlying yet' }
  }

  if (!header.marketOpen) {
    const last = snapshot ? `last capture ${istStamp(snapshot)} IST` : `last quote ${istStamp(header.liveOverlayUtc)} IST`
    return { tone: 'closed', text: `market closed — ${last} · ${sourceLabel(header.snapshotSourceKey ?? header.liveSourceKey)}` }
  }

  if (header.mode === 'live' && header.liveOverlayUtc) {
    const liveAge = age(header.liveOverlayUtc)
    if (liveAge <= header.freshSeconds) {
      const legs = header.totalLegs > 0 ? ` · ${header.liveLegs}/${header.totalLegs} legs live, rest from ${istTime(snapshot)} capture` : ''
      return { tone: 'live', text: `live · updated ${ageText(liveAge)} ago · ${source}${legs}` }
    }
    return { tone: 'stale', text: `stale — last live update ${ageText(liveAge)} ago (${istTime(header.liveOverlayUtc)} IST) · ${source}` }
  }

  const snapshotAge = age(snapshot)
  const behind = snapshotAge > SNAPSHOT_BEHIND_SECONDS
  return {
    tone: behind ? 'stale' : 'snapshot',
    text: `snapshot ${istTime(snapshot)} IST (${ageText(snapshotAge)} old) · ${sourceLabel(header.snapshotSourceKey)} · no live quotes${behind ? ' — the recorder may be behind' : ''}`,
  }
}

// --- build-up -------------------------------------------------------------------

export const BUILD_UP: Record<string, { label: string; className: string; meaning: string }> = {
  LongBuildUp: { label: 'Long build', className: 'oc-build-long', meaning: 'Price up, OI up — new buyers are opening positions.' },
  ShortBuildUp: { label: 'Short build', className: 'oc-build-short', meaning: 'Price down, OI up — new writers are selling.' },
  ShortCovering: { label: 'Short cover', className: 'oc-build-cover', meaning: 'Price up, OI down — writers are buying back.' },
  LongUnwinding: { label: 'Long unwind', className: 'oc-build-unwind', meaning: 'Price down, OI down — buyers are exiting.' },
}
