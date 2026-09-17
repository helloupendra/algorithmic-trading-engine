/**
 * The scroll story: one 3D world behind the homepage that rearranges itself as
 * you read down the page, so the product explains itself before a word of copy
 * has to.
 *
 * There is one object — 120 boxes — and six arrangements of it. Scrolling moves
 * a single progress value from 0 to 1; every box eases from where the last
 * chapter put it to where the next one wants it, and the camera eases along the
 * same curve. Nothing is created or destroyed between chapters, which is why a
 * candle can become a strike, a strike can become a point on an equity curve,
 * and the whole chart can collapse into the mark in the footer:
 *
 *   1 THE TAPE      a candlestick chart draws itself, one candle at a time
 *   2 THE HISTORY   the chart multiplies into five years standing behind it
 *   3 THE CHAIN     the candles split into calls and puts around a strike spine
 *   4 THE SETUP     back to one chart, with the bars a setup would have taken lit
 *   5 THE LEDGER    the boxes fall into the equity curve of a real, losing run
 *   6 THE MARK      what is left assembles into the logo
 *
 * Rendering: one instanced mesh (one draw call), one floor plane, no lights —
 * the face tones are baked from the box normals and a key direction, which costs
 * nothing and reads as lit. Depth fades to the page colour. The frame loop runs
 * only while the canvas is on screen and the tab is visible, and a scroll that
 * does not move the progress value past a threshold does not schedule a frame.
 *
 * Under prefers-reduced-motion the scene is drawn once, at the chapter matching
 * the current scroll position, and never animates. Without WebGL, or without
 * the vendored three.js, the page keeps its copy and its screenshots: the CSS
 * behind the canvas is a finished background on its own.
 *
 * The prices are a seeded walk — a plausible tape, not a real instrument — and
 * the object prints no number. The equity curve in chapter 5 is the shape of the
 * run the page shows in full further down: it ends below where it started.
 */

import { useLayoutEffect, useRef, type RefObject } from 'react'
import { prefersReducedMotion } from '../lib/motion'
import { loadThree, webglAvailable } from '../lib/three'

/** Boxes in the world. Every arrangement has to fit inside this budget. */
const ATOMS = 120
/** Candles in the tape: 60 bodies + 60 wicks uses the budget exactly. */
const CANDLES = 60

const COLORS = {
  up: '#25d69a',
  down: '#ff5f5c',
  call: '#5b86ff',
  put: '#2bd4bd',
  dim: '#334565',
  mark: '#c9dbff',
  bg: '#04060c',
}

export interface Chapter {
  /** 0 … 1 — where this chapter sits on the page. */
  at: number
  camera: { pos: [number, number, number]; look: [number, number, number] }
}

/* ------------------------------------------------------------------ the tape */

interface Candle { o: number; h: number; l: number; c: number }

/** A seeded, mean-reverting walk: the same tape on every load, always in frame. */
function tape(seed: number, n: number): Candle[] {
  let s = seed >>> 0
  const rnd = () => {
    s ^= s << 13; s ^= s >>> 17; s ^= s << 5
    return ((s >>> 0) % 100000) / 100000
  }
  const out: Candle[] = []
  let p = 1
  for (let i = 0; i < n; i++) {
    const o = p
    const c = o + (rnd() - 0.5) * 0.12 + (1 - o) * 0.05
    const w = 0.01 + rnd() * 0.05
    out.push({ o, c, h: Math.max(o, c) + w, l: Math.min(o, c) - w })
    p = c
  }
  return out
}

const TAPE = tape(20260918, CANDLES)
/** Price → world height: the chart stands between y = 0.2 and y ≈ 3. */
const yAt = (p: number) => 1.6 + (p - 1) * 6.5

/* --------------------------------------------------------------- the atoms */

/** What one box is doing in one chapter. */
interface Atom {
  x: number; y: number; z: number
  w: number; h: number; d: number
  /** Colour index: 0 up, 1 down, 2 call, 3 put, 4 dim, 5 mark. */
  c: number
}

const HIDDEN: Atom = { x: 0, y: -40, z: 0, w: 0.001, h: 0.001, d: 0.001, c: 4 }

