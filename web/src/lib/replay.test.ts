import { describe, expect, it, vi } from 'vitest'

import {
  REPLAY_POLL_ACTIVE_MS,
  REPLAY_POLL_IDLE_MS,
  clockText,
  coverageNote,
  errorLine,
  isActiveState,
  isEndedState,
  istHms,
  launchReplay,
  minutesText,
  minutesTone,
  parseFromTime,
  progressText,
  readMinutes,
  readReplayDays,
  readReplayLogs,
  readReplaySession,
  readReplayStatus,
  recapStartBody,
  replayClock,
  replayNetTotal,
  replayPollMs,
  replayProgress,
  replayStateLabel,
  replayUnderlyingsFor,
  setupError,
  setupSummary,
  speedLabel,
  speedNote,
  startedRunId,
} from './replay'
import type { ReplayDay, ReplayRunDraft, ReplayRunPlan, ReplayStrategyInfo } from './replay'

/** 30 Sep 2026, 11:42:07 IST. */
const CLOCK_UTC = '2026-09-30T06:12:07Z'

const session = (over: Record<string, unknown> = {}) => ({
  id: 3,
  date: '2026-09-30',
  speed: 5,
  fromIst: '09:15',
  state: 'playing',
  clockUtc: CLOCK_UTC,
  clockIst: '11:42:07',
  progress: 0.4,
  ticksSent: 123456,
  startedUtc: '2026-10-01T12:00:00Z',
  endedUtc: null,
  error: null,
  runIds: [412, 413],
  startedBy: 'admin',
  runs: [
    { runId: 412, strategy: 'GhostNifty', underlying: 'NIFTY', account: 'admin', status: 'Running', netPnl: 1250.5 },
    { runId: 413, strategy: 'Fulcrum', underlying: 'BANKNIFTY', account: 'coderforchange', status: 'Running', netPnl: -640 },
  ],
  ...over,
})

describe('readReplayDays', () => {
  it('reads the contract, newest first', () => {
    const d = readReplayDays({
      days: [
        { date: '2026-09-29', weekday: 'Tue', minutes: { NIFTY: 375, BANKNIFTY: 375, SENSEX: 343, INDIAVIX: 375 }, sizeBytes: 52_000_000 },
        { date: '2026-09-30', weekday: 'Wed', minutes: { NIFTY: 374, BANKNIFTY: 375, SENSEX: 375, INDIAVIX: 0 }, sizeBytes: 61_000_000 },
      ],
      earliest: '2026-09-09',
      latest: '2026-09-30',
    })
    expect(d.days.map((x) => x.date)).toEqual(['2026-09-30', '2026-09-29'])
    expect(d.days[1]).toEqual({
      date: '2026-09-29',
      weekday: 'Tue',
      minutes: { NIFTY: 375, BANKNIFTY: 375, SENSEX: 343, INDIAVIX: 375 },
      sizeBytes: 52_000_000,
    })
    expect(d.earliest).toBe('2026-09-09')
    expect(d.latest).toBe('2026-09-30')
  })

  it('reads the minutes whatever the key spelling', () => {
    expect(readMinutes({ nifty: 375, bankNifty: 300, sensex: 12, indiavix: 375 })).toEqual({
      NIFTY: 375,
      BANKNIFTY: 300,
      SENSEX: 12,
      INDIAVIX: 375,
    })
    expect(readMinutes({ 'INDIA VIX': 370 }).INDIAVIX).toBe(370)
    expect(readMinutes({ VIX: 370 }).INDIAVIX).toBe(370)
  })

  it('keeps an unknown count unknown, never zero', () => {
    const m = readMinutes({ NIFTY: 'lots', BANKNIFTY: -3, SENSEX: null, INDIAVIX: Number.NaN, FINNIFTY: 375 })
    expect(m).toEqual({ NIFTY: null, BANKNIFTY: null, SENSEX: null, INDIAVIX: null })
    expect(readMinutes(null)).toEqual({ NIFTY: null, BANKNIFTY: null, SENSEX: null, INDIAVIX: null })
  })

  it('takes a bare array, drops rows without a date, and keeps the first of a repeated date', () => {
    const d = readReplayDays([
      { date: '2026-09-29T00:00:00', weekday: 'Tuesday', minutes: { NIFTY: 375 } },
      { date: 'yesterday' },
      { weekday: 'Mon' },
      'nonsense',
      { date: '2026-09-29', minutes: { NIFTY: 1 } },
      { date: '2026-09-28' },
    ])
    expect(d.days.map((x) => x.date)).toEqual(['2026-09-29', '2026-09-28'])
    expect(d.days[0].weekday).toBe('Tue')
    expect(d.days[0].minutes.NIFTY).toBe(375)
    // No weekday sent: the date's own.
    expect(d.days[1].weekday).toBe('Mon')
    expect(d.days[1].sizeBytes).toBeNull()
    // No range sent: the list's own ends.
    expect(d.earliest).toBe('2026-09-28')
    expect(d.latest).toBe('2026-09-29')
  })

  it('reads anything else as no days', () => {
    for (const body of [null, undefined, 'oops', 42, { days: 'x' }, {}]) {
      expect(readReplayDays(body)).toEqual({ days: [], earliest: null, latest: null })
    }
  })
})

