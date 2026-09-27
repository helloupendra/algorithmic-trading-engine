/**
 * lightweight-charts wrappers.
 *
 * Each component owns one chart instance for its lifetime and resizes with its
 * container. Colors come from the CSS custom properties so charts follow the
 * app theme without duplicating hex values here.
 */

import { useEffect, useRef } from 'react'
import {
  CandlestickSeries,
  ColorType,
  createChart,
  HistogramSeries,
  type IChartApi,
  type UTCTimestamp,
} from 'lightweight-charts'

function cssVar(name: string): string {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim()
}

function baseOptions(container: HTMLElement) {
  return {
    width: container.clientWidth,
    height: container.clientHeight,
    layout: {
      background: { type: ColorType.Solid, color: 'transparent' },
      textColor: cssVar('--fg-muted'),
      fontSize: 11,
    },
    grid: {
      vertLines: { color: cssVar('--border-soft') },
      horzLines: { color: cssVar('--border-soft') },
    },
    rightPriceScale: { borderColor: cssVar('--border') },
    timeScale: { borderColor: cssVar('--border'), timeVisible: true, secondsVisible: false },
    crosshair: { mode: 0 },
  }
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
 * Full-height price chart: candlesticks with a volume histogram tucked into the
 * bottom fifth of the same pane. Crosshair/tooltip come from lightweight-charts.
 */
export function PriceChart({ candles }: { candles: PriceCandle[] }) {
  const ref = useRef<HTMLDivElement | null>(null)
  const chartRef = useRef<IChartApi | null>(null)

  useEffect(() => {
    const el = ref.current
    if (!el) return

    const chart = createChart(el, baseOptions(el))
    chartRef.current = chart

    const up = cssVar('--success')
    const down = cssVar('--danger')

    const priceSeries = chart.addSeries(CandlestickSeries, {
      upColor: up,
      wickUpColor: up,
      downColor: down,
      wickDownColor: down,
      borderVisible: false,
    })
    priceSeries.priceScale().applyOptions({ scaleMargins: { top: 0.05, bottom: 0.22 } })

    const volumeSeries = chart.addSeries(HistogramSeries, {
      priceFormat: { type: 'volume' },
      priceScaleId: 'volume',
    })
    chart.priceScale('volume').applyOptions({ scaleMargins: { top: 0.82, bottom: 0 }, visible: false })

    // The API can hand back candles sharing a timestamp after a re-backfill;
    // lightweight-charts requires strictly ascending unique times.
    const byTime = new Map<number, PriceCandle>()
    for (const c of [...candles].sort((a, b) => a.timeUtc.localeCompare(b.timeUtc))) {
      byTime.set(istTime(c.timeUtc), c)
    }
    const rows = [...byTime.entries()]

    priceSeries.setData(
      rows.map(([time, c]) => ({
        time: time as UTCTimestamp,
        open: c.open,
        high: c.high,
        low: c.low,
        close: c.close,
      })),
    )
    volumeSeries.setData(
      rows.map(([time, c]) => ({
        time: time as UTCTimestamp,
        value: c.volume ?? 0,
        color: c.close >= c.open ? `${up}55` : `${down}55`,
      })),
    )
    chart.timeScale().fitContent()

    return () => {
      chart.remove()
      chartRef.current = null
    }
  }, [candles])

  useResize(chartRef, ref)

  return <div ref={ref} className="chart chart--tall" />
}
