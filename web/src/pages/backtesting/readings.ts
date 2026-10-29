/**
 * Backtesting module — pure readings of what the coverage endpoint sends,
 * kept apart from the React files so they can be tested on their own: the
 * sessions a bar count implies, the sessions a set of stored ranges really
 * holds once each index is counted a single time, and one index's ranges
 * folded into the single line the overview gives it; and the day labels the
 * run results carry.
 */

import { resolutionRank, toCandleResolution } from '../../lib/symbols'

/* ------------------------------------------------------- session estimate */

/** Minutes in a full NSE session (09:15–15:30 IST). */
const SESSION_MINUTES = 375

/**
 * Bars in a full session at a resolution, in any spelling ("1", "1m", "60",
 * "D", "1D"): one a day for daily bars, else the session's minutes over the
 * bar's, the last bar short (60 minutes: 7). A spelling that is neither
 * reads as five minutes.
 */
function barsPerSession(resolution: string): number {
  const r = toCandleResolution(resolution)
  if (r === 'D') return 1
  const minutes = Number(r)
  return Number.isInteger(minutes) && minutes > 0 ? Math.ceil(SESSION_MINUTES / minutes) : 75
}

/**
 * Sessions implied by a bar count at a resolution. The generic coverage
 * endpoint only reports bars; the per-underlying backtest coverage reports
 * exact sessions, so this is only used where that endpoint does not apply.
 */
export function estimateSessions(resolution: string, barCount: number): number {
  return Math.max(barCount > 0 ? 1 : 0, Math.ceil(barCount / barsPerSession(resolution)))
}

/* ------------------------------------------------------- sessions on hand */

/** The part of a coverage row the count needs. */
export interface StoredRange {
  symbol: string
  resolution: string
  barCount: number
}

/**
 * Sessions on hand across stored ranges, with each index counted once.
 *
 * The same trading days are stored at 1m, 5m, 15m and 1D, and again by the
 * live recorder, so adding every row up counts a day once per row: the
 * 28 Sep 2026 audit found ≈ 21,782 on the store as recorded on 27 Sep, for
 * about 5,450 real index-days (SENSEX's 1,274 days were in it four times
 * over, and its last 16 a fifth time from the live recorder). Each index
 * contributes the estimate of its best-covered resolution; `total` adds the
 * indices up, so its unit is index-days, and `longest` is the one index with
 * the most, which is how far back a single replay can reach.
 *
 * Still an estimate, hence the "≈" beside it. It can read a day or two high
 * as well as low: bars stored outside 09:15–15:30 pad a range (MIDCPNIFTY
 * read 11 for 9 weekdays in that recording), short sessions pool and read
 * low, the largest estimate wins, and two ranges of one index that cover
 * different days (a daily series that stops a week before the minute one)
 * are not added together. Rows of any symbol are counted, so the caller
 * passes index rows only.
 */
export function sessionsOnHand(rows: readonly StoredRange[]): { total: number; indices: number; longest: number } {
  const perIndex = new Map<string, number>()
  for (const r of rows) {
    const sessions = estimateSessions(r.resolution, r.barCount)
    if (sessions <= 0) continue
    const key = r.symbol.toUpperCase()
    perIndex.set(key, Math.max(perIndex.get(key) ?? 0, sessions))
  }
  let total = 0
  let longest = 0
  for (const n of perIndex.values()) {
    total += n
    longest = Math.max(longest, n)
  }
  return { total, indices: perIndex.size, longest }
}

/* ------------------------------------------------------- one index's line */

/** A stored range with the days it reaches: a coverage row. */
export interface StoredSpan extends StoredRange {
  fromUtc: string
  toUtc: string
}

/** "1m", "15m", "daily": a resolution as the overview's line names it. */
function spanLabel(resolution: string): string {
  const r = toCandleResolution(resolution)
  return r === 'D' ? 'daily' : /^\d+$/.test(r) ? `${r}m` : resolution
}

/**
 * The resolutions an index is stored at, finest to coarsest: "1m to daily",
 * "5m only". The stored 1-minute candles ("1") and the live recorder's
 * minute bars ("1m") are one resolution here; the detail rows tell them apart.
 */
export function resolutionSpan(resolutions: readonly string[]): string {
  const distinct = [...new Set(resolutions.map(toCandleResolution))].sort((a, b) => resolutionRank(a) - resolutionRank(b))
  if (distinct.length === 0) return ''
  if (distinct.length === 1) return `${spanLabel(distinct[0])} only`
  return `${spanLabel(distinct[0])} to ${spanLabel(distinct[distinct.length - 1])}`
}

/**
 * One index's stored ranges as the overview's one line: the sessions on hand
 * (its best-covered range, the count sessionsOnHand gives it), the
 * resolutions it is stored at, and the first and last day any range reaches.
 * The ranges themselves are the line's detail, shown when it is opened.
 * Null days with nothing stored. Rows of one index only.
 */
export function foldIndexCoverage(rows: readonly StoredSpan[]): {
  sessions: number
  resolutions: string
  fromUtc: string | null
  toUtc: string | null
} {
  const stored = rows.filter((r) => r.barCount > 0)
  let fromUtc: string | null = null
  let toUtc: string | null = null
  for (const r of stored) {
    if (fromUtc == null || Date.parse(r.fromUtc) < Date.parse(fromUtc)) fromUtc = r.fromUtc
    if (toUtc == null || Date.parse(r.toUtc) > Date.parse(toUtc)) toUtc = r.toUtc
  }
  return {
    sessions: sessionsOnHand(stored).longest,
    resolutions: resolutionSpan(stored.map((r) => r.resolution)),
    fromUtc,
    toUtc,
  }
}

/* ------------------------------------------------------------- day labels */

const IST_OFFSET_MS = 330 * 60_000

/**
 * "03 Aug 2025": a day of a result, which can lie in any year. An IST
 * "yyyy-MM-dd" is that day as written; an instant is the IST day it fell on,
 * whatever zone the browser is in.
 */
export function formatDayWithYear(value: string | null | undefined): string {
  if (!value) return '—'
  let ymd = /^\d{4}-\d{2}-\d{2}$/.test(value) ? value : null
  if (!ymd) {
    const ms = Date.parse(value)
    if (Number.isNaN(ms)) return '—'
    ymd = new Date(ms + IST_OFFSET_MS).toISOString().slice(0, 10)
  }
  return new Date(`${ymd}T00:00:00Z`).toLocaleDateString('en-IN', { day: '2-digit', month: 'short', year: 'numeric', timeZone: 'UTC' })
}
