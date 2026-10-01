import { describe, expect, it } from 'vitest'

import {
  SENTINEL_STALE_MINUTES,
  agoText,
  attentionKind,
  attentionLevel,
  consoleLink,
  decisionStatus,
  examText,
  istWhen,
  longDate,
  marketText,
  mostSeriousFirst,
  plural,
  readToday,
  recapText,
  runStatus,
  sentinelState,
  worstFirst,
} from './today'
import type { AttentionItem, TodayRun } from './today'

/** 11:00 IST on 1 Oct 2026. */
const NOW = Date.parse('2026-10-01T05:30:00Z')

/** GET /api/Today exactly as the contract (private/ai-workspace/TODAY-CONTRACT.md) sends it. */
const body = {
  date: '2026-10-01',
  nowUtc: '2026-10-01T05:30:00Z',
  markets: [
    { exchange: 'NSE', state: 'open', opensUtc: '2026-10-01T03:45:00Z', closesUtc: '2026-10-01T10:00:00Z' },
    { exchange: 'MCX', state: 'open', opensUtc: '2026-10-01T03:30:00Z', closesUtc: '2026-10-01T18:00:00Z' },
  ],
  attention: [
    {
      level: 'critical',
      kind: 'trading',
      title: 'admin is down ₹1,03,383 today',
      detail: 'Fulcrum BANKNIFTY −₹86,804, charges ₹45,662',
      link: '/trade/runs/362',
      atUtc: '2026-10-01T05:20:00Z',
    },
    {
      level: 'high',
      kind: 'incident',
      title: 'Dhan feed stalled twice',
      detail: null,
      link: '/system/incidents?id=206',
      atUtc: '2026-10-01T04:58:00Z',
    },
    {
      level: 'info',
      kind: 'learning',
      title: 'One memory dropped',
      detail: 'did not fix the question it came from',
      link: '/ai/memory',
      atUtc: null,
    },
  ],
  trading: {
    accounts: [
      { userName: 'admin', net: -109156.0, gross: -48986.0, charges: 70006.0, runsLive: 10, runsStopped: 3, openLegs: 6 },
      { userName: 'coderforchange', net: 2551.0, gross: 4120.0, charges: 1569.0, runsLive: 4, runsStopped: 0, openLegs: 2 },
    ],
    runs: [
      {
        runId: 362,
        userName: 'admin',
        strategy: 'Fulcrum',
        underlying: 'BANKNIFTY',
        status: 'Stopped',
        net: -86804.0,
        trades: 266,
        startedUtc: '2026-10-01T03:46:00Z',
        stoppedUtc: '2026-10-01T05:10:00Z',
        stopReason: 'Stop loss hit: P&L −₹86,804 ≤ −₹80,000',
      },
      {
        runId: 371,
        userName: 'coderforchange',
        strategy: 'GhostNifty',
        underlying: 'NIFTY',
        status: 'Running',
        net: 2551.0,
        trades: 12,
        startedUtc: '2026-10-01T03:46:00Z',
        stoppedUtc: null,
        stopReason: null,
      },
    ],
    recapsToday: 0,
  },
  agents: [
    {
      key: 'trade-reviewer',
      name: 'Trade Reviewer',
      on: true,
      callsToday: 5,
      failedCallsToday: 0,
      reportsToday: { ok: 3, invalid: 0, failed: 0 },
      lastActivityUtc: '2026-10-01T05:26:00Z',
      highlights: [{ text: 'Run 299: followed the spec; one runner held to 15:30', link: '/ai/reports?id=43' }],
    },
    {
      key: 'news-analyst',
      name: 'News Analyst',
      on: false,
      callsToday: 0,
      failedCallsToday: 0,
      reportsToday: { ok: 0, invalid: 0, failed: 0 },
      lastActivityUtc: null,
      highlights: [],
    },
  ],
  learning: {
    activeMemories: 3,
    learnedToday: [{ id: 15, agentName: 'Desk Assistant', text: 'Sort the worst run by net after charges.', how: 'verified by the check' }],
    droppedToday: [{ id: 16, agentName: 'Desk Assistant', text: 'Quote gross P&L first.', why: 'did not fix the question it came from' }],
    checkToday: { passed: 11, total: 12 },
    checkDays: [
      { date: '2026-09-29', passed: 9, total: 12 },
      { date: '2026-09-30', passed: 11, total: 12 },
    ],
    latestExam: { reportId: 812, date: '2026-10-04', repeats: 3, questions: 150, practicePassK: 0.8654, holdoutPassK: 0.8 },
  },
  system: {
    sentinelLastUtc: '2026-10-01T05:28:00Z',
    checkup: { verdict: 'action', headline: 'Dhan token expires at 08:00 tomorrow', utc: '2026-10-01T03:25:00Z' },
    deploy: { commit: '83cfd22', utc: '2026-09-30T18:40:00Z', summary: 'engine changed: risk guard on net' },
    openIncidents: 4,
  },
  decisions: [
    {
      date: '2026-10-01',
      title: 'AI Trader setup',
      decided: "₹5 lakh paper account 'ai-trader'; index options buying only",
      by: 'owner',
      status: 'decided',
    },
    { date: '2026-09-30', title: 'Gross daily-loss limit', decided: 'Not set yet', by: 'Claude (default)', status: 'open' },
  ],
}

