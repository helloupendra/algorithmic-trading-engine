/**
 * Camera, layout and colour for the strike ledger — shared by the WebGL scene,
 * its inline-SVG twin and the DOM labels, so all three register to the pixel.
 *
 * Everything is hand-rolled (no three import): the same lookAt and perspective
 * maths THREE.PerspectiveCamera uses, written once here and fed to the camera
 * as numbers. That is what lets the SVG twin be the loading state rather than
 * a different picture, lets the labels sit on the object instead of near it,
 * and keeps the per-frame parallax free of allocation (updateFrame mutates).
 *
 * Group space: the strike spine runs along y from -20 to +20 at x = z = 0;
 * call bars extend toward -x, put bars toward +x; older poll rounds sit at
 * z = -age * D. One unit is one strike pitch. The login variant rotates the
 * whole group -90° about z so the spine lies along the foot of the page.
 */

import { CELLS, STRIKES, type ChainModel, slotAtAge } from './chainModel'

export type Variant = 'hero' | 'login'

export interface AnchorRect {
  left: number
  top: number
  width: number
  height: number
  bottom: number
}

export interface Rects {
  W: number
  H: number
  /** The element the object is laid out against, relative to the canvas wrapper. */
  anchor: AnchorRect
}

export interface Frame {
  variant: Variant
  W: number
  H: number
  fov: number
  aspect: number
  camPos: [number, number, number]
  target: [number, number, number]
  pxPerUnit: number
  N: number
  D: number
  maxLen: number
  barH: number
  rotZ: 0 | -90
  pivotPx: [number, number]
  dim: number
  /** Tone of the lit face (world +y); the login lowers it so the tips stop dominating. */
  topTone: number
  /** Whether the spine carries per-strike rungs (the login keeps only the hairline). */
  rungs: boolean
  /* Derived, for project(); kept on the frame so per-frame work allocates nothing. */
  right: [number, number, number]
  up: [number, number, number]
  forward: [number, number, number]
  tanHalf: number
  d: number
  /**
   * False when the object cannot be placed without either clipping at the
   * bottom or crowding the card above it. The component hides the wrapper
   * rather than draw a cut-off shelf — an object partly off the page reads as
   * a bug, an absent one reads as a quiet login.
   */
  fits: boolean
}

export const FOV = 28
export const BAR_H = 0.5

/**
 * Per-variant geometry. The login is the hero seen from a lower angle with the
 * layers spread further apart: at 30° over eight tightly packed layers the lit
 * tips stacked into columns of squares; at 14° with D = 0.7 each older layer
 * shows as a thin sliver behind the one in front — a receding stair. (The
 * login camera sits only ~67 units out, so a layer also converges toward the
 * screen centre: a step is ~8 px, not the 4.6 the pitch alone would give, and
 * seven of them must still keep the back tips clear of the risk note.) Its
 * lit face is held to 0.85 so that, with dim 0.7 (enough for the put fronts
 * to read), the brightest pixel stays at about 60% of the hero's.
 */
export const GEOM = {
  hero: { yaw: 26, pitch: 10, D: 3.2, maxLen: 14.7, dim: 1, topTone: 1, rungs: true, rotZ: 0 as const },
  login: { yaw: 0, pitch: 14, D: 0.7, maxLen: 1.78, dim: 0.7, topTone: 0.85, rungs: false, rotZ: -90 as const },
}

/** Pointer parallax range (hero only), degrees; the crop rect is sized for it. */
export const PARALLAX = { yaw: 1.5, pitch: 0.8 }

/**
 * Colours are final sRGB values from the design tokens, mixed here once so the
 * shader, the SVG and the labels agree without reading computed styles.
 *  call / put: --neg / --pos mixed 40% toward --text-2 (saturated bars look cheap)
 *  nullCell:   --line 40% toward --bg0    spine: --line at 60% on --bg0
 *  rungWide:   --text-3 30% toward --bg0  wire: --brand-hi   rung: --brand
 */
