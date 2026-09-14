/**
 * The OI analysis under the chain: open interest and its change by strike
 * around the money, and how the PCR and the spot moved through the session.
 *
 * Inline SVG sized to its container. The PCR and the spot are two separate
 * small charts on the same time axis rather than one chart with two scales —
 * a dual axis invites reading a crossing of the lines as meaningful when it
 * is only an accident of the two scales.
 */

import { useLayoutEffect, useMemo, useRef, useState } from 'react'
import type { MouseEvent, RefObject } from 'react'
import type { OptionChain, OptionChainHeader, OptionChainTrend } from '../../lib/types'
import { EmptyState } from '../../components/ui'
import { compactIndian, compactSigned, istTime, price, strikeWindow } from '../../lib/optionChain'

const CALL = 'var(--neg)'
const PUT = 'var(--pos)'

function useWidth<T extends HTMLElement>(): [RefObject<T | null>, number] {
  const ref = useRef<T>(null)
  const [width, setWidth] = useState(0)
  useLayoutEffect(() => {
    const el = ref.current
    if (!el) return
    setWidth(el.clientWidth)
    const observer = new ResizeObserver((entries) => {
      const w = Math.floor(entries[0]?.contentRect.width ?? 0)
      setWidth((prev) => (prev === w ? prev : w))
    })
    observer.observe(el)
    return () => observer.disconnect()
  }, [])
  return [ref, width]
}

function niceMax(value: number): number {
  if (value <= 0) return 1
  const exp = Math.pow(10, Math.floor(Math.log10(value)))
  const f = value / exp
  const step = f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10
  return step * exp
}

// --- by strike ------------------------------------------------------------------

interface StrikePoint {
  strike: number
  call: number | null
  put: number | null
}

function StrikeBars({
  title,
  points,
  spot,
  maxPain,
  signed,
  note,
}: {
  title: string
  points: StrikePoint[]
  spot: number | null
  maxPain: number | null
  signed: boolean
  note: string
}) {
  const [ref, width] = useWidth<HTMLDivElement>()
  const [hover, setHover] = useState<number | null>(null)

  const H = 210
  const pad = { top: 14, right: 8, bottom: 30, left: 46 }
  const plotW = Math.max(0, width - pad.left - pad.right)
  const plotH = H - pad.top - pad.bottom
  const n = points.length

  const { lo, hi } = useMemo(() => {
    let max = 0
    let min = 0
    for (const p of points) {
      for (const v of [p.call, p.put]) {
        if (v == null) continue
        if (v > max) max = v
        if (v < min) min = v
      }
    }
    if (!signed) return { lo: 0, hi: niceMax(max) }
    const m = niceMax(Math.max(Math.abs(min), Math.abs(max)))
    return { lo: min < 0 ? -m : 0, hi: max > 0 ? m : min < 0 ? 0 : m }
  }, [points, signed])

  const y = (v: number) => pad.top + plotH - ((v - lo) / (hi - lo || 1)) * plotH
  const group = n > 0 ? plotW / n : 0
  const barW = Math.max(1, Math.min(14, (group - 4) / 2))
  const cx = (i: number) => pad.left + group * (i + 0.5)

  // The spot, placed between the two strikes it lies between.
  let spotX: number | null = null
  if (spot != null && n > 1) {
    for (let i = 0; i < n - 1; i++) {
      const a = points[i].strike
      const b = points[i + 1].strike
      if (spot >= a && spot <= b) {
        spotX = cx(i) + ((spot - a) / (b - a)) * group
        break
      }
    }
  }
  const painIndex = maxPain == null ? -1 : points.findIndex((p) => p.strike === maxPain)
  const labelEvery = Math.max(1, Math.ceil(n / Math.max(1, plotW / 52)))
  const ticks = signed && lo < 0 ? [hi, 0, lo] : [hi, hi / 2, 0]
  const hovered = hover != null ? points[hover] : null

  return (
    <figure className="oc-chart">
      <figcaption className="oc-chart__head">
        <span className="oc-chart__title">{title}</span>
        <span className="oi-legend"><span className="oi-swatch" style={{ background: CALL }} />CE</span>
        <span className="oi-legend"><span className="oi-swatch" style={{ background: PUT }} />PE</span>
        {spotX != null && <span className="oi-legend"><span className="oc-swatch-line" />Spot</span>}
      </figcaption>
      <div ref={ref} className="oc-chart__plot" onMouseLeave={() => setHover(null)}>
        {width > 0 && n > 0 && (
          <svg width={width} height={H} role="img" aria-label={`${title}: ${n} strikes around the money`}>
            {ticks.map((t) => (
              <g key={t}>
                <line x1={pad.left} x2={width - pad.right} y1={y(t)} y2={y(t)} className={t === 0 ? 'oc-axis' : 'oc-grid'} />
                <text x={pad.left - 6} y={y(t) + 3.5} className="oc-tick" textAnchor="end">{signed ? compactSigned(t) : compactIndian(t)}</text>
              </g>
            ))}
            {points.map((p, i) => {
              const x0 = cx(i)
              const bars: { v: number | null; x: number; fill: string }[] = [
                { v: p.call, x: x0 - barW - 1, fill: CALL },
                { v: p.put, x: x0 + 1, fill: PUT },
              ]
              return (
                <g key={p.strike}>
                  {hover === i && <rect x={x0 - group / 2} y={pad.top} width={group} height={plotH} className="oc-hoverband" />}
                  {bars.map((b, k) =>
                    b.v == null || b.v === 0 ? null : (
                      <rect
                        key={k}
                        x={b.x}
                        y={Math.min(y(b.v), y(0))}
                        width={barW}
                        height={Math.max(1, Math.abs(y(b.v) - y(0)))}
                        rx={1.5}
                        fill={b.fill}
                        opacity={0.85}
                      />
                    ),
                  )}
                  {i % labelEvery === 0 && (
                    <text x={x0} y={H - pad.bottom + 14} className="oc-tick" textAnchor="middle">{p.strike}</text>
                  )}
                  {i === painIndex && (
                    <text x={x0} y={H - 4} className="oc-tick oc-tick--pain" textAnchor="middle">max pain</text>
                  )}
                  <rect
                    x={x0 - group / 2}
                    y={pad.top}
                    width={group}
                    height={plotH + pad.bottom}
                    fill="transparent"
                    onMouseEnter={() => setHover(i)}
                    onClick={() => setHover(i)}
                  />
                </g>
              )
            })}
            {spotX != null && (
              <line x1={spotX} x2={spotX} y1={pad.top - 6} y2={pad.top + plotH} className="oc-spotline" />
            )}
          </svg>
        )}
        {hovered && (
          <div className="oc-tooltip" style={{ left: Math.min(Math.max(cx(hover!) , 90), Math.max(90, width - 90)) }}>
            <b>{hovered.strike.toLocaleString('en-IN')}</b>
            <span><span className="oi-swatch" style={{ background: CALL }} /> CE {signed ? compactSigned(hovered.call) : compactIndian(hovered.call)}</span>
            <span><span className="oi-swatch" style={{ background: PUT }} /> PE {signed ? compactSigned(hovered.put) : compactIndian(hovered.put)}</span>
          </div>
        )}
      </div>
      <p className="oc-chart__note">{note}</p>
    </figure>
  )
}

