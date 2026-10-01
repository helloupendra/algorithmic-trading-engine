/**
 * Market replay (Data → Replay), as data.
 *
 * The owner asked on 1 Oct to replay any recorded trading day exactly as the
 * desk ran it, whenever he wants. The API plays a day's recorded ticks, in
 * the order they arrived, through the same strategy runners the live desk
 * uses; the runs trade the day as recap runs and stay out of every live
 * total (private/notes/2026-10-01-market-replay.md is the contract).
 *
 * This module is the page-free half, so it can be tested:
 *   - the shapes, and readers that never trust a body: a field the API left
 *     out is null ("not known"), never a made-up zero or an "all clear";
 *   - the order a start happens in: the recap runs first, then the replay,
 *     and nothing played when any run did not start (launchReplay);
 *   - the words and tones the page puts on a session: the replay clock in
 *     IST, the progress, the minutes a day recorded, the speed note;
 *   - the queue: recorded days replayed one after another with the AI
 *     Trader alone, to score it over many days; which day is playing,
 *     which were played or skipped and why, and how long a day takes.
 *
 * The query hooks live with the others in lib/queries.ts.
 */

import { dayMonth, weekdayOf } from './desk'
import { formatNumber } from './format'
import type { StartStrategyRequest } from './types'

// ---------- the rules the page shows ------------------------------------------------

/** The underlyings v1 replays: NSE and BSE indices only, never MCX. */
export const REPLAY_UNDERLYINGS = ['NIFTY', 'BANKNIFTY', 'SENSEX'] as const
export type ReplayUnderlying = (typeof REPLAY_UNDERLYINGS)[number]

/** The coverage columns of the days table, in order: the three indices and India VIX. */
export const COVERAGE_KEYS = ['NIFTY', 'BANKNIFTY', 'SENSEX', 'INDIAVIX'] as const
export type CoverageKey = (typeof COVERAGE_KEYS)[number]

export const COVERAGE_LABELS: Record<CoverageKey, string> = {
  NIFTY: 'NIFTY',
  BANKNIFTY: 'BANKNIFTY',
  SENSEX: 'SENSEX',
  INDIAVIX: 'VIX',
}

/** 1 is faithful; the API refuses anything else. */
export const REPLAY_SPEEDS = [1, 2, 5, 10] as const
export type ReplaySpeed = (typeof REPLAY_SPEEDS)[number]

/** 1-minute bars in a full NSE session, 09:15 to 15:29. */
export const SESSION_MINUTES = 375
/** Below this many recorded minutes a day is shown in the warning tone: a feed gap of about a quarter hour or more. */
export const MINUTES_WARN_BELOW = 360

/** The start times the API accepts, IST. */
export const FROM_EARLIEST = '09:15'
export const FROM_LATEST = '15:00'

/** How often the status is read: every 2 s while a replay runs, every 15 s otherwise. */
export const REPLAY_POLL_ACTIVE_MS = 2_000
export const REPLAY_POLL_IDLE_MS = 15_000

/** Lines of the player's log the page asks for. */
export const REPLAY_LOG_LINES = 200

const SESSION_OPEN_SEC = (9 * 60 + 15) * 60
const SESSION_CLOSE_SEC = (15 * 60 + 30) * 60
const IST_OFFSET_MS = 330 * 60_000

// ---------- shapes -----------------------------------------------------------------

/** Minutes recorded per coverage column; null when the API did not say (not the same as 0). */
export type ReplayMinutes = Record<CoverageKey, number | null>

export interface ReplayDay {
  /** yyyy-MM-dd, IST. */
  date: string
  /** "Tue". */
  weekday: string
  minutes: ReplayMinutes
  /** The recorded chunk's size; null when not known. */
  sizeBytes: number | null
}

export interface ReplayDays {
  /** Newest first. */
  days: ReplayDay[]
  earliest: string | null
  latest: string | null
}

/** Kept a string, so a state the server adds later still shows by its own name. */
export type ReplayState = 'starting' | 'waiting' | 'playing' | 'paused' | 'finished' | 'stopped' | 'failed'

export interface ReplayRun {
  runId: number
  strategy: string
  underlying: string
  account: string
  status: string
  /** Rupees after charges; null when not known yet. */
  netPnl: number | null
}

export interface ReplaySession {
  id: number | null
  date: string
  speed: number | null
  /** "09:15". */
  fromIst: string
  state: ReplayState | string
  clockUtc: string | null
  clockIst: string | null
  /** 0..1 across 09:15–15:30; null when not known. */
  progress: number | null
  ticksSent: number | null
  startedUtc: string | null
  endedUtc: string | null
  error: string | null
  runIds: number[]
  runs: ReplayRun[]
  /** Who pressed Start; null when not sent. */
  startedBy: string | null
  /** The AI Trader decides along, on the replay's clock (shadow: it places nothing). False unless the API said so. */
  aiTrader: boolean
}

