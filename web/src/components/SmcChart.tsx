/**
 * Candles with the market structure drawn on them: swing points labelled
 * HH / HL / LH / LL, a line from every broken level to the candle that broke
 * it (BOS or CHoCH), the inducement of each leg, and — as boxes behind the
 * candles — order blocks, fair value gaps, and the run of delivery from one
 * break to the next.
 *
 * The chart instance is persistent, like CandleChart: refreshes go through
 * setData so a poll never resets the user's zoom. Overlays are redrawn as a
 * whole whenever the marks change, which is cheap because they are a handful
 * of line series, one marker list and one primitive that paints every box in
 * a single pass, rather than one object per mark.
 *
 * Nothing new goes into the marker list. All markers of every kind already
 * share one flat array with no priority between them, and a single candle can
 * already carry a swing circle, a break arrow and an inducement square; a
 * fourth and fifth shape on the same candle would say less, not more. The
 * boxes speak for themselves.
 *
 * Times are shifted into IST before they reach the chart, so the axis reads
 * 09:15 for the open, and every overlay uses the same shift as the candles.
 * The boxes are the one exception, and only halfway: lightweight-charts numbers
 * the bars 0..n-1 in the order setData was given them, and the zone primitive
 * speaks in those numbers rather than in times, so the marks' times are looked
 * up against the same candle list the chart itself was drawn from.
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
import { ZonePrimitive, type Zone } from './SmcZones'

const IST_OFFSET_SECONDS = 5.5 * 3600

/**
 * Where the order-flow band sits: a thin strip just under the window's low,
 * as a fraction of the window's price range. The primitive speaks in prices,
 * not in pane fractions, so the band is pinned to a price rather than to the
 * bottom of the pane — drag the price scale far enough and it travels with the
 * candles instead of staying put. That is the price of reusing one primitive
 * for every box, and it is cheaper than a second one that works in pixels.
 */
const FLOW_BAND_TOP = 0.02
const FLOW_BAND_BOTTOM = 0.035

function cssVar(name: string): string {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim()
}

/**
 * The resolved fill and edge for one kind of zone. Zones carry their own tokens
 * rather than reusing --pos-soft / --neg-soft: that alpha was tuned for a CSS
 * background on a panel and all but disappears over the chart's darker ground,
 * and where a block and a gap sit in the same band of price two of them compound
 * into a slab that hides the candles behind it. The edge is the stronger colour
 * of the pair, so a zone still reads by its border once the fill washes out.
 *
 * The literals are the fallback for a missing token, in the same voice as the
 * `cssVar('--pos') || '#089981'` below — a layer that is switched on should
 * never paint nothing at all.
 */
