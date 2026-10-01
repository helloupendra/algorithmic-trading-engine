/**
 * Data → Replay (/data/replay): play a recorded trading day back through the
 * strategy runners, exactly as the desk ran it, whenever the owner wants.
 *
 * Top to bottom: the replay now (or the last one's result), the days the desk
 * recorded and what each holds (the house rule: show what data exists before
 * any picker), the set-up for the chosen day, and the player's log.
 *
 * Start does two things in order (lib/replay.ts, launchReplay): each run is
 * started as a recap of the day through the ordinary start endpoint, then the
 * replay is started with their ids. When a run does not start, the replay is
 * not asked for, and the page offers to stop the runs that did.
 *
 * Replay runs are tests: the API keeps them out of every live total and
 * squares them off at the replay's prices when it ends or is stopped.
 */

import { useMemo, useRef, useState } from 'react'
import type { Ref } from 'react'
import { Link } from 'react-router-dom'
import { useAuth } from '../../lib/auth'
import {
  useReplayControl,
  useReplayDays,
  useReplayLogs,
  useReplayStatus,
  useStartReplay,
  useStartStrategy,
  useStopStrategy,
  useStrategies,
  useUserAccounts,
} from '../../lib/queries'
import {
  COVERAGE_KEYS,
  COVERAGE_LABELS,
  FROM_EARLIEST,
  FROM_LATEST,
  REPLAY_SPEEDS,
  REPLAY_UNDERLYINGS,
  SESSION_MINUTES,
  clockText,
  coverageNote,
  errorLine,
  isActiveState,
  isEndedState,
  launchReplay,
  minutesText,
  minutesTone,
  parseFromTime,
  progressText,
  recapStartBody,
  replayNetTotal,
  replayProgress,
  replayStateLabel,
  replayUnderlyingsFor,
  setupError,
  shortDay,
  speedLabel,
  speedNote,
} from '../../lib/replay'
import type {
  LaunchOutcome,
  ReplayDay,
  ReplayRunDraft,
  ReplayRunPlan,
  ReplaySession,
  ReplaySpeed,
  ReplayStatus,
  ReplayStrategyInfo,
} from '../../lib/replay'
import { blockedUnderlyings } from '../../lib/strategyList'
import { runStatusTone } from '../../lib/runHistory'
import { formatDateTime, formatInrSigned, formatNumber, formatTime, pnlClass } from '../../lib/format'
import { formatBytes } from '../../lib/system'
import { prefersReducedMotion } from '../../lib/motion'
import { Badge, InlineError, Loading, Panel } from '../../components/ui'
import { IconCalendar, IconClock, IconPlay, IconPlus, IconStop, IconX } from '../../components/icons'
import { ConsoleOutput, Disclosure } from '../strategies/shared'
import './data.css'
import './replay.css'

/** A session's identity, so a dismissed result stays dismissed across polls. */
const sessionKey = (s: ReplaySession) => (s.id != null ? `id:${s.id}` : `${s.date}|${s.startedUtc ?? ''}`)

const fullIst = (iso: string | null) => (iso ? `${formatDateTime(iso)} IST` : undefined)

// ---------- the replay now ---------------------------------------------------------------

function ReplayControls({ session }: { session: ReplaySession }) {
  const control = useReplayControl()
  const busy = control.isPending
  return (
    <>
      {session.state === 'playing' && (
        <button type="button" className="btn btn--sm" disabled={busy} onClick={() => control.mutate('pause')}>
          Pause
        </button>
      )}
      {session.state === 'paused' && (
        <button type="button" className="btn btn--sm" disabled={busy} onClick={() => control.mutate('resume')}>
          <IconPlay style={{ width: 12, height: 12 }} /> Resume
        </button>
      )}
      <button
        type="button"
        className="btn btn--sm btn--danger"
        disabled={busy}
        onClick={() => control.mutate('stop')}
        title="Stop the replay; its runs are squared off at the replay's prices"
      >
        <IconStop style={{ width: 12, height: 12 }} /> Stop
      </button>
      {control.isError && (
        <span className="neg rp-inline-error" role="alert">
          {control.variables ? `${control.variables[0].toUpperCase()}${control.variables.slice(1)}` : 'That'} did not go
          through: {errorLine(control.error)}
        </span>
      )}
    </>
  )
}

