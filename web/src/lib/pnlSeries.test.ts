import { describe, expect, it } from 'vitest'

import {
  GAP_MINUTES,
  axisInr,
  MCX_CLOSE,
  NSE_CLOSE,
  NSE_OPEN,
  dayAxis,
  dayCurves,
  dayStartMs,
  gapText,
  mcxCloseMinute,
  mergeGaps,
  minuteLabel,
  minuteOfDay,
  recorderGaps,
  seriesPoints,
  spreadLabels,
  splitAtGaps,
  stepPath,
  timeTicks,
  valueAt,
  valueDomain,
  valueTicks,
} from './pnlSeries'
import type { AccountPnlSeries, RunPnlSeries } from './types'

const DAY = '2026-09-28'
const START = Date.parse(`${DAY}T00:00:00+05:30`)
const at = (hm: string) => new Date(Date.parse(`${DAY}T${hm}:00+05:30`)).toISOString()
const min = (hm: string) => Number(hm.slice(0, 2)) * 60 + Number(hm.slice(3, 5))
/** Every minute from `a` to `b` inclusive. */
const span = (a: string, b: string) => Array.from({ length: min(b) - min(a) + 1 }, (_, i) => min(a) + i)

function run(over: Partial<RunPnlSeries> & { minutes: number[] }): RunPnlSeries {
  const n = over.minutes.length
  return {
    runId: 1,
    userId: 1,
    userName: 'admin',
    strategyName: 'GhostTangentCrossings',
    underlying: 'NIFTY',
    isManualBook: false,
    status: 'Stopped',
    startedUtc: at('09:18'),
    inAccountTotals: true,
    realized: Array(n).fill(0),
    unrealized: Array(n).fill(0),
    charges: Array(n).fill(0),
    net: over.minutes.map((_, i) => i * 10),
    ...over,
  }
}

function account(userId: number, minutes: number[], net: (i: number) => number = (i) => -i * 5): AccountPnlSeries {
  const n = minutes.length
  return {
    userId,
    userName: userId === 1 ? 'admin' : 'coderforchange',
    runs: 1,
    minutes,
    realized: Array(n).fill(0),
    unrealized: Array(n).fill(0),
    charges: Array(n).fill(0),
    net: minutes.map((_, i) => net(i)),
  }
}

describe('minutes of the day', () => {
  it('names a minute and finds the minute of an instant', () => {
    expect(minuteLabel(NSE_OPEN)).toBe('09:15')
    expect(minuteLabel(MCX_CLOSE)).toBe('23:30')
    expect(minuteOfDay(at('11:42'), START)).toBe(min('11:42'))
    expect(minuteOfDay('2026-09-28T06:12:59.900Z', START)).toBe(min('11:42'))
    expect(minuteOfDay(null, START)).toBeNull()
    expect(minuteOfDay('not a date', START)).toBeNull()
  })

  it("takes the series' own day start, else 00:00 IST of the date", () => {
    expect(dayStartMs(DAY, '2026-09-27T18:30:00Z')).toBe(START)
    expect(dayStartMs(DAY, null)).toBe(START)
    expect(dayStartMs(DAY, 'garbage')).toBe(START)
  })
})

describe('seriesPoints', () => {
  it('zips minutes and net, oldest first, leaving out what is not a number', () => {
    expect(seriesPoints({ minutes: [600, 558, 559], net: [30, 10, Number.NaN] })).toEqual([
      { m: 558, v: 10 },
      { m: 600, v: 30 },
    ])
    expect(seriesPoints({ minutes: [558, 559], net: [5] })).toEqual([{ m: 558, v: 5 }])
  })
})

