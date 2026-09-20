/**
 * Candles with the market structure drawn on them: swing points labelled
 * HH / HL / LH / LL, a line from every broken level to the candle that broke
 * it (BOS or CHoCH), and the inducement of each leg.
 *
 * The chart instance is persistent, like CandleChart: refreshes go through
 * setData so a poll never resets the user's zoom. Overlays are redrawn as a
 * whole whenever the marks change, which is cheap because they are three line
 * series and one marker list rather than one object per mark.
 *
 * Times are shifted into IST before they reach the chart, so the axis reads
 * 09:15 for the open, and every overlay uses the same shift as the candles.
 */

import { useEffect, useMemo, useRef } from 'react'
import {
  CandlestickSeries,
  ColorType,
  createChart,
  createSeriesMarkers,
  LineSeries,
  LineStyle,
  type IChartApi,
  type IPriceLine,
  type ISeriesApi,
  type ISeriesMarkersPluginApi,
  type SeriesMarker,
  type Time,
  type UTCTimestamp,
} from 'lightweight-charts'
import type { SmcStructure } from '../lib/types'

const IST_OFFSET_SECONDS = 5.5 * 3600

function cssVar(name: string): string {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim()
}

function ist(iso: string): UTCTimestamp {
  return (Math.floor(new Date(iso).getTime() / 1000) + IST_OFFSET_SECONDS) as UTCTimestamp
}

export type SmcLayers = {
  swings: boolean
  breaks: boolean
  inducements: boolean
  minorSwings: boolean
  /** The levels the higher timeframes are holding, drawn across the chart. */
  higher: boolean
}

