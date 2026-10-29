import { describe, expect, it } from 'vitest'

import {
  INDICATOR_DEFAULTS,
  RANGES_FOR,
  aggregate,
  chartLayer,
  coverageByResolution,
  drawnIndicators,
  hasResolution,
  hasStructure,
  isPreOpen,
  istWeek,
  lastSessions,
  outsideSession,
  parseIndicatorSettings,
  rangeFromDate,
  readIndicatorSettings,
  rollUp,
  stitch,
  storedResolution,
  structureTimeframes,
  weekly,
  withLiveTick,
  writeIndicatorSettings,
} from './chart'
import type { Candle } from './chart'
import type { CoverageRow } from './queries'

const row = (resolution: string, source: CoverageRow['source'], toUtc = '2026-09-25T09:59:00Z', symbol = 'NSE:NIFTY50-INDEX'): CoverageRow => ({
  symbol,
  resolution,
  fromUtc: '2026-09-01T03:45:00Z',
  toUtc,
  barCount: 100,
  source,
})

const bar = (timeUtc: string, close = 100, volume: number | null = 10): Candle => ({
  timeUtc,
  open: close,
  high: close + 1,
  low: close - 1,
  close,
  volume,
})

describe('chartLayer', () => {
  it('draws SMC only when the URL asks for it', () => {
    expect(chartLayer('smc')).toBe('smc')
    expect(chartLayer(null)).toBe('price')
    expect(chartLayer('candles')).toBe('price')
  })

  it('still opens SMC from a link saved before the layer was renamed', () => {
    expect(chartLayer('structure')).toBe('smc')
  })
})

describe('coverage', () => {
  it("reads the live bars' 1m and the stored 1 as the same resolution, for the symbol asked", () => {
    const by = coverageByResolution([row('1', 'backfill'), row('1m', 'live'), row('D', 'backfill'), row('5', 'backfill', undefined, 'NSE:SBIN-EQ')], 'NSE:NIFTY50-INDEX')
    expect(by['1']).toHaveLength(2)
    expect(by.D).toHaveLength(1)
    expect(by['5']).toBeUndefined()
  })

  it('offers every intraday resolution to a symbol recording live, and the day only with daily candles', () => {
    const live = coverageByResolution([row('1m', 'live')], 'NSE:NIFTY50-INDEX')
    expect(hasResolution(live, '15')).toBe(true)
    expect(hasResolution(live, 'D')).toBe(false)
    const stored = coverageByResolution([row('5', 'backfill')], 'NSE:NIFTY50-INDEX')
    expect(hasResolution(stored, '5')).toBe(true)
    expect(hasResolution(stored, '1')).toBe(false)
  })

  it('never offers a range that is noise at the resolution', () => {
    expect(RANGES_FOR['1']).not.toContain('1Y')
    expect(RANGES_FOR.D).not.toContain('1D')
  })
})

describe('rangeFromDate', () => {
  const by = coverageByResolution([row('5', 'backfill', '2026-09-25T09:59:00Z'), row('1m', 'live', '2026-09-26T04:00:00Z')], 'NSE:NIFTY50-INDEX')

  it('counts back from the last bar the symbol has, not from the clock', () => {
    // 1D asks for 8 calendar days, ending on the newest bar (the live one, on the 26th).
    expect(rangeFromDate(by, '5', '1D', '2026-09-28T12:00:00Z')).toBe('2026-09-19')
  })

  it('falls back to the clock with nothing stored, and asks for everything on All', () => {
    expect(rangeFromDate({}, '5', '1D', '2026-09-28T12:00:00Z')).toBe('2026-09-21')
    expect(rangeFromDate(by, 'D', 'ALL', '2026-09-28T12:00:00Z')).toBeUndefined()
  })
})

