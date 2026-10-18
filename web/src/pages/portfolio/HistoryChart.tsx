/**
 * Value over time: the portfolio's current value and the amount invested, one
 * point a daily snapshot. Drawn in pixels at the width it is given, like the
 * Desk's P&L curve (never stretched from a viewBox, so text stays 10px), with
 * the snapshots evenly spaced: a weekend has no snapshot and takes no room.
 *
 * Hover or the arrow keys read one day: the date, both lines and the P&L.
 */

import { useState } from 'react'
import type { KeyboardEvent, MouseEvent } from 'react'
import { formatInrSigned, formatInrWhole } from '../../lib/format'
import { compactRupees, formatDay, signTone } from '../../lib/portfolio'
import type { HistoryPoint } from '../../lib/portfolio'
import { valueTicks } from '../../lib/pnlSeries'

const L = 48
const R = 10
const T = 8
const B = 18

type Drawn = HistoryPoint & { current: number }

function path(points: readonly { x: number; y: number }[]): string {
  return points.map((p, i) => `${i === 0 ? 'M' : 'L'}${p.x.toFixed(1)},${p.y.toFixed(1)}`).join('')
}

export function HistoryChart({ points, width, height = 168 }: { points: readonly HistoryPoint[]; width: number; height?: number }) {
  const [hover, setHover] = useState<number | null>(null)
  const drawn = points.filter((p): p is Drawn => p.current != null)
  if (width < 160 || drawn.length < 2) return <div style={{ height }} />

  const values = drawn.flatMap((p) => (p.invested == null ? [p.current] : [p.current, p.invested]))
  const lo0 = Math.min(...values)
  const hi0 = Math.max(...values)
  const span = Math.max(hi0 - lo0, hi0 * 0.02, 1)
  const domain = { lo: lo0 - span * 0.1, hi: hi0 + span * 0.1 }
  const plotW = width - L - R
  const plotH = height - T - B
  const x = (i: number) => L + (drawn.length === 1 ? plotW / 2 : (i / (drawn.length - 1)) * plotW)
  const y = (v: number) => T + (1 - (v - domain.lo) / (domain.hi - domain.lo)) * plotH
  const ticks = valueTicks(domain, 3).filter((v) => v >= domain.lo && v <= domain.hi)
  const dateTicks = [...new Set([0, 1, 2, 3].map((k) => Math.round((k / 3) * (drawn.length - 1))))]

  const currentLine = path(drawn.map((p, i) => ({ x: x(i), y: y(p.current) })))
  // The invested line is drawn where it is known; a gap breaks it rather than joining across.
  const investedRuns: { x: number; y: number }[][] = []
  drawn.forEach((p, i) => {
    if (p.invested == null) {
      investedRuns.push([])
      return
    }
    if (investedRuns.length === 0) investedRuns.push([])
    investedRuns[investedRuns.length - 1].push({ x: x(i), y: y(p.invested) })
  })

  const first = drawn[0]
  const last = drawn[drawn.length - 1]
  const summary =
    `Current value from ${formatInrWhole(first.current)} on ${formatDay(first.date)} to ${formatInrWhole(last.current)} on ${formatDay(last.date)}` +
    (last.invested != null ? `; invested ${formatInrWhole(last.invested)} at the end.` : '.') +
    ' Use the left and right arrow keys to read one day.'

  function onMove(e: MouseEvent<SVGSVGElement>) {
    const box = e.currentTarget.getBoundingClientRect()
    const px = e.clientX - box.left
    if (px < L - 4 || px > L + plotW + 4) return setHover(null)
    setHover(Math.max(0, Math.min(drawn.length - 1, Math.round(((px - L) / plotW) * (drawn.length - 1)))))
  }

  function onKey(e: KeyboardEvent<SVGSVGElement>) {
    if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight' && e.key !== 'Home' && e.key !== 'End') return
    e.preventDefault()
    setHover((h) => {
      const at = h ?? drawn.length - 1
      if (e.key === 'Home') return 0
      if (e.key === 'End') return drawn.length - 1
      return Math.max(0, Math.min(drawn.length - 1, at + (e.key === 'ArrowLeft' ? -1 : 1)))
    })
  }

  const hp = hover == null ? null : drawn[hover]
  const tipLeft = hover != null && x(hover) > width / 2

  return (
    <div className="pf-chart" style={{ height }}>
      <svg
        width={width}
        height={height}
        role="img"
        aria-label={summary}
        tabIndex={0}
        onMouseMove={onMove}
        onMouseLeave={() => setHover(null)}
        onKeyDown={onKey}
        onFocus={() => setHover((h) => h ?? drawn.length - 1)}
        onBlur={() => setHover(null)}
      >
        {ticks.map((v) => (
          <g key={v}>
            <line x1={L} x2={L + plotW} y1={y(v)} y2={y(v)} className="pf-chart__grid" />
            <text x={L - 6} y={y(v) + 3.5} textAnchor="end">
              {compactRupees(v)}
            </text>
          </g>
        ))}
        {dateTicks.map((i) => (
          <text key={i} x={Math.min(Math.max(x(i), L + 16), L + plotW - 16)} y={height - 4} textAnchor="middle">
            {formatDay(drawn[i].date)}
          </text>
        ))}
        {investedRuns
          .filter((run) => run.length > 1)
          .map((run, i) => (
            <path key={i} d={path(run)} className="pf-chart__invested" />
          ))}
        <path d={currentLine} className="pf-chart__current" />
        <circle cx={x(drawn.length - 1)} cy={y(last.current)} r="2.5" className="pf-chart__dot" />
        {hover != null && <line className="pf-chart__cursor" x1={x(hover)} x2={x(hover)} y1={T} y2={height - B} />}
        {hp && <circle cx={x(hover!)} cy={y(hp.current)} r="3" className="pf-chart__dot" />}
      </svg>
      {hp && (
        <div className="pf-chart__tip" style={tipLeft ? { right: width - x(hover!) + 8 } : { left: x(hover!) + 8 }} aria-live="polite">
          <div className="faint">{formatDay(hp.date)}</div>
          <div className="pf-chart__row">
            <span>Value</span>
            <b>{formatInrWhole(hp.current)}</b>
          </div>
          <div className="pf-chart__row">
            <span>Invested</span>
            <span>{formatInrWhole(hp.invested)}</span>
          </div>
          {hp.pnl != null && (
            <div className="pf-chart__row">
              <span>P&amp;L</span>
              <span className={signTone(hp.pnl)}>{formatInrSigned(hp.pnl)}</span>
            </div>
          )}
        </div>
      )}
    </div>
  )
}
