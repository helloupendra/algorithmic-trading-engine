import { describe, expect, it } from 'vitest'

import type { CheckupDetail, CheckupItem } from './checkup'
import type { DeskReads, PlanRunLike } from './checkupNow'
import { LIVE_FRESH_MS, checkupNow, feedsExpected, foldReadiness, lastCloseMs } from './checkupNow'
import type { DeskPlanResponse, DeskPlanRun, LiveFeed, Provider } from './types'

/**
 * The Desk's checkup as of now, pinned on the morning that asked for it:
 * Mon 28 Sep, the 08:55 checkup said "23 of 23 planned runs are not live",
 * all 23 were live by 09:19, and at 13:15 the Desk still said "1 thing to do
 * before the open".
 */

const ist = (hm: string, day = '2026-09-28') => Date.parse(`${day}T${hm}:00+05:30`)
const istIso = (hm: string, day = '2026-09-28') => new Date(ist(hm, day)).toISOString()

function item(over: Partial<CheckupItem>): CheckupItem {
  return { key: 'x', area: 'Desk', title: 'Something', state: 'ok', detail: '', action: '', link: null, ...over }
}

const DHAN_OK = item({
  key: 'dhan-token',
  title: 'Dhan token',
  detail: 'Signed in; the token is good until 03:33 tomorrow. Taken by the automatic sign-in at 08:02.',
})
const FYERS_INFO = item({
  key: 'fyers-backup',
  title: 'FYERS backup',
  state: 'info',
  detail: 'Not signed in. The data runs on Dhan; FYERS is only the backup.',
  action: 'If you want the backup ready, sign in on Connectors → FYERS.',
})
const FEEDS_OK = item({ key: 'feeds', title: 'Live feeds', detail: 'Running: Dhan.' })
const PLAN_FAIL = item({
  key: 'plan',
  title: 'Morning plan',
  state: 'fail',
  detail: '23 of 23 planned runs are not live: admin Fulcrum NIFTY, … and 17 more.',
  action: 'Start them from Trade → Runs. Why they did not start is in logs/market-open-2026-09-28.log.',
})
const ARCHIVE_OK = item({ key: 'archive', title: 'Archive to Drive', detail: 'Ran today: 12 files copied and verified.' })

function morning(items: CheckupItem[], over: Partial<CheckupDetail> = {}): CheckupDetail {
  const states = items.map((i) => i.state)
  return {
    id: 7,
    slot: 'morning',
    status: 'done',
    verdict: states.includes('fail') ? 'action' : states.includes('warn') ? 'attention' : 'ok',
    headline: '1 thing to do before the open',
    requestedUtc: null,
    requestedBy: '',
    startedUtc: istIso('08:55'),
    completedUtc: istIso('08:55'),
    host: 'desk-server',
    counts: { ok: 0, warn: 0, fail: 0, info: 0, skip: 0 },
    items,
    itemsUnreadable: false,
    error: '',
    notifiedUtc: null,
    ...over,
  }
}

const TODAY_CHECKUP = morning([DHAN_OK, FYERS_INFO, FEEDS_OK, PLAN_FAIL, ARCHIVE_OK])

// ---------------------------------------------------------------- what the Desk reads now

const NSE = { isTradingDay: true, sessionOpenUtc: istIso('09:15'), sessionCloseUtc: istIso('15:30') }
const MCX = { isTradingDay: true, sessionOpenUtc: istIso('09:00'), sessionCloseUtc: istIso('23:30') }

const PLAN_KEYS: Array<[string, number, string, string]> = [
  ['admin', 1, 'Fulcrum', 'NIFTY'],
  ['admin', 1, 'Fulcrum', 'BANKNIFTY'],
  ['coderforchange', 7, 'Fulcrum', 'NIFTY'],
]

function planRun(i: number, isLive: boolean): DeskPlanRun {
  const [account, userId, strategy, underlying] = PLAN_KEYS[i]
  return { account, userId, strategy, underlying, lots: 2, isLive, runId: isLive ? 900 + i : null }
}

function plan(live: boolean[]): DeskPlanResponse {
  const runs = live.map((l, i) => planRun(i, l))
  return {
    file: '/srv/config/morning-plan.txt',
    modifiedUtc: istIso('08:00'),
    accounts: ['admin', 'coderforchange'],
    lines: [],
    runs,
    planned: runs.length,
    live: runs.filter((r) => r.isLive).length,
    warnings: [],
  }
}

