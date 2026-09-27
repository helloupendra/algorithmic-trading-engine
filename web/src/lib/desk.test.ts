import { describe, expect, it } from 'vitest'

import type { Forecast } from './analysis'
import type { CheckupDetail, CheckupSummary } from './checkup'
import {
  NIFTY50,
  PIN_KEY,
  buildGrid,
  carriedRunIds,
  chainLevels,
  clockPhase,
  commodityRoot,
  compactInr,
  dayLabel,
  dayMonth,
  dayTrace,
  deskAccounts,
  deskDay,
  deskLayout,
  deskLegs,
  deskTimeline,
  figureTone,
  forecastCounts,
  forecastRows,
  indexRows,
  liveIncidents,
  modelStanding,
  morningCheckupId,
  newsLines,
  overnightRows,
  planFromItem,
  plannedFor,
  rangeSoFar,
  readPin,
  runCounts,
  runFigures,
  scopeRuns,
  shownPhase,
  signedNumber,
  stopKind,
  strategyLabel,
  stripSpans,
  tickerOf,
  todaysPlan,
  untilText,
  validScope,
  weekdayOf,
  weekRows,
  writePin,
} from './desk'
import type { DeskAccount } from './desk'
import { accessFor } from './modules'
import { liveNet } from './strategyList'
import type {
  DeployRecord,
  Incident,
  IntelAnnouncement,
  IntelBoardMeeting,
  IntelHeadline,
  LiveRunSummary,
  MarketPulseResponse,
  OptionChain,
  OptionChainPosition,
} from './types'

// Monday 28 Sep 2026 in IST, as UTC instants.
const ist = (hm: string, day = '2026-09-28') => Date.parse(`${day}T${hm}:00+05:30`)
const istIso = (hm: string, day = '2026-09-28') => new Date(ist(hm, day)).toISOString()
const TODAY = '2026-09-28'

let nextRun = 600
function run(over: Partial<LiveRunSummary> = {}): LiveRunSummary {
  nextRun++
  return {
    runId: nextRun,
    userId: 1,
    userName: 'admin',
    strategyId: 1,
    strategyName: 'GhostTangentCrossings',
    category: 'Directional',
    underlying: 'NIFTY',
    spotSymbol: 'NSE:NIFTY50-INDEX',
    lots: 2,
    role: null,
    lotSize: 65,
    risk: null,
    status: 'Running',
    isActive: true,
    startedUtc: istIso('08:45'),
    stoppedUtc: null,
    stopReason: null,
    stoppedBy: null,
    durationSeconds: null,
    netPnl: 0,
    grossPnl: 0,
    charges: 0,
    realizedPnl: 0,
    unrealizedPnl: 0,
    trades: 0,
    openPositions: 0,
    groups: 0,
    ...over,
  }
}

/** A stopped run with a realized result, as the history row reports it. */
function stopped(over: Partial<LiveRunSummary>, realized: number, charges: number, trades: number): LiveRunSummary {
  return run({
    status: 'Stopped',
    isActive: false,
    realizedPnl: realized,
    grossPnl: realized,
    charges,
    netPnl: realized - charges,
    unrealizedPnl: 0,
    trades,
    ...over,
  })
}

const ADMIN: DeskAccount = { id: 1, name: 'admin', tone: 1 }
const CFC: DeskAccount = { id: 2, name: 'coderforchange', tone: 2 }

// ---------------------------------------------------------------- the part of the day

describe('clockPhase', () => {
  const session = (open: string, close: string, isTradingDay = true) => ({
    isTradingDay,
    sessionOpenUtc: istIso(open),
    sessionCloseUtc: istIso(close),
  })

  it('reads the NSE session: before the open, the session, after the close', () => {
    const s = session('09:15', '15:30')
    expect(clockPhase(ist('08:58'), s)).toBe('pre')
    expect(clockPhase(ist('09:15'), s)).toBe('live')
    expect(clockPhase(ist('15:29'), s)).toBe('live')
    expect(clockPhase(ist('15:30'), s)).toBe('post')
    expect(clockPhase(ist('23:10'), s)).toBe('post')
  })

  it('follows special hours the API reports', () => {
    const muhurat = session('18:15', '19:15')
    expect(clockPhase(ist('17:00'), muhurat)).toBe('pre')
    expect(clockPhase(ist('18:30'), muhurat)).toBe('live')
  })

  it('shows the last close on a day without a session', () => {
    expect(clockPhase(ist('11:00'), session('09:15', '15:30', false))).toBe('post')
  })

  it('falls back to the standard hours until the session is known', () => {
    expect(clockPhase(ist('09:00'))).toBe('pre')
    expect(clockPhase(ist('11:42'))).toBe('live')
    expect(clockPhase(ist('16:07'))).toBe('post')
    // Sunday 27 Sep.
    expect(clockPhase(ist('11:00', '2026-09-27'))).toBe('post')
  })

  it("ignores session bounds that are not today's", () => {
    const stale = { isTradingDay: true, sessionOpenUtc: istIso('09:15', '2026-09-25'), sessionCloseUtc: istIso('15:30', '2026-09-25') }
    expect(clockPhase(ist('11:42'), stale)).toBe('live')
  })
})

describe('shownPhase', () => {
  it('follows the clock by default', () => {
    expect(shownPhase('live', null, null, TODAY)).toEqual({ phase: 'live', how: 'clock' })
  })

  it('keeps a picked phase until the clock moves, then reorders itself', () => {
    const pick = { phase: 'pre' as const, from: 'live' as const }
    expect(shownPhase('live', pick, null, TODAY)).toEqual({ phase: 'pre', how: 'picked' })
    expect(shownPhase('post', pick, null, TODAY)).toEqual({ phase: 'post', how: 'clock' })
  })

  it('reads a pick of the current phase as the clock', () => {
    expect(shownPhase('live', { phase: 'live', from: 'live' }, null, TODAY).how).toBe('clock')
  })

  it('holds a pin through the clock moving, for the day it was set only', () => {
    const pin = { phase: 'live' as const, date: TODAY }
    expect(shownPhase('post', null, pin, TODAY)).toEqual({ phase: 'live', how: 'pinned' })
    expect(shownPhase('pre', null, pin, '2026-09-29')).toEqual({ phase: 'pre', how: 'clock' })
  })
})

