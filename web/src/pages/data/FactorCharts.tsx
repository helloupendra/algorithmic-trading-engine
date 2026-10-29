/**
 * The FII & DII section's charts, drawn as inline SVG in pixel units at the
 * panel's width, like the backtest's daily P&L (lib/bars.ts says why not
 * lightweight-charts' histogram).
 *
 * SessionChart is a reading by session: a bar a session for a daily quantity
 * (a net position, a day's net cash), a line for a level read each day (a
 * long share). Two panes (FII over DII) share the sessions and one value
 * range, so their bars are the same rupees per pixel. The readout above
 * shows the session under the pointer, or under keyboard focus, and
 * otherwise the newest.
 *
 * OptionsNetChart is the day's index options by participant: a bar either
 * side of a centre line, one scale for calls and puts.
 *
 * One hue throughout, the brand. A net short or a day of selling is not a
 * loss, so the sign is the bar's side of a drawn baseline and the signed
 * number beside it, never red and green. Every value is also in the table
 * one click away.
 */

import { useState } from 'react'
import type { ReactNode } from 'react'
import { barPath, niceTicks } from '../../lib/bars'
import { formatDay, formatSignedContracts, optionsNetScale, sharedBarRange, valueRange } from '../../lib/factors'
import type { OptionsPosition } from '../../lib/factors'
import { useWidth } from '../markets/chain/useWidth'

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']

/** The least room between two value ticks, in pixels: 10.5px labels closer than this crowd. */
const MIN_TICK_GAP = 28

/** "18 Sep" for the time axis. */
const axisDay = (date: string) => `${Number(date.slice(8, 10))} ${MONTHS[Number(date.slice(5, 7)) - 1]}`

export interface SessionPane {
  key: string
  /** Named over the pane when the chart has more than one, and in each session's tooltip. */
  label: string
  kind: 'bars' | 'line'
  /** One per session, oldest first; null leaves the session's place empty. */
  values: readonly (number | null)[]
  /** Where the bars stand, or a level the line is read against (50% for a share): drawn when in range. */
  baseline: number
}

export function SessionChart({
  dates,
  panes,
  format,
  label,
  readout,
}: {
  /** "yyyy-mm-dd", oldest first. */
  dates: readonly string[]
  panes: readonly SessionPane[]
  /** The value axis' labels, and the values in each session's tooltip. */
  format: (value: number) => string
  label: string
  /** The line above the chart for one session (an index into `dates`). */
  readout: (index: number) => ReactNode
}) {
  const [ref, width] = useWidth<HTMLDivElement>()
  const [hovered, setHovered] = useState<number | null>(null)
  const n = dates.length

  const multi = panes.length > 1
  const paneH = multi ? 108 : 170
  const paneGap = 26
  const mT = multi ? 18 : 10
  const mB = 24
  const mL = 4
  const mR = 84
  const plotW = Math.max(40, width - mL - mR)
  const height = mT + panes.length * paneH + (panes.length - 1) * paneGap + mB
  const slot = n > 0 ? plotW / n : plotW
  // Thin marks with a gap between neighbours: at most 18px, never touching.
  const barW = Math.min(18, Math.max(3, slot - 4))
  const centre = (i: number) => mL + i * slot + slot / 2

  // One scale for every pane of bars; a line keeps its own, around its values.
  const barRange = sharedBarRange(panes.filter((p) => p.kind === 'bars').map((p) => p.values))
  const scaleFor = (pane: SessionPane) => {
    if (pane.kind === 'bars') return niceTicks(barRange.min, barRange.max)
    const range = valueRange(pane.values) ?? { min: pane.baseline, max: pane.baseline }
    return niceTicks(range.min, range.max)
  }
  const labelEvery = Math.max(1, Math.ceil(n / Math.max(1, Math.floor(plotW / 56))))
  const active = hovered ?? n - 1
  const plotTop = mT
  const plotBottom = mT + panes.length * paneH + (panes.length - 1) * paneGap

  return (
    <div className="mf-svg" ref={ref}>
      <div className="chart-readout" aria-live="off">
        {n > 0 ? readout(active) : <span className="muted">No session stored yet.</span>}
      </div>
      {width > 0 && n > 0 && (
        <svg width={width} height={height} role="img" aria-label={label} onMouseLeave={() => setHovered(null)}>
          {hovered != null && <rect x={mL + hovered * slot} y={plotTop} width={slot} height={plotBottom - plotTop} className="mf-svg__focus" />}

          {panes.map((pane, p) => {
            const top = mT + p * (paneH + paneGap)
            const scale = scaleFor(pane)
            const y = (v: number) => top + ((scale.max - v) / (scale.max - scale.min)) * paneH
            const baseShown = pane.baseline >= scale.min && pane.baseline <= scale.max
            // A short pane labels every other tick, counted from the baseline so it keeps its label.
            const every = Math.max(1, Math.ceil(MIN_TICK_GAP / (paneH / Math.max(1, scale.ticks.length - 1))))
            const from = Math.max(0, scale.ticks.indexOf(pane.baseline))
            const ticks = scale.ticks.filter((_, i) => (i - from) % every === 0)
            return (
              <g key={pane.key}>
                {multi && (
                  <text x={mL} y={top - 7} className="mf-svg__pane">
                    {pane.label}
                  </text>
                )}
                {ticks.map((t) => (
                  <g key={t}>
                    <line x1={mL} x2={mL + plotW} y1={y(t)} y2={y(t)} className="mf-svg__grid" />
                    <text x={mL + plotW + 8} y={y(t) + 3.5} className="mf-svg__tick">
                      {format(t)}
                    </text>
                  </g>
                ))}
                {baseShown && <line x1={mL} x2={mL + plotW} y1={y(pane.baseline)} y2={y(pane.baseline)} className="mf-svg__base" />}
                {pane.kind === 'bars'
                  ? pane.values.map((v, i) =>
                      v == null || v === pane.baseline ? null : (
                        <path
                          key={dates[i]}
                          d={barPath(centre(i) - barW / 2, barW, y(pane.baseline), y(v), 3)}
                          className={`mf-svg__bar${hovered === i ? ' is-hover' : ''}`}
                        />
                      ),
                    )
                  : (
                    <>
                      <path d={linePath(pane.values, centre, y)} className="mf-svg__line" />
                      {pane.values[active] != null && (
                        <circle cx={centre(active)} cy={y(pane.values[active]!)} r={4} className="mf-svg__dot" />
                      )}
                    </>
                  )}
              </g>
            )
          })}

          {dates.map((date, i) =>
            (n - 1 - i) % labelEvery === 0 ? (
              <text key={date} x={centre(i)} y={height - 7} textAnchor="middle" className="mf-svg__tick">
                {axisDay(date)}
              </text>
            ) : null,
          )}

          {/* A whole session's column answers the pointer and the keyboard, not a 3px bar. */}
          {dates.map((date, i) => (
            <rect
              key={date}
              x={mL + i * slot}
              y={plotTop}
              width={slot}
              height={plotBottom - plotTop}
              className="mf-svg__hit"
              tabIndex={0}
              onMouseEnter={() => setHovered(i)}
              onFocus={() => setHovered(i)}
              onBlur={() => setHovered((h) => (h === i ? null : h))}
            >
              <title>{`${formatDay(date)}: ${panes.map((p) => `${p.label} ${p.values[i] == null ? '—' : format(p.values[i]!)}`).join(' · ')}`}</title>
            </rect>
          ))}
        </svg>
      )}
    </div>
  )
}

