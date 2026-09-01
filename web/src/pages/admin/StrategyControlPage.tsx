/**
 * Start and stop strategy processes. Each start spawns a Python subprocess on
 * the API host; state is in-process, so an API restart shows everything as
 * stopped even if orphaned processes were adopted elsewhere.
 */

import { useStartStrategy, useStopStrategy, useStrategies } from '../../lib/queries'
import { formatDateTime } from '../../lib/format'
import { Badge, InlineError, Panel, QueryBoundary } from '../../components/ui'

export function StrategyControlPage() {
  const strategies = useStrategies()
  const start = useStartStrategy()
  const stop = useStopStrategy()

  return (
    <div className="page">
      <header className="page__header">
        <h1 className="page__title">Strategy control</h1>
        <p className="page__subtitle">
          Each start launches the Python execution runner for that strategy against the live
          paper pipeline.
        </p>
      </header>

      <Panel title="Strategies">
        {(start.isError || stop.isError) && (
          <InlineError error={start.error ?? stop.error} />
        )}
        <QueryBoundary query={strategies} empty="No strategies registered.">
          {(data) => (
            <div className="tablewrap">
              <table className="table">
                <thead>
                  <tr>
                    <th>#</th>
                    <th>Name</th>
                    <th>Description</th>
                    <th>State</th>
                    <th className="r">Started</th>
                    <th className="r">Actions</th>
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
                          <Badge tone="pos">running · {s.startedBy}</Badge>
                        ) : (
                          <Badge>stopped</Badge>
                        )}
                      </td>
                      <td className="r muted">{formatDateTime(s.startedUtc)}</td>
                      <td className="r">
                        {s.isActive ? (
                          <button
                            type="button"
                            className="btn btn--ghost btn--sm"
                            disabled={stop.isPending}
                            onClick={() => stop.mutate(s.id)}
                          >
                            Stop
                          </button>
                        ) : (
                          <button
                            type="button"
                            className="btn btn--primary btn--sm"
                            disabled={start.isPending}
                            onClick={() => start.mutate(s.id)}
                          >
                            Start
                          </button>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </QueryBoundary>
        <p className="muted small-note">
          Starting requires the Python engine's virtualenv on the API host and a valid FYERS
          session for live data. Process state resets if the API restarts.
        </p>
      </Panel>
    </div>
  )
}