describe('the pin in storage', () => {
  const memory = () => {
    const m = new Map<string, string>()
    return {
      getItem: (k: string) => m.get(k) ?? null,
      setItem: (k: string, v: string) => void m.set(k, v),
      removeItem: (k: string) => void m.delete(k),
      raw: m,
    }
  }

  it('round-trips and clears', () => {
    const s = memory()
    writePin(s, { phase: 'post', date: TODAY })
    expect(readPin(s)).toEqual({ phase: 'post', date: TODAY })
    writePin(s, null)
    expect(readPin(s)).toBeNull()
  })

  it('reads garbage, an unknown phase or a bad date as no pin', () => {
    const s = memory()
    for (const bad of ['{', '"x"', JSON.stringify({ phase: 'lunch', date: TODAY }), JSON.stringify({ phase: 'pre', date: 'today' })]) {
      s.raw.set(PIN_KEY, bad)
      expect(readPin(s)).toBeNull()
    }
  })

  it('survives a storage that throws, or none at all', () => {
    const throwing = {
      getItem: () => {
        throw new Error('SecurityError')
      },
      setItem: () => {
        throw new Error('QuotaExceededError')
      },
      removeItem: () => {
        throw new Error('SecurityError')
      },
    }
    expect(readPin(throwing)).toBeNull()
    expect(() => writePin(throwing, { phase: 'pre', date: TODAY })).not.toThrow()
    expect(readPin(null)).toBeNull()
    expect(() => writePin(null, null)).not.toThrow()
  })
})

describe('deskDay', () => {
  const recent = [run({ startedUtc: istIso('08:45', '2026-09-24') }), run({ startedUtc: istIso('08:45', '2026-09-25') })]

  it('is today on a trading day, or while the answer is not in', () => {
    expect(deskDay(TODAY, true, 0, recent)).toBe(TODAY)
    expect(deskDay(TODAY, undefined, 0, recent)).toBe(TODAY)
  })

  it("opens a day without a session on the last day's runs", () => {
    expect(deskDay('2026-09-27', false, 0, recent)).toBe('2026-09-25')
    expect(deskDay('2026-09-27', false, 0, [])).toBe('2026-09-27')
  })

  it('stays on today when something ran today anyway (MCX on an NSE holiday)', () => {
    expect(deskDay('2026-10-02', false, 2, recent)).toBe('2026-10-02')
  })
})

describe('day labels', () => {
  it('spells the month the way a desk writes it', () => {
    expect(dayMonth('2026-09-29')).toBe('29 Sep')
    expect(dayLabel('2026-10-02')).toBe('Fri 2 Oct')
    expect(weekdayOf('2026-09-25')).toBe('Fri')
  })
})

describe('untilText', () => {
  it('counts down in minutes, then hours', () => {
    expect(untilText(ist('09:15'), ist('08:59'))).toBe('in 16 min')
    expect(untilText(ist('09:15'), ist('07:10'))).toBe('in 2 h 05 min')
    expect(untilText(ist('09:15'), ist('09:15'))).toBeNull()
  })
})

// ---------------------------------------------------------------- the layout

describe('deskLayout', () => {
  const admin = accessFor({ role: 'Admin' })
  const keys = (rows: ReturnType<typeof deskLayout>) => rows.map((r) => r.map((s) => s.key))
  const spans = (rows: ReturnType<typeof deskLayout>) => rows.map((r) => r.reduce((a, s) => a + s.span, 0))

  it("leads with each phase's job for an admin", () => {
    expect(keys(deskLayout('pre', admin))[0]).toEqual(['checkup', 'overnight', 'forecast'])
    expect(keys(deskLayout('live', admin))[0]).toEqual(['grid', 'pnl'])
    expect(keys(deskLayout('post', admin))[1]).toEqual(['scores', 'checkup', 'legs'])
  })

  it('keeps every row a full 12 columns', () => {
    for (const phase of ['pre', 'live', 'post'] as const) {
      for (const access of [admin, accessFor({ role: 'Trader', moduleGrants: ['strategies'] }), accessFor({ role: 'Trader', moduleGrants: [] })]) {
        for (const total of spans(deskLayout(phase, access))) expect(total).toBe(12)
      }
    }
  })

  it('never gives a trader Sentinel, the timeline, the news or the overnight table', () => {
    const all = accessFor({ role: 'Trader', moduleGrants: ['strategies', 'market-data', 'analysis', 'notebook', 'backtesting'] })
    for (const phase of ['pre', 'live', 'post'] as const) {
      const shown = deskLayout(phase, all).flat().flatMap((s) => [s.key, s.under])
      for (const hidden of ['checkup', 'timeline', 'news', 'overnight']) expect(shown).not.toContain(hidden)
    }
  })

  it('drops panels a trader lacks the grant for and widens the rest', () => {
    const rows = deskLayout('live', accessFor({ role: 'Trader', moduleGrants: ['market-data'] }))
    expect(keys(rows)).toEqual([['indices'], ['week', 'flows', 'movers']])
    expect(rows[0][0].span).toBe(12)
  })

  it('leaves a trader with no grants the index table alone', () => {
    expect(keys(deskLayout('live', accessFor({ role: 'Trader', moduleGrants: [] })))).toEqual([['indices']])
  })

  it('stacks a panel under another only when it may be shown', () => {
    const pre = deskLayout('pre', admin)
    expect(pre[0][2]).toEqual({ key: 'forecast', span: 4, under: 'flows' })
    expect(pre[2][1]).toEqual({ key: 'legs', span: 4, under: 'timeline' })
    const trader = deskLayout('pre', accessFor({ role: 'Trader', moduleGrants: ['strategies', 'analysis'] }))
    expect(trader.flat().some((s) => s.under)).toBe(false)
  })
})

// ---------------------------------------------------------------- accounts and figures

describe('deskAccounts', () => {
  it('orders accounts by user and colours the first two', () => {
    const accounts = deskAccounts([run({ userId: 3, userName: 'third' }), run({ userId: 2, userName: 'coderforchange' }), run({ userId: 1 })])
    expect(accounts).toEqual([
      { id: 1, name: 'admin', tone: 1 },
      { id: 2, name: 'coderforchange', tone: 2 },
      { id: 3, name: 'third', tone: null },
    ])
  })

  it('names a deleted user by id rather than dropping the runs', () => {
    expect(deskAccounts([run({ userId: 9, userName: null })])[0].name).toBe('user 9')
  })
})

describe('scope', () => {
  it('filters runs to one account', () => {
    const runs = [run({ userId: 1 }), run({ userId: 2 })]
    expect(scopeRuns(runs, 'all')).toHaveLength(2)
    expect(scopeRuns(runs, 2).map((r) => r.userId)).toEqual([2])
  })

  it('falls back to all accounts when the chosen one has no runs today', () => {
    expect(validScope(7, [ADMIN, CFC])).toBe('all')
    expect(validScope(2, [ADMIN, CFC])).toBe(2)
  })
})

