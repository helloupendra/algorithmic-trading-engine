/**
 * Backtesting module — Runs. Every OfflineReplay run, filterable by strategy,
 * underlying and status; running ones can be stopped, finished ones deleted,
 * and two to four can be ticked and compared side by side.
 */

import { useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { useBacktestRuns, useDeleteBacktest, useStopBacktest } from '../../lib/queries'
import { formatDateTime, formatNumber, formatPercent } from '../../lib/format'
import { resolutionLabel } from '../../lib/symbols'
import { InlineError, Panel, QueryBoundary } from '../../components/ui'
import { IconClock, IconPlus, IconStop, IconTrash } from '../../components/icons'
import type { BacktestRunSummary } from '../../lib/types'
import { togglePick } from '../../lib/backtestCompare'
import { hasOwnName, strategyName } from '../../lib/strategyNames'
import { PnlValue } from '../strategies/shared'
import { BacktestStatusBadge, CompareButton, ComparePick, formatDayRange, isActiveStatus } from './shared'

const STATUSES = ['Running', 'Pending', 'Completed', 'Failed', 'Stopped'] as const

function RunRow({ run, picked, onPick }: { run: BacktestRunSummary; picked: readonly number[]; onPick: (runId: number) => void }) {
  const stop = useStopBacktest()
  const remove = useDeleteBacktest()
  const active = isActiveStatus(run.status)

  function confirmStop() {
    if (window.confirm(`Stop backtest #${run.runId} (${strategyName(run.strategyName)} on ${run.underlying})? Open positions are squared off at the last mark.`))
      stop.mutate(run.runId)
  }
  function confirmDelete() {
    if (window.confirm(`Delete backtest #${run.runId} and all its positions, orders and equity points? This cannot be undone.`))
      remove.mutate(run.runId)
  }

  return (
    <>
      <tr>
        <ComparePick runId={run.runId} picked={picked} onToggle={onPick} />
        {/* Who started it and when sit under the run number: as a column of their own they pushed the row's
            buttons out of the panel on a 1440 screen. */}
        <td className="muted" title={run.completedUtc ? `finished ${formatDateTime(run.completedUtc)}` : undefined}>
          <span className="mono">#{run.runId}</span>
          <span className="cell-sub">
            {run.startedBy ? `${run.startedBy} · ` : ''}
            {formatDateTime(run.startedUtc ?? run.createdUtc)}
          </span>
        </td>
        <td className="bt-runs__name" title={run.strategyName}>
          <Link to={`/research/backtests/runs/${run.runId}`}>
            <b>{strategyName(run.strategyName)}</b>
          </Link>
          {hasOwnName(run.strategyName) && <span className="cell-sub mono">{run.strategyName}</span>}
        </td>
        <td className="mono">{run.underlying}</td>
        <td className="muted">
          {formatDayRange(run.fromDate, run.toDate)}
          <span className="cell-sub">{resolutionLabel(run.resolution)} candles</span>
        </td>
        <td className="r">{formatNumber(run.lots)}</td>
        <td className="r muted">
          {run.stopLoss != null ? `−${formatNumber(run.stopLoss)}` : '—'} / {run.target != null ? `+${formatNumber(run.target)}` : '—'}
        </td>
        <td className="r">
          <PnlValue value={run.netPnl} />
        </td>
        <td className="r">
          {formatNumber(run.trades)}
          {run.trades > 0 && <span className="cell-sub">{formatPercent(run.winRatePercent, 0).replace('+', '')} won</span>}
        </td>
        <td>
          <BacktestStatusBadge status={run.status} progressPercent={run.progressPercent} stopReason={run.stopReason} stacked />
        </td>
        <td className="r bt-runs__act">
          {active ? (
            <button
              type="button"
              className="btn btn--danger btn--sm"
              disabled={stop.isPending}
              onClick={confirmStop}
              title="Stop this backtest"
            >
              <IconStop style={{ width: 12, height: 12 }} /> {stop.isPending ? 'Stopping…' : 'Stop'}
            </button>
          ) : (
            <button
              type="button"
              className="btn btn--ghost btn--sm"
              disabled={remove.isPending}
              onClick={confirmDelete}
              title="Delete this run and its results"
              aria-label={`Delete run ${run.runId}`}
            >
              <IconTrash style={{ width: 13, height: 13 }} />
            </button>
          )}
        </td>
      </tr>
      {(stop.isError || remove.isError) && (
        <tr>
          <td colSpan={13}>
            <InlineError error={stop.error ?? remove.error} />
          </td>
        </tr>
      )}
    </>
  )
}

export function BacktestRunsPage() {
  const runs = useBacktestRuns()
  const [strategy, setStrategy] = useState('all')
  const [underlying, setUnderlying] = useState('all')
  const [status, setStatus] = useState('all')
  const [picked, setPicked] = useState<number[]>([])

  const list = useMemo(() => runs.data ?? [], [runs.data])
  const strategyNames = useMemo(() => [...new Set(list.map((r) => r.strategyName))].sort(), [list])
  const underlyings = useMemo(() => [...new Set(list.map((r) => r.underlying))].sort(), [list])

  const filtered = list.filter(
    (r) =>
      (strategy === 'all' || r.strategyName === strategy) &&
      (underlying === 'all' || r.underlying === underlying) &&
      (status === 'all' || r.status === status),
  )

  return (
    <div className="page">
      <header className="page__header">
        <div>
          <h1 className="page__title">Backtest runs</h1>
          <p className="page__subtitle">
            Every replay, newest first. Results survive restarts — delete what you no longer need.
          </p>
        </div>
        <Link className="btn btn--primary" to="/research/backtests/new">
          <IconPlus style={{ width: 14, height: 14 }} /> New backtest
        </Link>
      </header>

      <Panel
        title={
          <>
            <IconClock /> Runs
          </>
        }
        actions={
          <div className="filter-row">
            <select
              className="field__input field__input--sm"
              value={strategy}
              onChange={(e) => setStrategy(e.target.value)}
              aria-label="Filter by strategy"
            >
              <option value="all">All strategies</option>
              {strategyNames.map((n) => (
                <option key={n} value={n}>
                  {hasOwnName(n) ? `${strategyName(n)} (${n})` : n}
                </option>
              ))}
            </select>
            <select
              className="field__input field__input--sm"
              value={underlying}
              onChange={(e) => setUnderlying(e.target.value)}
              aria-label="Filter by underlying"
            >
              <option value="all">All underlyings</option>
              {underlyings.map((u) => (
                <option key={u} value={u}>
                  {u}
                </option>
              ))}
            </select>
            <select
              className="field__input field__input--sm"
              value={status}
              onChange={(e) => setStatus(e.target.value)}
              aria-label="Filter by status"
            >
              <option value="all">All statuses</option>
              {STATUSES.map((s) => (
                <option key={s} value={s}>
                  {s}
                </option>
              ))}
            </select>
            <span className="small-note" style={{ margin: 0 }}>
              {formatNumber(filtered.length)} of {formatNumber(list.length)}
            </span>
            <CompareButton picked={picked} onClear={() => setPicked([])} />
          </div>
        }
      >
        <QueryBoundary query={runs} empty="No backtests yet — start one from New backtest.">
          {() =>
            filtered.length === 0 ? (
              <p className="empty">Nothing matches these filters.</p>
            ) : (
              <div className="tablewrap">
                <table className="table bt-runs">
                  <thead>
                    <tr>
                      <th className="bt-pick" aria-label="Compare" />
                      <th>Run · started</th>
                      <th>Strategy</th>
                      <th>Underlying</th>
                      <th>Range</th>
                      <th className="r">Lots</th>
                      <th className="r">SL / target</th>
                      <th className="r">Net P&L</th>
                      <th className="r">Trades</th>
                      <th>Status</th>
                      <th className="bt-runs__act" aria-label="Actions" />
                    </tr>
                  </thead>
                  <tbody>
                    {filtered.map((run) => (
                      <RunRow key={run.runId} run={run} picked={picked} onPick={(id) => setPicked((was) => togglePick(was, id))} />
                    ))}
                  </tbody>
                </table>
              </div>
            )
          }
        </QueryBoundary>
      </Panel>
    </div>
  )
}