export interface ReplayStatus {
  canStart: boolean
  whyNot: string | null
  session: ReplaySession | null
  /** The queue of days, running or the last one; null when none was ever queued (or the API is older). */
  queue?: ReplayQueue | null
}

/** A queue of recorded days, replayed one after another from the open with the AI Trader alone. */
export interface ReplayQueue {
  id: number | null
  /** yyyy-MM-dd, oldest first: the order they play in. */
  dates: string[]
  speed: number | null
  fromIst: string
  /** How many of `dates` were started or skipped: the next to play is dates[next]. */
  next: number
  /** The replay session of each day that started, in order. */
  sessions: number[]
  /** The days that did not start, with why. */
  skipped: { date: string; reason: string }[]
  by: string | null
  createdUtc: string | null
  /** Set once it is done or cancelled; null while it runs. */
  endedUtc: string | null
  note: string | null
  /** The next day to play; null once it has ended. */
  nextDate: string | null
}

// ---------- reading the API ---------------------------------------------------------

const record = (v: unknown): v is Record<string, unknown> => v != null && typeof v === 'object' && !Array.isArray(v)
const num = (v: unknown): number | null => (typeof v === 'number' && Number.isFinite(v) ? v : null)
const str = (v: unknown): string | null => (typeof v === 'string' ? v : null)
const words = (v: unknown): string => (str(v) ?? '').trim()
const optional = (v: unknown): string | null => words(v) || null
const rows = (v: unknown): Record<string, unknown>[] => (Array.isArray(v) ? v.filter(record) : [])
const instant = (v: unknown): string | null => {
  const s = str(v)
  return s && !Number.isNaN(Date.parse(s)) ? s : null
}
/** A whole, non-negative count, or null. */
const count = (v: unknown): number | null => {
  const n = num(v)
  return n == null || n < 0 ? null : Math.round(n)
}
const ISO_DAY = /^\d{4}-\d{2}-\d{2}$/

/** "2026-09-30" from "2026-09-30" or "2026-09-30T00:00:00"; null otherwise. */
function isoDay(v: unknown): string | null {
  const s = words(v).slice(0, 10)
  return ISO_DAY.test(s) ? s : null
}

/**
 * A coverage key for whatever spelling the API used: "NIFTY", "nifty",
 * "IndiaVix", "INDIA VIX" or "VIX". A camelCase serializer would send
 * "nifty" and "indiavix" for the contract's NIFTY and INDIAVIX.
 */
function coverageKey(key: string): CoverageKey | null {
  const k = key.toUpperCase().replace(/[^A-Z0-9]/g, '')
  if (k === 'VIX' || k === 'INDIAVIX') return 'INDIAVIX'
  return (COVERAGE_KEYS as readonly string[]).includes(k) ? (k as CoverageKey) : null
}

export function readMinutes(v: unknown): ReplayMinutes {
  const out: ReplayMinutes = { NIFTY: null, BANKNIFTY: null, SENSEX: null, INDIAVIX: null }
  if (!record(v)) return out
  for (const [key, value] of Object.entries(v)) {
    const k = coverageKey(key)
    if (k) out[k] = count(value)
  }
  return out
}

/** "Tue" from "Tue" or "Tuesday"; the date's own weekday when the API sent none. */
function shortWeekday(v: unknown, date: string): string {
  const w = words(v)
  return w ? w.slice(0, 3) : weekdayOf(date)
}

function readDay(d: Record<string, unknown>): ReplayDay | null {
  const date = isoDay(d.date)
  if (!date) return null
  return { date, weekday: shortWeekday(d.weekday, date), minutes: readMinutes(d.minutes), sizeBytes: count(d.sizeBytes) }
}

/** GET /api/Replay/days. A bare array is read as the list; anything else is no days. */
export function readReplayDays(body: unknown): ReplayDays {
  const list = Array.isArray(body) ? body : record(body) ? body.days : null
  // One row a date: a repeated one keeps its first reading.
  const byDate = new Map<string, ReplayDay>()
  for (const d of rows(list).map(readDay)) {
    if (d && !byDate.has(d.date)) byDate.set(d.date, d)
  }
  const days = [...byDate.values()].sort((a, b) => b.date.localeCompare(a.date))
  const head = record(body) ? body : {}
  return {
    days,
    earliest: isoDay(head.earliest) ?? days[days.length - 1]?.date ?? null,
    latest: isoDay(head.latest) ?? days[0]?.date ?? null,
  }
}

