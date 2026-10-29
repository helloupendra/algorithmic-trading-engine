/**
 * Backtesting module — the result charts.
 *
 * EquityCurveChart: one series (equity), so one hue — the brand token — as a
 * 2px line with a 10% wash, a hairline at the starting capital and a readout
 * that follows the crosshair (every value is also in the positions table and
 * the metric tiles, so the tooltip never gates anything). Under it, in a
 * second pane on the same time axis, the underwater curve: the balance less
 * its best so far, in --neg, with a dashed line at the deepest fall.
 * Timestamps are shifted so the axis reads in IST.
 *
 * CompareCurvesChart: two to four runs on one time axis, the same two panes.
 * Each run keeps its slot's colour (--run-1…4, chosen as a set against both
 * surfaces); the fourth is also dashed, because its colour alone sits too
 * close to the third's for some colour-blind readers. The readout is the
 * legend: every run's key, name and value at the crosshair.
 *
 * DailyPnlChart: diverging bars per IST day (--pos above the baseline, --neg
 * below), square at the baseline with a rounded data-end, a 2px surface gap
 * between neighbours, clean rupee ticks, and a full-height hit area per day so
 * the reader aims at a date rather than a 3px bar. A table twin sits beside
 * it on the run page.
 */

import { useTheme } from '../../lib/theme'
import { useEffect, useMemo, useRef, useState } from 'react'
import {
  AreaSeries,
  BaselineSeries,
  ColorType,
  createChart,
  LineSeries,
  LineStyle,
  TickMarkType,
  type IChartApi,
  type IPriceLine,
  type ISeriesApi,
  type Time,
  type UTCTimestamp,
} from 'lightweight-charts'
import { barPath, niceStep } from '../../lib/bars'
import { formatDateTime, formatInrSigned, formatInrWhole, formatNumber } from '../../lib/format'
import { underwater, type UnderwaterPoint } from '../../lib/drawdown'
import { curveValues, type CurveBase } from '../../lib/backtestCompare'
import type { BacktestDailyPnl, BacktestEquityPoint } from '../../lib/types'
import { formatDay, PnlValue } from '../strategies/shared'

const IST_OFFSET_S = 19_800

function cssVar(name: string): string {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim()
}

/** Appends an alpha byte to a 6-digit hex token; other formats pass through. */
function withAlpha(color: string, alphaHex: string): string {
  return /^#[0-9a-fA-F]{6}$/.test(color) ? `${color}${alphaHex}` : color
}

/* ------------------------------------------------------------ equity curve */

/**
 * A day tick names its month ("3 Sept"). Fitted to a long run the axis has
 * room for every second month only, and the day tick the library puts in the
 * gap after the last of them read as a bare "3". Every other tick keeps the
 * library's label (null). The stamps are already shifted to IST, hence UTC.
 */
function dayTickWithMonth(time: Time, type: TickMarkType, locale: string): string | null {
  if (type !== TickMarkType.DayOfMonth || typeof time !== 'number') return null
  return new Date(time * 1000).toLocaleDateString(locale, { day: 'numeric', month: 'short', timeZone: 'UTC' })
}

/** Seconds since the epoch, shifted so the axis reads IST; null for a time that is not one. */
function istSeconds(atUtc: string): number | null {
  const t = Math.floor(new Date(atUtc).getTime() / 1000)
  return Number.isNaN(t) ? null : t + IST_OFFSET_S
}

