/**
 * The small pieces both views of the homepage draw with: the GitHub mark, the
 * arrow in a call to action, and the run's equity curve.
 */

import { useMemo } from 'react'
import { RUN, SESSIONS } from '../../scene/evidence'
import { rupees } from './content'

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
