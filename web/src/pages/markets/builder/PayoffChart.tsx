/**
 * The strategy builder's payoff chart: the position's P&L in rupees against
 * the underlying's price, at expiry and before it.
 *
 * The expiry line is the exact one (lib/payoff.ts): straight between the
 * strikes, drawn through every strike and breakeven so its corners sit where
 * they are, green where it makes money and red where it loses, over a wash of
 * the same. The dashed line is the same legs priced before expiry by
 * Black-Scholes, on the day and IV the what-if sliders set: it is the model,
 * and dashed for that reason. The spot, the breakevens and the what-if point
 * are marked; hovering, or the arrow keys once the chart has focus, reads
 * both lines at any price.
 *
 * Drawn as inline SVG in pixel units at the panel's width, like the charts
 * under the chain (useWidth): axis text stays 10px at every width.
 */

import { useId, useMemo, useState } from 'react'
import type { KeyboardEvent, PointerEvent } from 'react'
import { expiryPnl, pnlBefore, priceGrid, priceRange } from '../../../lib/payoff'
import type { PayoffLeg, PricingInputs } from '../../../lib/payoff'
import { axisInr, niceStep, valueDomain, valueTicks } from '../../../lib/pnlSeries'
import { formatInrSigned } from '../../../lib/format'
import { price as formatPrice, signedPercent } from '../../../lib/optionChain'
import { useWidth } from '../chain/useWidth'

const L = 44
const R = 12
const T = 16
const B = 20

/** The curve before expiry, and what the legend calls it. */
export interface BeforeCurve {
  pricing: PricingInputs
  label: string
}

/** A price on the axis: whole points on an index, two places on a cheap underlying. */
function axisPrice(value: number, step: number): string {
  return value.toLocaleString('en-IN', { maximumFractionDigits: step < 1 ? 2 : 0 })
}

