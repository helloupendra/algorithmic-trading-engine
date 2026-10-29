/**
 * Backtests side by side: which runs the link names, the figures each run's
 * own data supports, and what makes two runs not like for like.
 *
 * Every figure here is one the run page already shows, read the same way:
 * net P&L, trades, win rate, the averages, profit factor and max drawdown
 * are the API's (BacktestRunViewBuilder), and the best and worst months come
 * from the same monthly reading as the run page's table. Nothing is
 * recomputed differently for the comparison, so a run reads the same here
 * as on its own page.
 */

import { describeRunCosts, readRunCosts, summariseMonths, type MonthRef, type RunCosts } from './backtestMonths'
import type { BacktestEquityPoint, BacktestRunView } from './types'

/** More than four lines on one chart stop being tellable apart. */
export const MAX_COMPARE = 4
export const MIN_COMPARE = 2

/**
 * The run ids of a compare link (`?ids=12,15,17`): positive integers, each
 * once, in the order given, at most four. `dropped` counts the ids past the
 * fourth so the page can say they were left out.
 */
export function parseCompareIds(raw: string | null | undefined): { ids: number[]; dropped: number } {
  const seen = new Set<number>()
  for (const part of (raw ?? '').split(',')) {
    const text = part.trim()
    if (!/^\d+$/.test(text)) continue
    const id = Number(text)
    if (id > 0 && Number.isSafeInteger(id)) seen.add(id)
  }
  const all = [...seen]
  return { ids: all.slice(0, MAX_COMPARE), dropped: Math.max(0, all.length - MAX_COMPARE) }
}

export function compareHref(ids: readonly number[]): string {
  return `/research/backtests/compare?ids=${ids.join(',')}`
}

/** What a profit factor can honestly be: a ratio, "no losses" (∞), or nothing to divide. */
export type ProfitFactor = number | 'no-losses' | null

export interface RunStats {
  runId: number
  strategyName: string
  underlying: string
  resolution: string
  fromDate: string
  toDate: string
  status: BacktestRunView['status']
  stopReason: string | null
  /** IST days the replay wrote equity for (the API's tradingDays). */
  sessions: number
  initialCapital: number
  lots: number
  lotSize: number
  costs: RunCosts
  costsLabel: string
  /** After the run's own charges (the API's pnl.total). */
  netPnl: number
  returnPercent: number
  charges: number
  trades: number
  /** Null when nothing closed. */
  winRatePercent: number | null
  /** Null when there was no win / no loss to average. */
  averageWin: number | null
  averageLoss: number | null
  profitFactor: ProfitFactor
  maxDrawdown: number
  maxDrawdownPercent: number
  bestMonth: MonthRef | null
  worstMonth: MonthRef | null
  /** The months are before the real costs, which are stored as a run total only. */
  monthsBeforeStatutory: boolean
}

export function runStats(view: BacktestRunView): RunStats {
  const m = view.metrics
  const costs = readRunCosts(view)
  const months = summariseMonths(view.positions, costs)
  return {
    runId: view.runId,
    strategyName: view.strategyName,
    underlying: view.underlying,
    resolution: view.resolution,
    fromDate: view.fromDate,
    toDate: view.toDate,
    status: view.status,
    stopReason: view.stopReason,
    sessions: m.tradingDays,
    initialCapital: view.initialCapital,
    lots: view.lots,
    lotSize: view.lotSize,
    costs,
    costsLabel: describeRunCosts(costs),
    netPnl: view.pnl.total,
    returnPercent: view.pnl.returnPercent,
    charges: view.pnl.charges,
    trades: m.closedPositions,
    winRatePercent: m.closedPositions > 0 ? m.winRatePercent : null,
    averageWin: m.winning > 0 ? m.averageWin : null,
    averageLoss: m.losing > 0 ? Math.abs(m.averageLoss) : null,
    // The API sends 0 for "no loss to divide by"; that is not a factor of 0.
    profitFactor: m.grossLoss !== 0 ? m.profitFactor : m.grossProfit > 0 ? 'no-losses' : null,
    maxDrawdown: Math.abs(m.maxDrawdownAmount),
    maxDrawdownPercent: Math.abs(m.maxDrawdownPercent),
    bestMonth: months.best,
    worstMonth: months.worst,
    monthsBeforeStatutory: months.netted === 'statutory-missing',
  }
}

