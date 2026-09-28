/**
 * Trade → Orders: one day's orders across every run and manual book, newest
 * first (GET /api/Orders). An admin sees every account's, with a filter to
 * one; a trader, their own (the API scopes the answer). The orders the risk
 * gate refused (kill switch, order rate, daily loss) are listed too, with
 * its reason.
 *
 * The day, the account, the run and the status are kept in the URL, so a
 * view can be linked to. Without a date the day is the Desk's: today, or on
 * a day without a session the last one with runs. The list is read again
 * when an order, a fill or a risk trip arrives as a desk event (lib/live.ts);
 * the poll is the safety net, and only today's list is polled at all.
 *
 * It replaces the v1 Orders page, which showed one Simulator run picked from
 * a list, and this page's first version, which asked each of the day's runs
 * for its own ledger and found manual books through their open legs.
 */

import { useMemo } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { useAuth } from '../../lib/auth'
import { dayLabel } from '../../lib/desk'
import { formatPrice } from '../../lib/format'
import {
  dayOrderCounts,
  mergeOrderPages,
  orderAccounts,
  orderKey,
  priceSourceLabel,
  readOrdersFilter,
  runName,
  runOptionLabel,
  runOptions,
  sizeText,
  statusOptions,
  statusTone,
  writeOrdersFilter,
  ORDERS_PAGE,
} from '../../lib/orders'
import type { OrdersFilter } from '../../lib/orders'
import { useOrders } from '../../lib/queries'
import { runUserLabel } from '../../lib/runHistory'
import { formatContract } from '../../lib/symbols'
import type { OrderRow } from '../../lib/types'
import { DateField } from '../../components/DateField'
import { InlineError } from '../../components/ui'
import { Chip, Swatch, Waiting } from '../desk/parts'
import { useNow, useShownDay } from '../desk/data'
import '../desk/desk.css'
import './trade.css'

const time = (iso: string) => new Date(iso).toLocaleTimeString('en-IN', { timeZone: 'Asia/Kolkata', hour12: false })

function Row({ o, multi, toneOf }: { o: OrderRow; multi: boolean; toneOf: (id: number) => 1 | 2 | null }) {
  const run = runName(o)
  const who = runUserLabel(o.userName, o.userId)
  const source = priceSourceLabel(o.priceRule)
  const side = o.side?.toUpperCase() ?? null
  const size = sizeText(o)
  const units =
    o.quantity != null ? `${o.quantity.toLocaleString('en-IN')} units` : o.lots != null ? 'Lots; the lot size is not known' : 'The size was not recorded with this refusal'
  const when = o.kind === 'rejection' ? `Refused ${time(o.atUtc)} IST` : `Placed ${time(o.atUtc)}${o.filledUtc ? `, filled ${time(o.filledUtc)}` : ''} IST`
  return (
    <tr>
      <td className="dk-n tr-when" title={`${when}${o.clientSignalId ? ` · signal ${o.clientSignalId}` : ''}`}>
        {time(o.atUtc)}
      </td>
      {multi && (
        <td className="tr-hide-s">
          <Swatch tone={toneOf(o.userId)} />
          <span className="dk-t2">{who}</span>
        </td>
      )}
      <td className="tr-hide-s">
        <Link to={`/trade/runs/${o.runId}`} className="tr-run-link" title={`Run #${o.runId}`}>
          {run}
        </Link>
      </td>
      <td className="tr-leg">
        <span className="tr-contract" title={o.symbol}>
          {formatContract(o.symbol)}
        </span>
        <span className="tr-sub dk-t3 tr-only-s">
          {o.status.toLowerCase() !== 'filled' && <span className={statusTone(o.status)}>{o.status} · </span>}
          {run}
          {size ? ` · ${size}` : ''}
          {multi ? ` · ${who}` : ''}
        </span>
        {o.reason && <span className="tr-reason">{o.reason}</span>}
      </td>
      <td>{side ? <Chip tone={side === 'BUY' ? 'pos' : 'neg'}>{side}</Chip> : <span className="dk-t3">—</span>}</td>
      <td className="r dk-n tr-hide-s" title={units}>
        {size ?? <span className="dk-t3">—</span>}
      </td>
      <td className="r dk-n tr-hide-s dk-t3">{o.requestedPrice != null ? formatPrice(o.requestedPrice) : '—'}</td>
      <td className="r dk-n" title={o.priceNote ?? undefined}>
        {o.fillPrice != null ? formatPrice(o.fillPrice) : <span className="dk-t3">—</span>}
      </td>
      <td className={`tr-hide-s tr-priced ${o.staleQuote ? 'warn' : 'dk-t3'}`} title={o.priceNote ?? undefined}>
        {source ? (o.staleQuote ? `${source} · stale` : source) : ''}
      </td>
      <td className="r tr-hide-s">
        <Chip tone={statusTone(o.status)} title={o.reason ?? undefined}>
          {o.status}
        </Chip>
      </td>
    </tr>
  )
}