// --- through the session ----------------------------------------------------------

function TrendLine({
  title,
  times,
  values,
  format,
  colour,
  reference,
}: {
  title: string
  times: string[]
  values: (number | null)[]
  format: (v: number) => string
  colour: string
  reference?: { value: number; label: string }
}) {
  const [ref, width] = useWidth<HTMLDivElement>()
  const [hover, setHover] = useState<number | null>(null)

  const H = 150
  const pad = { top: 12, right: 10, bottom: 22, left: 58 }
  const plotW = Math.max(0, width - pad.left - pad.right)
  const plotH = H - pad.top - pad.bottom
  const n = values.length

  const ms = times.map((t) => Date.parse(t))
  const t0 = ms[0] ?? 0
  const t1 = ms[n - 1] ?? 1
  const present = values.filter((v): v is number => v != null)
  let lo = present.length ? Math.min(...present) : 0
  let hi = present.length ? Math.max(...present) : 1
  if (reference) {
    lo = Math.min(lo, reference.value)
    hi = Math.max(hi, reference.value)
  }
  if (lo === hi) {
    lo -= Math.abs(lo) * 0.01 || 1
    hi += Math.abs(hi) * 0.01 || 1
  }
  const padY = (hi - lo) * 0.08
  lo -= padY
  hi += padY

  const x = (i: number) => pad.left + (n <= 1 ? plotW / 2 : ((ms[i] - t0) / (t1 - t0 || 1)) * plotW)
  const y = (v: number) => pad.top + plotH - ((v - lo) / (hi - lo)) * plotH

  const path = values
    .map((v, i) => (v == null ? null : `${x(i).toFixed(1)},${y(v).toFixed(1)}`))
    .filter(Boolean)
    .join(' ')

  const onMove = (e: MouseEvent<SVGSVGElement>) => {
    if (n === 0) return
    const rect = e.currentTarget.getBoundingClientRect()
    const px = e.clientX - rect.left
    let best = 0
    for (let i = 1; i < n; i++) if (Math.abs(x(i) - px) < Math.abs(x(best) - px)) best = i
    setHover(best)
  }

  const timeTicks = n > 1 ? [0, Math.floor((n - 1) / 3), Math.floor((2 * (n - 1)) / 3), n - 1] : [0]
  const last = [...values].reverse().find((v) => v != null) ?? null
  const hv = hover != null ? values[hover] : null

  return (
    <figure className="oc-chart">
      <figcaption className="oc-chart__head">
        <span className="oc-chart__title">{title}</span>
        <span className="oc-chart__value">{last != null ? format(last) : '—'}</span>
      </figcaption>
      <div ref={ref} className="oc-chart__plot" onMouseLeave={() => setHover(null)}>
        {width > 0 && n > 0 && (
          <svg width={width} height={H} onMouseMove={onMove} role="img" aria-label={`${title} through the session`}>
            {[hi - padY, (hi + lo) / 2, lo + padY].map((t, k) => (
              <g key={k}>
                <line x1={pad.left} x2={width - pad.right} y1={y(t)} y2={y(t)} className="oc-grid" />
                <text x={pad.left - 6} y={y(t) + 3.5} className="oc-tick" textAnchor="end">{format(t)}</text>
              </g>
            ))}
            {reference && (
              <g>
                <line x1={pad.left} x2={width - pad.right} y1={y(reference.value)} y2={y(reference.value)} className="oc-refline" />
                <text x={width - pad.right} y={y(reference.value) - 4} className="oc-tick" textAnchor="end">{reference.label}</text>
              </g>
            )}
            {timeTicks.map((i) => (
              <text key={i} x={x(i)} y={H - 6} className="oc-tick" textAnchor={i === 0 ? 'start' : i === n - 1 ? 'end' : 'middle'}>
                {istTime(times[i])}
              </text>
            ))}
            {n === 1 && values[0] != null ? (
              <circle cx={x(0)} cy={y(values[0])} r={4} fill={colour} />
            ) : (
              <polyline points={path} fill="none" stroke={colour} strokeWidth={2} strokeLinejoin="round" />
            )}
            {hover != null && hv != null && (
              <g>
                <line x1={x(hover)} x2={x(hover)} y1={pad.top} y2={pad.top + plotH} className="oc-crosshair" />
                <circle cx={x(hover)} cy={y(hv)} r={4} fill={colour} stroke="var(--surface)" strokeWidth={2} />
              </g>
            )}
          </svg>
        )}
        {hover != null && hv != null && (
          <div className="oc-tooltip" style={{ left: Math.min(Math.max(x(hover), 70), Math.max(70, width - 70)) }}>
            <b>{istTime(times[hover])} IST</b>
            <span>{format(hv)}</span>
          </div>
        )}
      </div>
    </figure>
  )
}