describe('candles', () => {
  it('drops the pre-open auction on NSE and BSE only', () => {
    expect(isPreOpen('NSE:NIFTY50-INDEX', '2026-09-25T03:30:00Z')).toBe(true)
    expect(isPreOpen('NSE:NIFTY50-INDEX', '2026-09-25T03:45:00Z')).toBe(false)
    expect(isPreOpen('MCX:CRUDEOIL26OCTFUT', '2026-09-25T03:30:00Z')).toBe(false)
  })

  it('takes the last full session for 1D, skipping a day of two stray bars', () => {
    const full = Array.from({ length: 12 }, (_, i) => bar(`2026-09-25T04:${String(i).padStart(2, '0')}:00Z`))
    const stray = [bar('2026-09-28T03:46:00Z'), bar('2026-09-28T03:47:00Z')]
    expect(lastSessions([...full, ...stray], 1)).toHaveLength(12)
    expect(lastSessions([...full, ...stray], 2)).toHaveLength(14)
  })

  it('rolls live minutes into buckets the way the server does', () => {
    const minutes = [bar('2026-09-25T04:00:00Z', 100), bar('2026-09-25T04:01:00Z', 104), bar('2026-09-25T04:05:00Z', 102)]
    const five = rollUp(minutes, 5)
    expect(five).toHaveLength(2)
    expect(five[0]).toMatchObject({ timeUtc: '2026-09-25T04:00:00.000Z', open: 100, close: 104, high: 105, low: 99, volume: 20 })
  })

  it('lays live bars after the last stored candle only', () => {
    const stored = [bar('2026-09-25T04:00:00Z'), bar('2026-09-25T04:01:00Z')]
    const live = [bar('2026-09-25T04:01:00Z', 999), bar('2026-09-25T04:02:00Z', 101)]
    const out = stitch(stored, live, { symbol: 'NSE:NIFTY50-INDEX', resolution: '1', range: '5D' })
    expect(out.map((c) => c.close)).toEqual([100, 100, 101])
  })
})

describe('outsideSession', () => {
  it('holds NSE and BSE to 09:15-15:30 IST and MCX to 09:00 to its evening close, and leaves other symbols alone', () => {
    const t = (hm: string) => `2026-09-28T${hm}:00Z`
    expect(['03:44', '03:45', '09:59', '10:00', '18:30'].map((hm) => outsideSession('NSE:NIFTY50-INDEX', t(hm)))).toEqual([true, false, false, true, true])
    expect(outsideSession('bse:sensex-index', t('10:05'))).toBe(true)
    expect(['03:29', '03:30', '17:59', '18:00'].map((hm) => outsideSession('MCX:GOLD26DECFUT', t(hm), '2026-09-28T18:00:00Z'))).toEqual([true, false, false, true])
    // A session answer that is not an evening close (a closed day, a bad value) falls back to 23:55 IST.
    expect(outsideSession('MCX:GOLD26DECFUT', t('18:10'), '2026-09-28T04:00:00Z')).toBe(false)
    expect(outsideSession('MCX:GOLD26DECFUT', t('18:10'), 'not a date')).toBe(false)
    expect(outsideSession('NIFTY', t('20:00'))).toBe(false)
  })

  it('from 7 Dec 2026 keeps an option’s and a future’s last quarter hour, and not the index’s', () => {
    const before = (hm: string) => `2026-12-04T${hm}:00Z`
    const from = (hm: string) => `2026-12-07T${hm}:00Z`
    expect(outsideSession('NSE:NIFTY26D0824500CE', before('10:05'))).toBe(true)
    expect(['10:00', '10:14', '10:15'].map((hm) => outsideSession('NSE:NIFTY26D0824500CE', from(hm)))).toEqual([false, false, true])
    expect(outsideSession('BSE:SENSEX26DECFUT', from('10:10'))).toBe(false)
    expect(outsideSession('NSE:NIFTY50-INDEX', from('10:05'))).toBe(true)
    expect(outsideSession('NSE:RELIANCE-EQ', from('10:05'))).toBe(true)
  })
})