describe('runFigures', () => {
  it('nets charges off realized plus the open book, as the run card does', () => {
    const r = run({ realizedPnl: 3980, grossPnl: 3980, charges: 11940, netPnl: -7960, unrealizedPnl: -260, trades: 142 })
    const f = runFigures(r)
    expect(f).toEqual({ net: -8220, gross: 3720, charges: 11940, open: -260, trades: 142 })
    // The same arithmetic as the Live runner's liveNet over the run's live view.
    expect(f.net).toBe(liveNet({ total: 3720, net: 3720 - 11940 }))
  })

  it('drops the open book once a run has stopped', () => {
    expect(runFigures(stopped({ unrealizedPnl: 500 }, 1000, 100, 3)).net).toBe(900)
  })

  it('shows the gross when an older API sends no charges', () => {
    const r = run({ realizedPnl: 1200, grossPnl: undefined, charges: undefined, netPnl: 1200, unrealizedPnl: 300 })
    expect(runFigures(r)).toMatchObject({ net: 1500, gross: 1500, charges: 0 })
  })
})

describe('stopKind', () => {
  it('tells the risk guard, the close, a fault and a person apart', () => {
    expect(stopKind(run())).toBeNull()
    expect(stopKind(stopped({ stoppedBy: 'risk-guard', stopReason: 'Stop loss hit: P&L −₹5,120 ≤ −₹5,000' }, 0, 0, 0))).toBe('rule')
    expect(stopKind(stopped({ stoppedBy: 'risk-guard', stopReason: 'Trailing stop hit: P&L ₹1,200 fell …' }, 0, 0, 0))).toBe('rule')
    expect(stopKind(stopped({ stoppedBy: 'risk-guard', stopReason: 'Target hit: P&L ₹8,100 ≥ ₹8,000' }, 0, 0, 0))).toBe('target')
    expect(stopKind(stopped({ stoppedBy: 'market-hours', stopReason: 'Market closed (15:30 IST)' }, 0, 0, 0))).toBe('close')
    expect(stopKind(stopped({ stoppedBy: null, stopReason: 'MCX closed (23:30 IST)' }, 0, 0, 0))).toBe('close')
    expect(stopKind(stopped({ stoppedBy: 'runner', stopReason: 'Runner exited (code 1)' }, 0, 0, 0))).toBe('fault')
    expect(stopKind(stopped({ stoppedBy: 'admin', stopReason: 'Stopped by admin' }, 0, 0, 0))).toBe('person')
  })
})

describe('strategyLabel', () => {
  it('splits the class name and keeps acronyms whole', () => {
    expect(strategyLabel('GhostTangentCrossings')).toBe('Ghost Tangent Crossings')
    expect(strategyLabel('SmcStructureBreak')).toBe('SMC Structure Break')
    expect(strategyLabel('Fulcrum')).toBe('Fulcrum')
    expect(strategyLabel('ATMStraddle')).toBe('ATM Straddle')
  })
})

// ---------------------------------------------------------------- the grid

describe('buildGrid', () => {
  const runs = [
    run({ userId: 1, strategyName: 'GhostTangentCrossings', underlying: 'NIFTY', realizedPnl: 1840, grossPnl: 1840, charges: 352, netPnl: 1488, unrealizedPnl: 620, trades: 4, openPositions: 1 }),
    run({ userId: 2, userName: 'coderforchange', strategyName: 'GhostTangentCrossings', underlying: 'NIFTY', realizedPnl: 1610, grossPnl: 1610, charges: 352, netPnl: 1258, unrealizedPnl: 640, trades: 4 }),
    run({ userId: 1, strategyName: 'GhostTangentCrossings', underlying: 'BANKNIFTY', startedUtc: istIso('08:45'), realizedPnl: -1230, grossPnl: -1230, charges: 268, netPnl: -1498, trades: 3 }),
    stopped({ userId: 1, strategyName: 'Fulcrum', underlying: 'SENSEX', startedUtc: istIso('08:46'), stoppedUtc: istIso('11:20'), stoppedBy: 'risk-guard', stopReason: 'Stop loss hit: P&L −₹5,120 ≤ −₹5,000' }, 2960, 8080, 96),
    run({ userId: 1, strategyName: 'CrudeMomentum', underlying: 'CRUDEOIL', startedUtc: istIso('08:47'), unrealizedPnl: 1400, charges: 60, grossPnl: 0, netPnl: -60, trades: 1, openPositions: 1 }),
    // coderforchange's SMC run died at 10:02 and was restarted at 10:04.
    stopped({ userId: 2, userName: 'coderforchange', strategyName: 'SmcStructureBreak', underlying: 'NIFTY', startedUtc: istIso('08:45'), stoppedUtc: istIso('10:02'), stoppedBy: 'runner', stopReason: 'Runner exited (code 1)' }, 0, 0, 0),
    run({ runId: 635, userId: 2, userName: 'coderforchange', strategyName: 'SmcStructureBreak', underlying: 'NIFTY', startedUtc: istIso('10:04'), realizedPnl: -470, grossPnl: -470, charges: 88, netPnl: -558, unrealizedPnl: 220, trades: 1 }),
    run({ userId: 1, strategyName: 'Watcher', role: 'alerts' }),
  ]
  const grid = buildGrid(runs, [ADMIN, CFC], new Set([runs[0].runId]))

  it('puts underlyings in index order and strategies in plan order', () => {
    expect(grid.underlyings).toEqual(['NIFTY', 'BANKNIFTY', 'SENSEX', 'CRUDEOIL'])
    expect(grid.rows.map((r) => r.label)).toEqual(['Ghost Tangent Crossings', 'SMC Structure Break', 'Fulcrum', 'Crude Momentum'])
  })

  it('leaves alert-only runs out', () => {
    expect(grid.rows.some((r) => r.strategy === 'Watcher')).toBe(false)
  })

  it('gives each cell one line per account, empty where that account has no run', () => {
    const ghost = grid.rows[0]
    expect(ghost.cells.NIFTY.map((c) => c?.figures.net)).toEqual([2108, 1898])
    expect(ghost.cells.BANKNIFTY.map((c) => c?.figures.net ?? null)).toEqual([-1498, null])
    expect(ghost.cells.SENSEX).toEqual([null, null])
  })

  it('sums a restart into its cell and marks it', () => {
    const smc = grid.rows[1].cells.NIFTY[1]!
    expect(smc.runs).toHaveLength(2)
    expect(smc.figures.net).toBe(-338)
    expect(smc.marks[0]).toMatchObject({ kind: 'restarted', label: '↻' })
    expect(smc.marks[0].title).toContain('10:04')
    expect(smc.marks.some((m) => m.kind === 'stopped')).toBe(false)
  })

  it('marks a risk-rule stop with its time and a carried leg', () => {
    expect(grid.rows[2].cells.SENSEX[0]!.marks).toEqual([
      { kind: 'rule', label: 'SL', at: '11:20', title: 'Stop loss hit: P&L −₹5,120 ≤ −₹5,000' },
    ])
    expect(grid.rows[0].cells.NIFTY[0]!.marks.map((m) => m.kind)).toEqual(['carried'])
  })

  it('marks a run that stopped by fault and was not restarted', () => {
    const g = buildGrid([stopped({ stoppedUtc: istIso('12:10'), stoppedBy: 'api', stopReason: 'API restarted; runner not found' }, 0, 0, 0)], [ADMIN])
    expect(g.rows[0].cells.NIFTY[0]!.marks).toEqual([
      { kind: 'stopped', label: 'off', at: '12:10', title: 'API restarted at 12:10, and not restarted' },
    ])
  })

  it('totals per strategy, per account and per underlying, and they agree', () => {
    expect(grid.rows[0].figures).toMatchObject({ trades: 11, charges: 972 })
    const [admin, cfc] = grid.totals
    expect(admin.runs).toBe(4)
    expect(admin.byUnderlying.NIFTY?.net).toBe(2108)
    expect(admin.byUnderlying.SENSEX?.net).toBe(-5120)
    expect(cfc.byUnderlying.CRUDEOIL).toBeNull()
    expect(admin.figures.net + cfc.figures.net).toBe(grid.figures.net)
    expect(grid.rows.reduce((a, r) => a + r.figures.net, 0)).toBe(grid.figures.net)
  })

  it("knows a deployed run that has not traded yet: the morning plan's tick", () => {
    const g = buildGrid([run(), run({ trades: 1 })], [ADMIN])
    expect(g.rows[0].cells.NIFTY[0]!.waiting).toBe(false)
    expect(buildGrid([run()], [ADMIN]).rows[0].cells.NIFTY[0]!.waiting).toBe(true)
  })

  it('shows only the accounts in scope', () => {
    const g = buildGrid(runs, [CFC])
    expect(g.rows.map((r) => r.strategy)).toEqual(['GhostTangentCrossings', 'SmcStructureBreak'])
    expect(g.rows[0].cells.NIFTY).toHaveLength(1)
  })

  it('reports lots when every run of a row trades the same, else null', () => {
    expect(grid.rows[0].lots).toBe(2)
    expect(buildGrid([run({ lots: 1 }), run({ lots: 2, underlying: 'SENSEX' })], [ADMIN]).rows[0].lots).toBeNull()
  })
})

