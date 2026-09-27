/**
 * Trade → Orders: the day's orders across every run and manual book, newest
 * first. An admin sees every account's, with a filter to one; a trader,
 * their own (the API refuses another account's ledger).
 *
 * There is no orders-across-runs endpoint, so the page reads each run's own
 * ledger (GET /api/Strategy/runs/{id}/orders) and merges them (lib/orders.ts):
 * the day's runs, the manual books holding an open leg, and the viewer's own
 * book. The page says what that leaves out. The day is the Desk's: today, or
 * on a day without a session the last one with runs.
 *
 * It replaces the v1 Orders page, which showed one Simulator run picked from
 * a list.
 */

import { useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { useAuth } from '../../lib/auth'
import { dayLabel, isTradingRun, strategyLabel, underlyingShort } from '../../lib/desk'
import type { Scope } from '../../lib/desk'
import { formatPrice } from '../../lib/format'
import { MANUAL_BOOK, dayOrders, orderCounts, orderSources } from '../../lib/orders'
import type { OrderLine } from '../../lib/orders'
import { useManualBook, useOpenPositions, useRunsOrders } from '../../lib/queries'
import { InlineError } from '../../components/ui'
import { Chip, Swatch, Waiting } from '../desk/parts'
import { useNow, useShownDay } from '../desk/data'
import '../desk/desk.css'
import './trade.css'

/** Rows drawn at a time: a busy day holds thousands of orders. */
const PAGE = 200

const time = (iso: string) => new Date(iso).toLocaleTimeString('en-IN', { timeZone: 'Asia/Kolkata', hour12: false })

function statusTone(status: string): 'pos' | 'neg' | 'warn' | undefined {
  const s = status.toLowerCase()
  return s === 'filled' ? undefined : s === 'rejected' ? 'neg' : s === 'cancelled' ? 'warn' : undefined
}

function Row({ line, multi, toneOf }: { line: OrderLine; multi: boolean; toneOf: (id: number) => 1 | 2 | null }) {
  const { order: o, source: s } = line
  const run = s.isManualBook ? 'Manual book' : `${strategyLabel(s.strategyName)}${s.underlying ? ` · ${underlyingShort(s.underlying.toUpperCase())}` : ''}`
  return (
    <tr>
      <td className="dk-n tr-when" title={`Placed ${time(o.createdUtc)}${o.filledUtc ? `, filled ${time(o.filledUtc)}` : ''} IST`}>
        {time(line.atUtc)}
      </td>
      {multi && (
        <td className="tr-hide-s">
          <Swatch tone={toneOf(s.userId)} />
          <span className="dk-t2">{s.userName}</span>
        </td>
      )}
      <td className="tr-hide-s">
        <Link to={`/trade/runs/${s.runId}`} className="tr-run-link" title={`Run #${s.runId}`}>
          {run}
        </Link>
      </td>
      <td className="tr-leg">
        <span className="tr-contract" title={o.symbol}>
          {line.contract}
        </span>
        <span className="tr-sub dk-t3 tr-only-s">
          {run}
          {multi ? ` · ${s.userName}` : ''}
        </span>
      </td>
      <td>
        <Chip tone={o.side.toUpperCase() === 'BUY' ? 'pos' : 'neg'}>{o.side.toUpperCase()}</Chip>
      </td>
      <td className="r dk-n">{o.quantity.toLocaleString('en-IN')}</td>
      <td className="dk-t3 tr-hide-s">{o.orderType.toLowerCase()}</td>
      <td className="r dk-n tr-hide-s dk-t3">{o.requestedPrice != null ? formatPrice(o.requestedPrice) : 'market'}</td>
      <td className="r dk-n">{o.fillPrice != null ? formatPrice(o.fillPrice) : <span className="dk-t3">—</span>}</td>
      <td className="r">
        <Chip tone={statusTone(o.status)}>{o.status}</Chip>
      </td>
    </tr>
  )
}

export function OrdersPage() {
  const { user, isAdmin } = useAuth()
  const nowMs = useNow(60_000)
  const shownDay = useShownDay(nowMs, true)
  const { day, today } = shownDay
  const open = useOpenPositions(60_000)
  const book = useManualBook()
  const [scope, setScope] = useState<Scope>('all')
  const [shown, setShown] = useState(PAGE)

  const sources = useMemo(() => {
    const books = (open.data?.positions ?? [])
      .filter((p) => p.isManualBook)
      .map((p) => ({ runId: p.runId, userId: p.userId, userName: p.userName }))
    if (book.data?.runId != null && user) books.push({ runId: book.data.runId, userId: user.id, userName: user.userName })
    return orderSources((shownDay.runs ?? []).filter(isTradingRun), books)
  }, [shownDay.runs, open.data, book.data, user])

  // A day that is over never changes: only today's live ledgers are read again.
  const read = useRunsOrders(
    sources.map((s) => ({ runId: s.runId, live: s.live && day === today })),
    shownDay.runs !== undefined,
  )
  const { ledgers, loaded } = read
  const failed = sources.filter((_, i) => read.failed[i])

  const accounts = useMemo(() => {
    const seen = new Map<number, string>()
    for (const s of sources) if (!seen.has(s.userId)) seen.set(s.userId, s.userName)
    return [...seen.entries()].sort(([a], [b]) => a - b).map(([id, name]) => ({ id, name }))
  }, [sources])
  const scopeShown: Scope = scope !== 'all' && accounts.some((a) => a.id === scope) ? scope : 'all'
  const lines = useMemo(
    () => dayOrders(sources.map((source, i) => ({ source, orders: ledgers[i] })), day, scopeShown === 'all' ? null : scopeShown),
    [sources, ledgers, day, scopeShown],
  )
  const counts = orderCounts(lines)
  const multi = accounts.length > 1 && scopeShown === 'all'
  const toneOf = (id: number): 1 | 2 | null => {
    const i = accounts.findIndex((a) => a.id === id)
    return i === 0 ? 1 : i === 1 ? 2 : null
  }
  const books = sources.filter((s) => s.isManualBook).length
  const runs = sources.length - books

  return (
    <div className="page tr">
      <div className="tr-bar">
        {isAdmin && accounts.length > 1 && (
          <span className="dk-seg" role="group" aria-label="Accounts">
            <button type="button" aria-pressed={scopeShown === 'all'} onClick={() => setScope('all')}>
              All accounts
            </button>
            {accounts.map((a) => (
              <button key={a.id} type="button" aria-pressed={scopeShown === a.id} onClick={() => setScope(a.id)}>
                {a.name}
              </button>
            ))}
          </span>
        )}
        <span className="tr-summary">
          {day === today ? 'Today' : dayLabel(day)} · <b>{counts.orders.toLocaleString('en-IN')}</b> order{counts.orders === 1 ? '' : 's'} from {counts.runs} run
          {counts.runs === 1 ? '' : 's'}
          {counts.orders > 0 && (
            <span className="dk-t3">
              {' '}
              · {counts.filled.toLocaleString('en-IN')} filled
              {counts.notFilled > 0 && <span className="warn"> · {counts.notFilled} rejected or cancelled</span>}
              {counts.open > 0 && ` · ${counts.open} open`}
            </span>
          )}
        </span>
        <span className="tr-grow" />
        {sources.length > 0 && loaded < sources.length && (
          <span className="dk-t3 dk-xs">
            read {loaded} of {sources.length} ledgers…
          </span>
        )}
      </div>

      {day !== today && (
        <p className="dk-note" role="status">
          No session today: these are the orders of {dayLabel(day)}, the last day with runs.
        </p>
      )}
      {failed.length > 0 && (
        <p className="tr-stale" role="status">
          {failed.length} ledger{failed.length === 1 ? '' : 's'} could not be read ({failed.map((s) => `#${s.runId}`).join(', ')}): {failed.length === 1 ? 'its' : 'their'} orders are missing below.
        </p>
      )}

      {shownDay.error != null && shownDay.runs === undefined ? (
        <InlineError error={shownDay.error} />
      ) : shownDay.runs === undefined ? (
        <Waiting>Reading the day’s runs…</Waiting>
      ) : sources.length === 0 ? (
        <p className="tr-empty">No run traded on {day === today ? 'today' : dayLabel(day)}, and no manual book is open.</p>
      ) : lines.length === 0 && loaded < sources.length ? (
        <Waiting>Reading the ledgers…</Waiting>
      ) : lines.length === 0 ? (
        <p className="tr-empty">No order was placed on {day === today ? 'today' : dayLabel(day)}{scopeShown === 'all' ? '' : ' in this account'}.</p>
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
                    <th className="r">Qty</th>
                    <th className="tr-hide-s">Type</th>
                    <th className="r tr-hide-s">Asked</th>
                    <th className="r">Fill</th>
                    <th className="r">Status</th>
                  </tr>
                </thead>
                <tbody>
                  {lines.slice(0, shown).map((l) => (
                    <Row key={l.key} line={l} multi={multi} toneOf={toneOf} />
                  ))}
                </tbody>
              </table>
            </div>
            {lines.length > shown && (
              <div className="tr-more">
                <button type="button" onClick={() => setShown((n) => n + PAGE)}>
                  Show {Math.min(PAGE, lines.length - shown)} more of {(lines.length - shown).toLocaleString('en-IN')} older
                </button>
              </div>
            )}
          </div>
        </div>
      )}

      {sources.length > 0 && (
        <p className="dk-note tr-foot">
          Read run by run: the API has no orders-across-runs answer, so this page asks each of the day’s {runs} run{runs === 1 ? '' : 's'}
          {books > 0 ? ` and the ${books} ${MANUAL_BOOK.toLowerCase()} book${books === 1 ? '' : 's'} it knows of` : ''} for its own ledger. A manual book
          is known when it holds an open leg, or is yours; {isAdmin ? 'another account’s book with neither is not asked, so its orders today are not here.' : 'one with neither holds no order today.'}
        </p>
      )}
    </div>
  )
}