describe('recorderGaps', () => {
  it('finds a stretch of missing minutes inside a run', () => {
    const r = run({ minutes: [...span('09:18', '11:02'), ...span('11:40', '15:31')] })
    expect(recorderGaps([r], { dayStartMs: START })).toEqual([{ from: min('11:02'), to: min('11:40') }])
  })

  it('lets a missed pass or two go', () => {
    const minutes = span('09:18', '10:00').filter((m) => m !== min('09:40') && m !== min('09:41'))
    expect(recorderGaps([run({ minutes })], { dayStartMs: START })).toEqual([])
    expect(GAP_MINUTES).toBe(3)
  })

  it('counts the stretch between a start and a late first point', () => {
    const r = run({ startedUtc: at('09:18'), minutes: span('10:30', '11:00') })
    expect(recorderGaps([r], { dayStartMs: START })).toEqual([{ from: min('09:18'), to: min('10:30') }])
  })

  it('says the recorder has gone quiet on a live run, today only', () => {
    const r = run({ status: 'Running', minutes: span('09:18', '11:30') })
    expect(recorderGaps([r], { dayStartMs: START, nowMinute: min('11:42') })).toEqual([{ from: min('11:30'), to: min('11:42') }])
    expect(recorderGaps([r], { dayStartMs: START, nowMinute: min('11:32') })).toEqual([])
    expect(recorderGaps([r], { dayStartMs: START })).toEqual([])
    // A stopped run is not waiting for points.
    expect(recorderGaps([run({ minutes: span('09:18', '11:30') })], { dayStartMs: START, nowMinute: min('15:00') })).toEqual([])
  })

  it('ignores manual books, whose silences mean nothing moved', () => {
    const book = run({ isManualBook: true, startedUtc: at('09:00'), minutes: [min('10:00'), min('13:00')] })
    expect(recorderGaps([book], { dayStartMs: START })).toEqual([])
  })

  it('ignores the start of a run begun on an earlier day', () => {
    const r = run({ startedUtc: '2026-09-25T03:48:00Z', minutes: span('09:15', '09:30') })
    expect(recorderGaps([r], { dayStartMs: START })).toEqual([])
  })

  it('counts only a stretch no run has a point in', () => {
    // b sat Pending from 10:30 while a was being written: only 11:02–11:40 is the recorder's.
    const a = run({ minutes: [...span('09:18', '11:02'), ...span('11:40', '12:00')] })
    const b = run({ runId: 2, startedUtc: at('10:30'), minutes: span('11:41', '12:00') })
    expect(recorderGaps([a, b], { dayStartMs: START })).toEqual([{ from: min('11:02'), to: min('11:40') }])
  })

  it('does not call the time between one run ending and the next starting a gap', () => {
    const a = run({ minutes: span('09:18', '11:00') })
    const b = run({ runId: 2, startedUtc: at('13:00'), minutes: span('13:00', '14:00') })
    expect(recorderGaps([a, b], { dayStartMs: START })).toEqual([])
  })

  it('merges touching and overlapping stretches', () => {
    expect(mergeGaps([{ from: 700, to: 720 }, { from: 600, to: 650 }, { from: 650, to: 660 }, { from: 710, to: 730 }, { from: 5, to: 5 }])).toEqual([
      { from: 600, to: 660 },
      { from: 700, to: 730 },
    ])
  })
})

describe('splitAtGaps', () => {
  it('cuts a curve where a gap lies between two points, and nowhere else', () => {
    const points = [558, 559, 662, 663, 700].map((m) => ({ m, v: m }))
    const segments = splitAtGaps(points, [{ from: 559, to: 662 }])
    expect(segments.map((s) => s.map((p) => p.m))).toEqual([
      [558, 559],
      [662, 663, 700],
    ])
  })

  it('keeps an uncut curve whole, and an empty one empty', () => {
    const points = [1, 2, 3].map((m) => ({ m, v: 0 }))
    expect(splitAtGaps(points, [])).toEqual([points])
    expect(splitAtGaps([], [{ from: 1, to: 9 }])).toEqual([])
  })
})

