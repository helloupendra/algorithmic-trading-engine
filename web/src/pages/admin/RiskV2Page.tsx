/**
 * Risk & kill switch — the controls that stop money moving.
 *
 * Lives under the System module. The kill switch is deliberately the first thing
 * on the page and the only destructive control on it: an operator reaching for
 * this page in a hurry should not have to look for it.
 */

import { useState } from 'react'
import { useKillSwitch, useLiveRiskExposure, useRiskEvents, useRiskLimits, useSetKillSwitch, useUpdateRiskLimits } from '../../lib/queries'
import {
  LIMIT_RANGES,
  dailyLossHelp,
  draftDiffers,
  draftToLimits,
  killSwitchConfirmText,
  killSwitchView,
  limitsToDraft,
} from '../../lib/riskLimits'
import type { LimitsDraft, LimitsField } from '../../lib/riskLimits'
import { formatDateTime, formatInr, formatInrWhole } from '../../lib/format'
import { Badge, EmptyState, InlineError, Panel, QueryBoundary, StatTile } from '../../components/ui'
import './risk.css'


function KillSwitchPanel() {
  const killSwitch = useKillSwitch()
  const setKillSwitch = useSetKillSwitch()
  const [reason, setReason] = useState('')

  // Only a state the API has answered is shown as a fact: a failed first read
  // is "Status unknown", never "Trading allowed" (the top bar's "Kill switch ?").
  const view = killSwitchView(killSwitch)
  const known = view.kind === 'on' || view.kind === 'off'
  const active = view.kind === 'on'
  const state = known ? view.state : null

  function toggle() {
    if (!known) return
    const activate = !active
    // It flattens every account at once, or lets every account trade again: never on one click.
    if (!window.confirm(killSwitchConfirmText(activate))) return
    setKillSwitch.mutate(
      { activate, reason: reason.trim() || (active ? 'Released from console' : 'Pulled from console') },
      { onSuccess: () => setReason('') },
    )
  }

  return (
    <Panel title="Kill switch">
      {setKillSwitch.isError && <InlineError error={setKillSwitch.error} />}
      <div className="broker-state">
        <div>
          <div className={`killswitch__state ${active ? 'neg' : view.kind === 'off' ? 'pos' : 'warn'}`} role="status">
            {view.kind === 'on'
              ? 'ACTIVE — trading halted'
              : view.kind === 'off'
                ? 'Trading allowed'
                : view.kind === 'unknown'
                  ? 'Status unknown'
                  : 'Reading the kill switch…'}
          </div>
          <p className="muted" style={{ maxWidth: '78ch' }}>
            {view.kind === 'unknown' ? (
              <>
                The kill switch could not be read, so whether trading is halted is not known. It is read again every
                few seconds; it cannot be pulled or released from here until its state is known.
              </>
            ) : view.kind === 'reading' ? null : active ? (
              <>
                Every strategy is paused and open positions were flattened when it was pulled.
                {state?.reason && <> Reason: “{state.reason}”.</>}{' '}
                {state?.updatedBy && (
                  <>
                    By {state.updatedBy} at {formatDateTime(state.updatedUtc)}.
                  </>
                )}
              </>
            ) : (
              <>
                Pulling this pauses every strategy <b>and squares off every open position</b> in every account at the
                last mark. It is announced to Telegram immediately.
              </>
            )}
          </p>
          {known && view.stale && (
            <p className="small-note warn" style={{ maxWidth: '78ch' }}>
              The latest read failed: this is the last answer the API gave, and it may have changed since.
            </p>
          )}
          {view.kind === 'unknown' && <InlineError error={killSwitch.error} />}
        </div>
        <div className="broker-state__actions">
          <button
            type="button"
            className={active ? 'btn btn--primary' : 'btn btn--danger'}
            disabled={!known || setKillSwitch.isPending}
            onClick={toggle}
          >
            {setKillSwitch.isPending
              ? 'Working…'
              : active
                ? 'Release the kill switch…'
                : 'Pull the kill switch…'}
          </button>
        </div>
      </div>
      <div className="field" style={{ maxWidth: '48ch', marginTop: 10 }}>
        <label className="field__label" htmlFor="ks-reason">
          Reason (recorded, and sent with the alert)
        </label>
        <input
          id="ks-reason"
          className="field__input"
          placeholder={active ? 'why it is safe to resume' : 'what went wrong'}
          value={reason}
          onChange={(e) => setReason(e.target.value)}
        />
      </div>
    </Panel>
  )
}

