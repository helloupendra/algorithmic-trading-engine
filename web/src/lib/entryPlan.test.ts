import { describe, expect, it } from 'vitest'

import { entryPlan, parseParams, trendRiderRules } from './entryPlan'
import type { Bar } from './runStory'

/** One session of 5-minute candles (09:15–15:25) on an IST day: opens at o, closes at c, reaches h and l. */
function session(day: string, o: number, h: number, l: number, c: number, bars = 75): Bar[] {
  const out: Bar[] = []
  for (let k = 0; k < bars; k++) {
    const t = Date.parse(`${day}T09:15:00+05:30`) + k * 5 * 60_000
    const p = k === 0 ? o : k === bars - 1 ? c : (o + c) / 2
    out.push({ t, o: p, h: k === 10 ? h : p, l: k === 20 ? l : p, c: p })
  }
  return out
}

/** Today's closed 5-minute candles from 09:15, as [open, high, low, close]. */
function today(day: string, rows: Array<[number, number, number, number]>): Bar[] {
  return rows.map(([o, h, l, c], k) => ({ t: Date.parse(`${day}T09:15:00+05:30`) + k * 5 * 60_000, o, h, l, c }))
}

const ist = (day: string, hm: string) => Date.parse(`${day}T${hm}:00+05:30`)

// The catalog's defaults (GET /api/Strategy, 25 Nov 2026).
const V1 = trendRiderRules('TrendRider', {
  itm_steps: 1, or_minutes: 30, first_entry: '09:45', last_entry: '14:30', max_entries: 3, adr_days: 10,
  skip_after_wide: 1.3, min_range_frac: 0.4, max_gap_against_pct: 0.2, max_prev_range: 0.7, timeframe: '5m',
})!
const V2BE = trendRiderRules('TrendRiderV2BE', {
  or_minutes: 30, first_entry: 'auto', last_entry: 'auto', max_entries: 3, adr_days: 10, skip_after_wide: 1.3,
  min_range_frac: 0.0, max_gap_against_pct: 0.2, max_prev_range: 0.0, timeframe: '5m', levels: 'prev_day',
  market: 'auto', instrument_kind: 'options',
})!

/** Ten ordinary sessions of a 300-point range, then `yesterday`. */
function history(yesterday: Bar[]): Bar[] {
  const days = ['2026-11-09', '2026-11-11', '2026-11-12', '2026-11-13', '2026-11-16', '2026-11-17', '2026-11-18', '2026-11-19', '2026-11-20']
  const out = days.flatMap((d) => session(d, 25_100, 25_300, 25_000, 25_200))
  // The 8 Nov Muhurat hour: 14 candles, never in the average.
  out.unshift(...session('2026-11-08', 25_070, 25_131, 24_985, 24_986, 14))
  out.unshift(...session('2026-11-06', 24_500, 24_800, 24_500, 24_700))
  return [...out, ...yesterday]
}

describe('trendRiderRules', () => {
  it('reads TrendRider as an opening-range strategy from 09:45 and V2 BE as yesterday-levels from 09:15', () => {
    expect(V1).toMatchObject({ levels: 'opening_range', firstEntry: '09:45', lastEntry: '14:30', maxPrevRange: 0.7, minRangeFrac: 0.4 })
    expect(V2BE).toMatchObject({ levels: 'prev_day', firstEntry: '09:15', lastEntry: '14:30', maxPrevRange: 0, minRangeFrac: 0 })
  })

  it('declines what it cannot mirror: other strategies, other timeframes, MCX, shares', () => {
    expect(trendRiderRules('Ghost', { timeframe: '5m' })).toBeNull()
    expect(trendRiderRules('TrendRider', { timeframe: '15m' })).toBeNull()
    expect(trendRiderRules('TrendRiderV2BE', { levels: 'prev_day' }, { mcx: true })).toBeNull()
    expect(trendRiderRules('TrendRiderV2BE', { levels: 'prev_day', instrument_kind: 'equity' })).toBeNull()
    expect(trendRiderRules('TrendRider', null)).toBeNull()
  })

  it('parses a catalog row only when it is a JSON object', () => {
    expect(parseParams('{"levels":"prev_day"}')).toEqual({ levels: 'prev_day' })
    expect(parseParams('[1]')).toBeNull()
    expect(parseParams('not json')).toBeNull()
    expect(parseParams(null)).toBeNull()
  })
})