function readRun(r: Record<string, unknown>): ReplayRun | null {
  const runId = num(r.runId)
  if (runId == null || runId <= 0) return null
  return {
    runId,
    strategy: words(r.strategy) || `Run ${runId}`,
    underlying: words(r.underlying),
    account: words(r.account),
    status: words(r.status),
    netPnl: num(r.netPnl),
  }
}

/** "2026-09-22: Nothing recorded" as a day and its reason; the text as the reason when it names no day. */
export function readSkipped(text: string): { date: string; reason: string } {
  const m = /^(\d{4}-\d{2}-\d{2})\s*:\s*(.*)$/.exec(text.trim())
  return m ? { date: m[1], reason: m[2].trim() } : { date: '', reason: text.trim() }
}

/** The queue, or null when the body is not one (no days). */
export function readReplayQueue(v: unknown): ReplayQueue | null {
  if (!record(v)) return null
  const dates = (Array.isArray(v.dates) ? v.dates : []).map(isoDay).filter((d): d is string => d != null)
  if (dates.length === 0) return null
  const next = count(v.next)
  return {
    id: num(v.id),
    dates,
    speed: num(v.speed),
    fromIst: words(v.fromIst) || FROM_EARLIEST,
    next: next == null ? 0 : Math.min(next, dates.length),
    sessions: (Array.isArray(v.sessions) ? v.sessions : []).filter((id): id is number => num(id) != null && (id as number) > 0),
    skipped: (Array.isArray(v.skipped) ? v.skipped : []).filter((x): x is string => typeof x === 'string' && x.trim() !== '').map(readSkipped),
    by: optional(v.by),
    createdUtc: instant(v.createdUtc),
    endedUtc: instant(v.endedUtc),
    note: optional(v.note),
    nextDate: isoDay(v.nextDate),
  }
}

/** One session, or null when the body is not one (no date). */
export function readReplaySession(v: unknown): ReplaySession | null {
  if (!record(v)) return null
  const date = isoDay(v.date)
  if (!date) return null
  const progress = num(v.progress)
  return {
    id: num(v.id),
    date,
    speed: num(v.speed),
    fromIst: words(v.fromIst) || FROM_EARLIEST,
    state: words(v.state).toLowerCase() || 'unknown',
    clockUtc: instant(v.clockUtc),
    clockIst: optional(v.clockIst),
    progress: progress == null ? null : Math.min(1, Math.max(0, progress)),
    ticksSent: count(v.ticksSent),
    startedUtc: instant(v.startedUtc),
    endedUtc: instant(v.endedUtc),
    error: optional(v.error),
    runIds: (Array.isArray(v.runIds) ? v.runIds : []).filter((id): id is number => num(id) != null && (id as number) > 0),
    runs: rows(v.runs)
      .map(readRun)
      .filter((r): r is ReplayRun => r != null),
    startedBy: optional(v.startedBy),
    aiTrader: v.aiTrader === true,
  }
}

/**
 * GET /api/Replay/status. canStart is true only when the API says so in as
 * many words: a body that does not say is "cannot start", with a reason.
 */
export function readReplayStatus(body: unknown): ReplayStatus {
  if (!record(body)) return { canStart: false, whyNot: 'The replay status did not come back from the API.', session: null }
  const canStart = body.canStart === true
  return {
    canStart,
    whyNot: canStart ? null : (optional(body.whyNot) ?? 'The API did not say why a replay cannot start now.'),
    session: readReplaySession(body.session),
    queue: readReplayQueue(body.queue),
  }
}

/** GET /api/Replay/logs: the lines as text; a bare array is read as the lines. */
export function readReplayLogs(body: unknown): string[] {
  const lines = Array.isArray(body) ? body : record(body) ? body.lines : null
  return Array.isArray(lines) ? lines.filter((l): l is string => typeof l === 'string') : []
}

// ---------- states ------------------------------------------------------------------

const ACTIVE_STATES: ReadonlySet<string> = new Set(['starting', 'waiting', 'playing', 'paused'])
const ENDED_STATES: ReadonlySet<string> = new Set(['finished', 'stopped', 'failed'])

/** A session still running: it holds the desk's one replay, and the status is read every 2 s. */
export function isActiveState(state: string | null | undefined): boolean {
  return state != null && ACTIVE_STATES.has(state)
}

/** A session that is over: its result is final. */
export function isEndedState(state: string | null | undefined): boolean {
  return state != null && ENDED_STATES.has(state)
}

