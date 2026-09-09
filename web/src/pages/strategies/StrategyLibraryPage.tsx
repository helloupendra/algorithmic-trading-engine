/**
 * Strategies module — Library. The catalogue as cards (same card and launch
 * dialog as the Live runner), a dense reference table of what each strategy
 * trades, needs and defaults to, and — for the selected strategy — its
 * specification (docs/strategies/<Name>.md) rendered below the table.
 */

import { useEffect, useState } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import { useStrategies } from '../../lib/queries'
import { contractRequirementSummary, contractRequirementsOf } from '../../lib/contracts'
import { formatResolution } from '../../lib/symbols'
import { Badge, Panel, QueryBoundary } from '../../components/ui'
import { IconArrowRight, IconLayers } from '../../components/icons'
import type { StrategyListItem } from '../../lib/types'
import { CategoryBadge, LaunchDialog, StrategyCard } from './shared'
import { activeUnderlyings } from '../../lib/strategyList'

// The spec renderer carries react-markdown and KaTeX (CSS and fonts included).
// Nobody pays for that until they open a spec, and the strategy list itself
// stays in the main bundle.

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

  // ?strategy=<catalog id> is how older whiteboard cards link here; the spec
  // now has a page of its own, so the link is forwarded there.
  const [params] = useSearchParams()
  useEffect(() => {
    const wanted = Number(params.get('strategy'))
    if (Number.isInteger(wanted) && wanted > 0) navigate(`/admin/strategies/library/${wanted}`, { replace: true })
  }, [params, navigate])

  return (
    <div className="page">
      <header className="page__header">
        <div>
          <h1 className="page__title">Strategy library</h1>
          <p className="page__subtitle">
            Every strategy the Python engine discovers, with what it trades, which underlyings it
            supports and the data it needs. “How it works” opens a strategy's specification: the rule as maths, the
            exits, and a worked example from a real run.
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
                <table className="table table--hover">
                  <thead>
                    <tr>
                      <th>Name</th>
                      <th aria-label="Specification" />
                      <th>Category</th>
                      <th>Underlyings</th>
                      <th>Legs</th>
                      <th title="Contract keys the strategy reads, with their distance from ATM">
                        Contracts
                      </th>
                      <th>Data needs</th>
                      <th>Default params</th>
                      <th>Source</th>
                    </tr>
                  </thead>
                  <tbody>
                    {items.map((s) => {
                      const on = activeUnderlyings(s)
                      return (
                        <tr
                          key={s.id}
                        >
                          <td>
                            <b>{s.name}</b>{' '}
                            {s.isActive && (
                              <Badge tone="pos">running{on.length > 0 ? ` · ${on.join(', ')}` : ''}</Badge>
                            )}
                          </td>
                          <td style={{ whiteSpace: 'nowrap' }}>
                            <Link className="btn btn--sm" to={`/admin/strategies/library/${s.id}`}>
                              How it works <IconArrowRight style={{ width: 12, height: 12 }} />
                            </Link>
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
                          <td className="mono muted" style={{ fontSize: 11, whiteSpace: 'normal', minWidth: 170 }}>
                            {contractRequirementSummary(contractRequirementsOf(s.contractRequirements)) || '—'}
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
