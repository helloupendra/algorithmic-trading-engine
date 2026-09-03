import { IconLayers } from '../../components/icons'
import { Panel, QueryBoundary, Badge } from '../../components/ui'
import { useStrategies } from '../../lib/queries'
import { formatDateTime } from '../../lib/format'

function StrategyList() {
  const strategies = useStrategies()
  const data = strategies.data ?? []

  if (data.length === 0) {
    return <div className="empty-state">No strategies found in the Python engine.</div>
  }

  return (
    <div className="table-wrapper">
      <table className="table">
        <thead>
          <tr>
            <th>Strategy Name</th>
            <th>Description</th>
            <th>Default Parameters</th>
            <th>Created</th>
          </tr>
        </thead>
        <tbody>
          {data.map((s) => (
            <tr key={s.id}>
              <td>
                <strong>{s.name}</strong>
                {s.isActive && <Badge tone="pos" style={{ marginLeft: 8 }}>Running</Badge>}
              </td>
              <td>{s.description}</td>
              <td className="mono" style={{ fontSize: 11 }}>{s.defaultParametersJson}</td>
              <td>{formatDateTime(s.createdUtc)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

export function StrategyLibraryPage() {
  return (
    <div className="page">
      <header className="page__header">
        <div>
          <h1 className="page__title">Strategy Library</h1>
          <p className="page__subtitle">
            View available strategies discovered in the Python Engine.
          </p>
        </div>
      </header>

      <Panel
        title={
          <>
            <IconLayers /> Registered Strategies
          </>
        }
      >
        <QueryBoundary query={useStrategies()}>
          {() => <StrategyList />}
        </QueryBoundary>
      </Panel>
    </div>
  )
}
