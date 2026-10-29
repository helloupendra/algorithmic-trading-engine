/**
 * Backtesting module — Compare. Two to four runs named in the address
 * (`?ids=12,15,17`, so a comparison can be shared): their equity curves on
 * one time axis, as balances or each from 0, the fall from each one's best
 * under them, and their figures side by side. Every figure is the one the
 * run's own page shows (lib/backtestCompare.ts), and whatever keeps the runs
 * from being like for like — a different period, underlying, bar size,
 * costs, capital or size, a run still replaying — is said above them.
 *
 * Each run keeps the colour of its place in the address. Runs are added at
 * the end, so a run already on the chart never changes colour.
 */

import { useMemo } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import { useBacktestRuns, useBacktestRunViews } from '../../lib/queries'
import {
  MAX_COMPARE,
  MIN_COMPARE,
  compareFlags,
  compareHref,
  parseCompareIds,
  runStats,
  type CurveBase,
  type RunStats,
} from '../../lib/backtestCompare'
import { monthLabel, type MonthRef } from '../../lib/backtestMonths'
import { formatInrSigned, formatInrWhole, formatLots, formatNumber, formatPercent } from '../../lib/format'
import { resolutionLabel } from '../../lib/symbols'
import { InlineError, Loading, Panel } from '../../components/ui'
import { IconActivity, IconArrowRight, IconLayers } from '../../components/icons'
import type { BacktestRunView } from '../../lib/types'
import { PnlValue } from '../strategies/shared'
import { CompareCurvesChart, type CompareCurve } from './charts'
import { formatDayWithYear } from './readings'
import { BacktestStatusBadge, formatDayRange } from './shared'
import './backtesting.css'

function runLabel(r: { runId: number; strategyName: string; underlying: string }): string {
  return `#${r.runId} ${r.strategyName} · ${r.underlying}`
}

/** "+₹12,400 · Mar 2025", with its trades under it. */
function MonthCell({ month, beforeCosts }: { month: MonthRef | null; beforeCosts: boolean }) {
  if (!month) return <span className="muted">—</span>
  return (
    <>
      <PnlValue value={month.pnl} /> <span className="muted">· {monthLabel(month.year, month.month)}</span>
      <span className="bt-compare__note">
        {formatNumber(month.trades)} {month.trades === 1 ? 'trade' : 'trades'}
        {beforeCosts ? ' · before the real costs' : ''}
      </span>
    </>
  )
}

interface Row {
  label: string
  /** What the figure is, for the row's title. */
  hint?: string
  cell: (s: RunStats) => React.ReactNode
}

