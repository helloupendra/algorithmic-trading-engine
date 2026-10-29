/**
 * The two charts of Straddle & IV, on lightweight-charts like the console's
 * other time series.
 *
 * TimeOfDayChart lays sessions over each other by the minute of the IST day.
 * The chosen day is the one coloured line (brand, 2px); the days compared
 * with it are context, drawn 1px in one neutral ink and told apart by their
 * geometry (solid, long dashes, dots: the slot they were added to). One hue
 * and three patterns stay apart for every reader; four hues on lines that
 * cross cannot (the palette check fails any four of them on all pairs). The
 * readout names every line with its value at the crosshair, so the pattern
 * is never the only way to tell which day is which.
 *
 * DailyIvChart is the ATM IV by day: the close as the line, the open as a
 * dotted one, expiry days marked, a hairline at the value now.
 */

import { useEffect, useMemo, useRef, useState } from 'react'
import {
  createChart,
  createSeriesMarkers,
  LineSeries,
  LineStyle,
  type IChartApi,
  type IPriceLine,
  type ISeriesApi,
  type ISeriesMarkersPluginApi,
  type Time,
  type UTCTimestamp,
} from 'lightweight-charts'
import { chartOptions, cssVar } from '../../../lib/chartTheme'
import { useTheme } from '../../../lib/theme'
import { formatInr } from '../../../lib/format'
import { dayLabel } from '../../../lib/straddle'
import type { IvHistoryDay, LinePoint } from '../../../lib/straddle'

/**
 * The style of each slot: the chosen day, then the three compared. Short
 * dashes and long ones are a pixel apart at 1px and read as one; a solid, a
 * long dash and a dot do not.
 */
const SLOT_STYLES = [LineStyle.Solid, LineStyle.Solid, LineStyle.LargeDashed, LineStyle.Dotted] as const
const SLOT_CLASSES = ['sv-key--0', 'sv-key--1', 'sv-key--2', 'sv-key--3'] as const

/** Any day will do: only the time of day is ever drawn or printed. */
const BASE_SECONDS = Date.UTC(2000, 0, 3) / 1000

const timeAt = (minute: number) => (BASE_SECONDS + minute * 60) as UTCTimestamp
const minuteAt = (time: Time) => (typeof time === 'number' ? Math.round((time - BASE_SECONDS) / 60) : null)
const clock = (minute: number) => `${String(Math.floor(minute / 60)).padStart(2, '0')}:${String(minute % 60).padStart(2, '0')}`

export type Unit = 'rupees' | 'percentOfFirst' | 'iv'

function formatValue(value: number | null | undefined, unit: Unit): string {
  if (value == null || !Number.isFinite(value)) return '—'
  if (unit === 'rupees') return formatInr(value)
  return `${value.toFixed(1)}%`
}

/** One session drawn against the time of day. */
export interface OverlayLine {
  /** The session ("yyyy-mm-dd"). */
  key: string
  /** 0 for the chosen day, 1 to 3 for the compared ones. */
  slot: number
  points: LinePoint[]
}

/** The line style's key, as the readout and the chips show it. */
export function SlotKey({ slot }: { slot: number }) {
  return <span className={`sv-key ${SLOT_CLASSES[slot] ?? ''}`} aria-hidden="true" />
}

