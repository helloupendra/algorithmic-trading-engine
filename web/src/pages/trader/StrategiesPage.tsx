/**
 * Strategy catalogue plus the full run history. Start/stop lives on the admin
 * screen; this is the trader's read view of what exists and what has run.
 */

import { Link } from 'react-router-dom'
import { useSimulationRuns, useStopStrategy, useStrategies } from '../../lib/queries'
import { useAuth } from '../../lib/auth'
import { formatDateTime, formatInrWhole, shortSymbol } from '../../lib/format'
import { Badge, InlineError, Panel, QueryBoundary } from '../../components/ui'

export function StrategiesPage() {
  const strategies = useStrategies()
  const runs = useSimulationRuns()
  const stop = useStopStrategy()
  const { user } = useAuth()

  // One shared mutation serves every Stop button, so only the run being
  // stopped is greyed and the outcome (403 not the starter, 400 already
  // stopped by the risk guard, 404 unknown run) is shown instead of swallowed.
  const stoppingRunId = stop.isPending ? (stop.variables?.runId ?? null) : null
  const stopOutcome = stop.isSuccess && stop.data ? stop.data : null

  return (
    <div className="page">
      <header className="page__header">
        <h1 className="page__title">Strategies</h1>
        <p className="page__subtitle">
          Registered strategy definitions and every simulation run recorded so far.
        </p>
      </header>

      <Panel title="Definitions">
        {stop.isError && (
          <div style={{ marginBottom: 10 }}>
            <InlineError error={stop.error} />
          </div>
        )}
        {stopOutcome && (
          <p className="small-note" style={{ margin: '0 0 10px' }} role="status">
            {stopOutcome.message}
            {stopOutcome.flattened > 0 ? ` · squared off ${stopOutcome.flattened}` : ''}
          </p>
        )}
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
                        {s.activeRuns.length > 0 ? (
                          // One line per live run: the same strategy may be
                          // running on several underlyings, each stopped alone.
                          <div className="chip-row">
                            {s.activeRuns.map((run) => (
                              <span key={run.runId} title={`run #${run.runId}`}>
                                <Badge tone="pos">
                                  running · {run.underlying} · {run.startedBy}
                                </Badge>{' '}
                                {run.startedBy === user?.userName && (
                                  <button
                                    type="button"
                                    className="btn btn--ghost btn--sm"
                                    disabled={stoppingRunId === run.runId}
                                    onClick={() => stop.mutate({ runId: run.runId })}
                                  >
                                    {stoppingRunId === run.runId ? 'Stopping…' : 'Stop'}
                                  </button>
                                )}
                              </span>
                            ))}
                          </div>
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
