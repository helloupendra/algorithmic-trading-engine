/**
 * The chart's indicators as pure maths: SMA, EMA, session VWAP, Bollinger
 * Bands, Supertrend, RSI and MACD.
 *
 * Where the strategies compute the same thing they are the definition, and
 * these follow them line for line (`strategies/indicators.py` in the Python
 * engine: `sma`, `ema`, `rsi`, `supertrend`, `vwap`; IndicatorMath.cs ports
 * the same four for the alerts). A chart that drew RSI one way while a
 * strategy traded it another would be showing a number nothing acts on.
 * indicators.test.ts holds them to values the Python functions printed.
 * MACD and Bollinger Bands are not in the Python file: MACD is built from its
 * EMA, and the bands use the population deviation, as Bollinger defined them.
 *
 * Each indicator is a fold over the candles (a Study): the state it carries
 * from one candle to the next, and its value on each. The value on candle i
 * is read from candles 0..i only, so a series computed forward once is the
 * same number the Python function gives on that prefix. The fold is also
 * what keeps the chart cheap while a candle forms: the candles that have
 * closed are folded once, and each pushed price costs one step on the
 * forming candle (`cursor`), not the whole series again.
 */

import { istDate } from './chart'
import type { Candle } from './chart'

/**
 * An indicator as a fold. `step` never changes the state it is given, so the
 * same state can be stepped with the forming candle again and again, once
 * per price, without that candle being taken in for good.
 */
export interface Study<S, V> {
  readonly start: S
  step(state: S, bar: Candle): readonly [S, V | null]
}

/** A study's value on every candle, null until it is defined. */
export function series<S, V>(study: Study<S, V>, bars: readonly Candle[]): (V | null)[] {
  let state = study.start
  return bars.map((bar) => {
    const [next, value] = study.step(state, bar)
    state = next
    return value
  })
}

/** A study walked forward over closed candles, able to read the forming one without keeping it. */
export interface StudyCursor<V> {
  /** Take a closed candle in for good; its value. */
  close(bar: Candle): V | null
  /** The value the forming candle has as it stands, leaving the cursor where it was. */
  peek(bar: Candle): V | null
}

export function cursor<S, V>(study: Study<S, V>): StudyCursor<V> {
  let state = study.start
  return {
    close(bar) {
      const [next, value] = study.step(state, bar)
      state = next
      return value
    },
    peek(bar) {
      return study.step(state, bar)[1]
    },
  }
}

// ------------------------------------------------------------ the averages

/** The last `period` values, oldest first, never longer. */
function slide(window: readonly number[], value: number, period: number): number[] {
  return window.length < period ? [...window, value] : [...window.slice(1), value]
}

/** Summed in order, as Python's sum() does, so the two agree to the last digit. */
function mean(values: readonly number[]): number {
  let sum = 0
  for (const v of values) sum += v
  return sum / values.length
}

/** Simple moving average of the last `period` closes. */
export function sma(period: number): Study<readonly number[], number> {
  return {
    start: [],
    step(window, bar) {
      const next = slide(window, bar.close, period)
      return [next, next.length === period ? mean(next) : null]
    },
  }
}

interface EmaState {
  readonly count: number
  readonly seed: number
  readonly value: number | null
}

const EMA_START: EmaState = { count: 0, seed: 0, value: null }

/**
 * One step of an EMA seeded with the SMA of its first `period` values, the
 * strategies' rule: seeded on the first value alone, the early series would
 * depend on where the data happens to begin.
 */
function emaStep(s: EmaState, x: number, period: number): readonly [EmaState, number | null] {
  const count = s.count + 1
  if (s.value == null) {
    const seed = s.seed + x
    if (count < period) return [{ count, seed, value: null }, null]
    const value = seed / period
    return [{ count, seed, value }, value]
  }
  const multiplier = 2 / (period + 1)
  const value = x * multiplier + s.value * (1 - multiplier)
  return [{ count, seed: s.seed, value }, value]
}

/** Exponential moving average of the closes (defined from the `period`th candle). */
export function ema(period: number): Study<EmaState, number> {
  return { start: EMA_START, step: (s, bar) => emaStep(s, bar.close, period) }
}

export interface Bands {
  upper: number
  middle: number
  lower: number
}

/** Bollinger Bands: the SMA, and `k` population standard deviations either side of it. */
export function bollinger(period = 20, k = 2): Study<readonly number[], Bands> {
  return {
    start: [],
    step(window, bar) {
      const next = slide(window, bar.close, period)
      if (next.length < period) return [next, null]
      const middle = mean(next)
      const sd = Math.sqrt(mean(next.map((v) => (v - middle) ** 2)))
      return [next, { upper: middle + k * sd, middle, lower: middle - k * sd }]
    },
  }
}

// ------------------------------------------------------------- the session

interface VwapState {
  readonly session: string
  readonly weighted: number
  readonly volume: number
}

/**
 * The session VWAP at each candle's close: the typical price
 * ((high + low + close) / 3) weighted by volume, from the session's first
 * candle. It starts again every session (an IST date, as the strategies'
 * session_bars reads one). Null while nothing has traded, the
 * honest answer for an index, which reports no volume.
 */
export function sessionVwap(): Study<VwapState, number> {
  return {
    start: { session: '', weighted: 0, volume: 0 },
    step(s, bar) {
      const session = istDate(bar.timeUtc)
      let { weighted, volume } = session === s.session ? s : { weighted: 0, volume: 0 }
      const traded = bar.volume ?? 0
      if (traded > 0) {
        weighted += ((bar.high + bar.low + bar.close) / 3) * traded
        volume += traded
      }
      return [{ session, weighted, volume }, volume > 0 ? weighted / volume : null]
    },
  }
}