const ROWS: Row[] = [
  { label: 'Strategy', cell: (s) => s.strategyName },
  { label: 'Underlying', cell: (s) => <span className="mono">{s.underlying}</span> },
  { label: 'Bars', cell: (s) => resolutionLabel(s.resolution) },
  {
    label: 'Period',
    hint: 'The IST days the run was asked to replay, and the sessions it wrote equity for',
    cell: (s) => (
      <>
        {formatDayWithYear(s.fromDate)} → {formatDayWithYear(s.toDate)}
        <span className="bt-compare__note">{formatNumber(s.sessions)} sessions replayed</span>
      </>
    ),
  },
  { label: 'Size', cell: (s) => formatLots(s.lots, s.lotSize) },
  { label: 'Capital', cell: (s) => formatInrWhole(s.initialCapital) },
  { label: 'Costs', cell: (s) => s.costsLabel },
  {
    label: 'Net P&L',
    hint: "After the run's own charges, as on its page",
    cell: (s) => (
      <>
        <PnlValue value={s.netPnl} />
        <span className="bt-compare__note">
          {formatPercent(s.returnPercent)} on capital{s.charges > 0 ? ` · charges ${formatInrWhole(s.charges)}` : ''}
        </span>
      </>
    ),
  },
  { label: 'Trades', hint: 'Closed positions', cell: (s) => formatNumber(s.trades) },
  {
    label: 'Win rate',
    hint: 'Closed positions with a positive price P&L',
    cell: (s) => (s.winRatePercent != null ? `${s.winRatePercent.toFixed(0)}%` : <span className="muted">—</span>),
  },
  {
    label: 'Avg win / avg loss',
    hint: 'Per closed position, before charges',
    cell: (s) => (
      <span className="mono">
        {s.averageWin != null ? formatInrSigned(s.averageWin) : '—'} / {s.averageLoss != null ? formatInrSigned(-s.averageLoss) : '—'}
      </span>
    ),
  },
  {
    label: 'Profit factor',
    hint: 'Gross profit over gross loss, before charges',
    cell: (s) =>
      s.profitFactor === 'no-losses' ? (
        <span title="No losing trade to divide by">∞</span>
      ) : s.profitFactor != null ? (
        s.profitFactor.toFixed(2)
      ) : (
        <span className="muted">—</span>
      ),
  },
  {
    label: 'Max drawdown',
    hint: 'The deepest fall of the balance from a high, after charges, and its percent of that high',
    cell: (s) =>
      s.maxDrawdown > 0 ? (
        <>
          <span className="mono neg">{formatInrSigned(-s.maxDrawdown)}</span>{' '}
          <span className="muted">({s.maxDrawdownPercent.toFixed(1)}%)</span>
        </>
      ) : (
        <span className="muted">none</span>
      ),
  },
  {
    label: 'Best month',
    hint: 'Net P&L of the best IST month of the close',
    cell: (s) => <MonthCell month={s.bestMonth} beforeCosts={s.monthsBeforeStatutory} />,
  },
  {
    label: 'Worst month',
    hint: 'Net P&L of the worst IST month of the close',
    cell: (s) => <MonthCell month={s.worstMonth} beforeCosts={s.monthsBeforeStatutory} />,
  },
]