describe('structureTimeframes', () => {
  it('carries the timeframes above the chart, highest first', () => {
    expect(structureTimeframes('5')).toBe('1D,15m,5m')
    expect(structureTimeframes('15')).toBe('1D,15m')
    expect(structureTimeframes('D')).toBe('1D')
  })

  it('is offered only where candles are stored for the server to read', () => {
    expect((['1', '5', '15', '30', '60', 'D', 'W'] as const).filter(hasStructure)).toEqual(['1', '5', '15', 'D'])
  })
})

/** A candle at HH:MM IST on a date, stamped in UTC as the API stamps it. */
const ist = (date: string, hm: string) => {
  const [h, m] = hm.split(':').map(Number)
  const minutes = h * 60 + m - 330
  return `${date}T${String(Math.floor(minutes / 60)).padStart(2, '0')}:${String(minutes % 60).padStart(2, '0')}:00.000Z`
}
const ohlc = (timeUtc: string, open: number, high: number, low: number, close: number, volume: number | null = 10): Candle => ({
  timeUtc,
  open,
  high,
  low,
  close,
  volume,
})

describe('30m and 1h from the session open', () => {
  const NIFTY = 'NSE:NIFTY50-INDEX'
  const CRUDE = 'MCX:CRUDEOIL26NOVFUT'
  const D = '2026-10-29'
  // A whole NSE day of 15-minute candles, 09:15 to 15:15 (the last one 15:15-15:30).
  const day = Array.from({ length: 25 }, (_, i) => {
    const m = 9 * 60 + 15 + 15 * i
    const hm = `${String(Math.floor(m / 60)).padStart(2, '0')}:${String(m % 60).padStart(2, '0')}`
    return ohlc(ist(D, hm), 100 + i, 101 + i, 99 + i, 100.5 + i)
  })

  it('starts NSE half hours at 09:15, 09:45, ... and the last one at 15:15', () => {
    const half = aggregate(day, NIFTY, 30)
    const starts = ['09:15', '09:45', '10:15', '10:45', '11:15', '11:45', '12:15', '12:45', '13:15', '13:45', '14:15', '14:45', '15:15']
    expect(half.map((c) => c.timeUtc)).toEqual(starts.map((hm) => ist(D, hm)))
    expect(half[0]).toMatchObject({ open: 100, high: 102, low: 99, close: 101.5, volume: 20 })
  })

  it('runs NSE hours 09:15-10:15 and so on, the last cut short by the 15:30 close', () => {
    const hours = aggregate(day, NIFTY, 60)
    expect(hours.map((c) => c.timeUtc)).toEqual(['09:15', '10:15', '11:15', '12:15', '13:15', '14:15', '15:15'].map((hm) => ist(D, hm)))
    // 09:15, 09:30, 09:45 and 10:00 make the first hour: the first open, the last close, the extremes.
    expect(hours[0]).toMatchObject({ open: 100, close: 103.5, high: 104, low: 99, volume: 40 })
    // 15:15-15:30 is one 15-minute candle, and the hour is that candle.
    expect(hours[6]).toMatchObject({ open: 124, close: 124.5, volume: 10 })
    // The UTC clock's hour would have started at 08:30 IST and again at 09:30.
    expect(rollUp(day, 60)[1].timeUtc).toBe(ist(D, '09:30'))
  })

  it('starts MCX hours at its 09:00 open and runs them into the evening', () => {
    const crude = ['09:00', '09:15', '09:30', '09:45', '10:00', '23:00', '23:15'].map((hm, i) =>
      ohlc(ist(D, hm), 5000 + i, 5001 + i, 4999 + i, 5000.5 + i),
    )
    expect(aggregate(crude, CRUDE, 60).map((c) => c.timeUtc)).toEqual(['09:00', '10:00', '23:00'].map((hm) => ist(D, hm)))
    expect(aggregate(crude, CRUDE, 30).map((c) => c.timeUtc)).toEqual(['09:00', '09:30', '10:00', '23:00'].map((hm) => ist(D, hm)))
  })

  it('builds a candle from the candles it has, and none for a stretch with no data', () => {
    // A feed outage from 10:00 to 11:30: the 09:45 half hour has one of its two, 10:15 and 10:45 have none.
    const gappy = day.filter((c) => c.timeUtc < ist(D, '10:00') || c.timeUtc >= ist(D, '11:30'))
    const half = aggregate(gappy, NIFTY, 30)
    expect(half.slice(0, 3).map((c) => c.timeUtc)).toEqual([ist(D, '09:15'), ist(D, '09:45'), ist(D, '11:15')])
    expect(half[1]).toMatchObject({ open: 102, close: 102.5, volume: 10 })
  })

  it("keeps an index's missing volume missing", () => {
    expect(aggregate(day.slice(0, 2).map((c) => ({ ...c, volume: null })), NIFTY, 30)[0].volume).toBeNull()
  })

  it("lays today's live minutes into the hour the stored candles began", () => {
    // Stored 15-minute candles end with 09:45; the live minutes carry on from 10:00.
    const stored = day.slice(0, 3)
    const live = ['10:00', '10:01', '10:14', '10:15', '10:16'].map((hm, i) => ohlc(ist(D, hm), 110 + i, 111 + i, 109 + i, 110.5 + i, 1))
    const out = stitch(stored, live, { symbol: NIFTY, resolution: '60', range: '5D', source: '15' })
    expect(out.map((c) => c.timeUtc)).toEqual([ist(D, '09:15'), ist(D, '10:15')])
    expect(out[0]).toMatchObject({ open: 100, close: 112.5, high: 113, low: 99, volume: 33 })
    expect(out[1]).toMatchObject({ open: 113, close: 114.5, volume: 2 })
  })

  it('opens the next hour at 10:15 from a pushed price, not at the UTC hour', () => {
    const forming = [ohlc(ist(D, '09:15'), 100, 101, 99, 100.5)]
    const at = (iso: string, lastTradedPrice: number) => ({ lastTradedPrice, exchangeTimestampUtc: iso, receivedAtMs: Date.parse(iso) })
    // 10:05 IST is still the 09:15 hour.
    expect(withLiveTick(forming, at(ist(D, '10:05'), 102), { symbol: NIFTY, resolution: '60' })).toHaveLength(1)
    const next = withLiveTick(forming, at(ist(D, '10:20'), 103), { symbol: NIFTY, resolution: '60' })
    expect(next[1]).toEqual({ timeUtc: ist(D, '10:15'), open: 103, high: 103, low: 103, close: 103, volume: null })
    // The session's last hour, 15:15, takes prices until the close and none after it.
    const last = [ohlc(ist(D, '15:15'), 100, 101, 99, 100.5)]
    expect(withLiveTick(last, at(ist(D, '15:29'), 104), { symbol: NIFTY, resolution: '60' })[0]).toMatchObject({ close: 104, high: 104 })
    expect(withLiveTick(last, at(ist(D, '15:31'), 104), { symbol: NIFTY, resolution: '60' })).toBe(last)
  })

  it('is built from the coarsest stored intraday candles the symbol has, and offered to a symbol recording live', () => {
    const fifteen = coverageByResolution([row('15', 'backfill'), row('5', 'backfill'), row('1m', 'live')], NIFTY)
    expect(storedResolution(fifteen, '60')).toBe('15')
    expect(storedResolution(fifteen, '5')).toBe('5')
    expect(storedResolution(coverageByResolution([row('5', 'backfill'), row('1m', 'live')], NIFTY), '30')).toBe('5')
    // Live bars are not stored candles: with nothing else the 15-minute ask comes back empty and the live minutes make the chart.
    const liveOnly = coverageByResolution([row('1m', 'live')], NIFTY)
    expect(storedResolution(liveOnly, '30')).toBe('15')
    expect(hasResolution(liveOnly, '60')).toBe(true)
    expect(hasResolution(coverageByResolution([row('D', 'backfill')], NIFTY), '30')).toBe(false)
  })
})

