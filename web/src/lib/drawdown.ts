/**
 * The underwater curve of a backtest and its deepest fall.
 *
 * Each equity point is the account's balance, net of the charges booked so
 * far (the API's CurrentEquity). Under water is that balance minus the best
 * balance seen up to then: zero at a new high, negative while it is below one.
 *
 * The rules are the API's own (BacktestRunViewBuilder.BuildMetrics), so the
 * episode found here is the one behind the run's "Max drawdown" figure: the
 * running peak starts at the first point, the deepest fall is the first one
 * to reach its depth, and its percent is of the peak it fell from. The API
 * thins a long curve before sending it but keeps the points this reads —
 * the peak, the trough and the recovery of that fall — so the dates are the
 * stored ones, not an approximation.
 *
 * Two readings the metric alone does not give: when the fall began (the last
 * time the balance stood at that peak — a later equal high starts the clock
 * again) and when it was made good (the first balance back at or above the
 * peak, or never).
 */

import type { BacktestEquityPoint } from './types'

export interface UnderwaterPoint {
  atUtc: string
  equity: number
  /** The best balance up to and including this point. */
  peak: number
  /** equity − peak: 0 at a high, negative below one. */
  drawdown: number
  /** drawdown as a percent of the peak (0 when the peak is not positive). */
  drawdownPercent: number
}

export interface DrawdownEpisode {
  /** Rupees fallen from the peak to the trough, as a positive number. */
  amount: number
  /** Of the peak. */
  percent: number
  peakUtc: string
  peakEquity: number
  troughUtc: string
  troughEquity: number
  /** The first point back at or above the peak; null when the run ended below it. */
  recoveredUtc: string | null
}

/** Points in time order; a curve the API sent in order stays in its order. */
function ordered(points: readonly BacktestEquityPoint[]): BacktestEquityPoint[] {
  const stamped = points
    .map((p, i) => ({ p, i, t: Date.parse(p.atUtc) }))
    .filter((x) => !Number.isNaN(x.t) && Number.isFinite(x.p.equity))
  stamped.sort((a, b) => a.t - b.t || a.i - b.i)
  return stamped.map((x) => x.p)
}

/** The underwater series, one point per equity point. */
export function underwater(points: readonly BacktestEquityPoint[] | null | undefined): UnderwaterPoint[] {
  const out: UnderwaterPoint[] = []
  let peak = Number.NEGATIVE_INFINITY
  for (const p of ordered(points ?? [])) {
    if (p.equity > peak) peak = p.equity
    const drawdown = p.equity - peak
    out.push({
      atUtc: p.atUtc,
      equity: p.equity,
      peak,
      drawdown,
      drawdownPercent: peak > 0 ? (drawdown / peak) * 100 : 0,
    })
  }
  return out
}

/**
 * The deepest fall from a peak, with its dates. Null for an empty curve or
 * one that never went below a high it had reached.
 */
export function maxDrawdownEpisode(points: readonly BacktestEquityPoint[] | null | undefined): DrawdownEpisode | null {
  const curve = ordered(points ?? [])
  if (curve.length === 0) return null

  let peak = Number.NEGATIVE_INFINITY
  let peakIdx = 0
  let worst = 0
  let worstPeakIdx = -1
  let worstTroughIdx = -1
  for (let i = 0; i < curve.length; i++) {
    const equity = curve[i].equity
    // ">=" moves the start of the fall to the latest point at the peak; the
    // peak's value, and so every depth, is the same as with ">".
    if (equity >= peak) {
      peak = equity
      peakIdx = i
    }
    const fall = peak - equity
    if (fall > worst) {
      worst = fall
      worstPeakIdx = peakIdx
      worstTroughIdx = i
    }
  }
  if (worstTroughIdx < 0) return null

  const peakPoint = curve[worstPeakIdx]
  let recoveredUtc: string | null = null
  for (let i = worstTroughIdx + 1; i < curve.length; i++) {
    if (curve[i].equity >= peakPoint.equity) {
      recoveredUtc = curve[i].atUtc
      break
    }
  }

  return {
    amount: worst,
    percent: peakPoint.equity > 0 ? (worst / peakPoint.equity) * 100 : 0,
    peakUtc: peakPoint.atUtc,
    peakEquity: peakPoint.equity,
    troughUtc: curve[worstTroughIdx].atUtc,
    troughEquity: curve[worstTroughIdx].equity,
    recoveredUtc,
  }
}