function RunsTable({ session }: { session: ReplaySession }) {
  if (session.runs.length === 0) {
    return session.runIds.length > 0 ? (
      <p className="small-note">
        Runs{' '}
        {session.runIds.map((id, i) => (
          <span key={id}>
            {i > 0 && ', '}
            <Link to={`/trade/runs/${id}`}>#{id}</Link>
          </span>
        ))}
        : their details are not in yet.
      </p>
    ) : (
      <p className="small-note">The API has not listed this replay's runs yet.</p>
    )
  }
  const total = replayNetTotal(session.runs)
  // Once it is over, the result below says the total.
  const showTotal = session.runs.length > 1 && !isEndedState(session.state)
  return (
    <>
      <div className="tablewrap rp-runs">
        <table className="table">
          <thead>
            <tr>
              <th>Run</th>
              <th>Strategy</th>
              <th>Underlying</th>
              <th>Account</th>
              <th>Status</th>
              <th className="r">Net P&amp;L</th>
            </tr>
          </thead>
          <tbody>
            {session.runs.map((r) => (
              <tr key={r.runId}>
                <td>
                  <Link to={`/trade/runs/${r.runId}`}>#{r.runId}</Link>
                </td>
                <td>{r.strategy}</td>
                <td className="mono">{r.underlying || '—'}</td>
                <td>{r.account || '—'}</td>
                <td>{r.status ? <Badge tone={runStatusTone(r.status)}>{r.status}</Badge> : <span className="faint">—</span>}</td>
                <td className={`r ${pnlClass(r.netPnl)}`} title={r.netPnl == null ? 'Not known yet' : 'After charges'}>
                  {formatInrSigned(r.netPnl)}
                </td>
              </tr>
            ))}
          </tbody>
          {showTotal && (
            <tfoot>
              <tr>
                <td colSpan={5}>All runs</td>
                <td className={`r ${pnlClass(total)}`} title={total == null ? 'Not known until every run reports' : 'After charges'}>
                  {formatInrSigned(total)}
                </td>
              </tr>
            </tfoot>
          )}
        </table>
      </div>
      {/* A phone's view of the table: the net stays on the first line instead of past the edge. */}
      <ul className="rp-rl">
        {session.runs.map((r) => (
          <li key={r.runId} className="rp-rl__row">
            <div className="rp-rl__head">
              <Link to={`/trade/runs/${r.runId}`}>#{r.runId}</Link>
              <span className="rp-rl__name">{r.strategy}</span>
              <span className={`rp-rl__net ${pnlClass(r.netPnl)}`}>{formatInrSigned(r.netPnl)}</span>
            </div>
            <div className="rp-rl__meta">
              <span className="mono">{r.underlying || '—'}</span>
              <span>{r.account || '—'}</span>
              {r.status && <Badge tone={runStatusTone(r.status)}>{r.status}</Badge>}
            </div>
          </li>
        ))}
        {showTotal && (
          <li className="rp-rl__row rp-rl__total">
            <div className="rp-rl__head">
              <span className="rp-rl__name">All runs</span>
              <span className={`rp-rl__net ${pnlClass(total)}`}>{formatInrSigned(total)}</span>
            </div>
          </li>
        )}
      </ul>
    </>
  )
}

