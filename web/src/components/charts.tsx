/**
 * lightweight-charts wrappers.
 *
 * Each component owns one chart instance for its lifetime and resizes with its
 * container. Colors come from the CSS custom properties so charts follow the
 * app theme without duplicating hex values here.
 */

import { chartOptions } from '../lib/chartTheme'
import { useTheme } from '../lib/theme'
import { hasLowerPanes, istDate, type IndicatorSettings } from '../lib/chart'
import { bollinger, cursor, ema, macd, rsi, sessionVwap, sma, supertrend, type StudyCursor } from '../lib/indicators'
import { useEffect, useMemo, useRef } from 'react'
import {
  CandlestickSeries,
  createChart,
  HistogramSeries,
  LineSeries,
  LineStyle,
  type IChartApi,
  type ISeriesApi,
  type LineSeriesPartialOptions,
  type UTCTimestamp,
  type WhitespaceData,
} from 'lightweight-charts'

function cssVar(name: string): string {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim()
}


function useResize(chartRef: React.RefObject<IChartApi | null>, ref: React.RefObject<HTMLDivElement | null>) {
  useEffect(() => {
    const el = ref.current
    if (!el) return
    const observer = new ResizeObserver(() => {
      if (chartRef.current && el.clientWidth > 0) {
        chartRef.current.applyOptions({ width: el.clientWidth, height: el.clientHeight })
      }
    })
    observer.observe(el)
    return () => observer.disconnect()
  }, [chartRef, ref])
}

/**
 * lightweight-charts labels its axis in UTC. Every bar here is an Indian
 * session, so the timestamps are shifted by the fixed IST offset (no daylight
 * saving) before they reach the chart: 03:45Z shows as 09:15, the way the
 * trader read it on the exchange.
 */
const IST_OFFSET_SECONDS = 19_800
function istTime(iso: string): UTCTimestamp {
  return (Math.floor(new Date(iso).getTime() / 1000) + IST_OFFSET_SECONDS) as UTCTimestamp
}

/** A normalized candle any source (stored history, live bars) can map into. */
export interface PriceCandle {
  timeUtc: string
  open: number
  high: number
  low: number
  close: number
  volume: number | null
}

/**
 * Whether `next` is `prev` with only its last candle moved or one candle
 * added after it: what a pushed price does to the forming candle. The
 * earlier candles are the same objects, so they are compared by reference.
 */
function isTailUpdate(prev: readonly PriceCandle[], next: readonly PriceCandle[]): boolean {
  if (prev.length === 0) return false
  const grew = next.length === prev.length + 1
  if (!grew && next.length !== prev.length) return false
  const same = grew ? prev.length : prev.length - 1
  for (let i = 0; i < same; i++) if (prev[i] !== next[i]) return false
  return true
}

/**
 * The candles as the chart takes them. The API can hand back candles sharing
 * a timestamp after a re-backfill; lightweight-charts requires strictly
 * ascending unique times, so the later of two wins.
 */
function drawable(candles: readonly PriceCandle[]): [UTCTimestamp, PriceCandle][] {
  const byTime = new Map<UTCTimestamp, PriceCandle>()
  for (const c of [...candles].sort((a, b) => a.timeUtc.localeCompare(b.timeUtc))) byTime.set(istTime(c.timeUtc), c)
  return [...byTime.entries()]
}

// ---------------------------------------------------------- the indicators

/** A study's value on one candle. */
interface Reading<V> {
  bar: PriceCandle
  value: V | null
}

/** One line (or histogram) a study draws: its series, and its number and colour from the study's value. */
interface Plot<V> {
  series: ISeriesApi<'Line' | 'Histogram'>
  value: (v: V) => number | null
  color?: (v: V) => string
  /**
   * Where the line must not run on to the next candle: a new session's VWAP,
   * a Supertrend that turned. lightweight-charts joins every point it is
   * given and colours a segment by its first point, so that point is drawn
   * in no colour at all.
   */
  breaks?: (a: Reading<V>, b: Reading<V>) => boolean
}

const NO_COLOUR = 'rgba(0, 0, 0, 0)'