describe('the week', () => {
  // FYERS and Dhan stamp a day at 00:00 IST, which is 18:30Z the day before.
  const daily = (date: string, open: number, high: number, low: number, close: number) => {
    const d = new Date(`${date}T00:00:00Z`)
    d.setUTCMinutes(d.getUTCMinutes() - 330)
    return ohlc(d.toISOString(), open, high, low, close, 1000)
  }

  it("names a week by its IST Monday, whatever the stamp's UTC date", () => {
    // Monday 26 Oct 2026, stamped 18:30Z on Sunday the 25th.
    expect(istWeek(daily('2026-10-26', 1, 1, 1, 1).timeUtc)).toBe('2026-10-26')
    expect(istWeek('2026-10-30T09:59:00Z')).toBe('2026-10-26')
    // A Sunday session (Muhurat trading) joins the week it closes.
    expect(istWeek('2026-11-01T10:00:00Z')).toBe('2026-10-26')
  })

  it("opens on the week's first day and closes on its last, a holiday simply missing", () => {
    const days = [
      daily('2026-10-19', 100, 104, 99, 103),
      daily('2026-10-20', 103, 106, 102, 105),
      // Nothing is stored for Wednesday the 21st, a holiday.
      daily('2026-10-22', 105, 105, 97, 98),
      daily('2026-10-23', 98, 101, 96, 100),
      daily('2026-10-26', 100, 102, 99, 101),
    ]
    const weeks = weekly(days)
    expect(weeks).toHaveLength(2)
    expect(weeks[0]).toEqual({ timeUtc: days[0].timeUtc, open: 100, high: 106, low: 96, close: 100, volume: 4000 })
    expect(weeks[1]).toMatchObject({ timeUtc: days[4].timeUtc, open: 100, close: 101 })
  })

  it('starts a week whose Monday was a holiday at its Tuesday', () => {
    const tuesday = daily('2026-11-10', 100, 101, 99, 100)
    const weeks = weekly([tuesday, daily('2026-11-11', 100, 103, 100, 102)])
    expect(weeks).toEqual([{ timeUtc: tuesday.timeUtc, open: 100, high: 103, low: 99, close: 102, volume: 2000 }])
  })

  it('drops a first week the window cut into, and keeps every week on All', () => {
    const days = ['2026-10-14', '2026-10-15', '2026-10-16', '2026-10-19', '2026-10-20'].map((d) => daily(d, 1, 2, 0.5, 1.5))
    // The window opens on Wednesday the 14th, so that week is missing its Monday and Tuesday.
    expect(stitch(days, [], { symbol: 'NSE:NIFTY50-INDEX', resolution: 'W', range: '1Y', fromDate: '2026-10-14' })).toHaveLength(1)
    expect(stitch(days, [], { symbol: 'NSE:NIFTY50-INDEX', resolution: 'W', range: 'ALL' })).toHaveLength(2)
  })

  it('is offered where daily candles are, and fetched from them', () => {
    const by = coverageByResolution([row('D', 'backfill')], 'NSE:NIFTY50-INDEX')
    expect(hasResolution(by, 'W')).toBe(true)
    expect(storedResolution(by, 'W')).toBe('D')
    expect(hasResolution(coverageByResolution([row('1m', 'live')], 'NSE:NIFTY50-INDEX'), 'W')).toBe(false)
  })

  it('keeps a daily candle stamped 00:00 UTC, which is a date and not the pre-open', () => {
    // The option history stamps its days that way; the pre-open rule used to throw them all away.
    const days = [ohlc('2026-10-26T00:00:00Z', 10, 12, 9, 11), ohlc('2026-10-27T00:00:00Z', 11, 13, 10, 12)]
    expect(stitch(days, [], { symbol: 'NSE:NIFTY26OCT25000CE', resolution: 'D', range: '1M' })).toHaveLength(2)
  })
})

