/**
 * Strategies module — Overview. Four tiles and a compact table of what is
 * running right now — one row per RUN, since a strategy may be live on several
 * underlyings at once; every row leads to the Live runner where the positions
 * live.
 */

import { useMemo } from 'react'
import { Link } from 'react-router-dom'
import { useStrategies, useStrategyLives } from '../../lib/queries'
import { formatDateTime } from '../../lib/format'
import { Panel, QueryBoundary, StatTile } from '../../components/ui'
import { IconPlay } from '../../components/icons'
import type { StrategyActiveRun, StrategyListItem, StrategyLiveView } from '../../lib/types'
import { CategoryBadge, PnlValue, ReadinessStrip } from './shared'
import { runningSummary } from '../../lib/strategyList'

interface RunRow {
  strategy: StrategyListItem
  run: StrategyActiveRun
}

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
    </div>
  )
}
