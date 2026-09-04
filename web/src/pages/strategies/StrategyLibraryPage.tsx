/**
 * Strategies module — Library. The catalogue as cards (same card and launch
 * dialog as the Live runner) plus a dense reference table of what each
 * strategy trades, needs and defaults to.
 */

import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { useStrategies } from '../../lib/queries'
import { formatResolution } from '../../lib/symbols'
import { Badge, Panel, QueryBoundary } from '../../components/ui'
import { IconLayers } from '../../components/icons'
import type { StrategyListItem } from '../../lib/types'
import { CategoryBadge, LaunchDialog, StrategyCard } from './shared'
import { activeUnderlyings } from '../../lib/strategyList'

function compactJson(json: string): string {
  try {
    const obj = JSON.parse(json || '{}') as Record<string, unknown>
    const entries = Object.entries(obj)
    if (entries.length === 0) return '—'
    return entries.map(([k, v]) => `${k}=${typeof v === 'object' ? JSON.stringify(v) : String(v)}`).join(' · ')
  } catch {
    return json || '—'
  }
}

export function StrategyLibraryPage() {
  const strategies = useStrategies()
  const navigate = useNavigate()
  const [launchId, setLaunchId] = useState<number | null>(null)
  // Read the strategy from the polled list so the dialog's "already running"
  // rows track runs started elsewhere while it is open.
  const launch: StrategyListItem | null =
    launchId != null ? (strategies.data?.find((s) => s.id === launchId) ?? null) : null

  return (
    <div className="page">
      <header className="page__header">
        <div>
          <h1 className="page__title">Strategy library</h1>
          <p className="page__subtitle">
            Every strategy the Python engine discovers, with what it trades, which underlyings it
            supports and the data it needs.
          </p>
        </div>
      </header>

      <QueryBoundary query={strategies} empty="No strategies found in the Python engine.">
        {(items) => (
          <>
            <div className="strategy-grid">
              {items.map((s) => (
                <StrategyCard key={s.id} strategy={s} onStart={(st) => setLaunchId(st.id)} />
              ))}
            </div>

            <Panel
              title={
                <>
                  <IconLayers /> Details
                </>
              }
            >
              <div className="tablewrap">
                <table className="table">
                  <thead>
                    <tr>
                      <th>Name</th>
                      <th>Category</th>
                      <th>Underlyings</th>
                      <th>Legs</th>
                      <th>Data needs</th>
                      <th>Default params</th>
                      <th>Source</th>
                    </tr>
                  </thead>
                  <tbody>
                    {items.map((s) => {
                      const on = activeUnderlyings(s)
                      return (
                        <tr key={s.id}>
                          <td>
                            <b>{s.name}</b>{' '}
                            {s.isActive && (
                              <Badge tone="pos">running{on.length > 0 ? ` · ${on.join(', ')}` : ''}</Badge>
                            )}
                          </td>
                          <td>
                            <CategoryBadge category={s.category} />
                          </td>
                          <td className="mono muted">
                            {s.supportedUnderlyings.length === 0 ? '—' : s.supportedUnderlyings.join(', ')}
                          </td>
                          <td className="muted" style={{ whiteSpace: 'normal', minWidth: 180 }}>
                            {s.legsSummary || '—'}
                          </td>
                          <td className="muted">
                            {s.dataRequirements.length === 0
                              ? '—'
                              : s.dataRequirements
                                  .map((d) => `${d.symbolType} @ ${formatResolution(d.resolution)}`)
                                  .join(', ')}
                          </td>
                          <td className="mono muted" style={{ fontSize: 11, whiteSpace: 'normal', minWidth: 200 }}>
                            {compactJson(s.defaultParametersJson)}
                            {s.defaultLots > 0 ? ` · lots=${s.defaultLots}` : ''}
                          </td>
                          <td className="mono muted" style={{ fontSize: 11 }}>
                            {s.sourceFile || '—'}
                          </td>
                        </tr>
                      )
                    })}
                  </tbody>
                </table>
              </div>
            </Panel>
          </>
        )}
      </QueryBoundary>

      {launch && (
        <LaunchDialog
          strategy={launch}
          onClose={() => setLaunchId(null)}
          onStarted={() => navigate('/admin/strategies/live')}
        />
      )}
    </div>
  )
}