describe('readReplayStatus', () => {
  it('reads a session that is playing', () => {
    const s = readReplayStatus({ canStart: false, whyNot: 'A replay is already playing.', session: session() })
    expect(s.canStart).toBe(false)
    expect(s.whyNot).toBe('A replay is already playing.')
    expect(s.session?.state).toBe('playing')
    expect(s.session?.runs).toHaveLength(2)
    expect(s.session?.startedBy).toBe('admin')
    expect(s.session?.runs[0]).toEqual({
      runId: 412,
      strategy: 'GhostNifty',
      underlying: 'NIFTY',
      account: 'admin',
      status: 'Running',
      netPnl: 1250.5,
    })
  })

  it('starts only when the API says so in as many words', () => {
    expect(readReplayStatus({ canStart: true, whyNot: 'ignored', session: null })).toEqual({ canStart: true, whyNot: null, session: null })
    expect(readReplayStatus({ canStart: 'true' }).canStart).toBe(false)
    expect(readReplayStatus({ canStart: false }).whyNot).toBe('The API did not say why a replay cannot start now.')
    expect(readReplayStatus(null)).toEqual({
      canStart: false,
      whyNot: 'The replay status did not come back from the API.',
      session: null,
    })
    expect(readReplayStatus('<html>').canStart).toBe(false)
  })

  it('reads a session body that is not one as no session', () => {
    expect(readReplayStatus({ canStart: true, session: { state: 'playing' } }).session).toBeNull()
    expect(readReplayStatus({ canStart: true, session: [] }).session).toBeNull()
  })
})

describe('readReplaySession', () => {
  it('keeps what it cannot read as not known', () => {
    const s = readReplaySession({ date: '2026-09-30', state: 'PLAYING', progress: 'half', runIds: [1, 'x', -2, 3], runs: [{ runId: 0 }, { runId: 7 }, null] })
    expect(s).not.toBeNull()
    expect(s!.state).toBe('playing')
    expect(s!.progress).toBeNull()
    expect(s!.speed).toBeNull()
    expect(s!.ticksSent).toBeNull()
    expect(s!.clockUtc).toBeNull()
    expect(s!.fromIst).toBe('09:15')
    expect(s!.startedBy).toBeNull()
    expect(s!.runIds).toEqual([1, 3])
    expect(s!.runs).toEqual([{ runId: 7, strategy: 'Run 7', underlying: '', account: '', status: '', netPnl: null }])
  })

  it('holds progress to 0..1 and drops an unreadable clock', () => {
    expect(readReplaySession(session({ progress: 1.7 }))!.progress).toBe(1)
    expect(readReplaySession(session({ progress: -0.2 }))!.progress).toBe(0)
    expect(readReplaySession(session({ clockUtc: 'soon' }))!.clockUtc).toBeNull()
  })

  it('says the AI Trader is along only when the API says so in as many words', () => {
    expect(readReplaySession(session())!.aiTrader).toBe(false)
    expect(readReplaySession(session({ aiTrader: true }))!.aiTrader).toBe(true)
    expect(readReplaySession(session({ aiTrader: 'true' }))!.aiTrader).toBe(false)
  })

  it('needs a date', () => {
    expect(readReplaySession({ state: 'playing' })).toBeNull()
    expect(readReplaySession(null)).toBeNull()
  })
})