/** How often to read the status, from the last answer. A queue between days is read as often as one idle. */
export function replayPollMs(status: ReplayStatus | undefined): number {
  return isActiveState(status?.session?.state) ? REPLAY_POLL_ACTIVE_MS : REPLAY_POLL_IDLE_MS
}

export type Tone = 'pos' | 'neg' | 'warn' | 'accent' | 'live' | 'neutral'

const STATE_WORDS: Record<ReplayState, { label: string; tone: Tone }> = {
  starting: { label: 'Starting', tone: 'accent' },
  waiting: { label: 'Waiting for runners', tone: 'accent' },
  playing: { label: 'Playing', tone: 'live' },
  paused: { label: 'Paused', tone: 'warn' },
  finished: { label: 'Finished', tone: 'pos' },
  stopped: { label: 'Stopped', tone: 'neutral' },
  failed: { label: 'Failed', tone: 'neg' },
}

/** A state's word and badge tone; a state this page does not know shows by its own name. */
export function replayStateLabel(state: string): { label: string; tone: Tone } {
  const known = STATE_WORDS[state as ReplayState]
  if (known) return known
  const s = state.trim()
  return { label: s ? s[0].toUpperCase() + s.slice(1) : 'Unknown', tone: 'neutral' }
}

// ---------- words -------------------------------------------------------------------

/** "11:42:07" in IST from a UTC instant; '' for a missing or unreadable one. */
export function istHms(iso: string | null | undefined): string {
  const ms = iso ? Date.parse(iso) : NaN
  if (Number.isNaN(ms)) return ''
  const d = new Date(ms + IST_OFFSET_MS)
  return [d.getUTCHours(), d.getUTCMinutes(), d.getUTCSeconds()].map((n) => String(n).padStart(2, '0')).join(':')
}

/** "11:42:07" from the IST text the API sent ("11:42:07", "11:42", "2026-09-30T11:42:07"); '' otherwise. */
function hmsFromText(text: string | null): string {
  const m = /(\d{1,2}):(\d{2})(?::(\d{2}))?/.exec(text ?? '')
  if (!m) return ''
  return `${m[1].padStart(2, '0')}:${m[2]}:${m[3] ?? '00'}`
}

/** The replay clock, IST "HH:mm:ss": from clockUtc, else from clockIst; '' when neither reads. */
export function replayClock(s: Pick<ReplaySession, 'clockUtc' | 'clockIst'>): string {
  return istHms(s.clockUtc) || hmsFromText(s.clockIst)
}

/** "30 Sep" for a yyyy-MM-dd date. */
export function shortDay(date: string): string {
  return ISO_DAY.test(date) ? dayMonth(date) : date
}

const CLOCK_VERB: Record<string, string> = {
  playing: 'Replaying',
  paused: 'Paused',
  finished: 'Finished',
  stopped: 'Stopped',
  failed: 'Failed',
}

/**
 * The line over the progress bar: "Replaying 30 Sep · 11:42:07". While it
 * starts or waits for the runners nothing plays yet, so there is no clock;
 * the clock is also left out until the player has sent one.
 */
export function clockText(s: Pick<ReplaySession, 'state' | 'date' | 'clockUtc' | 'clockIst'>): string {
  const day = shortDay(s.date)
  if (s.state === 'starting') return `Starting the replay of ${day}`
  if (s.state === 'waiting') return `Waiting for the runners · ${day}`
  const verb = CLOCK_VERB[s.state] ?? replayStateLabel(s.state).label
  const clock = replayClock(s)
  return `${verb} ${day}${clock ? ` · ${clock}` : ''}`
}

/**
 * How far through 09:15–15:30 the replay is, 0..1: the API's progress, else
 * worked out from the clock; null when neither is known. A replay that is
 * over keeps no clock, and the API then answers 1 for a day played out and 0
 * for any other: a replay stopped (or failed) at 13:00 is not 0% played, so
 * with no clock its progress is not known.
 */
export function replayProgress(s: Pick<ReplaySession, 'progress' | 'clockUtc' | 'clockIst'> & { state?: string }): number | null {
  const hms = replayClock(s)
  if (!hms && s.state !== 'finished' && isEndedState(s.state)) return null
  if (s.progress != null) return s.progress
  if (!hms) return null
  const [h, m, sec] = hms.split(':').map(Number)
  const t = h * 3600 + m * 60 + sec
  return Math.min(1, Math.max(0, (t - SESSION_OPEN_SEC) / (SESSION_CLOSE_SEC - SESSION_OPEN_SEC)))
}