export function BacktestComparePage() {
  const [params, setParams] = useSearchParams()
  const navigate = useNavigate()
  const { ids, dropped } = parseCompareIds(params.get('ids'))
  const base: CurveBase = params.get('base') === 'zero' ? 'zero' : 'balance'
  const { views, errors, pending } = useBacktestRunViews(ids)
  const runs = useBacktestRuns()

  // Each loaded run with its colour slot: its place in the address.
  const loaded = useMemo(
    () =>
      views
        .map((view, slot) => ({ view, slot }))
        .filter((x): x is { view: BacktestRunView; slot: number } => x.view != null),
    [views],
  )
  const stats = useMemo(() => loaded.map((x) => ({ slot: x.slot, stats: runStats(x.view) })), [loaded])
  const curves = useMemo<CompareCurve[]>(
    () =>
      loaded.map((x) => ({
        runId: x.view.runId,
        label: runLabel(x.view),
        slot: x.slot,
        points: x.view.equityCurve,
        initialCapital: x.view.initialCapital,
      })),
    [loaded],
  )
  const flags = compareFlags(
    stats.map((s) => s.stats),
    formatDayWithYear,
  )
  const failed = errors.map((error, i) => ({ id: ids[i], error })).filter((x) => x.error)

  const candidates = (runs.data ?? []).filter((r) => !ids.includes(r.runId) && r.status !== 'Pending')
  function add(runId: number) {
    navigate(compareHref([...ids, runId]) + (base === 'zero' ? '&base=zero' : ''))
  }
  function setBase(next: CurveBase) {
    const p = new URLSearchParams(params)
    if (next === 'zero') p.set('base', 'zero')
    else p.delete('base')
    setParams(p, { replace: true })
  }

  return (
    <div className="page">
      <header className="page__header">
        <div>
          <h1 className="page__title">Compare backtests</h1>
          <p className="page__subtitle">
            {MIN_COMPARE} to {MAX_COMPARE} runs on one time axis and side by side, each read as its own page reads it.
            The address is the comparison: share it to share this view.
          </p>
        </div>
        <div className="bt-compare-add">
          {ids.length < MAX_COMPARE && (
            <select
              className="field__input field__input--sm"
              value=""
              onChange={(e) => e.target.value && add(Number(e.target.value))}
              aria-label="Add a run to the comparison"
              disabled={!runs.data || candidates.length === 0}
            >
              <option value="">{runs.data ? 'Add a run…' : 'Loading runs…'}</option>
              {candidates.map((r) => (
                <option key={r.runId} value={r.runId}>
                  {runLabel(r)} · {resolutionLabel(r.resolution)} · {formatDayRange(r.fromDate, r.toDate)}
                </option>
              ))}
            </select>
          )}
          <Link className="btn btn--ghost btn--sm" to="/research/backtests/runs">
            All runs <IconArrowRight style={{ width: 12, height: 12 }} />
          </Link>
        </div>
      </header>

      {dropped > 0 && (
        <p className="small-note warn" role="status" style={{ margin: 0 }}>
          The address names {ids.length + dropped} runs; the first {MAX_COMPARE} are compared.
        </p>
      )}
      {failed.map((f) => (
        <InlineError key={f.id} error={new Error(`Run #${f.id} could not be loaded: ${f.error instanceof Error ? f.error.message : 'unknown error'}`)} />
      ))}

      {ids.length < MIN_COMPARE ? (
        <Panel>
          <p className="empty">
            {ids.length === 0 ? 'No runs picked yet.' : `Run #${ids[0]} is picked — add one more to compare.`} Tick {MIN_COMPARE}–{MAX_COMPARE} runs on{' '}
            <Link to="/research/backtests/runs">Backtest runs</Link> and press Compare, or add them here.
          </p>
        </Panel>
      ) : pending && loaded.length < ids.length ? (
        <Loading label={`Loading ${ids.length} runs…`} />
      ) : loaded.length < MIN_COMPARE ? (
        <Panel>
          <p className="empty">Fewer than {MIN_COMPARE} of these runs could be loaded, so there is nothing to compare.</p>
        </Panel>
      ) : (
        <>
          {flags.length > 0 && (
            <div className="alert alert--warn alert--stack" role="status">
              <b>Not like for like</b>
              <ul className="bt-notes" style={{ margin: 0, color: 'inherit' }}>
                {flags.map((f) => (
                  <li key={f.kind}>{f.text}</li>
                ))}
              </ul>
            </div>
          )}

          <Panel
            title={
              <>
                <IconActivity /> Equity and drawdown
              </>
            }
            actions={
              <div className="seg" role="group" aria-label="Curve base">
                <button type="button" className="seg__btn" aria-pressed={base === 'balance'} onClick={() => setBase('balance')}>
                  Balance ₹
                </button>
                <button type="button" className="seg__btn" aria-pressed={base === 'zero'} onClick={() => setBase('zero')}>
                  From 0
                </button>
              </div>
            }
          >
            <CompareCurvesChart curves={curves} base={base} />
            <p className="small-note">
              {base === 'zero'
                ? "Each curve is its run's P&L since its own start (balance − its starting capital), after its own charges."
                : "Each curve is its run's account balance, after its own charges."}{' '}
              Under them, how far each balance stood below its best so far. IST.
            </p>
          </Panel>

          <Panel
            title={
              <>
                <IconLayers /> Side by side
              </>
            }
          >
            <div className="tablewrap">
              <table className="table bt-compare">
                <thead>
                  <tr>
                    <th scope="col">Run</th>
                    {stats.map(({ slot, stats: s }) => (
                      <th key={s.runId} scope="col">
                        <span className="bt-compare__head">
                          <span>
                            <span className={`bt-run-key bt-run-key--${slot + 1}`} aria-hidden="true" />
                            <Link to={`/research/backtests/runs/${s.runId}`}>#{s.runId}</Link>
                          </span>
                          <span>
                            <BacktestStatusBadge status={s.status} stopReason={s.stopReason} />
                          </span>
                        </span>
                      </th>
                    ))}
                  </tr>
                </thead>
                <tbody>
                  {ROWS.map((row) => (
                    <tr key={row.label}>
                      <th scope="row" title={row.hint}>
                        {row.label}
                      </th>
                      {stats.map(({ stats: s }) => (
                        <td key={s.runId}>{row.cell(s)}</td>
                      ))}
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <p className="small-note">
              Net P&L, max drawdown, the curves and the months are after each run's own charges; win rate, the
              averages and profit factor are per closed position before charges, as on each run's page. Months are
              the IST month a position closed.
            </p>
          </Panel>
        </>
      )}
    </div>
  )
}
