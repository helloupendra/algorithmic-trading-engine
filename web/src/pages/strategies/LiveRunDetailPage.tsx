/**
 * Strategies module — one live run from the history. The same RunCard the
 * Live runner shows (positions with qty-0 closed rows, risk chips and bars,
 * activity incl. RISK_UPDATED / RUN_STOPPED, runner output when retained)
 * plus the paper order ledger, under a "Started by <user> · run #id ·
 * <duration>" header. A running run shows the live card with Stop for an
 * admin or the user who started it; anyone else gets a read-only view (the
 * API answers 403 to a trader opening someone else's run).
 */

import { useEffect, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { useAuth } from '../../lib/auth'
import { useLiveRunOrders, useStrategies, useStrategyLive } from '../../lib/queries'
import { formatDateTime, formatDuration, formatNumber, formatPrice, formatTime } from '../../lib/format'
import { formatContract } from '../../lib/symbols'
import { runDurationSeconds, shortStopReason, stoppedByLabel } from '../../lib/runHistory'
import { Badge, InlineError, Loading } from '../../components/ui'
import { IconClock } from '../../components/icons'
import type { PaperOrderRow } from '../../lib/types'
import { CategoryBadge, Disclosure } from './shared'
import { RunCard } from './RunCard'

/* ---------------------------------------------------------- order ledger */

/** "Buy"/"BUY" → BUY; the badge tone follows the side. */
function sideLabel(side: string): 'BUY' | 'SELL' | string {
  const s = side.toUpperCase()
  return s === 'BUY' || s === 'SELL' ? s : side
}

function OrdersTable({ orders, lotSize }: { orders: PaperOrderRow[]; lotSize: number | null }) {
  if (orders.length === 0) return <p className="empty">No paper orders were placed in this run.</p>
  return (
    <div className="tablewrap" style={{ maxHeight: 360, overflowY: 'auto' }}>
      <table className="table">
        <thead>
          <tr>
            <th>Time</th>
            <th>Contract</th>
            <th>Side</th>
            <th className="r">Lots</th>
            <th className="r">Qty</th>
            <th className="r">Fill price</th>
            <th>Status</th>
            <th>Group</th>
          </tr>
        </thead>
        <tbody>
          {orders.map((o) => {
            const side = sideLabel(o.side)
            // PaperOrder.quantity is the leg's LOT count (the same unit the
            // position table above uses); contracts = lots × lot size.
            const lots = o.quantity
            const qty = lotSize != null && lotSize > 0 ? o.quantity * lotSize : null
            const filled = o.status.toLowerCase() === 'filled'
            return (
              <tr key={o.id}>
                <td className="muted" title={formatDateTime(o.filledUtc ?? o.createdUtc)}>
                  {formatTime(o.filledUtc ?? o.createdUtc)}
                </td>
                <td className="mono" title={o.symbol}>
                  {formatContract(o.symbol)}
                </td>
                <td>
                  <Badge tone={side === 'BUY' ? 'pos' : side === 'SELL' ? 'neg' : 'neutral'}>{side}</Badge>
                </td>
                <td className="r">{formatNumber(lots)}</td>
                <td className="r">{qty != null ? formatNumber(qty) : <span className="muted">—</span>}</td>
                <td className="r mono">
                  {o.fillPrice != null ? formatPrice(o.fillPrice) : <span className="muted">{o.requestedPrice != null ? `req ${formatPrice(o.requestedPrice)}` : '—'}</span>}
                </td>
                <td>
                  <Badge tone={filled ? 'pos' : 'neutral'}>{o.status}</Badge>
                  {o.orderType && <span className="cell-sub">{o.orderType}</span>}
                </td>
                <td className="mono muted">{o.groupId || '—'}</td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}

function OrdersDisclosure({ runId, isActive, lotSize }: { runId: number; isActive: boolean; lotSize: number | null }) {
  const [open, setOpen] = useState(false)
  // Fetched once opened; re-polled while the run is live so new fills appear.
  const orders = useLiveRunOrders(runId, isActive && open, open)
  const list = Array.isArray(orders.data) ? orders.data : []
  return (
    <Disclosure
      label={`Orders${orders.data ? ` (${list.length})` : ''}`}
      open={open}
      onToggle={() => setOpen((v) => !v)}
    >
      {orders.isPending && open ? (
        <Loading label="Loading orders…" />
      ) : orders.isError && orders.data === undefined ? (
        <InlineError error={orders.error} />
      ) : (
        <OrdersTable orders={list} lotSize={lotSize} />
      )}
    </Disclosure>
  )
}

/* -------------------------------------------------------------------- page */

export function LiveRunDetailPage({ basePath }: { basePath: '/admin/strategies' | '/trader/strategies' }) {
  const { runId: idParam } = useParams()
  const runId = idParam != null && /^\d+$/.test(idParam) ? Number(idParam) : null
  const { user, isAdmin } = useAuth()
  const live = useStrategyLive(runId ?? 0, runId != null)
  const strategies = useStrategies()

  // The header's duration ticks while the run is live.
  const [now, setNow] = useState(() => Date.now())
  const isActive = live.data?.isActive ?? false
  useEffect(() => {
    if (!isActive) return
    const t = setInterval(() => setNow(Date.now()), 1000)
    return () => clearInterval(t)
  }, [isActive])

  if (runId == null) {
    return (
      <div className="page">
        <InlineError error={new Error('That is not a run id.')} />
        <p>
          <Link to={`${basePath}/history`}>← Run history</Link>
        </p>
      </div>
    )
  }

  const view = live.data
  const catalogue = view ? (strategies.data ?? []).find((s) => s.id === view.strategyId) ?? null : null
  const strategy = { name: view?.name ?? `Run #${runId}`, category: catalogue?.category ?? null }
  const canControl = !!view && (isAdmin || (view.startedBy != null && view.startedBy === user?.userName))
  const duration = view
    ? runDurationSeconds(
        { durationSeconds: null, startedUtc: view.startedUtc, stoppedUtc: view.stoppedUtc, isActive: view.isActive },
        now,
      )
    : null
  const reason = shortStopReason(view?.stopReason)
  const by = stoppedByLabel(stoppedByFromReason(view?.stopReason))
  const historyLink = basePath === '/admin/strategies' ? '/admin/strategies/history' : '/trader/strategies/history'

  return (
    <div className="page">
      <header className="page__header">
        <div>
          <h1 className="page__title" style={{ display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap' }}>
            {view ? view.name : `Run #${runId}`}
            {catalogue && <CategoryBadge category={catalogue.category} />}
            {view && view.underlying && <Badge tone="accent">{view.underlying}</Badge>}
            {view && (view.isActive ? <Badge tone="live">running</Badge> : <Badge tone="warn">Stopped{reason ? ` · ${reason}` : ''}</Badge>)}
          </h1>
          <p className="page__subtitle">
            {view ? (
              <>
                Started by <b>{view.startedBy ?? 'unknown'}</b> · run #{runId} ·{' '}
                <span title={view.isActive ? 'still running' : 'total run time'}>{formatDuration(duration)}</span>
                <span className="faint">
                  {view.startedUtc ? ` · started ${formatDateTime(view.startedUtc)}` : ''}
                  {!view.isActive && view.stoppedUtc ? ` · stopped ${formatDateTime(view.stoppedUtc)}` : ''}
                  {!view.isActive && by ? ` by ${by}` : ''}
                </span>
              </>
            ) : live.isPending ? (
              'Loading run…'
            ) : (
              'This run could not be loaded.'
            )}
          </p>
        </div>
        <Link className="btn btn--sm" to={historyLink}>
          <IconClock style={{ width: 13, height: 13 }} /> Run history
        </Link>
      </header>

      {live.isError && !view && <InlineError error={live.error} />}
      {live.isError && view && (
        <p className="small-note warn" role="status" style={{ margin: 0 }}>
          Refresh failed — showing the last loaded view.
        </p>
      )}
      {view && !view.isActive && view.stopReason && (
        <div className="alert alert--warn" role="status">
          <span>
            <b>Stopped:</b> {view.stopReason}
          </span>
        </div>
      )}

      {(view || live.isPending) && (
        <RunCard strategy={strategy} runId={runId} run={null} exit={null} canControl={canControl}>
          <OrdersDisclosure runId={runId} isActive={isActive} lotSize={view?.lotSize ?? null} />
        </RunCard>
      )}

      {view && !canControl && view.isActive && (
        <p className="small-note">
          Read-only: only an admin or the user who started this run can stop it or change its risk rules.
        </p>
      )}
      <p className="small-note">
        <Link to={historyLink}>← All runs</Link>
      </p>
    </div>
  )
}

/** "Stopped by admin" → "admin"; other reasons carry no actor in the text. */
function stoppedByFromReason(reason: string | null | undefined): string | null {
  if (!reason) return null
  const m = /^Stopped by (.+?)(?:\s\(|:|;|$)/.exec(reason)
  return m ? m[1] : null
}
