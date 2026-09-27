/**
 * A day's orders across runs, as Trade → Orders reads them.
 *
 * The API has no orders-across-runs answer: each run has its own ledger
 * (GET /api/Strategy/runs/{id}/orders). So the page asks every run of the
 * day, plus the manual books it knows of, and merges the ledgers here. A
 * manual book opened on an earlier day is not in the day's run list; the
 * page knows the books that hold an open leg (GET /api/Positions/open) and
 * the viewer's own, and says that another account's book with neither is
 * not asked.
 */

import { runUserLabel } from './runHistory'
import { formatContract } from './symbols'
import { istDate } from './factors'
import type { LiveRunSummary, PaperOrderRow } from './types'

/** The manual book's strategy name on the server (ManualOrdersController.BookStrategyName). */
export const MANUAL_BOOK = 'Manual'

/** A run whose ledger the page reads. */
export interface OrderSource {
  runId: number
  userId: number
  userName: string
  strategyName: string
  underlying: string | null
  isManualBook: boolean
  /** Still trading: its ledger is read again while the page is open. */
  live: boolean
}

/**
 * The ledgers to read: every run of the day, then the manual books known
 * from elsewhere (open legs, the viewer's own) that the day's list does not
 * already hold. Each run once, in that order.
 */
export function orderSources(
  runs: readonly Pick<LiveRunSummary, 'runId' | 'userId' | 'userName' | 'strategyName' | 'underlying' | 'isActive'>[],
  books: ReadonlyArray<{ runId: number; userId: number; userName: string | null }>,
): OrderSource[] {
  const out = new Map<number, OrderSource>()
  for (const r of runs) {
    const book = r.strategyName === MANUAL_BOOK
    out.set(r.runId, {
      runId: r.runId,
      userId: r.userId,
      userName: runUserLabel(r.userName, r.userId),
      strategyName: r.strategyName,
      underlying: book ? null : r.underlying,
      isManualBook: book,
      live: r.isActive,
    })
  }
  for (const b of books) {
    if (out.has(b.runId)) continue
    // A book is open for good: its ledger can grow while the page is open.
    out.set(b.runId, { runId: b.runId, userId: b.userId, userName: runUserLabel(b.userName, b.userId), strategyName: MANUAL_BOOK, underlying: null, isManualBook: true, live: true })
  }
  return [...out.values()]
}

/** One order with the run it belongs to. */
export interface OrderLine {
  key: string
  order: PaperOrderRow
  source: OrderSource
  /** "NIFTY 23100 CE · 29 Sep", or the symbol without its exchange. */
  contract: string
  /** When it filled, else when it was placed. */
  atUtc: string
}

/**
 * The day's orders from every ledger read so far, newest first, narrowed to
 * one account when `userId` is set. A manual book's ledger spans many days;
 * only the orders placed on `day` (IST) are kept.
 */
export function dayOrders(
  ledgers: ReadonlyArray<{ source: OrderSource; orders: readonly PaperOrderRow[] | undefined }>,
  day: string,
  userId: number | null = null,
): OrderLine[] {
  const lines: OrderLine[] = []
  for (const { source, orders } of ledgers) {
    if (userId != null && source.userId !== userId) continue
    for (const order of orders ?? []) {
      if (istDate(new Date(order.createdUtc)) !== day) continue
      lines.push({ key: `${source.runId}-${order.id}`, order, source, contract: formatContract(order.symbol), atUtc: order.filledUtc ?? order.createdUtc })
    }
  }
  return lines.sort((a, b) => Date.parse(b.order.createdUtc) - Date.parse(a.order.createdUtc) || b.order.id - a.order.id)
}

export interface OrderCounts {
  orders: number
  filled: number
  /** Rejected or cancelled: asked for and not done. */
  notFilled: number
  /** Neither filled nor refused yet. */
  open: number
  runs: number
}

export function orderCounts(lines: readonly OrderLine[]): OrderCounts {
  const status = (l: OrderLine) => l.order.status.toLowerCase()
  const filled = lines.filter((l) => status(l) === 'filled').length
  const notFilled = lines.filter((l) => status(l) === 'rejected' || status(l) === 'cancelled').length
  return {
    orders: lines.length,
    filled,
    notFilled,
    open: lines.length - filled - notFilled,
    runs: new Set(lines.map((l) => l.source.runId)).size,
  }
}
