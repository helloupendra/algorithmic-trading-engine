/**
 * Markets → Option chain → OI history: how open interest moved at one strike
 * through the session.
 *
 * The chain says what a strike looks like now. This says how it got there —
 * and the two answer different questions. A strike carrying two lakh contracts
 * tells you nothing on its own; two lakh that arrived in the last twenty
 * minutes, against a call side that has been shrinking all day, tells you where
 * the market has been putting its money.
 *
 * Plotted as CHANGE since the session opened rather than as the level, because
 * the level is dominated by whatever was already there at 09:15 and barely
 * moves on the scale of a day. The difference line (put change minus call
 * change) is the one to read: above zero, puts are being written faster than
 * calls.
 *
 * Drawn as inline SVG, in pixel units at the panel's width like the charts
 * under the chain. The series is a few hundred points of two or three lines,
 * which is a polyline — a charting library would be more code than the chart.
 */

import { useMemo, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useOptionChainSeries, useOptionChainView } from '../../../lib/queries'
import type { OptionChainSeries, OptionChainSeriesPoint } from '../../../lib/types'
import { capturedExpiry, expiryLabel, istDate } from '../../../lib/optionChain'
import { formatPrice } from '../../../lib/format'
import { EmptyState, InlineError, Loading, Panel, QueryBoundary } from '../../../components/ui'
import { useWidth } from './useWidth'
import './chain-views.css'

type View = 'oiChange' | 'oiLevel' | 'price' | 'volume'

const VIEWS: { key: View; label: string; hint: string }[] = [
  { key: 'oiChange', label: 'OI change', hint: 'Contracts opened or closed since the session began' },
  { key: 'oiLevel', label: 'OI', hint: 'Total contracts outstanding' },
  { key: 'price', label: 'Premium', hint: 'What each side costs' },
  { key: 'volume', label: 'Volume', hint: 'Contracts traded today' },
]

interface Line {
  label: string
  colour: string
  values: (number | null)[]
}

function linesFor(view: View, points: OptionChainSeriesPoint[]): Line[] {
  switch (view) {
    case 'oiChange':
      return [
        { label: 'Put OI chg', colour: 'var(--pos)', values: points.map((p) => p.putOpenInterestChange) },
        { label: 'Call OI chg', colour: 'var(--neg)', values: points.map((p) => p.callOpenInterestChange) },
        { label: 'Put − Call', colour: 'var(--brand)', values: points.map((p) => p.openInterestChangeDifference) },
      ]
    case 'oiLevel':
      return [
        { label: 'Put OI', colour: 'var(--pos)', values: points.map((p) => p.putOpenInterest) },
        { label: 'Call OI', colour: 'var(--neg)', values: points.map((p) => p.callOpenInterest) },
      ]
    case 'price':
      return [
        { label: 'Put LTP', colour: 'var(--pos)', values: points.map((p) => p.putLastTradedPrice) },
        { label: 'Call LTP', colour: 'var(--neg)', values: points.map((p) => p.callLastTradedPrice) },
      ]
    case 'volume':
      return [
        { label: 'Put volume', colour: 'var(--pos)', values: points.map((p) => p.putVolume) },
        { label: 'Call volume', colour: 'var(--neg)', values: points.map((p) => p.callVolume) },
      ]
  }
}

function compact(value: number): string {
  const abs = Math.abs(value)
  if (abs >= 1e7) return `${(value / 1e7).toFixed(1)}Cr`
  if (abs >= 1e5) return `${(value / 1e5).toFixed(2)}L`
  if (abs >= 1e3) return `${(value / 1e3).toFixed(1)}K`
  return value.toFixed(abs < 10 ? 2 : 0)
}

