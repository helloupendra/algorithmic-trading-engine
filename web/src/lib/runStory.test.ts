import { describe, expect, it } from 'vitest'

import {
  adrFrom,
  candleStart,
  chartTime,
  engineStatus,
  exitPlan,
  foldSession,
  istHm,
  mergeLevels,
  previousClose,
  tradeAt,
  tradeMarks,
  trendRiderEntries,
  v2Check,
  type Bar,
  type TrendRiderEntry,
} from './runStory'
import type { LiveActivity, LivePosition } from './types'

/** A bar at an IST clock on 17 Nov 2026. */
function at(hm: string, o: number, h: number, l: number, c: number): Bar {
  return { t: Date.parse(`2026-11-17T${hm}:00+05:30`), o, h, l, c }
}

// NIFTY TrendRiderV2 on 17 Nov 2026 (run 981): the 09:20 OPEN_GROUP as the runner logged it.
const OPEN: LiveActivity = {
  atUtc: '2026-11-17T03:50:00Z',
  type: 'OPEN_GROUP',
  text: 'Trend Rider V2: NIFTY 25344.8 closed above yesterday\'s high (25291.1) and the day\'s open 25282; buying the call',
  groupId: 'TR2_20261117T035000_001',
  metadata: {
    direction: 'BUY',
    trend_rider: {
      levels: 'prev_day',
      prev_high: 25291.1,
      prev_low: 25172.1,
      day_open: 25282.05,
      adr: 291.5,
      entry_of_day: 1,
      entry_spot: 25344.75,
      index_exits: { stop_adr: 0.5, trail_arm_adr: 0.5, trail_giveback_adr: 0.35 },
    },
  },
}

describe('foldSession', () => {
  it('folds 1-minute bars into 5-minute candles on the 09:15 grid and drops pre-open prints', () => {
    const bars = [
      at('09:08', 1, 1, 1, 1),
      at('09:15', 100, 105, 99, 104),
      at('09:16', 104, 110, 103, 108),
      at('09:19', 108, 109, 101, 102),
      at('09:20', 102, 103, 100, 101),
    ]
    const out = foldSession(bars, '2026-11-17', 5)
    expect(out.map((b) => istHm(b.t))).toEqual(['09:15', '09:20'])
    expect(out[0]).toMatchObject({ o: 100, h: 110, l: 99, c: 102 })
  })

  it('keeps only the asked day', () => {
    const other = { ...at('10:00', 5, 5, 5, 5), t: Date.parse('2026-11-16T10:00:00+05:30') }
    expect(foldSession([other, at('10:00', 1, 2, 0, 1)], '2026-11-17', 1)).toHaveLength(1)
  })
})

describe('candleStart / chartTime', () => {
  it('finds the 5-minute candle an instant falls in, and shifts chart time to IST', () => {
    const ms = Date.parse('2026-11-17T09:23:41+05:30')
    expect(istHm(candleStart(ms, 5))).toBe('09:20')
    expect(chartTime(Date.parse('2026-11-17T03:50:00Z'))).toBe(Date.parse('2026-11-17T09:20:00Z') / 1000)
  })
})

describe('trendRiderEntries', () => {
  it('reads the levels and the index exits from the OPEN_GROUP signal', () => {
    const [e] = trendRiderEntries([OPEN])
    expect(e).toMatchObject({
      groupId: 'TR2_20261117T035000_001',
      direction: 'BUY',
      entrySpot: 25344.75,
      levels: { prevHigh: 25291.1, prevLow: 25172.1, dayOpen: 25282.05, adr: 291.5 },
      stopAdr: 0.5,
      armAdr: 0.5,
      giveAdr: 0.35,
      levelsKind: 'prev_day',
    })
  })

  it('reads metadata sent as JSON text, and skips signals of other strategies', () => {
    const asText: LiveActivity = { ...OPEN, metadata: null, metadataJson: JSON.stringify(OPEN.metadata) }
    const ghost: LiveActivity = { ...OPEN, groupId: 'GTC_1', metadata: { direction: 'SELL' } }
    expect(trendRiderEntries([asText, ghost]).map((e) => e.groupId)).toEqual(['TR2_20261117T035000_001'])
  })
})

