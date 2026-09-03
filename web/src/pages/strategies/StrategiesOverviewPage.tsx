import { IconBot, IconFlask, IconPlay } from '../../components/icons'
import { StatTile } from '../../components/ui'
import { useStrategies } from '../../lib/queries'

export function StrategiesOverviewPage() {
  const strategies = useStrategies()
  const data = strategies.data ?? []
  const activeCount = data.filter(s => s.isActive).length

  return (
    <div className="page">
      <header className="page__header">
        <div>
          <h1 className="page__title">Strategies</h1>
          <p className="page__subtitle">
            Overview of your active execution runners and strategy library.
          </p>
        </div>
      </header>
      
      <div className="stat-grid">
        <StatTile
          label="Running strategies"
          value={activeCount.toString()}
          tone={activeCount > 0 ? 'pos' : undefined}
          sub={activeCount > 0 ? 'Live processes active' : 'No active processes'}
          to="/admin/strategies/live"
        />
        <StatTile
          label="Library"
          value={data.length.toString()}
          sub="Strategies available"
          to="/admin/strategies/library"
        />
        <StatTile
          label="Recent signals"
          value="0"
          sub="Generated today"
        />
      </div>
    </div>
  )
}