export type CompareFlagKind =
  | 'period'
  | 'underlying'
  | 'resolution'
  | 'costs'
  | 'capital'
  | 'lots'
  | 'running'
  | 'failed'
  | 'ended-early'
  | 'no-trades'

export interface CompareFlag {
  kind: CompareFlagKind
  text: string
}

function differ<T>(runs: readonly RunStats[], read: (r: RunStats) => T): boolean {
  const keys = new Set(runs.map((r) => JSON.stringify(read(r))))
  return keys.size > 1
}

/** "#12 NIFTY, #15 BANKNIFTY" — every run with its own value, so the reader sees which is which. */
function each(runs: readonly RunStats[], show: (r: RunStats) => string): string {
  return runs.map((r) => `#${r.runId} ${show(r)}`).join('; ')
}

/**
 * What stands between these runs and a like-for-like reading, most
 * fundamental first. `day` formats an IST "yyyy-MM-dd" for the text.
 */
export function compareFlags(runs: readonly RunStats[], day: (ymd: string) => string = (d) => d): CompareFlag[] {
  const flags: CompareFlag[] = []
  if (runs.length < 2) return flags

  if (differ(runs, (r) => [r.fromDate, r.toDate])) {
    flags.push({
      kind: 'period',
      text: `Different periods — ${each(runs, (r) => `${day(r.fromDate)} → ${day(r.toDate)}`)}. The curves and totals cover different markets.`,
    })
  }
  if (differ(runs, (r) => r.underlying)) {
    flags.push({ kind: 'underlying', text: `Different underlyings — ${each(runs, (r) => r.underlying)}.` })
  }
  if (differ(runs, (r) => r.resolution)) {
    flags.push({ kind: 'resolution', text: `Different bar sizes — ${each(runs, (r) => r.resolution)}.` })
  }
  if (differ(runs, (r) => r.costsLabel)) {
    flags.push({
      kind: 'costs',
      text: `Charged differently — ${each(runs, (r) => r.costsLabel)}. Net figures are not like for like.`,
    })
  }
  if (differ(runs, (r) => r.initialCapital)) {
    flags.push({
      kind: 'capital',
      text: `Different starting capital — ${each(runs, (r) => `₹${r.initialCapital.toLocaleString('en-IN')}`)}. Read the percent figures, or start the curves at 0.`,
    })
  }
  if (differ(runs, (r) => [r.lots, r.lotSize])) {
    flags.push({ kind: 'lots', text: `Different sizes — ${each(runs, (r) => `${r.lots} × ${r.lotSize}`)}. Rupee figures scale with size.` })
  }

  const running = runs.filter((r) => r.status === 'Running' || r.status === 'Pending')
  if (running.length > 0) {
    flags.push({ kind: 'running', text: `${running.map((r) => `#${r.runId}`).join(', ')} still replaying — its figures will change.` })
  }
  const failed = runs.filter((r) => r.status === 'Failed')
  if (failed.length > 0) {
    flags.push({
      kind: 'failed',
      text: `${failed.map((r) => `#${r.runId}`).join(', ')} failed — its figures cover only what it replayed before it stopped.`,
    })
  }
  const early = runs.filter((r) => (r.status === 'Completed' || r.status === 'Stopped') && r.stopReason)
  if (early.length > 0) {
    flags.push({ kind: 'ended-early', text: `Ended before the end of its range — ${each(early, (r) => r.stopReason ?? '')}.` })
  }
  const idle = runs.filter((r) => r.trades === 0)
  if (idle.length > 0) {
    flags.push({ kind: 'no-trades', text: `${idle.map((r) => `#${r.runId}`).join(', ')} closed no trade — there is no rate or average to compare.` })
  }
  return flags
}

export type CurveBase = 'balance' | 'zero'

export interface CurvePoint {
  atUtc: string
  value: number
}

/**
 * A run's equity for the overlay: the balance itself, or the P&L since its
 * own start (balance − its starting capital), so runs that started with
 * different capital all leave from 0.
 */
export function curveValues(points: readonly BacktestEquityPoint[], initialCapital: number, base: CurveBase): CurvePoint[] {
  return points.map((p) => ({ atUtc: p.atUtc, value: base === 'zero' ? p.equity - initialCapital : p.equity }))
}