/** The layout both curve charts share: two panes on one time axis, the whole run edge to edge. */
function createCurvesChart(el: HTMLDivElement): IChartApi {
  return createChart(el, {
    width: el.clientWidth,
    height: el.clientHeight,
    layout: {
      background: { type: ColorType.Solid, color: 'transparent' },
      textColor: cssVar('--text-2'),
      fontSize: 11,
      attributionLogo: false,
      panes: { separatorColor: cssVar('--line'), separatorHoverColor: cssVar('--surface2'), enableResize: false },
    },
    grid: {
      vertLines: { color: cssVar('--line-soft') },
      horzLines: { color: cssVar('--line-soft') },
    },
    rightPriceScale: { borderColor: cssVar('--line') },
    // The whole run, edge to edge. With both edges fixed the library lets a
    // bar be as thin as the plot needs to hold every point. Left alone it
    // refuses anything under half a pixel, so a curve with more points than
    // twice the plot's width opened part-way through the run with nothing
    // saying so. Nothing scrolls or zooms out past the data, and the lock
    // keeps the range in view when the plot changes width.
    timeScale: {
      borderColor: cssVar('--line'),
      timeVisible: true,
      secondsVisible: false,
      fixLeftEdge: true,
      fixRightEdge: true,
      lockVisibleTimeRangeOnResize: true,
      tickMarkFormatter: dayTickWithMonth,
    },
    // A double-click on the time axis is the library's reset to six pixels a
    // bar, which on a long run is its last hundred points.
    handleScale: { axisDoubleClickReset: { time: false } },
    crosshair: { mode: 0 },
    localization: { priceFormatter: (p: number) => formatInrWhole(p) },
  })
}

/** Three parts balance to one part under water: the fall reads, the curve leads. */
function sizePanes(chart: IChartApi): void {
  const panes = chart.panes()
  if (panes.length < 2) return
  panes[0].setStretchFactor(3)
  panes[1].setStretchFactor(1)
}

/**
 * Keeps the plot the size of its box. The lock keeps the range for every view
 * but one: zoomed in and resting on the first point, the library also moves
 * the view by the width the plot gained, and it slid off the start. Only that
 * view is asked for again; any other request here could replace a fit that is
 * still waiting for its frame.
 */
function observeSize(el: HTMLDivElement, chart: IChartApi, pointCount: () => number): ResizeObserver {
  const observer = new ResizeObserver(() => {
    if (el.clientWidth <= 0) return
    const scale = chart.timeScale()
    const range = scale.getVisibleLogicalRange()
    chart.applyOptions({ width: el.clientWidth, height: el.clientHeight })
    const last = pointCount() - 1
    if (range && range.from <= 1e-6 && range.to < last - 1e-6) scale.setVisibleLogicalRange(range)
  })
  observer.observe(el)
  return observer
}

interface CurveSample {
  point: BacktestEquityPoint
  under: UnderwaterPoint
}