function SeriesChart({ series, view }: { series: OptionChainSeries; view: View }) {
  const lines = useMemo(() => linesFor(view, series.points), [view, series.points])
  const [ref, width] = useWidth<HTMLDivElement>()

  const { min, max } = useMemo(() => {
    const all = lines.flatMap((l) => l.values).filter((v): v is number => v != null)
    if (all.length === 0) return { min: 0, max: 1 }
    let lo = Math.min(...all)
    let hi = Math.max(...all)
    // Keep zero in frame whenever the data crosses it: on a change chart the
    // zero line is the whole reading, and cropping it would hide the sign.
    if (view === 'oiChange') {
      lo = Math.min(lo, 0)
      hi = Math.max(hi, 0)
    }
    if (lo === hi) { lo -= 1; hi += 1 }
    const pad = (hi - lo) * 0.08
    return { min: lo - pad, max: hi + pad }
  }, [lines, view])

  // The panel's width, 300px tall on a desk screen and 240px on a phone: the
  // ticks are 10px text at either. The right pad holds the value labels.
  const W = width
  const H = width < 560 ? 240 : 300
  const PAD = { top: 12, right: 64, bottom: 26, left: 8 }
  const plotW = W - PAD.left - PAD.right
  const plotH = H - PAD.top - PAD.bottom
  const n = series.points.length

  const x = (i: number) => PAD.left + (n <= 1 ? plotW / 2 : (i / (n - 1)) * plotW)
  const y = (v: number) => PAD.top + plotH - ((v - min) / (max - min)) * plotH

  if (n === 0) {
    return <EmptyState>Nothing captured for this strike in the selected window.</EmptyState>
  }

  const zeroY = min <= 0 && max >= 0 ? y(0) : null
  const ticks = [max, min + (max - min) * 0.5, min]

  return (
    <div>
      <div className="chip-row oih-legend">
        {lines.map((l) => (
          <span key={l.label} className="oi-legend">
            <span className="oi-swatch" style={{ background: l.colour }} />
            {l.label}
          </span>
        ))}
      </div>

      <div ref={ref} className="oih-plot">
        {width > 0 && (
        <svg width={W} height={H} role="img"
             aria-label={`${series.underlying} ${series.strikePrice} ${view}`}>
          {ticks.map((t, i) => (
            <g key={i}>
              <line x1={PAD.left} x2={PAD.left + plotW} y1={y(t)} y2={y(t)} className="oi-grid" />
              <text x={PAD.left + plotW + 6} y={y(t) + 4} className="oi-axis">{compact(t)}</text>
            </g>
          ))}

          {zeroY != null && (
            <>
              <line x1={PAD.left} x2={PAD.left + plotW} y1={zeroY} y2={zeroY} className="oi-zero" />
              <text x={PAD.left + plotW + 6} y={zeroY + 4} className="oi-axis oi-axis--zero">0</text>
            </>
          )}

          {lines.map((line) => {
            // Broken into segments so a gap in capture leaves a gap in the line
            // rather than a straight edge across the minutes nobody recorded.
            const segments: string[] = []
            let current: string[] = []
            line.values.forEach((v, i) => {
              if (v == null) {
                if (current.length > 1) segments.push(current.join(' '))
                current = []
                return
              }
              current.push(`${current.length === 0 ? 'M' : 'L'} ${x(i).toFixed(1)} ${y(v).toFixed(1)}`)
            })
            if (current.length > 1) segments.push(current.join(' '))

            return segments.map((d, i) => (
              <path key={`${line.label}-${i}`} d={d} className="oi-line" style={{ stroke: line.colour }} />
            ))
          })}

          <text x={PAD.left} y={H - 8} className="oi-axis">
            {new Date(series.points[0].capturedUtc).toLocaleTimeString('en-IN', { timeZone: 'Asia/Kolkata', hour: '2-digit', minute: '2-digit' })}
          </text>
          <text x={PAD.left + plotW} y={H - 8} className="oi-axis" textAnchor="end">
            {new Date(series.points[n - 1].capturedUtc).toLocaleTimeString('en-IN', { timeZone: 'Asia/Kolkata', hour: '2-digit', minute: '2-digit' })}
          </text>
        </svg>
        )}
      </div>
    </div>
  )
}

