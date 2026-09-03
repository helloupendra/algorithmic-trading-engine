/**
 * Strategies module — Overview. Four tiles and a compact table of what is
 * running right now; every row leads to the Live runner where the positions
 * live.
 */

import { useMemo } from 'react'
import { Link } from 'react-router-dom'
import { useStrategies, useStrategyLives } from '../../lib/queries'
import { formatDateTime } from '../../lib/format'
import { Panel, QueryBoundary, StatTile } from '../../components/ui'
import { IconPlay } from '../../components/icons'
import type { StrategyLiveView } from '../../lib/types'
import { CategoryBadge, PnlValue, ReadinessStrip } from './shared'

export function StrategiesOverviewPage() {
  const strategies = useStrategies()
  const list = useMemo(() => strategies.data ?? [], [strategies.data])
  const running = useMemo(() => list.filter((s) => s.isActive), [list])
  const runningIds = useMemo(() => running.map((s) => s.id), [running])

  const lives = useStrategyLives(runningIds)
  const viewById = new Map<number, StrategyLiveView>()
  for (const q of lives) if (q.data) viewById.set(q.data.strategyId, q.data)

  const openPositions = running.reduce(
    (n, s) => n + (viewById.get(s.id)?.positions.filter((p) => p.status === 'Open').length ?? 0),
    0,
  )
  const livePnl = running.reduce((n, s) => n + (viewById.get(s.id)?.pnl.total ?? 0), 0)

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
          label="Running"
          value={running.length}
          tone={running.length > 0 ? 'pos' : undefined}
          sub={running.length > 0 ? 'live paper runners' : 'nothing running'}
          to="/admin/strategies/live"
        />
        <StatTile
          label="Open positions"
          value={openPositions}
          sub="across running strategies"
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
            running.length === 0 ? (
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
                    {running.map((s) => {
                      const v = viewById.get(s.id)
                      return (
                        <tr key={s.id}>
                          <td>
                            <b>{s.name}</b> <CategoryBadge category={s.category} />
                          </td>
                          <td className="mono">{v?.underlying ?? s.underlying ?? '—'}</td>
                          <td className="r">
                            {v ? v.positions.filter((p) => p.status === 'Open').length : '—'}
                          </td>
                          <td className="r">{v ? <PnlValue value={v.pnl.total} /> : '—'}</td>
                          <td className="muted">
                            {s.startedBy ? `${s.startedBy} · ` : ''}
                            {formatDateTime(s.startedUtc)}
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
