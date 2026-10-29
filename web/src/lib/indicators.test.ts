import { describe, expect, it } from 'vitest'

import { bollinger, cursor, ema, macd, rsi, series, sessionVwap, sma, supertrend } from './indicators'
import { istDate } from './chart'
import type { Study } from './indicators'
import type { Candle } from './chart'

/*
 * Sixty 5-minute NIFTY-like candles over two sessions (25 and 26 Sep 2026,
 * thirty each from 09:15 IST), each opening at the previous close. The
 * expected numbers were printed by the Python engine's
 * strategies/indicators.py on the same candles, each function called on the
 * growing prefix the way a strategy calls it (Supertrend with its display
 * rounding left off). MACD and the bands are not in that file; their numbers
 * come from its `ema` and from Python's statistics.pstdev.
 */
const OHLCV: ReadonlyArray<readonly [number, number, number, number, number]> = [
  [100.0, 101.15, 99.7, 100.8, 1000], [100.8, 102.18, 100.35, 101.63, 1400], [101.63, 103.93, 101.03, 103.18, 1800], [103.18, 104.89, 102.43, 104.54, 1100],
  [104.54, 106.2, 104.24, 105.65, 1500], [105.65, 107.19, 105.2, 106.44, 1900], [106.44, 107.23, 105.84, 106.88, 1200], [106.88, 108.3, 106.13, 107.75, 1600],
  [107.75, 108.5, 106.36, 106.66, 2000], [106.66, 107.01, 105.57, 106.02, 1300], [106.02, 106.57, 104.49, 105.09, 1700], [105.09, 105.84, 103.19, 103.94, 1000],
  [103.94, 104.29, 102.35, 102.65, 1400], [102.65, 103.2, 100.85, 101.3, 1800], [101.3, 102.05, 100.2, 100.8, 1100], [100.8, 101.15, 98.07, 98.82, 1500],
  [98.82, 99.37, 97.56, 97.86, 1900], [97.86, 98.61, 96.73, 97.18, 1200], [97.18, 97.53, 96.23, 96.83, 1600], [96.83, 97.4, 96.08, 96.85, 2000],
  [96.85, 98.0, 96.55, 97.25, 1300], [97.25, 99.15, 96.8, 98.8, 1700], [98.8, 99.62, 98.2, 99.07, 1000], [99.07, 101.15, 98.32, 100.4, 1400],
  [100.4, 102.27, 100.1, 101.92, 1800], [101.92, 104.1, 101.47, 103.55, 1100], [103.55, 105.94, 102.95, 105.19, 1500], [105.19, 107.1, 104.44, 106.75, 1900],
  [106.75, 109.49, 106.45, 108.94, 1200], [108.94, 110.04, 108.49, 109.29, 1600], [109.29, 110.48, 108.69, 110.13, 2000], [110.13, 111.17, 109.38, 110.62, 1300],
  [110.62, 111.49, 110.32, 110.74, 1700], [110.74, 111.09, 110.04, 110.49, 1000], [110.49, 111.04, 109.29, 109.89, 1400], [109.89, 110.64, 109.05, 109.8, 1800],
  [109.8, 110.15, 107.57, 107.87, 1100], [107.87, 108.42, 106.14, 106.59, 1500], [106.59, 107.34, 104.65, 105.25, 1900], [105.25, 105.6, 103.18, 103.93, 1200],
  [103.93, 104.48, 102.44, 102.74, 1600], [102.74, 103.49, 101.29, 101.74, 2000], [101.74, 102.17, 101.14, 101.82, 1300], [101.82, 102.37, 99.88, 100.63, 1700],
  [100.63, 101.38, 100.3, 100.6, 1000], [100.6, 101.29, 100.15, 100.94, 1400], [100.94, 102.2, 100.34, 101.65, 1800], [101.65, 103.43, 100.9, 102.68, 1100],
  [102.68, 104.33, 102.38, 103.98, 1500], [103.98, 106.83, 103.53, 106.28, 1900], [106.28, 107.85, 105.68, 107.1, 1200], [107.1, 109.1, 106.35, 108.75, 1600],
  [108.75, 110.87, 108.45, 110.32, 2000], [110.32, 112.49, 109.87, 111.74, 1300], [111.74, 113.27, 111.14, 112.92, 1700], [112.92, 114.36, 112.17, 113.81, 1000],
  [113.81, 115.89, 113.51, 115.14, 1400], [115.14, 115.49, 114.06, 114.51, 1800], [114.51, 115.06, 113.71, 114.31, 1100], [114.31, 115.06, 113.01, 113.76, 1500],
]