// --- the section ----------------------------------------------------------------

export function OptionChainAnalysis({
  chain,
  header,
  trend,
  trendLoading,
}: {
  chain: OptionChain
  header: OptionChainHeader
  trend: OptionChainTrend | null
  trendLoading: boolean
}) {
  const spot = header.spot?.lastPrice ?? null

  const around = useMemo(() => {
    const strikes = chain.strikes
    const { start, end } = strikeWindow(strikes.map((s) => s.strikePrice), chain.atTheMoneyStrike, 10)
    return strikes.slice(start, end)
  }, [chain.strikes, chain.atTheMoneyStrike])

  const oi = around.map((s) => ({ strike: s.strikePrice, call: s.call?.openInterest ?? null, put: s.put?.openInterest ?? null }))
  const change = around.map((s) => ({
    strike: s.strikePrice,
    call: s.call?.openInterestChange ?? null,
    put: s.put?.openInterestChange ?? null,
  }))

  const points = trend?.points ?? []
  const times = points.map((p) => p.capturedUtc)

  return (
    <div className="oc-analysis-grid">
      <StrikeBars
        title="Open interest by strike"
        points={oi}
        spot={spot}
        maxPain={header.maxPainStrike}
        signed={false}
        note="Twenty-one strikes around the ATM. Tall put bars below the spot are the floor writers defend; tall call bars above it, the ceiling."
      />
      <StrikeBars
        title="Change in OI today"
        points={change}
        spot={spot}
        maxPain={null}
        signed
        note="Contracts added (above zero) or closed (below) since the previous day's close."
      />
      {trendLoading && !trend ? (
        <EmptyState>Loading the session…</EmptyState>
      ) : points.length === 0 ? (
        <EmptyState>No captures this session to draw a trend from.</EmptyState>
      ) : (
        <div className="oc-trend">
          <TrendLine
            title={`PCR through the session${trend?.sessionDate ? ` · ${trend.sessionDate.slice(8, 10)}/${trend.sessionDate.slice(5, 7)}` : ''}`}
            times={times}
            values={points.map((p) => p.putCallRatio)}
            format={(v) => v.toFixed(2)}
            colour="var(--brand-hi)"
            reference={{ value: 1, label: 'PCR 1' }}
          />
          {header.spotIsFuture ? (
            // Dhan's chain reports an underlying price for MCX that is not the
            // future its options are written on (14 Sep: 9,577 against a
            // future at 9,971, which put-call parity on the chain agreed with).
            // Drawing it would chart a price nobody can trade.
            <p className="oc-chart__note">
              The underlying is not drawn for MCX: the chain's recorded price does not match the future its options are
              written on. The header shows the future itself.
            </p>
          ) : (
            <TrendLine
              title="Spot at each capture"
              times={times}
              values={points.map((p) => (p.spotPrice > 0 ? p.spotPrice : null))}
              format={(v) => price(v, v >= 1000 ? 0 : 2)}
              colour="var(--text-2)"
            />
          )}
          <p className="oc-chart__note">
            {trend?.captures ?? 0} {trend?.captures === 1 ? 'capture' : 'captures'}
            {trend && trend.captures > points.length ? `, ${points.length} drawn` : ''}. Per-minute captures only — the live
            overlay is not part of the trend.
          </p>
        </div>
      )}
    </div>
  )
}
