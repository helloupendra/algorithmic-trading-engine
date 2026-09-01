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
import { useBrokerSession, useIngestorStatuses, useKillSwitch, useStrategies } from '../../lib/queries'
import { formatInrWhole } from '../../lib/format'
import { Badge, InlineError, Panel, QueryBoundary } from '../../components/ui'
import type { SimulationRun, StrategyListItem } from '../../lib/types'

const UNDERLYINGS = [
  { symbol: 'NSE:NIFTYBANK-INDEX', label: 'Bank Nifty' },
  { symbol: 'NSE:NIFTY50-INDEX', label: 'Nifty 50' },
]

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

  const blockers: { text: string; to: string }[] = []
  if (broker.data && !broker.data.isAuthenticated)
    blockers.push({ text: 'FYERS session expired — connect the broker first', to: '/admin/broker' })
  if (ingestors.data && (!ingestors.data.length || !ingestors.data.every((s) => s.isHealthy)))
    blockers.push({ text: 'Live ingestor is not running — the strategy will get no ticks', to: '/admin/ingestion' })
  if (killSwitch.data?.isActive)
    blockers.push({ text: 'Kill switch is ACTIVE — trading is halted', to: '/admin/risk' })

  const [selected, setSelected] = useState<StrategyListItem | null>(null)
  const [symbol, setSymbol] = useState(UNDERLYINGS[0].symbol)
  const [capital, setCapital] = useState(1_000_000)
  const [params, setParams] = useState<ParamRow[]>([])

  // Re-seed the parameter grid whenever a different template is chosen.
  useEffect(() => {
    if (selected) setParams(parseDefaults(selected.defaultParametersJson))
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
      const run = await api.post<SimulationRun>('/api/Simulator/runs', {
        userId: user!.id,
        mode: 'LivePaper',
        symbol,
        resolution: '1m',
        strategyName: selected!.name,
        parametersJson,
        initialCapital: capital,
      })
      await api.post(`/api/Strategy/${selected!.id}/deploy`, { runId: run.id })
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
                  disabled={s.isActive}
                  title={s.isActive ? 'Already running — stop it first' : undefined}
                >
                  <b className="mono">{s.name}</b>
                  <span>{s.description || 'No description'}</span>
                  {s.isActive && <Badge tone="pos">running</Badge>}
                </button>
              ))}
            </div>
          )}
        </QueryBoundary>
        {!selected && (
          <p className="muted small-note">
            👆 Kisi card par click karo — select hote hi neeche <b>Step 2 (Configure)</b> aur{' '}
            <b>Step 3 (Deploy)</b> khul jayenge.
          </p>
        )}
      </Panel>

      {selected && (
        <>
          <Panel title={`2 · Configure ${selected.name}`}>
            <div className="form-row" style={{ marginBottom: 18 }}>
              <div className="field">
                <label className="field__label" htmlFor="dp-underlying">Underlying</label>
                <select
                  id="dp-underlying"
                  className="field__input"
                  value={symbol}
                  onChange={(e) => setSymbol(e.target.value)}
                >
                  {UNDERLYINGS.map((u) => (
                    <option key={u.symbol} value={u.symbol}>{u.label}</option>
                  ))}
                </select>
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
              <div><span className="muted">Strategy</span><b className="mono">{selected.name}</b></div>
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
                disabled={deploy.isPending}
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
