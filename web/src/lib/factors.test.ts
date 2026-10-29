import { describe, expect, it } from 'vitest'
import {
  cashStreak,
  cashTrend,
  distanceTo,
  expectedMove,
  formatCrore,
  formatDay,
  formatDistance,
  formatSignedContracts,
  futuresTrend,
  gapReading,
  istDate,
  movedFactorSection,
  optionsNetScale,
  optionsPositions,
  participantStance,
  sharedBarRange,
  splitEvents,
  topWalls,
  valueRange,
} from './factors'
import type { CashDay, MarketEvent, ParticipantDay, ParticipantPosition } from './factors'
import type { OptionChain } from './types'

const fii: ParticipantPosition = {
  clientType: 'FII',
  futureIndexLong: 47446,
  futureIndexShort: 335549,
  futureIndexNet: -288103,
  futureIndexLongPercent: 12.2,
  futureIndexNetChange: null,
  optionIndexCallLong: 0,
  optionIndexCallShort: 0,
  optionIndexPutLong: 0,
  optionIndexPutShort: 0,
  futureStockLong: 0,
  futureStockShort: 0,
}

describe('factors', () => {
  it('reads IST dates regardless of the browser zone', () => {
    // 19:00 UTC on 17 Sep is 00:30 IST on 18 Sep.
    expect(istDate(new Date('2026-09-17T19:00:00Z'))).toBe('2026-09-18')
    expect(istDate(new Date('2026-09-17T18:00:00Z'))).toBe('2026-09-17')
  })

  it('formats crores and signed contracts the Indian way', () => {
    expect(formatCrore(-3208.76)).toBe('−₹3,209 cr')
    expect(formatCrore(3617.75)).toBe('+₹3,618 cr')
    expect(formatCrore(null)).toBe('—')
    expect(formatSignedContracts(-288103)).toBe('−2.88 L')
    expect(formatSignedContracts(12000)).toBe('+12,000')
  })

  it('calls a participant group long, short or balanced from its long share', () => {
    expect(participantStance(fii)).toEqual({ label: 'Net short', tone: 'neg' })
    expect(participantStance({ ...fii, futureIndexLong: 300000, futureIndexShort: 100000 }).label).toBe('Net long')
    expect(participantStance({ ...fii, futureIndexLong: 100, futureIndexShort: 100 }).label).toBe('Balanced')
    expect(participantStance({ ...fii, futureIndexLong: 0, futureIndexShort: 0 }).label).toBe('No positions')
  })

  it('measures the distance to a level from the spot', () => {
    const d = distanceTo(23270, 23500)
    expect(d?.points).toBe(230)
    expect(formatDistance(d)).toBe('+230 pts (+0.99%)')
    expect(formatDistance(distanceTo(23270, 23000))).toBe('−270 pts (−1.16%)')
    expect(distanceTo(null, 23000)).toBeNull()
  })

  it('prices the expected move from the ATM straddle', () => {
    const chain = {
      spotPrice: 23270,
      strikes: [
        { strikePrice: 23200, isAtTheMoney: false, call: { lastTradedPrice: 150 }, put: { lastTradedPrice: 70 } },
        { strikePrice: 23250, isAtTheMoney: true, call: { lastTradedPrice: 120 }, put: { lastTradedPrice: 95 } },
      ],
    } as unknown as OptionChain
    expect(expectedMove(chain)).toEqual({ points: 215, percent: (215 / 23270) * 100, strike: 23250 })
    const noPut = { ...chain, strikes: [{ ...chain.strikes[1], put: null }] } as unknown as OptionChain
    expect(expectedMove(noPut)).toBeNull()
  })

  it('reads the GIFT Nifty gap with a flat band', () => {
    expect(gapReading(0.35).label).toBe('Points to a gap-up of 0.35%')
    expect(gapReading(-0.22).tone).toBe('neg')
    expect(gapReading(0.05).label).toBe('Points to a flat open')
    expect(gapReading(null).label).toBe('No reading')
  })

  it('splits events into today, upcoming and past, grouped by day', () => {
    const e = (date: string, title: string, timeIst: string | null = null): MarketEvent => ({
      id: null, date, timeIst, region: 'IN', category: 'Other', title, importance: 2, notes: null, source: null, kind: 'event',
    })
    const parts = splitEvents([e('2026-09-22', 'expiry', '15:30'), e('2026-09-17', 'today'), e('2026-09-16', 'fed', '23:30'), e('2026-09-22', 'cpi', '18:00')], '2026-09-17')
    expect(parts.today.map((x) => x.title)).toEqual(['today'])
    expect(parts.upcoming).toHaveLength(1)
    expect(parts.upcoming[0][1].map((x) => x.title)).toEqual(['expiry', 'cpi'])   // 15:30 before 18:00
    expect(parts.past[0][0]).toBe('2026-09-16')
  })

  it('formats a calendar day without shifting it across time zones', () => {
    expect(formatDay('2026-09-17')).toMatch(/17 Sept?/)
  })

  it('sums a cash streak', () => {
    const days = [
      { date: '2026-09-17', fii: { buy: 1, sell: 2, net: -3208.76 }, dii: { buy: 1, sell: 1, net: 3617.75 } },
      { date: '2026-09-16', fii: { buy: 1, sell: 1, net: 500 }, dii: null },
    ]
    expect(cashStreak(days, 'fii')).toEqual({ total: -2708.76, buyingDays: 1, days: 2 })
    expect(cashStreak(days, 'dii').days).toBe(1)
  })
})