function LimitsPanel() {
  const limits = useRiskLimits()
  const update = useUpdateRiskLimits()
  // The admin's own text once they type, else null: an untouched form shows
  // the stored limits as the 5 s poll reads them, so it never saves over a
  // newer change it never showed. Saved or reset, it follows the poll again.
  const [draft, setDraft] = useState<LimitsDraft | null>(null)
  const [errors, setErrors] = useState<Partial<Record<LimitsField, string>>>({})
  const shown = draft ?? (limits.data ? limitsToDraft(limits.data) : null)
  const dirty = draft != null && limits.data != null && draftDiffers(draft, limits.data)

  const edit = (key: LimitsField, value: string) => {
    if (!shown) return
    update.reset()
    setErrors((e) => ({ ...e, [key]: undefined }))
    setDraft({ ...shown, [key]: value })
  }

  const field = (key: LimitsField, label: string, hint: string) => {
    const range = LIMIT_RANGES[key]
    const error = errors[key]
    return (
      <div className="field">
        <label className="field__label" htmlFor={`rl-${key}`}>
          {label}
        </label>
        <input
          id={`rl-${key}`}
          className="field__input"
          type="number"
          inputMode={range.whole ? 'numeric' : 'decimal'}
          min={range.min}
          max={range.max}
          step={range.whole ? 1 : 'any'}
          value={shown?.[key] ?? ''}
          aria-invalid={error ? true : undefined}
          aria-describedby={`rl-${key}-help`}
          onChange={(e) => edit(key, e.target.value)}
        />
        {error && (
          <p className="field__error" role="alert">
            {error}
          </p>
        )}
        <p className="field__help" id={`rl-${key}-help`}>
          {hint}
        </p>
      </div>
    )
  }

  const lossTyped = shown?.maxDailyLossAmount.trim() ?? ''
  const lossAmount = lossTyped === '' ? null : Number(lossTyped)

  return (
    <Panel title="Trading limits" className="limits-panel">
      {update.isError && <InlineError error={update.error} />}
      {update.isSuccess && draft == null && (
        <div className="alert alert--success" role="status">
          Limits saved.
        </div>
      )}
      <QueryBoundary query={limits}>
        {(data) => (
          <>
            <p className="small-note muted limits-source">
              In force from <Badge tone="accent">{data.source}</Badge>
              {data.updatedBy && (
                <> · last changed by {data.updatedBy} at {formatDateTime(data.updatedUtc)}</>
              )}
            </p>
            {/* Not .form-row: that aligns fields on their bottom edge, so the one
                with two lines of help floated above the other three and the
                button sat in the grid like a fifth field.
                noValidate: every field is checked against the API's own ranges
                (draftToLimits) and the problem said under it. The browser's
                own check once refused the stored daily loss (negative, under
                min=0) and blocked every save without a word on the page. */}
            <form
              noValidate
              onSubmit={(e) => {
                e.preventDefault()
                if (!shown) return
                const checked = draftToLimits(shown)
                if (!checked.ok) {
                  setErrors(checked.errors)
                  return
                }
                setErrors({})
                update.mutate(checked.limits, { onSuccess: () => setDraft(null) })
              }}
            >
              <div className="limits-grid">
                {field('maxOrdersPerMinute', 'Max orders / minute', 'Per run, 1 to 10,000: throttles a runaway strategy.')}
                {field('maxDailyLossAmount', 'Max daily loss per run (₹)', dailyLossHelp(lossAmount, formatInrWhole))}
                {field('maxConcurrentRuns', 'Max concurrent runs', 'Total live runners allowed at once; 0 means no cap.')}
                {field('maxRunsPerUser', 'Max runs per trader', 'How many one trader may hold open; 0 means no cap.')}
              </div>
              <div className="limits-actions">
                <button type="submit" className="btn btn--primary" disabled={update.isPending || !dirty}>
                  {update.isPending ? 'Saving…' : 'Save limits'}
                </button>
                {draft != null && (
                  <button
                    type="button"
                    className="btn"
                    disabled={update.isPending}
                    onClick={() => {
                      setDraft(null)
                      setErrors({})
                    }}
                  >
                    Reset
                  </button>
                )}
              </div>
            </form>
          </>
        )}
      </QueryBoundary>
    </Panel>
  )
}