function SessionPanel({ session, onAnother }: { session: ReplaySession; onAnother: () => void }) {
  const active = isActiveState(session.state)
  const ended = isEndedState(session.state)
  const state = replayStateLabel(session.state)
  const progress = replayProgress(session)
  const barTone =
    session.state === 'failed' ? 'progress__bar--neg' : session.state === 'paused' ? 'progress__bar--warn' : session.state === 'finished' ? 'progress__bar--pos' : ''
  const total = replayNetTotal(session.runs)

  return (
    <Panel
      className={`rp-session${session.state === 'failed' ? ' panel--danger' : ''}`}
      title={
        <>
          <IconClock /> {ended ? 'Last replay' : 'Replay'} · {shortDay(session.date)}
        </>
      }
      actions={
        <>
          <Badge tone={state.tone}>{state.label}</Badge>
          {active && <ReplayControls session={session} />}
        </>
      }
    >
      <div className="rp-clock" aria-live="polite">
        {clockText(session)}
      </div>
      <div
        className="progress progress--lg rp-bar"
        role="progressbar"
        aria-label="Replay progress across 09:15–15:30"
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={progress == null ? undefined : Math.floor(progress * 100)}
      >
        <div className={`progress__bar ${barTone}`} style={{ width: `${(progress ?? 0) * 100}%` }} />
      </div>
      <div className="rp-facts">
        <span>
          <b>{progressText(progress)}</b> of 09:15–15:30
        </span>
        <span>from {session.fromIst}</span>
        <span>speed {speedLabel(session.speed)}</span>
        <span>{session.ticksSent == null ? 'ticks sent not known' : `${formatNumber(session.ticksSent)} ticks sent`}</span>
        {session.startedUtc && (
          <span title={fullIst(session.startedUtc)}>
            started {formatTime(session.startedUtc)} IST{session.startedBy ? ` by ${session.startedBy}` : ''}
          </span>
        )}
        {session.endedUtc && <span title={fullIst(session.endedUtc)}>ended {formatTime(session.endedUtc)} IST</span>}
      </div>

      {session.state === 'waiting' && (
        <p className="small-note">The player waits until every run is listening (at most 5 minutes) before the first tick.</p>
      )}
      {session.error && (
        <div className="alert alert--error rp-gap" role="alert">
          {session.error}
        </div>
      )}

      <RunsTable session={session} />

      {ended && (
        <div className="rp-result">
          <div>
            <span className="rp-result__label">Result, after charges</span>
            <span className={`rp-result__value ${pnlClass(total)}`}>{formatInrSigned(total)}</span>
            <span className="faint rp-result__note">
              {session.state === 'finished'
                ? 'The day played out; the runs were squared off at its closing prices.'
                : session.state === 'stopped'
                  ? 'Stopped early; the runs were squared off at the replay’s prices.'
                  : 'The replay failed; its runs were stopped.'}{' '}
              A test: not in any live total.
            </span>
          </div>
          <button type="button" className="btn btn--sm" onClick={onAnother}>
            <IconCalendar style={{ width: 13, height: 13 }} /> Replay another day
          </button>
        </div>
      )}
    </Panel>
  )
}

// ---------- the recorded days --------------------------------------------------------------

function MinutesCell({ minutes }: { minutes: number | null }) {
  const tone = minutesTone(minutes)
  return (
    <td
      className={`r ${tone === 'warn' ? 'warn' : tone === 'none' ? 'faint' : ''}`}
      title={minutes == null ? 'Not known' : `${minutes} of ${SESSION_MINUTES} minutes recorded`}
    >
      {minutesText(minutes)}
    </td>
  )
}

