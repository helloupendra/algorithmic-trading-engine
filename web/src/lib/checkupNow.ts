/**
 * Sentinel's latest checkup, as of now: what the Desk's checkup panel and its
 * status strip show. Pure, so the rules are tested rather than eyeballed.
 *
 * A checkup is a snapshot. On 28 Sep the 08:55 one said "23 of 23 planned
 * runs are not live", which was true at 08:55 (the morning job deploys the
 * plan after the 09:15 open); all 23 were live by 09:19, and at 13:15 the
 * Desk still showed the 08:55 failure and "1 thing to do before the open".
 * Some of a checkup's items are things the Desk reads itself anyway: the
 * morning plan (GET /api/Desk/plan), the Dhan and FYERS sessions (GET
 * /api/Providers) and the feeds (GET /api/Feeds). For those, this file says
 * what is true now, and since when, beside what Sentinel saw.
 *
 * Two rules, the checkup page's own:
 *
 * - Never pretend Sentinel said it. An item the Desk re-read keeps Sentinel's
 *   words beside its own, and a headline worked out here is marked as the
 *   Desk's, never passed off as Sentinel's sentence with new numbers.
 * - "Not known" is never "all good". A live read counts only when it is
 *   fresh: answered after the checkup finished and within the last few
 *   minutes. A read that failed, stalled or has not come yet leaves
 *   Sentinel's word standing, marked with the checkup's time. A failed or
 *   old checkup is shown as it is, with nothing laid over it.
 */

import type { CheckupDetail, CheckupItem, CheckupVerdict, Tone } from './checkup'
import { STALE_HOURS, verdictBadge } from './checkup'
import type { Phase } from './desk'
import { dayOf, istDay, istHm, istMinute, stopKind } from './desk'
import type { DeskPlanResponse, LiveFeed, LiveRunSummary, MarketSessionInfo, Provider } from './types'

// ---------------------------------------------------------------- inputs

/** A live answer the Desk holds, and when it arrived (the query's dataUpdatedAt; 0 before the first). */
export interface LiveRead<T> {
  data: T | undefined
  atMs: number
}

type Session = Pick<MarketSessionInfo, 'isTradingDay' | 'sessionOpenUtc' | 'sessionCloseUtc'>

export type PlanRunLike = Pick<
  LiveRunSummary,
  'runId' | 'userId' | 'strategyName' | 'underlying' | 'startedUtc' | 'role' | 'isActive' | 'stoppedBy' | 'stopReason'
>

/** What the Desk reads now, for the items it can check itself. */
export interface DeskReads {
  nowMs: number
  /** GET /api/Desk/plan for every account: Sentinel checks the whole plan, whatever the Desk's scope. */
  plan?: LiveRead<DeskPlanResponse>
  /** Today's runs, every account: when each planned run first went live, and which stopped on purpose. */
  runs?: readonly PlanRunLike[]
  providers?: LiveRead<Provider[]>
  feeds?: LiveRead<LiveFeed[]>
  /** The NSE session (when a feed must run) and MCX's (the day's last close, which a token must outlast). */
  nse?: Session | null
  mcx?: Session | null
}

/**
 * How old a live read may be and still stand for "now". The plan and the
 * connectors are read every minute and the feeds every 15 s, so five minutes
 * is several missed reads: past it the Desk has stopped hearing, and
 * Sentinel's word is the better one.
 */
export const LIVE_FRESH_MS = 5 * 60_000

/** From 09:25 IST a planned run not live is missing, as Sentinel's trading agent holds the plan to account (PLAN_START). */
const PLAN_START_MIN = 9 * 60 + 25

/** The morning job starts the feed at 08:45; from 08:50 one should be running, as Sentinel's morning check expects. */
const FEEDS_FROM_MIN = 8 * 60 + 50

function msOf(iso: string | null | undefined): number | null {
  const ms = iso ? Date.parse(iso) : NaN
  return Number.isNaN(ms) ? null : ms
}

const plural = (n: number, one: string, many = `${one}s`) => `${n} ${n === 1 ? one : many}`

/** "a, b, c and 3 more": the list Sentinel's details use, six names at most. */
function listed(names: readonly string[]): string {
  const shown = names.slice(0, 6).join(', ')
  return names.length > 6 ? `${shown} and ${names.length - 6} more` : shown
}

/** A session of today (IST), or null: yesterday's bounds say nothing about today. */
function todaysSession(s: Session | null | undefined, nowMs: number): { open: number; close: number } | null {
  if (!s?.isTradingDay) return null
  const open = msOf(s.sessionOpenUtc)
  const close = msOf(s.sessionCloseUtc)
  if (open == null || close == null || istDay(open) !== istDay(nowMs)) return null
  return { open, close }
}

