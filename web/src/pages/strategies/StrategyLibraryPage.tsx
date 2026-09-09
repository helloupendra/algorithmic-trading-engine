/**
 * Strategies module — Library. The catalogue as cards (same card and launch
 * dialog as the Live runner), a dense reference table of what each strategy
 * trades, needs and defaults to, and — for the selected strategy — its
 * specification (docs/strategies/<Name>.md) rendered below the table.
 */

import { Suspense, lazy, useEffect, useRef, useState } from 'react'
import { useNavigate, useSearchParams } from 'react-router-dom'
import { useStrategies } from '../../lib/queries'
import { contractRequirementSummary, contractRequirementsOf } from '../../lib/contracts'
import { formatResolution } from '../../lib/symbols'
import { Badge, Loading, Panel, QueryBoundary } from '../../components/ui'
import { IconLayers, IconX } from '../../components/icons'
import type { StrategyListItem } from '../../lib/types'
import { CategoryBadge, LaunchDialog, StrategyCard } from './shared'
import { activeUnderlyings } from '../../lib/strategyList'

// The spec renderer carries react-markdown and KaTeX (CSS and fonts included).
// Nobody pays for that until they open a spec, and the strategy list itself
// stays in the main bundle.
const StrategySpecPanel = lazy(() => import('./StrategySpecPanel'))

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

  // The strategy whose spec is open. The panel sits below the reference table
  // (the page is a single column), so a pick in the table scrolls it into
  // view; the panel carries its own strategy picker so the reader can move
  // between specs without scrolling back up. ?strategy=<catalog id> — how a
  // whiteboard card links here — opens with that spec selected; it is read
  // once, and the page's own picks take over from there.
  const [params] = useSearchParams()
  const [specId, setSpecId] = useState<number | null>(() => {
    const wanted = Number(params.get('strategy'))
    return Number.isInteger(wanted) && wanted > 0 ? wanted : null
  })
  const specRef = useRef<HTMLDivElement | null>(null)
  const spec: StrategyListItem | null =
    specId != null ? (strategies.data?.find((s) => s.id === specId) ?? null) : null
  // Keyed on the resolved spec, not the id: a deep-linked id is set before the
  // list has loaded, and the panel to scroll to only exists once it has.
  const openSpecId = spec?.id ?? null
  useEffect(() => {
    if (openSpecId != null) specRef.current?.scrollIntoView({ behavior: 'smooth', block: 'start' })
  }, [openSpecId])

  return (
    <div className="page">
      <header className="page__header">
        <div>
          <h1 className="page__title">Strategy library</h1>
          <p className="page__subtitle">
            Every strategy the Python engine discovers, with what it trades, which underlyings it
            supports and the data it needs. Pick a row for how it works: the rule as maths, the
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
                      <th>Category</th>
                      <th>Underlyings</th>
                      <th>Legs</th>
                      <th title="Contract keys the strategy reads, with their distance from ATM">
                        Contracts
                      </th>
                      <th>Data needs</th>
                      <th>Default params</th>
                      <th>Source</th>
                      <th title="The strategy's specification: rule, exits, worked example">Spec</th>
                    </tr>
                  </thead>
                  <tbody>
                    {items.map((s) => {
                      const on = activeUnderlyings(s)
                      return (
                        <tr
                          key={s.id}
                          className={s.id === specId ? 'row--selected' : undefined}
                          onClick={() => setSpecId(s.id)}
                        >
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
                          <td>
                            <button
                              type="button"
                              className="btn btn--sm btn--ghost"
                              onClick={(e) => {
                                e.stopPropagation()
                                setSpecId(s.id)
                              }}
                            >
                              How it works
                            </button>
                          </td>
                        </tr>
                      )
                    })}
                  </tbody>
                </table>
              </div>
            </Panel>

            {spec && (
              <div ref={specRef}>
                <Panel
                  title={
                    <>
                      <IconLayers /> How it works — {spec.name}
                    </>
                  }
                  actions={
                    <>
                      <select
                        className="field__input field__input--sm"
                        aria-label="Strategy"
                        value={spec.id}
                        onChange={(e) => setSpecId(Number(e.target.value))}
                      >
                        {items.map((s) => (
                          <option key={s.id} value={s.id}>
                            {s.name}
                          </option>
                        ))}
                      </select>
                      <button
                        type="button"
                        className="btn btn--sm btn--ghost"
                        onClick={() => setSpecId(null)}
                        aria-label="Close the spec"
                      >
                        <IconX style={{ width: 13, height: 13 }} /> Close
                      </button>
                    </>
                  }
                >
                  <Suspense fallback={<Loading label="Loading spec…" />}>
                    <StrategySpecPanel strategyId={spec.id} />
                  </Suspense>
                </Panel>
              </div>
            )}
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
