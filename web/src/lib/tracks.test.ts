import { describe, expect, it } from 'vitest'

import type { DeskAccount } from './desk'
import { NSE_CLOSE, NSE_OPEN } from './pnlSeries'
import {
  barPoints,
  priceTrace,
  readTrackUnderlying,
  sumCurves,
  trackHeight,
  trackScale,
  trackUnderlyings,
  tracksLayout,
  underlyingCode,
  validUnderlying,
  writeTrackUnderlying,
} from './tracks'
import type { LiveRunSummary, RunPnlSeries } from './types'

const DAY = '2026-09-25'
const at = (hm: string) => new Date(Date.parse(`${DAY}T${hm}:00+05:30`)).toISOString()
const min = (hm: string) => Number(hm.slice(0, 2)) * 60 + Number(hm.slice(3, 5))
const span = (a: string, b: string) => Array.from({ length: min(b) - min(a) + 1 }, (_, i) => min(a) + i)

const ADMIN: DeskAccount = { id: 2, name: 'admin', tone: 1 }
const CFC: DeskAccount = { id: 7, name: 'coderforchange', tone: 2 }

let nextRun = 700
function run(over: Partial<LiveRunSummary> = {}): LiveRunSummary {
  nextRun++
  return {
    runId: nextRun,
    userId: 2,
    userName: 'admin',
    strategyId: 1,
    strategyName: 'GhostTangentCrossings',
    category: null,
    underlying: 'NIFTY',
    spotSymbol: 'NSE:NIFTY50-INDEX',
    lots: 2,
    role: null,
    lotSize: 65,
    risk: null,
    status: 'Stopped',
    isActive: false,
    startedUtc: at('09:18'),
    stoppedUtc: at('15:30'),
    stopReason: 'Market closed (15:30 IST)',
    stoppedBy: 'market-hours',
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

function series(r: LiveRunSummary, minutes: number[], net: (i: number) => number): RunPnlSeries {
  const n = minutes.length
  return {
    runId: r.runId,
    userId: r.userId,
    userName: r.userName,
    strategyName: r.strategyName,
    underlying: r.underlying,
    isManualBook: false,
    status: r.status,
    startedUtc: r.startedUtc!,
    inAccountTotals: true,
    minutes,
    realized: Array(n).fill(0),
    unrealized: Array(n).fill(0),
    charges: Array(n).fill(0),
    net: minutes.map((_, i) => net(i)),
  }
}

describe('trackScale and trackHeight', () => {
  it('scales every track to the median swing, at least ₹1,000, unmoved tracks aside', () => {
    expect(trackScale([200, 400, 1_500, 2_000, 90_000])).toBe(1_500)
    expect(trackScale([0, 0, 3_000, 5_000])).toBe(4_000)
    expect(trackScale([100, 0, Number.NaN])).toBe(1_000)
    expect(trackScale([])).toBe(1_000)
  })

  it('names an underlying as narrowly as a phone needs', () => {
    expect(['NIFTY', 'BANKNIFTY', 'SENSEX', 'CRUDEOIL', 'gold'].map(underlyingCode)).toEqual(['N', 'BN', 'SX', 'CR', 'GOL'])
  })

  it('compresses by the square root and stops a big track at full height', () => {
    expect(trackHeight(1_000, 4_000)).toBeCloseTo(0.5)
    expect(trackHeight(-4_000, 4_000)).toBe(-1)
    expect(trackHeight(-90_000, 4_000)).toBe(-1)
    expect(trackHeight(0, 4_000)).toBe(0)
  })
})

describe('sumCurves', () => {
  it('adds runs minute by minute, each holding its last value, a later run adding nothing before it starts', () => {
    const first = [
      { m: 560, v: 100 },
      { m: 600, v: -300 },
    ]
    const restart = [
      { m: 610, v: 50 },
      { m: 620, v: 80 },
    ]
    expect(sumCurves([first, restart])).toEqual([
      { m: 560, v: 100 },
      { m: 600, v: -300 },
      { m: 610, v: -250 },
      { m: 620, v: -220 },
    ])
    expect(sumCurves([[], first])).toEqual(first)
    expect(sumCurves([])).toEqual([])
  })
})

describe('barPoints', () => {
  it("keeps the day's closes, oldest first, on the day's minutes", () => {
    const bar = (utc: string, close: number) => ({ symbol: 'NSE:NIFTY50-INDEX', barStartUtc: utc, open: close, high: close, low: close, close, volume: 0 })
    const bars = [bar('2026-09-25T04:00:00Z', 23120), bar('2026-09-24T09:59:00Z', 23000), bar('2026-09-25T03:45:00Z', 23100)]
    expect(barPoints(bars as never, DAY)).toEqual([
      { m: min('09:15'), v: 23100 },
      { m: min('09:30'), v: 23120 },
    ])
  })
})

describe('tracksLayout', () => {
  const ghostNifty = run({ netPnl: -1_200, grossPnl: -900, charges: 300, realizedPnl: -900, trades: 3 })
  const ghostBank = run({ underlying: 'BANKNIFTY', netPnl: 400, grossPnl: 500, charges: 100, realizedPnl: 500, trades: 1 })
  const fulcrum = run({
    strategyName: 'Fulcrum',
    // Started first: the grid, and so the board, lists strategies in the order they started.
    startedUtc: at('09:17'),
    stoppedUtc: at('11:20'),
    stoppedBy: 'risk-guard',
    stopReason: 'Stop loss hit: P&L −₹5,120 ≤ −₹5,000',
    netPnl: -5_400,
    grossPnl: -5_120,
    charges: 280,
    realizedPnl: -5_120,
    trades: 40,
  })
  const smcFirst = run({ strategyName: 'SmcStructureBreak', stoppedUtc: at('10:02'), stoppedBy: 'runner', stopReason: 'Runner exited (code 1)', netPnl: -100, grossPnl: -80, charges: 20, realizedPnl: -80, trades: 1 })
  const smcRestart = run({ strategyName: 'SmcStructureBreak', startedUtc: at('10:04'), status: 'Running', isActive: true, stoppedUtc: null, stoppedBy: null, stopReason: null, netPnl: 50, grossPnl: 60, charges: 10, realizedPnl: 60, trades: 1 })
  const theirs = run({ userId: 7, userName: 'coderforchange', netPnl: 900, grossPnl: 1_000, charges: 100, realizedPnl: 1_000, trades: 2 })
  const alerts = run({ role: 'alerts', strategyName: 'PatternAlerts' })
  const runs = [fulcrum, ghostNifty, theirs, smcFirst, smcRestart, ghostBank, alerts]

  const gapped = (r: LiveRunSummary) => series(r, [...span('09:18', '12:41'), ...span('12:58', '15:30')], (i) => -i)
  const res = {
    date: DAY,
    dayStartUtc: '2026-09-24T18:30:00Z',
    runs: [
      gapped(ghostNifty),
      gapped(ghostBank),
      series(fulcrum, span('09:17', '11:20'), (i) => -i * 40),
      series(smcFirst, span('09:18', '10:02'), () => -100),
      series(smcRestart, span('10:04', '12:41'), () => 50),
      gapped(theirs),
    ],
    accounts: [],
  }
  const layout = (over: Partial<Parameters<typeof tracksLayout>[0]> = {}) =>
    tracksLayout({
      runs,
      accounts: [ADMIN, CFC],
      series: res,
      fills: new Map([[fulcrum.runId, [at('09:20'), at('09:20'), at('09:41'), at('11:19')]]]),
      carried: new Set([ghostNifty.runId]),
      day: DAY,
      nowMs: Date.parse(at('16:07')),
      isToday: false,
      ...over,
    })

  it('makes a track per account, strategy and underlying, in the grid’s order, and leaves alert-only runs out', () => {
    const l = layout()
    expect(l.groups.map((g) => g.account.name)).toEqual(['admin', 'coderforchange'])
    expect(l.groups[0].tracks.map((t) => `${t.strategy} ${t.underlying}`)).toEqual([
      'Fulcrum NIFTY',
      'GhostTangentCrossings NIFTY',
      'GhostTangentCrossings BANKNIFTY',
      'SmcStructureBreak NIFTY',
    ])
    expect(l.groups[0].figures.net).toBeCloseTo(-1_200 + 400 - 5_400 - 100 + 50)
  })

  it('says how each track ended, and marks a restart and a carried leg', () => {
    const [fulcrumT, ghostT, , smcT] = layout().groups[0].tracks
    expect(fulcrumT.end).toEqual({ m: min('11:20'), kind: 'rule', label: 'SL 11:20' })
    expect(ghostT.end).toMatchObject({ kind: 'close', label: 'closed' })
    expect(ghostT.carried).toBe(true)
    // The restart is live: the track has no end yet, and its restart is marked.
    expect(smcT).toMatchObject({ live: true, end: null, restarts: [min('10:04')] })
    expect(smcT.runs.map((r) => r.runId)).toEqual([smcFirst.runId, smcRestart.runId])
  })

  it("adds a restart's line to the first run's final value", () => {
    const smcT = layout().groups[0].tracks[3]
    expect(smcT.points.find((p) => p.m === min('11:00'))!.v).toBe(-50)
  })

  it('puts fills on their minutes, once each', () => {
    expect(layout().groups[0].tracks[0].fills).toEqual([min('09:20'), min('09:41'), min('11:19')])
  })

  it("cuts every track at the recorder's gap, and scales to a typical swing", () => {
    const l = layout()
    expect(l.gaps).toEqual([{ from: min('12:41'), to: min('12:58') }])
    expect(l.groups[0].tracks[1].segments).toHaveLength(2)
    expect(l.scale).toBeGreaterThanOrEqual(1_000)
    expect(l.axis).toEqual({ from: NSE_OPEN, to: NSE_CLOSE, evening: false })
    expect(l.any).toBe(true)
  })

  it('narrows to the accounts asked for', () => {
    expect(layout({ accounts: [CFC] }).groups.map((g) => g.account.id)).toEqual([7])
  })

  it('draws tracks without lines when the recorder has nothing, and says so', () => {
    const l = layout({ series: undefined })
    expect(l.any).toBe(false)
    expect(l.groups[0].tracks.every((t) => t.points.length === 0)).toBe(true)
    expect(l.groups[0].tracks[0].fills).toHaveLength(3)
  })

  it('marks now on the day that is today, inside the axis', () => {
    expect(layout({ isToday: true, nowMs: Date.parse(at('11:42')) }).now).toBe(min('11:42'))
    expect(layout({ isToday: true }).now).toBeNull()
  })
})

describe('narrowing the board to one underlying', () => {
  const nifty = run()
  const bank = run({ underlying: 'BANKNIFTY', spotSymbol: 'NSE:NIFTYBANK-INDEX', netPnl: 400, grossPnl: 500, charges: 100, realizedPnl: 500, trades: 1 })
  const sensex = run({ underlying: 'sensex', spotSymbol: 'BSE:SENSEX-INDEX' })
  const crudeEarly = run({ underlying: 'CRUDEOIL', spotSymbol: 'MCX:CRUDEOIL26SEPFUT', startedUtc: at('09:05') })
  const crudeLate = run({ underlying: 'CRUDEOIL', spotSymbol: 'MCX:CRUDEOIL26OCTFUT', startedUtc: at('17:00') })
  const book = run({ strategyName: 'Manual', underlying: 'MANUAL', spotSymbol: 'MANUAL' })
  const alerter = run({ role: 'alerts', underlying: 'FINNIFTY' })

  it('offers the underlyings the runs are on, in the index order, and no manual book or alert-only run', () => {
    expect(trackUnderlyings([crudeLate, sensex, bank, nifty, book, alerter, crudeEarly])).toEqual(['NIFTY', 'BANKNIFTY', 'SENSEX', 'CRUDEOIL'])
    expect(trackUnderlyings([nifty])).toEqual(['NIFTY'])
    expect(trackUnderlyings([])).toEqual([])
  })

  it('keeps the pick in the URL beside the view, and reads back what it wrote', () => {
    const params = new URLSearchParams('view=tracks')
    for (const u of ['NIFTY', 'BANKNIFTY', 'SENSEX', 'CRUDEOIL']) {
      const written = writeTrackUnderlying(params, u)
      expect(written.get('view')).toBe('tracks')
      expect(readTrackUnderlying(written)).toBe(u)
    }
    expect(writeTrackUnderlying(new URLSearchParams('view=tracks&underlying=SENSEX'), null).toString()).toBe('view=tracks')
    expect(readTrackUnderlying(new URLSearchParams('view=tracks&underlying=banknifty'))).toBe('BANKNIFTY')
    expect(readTrackUnderlying(new URLSearchParams('underlying=%3Cscript%3E'))).toBeNull()
    expect(readTrackUnderlying(new URLSearchParams('view=tracks'))).toBeNull()
  })

  it('falls back to every underlying when the URL names one no run is on', () => {
    expect(validUnderlying('SENSEX', ['NIFTY', 'SENSEX'])).toBe('SENSEX')
    expect(validUnderlying('CRUDEOIL', ['NIFTY', 'SENSEX'])).toBeNull()
    expect(validUnderlying(null, ['NIFTY'])).toBeNull()
  })

  it("draws the picked underlying's price, from its newest run's spot, and NIFTY 50 otherwise", () => {
    const runs = [nifty, bank, crudeEarly, crudeLate]
    expect(priceTrace(runs, null)).toEqual({ symbol: 'NSE:NIFTY50-INDEX', label: 'NIFTY 50' })
    expect(priceTrace(runs, 'NIFTY')).toEqual({ symbol: 'NSE:NIFTY50-INDEX', label: 'NIFTY 50' })
    expect(priceTrace(runs, 'BANKNIFTY')).toEqual({ symbol: 'NSE:NIFTYBANK-INDEX', label: 'BANKNIFTY' })
    expect(priceTrace(runs, 'CRUDEOIL')).toEqual({ symbol: 'MCX:CRUDEOIL26OCTFUT', label: 'CRUDEOIL' })
    expect(priceTrace(runs, 'GOLD')).toEqual({ symbol: 'NSE:NIFTY50-INDEX', label: 'NIFTY 50' })
  })

  it("keeps only the underlying's tracks, and draws each account's line from those runs alone", () => {
    const res = {
      date: DAY,
      dayStartUtc: '2026-09-24T18:30:00Z',
      runs: [series(nifty, span('09:18', '10:00'), (i) => i * 10), series(bank, span('09:30', '10:30'), (i) => -i)],
      // The server's account line is every underlying's: a BANKNIFTY board must not draw it.
      accounts: [{ userId: 2, userName: 'admin', runs: 2, minutes: [min('09:18')], realized: [0], unrealized: [0], charges: [0], net: [999] }],
    }
    const input = {
      runs: [nifty, bank],
      accounts: [ADMIN, CFC],
      series: res,
      fills: new Map<number, string[]>(),
      carried: new Set<number>(),
      day: DAY,
      nowMs: Date.parse(at('16:00')),
      isToday: false,
    }

    const all = tracksLayout(input)
    const onBank = tracksLayout({ ...input, underlying: 'banknifty' })

    expect(all.groups[0].tracks.map((t) => t.underlying)).toEqual(['NIFTY', 'BANKNIFTY'])
    expect(all.accounts[0].points).toEqual([{ m: min('09:18'), v: 999 }])
    expect(onBank.groups.map((g) => g.account.id)).toEqual([2])
    expect(onBank.groups[0].tracks.map((t) => t.underlying)).toEqual(['BANKNIFTY'])
    expect(onBank.groups[0].figures.net).toBe(400)
    expect(onBank.accounts).toHaveLength(1)
    expect(onBank.accounts[0].points).toEqual(onBank.groups[0].tracks[0].points)
    expect(onBank.accounts[0].last).toEqual({ m: min('10:30'), v: -60 })
  })
})
