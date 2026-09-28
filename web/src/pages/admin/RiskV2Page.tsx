/**
 * Risk & kill switch — the controls that stop money moving.
 *
 * Lives under the System module. The kill switch is deliberately the first thing
 * on the page and the only destructive control on it: an operator reaching for
 * this page in a hurry should not have to look for it.
 */

import { useEffect, useState } from 'react'
import { useKillSwitch, useLiveRiskExposure, useRiskEvents, useRiskLimits, useSetKillSwitch, useUpdateRiskLimits } from '../../lib/queries'
import type { RiskLimits } from '../../lib/types'
import { formatDateTime, formatInr } from '../../lib/format'
import { Badge, EmptyState, InlineError, Panel, QueryBoundary, StatTile } from '../../components/ui'
import './risk.css'


function KillSwitchPanel() {
  const killSwitch = useKillSwitch()
  const setKillSwitch = useSetKillSwitch()
  const [reason, setReason] = useState('')

  const active = killSwitch.data?.isActive ?? false

  return (
    <Panel title="Kill switch">
      {setKillSwitch.isError && <InlineError error={setKillSwitch.error} />}
      <div className="broker-state">
        <div>
          <div className={`killswitch__state ${active ? 'neg' : 'pos'}`}>
            {active ? 'ACTIVE — trading halted' : 'Trading allowed'}
          </div>
          <p className="muted" style={{ maxWidth: '78ch' }}>
            {active ? (
              <>
                Every strategy is paused and open positions were flattened when it was pulled.
                {killSwitch.data?.reason && <> Reason: “{killSwitch.data.reason}”.</>}{' '}
                {killSwitch.data?.updatedBy && (
                  <>
                    By {killSwitch.data.updatedBy} at {formatDateTime(killSwitch.data.updatedUtc)}.
                  </>
                )}
              </>
            ) : (
              <>
                Pulling this pauses every strategy <b>and squares off every open position</b> at the
                last mark. It is announced to Telegram immediately.
              </>
            )}
          </p>
        </div>
        <div className="broker-state__actions">
          <button
            type="button"
            className={active ? 'btn btn--primary' : 'btn btn--danger'}
            disabled={setKillSwitch.isPending}
            onClick={() =>
              setKillSwitch.mutate(
                { activate: !active, reason: reason.trim() || (active ? 'Released from console' : 'Pulled from console') },
                { onSuccess: () => setReason('') },
              )
            }
          >
            {setKillSwitch.isPending
              ? 'Working…'
              : active
                ? 'Release the kill switch'
                : 'Pull the kill switch'}
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
  const [form, setForm] = useState<RiskLimits | null>(null)

  useEffect(() => {
    if (limits.data && form === null) setForm(limits.data)
  }, [limits.data, form])

  const field = (
    key: keyof Pick<
      RiskLimits,
      'maxOrdersPerMinute' | 'maxDailyLoss' | 'maxConcurrentRuns' | 'maxRunsPerUser'
    >,
    label: string,
    hint: string,
  ) => (
    <div className="field">
      <label className="field__label" htmlFor={`rl-${key}`}>
        {label}
      </label>
      <input
        id={`rl-${key}`}
        className="field__input"
        type="number"
        min={0}
        value={form?.[key] ?? 0}
        onChange={(e) => form && setForm({ ...form, [key]: Number(e.target.value) })}
      />
      <p className="field__help">{hint}</p>
    </div>
  )

  return (
    <Panel title="Trading limits" className="limits-panel">
      {update.isError && <InlineError error={update.error} />}
      {update.isSuccess && (
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
                button sat in the grid like a fifth field. */}
            <form
              onSubmit={(e) => {
                e.preventDefault()
                if (form) update.mutate(form)
              }}
            >
              <div className="limits-grid">
                {field('maxOrdersPerMinute', 'Max orders / minute', 'Throttles a runaway strategy.')}
                {field('maxDailyLoss', 'Max daily loss (₹)', 'Per run, before charges: a run below it cannot open new positions; exits stay allowed.')}
                {field('maxConcurrentRuns', 'Max concurrent runs', 'Total live runners allowed at once.')}
                {field('maxRunsPerUser', 'Max runs per trader', 'How many one trader may hold open.')}
              </div>
              <div className="limits-actions">
                <button className="btn btn--primary" disabled={update.isPending || !form}>
                  {update.isPending ? 'Saving…' : 'Save limits'}
                </button>
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