function DaysPanel({
  selected,
  onSelect,
  panelRef,
}: {
  selected: string | null
  onSelect: (date: string) => void
  panelRef: Ref<HTMLDivElement>
}) {
  const days = useReplayDays()
  const data = days.data
  return (
    <div ref={panelRef} className="rp-anchor">
      <Panel
        title={
          <>
            <IconCalendar /> Recorded days
          </>
        }
        actions={
          data && data.days.length > 0 ? (
            <span className="faint rp-head-note">
              {data.days.length} {data.days.length === 1 ? 'day' : 'days'}
              {data.earliest && data.latest ? ` · ${shortDay(data.earliest)} to ${shortDay(data.latest)}` : ''}
            </span>
          ) : undefined
        }
      >
        {days.isPending ? (
          <Loading label="Reading the recorded days…" />
        ) : days.isError && data === undefined ? (
          <InlineError error={days.error} />
        ) : !data || data.days.length === 0 ? (
          <p className="empty">
            No recorded day to replay yet. The desk records every trading day from 9 Sep 2026 and keeps the ticks for 90 days.
          </p>
        ) : (
          <>
            {days.isError && (
              <p className="small-note warn" role="status" style={{ margin: '0 0 8px' }}>
                Refresh failed — showing the last loaded days.
              </p>
            )}
            <div className="tablewrap tablewrap--tall">
              <table className="table table--hover rp-days">
                <thead>
                  <tr>
                    <th>Day</th>
                    {COVERAGE_KEYS.map((k) => (
                      <th key={k} className="r" title={`${COVERAGE_LABELS[k]}: minutes recorded of ${SESSION_MINUTES}`}>
                        {k === 'BANKNIFTY' ? (
                          <>
                            <span className="col--wide">BANKNIFTY</span>
                            <span className="col--phone">BNF</span>
                          </>
                        ) : (
                          COVERAGE_LABELS[k]
                        )}
                      </th>
                    ))}
                    <th className="r">Size</th>
                  </tr>
                </thead>
                <tbody>
                  {data.days.map((d) => (
                    <DayRow key={d.date} day={d} selected={selected === d.date} onSelect={onSelect} />
                  ))}
                </tbody>
              </table>
            </div>
            <p className="small-note">
              Minutes with a recorded 1-minute bar, 09:15–15:29, out of {SESSION_MINUTES}. Under 360 shows in amber: the
              feed had a gap that day. Click a day to set up its replay.
            </p>
          </>
        )}
      </Panel>
    </div>
  )
}

function DayRow({ day, selected, onSelect }: { day: ReplayDay; selected: boolean; onSelect: (date: string) => void }) {
  return (
    <tr className={selected ? 'row--selected' : ''} onClick={() => onSelect(day.date)}>
      <td title={`${day.weekday} ${shortDay(day.date)} ${day.date.slice(0, 4)}`}>
        <button
          type="button"
          className="row-pick"
          aria-pressed={selected}
          onClick={(e) => {
            e.stopPropagation()
            onSelect(day.date)
          }}
        >
          <b>{shortDay(day.date)}</b> <span className="faint">{day.weekday}</span>
        </button>
      </td>
      {COVERAGE_KEYS.map((k) => (
        <MinutesCell key={k} minutes={day.minutes[k]} />
      ))}
      <td className="r muted">{formatBytes(day.sizeBytes)}</td>
    </tr>
  )
}

// ---------- the set-up ---------------------------------------------------------------------

type Failure = Extract<LaunchOutcome, { ok: false }>

type LaunchState =
  | { phase: 'idle' }
  | { phase: 'starting'; step: string }
  | { phase: 'failed'; outcome: Failure; cleanup: 'idle' | 'stopping' | 'done'; cleanupErrors: string[] }

let draftSeq = 1
const newDraft = (): ReplayRunDraft => ({ key: draftSeq++, strategyId: null, underlying: 'NIFTY', lots: '1', ownerUserId: null })

function LaunchFailure({
  state,
  onStopStarted,
  onDismiss,
}: {
  state: Extract<LaunchState, { phase: 'failed' }>
  onStopStarted: () => void
  onDismiss: () => void
}) {
  const { outcome, cleanup, cleanupErrors } = state
  const started = outcome.started
  return (
    <div className="alert alert--error alert--stack rp-failure" role="alert">
      <div>
        {outcome.stage === 'run' ? (
          <>
            <b>{outcome.failed.label}</b> did not start: {outcome.failed.error} The replay was not started.
            {outcome.notTried.length > 0 && <> Not tried: {outcome.notTried.join(', ')}.</>}
          </>
        ) : (
          <>
            Every run started, but the replay did not: {outcome.error}
          </>
        )}
      </div>
      {started.length > 0 && (
        <div className="rp-failure__started">
          {cleanup === 'done' ? 'Stopped' : 'Started and waiting'}:{' '}
          {started.map((s, i) => (
            <span key={s.runId}>
              {i > 0 && ', '}
              <Link to={`/trade/runs/${s.runId}`}>#{s.runId}</Link> {s.label}
            </span>
          ))}
          {cleanupErrors.length > 0 && <div>Could not stop: {cleanupErrors.join('; ')}</div>}
        </div>
      )}
      <div className="rp-failure__actions">
        {started.length > 0 && cleanup !== 'done' && (
          <button type="button" className="btn btn--sm btn--danger" disabled={cleanup === 'stopping'} onClick={onStopStarted}>
            <IconStop style={{ width: 12, height: 12 }} />
            {cleanup === 'stopping'
              ? 'Stopping…'
              : `Stop the ${started.length === 1 ? 'run' : `${started.length} runs`} that started`}
          </button>
        )}
        <button type="button" className="btn btn--sm btn--ghost" onClick={onDismiss}>
          {started.length > 0 && cleanup !== 'done' ? `Keep ${started.length === 1 ? 'it' : 'them'} and dismiss` : 'Dismiss'}
        </button>
      </div>
    </div>
  )
}