export function TimeOfDayChart({
  lines,
  unit,
  fitKey,
  label,
}: {
  lines: readonly OverlayLine[]
  unit: Unit
  /** Refit the time range when this changes (another day, another reading), or when a line comes or goes. */
  fitKey: string
  label: string
}) {
  const ref = useRef<HTMLDivElement | null>(null)
  const chartRef = useRef<IChartApi | null>(null)
  const seriesRef = useRef(new Map<string, ISeriesApi<'Line'>>())
  const fittedRef = useRef<string | null>(null)
  const [hover, setHover] = useState<number | null>(null)

  // The tokens are read once, when the chart is created: a theme change makes a new chart.
  const { theme } = useTheme()
  useEffect(() => {
    const el = ref.current
    if (!el) return
    const base = chartOptions(el)
    const chart = createChart(el, {
      ...base,
      timeScale: { ...base.timeScale, tickMarkFormatter: (time: Time) => clock(minuteAt(time) ?? 0) },
      localization: { timeFormatter: (time: Time) => clock(minuteAt(time) ?? 0) },
    })
    chart.subscribeCrosshairMove((param) => setHover(param.time != null ? minuteAt(param.time) : null))
    chartRef.current = chart

    const observer = new ResizeObserver(() => {
      if (el.clientWidth > 0) chart.applyOptions({ width: el.clientWidth, height: el.clientHeight })
    })
    observer.observe(el)
    const series = seriesRef.current
    return () => {
      observer.disconnect()
      chart.remove()
      chartRef.current = null
      series.clear()
      fittedRef.current = null
    }
  }, [theme])

  useEffect(() => {
    chartRef.current?.applyOptions({ localization: { priceFormatter: (p: number) => formatValue(p, unit) } })
  }, [unit, theme])

  useEffect(() => {
    const chart = chartRef.current
    if (!chart) return
    const brand = cssVar('--brand')
    const past = cssVar('--text-3')
    const shown = seriesRef.current

    const wanted = new Set(lines.map((l) => l.key))
    for (const [key, series] of shown) {
      if (!wanted.has(key)) {
        chart.removeSeries(series)
        shown.delete(key)
      }
    }

    for (const line of lines) {
      const chosen = line.slot === 0
      const options = {
        color: chosen ? brand : past,
        lineWidth: (chosen ? 2 : 1) as 1 | 2,
        lineStyle: SLOT_STYLES[line.slot] ?? LineStyle.Solid,
        priceLineVisible: false,
        lastValueVisible: chosen,
        crosshairMarkerRadius: chosen ? 4 : 3,
      }
      let series = shown.get(line.key)
      if (series) series.applyOptions(options)
      else {
        series = chart.addSeries(LineSeries, options)
        shown.set(line.key, series)
      }
      // A minute nobody priced is whitespace: a gap in the line, not a straight edge across it.
      series.setData(line.points.map((p) => (p.value == null ? { time: timeAt(p.minute) } : { time: timeAt(p.minute), value: p.value })))
    }

    // The chosen day draws over the days it is compared with.
    const chosenKey = lines.find((l) => l.slot === 0)?.key
    if (chosenKey) shown.get(chosenKey)?.setSeriesOrder(shown.size - 1)

    // A compared day reaching further than the chosen one widens the time
    // scale; the view kept its old bar range and slid off the chosen day.
    const fit = `${fitKey}|${lines.map((l) => l.key).join(',')}`
    if (fittedRef.current !== fit && lines.some((l) => l.points.length > 0)) {
      fittedRef.current = fit
      chart.timeScale().fitContent()
    }
  }, [lines, fitKey, theme])

  // At the crosshair's minute; with the pointer elsewhere, at the chosen
  // day's newest minute, each compared day at its value by that time of day.
  const byMinute = useMemo(() => lines.map((l) => new Map(l.points.map((p) => [p.minute, p.value]))), [lines])
  const chosen = lines.find((l) => l.slot === 0)
  const lastMinute = chosen ? [...chosen.points].reverse().find((p) => p.value != null)?.minute ?? null : null
  const minute = hover ?? lastMinute
  const valueAt = (i: number, at: number) => (hover == null ? lastAtOrBefore(lines[i].points, at) : byMinute[i].get(at) ?? null)

  return (
    <div className="chart-wrap">
      <div className="chart-readout sv-readout" aria-live="off">
        {minute == null ? (
          <span className="muted">Nothing priced on this day yet.</span>
        ) : (
          <>
            <span className="chart-readout__time mono">{clock(minute)} IST</span>
            {lines.map((line, i) => (
              <span key={line.key} className="sv-readout__item">
                <SlotKey slot={line.slot} />
                <span className={line.slot === 0 ? '' : 'muted'}>{dayLabel(line.key)}</span>
                <b className="mono">{formatValue(valueAt(i, minute), unit)}</b>
              </span>
            ))}
          </>
        )}
      </div>
      <div ref={ref} className="chart sv-chart" role="img" aria-label={label} />
    </div>
  )
}

/** A line's value at a minute, or its last before it when that minute went unpriced. */
function lastAtOrBefore(points: readonly LinePoint[], minute: number): number | null {
  let value: number | null = null
  for (const p of points) {
    if (p.minute > minute) break
    if (p.value != null) value = p.value
  }
  return value
}