function dayRun(i: number, startedHm: string, over: Partial<PlanRunLike> = {}): PlanRunLike {
  const [, userId, strategyName, underlying] = PLAN_KEYS[i]
  return {
    runId: 900 + i,
    userId,
    strategyName,
    underlying,
    startedUtc: istIso(startedHm),
    role: null,
    isActive: true,
    stoppedBy: null,
    stopReason: null,
    ...over,
  }
}

const ALL_STARTED = [dayRun(0, '09:16'), dayRun(1, '09:17'), dayRun(2, '09:19')]

function provider(key: string, over: Partial<Provider['session']> = {}, configured = true): Provider {
  return {
    key,
    displayName: key,
    isConfigured: configured,
    session: { isConnected: true, connectedUtc: istIso('08:02'), ageSeconds: 0, needsReconnect: false, expiresUtc: istIso('03:33', '2026-09-29'), ...over },
  } as Provider
}

const SIGNED_OUT = { isConnected: false, connectedUtc: null, needsReconnect: true, expiresUtc: null }

function feed(key: string, isRunning: boolean): LiveFeed {
  return { key, displayName: key === 'dhan' ? 'Dhan' : key.toUpperCase(), isRunning, managed: true, processId: 1, source: 'managed' }
}

/** 13:15 on 28 Sep with everything read a few seconds ago: all 23 live since 09:19, Dhan in, FYERS out, Dhan's feed on. */
function reads(over: Partial<DeskReads> = {}): DeskReads {
  const nowMs = over.nowMs ?? ist('13:15')
  const at = nowMs - 5_000
  return {
    nowMs,
    plan: { data: plan([true, true, true]), atMs: at },
    runs: ALL_STARTED,
    providers: { data: [provider('dhan'), provider('fyers', SIGNED_OUT)], atMs: at },
    feeds: { data: [feed('dhan', true), feed('fyers', false)], atMs: at },
    nse: NSE,
    mcx: MCX,
    ...over,
  }
}

const byKey = (now: ReturnType<typeof checkupNow>, key: string) => now.items.find((i) => i.item.key === key)!

// ---------------------------------------------------------------- the morning plan