export function EquityCurveChart({
  points,
  initialCapital,
  fitKey,
  follow,
}: {
  points: BacktestEquityPoint[]
  initialCapital: number
  /** Refit the visible range when this changes. */
  fitKey: string
  /** Keep the whole curve in view as points arrive (running backtest). */
  follow: boolean
}) {
  const containerRef = useRef<HTMLDivElement | null>(null)
  const chartRef = useRef<IChartApi | null>(null)
  const seriesRef = useRef<ISeriesApi<'Area'> | null>(null)
  const underRef = useRef<ISeriesApi<'Baseline'> | null>(null)
  const priceLineRef = useRef<IPriceLine | null>(null)
  const deepestLineRef = useRef<IPriceLine | null>(null)
  const lastFitKey = useRef<string | null>(null)
  const byTime = useRef(new Map<number, CurveSample>())
  const [hover, setHover] = useState<CurveSample | null>(null)

  // Ascending and unique per second (lightweight-charts requires both), each
  // point with its place under water.
  const sorted = useMemo(() => {
    const map = new Map<number, BacktestEquityPoint>()
    for (const p of points) {
      const t = istSeconds(p.atUtc)
      if (t != null) map.set(t, p)
    }
    const entries = [...map.entries()].sort((a, b) => a[0] - b[0])
    const under = underwater(entries.map(([, p]) => p))
    return entries.map(([t, point], i): [number, CurveSample] => [t, { point, under: under[i] }])
  }, [points])

  // The tokens are read once, when the chart is created: a theme change makes a new chart.
  const { theme } = useTheme()
  useEffect(() => {
    const el = containerRef.current
    if (!el) return

    const chart = createCurvesChart(el)
    const brand = cssVar('--brand')
    const series = chart.addSeries(AreaSeries, {
      lineColor: brand,
      lineWidth: 2,
      topColor: withAlpha(brand, '2a'),
      bottomColor: withAlpha(brand, '00'),
      priceFormat: { type: 'price', precision: 0, minMove: 1 },
      crosshairMarkerRadius: 4,
      crosshairMarkerBorderColor: cssVar('--surface'),
      crosshairMarkerBorderWidth: 2,
      lastValueVisible: true,
      priceLineVisible: false,
    })

    // Every value is at or under 0, so only the baseline's lower half draws.
    const neg = cssVar('--neg')
    const under = chart.addSeries(
      BaselineSeries,
      {
        baseValue: { type: 'price', price: 0 },
        topLineColor: neg,
        topFillColor1: 'transparent',
        topFillColor2: 'transparent',
        bottomLineColor: neg,
        bottomFillColor1: withAlpha(neg, '10'),
        bottomFillColor2: withAlpha(neg, '38'),
        lineWidth: 2,
        priceFormat: { type: 'price', precision: 0, minMove: 1 },
        crosshairMarkerRadius: 3,
        crosshairMarkerBorderColor: cssVar('--surface'),
        lastValueVisible: false,
        priceLineVisible: false,
      },
      1,
    )
    sizePanes(chart)

    chart.subscribeCrosshairMove((param) => {
      const t = typeof param.time === 'number' ? param.time : null
      setHover(t != null ? (byTime.current.get(t) ?? null) : null)
    })

    chartRef.current = chart
    seriesRef.current = series
    underRef.current = under
    const observer = observeSize(el, chart, () => byTime.current.size)

    return () => {
      observer.disconnect()
      chart.remove()
      chartRef.current = null
      seriesRef.current = null
      underRef.current = null
      priceLineRef.current = null
      deepestLineRef.current = null
      lastFitKey.current = null
    }
  }, [theme])

  useEffect(() => {
    const series = seriesRef.current
    const under = underRef.current
    const chart = chartRef.current
    if (!series || !under || !chart) return

    byTime.current = new Map(sorted)
    series.setData(sorted.map(([time, s]) => ({ time: time as UTCTimestamp, value: s.point.equity })))
    under.setData(sorted.map(([time, s]) => ({ time: time as UTCTimestamp, value: s.under.drawdown })))

    if (priceLineRef.current) series.removePriceLine(priceLineRef.current)
    priceLineRef.current = series.createPriceLine({
      price: initialCapital,
      color: cssVar('--text-3'),
      lineWidth: 1,
      lineStyle: LineStyle.Solid,
      axisLabelVisible: true,
      title: 'start',
    })

    if (deepestLineRef.current) under.removePriceLine(deepestLineRef.current)
    deepestLineRef.current = null
    const deepest = sorted.reduce((min, [, s]) => Math.min(min, s.under.drawdown), 0)
    if (deepest < 0) {
      deepestLineRef.current = under.createPriceLine({
        price: deepest,
        color: cssVar('--text-3'),
        lineWidth: 1,
        lineStyle: LineStyle.Dashed,
        axisLabelVisible: true,
        title: 'deepest',
      })
    }

    if (follow || lastFitKey.current !== fitKey) {
      lastFitKey.current = fitKey
      chart.timeScale().fitContent()
    }
  }, [sorted, fitKey, follow, initialCapital, theme])

  const shown = hover ?? sorted[sorted.length - 1]?.[1] ?? null

  return (
    <div className="chart-wrap">
      <div className="chart-readout" aria-live="off">
        {shown ? (
          <>
            <span className="chart-readout__time mono">{formatDateTime(shown.point.atUtc)}</span>
            <span className="chart-readout__key" aria-hidden="true" />
            <b>{formatInrWhole(shown.point.equity)}</b>
            <span>
              P&L <PnlValue value={shown.point.equity - initialCapital} />
            </span>
            <span className="muted">realized {formatInrSigned(shown.point.realized)}</span>
            <span className="muted">unrealized {formatInrSigned(shown.point.unrealized)}</span>
            <span className="chart-readout__key chart-readout__key--neg" aria-hidden="true" />
            {shown.under.drawdown < 0 ? (
              <span>
                <span className="mono neg">{formatInrSigned(shown.under.drawdown)}</span>{' '}
                <span className="muted">({Math.abs(shown.under.drawdownPercent).toFixed(1)}%) from its best</span>
              </span>
            ) : (
              <span className="muted">at its high</span>
            )}
          </>
        ) : (
          <span className="muted">No equity points yet — one is written per bar.</span>
        )}
      </div>
      <div
        ref={containerRef}
        className="chart bt-curves"
        role="img"
        aria-label="Account balance over the run, and under it how far the balance stood below its best so far"
      />
    </div>
  )
}