describe('topWalls', () => {
  it('ranks strikes by open interest on one side, with their distance from the spot', () => {
    const leg = (oi: number, chg: number | null = null) => ({ openInterest: oi, openInterestChange: chg })
    const chain = {
      spotPrice: 23270,
      strikes: [
        { strikePrice: 23000, isAtTheMoney: false, call: leg(10), put: leg(900, 50) },
        { strikePrice: 23300, isAtTheMoney: true, call: leg(400), put: leg(300) },
        { strikePrice: 23500, isAtTheMoney: false, call: leg(1200, -20), put: leg(5) },
        { strikePrice: 23600, isAtTheMoney: false, call: null, put: leg(0) },
      ],
    } as unknown as OptionChain
    const calls = topWalls(chain, 'call', 2)
    expect(calls.map((w) => w.strike)).toEqual([23500, 23300])
    expect(calls[0].openInterestChange).toBe(-20)
    expect(calls[0].distance?.points).toBe(230)
    expect(topWalls(chain, 'put', 5).map((w) => w.strike)).toEqual([23000, 23300, 23500])
    expect(topWalls(undefined, 'put')).toEqual([])
  })
})

describe('movedFactorSection', () => {
  it('sends an old link to a section that moved on to its new page', () => {
    expect(movedFactorSection('levels')).toBe('/markets/chain/levels')
    expect(movedFactorSection('futures')).toBe('/markets/movers#futures')
  })

  it('leaves the sections still on the Factors page alone', () => {
    for (const s of ['flows', 'global', 'events', null, undefined, 'nonsense']) expect(movedFactorSection(s)).toBeNull()
  })
})

describe('the FII & DII chart series', () => {
  // NSE's file of Fri 25 Sep 2026 and the day before, as the API answers them: newest first.
  const group = (clientType: string, o: Partial<ParticipantPosition>): ParticipantPosition => ({
    ...fii,
    clientType,
    futureIndexNetChange: null,
    ...o,
  })
  const friday: ParticipantDay = {
    date: '2026-09-25',
    groups: [
      group('FII', {
        futureIndexNet: -312001, futureIndexLongPercent: 11.4, futureIndexNetChange: -2179,
        optionIndexCallLong: 857125, optionIndexCallShort: 1098094, optionIndexPutLong: 1401782, optionIndexPutShort: 717999,
      }),
      group('DII', { futureIndexNet: 6571, optionIndexCallLong: 12484, optionIndexCallShort: 2086, optionIndexPutLong: 78667, optionIndexPutShort: 0 }),
    ],
  }
  const thursday: ParticipantDay = {
    date: '2026-09-24',
    groups: [
      group('FII', {
        futureIndexNet: -309822, futureIndexLongPercent: 11.6,
        optionIndexCallLong: 800000, optionIndexCallShort: 1000000, optionIndexPutLong: 1400000, optionIndexPutShort: 700000,
      }),
    ],
  }

  it('lays a group out oldest first, keeping the place of a day without its row', () => {
    const wednesday: ParticipantDay = { date: '2026-09-23', groups: [] }
    expect(futuresTrend([friday, thursday, wednesday])).toEqual([
      { date: '2026-09-23', net: null, longPercent: null, netChange: null },
      { date: '2026-09-24', net: -309822, longPercent: 11.6, netChange: null },
      { date: '2026-09-25', net: -312001, longPercent: 11.4, netChange: -2179 },
    ])
    expect(futuresTrend([friday], 'DII')[0].net).toBe(6571)
  })

  it('lays the cash days out oldest first, with a side NSE did not publish left empty', () => {
    const days: CashDay[] = [
      { date: '2026-09-25', fii: { buy: 12327.36, sell: 16021.29, net: -3693.93 }, dii: { buy: 14035.77, sell: 11197.6, net: 2838.17 } },
      { date: '2026-09-24', fii: { buy: 13111.22, sell: 18138.58, net: -5027.36 }, dii: null },
    ]
    expect(cashTrend(days)).toEqual([
      { date: '2026-09-24', fii: -5027.36, dii: null },
      { date: '2026-09-25', fii: -3693.93, dii: 2838.17 },
    ])
  })

  it('nets each side of the index options, with the change since the day before', () => {
    const [f, d] = optionsPositions(friday, thursday)
    expect(f).toEqual({
      clientType: 'FII',
      callLong: 857125, callShort: 1098094, callNet: -240969, callNetChange: -40969,
      putLong: 1401782, putShort: 717999, putNet: 683783, putNetChange: -16217,
    })
    // Not in the day before's file: no change rather than a change from zero.
    expect(d.callNet).toBe(10398)
    expect(d.callNetChange).toBeNull()
    expect(optionsPositions(undefined)).toEqual([])
  })

  it('scales the options bars to the largest net on either side', () => {
    expect(optionsNetScale(optionsPositions(friday))).toBe(683783)
    expect(optionsNetScale([])).toBe(1)
  })

  it('draws every pane of bars over one range that holds zero', () => {
    expect(sharedBarRange([[-3693.93, -5027.36], [2838.17, null]])).toEqual({ min: -5027.36, max: 2838.17 })
    // All below zero: the range still ends at zero, where the bars stand.
    expect(sharedBarRange([[-300000, -200000]])).toEqual({ min: -300000, max: 0 })
    expect(sharedBarRange([[], [null]])).toEqual({ min: 0, max: 0 })
  })

  it("reads a line's range from the values it has", () => {
    expect(valueRange([11.6, null, 11.4, 12.2])).toEqual({ min: 11.4, max: 12.2 })
    expect(valueRange([null])).toBeNull()
  })
})
