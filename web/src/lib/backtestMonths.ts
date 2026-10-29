/**
 * A backtest's net P&L by calendar month, and the costs it was netted of.
 *
 * A position's P&L is booked in the IST month it closed — the same rule the
 * daily table uses for its IST day — so a leg opened on 31 Jan and closed on
 * 3 Feb is February's. The stored positions carry price P&L only (slippage,
 * when the run models it, is already in their fill prices); the run's own
 * charges are netted here, one position at a time, so a month is what that
 * month's trades kept.
 *
 * What can be netted depends on how the run was charged:
 *  - a flat amount per lot per fill: a closed position paid it on the lots it
 *    opened and again on the same lots closing, so its charges are
 *    2 × lots × the rate, booked with it;
 *  - the real costs (a `costs` block): the runner prices each fill's
 *    brokerage, STT, exchange, SEBI, stamp and GST from its turnover and
 *    stores only the total, so no month can be netted of them and the reading
 *    says so rather than inventing a split;
 *  - nothing: gross is net.
 */

import type { BacktestPosition, BacktestRunView } from './types'

/* ------------------------------------------------------------- the costs */

export type RunCosts =
  | { kind: 'none' }
  | { kind: 'flat'; perLot: number }
  | {
      kind: 'model'
      /** What the run's costs block set; null where it left the engine's default. */
      slippagePercent: number | null
      brokeragePerOrder: number | null
    }

function finite(value: unknown): number | null {
  const n = typeof value === 'string' && value.trim() !== '' ? Number(value) : value
  return typeof n === 'number' && Number.isFinite(n) ? n : null
}

/**
 * The way the runner read a `costs` block (backtest/rules.py): a non-empty
 * object, or anything truthy — true, "true", "yes", "1", a non-zero number —
 * turns the real costs on. Read that way here, because the runner ignores the
 * flat per-lot rate whenever they are on.
 */
function costsBlockOn(raw: unknown): boolean {
  if (raw == null) return false
  if (typeof raw === 'object') return Array.isArray(raw) ? raw.length > 0 : Object.keys(raw).length > 0
  if (typeof raw === 'string') return ['true', 'yes', '1'].includes(raw.trim().toLowerCase())
  return Boolean(raw)
}

/** How the run was charged, from its own parameters. */
export function readRunCosts(run: Pick<BacktestRunView, 'chargesPerLot' | 'parametersJson'>): RunCosts {
  let params: Record<string, unknown> = {}
  try {
    const parsed: unknown = JSON.parse(run.parametersJson || '{}')
    if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) params = parsed as Record<string, unknown>
  } catch {
    // An unreadable parameter string leaves the flat rate the API reported.
  }

  const block = params.costs
  if (costsBlockOn(block)) {
    const fields = block && typeof block === 'object' && !Array.isArray(block) ? (block as Record<string, unknown>) : {}
    return {
      kind: 'model',
      slippagePercent: finite(fields.slippage_percent ?? fields.slippage_pct),
      brokeragePerOrder: finite(fields.brokerage_per_order),
    }
  }
  return run.chargesPerLot > 0 ? { kind: 'flat', perLot: run.chargesPerLot } : { kind: 'none' }
}

/** "₹40 per lot per fill" / "real costs · 0.5% slippage a side" / "none". */
export function describeRunCosts(costs: RunCosts): string {
  switch (costs.kind) {
    case 'none':
      return 'none'
    case 'flat':
      return `₹${costs.perLot.toLocaleString('en-IN')} per lot per fill`
    case 'model': {
      const parts = [
        costs.slippagePercent != null ? `${costs.slippagePercent}% slippage a side` : null,
        costs.brokeragePerOrder != null ? `₹${costs.brokeragePerOrder.toLocaleString('en-IN')} brokerage an order` : null,
      ].filter(Boolean)
      return `real costs${parts.length > 0 ? ` · ${parts.join(' · ')}` : ' · engine defaults'}`
    }
  }
}

/* --------------------------------------------------------------- months */

const IST_OFFSET_MS = 330 * 60_000

/** "2026-01" for the IST calendar month a UTC instant falls in; null for no instant. */
export function istMonthOf(iso: string | null | undefined): string | null {
  if (!iso) return null
  const ms = Date.parse(iso)
  return Number.isNaN(ms) ? null : new Date(ms + IST_OFFSET_MS).toISOString().slice(0, 7)
}

export interface MonthCell {
  /** 1–12. */
  month: number
  /** After the charges that could be netted (see MonthlyReading.netted). */
  pnl: number
  /** Price P&L, before any charge. */
  gross: number
  charges: number
  /** Positions closed in the month. */
  trades: number
}