/** Chapter 1 — the tape: 60 bodies, then 60 wicks. */
function layoutTape(i: number): Atom {
  const body = i < CANDLES
  const k = body ? i : i - CANDLES
  const cd = TAPE[k]
  const x = (k - (CANDLES - 1) / 2) * 0.30
  const up = cd.c >= cd.o
  const top = yAt(Math.max(cd.o, cd.c))
  const bot = yAt(Math.min(cd.o, cd.c))
  if (body) {
    return { x, y: (top + bot) / 2, z: 0, w: 0.19, h: Math.max(0.03, top - bot), d: 0.19, c: up ? 0 : 1 }
  }
  return { x, y: (yAt(cd.h) + yAt(cd.l)) / 2, z: 0, w: 0.035, h: yAt(cd.h) - yAt(cd.l), d: 0.035, c: up ? 0 : 1 }
}

/** Chapter 2 — five years: five charts standing one behind the other. */
function layoutYears(i: number): Atom {
  const rows = 5
  const per = 24
  const row = Math.floor(i / per)
  if (row >= rows) return HIDDEN
  const k = i % per
  const cd = TAPE[(row * 7 + k * 2) % CANDLES]
  const x = (k - (per - 1) / 2) * 0.62
  const up = cd.c >= cd.o
  const top = yAt(Math.max(cd.o, cd.c))
  const bot = yAt(Math.min(cd.o, cd.c))
  return {
    x,
    y: (top + bot) / 2,
    z: -row * 2.6,
    w: 0.34,
    h: Math.max(0.06, top - bot),
    d: 0.34,
    c: row === 0 ? (up ? 0 : 1) : 4,
  }
}

/** Chapter 3 — the chain: calls left of the spine, puts right, 30 strikes. */
function layoutChain(i: number): Atom {
  const strikes = 30
  const side = i % 2 === 0 ? -1 : 1
  const k = Math.floor(i / 2)
  if (k >= strikes) return HIDDEN
  const atm = strikes / 2
  const d = Math.abs(k - atm)
  // Open interest peaks a little away from the money on both sides.
  const oi = 0.25 + Math.exp(-((d - 2.5) ** 2) / 26) * 2.0 + ((k * 37) % 11) / 40
  const len = oi * (side < 0 ? 1 : 0.92)
  const y = 0.45 + k * 0.118
  return { x: side * (len / 2 + 0.06), y, z: 0, w: len, h: 0.07, d: 0.22, c: side < 0 ? 2 : 3 }
}

/** Chapter 4 — the setup: the tape again, with the bars a setup would take lit. */
function layoutSetup(i: number): Atom {
  const a = layoutTape(i)
  const k = i < CANDLES ? i : i - CANDLES
  // Four entries, spread across the tape; everything else steps back.
  const taken = k === 11 || k === 26 || k === 38 || k === 51
  return { ...a, c: taken ? a.c : 4, d: taken ? 0.3 : a.d, w: taken ? a.w * 1.5 : a.w }
}

/** Chapter 5 — the ledger: the boxes fall into the run's equity curve. */
function layoutCurve(i: number): Atom {
  const n = ATOMS
  const t = i / (n - 1)
  // The shape of the run this page shows in full: up, over, and down through
  // where it started. It is not a straight line to the floor — it took months.
  const drift = -1.15 * t
  const swing = 0.55 * Math.sin(t * 7.1) + 0.3 * Math.sin(t * 3.3 + 1.2) + 0.18 * Math.sin(t * 13.7)
  const y = 1.75 + drift + swing * (1 - t * 0.45)
  const x = (t - 0.5) * 13.5
  const below = y < 1.75
  return { x, y, z: 0, w: 0.21, h: 0.21, d: 0.21, c: below ? 1 : 0 }
}

/**
 * Chapter 6 — the mark: the platform's own logo, built out of the same boxes.
 * A rounded tile drawn as a ring of cubes, and inside it the three candles the
 * icon is made of — so the object the story has been rearranging ends up as the
 * thing in the tab.
 */