/** 09:15 IST is 03:45Z; candle k of a session starts 5k minutes after it. */
const BARS: Candle[] = OHLCV.map(([open, high, low, close, volume], i) => {
  const day = i < 30 ? 25 : 26
  const minutes = 3 * 60 + 45 + 5 * (i % 30)
  const hm = `${String(Math.floor(minutes / 60)).padStart(2, '0')}:${String(minutes % 60).padStart(2, '0')}`
  return { timeUtc: `2026-09-${day}T${hm}:00Z`, open, high, low, close, volume }
})

const firstDefined = <V,>(values: readonly (V | null)[]) => values.findIndex((v) => v != null)

describe('the averages, against the strategies', () => {
  it('SMA(20) from the 20th candle', () => {
    const out = series(sma(20), BARS)
    expect(firstDefined(out)).toBe(19)
    expect(out[19]).toBeCloseTo(102.5435, 9)
    expect(out[30]).toBeCloseTo(101.876, 9)
    expect(out[59]).toBeCloseTo(107.271, 9)
  })

  it('EMA(9) seeded with the SMA of its first nine closes', () => {
    const out = series(ema(9), BARS)
    expect(firstDefined(out)).toBe(8)
    expect(out[8]).toBeCloseTo(104.83666666666666, 9)
    expect(out[30]).toBeCloseTo(105.9346784891266, 9)
    expect(out[59]).toBeCloseTo(112.33574424263065, 9)
  })

  it('Bollinger(20, 2): the SMA and two population deviations either side', () => {
    const out = series(bollinger(20, 2), BARS)
    expect(firstDefined(out)).toBe(19)
    expect(out[19]!.upper).toBeCloseTo(109.62448940826773, 9)
    expect(out[19]!.middle).toBeCloseTo(102.5435, 9)
    expect(out[19]!.lower).toBeCloseTo(95.46251059173225, 9)
    expect(out[59]).toMatchObject({ upper: expect.closeTo(118.09820074626863, 9), lower: expect.closeTo(96.44379925373137, 9) })
  })

  it('Bollinger on 1..20 by hand: mean 10.5, deviation sqrt(33.25)', () => {
    const ramp = Array.from({ length: 20 }, (_, i) => ({ ...BARS[0], close: i + 1 }))
    const last = series(bollinger(20, 2), ramp)[19]!
    expect(last.middle).toBe(10.5)
    expect(last.upper).toBeCloseTo(10.5 + 2 * Math.sqrt(33.25), 12)
  })
})

describe('the session VWAP', () => {
  it('weights the typical price by volume and starts again at the next session', () => {
    const out = series(sessionVwap(), BARS)
    expect(out[0]).toBeCloseTo(100.55000000000001, 9)
    expect(out[29]).toBeCloseTo(102.67455430711611, 9)
    // The 26th's first candle is its own typical price, nothing carried from the 25th.
    expect(out[30]).toBeCloseTo((110.48 + 108.69 + 110.13) / 3, 9)
    expect(out[59]).toBeCloseTo(107.5938169642857, 9)
  })

  it('has none while nothing has traded, which is every index candle', () => {
    const index = BARS.slice(0, 5).map((b) => ({ ...b, volume: null }))
    expect(series(sessionVwap(), index)).toEqual([null, null, null, null, null])
  })

  it('names a session by its IST date, so a candle stamped at IST midnight is that day', () => {
    expect(istDate('2026-09-25T03:45:00Z')).toBe('2026-09-25')
    expect(istDate('2026-09-24T18:30:00Z')).toBe('2026-09-25')
  })
})

