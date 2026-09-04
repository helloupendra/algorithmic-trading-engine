/**
 * Backtesting module — one run. Header with the spec and status, a progress
 * block while the runner is replaying (the sections below fill in as results
 * arrive), metric tiles, the equity curve and daily P&L charts, the
 * position-based table (closed legs stay as rows with qty 0), and the
 * activity / data notes / runner output disclosures. A failed run shows its
 * error first.
 */

import { useMemo, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import {
  useBacktestLogs,
  useBacktestRun,
  useDeleteBacktest,
  useStopBacktest,
  useStrategies,
} from '../../lib/queries'
import {
  formatDateTime,
  formatInrSigned,
  formatInrWhole,
  formatNumber,
  formatPercent,
  formatPrice,
} from '../../lib/format'
import { formatContract, resolutionLabel } from '../../lib/symbols'
import { positionValues } from '../../lib/positions'
import { describeRiskRules, effectiveRisk, isRiskEmpty } from '../../lib/risk'
import { Badge, InlineError, Loading, Panel, StatTile } from '../../components/ui'
import { IconActivity, IconFlask, IconLayers, IconPlay, IconStop, IconTrash } from '../../components/icons'
import type { BacktestPosition, BacktestRunView } from '../../lib/types'
import {
  ActivityList,
  CategoryBadge,
  ConsoleOutput,
  Disclosure,
  PnlValue,
  PositionPnlCell,
  PositionValueCell,
  formatDay,
} from '../strategies/shared'
import { BacktestDialog } from './BacktestDialog'
import { DailyPnlChart, EquityCurveChart } from './charts'
import { BacktestStatusBadge, formatDayRange, isActiveStatus, runSpecLabel } from './shared'

/* ---------------------------------------------------------- positions table */

function contractLabel(p: BacktestPosition): string {
  return p.contract?.label || formatContract(p.symbol)
}

function PositionsTable({ positions }: { positions: BacktestPosition[] }) {
  return (
    <div className="tablewrap tablewrap--tall">
      <table className="table">
        <thead>
          <tr>
            <th>Contract</th>
            <th>Side</th>
            <th className="r">Lots</th>
            <th className="r">Lot size</th>
            <th className="r">Qty</th>
            <th className="r">Entry</th>
            <th className="r">Exit</th>
            <th className="r" title="Entry premium × quantity; open rows also show the value at the last mark">
              Value
            </th>
            <th className="r">P&L</th>
            <th>Exit reason</th>
            <th>Status</th>
            <th>Opened</th>
            <th>Closed</th>
          </tr>
        </thead>
        <tbody>
          {positions.map((p) => {
            const open = p.status === 'Open'
            const values = positionValues({ ...p, mark: p.exitPrice })
            return (
              <tr key={p.id} className={open ? '' : 'pos-row--closed'}>
                <td className="mono" title={`${p.symbol} · group ${p.groupId}`}>
                  {contractLabel(p)}
                </td>
                <td>
                  <Badge tone={p.side === 'BUY' ? 'pos' : 'neg'}>{p.side}</Badge>
                </td>
                <td className="r">{open ? formatNumber(p.lots) : 0}</td>
                <td className="r muted">{formatNumber(p.lotSize)}</td>
                <td className="r">{open ? formatNumber(p.quantity) : 0}</td>
                <td className="r mono">{formatPrice(p.entryPrice)}</td>
                <td className="r mono">{p.exitPrice != null ? formatPrice(p.exitPrice) : <span className="muted">—</span>}</td>
                <PositionValueCell values={values} open={open} />
                <PositionPnlCell pnl={p.pnl} values={values} />
                <td className="muted" style={{ whiteSpace: 'normal', minWidth: 160 }} title={p.exitReason ?? undefined}>
                  {p.exitReason ?? (open ? '' : '—')}
                </td>
                <td>{open ? <Badge tone="accent">Open</Badge> : <Badge>Closed</Badge>}</td>
                <td className="muted">{formatDateTime(p.openedUtc)}</td>
                <td className="muted">{p.closedUtc ? formatDateTime(p.closedUtc) : '—'}</td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}

/* ---------------------------------------------------------------- progress */

function ProgressBlock({ view }: { view: BacktestRunView }) {
  const p = view.progress
  const pct = Math.max(0, Math.min(100, p?.percent ?? 0))
  return (
    <div className="bt-progress" role="status" aria-live="polite">
      <div className="bt-progress__row">
        <span className="pulse-dot" aria-hidden="true" />
        <span className="bt-progress__pct">{pct.toFixed(0)}%</span>
        <span>
          {p ? `${formatNumber(p.barsProcessed)} / ${formatNumber(p.totalBars)} bars` : 'starting the runner…'}
        </span>
        <span>{p?.currentUtc ? `at ${formatDateTime(p.currentUtc)} IST` : ''}</span>
        <span>{p ? `${formatNumber(p.trades)} ${p.trades === 1 ? 'trade' : 'trades'} so far` : ''}</span>
        {p?.message && <span className="faint">{p.message}</span>}
      </div>
      <div
        className="progress progress--lg"
        role="progressbar"
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={Math.round(pct)}
        aria-label="Backtest progress"
      >
        <div className="progress__bar" style={{ width: `${pct}%` }} />
      </div>
    </div>
  )
}

/* ------------------------------------------------------------------- page */

export function BacktestRunPage() {
  const { id: idParam } = useParams()
  const id = idParam != null && /^\d+$/.test(idParam) ? Number(idParam) : null
  const navigate = useNavigate()
  const run = useBacktestRun(id)
  const view = run.data
  const active = isActiveStatus(view?.status)
  // Live while the runner is alive; one fetch of the retained snapshot for a
  // failed run (the API keeps the output of the most recently finished runs).
  const logs = useBacktestLogs(id, active || view?.status === 'Failed', active)
  const strategies = useStrategies()
  const stop = useStopBacktest()
  const remove = useDeleteBacktest()

  const [showActivity, setShowActivity] = useState(false)
  const [showNotes, setShowNotes] = useState(true)
  const [showOutput, setShowOutput] = useState(false)
  const [showDaily, setShowDaily] = useState(false)
  const [rerun, setRerun] = useState(false)

  const strategy = useMemo(
    () => (view ? (strategies.data ?? []).find((s) => s.id === view.strategyId) ?? null : null),
    [strategies.data, view],
  )
  const logLines = Array.isArray(logs.data) ? logs.data : []

  if (id == null) {
    return (
      <div className="page">
        <InlineError error={new Error('That is not a run id.')} />
      </div>
    )
  }

  function confirmStop() {
    if (!view) return
    const open = view.positions.filter((p) => p.status === 'Open').length
    if (window.confirm(`Stop backtest #${view.runId}? ${open} open position${open === 1 ? '' : 's'} will be squared off at the last mark.`))
      stop.mutate(view.runId)
  }
  function confirmDelete() {
    if (!view) return
    if (window.confirm(`Delete backtest #${view.runId} and all its results? This cannot be undone.`))
      remove.mutate(view.runId, { onSuccess: () => navigate('/admin/backtesting/runs') })
  }

  const m = view?.metrics
  const openCount = view?.positions.filter((p) => p.status === 'Open').length ?? 0
  const pnlTone = (v: number | undefined) => (v == null || v === 0 ? undefined : v > 0 ? 'pos' : 'neg')
  // The rules the replay ran under: the three-level object when the API sends
  // it, else the overall shorthands of an older build / older run.
  const risk = view ? effectiveRisk(view) : null
  const riskLine = risk && !isRiskEmpty(risk) ? describeRiskRules(risk) : null

  return (
    <div className="page">
      <header className="page__header">
        <div>
          <h1 className="page__title" style={{ display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap' }}>
            {view ? view.strategyName : `Backtest #${id}`}
            {strategy && <CategoryBadge category={strategy.category} />}
            {view && (
              <BacktestStatusBadge
                status={view.status}
                progressPercent={view.progress?.percent}
                stopReason={view.stopReason}
              />
            )}
          </h1>
          <p className="page__subtitle">
            {view ? (
              <>
                {runSpecLabel(view)}
                {riskLine ? <span title="Risk rules of this run"> · {riskLine}</span> : ' · no risk rules'}
                <span className="faint">
                  {' '}
                  · run #{view.runId}
                  {view.startedUtc ? ` · started ${formatDateTime(view.startedUtc)}` : ''}
                  {view.completedUtc ? ` · finished ${formatDateTime(view.completedUtc)}` : ''}
                </span>
              </>
            ) : (
              'Loading run…'
            )}
          </p>
        </div>
        {view && (
          <div className="run-card__actions">
            {active ? (
              <button type="button" className="btn btn--danger btn--sm" disabled={stop.isPending} onClick={confirmStop}>
                <IconStop style={{ width: 13, height: 13 }} /> {stop.isPending ? 'Stopping…' : 'Stop'}
              </button>
            ) : (
              <button type="button" className="btn btn--ghost btn--sm" disabled={remove.isPending} onClick={confirmDelete}>
                <IconTrash style={{ width: 13, height: 13 }} /> {remove.isPending ? 'Deleting…' : 'Delete'}
              </button>
            )}
            <button
              type="button"
              className="btn btn--primary btn--sm"
              disabled={!strategy}
              title={strategy ? 'Open the dialog pre-filled with this run' : 'The strategy is no longer in the catalogue'}
              onClick={() => setRerun(true)}
            >
              <IconPlay style={{ width: 13, height: 13 }} /> Run again
            </button>
          </div>
        )}
      </header>

      {run.isPending && <Loading label="Loading run…" />}
      {run.isError && !view && <InlineError error={run.error} />}
      {run.isError && view && (
        <p className="small-note warn" role="status" style={{ margin: 0 }}>
          Refresh failed — showing the last loaded results.
        </p>
      )}
      {stop.isError && <InlineError error={stop.error} />}
      {remove.isError && <InlineError error={remove.error} />}

      {view && (
        <>
          {view.status === 'Failed' && (
            <div className="alert alert--error alert--stack" role="alert">
              <span>
                <b>Backtest failed:</b> {view.lastError || 'the runner exited without a reason.'}
              </span>
              {logLines.length > 0 && (
                <pre className="alert__lines">{logLines.slice(-12).join('\n')}</pre>
              )}
            </div>
          )}
          {view.status === 'Stopped' && view.stopReason && (
            <div className="alert alert--warn" role="status">
              <span>Stopped: {view.stopReason}</span>
            </div>
          )}
          {view.status === 'Completed' && view.stopReason && (
            <div className="alert alert--warn" role="status">
              <span>
                <b>Ended early:</b> {view.stopReason} — every open position was squared off at that bar and the
                rest of the range was not replayed.
              </span>
            </div>
          )}

          {active && <ProgressBlock view={view} />}

          <div className="metric-grid">
            <StatTile
              label="Net P&L"
              value={<PnlValue value={view.pnl.total} />}
              tone={pnlTone(view.pnl.total)}
              sub={`${formatPercent(view.pnl.returnPercent)} on ${formatInrWhole(view.initialCapital)}${view.pnl.charges > 0 ? ` · charges ${formatInrWhole(view.pnl.charges)}` : ''}`}
            />
            <StatTile
              label="Trades (closed)"
              value={m ? formatNumber(m.closedPositions) : '—'}
              sub={openCount > 0 ? `${openCount} open` : `realized ${formatInrSigned(view.pnl.realized)}`}
            />
            <StatTile
              label="Win rate"
              value={m && m.closedPositions > 0 ? `${m.winRatePercent.toFixed(0)}%` : '—'}
              sub={m ? `${formatNumber(m.winning)} W / ${formatNumber(m.losing)} L` : ''}
            />
            <StatTile
              label="Profit factor"
              value={m && m.grossLoss !== 0 ? m.profitFactor.toFixed(2) : m && m.grossProfit > 0 ? '∞' : '—'}
              sub={m ? `${formatInrSigned(m.grossProfit)} / ${formatInrSigned(-Math.abs(m.grossLoss))}` : ''}
            />
            <StatTile
              label="Max drawdown"
              value={m ? formatInrSigned(-Math.abs(m.maxDrawdownAmount)) : '—'}
              tone={m && m.maxDrawdownAmount !== 0 ? 'neg' : undefined}
              sub={m ? `${Math.abs(m.maxDrawdownPercent).toFixed(2)}% of equity` : ''}
            />
            <StatTile
              label="Avg win / avg loss"
              value={m ? `${formatInrSigned(m.averageWin)} / ${formatInrSigned(-Math.abs(m.averageLoss))}` : '—'}
              sub={m ? `expectancy ${formatInrSigned(m.expectancy)} per trade` : ''}
            />
            <StatTile
              label="Largest win / loss"
              value={m ? `${formatInrSigned(m.largestWin)} / ${formatInrSigned(-Math.abs(m.largestLoss))}` : '—'}
            />
            <StatTile
              label="Profitable days"
              value={m ? `${formatNumber(m.profitableDays)} / ${formatNumber(m.tradingDays)}` : '—'}
              sub="of sessions replayed · a day counts when its closed P&L is positive"
            />
            {view.pnl.capitalUsed != null && (
              <StatTile
                label="Capital used"
                value={formatInrWhole(view.pnl.capitalUsed)}
                sub={`peak margin over the run · of ${formatInrWhole(view.initialCapital)}`}
              />
            )}
          </div>

          <div className="two-col">
            <Panel
              title={
                <>
                  <IconActivity /> Equity curve
                </>
              }
              actions={<span className="muted" style={{ fontSize: 12 }}>{formatNumber(view.equityCurve.length)} points · IST</span>}
            >
              <EquityCurveChart
                points={view.equityCurve}
                initialCapital={view.initialCapital}
                fitKey={`${view.runId}|${view.status}`}
                follow={active}
              />
            </Panel>
            <Panel
              title={
                <>
                  <IconFlask /> Daily P&L
                </>
              }
              actions={<span className="muted" style={{ fontSize: 12 }}>per IST day</span>}
            >
              <DailyPnlChart days={view.daily} />
              {view.daily.length > 0 && (
                <Disclosure label="Daily table" open={showDaily} onToggle={() => setShowDaily((v) => !v)}>
                  <div className="tablewrap" style={{ maxHeight: 240, overflowY: 'auto' }}>
                    <table className="table">
                      <thead>
                        <tr>
                          <th>Day</th>
                          <th className="r">P&L</th>
                          <th className="r">Trades</th>
                        </tr>
                      </thead>
                      <tbody>
                        {view.daily.map((d) => (
                          <tr key={d.date}>
                            <td className="mono">{formatDay(d.date)}</td>
                            <td className="r">
                              <PnlValue value={d.pnl} />
                            </td>
                            <td className="r">{formatNumber(d.trades)}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                </Disclosure>
              )}
            </Panel>
          </div>

          <Panel
            title={
              <>
                <IconLayers /> Positions
              </>
            }
            actions={
              <span className="muted" style={{ fontSize: 12 }}>
                {formatNumber(view.positions.length)} rows · {openCount} open · lot size {view.lotSize}
                {view.lotSizeSource !== 'master' ? ` (${view.lotSizeSource})` : ''}
                {view.eodSquareOffIst ? ` · EOD square-off ${view.eodSquareOffIst} IST` : ' · no EOD square-off'}
              </span>
            }
          >
            {view.positions.length > 0 ? (
              <PositionsTable positions={view.positions} />
            ) : active ? (
              <div className="waiting" role="status">
                <span className="pulse-dot" aria-hidden="true" />
                <span>
                  Replaying {view.underlying} {resolutionLabel(view.resolution)} bars — no entry yet
                  {view.progress?.currentUtc ? ` (at ${formatDateTime(view.progress.currentUtc)})` : ''}.
                </span>
              </div>
            ) : (
              <p className="empty">No positions were opened during this backtest.</p>
            )}
          </Panel>

          <Panel
            title={
              <>
                <IconFlask /> Run log
              </>
            }
          >
            <Disclosure
              label={`Data notes (${view.dataNotes.length})`}
              open={showNotes}
              onToggle={() => setShowNotes((v) => !v)}
            >
              {view.dataNotes.length === 0 ? (
                <p className="empty">Nothing was skipped and no data caveat was recorded.</p>
              ) : (
                <ul className="data-notes">
                  {view.dataNotes.map((n, i) => (
                    <li key={i}>{n}</li>
                  ))}
                </ul>
              )}
            </Disclosure>
            <Disclosure
              label={`Activity (${view.activity.length})`}
              open={showActivity}
              onToggle={() => setShowActivity((v) => !v)}
            >
              <ActivityList items={view.activity} showDate />
            </Disclosure>
            <Disclosure label="Runner output" open={showOutput} onToggle={() => setShowOutput((v) => !v)}>
              <ConsoleOutput
                lines={logLines}
                title="Backtest runner output"
                placeholder={
                  active
                    ? 'No output yet — the runner prints a [CONFIG] line at startup and progress as it replays.'
                    : 'The runner has exited — its output is kept for the most recently finished runs only, and nothing was retained for this one.'
                }
              />
            </Disclosure>
          </Panel>

          <p className="small-note">
            Range {formatDayRange(view.fromDate, view.toDate)} in IST days · fills at the option candle
            close of the signal bar · P&L = Δprice × lots × lot size · risk rules checked every bar, leg
            (closes that leg) → group (closes that group) → overall (ends the run).{' '}
            <Link to="/admin/backtesting/runs">All runs</Link>
          </p>
        </>
      )}

      {rerun && view && strategy && (
        <BacktestDialog
          strategy={strategy}
          initial={{
            underlying: view.underlying,
            resolution: view.resolution,
            fromDate: view.fromDate,
            toDate: view.toDate,
            lots: view.lots,
            stopLoss: view.stopLoss,
            target: view.target,
            risk: view.risk ?? null,
            eodSquareOffIst: view.eodSquareOffIst ?? '',
            chargesPerLot: view.chargesPerLot,
            initialCapital: view.initialCapital,
            parametersJson: view.parametersJson,
          }}
          onClose={() => setRerun(false)}
          onStarted={(response) => navigate(`/admin/backtesting/runs/${response.runId}`)}
        />
      )}
    </div>
  )
}