const MARK = { y: 3.5, half: 1.15, cube: 0.11 }

function layoutMark(i: number): Atom {
  const RING = 36
  const PER = 28 // cubes per candle: 18 body, 10 wick
  if (i < RING) {
    // The tile: a square ring with its corners pulled in, walked at a constant
    // step so the cubes are evenly spaced whatever the side length.
    const t = (i / RING) * 4
    const side = Math.floor(t)
    const f = t - side
    const h = MARK.half
    const r = 0.30
    const a = -h + r + f * (2 * h - 2 * r)
    const pts: [number, number][] = [[a, h], [h, -a], [-a, -h], [-h, a]]
    const [x, y] = pts[side]
    return { x, y: MARK.y + y, z: 0, w: MARK.cube, h: MARK.cube, d: MARK.cube, c: 5 }
  }
  const k = i - RING
  const which = Math.floor(k / PER)
  if (which > 2) return HIDDEN
  const j = k % PER
  // Open, close and the wick of each candle in the icon, in its own units.
  const candles = [
    { x: -0.58, top: 0.36, bot: -0.36, wickTop: 0.68, wickBot: -0.62 },
    { x: 0.0, top: 0.64, bot: -0.44, wickTop: 0.86, wickBot: -0.72 },
    { x: 0.58, top: 0.48, bot: -0.34, wickTop: 0.72, wickBot: -0.64 },
  ]
  const c = candles[which]
  if (j < 18) {
    // The body: a column of cubes two wide, so it reads as a solid bar.
    const row = Math.floor(j / 2)
    const col = j % 2
    const steps = 9
    const y = c.bot + ((row + 0.5) / steps) * (c.top - c.bot)
    return { x: c.x + (col ? 0.07 : -0.07), y: MARK.y + y, z: 0, w: 0.14, h: (c.top - c.bot) / steps + 0.015, d: 0.14, c: 5 }
  }
  const row = j - 18
  const steps = 10
  const y = c.wickBot + ((row + 0.5) / steps) * (c.wickTop - c.wickBot)
  return { x: c.x, y: MARK.y + y, z: 0, w: 0.075, h: (c.wickTop - c.wickBot) / steps + 0.01, d: 0.075, c: 5 }
}

const LAYOUTS = [layoutTape, layoutYears, layoutChain, layoutSetup, layoutCurve, layoutMark]

/** Where the camera stands for each chapter. */
export const CHAPTERS: Chapter[] = [
  { at: 0.00, camera: { pos: [0, 2.7, 13.4], look: [0, 1.35, 0] } },
  { at: 0.20, camera: { pos: [9.0, 5.2, 12.0], look: [-1.6, 1.2, -5.2] } },
  { at: 0.40, camera: { pos: [0.4, 2.5, 10.8], look: [0, 2.1, 0] } },
  { at: 0.60, camera: { pos: [-2.4, 2.4, 11.2], look: [0, 1.6, 0] } },
  { at: 0.80, camera: { pos: [0, 2.1, 13.8], look: [0, 1.45, 0] } },
  { at: 1.00, camera: { pos: [0, 3.4, 10.8], look: [0, 2.3, 0] } },
]

/* ---------------------------------------------------------------- shaders */

const VERT = `
attribute vec3 aPos;
attribute vec3 aSize;
attribute vec3 aCol;
uniform float uFogNear, uFogFar;
varying vec3 vCol;
varying float vFog;
void main() {
  vec3 p = position * aSize + aPos;
  vec4 mv = modelViewMatrix * vec4(p, 1.0);
  vFog = clamp((-mv.z - uFogNear) / (uFogFar - uFogNear), 0.0, 1.0);

  // Face tones baked from the box's own normal against a fixed key direction:
  // the top face brightest, the two sides stepping down. No lights, and it
  // still reads as a solid object.
  vec3 n = normalize(mat3(modelMatrix) * normal);
  float key = 0.55 + 0.45 * clamp(dot(n, normalize(vec3(-0.3, 0.85, 0.42))), 0.0, 1.0);
  float rim = 0.18 * pow(1.0 - abs(n.z), 2.0);
  vCol = aCol * key + aCol * rim;

  gl_Position = projectionMatrix * mv;
}
`