function SetupPanel({ day, status }: { day: ReplayDay | null; status: ReplayStatus | undefined }) {
  const { user } = useAuth()
  const strategies = useStrategies()
  const accounts = useUserAccounts()
  const startRun = useStartStrategy()
  const startReplay = useStartReplay()
  const stopRun = useStopStrategy()

  const [drafts, setDrafts] = useState<ReplayRunDraft[]>(() => [newDraft()])
  const [speed, setSpeed] = useState<ReplaySpeed>(1)
  const [fromText, setFromText] = useState(FROM_EARLIEST)
  const [launch, setLaunch] = useState<LaunchState>({ phase: 'idle' })

  const me = user?.id ?? null

  // The strategies a replay can run: those that trade NIFTY, BANKNIFTY or SENSEX.
  const catalog = useMemo(() => {
    const list = (strategies.data ?? [])
      .filter((s) => replayUnderlyingsFor(s.supportedUnderlyings).length > 0)
      .sort((a, b) => a.name.localeCompare(b.name))
    const info = new Map<number, ReplayStrategyInfo>(
      list.map((s) => [
        s.id,
        {
          id: s.id,
          name: s.name,
          underlyings: replayUnderlyingsFor(s.supportedUnderlyings),
          takenFor: (owner: number | null) => blockedUnderlyings(s, owner ?? me),
        },
      ]),
    )
    return { list, info }
  }, [strategies.data, me])

  // Service accounts sign runners in; they hold no book of their own.
  const accountChoices = useMemo(
    () => (accounts.data ?? []).filter((a) => a.isActive && a.role !== 'Service'),
    [accounts.data],
  )
  const accountName = (owner: number | null) =>
    owner == null || owner === me ? null : (accountChoices.find((a) => a.id === owner)?.userName ?? `user ${owner}`)

  const from = parseFromTime(fromText)
  const problem = setupError(day?.date ?? null, fromText, drafts, catalog.info)
  const statusKnown = status !== undefined
  const busy = launch.phase === 'starting'
  const canPress = statusKnown && status.canStart && problem == null && !busy && launch.phase !== 'failed'

  function update(key: number, patch: Partial<ReplayRunDraft>) {
    setDrafts((list) => list.map((d) => (d.key === key ? { ...d, ...patch } : d)))
  }

  function pickStrategy(key: number, id: number | null) {
    const s = id == null ? undefined : catalog.list.find((x) => x.id === id)
    setDrafts((list) =>
      list.map((d) => {
        if (d.key !== key) return d
        const allowed = s ? replayUnderlyingsFor(s.supportedUnderlyings) : [...REPLAY_UNDERLYINGS]
        return {
          ...d,
          strategyId: id,
          underlying: allowed.includes(d.underlying as (typeof REPLAY_UNDERLYINGS)[number]) ? d.underlying : allowed[0],
          lots: s ? String(Math.max(1, s.defaultLots || 1)) : d.lots,
        }
      }),
    )
  }

  async function start() {
    if (!day || !canPress || from.value == null) return
    const plans: ReplayRunPlan[] = drafts.map((d) => {
      const s = catalog.info.get(d.strategyId as number)!
      const owner = accountName(d.ownerUserId)
      return {
        label: `${s.name} on ${d.underlying}${owner ? ` (${owner})` : ''}`,
        strategyId: s.id,
        body: recapStartBody(d, day.date, me),
      }
    })
    setLaunch({ phase: 'starting', step: 'Starting…' })
    const outcome = await launchReplay(
      plans,
      { date: day.date, speed, from: from.value },
      {
        startRun: (plan) => startRun.mutateAsync({ id: plan.strategyId, body: plan.body }),
        startReplay: (body) => startReplay.mutateAsync(body),
        onStep: (step) => setLaunch({ phase: 'starting', step }),
      },
    )
    setLaunch(outcome.ok ? { phase: 'idle' } : { phase: 'failed', outcome, cleanup: 'idle', cleanupErrors: [] })
  }

  async function stopStarted() {
    if (launch.phase !== 'failed') return
    const failure = launch
    setLaunch({ ...failure, cleanup: 'stopping' })
    const errors: string[] = []
    for (const s of failure.outcome.started) {
      try {
        await stopRun.mutateAsync({ runId: s.runId, flatten: true })
      } catch (e) {
        errors.push(`#${s.runId}: ${errorLine(e)}`)
      }
    }
    setLaunch({ ...failure, cleanup: 'done', cleanupErrors: errors })
  }

  return (
    <Panel
      title={
        <>
          <IconPlay /> Set up{day ? ` · ${shortDay(day.date)} ${day.weekday}` : ''}
        </>
      }
    >
      {statusKnown && !status.canStart && (
        <div className="alert alert--warn rp-gap" role="status">
          <span>A replay cannot start now: {status.whyNot}</span>
        </div>
      )}

      {!day ? (
        <p className="empty">Pick a recorded day in the table to set up its replay.</p>
      ) : (
        <div className="rp-setup">
          <div className="field">
            <span className="field__label">Runs</span>
            {strategies.isPending ? (
              <Loading label="Loading the strategy catalog…" />
            ) : strategies.isError && strategies.data === undefined ? (
              <InlineError error={strategies.error} />
            ) : catalog.list.length === 0 ? (
              <p className="small-note warn">No strategy in the catalog trades NIFTY, BANKNIFTY or SENSEX.</p>
            ) : (
              <div className="rp-runs-edit">
                {drafts.map((d, i) => {
                  const s = d.strategyId == null ? undefined : catalog.info.get(d.strategyId)
                  const choices = s ? s.underlyings : REPLAY_UNDERLYINGS
                  const note = coverageNote(day, d.underlying)
                  const taken = s?.takenFor?.(d.ownerUserId).has(d.underlying) ?? false
                  return (
                    <div key={d.key} className="rp-run">
                      <div className="rp-run__head">
                        <span className="rp-run__n">Run {i + 1}</span>
                        {drafts.length > 1 && (
                          <button
                            type="button"
                            className="btn btn--ghost btn--sm"
                            disabled={busy}
                            onClick={() => setDrafts((list) => list.filter((x) => x.key !== d.key))}
                            aria-label={`Remove run ${i + 1}`}
                          >
                            <IconX style={{ width: 12, height: 12 }} />
                          </button>
                        )}
                      </div>
                      <div className="rp-run__fields">
                        <div className="field rp-run__strategy">
                          <label className="field__label" htmlFor={`rp-strategy-${d.key}`}>
                            Strategy
                          </label>
                          <select
                            id={`rp-strategy-${d.key}`}
                            className="field__input"
                            value={d.strategyId ?? ''}
                            disabled={busy}
                            onChange={(e) => pickStrategy(d.key, e.target.value === '' ? null : Number(e.target.value))}
                          >
                            <option value="">Pick a strategy</option>
                            {catalog.list.map((x) => (
                              <option key={x.id} value={x.id}>
                                {x.name}
                              </option>
                            ))}
                          </select>
                        </div>
                        <div className="field">
                          <label className="field__label" htmlFor={`rp-underlying-${d.key}`}>
                            Underlying
                          </label>
                          <select
                            id={`rp-underlying-${d.key}`}
                            className="field__input"
                            value={d.underlying}
                            disabled={busy}
                            onChange={(e) => update(d.key, { underlying: e.target.value })}
                          >
                            {choices.map((u) => (
                              <option key={u} value={u}>
                                {u}
                              </option>
                            ))}
                          </select>
                        </div>
                        <div className="field rp-run__lots">
                          <label className="field__label" htmlFor={`rp-lots-${d.key}`}>
                            Lots
                          </label>
                          <input
                            id={`rp-lots-${d.key}`}
                            className="field__input"
                            type="number"
                            min={1}
                            step={1}
                            inputMode="numeric"
                            value={d.lots}
                            disabled={busy}
                            onChange={(e) => update(d.key, { lots: e.target.value })}
                          />
                        </div>
                        <div className="field">
                          <label className="field__label" htmlFor={`rp-account-${d.key}`}>
                            Account
                          </label>
                          <select
                            id={`rp-account-${d.key}`}
                            className="field__input"
                            value={d.ownerUserId ?? me ?? ''}
                            disabled={busy || accounts.isPending}
                            onChange={(e) => {
                              const id = Number(e.target.value)
                              update(d.key, { ownerUserId: id === me ? null : id })
                            }}
                          >
                            {accountChoices.length === 0 && me != null && <option value={me}>your account</option>}
                            {accountChoices.map((a) => (
                              <option key={a.id} value={a.id}>
                                {a.userName}
                                {a.id === me ? ' (you)' : ''}
                              </option>
                            ))}
                          </select>
                        </div>
                      </div>
                      {(note || taken) && (
                        <div className="rp-run__notes">
                          {taken && s && (
                            <span className="field__help warn">
                              {s.name} is already running on {d.underlying} in this account: the API will refuse a second run there.
                            </span>
                          )}
                          {note && <span className="field__help warn">{note}</span>}
                        </div>
                      )}
                    </div>
                  )
                })}
                <button
                  type="button"
                  className="btn btn--ghost btn--sm rp-add"
                  disabled={busy}
                  onClick={() => setDrafts((list) => [...list, newDraft()])}
                >
                  <IconPlus style={{ width: 12, height: 12 }} /> Add a run
                </button>
              </div>
            )}
            {accounts.isError && accountChoices.length === 0 && (
              <span className="field__help warn">The account list did not load: runs go into your own account.</span>
            )}
          </div>

          <div className="rp-setup__row">
            <div className="field">
              <span className="field__label" id="rp-speed-label">
                Speed
              </span>
              <div className="seg" role="radiogroup" aria-labelledby="rp-speed-label">
                {REPLAY_SPEEDS.map((s) => (
                  <button
                    key={s}
                    type="button"
                    role="radio"
                    aria-checked={speed === s}
                    className={`seg__btn ${speed === s ? 'is-active' : ''}`}
                    disabled={busy}
                    onClick={() => setSpeed(s)}
                  >
                    {s === 1 ? '1× faithful' : speedLabel(s)}
                  </button>
                ))}
              </div>
              <span className="field__help">{speedNote(speed)}</span>
            </div>
            <div className="field rp-from">
              <label className="field__label" htmlFor="rp-from">
                Start at (IST)
              </label>
              <input
                id="rp-from"
                className="field__input"
                type="text"
                inputMode="numeric"
                maxLength={5}
                placeholder={FROM_EARLIEST}
                value={fromText}
                disabled={busy}
                aria-invalid={from.error ? true : undefined}
                onChange={(e) => setFromText(e.target.value)}
                onBlur={() => {
                  if (from.value) setFromText(from.value)
                }}
              />
              <span className={`field__help ${from.error ? 'warn' : ''}`}>
                {from.error ?? `${FROM_EARLIEST} to ${FROM_LATEST}`}
              </span>
            </div>
          </div>

          {launch.phase === 'failed' ? (
            <LaunchFailure state={launch} onStopStarted={stopStarted} onDismiss={() => setLaunch({ phase: 'idle' })} />
          ) : (
            <div className="rp-go">
              <p className="small-note rp-go__what">
                {problem ??
                  `Starts ${drafts.length === 1 ? 'the run as a recap' : `the ${drafts.length} runs as recaps`} of ${shortDay(day.date)}, then plays the day from ${from.value} at ${speedLabel(speed)}. Replay runs are tests: they stay out of every live total and are squared off when the replay ends.`}
              </p>
              <button type="button" className="btn btn--pos" disabled={!canPress} onClick={start}>
                <IconPlay style={{ width: 14, height: 14 }} />
                {busy ? launch.step : `Start replay of ${shortDay(day.date)}`}
              </button>
            </div>
          )}
        </div>
      )}
    </Panel>
  )
}

