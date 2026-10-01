/**
 * Data → Replay, the queue: recorded days replayed one after another, each
 * from 09:15 with the AI Trader alone, so its shadow book is scored over many
 * days against a fixed rule (AI → Agents, the scoreboard), not over one.
 *
 * Two views. With no queue running: the days to tick (with NIFTY's recorded
 * minutes, so a partial day is seen before it is picked), a speed and the
 * line of what Queue does; the last queue's days fold under it. While one
 * runs: where it stands, each day's state and score, and Cancel. The day
 * playing now is the session panel above, as for any replay.
 *
 * The API decides when a day may start: never in a trading day's session or
 * the morning before it, and only when the day will end before the next
 * trading morning's 08:45. A day it cannot start is skipped with its reason.
 */

import { useState } from 'react'
import { Link } from 'react-router-dom'
import { netTone } from '../../lib/aiTrader'
import type { AiTraderScoreRow } from '../../lib/aiTrader'
import { istDay, weekdayOf } from '../../lib/desk'
import { formatInrSigned, formatTime, formatDateTime } from '../../lib/format'
import { useAiTraderScoreboard, useAiTraderStatus, useCancelReplayQueue, useQueueReplay, useReplayDays } from '../../lib/queries'
import {
  QUEUE_DEFAULT_SPEED,
  QUEUE_MAX_DAYS,
  REPLAY_SPEEDS,
  SESSION_MINUTES,
  durationText,
  errorLine,
  fullDayDates,
  isActiveState,
  isFullDay,
  isQueueRunning,
  minutesText,
  queueDayLabel,
  queueDayMinutes,
  queueDays,
  queueProgressText,
  queueSetupError,
  queueSummary,
  shortDay,
  speedLabel,
} from '../../lib/replay'
import type { QueueDay, ReplayQueue, ReplaySession, ReplaySpeed, ReplayStatus } from '../../lib/replay'
import { Badge, InlineError, Loading, Panel } from '../../components/ui'
import { IconCalendar, IconStop } from '../../components/icons'

const AGENTS_LINK = '/ai/agents?agent=ai-trader'

function Signed({ value }: { value: number | null }) {
  return <span className={`rp-q-n ${netTone(value)}`}>{formatInrSigned(value)}</span>
}

/** Said before a queue is asked for and while it runs: an AI Trader switched off decides nothing. */
function TraderOffNote() {
  const s = useAiTraderStatus()
  if (s.data?.status === 'off') {
    return (
      <p className="field__help warn rp-flush">
        The AI Trader is switched off now, so the queued days would play with nobody deciding: switch it on in{' '}
        <Link to={AGENTS_LINK}>AI → Agents</Link> first.
      </p>
    )
  }
  if (s.isError && !s.data) return <p className="field__help warn rp-flush">Whether the AI Trader is switched on could not be read: {errorLine(s.error)}</p>
  return null
}

/** The rule that decides when a day may start, in one line. */
function WhenItRuns() {
  return (
    <p className="small-note rp-flush">
      It runs only outside market hours: no day starts during a trading day's session or the morning before it, and a day starts only
      if it will end before the next trading morning's 08:45; otherwise it waits. Days play oldest first, 90 seconds apart.
    </p>
  )
}

/** Each day of a queue: its state, its replay, and its score once the rule is scored. */
function QueueDayList({ days, rows }: { days: QueueDay[]; rows: AiTraderScoreRow[] | undefined }) {
  return (
    <ol className="rp-q-days">
      {days.map((d) => {
        const st = queueDayLabel(d.state)
        const score = d.sessionId != null ? rows?.find((r) => r.kind === 'replay' && r.replaySessionId === d.sessionId) : undefined
        return (
          <li key={d.date} className={`rp-q-row rp-q-row--${d.state}`}>
            <span className="rp-q-row__date">
              <b>{shortDay(d.date)}</b> <span className="faint">{weekdayOf(d.date)}</span>
            </span>
            <Badge tone={st.tone}>{st.label}</Badge>
            {d.sessionId != null &&
              (d.state === 'playing' ? (
                <span className="faint">replay #{d.sessionId}, above</span>
              ) : (
                <Link to={`/ai/agents#atr-score-r-${d.sessionId}`} title="Its row on the AI Trader's scoreboard">
                  replay #{d.sessionId}
                </Link>
              ))}
            {d.state === 'played' &&
              (score ? (
                <span className="rp-q-row__score">
                  AI <Signed value={score.net} /> · rule{' '}
                  {!score.baseline ? (
                    <span className="faint">scoring…</span>
                  ) : score.baseline.optionType === '' ? (
                    <span title={score.baseline.note || undefined}>no trade, ₹0</span>
                  ) : (
                    <Signed value={score.baseline.net} />
                  )}
                  {!score.full && <span className="faint"> · partial, not in the totals</span>}
                </span>
              ) : (
                <span className="faint rp-q-row__score">not on the scoreboard yet</span>
              ))}
            {d.reason && <span className="warn rp-q-row__why">{d.reason}</span>}
          </li>
        )
      })}
    </ol>
  )
}