/**
 * Whether a feed must be running now: on a trading day, from 08:50 to the NSE
 * close. Null outside that (before the morning job, after the close, a day
 * with no session, the session not read yet): then either answer is fine and
 * the feeds item stays as Sentinel said.
 */
export function feedsExpected(nowMs: number, nse: Session | null | undefined): boolean | null {
  const s = todaysSession(nse, nowMs)
  if (!s) return null
  return istMinute(nowMs) >= FEEDS_FROM_MIN && nowMs < s.close ? true : null
}

/** Today's last close, which a Dhan token must outlast: MCX's when it trades today, else the NSE's; null when neither is known. */
export function lastCloseMs(nowMs: number, nse: Session | null | undefined, mcx: Session | null | undefined): number | null {
  return todaysSession(mcx, nowMs)?.close ?? todaysSession(nse, nowMs)?.close ?? null
}

// ---------------------------------------------------------------- one item

/** What the Desk reads for one item now. `same`: it agrees with what Sentinel saw, so Sentinel's words stand. */
interface Reading {
  state: string
  detail: string
  action: string
  same: boolean
  /** When it became so, when known and after the checkup. */
  sinceMs: number | null
}

interface Ctx {
  nowMs: number
  checkedMs: number
  today: string
  /** Today is an NSE trading day, by the session the Desk read. */
  tradingDay: boolean
  plan?: DeskPlanResponse
  /** Null until the day's runs have been read. */
  runs: readonly PlanRunLike[] | null
  providers?: Provider[]
  feeds?: LiveFeed[]
  feedsExpected: boolean | null
  lastCloseMs: number | null
}

/** The planned runs Sentinel counted, from its sentence: "All 23 planned runs are live" or "3 of 23 planned runs are not live…". */
function sentinelPlanLive(detail: string): { live: number; planned: number } | null {
  const all = /^All (\d+) planned runs are live/.exec(detail)
  if (all) return { live: Number(all[1]), planned: Number(all[1]) }
  const some = /^(\d+) of (\d+) planned runs are not live/.exec(detail)
  if (some) return { live: Number(some[2]) - Number(some[1]), planned: Number(some[2]) }
  return null
}

/**
 * The morning plan now. Each planned run is matched to the day's runs the
 * API's way (owner, strategy in any case, underlying), and counts as started
 * when it is live or stopped on purpose (its stop-loss, its target, the
 * close, a person). One stopped by a fault and not restarted is missing, as
 * Sentinel would say. Done at the moment the last planned run first went
 * live, when every one of them is in the day's runs. Only on a trading day
 * (the plan deploys nothing on a Sunday), and not before the day's runs have
 * been read, unless every planned run is live.
 */
function planReading(item: CheckupItem, ctx: Ctx): Reading | null {
  const plan = ctx.plan
  if (!plan || plan.runs.length === 0 || !ctx.tradingDay) return null
  if (ctx.runs == null && !plan.runs.every((p) => p.isLive)) return null
  const key = (userId: number | null, strategy: string, underlying: string) =>
    `${userId}|${strategy.toLowerCase()}|${underlying.toUpperCase()}`
  const byKey = new Map<string, PlanRunLike[]>()
  for (const r of ctx.runs ?? []) {
    if (r.role === 'alerts' || dayOf(r.startedUtc) !== ctx.today) continue
    const k = key(r.userId, r.strategyName, r.underlying)
    byKey.set(k, [...(byKey.get(k) ?? []), r])
  }
  const planned = plan.runs.length
  const missing: string[] = []
  let live = 0
  let lastStart: number | null = null
  let startsKnown = true
  for (const p of plan.runs) {
    const own = p.userId == null ? [] : (byKey.get(key(p.userId, p.strategy, p.underlying)) ?? [])
    const deliberate = own.some((r) => {
      const kind = stopKind(r)
      return kind != null && kind !== 'fault'
    })
    if (p.isLive) live++
    else if (!deliberate) {
      missing.push(`${p.account} ${p.strategy} ${p.underlying.toUpperCase()}`)
      continue
    }
    const first = Math.min(...own.map((r) => msOf(r.startedUtc) ?? Infinity))
    if (Number.isFinite(first)) lastStart = Math.max(lastStart ?? first, first)
    else startsKnown = false
  }

  const said = sentinelPlanLive(item.detail)
  let reading: Omit<Reading, 'same'>
  if (missing.length === 0) {
    const at = startsKnown ? lastStart : null
    const done = at != null ? `Done ${istHm(new Date(at).toISOString())} — ` : 'Done — '
    const detail =
      live === planned
        ? `${done}${live} of ${planned} planned runs live${at != null ? '' : ' now'}.`
        : `${done}all ${planned} planned runs started; ${live} live now, ${planned - live} since stopped.`
    reading = { state: 'ok', detail, action: '', sinceMs: at != null && at >= ctx.checkedMs ? at : null }
  } else if (istMinute(ctx.nowMs) >= PLAN_START_MIN) {
    reading = {
      state: 'fail',
      detail: `${missing.length} of ${planned} planned runs are not live: ${listed(missing)}.`,
      action: `Start them from Trade → Runs. Why they did not start is in logs/market-open-${ctx.today}.log.`,
      sinceMs: null,
    }
  } else {
    reading = {
      state: 'info',
      detail: `${missing.length} of ${planned} planned runs are not live yet: the morning job deploys the plan after the 09:15 open.`,
      action: '',
      sinceMs: null,
    }
  }
  const same = reading.state === item.state && said != null && said.live === live && said.planned === planned
  return { ...reading, same }
}