export const COLORS = {
  call: '#cc7b80',
  put: '#57b69d',
  nullCell: '#131b26',
  spine: '#141d29',
  rungWide: '#5a6779',
  wire: '#7096ff',
  rung: '#4f7dff',
  bg: '#070b11',
  text3: '#74849a',
}

const DEG = Math.PI / 180

function clamp(v: number, lo: number, hi: number): number {
  return v < lo ? lo : v > hi ? hi : v
}

/** Layers kept for a viewport width; a model parameter, so it only changes on resize. */
export function layersFor(variant: Variant, W: number): number {
  if (variant === 'login') return 8
  return W >= 1100 ? 24 : W >= 900 ? 16 : 12
}

/**
 * Lays the object out against the anchor and derives the camera. Allocating:
 * call on layout only. Per-frame parallax goes through updateFrame().
 */
/**
 * Pixels the login object needs below its pivot: the ATM label's bottom edge
 * projects 71 px under the pivot (measured, not summed from the parts — the
 * put reach is above the label, not in addition to it), plus a 16 px margin.
 */
export const LOGIN_BOTTOM_RESERVE = 87

/** How far above the pivot the oldest layer's call tips project at full size, CSS px (measured). */
export const LOGIN_BACK_TIP_RISE = 86

/** Gap kept between the risk note's bottom edge and the highest tip, CSS px. */
export const LOGIN_NOTE_MARGIN = 8

/** Below this fraction of full size the object stops reading as one; hide instead. */
export const LOGIN_MIN_SCALE = 0.4

export function frameFor(variant: Variant, rects: Rects, yawOffsetDeg = 0, pitchOffsetDeg = 0): Frame {
  const g = GEOM[variant]
  const { W, H, anchor } = rects
  let pivotX: number
  let pivotY: number
  let pxPerUnit: number
  let fits = true
  const narrow = variant === 'hero' && W <= 900

  if (variant === 'hero') {
    pivotX = anchor.left + (narrow ? 0.42 : 0.36) * anchor.width
    pivotY = anchor.top + 0.5 * anchor.height
    // Front spine height in CSS px for 41 strikes; bounded by the stage column
    // and clamped so the object is never the biggest thing on the page.
    const S = narrow ? 240 : clamp(0.75 * anchor.height, 280, 420) * clamp(anchor.width / 508, 0.7, 1)
    pxPerUnit = Math.max(S, 40) / 40
  } else {
    pxPerUnit = clamp((0.75 * W) / 40, 14, 30)
    pivotX = W / 2
    // Two constraints, measured rather than guessed with a media query:
    //  - never so high that the back layer's call tips (47 px of bar plus
    //    ~57 px of stair) come within 15 px of the card's risk note;
    //  - never so low that the ATM label under the put tips (its bottom edge
    //    projects 71 px below the pivot) and a 16 px margin leave the page.
    // Between 680 and ~816 px tall — every 13-inch laptop with browser chrome —
    // the old rule let the second lose: the ATM label was cut mid-glyph.
    // The shelf sits as low as the ATM label allows, and is exactly as large as
    // the room between the risk note and that line permits: the oldest layer's
    // call tips rise LOGIN_BACK_TIP_RISE px above the pivot at full size, so
    // the size is the fraction of that the gap can hold. A full-height display
    // gets the whole object; a 13-inch laptop under browser chrome (730-790 px)
    // gets it at 40-75%; a genuinely short page gets none — a shelf pushed up
    // through the note, or cut off at the bottom, reads as a bug, and absence
    // reads as a quiet login.
    pivotY = H - LOGIN_BOTTOM_RESERVE
    const room = pivotY - anchor.bottom - LOGIN_NOTE_MARGIN
    const scale = Math.min(1, room / LOGIN_BACK_TIP_RISE)
    fits = scale >= LOGIN_MIN_SCALE
    pxPerUnit *= Math.max(scale, LOGIN_MIN_SCALE)
  }

  const frame: Frame = {
    variant,
    W,
    H,
    fov: FOV,
    aspect: H > 0 ? W / H : 1,
    camPos: [0, 0, 0],
    target: [0, 0, 0],
    pxPerUnit,
    N: layersFor(variant, W),
    D: g.D,
    maxLen: g.maxLen,
    barH: BAR_H,
    rotZ: g.rotZ,
    pivotPx: [pivotX, pivotY],
    dim: g.dim,
    topTone: g.topTone,
    rungs: g.rungs,
    right: [0, 0, 0],
    up: [0, 0, 0],
    forward: [0, 0, 0],
    tanHalf: Math.tan((FOV / 2) * DEG),
    d: 1,
    fits,
  }
  updateFrame(frame, yawOffsetDeg, pitchOffsetDeg)
  if (narrow) fitToStage(frame, anchor, yawOffsetDeg, pitchOffsetDeg)
  return frame
}