/** A line through the sessions, broken where a session has no value rather than drawn straight across it. */
function linePath(values: readonly (number | null)[], x: (i: number) => number, y: (v: number) => number): string {
  let d = ''
  let open = false
  values.forEach((v, i) => {
    if (v == null) {
      open = false
      return
    }
    d += `${open ? 'L' : 'M'}${x(i).toFixed(1)},${y(v).toFixed(1)} `
    open = true
  })
  return d.trim()
}

/** One net as a bar from the centre line: right of it net long, left net short. */
function NetBar({ value, change, scale, title }: { value: number; change: number | null; scale: number; title: string }) {
  const share = Math.min(1, Math.abs(value) / scale)
  return (
    <span className="mf-net" title={title}>
      <span className="mf-net__track" aria-hidden="true">
        {value !== 0 && <span className={`mf-net__fill mf-net__fill--${value > 0 ? 'long' : 'short'}`} style={{ width: `${(share * 50).toFixed(1)}%` }} />}
      </span>
      <span className="mf-net__value mono">
        {formatSignedContracts(value)}
        {change != null && <span className="mf-sub muted">{formatSignedContracts(change)}</span>}
      </span>
    </span>
  )
}

export function OptionsNetChart({ rows, previousDay }: { rows: readonly OptionsPosition[]; previousDay: string | null }) {
  const scale = optionsNetScale(rows)
  const title = (who: string, side: string, net: number, change: number | null) =>
    `${who} ${side}: net ${formatSignedContracts(net)}${change != null && previousDay ? `, ${formatSignedContracts(change)} since ${formatDay(previousDay)}` : ''}`
  return (
    <div className="mf-opt" role="img" aria-label="Net index options contracts by participant, calls and puts">
      <span />
      <span className="mf-opt__head small muted">Calls · net</span>
      <span className="mf-opt__head small muted">Puts · net</span>
      {rows.map((r) => (
        <div key={r.clientType} className="mf-opt__row">
          <span className="mf-opt__who">{r.clientType}</span>
          <NetBar value={r.callNet} change={r.callNetChange} scale={scale} title={title(r.clientType, 'calls', r.callNet, r.callNetChange)} />
          <NetBar value={r.putNet} change={r.putNetChange} scale={scale} title={title(r.clientType, 'puts', r.putNet, r.putNetChange)} />
        </div>
      ))}
    </div>
  )
}
