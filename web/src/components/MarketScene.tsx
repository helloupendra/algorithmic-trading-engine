/**
 * The shared WebGL backdrop: a live 3D candlestick chart.
 *
 * It is the product's own object, not an abstract effect — chunky lit candles
 * on a fading grid floor, a glowing close ribbon through them, a dashed last
 * price line and a price tag that ticks. A new bar forms every BAR_MS: the
 * rightmost candle grows as its close moves, the whole tape slides left by one
 * slot over the same period, and the vertical scale eases to the visible high
 * and low the way a real chart auto-fits.
 *
 * Depth comes from a low three-quarter camera (the candles are boxes, so the
 * angle shows their sides), an additive halo instance behind every candle, one
 * horizon glow, and distance fades straight to alpha so the tape dissolves into
 * the page instead of ending on a hard edge.
 *
 * Draw calls: bodies, halos, wicks, ribbon, grid, glow, price tag, sparks.
 *
 * Three.js r128 (UMD) is injected on demand from our own origin, so the console
 * bundle never carries it and a self-hosted install with no internet still gets
 * the scene — a CDN would silently drop it to the CSS gradient. The file is
 * vendored at web/public/vendor/three.min.js. Without the script or WebGL the
 * caller's CSS gradient stays up.
 */

import { useEffect, useRef, type RefObject } from 'react'
import { prefersReducedMotion } from '../lib/motion'

const THREE_SRC = `${import.meta.env.BASE_URL}vendor/three.min.js`

declare global {
  interface Window {
    // Injected at runtime by loadThree(); typed loosely on purpose (not a bundled dep).
    THREE?: any
  }
}

let threeLoader: Promise<boolean> | null = null

/**
 * Injects the Three.js script once; resolves false when it cannot load. A failed
 * load is not memoised: the dead <script> is removed and the loader reset so the
 * next mount retries.
 */
function loadThree(): Promise<boolean> {
  if (window.THREE) return Promise.resolve(true)
  if (threeLoader) return threeLoader
  threeLoader = new Promise<boolean>((resolve) => {
    const script = document.createElement('script')
    script.src = THREE_SRC
    script.async = true
    const fail = () => {
      script.remove()
      threeLoader = null
      resolve(false)
    }
    script.onload = () => {
      if (window.THREE) resolve(true)
      else fail()
    }
    script.onerror = fail
    document.head.appendChild(script)
  })
  return threeLoader
}

function webglAvailable(): boolean {
  try {
    const probe = document.createElement('canvas')
    const gl = probe.getContext('webgl') || probe.getContext('experimental-webgl')
    if (!gl) return false
    const lose = (gl as WebGLRenderingContext).getExtension('WEBGL_lose_context')
    lose?.loseContext()
    return true
  } catch {
    return false
  }
}