describe('indicator settings', () => {
  const memory = () => {
    const kept = new Map<string, string>()
    return { getItem: (k: string) => kept.get(k) ?? null, setItem: (k: string, v: string) => void kept.set(k, v) }
  }

  it('reads back what was chosen, in this browser', () => {
    const store = memory()
    const chosen = { ...INDICATOR_DEFAULTS, on: { ...INDICATOR_DEFAULTS.on, ema: true, rsi: true }, emaPeriod: 9 }
    writeIndicatorSettings(chosen, store)
    expect(readIndicatorSettings(store)).toEqual(chosen)
  })

  it('takes the default for whatever is missing or wrong, and starts with nothing drawn', () => {
    expect(parseIndicatorSettings(null)).toEqual(INDICATOR_DEFAULTS)
    expect(parseIndicatorSettings('not json')).toEqual(INDICATOR_DEFAULTS)
    const odd = parseIndicatorSettings(JSON.stringify({ on: { sma: true, macd: 'yes', bogus: true }, smaPeriod: 1, emaPeriod: 12.5 }))
    expect(odd.on).toEqual({ ...INDICATOR_DEFAULTS.on, sma: true })
    expect(odd.smaPeriod).toBe(INDICATOR_DEFAULTS.smaPeriod)
    expect(odd.emaPeriod).toBe(INDICATOR_DEFAULTS.emaPeriod)
  })

  it('survives storage that throws (a private window, blocked site data)', () => {
    const denied = () => {
      throw new Error('denied')
    }
    const blocked = { getItem: denied, setItem: denied }
    expect(readIndicatorSettings(blocked)).toEqual(INDICATOR_DEFAULTS)
    expect(() => writeIndicatorSettings(INDICATOR_DEFAULTS, blocked)).not.toThrow()
  })

  it('draws no VWAP on a day or a week, and keeps the choice for the next intraday chart', () => {
    const chosen = { ...INDICATOR_DEFAULTS, on: { ...INDICATOR_DEFAULTS.on, vwap: true, sma: true } }
    expect(drawnIndicators(chosen, 'W').on).toMatchObject({ vwap: false, sma: true })
    expect(drawnIndicators(chosen, '60')).toBe(chosen)
  })
})

