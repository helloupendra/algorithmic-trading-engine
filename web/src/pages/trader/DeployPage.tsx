/**
 * Deploy wizard — the "pick, configure, run" experience:
 *
 *   1. choose a strategy template
 *   2. tune its parameters (pre-filled from the strategy's defaults),
 *      underlying and capital
 *   3. deploy to PAPER — a simulation run is created with your parameters and
 *      the Python runner is attached to it; you land on the live run page.
 *
 * Live mode is intentionally locked until the broker execution loop exists —
 * everything configured here will carry over unchanged when it does.
 */

import { useEffect, useMemo, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { useMutation } from '@tanstack/react-query'
import { api } from '../../lib/api'
import { useAuth } from '../../lib/auth'
import { useBrokerSession, useIngestorStatuses, useKillSwitch, useStrategies, useRiskExposure, useRiskLimits } from '../../lib/queries'
import { formatInrWhole } from '../../lib/format'
import { Badge, InlineError, Panel, QueryBoundary } from '../../components/ui'
import type { SimulationRun, StrategyListItem } from '../../lib/types'

// `underlying` is what the API derives from the spot symbol (UnderlyingCatalog)
// and what the registry keys a live run by — the same strategy may not run
// twice on one underlying, so the picker needs it to grey out taken rows.
const UNDERLYINGS = [
  { symbol: 'NSE:NIFTYBANK-INDEX', label: 'Bank Nifty', underlying: 'BANKNIFTY' },
  { symbol: 'NSE:NIFTY50-INDEX', label: 'Nifty 50', underlying: 'NIFTY' },
]

function underlyingFor(symbol: string): string | null {
  return UNDERLYINGS.find((u) => u.symbol === symbol)?.underlying ?? null
}

/** Underlyings the strategy is live on right now, upper-cased like the registry keys them. */
function takenUnderlyings(strategy: StrategyListItem | null): ReadonlySet<string> {
  return new Set((strategy?.activeRuns ?? []).map((r) => r.underlying.toUpperCase()))
}

function alreadyRunningMessage(name: string, underlying: string): string {
  return `${name} is already running on ${underlying} — stop that run or pick another underlying.`
}

interface ParamRow {
  key: string
  value: string
}

function parseDefaults(json: string): ParamRow[] {
  try {
    const obj = JSON.parse(json || '{}') as Record<string, unknown>
    return Object.entries(obj).map(([key, value]) => ({ key, value: String(value) }))
  } catch {
    return []
  }
}

/** "70" -> 70, "true" -> true, anything else stays a string. */
function coerce(value: string): unknown {
  const trimmed = value.trim()
  if (trimmed === 'true') return true
  if (trimmed === 'false') return false
  if (trimmed !== '' && !Number.isNaN(Number(trimmed))) return Number(trimmed)
  return trimmed
}

export function DeployPage() {
  const { user } = useAuth()
  const navigate = useNavigate()
  const strategies = useStrategies()

  const broker = useBrokerSession()
  const ingestors = useIngestorStatuses()
  const killSwitch = useKillSwitch()
  const exposure = useRiskExposure()
  const riskLimits = useRiskLimits()

  const blockers: { text: string; to: string }[] = []
  if (broker.data && !broker.data.isAuthenticated)
    blockers.push({ text: 'FYERS session expired — connect the broker first', to: '/admin/broker' })
  if (ingestors.data && (!ingestors.data.length || !ingestors.data.every((s) => s.isHealthy)))
    blockers.push({ text: 'Live ingestor is not running — the strategy will get no ticks', to: '/admin/ingestion' })
  if (killSwitch.data?.isActive)
    blockers.push({ text: 'Kill switch is ACTIVE — trading is halted', to: '/admin/system/risk' })
  if (exposure.data && riskLimits.data && exposure.data.activeRunsCount >= riskLimits.data.maxConcurrentRuns)
    blockers.push({ text: `Max concurrent runs limit reached (${exposure.data.activeRunsCount}/${riskLimits.data.maxConcurrentRuns})`, to: '/trader/overview' })

  const [selected, setSelected] = useState<StrategyListItem | null>(null)
  const [symbol, setSymbol] = useState(UNDERLYINGS[0].symbol)
  const [capital, setCapital] = useState(1_000_000)
  const [params, setParams] = useState<ParamRow[]>([])

  // The card click captured one snapshot of the strategy; read its live runs
  // from the polled list so a run started elsewhere greys its underlying out
  // (and a stopped one frees it) while the wizard is open.
  const current = useMemo(
    () => (selected ? (strategies.data?.find((s) => s.id === selected.id) ?? selected) : null),
    [strategies.data, selected],
  )
  const taken = useMemo(() => takenUnderlyings(current), [current])
  const chosenUnderlying = underlyingFor(symbol)
  const chosenTaken = chosenUnderlying != null && taken.has(chosenUnderlying)
  const allTaken = UNDERLYINGS.every((u) => taken.has(u.underlying))

  // Re-seed the parameter grid whenever a different template is chosen, and
  // move the underlying off one the strategy is already running on.
  useEffect(() => {
    if (!selected) return
    setParams(parseDefaults(selected.defaultParametersJson))
    const busy = takenUnderlyings(selected)
    setSymbol((chosen) => {
      const chosenUnderlyingNow = underlyingFor(chosen)
      if (chosenUnderlyingNow != null && !busy.has(chosenUnderlyingNow)) return chosen
      return UNDERLYINGS.find((u) => !busy.has(u.underlying))?.symbol ?? chosen
    })
  }, [selected])

  const parametersJson = useMemo(
    () =>
      JSON.stringify(
        Object.fromEntries(
          params.filter((p) => p.key.trim() !== '').map((p) => [p.key.trim(), coerce(p.value)]),
        ),
      ),
    [params],
  )

  const deploy = useMutation({
    mutationFn: async () => {
      const strategy = current!
      // Refuse BEFORE creating the run: the deploy call would answer 409 for a
      // taken underlying and the freshly created Pending row would be left
      // behind in the run history with no runner and no way to close it.
      const wanted = underlyingFor(symbol)
      if (wanted != null && takenUnderlyings(strategy).has(wanted)) {
        throw new Error(alreadyRunningMessage(strategy.name, wanted))
      }
      const run = await api.post<SimulationRun>('/api/Simulator/runs', {
        userId: user!.id,
        mode: 'LivePaper',
        symbol,
        resolution: '1m',
        strategyName: strategy.name,
        parametersJson,
        initialCapital: capital,
      })
      await api.post(`/api/Strategy/${strategy.id}/deploy`, { runId: run.id })
      return run
    },
    onSuccess: (run) => navigate(`/trader/runs/${run.id}?deployed=1`),
  })

  return (
    <div className="page">
      <header className="page__header">
        <h1 className="page__title">Deploy a strategy</h1>
        <p className="page__subtitle">
          Three steps on this one page: <b>click a strategy card below</b> → its settings
          open in step 2 → review and press Deploy in step 3. No code.
        </p>
      </header>

      {blockers.length > 0 && (
        <div className="alert alert--error" role="alert">
          <b>Not ready to deploy:</b>
          {blockers.map((b) => (
            <div key={b.to}>
              • {b.text} — <Link to={b.to}>fix →</Link>
            </div>
          ))}
        </div>
      )}

      <Panel title="1 · Choose a strategy — click a card to select it">
        <QueryBoundary query={strategies} empty="No strategies registered yet.">
          {(list) => (
            <div className="deploy-grid">
              {list.map((s) => (
                <button
                  key={s.id}
                  type="button"
                  className={`deploy-card ${selected?.id === s.id ? 'is-selected' : ''}`}
                  onClick={() => setSelected(s)}
                  title={
                    s.activeRuns.length > 0
                      ? `Already running on ${s.activeRuns.map((r) => r.underlying).join(', ')} — deploy on a different underlying`
                      : undefined
                  }
                >
                  <b className="mono">{s.name}</b>
                  <span>{s.description || 'No description'}</span>
                  {s.isActive && (
                    <Badge tone="pos">
                      running
                      {s.activeRuns.length > 0 ? ` · ${s.activeRuns.map((r) => r.underlying).join(', ')}` : ''}
                    </Badge>
                  )}
                </button>
              ))}
            </div>
          )}
        </QueryBoundary>
        {!selected && (
          <p className="muted small-note">
            Select a strategy card — <b>Step 2 (Configure)</b> and <b>Step 3 (Deploy)</b> unlock
            below once one is chosen.
          </p>
        )}
      </Panel>

      {selected && current && (
        <>
          <Panel title={`2 · Configure ${current.name}`}>
            <div className="form-row" style={{ marginBottom: 18 }}>
              <div className="field">
                <label className="field__label" htmlFor="dp-underlying">Underlying</label>
                <select
                  id="dp-underlying"
                  className="field__input"
                  value={symbol}
                  onChange={(e) => setSymbol(e.target.value)}
                  aria-invalid={chosenTaken || undefined}
                >
                  {UNDERLYINGS.map((u) => {
                    const busy = taken.has(u.underlying)
                    return (
                      <option key={u.symbol} value={u.symbol} disabled={busy}>
                        {busy ? `${u.label} — already running` : u.label}
                      </option>
                    )
                  })}
                </select>
                {allTaken ? (
                  <p className="muted small-note" style={{ marginBottom: 0 }}>
                    {current.name} is already running on every underlying offered here — stop a
                    run first.
                  </p>
                ) : chosenTaken && chosenUnderlying ? (
                  <p className="muted small-note" style={{ marginBottom: 0 }}>
                    {alreadyRunningMessage(current.name, chosenUnderlying)}
                  </p>
                ) : null}
              </div>
              <div className="field">
                <label className="field__label" htmlFor="dp-capital">
                  Virtual capital ({formatInrWhole(capital)})
                </label>
                <input
                  id="dp-capital"
                  className="field__input"
                  type="number"
                  min={100000}
                  step={100000}
                  value={capital}
                  onChange={(e) => setCapital(Number(e.target.value))}
                />
              </div>
              <div className="field">
                <label className="field__label">Mode</label>
                <div className="chip-row" style={{ paddingTop: 6 }}>
                  <Badge tone="accent">Paper trading</Badge>
                  <Badge tone="neutral">Live — unlocks with the execution loop</Badge>
                </div>
              </div>
            </div>

            <h4 style={{ marginTop: 0 }}>Parameters</h4>
            {params.length === 0 && (
              <p className="muted small-note" style={{ marginTop: 0 }}>
                This strategy declares no default parameters — add any it understands.
              </p>
            )}
            <div className="param-grid">
              {params.map((p, i) => (
                <div key={i} className="param-row">
                  <input
                    className="field__input field__input--sm"
                    placeholder="parameter"
                    value={p.key}
                    aria-label={`Parameter ${i + 1} name`}
                    onChange={(e) =>
                      setParams(params.map((x, j) => (j === i ? { ...x, key: e.target.value } : x)))
                    }
                  />
                  <input
                    className="field__input field__input--sm"
                    placeholder="value"
                    value={p.value}
                    aria-label={`Parameter ${i + 1} value`}
                    onChange={(e) =>
                      setParams(params.map((x, j) => (j === i ? { ...x, value: e.target.value } : x)))
                    }
                  />
                  <button
                    type="button"
                    className="btn btn--ghost btn--sm"
                    onClick={() => setParams(params.filter((_, j) => j !== i))}
                    aria-label={`Remove parameter ${i + 1}`}
                  >
                    ✕
                  </button>
                </div>
              ))}
            </div>
            <button
              type="button"
              className="btn btn--ghost btn--sm"
              onClick={() => setParams([...params, { key: '', value: '' }])}
            >
              + Add parameter
            </button>
          </Panel>

          <Panel title="3 · Review & deploy">
            <div className="kv-grid" style={{ marginBottom: 16 }}>
              <div><span className="muted">Strategy</span><b className="mono">{current.name}</b></div>
              <div><span className="muted">Underlying</span><b className="mono">{symbol.replace('NSE:', '')}</b></div>
              <div><span className="muted">Capital</span><b>{formatInrWhole(capital)}</b></div>
              <div><span className="muted">Mode</span><b>Paper (LivePaper)</b></div>
              <div style={{ gridColumn: '1 / -1' }}>
                <span className="muted">parametersJson</span>
                <b className="mono" style={{ fontSize: '0.78rem', wordBreak: 'break-all' }}>{parametersJson}</b>
              </div>
            </div>

            {deploy.isError && <InlineError error={deploy.error} />}

            <div className="toolbar">
              <button
                type="button"
                className="btn btn--primary"
                disabled={deploy.isPending || chosenTaken}
                title={
                  chosenTaken && chosenUnderlying
                    ? alreadyRunningMessage(current.name, chosenUnderlying)
                    : undefined
                }
                onClick={() => deploy.mutate()}
              >
                {deploy.isPending ? 'Deploying…' : 'Deploy on paper'}
              </button>
              <span className="muted small-note" style={{ marginTop: 0 }}>
                Creates a run with your parameters and attaches the strategy runner to it.
                Fills need live market data — start the ingestor during market hours.
              </span>
            </div>
          </Panel>
        </>
      )}

      <p className="muted small-note">
        Already deployed something? See <Link to="/trader/strategies">Strategies → run history</Link>.
      </p>
    </div>
  )
}