const FRAG = `
precision mediump float;
uniform vec3 uBg;
varying vec3 vCol;
varying float vFog;
void main() { gl_FragColor = vec4(mix(vCol, uBg, vFog), 1.0); }
`

/** The floor: a grid that fades out well before the edge of the world. */
const FLOOR_VERT = `
varying vec2 vUv;
varying float vFog;
uniform float uFogNear, uFogFar;
void main() {
  vUv = uv;
  vec4 mv = modelViewMatrix * vec4(position, 1.0);
  vFog = clamp((-mv.z - uFogNear) / (uFogFar - uFogNear), 0.0, 1.0);
  gl_Position = projectionMatrix * mv;
}
`

const FLOOR_FRAG = `
precision mediump float;
uniform vec3 uBg, uLine;
uniform float uCells;
varying vec2 vUv;
varying float vFog;
float grid(vec2 uv, float n) {
  vec2 g = abs(fract(uv * n - 0.5) - 0.5) / fwidth(uv * n);
  float l = min(g.x, g.y);
  return 1.0 - clamp(l - 0.6, 0.0, 1.0);
}
void main() {
  float g = grid(vUv, uCells);
  float fade = smoothstep(0.62, 0.05, length(vUv - 0.5));
  vec3 col = mix(uBg, uLine, g * fade * 0.85);
  gl_FragColor = vec4(mix(col, uBg, vFog), 1.0);
}
`

/* ---------------------------------------------------------------- runtime */

function hexRgb(hex: string): [number, number, number] {
  const n = parseInt(hex.slice(1), 16)
  return [((n >> 16) & 255) / 255, ((n >> 8) & 255) / 255, (n & 255) / 255]
}

const PALETTE = [COLORS.up, COLORS.down, COLORS.call, COLORS.put, COLORS.dim, COLORS.mark].map(hexRgb)

function lerp(a: number, b: number, t: number) { return a + (b - a) * t }
/** Ease used everywhere, so the boxes and the camera move as one thing. */
function ease(t: number) { return t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2 }