// ---------- the player's log ---------------------------------------------------------------

function PlayerLog({ active }: { active: boolean }) {
  const [open, setOpen] = useState(false)
  const logs = useReplayLogs(open, active)
  return (
    <section className="panel rp-log">
      <Disclosure label="Player log" open={open} onToggle={() => setOpen((v) => !v)}>
        {logs.isError && logs.data === undefined ? (
          <InlineError error={logs.error} />
        ) : (
          <ConsoleOutput
            title="Replay player"
            lines={logs.data ?? []}
            placeholder={logs.isPending ? 'Reading the log…' : 'The player has written nothing yet.'}
          />
        )}
      </Disclosure>
    </section>
  )
}

// ---------- the page -----------------------------------------------------------------------

export function ReplayPage() {
  const status = useReplayStatus()
  const days = useReplayDays()
  const [selected, setSelected] = useState<string | null>(null)
  const [dismissed, setDismissed] = useState<string | null>(null)
  const daysRef = useRef<HTMLDivElement>(null)
  const setupRef = useRef<HTMLDivElement>(null)

  const data = status.data
  const session = data?.session ?? null
  const active = isActiveState(session?.state)
  const showSession = session != null && (active || sessionKey(session) !== dismissed)
  const day = days.data?.days.find((d) => d.date === selected) ?? null

  // Stacked (a phone, a narrow window), the set-up is below a long table: bring it up.
  function pickDay(date: string) {
    setSelected(date)
    if (window.matchMedia?.('(max-width: 980px)').matches) {
      requestAnimationFrame(() =>
        setupRef.current?.scrollIntoView({ behavior: prefersReducedMotion() ? 'auto' : 'smooth', block: 'start' }),
      )
    }
  }

  function replayAnother() {
    if (session) setDismissed(sessionKey(session))
    setSelected(null)
    daysRef.current?.scrollIntoView({ behavior: prefersReducedMotion() ? 'auto' : 'smooth', block: 'start' })
  }

  return (
    <div className="page data-page rp-page">
      <header className="page__header">
        <div>
          <h1 className="page__title">Market replay</h1>
          <p className="page__subtitle">
            Play a recorded day back through the strategy runners, tick by tick, as the desk ran it. Replay runs are tests:
            they stay out of every live total.
          </p>
        </div>
      </header>

      {status.isPending ? (
        <Loading label="Reading the replay status…" />
      ) : status.isError && data === undefined ? (
        <InlineError error={status.error} />
      ) : (
        <>
          {status.isError && (
            <p className="small-note warn" role="status">
              The last status read failed: showing what was read before.
            </p>
          )}
          {showSession && session && <SessionPanel session={session} onAnother={replayAnother} />}
        </>
      )}

      <div className="two-col rp-cols">
        <DaysPanel selected={selected} onSelect={pickDay} panelRef={daysRef} />
        {active ? (
          <Panel title="Set up" className="rp-setup-wait">
            <p className="empty">One replay at a time: the set-up opens again when this one ends.</p>
          </Panel>
        ) : (
          <div ref={setupRef} className="rp-anchor">
            <SetupPanel day={day} status={status.isError && data === undefined ? undefined : data} />
          </div>
        )}
      </div>

      <PlayerLog active={active} />
    </div>
  )
}