/**
 * Single-column hero: the stage is a fixed row under the stats and the whole
 * block — side labels, stack, caption — must live inside it, or the labels
 * land among the stat labels and read as two more of them. Measures the
 * projected block, shrinks the scale if it overflows, and centres it. The
 * picture is a pure translation of the pivot, so centring is exact.
 */
function fitToStage(frame: Frame, anchor: AnchorRect, yawOffsetDeg: number, pitchOffsetDeg: number): void {
  const avail = anchor.height - 2 * STAGE_PAD
  if (avail <= 0) return
  // The block is not quite proportional to the scale (the camera distance
  // moves with it), so the shrink takes a couple of passes to settle.
  for (let pass = 0; pass < 3; pass++) {
    const height = blockExtent(frame, B)
    if (height <= avail + 0.01) break
    frame.pxPerUnit *= avail / height
    updateFrame(frame, yawOffsetDeg, pitchOffsetDeg)
  }
  const height = blockExtent(frame, B)
  frame.pivotPx[1] += anchor.top + STAGE_PAD + (avail - height) / 2 - B[0]
  updateFrame(frame, yawOffsetDeg, pitchOffsetDeg)
}

/** Scratch for fitToStage: [top, bottom] of the projected block in wrapper px. */
const B: [number, number] = [0, 0]
const Q: [number, number] = [0, 0]

/** Top and bottom of the labels-to-caption block on screen; returns its height. */
function blockExtent(frame: Frame, out: [number, number]): number {
  callsLabel(frame, Q)
  let top = Q[1] - LABEL_H
  putsLabel(frame, Q)
  if (Q[1] - LABEL_H < top) top = Q[1] - LABEL_H
  // The stack itself can climb above either label anchor (the back layers
  // rise). Measured at the last layer that still carries contrast — beyond
  // it the fog has taken ~80% — so the visible object is what gets centred.
  const backZ = -Math.round(0.75 * frame.N) * frame.D
  project(frame, -frame.maxLen, 20.5, backZ, Q)
  if (Q[1] < top) top = Q[1]
  project(frame, frame.maxLen, 20.5, backZ, Q)
  if (Q[1] < top) top = Q[1]
  captionAnchor(frame, Q)
  const bottom = Q[1] + CAPTION_H
  out[0] = top
  out[1] = bottom
  return bottom - top
}

/**
 * Re-derives the camera for a yaw/pitch offset, in place. The camera orbits
 * the pivot and is then translated so the pivot lands on pivotPx regardless
 * of the angle — the front spine stays pinned and only the depth swings.
 */