function point<V>(plot: Plot<V>, at: Reading<V>, next: Reading<V> | null): { time: UTCTimestamp; value: number; color?: string } | WhitespaceData<UTCTimestamp> {
  const time = istTime(at.bar.timeUtc)
  if (at.value == null) return { time }
  const value = plot.value(at.value)
  if (value == null) return { time }
  if (next && plot.breaks?.(at, next)) return { time, value, color: NO_COLOUR }
  return plot.color ? { time, value, color: plot.color(at.value) } : { time, value }
}

/**
 * One study on the chart. `fill` draws it over every candle; `tick` moves it
 * with the forming candle, which costs one step of the study (lib/indicators,
 * `cursor`) and an update of the last point of each line, not the series
 * again: on a day of 1-minute candles a price arrives several times a second.
 */
interface Track {
  fill(rows: readonly PriceCandle[]): void
  /** The last candle moved (`grew` false) or a new one opened after it (`grew` true). */
  tick(last: PriceCandle, grew: boolean): void
  remove(): void
}

function track<V>(chart: IChartApi, start: () => StudyCursor<V>, plots: Plot<V>[]): Track {
  let walk = start()
  // The candle before the forming one, and the forming one, as last drawn.
  let before: Reading<V> | null = null
  let last: Reading<V> | null = null
  // Per plot, whether the point before the forming candle is drawn as a break.
  let broken = plots.map(() => false)

  return {
    fill(rows) {
      walk = start()
      const readings = rows.map((bar, i) => ({ bar, value: i < rows.length - 1 ? walk.close(bar) : walk.peek(bar) }))
      for (const plot of plots) plot.series.setData(readings.map((r, i) => point(plot, r, readings[i + 1] ?? null)))
      before = readings[readings.length - 2] ?? null
      last = readings[readings.length - 1] ?? null
      broken = plots.map((plot) => !!(before && last && plot.breaks?.(before, last)))
    },
    tick(bar, grew) {
      if (grew && last) {
        // The candle that was forming has closed (the same object, as isTailUpdate checks).
        walk.close(last.bar)
        before = last
        broken = plots.map(() => false)
      }
      const now: Reading<V> = { bar, value: walk.peek(bar) }
      last = now
      plots.forEach((plot, i) => {
        // The forming candle can turn the trend and turn it back between two prices.
        const breaks = !!(before && plot.breaks?.(before, now))
        if (before && breaks !== broken[i]) {
          plot.series.update(point(plot, before, now), true)
          broken[i] = breaks
        }
        plot.series.update(point(plot, now, null))
      })
    },
    remove() {
      for (const plot of plots) chart.removeSeries(plot.series)
    },
  }
}

/** The colours the studies draw in, read from the theme's tokens when the chart is made. */
function studyColours() {
  return {
    sma: cssVar('--warn') || '#e3b341',
    ema: cssVar('--brand-hi') || '#7096ff',
    vwap: cssVar('--study-vwap') || '#b48cff',
    band: cssVar('--text-3') || '#76869c',
    guide: cssVar('--line-strong') || '#2b3a4e',
    up: cssVar('--success') || '#31c48d',
    down: cssVar('--danger') || '#f4635e',
  }
}

/**
 * The tracks for a choice of indicators: the overlays on the price pane, then
 * RSI and MACD in panes of their own under it, in that order.
 */
