/**
 * Two readings a backtest's numbers do not give on their own: how the account
 * itself fared, and how each year did.
 *
 * A total P&L says nothing about whether the account could have survived to
 * collect it. The lowest the equity ever went, and the day it went there, is
 * what says that — and a run whose equity crosses zero did not "lose money", it
 * ran out of it and the rest of the curve is fiction.
 */

import type { BacktestDailyPnl, BacktestEquityPoint } from './types'

export interface AccountReading {
  /** Where the account started. */
  initialCapital: number
  /** The lowest balance it ever showed, and when. */
  lowest: number
  lowestAtUtc: string | null
  /** Where it ended. */
  final: number
  /** The day the balance first went to zero or below, if it ever did. */
  ranOutAtUtc: string | null
  /** The deepest fall from a peak, in rupees and percent of that peak. */
  drawdown: number
  drawdownPercent: number
}

/** The account's own story over the run: balance = capital + P&L at that point. */
export function readAccount(
  points: BacktestEquityPoint[] | null | undefined,
  initialCapital: number,
): AccountReading | null {
  if (!points || points.length === 0 || !(initialCapital > 0)) return null

  let lowest = Number.POSITIVE_INFINITY
  let lowestAtUtc: string | null = null
  let ranOutAtUtc: string | null = null
  let peak = initialCapital
  let drawdown = 0
  let drawdownPercent = 0
  let final = initialCapital

  for (const point of points) {
    const balance = initialCapital + point.equity
    final = balance
    if (balance < lowest) {
      lowest = balance
      lowestAtUtc = point.atUtc
    }
    if (balance <= 0 && ranOutAtUtc === null) ranOutAtUtc = point.atUtc
    if (balance > peak) peak = balance
    const fall = peak - balance
    if (fall > drawdown) {
      drawdown = fall
      drawdownPercent = peak > 0 ? (fall / peak) * 100 : 0
    }
  }

  return {
    initialCapital,
    lowest: Number.isFinite(lowest) ? lowest : initialCapital,
    lowestAtUtc,
    final,
    ranOutAtUtc,
    drawdown,
    drawdownPercent,
  }
}

export interface YearRow {
  year: number
  pnl: number
  trades: number
  days: number
  winningDays: number
}

/**
 * P&L by calendar year of the IST days the run traded. A strategy that made its
 * money in one year and lost in the others is a different animal from one that
 * made a little every year, and the total hides which it is.
 */
export function summariseYears(daily: BacktestDailyPnl[] | null | undefined): YearRow[] {
  const byYear = new Map<number, YearRow>()
  for (const day of daily ?? []) {
    const year = Number(day.date.slice(0, 4))
    if (!Number.isFinite(year)) continue
    const row = byYear.get(year) ?? { year, pnl: 0, trades: 0, days: 0, winningDays: 0 }
    row.pnl += day.pnl
    row.trades += day.trades
    row.days += 1
    if (day.pnl > 0) row.winningDays += 1
    byYear.set(year, row)
  }
  return [...byYear.values()].sort((a, b) => a.year - b.year)
}