/** A queue that is running: where it stands, every day, and Cancel. */
export function QueueProgress({ queue, session }: { queue: ReplayQueue; session: ReplaySession | null }) {
  const cancel = useCancelReplayQueue()
  const board = useAiTraderScoreboard()
  const [asking, setAsking] = useState(false)
  const days = queueDays(queue, session)
  const playing = days.some((d) => d.state === 'playing')
  const activeOther = session != null && isActiveState(session.state) && !playing

  return (
    <Panel
      className="rp-queue"
      title={
        <>
          <IconCalendar /> Queue of days · {queueProgressText(queue, days)}
        </>
      }
      actions={
        <>
          <Badge tone="live">Running</Badge>
          {!asking && (
            <button type="button" className="btn btn--sm btn--danger" onClick={() => setAsking(true)}>
              <IconStop style={{ width: 12, height: 12 }} /> Cancel
            </button>
          )}
        </>
      }
    >
      {asking && (
        <div className="alert alert--warn alert--stack rp-gap" role="alertdialog" aria-label="Cancel the queue?">
          <span>
            Cancel the queue? No further day starts. The day playing now plays on; <b>Stop</b> above stops it.
          </span>
          <span className="rp-q-confirm">
            <button
              type="button"
              className="btn btn--sm btn--danger"
              disabled={cancel.isPending}
              onClick={() => cancel.mutate(undefined, { onSuccess: () => setAsking(false) })}
            >
              {cancel.isPending ? 'Cancelling…' : 'Cancel the queue'}
            </button>
            <button type="button" className="btn btn--sm btn--ghost" disabled={cancel.isPending} onClick={() => setAsking(false)}>
              Keep it
            </button>
          </span>
          {cancel.isError && <span className="neg">That did not go through: {errorLine(cancel.error)}</span>}
        </div>
      )}
      <div className="rp-facts">
        <span>speed {speedLabel(queue.speed)}</span>
        <span>from {queue.fromIst}</span>
        <span>about {durationText(queueDayMinutes(queue.speed ?? 1, queue.fromIst))} a day</span>
        <span>AI Trader alone, in shadow</span>
        {queue.createdUtc && (
          <span title={`${formatDateTime(queue.createdUtc)} IST`}>
            queued {formatTime(queue.createdUtc)} IST{queue.by ? ` by ${queue.by}` : ''}
          </span>
        )}
      </div>
      {!playing && queue.nextDate && (
        <p className="small-note">
          {activeOther
            ? `Another replay is playing; ${shortDay(queue.nextDate)} starts after it ends.`
            : `Next: ${shortDay(queue.nextDate)}. It starts 90 seconds after the last day ended, once NSE is closed and the day can end before the next trading morning's 08:45.`}
        </p>
      )}
      <TraderOffNote />
      <QueueDayList days={days} rows={board.data?.rows} />
      <p className="small-note rp-flush">
        Each played day is scored against the fixed rule on the <Link to="/ai/agents#ai-trader-scoreboard">AI Trader's scoreboard</Link>; the
        rule is scored about a minute after a day ends.
      </p>
    </Panel>
  )
}

