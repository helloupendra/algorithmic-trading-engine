/**
 * Backtesting module — Overview. The data that exists comes first (which
 * index has candles at which resolution, and for how many sessions), then
 * the most recent runs; "New backtest" leads to the strategy grid.
 */

import { useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import {
  useBacktestRuns,
  useBrokerSession,
  useDataCoverage,
  useFnoUnderlyings,
  type CoverageRow,
} from '../../lib/queries'
import { formatNumber, formatPercent, shortSymbol } from '../../lib/format'
import { classifySymbol, resolutionLabel, resolutionRank } from '../../lib/symbols'
import { Badge, Panel, QueryBoundary, StatTile } from '../../components/ui'
import { IconArrowRight, IconDatabase, IconFlask, IconPlus } from '../../components/icons'
import type { BacktestRunSummary, FnoUnderlying } from '../../lib/types'
import { PnlValue } from '../strategies/shared'
import { BackfillDialog } from './BackfillDialog'
import {
  BacktestStatusBadge,
  estimateSessions,
  formatDayRange,
  isActiveStatus,
  runSpecLabel,
} from './shared'

interface CoverageLine {
  underlying: string | null
  spotSymbol: string
  row: CoverageRow | null
}

/**
 * One line per index × resolution that has data, plus one "no data" line per
 * F&O index underlying that has none — so the backfill button exists for it.
 */
function buildLines(rows: CoverageRow[], fno: FnoUnderlying[]): CoverageLine[] {
  const bySpot = new Map<string, FnoUnderlying>()
  for (const u of fno) bySpot.set(u.spotSymbol.toUpperCase(), u)

  const indexRows = rows
    .filter((r) => classifySymbol(r.symbol) === 'Index')
    .sort(
      (a, b) =>
        a.symbol.localeCompare(b.symbol) ||
        resolutionRank(a.resolution) - resolutionRank(b.resolution) ||
        a.source.localeCompare(b.source),
    )

  const lines: CoverageLine[] = indexRows.map((r) => ({
    underlying: bySpot.get(r.symbol.toUpperCase())?.underlying ?? null,
    spotSymbol: r.symbol,
    row: r,
  }))

  const covered = new Set(indexRows.map((r) => r.symbol.toUpperCase()))
  for (const u of fno) {
    if (!u.spotSymbol.toUpperCase().endsWith('-INDEX')) continue
    if (covered.has(u.spotSymbol.toUpperCase())) continue
    lines.push({ underlying: u.underlying, spotSymbol: u.spotSymbol, row: null })
  }
  return lines
}

function RecentRunRow({ run }: { run: BacktestRunSummary }) {
  return (
    <tr>
      <td>
        <Link to={`/admin/backtesting/runs/${run.runId}`}>
          <b>{run.strategyName}</b>
        </Link>
        <span className="faint"> · #{run.runId}</span>
      </td>
      <td className="mono">{run.underlying}</td>
      <td className="muted">
        {resolutionLabel(run.resolution)} · {formatDayRange(run.fromDate, run.toDate)}
      </td>
      <td className="r">{formatNumber(run.lots)}</td>
      <td className="r">
        <PnlValue value={run.netPnl} />
      </td>
      <td className="r">{formatNumber(run.trades)}</td>
      <td className="r muted">{run.trades > 0 ? formatPercent(run.winRatePercent, 0).replace('+', '') : '—'}</td>
      <td>
        <BacktestStatusBadge status={run.status} progressPercent={run.progressPercent} stopReason={run.stopReason} />
      </td>
    </tr>
  )
}

export function BacktestOverviewPage() {
  const runs = useBacktestRuns()
  const coverage = useDataCoverage()
  const fno = useFnoUnderlyings()
  const broker = useBrokerSession()
  const [backfillFor, setBackfillFor] = useState<{ underlying: string; spotSymbol: string } | null>(null)

  const runList = useMemo(() => runs.data ?? [], [runs.data])
  const running = runList.filter((r) => isActiveStatus(r.status))
  const best = runList
    .filter((r) => (r.status === 'Completed' || r.status === 'Stopped') && r.trades > 0)
    .reduce<BacktestRunSummary | null>((acc, r) => (acc == null || r.netPnl > acc.netPnl ? r : acc), null)

  const lines = useMemo(() => buildLines(coverage.data ?? [], fno.data ?? []), [coverage.data, fno.data])
  const sessionsAvailable = lines.reduce(
    (n, l) => n + (l.row ? estimateSessions(l.row.resolution, l.row.barCount) : 0),
    0,
  )
  const brokerLinked = broker.data?.isAuthenticated ?? false
  const recent = runList.slice(0, 8)

  return (
    <div className="page">
      <header className="page__header">
        <div>
          <h1 className="page__title">Backtesting</h1>
          <p className="page__subtitle">
            Replay a catalogue strategy over stored index history — the same on_bar contract as the
            live runner, position-based results.
          </p>
        </div>
        <Link className="btn btn--primary" to="/admin/backtesting/new">
          <IconPlus style={{ width: 14, height: 14 }} /> New backtest
        </Link>
      </header>

      <div className="stat-grid">
        <StatTile
          label="Backtests run"
          value={runs.data ? formatNumber(runList.length) : '—'}
          sub={runs.data ? `${formatNumber(runList.filter((r) => r.status === 'Completed').length)} completed` : 'loading runs…'}
          to="/admin/backtesting/runs"
        />
        <StatTile
          label="Running now"
          value={runs.data ? formatNumber(running.length) : '—'}
          tone={running.length > 0 ? 'accent' : undefined}
          sub={
            running.length > 0
              ? running.map((r) => `${r.strategyName} ${Math.round(r.progressPercent)}%`).join(', ')
              : 'nothing replaying'
          }
          to="/admin/backtesting/runs"
        />
        <StatTile
          label="Best net P&L"
          value={best ? <PnlValue value={best.netPnl} /> : '—'}
          tone={best ? (best.netPnl > 0 ? 'pos' : best.netPnl < 0 ? 'neg' : undefined) : undefined}
          sub={best ? `${best.strategyName} · ${best.underlying} · ${formatDayRange(best.fromDate, best.toDate)}` : 'no finished run with trades yet'}
          to={best ? `/admin/backtesting/runs/${best.runId}` : undefined}
        />
        <StatTile
          label="Data sessions available"
          value={coverage.data ? `≈ ${formatNumber(sessionsAvailable)}` : '—'}
          sub={`index candles at any resolution · estimated from ${formatNumber(lines.filter((l) => l.row).length)} stored ranges`}
          to="/admin/data/historical"
        />
      </div>

      <Panel
        title={
          <>
            <IconDatabase /> Historical data on hand
          </>
        }
        actions={
          <Link className="btn btn--ghost btn--sm" to="/admin/data/historical">
            All ranges <IconArrowRight style={{ width: 12, height: 12 }} />
          </Link>
        }
      >
        <QueryBoundary query={coverage}>
          {() =>
            lines.length === 0 ? (
              <p className="empty">
                No index candles are stored yet, and no F&O index underlying is loaded to backfill
                for. Import the instrument master on{' '}
                <Link to="/admin/data/instruments">Data › Instruments & F&O</Link> first.
              </p>
            ) : (
              <div className="tablewrap">
                <table className="table coverage-table">
                  <thead>
                    <tr>
                      <th>Index</th>
                      <th>Resolution</th>
                      <th>From → To</th>
                      <th className="r">Sessions</th>
                      <th className="r">Bars</th>
                      <th>Source</th>
                      <th />
                    </tr>
                  </thead>
                  <tbody>
                    {lines.map((l, i) => {
                      const firstOfGroup = i === 0 || lines[i - 1].spotSymbol !== l.spotSymbol
                      const name = l.underlying ?? shortSymbol(l.spotSymbol)
                      return (
                        <tr key={`${l.spotSymbol}|${l.row?.resolution ?? 'none'}|${l.row?.source ?? 'none'}`}>
                          <td className="mono">
                            {firstOfGroup ? (
                              <span className="coverage-table__underlying" title={l.spotSymbol}>
                                {name}
                              </span>
                            ) : (
                              <span className="faint">〃</span>
                            )}
                          </td>
                          {l.row ? (
                            <>
                              <td>
                                <Badge tone="neutral">{resolutionLabel(l.row.resolution)}</Badge>
                              </td>
                              <td className="muted">{formatDayRange(l.row.fromUtc, l.row.toUtc)}</td>
                              <td className="r">≈ {formatNumber(estimateSessions(l.row.resolution, l.row.barCount))}</td>
                              <td className="r">{formatNumber(l.row.barCount)}</td>
                              <td>
                                <Badge tone={l.row.source === 'live' ? 'live' : 'accent'}>{l.row.source}</Badge>
                              </td>
                            </>
                          ) : (
                            <td colSpan={5} className="coverage-table__none">
                              No index candles stored — backfill to make {name} backtestable.
                            </td>
                          )}
                          <td className="r">
                            {firstOfGroup && (
                              <button
                                type="button"
                                className="btn btn--sm"
                                disabled={!l.underlying}
                                title={
                                  l.underlying
                                    ? `Fetch ${name} index candles from FYERS`
                                    : 'Not an F&O underlying in the instrument master'
                                }
                                onClick={() =>
                                  l.underlying &&
                                  setBackfillFor({ underlying: l.underlying, spotSymbol: l.spotSymbol })
                                }
                              >
                                Backfill…
                              </button>
                            )}
                          </td>
                        </tr>
                      )
                    })}
                  </tbody>
                </table>
              </div>
            )
          }
        </QueryBoundary>
        <p className="small-note">
          Sessions are estimated from bar counts (75 × 5m or 375 × 1m bars per full NSE day); the
          launch dialog shows the exact count per underlying. Backtests read backfilled candles; live
          1m bars only cover the hours the ingestor ran.
        </p>
      </Panel>

      <Panel
        title={
          <>
            <IconFlask /> Recent backtests
          </>
        }
        actions={
          <Link className="btn btn--ghost btn--sm" to="/admin/backtesting/runs">
            All runs <IconArrowRight style={{ width: 12, height: 12 }} />
          </Link>
        }
      >
        <QueryBoundary query={runs}>
          {() =>
            recent.length === 0 ? (
              <p className="empty">
                No backtests yet. <Link to="/admin/backtesting/new">Start one</Link> from the strategy
                catalogue.
              </p>
            ) : (
              <div className="tablewrap">
                <table className="table">
                  <thead>
                    <tr>
                      <th>Strategy</th>
                      <th>Underlying</th>
                      <th>Range</th>
                      <th className="r">Lots</th>
                      <th className="r">Net P&L</th>
                      <th className="r">Trades</th>
                      <th className="r">Win rate</th>
                      <th>Status</th>
                    </tr>
                  </thead>
                  <tbody>
                    {recent.map((run) => (
                      <RecentRunRow key={run.runId} run={run} />
                    ))}
                  </tbody>
                </table>
              </div>
            )
          }
        </QueryBoundary>
        {recent.length > 0 && (
          <p className="small-note">
            Latest: {runSpecLabel(recent[0])} · {recent[0].strategyName}.
          </p>
        )}
      </Panel>

      {backfillFor && (
        <BackfillDialog
          underlying={backfillFor.underlying}
          spotSymbol={backfillFor.spotSymbol}
          brokerLinked={brokerLinked}
          onClose={() => setBackfillFor(null)}
        />
      )}
    </div>
  )
}