describe('exitPlan', () => {
  const entry = trendRiderEntries([OPEN])[0] as TrendRiderEntry

  it('is the stop alone until the index is 0.5 x ADR in favour', () => {
    const p = exitPlan(entry, [at('09:25', 25340, 25400, 25330, 25390)], 25390)!
    expect(p.armed).toBe(false)
    expect(p.stop).toBeCloseTo(25344.75 - 145.75, 6)
    expect(p.armAt).toBeCloseTo(25344.75 + 145.75, 6)
    expect(p.activeKind).toBe('stop')
  })

  it('17 Nov 2026: best 25,501.3 at 12:13 arms the trail, which sells at ~25,399.27; the spot 25,413.4 is 14 points clear', () => {
    const p = exitPlan(entry, [at('12:10', 25480, 25501.3, 25470, 25490)], 25413.4)!
    expect(p.armed).toBe(true)
    expect(p.best).toBeCloseTo(156.55, 6)
    expect(p.trail).toBeCloseTo(25501.3 - 0.35 * 291.5, 6)
    expect(p.activeKind).toBe('trail')
    expect(p.room).toBeCloseTo(25413.4 - (25501.3 - 102.025), 6)
  })

  it('mirrors for a put', () => {
    const put = { ...entry, direction: 'SELL' as const, entrySpot: 25000 }
    const p = exitPlan(put, [at('10:00', 24900, 24910, 24800, 24820)], 24850)!
    expect(p.best).toBe(200)
    expect(p.stop).toBeCloseTo(25145.75, 6)
    expect(p.trail).toBeCloseTo(24800 + 102.025, 6)
    expect(p.room).toBeCloseTo(24902.025 - 24850, 6)
  })

  it('needs an entry spot and an ADR', () => {
    expect(exitPlan({ ...entry, entrySpot: null }, [], 1)).toBeNull()
    expect(exitPlan({ ...entry, levels: { ...entry.levels, adr: null } }, [], 1)).toBeNull()
  })
})

describe('v2Check', () => {
  it('buys a close above yesterday\'s high and the open, sells one below the low and the open', () => {
    const levels = { prevHigh: 25291.1, prevLow: 25172.1, dayOpen: 25282.05, adr: 291.5 }
    expect(v2Check(levels, 25344.75).side).toBe('BUY')
    expect(v2Check(levels, 25150).side).toBe('SELL')
    expect(v2Check(levels, 25285).side).toBeNull()
  })
})

describe('tradeMarks', () => {
  const position: LivePosition = {
    id: 7,
    groupId: 'TR2_20261117T035000_001',
    symbol: 'NSE:NIFTY26N1725300CE',
    contract: { underlying: 'NIFTY', strike: 25300, optionType: 'CE', expiryDate: '2026-11-17', label: 'NIFTY 25300 CE · 17 Nov' },
    side: 'BUY',
    lots: 2,
    lotSize: 65,
    quantity: 130,
    status: 'Open',
    entryPrice: 93.35,
    exitPrice: null,
    ltp: 121.95,
    ltpUpdatedUtc: '2026-11-17T07:36:04Z',
    pnl: 3718,
    openedUtc: '2026-11-17T03:50:00Z',
    closedUtc: null,
    entryValue: 93.35 * 130,
  } as LivePosition

  it('joins a position to its signal: entry spot, reasons, open state and P&L %', () => {
    const [m] = tradeMarks([position], [OPEN], [at('09:15', 25282, 25350, 25280, 25344.75)])
    expect(m).toMatchObject({ open: true, label: 'NIFTY 25300 CE · 17 Nov', indexAtEntry: 25344.75, manual: false, positionIds: [7] })
    expect(m.pnlPercent).toBeCloseTo((3718 / (93.35 * 130)) * 100, 6)
  })

  it('flags a position booked by hand', () => {
    const manual: LiveActivity = { ...OPEN, metadata: { ...OPEN.metadata, manual_backfill: true } }
    expect(tradeMarks([position], [manual], [])[0].manual).toBe(true)
  })
})