/** Signed in now: connected, not asking to reconnect, and not past its expiry. */
function signedIn(p: Provider, nowMs: number): boolean {
  const s = p.session
  const ends = msOf(s.expiresUtc)
  return s.isConnected && !s.needsReconnect && (ends == null || ends > nowMs)
}

/** "15:20", or "03:33 tomorrow" for a moment on a later IST day. */
function whenText(ms: number, nowMs: number): string {
  const hm = istHm(new Date(ms).toISOString())
  return istDay(ms) === istDay(nowMs) ? hm : istDay(ms) > istDay(nowMs) ? `${hm} tomorrow` : `${hm} on ${istDay(ms)}`
}

/**
 * The Dhan token now. Sentinel's ok and warn were signed in (warn: ending
 * before the day's last close), its fail and info signed out (info: the
 * automatic sign-in has not had its turn yet). Where the Desk sees the same,
 * Sentinel's fuller words stand.
 */
function dhanReading(item: CheckupItem, ctx: Ctx): Reading | null {
  const dhan = ctx.providers?.find((p) => p.key === 'dhan')
  if (!dhan?.isConfigured) return null
  const was = item.state === 'ok' || item.state === 'warn' ? true : item.state === 'fail' || item.state === 'info' ? false : null
  const on = signedIn(dhan, ctx.nowMs)
  if (!on) {
    if (was === false) return { state: item.state, detail: item.detail, action: item.action, same: true, sinceMs: null }
    const ended = msOf(dhan.session.expiresUtc)
    return {
      state: 'fail',
      detail: `Signed out.${ended != null && ended <= ctx.nowMs ? ` The token ended at ${whenText(ended, ctx.nowMs)}.` : ''}`,
      action: "Sign in on Connectors → Dhan → Connect. Until then no strategy gets Dhan's data.",
      same: false,
      sinceMs: null,
    }
  }
  const ends = msOf(dhan.session.expiresUtc)
  // Whether the token outlasts the day cannot be said without the day's close:
  // Sentinel's "ends too early" then stands.
  if (item.state === 'warn' && (ctx.lastCloseMs == null || ends == null)) return null
  const since = msOf(dhan.session.connectedUtc)
  const sinceMs = since != null && since >= ctx.checkedMs ? since : null
  if (ends != null && ctx.lastCloseMs != null && ends < ctx.lastCloseMs) {
    return {
      state: 'warn',
      detail: `Signed in, but the token ends at ${whenText(ends, ctx.nowMs)}, before today's last close at ${whenText(ctx.lastCloseMs, ctx.nowMs)}.`,
      action: `Sign in again on Connectors → Dhan → Connect before ${whenText(ends, ctx.nowMs)}.`,
      same: item.state === 'warn',
      sinceMs,
    }
  }
  return {
    state: 'ok',
    detail: `Signed in${since != null ? ` since ${whenText(since, ctx.nowMs)}` : ''}${ends != null ? `; the token is good until ${whenText(ends, ctx.nowMs)}` : ''}.`,
    action: '',
    same: item.state === 'ok',
    sinceMs,
  }
}

