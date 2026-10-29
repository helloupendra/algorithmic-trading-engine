import { describe, expect, it } from 'vitest'
import {
  NO_COMPARE,
  compareWith,
  dayLabel,
  daysBefore,
  expiryDaysBefore,
  istMinuteOfDay,
  lastIv,
  pricingNote,
  sessionLine,
  standingBasis,
  straddleChange,
  straddleFreshness,
  toggleCompare,
} from './straddle'
import type { IvHistoryDay, StraddlePoint, StraddleSeries } from './straddle'

/** 09:15 IST on 29 Oct 2026. */
const OPEN = '2026-10-29T03:45:00Z'

function point(minutesAfterOpen: number, straddle: number | null, iv: number | null = 13): StraddlePoint {
  return {
    capturedUtc: new Date(Date.parse(OPEN) + minutesAfterOpen * 60_000).toISOString(),
    spotPrice: 24_510,
    atTheMoneyStrike: 24_500,
    callPrice: straddle == null ? null : straddle / 2,
    callPriceBasis: straddle == null ? null : 'mid',
    putPrice: straddle == null ? null : straddle / 2,
    putPriceBasis: straddle == null ? null : 'mid',
    straddlePrice: straddle,
    atTheMoneyIv: iv,
  }
}

function day(date: string, expiryDate: string): IvHistoryDay {
  return { date, expiryDate, daysToExpiry: 0, openIv: 13, openCapturedUtc: null, closeIv: 12, closeCapturedUtc: null, source: 'dhan' }
}

function series(overrides: Partial<StraddleSeries> = {}): StraddleSeries {
  return {
    underlying: 'NIFTY',
    date: '2026-10-29',
    expiryDate: '2026-11-03',
    daysToExpiry: 5,
    isToday: true,
    marketOpen: true,
    serverUtc: '2026-10-29T05:00:00Z',
    firstCapturedUtc: OPEN,
    lastCapturedUtc: '2026-10-29T04:59:20Z',
    sources: ['dhan'],
    midPricedPoints: 2,
    lotSize: 65,
    points: [point(0, 300), point(1, 290)],
    ...overrides,
  }
}

describe('the days laid over the chosen one', () => {
  it('fills the first free slot and frees a slot without moving the others', () => {
    let slots = toggleCompare(NO_COMPARE, '2026-10-27')
    slots = toggleCompare(slots, '2026-10-28')
    slots = toggleCompare(slots, '2026-10-20')
    expect(slots).toEqual(['2026-10-27', '2026-10-28', '2026-10-20'])

    // Taking the first off must not restyle the other two.
    slots = toggleCompare(slots, '2026-10-27')
    expect(slots).toEqual([null, '2026-10-28', '2026-10-20'])
    expect(toggleCompare(slots, '2026-10-13')).toEqual(['2026-10-13', '2026-10-28', '2026-10-20'])
  })

  it('leaves a full set alone rather than dropping a day the reader chose', () => {
    const full = compareWith(['2026-10-27', '2026-10-28', '2026-10-20'])
    expect(toggleCompare(full, '2026-10-13')).toBe(full)
  })

  it('takes a whole set in order, at most three', () => {
    expect(compareWith(['2026-10-27'])).toEqual(['2026-10-27', null, null])
    expect(compareWith(['a', 'b', 'c', 'd'])).toEqual(['a', 'b', 'c'])
  })

  const days = [
    day('2026-10-19', '2026-10-20'),
    day('2026-10-20', '2026-10-20'),
    day('2026-10-21', '2026-10-27'),
    day('2026-10-26', '2026-10-27'),
    day('2026-10-27', '2026-10-27'),
    day('2026-10-28', '2026-11-03'),
    day('2026-10-29', '2026-11-03'),
  ]

  it('names the sessions before the chosen one, newest first', () => {
    expect(daysBefore(days, '2026-10-29', 3)).toEqual(['2026-10-28', '2026-10-27', '2026-10-26'])
    expect(daysBefore(days, '2026-10-19', 3)).toEqual([])
  })

  it('finds the last expiry days before the chosen one: sessions whose contract expired that day', () => {
    expect(expiryDaysBefore(days, '2026-10-29', 3)).toEqual(['2026-10-27', '2026-10-20'])
    // The chosen day itself is never compared with itself.
    expect(expiryDaysBefore(days, '2026-10-27', 3)).toEqual(['2026-10-20'])
  })
})