describe('tradeAt (the option in a replay)', () => {
  // The real trade: bought 09:20 at 93.35, 2 lots (130), sold by the trail 13:07:37 at 115.75.
  const closed: LivePosition = {
    id: 7,
    groupId: 'TR2_20261117T035000_001',
    symbol: 'NSE:NIFTY26N1725300CE',
    contract: { underlying: 'NIFTY', strike: 25300, optionType: 'CE', expiryDate: '2026-11-17', label: 'NIFTY 25300 CE · 17 Nov' },
    side: 'BUY',
    lots: 2,
    lotSize: 65,
    quantity: 130,
    status: 'Closed',
    entryPrice: 93.35,
    exitPrice: 115.75,
    ltp: 165.25,
    ltpUpdatedUtc: '2026-11-17T09:00:00Z',
    pnl: 2912,
    openedUtc: '2026-11-17T03:50:00Z',
    closedUtc: '2026-11-17T07:37:37Z',
    entryValue: 93.35 * 130,
  } as LivePosition
  const [trade] = tradeMarks([closed], [], [])
  const option = [at('09:19', 90, 92, 89, 91), at('09:20', 93, 96, 92, 95), at('12:00', 200, 215, 199, 210), at('13:10', 82, 86, 69.35, 82.6)]
  const clock = (hm: string) => Date.parse(`2026-11-17T${hm}:00+05:30`)

  it('before the entry: nothing at stake, the option still priced', () => {
    expect(tradeAt(trade, option, clock('09:20'))).toEqual({ bought: false, sold: false, price: 91, pnl: 0, pnlPercent: null })
  })

  it('while held: the last 1-minute close against the entry', () => {
    const noon = tradeAt(trade, option, clock('12:01'))
    expect(noon).toMatchObject({ bought: true, sold: false, price: 210 })
    expect(noon.pnl).toBeCloseTo((210 - 93.35) * 130, 6)
    expect(noon.pnlPercent).toBeCloseTo(((210 - 93.35) / 93.35) * 100, 6)
  })

  it('once sold: the booked P&L, whatever the option did after', () => {
    expect(tradeAt(trade, option, clock('13:11'))).toMatchObject({ bought: true, sold: true, price: 115.75, pnl: 2912 })
  })

  it('out of a replay: the trade as it stands', () => {
    expect(tradeAt(trade, option, null)).toMatchObject({ bought: true, sold: true, price: 115.75, pnl: 2912 })
  })

  it('a sold-to-open leg gains when the option falls', () => {
    const sold = tradeAt({ ...trade, side: 'SELL', open: true, exitMs: null, exitPrice: null }, option, clock('13:11'))
    expect(sold.pnl).toBeCloseTo((93.35 - 82.6) * 130, 6)
  })
})

describe('engineStatus', () => {
  const noon = Date.parse('2026-11-17T12:00:00+05:30')
  it('says what the strategy is doing', () => {
    expect(engineStatus(false, [], 0, noon).label).toBe('Stopped')
    expect(engineStatus(true, [], 0, noon).label).toBe('Waiting for signal')
    expect(engineStatus(true, [], 0, Date.parse('2026-11-17T14:45:00+05:30')).label).toBe('No new entries today')
    const entry = trendRiderEntries([OPEN])[0]
    const armed = exitPlan(entry, [at('12:10', 25480, 25501.3, 25470, 25490)], 25413.4)!
    expect(engineStatus(true, [armed], 1, noon)).toMatchObject({ label: 'Trailing active', detail: 'sells below 25399.3' })
  })

  it('a position booked by hand does not put the strategy in a trade', () => {
    expect(engineStatus(true, [], 0, noon, '14:30', 1)).toEqual({
      label: 'Waiting for signal',
      tone: 'live',
      detail: '1 booked by hand, not managed',
    })
  })
})

describe('adrFrom', () => {
  it('averages the last sessions\' ranges before the day', () => {
    const d = (day: string, h: number, l: number): Bar => ({ t: Date.parse(`${day}T00:00:00Z`), o: l, h, l, c: h })
    expect(adrFrom([d('2026-11-13', 110, 100), d('2026-11-16', 130, 100), d('2026-11-17', 999, 0)], '2026-11-17', 10)).toBe(20)
    expect(adrFrom([], '2026-11-17')).toBeNull()
  })
})

describe('mergeLevels', () => {
  it('joins levels that would print on top of each other, keeps the rest', () => {
    const out = mergeLevels([
      { price: 25291.1, title: 'Yday high', kind: 'prev' },
      { price: 25282.05, title: 'Day open', kind: 'open' },
      { price: 25172.1, title: 'Yday low', kind: 'prev' },
    ])
    expect(out.map((l) => l.title)).toEqual(['Yday low', 'Day open · Yday high'])
    expect(out[1].kind).toBe('prev')
  })
})

describe('previousClose', () => {
  it('is the last daily close before the day', () => {
    const d = (day: string, c: number): Bar => ({ t: Date.parse(`${day}T00:00:00Z`), o: c, h: c, l: c, c })
    expect(previousClose([d('2026-11-13', 1), d('2026-11-16', 2), d('2026-11-17', 3)], '2026-11-17')).toBe(2)
  })
})
