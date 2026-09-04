/**
 * Strategies module — the run card: one RUN (a strategy on one underlying)
 * with its spot, P&L, risk rules, every position as a row (closed legs stay
 * with quantity 0), and the activity / runner-output disclosures.
 *
 * Shared by the Live runner (one card per running or just-stopped run) and
 * the Run history detail page (one card for any run ever started, read-only
 * for a trader who did not start it). Everything is keyed by runId — a
 * strategy may be live on several underlyings at once.
 */

import { useEffect, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import { useStopStrategy, useStrategyLive, useStrategyLogs, useUpdateRunRisk } from '../../lib/queries'
import { formatAge, formatInrWhole, formatLots, formatNumber, formatPrice, formatTime } from '../../lib/format'
import { formatContract } from '../../lib/symbols'
import { positionValues } from '../../lib/positions'
import { effectiveRisk, isRiskEmpty, parseRiskDraft, riskChips, riskDraftFrom } from '../../lib/risk'
import type { RiskDraft, RiskDraftField } from '../../lib/risk'
import { Badge, FlashPrice, InlineError, Loading } from '../../components/ui'
import { RiskRulesForm } from '../../components/RiskRulesForm'
import { IconShield, IconStop } from '../../components/icons'
import type {
  LivePosition,
  RiskRules,
  StrategyActiveRun,
  StrategyLastExit,
  StrategyLiveView,
} from '../../lib/types'
import {
  ActivityList,
  CategoryBadge,
  ConsoleOutput,
  Disclosure,
  PnlValue,
  PositionPnlCell,
  PositionValueCell,
} from './shared'

/* ------------------------------------------------------------------ helpers */

function contractLabel(p: LivePosition): string {
  return p.contract?.label || formatContract(p.symbol)
}

/** What the card needs to know about the strategy — a catalogue item fits, so does a history row. */
export interface RunCardStrategy {
  name: string
  category: string | null
}

/* ------------------------------------------------------------- risk metric */

function RiskMetric({
  label,
  limit,
  used,
  kind,
}: {
  label: string
  limit: number
  used: number
  kind: 'stop' | 'target'
}) {
  const pct = limit > 0 ? Math.min(100, (Math.max(0, used) / limit) * 100) : 0
  const modifier =
    kind === 'target' ? 'progress__bar--pos' : pct >= 100 ? 'progress__bar--neg' : pct >= 70 ? 'progress__bar--warn' : ''
  return (
    <div className="metric">
      <div className="metric__label">{label}</div>
      <div className="metric__value">{formatInrWhole(limit)}</div>
      <div className="metric__sub">{pct.toFixed(0)}% of the way</div>
      <div
        className="progress"
        role="progressbar"
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={Math.round(pct)}
        aria-label={label}
      >
        <div className={`progress__bar ${modifier}`} style={{ width: `${pct}%` }} />
      </div>
    </div>
  )
}

/* ------------------------------------------------------------ risk section */

/**
 * The run's risk rules at all three levels: chips for what is set, the
 * overall stop-loss / target progress bars, and — while the run is live and
 * the viewer may control it — an inline editor whose Save PATCHes the rules
 * so the guard applies them on its next sweep. Shown even when nothing is
 * set, so the operator sees "No risk rules · Set" rather than a silent
 * absence of protection.
 */
function RiskSection({
  runId,
  view,
  isActive,
  canEdit,
}: {
  runId: number
  view: StrategyLiveView
  isActive: boolean
  canEdit: boolean
}) {
  const update = useUpdateRunRisk()
  const [editing, setEditing] = useState(false)
  const [draft, setDraft] = useState<RiskDraft>(riskDraftFrom(null))
  const [invalidField, setInvalidField] = useState<RiskDraftField | null>(null)
  // Bumped on every failed save so the form re-focuses the same field on a repeat.
  const [invalidNonce, setInvalidNonce] = useState(0)
  const [validation, setValidation] = useState<string | null>(null)

  // The three-level object when the API sends one, else the overall
  // shorthands an older build carries.
  const rules: RiskRules = effectiveRisk(view)
  const chips = riskChips(rules)
  const empty = isRiskEmpty(rules)
  const total = view.pnl.total
  const overallSl = rules.overall?.stopLoss ?? null
  const overallTg = rules.overall?.target ?? null
  const groups = (view.groups ?? []).filter((g) => g.openLegs > 0)
  const showGroups = !!rules.group && groups.length > 0

  // The run ended while the editor was open: there is nothing left to PATCH.
  useEffect(() => {
    if (!isActive && editing) setEditing(false)
  }, [isActive, editing])

  function beginEdit() {
    setDraft(riskDraftFrom(rules))
    setInvalidField(null)
    setValidation(null)
    update.reset()
    setEditing(true)
  }

  function cancel() {
    setEditing(false)
    setInvalidField(null)
    setValidation(null)
    update.reset()
  }

  function save() {
    setValidation(null)
    setInvalidField(null)
    const parsed = parseRiskDraft(draft)
    if (parsed.rules === null) {
      setInvalidField(parsed.field)
      setInvalidNonce((n) => n + 1)
      setValidation(parsed.error)
      return
    }
    update.mutate({ runId, risk: parsed.rules }, { onSuccess: () => setEditing(false) })
  }

  return (
    <section className="risk-section" aria-label={`Risk rules of run ${runId}`}>
      <div className="risk-section__head">
        <span className="risk-section__title">
          <IconShield /> Risk
        </span>
        <span className="risk-section__chips">
          {empty ? (
            <span className="muted" style={{ fontSize: 12.5 }}>
              No risk rules
            </span>
          ) : (
            chips.map((c) => (
              <Badge key={c.level} tone={c.level === 'overall' ? 'accent' : 'neutral'}>
                {c.label}
              </Badge>
            ))
          )}
        </span>
        {isActive && canEdit && !editing && (
          <div className="risk-section__actions">
            <button
              type="button"
              className="btn btn--ghost btn--sm"
              onClick={beginEdit}
              title="Change the rules of this run — the guard applies them on its next sweep"
            >
              {empty ? 'Set' : 'Edit'}
            </button>
          </div>
        )}
      </div>

      {!editing && (overallSl != null || overallTg != null || showGroups) && (
        <div className="risk-section__bars">
          {overallSl != null && (
            <RiskMetric label="Overall stop-loss" limit={overallSl} used={-Math.min(total, 0)} kind="stop" />
          )}
          {overallTg != null && (
            <RiskMetric label="Overall target" limit={overallTg} used={Math.max(total, 0)} kind="target" />
          )}
          {showGroups && (
            <div className="metric">
              <div className="metric__label">Open groups</div>
              <div className="metric__value" style={{ whiteSpace: 'normal' }}>
                {groups.map((g, i) => (
                  <span key={g.groupId} className="mono" title={`${g.openLegs} open · ${g.closedLegs} closed`}>
                    {i > 0 ? ' · ' : ''}
                    {g.groupId} <PnlValue value={g.pnl} />
                  </span>
                ))}
              </div>
              <div className="metric__sub">
                group SL {rules.group?.stopLoss != null ? formatInrWhole(rules.group.stopLoss) : '—'} · target{' '}
                {rules.group?.target != null ? formatInrWhole(rules.group.target) : '—'}
              </div>
            </div>
          )}
        </div>
      )}

      {editing && (
        <div
          className="risk-section__editor"
          onKeyDown={(e) => {
            if (e.key === 'Escape') {
              e.stopPropagation()
              cancel()
            }
          }}
        >
          <RiskRulesForm
            value={draft}
            onChange={(next) => {
              setDraft(next)
              setInvalidField(null)
            }}
            idPrefix={`run-${runId}-risk`}
            invalidField={invalidField}
            invalidNonce={invalidNonce}
            disabled={update.isPending}
            autoFocus
          />
          {validation && (
            <div className="alert alert--error" role="alert">
              {validation}
            </div>
          )}
          {update.isError && <InlineError error={update.error} />}
          <div className="risk-section__editor-foot">
            <button type="button" className="btn btn--primary btn--sm" disabled={update.isPending} onClick={save}>
              {update.isPending ? 'Saving…' : 'Save'}
            </button>
            <button type="button" className="btn btn--ghost btn--sm" disabled={update.isPending} onClick={cancel}>
              Cancel
            </button>
            <span className="faint" style={{ fontSize: 12 }}>
              Esc cancels · applies on the guard's next sweep; the change is logged in Activity
            </span>
          </div>
        </div>
      )}
    </section>
  )
}

/* ---------------------------------------------------------- positions table */

function PositionsTable({ positions }: { positions: LivePosition[] }) {
  return (
    <div className="tablewrap">
      <table className="table">
        <thead>
          <tr>
            <th>Contract</th>
            <th>Side</th>
            <th className="r">Lots</th>
            <th className="r">Lot size</th>
            <th className="r">Qty</th>
            <th className="r">Entry</th>
            <th className="r">LTP</th>
            <th className="r" title="Entry premium × quantity; open rows also show the value at the last price">
              Value
            </th>
            <th className="r">P&L</th>
            <th>Status</th>
            <th>Time</th>
          </tr>
        </thead>
        <tbody>
          {positions.map((p) => {
            const open = p.status === 'Open'
            const values = positionValues({ ...p, mark: p.ltp })
            return (
              <tr key={p.id} className={open ? '' : 'pos-row--closed'}>
                <td className="mono" title={`${p.symbol} · group ${p.groupId}`}>
                  {contractLabel(p)}
                </td>
                <td>
                  <Badge tone={p.side === 'BUY' ? 'pos' : 'neg'}>{p.side}</Badge>
                </td>
                <td className="r">{open ? formatNumber(p.lots) : 0}</td>
                <td className="r muted">{formatNumber(p.lotSize)}</td>
                <td className="r">{open ? formatNumber(p.quantity) : 0}</td>
                <td className="r mono">{formatPrice(p.entryPrice)}</td>
                <td className="r">{open ? <FlashPrice value={p.ltp} /> : <span className="muted">—</span>}</td>
                <PositionValueCell values={values} open={open} />
                <PositionPnlCell pnl={p.pnl} values={values} />
                <td>{open ? <Badge tone="accent">Open</Badge> : <Badge>Closed</Badge>}</td>
                <td className="muted">{open ? formatTime(p.openedUtc) : formatTime(p.closedUtc)}</td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}

/* ------------------------------------------------------------ runner output */

/**
 * The runner's recent output. Polled while the run is live; fetched once for a
 * finished run, whose last lines (the traceback of a crashed runner included)
 * the API retains after the process exits.
 */
function RunnerOutput({
  runId,
  isActive,
  adopted = false,
}: {
  runId: number
  isActive: boolean
  /** Adopted after an API restart: the API holds the process but not its pipes. */
  adopted?: boolean
}) {
  const logs = useStrategyLogs(runId, isActive)
  const { refetch } = logs

  // The run ended while this box was open: the last poll predates the exit,
  // so pull the retained snapshot once instead of showing a stale tail.
  const wasActive = useRef(isActive)
  useEffect(() => {
    if (wasActive.current && !isActive) void refetch()
    wasActive.current = isActive
  }, [isActive, refetch])

  const lines = Array.isArray(logs.data) ? logs.data : []
  let placeholder: string
  if (logs.isPending) placeholder = 'Loading runner output…'
  else if (logs.isError) placeholder = 'Could not load the runner output.'
  else if (adopted)
    placeholder =
      'Adopted after an API restart — output not captured. The runner keeps writing to logs/engine/runner-<runId>-<pid>.log on the API host.'
  else if (!isActive) placeholder = 'No output was retained for this run.'
  else placeholder = 'No output yet — the runner prints a [CONFIG] line at startup and a [STATUS] line every 10 s.'
  return <ConsoleOutput lines={lines} placeholder={placeholder} />
}

/* ---------------------------------------------------------------- run card */

export interface RunCardProps {
  strategy: RunCardStrategy
  runId: number
  /** Registry snapshot of a live run — paints the header before the live view loads. */
  run: StrategyActiveRun | null
  /** Exit record of a finished run — same purpose. */
  exit: StrategyLastExit | null
  /**
   * Stop and risk editing are offered (an admin, or the user who started the
   * run). The API enforces the same rule; this only hides what would 403.
   */
  canControl?: boolean
  /** Offered on a finished card; when absent there is no Dismiss button (the history detail). */
  onDismiss?: () => void
  dismissTitle?: string
  /** Extra disclosures rendered after Activity and Runner output (the order ledger). */
  children?: ReactNode
}

export function RunCard({
  strategy,
  runId,
  run,
  exit,
  canControl = true,
  onDismiss,
  dismissTitle,
  children,
}: RunCardProps) {
  const live = useStrategyLive(runId, true)
  const stop = useStopStrategy()
  const [showActivity, setShowActivity] = useState(false)
  const [showOutput, setShowOutput] = useState(false)

  const view = live.data
  const isActive = view ? view.isActive : run != null
  const positions = view?.positions ?? []
  const openCount = positions.filter((p) => p.status === 'Open').length
  const underlying = view?.underlying ?? run?.underlying ?? exit?.underlying ?? null
  const stopReason = view?.stopReason ?? exit?.reason ?? null
  const startedBy = view?.startedBy ?? run?.startedBy ?? null
  const startedUtc = view?.startedUtc ?? run?.startedUtc ?? null
  const stoppedUtc = view?.stoppedUtc ?? exit?.atUtc ?? null

  // "Capital used" sub-line: premium paid for open BUY legs and received for
  // open SELL legs; a zero side is left out rather than shown as ₹0.
  const capitalParts: string[] = []
  if (view?.pnl.premiumOutlay != null && view.pnl.premiumOutlay > 0)
    capitalParts.push(`outlay ${formatInrWhole(view.pnl.premiumOutlay)}`)
  if (view?.pnl.premiumReceived != null && view.pnl.premiumReceived > 0)
    capitalParts.push(`received ${formatInrWhole(view.pnl.premiumReceived)}`)

  // The stop result belongs to exactly one run. The mutation lives in this
  // card and the card is keyed by runId, but the guard makes the rule
  // explicit: a message for run 42 never renders under any other card.
  const stopResult = stop.isSuccess && stop.variables?.runId === runId ? stop.data : null
  const stopError = stop.isError && stop.variables?.runId === runId ? stop.error : null

  function confirmStop() {
    const where = underlying ? ` on ${underlying}` : ''
    const msg = `Square off ${openCount} open position${openCount === 1 ? '' : 's'} at the last price and stop ${strategy.name}${where}?`
    if (window.confirm(msg)) stop.mutate({ runId })
  }

  return (
    <section
      id={`run-${runId}`}
      className={`run-card ${isActive ? 'run-card--live' : 'run-card--stopped'}`}
      aria-label={`${strategy.name}${underlying ? ` on ${underlying}` : ''} run ${runId}`}
    >
      <header className="run-card__head">
        <span className="run-card__name">
          {strategy.name}
          <CategoryBadge category={strategy.category} />
        </span>
        {underlying && (
          <span className="badge badge--accent" title={view?.spotSymbol ?? run?.spotSymbol ?? undefined}>
            {underlying} <FlashPrice value={view?.spotLtp} bold />
          </span>
        )}
        {isActive ? (
          <Badge tone="live">running</Badge>
        ) : (
          <Badge tone="warn">Stopped{stopReason ? ` · ${stopReason}` : ''}</Badge>
        )}
        <span className="run-card__meta">
          {startedBy ? `started by ${startedBy}` : 'started'} · {formatTime(startedUtc)}
          {!isActive && stoppedUtc && <> · stopped {formatTime(stoppedUtc)}</>}
          <span className="faint">· run #{runId}</span>
        </span>
        <div className="run-card__actions">
          {isActive ? (
            canControl && (
              <button
                type="button"
                className="btn btn--danger btn--sm"
                disabled={stop.isPending}
                onClick={confirmStop}
              >
                <IconStop style={{ width: 13, height: 13 }} />
                {stop.isPending ? 'Stopping…' : 'Stop'}
              </button>
            )
          ) : (
            onDismiss && (
              <button type="button" className="btn btn--ghost btn--sm" onClick={onDismiss} title={dismissTitle}>
                Dismiss
              </button>
            )
          )}
        </div>
      </header>

      {stopError && (
        <div style={{ marginBottom: 10 }}>
          <InlineError error={stopError} />
        </div>
      )}
      {stopResult && (
        <p className="small-note" style={{ margin: '0 0 10px' }} role="status">
          {stopResult.message}
          {stopResult.flattened > 0 ? ` · squared off ${stopResult.flattened}` : ''}
        </p>
      )}

      {!view && live.isPending && <Loading label="Loading run…" />}
      {!view && live.isError && <InlineError error={live.error} />}

      {view && (
        <>
          <div className="metric-strip">
            <div className="metric">
              <div className="metric__label">Total P&L</div>
              <div className="metric__value metric__value--lg">
                <PnlValue value={view.pnl.total} />
              </div>
              <div className="metric__sub">realized + unrealized</div>
            </div>
            <div className="metric">
              <div className="metric__label">Realized</div>
              <div className="metric__value">
                <PnlValue value={view.pnl.realized} />
              </div>
            </div>
            <div className="metric">
              <div className="metric__label">Unrealized</div>
              <div className="metric__value">
                <PnlValue value={view.pnl.unrealized} />
              </div>
              <div className="metric__sub">
                {openCount} open · {positions.length - openCount} closed
              </div>
            </div>
            <div className="metric">
              <div className="metric__label">Lots</div>
              <div className="metric__value">{formatLots(view.lots, view.lotSize)}</div>
              <div className="metric__sub">
                {view.lotSize != null ? `qty ${formatNumber((view.lots ?? 0) * view.lotSize)}` : ''}
                {view.lotSizeSource && view.lotSizeSource !== 'master' ? ` · lot size ${view.lotSizeSource}` : ''}
              </div>
            </div>
            {view.pnl.capitalUsed != null && (
              <div className="metric">
                <div className="metric__label">Capital used</div>
                <div className="metric__value">{formatInrWhole(view.pnl.capitalUsed)}</div>
                <div className="metric__sub">
                  {capitalParts.length > 0 ? capitalParts.join(' · ') : 'no open premium'}
                </div>
              </div>
            )}
          </div>

          <RiskSection runId={runId} view={view} isActive={isActive} canEdit={canControl} />

          {positions.length > 0 ? (
            <PositionsTable positions={positions} />
          ) : isActive ? (
            <div className="waiting" role="status">
              <span className="pulse-dot" aria-hidden="true" />
              <span>
                Waiting for entry conditions
                {view.underlying ? (
                  <>
                    {' '}
                    — spot {view.underlying} {formatPrice(view.spotLtp)} · last tick{' '}
                    {view.spotUpdatedUtc ? formatAge(view.spotUpdatedUtc) : 'not yet received'}
                  </>
                ) : null}
              </span>
            </div>
          ) : (
            <p className="empty">No positions were opened during this run.</p>
          )}

          <Disclosure
            label={`Activity (${view.activity.length})`}
            open={showActivity}
            onToggle={() => setShowActivity((v) => !v)}
          >
            <ActivityList items={view.activity} />
          </Disclosure>
          <Disclosure
            label={`Runner output${view.runner?.adopted ? ' · adopted after API restart' : ''}`}
            open={showOutput}
            onToggle={() => setShowOutput((v) => !v)}
          >
            <RunnerOutput runId={runId} isActive={isActive} adopted={view.runner?.adopted ?? false} />
          </Disclosure>
          {children}
        </>
      )}
    </section>
  )
}