describe('a session as a line', () => {
  it('is placed by the minute of the IST day, so days sit over each other', () => {
    expect(istMinuteOfDay(OPEN)).toBe(9 * 60 + 15)
    expect(istMinuteOfDay('2026-10-20T03:45:59Z')).toBe(9 * 60 + 15)
    expect(istMinuteOfDay('2026-10-29T18:29:00Z')).toBe(23 * 60 + 59)
  })

  it('can read the straddle in rupees or as a share of its first priced capture', () => {
    const points = [point(0, null), point(1, 400), point(2, 300), point(3, null)]

    expect(sessionLine(points, 'straddle').map((p) => p.value)).toEqual([null, 400, 300, null])
    expect(sessionLine(points, 'straddle', 'percentOfFirst').map((p) => p.value)).toEqual([null, 100, 75, null])
    expect(sessionLine(points, 'straddle', 'percentOfFirst').map((p) => p.minute)).toEqual([555, 556, 557, 558])
  })

  it('draws nothing as a share when nothing was priced, and IV is never rescaled', () => {
    expect(sessionLine([point(0, null)], 'straddle', 'percentOfFirst')).toEqual([{ minute: 555, value: null }])
    expect(sessionLine([point(0, 400, 14.5)], 'iv', 'percentOfFirst')).toEqual([{ minute: 555, value: 14.5 }])
  })

  it('measures the change from the first priced straddle to the last', () => {
    const change = straddleChange([point(0, null), point(1, 400), point(2, 300), point(3, null)])!
    expect(change.first.straddlePrice).toBe(400)
    expect(change.last.straddlePrice).toBe(300)
    expect(change.percent).toBeCloseTo(-25)
    expect(straddleChange([point(0, null)])).toBeNull()
  })

  it('finds the newest point that has an IV', () => {
    expect(lastIv([point(0, 300, 13), point(1, 290, 12.5), point(2, 280, null)])?.atTheMoneyIv).toBe(12.5)
    expect(lastIv([point(0, 300, null)])).toBeNull()
  })
})

describe('what the numbers are measured over', () => {
  it('says how few days an IV rank is over until there is a year of them', () => {
    expect(standingBasis({ currentIv: 13, daysUsed: 31, ivRank: 42 })).toBe('over 31 days only — a year is about 250')
    expect(standingBasis({ currentIv: 13, daysUsed: 1, ivRank: 100 })).toBe('over 1 day only — a year is about 250')
    expect(standingBasis({ currentIv: 13, daysUsed: 251, ivRank: 42 })).toBe('over the last year (251 sessions)')
    expect(standingBasis({ currentIv: 13, daysUsed: 0, ivRank: null })).toBe('no earlier session to rank against yet')
    expect(standingBasis({ currentIv: null, daysUsed: 0, ivRank: null })).toBe('no ATM IV recorded yet')
  })

  it('says how the legs were priced, and how many minutes fell back to the last trade', () => {
    expect(pricingNote(series())).toBe('Both legs at the mid of bid and ask in every minute (2).')
    expect(pricingNote(series({ midPricedPoints: 1 }))).toBe(
      'Both legs at the mid of bid and ask in 1 of 2 minutes; in 1, at least one leg at its last trade (no two-sided quote).',
    )
    expect(pricingNote(series({ points: [point(0, null)], midPricedPoints: 0 }))).toBeNull()
  })

  it('names a session the way the picker does', () => {
    expect(dayLabel('2026-10-27')).toBe('Tue 27 Oct')
    expect(dayLabel('2026-11-01')).toBe('Sun 1 Nov')
  })
})

describe('how old the straddle is', () => {
  const now = Date.parse('2026-10-29T05:00:00Z')

  it('reads as recording while the captures keep coming', () => {
    const f = straddleFreshness(series(), now)
    expect(f.tone).toBe('snapshot')
    expect(f.text).toBe('recording · last capture 10:29 IST (40 s old) · Dhan · one capture a minute')
  })

  it('says the recorder may be behind once a capture is older than three minutes in the session', () => {
    const f = straddleFreshness(series({ lastCapturedUtc: '2026-10-29T04:50:00Z' }), now)
    expect(f.tone).toBe('stale')
    expect(f.text).toContain('10 min old')
  })

  it('says the market is closed rather than that the numbers are stale', () => {
    const f = straddleFreshness(series({ marketOpen: false, lastCapturedUtc: '2026-10-29T09:59:00Z' }), now)
    expect(f).toEqual({ tone: 'closed', text: 'market closed — last capture 29 Oct 15:29 IST · Dhan' })
  })

  it('calls a past day closed, with its captures and sources', () => {
    const f = straddleFreshness(series({ isToday: false, marketOpen: false, date: '2026-10-27', sources: ['dhan', 'fyers'] }), now)
    expect(f.text).toBe('Tue 27 Oct · a closed session · 2 captures, last 10:29 IST · Dhan + FYERS')
  })

  it('says nothing was recorded rather than drawing an empty chart', () => {
    expect(straddleFreshness(series({ lastCapturedUtc: null, points: [] }), now)).toEqual({
      tone: 'empty',
      text: 'no capture recorded for NIFTY today yet',
    })
  })
})