describe('runCounts', () => {
  it('counts what each cell is doing now, a restart once', () => {
    const runs = [
      run(),
      stopped({ stoppedBy: 'risk-guard', stopReason: 'Stop loss hit: …', strategyName: 'Fulcrum' }, 0, 0, 0),
      stopped({ stoppedBy: 'runner', stopReason: 'Runner exited (code 1)', strategyName: 'SmcStructureBreak', stoppedUtc: istIso('10:02') }, 0, 0, 0),
      run({ strategyName: 'SmcStructureBreak', startedUtc: istIso('10:04') }),
      stopped({ stoppedBy: 'market-hours', stopReason: 'Market closed (15:30 IST)', strategyName: 'ChainFlowBuy' }, 0, 0, 0),
      run({ role: 'alerts', strategyName: 'Watcher' }),
    ]
    expect(runCounts(runs)).toEqual({ total: 4, live: 2, rule: 1, target: 0, closed: 1, fault: 0, person: 0 })
  })
})

// ---------------------------------------------------------------- the plan

describe('planFromItem', () => {
  it("reads Sentinel's all-live wording with the accounts", () => {
    expect(planFromItem({ key: 'plan', detail: 'All 23 planned runs are live (admin 13, coderforchange 10).' })).toEqual({
      total: 23,
      perAccount: [
        { name: 'admin', runs: 13 },
        { name: 'coderforchange', runs: 10 },
      ],
      notLive: 0,
      checkedUtc: null,
    })
  })

  it('reads the missing-runs wording', () => {
    expect(planFromItem({ key: 'plan', detail: '2 of 23 planned runs are not live: admin Fulcrum NIFTY, admin Fulcrum SENSEX.' })).toMatchObject({
      total: 23,
      notLive: 2,
    })
  })

  it('returns null for another item, a skip, or words it does not know', () => {
    expect(planFromItem({ key: 'feeds', detail: 'All 3 planned runs are live' })).toBeNull()
    expect(planFromItem({ key: 'plan', detail: 'Not checked: the plan is checked between 08:50 and 15:25.' })).toBeNull()
    expect(planFromItem(undefined)).toBeNull()
  })
})

describe('plannedFor', () => {
  const plan = planFromItem({ key: 'plan', detail: 'All 23 planned runs are live (admin 13, coderforchange 10).' })
  it('counts the whole plan, or one account of it', () => {
    expect(plannedFor(plan, null)).toBe(23)
    expect(plannedFor(plan, 'coderforchange')).toBe(10)
  })
  it('does not guess an account the checkup did not name', () => {
    expect(plannedFor(planFromItem({ key: 'plan', detail: '2 of 23 planned runs are not live: a, b.' }), 'admin')).toBeNull()
    expect(plannedFor(null, null)).toBeNull()
  })
})

describe("today's plan and checkups", () => {
  const detail = (id: number, slot: string, done: string, items: CheckupDetail['items']): CheckupDetail => ({
    id,
    slot,
    status: 'done',
    verdict: 'ok',
    headline: '',
    requestedUtc: null,
    requestedBy: '',
    startedUtc: done,
    completedUtc: done,
    host: 'h',
    counts: { ok: 0, warn: 0, fail: 0, info: 0, skip: 0 },
    items,
    itemsUnreadable: false,
    error: '',
    notifiedUtc: null,
  })
  const plan = (text: string) => ({ key: 'plan', area: 'Strategies', title: 'Morning plan', state: 'ok', detail: text, action: '', link: null })

  it("takes the newest of today's checkups that read the plan", () => {
    const morning = detail(61, 'morning', istIso('08:47'), [plan('All 23 planned runs are live (admin 13, coderforchange 10).')])
    const close = detail(62, 'close', istIso('15:52'), [])
    const request = detail(63, 'on-request', istIso('11:00'), [plan('1 of 23 planned runs are not live: admin Fulcrum NIFTY.')])
    expect(todaysPlan([close, morning, request], TODAY)).toMatchObject({ total: 23, notLive: 1 })
    expect(todaysPlan([close, morning], TODAY)).toMatchObject({ total: 23, notLive: 0, checkedUtc: istIso('08:47') })
  })

  it("ignores yesterday's plan", () => {
    const friday = detail(59, 'morning', istIso('08:47', '2026-09-25'), [plan('All 21 planned runs are live (admin 13, coderforchange 8).')])
    expect(todaysPlan([friday], TODAY)).toBeNull()
  })

  it("finds today's morning checkup in the history", () => {
    const list = [
      { id: 62, slot: 'close', status: 'done', completedUtc: istIso('15:52') },
      { id: 61, slot: 'morning', status: 'done', completedUtc: istIso('08:47') },
      { id: 59, slot: 'morning', status: 'done', completedUtc: istIso('08:47', '2026-09-25') },
    ] as CheckupSummary[]
    expect(morningCheckupId(list, TODAY)).toBe(61)
    expect(morningCheckupId(list.slice(2), TODAY)).toBeNull()
  })
})