export function updateFrame(frame: Frame, yawOffsetDeg: number, pitchOffsetDeg: number): void {
  const g = GEOM[frame.variant]
  const yaw = (g.yaw + yawOffsetDeg) * DEG
  const pitch = (g.pitch + pitchOffsetDeg) * DEG
  const { W, H, pxPerUnit, tanHalf, aspect } = frame

  // Distance at which one unit at the pivot covers pxPerUnit pixels.
  const d = (H / pxPerUnit / 2) / tanHalf
  frame.d = d

  const cp = Math.cos(pitch)
  const dx = Math.sin(yaw) * cp
  const dy = Math.sin(pitch)
  const dz = Math.cos(yaw) * cp

  const f = frame.forward
  f[0] = -dx
  f[1] = -dy
  f[2] = -dz

  // right = normalize(cross(forward, up(0,1,0)))
  const r = frame.right
  r[0] = Math.cos(yaw)
  r[1] = 0
  r[2] = -Math.sin(yaw)

  // up = cross(right, forward)
  const u = frame.up
  u[0] = r[1] * f[2] - r[2] * f[1]
  u[1] = r[2] * f[0] - r[0] * f[2]
  u[2] = r[0] * f[1] - r[1] * f[0]

  const visH = 2 * d * tanHalf
  const visW = visH * aspect
  const fx = (frame.pivotPx[0] - W / 2) / W
  const fy = (frame.pivotPx[1] - H / 2) / H
  const sr = -fx * visW
  const su = fy * visH
  const shx = r[0] * sr + u[0] * su
  const shy = r[1] * sr + u[1] * su
  const shz = r[2] * sr + u[2] * su

  frame.camPos[0] = d * dx + shx
  frame.camPos[1] = d * dy + shy
  frame.camPos[2] = d * dz + shz
  frame.target[0] = shx
  frame.target[1] = shy
  frame.target[2] = shz
}

/**
 * Group-space point -> CSS pixels in the wrapper. Mirrors THREE's lookAt view
 * matrix and vertical-fov perspective exactly; near/far do not affect x, y.
 */
export function project(frame: Frame, x: number, y: number, z: number, out: [number, number]): void {
  let wx = x
  let wy = y
  if (frame.rotZ === -90) {
    wx = y
    wy = -x
  }
  const vx = wx - frame.camPos[0]
  const vy = wy - frame.camPos[1]
  const vz = z - frame.camPos[2]
  const r = frame.right
  const u = frame.up
  const f = frame.forward
  const xc = vx * r[0] + vy * r[1] + vz * r[2]
  const yc = vx * u[0] + vy * u[1] + vz * u[2]
  const zc = vx * f[0] + vy * f[1] + vz * f[2]
  const ndcX = xc / (zc * frame.tanHalf * frame.aspect)
  const ndcY = yc / (zc * frame.tanHalf)
  out[0] = (ndcX + 1) * 0.5 * frame.W
  out[1] = (1 - ndcY) * 0.5 * frame.H
}

/* ------------------------------------------------------------------ colour */

function hexToRgb(hex: string): [number, number, number] {
  const n = parseInt(hex.slice(1), 16)
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255]
}

const RGB = {
  call: hexToRgb(COLORS.call),
  put: hexToRgb(COLORS.put),
  nullCell: hexToRgb(COLORS.nullCell),
  bg: hexToRgb(COLORS.bg),
  rung: hexToRgb(COLORS.rung),
  wire: hexToRgb(COLORS.wire),
}

/** The three baked face tones; there are no lights in the scene. */
export const TONE = { top: 1.0, cap: 0.72, front: 0.55 } as const
export type Face = keyof typeof TONE

function rgbString(r: number, g: number, b: number): string {
  return `rgb(${Math.round(r)},${Math.round(g)},${Math.round(b)})`
}

/** Depth fog toward the page colour: age 1 of 24 -> 6%, age 12 -> 54%, age 23 -> 96%. */
export function fogFor(age: number, N: number): number {
  return Math.pow(clamp(age / N, 0, 1), 0.9)
}

