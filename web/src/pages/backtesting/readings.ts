/**
 * Backtesting module — pure readings of what the coverage endpoint sends,
 * kept apart from the React files so they can be tested on their own: the
 * sessions a bar count implies, and the sessions a set of stored ranges
 * really holds once each index is counted a single time.
 */

import { toCandleResolution } from '../../lib/symbols'

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