/** The FYERS backup now: Sentinel's ok was signed in, its warn and info signed out. */
function fyersReading(item: CheckupItem, ctx: Ctx): Reading | null {
  const fyers = ctx.providers?.find((p) => p.key === 'fyers')
  if (!fyers?.isConfigured) return null
  const was = item.state === 'ok' ? true : item.state === 'warn' || item.state === 'info' ? false : null
  const on = signedIn(fyers, ctx.nowMs)
  if (on === was) return { state: item.state, detail: item.detail, action: item.action, same: true, sinceMs: null }
  if (on) {
    const since = msOf(fyers.session.connectedUtc)
    return {
      state: 'ok',
      detail: `Signed in${since != null ? ` since ${whenText(since, ctx.nowMs)}` : ''}: ready as the backup feed.`,
      action: '',
      same: false,
      sinceMs: since != null && since >= ctx.checkedMs ? since : null,
    }
  }
  return {
    state: 'warn',
    detail: 'Signed out since the checkup: the backup feed is not ready if Dhan goes silent.',
    action: 'Sign in on Connectors → FYERS.',
    same: false,
    sinceMs: null,
  }
}

/** The feeds, while one must be running (feedsExpected). Sentinel's "Running…" and "Still running…" were running. */
function feedsReading(item: CheckupItem, ctx: Ctx): Reading | null {
  if (ctx.feedsExpected !== true || !ctx.feeds) return null
  const running = ctx.feeds.filter((f) => f.isRunning).map((f) => f.displayName || f.key)
  const was = /^(Running|Still running)\b/.test(item.detail) ? true : /^(No live feed|Every feed)\b/.test(item.detail) ? false : null
  const reading: Omit<Reading, 'same'> = running.length
    ? { state: 'ok', detail: `Running: ${listed(running)}.`, action: '', sinceMs: null }
    : {
        state: 'fail',
        detail: 'No live feed is running.',
        action: 'Start the Dhan feed on Data → Live feeds.',
        sinceMs: null,
      }
  return { ...reading, same: reading.state === item.state && was === running.length > 0 }
}

const READERS: Readonly<Record<string, (item: CheckupItem, ctx: Ctx) => Reading | null>> = {
  plan: planReading,
  'dhan-token': dhanReading,
  'fyers-backup': fyersReading,
  feeds: feedsReading,
}

/** The item keys the Desk can re-read itself. */
export const LIVE_KEYS: readonly string[] = Object.keys(READERS)

/** One item as the Desk shows it. */
export interface ItemNow {
  /** The item as Sentinel wrote it. */
  item: CheckupItem
  /** 'now': the Desk re-read it just now; 'checkup': as Sentinel saw it at the checkup's time. */
  source: 'now' | 'checkup'
  /** The Desk's reading differs from Sentinel's; then `state`, `detail` and `action` are the Desk's. */
  changed: boolean
  state: string
  detail: string
  action: string
  /** When it became what it is now, when known (the last planned run going live, a sign-in); else null. */
  sinceMs: number | null
}

// ---------------------------------------------------------------- the whole checkup

export interface CheckupNow {
  items: ItemNow[]
  /** Any item the Desk re-read reads differently from what Sentinel saw. */
  changed: boolean
  /** The verdict the items read as now, Sentinel's rule: any fail is action, else any warn is attention. */
  verdict: CheckupVerdict
  /** Items that need a person now, and ones worth a look. */
  toDo: number
  toLook: number
  /** Sentinel listed something to do or look at, and every one of them now reads as fine. */
  done: boolean
  /** When the last of it came right, when known. */
  doneMs: number | null
  /** 'sentinel': the badge and headline are Sentinel's own; 'now': worked out here from the items as they are now. */
  voice: 'sentinel' | 'now'
  badge: { label: string; tone: Tone }
  headline: string
}

/** How a slot's "done" line starts: "Before the open: done 09:19". */
const DONE_LEAD: Record<string, string> = {
  morning: 'Before the open',
  close: 'After the close',
  night: 'End of day',
  weekly: 'This week',
  'on-request': 'Checkup',
}

/** Sentinel's verdict for these states. */
function verdictOf(states: readonly string[]): CheckupVerdict {
  return states.includes('fail') ? 'action' : states.includes('warn') ? 'attention' : 'ok'
}

/** The checkup as Sentinel wrote it, nothing laid over it. */
function asWritten(c: CheckupDetail): CheckupNow {
  const items: ItemNow[] = c.items.map((item) => ({
    item,
    source: 'checkup',
    changed: false,
    state: item.state,
    detail: item.detail,
    action: item.action,
    sinceMs: null,
  }))
  const states = c.items.map((i) => i.state)
  return {
    items,
    changed: false,
    verdict: verdictOf(states),
    toDo: states.filter((s) => s === 'fail').length,
    toLook: states.filter((s) => s === 'warn').length,
    done: false,
    doneMs: null,
    voice: 'sentinel',
    badge: verdictBadge(c),
    headline: c.headline,
  }
}