/**
 * Mirrors the bar shader: lit = mix(bg, colour * tone, dim), then fogged
 * toward the page by age. Null cells take the socket colour with no tone.
 * `topTone` replaces the lit face's 1.0 where a variant holds it lower.
 */
export function faceColour(
  side: 'call' | 'put' | 'null',
  face: Face,
  age: number,
  N: number,
  dim: number,
  topTone: number = TONE.top,
): string {
  const c = side === 'null' ? RGB.nullCell : side === 'call' ? RGB.call : RGB.put
  const tone = side === 'null' ? 1 : face === 'top' ? topTone : TONE[face]
  const fog = fogFor(age, N)
  const bg = RGB.bg
  const out = [0, 0, 0]
  for (let i = 0; i < 3; i++) {
    const lit = bg[i] * (1 - dim) + c[i] * tone * dim
    out[i] = lit * (1 - fog) + bg[i] * fog
  }
  return rgbString(out[0], out[1], out[2])
}

/** A brand colour dimmed toward the page by (1 - dim): the ATM rung and the wire. */
export function accentColour(which: 'rung' | 'wire', dim: number): string {
  const c = RGB[which]
  const bg = RGB.bg
  return rgbString(
    bg[0] * (1 - dim) + c[0] * dim,
    bg[1] * (1 - dim) + c[1] * dim,
    bg[2] * (1 - dim) + c[2] * dim,
  )
}

/* -------------------------------------------------------------- SVG twin */

export interface SvgPath {
  fill: string
  d: string
  kind: 'face' | 'null'
}

/** Thickness of a null socket in units (about one CSS pixel at hero scale). */
export const NULL_H = 0.1
/** Length of a null socket in units. */
export const NULL_LEN = 0.6

/* Scratch for the SVG builders; they run on layout, never per frame. */
const P: [number, number] = [0, 0]

function pt(frame: Frame, x: number, y: number, z: number): string {
  project(frame, x, y, z, P)
  return `${Math.round(P[0])} ${Math.round(P[1])}`
}

/**
 * The four corners of one box face in group space, as an SVG subpath.
 * Boxes: x in [x0, x1], y in [y0, y1], z in [z0, z1].
 */
function quad(
  frame: Frame,
  face: 'px' | 'nx' | 'py' | 'pz',
  x0: number, x1: number, y0: number, y1: number, z0: number, z1: number,
): string {
  switch (face) {
    case 'px':
      return `M${pt(frame, x1, y0, z0)}L${pt(frame, x1, y1, z0)}L${pt(frame, x1, y1, z1)}L${pt(frame, x1, y0, z1)}Z`
    case 'nx':
      return `M${pt(frame, x0, y0, z0)}L${pt(frame, x0, y1, z0)}L${pt(frame, x0, y1, z1)}L${pt(frame, x0, y0, z1)}Z`
    case 'py':
      return `M${pt(frame, x0, y1, z0)}L${pt(frame, x1, y1, z0)}L${pt(frame, x1, y1, z1)}L${pt(frame, x0, y1, z1)}Z`
    default:
      return `M${pt(frame, x0, y0, z1)}L${pt(frame, x1, y0, z1)}L${pt(frame, x1, y1, z1)}L${pt(frame, x0, y1, z1)}Z`
  }
}

/**
 * Which box faces the camera can see, and the tone each takes, using the same
 * rule as the shader on the world-space normal: +y is the lit top, +x the
 * end cap, +z the front. The hero (camera on +x, above, in front) sees the
 * top, the +x cap and the front; the login (spine rotated flat, camera above)
 * sees the front and the call tips, which face world +y after the rotation.
 */