function ExposurePanel() {
  const exposure = useLiveRiskExposure()

  return (
    <Panel title="What is at risk right now">
      <QueryBoundary query={exposure}>
        {(data) => (
          <>
            <div className="stat-grid">
              <StatTile label="Active runs" value={String(data.activeRunsCount)} />
              <StatTile
                label="Unrealised P&L"
                value={formatInr(data.totalUnrealizedPnL)}
                tone={data.totalUnrealizedPnL >= 0 ? 'pos' : 'neg'}
              />
              <StatTile
                label="Realised P&L"
                value={formatInr(data.totalRealizedPnL)}
                tone={data.totalRealizedPnL >= 0 ? 'pos' : 'neg'}
              />
            </div>

            {data.activeRuns.length === 0 ? (
              <EmptyState>No runs are live, so nothing is exposed.</EmptyState>
            ) : (
              <div className="tablewrap">
                <table className="table">
                  <thead>
                    <tr>
                      <th>Run</th>
                      <th>Strategy</th>
                      <th>Underlying</th>
                      <th className="r">Unrealised</th>
                      <th className="r">Realised</th>
                    </tr>
                  </thead>
                  <tbody>
                    {data.activeRuns.map((run) => (
                      <tr key={run.runId}>
                        <td className="mono">#{run.runId}</td>
                        <td>{run.strategyName}</td>
                        <td>{run.underlying}</td>
                        <td className={`r ${run.unrealizedPnL >= 0 ? 'pos' : 'neg'}`}>
                          {formatInr(run.unrealizedPnL)}
                        </td>
                        <td className={`r ${run.realizedPnL >= 0 ? 'pos' : 'neg'}`}>
                          {formatInr(run.realizedPnL)}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </>
        )}
      </QueryBoundary>
    </Panel>
  )
}

function RiskEventsPanel() {
  const events = useRiskEvents(50)

  return (
    <Panel title="Risk log">
      {/* One state at a time: a failed read used to show its error AND a bare
          table head with no rows under it. */}
      <QueryBoundary query={events} empty="Nothing recorded yet.">
        {(rows) => (
          <div className="tablewrap tablewrap--tall">
            <table className="table">
              <thead>
                <tr>
                  <th>When</th>
                  <th>Event</th>
                  <th>By</th>
                  <th>Reason</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((ev) => (
                  <tr key={ev.id}>
                    <td className="mono">{formatDateTime(ev.occurredUtc)}</td>
                    <td>
                      {ev.kind}
                      {(ev.symbol || ev.simulationRunId) && (
                        <div className="small-note muted mono">
                          {ev.symbol}
                          {ev.symbol && ev.simulationRunId ? ' · ' : ''}
                          {ev.simulationRunId ? `run #${ev.simulationRunId}` : ''}
                        </div>
                      )}
                    </td>
                    <td>{ev.actorName ?? <span className="muted">system</span>}</td>
                    <td className="muted">{ev.reason}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </QueryBoundary>
    </Panel>
  )
}

export function RiskV2Page() {
  return (
    <div className="page">
      <header className="page__header">
        <div>
          <h1 className="page__title">Risk &amp; kill switch</h1>
          <p className="page__subtitle">
            The controls that stop money moving, what is exposed right now, and the log of every risk
            action taken.
          </p>
        </div>
      </header>

      <KillSwitchPanel />
      <ExposurePanel />
      <LimitsPanel />
      <RiskEventsPanel />
    </div>
  )
}