describe('reading the day', () => {
  it('reads the body as the contract sends it', () => {
    const d = readToday(body)
    expect(d.date).toBe('2026-10-01')
    expect(d.nowUtc).toBe('2026-10-01T05:30:00Z')
    expect(d.markets).toEqual(body.markets)
    expect(d.attention).toEqual(body.attention)
    expect(d.trading).toEqual({ ...body.trading, runs: body.trading.runs })
    expect(d.agents).toEqual(body.agents)
    expect(d.learning).toEqual(body.learning)
    expect(d.system).toEqual(body.system)
    expect(d.decisions).toEqual(body.decisions)
  })

  it('refuses a body that is not the day, rather than drawing seven empty sections', () => {
    expect(() => readToday('<!doctype html>')).toThrow(/shape this page cannot read/)
    expect(() => readToday(null)).toThrow(/shape this page cannot read/)
    expect(() => readToday([body])).toThrow(/shape this page cannot read/)
    expect(() => readToday({ date: '2026-10-01' })).toThrow(/shape this page cannot read/)
  })

  it('marks a section the body left out as not known, never as empty', () => {
    const { attention: _a, learning: _l, ...rest } = body
    const d = readToday({ ...rest, system: 'oops', agents: { not: 'a list' } })
    expect(d.attention).toBeNull()
    expect(d.learning).toBeNull()
    expect(d.system).toBeNull()
    expect(d.agents).toBeNull()
    // The rest still reads.
    expect(d.trading?.accounts).toHaveLength(2)
    expect(d.decisions).toHaveLength(2)
  })

  it('keeps an empty attention list empty: that is "nothing needs you"', () => {
    expect(readToday({ ...body, attention: [] }).attention).toEqual([])
  })

  it('puts the most serious first and the worst run first, whatever order they came in', () => {
    const d = readToday({
      ...body,
      attention: [body.attention[2], body.attention[1], { ...body.attention[0], level: 'medium' }, body.attention[0]],
      trading: { ...body.trading, runs: [body.trading.runs[1], body.trading.runs[0]] },
    })
    expect(d.attention!.map((a) => a.level)).toEqual(['critical', 'high', 'medium', 'info'])
    expect(d.trading!.runs.map((r) => r.runId)).toEqual([362, 371])
  })

  it('keeps unknown money as unknown, not as ₹0', () => {
    const d = readToday({
      ...body,
      trading: {
        accounts: [{ userName: 'admin', net: null, gross: 'lots', runsLive: 2 }],
        runs: [{ runId: 5, net: null }],
        recapsToday: 2,
      },
    })
    expect(d.trading!.accounts[0]).toEqual({
      userName: 'admin',
      net: null,
      gross: null,
      charges: null,
      runsLive: 2,
      runsStopped: 0,
      openLegs: 0,
    })
    expect(d.trading!.runs[0]).toMatchObject({ runId: 5, strategy: 'Run 5', net: null, trades: 0, stopReason: null })
    expect(d.trading!.recapsToday).toBe(2)
  })

  it('drops rows it cannot show and keeps words it does not know', () => {
    const d = readToday({
      ...body,
      markets: [{ exchange: 'NSE', state: 'Halted', opensUtc: 'not a time' }, { state: 'open' }],
      attention: [
        { level: 'Urgent', kind: 'Weather', title: '  Rain  ', link: 'javascript:alert(1)' },
        { level: 'high', title: '' },
        'junk',
      ],
      trading: { accounts: [{ net: 5 }], runs: [{ strategy: 'no id' }] },
      agents: [
        { name: 'no key' },
        {
          key: 'desk-assistant',
          highlights: [{ text: '' }, { text: 'a', link: 'https://example.com' }, ...'bcdef'.split('').map((t) => ({ text: t }))],
        },
      ],
      decisions: [
        { date: '2026-10-01', title: '' },
        { title: 'Keep', status: 'Decided' },
      ],
    })
    expect(d.markets).toEqual([{ exchange: 'NSE', state: 'halted', opensUtc: null, closesUtc: null }])
    expect(d.attention).toEqual([{ level: 'urgent', kind: 'weather', title: 'Rain', detail: null, link: null, atUtc: null }])
    expect(d.trading).toEqual({ accounts: [], runs: [], recapsToday: 0 })
    expect(d.agents).toHaveLength(1)
    const agent = d.agents![0]
    expect(agent).toMatchObject({
      key: 'desk-assistant',
      name: 'desk-assistant',
      on: null,
      callsToday: 0,
      reportsToday: { ok: 0, invalid: 0, failed: 0 },
    })
    // At most four highlights, and only console routes become links.
    expect(agent.highlights).toEqual([
      { text: 'a', link: null },
      { text: 'b', link: null },
      { text: 'c', link: null },
      { text: 'd', link: null },
    ])
    expect(d.decisions).toEqual([{ date: '', title: 'Keep', decided: '', by: '', status: 'decided' }])
  })

  it('keeps the last seven days with a check, oldest first, and drops a day it cannot read', () => {
    const days = ['2026-09-21', '2026-09-30', '2026-09-22', '2026-09-23', '2026-09-24', '2026-09-25', '2026-09-26', '2026-09-29'].map(
      (date) => ({
        date,
        passed: 10,
        total: 12,
      }),
    )
    const d = readToday({
      ...body,
      learning: {
        ...body.learning,
        checkDays: [...days, { date: 'yesterday', passed: 1, total: 2 }, { date: '2026-09-28', passed: 1 }],
        checkToday: { passed: 3 },
      },
    })
    expect(d.learning!.checkDays.map((x) => x.date)).toEqual([
      '2026-09-22',
      '2026-09-23',
      '2026-09-24',
      '2026-09-25',
      '2026-09-26',
      '2026-09-29',
      '2026-09-30',
    ])
    expect(d.learning!.checkToday).toBeNull()
  })

  it('reads the learning lists without a row it cannot name', () => {
    const d = readToday({
      ...body,
      learning: {
        activeMemories: 'three',
        learnedToday: [
          { id: 'x', text: 'no id' },
          { id: 17, text: '' },
          { id: 18, text: 'kept' },
        ],
        droppedToday: null,
      },
    })
    expect(d.learning).toEqual({
      activeMemories: null,
      learnedToday: [{ id: 18, agentName: '', text: 'kept', how: '' }],
      droppedToday: [],
      checkToday: null,
      checkDays: [],
      latestExam: null,
    })
  })

  it('says the weekly exam in one line, a dash for a set with nothing scored', () => {
    const d = readToday({
      ...body,
      learning: { ...body.learning, latestExam: { reportId: 9, date: 'soon', repeats: 3, questions: 40, practicePassK: 0.5, holdoutPassK: 7 } },
    })
    expect(d.learning!.latestExam).toEqual({ reportId: 9, date: null, repeats: 3, questions: 40, practicePassK: 0.5, holdoutPassK: null })
    expect(examText(d.learning!.latestExam!)).toBe('pass^3: 50% practice, — held out · 40 questions')
  })

  it('reads a system with nothing reported yet', () => {
    const d = readToday({ ...body, system: { sentinelLastUtc: null, checkup: null, deploy: null } })
    expect(d.system).toEqual({ sentinelLastUtc: null, checkup: null, deploy: null, openIncidents: null })
  })
})

