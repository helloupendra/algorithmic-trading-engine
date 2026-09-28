/**
 * Trade → Orders, apart from the drawing: the query GET /api/Orders is asked
 * with, the filters the page keeps in its URL, the day's counts it states,
 * and how a row's price and size read.
 *
 * The API answers one IST day of orders across every run and manual book the
 * viewer may see (a trader their own, an admin every account's or one), the
 * orders the risk gate refused among them, a page at a time. Until 28 Sep
 * there was no such read: the page asked each of the day's runs for its own
 * ledger and found manual books through their open legs, so another
 * account's book with nothing open was never asked, and a refusal was
 * nowhere.
 */

import { deskAccounts, strategyLabel, underlyingShort } from './desk'
import type { DeskAccount } from './desk'
import type { OrderRow, OrderRunFacet, OrderStatusCount, OrdersResponse } from './types'

/** The manual book's strategy name on the server (ManualOrdersController.BookStrategyName). */
export const MANUAL_BOOK = 'Manual'

/** Rows asked for at a time; the API gives at most 500. */
export const ORDERS_PAGE = 200

/** What the page narrows the day to; null is not narrowed. */
export interface OrdersFilter {
  /** yyyy-MM-dd (IST); null for the Desk's day. */
  date: string | null
  /** One account (an admin's pick; the API gives a trader their own whatever is asked). */
  userId: number | null
  runId: number | null
  /** 'Filled', 'Rejected', ...; null for every status. */
  status: string | null
}

/** The URL's names for the filters: ?date=&user=&run=&status= */
const PARAM: Record<keyof OrdersFilter, string> = { date: 'date', userId: 'user', runId: 'run', status: 'status' }

const DAY = /^\d{4}-\d{2}-\d{2}$/

function positiveId(raw: string | null): number | null {
  if (raw == null || !/^\d{1,15}$/.test(raw)) return null
  const n = Number(raw)
  return n > 0 ? n : null
}

/** The filters in the page's URL; anything malformed reads as not narrowed rather than as an error. */
export function readOrdersFilter(params: URLSearchParams): OrdersFilter {
  const date = params.get(PARAM.date)
  const status = params.get(PARAM.status)
  return {
    date: date && DAY.test(date) ? date : null,
    userId: positiveId(params.get(PARAM.userId)),
    runId: positiveId(params.get(PARAM.runId)),
    status: status && /^[A-Za-z]{1,20}$/.test(status) ? status : null,
  }
}

/** The URL's params with `change` applied: null clears a filter; every other param is kept. */
export function writeOrdersFilter(params: URLSearchParams, change: Partial<OrdersFilter>): URLSearchParams {
  const next = new URLSearchParams(params)
  for (const field of Object.keys(PARAM) as (keyof OrdersFilter)[]) {
    if (!(field in change)) continue
    const value = change[field]
    if (value == null || value === '') next.delete(PARAM[field])
    else next.set(PARAM[field], String(value))
  }
  return next
}

/** GET /api/Orders's query string: the day, the filters that narrow, and the page. */
export function ordersQuery(q: { date: string; userId?: number | null; runId?: number | null; status?: string | null; skip?: number; take?: number }): string {
  const p = new URLSearchParams({ date: q.date })
  if (q.userId != null) p.set('userId', String(q.userId))
  if (q.runId != null) p.set('runId', String(q.runId))
  if (q.status) p.set('status', q.status)
  if (q.skip) p.set('skip', String(q.skip))
  p.set('take', String(q.take ?? ORDERS_PAGE))
  return `?${p}`
}

/** A row's identity: an order and a rejection are kept in different tables and can share an id. */
export function orderKey(row: Pick<OrderRow, 'kind' | 'id'>): string {
  return `${row.kind}-${row.id}`
}

/**
 * The pages read so far as one list, newest first. An order placed while
 * the next page is read pushes every row down one, so a row can come back
 * at the top of the next page: it is kept once.
 */
export function mergeOrderPages(pages: readonly Pick<OrdersResponse, 'orders'>[] | undefined): OrderRow[] {
  const seen = new Set<string>()
  const rows: OrderRow[] = []
  for (const page of pages ?? []) {
    for (const row of page.orders) {
      const key = orderKey(row)
      if (seen.has(key)) continue
      seen.add(key)
      rows.push(row)
    }
  }
  return rows
}