describe('the morning plan, read again', () => {
  it('shows the plan done at 09:19 once all 23 are live, with what Sentinel saw kept', () => {
    const now = checkupNow(TODAY_CHECKUP, reads())
    const p = byKey(now, 'plan')
    expect(p).toMatchObject({ source: 'now', changed: true, state: 'ok', action: '' })
    expect(p.detail).toBe('Done 09:19 — 3 of 3 planned runs live.')
    expect(p.sinceMs).toBe(ist('09:19'))
    expect(p.item.detail).toBe(PLAN_FAIL.detail)
  })

  it('says "now" rather than a time it does not have', () => {
    const now = checkupNow(TODAY_CHECKUP, reads({ runs: [] }))
    expect(byKey(now, 'plan').detail).toBe('Done — 3 of 3 planned runs live now.')
    expect(byKey(now, 'plan').sinceMs).toBeNull()
  })

  it('counts a planned run stopped by its stop-loss as started, and one stopped by a fault as missing', () => {
    const sl = dayRun(1, '09:17', { isActive: false, stoppedBy: 'risk-guard', stopReason: 'Stop loss hit' })
    const done = checkupNow(TODAY_CHECKUP, reads({ plan: { data: plan([true, false, true]), atMs: ist('13:15') }, runs: [ALL_STARTED[0], sl, ALL_STARTED[2]] }))
    expect(byKey(done, 'plan')).toMatchObject({ state: 'ok', detail: 'Done 09:19 — all 3 planned runs started; 2 live now, 1 since stopped.' })

    const fault = dayRun(1, '09:17', { isActive: false, stoppedBy: 'runner', stopReason: 'Runner exited' })
    const missing = checkupNow(TODAY_CHECKUP, reads({ plan: { data: plan([true, false, true]), atMs: ist('13:15') }, runs: [ALL_STARTED[0], fault, ALL_STARTED[2]] }))
    expect(byKey(missing, 'plan')).toMatchObject({
      state: 'fail',
      detail: '1 of 3 planned runs are not live: admin Fulcrum BANKNIFTY.',
      changed: true,
    })
  })

  it('reads runs not live yet before 09:25 as a note, and as a failure after', () => {
    const early = checkupNow(TODAY_CHECKUP, reads({ nowMs: ist('09:05'), plan: { data: plan([false, false, false]), atMs: ist('09:05') }, runs: [] }))
    expect(byKey(early, 'plan')).toMatchObject({ state: 'info', changed: true })
    expect(byKey(early, 'plan').detail).toMatch(/^3 of 3 planned runs are not live yet/)
    const late = checkupNow(TODAY_CHECKUP, reads({ nowMs: ist('09:40'), plan: { data: plan([true, false, false]), atMs: ist('09:40') }, runs: [ALL_STARTED[0]] }))
    expect(byKey(late, 'plan')).toMatchObject({ state: 'fail', changed: true })
    expect(byKey(late, 'plan').action).toContain('logs/market-open-2026-09-28.log')
  })

  it("keeps Sentinel's words while the count is the one it gave", () => {
    const all = item({ key: 'plan', title: 'Morning plan', detail: 'All 3 planned runs are live (admin 2, coderforchange 1).' })
    const now = checkupNow(morning([all]), reads())
    expect(byKey(now, 'plan')).toMatchObject({ source: 'now', changed: false, detail: all.detail })
  })

  it('does not judge the plan before the day’s runs are read, unless every run is live', () => {
    const now = checkupNow(TODAY_CHECKUP, reads({ plan: { data: plan([true, false, true]), atMs: ist('13:15') }, runs: undefined }))
    expect(byKey(now, 'plan')).toMatchObject({ source: 'checkup', state: 'fail', detail: PLAN_FAIL.detail })
  })

  it('does not judge the plan on a day with no session', () => {
    const sunday = { isTradingDay: false, sessionOpenUtc: istIso('09:15'), sessionCloseUtc: istIso('15:30') }
    const now = checkupNow(TODAY_CHECKUP, reads({ nse: sunday, plan: { data: plan([false, false, false]), atMs: ist('13:15') } }))
    expect(byKey(now, 'plan').source).toBe('checkup')
  })
})

// ---------------------------------------------------------------- tokens

