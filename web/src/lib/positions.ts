/**
 * Per-position derived values shown by the live and backtest position
 * tables: entry value, current value, and the premium move in points and
 * percent. The API sends them; an API build from before the risk-rules
 * change does not, so each falls back to the same arithmetic client-side —
 * a screen must never show "—" for a number it can compute from the row.
 */

import type { PositionStatus } from './types'

interface PositionLike {
  side: 'BUY' | 'SELL'
  status: PositionStatus
  quantity: number
  entryPrice: number
  /** LTP (live) or exit price (backtest, closed rows). */
  mark: number | null | undefined
  pnl: number
  entryValue?: number | null
  currentValue?: number | null
  pnlPoints?: number | null
  pnlPercent?: number | null
}

export interface PositionValues {
  /** entry × qty. */
  entryValue: number | null
  /** mark × qty, open rows only. */
  currentValue: number | null
  /** Signed premium points from entry (sign = profit). */
  pnlPoints: number | null
  /** pnlPoints / entry × 100. */
  pnlPercent: number | null
}

export function positionValues(p: PositionLike): PositionValues {
  const qty = p.quantity > 0 ? p.quantity : null
  const open = p.status === 'Open'
  const mark = p.mark ?? null

  const entryValue = p.entryValue ?? (qty != null ? p.entryPrice * qty : null)
  const currentValue = open ? (p.currentValue ?? (mark != null && qty != null ? mark * qty : null)) : null

  let pnlPoints = p.pnlPoints ?? null
  if (pnlPoints == null) {
    if (mark != null) pnlPoints = p.side === 'BUY' ? mark - p.entryPrice : p.entryPrice - mark
    // A carried leg was not sold: its move belongs to the manual book now, and
    // "+0.0 pts" here would read as a trade that went nowhere.
    else if (p.status === 'Closed' && qty != null) pnlPoints = p.pnl / qty
  }
  const pnlPercent =
    p.pnlPercent ?? (pnlPoints != null && p.entryPrice > 0 ? (pnlPoints / p.entryPrice) * 100 : null)

  return { entryValue, currentValue, pnlPoints, pnlPercent }
}

/**
 * The class of a position's row. A leg that is no longer open stays in the
 * table dimmed, so the open ones stand out: while another row is open, and
 * for as long as the run is going and can open one (by the open rows alone, a
 * run that is flat between two trades would flip its whole table to full
 * strength and back). Once the run is over with nothing open (a finished
 * backtest, a run stopped and squared off) there is nothing to stand out
 * from, and every row at 55% only made the whole table hard to read, so none
 * is dimmed.
 */
export function positionRowClass(open: boolean, anyOpen: boolean, runActive: boolean): string {
  return !open && (anyOpen || runActive) ? 'pos-row--closed' : ''
}

/** "+6.2 pts · +0.7%" — the muted line under a position's P&L; null when unknown. */
export function formatPnlMove(v: PositionValues): string | null {
  if (v.pnlPoints == null) return null
  const sign = (n: number) => (n > 0 ? '+' : n < 0 ? '−' : '')
  const pts = `${sign(v.pnlPoints)}${Math.abs(v.pnlPoints).toLocaleString('en-IN', {
    minimumFractionDigits: 1,
    maximumFractionDigits: 1,
  })} pts`
  if (v.pnlPercent == null) return pts
  const pct = `${sign(v.pnlPercent)}${Math.abs(v.pnlPercent).toFixed(1)}%`
  return `${pts} · ${pct}`
}
