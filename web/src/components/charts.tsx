/**
 * lightweight-charts wrappers.
 *
 * Each component owns one chart instance for its lifetime and resizes with its
 * container. Colors come from the CSS custom properties so charts follow the
 * app theme without duplicating hex values here.
 */

import { chartOptions } from '../lib/chartTheme'
import { useTheme } from '../lib/theme'
import { useEffect, useRef } from 'react'
import {
  CandlestickSeries,
  createChart,
  HistogramSeries,
  type IChartApi,
  type ISeriesApi,
  type UTCTimestamp,
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
 * Full-height price chart: candlesticks with a volume histogram tucked into the
 * bottom fifth of the same pane. Crosshair/tooltip come from lightweight-charts.
 *
 * The chart is built once and fed new data in place. The forming candle
 * moves with every pushed price, several times a second; rebuilding the
 * chart for each would flicker and throw away the reader's zoom. It is
 * fitted to its data only when `fitKey` changes (another symbol, resolution
 * or range), or on its first data when no key is given.
 */
export function PriceChart({ candles, fitKey = '' }: { candles: readonly PriceCandle[]; fitKey?: string }) {
  const ref = useRef<HTMLDivElement | null>(null)
  const chartRef = useRef<IChartApi | null>(null)
  const priceRef = useRef<ISeriesApi<'Candlestick'> | null>(null)
  const volumeRef = useRef<ISeriesApi<'Histogram'> | null>(null)
  const colorsRef = useRef({ up: '', down: '' })
  const shownRef = useRef<readonly PriceCandle[] | null>(null)
  const fittedRef = useRef<string | null>(null)

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

    const volumeSeries = chart.addSeries(HistogramSeries, {
      priceFormat: { type: 'volume' },
      priceScaleId: 'volume',
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
      return
    }

    // The API can hand back candles sharing a timestamp after a re-backfill;
    // lightweight-charts requires strictly ascending unique times.
    const byTime = new Map<UTCTimestamp, PriceCandle>()
    for (const c of [...candles].sort((a, b) => a.timeUtc.localeCompare(b.timeUtc))) {
      byTime.set(istTime(c.timeUtc), c)
    }
    const rows = [...byTime.entries()]
    priceSeries.setData(rows.map(([time, c]) => bar(time, c)))
    volumeSeries.setData(rows.map(([time, c]) => volume(time, c)))
    if (fittedRef.current !== fitKey && rows.length > 0) {
      fittedRef.current = fitKey
      chart.timeScale().fitContent()
    }
  }, [candles, fitKey, theme])

  useResize(chartRef, ref)

  return <div ref={ref} className="chart chart--tall" />
}