function buildTracks(chart: IChartApi, settings: IndicatorSettings): Track[] {
  const c = studyColours()
  const { on } = settings
  const line = (pane: number, options: LineSeriesPartialOptions) =>
    chart.addSeries(LineSeries, { lineWidth: 1, priceLineVisible: false, crosshairMarkerVisible: false, ...options }, pane) as ISeriesApi<'Line' | 'Histogram'>
  const tracks: Track[] = []

  if (on.sma) {
    const period = settings.smaPeriod
    tracks.push(track(chart, () => cursor(sma(period)), [{ series: line(0, { color: c.sma, title: `SMA ${period}` }), value: (v) => v }]))
  }
  if (on.ema) {
    const period = settings.emaPeriod
    tracks.push(track(chart, () => cursor(ema(period)), [{ series: line(0, { color: c.ema, title: `EMA ${period}` }), value: (v) => v }]))
  }
  if (on.vwap) {
    tracks.push(
      track(chart, () => cursor(sessionVwap()), [
        { series: line(0, { color: c.vwap, title: 'VWAP' }), value: (v) => v, breaks: (a, b) => istDate(a.bar.timeUtc) !== istDate(b.bar.timeUtc) },
      ]),
    )
  }
  if (on.bollinger) {
    const edge = { color: c.band, lastValueVisible: false }
    tracks.push(
      track(chart, () => cursor(bollinger(20, 2)), [
        { series: line(0, edge), value: (v) => v.upper },
        { series: line(0, { color: c.band, lineStyle: LineStyle.Dashed, title: 'BB 20, 2' }), value: (v) => v.middle },
        { series: line(0, edge), value: (v) => v.lower },
      ]),
    )
  }
  if (on.supertrend) {
    tracks.push(
      track(chart, () => cursor(supertrend(10, 3)), [
        {
          series: line(0, { lineWidth: 2, title: 'ST 10, 3' }),
          value: (v) => v.line,
          color: (v) => (v.up ? c.up : c.down),
          breaks: (a, b) => a.value != null && b.value != null && a.value.up !== b.value.up,
        },
      ]),
    )
  }

  // The panes under the price are numbered in the order they are added.
  let pane = 1
  if (on.rsi) {
    const series = line(pane, {
      color: c.ema,
      title: 'RSI 14',
      // RSI lives between 0 and 100; a scale fitted to the visible stretch would move the 70 and 30 lines.
      autoscaleInfoProvider: () => ({ priceRange: { minValue: 0, maxValue: 100 } }),
    })
    for (const price of [70, 30]) series.createPriceLine({ price, color: c.guide, lineWidth: 1, lineStyle: LineStyle.Dashed, axisLabelVisible: false, title: '' })
    tracks.push(track(chart, () => cursor(rsi(14)), [{ series, value: (v) => v }]))
    pane += 1
  }
  if (on.macd) {
    const histogram = chart.addSeries(HistogramSeries, { priceLineVisible: false, lastValueVisible: false }, pane) as ISeriesApi<'Line' | 'Histogram'>
    tracks.push(
      track(chart, () => cursor(macd(12, 26, 9)), [
        { series: histogram, value: (v) => v.histogram, color: (v) => ((v.histogram ?? 0) >= 0 ? `${c.up}88` : `${c.down}88`) },
        { series: line(pane, { color: c.ema, title: 'MACD 12, 26' }), value: (v) => v.macd },
        { series: line(pane, { color: c.sma, title: 'Signal 9' }), value: (v) => v.signal },
      ]),
    )
  }

  // The price keeps most of the height: three parts to each pane under it.
  const panes = chart.panes()
  if (panes.length > 1) panes[0].setStretchFactor(3)
  return tracks
}

/**
 * Full-height price chart: candlesticks with a volume histogram tucked into the
 * bottom fifth of the same pane, the chosen indicators over the price, and
 * RSI and MACD in panes under it. Crosshair/tooltip come from lightweight-charts.
 *
 * The chart is built once and fed new data in place. The forming candle
 * moves with every pushed price, several times a second; rebuilding the
 * chart for each would flicker and throw away the reader's zoom. It is
 * fitted to its data only when `fitKey` changes (another symbol, resolution
 * or range), or on its first data when no key is given. The indicators move
 * with the forming candle the same way, one step and one point per price.
 */