function mount(wrap: HTMLDivElement, canvas: HTMLCanvasElement, track: HTMLElement): () => void {
  const still = prefersReducedMotion()
  let disposed = false
  let cancelled = false
  let renderer: any = null
  let scene: any = null
  let camera: any = null
  let mesh: any = null
  let floor: any = null
  let attrs: { pos: any; size: any; col: any } | null = null
  let disposables: { dispose: () => void }[] = []

  let rafId = 0
  let hidden = document.hidden
  let offscreen = false
  /** Where the page is, 0 … 1, and where the scene has eased to. */
  let target = 0
  let current = -1
  let pointerX = 0
  let pointerY = 0
  let panX = 0
  let panY = 0
  let dprCap = 2

  const parked = () => disposed || hidden || offscreen

  /** Scroll position of the tracked element, as 0 … 1. */
  function readScroll(): number {
    const r = track.getBoundingClientRect()
    const total = r.height - window.innerHeight
    if (total <= 0) return 0
    return Math.min(1, Math.max(0, -r.top / total))
  }

  function layoutFor(p: number): { a: number; b: number; t: number } {
    const n = LAYOUTS.length
    const x = Math.min(0.9999, Math.max(0, p)) * (n - 1)
    const a = Math.floor(x)
    return { a, b: Math.min(n - 1, a + 1), t: ease(x - a) }
  }

  function writeAtoms(p: number) {
    if (!attrs) return
    const { a, b, t } = layoutFor(p)
    const fa = LAYOUTS[a]
    const fb = LAYOUTS[b]
    const { pos, size, col } = attrs
    for (let i = 0; i < ATOMS; i++) {
      const A = fa(i)
      const B = fb(i)
      pos.setXYZ(i, lerp(A.x, B.x, t), lerp(A.y, B.y, t), lerp(A.z, B.z, t))
      size.setXYZ(i, lerp(A.w, B.w, t), lerp(A.h, B.h, t), lerp(A.d, B.d, t))
      const ca = PALETTE[A.c]
      const cb = PALETTE[B.c]
      col.setXYZ(i, lerp(ca[0], cb[0], t), lerp(ca[1], cb[1], t), lerp(ca[2], cb[2], t))
    }
    pos.needsUpdate = true
    size.needsUpdate = true
    col.needsUpdate = true
  }

  function placeCamera(p: number) {
    if (!camera) return
    const n = CHAPTERS.length
    const x = Math.min(0.9999, Math.max(0, p)) * (n - 1)
    const a = Math.floor(x)
    const b = Math.min(n - 1, a + 1)
    const t = ease(x - a)
    const A = CHAPTERS[a].camera
    const B = CHAPTERS[b].camera
    camera.position.set(
      lerp(A.pos[0], B.pos[0], t) + panX,
      lerp(A.pos[1], B.pos[1], t) + panY,
      lerp(A.pos[2], B.pos[2], t),
    )
    camera.lookAt(lerp(A.look[0], B.look[0], t), lerp(A.look[1], B.look[1], t), lerp(A.look[2], B.look[2], t))
  }

  function draw(p: number) {
    writeAtoms(p)
    placeCamera(p)
    if (renderer && scene && camera) renderer.render(scene, camera)
  }

  function schedule() {
    if (parked() || rafId || !renderer) return
    rafId = requestAnimationFrame(tick)
  }

  function tick() {
    rafId = 0
    if (parked()) return
    target = readScroll()
    // The scene follows the page rather than snapping to it: a flick of the
    // wheel then reads as the world turning, not as a jump cut.
    const next = current < 0 ? target : current + (target - current) * 0.12
    const dx = pointerX - panX
    const dy = pointerY - panY
    panX += dx * 0.06
    panY += dy * 0.06
    const moved = Math.abs(next - current) > 0.0004 || Math.abs(dx) + Math.abs(dy) > 0.002
    current = next
    if (moved) draw(current)
    if (moved || Math.abs(target - current) > 0.0004) schedule()
  }

  const onScroll = () => schedule()
  const onPointer = (e: PointerEvent) => {
    pointerX = (e.clientX / window.innerWidth - 0.5) * 1.1
    pointerY = -(e.clientY / window.innerHeight - 0.5) * 0.6
    schedule()
  }
  const onVisibility = () => {
    hidden = document.hidden
    if (hidden) { if (rafId) cancelAnimationFrame(rafId); rafId = 0 } else schedule()
  }
  const io =
    'IntersectionObserver' in window
      ? new IntersectionObserver((entries) => {
          const e = entries[entries.length - 1]
          if (!e) return
          offscreen = !e.isIntersecting
          if (offscreen) { if (rafId) cancelAnimationFrame(rafId); rafId = 0 } else schedule()
        })
      : null

  function size() {
    if (!renderer || !camera) return
    const w = wrap.clientWidth
    const h = wrap.clientHeight
    if (!w || !h) return
    renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, dprCap))
    renderer.setSize(w, h, false)
    camera.aspect = w / h
    camera.updateProjectionMatrix()
  }

  let resizeTimer = 0
  const ro = new ResizeObserver(() => {
    if (resizeTimer) clearTimeout(resizeTimer)
    resizeTimer = window.setTimeout(() => {
      resizeTimer = 0
      if (disposed) return
      size()
      draw(current < 0 ? readScroll() : current)
    }, 140)
  })

  function start(): boolean {
    const THREE = window.THREE
    if (!THREE) return false
    try {
      renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: true, powerPreference: 'high-performance' })
    } catch {
      renderer = null
      return false
    }
    renderer.setClearColor(0x000000, 0)
    scene = new THREE.Scene()
    camera = new THREE.PerspectiveCamera(34, 16 / 9, 0.1, 80)

    const fog = { uFogNear: { value: 9.0 }, uFogFar: { value: 34.0 } }
    const bg = { value: new THREE.Color(COLORS.bg) }

    const geo = new THREE.BoxGeometry(1, 1, 1)
    const pos = new THREE.InstancedBufferAttribute(new Float32Array(ATOMS * 3), 3)
    const sz = new THREE.InstancedBufferAttribute(new Float32Array(ATOMS * 3), 3)
    const col = new THREE.InstancedBufferAttribute(new Float32Array(ATOMS * 3), 3)
    ;[pos, sz, col].forEach((a) => a.setUsage(THREE.DynamicDrawUsage))
    geo.setAttribute('aPos', pos)
    geo.setAttribute('aSize', sz)
    geo.setAttribute('aCol', col)
    attrs = { pos, size: sz, col }
    const mat = new THREE.ShaderMaterial({
      uniforms: { ...fog, uBg: bg },
      vertexShader: VERT,
      fragmentShader: FRAG,
    })
    mesh = new THREE.InstancedMesh(geo, mat, ATOMS)
    mesh.frustumCulled = false
    scene.add(mesh)

    const floorGeo = new THREE.PlaneGeometry(60, 60)
    const floorMat = new THREE.ShaderMaterial({
      uniforms: { ...fog, uBg: bg, uLine: { value: new THREE.Color('#16223a') }, uCells: { value: 34 } },
      vertexShader: FLOOR_VERT,
      fragmentShader: FLOOR_FRAG,
      extensions: { derivatives: true },
    })
    floor = new THREE.Mesh(floorGeo, floorMat)
    floor.rotation.x = -Math.PI / 2
    floor.position.y = 0
    floor.frustumCulled = false
    scene.add(floor)

    disposables = [geo, mat, floorGeo, floorMat]
    canvas.addEventListener('webglcontextlost', onLost)
    size()
    current = readScroll()
    draw(current)
    canvas.classList.add('is-on')
    if (!still) {
      window.addEventListener('scroll', onScroll, { passive: true })
      window.addEventListener('pointermove', onPointer, { passive: true })
    }
    return true
  }

  const onLost = () => {
    teardown()
    canvas.classList.remove('is-on')
  }

  function teardown() {
    if (rafId) cancelAnimationFrame(rafId)
    rafId = 0
    canvas.removeEventListener('webglcontextlost', onLost)
    window.removeEventListener('scroll', onScroll)
    window.removeEventListener('pointermove', onPointer)
    if (scene) {
      if (mesh) scene.remove(mesh)
      if (floor) scene.remove(floor)
    }
    disposables.forEach((d) => d.dispose?.())
    disposables = []
    renderer?.dispose()
    renderer = null
    scene = null
    camera = null
    mesh = null
    floor = null
    attrs = null
  }

  document.addEventListener('visibilitychange', onVisibility)
  ro.observe(wrap)
  io?.observe(wrap)

  if (webglAvailable()) {
    void loadThree().then((ok) => {
      if (cancelled || !ok) return
      start()
    })
  }

  return () => {
    disposed = true
    cancelled = true
    if (resizeTimer) clearTimeout(resizeTimer)
    ro.disconnect()
    io?.disconnect()
    document.removeEventListener('visibilitychange', onVisibility)
    teardown()
  }
}

/**
 * The world as a fixed layer behind the page. `trackRef` is the element whose
 * scroll drives the story — the story's own wrapper, so the scene is still
 * when the page is above or below it.
 */
export function ScrollScene({ trackRef, className }: { trackRef: RefObject<HTMLElement | null>; className: string }) {
  const wrapRef = useRef<HTMLDivElement | null>(null)
  const canvasRef = useRef<HTMLCanvasElement | null>(null)

  useLayoutEffect(() => {
    const wrap = wrapRef.current
    const canvas = canvasRef.current
    // The story's own ref is attached AFTER its children's layout effects run —
    // refs attach bottom-up — so trackRef.current is still null here on the
    // first pass. The wrapper's parent IS the story, which is why the fallback
    // is not a guess.
    const track = trackRef.current ?? wrap?.parentElement ?? null
    if (!wrap || !canvas || !track) return
    return mount(wrap, canvas, track)
  }, [trackRef])

  return (
    <div className={`${className} world`} ref={wrapRef} aria-hidden="true">
      <canvas className="world__gl" ref={canvasRef} />
    </div>
  )
}