describe('the Dhan token and the FYERS backup, read again', () => {
  it("confirms a token still signed in with Sentinel's fuller words", () => {
    const now = checkupNow(TODAY_CHECKUP, reads())
    expect(byKey(now, 'dhan-token')).toMatchObject({ source: 'now', changed: false, state: 'ok', detail: DHAN_OK.detail })
  })

  it('says a token that signed out since the checkup is a thing to do', () => {
    const now = checkupNow(TODAY_CHECKUP, reads({ providers: { data: [provider('dhan', SIGNED_OUT), provider('fyers', SIGNED_OUT)], atMs: ist('13:15') } }))
    expect(byKey(now, 'dhan-token')).toMatchObject({ changed: true, state: 'fail', detail: 'Signed out.' })
    expect(byKey(now, 'dhan-token').action).toMatch(/^Sign in on Connectors → Dhan/)
  })

  it('reads a token past its expiry as signed out, whatever the flag says', () => {
    const now = checkupNow(TODAY_CHECKUP, reads({ providers: { data: [provider('dhan', { expiresUtc: istIso('12:00') })], atMs: ist('13:15') } }))
    expect(byKey(now, 'dhan-token')).toMatchObject({ state: 'fail', detail: 'Signed out. The token ended at 12:00.' })
  })

  it('shows a sign-in after the checkup as done, and when', () => {
    const out = item({ key: 'dhan-token', title: 'Dhan token', state: 'fail', detail: 'Dhan is signed out, and the last automatic sign-in failed.' })
    const now = checkupNow(morning([out]), reads({ providers: { data: [provider('dhan', { connectedUtc: istIso('09:02') })], atMs: ist('13:15') } }))
    expect(byKey(now, 'dhan-token')).toMatchObject({ changed: true, state: 'ok', sinceMs: ist('09:02') })
    expect(byKey(now, 'dhan-token').detail).toBe('Signed in since 09:02; the token is good until 03:33 tomorrow.')
  })

  it("keeps Sentinel's reason for a token still signed out", () => {
    const waiting = item({ key: 'dhan-token', state: 'info', detail: 'Signed out; the automatic sign-in takes a new token between 08:00 and 08:40 today.' })
    const now = checkupNow(morning([waiting]), reads({ providers: { data: [provider('dhan', SIGNED_OUT)], atMs: ist('13:15') } }))
    expect(byKey(now, 'dhan-token')).toMatchObject({ changed: false, state: 'info', detail: waiting.detail })
  })

  it("clears \"ends before the close\" once the token outlasts today's last close", () => {
    const short = item({ key: 'dhan-token', state: 'warn', detail: 'Signed in, but the token ends at 20:00 today, before today\'s last close at 23:30.' })
    const renewed = checkupNow(morning([short]), reads())
    expect(byKey(renewed, 'dhan-token')).toMatchObject({ changed: true, state: 'ok' })
    const still = checkupNow(morning([short]), reads({ providers: { data: [provider('dhan', { expiresUtc: istIso('20:00') })], atMs: ist('13:15') } }))
    expect(byKey(still, 'dhan-token')).toMatchObject({ changed: false, state: 'warn', detail: short.detail })
    const unknownClose = checkupNow(morning([short]), reads({ mcx: null, nse: null }))
    expect(byKey(unknownClose, 'dhan-token').source).toBe('checkup')
  })

  it('confirms FYERS still signed out, and shows it signed in or out once that changes', () => {
    expect(byKey(checkupNow(TODAY_CHECKUP, reads()), 'fyers-backup')).toMatchObject({ changed: false, state: 'info', detail: FYERS_INFO.detail })
    const signedIn = checkupNow(TODAY_CHECKUP, reads({ providers: { data: [provider('dhan'), provider('fyers', { connectedUtc: istIso('10:12') })], atMs: ist('13:15') } }))
    expect(byKey(signedIn, 'fyers-backup')).toMatchObject({ changed: true, state: 'ok', detail: 'Signed in since 10:12: ready as the backup feed.' })
    const wasIn = item({ key: 'fyers-backup', detail: 'Signed in: ready as the backup feed.' })
    const lost = checkupNow(morning([wasIn]), reads())
    expect(byKey(lost, 'fyers-backup')).toMatchObject({ changed: true, state: 'warn' })
  })

  it('leaves a connector the desk has not set up as Sentinel said', () => {
    const now = checkupNow(TODAY_CHECKUP, reads({ providers: { data: [provider('dhan', {}, false)], atMs: ist('13:15') } }))
    expect(byKey(now, 'dhan-token').source).toBe('checkup')
    expect(byKey(now, 'fyers-backup').source).toBe('checkup')
  })
})

// ---------------------------------------------------------------- feeds

describe('the feeds, read again', () => {
  it('confirms a feed still running', () => {
    expect(byKey(checkupNow(TODAY_CHECKUP, reads()), 'feeds')).toMatchObject({ source: 'now', changed: false, detail: 'Running: Dhan.' })
  })

  it('says a feed that stopped in the session is a thing to do', () => {
    const now = checkupNow(TODAY_CHECKUP, reads({ feeds: { data: [feed('dhan', false)], atMs: ist('13:15') } }))
    expect(byKey(now, 'feeds')).toMatchObject({ changed: true, state: 'fail', detail: 'No live feed is running.' })
  })

  it('shows a feed started after the checkup as running', () => {
    const none = item({ key: 'feeds', state: 'fail', detail: 'No live feed is running.', action: 'Start the Dhan feed on Data → Live feeds.' })
    const now = checkupNow(morning([none]), reads())
    expect(byKey(now, 'feeds')).toMatchObject({ changed: true, state: 'ok', detail: 'Running: Dhan.', action: '' })
  })

  it('does not take "every feed is stopped" at night for "running" in the day, nor the other way', () => {
    const stoppedAtNight = item({ key: 'feeds', detail: 'Every feed is stopped.' })
    const now = checkupNow(morning([stoppedAtNight], { slot: 'night', completedUtc: istIso('00:15') }), reads())
    expect(byKey(now, 'feeds')).toMatchObject({ changed: true, state: 'ok', detail: 'Running: Dhan.' })
  })

  it('leaves the feeds as Sentinel said outside the hours a feed must run', () => {
    const evening = checkupNow(TODAY_CHECKUP, reads({ nowMs: ist('16:10'), feeds: { data: [feed('dhan', false)], atMs: ist('16:10') } }))
    expect(byKey(evening, 'feeds').source).toBe('checkup')
    expect(feedsExpected(ist('08:40'), NSE)).toBeNull()
    expect(feedsExpected(ist('08:50'), NSE)).toBe(true)
    expect(feedsExpected(ist('15:30'), NSE)).toBeNull()
    expect(feedsExpected(ist('11:00'), { ...NSE, isTradingDay: false })).toBeNull()
    expect(feedsExpected(ist('11:00'), null)).toBeNull()
  })

  it("takes the day's last close from MCX when it trades, else the NSE", () => {
    expect(lastCloseMs(ist('11:00'), NSE, MCX)).toBe(ist('23:30'))
    expect(lastCloseMs(ist('11:00'), NSE, null)).toBe(ist('15:30'))
    expect(lastCloseMs(ist('11:00'), NSE, { ...MCX, sessionOpenUtc: istIso('09:00', '2026-09-25'), sessionCloseUtc: istIso('23:30', '2026-09-25') })).toBe(ist('15:30'))
  })
})