export function PriceChart({
  candles,
  fitKey = '',
  indicators,
}: {
  candles: readonly PriceCandle[]
  fitKey?: string
  indicators?: IndicatorSettings
}) {
  const ref = useRef<HTMLDivElement | null>(null)
  const chartRef = useRef<IChartApi | null>(null)
  const priceRef = useRef<ISeriesApi<'Candlestick'> | null>(null)
  const volumeRef = useRef<ISeriesApi<'Histogram'> | null>(null)
  const colorsRef = useRef({ up: '', down: '' })
  const shownRef = useRef<readonly PriceCandle[] | null>(null)
  const fittedRef = useRef<string | null>(null)
  const tracksRef = useRef<Track[]>([])

  // The tokens are read once, when the chart is created: a theme change makes a new chart.
  const { theme } = useTheme()
  useEffect(() => {
    const el = ref.current
    if (!el) return

    const chart = createChart(el, chartOptions(el))
    chartRef.current = chart

    const up = cssVar('--success')
    const down = cssVar('--danger')
    colorsRef.current = { up, down }

    const priceSeries = chart.addSeries(CandlestickSeries, {
      upColor: up,
      wickUpColor: up,
      downColor: down,
      wickDownColor: down,
      borderVisible: false,
    })
    priceSeries.priceScale().applyOptions({ scaleMargins: { top: 0.05, bottom: 0.22 } })
    priceRef.current = priceSeries

    // No label or line for the last bar's volume: they stood on the price
    // axis as a stray "0" for every index.
    const volumeSeries = chart.addSeries(HistogramSeries, {
      priceFormat: { type: 'volume' },
      priceScaleId: 'volume',
      lastValueVisible: false,
      priceLineVisible: false,
    })
    chart.priceScale('volume').applyOptions({ scaleMargins: { top: 0.82, bottom: 0 }, visible: false })
    volumeRef.current = volumeSeries

    return () => {
      chart.remove()
      chartRef.current = null
      priceRef.current = null
      volumeRef.current = null
      shownRef.current = null
      fittedRef.current = null
    }
  }, [theme])

  // The indicators, made again whenever the choice changes and drawn over
  // the candles already on the chart. A theme change makes a new chart, and
  // the data effect below draws them on it.
  const chosen = useMemo(() => (indicators ? JSON.stringify(indicators) : ''), [indicators])
  useEffect(() => {
    const chart = chartRef.current
    if (!chart || !chosen) return
    const tracks = buildTracks(chart, JSON.parse(chosen) as IndicatorSettings)
    tracksRef.current = tracks
    const rows = drawable(shownRef.current ?? []).map(([, c]) => c)
    for (const t of tracks) t.fill(rows)
    return () => {
      // After a theme change the chart they were on is gone, and its series with it.
      if (chartRef.current === chart) for (const t of tracks) t.remove()
      tracksRef.current = []
    }
  }, [chosen, theme])

  useEffect(() => {
    const chart = chartRef.current
    const priceSeries = priceRef.current
    const volumeSeries = volumeRef.current
    if (!chart || !priceSeries || !volumeSeries) return
    const { up, down } = colorsRef.current
    const bar = (time: UTCTimestamp, c: PriceCandle) => ({ time, open: c.open, high: c.high, low: c.low, close: c.close })
    const volume = (time: UTCTimestamp, c: PriceCandle) => ({
      time,
      value: c.volume ?? 0,
      color: c.close >= c.open ? `${up}55` : `${down}55`,
    })

    const prev = shownRef.current
    shownRef.current = candles
    if (prev && fittedRef.current === fitKey && isTailUpdate(prev, candles)) {
      const last = candles[candles.length - 1]
      const time = istTime(last.timeUtc)
      priceSeries.update(bar(time, last))
      volumeSeries.update(volume(time, last))
      for (const t of tracksRef.current) t.tick(last, candles.length > prev.length)
      return
    }

    const rows = drawable(candles)
    priceSeries.setData(rows.map(([time, c]) => bar(time, c)))
    volumeSeries.setData(rows.map(([time, c]) => volume(time, c)))
    // An index trades no volume, and a row of empty bars reads as a dotted line under the candles.
    volumeSeries.applyOptions({ visible: rows.some(([, c]) => (c.volume ?? 0) > 0) })
    for (const t of tracksRef.current) t.fill(rows.map(([, c]) => c))
    if (fittedRef.current !== fitKey && rows.length > 0) {
      fittedRef.current = fitKey
      chart.timeScale().fitContent()
    }
  }, [candles, fitKey, theme])

  useResize(chartRef, ref)

  return <div ref={ref} className={`chart chart--tall${hasLowerPanes(indicators) ? ' chart--panes' : ''}`} />
}