describe('valueAt', () => {
  const points = [
    { m: 560, v: 100 },
    { m: 600, v: -50 },
    { m: 931, v: 300 },
  ]
  it('holds the last value at or before a minute', () => {
    expect(valueAt(points, 559)).toBeNull()
    expect(valueAt(points, 560)).toBe(100)
    expect(valueAt(points, 599)).toBe(100)
    expect(valueAt(points, 600)).toBe(-50)
    expect(valueAt(points, 1200)).toBe(300)
    expect(valueAt([], 700)).toBeNull()
  })
})

describe('dayAxis', () => {
  it('is the NSE session when nothing reaches outside it', () => {
    expect(dayAxis([])).toEqual({ from: NSE_OPEN, to: NSE_CLOSE, evening: false })
    expect(dayAxis(span('09:18', '11:42'))).toEqual({ from: NSE_OPEN, to: NSE_CLOSE, evening: false })
  })

  it('keeps a run the close stopped a minute late on the NSE day, ending at 15:30', () => {
    expect(dayAxis([min('09:18'), min('15:31')])).toEqual({ from: NSE_OPEN, to: NSE_CLOSE, evening: false })
  })

  it('starts at the MCX open when a crude run has points before 09:15', () => {
    expect(dayAxis([min('09:00'), min('12:00')]).from).toBe(min('09:00'))
    expect(dayAxis([min('08:45'), min('12:00')]).from).toBe(min('09:00'))
  })

  it('runs on to the MCX close once a run has points in the evening', () => {
    expect(dayAxis([min('09:18'), min('16:07')])).toEqual({ from: NSE_OPEN, to: MCX_CLOSE, evening: true })
    expect(dayAxis([min('09:18'), min('16:07')], min('23:55')).to).toBe(min('23:55'))
  })

  it('reads the MCX close from the session answer when it is for the day', () => {
    expect(mcxCloseMinute('2026-09-28T18:25:00Z', START)).toBe(min('23:55'))
    expect(mcxCloseMinute('2026-09-29T18:00:00Z', START)).toBe(MCX_CLOSE)
    expect(mcxCloseMinute(null, START)).toBe(MCX_CLOSE)
  })
})

describe('timeTicks', () => {
  it('names the session ends and a few whole hours between', () => {
    expect(timeTicks({ from: NSE_OPEN, to: NSE_CLOSE }).map(minuteLabel)).toEqual(['09:15', '12:00', '14:00', '15:30'])
    expect(timeTicks({ from: NSE_OPEN, to: NSE_CLOSE }, 4).map(minuteLabel)).toEqual(['09:15', '12:00', '15:30'])
  })

  it('names the NSE close on an evening axis, and keeps hours clear of it', () => {
    expect(timeTicks({ from: NSE_OPEN, to: MCX_CLOSE }).map(minuteLabel)).toEqual(['09:15', '12:00', '15:30', '20:00', '23:30'])
  })

  it('does not crowd 09:15 against a 09:00 start', () => {
    expect(timeTicks({ from: min('09:00'), to: NSE_CLOSE }).map(minuteLabel)).toEqual(['09:00', '10:00', '12:00', '14:00', '15:30'])
  })
})

describe('value scale', () => {
  it('keeps zero inside and a flat day at least ₹1,000 tall', () => {
    const flat = valueDomain([0, 0])
    expect(flat.lo).toBeLessThan(0)
    expect(flat.hi).toBeGreaterThan(0)
    expect(flat.hi - flat.lo).toBeGreaterThanOrEqual(1000)
    const down = valueDomain([-100, -17_400, -9_000])
    expect(down.hi).toBeGreaterThan(0)
    expect(down.lo).toBeLessThan(-17_400)
  })

  it('puts gridlines on a round step, zero among them', () => {
    const ticks = valueTicks({ lo: -18_800, hi: 1_400 })
    expect(ticks).toEqual([-15_000, -10_000, -5_000, 0])
    expect(valueTicks({ lo: -580, hi: 580 })).toContain(0)
  })
})