describe('withLiveTick', () => {
  const NIFTY = 'NSE:NIFTY50-INDEX'
  // The forming 5-minute candle opened 10:10 IST (04:40Z).
  const candles = [bar('2026-09-28T04:35:00.000Z', 100), bar('2026-09-28T04:40:00.000Z', 102)]
  const at = (iso: string, lastTradedPrice: number) => ({ lastTradedPrice, exchangeTimestampUtc: iso, receivedAtMs: Date.parse(iso) + 80 })

  it('moves the forming candle: its close, and its high and low when the price passes them', () => {
    const up = withLiveTick(candles, at('2026-09-28T04:42:10Z', 104.5), { symbol: NIFTY, resolution: '5' })
    expect(up).toHaveLength(2)
    expect(up[0]).toBe(candles[0])
    expect(up[1]).toMatchObject({ open: 102, close: 104.5, high: 104.5, low: 101 })
    const down = withLiveTick(up, at('2026-09-28T04:43:00Z', 100.5), { symbol: NIFTY, resolution: '5' })
    // The high reached in between stays on the candle.
    expect(down[1]).toMatchObject({ close: 100.5, high: 104.5, low: 100.5 })
  })

  it('opens the next candle when the price is past the forming one, on the same session', () => {
    const next = withLiveTick(candles, at('2026-09-28T04:45:02Z', 103), { symbol: NIFTY, resolution: '5' })
    expect(next).toHaveLength(3)
    expect(next[2]).toEqual({ timeUtc: '2026-09-28T04:45:00.000Z', open: 103, high: 103, low: 103, close: 103, volume: null })
  })

  it('leaves the candles alone for a day chart, an older price, a new session, a pre-open print or no price', () => {
    const same = (tick: ReturnType<typeof at> | undefined, resolution: '1' | '5' | 'D' = '5', symbol = NIFTY) =>
      expect(withLiveTick(candles, tick, { symbol, resolution })).toBe(candles)
    same(at('2026-09-28T04:42:10Z', 104), 'D')
    same(at('2026-09-28T04:39:59Z', 104))
    same(at('2026-09-29T03:46:00Z', 104))
    same({ ...at('2026-09-28T04:42:10Z', 0) })
    same(undefined)
    const opening = [bar('2026-09-28T03:45:00.000Z', 100)]
    expect(withLiveTick(opening, at('2026-09-28T03:40:00Z', 90), { symbol: NIFTY, resolution: '1' })).toBe(opening)
  })

  it('adds no candle after the close, when the vendors go on repeating the last price', () => {
    // The last 5-minute candle of the session, 15:25 IST.
    const closing = [bar('2026-09-28T09:50:00.000Z', 100), bar('2026-09-28T09:55:00.000Z', 101)]
    const opts = { symbol: NIFTY, resolution: '5' as const }
    expect(withLiveTick(closing, at('2026-09-28T10:01:00Z', 101), opts)).toBe(closing)
    expect(withLiveTick(closing, at('2026-09-28T10:00:00Z', 101.5), opts)).toBe(closing)
    expect(withLiveTick(closing, at('2026-09-28T14:12:00Z', 101), opts)).toBe(closing)
    // The session's last seconds still move the forming candle.
    expect(withLiveTick(closing, at('2026-09-28T09:59:58Z', 101.5), opts)[1]).toMatchObject({ close: 101.5 })
  })

  it("adds no candle at 00:00 IST for BSE's replay of the previous close before today has bars", () => {
    const yesterday = [bar('2026-09-27T09:50:00.000Z', 80_000), bar('2026-09-27T09:55:00.000Z', 80_100)]
    // 00:00 IST on the 28th is 18:30Z on the 27th: the same UTC date as yesterday's candles.
    expect(withLiveTick(yesterday, at('2026-09-27T18:30:00Z', 80_100), { symbol: 'BSE:SENSEX-INDEX', resolution: '5' })).toBe(yesterday)
  })

  it('keeps MCX to its own hours: from 09:00 IST to the evening close the session answer gives', () => {
    const CRUDE = 'MCX:CRUDEOIL26OCTFUT'
    // The 23:25 IST candle.
    const late = [bar('2026-09-28T17:50:00.000Z', 5_300), bar('2026-09-28T17:55:00.000Z', 5_310)]
    const summer = { symbol: CRUDE, resolution: '5' as const, mcxCloseUtc: '2026-09-28T18:00:00Z' }
    expect(withLiveTick(late, at('2026-09-28T18:02:00Z', 5_312), summer)).toBe(late)
    // In the US winter it trades to 23:55 IST.
    const winter = { ...summer, mcxCloseUtc: '2026-11-28T18:25:00Z' }
    expect(withLiveTick(late, at('2026-09-28T18:02:00Z', 5_312), winter)).toHaveLength(3)
    // No answer yet: the later close, so a real candle is never held back.
    expect(withLiveTick(late, at('2026-09-28T18:02:00Z', 5_312), { symbol: CRUDE, resolution: '5' })).toHaveLength(3)
    expect(withLiveTick(late, at('2026-09-28T18:26:00Z', 5_312), { symbol: CRUDE, resolution: '5' })).toBe(late)
    // MCX's morning, which NSE has not opened yet, is a session.
    const morning = [bar('2026-09-28T03:30:00.000Z', 5_290)]
    expect(withLiveTick(morning, at('2026-09-28T03:36:00Z', 5_295), { symbol: CRUDE, resolution: '5' })).toHaveLength(2)
  })

  it('reads the arrival time when the exchange sent no stamp', () => {
    const next = withLiveTick(candles, { lastTradedPrice: 101.5, exchangeTimestampUtc: null, receivedAtMs: Date.parse('2026-09-28T04:41:00Z') }, { symbol: NIFTY, resolution: '5' })
    expect(next[1]).toMatchObject({ close: 101.5 })
  })
})
