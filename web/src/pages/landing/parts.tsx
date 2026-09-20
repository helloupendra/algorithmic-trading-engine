/**
 * The small pieces both views of the homepage draw with: the GitHub mark, the
 * arrow in a call to action, the run's equity curve, and the neon sign in the
 * footer.
 */

import { useEffect, useId, useMemo, useRef } from 'react'
import { RUN, SESSIONS } from '../../scene/evidence'
import { rupees } from './content'
import { WORDMARK } from './wordmark'

/* -------------------------------------------------------------------- marks */

export function GitHubMark() {
  return (
    <svg viewBox="0 0 16 16" width="17" height="17" fill="currentColor" aria-hidden="true">
      <path d="M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27.68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.01 8.01 0 0 0 16 8c0-4.42-3.58-8-8-8z" />
    </svg>
  )
}

export const Arrow = () => <span className="arrow" aria-hidden="true">→</span>

/** The account's equity over the run, as an SVG path drawn from the same sessions the world uses. */
export function EquityCurve() {
  const { path, startY, endX, endY } = useMemo(() => {
    const W = 1000
    const H = 260
    const values = SESSIONS.map((s) => s[4])
    const min = Math.min(...values, RUN.initialCapital)
    const max = Math.max(...values, RUN.initialCapital)
    const x = (i: number) => (i / (values.length - 1)) * W
    const y = (v: number) => 14 + (1 - (v - min) / (max - min)) * (H - 28)
    return {
      path: values.map((v, i) => `${i ? 'L' : 'M'}${x(i).toFixed(1)} ${y(v).toFixed(1)}`).join(' '),
      startY: y(RUN.initialCapital),
      endX: W,
      endY: y(values[values.length - 1]),
    }
  }, [])
  return (
    <svg className="curve" viewBox="0 0 1000 260" role="img" aria-label={`The account's equity over the run: it starts at ${rupees(RUN.initialCapital)}, rises briefly, and ends at ${rupees(RUN.closingEquity)}.`} preserveAspectRatio="none">
      <line x1="0" x2="1000" y1={startY} y2={startY} className="curve__start" />
      <path d={path} className="curve__line" />
      <circle cx={endX - 3} cy={endY} r="5" className="curve__end" />
    </svg>
  )
}

/* ---------------------------------------------------------------- the sign */

/** Room around the letters for their glow, and where the floor they stand over is, in font units. */
const SIGN = { pad: 150, floor: WORDMARK.bottom + 46, reflection: 560 }

/** The two circuits of the sign, and the colours their gas runs through, left to right. */
const CIRCUITS = [
  { key: 'a', from: 0, to: WORDMARK.split, colours: ['#b18cff', '#7a86ff', '#4fb4ff'] },
  { key: 'b', from: WORDMARK.split, to: WORDMARK.letters.length, colours: ['#2cecec', '#2ff5c4', '#64ff9c'] },
] as const

/**
 * The footer's wordmark as a neon sign: the word's outline bent in glass over
 * the same black mirror floor the engine stands on, so it is seen twice. Two
 * circuits — "open" running violet to sky blue, "fno" cyan to mint.
 *
 * The glass is always there, unlit. Each time the sign scrolls into view it
 * strikes like a real one — a few stutters, "fno" a beat after "open" — and
 * then it stays alive for as long as it is watched: the glow breathes, each
 * circuit buzzes on its own clock, and pulses of current run round every
 * letter.
 *
 * It is built to cost nothing while it does that. Each circuit is its own
 * stacked SVG whose blurs are rasterised once; what animates on them is CSS
 * opacity, which the compositor handles without repainting. The running pulses
 * live in a separate SVG with no filters in it, so redrawing them every frame
 * is a few thin strokes. Everything pauses while the sign is off screen, and
 * under prefers-reduced-motion it is simply on.
 */