// ---------------------------------------------------------------- indices, levels, traces

describe('indexRows', () => {
  const item = (symbol: string, name: string, ltp: number, prev: number, contract: string | null = null) => ({
    symbol,
    name,
    contract,
    lastTradedPrice: ltp,
    previousClose: prev,
    open: prev,
    high: ltp + 10,
    low: prev - 10,
    volume: null,
    change: ltp - prev,
    changePercent: ((ltp - prev) / prev) * 100,
    updatedUtc: istIso('11:42'),
    isSubscribed: true,
  })
  const pulse: MarketPulseResponse = {
    latestQuoteUtc: istIso('11:42'),
    groups: [
      { key: 'index', title: 'Indices', items: [item('BSE:SENSEX-INDEX', 'SENSEX', 74588.3, 74406.9), item('NSE:NIFTY50-INDEX', 'NIFTY 50', 23341.55, 23284.1), item('NSE:NIFTYBANK-INDEX', 'BANK NIFTY', 56248.2, 56102.35)] },
      { key: 'equity', title: 'Large caps', items: [item('NSE:RELIANCE-EQ', 'Reliance', 1418.6, 1409.35)] },
      {
        key: 'commodity',
        title: 'Commodities',
        items: [item('MCX:CRUDEOIL26OCTFUT', 'Crude oil', 9719, 9642, 'Oct 2026'), item('MCX:GOLD26OCTFUT', 'Gold', 153240, 152870, 'Oct 2026'), item('MCX:SILVER26DECFUT', 'Silver', 1, 1)],
      },
    ],
  }
  const vix = { symbol: 'NSE:INDIAVIX-INDEX', lastPrice: 13.42, change: -0.29, changePercent: -2.12, previousClose: 13.71, previousCloseBasis: 'feed', asOfUtc: null, sourceKey: 'dhan', isLive: true, basis: 'live-quote' }

  it('reads the indices in desk order, then VIX from the chain, then crude and gold', () => {
    const rows = indexRows(pulse, vix)
    expect(rows.map((r) => r.key)).toEqual(['NIFTY', 'BANKNIFTY', 'SENSEX', 'VIX', 'CRUDEOIL', 'GOLD'])
    expect(rows[4]).toMatchObject({ contract: 'Oct', digits: 0, chained: false })
    expect(rows.filter((r) => r.chained).map((r) => r.key)).toEqual(['NIFTY', 'BANKNIFTY', 'SENSEX'])
  })

  it('leaves out what has not arrived rather than zeroing it', () => {
    expect(indexRows(undefined, undefined)).toEqual([])
    expect(indexRows(pulse, null).map((r) => r.key)).not.toContain('VIX')
  })

  it("reads a commodity's root off its contract symbol", () => {
    expect(commodityRoot('MCX:CRUDEOIL26OCTFUT')).toBe('CRUDEOIL')
    expect(commodityRoot('NSE:NIFTY50-INDEX')).toBeNull()
  })

  it('measures the range so far only with both ends', () => {
    expect(rangeSoFar({ high: 23352.7, low: 23229.65 })).toBeCloseTo(123.05)
    expect(rangeSoFar({ high: null, low: 1 })).toBeNull()
  })
})

describe('chainLevels', () => {
  it("takes the header's levels and the straddle's move", () => {
    const chain = {
      spotPrice: 23341.55,
      expiryDate: '2026-09-29',
      strikes: [{ strikePrice: 23350, isAtTheMoney: true, call: { lastTradedPrice: 62.4 }, put: { lastTradedPrice: 59 } }],
      header: { supportStrike: 23200, resistanceStrike: 23500, putCallRatio: 0.94, maxPainStrike: 23300, atTheMoneyIv: 11.6 },
    } as unknown as OptionChain
    const levels = chainLevels(chain)!
    expect(levels).toMatchObject({ putWall: 23200, callWall: 23500, pcr: 0.94, maxPain: 23300, iv: 11.6, expiry: '2026-09-29' })
    expect(levels.straddlePct).toBeCloseTo((121.4 / 23341.55) * 100, 4)
  })

  it('has nothing without a header', () => {
    expect(chainLevels(undefined)).toBeNull()
    expect(chainLevels({ strikes: [] } as unknown as OptionChain)).toBeNull()
  })
})

describe('dayTrace', () => {
  it("keeps today's one-minute closes, oldest first", () => {
    const bar = (at: string, close: number, day = TODAY) => ({ symbol: 's', resolution: '1m', barStartUtc: istIso(at, day), open: 0, high: 0, low: 0, close, volumeDelta: 0, tickCount: 1, updatedUtc: '' })
    expect(dayTrace([bar('09:16', 2), bar('15:29', 9, '2026-09-25'), bar('09:15', 1)], TODAY)).toEqual([1, 2])
    expect(dayTrace(undefined, TODAY)).toEqual([])
  })
})

// ---------------------------------------------------------------- forecasts

describe('forecast rows', () => {
  const fc = (over: Partial<Forecast>): Forecast => ({
    id: 1,
    modelKey: 'range.har-vix',
    modelVersion: '1',
    target: 'range',
    underlying: 'NIFTY',
    sessionDate: TODAY,
    issuedUtc: istIso('08:50'),
    prediction: { median: 0.8, low80: 0.55, high80: 1.12, prevClose: 23284.1, points: { median: 186, low80: 128, high80: 262 }, buckets: { quiet: 0.3, normal: 0.5, wild: 0.2 }, bucketEdges: [0.6, 1] },
    baseline: { median: 0.88, low80: 0.6, high80: 1.2, prevClose: 23284.1, points: { median: 204, low80: 140, high80: 280 }, buckets: { quiet: 0.3, normal: 0.4, wild: 0.3 }, bucketEdges: [0.6, 1] },
    inputs: null,
    outcome: null,
    scores: null,
    scoredUtc: null,
    ...over,
  })
  const list = [
    fc({ id: 1, modelKey: 'range.har' }),
    fc({ id: 2 }),
    fc({ id: 3, target: 'trend', modelKey: 'trend.logit', prediction: { p: 0.22 }, baseline: { p: 0.25 } }),
    fc({ id: 4, target: 'direction', modelKey: 'direction.logit', prediction: { p: 0.54 }, baseline: { p: 0.52 } }),
    fc({ id: 5, underlying: 'BANKNIFTY' }),
    fc({ id: 6, sessionDate: '2026-09-25' }),
  ]

  it("leads with range.har-vix and reads points, trend and up-close against the baseline", () => {
    const rows = forecastRows(list, TODAY)
    expect(rows.map((r) => r.underlying)).toEqual(['NIFTY', 'BANKNIFTY'])
    expect(rows[0]).toMatchObject({
      range: { median: 186, low80: 128, high80: 262, baseMedian: 204 },
      trend: { p: 0.22, base: 0.25 },
      up: { p: 0.54, base: 0.52 },
      scored: null,
    })
    expect(rows[1].trend).toBeNull()
  })

  it('reads the score once the session is scored', () => {
    const scored = fc({
      outcome: { open: 23296.8, high: 23398.4, low: 23229.65, close: 23356.8, range: 0.72, bucket: 'normal', trendDay: false, efficiency: 0.31, up: true },
      scores: { loss: 0.1, baselineLoss: 0.12, metrics: { covered80: true } },
      scoredUtc: istIso('15:50'),
    })
    const [row] = forecastRows([scored], TODAY)
    expect(row.scored).toMatchObject({ inside: true, up: true, scoredUtc: istIso('15:50') })
    expect(row.scored!.range).toBeCloseTo(168.75)
    expect(forecastCounts([scored, fc({ id: 9 })], TODAY)).toEqual({ issued: 2, scored: 1, issuedUtc: istIso('08:50'), rangesScored: 1, rangesInside: 1 })
  })
})