/**
 * "1,23,456 ticks sent"; null when there is nothing true to say. The API
 * counts the ticks only while the player runs and keeps no count once the
 * replay is over: it then answers 0, which is not a count, so an ended
 * replay's 0 is left out rather than shown as "0 ticks sent".
 */
export function ticksSentText(s: Pick<ReplaySession, 'state' | 'ticksSent'>): string | null {
  const ended = isEndedState(s.state)
  if (s.ticksSent == null) return ended ? null : 'ticks sent not known'
  if (s.ticksSent === 0 && ended) return null
  return `${formatNumber(s.ticksSent)} ${s.ticksSent === 1 ? 'tick' : 'ticks'} sent`
}

/** "42%": whole percent, rounded down, so 100% means the day is played out; '—' when not known. */
export function progressText(progress: number | null | undefined): string {
  if (progress == null || !Number.isFinite(progress)) return '—'
  const p = Math.min(1, Math.max(0, progress))
  return `${Math.floor(p * 100 + 1e-9)}%`
}

/** A coverage cell's tone: 'none' for no minutes or not known, 'warn' below MINUTES_WARN_BELOW, else 'ok'. */
export function minutesTone(minutes: number | null | undefined): 'ok' | 'warn' | 'none' {
  if (minutes == null || minutes <= 0) return 'none'
  return minutes < MINUTES_WARN_BELOW ? 'warn' : 'ok'
}

/** "375", "343"; '—' when not known. */
export function minutesText(minutes: number | null | undefined): string {
  return minutes == null ? '—' : String(minutes)
}

/** "5×". */
export function speedLabel(speed: number | null | undefined): string {
  return speed == null ? '—' : `${speed}×`
}

/** The one line under the speed choice. */
export function speedNote(speed: number): string {
  return speed <= 1
    ? 'Faithful: the day plays at its recorded pace.'
    : `At ${speed}× a fill can be priced a little after the tick that triggered it.`
}

/**
 * A start time as the API takes it ("09:30"), from what was typed: "9:30",
 * "0930" and "930" all read as 09:30. An error in words when it is not a
 * time, or outside 09:15–15:00.
 */
export function parseFromTime(text: string): { value: string; error: null } | { value: null; error: string } {
  const t = text.trim()
  const m = /^(\d{1,2}):?(\d{2})$/.exec(t)
  const h = m ? Number(m[1]) : NaN
  const min = m ? Number(m[2]) : NaN
  if (!m || h > 23 || min > 59) return { value: null, error: 'Start time must be a time, like 09:15.' }
  const value = `${String(h).padStart(2, '0')}:${String(min).padStart(2, '0')}`
  if (value < FROM_EARLIEST || value > FROM_LATEST) {
    return { value: null, error: `Start time must be between ${FROM_EARLIEST} and ${FROM_LATEST} IST.` }
  }
  return { value, error: null }
}

/** A coverage line for a run's underlying on the chosen day, or null when the day is whole. */
export function coverageNote(day: ReplayDay | null | undefined, underlying: string): string | null {
  if (!day) return null
  const key = coverageKey(underlying)
  if (!key) return null
  const minutes = day.minutes[key]
  if (minutes == null) return `How many ${underlying} minutes ${shortDay(day.date)} holds is not known.`
  if (minutes <= 0) return `${shortDay(day.date)} has no recorded ${underlying} minutes: the run will see no ticks.`
  if (minutes < MINUTES_WARN_BELOW) return `${shortDay(day.date)} holds ${minutes} of ${SESSION_MINUTES} ${underlying} minutes.`
  return null
}

/** The sum of the runs' net P&L; null unless every run's is known. */
export function replayNetTotal(runs: ReplayRun[]): number | null {
  if (runs.length === 0) return null
  let total = 0
  for (const r of runs) {
    if (r.netPnl == null) return null
    total += r.netPnl
  }
  return total
}

/** The first line of an error, for a sentence on screen. */
export function errorLine(error: unknown): string {
  const raw = error instanceof Error ? error.message : typeof error === 'string' ? error : ''
  return raw.split('\n')[0].trim() || 'Something went wrong.'
}

// ---------- the set-up ----------------------------------------------------------------

/** The underlyings a strategy can be replayed on: the replayable ones it supports (an empty list supports all). */
export function replayUnderlyingsFor(supported: readonly string[]): ReplayUnderlying[] {
  if (supported.length === 0) return [...REPLAY_UNDERLYINGS]
  const set = new Set(supported.map((u) => u.toUpperCase()))
  return REPLAY_UNDERLYINGS.filter((u) => set.has(u))
}