/** The accounts with a row on the day, in the Desk's order and colours. */
export function orderAccounts(runs: readonly OrderRunFacet[]): DeskAccount[] {
  return deskAccounts(runs)
}

/** The runs the run filter offers: one account's, or every account's. */
export function runOptions(runs: readonly OrderRunFacet[], userId: number | null): OrderRunFacet[] {
  return userId == null ? [...runs] : runs.filter((r) => r.userId === userId)
}

/** "Ghost · NIFTY", "Manual book": a run as the page names it. */
export function runName(run: Pick<OrderRow, 'strategyName' | 'underlying' | 'isManualBook'>): string {
  if (run.isManualBook) return 'Manual book'
  return `${strategyLabel(run.strategyName)}${run.underlying ? ` · ${underlyingShort(run.underlying.toUpperCase())}` : ''}`
}

/** The run filter's option: "Ghost · NIFTY · #612 (14)", with the account in front when several are listed. */
export function runOptionLabel(run: OrderRunFacet, withAccount: boolean): string {
  const who = withAccount ? `${run.userName && run.userName.trim() ? run.userName : `user ${run.userId}`} · ` : ''
  return `${who}${runName(run)} · #${run.runId} (${run.orders.toLocaleString('en-IN')})`
}

export interface DayOrderCounts {
  /** Rows, rejections included. */
  orders: number
  filled: number
  rejected: number
  /** Neither: an order still pending, or cancelled. */
  other: number
  runs: number
}

/** The day's counts for what is shown: one account and one run, or all of them. */
export function dayOrderCounts(runs: readonly OrderRunFacet[], userId: number | null, runId: number | null): DayOrderCounts {
  const shown = runOptions(runs, userId).filter((r) => runId == null || r.runId === runId)
  const sum = (f: (r: OrderRunFacet) => number) => shown.reduce((n, r) => n + f(r), 0)
  const orders = sum((r) => r.orders)
  const filled = sum((r) => r.filled)
  const rejected = sum((r) => r.rejected)
  return { orders, filled, rejected, other: orders - filled - rejected, runs: shown.length }
}

/** The statuses the status filter offers: every one the day holds, Filled first and Rejected last, and the one picked even on a day without it. */
export function statusOptions(statuses: readonly OrderStatusCount[], picked: string | null): string[] {
  const names = statuses.map((s) => s.status)
  if (picked && !names.some((n) => n.toLowerCase() === picked.toLowerCase())) names.push(picked)
  const rank = (s: string) => (s === 'Filled' ? 0 : s === 'Rejected' ? 2 : 1)
  return names.sort((a, b) => rank(a) - rank(b) || a.localeCompare(b))
}

export function statusTone(status: string): 'neg' | 'warn' | undefined {
  const s = status.toLowerCase()
  return s === 'rejected' ? 'neg' : s === 'cancelled' || s === 'pending' ? 'warn' : undefined
}

const BASIS: Record<string, string> = { ltp: 'LTP', signal: 'signal', mark: 'mark', entry: 'entry' }

/**
 * A fill's price source in a few words, from the rule the API recorded
 * (docs/modules/strategies_module.md §11): "bid", "ask", "LTP − ½ spread",
 * "mark + ½ spread", or a price filled as given ("as asked", "last mark").
 * Null for an order booked before fills recorded it; the rule itself for a
 * code this does not know yet, rather than nothing.
 */
export function priceSourceLabel(rule: string | null | undefined): string | null {
  if (!rule) return null
  if (rule === 'bid' || rule === 'ask') return rule
  const crossed = /^([a-z]+)-(less|plus)-half-spread$/.exec(rule)
  if (crossed) return `${BASIS[crossed[1]] ?? crossed[1]} ${crossed[2] === 'less' ? '−' : '+'} ½ spread`
  const given: Record<string, string> = { signal: 'as asked', ltp: 'last trade', mark: 'last mark', entry: 'entry price' }
  return given[rule] ?? rule
}

/** "2 × 75": lots by lot size, the Positions page's form; the lots alone when the lot size is unknown. */
export function sizeText(row: Pick<OrderRow, 'lots' | 'lotSize'>): string | null {
  if (row.lots == null) return null
  return row.lotSize != null ? `${row.lots.toLocaleString('en-IN')} × ${row.lotSize.toLocaleString('en-IN')}` : row.lots.toLocaleString('en-IN')
}