describe('modelStanding', () => {
  it("reads the model's row for all indices, else its first", () => {
    const rows = [
      { modelKey: 'range.har-vix', underlying: 'NIFTY', status: 'collecting' as const, liveCount: 4 },
      { modelKey: 'range.har-vix', underlying: 'ALL', status: 'collecting' as const, liveCount: 12 },
      { modelKey: 'range.har', underlying: 'ALL', status: 'testing' as const, liveCount: 40 },
    ]
    expect(modelStanding(rows)).toEqual({ status: 'collecting', liveCount: 12 })
    expect(modelStanding(rows.slice(0, 1))).toEqual({ status: 'collecting', liveCount: 4 })
    expect(modelStanding(undefined)).toBeNull()
  })
})

// ---------------------------------------------------------------- open legs

describe('deskLegs', () => {
  const pos = (over: Partial<OptionChainPosition>): OptionChainPosition => ({
    runId: 612,
    strategyName: 'GhostTangentCrossings',
    isManual: false,
    userName: 'admin',
    groupId: 'g1',
    symbol: 'NSE:NIFTY2692923300PE',
    instrumentType: 'PE',
    strikePrice: 23300,
    expiryDate: '2026-09-29',
    direction: 'LONG',
    quantity: 2,
    lotSize: 65,
    averagePrice: 64.8,
    markPrice: 69.57,
    markUtc: null,
    unrealizedPnl: 620,
    stopLossPrice: null,
    targetPrice: null,
    openedUtc: istIso('11:12'),
    ...over,
  })

  it('lists legs largest first, marking the carried and the expiring', () => {
    const legs = deskLegs(
      [
        {
          underlying: 'NIFTY',
          rows: [
            pos({}),
            pos({ runId: 540, isManual: true, strategyName: 'Manual book', groupId: 'm1', symbol: 'NSE:NIFTY2692923300CE', instrumentType: 'CE', unrealizedPnl: 3146, openedUtc: istIso('14:06', '2026-09-25') }),
          ],
        },
        { underlying: 'CRUDEOIL', rows: [pos({ runId: 624, symbol: 'MCX:CRUDEOIL26OCTFUT', instrumentType: 'FUT', strikePrice: null, expiryDate: '2026-10-19', unrealizedPnl: -1400 })] },
      ],
      { today: TODAY, nextSession: '2026-09-29' },
    )
    expect(legs.map((l) => l.label)).toEqual(['NIFTY 23300 CE', 'CRUDEOIL FUT', 'NIFTY 23300 PE'])
    expect(legs[0]).toMatchObject({ manual: true, carriedFrom: 'Fri', expires: 'next', lots: 2 })
    expect(legs[1]).toMatchObject({ carriedFrom: null, expires: null })
  })

  it('counts a leg asked for twice once, and narrows to one account', () => {
    const rows = [pos({}), pos({ userName: 'coderforchange', runId: 613 })]
    expect(deskLegs([{ underlying: 'NIFTY', rows }, { underlying: 'NIFTY', rows }], { today: TODAY, nextSession: null })).toHaveLength(2)
    expect(deskLegs([{ underlying: 'NIFTY', rows }], { today: TODAY, nextSession: null, userName: 'coderforchange' })).toHaveLength(1)
  })

  it('keeps a leg without a mark, last', () => {
    const legs = deskLegs([{ underlying: 'NIFTY', rows: [pos({ markPrice: null, unrealizedPnl: null, groupId: 'x' }), pos({})] }], { today: TODAY, nextSession: null })
    expect(legs.map((l) => l.pnl)).toEqual([620, null])
  })

  it('reads the runs a leg was carried from off the book', () => {
    expect([...carriedRunIds([{ carriedFromRunId: 615 }, { carriedFromRunId: null }, {}])]).toEqual([615])
  })
})

// ---------------------------------------------------------------- the timeline