function zoneInk(token: string, fill: string, edge: string): { fill: string; edge: string } {
  return { fill: cssVar(`--zone-${token}`) || fill, edge: cssVar(`--zone-${token}-edge`) || edge }
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
  /**
   * Order blocks. The box runs from the block's own candle, but it appears only
   * at the candle that broke structure and found it, which is usually later and
   * often after price has already left the zone.
   */
  orderBlocks: boolean
  /** Fair value gaps: the band of price three candles stepped over. */
  fvg: boolean
  /**
   * Delivery runs: which way the market was being delivered, break to break.
   * This is the SMC reading, inferred from price — not footprint order flow,
   * which needs an aggressor side this platform's feed does not carry.
   */
  orderFlow: boolean
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
  const zonesRef = useRef<ZonePrimitive | null>(null)
  const lastFitKey = useRef<string | null>(null)

  const candles = useMemo(() => {
    if (!data) return []
    const byTime = new Map<UTCTimestamp, (typeof data.candles)[number]>()
    for (const c of data.candles) byTime.set(ist(c.timestampUtc), c)
    return [...byTime.entries()].sort((a, b) => (a[0] as number) - (b[0] as number))
  }, [data])

  // The translation the zone primitive needs. No index crosses the wire — the
  // marks are stamped with times, like every other mark in this contract — so
  // the candle list that setData is given is also what turns those times into
  // the bar numbers the boxes are drawn against.
  const indexByTime = useMemo(() => {
    const byIndex = new Map<UTCTimestamp, number>()
    candles.forEach(([time], index) => byIndex.set(time, index))
    return byIndex
  }, [candles])

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

    // One primitive for every box of every kind, attached once and handed a new
    // zone list when the marks change. It paints behind the candles, so a zone
    // can never hide the price it is a reading of.
    const zones = new ZonePrimitive()
    price.attachPrimitive(zones)
    zonesRef.current = zones

    const observer = new ResizeObserver(() => {
      if (el.clientWidth > 0) chart.applyOptions({ width: el.clientWidth, height: el.clientHeight })
    })
    observer.observe(el)

    return () => {
      observer.disconnect()
      price.detachPrimitive(zones)
      chart.remove()
      chartRef.current = null
      priceRef.current = null
      markersRef.current = null
      linesRef.current = []
      zonesRef.current = null
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
    const zones = zonesRef.current
    if (!chart || !markers || !zones) return

    for (const line of linesRef.current) chart.removeSeries(line)
    linesRef.current = []
    if (!data) {
      markers.setMarkers([])
      zones.setZones([], 0)
      return
    }

    const bull = cssVar('--pos') || '#089981'
    const bear = cssVar('--neg') || '#f2364a'
    const muted = cssVar('--text-3') || '#8b8f9a'

    // Resolved once here, never inside the primitive: the boxes are recomputed
    // on every pan and zoom frame, and a getComputedStyle call in that loop
    // would read the same value back hundreds of times a second.
    const blockInk = {
      bullish: zoneInk('ob-bull', 'rgba(49, 196, 141, 0.10)', 'rgba(49, 196, 141, 0.55)'),
      bearish: zoneInk('ob-bear', 'rgba(244, 99, 94, 0.10)', 'rgba(244, 99, 94, 0.55)'),
    }
    const gapInk = {
      bullish: zoneInk('fvg-bull', 'rgba(49, 196, 141, 0.07)', 'rgba(49, 196, 141, 0.34)'),
      bearish: zoneInk('fvg-bear', 'rgba(244, 99, 94, 0.07)', 'rgba(244, 99, 94, 0.34)'),
    }
    const flowInk = {
      bullish: zoneInk('flow-bull', 'rgba(49, 196, 141, 0.20)', 'rgba(49, 196, 141, 0.60)'),
      bearish: zoneInk('flow-bear', 'rgba(244, 99, 94, 0.20)', 'rgba(244, 99, 94, 0.60)'),
    }

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

    /**
     * The span of one box in bar numbers, or null when the chart cannot say
     * where it belongs. The API trims to the last few thousand candles, so a
     * zone that has stood for a long time can outlive the candle it was cut
     * from; starting the box at the first candle still on screen would put its
     * edge somewhere the market never put it, so it is left out instead.
     */
    const indices = (fromTime: string, toTime: string | null) => {
      const fromIndex = indexByTime.get(ist(fromTime))
      if (fromIndex === undefined) return null
      if (toTime === null) return { fromIndex, toIndex: null }
      // An end always comes after its own start, and the start is on the chart,
      // so a miss here means the marks and the candles disagree about which
      // candles exist. Say nothing rather than guess at an edge.
      const toIndex = indexByTime.get(ist(toTime))
      return toIndex === undefined ? null : { fromIndex, toIndex }
    }

    const boxes: Zone[] = []

    // Drawn first, so the more specific boxes paint over it if they ever meet.
    if (layers.orderFlow) {
      let low = Infinity
      let high = -Infinity
      for (const [, candle] of candles) {
        if (candle.low < low) low = candle.low
        if (candle.high > high) high = candle.high
      }
      // A window with no candles, or one that never moved, gives the band no
      // height to be drawn at.
      const range = high - low
      for (const run of data.orderFlowRuns) {
        const span = range > 0 ? indices(run.fromTimeUtc, run.toTimeUtc) : null
        if (!span) continue
        const ink = flowInk[run.direction]
        boxes.push({
          ...span,
          top: low - range * FLOW_BAND_TOP,
          bottom: low - range * FLOW_BAND_BOTTOM,
          fill: ink.fill,
          edge: ink.edge,
          // On a run the dash does not mean the right edge is still open — a
          // band that runs to the edge of the chart says that by itself. It
          // means this leg's inducement has not been swept yet, which is the
          // moment the break behind the run becomes something to act on.
          dashed: run.inducedTimeUtc === null,
        })
      }
    }

    if (layers.fvg) {
      // Every gap that came back is drawn. Which gaps come back is the caller's
      // question, asked of the server (`standingZonesOnly`) — most gaps fill
      // within a few candles, and pruning them here meant fetching hundreds of
      // spent ones on every poll only to skip them in this loop.
      for (const gap of data.gaps) {
        const span = indices(gap.timeUtc, gap.filledTimeUtc)
        if (!span) continue
        const ink = gapInk[gap.direction]
        // The box reaches back to the first of the three candles, which is
        // earlier than the candle that confirmed the gap. That is the same
        // licence a BOS line takes in running back to the level it broke: the
        // mark does not appear before its confirming candle, it only reaches
        // back once it does.
        boxes.push({
          ...span,
          top: gap.top,
          bottom: gap.bottom,
          fill: ink.fill,
          edge: ink.edge,
          dashed: gap.filledTimeUtc === null,
          label: 'FVG',
        })
      }
    }

    if (layers.orderBlocks) {
      for (const block of data.orderBlocks) {
        const span = indices(block.timeUtc, block.mitigatedTimeUtc)
        if (!span) continue
        const ink = blockInk[block.direction]
        // Dashed while the block still stands, solid once price has come back
        // and used it: mitigated is a stamp the engine makes, not a reading
        // taken here, and it never comes off again.
        boxes.push({
          ...span,
          top: block.top,
          bottom: block.bottom,
          fill: ink.fill,
          edge: ink.edge,
          dashed: block.mitigatedTimeUtc === null,
          label: 'OB',
        })
      }
    }

    // An open box carries toIndex null and the primitive resolves it against
    // this index on every frame, so a poll that adds candles extends it without
    // anything being pushed here.
    zones.setZones(boxes, candles.length - 1)
  }, [data, layers, candles, indexByTime])

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