/**
 * The latest checkup with what the Desk can read now laid over it. Only a
 * finished checkup of the last day is: a failed one has no verdict to
 * revise, an old one is said to be old (staleNote) rather than patched, and
 * the weekly one's token item is about next Monday's sign-in, not today's.
 */
export function checkupNow(c: CheckupDetail, reads: DeskReads): CheckupNow {
  const checkedMs = msOf(c.completedUtc)
  const { nowMs } = reads
  if (c.status !== 'done' || checkedMs == null || nowMs - checkedMs >= STALE_HOURS * 3_600_000 || c.slot === 'weekly') {
    return asWritten(c)
  }
  const fresh = <T,>(read: LiveRead<T> | undefined): T | undefined =>
    read && read.data !== undefined && read.atMs >= checkedMs && nowMs - read.atMs <= LIVE_FRESH_MS ? read.data : undefined
  const ctx: Ctx = {
    nowMs,
    checkedMs,
    today: istDay(nowMs),
    tradingDay: todaysSession(reads.nse, nowMs) != null,
    plan: fresh(reads.plan),
    runs: reads.runs ?? null,
    providers: fresh(reads.providers),
    feeds: fresh(reads.feeds),
    feedsExpected: feedsExpected(nowMs, reads.nse),
    lastCloseMs: lastCloseMs(nowMs, reads.nse, reads.mcx),
  }

  const items: ItemNow[] = c.items.map((item) => {
    const reading = READERS[item.key]?.(item, ctx) ?? null
    if (!reading) return { item, source: 'checkup', changed: false, state: item.state, detail: item.detail, action: item.action, sinceMs: null }
    if (reading.same) return { item, source: 'now', changed: false, state: item.state, detail: item.detail, action: item.action, sinceMs: null }
    return { item, source: 'now', changed: true, state: reading.state, detail: reading.detail, action: reading.action, sinceMs: reading.sinceMs }
  })

  const states = items.map((i) => i.state)
  const verdict = verdictOf(states)
  const toDo = states.filter((s) => s === 'fail').length
  const toLook = states.filter((s) => s === 'warn').length
  const changed = items.some((i) => i.changed)
  const flagged = items.filter((i) => i.item.state === 'fail' || i.item.state === 'warn')
  const done = flagged.length > 0 && toDo === 0 && toLook === 0 && flagged.every((i) => i.source === 'now' && i.state === 'ok')
  const times = flagged.map((i) => i.sinceMs)
  const doneMs = done && times.every((t) => t != null) ? Math.max(...(times as number[])) : null

  const wasToDo = c.items.filter((i) => i.state === 'fail').length
  const wasToLook = c.items.filter((i) => i.state === 'warn').length
  // Sentinel's headline stands while it is still true in its numbers; the
  // rows then carry what changed. Once it is not, the Desk says its own.
  if (!changed || (verdict === c.verdict && toDo === wasToDo && toLook === wasToLook)) {
    return { ...asWritten(c), items, changed, done, doneMs }
  }
  let badge: CheckupNow['badge']
  let headline: string
  if (done) {
    badge = { label: 'Done', tone: 'pos' }
    headline = `${DONE_LEAD[c.slot] ?? 'Checkup'}: done${doneMs != null ? ` ${istHm(new Date(doneMs).toISOString())}` : ''}`
  } else if (toDo > 0) {
    badge = { label: 'Needs action', tone: 'neg' }
    headline = `${plural(toDo, 'thing')} to do${toLook ? `, ${toLook} more worth a look` : ''}`
  } else if (toLook > 0) {
    badge = { label: 'Worth a look', tone: 'warn' }
    headline = `${plural(toLook, 'thing')} worth a look`
  } else {
    badge = { label: 'All good', tone: 'pos' }
    headline = 'Nothing to do now'
  }
  return { items, changed, verdict, toDo, toLook, done, doneMs, voice: 'now', badge, headline }
}

/**
 * Whether the checkup panel folds to its one line. The morning checkup is the
 * readiness list: once the open has passed and nothing on it needs a hand
 * now, it is history, and it gives its room to the day. Anything still wrong
 * keeps the list open, whatever the hour.
 */
export function foldReadiness(slot: string, clock: Phase, now: Pick<CheckupNow, 'toDo' | 'toLook'>): boolean {
  return slot === 'morning' && clock !== 'pre' && now.toDo === 0 && now.toLook === 0
}