describe('links', () => {
  it('lets only a console route become a link', () => {
    expect(consoleLink('/system/incidents?id=206')).toBe('/system/incidents?id=206')
    expect(consoleLink(' /ai/memory#memory-15 ')).toBe('/ai/memory#memory-15')
    for (const bad of ['https://evil.example', '//evil.example', '/\\evil.example', 'javascript:alert(1)', 'ai/memory', '', null, 42]) {
      expect(consoleLink(bad)).toBeNull()
    }
  })
})

describe('the words', () => {
  it('names each attention level with a word and an incident tone, and puts an unknown one last', () => {
    expect(attentionLevel('critical')).toEqual({ key: 'critical', label: 'Critical', tone: 'neg', rank: 0 })
    expect(attentionLevel('high')).toMatchObject({ label: 'High', tone: 'neg' })
    expect(attentionLevel('medium')).toMatchObject({ label: 'Medium', tone: 'warn' })
    expect(attentionLevel('info')).toMatchObject({ label: 'Info', tone: 'neutral' })
    expect(attentionLevel('urgent')).toEqual({ key: null, label: 'Urgent', tone: 'neutral', rank: 4 })
    expect(attentionKind('check')).toBe('Check')
    expect(attentionKind('weather')).toBe('Weather')
    expect(attentionKind('')).toBe('')
  })

  it('sorts stably within a level', () => {
    const item = (level: string, title: string): AttentionItem => ({ level, kind: '', title, detail: null, link: null, atUtc: null })
    const sorted = mostSeriousFirst([item('info', 'a'), item('high', 'b'), item('odd', 'c'), item('high', 'd'), item('critical', 'e')])
    expect(sorted.map((x) => x.title)).toEqual(['e', 'b', 'd', 'a', 'c'])
  })

  it('puts a run without a net after every run with one', () => {
    const run = (runId: number, net: number | null) => ({ runId, net }) as TodayRun
    expect(worstFirst([run(1, null), run(2, 50), run(3, -10), run(4, null), run(5, -10)]).map((r) => r.runId)).toEqual([3, 5, 2, 1, 4])
  })

  it("says a market's state and its next time in IST", () => {
    const [nse, mcx] = readToday(body).markets!
    expect(marketText(nse, NOW)).toEqual({ text: 'NSE open · closes 15:30', tone: 'pos' })
    expect(marketText(mcx, NOW)).toEqual({ text: 'MCX open · closes 23:30', tone: 'pos' })
    expect(marketText({ exchange: 'NSE', state: 'pre-open', opensUtc: '2026-10-01T03:45:00Z', closesUtc: null }, NOW).text).toBe(
      'NSE pre-open · opens 09:15',
    )
    // Opening on another day names the day: Friday 2 Oct.
    expect(marketText({ exchange: 'NSE', state: 'closed', opensUtc: '2026-10-02T03:45:00Z', closesUtc: null }, NOW).text).toBe(
      'NSE closed · opens Fri 09:15',
    )
    expect(marketText({ exchange: 'NSE', state: 'holiday', opensUtc: null, closesUtc: null }, NOW)).toEqual({
      text: 'NSE holiday',
      tone: 'neutral',
    })
    expect(marketText({ exchange: 'NSE', state: 'halted', opensUtc: null, closesUtc: null }, NOW).text).toBe('NSE halted')
  })

  it('writes IST clock times, with the weekday on another day', () => {
    expect(istWhen('2026-10-01T05:20:00Z', NOW)).toBe('10:50')
    expect(istWhen('2026-09-30T18:40:00Z', NOW)).toBe('00:10')
    expect(istWhen('2026-09-30T10:00:00Z', NOW)).toBe('Wed 15:30')
    expect(istWhen(null, NOW)).toBe('')
  })

  it('writes how long ago, and never a time ahead of the clock', () => {
    expect(agoText('2026-10-01T05:29:30Z', NOW)).toBe('just now')
    expect(agoText('2026-10-01T05:31:00Z', NOW)).toBe('just now')
    expect(agoText('2026-10-01T05:26:00Z', NOW)).toBe('4 min ago')
    expect(agoText('2026-10-01T02:00:00Z', NOW)).toBe('3 h ago')
    expect(agoText('2026-09-30T05:00:00Z', NOW)).toBe('1 day ago')
    expect(agoText('2026-09-27T05:00:00Z', NOW)).toBe('4 days ago')
    expect(agoText(null, NOW)).toBe('')
  })

  it('flags Sentinel once its last round is more than ten minutes old, or when it never reported', () => {
    expect(SENTINEL_STALE_MINUTES).toBe(10)
    expect(sentinelState('2026-10-01T05:28:00Z', NOW)).toEqual({ text: 'last round 2 min ago', late: false })
    expect(sentinelState('2026-10-01T05:20:00Z', NOW).late).toBe(false)
    expect(sentinelState('2026-10-01T05:16:00Z', NOW)).toEqual({ text: 'last round 14 min ago; it may have stopped', late: true })
    expect(sentinelState(null, NOW)).toEqual({ text: 'no round reported yet', late: true })
  })

  it('names run and decision statuses in words', () => {
    expect(runStatus('Running')).toEqual({ label: 'Running', tone: 'live' })
    expect(runStatus('stopped')).toEqual({ label: 'Stopped', tone: 'warn' })
    expect(runStatus('Failed')).toEqual({ label: 'Failed', tone: 'neg' })
    expect(runStatus('Paused')).toEqual({ label: 'Paused', tone: 'neutral' })
    expect(decisionStatus('decided')).toMatchObject({ label: 'Decided', tone: 'pos' })
    expect(decisionStatus('default')).toMatchObject({ label: 'Default', tone: 'accent' })
    expect(decisionStatus('open')).toMatchObject({ label: 'Open', tone: 'warn' })
    expect(decisionStatus('parked')).toMatchObject({ label: 'Parked', tone: 'neutral' })
  })

  it('writes the date in full, and the recap and count lines', () => {
    expect(longDate('2026-10-01')).toBe('Thursday 1 October 2026')
    expect(longDate('2026-02-30')).toBe('2026-02-30')
    expect(longDate('today')).toBe('today')
    expect(recapText(0)).toBe('')
    expect(recapText(1)).toBe('1 recap run today (a test, not counted)')
    expect(recapText(3)).toBe('3 recap runs today (tests, not counted)')
    expect(plural(1, 'call')).toBe('1 call')
    expect(plural(5, 'call')).toBe('5 calls')
    expect(plural(2, 'open leg')).toBe('2 open legs')
  })
})