describe('entryPlan', () => {
  // 25 Nov 2026: 23 Nov (24 Nov was a holiday) ranged 25,992.0 – 25,344.3, 647.7 points against an ADR near 316;
  // NIFTY opened 25,651.6, 1.2% above 23 Nov's close. No TrendRider version may trade.
  it('keeps every version out the day after a wide day (25 Nov 2026)', () => {
    const hist = history(session('2026-11-23', 25_936.95, 25_991.95, 25_344.3, 25_366.4))
    const t = today('2026-11-25', [[25_651.6, 25_700, 25_619, 25_690]])
    const plan = entryPlan(V2BE, hist, t, '2026-11-25', ist('2026-11-25', '10:00'))
    expect(plan.status).toBe('day_blocked')
    expect(plan.prevDay).toBe('2026-11-23')
    expect(plan.prevRangeAdr).toBeGreaterThan(1.3)
    expect(plan.headline).toMatch(/^No entry today: yesterday \(23 Nov\) moved 647\.7 points/)
    expect(plan.headline).toContain('wider than 1.3 ×')
    // The levels are still stated, so the trader sees what it would have needed.
    expect(plan.call).toEqual({ level: 25_991.95, from: "yesterday's high", off: null, ruledOut: null })
    expect(plan.put.level).toBe(25_344.3)
    expect(plan.put.off).toMatch(/opened 1\.12% up/)
    expect(entryPlan(V1, hist, t, '2026-11-25', ist('2026-11-25', '10:00')).status).toBe('day_blocked')
  })

  it('trades through the day rules when they are off, and says which one it passed (26 Nov on)', () => {
    const off = trendRiderRules('TrendRiderV2BE', { levels: 'prev_day', first_entry: 'auto', last_entry: 'auto', day_filters: 'off' })!
    expect(off.dayFilters).toBe(false)
    const hist = history(session('2026-11-23', 25_936.95, 25_991.95, 25_344.3, 25_366.4))
    const t = today('2026-11-25', [[25_651.6, 25_700, 25_619, 25_690]])
    const plan = entryPlan(off, hist, t, '2026-11-25', ist('2026-11-25', '10:00'))
    expect(plan.status).toBe('watching')
    expect(plan.dayOff).toBeNull()
    expect(plan.ruledOut).toMatch(/^yesterday \(23 Nov\) moved 647\.7 points/)
    expect(plan.call).toEqual({ level: 25_991.95, from: "yesterday's high", off: null, ruledOut: null })
    expect(plan.put.off).toBeNull()
    expect(plan.put.ruledOut).toMatch(/opened 1\.12% up/)
    expect(plan.headline).toBe('Buys on a 5-minute close: call above 25,992.0 or put below 25,344.3 (until 14:35); day rules off.')
    // TrendRider's own range rule is a day rule too: off, it does not wait for the range.
    const v1off = trendRiderRules('TrendRider', { first_entry: '09:45', min_range_frac: 0.4, max_prev_range: 0.7, day_filters: false })!
    expect(entryPlan(v1off, hist, t, '2026-11-25', ist('2026-11-25', '10:00')).rangeNeeded).toBeNull()
  })

  it('watches yesterday\'s high and low from 09:20 on an ordinary day (V2 BE)', () => {
    const hist = history(session('2026-11-23', 25_200, 25_350, 25_100, 25_250))
    const t = today('2026-11-25', [[25_260, 25_290, 25_240, 25_280]])
    const early = entryPlan(V2BE, hist, t, '2026-11-25', ist('2026-11-25', '09:18'))
    expect(early.status).toBe('before_window')
    expect(early.firstChance).toBe('09:20')
    expect(early.lastChance).toBe('14:35')
    const plan = entryPlan(V2BE, hist, t, '2026-11-25', ist('2026-11-25', '09:31'))
    expect(plan.status).toBe('watching')
    expect(plan.call).toEqual({ level: 25_350, from: "yesterday's high", off: null, ruledOut: null })
    expect(plan.put).toEqual({ level: 25_100, from: "yesterday's low", off: null, ruledOut: null })
    expect(plan.headline).toBe('Buys on a 5-minute close: call above 25,350.0 or put below 25,100.0 (until 14:35).')
    expect(entryPlan(V2BE, hist, t, '2026-11-25', ist('2026-11-25', '14:36')).status).toBe('window_closed')
    expect(entryPlan(V2BE, hist, t, '2026-11-25', ist('2026-11-25', '11:00'), 3).status).toBe('max_entries')
  })

  it('takes the day\'s open as the level when it is beyond yesterday\'s high, and turns a side off on a gap against it', () => {
    const hist = history(session('2026-11-23', 25_200, 25_350, 25_100, 25_250))
    // Opened 0.6% up, above yesterday's high: the call needs a close above the open; the put is off.
    const t = today('2026-11-25', [[25_400, 25_420, 25_380, 25_410]])
    const plan = entryPlan(V2BE, hist, t, '2026-11-25', ist('2026-11-25', '09:40'))
    expect(plan.call).toEqual({ level: 25_400, from: "the day's open", off: null, ruledOut: null })
    expect(plan.put.off).toMatch(/more than 0\.2% against this side/)
    expect(plan.headline).toBe('Buys on a 5-minute close: call above 25,400.0 (until 14:35).')
  })

  it('reads TrendRider\'s opening range once its six candles have closed, and needs a narrow yesterday', () => {
    // Yesterday 180 points = 0.6 × ADR (300): narrow enough for TrendRider's 0.7.
    const hist = history(session('2026-11-23', 25_200, 25_300, 25_120, 25_250))
    const forming = today('2026-11-25', [[25_260, 25_290, 25_240, 25_280]])
    const before = entryPlan(V1, hist, forming, '2026-11-25', ist('2026-11-25', '09:25'))
    expect(before.status).toBe('before_window')
    expect(before.firstChance).toBe('09:50')
    expect(before.headline).toMatch(/^Opening range forming until 09:45/)
    const six = today('2026-11-25', [
      [25_260, 25_290, 25_240, 25_280], [25_280, 25_300, 25_270, 25_290], [25_290, 25_310, 25_280, 25_300],
      [25_300, 25_305, 25_250, 25_260], [25_260, 25_270, 25_230, 25_240], [25_240, 25_275, 25_235, 25_270],
    ])
    const plan = entryPlan(V1, hist, six, '2026-11-25', ist('2026-11-25', '09:51'))
    expect(plan.status).toBe('watching')
    expect(plan.orHigh).toBe(25_310)
    expect(plan.orLow).toBe(25_230)
    expect(plan.call).toMatchObject({ level: 25_310, from: 'the opening range high' })
    expect(plan.put).toMatchObject({ level: 25_230, from: 'the opening range low' })
    expect(plan.rangeNeeded).toBeCloseTo(115.2)
    expect(plan.rangeNow).toBe(80)
    // 210 points yesterday = 0.72 × ADR: not narrower than 0.7, so TrendRider sits out (V2 has the rule off).
    const notNarrow = history(session('2026-11-23', 25_200, 25_330, 25_120, 25_250))
    expect(entryPlan(V1, notNarrow, six, '2026-11-25', ist('2026-11-25', '09:51')).headline).toContain('narrower than 0.7 ×')
    expect(entryPlan(V2BE, notNarrow, six, '2026-11-25', ist('2026-11-25', '09:51')).status).toBe('watching')
  })

  it('says it cannot trade without ten complete sessions, and reads yesterday as the strategy does', () => {
    const short = session('2026-11-20', 25_100, 25_300, 25_000, 25_200)
    expect(entryPlan(V2BE, short, [], '2026-11-25', ist('2026-11-25', '10:00')).status).toBe('no_history')
    // On 9 Nov the strategy's yesterday is the 8 Nov Muhurat hour (its last session with candles).
    const hist = history([]).filter((b) => b.t < Date.parse('2026-11-09T00:00:00+05:30'))
    const plan = entryPlan(V2BE, hist, [], '2026-11-09', ist('2026-11-09', '10:00'))
    expect(plan.prevDay).toBe('2026-11-08')
  })
})