/** One run of the set-up, as typed. */
export interface ReplayRunDraft {
  /** Stable row key. */
  key: number
  strategyId: number | null
  underlying: string
  lots: string
  /** Whose account; null is the signed-in user's. */
  ownerUserId: number | null
}

/** What the set-up knows of a strategy: its name and the underlyings it can be replayed on. */
export interface ReplayStrategyInfo {
  id: number
  name: string
  underlyings: readonly string[]
  /** Underlyings it already runs on in an account, upper-cased: the API refuses a second run there. */
  takenFor?: (ownerUserId: number | null) => ReadonlySet<string>
}

/**
 * Why the set-up cannot start, in words, or null when it can. Checked in
 * the order the form reads: the day, the start time, then each run. With
 * the AI Trader along (`aiTrader`), a replay needs no run: it may decide alone.
 */
export function setupError(
  date: string | null,
  fromText: string,
  drafts: ReplayRunDraft[],
  strategies: ReadonlyMap<number, ReplayStrategyInfo>,
  aiTrader = false,
): string | null {
  if (!date) return 'Pick a recorded day first.'
  const from = parseFromTime(fromText)
  if (from.error) return from.error
  if (drafts.length === 0 && !aiTrader) return 'Add at least one strategy run, or ask the AI Trader along.'
  const seen = new Set<string>()
  for (const [i, d] of drafts.entries()) {
    const n = drafts.length > 1 ? ` ${i + 1}` : ''
    const s = d.strategyId == null ? undefined : strategies.get(d.strategyId)
    if (!s) return aiTrader ? `Run${n}: pick a strategy, or remove the run to replay with the AI Trader alone.` : `Run${n}: pick a strategy.`
    if (!(REPLAY_UNDERLYINGS as readonly string[]).includes(d.underlying)) return `Run${n}: pick NIFTY, BANKNIFTY or SENSEX.`
    if (!s.underlyings.includes(d.underlying)) return `Run${n}: ${s.name} does not trade ${d.underlying}.`
    const lots = Number(d.lots)
    if (!Number.isInteger(lots) || lots < 1) return `Run${n}: lots must be a whole number of at least 1.`
    if (s.takenFor?.(d.ownerUserId).has(d.underlying)) {
      return `Run${n}: ${s.name} is already running on ${d.underlying} in that account. Stop it first, or pick another account.`
    }
    const key = `${s.id}|${d.underlying}|${d.ownerUserId ?? 'me'}`
    if (seen.has(key)) return `Run${n}: ${s.name} on ${d.underlying} is in the list twice for the same account.`
    seen.add(key)
  }
  return null
}

/**
 * The line beside Start: what pressing it does. With no runs, only the AI
 * Trader decides along; it places nothing in a replay.
 */
export function setupSummary(o: { runs: number; date: string; from: string; speed: number; aiTrader: boolean }): string {
  const day = shortDay(o.date)
  const play = `plays the day from ${o.from} at ${speedLabel(o.speed)}`
  if (o.runs === 0) {
    return `Plays ${day} from ${o.from} at ${speedLabel(o.speed)} with only the AI Trader deciding along, in shadow: it places nothing, and nothing enters a live total.`
  }
  const runs = o.runs === 1 ? 'the run as a recap' : `the ${o.runs} runs as recaps`
  const ai = o.aiTrader ? ', with the AI Trader deciding along in shadow' : ''
  return `Starts ${runs} of ${day}, then ${play}${ai}. Replay runs are tests: they stay out of every live total and are squared off when the replay ends.`
}

/** The recap parameters a replay run starts with. */
export function recapParameters(date: string): Record<string, string> {
  return { session: 'recap', recap_date: date }
}

/**
 * The POST /api/Strategy/{id}/start body of one replay run: a recap of the
 * day. The strategy's defaults are merged in by the API. The account is named
 * only when it is not the caller's own (the API reads a missing owner as the
 * caller's, and refuses the field from anyone but an admin).
 */
export function recapStartBody(
  draft: Pick<ReplayRunDraft, 'underlying' | 'lots' | 'ownerUserId'>,
  date: string,
  signedInUserId: number | null | undefined,
): StartStrategyRequest & { ownerUserId?: number } {
  const body: StartStrategyRequest & { ownerUserId?: number } = {
    underlying: draft.underlying,
    lots: Number(draft.lots),
    parameters: recapParameters(date),
  }
  if (draft.ownerUserId != null && draft.ownerUserId !== signedInUserId) body.ownerUserId = draft.ownerUserId
  return body
}