describe('deskTimeline', () => {
  const incident = (over: Partial<Incident>): Incident => ({
    id: 88,
    fingerprint: 'f',
    agent: 'logs',
    rule: 'r',
    severity: 'medium',
    status: 'open',
    title: 'Dhan feed reconnected 3× in 10 min',
    summary: '',
    location: '',
    evidence: [],
    suggestion: '',
    occurrences: 1,
    firstSeenUtc: istIso('11:31'),
    lastSeenUtc: istIso('11:41'),
    resolvedUtc: null,
    acknowledgedBy: null,
    acknowledgedUtc: null,
    ...over,
  })

  const runs = [
    ...Array.from({ length: 3 }, (_, i) => run({ startedUtc: istIso('08:45'), underlying: ['NIFTY', 'BANKNIFTY', 'SENSEX'][i] })),
    stopped({ strategyName: 'Fulcrum', underlying: 'SENSEX', startedUtc: istIso('08:46'), stoppedUtc: istIso('11:20'), stoppedBy: 'risk-guard', stopReason: 'Stop loss hit: P&L −₹5,120 ≤ −₹5,000' }, 2960, 8080, 96),
    stopped({ userId: 2, userName: 'coderforchange', strategyName: 'SmcStructureBreak', startedUtc: istIso('08:45'), stoppedUtc: istIso('10:02'), stoppedBy: 'runner', stopReason: 'Runner exited (code 1)' }, 0, 0, 0),
    run({ userId: 2, userName: 'coderforchange', strategyName: 'SmcStructureBreak', startedUtc: istIso('10:04') }),
    stopped({ strategyName: 'ChainFlowBuy', startedUtc: istIso('08:45'), stoppedUtc: istIso('15:30'), stoppedBy: 'market-hours', stopReason: 'Market closed (15:30 IST)' }, 0, 0, 0),
    stopped({ strategyName: 'ChainFlowBuy', underlying: 'SENSEX', startedUtc: istIso('08:45'), stoppedUtc: istIso('15:30'), stoppedBy: 'market-hours', stopReason: 'Market closed (15:30 IST)' }, 0, 0, 0),
  ]
  const deploys: DeployRecord[] = [
    { startedUtc: istIso('07:59'), finishedUtc: istIso('08:01'), outcome: 'applied', summary: '', fromCommit: 'aaaaaaa1', toCommit: 'bbbbbbb2', commits: ['x', 'y'], filesChanged: 3, steps: [], machine: 'm' },
    { startedUtc: istIso('08:05'), finishedUtc: istIso('08:05'), outcome: 'skipped', summary: 'nothing new', fromCommit: 'b', toCommit: 'b', commits: [], filesChanged: 0, steps: [], machine: 'm' },
    { startedUtc: istIso('22:00', '2026-09-27'), finishedUtc: istIso('22:14', '2026-09-27'), outcome: 'applied', summary: '', fromCommit: 'a', toCommit: 'c', commits: [], filesChanged: 1, steps: [], machine: 'm' },
  ]
  const events = deskTimeline({
    today: TODAY,
    runs,
    incidents: [
      incident({}),
      incident({ id: 84, severity: 'high', status: 'resolved', title: 'coderforchange · SMC break on NIFTY runner exited', firstSeenUtc: istIso('10:02'), resolvedUtc: istIso('10:04') }),
      incident({ id: 79, status: 'resolved', title: 'old one', firstSeenUtc: istIso('10:31', '2026-09-25'), resolvedUtc: istIso('15:31', '2026-09-25') }),
    ],
    checkups: [{ id: 61, slot: 'morning', status: 'done', verdict: 'attention', headline: 'FYERS backup signed out', completedUtc: istIso('08:47') } as CheckupSummary],
    deploys,
  })
  const texts = events.map((e) => `${e.time} ${e.text}`)

  it('orders the day newest first', () => {
    expect(events.map((e) => e.atMs)).toEqual([...events.map((e) => e.atMs)].sort((a, b) => b - a))
  })

  it('folds the morning start and the close into one line each', () => {
    expect(texts).toContain('08:45 7 runs started')
    expect(texts).toContain('15:30 Market close · 2 runs stopped')
  })

  it('names every other start and stop, with the net of a risk stop', () => {
    expect(texts).toContain('11:20 Stop loss hit · admin Fulcrum SENSEX · net −₹5,120')
    expect(texts).toContain('10:02 Runner exited · coderforchange SMC Structure Break NIFTY')
    expect(texts).toContain('10:04 Restarted coderforchange SMC Structure Break NIFTY')
  })

  it("keeps today's incidents, checkups and applied deploys, and nothing from other days", () => {
    expect(texts).toContain('11:31 Medium · Dhan feed reconnected 3× in 10 min')
    expect(texts).toContain('10:02 High · coderforchange · SMC break on NIFTY runner exited · resolved 10:04')
    expect(texts).toContain('08:47 Checkup before the open: attention · FYERS backup signed out')
    expect(texts).toContain('08:01 Deployed bbbbbbb · 2 commits')
    expect(texts.join('\n')).not.toMatch(/old one|nothing new|22:14/)
  })

  it('leads with the live incidents, newest first', () => {
    const live = liveIncidents([incident({ id: 1, firstSeenUtc: istIso('10:48'), status: 'acknowledged' }), incident({ id: 2 }), incident({ id: 3, status: 'resolved' })])
    expect(live.map((i) => i.id)).toEqual([2, 1])
  })

  it('reads forecasts issued and scored', () => {
    const f = (id: number, scored: boolean): Forecast =>
      ({
        id,
        modelKey: 'range.har-vix',
        target: 'range',
        sessionDate: TODAY,
        issuedUtc: istIso('08:50'),
        outcome: scored ? {} : null,
        scores: scored ? { metrics: { covered80: id !== 3 } } : null,
        scoredUtc: scored ? istIso('15:50') : null,
      }) as unknown as Forecast
    const t = deskTimeline({ today: TODAY, forecasts: [f(1, true), f(2, true), f(3, true)] }).map((e) => `${e.time} ${e.text}`)
    expect(t).toEqual(['15:50 Forecasts scored · 2 of 3 ranges inside the 80% band', '08:50 Forecasts issued · 3'])
  })
})

// ---------------------------------------------------------------- news

describe('newsLines', () => {
  const headline = (id: number, title: string, symbols: string, topics = '', at = '08:05'): IntelHeadline => ({
    id,
    source: 'Moneycontrol',
    category: 'markets',
    title,
    summary: '',
    link: '',
    publishedUtc: null,
    firstSeenUtc: istIso(at),
    sentiment: -0.2,
    importance: 1,
    symbols,
    topics,
    scoredUtc: null,
    scoreModel: '',
  })
  const filing = (id: number, symbol: string, subject: string, topics = '', at = '09:48'): IntelAnnouncement => ({
    id,
    exchange: 'NSE',
    symbol,
    company: symbol,
    subject,
    details: '',
    attachmentUrl: '',
    announcedUtc: istIso(at),
    firstSeenUtc: istIso(at),
    sentiment: null,
    importance: null,
    symbols: '',
    topics,
    scoredUtc: null,
    scoreModel: '',
  })
  const news = [headline(1, 'FIIs sell', ''), headline(2, 'ONGC slides', 'ONGC', 'guidance', '10:55'), headline(3, 'TCS Q2 profit up', 'TCS', 'results', '11:00')]
  const filings = [filing(10, 'BEL', 'Award of order'), filing(11, 'SMALLCO', 'Loss of share certificate'), filing(12, 'INFY', 'Financial Results', '', '11:30')]
  const names = { held: new Set(['ONGC']), watched: new Set(['BEL']) }

  it('mixes news with the filings of desk names, newest first', () => {
    const all = newsLines(news, filings, 'all', names)
    expect(all.map((l) => l.key)).toEqual(['a12', 'n3', 'n2', 'a10', 'n1'])
    expect(all.find((l) => l.key === 'a10')).toMatchObject({ kind: 'filing', title: 'BEL: Award of order', source: 'NSE filing', sentiment: null })
  })

  it('narrows to held and watched names', () => {
    expect(newsLines(news, filings, 'held', names).map((l) => l.key)).toEqual(['n2', 'a10'])
  })

  it('shows filings alone, and results from either source', () => {
    expect(newsLines(news, filings, 'filings', names).map((l) => l.key)).toEqual(['a12', 'a10'])
    expect(newsLines(news, filings, 'results', names).map((l) => l.key)).toEqual(['a12', 'n3'])
  })

  it('knows NIFTY 50 by the scorer\'s list, and reads a ticker off a symbol', () => {
    expect(NIFTY50).toHaveLength(50)
    expect(new Set(NIFTY50).size).toBe(50)
    expect(tickerOf('NSE:RELIANCE-EQ')).toBe('RELIANCE')
    expect(tickerOf('NSE:NIFTY50-INDEX')).toBe('NIFTY50')
    expect(tickerOf('ONGC')).toBe('ONGC')
  })
})