export function PayoffChart({
  legs,
  spot,
  underlying,
  breakevens,
  curve,
  whatIf,
}: {
  legs: readonly PayoffLeg[]
  spot: number
  underlying: string
  breakevens: readonly number[]
  /** Null when a leg has no IV: no curve is drawn rather than one that leaves a leg out. */
  curve: BeforeCurve | null
  /** The what-if point on that curve. */
  whatIf: { price: number; pnl: number } | null
}) {
  const [ref, width] = useWidth<HTMLDivElement>()
  const [hover, setHover] = useState<number | null>(null)
  const clip = useId()

  const plot = useMemo(() => {
    const range = priceRange(spot, legs, breakevens, whatIf ? [whatIf.price] : [])
    const strikes = legs.map((l) => l.strike)
    const xs = priceGrid(range.lo, range.hi, [...strikes, ...breakevens, spot])
    const expiry = xs.map((x) => expiryPnl(legs, x))
    const before = curve ? xs.map((x) => pnlBefore(legs, x, curve.pricing)) : null
    const all = [...expiry, ...(before ?? []).filter((v): v is number => v != null), ...(whatIf ? [whatIf.pnl] : [])]
    return { range, xs, expiry, before, domain: valueDomain(all) }
  }, [legs, spot, breakevens, curve, whatIf])

  const height = width < 560 ? 240 : 300
  const plotW = Math.max(1, width - L - R)
  const { range, xs, expiry, before, domain } = plot
  const x = (p: number) => L + ((p - range.lo) / (range.hi - range.lo)) * plotW
  const y = (v: number) => T + (1 - (v - domain.lo) / (domain.hi - domain.lo)) * (height - T - B)
  const zeroY = y(0)
  const bottom = height - B

  const path = (values: readonly (number | null)[]) =>
    values.map((v, i) => (v == null ? '' : `${i === 0 ? 'M' : 'L'}${x(xs[i]).toFixed(1)},${y(v).toFixed(1)}`)).join('')
  const expiryPath = path(expiry)
  const area = `${expiryPath}L${x(xs[xs.length - 1]).toFixed(1)},${zeroY.toFixed(1)}L${x(xs[0]).toFixed(1)},${zeroY.toFixed(1)}Z`
  const beforePath = before ? path(before) : null

  const step = niceStep(range.hi - range.lo, width < 560 ? 4 : 6)
  const priceTicks: number[] = []
  for (let p = Math.ceil(range.lo / step) * step; p <= range.hi; p += step) priceTicks.push(p)
  const values = valueTicks(domain, 4)

  // A breakeven's price goes on the side of it where the expiry line is under
  // zero, so the line never runs through its label.
  const beLabels = breakevens
    .filter((b) => b > range.lo && b < range.hi)
    .map((b) => {
      const nudge = (range.hi - range.lo) / 1000
      const rising = expiryPnl(legs, b + nudge) > expiryPnl(legs, b - nudge)
      return { price: b, dx: rising ? -6 : 6, anchor: rising ? ('end' as const) : ('start' as const) }
    })

  const readAt = (p: number) => ({
    expiry: expiryPnl(legs, p),
    before: curve ? pnlBefore(legs, p, curve.pricing) : null,
  })
  const reading = hover != null ? readAt(hover) : null

  function onMove(e: PointerEvent<HTMLDivElement>) {
    const box = e.currentTarget.getBoundingClientRect()
    const px = e.clientX - box.left
    if (px < L || px > L + plotW) return setHover(null)
    setHover(range.lo + ((px - L) / plotW) * (range.hi - range.lo))
  }

  function onKey(e: KeyboardEvent<HTMLDivElement>) {
    const nudge = (range.hi - range.lo) / 100
    if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') {
      e.preventDefault()
      const from = hover ?? spot
      setHover(Math.min(range.hi, Math.max(range.lo, from + (e.key === 'ArrowRight' ? nudge : -nudge))))
    } else if (e.key === 'Escape') setHover(null)
  }

  return (
    <div
      ref={ref}
      className="sb-chart"
      style={{ height }}
      tabIndex={0}
      role="group"
      aria-label={`Payoff of the position against ${underlying}. Arrow keys read it at other prices.`}
      onPointerMove={onMove}
      onPointerLeave={() => setHover(null)}
      onKeyDown={onKey}
      onBlur={() => setHover(null)}
    >
      {width > 0 && (
        <svg width={width} height={height} aria-hidden="true">
          <defs>
            <clipPath id={`${clip}-up`}>
              <rect x={L} y={T} width={plotW} height={Math.max(0, zeroY - T)} />
            </clipPath>
            <clipPath id={`${clip}-down`}>
              <rect x={L} y={zeroY} width={plotW} height={Math.max(0, bottom - zeroY)} />
            </clipPath>
          </defs>

          {values.map((v) => (
            <g key={v}>
              <line x1={L} x2={L + plotW} y1={y(v)} y2={y(v)} className={v === 0 ? 'sb-chart__zero' : 'oi-grid'} />
              <text x={L - 6} y={y(v) + 3.5} textAnchor="end" className="oi-axis">
                {axisInr(v)}
              </text>
            </g>
          ))}
          {priceTicks.map((p) => (
            <text key={p} x={x(p)} y={height - 5} textAnchor="middle" className="oi-axis">
              {axisPrice(p, step)}
            </text>
          ))}

          <path d={area} className="sb-chart__wash sb-chart__wash--pos" clipPath={`url(#${clip}-up)`} />
          <path d={area} className="sb-chart__wash sb-chart__wash--neg" clipPath={`url(#${clip}-down)`} />

          <line x1={x(spot)} x2={x(spot)} y1={T} y2={bottom} className="sb-chart__spot" />
          <text x={x(spot)} y={T - 5} textAnchor="middle" className="oi-axis">
            spot {axisPrice(spot, step)}
          </text>

          {beforePath && <path d={beforePath} className="sb-chart__before" />}
          <path d={expiryPath} className="sb-chart__expiry sb-chart__expiry--pos" clipPath={`url(#${clip}-up)`} />
          <path d={expiryPath} className="sb-chart__expiry sb-chart__expiry--neg" clipPath={`url(#${clip}-down)`} />

          {beLabels.map((b) => (
            <g key={b.price}>
              <circle cx={x(b.price)} cy={zeroY} r={3} className="sb-chart__be" />
              <text x={x(b.price) + b.dx} y={zeroY - 6} textAnchor={b.anchor} className="oi-axis">
                {axisPrice(b.price, step)}
              </text>
            </g>
          ))}

          {whatIf && <circle cx={x(whatIf.price)} cy={y(whatIf.pnl)} r={4.5} className="sb-chart__whatif" />}

          {hover != null && <line x1={x(hover)} x2={x(hover)} y1={T} y2={bottom} className="sb-chart__cursor" />}
        </svg>
      )}

      {hover != null && reading && (
        <div className="sb-chart__tip" style={x(hover) > width / 2 ? { right: width - x(hover) + 8 } : { left: x(hover) + 8 }}>
          <div className="muted">
            {underlying} {formatPrice(hover, 0)} <span className="faint">({signedPercent(((hover - spot) / spot) * 100, 1)})</span>
          </div>
          <div className="sb-chart__row">
            <span className="faint">At expiry</span>
            <span className={reading.expiry >= 0.5 ? 'pos' : reading.expiry <= -0.5 ? 'neg' : ''}>{formatInrSigned(reading.expiry)}</span>
          </div>
          {curve && reading.before != null && (
            <div className="sb-chart__row">
              <span className="faint">{curve.label}</span>
              <span className={reading.before >= 0.5 ? 'pos' : reading.before <= -0.5 ? 'neg' : ''}>{formatInrSigned(reading.before)}</span>
            </div>
          )}
        </div>
      )}
    </div>
  )
}
