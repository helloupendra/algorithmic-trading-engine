/**
 * Backtesting module — helpers shared by the overview, the runs list, the
 * dialog and the run page: IST calendar-day arithmetic (a backtest is
 * bounded by IST dates, not UTC instants), session estimates from bar counts,
 * the status badge and the one-line run description.
 */

import { Badge } from '../../components/ui'
import { formatLots } from '../../lib/format'
import { resolutionLabel } from '../../lib/symbols'
import type { BacktestStatus } from '../../lib/types'
import { formatDay } from '../strategies/shared'

/* ------------------------------------------------------------- IST dates */

const IST_OFFSET_MS = 5.5 * 60 * 60 * 1000
const DAY_MS = 24 * 60 * 60 * 1000

/** "yyyy-MM-dd" of the IST calendar day a UTC instant falls on. */
export function istDate(iso: string | null | undefined): string | null {
  if (!iso) return null
  const t = new Date(iso).getTime()
  if (Number.isNaN(t)) return null
  return new Date(t + IST_OFFSET_MS).toISOString().slice(0, 10)
}

export function todayIst(): string {
  return istDate(new Date().toISOString())!
}

export function addDays(ymd: string, days: number): string {
  return new Date(Date.parse(`${ymd}T00:00:00Z`) + days * DAY_MS).toISOString().slice(0, 10)
}

/** Mon–Fri days in [from, to] inclusive; 0 when the range is empty. */
export function countWeekdays(from: string, to: string): number {
  const start = Date.parse(`${from}T00:00:00Z`)
  const end = Date.parse(`${to}T00:00:00Z`)
  if (Number.isNaN(start) || Number.isNaN(end) || end < start) return 0
  let n = 0
  for (let t = start; t <= end; t += DAY_MS) {
    const dow = new Date(t).getUTCDay()
    if (dow !== 0 && dow !== 6) n++
  }
  return n
}

/** "19 Aug → 3 Sep" for yyyy-MM-dd or ISO bounds. */
export function formatDayRange(from: string | null | undefined, to: string | null | undefined): string {
  return `${formatDay(from)} → ${formatDay(to)}`
}

/* ------------------------------------------------------- session estimate */

/** Full NSE session bar counts per resolution (09:15–15:30 IST). */
const BARS_PER_SESSION: Record<string, number> = { '1': 375, '5': 75, '15': 25, D: 1 }

/**
 * Sessions implied by a bar count at a resolution. The generic coverage
 * endpoint only reports bars; the per-underlying backtest coverage reports
 * exact sessions, so this is only used where that endpoint does not apply.
 */
export function estimateSessions(resolution: string, barCount: number): number {
  const per = BARS_PER_SESSION[resolution.toUpperCase().replace(/M$/, '')] ?? 75
  return Math.max(barCount > 0 ? 1 : 0, Math.ceil(barCount / per))
}

/* ------------------------------------------------------------- run status */

export function isActiveStatus(status: string | null | undefined): boolean {
  return status === 'Running' || status === 'Pending'
}

/** "Stop loss hit: P&L −5,120 ≤ −5,000" -> "Stop loss hit" for a badge; the full text goes in the title. */
function shortReason(reason: string): string {
  const cut = reason.indexOf(':')
  return cut > 0 ? reason.slice(0, cut) : reason
}

export function BacktestStatusBadge({
  status,
  progressPercent,
  stopReason,
}: {
  status: BacktestStatus | string
  progressPercent?: number | null
  stopReason?: string | null
}) {
  switch (status) {
    case 'Running':
      return (
        <Badge tone="live">
          running{progressPercent != null ? ` · ${Math.round(progressPercent)}%` : ''}
        </Badge>
      )
    case 'Pending':
      return <Badge tone="accent">pending</Badge>
    case 'Completed':
      // A stop-loss / target trip ends the replay early but is still a
      // Completed run (the runner posted its summary); the reason must show.
      return stopReason ? (
        <span title={stopReason}>
          <Badge tone="warn">Completed · {shortReason(stopReason)}</Badge>
        </span>
      ) : (
        <Badge tone="pos">Completed</Badge>
      )
    case 'Failed':
      return <Badge tone="neg">Failed</Badge>
    case 'Stopped':
      return <Badge tone="warn">Stopped{stopReason ? ` · ${stopReason}` : ''}</Badge>
    default:
      return <Badge>{status}</Badge>
  }
}

/** "BANKNIFTY · 5m · 19 Aug → 3 Sep · 2 lots × 30 = 60". */
export function runSpecLabel(run: {
  underlying: string
  resolution: string
  fromDate: string
  toDate: string
  lots: number
  lotSize?: number | null
}): string {
  return [
    run.underlying,
    resolutionLabel(run.resolution),
    formatDayRange(run.fromDate, run.toDate),
    formatLots(run.lots, run.lotSize ?? null),
  ].join(' · ')
}

/**
 * Keys the backend merges into a run's parametersJson that the dialog shows
 * as dedicated fields — they are stripped from the "Parameters" grid.
 */
export const RESERVED_PARAM_KEYS: ReadonlySet<string> = new Set([
  'lots',
  'stop_loss',
  'target',
  'underlying',
  'resolution',
  'eod_square_off_ist',
  'charges_per_lot',
  'initial_capital',
])
