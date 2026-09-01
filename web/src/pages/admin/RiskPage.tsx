/**
 * The kill switch. Activating it blocks every new order AND flattens all open
 * positions, so the button demands a typed confirmation — this is the one
 * control on the platform that must never be pressed by accident.
 */

import { useState } from 'react'
import { useKillSwitch, useSetKillSwitch } from '../../lib/queries'
import { formatDateTime } from '../../lib/format'
import { InlineError, Panel, QueryBoundary } from '../../components/ui'

export function RiskPage() {
  const killSwitch = useKillSwitch()
  const setKillSwitch = useSetKillSwitch()
  const [reason, setReason] = useState('')
  const [confirmText, setConfirmText] = useState('')

  const isActive = killSwitch.data?.isActive ?? false
  const confirmed = confirmText.trim().toUpperCase() === 'HALT'

  function activate() {
    if (!confirmed) return
    setKillSwitch.mutate(
      { activate: true, reason: reason.trim() || 'manual halt' },
      { onSuccess: () => { setReason(''); setConfirmText('') } },
    )
  }

  function deactivate() {
    setKillSwitch.mutate(
      { activate: false, reason: reason.trim() || 'resuming trading' },
      { onSuccess: () => setReason('') },
    )
  }

  return (
    <div className="page">
      <header className="page__header">
        <h1 className="page__title">Risk &amp; kill switch</h1>
        <p className="page__subtitle">
          Global trading halt. Activation also flattens every open position.
        </p>
      </header>

      <QueryBoundary query={killSwitch}>
        {(state) => (
          <Panel className={state.isActive ? 'panel--danger' : ''}>
            <div className="killswitch">
              <div>
                <div className={`killswitch__state ${state.isActive ? 'neg' : 'pos'}`}>
                  {state.isActive ? 'TRADING HALTED' : 'Trading enabled'}
                </div>
                <p className="muted">
                  {state.updatedUtc
                    ? `Last changed by ${state.updatedBy ?? 'unknown'} · ${formatDateTime(state.updatedUtc)} · "${state.reason ?? ''}"`
                    : 'Never toggled on this installation.'}
                </p>
              </div>
            </div>
          </Panel>
        )}
      </QueryBoundary>

      <Panel title={isActive ? 'Resume trading' : 'Halt trading'}>
        {setKillSwitch.isError && <InlineError error={setKillSwitch.error} />}
        <div className="field">
          <label className="field__label" htmlFor="ks-reason">Reason (recorded in the audit trail)</label>
          <input
            id="ks-reason"
            className="field__input"
            value={reason}
            onChange={(e) => setReason(e.target.value)}
            placeholder={isActive ? 'why is it safe to resume?' : 'why are we halting?'}
          />
        </div>

        {!isActive ? (
          <>
            <div className="field">
              <label className="field__label" htmlFor="ks-confirm">
                Type <b>HALT</b> to enable the button — this cancels nothing gracefully; it
                flattens all open positions at market.
              </label>
              <input
                id="ks-confirm"
                className="field__input"
                value={confirmText}
                onChange={(e) => setConfirmText(e.target.value)}
                autoComplete="off"
              />
            </div>
            <button
              type="button"
              className="btn btn--danger"
              disabled={!confirmed || setKillSwitch.isPending}
              onClick={activate}
            >
              {setKillSwitch.isPending ? 'Halting…' : 'Activate kill switch'}
            </button>
          </>
        ) : (
          <button
            type="button"
            className="btn btn--primary"
            disabled={setKillSwitch.isPending}
            onClick={deactivate}
          >
            {setKillSwitch.isPending ? 'Resuming…' : 'Deactivate kill switch'}
          </button>
        )}
      </Panel>

      <Panel title="Static limits (from configuration)">
        <p className="muted">
          Order rate and daily-loss caps are configured in <code>appsettings</code>{' '}
          (<code>RiskManagement:MaxOrdersPerMinute</code>, <code>RiskManagement:MaxDailyLoss</code>)
          and enforced by the API on every order. Exposing them read/write over HTTP is on the
          roadmap; until then this screen controls only the kill switch.
        </p>
      </Panel>
    </div>
  )
}