/** Deterministic PRNG so every visit opens on the same tape. */
function mulberry32(seed: number) {
  let a = seed | 0
  return () => {
    a = (a + 0x6d2b79f5) | 0
    let t = Math.imul(a ^ (a >>> 15), 1 | a)
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
}

/* ---------------------------------------------------------------- the tape */

interface Bar {
  o: number
  h: number
  l: number
  c: number
}

const BAR_MS = 1600

/** One more bar of the same random walk; drift is gentle so the tape reads calm. */
function nextBar(open: number, rand: () => number): Bar {
  const close = open + (rand() - 0.475) * 1.7
  return {
    o: open,
    c: close,
    h: Math.max(open, close) + rand() * 0.85,
    l: Math.min(open, close) - rand() * 0.85,
  }
}

/* -------------------------------------------------------------- grid floor */

const GRID_VERT = `
varying vec3 vWorld;
varying vec3 vView;
void main() {
  vec4 world = modelMatrix * vec4(position, 1.0);
  vWorld = world.xyz;
  vec4 mv = modelViewMatrix * vec4(position, 1.0);
  vView = mv.xyz;
  gl_Position = projectionMatrix * mv;
}
`

const GRID_FRAG = `
uniform vec3 uCol;
uniform float uScale;
uniform float uFade;
varying vec3 vWorld;
varying vec3 vView;
void main() {
  vec2 c = vWorld.xz * uScale;
  vec2 g = abs(fract(c) - 0.5) / max(fwidth(c), 1e-4);
  float line = 1.0 - min(min(g.x, g.y), 1.0);
  float fade = clamp(exp(-pow(length(vView) * uFade, 2.0)), 0.0, 1.0);
  gl_FragColor = vec4(uCol, line * 0.42 * fade);
}
`

/** Soft round sprite, generated at runtime — the page ships no image assets. */
function radialTexture(THREE: any, inner: string, mid: string): any {
  const size = 128
  const el = document.createElement('canvas')
  el.width = size
  el.height = size
  const ctx = el.getContext('2d')
  if (!ctx) return null
  const g = ctx.createRadialGradient(size / 2, size / 2, 0, size / 2, size / 2, size / 2)
  g.addColorStop(0, inner)
  g.addColorStop(0.42, mid)
  g.addColorStop(1, 'rgba(0,0,0,0)')
  ctx.fillStyle = g
  ctx.fillRect(0, 0, size, size)
  const tex = new THREE.CanvasTexture(el)
  tex.minFilter = THREE.LinearFilter
  tex.magFilter = THREE.LinearFilter
  return tex
}

/* -------------------------------------------------------------------- scene */

/**
 * 'hero' puts the tape right of the headline column; 'ambient' centres it and
 * dims it so foreground UI stays dominant.
 */
export type MarketVariant = 'hero' | 'ambient'

interface Framing {
  fov: number
  chart: [number, number, number]
  cam: [number, number, number]
  target: [number, number, number]
  glow: [number, number, number]
}

function framingFor(variant: MarketVariant, narrow: boolean): Framing {
  // 'ambient' keeps the tape low, small and far back so foreground UI dominates.
  if (variant === 'ambient') {
    return narrow
      ? { fov: 52, chart: [0, -3.4, 0], cam: [0, 0.6, 22], target: [0, -1.6, 0], glow: [0, -3, -16] }
      : { fov: 40, chart: [0, -3.2, 0], cam: [0, 0.8, 20], target: [0, -1.2, 0], glow: [0, -3, -16] }
  }
  return narrow
    ? { fov: 52, chart: [0, -2.0, 0], cam: [0, 1.0, 17], target: [0, -1.4, 0], glow: [0, -1.6, -14] }
    : { fov: 40, chart: [5.0, 0.3, 0], cam: [0, 2.2, 17], target: [0, 0.2, 0], glow: [5.0, 0, -14] }
}

interface SceneHandle {
  dispose: () => void
}

function buildScene(
  canvas: HTMLCanvasElement,
  variant: MarketVariant,
  reduceMotion: boolean,
  onLost: () => void,
): SceneHandle | null {
  const THREE = window.THREE
  if (!THREE) return null

  let renderer: any
  try {
    renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: true, powerPreference: 'high-performance' })
  } catch {
    return null
  }
  renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2))
  renderer.setClearColor(0x000000, 0)

  const dim = variant === 'ambient' ? 0.45 : 1
  const narrowAtBuild = canvas.clientWidth < 900
  // Instance counts are fixed at build; only the framing responds to resize.
  const N = variant === 'ambient' ? 44 : narrowAtBuild ? 22 : 26
  const SPACING = variant === 'ambient' ? 0.26 : 0.32
  const SPAN_Y = variant === 'ambient' ? 3.2 : 5.0

  const scene = new THREE.Scene()
  const camera = new THREE.PerspectiveCamera(40, 1, 0.1, 200)
  const camTarget = new THREE.Vector3()

  scene.add(new THREE.AmbientLight(0x2a3646, 0.42))
  const key = new THREE.DirectionalLight(0xffffff, 0.78)
  key.position.set(5, 11, 9)
  scene.add(key)
  const rimTeal = new THREE.PointLight(0x2bd4bd, 0.85, 46)
  rimTeal.position.set(-9, 4, 7)
  scene.add(rimTeal)
  const rimBlue = new THREE.PointLight(0x4f7dff, 0.42, 46)
  rimBlue.position.set(11, -3, 8)
  scene.add(rimBlue)

  // Horizon glow the tape is read against; drawn first, never depth-tested.
  const glowTex = radialTexture(THREE, 'rgba(86,132,255,0.40)', 'rgba(43,212,189,0.10)')
  const glowGeo = new THREE.PlaneGeometry(34, 20)
  const glowMat = new THREE.MeshBasicMaterial({
    map: glowTex,
    transparent: true,
    depthWrite: false,
    depthTest: false,
    blending: THREE.AdditiveBlending,
    opacity: 0.85 * dim,
  })
  const glow = new THREE.Mesh(glowGeo, glowMat)
  glow.renderOrder = -1
  scene.add(glow)

  const chart = new THREE.Group()
  scene.add(chart)

  // Candles: a lit instance, a larger additive instance behind it for the halo.
  const bodyW = variant === 'ambient' ? 0.16 : 0.2
  const bodyGeo = new THREE.BoxGeometry(bodyW, 1, bodyW)
  const haloGeo = new THREE.BoxGeometry(bodyW * 1.6, 1, bodyW * 1.6)
  const wickGeo = new THREE.BoxGeometry(0.035, 1, 0.035)
  const bodyMat = new THREE.MeshStandardMaterial({ roughness: 0.45, metalness: 0.06 })
  const haloMat = new THREE.MeshBasicMaterial({
    transparent: true,
    opacity: 0.09 * dim,
    depthWrite: false,
    blending: THREE.AdditiveBlending,
  })
  const wickMat = new THREE.MeshStandardMaterial({ roughness: 0.5, metalness: 0.1, transparent: true, opacity: 0.85 })

  const bodies = new THREE.InstancedMesh(bodyGeo, bodyMat, N)
  const halos = new THREE.InstancedMesh(haloGeo, haloMat, N)
  const wicks = new THREE.InstancedMesh(wickGeo, wickMat, N)
  bodies.instanceMatrix.setUsage(THREE.DynamicDrawUsage)
  halos.instanceMatrix.setUsage(THREE.DynamicDrawUsage)
  wicks.instanceMatrix.setUsage(THREE.DynamicDrawUsage)
  // Instanced meshes are updated every frame; a stale bounding sphere from the
  // initial identity matrices would cull the whole tape at some camera angles.
  bodies.frustumCulled = false
  halos.frustumCulled = false
  wicks.frustumCulled = false
  chart.add(halos, bodies, wicks)

  // Close ribbon: a thin strip through the closes, two vertices per bar.
  const ribbonPos = new Float32Array(N * 2 * 3)
  const ribbonIdx: number[] = []
  for (let i = 0; i < N - 1; i++) {
    const a = i * 2
    ribbonIdx.push(a, a + 1, a + 2, a + 1, a + 3, a + 2)
  }
  const ribbonGeo = new THREE.BufferGeometry()
  ribbonGeo.setAttribute('position', new THREE.BufferAttribute(ribbonPos, 3))
  ribbonGeo.setIndex(ribbonIdx)
  const ribbonMat = new THREE.MeshBasicMaterial({
    color: 0x6ea8ff,
    transparent: true,
    opacity: 0.3 * dim,
    depthWrite: false,
    blending: THREE.AdditiveBlending,
    side: THREE.DoubleSide,
  })
  const ribbon = new THREE.Mesh(ribbonGeo, ribbonMat)
  ribbon.frustumCulled = false
  chart.add(ribbon)

  // Dashed last-price line, the length of the tape.
  const halfW = ((N - 1) * SPACING) / 2
  const priceGeo = new THREE.BufferGeometry().setFromPoints([
    new THREE.Vector3(-halfW - 0.6, 0, 0),
    new THREE.Vector3(halfW + 1.1, 0, 0),
  ])
  const priceMat = new THREE.LineDashedMaterial({
    color: 0x2bd4bd,
    dashSize: 0.34,
    gapSize: 0.26,
    transparent: true,
    opacity: 0.5 * dim,
  })
  const priceLine = new THREE.Line(priceGeo, priceMat)
  priceLine.computeLineDistances()
  priceLine.frustumCulled = false
  chart.add(priceLine)

  // Price tag: a canvas texture redrawn only when the printed value changes.
  const tagCanvas = document.createElement('canvas')
  tagCanvas.width = 256
  tagCanvas.height = 84
  const tagCtx = tagCanvas.getContext('2d')
  const tagTex = new THREE.CanvasTexture(tagCanvas)
  tagTex.minFilter = THREE.LinearFilter
  const tagMat = new THREE.SpriteMaterial({ map: tagTex, transparent: true, opacity: 0.95 * dim, depthTest: false })
  const tag = new THREE.Sprite(tagMat)
  tag.scale.set(1.6, 0.53, 1)
  // On the login page the tag would compete with the form; the tape alone is enough.
  tag.visible = variant !== 'ambient'
  tag.renderOrder = 3
  chart.add(tag)
  let tagPrinted = ''
  function drawTag(text: string) {
    if (!tagCtx || text === tagPrinted) return
    tagPrinted = text
    const w = tagCanvas.width
    const h = tagCanvas.height
    tagCtx.clearRect(0, 0, w, h)
    tagCtx.fillStyle = 'rgba(10,32,34,0.92)'
    tagCtx.strokeStyle = 'rgba(43,212,189,0.85)'
    tagCtx.lineWidth = 3
    const r = 14
    tagCtx.beginPath()
    tagCtx.moveTo(r + 2, 4)
    tagCtx.arcTo(w - 2, 4, w - 2, h - 4, r)
    tagCtx.arcTo(w - 2, h - 4, 2, h - 4, r)
    tagCtx.arcTo(2, h - 4, 2, 4, r)
    tagCtx.arcTo(2, 4, w - 2, 4, r)
    tagCtx.closePath()
    tagCtx.fill()
    tagCtx.stroke()
    tagCtx.fillStyle = '#7ff0dd'
    tagCtx.font = '600 44px ui-monospace, SFMono-Regular, Menlo, monospace'
    tagCtx.textAlign = 'center'
    tagCtx.textBaseline = 'middle'
    tagCtx.fillText(text, w / 2, h / 2 + 2)
    tagTex.needsUpdate = true
  }

  // Grid floor under the tape.
  const gridGeo = new THREE.PlaneGeometry(90, 60)
  const gridUniforms = {
    uCol: { value: new THREE.Color(0x3d6ea8) },
    uScale: { value: 0.8 },
    uFade: { value: 0.05 },
  }
  const gridMat = new THREE.ShaderMaterial({
    uniforms: gridUniforms,
    vertexShader: GRID_VERT,
    fragmentShader: GRID_FRAG,
    transparent: true,
    depthWrite: false,
    extensions: { derivatives: true },
  })
  const grid = new THREE.Mesh(gridGeo, gridMat)
  grid.rotation.x = -Math.PI / 2
  grid.position.y = -SPAN_Y / 2 - 1.6
  scene.add(grid)

  // Tick sparks drifting behind the tape.
  const P = 260
  const sparkPos = new Float32Array(P * 3)
  const rand = mulberry32(20260906)
  for (let i = 0; i < P; i++) {
    sparkPos[i * 3] = (rand() - 0.5) * 60
    sparkPos[i * 3 + 1] = (rand() - 0.4) * 24
    sparkPos[i * 3 + 2] = -6 - rand() * 34
  }
  const sparkBase = sparkPos.slice()
  const sparkGeo = new THREE.BufferGeometry()
  sparkGeo.setAttribute('position', new THREE.BufferAttribute(sparkPos, 3))
  const sparkTex = radialTexture(THREE, 'rgba(190,220,255,0.95)', 'rgba(79,125,255,0.35)')
  const sparkMat = new THREE.PointsMaterial({
    map: sparkTex,
    color: 0x8fb4ff,
    size: 0.3,
    transparent: true,
    opacity: 0.5 * dim,
    depthWrite: false,
    blending: THREE.AdditiveBlending,
    sizeAttenuation: true,
  })
  const sparks = new THREE.Points(sparkGeo, sparkMat)
  scene.add(sparks)

  /* ------------------------------------------------------------- tape state */

  const bars: Bar[] = []
  let last = 100
  for (let i = 0; i < N; i++) {
    const b = nextBar(last, rand)
    bars.push(b)
    last = b.c
  }
  // The rightmost bar is still forming; `pending` is what it will settle to.
  let pending = nextBar(last, rand)
  let barStart = performance.now()
  let loSmooth = 0
  let hiSmooth = 0
  let scaleReady = false

  const cUp = new THREE.Color(0x35d99a)
  const cDown = new THREE.Color(0xff5b55)
  const cWickUp = new THREE.Color(0x1d7a5a)
  const cWickDown = new THREE.Color(0x9c3b38)
  const dummy = new THREE.Object3D()

  function updateTape(now: number) {
    let progress = (now - barStart) / BAR_MS
    while (progress >= 1) {
      // Settle the forming bar, roll the window, start the next one.
      bars[N - 1] = pending
      bars.shift()
      const open = pending.c
      bars.push({ o: open, c: open, h: open, l: open })
      pending = nextBar(open, rand)
      barStart += BAR_MS
      progress = (now - barStart) / BAR_MS
    }

    // The forming bar walks from its open toward the pending close.
    const forming = bars[N - 1]
    const wobble = Math.sin(now * 0.006) * 0.05
    forming.c = pending.o + (pending.c - pending.o) * progress + wobble
    forming.h = Math.max(forming.o, forming.c, pending.o + (pending.h - pending.o) * progress)
    forming.l = Math.min(forming.o, forming.c, pending.o + (pending.l - pending.o) * progress)

    // Auto-fit the vertical scale the way a chart does, but eased.
    let lo = Infinity
    let hi = -Infinity
    for (let i = 0; i < N; i++) {
      if (bars[i].l < lo) lo = bars[i].l
      if (bars[i].h > hi) hi = bars[i].h
    }
    if (!scaleReady) {
      loSmooth = lo
      hiSmooth = hi
      scaleReady = true
    } else {
      loSmooth += (lo - loSmooth) * 0.09
      hiSmooth += (hi - hiSmooth) * 0.09
    }
    const mid = (loSmooth + hiSmooth) / 2
    const k = SPAN_Y / Math.max(hiSmooth - loSmooth, 0.5)
    const yOf = (p: number) => (p - mid) * k

    for (let i = 0; i < N; i++) {
      const b = bars[i]
      const x = (i - (N - 1) / 2) * SPACING
      const up = b.c >= b.o
      const top = yOf(Math.max(b.o, b.c))
      const bot = yOf(Math.min(b.o, b.c))
      const height = Math.max(top - bot, 0.06)

      dummy.position.set(x, (top + bot) / 2, 0)
      dummy.scale.set(1, height, 1)
      dummy.updateMatrix()
      bodies.setMatrixAt(i, dummy.matrix)
      halos.setMatrixAt(i, dummy.matrix)
      bodies.setColorAt(i, up ? cUp : cDown)
      halos.setColorAt(i, up ? cUp : cDown)

      const wt = yOf(b.h)
      const wb = yOf(b.l)
      dummy.position.set(x, (wt + wb) / 2, 0)
      dummy.scale.set(1, Math.max(wt - wb, 0.08), 1)
      dummy.updateMatrix()
      wicks.setMatrixAt(i, dummy.matrix)
      wicks.setColorAt(i, up ? cWickUp : cWickDown)

      const y = yOf(b.c)
      const v = i * 6
      ribbonPos[v] = x
      ribbonPos[v + 1] = y + 0.03
      ribbonPos[v + 2] = 0.26
      ribbonPos[v + 3] = x
      ribbonPos[v + 4] = y - 0.03
      ribbonPos[v + 5] = 0.26
    }

    bodies.instanceMatrix.needsUpdate = true
    halos.instanceMatrix.needsUpdate = true
    wicks.instanceMatrix.needsUpdate = true
    if (bodies.instanceColor) bodies.instanceColor.needsUpdate = true
    if (halos.instanceColor) halos.instanceColor.needsUpdate = true
    if (wicks.instanceColor) wicks.instanceColor.needsUpdate = true
    ribbonGeo.attributes.position.needsUpdate = true

    const lastY = yOf(forming.c)
    priceLine.position.y = lastY
    tag.position.set(halfW - 0.85, lastY + 0.85, 0.4)
    drawTag((57000 + forming.c * 12).toFixed(2))

    // The tape slides left by one slot across the life of a bar.
    return progress
  }

  /* ---------------------------------------------------------------- runtime */

  const mouse = { x: 0, y: 0 }
  const pointer = { x: 0, y: 0 }
  const base = { x: 0, y: 2.4, z: 17 }
  const chartHome = { x: 0, y: 0, z: 0 }
  let rafId = 0
  let disposed = false
  // False while the canvas is scrolled out of view; the loop parks until it returns.
  let visible = true

  function layout() {
    const w = canvas.clientWidth
    const h = canvas.clientHeight
    if (!w || !h) return
    const narrow = w < 900
    const f = framingFor(variant, narrow)
    renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, narrow ? 1.75 : 2))
    renderer.setSize(w, h, false)
    camera.aspect = w / h
    camera.fov = f.fov
    camera.updateProjectionMatrix()
    chartHome.x = f.chart[0]
    chartHome.y = f.chart[1]
    chartHome.z = f.chart[2]
    chart.rotation.y = variant === 'hero' && !narrow ? -0.26 : -0.14
    chart.rotation.x = 0.04
    base.x = f.cam[0]
    base.y = f.cam[1]
    base.z = f.cam[2]
    camTarget.set(f.target[0], f.target[1], f.target[2])
    glow.position.set(f.glow[0], f.glow[1], f.glow[2])
    grid.position.set(f.chart[0], f.chart[1] - SPAN_Y / 2 - 1.6, 0)
  }

  function renderOnce() {
    camera.position.set(base.x + mouse.x * 1.5, base.y - mouse.y * 1.0, base.z)
    camera.lookAt(camTarget)
    renderer.render(scene, camera)
  }

  function frame(now: number) {
    rafId = 0
    if (disposed || document.hidden || !visible) return

    mouse.x += (pointer.x - mouse.x) * 0.045
    mouse.y += (pointer.y - mouse.y) * 0.045

    const progress = updateTape(now)
    chart.position.set(chartHome.x - progress * SPACING, chartHome.y, chartHome.z)

    const t = now / 1000
    const arr = sparkGeo.attributes.position.array as Float32Array
    for (let i = 0; i < P; i++) {
      arr[i * 3 + 1] = sparkBase[i * 3 + 1] + Math.sin(t * 0.3 + sparkBase[i * 3] * 0.2) * 0.6
    }
    sparkGeo.attributes.position.needsUpdate = true

    renderOnce()
    schedule()
  }

  function schedule() {
    if (disposed || reduceMotion || rafId) return
    rafId = requestAnimationFrame(frame)
  }

  const onResize = () => {
    layout()
    if (reduceMotion) renderOnce()
  }
  const onPointer = (e: PointerEvent) => {
    pointer.x = e.clientX / window.innerWidth - 0.5
    pointer.y = e.clientY / window.innerHeight - 0.5
  }
  const onVisibility = () => {
    if (!document.hidden) {
      // Skip the bars that "formed" while the tab was hidden instead of
      // fast-forwarding through them on the next frame.
      barStart = performance.now()
      schedule()
    }
  }
  // Park the loop while the canvas is scrolled off-screen (rAF is not throttled
  // for off-screen elements in a visible tab).
  const observer =
    'IntersectionObserver' in window
      ? new IntersectionObserver((entries) => {
          const entry = entries[entries.length - 1]
          if (!entry) return
          visible = entry.isIntersecting
          if (visible) {
            barStart = performance.now()
            schedule()
          }
        })
      : null
  // The context is not restored (no preventDefault): tear everything down and
  // let the page fall back to the CSS gradient instead of animating a dead canvas.
  const onContextLost = () => {
    dispose()
    onLost()
  }

  function dispose() {
    if (disposed) return
    disposed = true
    if (rafId) cancelAnimationFrame(rafId)
    rafId = 0
    observer?.disconnect()
    window.removeEventListener('resize', onResize)
    document.removeEventListener('visibilitychange', onVisibility)
    canvas.removeEventListener('webglcontextlost', onContextLost)
    window.removeEventListener('pointermove', onPointer)
    bodyGeo.dispose(); haloGeo.dispose(); wickGeo.dispose()
    ribbonGeo.dispose(); priceGeo.dispose(); gridGeo.dispose(); glowGeo.dispose(); sparkGeo.dispose()
    bodyMat.dispose(); haloMat.dispose(); wickMat.dispose()
    ribbonMat.dispose(); priceMat.dispose(); gridMat.dispose(); glowMat.dispose(); sparkMat.dispose(); tagMat.dispose()
    glowTex?.dispose(); sparkTex?.dispose(); tagTex.dispose()
    bodies.dispose(); halos.dispose(); wicks.dispose()
    renderer.dispose()
  }

  window.addEventListener('resize', onResize, { passive: true })
  document.addEventListener('visibilitychange', onVisibility)
  canvas.addEventListener('webglcontextlost', onContextLost)
  if (!reduceMotion) window.addEventListener('pointermove', onPointer, { passive: true })
  observer?.observe(canvas)

  layout()
  if (reduceMotion) {
    // One composed still: a settled tape, mid-bar.
    updateTape(barStart + BAR_MS * 0.6)
    chart.position.set(chartHome.x - 0.6 * SPACING, chartHome.y, chartHome.z)
    renderOnce()
  } else {
    schedule()
  }

  return { dispose }
}