/** The POST /api/Replay/start body. */
export interface ReplayStartBody {
  date: string
  speed: number
  from: string
  runIds: number[]
  /** The AI Trader decides along; with it, runIds may be empty. The API reads a missing one as false. */
  aiTrader?: boolean
}

// ---------- starting ------------------------------------------------------------------

/** One run to start: a label for the page, the strategy, and the start body. */
export interface ReplayRunPlan {
  label: string
  strategyId: number
  body: StartStrategyRequest & { ownerUserId?: number }
}

export interface StartedRun {
  label: string
  runId: number
}

export type LaunchOutcome =
  | { ok: true; runIds: number[]; session: ReplaySession | null }
  /** A run did not start: the replay was not asked for, and the runs after it were not tried. */
  | { ok: false; stage: 'run'; failed: { label: string; error: string }; started: StartedRun[]; notTried: string[] }
  /** Every run started, and then the replay was refused. */
  | { ok: false; stage: 'replay'; error: string; started: StartedRun[] }

/** The run id a start answered with; null when it is missing. */
export function startedRunId(response: unknown): number | null {
  const id = record(response) ? num(response.runId) : null
  return id != null && id > 0 ? id : null
}

/**
 * Start a replay, in the one order that is safe: each run as a recap of the
 * day, one at a time (the API starts one runner at a time anyway), and only
 * when every run is up, the replay with their ids. The first run that does
 * not start ends it there: the replay is not started and the runs after it
 * are not tried, so as few runs as possible are left to stop. With no runs
 * (the AI Trader alone), the replay is asked for at once.
 */
export async function launchReplay(
  plans: ReplayRunPlan[],
  replay: Omit<ReplayStartBody, 'runIds'>,
  deps: {
    startRun: (plan: ReplayRunPlan) => Promise<unknown>
    startReplay: (body: ReplayStartBody) => Promise<unknown>
    onStep?: (text: string) => void
  },
): Promise<LaunchOutcome> {
  const started: StartedRun[] = []
  for (const [i, plan] of plans.entries()) {
    deps.onStep?.(`Starting ${plan.label} (${i + 1} of ${plans.length})…`)
    let runId: number | null = null
    let error = ''
    try {
      runId = startedRunId(await deps.startRun(plan))
      if (runId == null) error = 'The API did not answer with a run id.'
    } catch (e) {
      error = errorLine(e)
    }
    if (runId == null) {
      return {
        ok: false,
        stage: 'run',
        failed: { label: plan.label, error },
        started,
        notTried: plans.slice(i + 1).map((p) => p.label),
      }
    }
    started.push({ label: plan.label, runId })
  }
  const runIds = started.map((s) => s.runId)
  deps.onStep?.('Starting the replay…')
  try {
    const session = readReplaySession(await deps.startReplay({ ...replay, runIds }))
    return { ok: true, runIds, session }
  } catch (e) {
    return { ok: false, stage: 'replay', error: errorLine(e), started }
  }
}

// ---------- the queue -----------------------------------------------------------------

/** The most days one queue may hold (the API's limit). */
export const QUEUE_MAX_DAYS = 20
/** A day counts as full when NIFTY has at least this many of its 375 recorded minutes. */
export const FULL_DAY_MINUTES = MINUTES_WARN_BELOW
/** The speed a queue starts at. */
export const QUEUE_DEFAULT_SPEED: ReplaySpeed = 2

/** The POST /api/Replay/queue body. */
export interface ReplayQueueBody {
  dates: string[]
  speed: number
}

/** A queue that is still running: no further replay or queue can start until it ends. */
export function isQueueRunning(q: ReplayQueue | null | undefined): boolean {
  return q != null && q.endedUtc == null
}

/** Whether a day holds NIFTY's session in full (at least 360 of 375 minutes). */
export function isFullDay(day: Pick<ReplayDay, 'minutes'>): boolean {
  const n = day.minutes.NIFTY
  return n != null && n >= FULL_DAY_MINUTES
}

/**
 * How long one day takes, in minutes: the player plays 09:15 to 15:40 at the
 * speed, plus ten minutes for its start and last checks (as the API reckons
 * when it decides whether a day fits before the next trading morning).
 */
export function queueDayMinutes(speed: number, fromIst = FROM_EARLIEST): number {
  const m = /^(\d{1,2}):(\d{2})$/.exec(fromIst)
  const from = m ? Number(m[1]) * 60 + Number(m[2]) : 9 * 60 + 15
  return Math.round((15 * 60 + 40 - from) / Math.max(1, speed) + 10)
}

/** "3 h 23 min", "48 min". */
export function durationText(minutes: number): string {
  const h = Math.floor(minutes / 60)
  const m = Math.round(minutes - h * 60)
  return h > 0 ? `${h} h${m > 0 ? ` ${m} min` : ''}` : `${m} min`
}

