/**
 * Candlestick + volume chart on a persistent lightweight-charts instance.
 *
 * The chart is created once per mount; data refreshes go through
 * series.setData() so polling never resets the user's zoom/pan (the v1 charts
 * destroyed and rebuilt the whole chart on every poll, flickering and losing
 * the viewport). Only an explicit symbol/resolution change refits the view.
 */

import { chartOptions } from '../lib/chartTheme'
import { useTheme } from '../lib/theme'
import { useEffect, useMemo, useRef } from 'react'
import {
  CandlestickSeries,
  createChart,
  HistogramSeries,
  type IChartApi,
  type ISeriesApi,
  type UTCTimestamp,
} from 'lightweight-charts'
import type { CandleDto } from '../lib/types'

function cssVar(name: string): string {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim()
}

/** A token's colour at an alpha: "#31c48d" → "rgba(49,196,141,0.35)" (a non-hex token is returned as it is). */
function tint(hex: string, alpha: number): string {
  const m = /^#([0-9a-f]{6})$/i.exec(hex)
  if (!m) return hex
  const n = parseInt(m[1], 16)
  return `rgba(${(n >> 16) & 255},${(n >> 8) & 255},${n & 255},${alpha})`
}

export function CandleChart({
  candles,
  /** Refit the visible range when this changes (e.g. "SYMBOL|RES"). */
  fitKey,
  tall,
}: {
  candles: CandleDto[]
  fitKey: string
  tall?: boolean
}) {
  const containerRef = useRef<HTMLDivElement | null>(null)
  const chartRef = useRef<IChartApi | null>(null)
  const priceRef = useRef<ISeriesApi<'Candlestick'> | null>(null)
  const volumeRef = useRef<ISeriesApi<'Histogram'> | null>(null)
  const lastFitKey = useRef<string | null>(null)

  // Ascending, deduplicated by timestamp — lightweight-charts requires both.
  const sorted = useMemo(() => {
    const byTime = new Map<number, CandleDto>()
    for (const c of candles) {
      byTime.set(Math.floor(new Date(c.timestampUtc).getTime() / 1000), c)
    }
    return [...byTime.entries()].sort((a, b) => a[0] - b[0])
  }, [candles])

  // The tokens are read once, when the chart is created: a theme change makes a new chart.
  const { theme } = useTheme()
  useEffect(() => {
    const el = containerRef.current
    if (!el) return

    const chart = createChart(el, chartOptions(el))

    const price = chart.addSeries(CandlestickSeries, {
      upColor: cssVar('--pos'),
      downColor: cssVar('--neg'),
      wickUpColor: cssVar('--pos'),
      wickDownColor: cssVar('--neg'),
      borderVisible: false,
    })

    const volume = chart.addSeries(HistogramSeries, {
      priceFormat: { type: 'volume' },
      priceScaleId: 'vol',
      color: cssVar('--line-strong'),
    })
    chart.priceScale('vol').applyOptions({ scaleMargins: { top: 0.82, bottom: 0 } })

    chartRef.current = chart
    priceRef.current = price
    volumeRef.current = volume

    const observer = new ResizeObserver(() => {
      if (el.clientWidth > 0) {
        chart.applyOptions({ width: el.clientWidth, height: el.clientHeight })
      }
    })
    observer.observe(el)

    return () => {
      observer.disconnect()
      chart.remove()
      chartRef.current = null
      priceRef.current = null
      volumeRef.current = null
      lastFitKey.current = null
    }
  }, [theme])

  useEffect(() => {
    const price = priceRef.current
    const volume = volumeRef.current
    const chart = chartRef.current
    if (!price || !volume || !chart) return

    const up = tint(cssVar('--pos'), 0.35)
    const down = tint(cssVar('--neg'), 0.35)
    price.setData(
      sorted.map(([t, c]) => ({
        time: t as UTCTimestamp,
        open: c.open,
        high: c.high,
        low: c.low,
        close: c.close,
      })),
    )
    volume.setData(
      sorted.map(([t, c]) => ({
        time: t as UTCTimestamp,
        value: c.volume,
        color: c.close >= c.open ? up : down,
      })),
    )

    if (lastFitKey.current !== fitKey) {
      lastFitKey.current = fitKey
      chart.timeScale().fitContent()
    }
  }, [sorted, fitKey, theme])

  return <div ref={containerRef} className={tall ? 'chart chart--tall' : 'chart'} />
}