describe('readReplayLogs', () => {
  it('reads the lines, as text only', () => {
    expect(readReplayLogs({ lines: ['a', 2, 'b', null] })).toEqual(['a', 'b'])
    expect(readReplayLogs(['x'])).toEqual(['x'])
    expect(readReplayLogs({ lines: 'a' })).toEqual([])
    expect(readReplayLogs(null)).toEqual([])
  })
})

describe('states', () => {
  it('knows which states hold the desk and which are over', () => {
    for (const s of ['starting', 'waiting', 'playing', 'paused']) {
      expect(isActiveState(s)).toBe(true)
      expect(isEndedState(s)).toBe(false)
    }
    for (const s of ['finished', 'stopped', 'failed']) {
      expect(isActiveState(s)).toBe(false)
      expect(isEndedState(s)).toBe(true)
    }
    expect(isActiveState(undefined)).toBe(false)
    expect(isActiveState('rewinding')).toBe(false)
  })

  it('reads the status every 2 s while a replay runs, every 15 s otherwise', () => {
    const playing = readReplayStatus({ canStart: false, session: session() })
    const finished = readReplayStatus({ canStart: true, session: session({ state: 'finished' }) })
    expect(replayPollMs(playing)).toBe(REPLAY_POLL_ACTIVE_MS)
    expect(REPLAY_POLL_ACTIVE_MS).toBe(2_000)
    expect(replayPollMs(finished)).toBe(REPLAY_POLL_IDLE_MS)
    expect(REPLAY_POLL_IDLE_MS).toBe(15_000)
    expect(replayPollMs(undefined)).toBe(REPLAY_POLL_IDLE_MS)
  })

  it('names every state, and one it does not know by its own name', () => {
    expect(replayStateLabel('playing')).toEqual({ label: 'Playing', tone: 'live' })
    expect(replayStateLabel('waiting').label).toBe('Waiting for runners')
    expect(replayStateLabel('failed').tone).toBe('neg')
    expect(replayStateLabel('rewinding')).toEqual({ label: 'Rewinding', tone: 'neutral' })
    expect(replayStateLabel('')).toEqual({ label: 'Unknown', tone: 'neutral' })
  })
})

describe('the replay clock', () => {
  it('reads the clock in IST', () => {
    expect(istHms(CLOCK_UTC)).toBe('11:42:07')
    expect(istHms(null)).toBe('')
    expect(istHms('later')).toBe('')
  })

  it('prefers the UTC instant, and falls back to the IST text', () => {
    expect(replayClock({ clockUtc: CLOCK_UTC, clockIst: '01:02:03' })).toBe('11:42:07')
    expect(replayClock({ clockUtc: null, clockIst: '11:42:07' })).toBe('11:42:07')
    expect(replayClock({ clockUtc: null, clockIst: '2026-09-30T9:05' })).toBe('09:05:00')
    expect(replayClock({ clockUtc: null, clockIst: null })).toBe('')
  })

  it('writes the line over the bar', () => {
    const s = readReplaySession(session())!
    expect(clockText(s)).toBe('Replaying 30 Sep · 11:42:07')
    expect(clockText({ ...s, state: 'paused' })).toBe('Paused 30 Sep · 11:42:07')
    expect(clockText({ ...s, state: 'finished' })).toBe('Finished 30 Sep · 11:42:07')
    expect(clockText({ ...s, state: 'stopped', clockUtc: null, clockIst: null })).toBe('Stopped 30 Sep')
    // Nothing plays yet: no clock.
    expect(clockText({ ...s, state: 'waiting' })).toBe('Waiting for the runners · 30 Sep')
    expect(clockText({ ...s, state: 'starting', date: '2026-09-09' })).toBe('Starting the replay of 09 Sep')
    expect(clockText({ ...s, state: 'rewinding' })).toBe('Rewinding 30 Sep · 11:42:07')
  })
})