function visibleFaces(frame: Frame): { face: 'px' | 'nx' | 'py' | 'pz'; tone: Face }[] {
  const g = GEOM[frame.variant]
  const yaw = g.yaw * DEG
  const pitch = g.pitch * DEG
  const cam = [Math.sin(yaw) * Math.cos(pitch), Math.sin(pitch), Math.cos(yaw) * Math.cos(pitch)]
  const faces: { face: 'px' | 'nx' | 'py' | 'pz'; tone: Face }[] = []
  const candidates: { face: 'px' | 'nx' | 'py' | 'pz'; n: [number, number, number] }[] = [
    { face: 'py', n: [0, 1, 0] },
    { face: 'px', n: [1, 0, 0] },
    { face: 'nx', n: [-1, 0, 0] },
    { face: 'pz', n: [0, 0, 1] },
  ]
  for (const c of candidates) {
    let wx = c.n[0]
    let wy = c.n[1]
    if (frame.rotZ === -90) {
      wx = c.n[1]
      wy = -c.n[0]
    }
    const dot = wx * cam[0] + wy * cam[1] + c.n[2] * cam[2]
    if (dot < 1e-3) continue
    const tone: Face = wy > 0.5 ? 'top' : wx > 0.5 ? 'cap' : 'front'
    faces.push({ face: c.face, tone })
  }
  return faces
}

/** True when put bars (group +x) point toward the camera and must be painted last. */
function putsNearer(frame: Frame): boolean {
  const g = GEOM[frame.variant]
  const yaw = g.yaw * DEG
  const pitch = g.pitch * DEG
  const cam = [Math.sin(yaw) * Math.cos(pitch), Math.sin(pitch)]
  const px = frame.rotZ === -90 ? 0 : 1
  const py = frame.rotZ === -90 ? -1 : 0
  return px * cam[0] + py * cam[1] > 0
}

/**
 * Back-to-front fills for the front `layers` rounds: per layer, per side, one
 * path per visible face (all 41 bars of that face in one path) plus a path of
 * null sockets where the poller has never seen the contract.
 */
export function svgPaths(model: ChainModel, frame: Frame, layers: number): SvgPath[] {
  const out: SvgPath[] = []
  const faces = visibleFaces(frame)
  const sides: (0 | 1)[] = putsNearer(frame) ? [0, 1] : [1, 0]
  const count = Math.min(layers, model.n)
  const depth = 0.6 * frame.D
  const half = frame.barH / 2

  for (let i = count - 1; i >= 0; i--) {
    const slot = slotAtAge(model.head, i, model.n)
    const z0 = -i * frame.D - depth
    const z1 = -i * frame.D
    for (const side of sides) {
      const sign = side === 0 ? -1 : 1
      const sideName = side === 0 ? 'call' : 'put'
      let nulls = ''
      const perFace: string[] = faces.map(() => '')
      for (let s = 0; s < STRIKES; s++) {
        const v = model.values[slot * CELLS + s * 2 + side]
        const y = s - 20
        if (v <= 0.001) {
          const len = NULL_LEN
          const x0 = sign < 0 ? -len : 0
          const x1 = sign < 0 ? 0 : len
          nulls += quad(frame, 'pz', x0, x1, y - NULL_H / 2, y + NULL_H / 2, z0, z1)
          continue
        }
        const len = v * frame.maxLen
        const x0 = sign < 0 ? -len : 0
        const x1 = sign < 0 ? 0 : len
        for (let k = 0; k < faces.length; k++) {
          perFace[k] += quad(frame, faces[k].face, x0, x1, y - half, y + half, z0, z1)
        }
      }
      for (let k = 0; k < faces.length; k++) {
        if (perFace[k]) {
          out.push({
            kind: 'face',
            fill: faceColour(sideName, faces[k].tone, i, frame.N, frame.dim, frame.topTone),
            d: perFace[k],
          })
        }
      }
      if (nulls) out.push({ kind: 'null', fill: faceColour('null', 'front', i, frame.N, frame.dim), d: nulls })
    }
  }
  return out
}

/* ------------------------------------------------------------------ type */