describe('axisInr', () => {
  it('names a gridline as short as it reads', () => {
    expect([0, 500, -50_000, 20_000, -100_000, 150_000, -2_500].map(axisInr)).toEqual(['0', '500', '−50k', '20k', '−1L', '1.5L', '−2.5k'])
  })
})

describe('spreadLabels', () => {
  it('pushes labels apart, keeping their order', () => {
    expect(spreadLabels([50, 52, 120], 12)).toEqual([50, 62, 120])
    expect(spreadLabels([52, 50], 12)).toEqual([62, 50])
  })

  it('keeps them inside the plot, pushing up from the bottom', () => {
    expect(spreadLabels([95, 96], 12, 0, 100)).toEqual([88, 100])
    expect(spreadLabels([2, 3], 12, 0, 100)).toEqual([2, 14])
  })
})

describe('stepPath', () => {
  it('holds each value until the next point', () => {
    expect(stepPath([{ m: 0, v: 0 }, { m: 10, v: 5 }], (m) => m, (v) => -v)).toBe('M0.0 0.0H10.0V-5.0')
    expect(stepPath([], (m) => m, (v) => v)).toBe('')
  })
})

describe('gapText', () => {
  it('names each stretch', () => {
    expect(gapText([{ from: min('11:02'), to: min('11:40') }, { from: min('14:00'), to: min('14:05') }])).toBe('11:02–11:40, 14:00–14:05')
  })
})

describe('dayCurves', () => {
  const res = {
    date: DAY,
    dayStartUtc: '2026-09-27T18:30:00Z',
    runs: [
      run({ userId: 1, minutes: [...span('09:18', '11:02'), ...span('11:40', '15:31')] }),
      run({ userId: 2, runId: 2, userName: 'coderforchange', minutes: [...span('09:18', '11:02'), ...span('11:40', '15:31')] }),
    ],
    accounts: [
      account(2, [...span('09:18', '11:02'), ...span('11:40', '15:31')], (i) => i),
      account(1, [...span('09:18', '11:02'), ...span('11:40', '15:31')]),
    ],
  }

  it('draws the accounts asked for, in that order, cut at the gap', () => {
    const c = dayCurves(res, { userIds: [1, 2], nowMs: Date.parse(at('16:07')), isToday: true })
    expect(c.accounts.map((a) => a.userId)).toEqual([1, 2])
    expect(c.accounts[0].segments).toHaveLength(2)
    expect(c.accounts[0].last).toEqual({ m: min('15:31'), v: -(c.accounts[0].points.length - 1) * 5 })
    expect(c.gaps).toEqual([{ from: min('11:02'), to: min('11:40') }])
    expect(c.axis).toEqual({ from: NSE_OPEN, to: NSE_CLOSE, evening: false })
    expect(c.any).toBe(true)
    // 16:07 is past this axis: no "now" on it.
    expect(c.now).toBeNull()
  })

  it('marks now inside the axis, today only', () => {
    expect(dayCurves(res, { userIds: [1], nowMs: Date.parse(at('11:42')), isToday: true }).now).toBe(min('11:42'))
    expect(dayCurves(res, { userIds: [1], nowMs: Date.parse(at('11:42')), isToday: false }).now).toBeNull()
  })

  it('narrows to one account, and says when there is nothing to draw', () => {
    expect(dayCurves(res, { userIds: [2], nowMs: 0, isToday: false }).accounts.map((a) => a.userId)).toEqual([2])
    const empty = dayCurves({ ...res, runs: [], accounts: [] }, { userIds: [1, 2], nowMs: 0, isToday: false })
    expect(empty.any).toBe(false)
    expect(empty.accounts).toEqual([])
    expect(empty.axis).toEqual({ from: NSE_OPEN, to: NSE_CLOSE, evening: false })
  })
})
