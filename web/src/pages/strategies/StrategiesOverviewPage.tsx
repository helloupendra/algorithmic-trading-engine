/**
 * Strategies module — Overview. Tiles and a compact table of what is running
 * right now — one row per RUN, since a strategy may be live on several
 * underlyings at once; every row leads to the Live runner where the positions
 * live. Under it, the last six runs from Run history, whatever ended them.
 */

import { useMemo } from 'react'
import { Link } from 'react-router-dom'
import { useLiveRunHistory, useStrategies, useStrategyLives } from '../../lib/queries'
import { formatDateTime, formatDuration, formatNumber } from '../../lib/format'
import { runDurationSeconds, runNetPnl, runUserLabel } from '../../lib/runHistory'
import { Panel, QueryBoundary, StatTile } from '../../components/ui'
import { IconClock, IconPlay } from '../../components/icons'
import type { StrategyActiveRun, StrategyListItem, StrategyLiveView } from '../../lib/types'
import { CategoryBadge, PnlValue, ReadinessStrip } from './shared'
import { RunStatusCell } from './RunHistoryPage'
import { runningSummary } from '../../lib/strategyList'
import { todayIst } from '../backtesting/shared'

interface RunRow {
  strategy: StrategyListItem
  run: StrategyActiveRun
}

const RECENT_RUNS = 6