// ---------------------------------------------------------------- the week

describe('weekRows', () => {
  const event = (date: string, title: string, kind: string, timeIst: string | null = null, importance = 2) => ({
    id: null,
    date,
    timeIst,
    region: 'IN',
    category: kind,
    title,
    importance,
    notes: null,
    source: null,
    kind,
  })
  const meeting = (id: number, symbol: string, purpose: string, eventDate: string): IntelBoardMeeting => ({ id, exchange: 'NSE', symbol, company: symbol, purpose, eventDate, firstSeenUtc: '' })

  it('merges expiries of one day, keeps events and holidays, and the desk names’ board meetings', () => {
    const rows = weekRows(
      [
        event('2026-09-29', 'NIFTY options expiry', 'expiry', '15:30', 1),
        event('2026-10-01', 'SENSEX options expiry', 'expiry', '15:30', 1),
        event('2026-10-01', 'RBI policy decision', 'event', '10:00', 3),
        event('2026-10-02', 'NSE closed: Gandhi Jayanti', 'holiday'),
        event('2026-10-20', 'Too far', 'event'),
        event('2026-09-29', 'SENSEX options expiry', 'expiry', '15:30', 1),
      ],
      [meeting(1, 'TCS', 'Financial Results', '2026-10-01'), meeting(2, 'SMALLCO', 'Financial Results', '2026-10-01'), meeting(3, 'SBIN', 'Fund raising by bonds', '2026-09-30')],
      new Set(['TCS', 'SBIN']),
      '2026-09-28',
      '2026-10-05',
    )
    expect(rows.map((r) => `${r.date} ${r.time ?? '—'} ${r.kind}: ${r.title}`)).toEqual([
      '2026-09-29 15:30 expiry: NIFTY & SENSEX expiry',
      '2026-09-30 — meeting: SBIN board meeting · fund raising by bonds',
      '2026-10-01 10:00 event: RBI policy decision',
      '2026-10-01 15:30 expiry: SENSEX expiry',
      '2026-10-01 — results: TCS results',
      '2026-10-02 — holiday: NSE closed: Gandhi Jayanti',
    ])
  })
})

// ---------------------------------------------------------------- overnight

describe('overnightRows', () => {
  it("prefers this morning's snapshot, else the last daily close, and writes a yield's change in bp", () => {
    const rows = overnightRows(
      [
        { key: 'SPX', price: 6684.1, previousClose: 6663.4, changePct: 0.31, asOfUtc: null, fetchedUtc: istIso('08:30'), source: 'yahoo' },
        { key: 'SPX', price: 6690, previousClose: 6663.4, changePct: 0.4, asOfUtc: istIso('08:58'), fetchedUtc: istIso('08:58'), source: 'yahoo' },
        { key: 'US10Y', price: 4.08, previousClose: 4.1, changePct: -0.5, asOfUtc: istIso('08:58'), fetchedUtc: istIso('08:58'), source: 'yahoo' },
        // New York's Friday close, stamped 01:30 IST on Saturday, taken again on Monday morning.
        { key: 'NDX', price: 22712.6, previousClose: 22581.6, changePct: 0.58, asOfUtc: istIso('01:30', '2026-09-26'), fetchedUtc: istIso('08:45'), source: 'yahoo' },
      ],
      [
        { symbol: 'DJI', date: '2026-09-24', open: null, high: null, low: null, close: 46000, volume: null, source: 'yahoo', fetchedUtc: '' },
        { symbol: 'DJI', date: '2026-09-25', open: null, high: null, low: null, close: 46218.4, volume: null, source: 'yahoo', fetchedUtc: '' },
      ],
    )
    expect(rows.map((r) => r.key)).toEqual(['DJI', 'SPX', 'NDX', 'US10Y'])
    expect(rows[2].asOf).toBe('last close')
    expect(rows[0]).toMatchObject({ value: 46218.4, asOf: 'Fri close' })
    expect(rows[0].change).toBeCloseTo(0.4748, 3)
    expect(rows[1]).toMatchObject({ value: 6690, change: 0.4, asOf: '08:58' })
    expect(rows[3]).toMatchObject({ isYield: true })
    expect(rows[3].change).toBeCloseTo(-2)
  })
})

// ---------------------------------------------------------------- the strip on a phone

describe('stripSpans', () => {
  it('packs the session strip as the mockup does', () => {
    // Net · admin · cfc · runs · legs · feed · incidents · checkup
    expect(stripSpans(['lead', 'account', 'account', 'other', 'other', 'other', 'other', 'other'])).toEqual([6, 3, 3, 2, 2, 2, 3, 3])
  })

  it('stretches a lone last cell across the row', () => {
    expect(stripSpans(['lead', 'other', 'other', 'other', 'other'])).toEqual([6, 2, 2, 2, 6])
  })

  it('closes a row a wide cell cannot join', () => {
    expect(stripSpans(['lead', 'account', 'other', 'other', 'other'])).toEqual([6, 3, 3, 3, 3])
  })

  it('always fills whole rows', () => {
    const kinds = ['lead', 'account', 'other'] as const
    for (let n = 1; n < 10; n++) {
      const list = Array.from({ length: n }, (_, i) => kinds[(i * 7) % 3])
      expect(stripSpans(list).reduce((a, b) => a + b, 0) % 6).toBe(0)
    }
  })
})

// ---------------------------------------------------------------- figures in words

describe('figures', () => {
  it('tones a figure by what it rounds to', () => {
    expect(figureTone(482)).toBe('pos')
    expect(figureTone(-0.4)).toBe('flat')
    expect(figureTone(-3)).toBe('neg')
    expect(figureTone(null)).toBeNull()
  })

  it('writes a dense rupee figure in thousands and lakhs', () => {
    expect(compactInr(482)).toBe('+482')
    expect(compactInr(-8220)).toBe('−8.2k')
    expect(compactInr(136000)).toBe('+1.36L')
    expect(compactInr(0)).toBe('0')
    expect(compactInr(29900, false)).toBe('29.9k')
  })

  it('signs a number with Indian grouping and a true minus', () => {
    expect(signedNumber(57.45)).toBe('+57.45')
    expect(signedNumber(-2184, 0)).toBe('−2,184')
    expect(signedNumber(145600.2, 1)).toBe('+1,45,600.2')
  })
})