export function DailyIvChart({ days, currentIv }: { days: readonly IvHistoryDay[]; currentIv: number | null }) {
  const ref = useRef<HTMLDivElement | null>(null)
  const chartRef = useRef<IChartApi | null>(null)
  const closeRef = useRef<ISeriesApi<'Line'> | null>(null)
  const openRef = useRef<ISeriesApi<'Line'> | null>(null)
  const markersRef = useRef<ISeriesMarkersPluginApi<Time> | null>(null)
  const nowLineRef = useRef<IPriceLine | null>(null)
  const [hover, setHover] = useState<string | null>(null)

  const { theme } = useTheme()
  useEffect(() => {
    const el = ref.current
    if (!el) return
    const base = chartOptions(el)
    const chart = createChart(el, {
      ...base,
      timeScale: { ...base.timeScale, timeVisible: false },
      localization: { priceFormatter: (p: number) => `${p.toFixed(1)}%` },
    })
    const open = chart.addSeries(LineSeries, {
      color: cssVar('--text-3'),
      lineWidth: 1,
      lineStyle: LineStyle.Dotted,
      priceLineVisible: false,
      lastValueVisible: false,
      crosshairMarkerRadius: 3,
    })
    const close = chart.addSeries(LineSeries, {
      color: cssVar('--brand'),
      lineWidth: 2,
      priceLineVisible: false,
      lastValueVisible: false,
      crosshairMarkerRadius: 4,
    })
    chart.subscribeCrosshairMove((param) => setHover(dateOf(param.time)))
    chartRef.current = chart
    closeRef.current = close
    openRef.current = open
    markersRef.current = createSeriesMarkers(close, [])

    const observer = new ResizeObserver(() => {
      if (el.clientWidth > 0) chart.applyOptions({ width: el.clientWidth, height: el.clientHeight })
    })
    observer.observe(el)
    return () => {
      observer.disconnect()
      chart.remove()
      chartRef.current = null
      closeRef.current = null
      openRef.current = null
      markersRef.current = null
      nowLineRef.current = null
    }
  }, [theme])

  useEffect(() => {
    const chart = chartRef.current
    const close = closeRef.current
    const open = openRef.current
    if (!chart || !close || !open) return
    close.setData(days.map((d) => (d.closeIv == null ? { time: d.date } : { time: d.date, value: d.closeIv })))
    open.setData(days.map((d) => (d.openIv == null ? { time: d.date } : { time: d.date, value: d.openIv })))
    // Expiry days, where the nearest contract has hours to live and its IV is at its noisiest.
    markersRef.current?.setMarkers(
      days
        .filter((d) => d.date === d.expiryDate && d.closeIv != null)
        .map((d) => ({ time: d.date, position: 'belowBar' as const, shape: 'circle' as const, color: cssVar('--text-3'), text: 'E', size: 0.6 })),
    )
    if (nowLineRef.current) close.removePriceLine(nowLineRef.current)
    nowLineRef.current = currentIv == null
      ? null
      : close.createPriceLine({ price: currentIv, color: cssVar('--brand'), lineWidth: 1, lineStyle: LineStyle.Dashed, axisLabelVisible: true, title: 'now' })
    chart.timeScale().fitContent()
  }, [days, currentIv, theme])

  const shown = days.find((d) => d.date === hover) ?? days[days.length - 1] ?? null

  return (
    <div className="chart-wrap">
      <div className="chart-readout sv-readout" aria-live="off">
        {shown ? (
          <>
            <span className="chart-readout__time mono">{dayLabel(shown.date)}</span>
            <span className="sv-readout__item">
              <span className="sv-key sv-key--0" aria-hidden="true" />
              close <b className="mono">{formatValue(shown.closeIv, 'iv')}</b>
            </span>
            <span className="sv-readout__item">
              <span className="sv-key sv-key--3" aria-hidden="true" />
              open <b className="mono">{formatValue(shown.openIv, 'iv')}</b>
            </span>
            <span className="muted">
              {shown.daysToExpiry === 0 ? 'expiry day' : `${shown.daysToExpiry} ${shown.daysToExpiry === 1 ? 'day' : 'days'} to expiry`}
            </span>
          </>
        ) : (
          <span className="muted">No session recorded yet.</span>
        )}
      </div>
      <div ref={ref} className="chart sv-chart sv-chart--short" role="img" aria-label="ATM IV by day: open and close" />
    </div>
  )
}

/** A daily chart's crosshair time as "yyyy-mm-dd", whichever form the library hands back. */
function dateOf(time: Time | undefined): string | null {
  if (time == null) return null
  if (typeof time === 'string') return time
  if (typeof time === 'object') return `${time.year}-${String(time.month).padStart(2, '0')}-${String(time.day).padStart(2, '0')}`
  return new Date(time * 1000).toISOString().slice(0, 10)
}
