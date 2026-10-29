/**
 * The geometry the console's hand-drawn bar charts share (a backtest's
 * daily P&L, Market factors' sessions): clean tick steps, a value axis that
 * ends on them, and a bar that is square at its baseline and rounded at its
 * data end. lightweight-charts draws a histogram's columns edge to edge,
 * with no width or corner to set, which at twenty sessions is a slab.
 */

/** 1/2/5 × 10^k so that the extreme splits into 2–5 clean ticks. */
export function niceStep(maxAbs: number): number {
  const raw = maxAbs / 4
  const mag = 10 ** Math.floor(Math.log10(Math.max(raw, 1)))
  for (const m of [1, 2, 5, 10]) {
    if (m * mag >= raw) return m * mag
  }
  return 10 * mag
}

/**
 * The value axis over [min, max]: about five clean steps (1, 2, 2.5 or 5 ×
 * 10^k, fractions too, for a share in percent), from the step at or below the
 * lowest value to the one at or above the highest, so zero is a tick whenever
 * the range holds it.
 */
export function niceTicks(min: number, max: number): { min: number; max: number; ticks: number[] } {
  // A range of one value still needs a step: its own size, or one.
  const raw = (max - min || Math.abs(max) || 1) / 5
  const mag = 10 ** Math.floor(Math.log10(raw))
  const step = ([1, 2, 2.5, 5, 10].find((m) => m * mag >= raw) ?? 10) * mag
  const lo = Math.floor(min / step) * step
  let hi = Math.ceil(max / step) * step
  if (hi === lo) hi = lo + step
  const ticks: number[] = []
  // Counted rather than stepped by adding, so 0.1 + 0.2 never leaves a tick off the end.
  for (let i = 0; i <= Math.round((hi - lo) / step); i++) ticks.push(lo + i * step)
  return { min: lo, max: hi, ticks }
}

/** Bar from the baseline to the value: square at the baseline, rounded at the data end. */
export function barPath(x: number, w: number, yBase: number, yValue: number, radius: number): string {
  const top = Math.min(yBase, yValue)
  const bottom = Math.max(yBase, yValue)
  const r = Math.min(radius, (bottom - top) / 2, w / 2)
  if (yValue < yBase) {
    return `M${x},${bottom} V${top + r} Q${x},${top} ${x + r},${top} H${x + w - r} Q${x + w},${top} ${x + w},${top + r} V${bottom} Z`
  }
  return `M${x},${top} V${bottom - r} Q${x},${bottom} ${x + r},${bottom} H${x + w - r} Q${x + w},${bottom} ${x + w},${bottom - r} V${top} Z`
}
