/**
 * The homepage's story as numbers.
 *
 * The page shows one object — the engine in its glass case — and scrolling
 * takes the lid off: the case lifts, the five layers of the engine separate
 * into an exploded stack (data, chain, strategy, risk, ledger), the camera
 * climbs the stack one layer at a time, and at the end everything closes again.
 *
 * A chapter is a range of scroll progress with a plateau where the camera holds
 * so its copy can be read. Everything between chapters is interpolated from the
 * two neighbours; nothing here knows about three.js, so the mapping from
 * scroll to state is tested without a GPU.
 */

import { smooth } from '../lib/timeline'

export const LAYERS = ['data', 'chain', 'strategy', 'risk', 'ledger'] as const
export type LayerKey = (typeof LAYERS)[number]
export type ChapterKey = 'hero' | LayerKey | 'open'

export interface Chapter {
  key: ChapterKey
  /** Scroll progress this chapter owns, and the plateau inside it where the camera holds. */
  from: number
  to: number
  hold: readonly [number, number]
  /** The layer the camera is on, or −1 for the closed engine. */
  layer: number
  /** How far the layers have separated (0 assembled … 1 exploded) and the case has lifted. */
  explode: number
  /** Camera: degrees around the engine, degrees above the floor, metres from its target. */
  azim: number
  elev: number
  dist: number
  /** Where the engine sits on screen, as a fraction of the viewport from its centre (+x right, +y down). */
  shiftX: number
  shiftY: number
  /** How much the engine turns on its own, 0…1. */
  spin: number
}

export const CHAPTERS: readonly Chapter[] = [
  { key: 'hero', from: 0.0, to: 0.1, hold: [0.0, 0.035], layer: -1, explode: 0, azim: 36, elev: 14, dist: 9.8, shiftX: 0, shiftY: 0.175, spin: 1 },
  { key: 'data', from: 0.1, to: 0.27, hold: [0.17, 0.25], layer: 0, explode: 1, azim: 24, elev: 24, dist: 5.1, shiftX: 0.23, shiftY: 0.02, spin: 0 },
  { key: 'chain', from: 0.27, to: 0.43, hold: [0.33, 0.41], layer: 1, explode: 1, azim: -28, elev: 27, dist: 5.0, shiftX: -0.23, shiftY: 0.02, spin: 0 },
  { key: 'strategy', from: 0.43, to: 0.59, hold: [0.49, 0.57], layer: 2, explode: 1, azim: 20, elev: 20, dist: 5.0, shiftX: 0.23, shiftY: 0.02, spin: 0 },
  { key: 'risk', from: 0.59, to: 0.74, hold: [0.65, 0.72], layer: 3, explode: 1, azim: -22, elev: 32, dist: 5.2, shiftX: -0.24, shiftY: 0.02, spin: 0 },
  { key: 'ledger', from: 0.74, to: 0.9, hold: [0.8, 0.88], layer: 4, explode: 1, azim: 26, elev: 30, dist: 5.0, shiftX: 0.23, shiftY: 0.02, spin: 0 },
  { key: 'open', from: 0.9, to: 1.0, hold: [0.955, 1.0], layer: -1, explode: 0, azim: -30, elev: 13, dist: 10.2, shiftX: 0, shiftY: 0.175, spin: 1 },
]

/** Where each layer sits when the engine is assembled, and how far apart they spread. */
export const LAYER_BASE = [0.08, 0.72, 1.04, 1.34, 1.62] as const
export const LAYER_SPREAD = 1.05

/** Height of layer `i`'s plate for a given amount of separation. */
export function layerY(i: number, explode: number): number {
  return LAYER_BASE[i] + i * LAYER_SPREAD * explode
}

/**
 * Where scroll progress lands on the story: `u` is a continuous chapter index
 * (2 = holding on chapter 2, 2.5 = halfway to chapter 3) and `phase` runs 0…1
 * through the current plateau.
 */
export function locate(p: number): { u: number; phase: number; chapter: number } {
  const n = CHAPTERS.length
  if (p <= CHAPTERS[0].hold[0]) return { u: 0, phase: 0, chapter: 0 }
  for (let i = 0; i < n; i++) {
    const [a, b] = CHAPTERS[i].hold
    if (p >= a && p <= b) return { u: i, phase: b > a ? (p - a) / (b - a) : 1, chapter: i }
    if (i < n - 1) {
      const next = CHAPTERS[i + 1].hold[0]
      if (p > b && p < next) {
        const t = smooth((p - b) / (next - b))
        return { u: i + t, phase: 1, chapter: t < 0.5 ? i : i + 1 }
      }
    }
  }
  return { u: n - 1, phase: 1, chapter: n - 1 }
}