describe('progress', () => {
  it("uses the API's progress, else works it out from the clock across 09:15–15:30", () => {
    expect(replayProgress({ progress: 0.25, clockUtc: CLOCK_UTC, clockIst: null })).toBe(0.25)
    // 12:22:30 IST is half way through the 375 minutes.
    expect(replayProgress({ progress: null, clockUtc: '2026-09-30T06:52:30Z', clockIst: null })).toBe(0.5)
    expect(replayProgress({ progress: null, clockUtc: null, clockIst: '09:00:00' })).toBe(0)
    expect(replayProgress({ progress: null, clockUtc: null, clockIst: '15:40:00' })).toBe(1)
    expect(replayProgress({ progress: null, clockUtc: null, clockIst: null })).toBeNull()
  })

  it('writes whole percent, rounded down, so 100% means played out', () => {
    expect(progressText(0.4237)).toBe('42%')
    expect(progressText(0.29)).toBe('29%')
    expect(progressText(0.999)).toBe('99%')
    expect(progressText(1)).toBe('100%')
    expect(progressText(0)).toBe('0%')
    expect(progressText(1.4)).toBe('100%')
    expect(progressText(null)).toBe('—')
    expect(progressText(Number.NaN)).toBe('—')
  })
})

describe('minutes recorded', () => {
  it('warns quietly below 360 of 375', () => {
    expect(minutesTone(375)).toBe('ok')
    expect(minutesTone(360)).toBe('ok')
    expect(minutesTone(359)).toBe('warn')
    expect(minutesTone(343)).toBe('warn')
    expect(minutesTone(0)).toBe('none')
    expect(minutesTone(null)).toBe('none')
  })

  it('shows the count, and a dash when not known', () => {
    expect(minutesText(375)).toBe('375')
    expect(minutesText(0)).toBe('0')
    expect(minutesText(null)).toBe('—')
  })

  it("notes a run's underlying when the day is short of it", () => {
    const day: ReplayDay = {
      date: '2026-09-30',
      weekday: 'Wed',
      minutes: { NIFTY: 375, BANKNIFTY: 343, SENSEX: 0, INDIAVIX: null },
      sizeBytes: null,
    }
    expect(coverageNote(day, 'NIFTY')).toBeNull()
    expect(coverageNote(day, 'BANKNIFTY')).toBe('30 Sep holds 343 of 375 BANKNIFTY minutes.')
    expect(coverageNote(day, 'SENSEX')).toBe('30 Sep has no recorded SENSEX minutes: the run will see no ticks.')
    expect(coverageNote({ ...day, minutes: { ...day.minutes, NIFTY: null } }, 'NIFTY')).toBe(
      'How many NIFTY minutes 30 Sep holds is not known.',
    )
    expect(coverageNote(null, 'NIFTY')).toBeNull()
  })
})

describe('speed', () => {
  it('labels the speeds', () => {
    expect(speedLabel(1)).toBe('1×')
    expect(speedLabel(10)).toBe('10×')
    expect(speedLabel(null)).toBe('—')
  })

  it('says what a faster replay costs', () => {
    expect(speedNote(1)).toBe('Faithful: the day plays at its recorded pace.')
    expect(speedNote(5)).toBe('At 5× a fill can be priced a little after the tick that triggered it.')
  })
})