/**
 * "Select all full days": the full days a queue may take, newest first, at
 * most QUEUE_MAX_DAYS (the newest when there are more), and only days that
 * are over (before `today`, IST).
 */
export function fullDayDates(days: readonly ReplayDay[], today: string, max = QUEUE_MAX_DAYS): string[] {
  return days
    .filter((d) => d.date < today && isFullDay(d))
    .map((d) => d.date)
    .sort((a, b) => b.localeCompare(a))
    .slice(0, max)
}

/** Why the queue cannot be asked for, in words, or null when it can. */
export function queueSetupError(dates: readonly string[]): string | null {
  if (dates.length === 0) return 'Tick at least one recorded day.'
  if (dates.length > QUEUE_MAX_DAYS) return `At most ${QUEUE_MAX_DAYS} days a queue; ${dates.length} are ticked.`
  return null
}

/** The line beside "Queue": what pressing it does and how long it takes. */
export function queueSummary(dates: readonly string[], speed: number): string {
  if (dates.length === 0) return ''
  const sorted = [...dates].sort()
  const days = sorted.length === 1 ? shortDay(sorted[0]) : `${sorted.length} days, ${shortDay(sorted[0])} to ${shortDay(sorted[sorted.length - 1])}`
  const each = queueDayMinutes(speed)
  return `Plays ${days}, oldest first, each from 09:15 at ${speedLabel(speed)} with only the AI Trader deciding along, in shadow. About ${durationText(each)} a day${sorted.length > 1 ? `, ${durationText(each * sorted.length + 1.5 * (sorted.length - 1))} of play in all` : ''}.`
}

export type QueueDayState = 'played' | 'playing' | 'skipped' | 'queued' | 'not-played'

export interface QueueDay {
  date: string
  state: QueueDayState
  /** The replay session it played in; null for a day that did not start. */
  sessionId: number | null
  /** Why it was skipped. */
  reason: string | null
}

/**
 * Each day of the queue and what became of it. The days before `next` were
 * started (each took the next session id) or skipped (named in `skipped`);
 * the one whose session is the replay playing now is "playing"; the rest are
 * queued, or "not played" once the queue has ended.
 */
export function queueDays(q: ReplayQueue, session: Pick<ReplaySession, 'id' | 'state'> | null | undefined): QueueDay[] {
  const skipped = new Map<string, string>()
  for (const s of q.skipped) if (s.date && !skipped.has(s.date)) skipped.set(s.date, s.reason)
  const playingId = session && isActiveState(session.state) ? session.id : null
  let nextSession = 0
  return q.dates.map((date, i) => {
    if (i >= q.next) return { date, state: q.endedUtc == null ? 'queued' : 'not-played', sessionId: null, reason: null }
    if (skipped.has(date)) return { date, state: 'skipped', sessionId: null, reason: skipped.get(date) || null }
    const sessionId = q.sessions[nextSession++] ?? null
    return { date, state: sessionId != null && sessionId === playingId ? 'playing' : 'played', sessionId, reason: null }
  })
}

const QUEUE_DAY_WORDS: Record<QueueDayState, { label: string; tone: Tone }> = {
  played: { label: 'Played', tone: 'pos' },
  playing: { label: 'Playing', tone: 'live' },
  skipped: { label: 'Skipped', tone: 'warn' },
  queued: { label: 'Queued', tone: 'neutral' },
  'not-played': { label: 'Not played', tone: 'neutral' },
}

export function queueDayLabel(state: QueueDayState): { label: string; tone: Tone } {
  return QUEUE_DAY_WORDS[state]
}

/**
 * Where the queue stands, in a line: "Day 2 of 5 · playing 29 Sep",
 * "3 of 5 days handled · next 30 Sep", "Ended: Every day was played.".
 */
export function queueProgressText(q: ReplayQueue, days: QueueDay[]): string {
  const total = q.dates.length
  const playing = days.findIndex((d) => d.state === 'playing')
  if (q.endedUtc != null) {
    const played = days.filter((d) => d.state === 'played').length
    return `${played} of ${total} ${total === 1 ? 'day' : 'days'} played${q.note ? ` · ${q.note}` : ''}`
  }
  if (playing >= 0) return `Day ${playing + 1} of ${total} · playing ${shortDay(days[playing].date)}`
  return `${q.next} of ${total} ${total === 1 ? 'day' : 'days'} done${q.nextDate ? ` · next ${shortDay(q.nextDate)}` : ''}`
}
