/**
 * The Day P&L curve: each account's net after charges through the day, on
 * the IST axis (the NSE session, on to the MCX close when an MCX run trades
 * into the evening), with zero, "now", and the recorder's gaps shaded and
 * left undrawn. Hovering reads every account at one minute.
 *
 * Drawn in pixels at the width it is given (never stretched from a viewBox),
 * so text stays 10.5px at any width. The lines hold each value until the next
 * point, the way the series is defined; the line ends name their figure and
 * the account rows under the chart name the account beside the same colour.
 */

import { useState } from 'react'
import type { MouseEvent } from 'react'
import type { DeskAccount } from '../../lib/desk'
import { accountStroke, compactInr } from '../../lib/desk'
import type { DayCurves } from '../../lib/pnlSeries'
import { minuteLabel, spreadLabels, stepPath, timeTicks, valueAt, valueDomain, valueTicks } from '../../lib/pnlSeries'
import { formatInrSigned } from '../../lib/format'

const L = 34
const R = 50
const T = 8
const B = 17

/** "−10k", "0", "+5k": the value gridlines, as short as they can be. */
function axisValue(v: number): string {
  return v === 0 ? '0' : compactInr(v).replace('+', '')
}

export function PnlChart({
  curves,
  accounts,
  width,
  height = 136,
}: {
  curves: DayCurves
  accounts: readonly DeskAccount[]
  width: number
  height?: number
}) {
  const [hover, setHover] = useState<number | null>(null)
  if (width < 120) return <div style={{ height }} />
  const { axis } = curves
  const plotW = width - L - R
  const x = (m: number) => L + ((Math.min(Math.max(m, axis.from), axis.to) - axis.from) / (axis.to - axis.from)) * plotW
  const domain = valueDomain(curves.accounts.flatMap((a) => a.points.map((p) => p.v)))
  const y = (v: number) => T + (1 - (v - domain.lo) / (domain.hi - domain.lo)) * (height - T - B)
  const ticks = timeTicks(axis, width < 380 ? 4 : 5)
  const values = valueTicks(domain, 3)
  const toneOf = new Map(accounts.map((a) => [a.id, a]))
  const ends = curves.accounts.filter((c) => c.last)
  const labelY = spreadLabels(
    ends.map((c) => y(c.last!.v)),
    12,
    T + 4,
    height - B - 2,
  )

  function onMove(e: MouseEvent<SVGSVGElement>) {
    const box = e.currentTarget.getBoundingClientRect()
    const px = e.clientX - box.left
    if (px < L || px > L + plotW) return setHover(null)
    setHover(Math.round(axis.from + ((px - L) / plotW) * (axis.to - axis.from)))
  }

  const readout =
    hover == null
      ? null
      : curves.accounts
          .map((c) => ({ account: toneOf.get(c.userId), value: valueAt(c.points, hover) }))
          .filter((r) => r.account && r.value != null)
  const inGap = hover != null && curves.gaps.some((g) => hover > g.from && hover < g.to)
  const summary = ends
    .map((c) => `${toneOf.get(c.userId)?.name ?? c.userId} ${formatInrSigned(c.last!.v)} at ${minuteLabel(c.last!.m)}`)
    .join(', ')

  return (
    <div className="dk-chart" style={{ height }}>
      <svg
        width={width}
        height={height}
        role="img"
        aria-label={`Net P&L through the day, after charges: ${summary || 'no points'}`}
        onMouseMove={onMove}
        onMouseLeave={() => setHover(null)}
      >
        {values.map((v) => (
          <g key={v}>
            <line x1={L} x2={L + plotW} y1={y(v)} y2={y(v)} stroke={v === 0 ? 'var(--line-strong)' : 'var(--line-soft)'} />
            <text x={L - 5} y={y(v) + 3.5} textAnchor="end">
              {axisValue(v)}
            </text>
          </g>
        ))}
        {curves.gaps.map((g) => (
          <rect
            key={`${g.from}-${g.to}`}
            className="dk-chart__gap"
            x={x(g.from)}
            y={T}
            width={Math.max(1, x(g.to) - x(g.from))}
            height={height - T - B}
          />
        ))}
        {ticks.map((m) => (
          <text key={m} x={Math.min(Math.max(x(m), L + 12), L + plotW - 12)} y={height - 4} textAnchor="middle">
            {minuteLabel(m)}
          </text>
        ))}
        {curves.now != null && (
          <line className="dk-chart__now" x1={x(curves.now)} x2={x(curves.now)} y1={T} y2={height - B} />
        )}
        {curves.accounts.map((c) => {
          const stroke = accountStroke(toneOf.get(c.userId)?.tone ?? null)
          return (
            <g key={c.userId}>
              {c.segments.map((s, i) => (
                <path key={i} d={stepPath(s, x, y)} fill="none" stroke={stroke} strokeWidth="1.6" strokeLinejoin="round" />
              ))}
              {c.last && <circle cx={x(c.last.m)} cy={y(c.last.v)} r="2.5" fill={stroke} />}
            </g>
          )
        })}
        {ends.map((c, i) => (
          <text
            key={c.userId}
            x={Math.min(x(c.last!.m) + 6, width - 2)}
            y={labelY[i] + 3.5}
            className={c.last!.v >= 0.5 ? 'dk-chart__pos' : c.last!.v <= -0.5 ? 'dk-chart__neg' : ''}
            textAnchor={x(c.last!.m) + 6 > width - R + 6 ? 'end' : 'start'}
          >
            {compactInr(c.last!.v)}
          </text>
        ))}
        {hover != null && <line className="dk-chart__cursor" x1={x(hover)} x2={x(hover)} y1={T} y2={height - B} />}
      </svg>
      {hover != null && readout && (
        <div className="dk-chart__tip" style={x(hover) > width / 2 ? { right: width - x(hover) + 8 } : { left: x(hover) + 8 }}>
          <div className="dk-t3">{minuteLabel(hover)} IST</div>
          {inGap ? (
            <div className="dk-t3">no points: the recorder was not running</div>
          ) : readout.length === 0 ? (
            <div className="dk-t3">nothing traded yet</div>
          ) : (
            readout.map((r) => (
              <div key={r.account!.id} className="dk-chart__row">
                <i className="dk-sw" style={{ background: accountStroke(r.account!.tone) }} />
                <span className="dk-t2">{r.account!.name}</span>
                <span className={`dk-n ${r.value! >= 0.5 ? 'pos' : r.value! <= -0.5 ? 'neg' : 'dk-flat'}`}>{formatInrSigned(r.value!)}</span>
              </div>
            ))
          )}
        </div>
      )}
    </div>
  )
}