/** One strike's session, for the underlying the page is on and the expiry the chain view picked. */
export function OiHistoryView({ underlying }: { underlying: string }) {
  const [params] = useSearchParams()
  const expiry = params.get('expiry') ?? undefined
  const [strike, setStrike] = useState<number | null>(null)
  const [view, setView] = useState<View>('oiChange')

  // The chain view's own query, so switching tabs costs no request.
  const chain = useOptionChainView(underlying, expiry)
  // keepPreviousData must never show one underlying's strikes under another's name.
  const data = chain.data && chain.data.underlying === underlying ? chain.data : undefined

  // Default to the money: it is the strike anyone opens this view to look at.
  // A strike picked earlier that is no longer listed (the chain moved on, or
  // another underlying) falls back to the money rather than showing nothing.
  const strikes = data?.strikes.map((s) => s.strikePrice) ?? []
  const selected = (strike != null && strikes.includes(strike) ? strike : null) ?? data?.atTheMoneyStrike ?? strikes[0] ?? null
  const selectedIndex = selected != null ? strikes.indexOf(selected) : -1

  const series = useOptionChainSeries(underlying, selected, data?.expiryDate)
  // The expiry as the chain's toolbar prints it ("29 Sep 2026 (+2 days)"), not the ISO date.
  const expiryNote = data && capturedExpiry(data.expiryDate)

  if (chain.isError && !data) return <InlineError error={chain.error} />
  if (!data) return <Loading label={`Loading the ${underlying} chain…`} />
  if (data.strikes.length === 0) {
    return (
      <EmptyState>
        No chain has been captured for {underlying} yet. Open interest is only recorded while the chain recorder runs.
      </EmptyState>
    )
  }

  return (
    <>
      {/* A dropdown, not a row of every strike: a BANKNIFTY chain lists a hundred-odd
          strikes, and the row ran off the screen. The arrows step one strike either way. */}
      <div className="oc-toolbar">
        <span className="oc-control__label">Strike</span>
        <button
          type="button"
          className="btn btn--ghost btn--sm"
          aria-label="Previous strike"
          disabled={selectedIndex <= 0}
          onClick={() => setStrike(strikes[selectedIndex - 1])}
        >
          ‹
        </button>
        <select
          className="field__input field__input--sm oi-strike"
          aria-label="Strike"
          value={selected ?? ''}
          onChange={(e) => setStrike(Number(e.target.value))}
        >
          {data.strikes.map((s) => (
            <option key={s.strikePrice} value={s.strikePrice}>
              {s.strikePrice.toLocaleString('en-IN')}
              {s.isAtTheMoney ? ' · ATM' : ''}
            </option>
          ))}
        </select>
        <button
          type="button"
          className="btn btn--ghost btn--sm"
          aria-label="Next strike"
          disabled={selectedIndex < 0 || selectedIndex >= strikes.length - 1}
          onClick={() => setStrike(strikes[selectedIndex + 1])}
        >
          ›
        </button>
        {data.atTheMoneyStrike != null && selected !== data.atTheMoneyStrike && (
          <button type="button" className="btn btn--ghost btn--sm" onClick={() => setStrike(data.atTheMoneyStrike)}>
            Back to ATM {data.atTheMoneyStrike.toLocaleString('en-IN')}
          </button>
        )}
        <span className="oc-toolbar__spacer" />
        <div className="oc-seg" role="group" aria-label="Series">
          {VIEWS.map((v) => (
            <button key={v.key} type="button" title={v.hint} aria-pressed={view === v.key} onClick={() => setView(v.key)}>
              {v.label}
            </button>
          ))}
        </div>
      </div>

      <Panel
        title={`${underlying} ${selected != null ? selected.toLocaleString('en-IN') : '—'}${selected === data.atTheMoneyStrike ? ' · ATM' : ''}`}
        actions={
          <span className="faint small">
            {data.spotPrice > 0 && `spot ${formatPrice(data.spotPrice)}`}
            {data.spotPrice > 0 && expiryNote && ' · '}
            {expiryNote && `expiry ${expiryLabel(expiryNote, istDate(Date.now()))}`}
          </span>
        }
      >
        {/* QueryBoundary shows the series' error itself; a second InlineError here drew it twice. */}
        <QueryBoundary query={series}>
          {(points) =>
            points.openInterestUnavailable && view.startsWith('oi') ? (
              <EmptyState>
                No open interest was recorded for this strike in this window. The broker's tick feed does not carry it,
                so it exists only where the chain recorder was running, and it cannot be filled in afterwards. Premium
                and volume are still available.
              </EmptyState>
            ) : (
              <SeriesChart series={points} view={view} />
            )
          }
        </QueryBoundary>
      </Panel>
      <p className="small-note muted">
        Plotted as change since the open rather than as the level: the level is dominated by whatever was already
        there at the bell and barely moves on the scale of a day. Above zero on Put − Call, puts are being written
        faster than calls.
      </p>
    </>
  )
}