/** With no queue running: tick the days, pick a speed, Queue. The last queue folds underneath. */
export function QueueSetup({ status }: { status: ReplayStatus | undefined }) {
  const days = useReplayDays()
  const queueIt = useQueueReplay()
  const [picked, setPicked] = useState<ReadonlySet<string>>(() => new Set())
  const [speed, setSpeed] = useState<ReplaySpeed>(QUEUE_DEFAULT_SPEED)
  const last = status?.queue && !isQueueRunning(status.queue) ? status.queue : null
  const board = useAiTraderScoreboard(last != null)

  const today = istDay(Date.now())
  const list = days.data?.days ?? []
  const chosen = [...picked].sort()
  const problem = queueSetupError(chosen)
  const full = fullDayDates(list, today)
  const replayActive = isActiveState(status?.session?.state)
  // Queued while NSE trades (or the morning before), the queue waits; the API takes it all the same.
  const waits = status && !status.canStart && !replayActive ? status.whyNot : null
  const busy = queueIt.isPending
  const canPress = status !== undefined && !replayActive && problem == null && !busy

  function toggle(date: string) {
    setPicked((prev) => {
      const next = new Set(prev)
      if (next.has(date)) next.delete(date)
      else next.add(date)
      return next
    })
  }

  return (
    <Panel
      className="rp-queue"
      title={
        <>
          <IconCalendar /> Queue several days with the AI Trader
        </>
      }
      actions={list.length > 0 ? <span className="faint rp-head-note">{picked.size} of at most {QUEUE_MAX_DAYS} ticked</span> : undefined}
    >
      <p className="small-note rp-flush rp-q-lead">
        Plays each ticked day from 09:15 with only the AI Trader deciding, in shadow, one after another, so its shadow book is scored over
        many days against a fixed rule rather than over one.
      </p>
      {replayActive ? (
        <p className="empty">One replay at a time: the queue can be set up when the replay above ends.</p>
      ) : days.isPending ? (
        <Loading label="Reading the recorded days…" />
      ) : days.isError && days.data === undefined ? (
        <InlineError error={days.error} />
      ) : list.length === 0 ? (
        <p className="empty">No recorded day to queue yet.</p>
      ) : (
        <div className="rp-q-setup">
          <div className="rp-q-tools">
            <button
              type="button"
              className="btn btn--sm"
              disabled={busy || full.length === 0}
              onClick={() => setPicked(new Set(full))}
              title={`Days with at least 360 of ${SESSION_MINUTES} NIFTY minutes${full.length === QUEUE_MAX_DAYS ? `; the newest ${QUEUE_MAX_DAYS}` : ''}`}
            >
              Select all full days ({full.length})
            </button>
            <button type="button" className="btn btn--sm btn--ghost" disabled={busy || picked.size === 0} onClick={() => setPicked(new Set())}>
              Clear
            </button>
            <span className="faint rp-q-tools__note">full: at least 360 of {SESSION_MINUTES} NIFTY minutes recorded</span>
          </div>
          <ul className="rp-q-pick" aria-label="Recorded days">
            {list.map((d) => {
              const over = d.date < today
              const isFull = isFullDay(d)
              const id = `rp-q-${d.date}`
              return (
                <li key={d.date}>
                  <label htmlFor={id} className={`rp-q-day${picked.has(d.date) ? ' is-on' : ''}${over ? '' : ' is-off'}`} title={over ? undefined : 'Not over yet'}>
                    <input id={id} type="checkbox" checked={picked.has(d.date)} disabled={busy || !over} onChange={() => toggle(d.date)} />
                    <span className="rp-q-day__date">
                      <b>{shortDay(d.date)}</b> <span className="faint">{d.weekday}</span>
                    </span>
                    <span className={`rp-q-day__min ${isFull ? '' : 'warn'}`} title={`NIFTY: ${minutesText(d.minutes.NIFTY)} of ${SESSION_MINUTES} minutes recorded`}>
                      {minutesText(d.minutes.NIFTY)}
                      {isFull ? '' : ' · partial'}
                    </span>
                  </label>
                </li>
              )
            })}
          </ul>

          <div className="field">
            <span className="field__label" id="rp-q-speed-label">
              Speed
            </span>
            <div className="seg" role="radiogroup" aria-labelledby="rp-q-speed-label">
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
                  {speedLabel(s)}
                </button>
              ))}
            </div>
            <span className="field__help">
              At {speedLabel(speed)} a day takes about {durationText(queueDayMinutes(speed))}.
              {speed > 2 && ' Above 2× the AI Trader may skip looks: the model takes up to a minute to answer.'}
            </span>
          </div>

          <WhenItRuns />
          <TraderOffNote />
          {waits && <p className="field__help rp-flush">Queued now, it waits: {waits}</p>}

          <div className="rp-go">
            <p className="small-note rp-go__what">{problem ?? queueSummary(chosen, speed)}</p>
            <button
              type="button"
              className="btn btn--pos"
              disabled={!canPress}
              onClick={() => queueIt.mutate({ dates: chosen, speed }, { onSuccess: () => setPicked(new Set()) })}
            >
              <IconCalendar style={{ width: 14, height: 14 }} />
              {busy ? 'Queueing…' : chosen.length === 0 ? 'Queue days' : `Queue ${chosen.length} ${chosen.length === 1 ? 'day' : 'days'}`}
            </button>
          </div>
          {queueIt.isError && (
            <div className="alert alert--error" role="alert">
              The queue was not taken: {errorLine(queueIt.error)}
            </div>
          )}
        </div>
      )}

      {last && (
        <details className="hp-details rp-q-last">
          <summary>
            Last queue: {queueProgressText(last, queueDays(last, status?.session))}
            {last.endedUtc ? <span className="faint"> · ended {formatDateTime(last.endedUtc)} IST</span> : null}
          </summary>
          <QueueDayList days={queueDays(last, status?.session)} rows={board.data?.rows} />
        </details>
      )}
    </Panel>
  )
}