function useMarketScene(
  canvasRef: RefObject<HTMLCanvasElement | null>,
  fallbackRef: RefObject<HTMLDivElement | null>,
  variant: MarketVariant,
) {
  useEffect(() => {
    const canvas = canvasRef.current
    const fallback = fallbackRef.current
    if (!canvas || !fallback) return

    let handle: SceneHandle | null = null
    let cancelled = false

    const showFallback = () => {
      canvas.style.display = 'none'
      fallback.style.display = ''
    }

    if (!webglAvailable()) {
      showFallback()
      return
    }

    void loadThree().then((ok) => {
      if (cancelled) return
      if (!ok) {
        showFallback()
        return
      }
      handle = buildScene(canvas, variant, prefersReducedMotion(), () => {
        // Context lost: the scene has already torn itself down; drop the handle.
        handle = null
        showFallback()
      })
      if (!handle) {
        showFallback()
        return
      }
      fallback.style.display = 'none'
    })

    return () => {
      cancelled = true
      handle?.dispose()
      handle = null
    }
  }, [canvasRef, fallbackRef, variant])
}

/**
 * Renders the fallback gradient and the canvas as siblings; the caller supplies
 * the class names so each page can position and style them itself.
 */
export function MarketCanvas({
  variant,
  canvasClass,
  fallbackClass,
}: {
  variant: MarketVariant
  canvasClass: string
  fallbackClass: string
}) {
  const canvasRef = useRef<HTMLCanvasElement | null>(null)
  const fallbackRef = useRef<HTMLDivElement | null>(null)
  useMarketScene(canvasRef, fallbackRef, variant)
  return (
    <>
      <div className={fallbackClass} ref={fallbackRef} aria-hidden="true" />
      <canvas className={canvasClass} ref={canvasRef} aria-hidden="true" />
    </>
  )
}