// ---------------------------------------------------------------- stale and fresh

describe('a live read counts only while it is fresh', () => {
  it('ignores a read older than the checkup', () => {
    const now = checkupNow(TODAY_CHECKUP, reads({ plan: { data: plan([true, true, true]), atMs: ist('08:54') } }))
    expect(byKey(now, 'plan')).toMatchObject({ source: 'checkup', state: 'fail' })
  })

  it('ignores a read that has not been renewed for five minutes', () => {
    const at = ist('13:15') - LIVE_FRESH_MS - 1
    const now = checkupNow(TODAY_CHECKUP, reads({ plan: { data: plan([true, true, true]), atMs: at }, providers: { data: [], atMs: at }, feeds: { data: [], atMs: at } }))
    expect(now.items.every((i) => i.source === 'checkup')).toBe(true)
    expect(now.voice).toBe('sentinel')
    expect(now.badge).toEqual({ label: 'Needs action', tone: 'neg' })
  })

  it('ignores a read that never came', () => {
    const now = checkupNow(TODAY_CHECKUP, { nowMs: ist('13:15'), plan: { data: undefined, atMs: 0 }, nse: NSE, mcx: MCX })
    expect(now.items.every((i) => i.source === 'checkup')).toBe(true)
  })

  it('lays nothing over a failed checkup, an old one, or the weekly one', () => {
    const failed = checkupNow({ ...TODAY_CHECKUP, status: 'failed', verdict: '' }, reads())
    expect(failed.items.every((i) => i.source === 'checkup')).toBe(true)
    expect(failed.badge.label).toBe('Did not finish')
    const old = checkupNow({ ...TODAY_CHECKUP, completedUtc: istIso('08:55', '2026-09-27') }, reads())
    expect(old.items.every((i) => i.source === 'checkup')).toBe(true)
    const weekly = checkupNow({ ...TODAY_CHECKUP, slot: 'weekly' }, reads())
    expect(weekly.items.every((i) => i.source === 'checkup')).toBe(true)
  })

  it('marks every item it cannot read itself as Sentinel saw it', () => {
    expect(byKey(checkupNow(TODAY_CHECKUP, reads()), 'archive')).toMatchObject({ source: 'checkup', changed: false, detail: ARCHIVE_OK.detail })
  })
})

// ---------------------------------------------------------------- the badge and the headline