// ------------------------------------------------------------ the momentum

interface RsiState {
  readonly count: number
  readonly previous: number
  readonly gains: number
  readonly losses: number
}

/** A window with no losses is 100 if it rose and 50 if it did not move: the strategies' answer. */
function rsiOf(avgGain: number, avgLoss: number): number {
  if (avgLoss === 0) return avgGain > 0 ? 100 : 50
  return 100 - 100 / (1 + avgGain / avgLoss)
}

/**
 * Wilder's RSI of the closes (defined from the candle after the first
 * `period` changes). `gains` and `losses` are the running sums until then and
 * Wilder's smoothed averages after.
 */
export function rsi(period = 14): Study<RsiState, number> {
  return {
    start: { count: 0, previous: 0, gains: 0, losses: 0 },
    step(s, bar) {
      const x = bar.close
      if (s.count === 0) return [{ count: 1, previous: x, gains: 0, losses: 0 }, null]
      const change = x - s.previous
      const gain = Math.max(change, 0)
      const loss = Math.max(-change, 0)
      const count = s.count + 1
      // This is change number s.count; the first `period` of them seed the averages.
      if (s.count <= period) {
        const gains = s.gains + gain
        const losses = s.losses + loss
        if (s.count < period) return [{ count, previous: x, gains, losses }, null]
        const avgGain = gains / period
        const avgLoss = losses / period
        return [{ count, previous: x, gains: avgGain, losses: avgLoss }, rsiOf(avgGain, avgLoss)]
      }
      const avgGain = (s.gains * (period - 1) + gain) / period
      const avgLoss = (s.losses * (period - 1) + loss) / period
      return [{ count, previous: x, gains: avgGain, losses: avgLoss }, rsiOf(avgGain, avgLoss)]
    },
  }
}

export interface Macd {
  macd: number
  /** Null until the signal EMA has its first `signal` MACD values. */
  signal: number | null
  histogram: number | null
}

interface MacdState {
  readonly fast: EmaState
  readonly slow: EmaState
  readonly signal: EmaState
}

/** MACD: the fast EMA less the slow, the signal an EMA of that, and the histogram between them. */
export function macd(fast = 12, slow = 26, signal = 9): Study<MacdState, Macd> {
  return {
    start: { fast: EMA_START, slow: EMA_START, signal: EMA_START },
    step(s, bar) {
      const [f, fastValue] = emaStep(s.fast, bar.close, fast)
      const [sl, slowValue] = emaStep(s.slow, bar.close, slow)
      if (fastValue == null || slowValue == null) return [{ ...s, fast: f, slow: sl }, null]
      const line = fastValue - slowValue
      const [sg, signalValue] = emaStep(s.signal, line, signal)
      return [
        { fast: f, slow: sl, signal: sg },
        { macd: line, signal: signalValue, histogram: signalValue == null ? null : line - signalValue },
      ]
    },
  }
}

// -------------------------------------------------------------- the trend

/** Supertrend on one candle: which way it points and where its line is. */
export interface SupertrendPoint {
  up: boolean
  line: number
}

interface SupertrendState {
  readonly count: number
  readonly previous: Candle | null
  /** The first `period` true ranges summed, then Wilder's ATR. */
  readonly atr: number
  readonly up: boolean
  readonly upper: number | null
  readonly lower: number | null
}

/** The classic true range: the candle's span, widened by any gap from the previous close. */
function trueRange(bar: Candle, previous: Candle): number {
  return Math.max(bar.high - bar.low, Math.abs(bar.high - previous.close), Math.abs(bar.low - previous.close))
}

/**
 * Supertrend on Wilder's ATR(`period`): the bands are the median price plus
 * and minus `multiplier` ATRs, each only tightens while the trend holds, and
 * a close through the active band turns it. The line is the lower band while
 * it points up and the upper while down. It starts pointing up, as the
 * strategies' does, and gives its first answer one candle after the ATR's
 * seed (candle `period` + 1), where theirs does.
 */
export function supertrend(period = 10, multiplier = 3): Study<SupertrendState, SupertrendPoint> {
  return {
    start: { count: 0, previous: null, atr: 0, up: true, upper: null, lower: null },
    step(s, bar) {
      const index = s.count
      const count = index + 1
      if (!s.previous) return [{ ...s, count, previous: bar }, null]
      const tr = trueRange(bar, s.previous)
      if (index < period) return [{ ...s, count, previous: bar, atr: s.atr + tr }, null]
      const atr = index === period ? (s.atr + tr) / period : (s.atr * (period - 1) + tr) / period

      const median = (bar.high + bar.low) / 2
      const bandUp = median + multiplier * atr
      const bandDown = median - multiplier * atr
      const previousClose = s.previous.close
      let upper = s.upper == null || bandUp < s.upper || previousClose > s.upper ? bandUp : s.upper
      let lower = s.lower == null || bandDown > s.lower || previousClose < s.lower ? bandDown : s.lower
      let up = s.up
      if (up && bar.close < lower) {
        up = false
        upper = bandUp
      } else if (!up && bar.close > upper) {
        up = true
        lower = bandDown
      }
      const state = { count, previous: bar, atr, up, upper, lower }
      return [state, index >= period + 1 ? { up, line: up ? lower : upper } : null]
    },
  }
}
