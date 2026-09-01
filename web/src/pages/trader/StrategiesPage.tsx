/**
 * Strategy catalogue plus the full run history. Start/stop lives on the admin
 * screen; this is the trader's read view of what exists and what has run.
 */

import { Link } from 'react-router-dom'
import { useSimulationRuns, useStopStrategy, useStrategies } from '../../lib/queries'
import { useAuth } from '../../lib/auth'
import { formatDateTime, formatInrWhole, shortSymbol } from '../../lib/format'
import { Badge, Panel, QueryBoundary } from '../../components/ui'

export function StrategiesPage() {
  const strategies = useStrategies()
  const runs = useSimulationRuns()
  const stop = useStopStrategy()
  const { user } = useAuth()

  return (
    <div className="page">
      <header className="page__header">
        <h1 className="page__title">Strategies</h1>
        <p className="page__subtitle">
          Registered strategy definitions and every simulation run recorded so far.
        </p>
      </header>

      <Panel title="Definitions">
        <QueryBoundary query={strategies} empty="No strategies registered yet.">
          {(data) => (
            <div className="tablewrap">
              <table className="table">
                <thead>
                  <tr>
                    <th>#</th>
                    <th>Name</th>
                    <th>Description</th>
                    <th>Process</th>
                    <th className="r">Registered</th>
                  </tr>
                </thead>
                <tbody>
                  {data.map((s) => (
                    <tr key={s.id}>
                      <td className="mono muted">{s.id}</td>
                      <td className="mono">{s.name}</td>
                      <td className="muted">{s.description || '—'}</td>
                      <td>
                        {s.isActive ? (
                          <>
                            <Badge tone="pos">running · {s.startedBy}</Badge>{' '}
                            {s.startedBy === user?.userName && (
                              <button
                                type="button"
                                className="btn btn--ghost btn--sm"
                                disabled={stop.isPending}
                                onClick={() => stop.mutate(s.id)}
                              >
                                Stop
                              </button>
                            )}
                          </>
                        ) : (
                          <Badge>stopped</Badge>
                        )}
                      </td>
                      <td className="r muted">{formatDateTime(s.createdUtc)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </QueryBoundary>
      </Panel>

      <Panel title="Run history">
        <QueryBoundary query={runs} empty="No runs yet — start a strategy to create one.">
          {(data) => (
            <div className="tablewrap">
              <table className="table table--hover">
                <thead>
                  <tr>
                    <th>Run</th>
                    <th>Strategy</th>
                    <th>Mode</th>
                    <th>Symbol</th>
                    <th>Status</th>
                    <th className="r">Capital</th>
                    <th className="r">Created</th>
                    <th />
                  </tr>
                </thead>
                <tbody>
                  {data.map((run) => (
                    <tr key={run.id}>
                      <td className="mono">#{run.id}</td>
                      <td>{run.strategyName}</td>
                      <td className="muted">{run.mode}</td>
                      <td className="mono">{shortSymbol(run.symbol)}</td>
                      <td>
                        <Badge
                          tone={
                            run.status === 'Running'
                              ? 'pos'
                              : run.status === 'Failed'
                                ? 'neg'
                                : 'neutral'
                          }
                        >
                          {run.status}
                        </Badge>
                      </td>
                      <td className="r mono">{formatInrWhole(run.initialCapital)}</td>
                      <td className="r muted">{formatDateTime(run.createdUtc)}</td>
                      <td className="r">
                        <Link to={`/trader/runs/${run.id}`}>Open →</Link>
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