describe('the badge and the headline, as of now', () => {
  it('says "Before the open: done 09:19" when all that needed a hand is done', () => {
    const now = checkupNow(TODAY_CHECKUP, reads())
    expect(now).toMatchObject({ voice: 'now', done: true, doneMs: ist('09:19'), toDo: 0, toLook: 0, verdict: 'ok' })
    expect(now.badge).toEqual({ label: 'Done', tone: 'pos' })
    expect(now.headline).toBe('Before the open: done 09:19')
  })

  it('leaves the time out when it does not know it', () => {
    const now = checkupNow(TODAY_CHECKUP, reads({ runs: [] }))
    expect(now.headline).toBe('Before the open: done')
    expect(now.doneMs).toBeNull()
  })

  it("keeps Sentinel's badge and sentence when nothing it said has changed", () => {
    const now = checkupNow(TODAY_CHECKUP, reads({ plan: { data: plan([false, false, false]), atMs: ist('13:15') }, runs: [] }))
    // 3 of 3 not live, as it said (its fixture says 23, the plan now 3: a different count is a change).
    expect(byKey(now, 'plan').changed).toBe(true)
    const same = morning([DHAN_OK, item({ ...PLAN_FAIL, detail: '3 of 3 planned runs are not live: admin Fulcrum NIFTY, admin Fulcrum BANKNIFTY, coderforchange Fulcrum NIFTY.' })])
    const unchanged = checkupNow(same, reads({ plan: { data: plan([false, false, false]), atMs: ist('13:15') }, runs: [] }))
    expect(unchanged).toMatchObject({ voice: 'sentinel', changed: false, done: false })
    expect(unchanged.badge).toEqual({ label: 'Needs action', tone: 'neg' })
    expect(unchanged.headline).toBe('1 thing to do before the open')
  })

  it('counts what is still to do, its own and what it could not re-read', () => {
    const disk = item({ key: 'disk', state: 'warn', detail: '12% free.' })
    const now = checkupNow(morning([DHAN_OK, PLAN_FAIL, disk], { headline: '1 thing to do before the open, 1 more worth a look' }), reads())
    expect(now).toMatchObject({ voice: 'now', done: false, toDo: 0, toLook: 1, verdict: 'attention' })
    expect(now.badge).toEqual({ label: 'Worth a look', tone: 'warn' })
    expect(now.headline).toBe('1 thing worth a look')
  })

  it('says a new failure plainly, never as Sentinel', () => {
    const now = checkupNow(
      morning([DHAN_OK, FEEDS_OK, ARCHIVE_OK], { headline: 'All clear before the open: 3 checks fine' }),
      reads({ feeds: { data: [feed('dhan', false)], atMs: ist('13:15') } }),
    )
    expect(now).toMatchObject({ voice: 'now', toDo: 1, verdict: 'action' })
    expect(now.badge).toEqual({ label: 'Needs action', tone: 'neg' })
    expect(now.headline).toBe('1 thing to do')
  })

  it('does not call a plan that is merely not due yet "done"', () => {
    const now = checkupNow(TODAY_CHECKUP, reads({ nowMs: ist('09:05'), plan: { data: plan([false, false, false]), atMs: ist('09:05') }, runs: [] }))
    expect(now).toMatchObject({ done: false, voice: 'now', toDo: 0 })
    expect(now.headline).toBe('Nothing to do now')
  })

  it("keeps an all-clear Sentinel sentence while it is still all clear, with the rows re-read", () => {
    const info = item({ ...PLAN_FAIL, state: 'info', detail: '23 of 23 planned runs are not live yet: the morning job deploys the plan after the 09:15 open.' })
    const now = checkupNow(morning([DHAN_OK, info], { headline: 'All clear before the open: 2 checks fine' }), reads())
    expect(byKey(now, 'plan')).toMatchObject({ changed: true, state: 'ok' })
    expect(now).toMatchObject({ voice: 'sentinel', done: false })
    expect(now.headline).toBe('All clear before the open: 2 checks fine')
  })
})

describe('foldReadiness', () => {
  it('folds the morning list to one line after the open, while nothing needs a hand', () => {
    expect(foldReadiness('morning', 'live', { toDo: 0, toLook: 0 })).toBe(true)
    expect(foldReadiness('morning', 'post', { toDo: 0, toLook: 0 })).toBe(true)
  })

  it('keeps it open before the open, when something is still wrong, and for any other checkup', () => {
    expect(foldReadiness('morning', 'pre', { toDo: 0, toLook: 0 })).toBe(false)
    expect(foldReadiness('morning', 'live', { toDo: 1, toLook: 0 })).toBe(false)
    expect(foldReadiness('morning', 'live', { toDo: 0, toLook: 1 })).toBe(false)
    expect(foldReadiness('close', 'post', { toDo: 0, toLook: 0 })).toBe(false)
  })
})