/* ---------------------------------------------------------- compare curves */

export interface CompareCurve {
  runId: number
  /** "#12 Ghost · NIFTY". */
  label: string
  /** 0–3: the run's colour slot, kept for the run whatever else is on the chart. */
  slot: number
  points: BacktestEquityPoint[]
  initialCapital: number
}

interface TimedValue {
  t: number
  value: number
}

interface CompareSample {
  equity: TimedValue[]
  under: TimedValue[]
}

/** The value at or before `t`; null when `t` is outside the run's own span. */
function valueAt(series: TimedValue[], t: number): number | null {
  if (series.length === 0 || t < series[0].t || t > series[series.length - 1].t) return null
  let lo = 0
  let hi = series.length - 1
  while (lo < hi) {
    const mid = (lo + hi + 1) >> 1
    if (series[mid].t <= t) lo = mid
    else hi = mid - 1
  }
  return series[lo].value
}

/** The --run-N token of a colour slot. */
function runSlotToken(slot: number): string {
  return `--run-${(slot % 4) + 1}`
}

export function CompareCurvesChart({ curves, base }: { curves: CompareCurve[]; base: CurveBase }) {
  const containerRef = useRef<HTMLDivElement | null>(null)
  const chartRef = useRef<IChartApi | null>(null)
  const equityRefs = useRef<ISeriesApi<'Line'>[]>([])
  const underRefs = useRef<ISeriesApi<'Line'>[]>([])
  const zeroLineRef = useRef<IPriceLine | null>(null)
  const stamps = useRef(0)
  const [hoverT, setHoverT] = useState<number | null>(null)

  const samples = useMemo<CompareSample[]>(
    () =>
      curves.map((c) => {
        const map = new Map<number, BacktestEquityPoint>()
        for (const p of c.points) {
          const t = istSeconds(p.atUtc)
          if (t != null) map.set(t, p)
        }
        const entries = [...map.entries()].sort((a, b) => a[0] - b[0])
        const values = curveValues(entries.map(([, p]) => p), c.initialCapital, base)
        const under = underwater(entries.map(([, p]) => p))
        return {
          equity: entries.map(([t], i) => ({ t, value: values[i].value })),
          under: entries.map(([t], i) => ({ t, value: under[i].drawdown })),
        }
      }),
    [curves, base],
  )

  // A run added or dropped changes the series the chart holds: it is made again.
  const slots = curves.map((c) => c.slot).join(',')
  const { theme } = useTheme()
  useEffect(() => {
    const el = containerRef.current
    if (!el) return

    const chart = createCurvesChart(el)
    const surface = cssVar('--surface')
    const lineOf = (slot: number, pane: number) =>
      chart.addSeries(
        LineSeries,
        {
          color: cssVar(runSlotToken(slot)),
          lineWidth: 2,
          lineStyle: slot === 3 ? LineStyle.Dashed : LineStyle.Solid,
          priceFormat: { type: 'price', precision: 0, minMove: 1 },
          crosshairMarkerRadius: pane === 0 ? 4 : 3,
          crosshairMarkerBorderColor: surface,
          crosshairMarkerBorderWidth: 2,
          lastValueVisible: false,
          priceLineVisible: false,
        },
        pane,
      )
    const order = slots === '' ? [] : slots.split(',').map(Number)
    equityRefs.current = order.map((slot) => lineOf(slot, 0))
    underRefs.current = order.map((slot) => lineOf(slot, 1))
    sizePanes(chart)

    chart.subscribeCrosshairMove((param) => setHoverT(typeof param.time === 'number' ? param.time : null))
    chartRef.current = chart
    const observer = observeSize(el, chart, () => stamps.current)

    return () => {
      observer.disconnect()
      chart.remove()
      chartRef.current = null
      equityRefs.current = []
      underRefs.current = []
      zeroLineRef.current = null
    }
  }, [theme, slots])

  useEffect(() => {
    const chart = chartRef.current
    if (!chart || equityRefs.current.length !== samples.length) return
    samples.forEach((s, i) => {
      equityRefs.current[i].setData(s.equity.map((p) => ({ time: p.t as UTCTimestamp, value: p.value })))
      underRefs.current[i].setData(s.under.map((p) => ({ time: p.t as UTCTimestamp, value: p.value })))
    })
    stamps.current = new Set(samples.flatMap((s) => s.equity.map((p) => p.t))).size

    const first = equityRefs.current[0]
    if (zeroLineRef.current && first) first.removePriceLine(zeroLineRef.current)
    zeroLineRef.current =
      base === 'zero' && first
        ? first.createPriceLine({
            price: 0,
            color: cssVar('--text-3'),
            lineWidth: 1,
            lineStyle: LineStyle.Solid,
            axisLabelVisible: false,
            title: 'start',
          })
        : null
    chart.timeScale().fitContent()
  }, [samples, base, theme, slots])

  const lastT = samples.reduce((max, s) => Math.max(max, s.equity[s.equity.length - 1]?.t ?? 0), 0)
  const at = hoverT ?? (lastT > 0 ? lastT : null)

  return (
    <div className="chart-wrap">
      <div className="chart-readout bt-compare-readout" aria-live="off">
        <span className="chart-readout__time mono">
          {at != null ? formatDateTime(new Date((at - IST_OFFSET_S) * 1000).toISOString()) : '—'}
        </span>
        {curves.map((c, i) => {
          const value = at != null && samples[i] ? valueAt(samples[i].equity, at) : null
          const under = at != null && samples[i] ? valueAt(samples[i].under, at) : null
          return (
            <span key={c.runId} className="bt-compare-readout__run">
              <span className={`bt-run-key bt-run-key--${c.slot + 1}`} aria-hidden="true" />
              <span className="muted">{c.label}</span>
              {value == null ? (
                <span className="faint">not replaying then</span>
              ) : (
                <>
                  <b className="mono">{base === 'zero' ? formatInrSigned(value) : formatInrWhole(value)}</b>
                  {under != null && under < 0 && (
                    <span>
                      <span className="mono neg">{formatInrSigned(under)}</span>
                      <span className="muted"> from its best</span>
                    </span>
                  )}
                </>
              )}
            </span>
          )
        })}
      </div>
      <div
        ref={containerRef}
        className="chart bt-curves"
        role="img"
        aria-label={`${base === 'zero' ? 'P&L from each run’s start' : 'Account balance'} for ${curves.length} runs on one time axis, and under it how far each stood below its best so far`}
      />
    </div>
  )
}

