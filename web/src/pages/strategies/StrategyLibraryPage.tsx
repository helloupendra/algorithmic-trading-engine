/**
 * Strategies module — Library. The catalogue as cards, each with the launch
 * dialog the Live runner uses and a "How it works" link to the strategy's
 * own page (StrategySpecPage: everything the catalog knows about it, then
 * its specification). The dense reference table that used to sit under the
 * cards is gone — its eight columns needed a horizontal scroll to reach the
 * one button that mattered.
 */

import { useEffect, useState } from 'react'
import { useNavigate, useSearchParams } from 'react-router-dom'
import { useStrategies } from '../../lib/queries'
import { QueryBoundary } from '../../components/ui'
import type { StrategyListItem } from '../../lib/types'
import { LaunchDialog, StrategyCard } from './shared'

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