export interface StoryState {
  chapter: number
  u: number
  phase: number
  explode: number
  azim: number
  elev: number
  dist: number
  /** The height the camera looks at. */
  targetY: number
  shiftX: number
  shiftY: number
  spin: number
  /** How lit each layer is, 0…1: the one the camera is on is 1, the rest fall away. */
  focus: [number, number, number, number, number]
}

const lerp = (a: number, b: number, t: number) => a + (b - a) * t

/** The camera's target height for a chapter: its layer, or the middle of the closed engine. */
function chapterTargetY(c: Chapter): number {
  return c.layer < 0 ? 1.0 : layerY(c.layer, 1) + 0.16
}

/** The state of the world at scroll progress `p`. */
export function stateAt(p: number): StoryState {
  const { u, phase, chapter } = locate(p)
  const i = Math.min(CHAPTERS.length - 1, Math.floor(u))
  const j = Math.min(CHAPTERS.length - 1, i + 1)
  const t = u - i
  const A = CHAPTERS[i]
  const B = CHAPTERS[j]
  const focus: StoryState['focus'] = [0, 0, 0, 0, 0]
  for (let k = 0; k < LAYERS.length; k++) {
    // Chapter k + 1 is layer k. A closed engine lights every layer a little.
    const near = Math.max(0, 1 - Math.abs(u - (k + 1)))
    focus[k] = near
  }
  return {
    chapter,
    u,
    phase,
    explode: lerp(A.explode, B.explode, t),
    azim: lerp(A.azim, B.azim, t),
    elev: lerp(A.elev, B.elev, t),
    dist: lerp(A.dist, B.dist, t),
    targetY: lerp(chapterTargetY(A), chapterTargetY(B), t),
    shiftX: lerp(A.shiftX, B.shiftX, t),
    shiftY: lerp(A.shiftY, B.shiftY, t),
    spin: lerp(A.spin, B.spin, t),
    focus,
  }
}

/** The scroll progress at the middle of a chapter's plateau: where a link to it should land. */
export function chapterProgress(key: ChapterKey): number {
  const c = CHAPTERS.find((x) => x.key === key)
  if (!c) return 0
  return (c.hold[0] + c.hold[1]) / 2
}

/* ------------------------------------------------------------------ staging */

/**
 * A rectangle of the viewport, in fractions of its width and height from the
 * top-left corner. A page that lays the engine out itself — the phone story,
 * the sign-in page — measures the box its layout leaves free and hands it to
 * the world, so the two can never drift apart.
 */
export interface Rect {
  x: number
  y: number
  w: number
  h: number
}

export const lerpRect = (a: Rect, b: Rect, t: number): Rect => ({
  x: lerp(a.x, b.x, t),
  y: lerp(a.y, b.y, t),
  w: lerp(a.w, b.w, t),
  h: lerp(a.h, b.h, t),
})

/**
 * How much room the engine takes, in metres. Its width is fixed — the closed
 * case turning on its diagonal, or one layer's plate seen corner-on — but how
 * tall it looks depends on how far above it the camera is, and its nearest
 * edge is `depth` closer to the camera than its middle, so it is that edge
 * that has to fit.
 */
export const SUBJECT = {
  closed: { w: 2.95, tall: 2.0, across: 2.83, depth: 1.42 },
  open: { w: 2.5, tall: 0.5, across: 2.3, depth: 1.15 },
} as const

export interface Staging {
  /** Camera distance at which the engine just fits the rectangle. */
  dist: number
  /** Where the engine's centre lands, as fractions of the viewport from its centre (+x right, +y down). */
  shiftX: number
  shiftY: number
}

/**
 * Fit the engine into a rectangle of a `width`×`height` px viewport, seen from
 * `elevDeg` above the floor through a camera of vertical field of view
 * `fovDeg`. `explode` blends the room the closed case needs with the room one
 * layer needs.
 */
export function stageFit(rect: Rect, width: number, height: number, fovDeg: number, explode: number, elevDeg: number): Staging {
  const focal = height / 2 / Math.tan((fovDeg * Math.PI) / 360)
  const e = (elevDeg * Math.PI) / 180
  const seenHeight = (k: { tall: number; across: number }) => k.tall * Math.cos(e) + k.across * Math.sin(e)
  const w = lerp(SUBJECT.closed.w, SUBJECT.open.w, explode)
  const h = lerp(seenHeight(SUBJECT.closed), seenHeight(SUBJECT.open), explode)
  const depth = lerp(SUBJECT.closed.depth, SUBJECT.open.depth, explode)
  const boxW = Math.max(1, rect.w * width)
  const boxH = Math.max(1, rect.h * height)
  return {
    dist: focal * Math.max(w / boxW, h / boxH) + depth,
    shiftX: rect.x + rect.w / 2 - 0.5,
    shiftY: rect.y + rect.h / 2 - 0.5,
  }
}