/** Line height of a side label, CSS px (11px/1 mono, see .chain__label). */
export const LABEL_H = 11
/** Clearance between a label's bottom edge and the tips it names, CSS px. */
export const LABEL_GAP = 10
/** Height of the hero's two-line caption, CSS px (11px/1.45 mono). */
export const CAPTION_H = 32
/** Breathing room between the stage row's edges and the object's block. */
export const STAGE_PAD = 4

/**
 * The layer whose put tips the PUTS label names: about where the fog reaches
 * half, which is where the eye reads the edge of the green mass. Beyond it
 * the layers are ghosts; a label over them would float on empty page.
 */
export function massAge(N: number): number {
  return Math.round(0.45 * N)
}

/**
 * CALLS: left-aligned at the front layer's call reach, its bottom edge
 * LABEL_GAP above the front layer's top. `out` = [x, bottom y].
 */
export function callsLabel(frame: Frame, out: [number, number]): void {
  project(frame, -frame.maxLen, 20.5, 0, out)
  out[1] -= LABEL_GAP
}

/**
 * PUTS: right-aligned at the put tips of the mass layer — so it sits over the
 * green, not over the front tips the stack has already stepped away from — but
 * on the SAME baseline as CALLS. Taking y from the mass layer too put the two
 * labels 11 px apart and PUTS over fog rather than over lit tips. `out` =
 * [x, bottom y].
 */
export function putsLabel(frame: Frame, out: [number, number]): void {
  project(frame, frame.maxLen, 20.5, -massAge(frame.N) * frame.D, out)
  const x = out[0]
  project(frame, -frame.maxLen, 20.5, 0, out)
  out[0] = x
  out[1] -= LABEL_GAP
}

/** Hero caption: right-aligned to the put reach, top edge below the spine's foot. */
export function captionAnchor(frame: Frame, out: [number, number]): void {
  project(frame, frame.maxLen, -24.5, 0, out)
}

/**
 * The ATM label anchor. Hero: off the left end of the rung, which is always
 * clear — the rung outreaches the longest bar and older layers only step
 * right. Login: below the put tips.
 */
export function atmAnchor(frame: Frame, atm: number, out: [number, number, number]): void {
  if (frame.variant === 'hero') {
    out[0] = -(frame.maxLen + 0.7)
    out[1] = atm - 20
    out[2] = 0
  } else {
    out[0] = 2.4
    out[1] = atm - 20
    out[2] = 0
  }
}

/* ------------------------------------------------------------------ crop */

/**
 * The rect of the wrapper the GL canvas needs to cover: the object's projected
 * bounds at every parallax extreme plus a margin, clamped to the wrapper and
 * snapped outward to whole pixels. The canvas is sized to this and the camera
 * takes it as a view offset, so the picture is a crop of the full-frame
 * projection — the SVG twin and the labels still register — while the GPU
 * clears and resolves a fraction of the page instead of all of it.
 * `out` = [left, top, width, height]. Restores the frame to (yaw, pitch).
 */
export function cropFor(
  frame: Frame,
  yawOffsetDeg: number,
  pitchOffsetDeg: number,
  margin: number,
  out: [number, number, number, number],
): void {
  let minX = Infinity
  let minY = Infinity
  let maxX = -Infinity
  let maxY = -Infinity
  const xs = CROP_X
  xs[0] = -(frame.maxLen + 0.15)
  xs[1] = frame.maxLen + 0.15
  const zs = CROP_Z
  zs[0] = 0.3
  zs[1] = -(frame.N - 1) * frame.D - 0.6 * frame.D
  const swing = frame.variant === 'hero' && !frame.rotZ
  for (let e = 0; e < (swing ? 4 : 1); e++) {
    if (swing) {
      updateFrame(frame, (e & 1 ? 1 : -1) * PARALLAX.yaw, (e & 2 ? 1 : -1) * PARALLAX.pitch)
    }
    for (let i = 0; i < 2; i++) {
      for (let j = 0; j < 2; j++) {
        for (let k = 0; k < 2; k++) {
          project(frame, xs[i], j ? 20.5 : -20.5, zs[k], Q)
          if (Q[0] < minX) minX = Q[0]
          if (Q[0] > maxX) maxX = Q[0]
          if (Q[1] < minY) minY = Q[1]
          if (Q[1] > maxY) maxY = Q[1]
        }
      }
    }
  }
  if (swing) updateFrame(frame, yawOffsetDeg, pitchOffsetDeg)
  const x0 = clamp(Math.floor(minX - margin), 0, frame.W)
  const y0 = clamp(Math.floor(minY - margin), 0, frame.H)
  const x1 = clamp(Math.ceil(maxX + margin), 0, frame.W)
  const y1 = clamp(Math.ceil(maxY + margin), 0, frame.H)
  out[0] = x0
  out[1] = y0
  out[2] = Math.max(x1 - x0, 1)
  out[3] = Math.max(y1 - y0, 1)
}

