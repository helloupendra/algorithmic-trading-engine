/**
 * Strategies, for a trader: the same cards as the admin library, "How it
 * works" for each, and Deploy — which opens the same launch dialog the
 * admin's live runner uses.
 *
 * It used to be a three-step wizard of its own, with a hand-written list of
 * two underlyings (Bank Nifty, Nifty 50) and a simulation-run-plus-attach
 * path. That list is why CrudeMomentum — an MCX strategy — could be deployed
 * on BANKNIFTY and never on crude. The launch dialog reads each strategy's
 * own supported underlyings, resolves the contract (the nearest crude future
 * for CRUDEOIL), takes lots and risk rules, and starts through the one API
 * every other run goes through.
 */
import { useEffect, useState } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import { useKillSwitch, useStrategies, useRiskExposure, useRiskLimits } from '../../lib/queries'
import { Panel, QueryBoundary } from '../../components/ui'
import { LaunchDialog, StrategyCard } from '../strategies/shared'
import type { StrategyListItem } from '../../lib/types'

export function DeployPage() {
  const navigate = useNavigate()
  const strategies = useStrategies()

  const killSwitch = useKillSwitch()
  const exposure = useRiskExposure()
  const riskLimits = useRiskLimits()

  // Only what concerns the trader: a halt they must respect and a ceiling
  // they have hit. The broker link and the feed are the operator's job — a
  // trader is never told "FYERS is not signed in", because there is nothing
  // they can do about it; the operator sees it in the top bar instead.
  const blockers: { text: string; to: string }[] = []
  if (killSwitch.data?.isActive)
    blockers.push({ text: 'Trading is halted by the operator (kill switch) — new runs are refused', to: '/trader' })
  if (exposure.data && riskLimits.data && exposure.data.activeRunsCount >= riskLimits.data.maxConcurrentRuns)
    blockers.push({ text: `Concurrent runs limit reached (${exposure.data.activeRunsCount}/${riskLimits.data.maxConcurrentRuns}) — stop a run first`, to: '/trader/strategies/history' })

  const [launching, setLaunching] = useState<StrategyListItem | null>(null)

  // "Deploy this strategy…" on a How-it-works page lands here with the
  // strategy in the address; the dialog opens on it as soon as the catalog
  // arrives, and the address is cleaned so a refresh does not reopen it.
  const [search, setSearch] = useSearchParams()
  useEffect(() => {
    const wanted = Number(search.get('strategy'))
    if (!Number.isInteger(wanted) || wanted <= 0 || !strategies.data) return
    const found = strategies.data.find((s) => s.id === wanted)
    if (found) setLaunching(found)
    setSearch({}, { replace: true })
  }, [search, setSearch, strategies.data])

  return (
    <div className="page">
      <header className="page__header">
        <h1 className="page__title">Strategies</h1>
        <p className="page__subtitle">
          Every strategy your package allows. <b>How it works</b> is the full write-up; <b>Deploy</b> asks
          for the underlying, lots and risk rules and starts it on paper.
        </p>
      </header>

      {blockers.length > 0 && (
        <div className="alert alert--error" role="alert">
          <b>Not ready to trade:</b>
          {blockers.map((b) => (
            <div key={b.text}>
              • {b.text} — <Link to={b.to}>see →</Link>
            </div>
          ))}
        </div>
      )}

      <Panel title="Pick a strategy">
        <QueryBoundary query={strategies} empty="No strategies in your package yet — ask the operator.">
          {(list) => (
            <div className="strategy-grid">
              {list.map((s) => (
                <StrategyCard
                  key={s.id}
                  strategy={s}
                  actionLabel="Deploy…"
                  specHref={`/trader/strategies/${s.id}/how-it-works`}
                  onStart={(st) => setLaunching(st)}
                />
              ))}
            </div>
          )}
        </QueryBoundary>
      </Panel>

      <p className="muted small-note">
        Running and finished runs are under <Link to="/trader/strategies/history">My runs</Link>.
      </p>

      {launching && (
        <LaunchDialog
          strategy={launching}
          onClose={() => setLaunching(null)}
          onStarted={(r) => navigate(`/trader/strategies/runs/${r.runId}`)}
        />
      )}
    </div>
  )
}