export function StrategiesOverviewPage() {
  const strategies = useStrategies()
  const list = useMemo(() => strategies.data ?? [], [strategies.data])
  const runningStrategies = useMemo(() => list.filter((s) => s.activeRuns.length > 0), [list])
  const rows = useMemo<RunRow[]>(
    () => list.flatMap((s) => s.activeRuns.map((run) => ({ strategy: s, run }))),
    [list],
  )
  const runIds = useMemo(() => rows.map((r) => r.run.runId), [rows])

  const lives = useStrategyLives(runIds)
  const viewByRun = new Map<number, StrategyLiveView>()
  for (const q of lives) if (q.data?.runId != null) viewByRun.set(q.data.runId, q.data)

  const openPositions = rows.reduce(
    (n, r) => n + (viewByRun.get(r.run.runId)?.positions.filter((p) => p.status === 'Open').length ?? 0),
    0,
  )
  const livePnl = rows.reduce((n, r) => n + (viewByRun.get(r.run.runId)?.pnl.total ?? 0), 0)

  // Run history: today's runs for the tile, the newest six for the list.
  const today = todayIst()
  const todayFilters = useMemo(() => ({ fromDate: today, toDate: today, take: 500 }), [today])
  const todayRuns = useLiveRunHistory(todayFilters)
  const recentFilters = useMemo(() => ({ take: RECENT_RUNS }), [])
  const recent = useLiveRunHistory(recentFilters)
  const todayList = todayRuns.data ?? []
  const todayPnl = todayList.reduce((n, r) => n + runNetPnl(r), 0)

  return (
    <div className="page">
      <header className="page__header">
        <div>
          <h1 className="page__title">Strategies</h1>
          <p className="page__subtitle">
            What is running, how it is doing, and the catalogue it was started from.
          </p>
        </div>
        <ReadinessStrip />
      </header>

      <div className="stat-grid">
        <StatTile
          label={rows.length === 1 ? 'Running run' : 'Running runs'}
          value={rows.length}
          tone={rows.length > 0 ? 'pos' : undefined}
          sub={
            runningStrategies.length > 0
              ? runningStrategies.map((s, i) => (
                  <span key={s.id}>
                    {i > 0 && <br />}
                    {runningSummary(s)}
                  </span>
                ))
              : 'nothing running'
          }
          to="/admin/strategies/live"
        />
        <StatTile
          label="Open positions"
          value={openPositions}
          sub="across running runs"
          to="/admin/strategies/live"
        />
        <StatTile
          label="Live P&L"
          value={<PnlValue value={livePnl} />}
          tone={livePnl > 0 ? 'pos' : livePnl < 0 ? 'neg' : undefined}
          sub="realized + unrealized"
          to="/admin/strategies/live"
        />
        <StatTile
          label="Runs today"
          value={todayRuns.data ? formatNumber(todayList.length) : '—'}
          tone={todayPnl > 0 ? 'pos' : todayPnl < 0 ? 'neg' : undefined}
          sub={
            todayRuns.data
              ? todayList.length > 0
                ? <>net <PnlValue value={todayPnl} /> · every user · incl. stopped</>
                : 'none started yet — all in Run history'
              : todayRuns.isError
                ? 'history unavailable'
                : 'loading…'
          }
          to="/admin/strategies/history"
        />
        <StatTile
          label="Library size"
          value={list.length}
          sub="strategies discovered"
          to="/admin/strategies/library"
        />
      </div>

      <Panel
        title={
          <>
            <IconPlay /> Running now
          </>
        }
        actions={
          <Link className="btn btn--sm" to="/admin/strategies/live">
            Open live runner
          </Link>
        }
      >
        <QueryBoundary query={strategies}>
          {() =>
            rows.length === 0 ? (
              <p className="empty">
                Nothing is running. Start one from the{' '}
                <Link to="/admin/strategies/live">Live runner</Link>.
              </p>
            ) : (
              <div className="tablewrap">
                <table className="table">
                  <thead>
                    <tr>
                      <th>Strategy</th>
                      <th>Underlying</th>
                      <th className="r">Open</th>
                      <th className="r">P&L</th>
                      <th>Started</th>
                      <th />
                    </tr>
                  </thead>
                  <tbody>
                    {rows.map(({ strategy: s, run }) => {
                      const v = viewByRun.get(run.runId)
                      return (
                        <tr key={run.runId}>
                          <td>
                            <b>{s.name}</b> <CategoryBadge category={s.category} />
                            <span className="faint"> · #{run.runId}</span>
                          </td>
                          <td className="mono">{v?.underlying ?? run.underlying}</td>
                          <td className="r">
                            {v ? v.positions.filter((p) => p.status === 'Open').length : '—'}
                          </td>
                          <td className="r">{v ? <PnlValue value={v.pnl.total} /> : '—'}</td>
                          <td className="muted">
                            {run.startedBy ? `${run.startedBy} · ` : ''}
                            {formatDateTime(run.startedUtc)}
                          </td>
                          <td className="r">
                            <Link to="/admin/strategies/live">Positions →</Link>
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
      </Panel>

      <Panel
        title={
          <>
            <IconClock /> Recent runs
          </>
        }
        actions={
          <Link className="btn btn--sm" to="/admin/strategies/history">
            All history →
          </Link>
        }
      >
        <QueryBoundary
          query={recent}
          empty={
            <>
              No live runs yet — start one from the <Link to="/admin/strategies/live">Live runner</Link>.
            </>
          }
        >
          {(runs) => (
            <div className="tablewrap">
              <table className="table">
                <thead>
                  <tr>
                    <th>Run #</th>
                    <th>User</th>
                    <th>Strategy</th>
                    <th>Underlying</th>
                    <th>Started</th>
                    <th className="r">Duration</th>
                    <th className="r">Trades</th>
                    <th className="r">Net P&L</th>
                    <th>Status</th>
                    <th />
                  </tr>
                </thead>
                <tbody>
                  {runs.slice(0, RECENT_RUNS).map((run) => (
                    <tr key={run.runId} className={run.isActive ? 'row--live' : ''}>
                      <td className="mono muted">#{run.runId}</td>
                      <td>{run.userName || <span className="faint">{runUserLabel(run.userName, run.userId)}</span>}</td>
                      <td>
                        <b>{run.strategyName}</b> <CategoryBadge category={run.category} />
                      </td>
                      <td className="mono">{run.underlying}</td>
                      <td className="muted">{formatDateTime(run.startedUtc)}</td>
                      <td className="r mono muted">{formatDuration(runDurationSeconds(run))}</td>
                      <td className="r">{formatNumber(run.trades)}</td>
                      <td className="r">
                        <PnlValue value={runNetPnl(run)} />
                      </td>
                      <td>
                        <RunStatusCell run={run} />
                      </td>
                      <td className="r">
                        <Link to={`/admin/strategies/runs/${run.runId}`}>Detail →</Link>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </QueryBoundary>
      </Panel>
    </div>
  )
}