describe('parseFromTime', () => {
  it('reads what was typed as HH:MM', () => {
    expect(parseFromTime('09:15')).toEqual({ value: '09:15', error: null })
    expect(parseFromTime(' 9:30 ')).toEqual({ value: '09:30', error: null })
    expect(parseFromTime('0930').value).toBe('09:30')
    expect(parseFromTime('930').value).toBe('09:30')
    expect(parseFromTime('15:00').value).toBe('15:00')
  })

  it('refuses a time outside 09:15–15:00, and what is not a time', () => {
    expect(parseFromTime('09:14').error).toBe('Start time must be between 09:15 and 15:00 IST.')
    expect(parseFromTime('15:01').error).toBe('Start time must be between 09:15 and 15:00 IST.')
    for (const bad of ['', 'abc', '25:00', '9:75', '9.30']) {
      expect(parseFromTime(bad)).toEqual({ value: null, error: 'Start time must be a time, like 09:15.' })
    }
  })
})

describe('the set-up', () => {
  it('replays only what a strategy trades of NIFTY, BANKNIFTY and SENSEX', () => {
    expect(replayUnderlyingsFor([])).toEqual(['NIFTY', 'BANKNIFTY', 'SENSEX'])
    expect(replayUnderlyingsFor(['sensex', 'nifty', 'CRUDEOIL'])).toEqual(['NIFTY', 'SENSEX'])
    expect(replayUnderlyingsFor(['CRUDEOIL'])).toEqual([])
  })

  const ghost: ReplayStrategyInfo = {
    id: 1,
    name: 'GhostNifty',
    underlyings: ['NIFTY', 'BANKNIFTY'],
    takenFor: (owner) => new Set(owner === 9 ? ['NIFTY'] : []),
  }
  const strategies = new Map([[1, ghost]])
  const draft = (over: Partial<ReplayRunDraft> = {}): ReplayRunDraft => ({
    key: 1,
    strategyId: 1,
    underlying: 'NIFTY',
    lots: '2',
    ownerUserId: null,
    ...over,
  })

  it('passes a complete set-up', () => {
    expect(setupError('2026-09-30', '09:15', [draft()], strategies)).toBeNull()
    expect(setupError('2026-09-30', '11:00', [draft(), draft({ key: 2, underlying: 'BANKNIFTY' })], strategies)).toBeNull()
  })

  it('says what is missing, in the order the form reads', () => {
    expect(setupError(null, '09:15', [draft()], strategies)).toBe('Pick a recorded day first.')
    expect(setupError('2026-09-30', '16:00', [draft()], strategies)).toBe('Start time must be between 09:15 and 15:00 IST.')
    expect(setupError('2026-09-30', '09:15', [], strategies)).toBe('Add at least one strategy run, or ask the AI Trader along.')
    expect(setupError('2026-09-30', '09:15', [draft({ strategyId: null })], strategies)).toBe('Run: pick a strategy.')
    expect(setupError('2026-09-30', '09:15', [draft({ underlying: 'CRUDEOIL' })], strategies)).toBe('Run: pick NIFTY, BANKNIFTY or SENSEX.')
    expect(setupError('2026-09-30', '09:15', [draft({ underlying: 'SENSEX' })], strategies)).toBe('Run: GhostNifty does not trade SENSEX.')
    expect(setupError('2026-09-30', '09:15', [draft({ lots: '1.5' })], strategies)).toBe(
      'Run: lots must be a whole number of at least 1.',
    )
    expect(setupError('2026-09-30', '09:15', [draft({ lots: '0' })], strategies)).toBe(
      'Run: lots must be a whole number of at least 1.',
    )
  })

  it('numbers the run when there are several, and refuses a doubled run', () => {
    expect(setupError('2026-09-30', '09:15', [draft(), draft({ key: 2, strategyId: null })], strategies)).toBe('Run 2: pick a strategy.')
    expect(setupError('2026-09-30', '09:15', [draft(), draft({ key: 2 })], strategies)).toBe(
      'Run 2: GhostNifty on NIFTY is in the list twice for the same account.',
    )
    // The same run in another account is another book.
    expect(setupError('2026-09-30', '09:15', [draft(), draft({ key: 2, ownerUserId: 5 })], strategies)).toBeNull()
  })

  it('refuses a run the API would refuse: the strategy already running there in that account', () => {
    expect(setupError('2026-09-30', '09:15', [draft({ ownerUserId: 9 })], strategies)).toBe(
      'Run: GhostNifty is already running on NIFTY in that account. Stop it first, or pick another account.',
    )
  })

  it('needs no run with the AI Trader along, and says how to drop a blank one', () => {
    expect(setupError('2026-09-30', '09:15', [], strategies, true)).toBeNull()
    expect(setupError('2026-09-30', '09:15', [draft()], strategies, true)).toBeNull()
    expect(setupError('2026-09-30', '09:15', [draft({ strategyId: null })], strategies, true)).toBe(
      'Run: pick a strategy, or remove the run to replay with the AI Trader alone.',
    )
    // The day and the time still come first, and a run is still checked in full.
    expect(setupError(null, '09:15', [], strategies, true)).toBe('Pick a recorded day first.')
    expect(setupError('2026-09-30', '09:15', [draft({ lots: '0' })], strategies, true)).toBe('Run: lots must be a whole number of at least 1.')
  })

  it('says what Start does, with runs, with the AI Trader along, and with it alone', () => {
    expect(setupSummary({ runs: 1, date: '2026-09-30', from: '09:15', speed: 1, aiTrader: false })).toBe(
      'Starts the run as a recap of 30 Sep, then plays the day from 09:15 at 1×. Replay runs are tests: they stay out of every live total and are squared off when the replay ends.',
    )
    expect(setupSummary({ runs: 2, date: '2026-09-30', from: '10:00', speed: 5, aiTrader: true })).toBe(
      'Starts the 2 runs as recaps of 30 Sep, then plays the day from 10:00 at 5×, with the AI Trader deciding along in shadow. Replay runs are tests: they stay out of every live total and are squared off when the replay ends.',
    )
    expect(setupSummary({ runs: 0, date: '2026-09-30', from: '09:15', speed: 2, aiTrader: true })).toBe(
      'Plays 30 Sep from 09:15 at 2× with only the AI Trader deciding along, in shadow: it places nothing, and nothing enters a live total.',
    )
  })

  it('starts each run as a recap of the day, naming the account only when it is not the caller', () => {
    expect(recapStartBody(draft(), '2026-09-30', 1)).toEqual({
      underlying: 'NIFTY',
      lots: 2,
      parameters: { session: 'recap', recap_date: '2026-09-30' },
    })
    expect(recapStartBody(draft({ ownerUserId: 1 }), '2026-09-30', 1)).not.toHaveProperty('ownerUserId')
    expect(recapStartBody(draft({ ownerUserId: 5 }), '2026-09-30', 1).ownerUserId).toBe(5)
  })

  it('adds the runs up only when every one is known', () => {
    const runs = readReplaySession(session())!.runs
    expect(replayNetTotal(runs)).toBeCloseTo(610.5)
    expect(replayNetTotal([...runs, { ...runs[0], runId: 9, netPnl: null }])).toBeNull()
    expect(replayNetTotal([])).toBeNull()
  })
})