export function SmcChart({
  data,
  higher,
  layers,
  fitKey,
}: {
  data: SmcStructure | undefined
  /** The timeframes above this one, highest first; only their levels are drawn. */
  higher?: SmcStructure[]
  layers: SmcLayers
  fitKey: string
}) {
  const containerRef = useRef<HTMLDivElement | null>(null)
  const chartRef = useRef<IChartApi | null>(null)
  const priceRef = useRef<ISeriesApi<'Candlestick'> | null>(null)
  const priceLinesRef = useRef<IPriceLine[]>([])
  const markersRef = useRef<ISeriesMarkersPluginApi<Time> | null>(null)
  const linesRef = useRef<ISeriesApi<'Line'>[]>([])
  const lastFitKey = useRef<string | null>(null)

  const candles = useMemo(() => {
    if (!data) return []
    const byTime = new Map<UTCTimestamp, (typeof data.candles)[number]>()
    for (const c of data.candles) byTime.set(ist(c.timestampUtc), c)
    return [...byTime.entries()].sort((a, b) => (a[0] as number) - (b[0] as number))
  }, [data])

  useEffect(() => {
    const el = containerRef.current
    if (!el) return

    const chart = createChart(el, {
      width: el.clientWidth,
      height: el.clientHeight,
      layout: {
        background: { type: ColorType.Solid, color: 'transparent' },
        textColor: cssVar('--text-2'),
        fontSize: 11,
        attributionLogo: false,
      },
      grid: {
        vertLines: { color: cssVar('--line-soft') },
        horzLines: { color: cssVar('--line-soft') },
      },
      rightPriceScale: { borderColor: cssVar('--line') },
      timeScale: { borderColor: cssVar('--line'), timeVisible: true, secondsVisible: false },
      crosshair: { mode: 0 },
    })

    const price = chart.addSeries(CandlestickSeries, {
      upColor: cssVar('--pos'),
      downColor: cssVar('--neg'),
      wickUpColor: cssVar('--pos'),
      wickDownColor: cssVar('--neg'),
      borderVisible: false,
    })

    chartRef.current = chart
    priceRef.current = price
    markersRef.current = createSeriesMarkers(price, [])

    const observer = new ResizeObserver(() => {
      if (el.clientWidth > 0) chart.applyOptions({ width: el.clientWidth, height: el.clientHeight })
    })
    observer.observe(el)

    return () => {
      observer.disconnect()
      chart.remove()
      chartRef.current = null
      priceRef.current = null
      markersRef.current = null
      linesRef.current = []
      priceLinesRef.current = []
      lastFitKey.current = null
    }
  }, [])

  useEffect(() => {
    const price = priceRef.current
    const chart = chartRef.current
    if (!price || !chart) return

    price.setData(
      candles.map(([t, c]) => ({ time: t, open: c.open, high: c.high, low: c.low, close: c.close })),
    )

    if (lastFitKey.current !== fitKey) {
      lastFitKey.current = fitKey
      chart.timeScale().fitContent()
    }
  }, [candles, fitKey])

  // Overlays: rebuilt whenever the marks or the chosen layers change.
  useEffect(() => {
    const chart = chartRef.current
    const markers = markersRef.current
    if (!chart || !markers) return

    for (const line of linesRef.current) chart.removeSeries(line)
    linesRef.current = []
    if (!data) {
      markers.setMarkers([])
      return
    }

    const bull = cssVar('--pos') || '#089981'
    const bear = cssVar('--neg') || '#f2364a'
    const muted = cssVar('--text-3') || '#8b8f9a'

    const drawn: SeriesMarker<Time>[] = []

    if (layers.swings) {
      for (const swing of data.swings) {
        if (!swing.major && !layers.minorSwings) continue
        const high = swing.kind === 'high'
        drawn.push({
          time: ist(swing.timeUtc),
          position: high ? 'aboveBar' : 'belowBar',
          shape: 'circle',
          // Highs are read against the sellers above them and lows against the
          // buyers below, which is why the colours look inverted at first.
          color: swing.major ? (high ? bear : bull) : muted,
          text: swing.major ? (swing.label ?? '') : '',
          size: swing.major ? 1 : 0.6,
        })
      }
    }

    // One short line series per mark. A segment cannot share a series with the
    // next one — lightweight-charts wants strictly ascending, unique times —
    // and a chart window holds few enough marks for this to stay cheap.
    const line = (from: UTCTimestamp, to: UTCTimestamp, value: number, color: string, style: LineStyle) => {
      const series = chart.addSeries(LineSeries, {
        color,
        lineWidth: 1,
        lineStyle: style,
        priceLineVisible: false,
        lastValueVisible: false,
        crosshairMarkerVisible: false,
      })
      series.setData(from === to ? [{ time: from, value }] : [
        { time: from, value },
        { time: to, value },
      ])
      linesRef.current.push(series)
    }

    if (layers.breaks) {
      for (const event of data.events) {
        const bullish = event.direction === 'bullish'
        const colour = bullish ? bull : bear
        line(
          ist(event.levelTimeUtc),
          ist(event.breakTimeUtc),
          event.level,
          colour,
          event.kind === 'CHOCH' ? LineStyle.Dashed : LineStyle.Solid,
        )
        drawn.push({
          time: ist(event.breakTimeUtc),
          position: bullish ? 'aboveBar' : 'belowBar',
          shape: bullish ? 'arrowUp' : 'arrowDown',
          color: colour,
          text: event.kind === 'CHOCH' ? 'CHoCH' : 'BOS',
        })
      }
    }

    if (layers.inducements) {
      const last = candles.length > 0 ? candles[candles.length - 1][0] : undefined
      for (const mark of data.inducements) {
        // A pullback the market never came back for is not an inducement that
        // mattered; the chart shows the ones that were taken and the one still
        // standing, which is the level the next break is waiting on.
        if (mark.sweptTimeUtc === null && mark.endedTimeUtc !== null) continue
        const to = mark.sweptTimeUtc ? ist(mark.sweptTimeUtc) : last
        if (to === undefined) continue
        line(ist(mark.timeUtc), to, mark.level, muted, LineStyle.Dotted)
        drawn.push({
          time: to,
          position: mark.kind === 'low' ? 'belowBar' : 'aboveBar',
          shape: 'square',
          color: muted,
          text: 'IDM',
          size: 0.6,
        })
      }
    }

    markers.setMarkers(drawn.sort((a, b) => (a.time as number) - (b.time as number)))
  }, [data, layers, candles])

  useEffect(() => {
    const price = priceRef.current
    if (!price) return

    for (const line of priceLinesRef.current) price.removePriceLine(line)
    priceLinesRef.current = []
    if (!layers.higher || !higher?.length) return

    const bull = cssVar('--pos') || '#089981'
    const bear = cssVar('--neg') || '#f2364a'

    for (const timeframe of higher) {
      const colour = timeframe.trend === 'bearish' ? bear : timeframe.trend === 'bullish' ? bull : cssVar('--text-3')
      // What that timeframe is holding: the level whose break turns it, and
      // the one whose break carries it on.
      const levels: [number | null, string, LineStyle][] = [
        [timeframe.protectedLevel, `${timeframe.resolution} turns`, LineStyle.Dashed],
        [timeframe.breakLevel, `${timeframe.resolution} ${timeframe.trend === 'bearish' ? 'breaks ↓' : 'breaks ↑'}`, LineStyle.Dotted],
      ]
      for (const [value, title, style] of levels) {
        if (value == null) continue
        priceLinesRef.current.push(
          price.createPriceLine({ price: value, color: colour, lineWidth: 1, lineStyle: style, axisLabelVisible: true, title }),
        )
      }
    }
  }, [higher, layers.higher])

  return <div ref={containerRef} className="chart chart--tall" />
}