/* -------------------------------------------------------------- daily P&L */

function useElementWidth(ref: React.RefObject<HTMLDivElement | null>, fallback: number): number {
  const [width, setWidth] = useState(fallback)
  useEffect(() => {
    const el = ref.current
    if (!el) return
    const observer = new ResizeObserver(() => {
      if (el.clientWidth > 0) setWidth(el.clientWidth)
    })
    observer.observe(el)
    if (el.clientWidth > 0) setWidth(el.clientWidth)
    return () => observer.disconnect()
  }, [ref])
  return width
}

export function DailyPnlChart({ days }: { days: BacktestDailyPnl[] }) {
  const ref = useRef<HTMLDivElement | null>(null)
  const width = useElementWidth(ref, 640)
  const [hovered, setHovered] = useState<number | null>(null)

  const sorted = useMemo(() => [...days].sort((a, b) => a.date.localeCompare(b.date)), [days])
  const n = sorted.length

  const height = 220
  const mL = 68
  const mR = 12
  const mT = 14
  const mB = 28
  const plotW = Math.max(40, width - mL - mR)
  const plotH = height - mT - mB

  const maxAbs = Math.max(1, ...sorted.map((d) => Math.abs(d.pnl)))
  const step = niceStep(maxAbs)
  const extent = Math.ceil(maxAbs / step) * step
  const hasPos = sorted.some((d) => d.pnl > 0)
  const hasNeg = sorted.some((d) => d.pnl < 0)
  const yMax = hasPos || !hasNeg ? extent : 0
  const yMin = hasNeg ? -extent : 0
  const y = (v: number) => mT + ((yMax - v) / (yMax - yMin)) * plotH
  const yZero = y(0)

  const slot = n > 0 ? plotW / n : plotW
  const barW = Math.min(24, Math.max(3, slot - 2))
  const xOf = (i: number) => mL + i * slot + (slot - barW) / 2

  const ticks: number[] = []
  for (let v = yMin; v <= yMax + 1e-9; v += step) ticks.push(v)
  const labelEvery = Math.max(1, Math.ceil(n / Math.max(1, Math.floor(plotW / 64))))

  const profitable = sorted.filter((d) => d.pnl > 0).length
  const active = hovered != null ? sorted[hovered] : null

  return (
    <div className="daily-pnl" ref={ref}>
      <div className="daily-pnl__readout" aria-live="off">
        {active ? (
          <>
            <span className="mono">{formatDay(active.date)}</span>
            <b>
              <PnlValue value={active.pnl} />
            </b>
            <span className="muted">
              {formatNumber(active.trades)} {active.trades === 1 ? 'trade' : 'trades'}
            </span>
          </>
        ) : n === 0 ? (
          <span className="muted">No trading days with P&L yet.</span>
        ) : (
          <>
            <span className="muted">
              {formatNumber(n)} {n === 1 ? 'day' : 'days'} · {formatNumber(profitable)} profitable
            </span>
            <span className="faint">hover a day for its P&L and trade count</span>
          </>
        )}
      </div>
      {n > 0 && (
        <svg
          viewBox={`0 0 ${width} ${height}`}
          width={width}
          height={height}
          role="img"
          aria-label="Daily profit and loss, one bar per IST trading day"
          onMouseLeave={() => setHovered(null)}
        >
          {ticks.map((v) => (
            <g key={v}>
              <line
                x1={mL}
                x2={mL + plotW}
                y1={y(v)}
                y2={y(v)}
                className={v === 0 ? 'daily-pnl__zero' : 'daily-pnl__grid'}
              />
              <text x={mL - 8} y={y(v) + 3.5} textAnchor="end" className="daily-pnl__tick">
                {formatInrSigned(v)}
              </text>
            </g>
          ))}
          {sorted.map((d, i) => {
            const isHover = hovered === i
            const showLabel = i % labelEvery === 0 || i === n - 1
            return (
              <g key={d.date}>
                {d.pnl !== 0 && (
                  <path
                    d={barPath(xOf(i), barW, yZero, y(d.pnl), 4)}
                    className={`daily-pnl__bar ${d.pnl > 0 ? 'daily-pnl__bar--pos' : 'daily-pnl__bar--neg'} ${isHover ? 'is-hover' : ''}`}
                  />
                )}
                {showLabel && (
                  <text
                    x={xOf(i) + barW / 2}
                    y={height - 8}
                    textAnchor="middle"
                    className="daily-pnl__tick"
                  >
                    {formatDay(d.date)}
                  </text>
                )}
                <rect
                  x={mL + i * slot}
                  y={mT}
                  width={slot}
                  height={plotH}
                  className="daily-pnl__hit"
                  tabIndex={0}
                  onMouseEnter={() => setHovered(i)}
                  onFocus={() => setHovered(i)}
                  onBlur={() => setHovered((h) => (h === i ? null : h))}
                >
                  <title>{`${formatDay(d.date)}: ${formatInrSigned(d.pnl)} · ${d.trades} trades`}</title>
                </rect>
              </g>
            )
          })}
        </svg>
      )}
    </div>
  )
}