const CROP_X = [0, 0]
const CROP_Z = [0, 0]

/* -------------------------------------------------------------- SVG twin */

/**
 * The whole twin as SVG inner markup: faces, spine (and, on the hero, the
 * rungs), the wire and the ATM rung. Built on layout, not per frame.
 */
export function svgMarkup(model: ChainModel, frame: Frame, layers: number): string {
  const parts: string[] = []
  const atm = model.atm()

  for (const p of svgPaths(model, frame, layers)) {
    parts.push(`<path fill="${p.fill}" d="${p.d}"/>`)
  }

  // Spine and rungs: hairlines between the calls and the puts. The login has
  // no rungs — rotated onto the bar axis they would poke out of short bars.
  let spine = `M${pt(frame, 0, -20, 0.05)}L${pt(frame, 0, 20, 0.05)}`
  let wide = ''
  if (frame.rungs) {
    for (let s = 0; s < STRIKES; s++) {
      const y = s - 20
      if (s % 5 === 0) wide += `M${pt(frame, -0.6, y, 0.05)}L${pt(frame, 0.6, y, 0.05)}`
      else spine += `M${pt(frame, -0.3, y, 0.05)}L${pt(frame, 0.3, y, 0.05)}`
    }
  }
  parts.push(`<path fill="none" stroke="${COLORS.spine}" stroke-width="1" d="${spine}"/>`)
  if (wide) parts.push(`<path fill="none" stroke="${COLORS.rungWide}" stroke-width="1" d="${wide}"/>`)

  // The spot wire through the front layers, one segment per pair so it fades with depth.
  const wireLayers = Math.min(layers, model.n)
  const wireColour = accentColour('wire', frame.dim)
  const wireWidth = wireWidthFor(frame)
  let prev = ''
  for (let i = 0; i < wireLayers; i++) {
    const slot = slotAtAge(model.head, i, model.n)
    const here = pt(frame, 0, model.spot[slot] - 20, -i * frame.D)
    if (prev) {
      const fade = 1 - fogFor(i, frame.N)
      parts.push(
        `<path fill="none" stroke="${wireColour}" stroke-width="${wireWidth}" stroke-opacity="${(0.85 * fade).toFixed(2)}" d="M${prev}L${here}"/>`,
      )
    }
    prev = here
  }

  // ATM rung across the front layer.
  const ext = frame.maxLen + 0.15
  parts.push(
    `<path fill="none" stroke="${accentColour('rung', frame.dim)}" stroke-width="1.5" d="M${pt(frame, -ext, atm - 20, 0.3)}L${pt(frame, ext, atm - 20, 0.3)}"/>`,
  )

  return parts.join('')
}

/**
 * Full width of the spot wire in CSS px. The login's wire runs through a
 * dimmer object over a shorter depth, so it gets a little more weight to
 * stay discernible at 1x.
 */
export function wireWidthFor(frame: Frame): number {
  return frame.variant === 'login' ? 2 : 1.5
}