describe('the momentum, against the strategies', () => {
  it("Wilder's RSI(14) from the candle after the first fourteen changes", () => {
    const out = series(rsi(14), BARS)
    expect(firstDefined(out)).toBe(14)
    expect(out[20]).toBeCloseTo(39.76707208663916, 9)
    expect(out[45]).toBeCloseTo(37.23388655894486, 9)
    expect(out[59]).toBeCloseTo(71.00879672265972, 9)
  })

  it('RSI with no losses in the window is 100, and 50 when nothing moved', () => {
    const flat = Array.from({ length: 4 }, () => ({ ...BARS[0], close: 100 }))
    const rising = flat.map((b, i) => ({ ...b, close: 100 + i }))
    expect(series(rsi(3), rising)[3]).toBe(100)
    expect(series(rsi(3), flat)[3]).toBe(50)
  })

  it('MACD(12, 26, 9): the line from the 26th candle, the signal nine MACD values later', () => {
    const out = series(macd(12, 26, 9), BARS)
    expect(firstDefined(out)).toBe(25)
    expect(out[25]).toMatchObject({ macd: expect.closeTo(-1.5061529657336905, 9), signal: null, histogram: null })
    expect(out[32]!.signal).toBeNull()
    expect(out[33]!.signal).toBeCloseTo(0.3938561100894778, 9)
    expect(out[59]!.macd).toBeCloseTo(2.6435016073991164, 9)
    expect(out[59]!.signal).toBeCloseTo(1.774751926494556, 9)
    expect(out[59]!.histogram).toBeCloseTo(2.6435016073991164 - 1.774751926494556, 9)
  })
})

describe('Supertrend, against the strategies', () => {
  it('(10, 3) on Wilder ATR: first answer on candle 11, then the line and its turns', () => {
    const out = series(supertrend(10, 3), BARS)
    expect(firstDefined(out)).toBe(11)
    expect(out[11]).toEqual({ up: true, line: expect.closeTo(99.422, 9) })
    expect(out[29]).toEqual({ up: true, line: expect.closeTo(102.59432783240848, 9) })
    expect(out[45]).toEqual({ up: false, line: expect.closeTo(106.32320758126069, 9) })
    expect(out[59]).toEqual({ up: true, line: expect.closeTo(108.33613046049861, 9) })
    // The directions the Python function gave, candle by candle.
    const turns = out.map((p) => (p == null ? '.' : p.up ? 'U' : 'D')).join('')
    expect(turns).toBe('...........UUUUDDDDDDDDDDUUUUUUUUUUUUUUDDDDDDDDDDDUUUUUUUUUU')
  })
})

describe('the cursor a forming candle is read with', () => {
  const studies: Record<string, Study<unknown, unknown>> = {
    sma: sma(20) as Study<unknown, unknown>,
    ema: ema(9) as Study<unknown, unknown>,
    bollinger: bollinger() as Study<unknown, unknown>,
    vwap: sessionVwap() as Study<unknown, unknown>,
    rsi: rsi() as Study<unknown, unknown>,
    macd: macd() as Study<unknown, unknown>,
    supertrend: supertrend() as Study<unknown, unknown>,
  }

  it.each(Object.keys(studies))('%s: peeking at a candle as it forms changes nothing it closes on', (key) => {
    const study = studies[key]
    const full = series(study, BARS)
    const c = cursor(study)
    BARS.forEach((bar, i) => {
      // The forming candle moves three times before it closes where it did.
      for (const close of [bar.open, bar.high, bar.low]) c.peek({ ...bar, close })
      expect(c.peek(bar)).toEqual(full[i])
      expect(c.close(bar)).toEqual(full[i])
    })
  })
})
