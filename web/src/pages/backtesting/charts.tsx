/**
 * Backtesting module — the two result charts.
 *
 * EquityCurveChart: one series (equity), so one hue — the brand token — as a
 * 2px line with a 10% wash, a hairline at the starting capital and a readout
 * that follows the crosshair (every value is also in the positions table and
 * the metric tiles, so the tooltip never gates anything). Timestamps are
 * shifted so the axis reads in IST.
 *
 * DailyPnlChart: diverging bars per IST day (--pos above the baseline, --neg
 * below), square at the baseline with a rounded data-end, a 2px surface gap
 * between neighbours, clean rupee ticks, and a full-height hit area per day so
 * the reader aims at a date rather than a 3px bar. A table twin sits beside
 * it on the run page.
 */

import { useEffect, useMemo, useRef, useState } from 'react'
import {
  AreaSeries,
  ColorType,
  createChart,
  LineStyle,
  type IChartApi,
  type IPriceLine,
  type ISeriesApi,
  type UTCTimestamp,
} from 'lightweight-charts'
import { formatDateTime, formatInrSigned, formatInrWhole, formatNumber } from '../../lib/format'
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
  const priceLineRef = useRef<IPriceLine | null>(null)
  const lastFitKey = useRef<string | null>(null)
  const byTime = useRef(new Map<number, BacktestEquityPoint>())
  const [hover, setHover] = useState<BacktestEquityPoint | null>(null)

  // Ascending, unique per second (lightweight-charts requires both); the
  // time is shifted so the axis labels read as IST.
  const sorted = useMemo(() => {
    const map = new Map<number, BacktestEquityPoint>()
    for (const p of points) {
      const t = Math.floor(new Date(p.atUtc).getTime() / 1000)
      if (!Number.isNaN(t)) map.set(t + IST_OFFSET_S, p)
    }
    return [...map.entries()].sort((a, b) => a[0] - b[0])
  }, [points])

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
      localization: { priceFormatter: (p: number) => formatInrWhole(p) },
    })

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

    chart.subscribeCrosshairMove((param) => {
      const t = typeof param.time === 'number' ? param.time : null
      setHover(t != null ? (byTime.current.get(t) ?? null) : null)
    })

    chartRef.current = chart
    seriesRef.current = series

    const observer = new ResizeObserver(() => {
      if (el.clientWidth > 0) chart.applyOptions({ width: el.clientWidth, height: el.clientHeight })
    })
    observer.observe(el)

    return () => {
      observer.disconnect()
      chart.remove()
      chartRef.current = null
      seriesRef.current = null
      priceLineRef.current = null
      lastFitKey.current = null
    }
  }, [])

  useEffect(() => {
    const series = seriesRef.current
    const chart = chartRef.current
    if (!series || !chart) return

    byTime.current = new Map(sorted)
    series.setData(sorted.map(([time, p]) => ({ time: time as UTCTimestamp, value: p.equity })))

    if (priceLineRef.current) series.removePriceLine(priceLineRef.current)
    priceLineRef.current = series.createPriceLine({
      price: initialCapital,
      color: cssVar('--text-3'),
      lineWidth: 1,
      lineStyle: LineStyle.Solid,
      axisLabelVisible: true,
      title: 'start',
    })

    if (follow || lastFitKey.current !== fitKey) {
      lastFitKey.current = fitKey
      chart.timeScale().fitContent()
    }
  }, [sorted, fitKey, follow, initialCapital])

  const shown = hover ?? sorted[sorted.length - 1]?.[1] ?? null

  return (
    <div className="chart-wrap">
      <div className="chart-readout" aria-live="off">
        {shown ? (
          <>
            <span className="chart-readout__time mono">{formatDateTime(shown.atUtc)}</span>
            <span className="chart-readout__key" aria-hidden="true" />
            <b>{formatInrWhole(shown.equity)}</b>
            <span>
              P&L <PnlValue value={shown.equity - initialCapital} />
            </span>
            <span className="muted">realized {formatInrSigned(shown.realized)}</span>
            <span className="muted">unrealized {formatInrSigned(shown.unrealized)}</span>
          </>
        ) : (
          <span className="muted">No equity points yet — one is written per bar.</span>
        )}
      </div>
      <div ref={containerRef} className="chart" />
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

/** 1/2/5 × 10^k so that the extreme splits into 2–5 clean ticks. */
function niceStep(maxAbs: number): number {
  const raw = maxAbs / 4
  const mag = 10 ** Math.floor(Math.log10(Math.max(raw, 1)))
  for (const m of [1, 2, 5, 10]) {
    if (m * mag >= raw) return m * mag
  }
  return 10 * mag
}

/** Bar from the baseline to the value: square at the baseline, rounded at the data end. */
function barPath(x: number, w: number, yBase: number, yValue: number, radius: number): string {
  const top = Math.min(yBase, yValue)
  const bottom = Math.max(yBase, yValue)
  const r = Math.min(radius, (bottom - top) / 2, w / 2)
  if (yValue < yBase) {
    return `M${x},${bottom} V${top + r} Q${x},${top} ${x + r},${top} H${x + w - r} Q${x + w},${top} ${x + w},${top + r} V${bottom} Z`
  }
  return `M${x},${top} V${bottom - r} Q${x},${bottom} ${x + r},${bottom} H${x + w - r} Q${x + w},${bottom} ${x + w},${bottom - r} V${top} Z`
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