export function OrdersPage() {
  const { isAdmin } = useAuth()
  const [params, setParams] = useSearchParams()
  const filter = readOrdersFilter(params)
  const nowMs = useNow(60_000)
  // The Desk's day is only needed when the URL names none.
  const shownDay = useShownDay(nowMs, filter.date == null)
  const today = shownDay.today
  const date = filter.date ?? (shownDay.runs !== undefined || shownDay.error != null ? shownDay.day : null)
  const userId = isAdmin ? filter.userId : null

  const query = useOrders({ date, userId, runId: filter.runId, status: filter.status }, date === today)
  const pages = query.data?.pages
  const answer = pages?.[0]
  const rows = useMemo(() => mergeOrderPages(pages), [pages])
  const facets = useMemo(() => answer?.runs ?? [], [answer])

  const set = (change: Partial<OrdersFilter>) => setParams(writeOrdersFilter(params, change), { replace: true })

  const accounts = orderAccounts(facets)
  const picked = userId != null && !accounts.some((a) => a.id === userId) ? [{ id: userId, name: `user ${userId}`, tone: null }] : []
  const accountOptions = [...accounts, ...picked]
  const runsOffered = runOptions(facets, userId)
  const counts = dayOrderCounts(facets, userId, filter.runId)
  const statuses = statusOptions(answer?.statuses ?? [], filter.status)
  const multi = isAdmin && accounts.length > 1 && userId == null
  const toneOf = (id: number): 1 | 2 | null => accounts.find((a) => a.id === id)?.tone ?? null
  const dayText = date == null ? '' : date === today ? 'Today' : dayLabel(date)
  const narrowed = filter.runId != null || filter.status != null
  const older = answer ? answer.total - rows.length : 0

  return (
    <div className="page tr">
      <div className="tr-bar">
        {isAdmin && accountOptions.length > 1 && (
          <span className="dk-seg" role="group" aria-label="Accounts">
            <button type="button" aria-pressed={userId == null} onClick={() => set({ userId: null, runId: null })}>
              All accounts
            </button>
            {accountOptions.map((a) => (
              <button key={a.id} type="button" aria-pressed={userId === a.id} onClick={() => set({ userId: a.id, runId: null })}>
                {a.name}
              </button>
            ))}
          </span>
        )}
        <DateField
          className="field__input field__input--sm field__input--date"
          value={date ?? ''}
          max={today}
          onChange={(iso) => set({ date: iso || null, runId: null })}
          aria-label="Day"
          title="The IST day the orders were placed on"
        />
        <select
          className="field__input field__input--sm tr-run-filter"
          value={filter.runId ?? 'all'}
          onChange={(e) => set({ runId: e.target.value === 'all' ? null : Number(e.target.value) })}
          aria-label="Run"
        >
          <option value="all">All runs and books</option>
          {runsOffered.map((r) => (
            <option key={r.runId} value={r.runId}>
              {runOptionLabel(r, multi)}
            </option>
          ))}
          {filter.runId != null && !runsOffered.some((r) => r.runId === filter.runId) && <option value={filter.runId}>Run #{filter.runId} (no orders)</option>}
        </select>
        {statuses.length > 1 || filter.status != null ? (
          <span className="dk-seg" role="group" aria-label="Status">
            <button type="button" aria-pressed={filter.status == null} onClick={() => set({ status: null })}>
              All
            </button>
            {statuses.map((s) => (
              <button key={s} type="button" aria-pressed={filter.status?.toLowerCase() === s.toLowerCase()} onClick={() => set({ status: s })}>
                {s}
              </button>
            ))}
          </span>
        ) : null}
        <span className="tr-grow" />
        {query.isFetching && query.isPlaceholderData && <span className="dk-t3 dk-xs">reading…</span>}
      </div>

      {answer && date != null && (
        <div className="tr-bar">
          <span className="tr-summary">
            {dayText} · <b>{counts.orders.toLocaleString('en-IN')}</b> order{counts.orders === 1 ? '' : 's'}
            {filter.runId == null && ` from ${counts.runs} run${counts.runs === 1 ? '' : 's'}`}
            {counts.orders > 0 && (
              <span className="dk-t3">
                {' '}
                · {counts.filled.toLocaleString('en-IN')} filled
                {counts.rejected > 0 && <span className="neg"> · {counts.rejected.toLocaleString('en-IN')} rejected</span>}
                {counts.other > 0 && <span className="warn"> · {counts.other.toLocaleString('en-IN')} pending or cancelled</span>}
              </span>
            )}
          </span>
        </div>
      )}

      {filter.date == null && date != null && date !== today && (
        <p className="dk-note" role="status">
          No session today: these are the orders of {dayLabel(date)}, the last day with runs.
        </p>
      )}
      {query.isError && query.data && (
        <p className="tr-stale" role="status">
          The orders could not be read again just now; this is the last answer.
        </p>
      )}

      {date == null ? (
        <Waiting>Reading the day’s runs…</Waiting>
      ) : query.isError && !query.data ? (
        <InlineError error={query.error} />
      ) : !answer ? (
        <Waiting>Reading the day’s orders…</Waiting>
      ) : rows.length === 0 ? (
        <p className="tr-empty">
          No order {filter.status?.toLowerCase() === 'rejected' ? 'was refused' : 'was placed'} {date === today ? 'today' : `on ${dayLabel(date)}`}
          {narrowed || userId != null ? ' that matches these filters' : ''}.
        </p>
      ) : (
        <div className="tr-sheet">
          <div className="tr-acct">
            <div className="tr-scroll">
              <table className="dk-t tr-orders">
                <thead>
                  <tr>
                    <th>Time</th>
                    {multi && <th className="tr-hide-s">Account</th>}
                    <th className="tr-hide-s">Run</th>
                    <th>Contract</th>
                    <th>Side</th>
                    <th className="r tr-hide-s" title="Lots × lot size; the units are in the tooltip. A share's lot size is 1.">
                      Lots × size
                    </th>
                    <th className="r tr-hide-s" title="The price the runner or the ticket asked for; the fill can differ by the spread, or the move since">
                      Asked
                    </th>
                    <th className="r">Fill</th>
                    <th className="tr-hide-s" title="Where the fill's price came from: the bid or the ask, else the last trade across half the spread">
                      Priced at
                    </th>
                    <th className="r tr-hide-s">Status</th>
                  </tr>
                </thead>
                <tbody>
                  {rows.map((o) => (
                    <Row key={orderKey(o)} o={o} multi={multi} toneOf={toneOf} />
                  ))}
                </tbody>
              </table>
            </div>
            {query.hasNextPage && older > 0 && (
              <div className="tr-more">
                <button type="button" disabled={query.isFetchingNextPage} onClick={() => void query.fetchNextPage()}>
                  {query.isFetchingNextPage ? 'Reading…' : `Show ${Math.min(ORDERS_PAGE, older)} more of ${older.toLocaleString('en-IN')} older`}
                </button>
              </div>
            )}
          </div>
        </div>
      )}

      <p className="dk-note tr-foot">
        Every order booked on the day in the runs and manual books {isAdmin ? 'of every account' : 'of your account'}, and every order the risk gate
        refused (kill switch, order rate, daily loss) with its reason. A signal refused for a stale quote or a stopped run books nothing, so it is not
        here; the run’s log has it. Size is lots × lot size, the units in its tooltip. Priced at says where a fill’s price came from (its tooltip has
        the whole sentence), and a fill priced on a stale quote says so.
      </p>
    </div>
  )
}