describe('launchReplay', () => {
  const plan = (label: string, strategyId: number): ReplayRunPlan => ({
    label,
    strategyId,
    body: { underlying: 'NIFTY', lots: 1, parameters: { session: 'recap', recap_date: '2026-09-30' } },
  })
  const replay = { date: '2026-09-30', speed: 5, from: '10:00' }

  it('starts every run, then the replay with their ids', async () => {
    const calls: string[] = []
    const startRun = vi.fn(async (p: ReplayRunPlan) => {
      calls.push(`run ${p.label}`)
      return { runId: p.strategyId * 100, message: 'ok' }
    })
    const startReplay = vi.fn(async (body: unknown) => {
      calls.push('replay')
      return { ...session({ state: 'starting' }), runIds: (body as { runIds: number[] }).runIds }
    })
    const steps: string[] = []
    const out = await launchReplay([plan('A', 1), plan('B', 2)], replay, { startRun, startReplay, onStep: (s) => steps.push(s) })
    expect(calls).toEqual(['run A', 'run B', 'replay'])
    expect(startReplay).toHaveBeenCalledWith({ date: '2026-09-30', speed: 5, from: '10:00', runIds: [100, 200] })
    expect(out.ok).toBe(true)
    if (out.ok) {
      expect(out.runIds).toEqual([100, 200])
      expect(out.session?.state).toBe('starting')
    }
    expect(steps).toEqual(['Starting A (1 of 2)…', 'Starting B (2 of 2)…', 'Starting the replay…'])
  })

  it('stops at the first run that does not start, and never asks for the replay', async () => {
    const startRun = vi.fn(async (p: ReplayRunPlan) => {
      if (p.label === 'B') throw new Error('GhostNifty is already running on NIFTY in admin\'s account.\n   at Strategy.Start')
      return { runId: p.strategyId * 100 }
    })
    const startReplay = vi.fn()
    const out = await launchReplay([plan('A', 1), plan('B', 2), plan('C', 3)], replay, { startRun, startReplay })
    expect(startReplay).not.toHaveBeenCalled()
    expect(startRun).toHaveBeenCalledTimes(2)
    expect(out).toEqual({
      ok: false,
      stage: 'run',
      failed: { label: 'B', error: "GhostNifty is already running on NIFTY in admin's account." },
      started: [{ label: 'A', runId: 100 }],
      notTried: ['C'],
    })
  })

  it('counts an answer without a run id as a run that did not start', async () => {
    const out = await launchReplay([plan('A', 1)], replay, { startRun: async () => ({ message: 'ok' }), startReplay: vi.fn() })
    expect(out).toMatchObject({ ok: false, stage: 'run', failed: { label: 'A', error: 'The API did not answer with a run id.' }, started: [] })
  })

  it('asks for the replay at once, with no runs, when the AI Trader decides alone', async () => {
    const startRun = vi.fn()
    const startReplay = vi.fn(async () => session({ state: 'starting', runIds: [], runs: [], aiTrader: true }))
    const out = await launchReplay([], { ...replay, aiTrader: true }, { startRun, startReplay })
    expect(startRun).not.toHaveBeenCalled()
    expect(startReplay).toHaveBeenCalledWith({ date: '2026-09-30', speed: 5, from: '10:00', aiTrader: true, runIds: [] })
    expect(out.ok).toBe(true)
    if (out.ok) expect(out.session?.aiTrader).toBe(true)
  })

  it('reports a refused replay with the runs that are waiting', async () => {
    const out = await launchReplay([plan('A', 1)], replay, {
      startRun: async () => ({ runId: 412 }),
      startReplay: async () => {
        throw new Error('NSE is open: a replay starts only while the exchange is closed.')
      },
    })
    expect(out).toEqual({
      ok: false,
      stage: 'replay',
      error: 'NSE is open: a replay starts only while the exchange is closed.',
      started: [{ label: 'A', runId: 412 }],
    })
  })
})

describe('small readers', () => {
  it('reads a run id from a start answer', () => {
    expect(startedRunId({ runId: 412 })).toBe(412)
    expect(startedRunId({ runId: 0 })).toBeNull()
    expect(startedRunId({ runId: '412' })).toBeNull()
    expect(startedRunId(null)).toBeNull()
  })

  it("keeps an error's first line", () => {
    expect(errorLine(new Error('Refused.\n  at x'))).toBe('Refused.')
    expect(errorLine('plain')).toBe('plain')
    expect(errorLine(undefined)).toBe('Something went wrong.')
    expect(errorLine(new Error(''))).toBe('Something went wrong.')
  })
})