export interface MonthYear {
  year: number
  /** Index 0 is January. Null: no position closed that month. */
  months: Array<MonthCell | null>
  pnl: number
  gross: number
  charges: number
  trades: number
}

export interface MonthRef {
  year: number
  month: number
  pnl: number
  trades: number
}

export interface MonthlyReading {
  years: MonthYear[]
  /**
   * 'all': every closed position is net of its charges (or the run had none).
   * 'statutory-missing': the run charged the real costs, which are stored as a
   * total only — the months are before them.
   */
  netted: 'all' | 'statutory-missing'
  /** Charges booked into the months. */
  charges: number
  /** Positions still open: no close, so no month. */
  open: number
  /**
   * Closed positions whose size could not be read back from the fills (the
   * API sends 0 lots then): their P&L is in the months, their charges are not.
   */
  unsized: number
  best: MonthRef | null
  worst: MonthRef | null
}

/** A closed position's share of a flat per-lot rate: both its fills, on the lots it opened. */
export function flatPositionCharges(position: Pick<BacktestPosition, 'lots'>, perLot: number): number {
  return perLot > 0 && position.lots > 0 ? 2 * position.lots * perLot : 0
}

/**
 * Net P&L by year and month of the IST close. `range` (the run's IST days)
 * adds the years the run covered without a single close, so a dead year
 * reads as blank months rather than vanishing from the table.
 */
export function summariseMonths(
  positions: readonly BacktestPosition[] | null | undefined,
  costs: RunCosts,
  range?: { fromDate: string; toDate: string } | null,
): MonthlyReading {
  const cells = new Map<string, MonthCell & { year: number }>()
  let open = 0
  let unsized = 0
  let charges = 0

  for (const p of positions ?? []) {
    if (p.status !== 'Closed') {
      open += 1
      continue
    }
    const key = istMonthOf(p.closedUtc)
    if (!key) continue
    const year = Number(key.slice(0, 4))
    const month = Number(key.slice(5, 7))

    let charge = 0
    if (costs.kind === 'flat') {
      if (p.lots > 0) charge = flatPositionCharges(p, costs.perLot)
      else unsized += 1
    }
    charges += charge

    const cell = cells.get(key) ?? { year, month, pnl: 0, gross: 0, charges: 0, trades: 0 }
    cell.gross += p.pnl
    cell.charges += charge
    cell.pnl += p.pnl - charge
    cell.trades += 1
    cells.set(key, cell)
  }

  const yearSet = new Set<number>([...cells.values()].map((c) => c.year))
  const fromYear = Number(range?.fromDate?.slice(0, 4))
  const toYear = Number(range?.toDate?.slice(0, 4))
  if (Number.isInteger(fromYear) && Number.isInteger(toYear) && fromYear > 1900 && toYear >= fromYear && toYear - fromYear < 50) {
    for (let y = fromYear; y <= toYear; y++) yearSet.add(y)
  }

  const years: MonthYear[] = [...yearSet]
    .sort((a, b) => a - b)
    .map((year) => {
      const months = Array.from({ length: 12 }, (_, i) => {
        const cell = cells.get(`${year}-${String(i + 1).padStart(2, '0')}`)
        return cell ? { month: cell.month, pnl: cell.pnl, gross: cell.gross, charges: cell.charges, trades: cell.trades } : null
      })
      const traded = months.filter((m): m is MonthCell => m != null)
      return {
        year,
        months,
        pnl: traded.reduce((s, m) => s + m.pnl, 0),
        gross: traded.reduce((s, m) => s + m.gross, 0),
        charges: traded.reduce((s, m) => s + m.charges, 0),
        trades: traded.reduce((s, m) => s + m.trades, 0),
      }
    })

  // Best and worst among the months that traded; the earlier month wins a tie.
  let best: MonthRef | null = null
  let worst: MonthRef | null = null
  for (const y of years) {
    for (const m of y.months) {
      if (!m) continue
      const ref = { year: y.year, month: m.month, pnl: m.pnl, trades: m.trades }
      if (best == null || m.pnl > best.pnl) best = ref
      if (worst == null || m.pnl < worst.pnl) worst = ref
    }
  }

  return {
    years,
    netted: costs.kind === 'model' ? 'statutory-missing' : 'all',
    charges,
    open,
    unsized,
    best,
    worst,
  }
}

const MONTH_NAMES = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'] as const

/** "Mar 2025". */
export function monthLabel(year: number, month: number): string {
  return `${MONTH_NAMES[month - 1] ?? '?'} ${year}`
}

export const MONTH_HEADERS: readonly string[] = MONTH_NAMES