export function NeonWordmark() {
  const ref = useRef<HTMLDivElement | null>(null)
  const id = useId().replace(/[^a-zA-Z0-9]/g, '')

  useEffect(() => {
    const el = ref.current
    if (!el) return
    if (!('IntersectionObserver' in window)) {
      el.classList.add('is-lit')
      return
    }
    const io = new IntersectionObserver(
      (entries) => {
        const e = entries[entries.length - 1]
        // It strikes when most of it is on screen, and goes dark once it has left
        // entirely — so coming back to the footer is coming back to a sign striking.
        if (e.intersectionRatio >= 0.45) el.classList.add('is-lit')
        else if (!e.isIntersecting) el.classList.remove('is-lit')
        el.classList.toggle('is-away', !e.isIntersecting)
      },
      { threshold: [0, 0.45] },
    )
    io.observe(el)
    return () => io.disconnect()
  }, [])

  const { left, right, top, letters } = WORDMARK
  const x = left - SIGN.pad
  const y = top - SIGN.pad
  const width = right - left + SIGN.pad * 2
  const height = SIGN.floor + SIGN.reflection - y
  const viewBox = `${x} ${y} ${width} ${height}`
  const uses = (from: number, to: number) =>
    letters.slice(from, to).map((_, i) => <use key={i} href={`#${id}-l${from + i}`} className={`neon__l neon__l--${from + i}`} />)

  return (
    <div className="neon" ref={ref} aria-hidden="true">
      {/* The glass, unlit, and the floor — plus everything the other layers refer to. */}
      <svg className="neon__layer neon__layer--glass" viewBox={viewBox} role="presentation" focusable="false">
        <defs>
          {letters.map((l, i) => (
            // pathLength: the pulses' dashes are written in thousandths of a letter's outline.
            <path key={i} id={`${id}-l${i}`} d={l.d} pathLength={1000} />
          ))}
          {CIRCUITS.map((c) => (
            <linearGradient key={c.key} id={`${id}-ink-${c.key}`} gradientUnits="userSpaceOnUse" x1={letters[c.from].x0} x2={letters[c.to - 1].x1} y1="0" y2="0">
              {c.colours.map((colour, i) => (
                <stop key={i} offset={i / (c.colours.length - 1)} stopColor={colour} />
              ))}
            </linearGradient>
          ))}
          <filter id={`${id}-near`} x="-10%" y="-30%" width="120%" height="160%">
            <feGaussianBlur stdDeviation="14" />
          </filter>
          <filter id={`${id}-far`} x="-15%" y="-60%" width="130%" height="220%">
            <feGaussianBlur stdDeviation="48" />
          </filter>
          <filter id={`${id}-wet`} x="-5%" y="-10%" width="110%" height="120%">
            <feGaussianBlur stdDeviation="7 16" />
          </filter>
          <linearGradient id={`${id}-fade`} gradientUnits="userSpaceOnUse" x1="0" y1={SIGN.floor} x2="0" y2={SIGN.floor + SIGN.reflection}>
            <stop offset="0" stopColor="#fff" stopOpacity="0.62" />
            <stop offset="0.55" stopColor="#fff" stopOpacity="0.2" />
            <stop offset="1" stopColor="#fff" stopOpacity="0" />
          </linearGradient>
          <mask id={`${id}-mask`} maskUnits="userSpaceOnUse" x={x} y={SIGN.floor} width={width} height={SIGN.reflection}>
            <rect x={x} y={SIGN.floor} width={width} height={SIGN.reflection} fill={`url(#${id}-fade)`} />
          </mask>
          <linearGradient id={`${id}-line`} x1="0" x2="1">
            <stop offset="0" stopColor="#fff" stopOpacity="0" />
            <stop offset="0.5" stopColor="#fff" stopOpacity="0.22" />
            <stop offset="1" stopColor="#fff" stopOpacity="0" />
          </linearGradient>
        </defs>
        <g className="neon__glass">{uses(0, letters.length)}</g>
        <rect x={x} y={SIGN.floor - 1} width={width} height="2" fill={`url(#${id}-line)`} />
      </svg>

      {/* One layer per circuit: its reflection, its far and near glow, the tube and its white-hot core. */}
      {CIRCUITS.map((c) => (
        <svg key={c.key} className={`neon__layer neon__layer--lit neon__layer--${c.key}`} viewBox={viewBox} role="presentation" focusable="false">
          <g stroke={`url(#${id}-ink-${c.key})`}>
            <g mask={`url(#${id}-mask)`}>
              <g className="neon__mirror" filter={`url(#${id}-wet)`} transform={`translate(0 ${SIGN.floor * 2}) scale(1 -1)`}>
                {uses(c.from, c.to)}
              </g>
            </g>
            <g className="neon__far" filter={`url(#${id}-far)`}>{uses(c.from, c.to)}</g>
            <g className="neon__near" filter={`url(#${id}-near)`}>{uses(c.from, c.to)}</g>
            <g className="neon__tube">{uses(c.from, c.to)}</g>
          </g>
          <g className="neon__core">{uses(c.from, c.to)}</g>
        </svg>
      ))}

      {/* Current running round the letters. No filters in here: this is the one layer that repaints. */}
      <svg className="neon__layer neon__layer--pulse" viewBox={viewBox} role="presentation" focusable="false">
        {CIRCUITS.map((c) => (
          <g key={c.key} stroke={`url(#${id}-ink-${c.key})`}>
            <g className="neon__halo">{uses(c.from, c.to)}</g>
          </g>
        ))}
        <g className="neon__spark">{uses(0, letters.length)}</g>
      </svg>
    </div>
  )
}
