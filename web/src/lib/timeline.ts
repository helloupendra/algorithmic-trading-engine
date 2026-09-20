/**
 * The three small pieces of maths a scroll-driven scene needs, kept free of
 * three.js so they can be tested without a GPU: an easing, a frame-rate
 * independent follow, and the mapping from an element's scroll position to
 * story progress.
 */

/** Hermite smoothstep on 0…1: gentle at both ends. */
export const smooth = (t: number): number => t * t * (3 - 2 * t)

/**
 * Frame-rate-independent follow: moves `current` toward `target` so that half
 * the remaining distance is covered every `halfLife` seconds, whatever the
 * frame time. The classic `x += (target - x) * 0.1` does a different thing at
 * 30 and 120 fps; this does not.
 */
export function follow(current: number, target: number, dt: number, halfLife: number): number {
  if (halfLife <= 0 || dt <= 0) return target
  const k = 1 - Math.pow(0.5, dt / halfLife)
  return current + (target - current) * k
}

/**
 * Scroll position of a track element as story progress: 0 when its top is at
 * the top of the viewport, 1 when its bottom has reached the bottom. Pure so
 * it can be tested; the caller passes the rect and the viewport height.
 */
export function progressOf(rectTop: number, rectHeight: number, viewportHeight: number): number {
  const total = rectHeight - viewportHeight
  if (total <= 0) return 0
  const t = (0 - rectTop) / total
  // Written out rather than clamped with Math.min/max so a rect at exactly the top yields +0, not -0.
  return t <= 0 ? 0 : t >= 1 ? 1 : t
}
