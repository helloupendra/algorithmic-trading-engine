/**
 * Public homepage ("/"). Marketing surface for the console: a Three.js hero
 * (candlestick ribbon + tick field), the four modules as 3D tilt cards, a
 * position-based live sample, an honest backtest sample, and the principles.
 *
 * Three.js is loaded on demand from cdnjs (UMD r128) so the console bundle
 * does not carry it; when the script or WebGL is unavailable the hero keeps a
 * CSS gradient. Sample numbers are real figures from the console (see the
 * strategies/backtesting module docs), not decoration.
 */

import {
  useEffect,
  useRef,
  type CSSProperties,
  type PointerEvent as ReactPointerEvent,
  type ReactNode,
  type RefObject,
} from 'react'
import { Link } from 'react-router-dom'
import { useAuth } from '../lib/auth'
import './landing.css'

const THREE_CDN = 'https://cdnjs.cloudflare.com/ajax/libs/three.js/r128/three.min.js'

declare global {
  interface Window {
    // Loaded at runtime from the CDN; typed loosely on purpose (no bundled dependency).
    THREE?: any
  }
}

let threeLoader: Promise<boolean> | null = null

/** Injects the Three.js script once; resolves false when it cannot load. */
function loadThree(): Promise<boolean> {
  if (window.THREE) return Promise.resolve(true)
  if (threeLoader) return threeLoader
  threeLoader = new Promise<boolean>((resolve) => {
    const script = document.createElement('script')
    script.src = THREE_CDN
    script.async = true
    script.crossOrigin = 'anonymous'
    script.onload = () => resolve(!!window.THREE)
    script.onerror = () => resolve(false)
    document.head.appendChild(script)
  })
  return threeLoader
}

function prefersReducedMotion(): boolean {
  return typeof window !== 'undefined' && !!window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches
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

/** Deterministic PRNG so every visit renders the same ribbon. */
function mulberry32(seed: number) {
  let a = seed | 0
  return () => {
    a = (a + 0x6d2b79f5) | 0
    let t = Math.imul(a ^ (a >>> 15), 1 | a)
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
}

/* ------------------------------------------------------------- hero scene */

interface SceneHandle {
  dispose: () => void
}

function buildScene(canvas: HTMLCanvasElement, reduceMotion: boolean, onLost: () => void): SceneHandle | null {
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

  const scene = new THREE.Scene()
  scene.fog = new THREE.FogExp2(0x070b11, 0.04)

  const camera = new THREE.PerspectiveCamera(50, 1, 0.1, 100)
  camera.position.set(3.2, 1.6, 15)
  const camTarget = new THREE.Vector3(2.2, 0.4, 0)

  scene.add(new THREE.AmbientLight(0x4f7dff, 0.55))
  const key = new THREE.DirectionalLight(0xffffff, 0.9)
  key.position.set(6, 10, 8)
  scene.add(key)
  const rim = new THREE.PointLight(0x2bd4bd, 1.4, 40)
  rim.position.set(-8, 4, 6)
  scene.add(rim)

  const rand = mulberry32(57600)

  // Candlestick ribbon: a seeded random walk, coloured like the console.
  const group = new THREE.Group()
  const COUNT = 120
  const SPACING = 0.24
  const SCALE = 0.85
  const bodyGeo = new THREE.BoxGeometry(0.13, 1, 0.13)
  const wickGeo = new THREE.BoxGeometry(0.028, 1, 0.028)
  const matUp = new THREE.MeshStandardMaterial({ color: 0x31c48d, emissive: 0x0f6b4a, emissiveIntensity: 0.55, roughness: 0.35, metalness: 0.25 })
  const matDown = new THREE.MeshStandardMaterial({ color: 0xf4635e, emissive: 0x7a2420, emissiveIntensity: 0.5, roughness: 0.35, metalness: 0.25 })
  const matWick = new THREE.MeshStandardMaterial({ color: 0x91a0b4, roughness: 0.6, metalness: 0.1, transparent: true, opacity: 0.8 })

  let price = 0
  const candles: { o: number; c: number; h: number; l: number }[] = []
  for (let i = 0; i < COUNT; i++) {
    const drift = Math.sin(i / 17) * 0.08
    const open = price
    const close = open + (rand() - 0.47) * 0.9 + drift
    candles.push({ o: open, c: close, h: Math.max(open, close) + rand() * 0.45, l: Math.min(open, close) - rand() * 0.45 })
    price = close
  }
  const mean = candles.reduce((s, k) => s + k.c, 0) / COUNT
  const bodies: { mesh: any; base: number; idx: number }[] = []
  candles.forEach((k, idx) => {
    const x = (idx - COUNT / 2) * SPACING
    const up = k.c >= k.o
    const body = new THREE.Mesh(bodyGeo, up ? matUp : matDown)
    const mid = ((k.o + k.c) / 2 - mean) * SCALE
    body.position.set(x, mid, 0)
    body.scale.y = Math.max(Math.abs(k.c - k.o), 0.06) * SCALE
    const wick = new THREE.Mesh(wickGeo, matWick)
    wick.position.set(x, ((k.h + k.l) / 2 - mean) * SCALE, 0)
    wick.scale.y = Math.max(k.h - k.l, 0.08) * SCALE
    group.add(wick)
    group.add(body)
    bodies.push({ mesh: body, base: mid, idx })
  })

  const planeGeo = new THREE.PlaneGeometry(COUNT * SPACING + 4, 7, 40, 14)
  const planeMat = new THREE.MeshBasicMaterial({ color: 0x4f7dff, wireframe: true, transparent: true, opacity: 0.07 })
  const plane = new THREE.Mesh(planeGeo, planeMat)
  plane.position.set(0, 0, -1.2)
  group.add(plane)
  group.rotation.set(-0.28, 0.42, 0.04)
  group.position.set(2.6, 0.2, 0)
  scene.add(group)

  // Tick field: additive points drifting in a slow wave behind the ribbon.
  const P = 2600
  const positions = new Float32Array(P * 3)
  const colors = new Float32Array(P * 3)
  const cBrand = new THREE.Color(0x4f7dff)
  const cLive = new THREE.Color(0x2bd4bd)
  for (let p = 0; p < P; p++) {
    positions[p * 3] = (rand() - 0.5) * 64
    positions[p * 3 + 1] = (rand() - 0.5) * 30
    positions[p * 3 + 2] = -5 - rand() * 14
    const c = rand() < 0.35 ? cLive : cBrand
    colors[p * 3] = c.r
    colors[p * 3 + 1] = c.g
    colors[p * 3 + 2] = c.b
  }
  const basePositions = positions.slice()
  const ptsGeo = new THREE.BufferGeometry()
  ptsGeo.setAttribute('position', new THREE.BufferAttribute(positions, 3))
  ptsGeo.setAttribute('color', new THREE.BufferAttribute(colors, 3))
  const ptsMat = new THREE.PointsMaterial({ size: 0.12, vertexColors: true, transparent: true, opacity: 0.85, depthWrite: false, blending: THREE.AdditiveBlending, sizeAttenuation: true })
  const points = new THREE.Points(ptsGeo, ptsMat)
  scene.add(points)

  const mouse = { x: 0, y: 0 }
  const target = { x: 0, y: 0 }
  let rafId = 0
  let disposed = false
  const start = performance.now()

  const restingY = () => (canvas.clientWidth < 900 ? -1.6 : 0.2)

  function resize() {
    const w = canvas.clientWidth
    const h = canvas.clientHeight
    if (!w || !h) return
    renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2))
    renderer.setSize(w, h, false)
    camera.aspect = w / h
    camera.fov = w < 760 ? 62 : 50
    camera.updateProjectionMatrix()
    group.position.x = w < 900 ? 0.6 : 2.6
    group.position.y = restingY()
  }

  function renderOnce() {
    camera.lookAt(camTarget)
    renderer.render(scene, camera)
  }

  function frame(now: number) {
    rafId = 0
    if (disposed || document.hidden) return
    const t = (now - start) / 1000

    mouse.x += (target.x - mouse.x) * 0.05
    mouse.y += (target.y - mouse.y) * 0.05

    group.rotation.y = 0.42 + Math.sin(t * 0.18) * 0.08 + mouse.x * 0.25
    group.rotation.x = -0.28 + mouse.y * 0.12
    group.position.y += (restingY() + Math.sin(t * 0.5) * 0.06 - group.position.y) * 0.05

    for (let b = 0; b < bodies.length; b++) {
      const item = bodies[b]
      item.mesh.position.y = item.base + Math.sin(t * 0.9 + item.idx * 0.22) * 0.05
    }

    const arr = ptsGeo.attributes.position.array as Float32Array
    for (let q = 0; q < P; q++) {
      const bx = basePositions[q * 3]
      const by = basePositions[q * 3 + 1]
      arr[q * 3 + 1] = by + Math.sin(t * 0.35 + bx * 0.35) * 0.35
      arr[q * 3] = bx + Math.cos(t * 0.12 + by * 0.2) * 0.15
    }
    ptsGeo.attributes.position.needsUpdate = true
    points.rotation.z = Math.sin(t * 0.05) * 0.02

    camera.position.x = 3.2 + mouse.x * 0.8
    camera.position.y = 1.6 - mouse.y * 0.6
    renderOnce()
    schedule()
  }

  function schedule() {
    if (disposed || reduceMotion || rafId) return
    rafId = requestAnimationFrame(frame)
  }

  const onResize = () => {
    resize()
    if (reduceMotion) renderOnce()
  }
  const onPointer = (e: PointerEvent) => {
    target.x = e.clientX / window.innerWidth - 0.5
    target.y = e.clientY / window.innerHeight - 0.5
  }
  const onVisibility = () => {
    if (!document.hidden) schedule()
  }
  const onContextLost = (e: Event) => {
    e.preventDefault()
    onLost()
  }

  window.addEventListener('resize', onResize, { passive: true })
  document.addEventListener('visibilitychange', onVisibility)
  canvas.addEventListener('webglcontextlost', onContextLost)
  if (!reduceMotion) window.addEventListener('pointermove', onPointer, { passive: true })

  resize()
  if (reduceMotion) renderOnce()
  else schedule()

  return {
    dispose() {
      disposed = true
      if (rafId) cancelAnimationFrame(rafId)
      window.removeEventListener('resize', onResize)
      document.removeEventListener('visibilitychange', onVisibility)
      canvas.removeEventListener('webglcontextlost', onContextLost)
      window.removeEventListener('pointermove', onPointer)
      bodyGeo.dispose(); wickGeo.dispose(); planeGeo.dispose(); ptsGeo.dispose()
      matUp.dispose(); matDown.dispose(); matWick.dispose(); planeMat.dispose(); ptsMat.dispose()
      renderer.dispose()
    },
  }
}

function useHeroScene(canvasRef: RefObject<HTMLCanvasElement | null>, fallbackRef: RefObject<HTMLDivElement | null>) {
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
      handle = buildScene(canvas, prefersReducedMotion(), showFallback)
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
  }, [canvasRef, fallbackRef])
}

/* ------------------------------------------------------- small behaviours */

function useReveal(rootRef: RefObject<HTMLDivElement | null>) {
  useEffect(() => {
    const root = rootRef.current
    if (!root) return
    const els = Array.from(root.querySelectorAll<HTMLElement>('.reveal'))
    if (!('IntersectionObserver' in window) || prefersReducedMotion()) {
      els.forEach((el) => el.classList.add('is-in'))
      return
    }
    const io = new IntersectionObserver(
      (entries) => {
        entries.forEach((e) => {
          if (e.isIntersecting) {
            e.target.classList.add('is-in')
            io.unobserve(e.target)
          }
        })
      },
      { rootMargin: '0px 0px -8% 0px', threshold: 0.08 },
    )
    els.forEach((el) => io.observe(el))
    return () => io.disconnect()
  }, [rootRef])
}

/** Pointer-driven 3D tilt for a card; rAF-throttled, reset on leave. */
function useTilt() {
  const raf = useRef(0)
  const enabled = useRef(false)
  useEffect(() => {
    enabled.current = !prefersReducedMotion() && window.matchMedia('(hover: hover)').matches
  }, [])

  const onPointerMove = (ev: ReactPointerEvent<HTMLElement>) => {
    if (!enabled.current) return
    const card = ev.currentTarget
    const r = card.getBoundingClientRect()
    const px = (ev.clientX - r.left) / r.width
    const py = (ev.clientY - r.top) / r.height
    if (raf.current) cancelAnimationFrame(raf.current)
    raf.current = requestAnimationFrame(() => {
      raf.current = 0
      const rx = (0.5 - py) * 10
      const ry = (px - 0.5) * 12
      card.style.transform = `rotateX(${rx.toFixed(2)}deg) rotateY(${ry.toFixed(2)}deg) translateZ(6px)`
      card.style.setProperty('--mx', `${(px * 100).toFixed(1)}%`)
      card.style.setProperty('--my', `${(py * 100).toFixed(1)}%`)
    })
  }
  const onPointerLeave = (ev: ReactPointerEvent<HTMLElement>) => {
    if (raf.current) {
      cancelAnimationFrame(raf.current)
      raf.current = 0
    }
    ev.currentTarget.style.transform = ''
  }
  return { onPointerMove, onPointerLeave }
}

function TiltCard({ children, delay }: { children: ReactNode; delay?: number }) {
  const tilt = useTilt()
  return (
    <div className="reveal" style={delay ? ({ '--d': `${delay}s` } as CSSProperties) : undefined}>
      <article className="tilt" onPointerMove={tilt.onPointerMove} onPointerLeave={tilt.onPointerLeave}>
        {children}
      </article>
    </div>
  )
}

const Mark = () => (
  <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
    <path d="M3 17l5-8 4 5 3-4 6 7" />
  </svg>
)

/* ---------------------------------------------------------------- page */

export function LandingPage() {
  const { isAuthenticated, isAdmin } = useAuth()
  const consoleHref = isAuthenticated ? (isAdmin ? '/admin' : '/trader') : '/login'
  const consoleLabel = isAuthenticated ? 'Go to console' : 'Open the console'

  const rootRef = useRef<HTMLDivElement | null>(null)
  const canvasRef = useRef<HTMLCanvasElement | null>(null)
  const fallbackRef = useRef<HTMLDivElement | null>(null)
  useHeroScene(canvasRef, fallbackRef)
  useReveal(rootRef)

  useEffect(() => {
    const previous = document.title
    document.title = 'AlgoTrading — see every trade your strategy makes'
    return () => {
      document.title = previous
    }
  }, [])

  return (
    <div className="lp" ref={rootRef}>
      <header className="nav">
        <div className="wrap">
          <a className="brand" href="#top" aria-label="AlgoTrading home">
            <span className="brand__mark"><Mark /></span>
            <span>AlgoTrading<small>Console</small></span>
          </a>
          <nav className="nav__links" aria-label="Sections">
            <a href="#modules">Modules</a>
            <a href="#live">Live view</a>
            <a href="#backtest">Backtesting</a>
            <a href="#how">How it works</a>
            <a href="#principles">Principles</a>
            <Link className="btn btn--primary btn--sm" to={consoleHref}>{isAuthenticated ? 'Console' : 'Open console'}</Link>
          </nav>
        </div>
      </header>

      <main id="top">
        {/* ------------------------------------------------------- hero */}
        <section className="hero" aria-labelledby="hero-title">
          <div className="hero__fallback" ref={fallbackRef} aria-hidden="true" />
          <canvas className="hero__scene" ref={canvasRef} aria-hidden="true" />
          <div className="hero__veil" aria-hidden="true" />

          <div className="wrap hero__grid">
            <div>
              <span className="eyebrow"><span className="dot" aria-hidden="true" /> Open-source algo trading for Indian F&amp;O</span>
              <h1 id="hero-title">See <em>every trade</em> your strategy makes.</h1>
              <p className="lead">
                Live paper runs on real ticks, coverage-first backtests, and a <b>position-based view</b> of
                everything — contract, lots, lot size, entry premium, LTP and P&amp;L — for NIFTY, BANKNIFTY and
                200+ F&amp;O underlyings. Self-hosted, on your own broker keys.
              </p>
              <div className="cta">
                <Link className="btn btn--primary" to={consoleHref}>{consoleLabel}</Link>
                <a className="btn" href="#modules">Explore the modules</a>
                <a className="btn btn--ghost" href="#how">How a run works →</a>
              </div>

              <div className="hero__stats" aria-label="Platform facts">
                <div className="stat"><div className="stat__v">15</div><div className="stat__l">Strategies</div></div>
                <div className="stat"><div className="stat__v">200+</div><div className="stat__l">F&amp;O underlyings</div></div>
                <div className="stat"><div className="stat__v">3 s</div><div className="stat__l">Risk guard interval</div></div>
                <div className="stat"><div className="stat__v live">1 m</div><div className="stat__l">Live bar resolution</div></div>
              </div>
            </div>
          </div>

          <div className="scroll-hint" aria-hidden="true">scroll<span /></div>
        </section>

        {/* ---------------------------------------------------- modules */}
        <section id="modules" aria-labelledby="modules-title">
          <div className="wrap">
            <div className="section__head reveal">
              <p className="kicker">Four modules, one pipeline</p>
              <h2 id="modules-title">Data in. Decisions out. Nothing in between hidden.</h2>
              <p>Every module is built on the same rule: show what exists before asking you to pick anything.</p>
            </div>

            <div className="pipeline">
              <TiltCard>
                <span className="tag">Ready</span>
                <div className="tilt__n">01 / DATA</div>
                <div className="tilt__icon" aria-hidden="true"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round"><ellipse cx="12" cy="5" rx="8" ry="3" /><path d="M4 5v6c0 1.7 3.6 3 8 3s8-1.3 8-3V5" /><path d="M4 11v6c0 1.7 3.6 3 8 3s8-1.3 8-3v-6" /></svg></div>
                <h3>Live feeds &amp; history</h3>
                <p>Broker websocket → watchlist → ticks, 1m bars and quotes, with an honest heartbeat.</p>
                <ul>
                  <li>Index tickers that move the moment the feed runs</li>
                  <li>Coverage matrix: symbol × resolution × range × bars</li>
                  <li>FYERS backfill for index and option chains</li>
                </ul>
              </TiltCard>

              <TiltCard delay={0.08}>
                <span className="tag">Ready</span>
                <div className="tilt__n">02 / STRATEGIES</div>
                <div className="tilt__icon" aria-hidden="true"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round"><rect x="4" y="8" width="16" height="12" rx="3" /><path d="M12 8V4M8 4h8M9 14h.01M15 14h.01" /></svg></div>
                <h3>Catalog &amp; Live runner</h3>
                <p>Pick a strategy, pick an underlying from the real F&amp;O inventory, set lots and optional ₹ SL/target. Start.</p>
                <ul>
                  <li>Descriptions, categories and supported underlyings from the code itself</li>
                  <li>API-side risk guard squares off on stop-loss or target</li>
                  <li>One stop pipeline: UI, guard, market close, runner exit</li>
                </ul>
              </TiltCard>

              <TiltCard delay={0.16}>
                <span className="tag">Ready</span>
                <div className="tilt__n">03 / BACKTESTING</div>
                <div className="tilt__icon" aria-hidden="true"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round"><path d="M9 3h6M10 3v6l-5 9a2 2 0 0 0 1.8 3h10.4a2 2 0 0 0 1.8-3l-5-9V3" /><path d="M7.5 15h9" /></svg></div>
                <h3>Replay any strategy</h3>
                <p>The same <span className="mono">on_bar</span> contract as live, replayed bar by bar over stored candles.</p>
                <ul>
                  <li>Resolution and date range bounded by what is actually stored</li>
                  <li>Fills at option candle close, EOD square-off, ₹ SL/target</li>
                  <li>Skipped entries are listed, never dropped</li>
                </ul>
              </TiltCard>

              <TiltCard delay={0.24}>
                <span className="tag">Ready</span>
                <div className="tilt__n">04 / RISK &amp; ALERTS</div>
                <div className="tilt__icon" aria-hidden="true"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round"><path d="M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6l8-3z" /><path d="M9 12l2 2 4-4" /></svg></div>
                <h3>Kill switch, limits, alerts</h3>
                <p>A persisted kill switch that survives restarts, per-run rate limits and a daily-loss gate.</p>
                <ul>
                  <li>Global halt flattens every open paper position</li>
                  <li>Telegram alerts from the rule engine</li>
                  <li>Per-trader module grants — next up</li>
                </ul>
              </TiltCard>
            </div>
          </div>
        </section>

        {/* -------------------------------------------------- live view */}
        <section id="live" aria-labelledby="live-title">
          <div className="wrap">
            <div className="section__head reveal">
              <p className="kicker">Position-based, not order-based</p>
              <h2 id="live-title">A strategy started is not a strategy understood.</h2>
              <p>Every leg is one row: what you hold, how many lots, at what premium, what it is worth now. When a leg exits, the same row goes to zero — no separate sell order to decode.</p>
            </div>

            <div className="showcase">
              <div className="panel reveal">
                <div className="panel__head">
                  <h3 className="panel__title">
                    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d="M4 12h3l2-6 4 12 2-6h5" /></svg>
                    Titli · BANKNIFTY <span className="badge badge--live">Running · paper</span>
                  </h3>
                  <span className="muted mono" style={{ fontSize: 12 }}>2 lots × 30 · SL ₹5,000 · Target ₹8,000</span>
                </div>
                <div className="metrics">
                  <div className="metric"><div className="metric__l">Total P&amp;L</div><div className="metric__v pos">+₹349</div></div>
                  <div className="metric"><div className="metric__l">Realized</div><div className="metric__v pos">+₹70</div></div>
                  <div className="metric"><div className="metric__l">Unrealized</div><div className="metric__v pos">+₹279</div></div>
                  <div className="metric"><div className="metric__l">Spot</div><div className="metric__v">57,582.75</div></div>
                </div>
                <div className="tablewrap">
                  <table>
                    <thead><tr><th>Contract</th><th>Side</th><th className="r">Lots</th><th className="r">Lot size</th><th className="r">Qty</th><th className="r">Entry</th><th className="r">LTP</th><th className="r">P&amp;L</th><th>Status</th></tr></thead>
                    <tbody>
                      <tr><td className="mono">BANKNIFTY 57600 PE · 29 Sep</td><td><span className="badge badge--neg">SELL</span></td><td className="r">2</td><td className="r">30</td><td className="r">60</td><td className="r mono">587.75</td><td className="r mono">571.45</td><td className="r mono pos">+978.00</td><td><span className="badge badge--neutral">Open</span></td></tr>
                      <tr><td className="mono">BANKNIFTY 57600 CE · 29 Sep</td><td><span className="badge badge--neg">SELL</span></td><td className="r">2</td><td className="r">30</td><td className="r">60</td><td className="r mono">813.25</td><td className="r mono">824.90</td><td className="r mono neg">−699.00</td><td><span className="badge badge--neutral">Open</span></td></tr>
                      <tr className="row--closed"><td className="mono">BANKNIFTY 57500 PE · 29 Sep</td><td><span className="badge badge--neg">SELL</span></td><td className="r">0</td><td className="r">30</td><td className="r">0</td><td className="r mono">547.75</td><td className="r mono">—</td><td className="r mono pos">+33.75</td><td><span className="badge badge--neutral">Closed</span></td></tr>
                      <tr className="row--closed"><td className="mono">BANKNIFTY 57500 CE · 29 Sep</td><td><span className="badge badge--neg">SELL</span></td><td className="r">0</td><td className="r">30</td><td className="r">0</td><td className="r mono">872.20</td><td className="r mono">—</td><td className="r mono pos">+36.00</td><td><span className="badge badge--neutral">Closed</span></td></tr>
                    </tbody>
                  </table>
                </div>
                <p className="note">Lot sizes come from the broker's instrument master; P&amp;L = Δpremium × lots × lot size. The guard checks total P&amp;L every 3 seconds and squares off at the last mark when it trips.</p>
              </div>

              <div className="panel reveal" style={{ '--d': '0.1s' } as CSSProperties}>
                <div className="panel__head">
                  <h3 className="panel__title">
                    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d="M12 8v4l3 3" /><circle cx="12" cy="12" r="9" /></svg>
                    Activity
                  </h3>
                  <span className="faint" style={{ fontSize: 12 }}>reasons, in the strategy's own words</span>
                </div>
                <ul className="activity">
                  <li><span className="mono faint">10:47:12</span> <span className="badge badge--pos">OPEN_GROUP</span> Initial Titli short straddle at ATM 57500</li>
                  <li><span className="mono faint">11:02:40</span> <span className="badge badge--neutral">CLOSE_GROUP</span> Closing previous group because ATM shifted from 57500 to 57600</li>
                  <li><span className="mono faint">11:02:40</span> <span className="badge badge--pos">OPEN_GROUP</span> Opening new Titli short straddle at ATM 57600</li>
                </ul>
                <p className="note">When a run ends — stop-loss, target, market close, or you — the reason is persisted with it, so the card still says <i>why</i> after an API restart. Runner output is one click away.</p>
              </div>
            </div>
          </div>
        </section>

        {/* ------------------------------------------------ backtesting */}
        <section id="backtest" aria-labelledby="bt-title">
          <div className="wrap">
            <div className="section__head reveal">
              <p className="kicker">Backtesting, coverage-first</p>
              <h2 id="bt-title">A losing backtest, shown honestly.</h2>
              <p>A real run from the console: GhostTangentCrossings on BANKNIFTY 5-minute candles, 12 sessions, one lot. The 13 entries on seven expired contracts the broker no longer serves history for are listed as skipped — not quietly filled at a made-up price.</p>
            </div>

            <div className="showcase">
              <div className="panel reveal">
                <div className="panel__head">
                  <h3 className="panel__title">
                    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d="M9 3h6M10 3v6l-5 9a2 2 0 0 0 1.8 3h10.4a2 2 0 0 0 1.8-3l-5-9V3" /></svg>
                    Ghost · BANKNIFTY · 5m · 19 Aug → 3 Sep <span className="badge badge--neutral">Completed</span>
                  </h3>
                </div>
                <div className="metrics">
                  <div className="metric"><div className="metric__l">Net P&amp;L</div><div className="metric__v neg">−₹2,428</div></div>
                  <div className="metric"><div className="metric__l">Trades</div><div className="metric__v">26</div></div>
                  <div className="metric"><div className="metric__l">Win rate</div><div className="metric__v">50%</div></div>
                  <div className="metric"><div className="metric__l">Profit factor</div><div className="metric__v">0.88</div></div>
                  <div className="metric"><div className="metric__l">Max drawdown</div><div className="metric__v">1.39%</div></div>
                  <div className="metric"><div className="metric__l">Skipped</div><div className="metric__v">16</div></div>
                </div>
                <div className="bt-curve" aria-label="Equity curve, illustrative">
                  <svg viewBox="0 0 600 120" preserveAspectRatio="none" role="img">
                    <defs><linearGradient id="lp-eq" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stopColor="#4f7dff" stopOpacity=".35" /><stop offset="1" stopColor="#4f7dff" stopOpacity="0" /></linearGradient></defs>
                    <path d="M0 60 L250 60 L300 59 L350 96 L400 92 L450 90 L500 78 L550 78 L600 69 L600 120 L0 120 Z" fill="url(#lp-eq)" />
                    <path d="M0 60 L250 60 L300 59 L350 96 L400 92 L450 90 L500 78 L550 78 L600 69" fill="none" stroke="#7096ff" strokeWidth="2" />
                    <line x1="0" y1="60" x2="600" y2="60" stroke="#2b3a4e" strokeDasharray="4 4" />
                  </svg>
                </div>
                <div className="daily" aria-label="Daily P&amp;L, illustrative">
                  <i style={{ height: '3%' }} /><i style={{ height: '3%' }} /><i style={{ height: '3%' }} /><i style={{ height: '3%' }} /><i style={{ height: '3%' }} />
                  <i style={{ height: '4%' }} /><i className="n" style={{ height: '100%' }} /><i style={{ height: '10%' }} /><i style={{ height: '7%' }} /><i style={{ height: '31%' }} /><i style={{ height: '4%' }} /><i style={{ height: '24%' }} />
                </div>
                <div className="daily-l"><span>19 Aug</span><span>27 Aug · −₹8,940</span><span>03 Sep</span></div>
              </div>

              <div className="panel reveal" style={{ '--d': '0.1s' } as CSSProperties}>
                <div className="panel__head"><h3 className="panel__title">Data notes</h3></div>
                <ul className="notes">
                  <li>Lot size 30 from the instrument master applied to the whole range.</li>
                  <li>Option premiums come from broker history for contracts that still exist; expired contracts were skipped.</li>
                  <li>Skipped 13 entries — no premium history (7 contracts: 26AUG 57300–57700 CE/PE).</li>
                  <li>Skipped 3 entries signalled after the 15:15 IST square-off.</li>
                  <li>6 end-of-day square-offs applied.</li>
                </ul>
                <p className="note">Every exit row carries its reason — <i>End-of-day square-off 15:15 IST</i>, <i>End of backtest</i>, or a stop-loss / target trip — so the ledger explains itself.</p>
              </div>
            </div>
          </div>
        </section>

        {/* ----------------------------------------------- how it works */}
        <section id="how" aria-labelledby="how-title">
          <div className="wrap">
            <div className="section__head reveal">
              <p className="kicker">A run, end to end</p>
              <h2 id="how-title">Six steps from idea to ledger.</h2>
              <p>The same path whether you replay history or run on today's ticks.</p>
            </div>
            <div className="steps reveal">
              <div className="step"><h4>Pick a strategy</h4><p>Description, category, legs and data needs come from the Python class itself.</p></div>
              <div className="step"><h4>Choose the underlying</h4><p>From the loaded F&amp;O inventory: next expiry, lot size, strike step, contracts.</p></div>
              <div className="step"><h4>Size and guard</h4><p>Lots, optional ₹ stop-loss and target on total P&amp;L. Or a date range for a backtest.</p></div>
              <div className="step"><h4>Start</h4><p>A dedicated runner process per run; output streamed to the console.</p></div>
              <div className="step"><h4>Watch positions</h4><p>Lots × lot size, entry, LTP, P&amp;L per contract; activity in plain words.</p></div>
              <div className="step"><h4>Stop with a reason</h4><p>Guard, market close, you, or the end of the range — recorded, never silent.</p></div>
            </div>
          </div>
        </section>

        {/* ------------------------------------------------- principles */}
        <section id="principles" aria-labelledby="pr-title">
          <div className="wrap">
            <div className="section__head reveal">
              <p className="kicker">Principles</p>
              <h2 id="pr-title">Built for operators who want to know.</h2>
            </div>
            <div className="principles">
              <div className="principle reveal"><span className="k">01</span><h3>Nothing fails silently</h3><p>A stalled feed, an expired contract, a skipped entry, a runner that died — each is visible where you are looking, with a reason.</p></div>
              <div className="principle reveal" style={{ '--d': '0.08s' } as CSSProperties}><span className="k">02</span><h3>Coverage first</h3><p>Every picker is built from an inventory of what is actually stored. You cannot ask for a range that has no sessions.</p></div>
              <div className="principle reveal" style={{ '--d': '0.16s' } as CSSProperties}><span className="k">03</span><h3>One contract, live and replay</h3><p>Strategies implement one <span className="mono">on_bar</span>. The live runner and the backtester feed it the same shapes.</p></div>
              <div className="principle reveal" style={{ '--d': '0.24s' } as CSSProperties}><span className="k">04</span><h3>Self-hosted, your keys</h3><p>Runs on your machine or your server with your broker credentials. Postgres/TimescaleDB, Redis, a .NET API and a Python engine — nothing leaves.</p></div>
            </div>
          </div>
        </section>

        <div className="wrap">
          <div className="stack reveal">
            <span><b>.NET 10</b> API</span><span><b>Python</b> engine</span><span><b>React 19</b> console</span><span><b>TimescaleDB</b></span><span><b>Redis</b></span><span><b>FYERS</b> data &amp; auth</span><span><b>SignalR</b> live updates</span>
          </div>
        </div>

        <section className="final" aria-labelledby="final-title">
          <div className="wrap reveal">
            <p className="kicker">Get started</p>
            <h2 id="final-title">Open the console.</h2>
            <p>Sign in, start the live feed, and run your first strategy on paper today. Backtest it over stored history tonight.</p>
            <div className="cta" style={{ justifyContent: 'center' }}>
              <Link className="btn btn--primary" to={consoleHref}>{consoleLabel}</Link>
              <a className="btn" href="#modules">See the modules</a>
            </div>
          </div>
        </section>
      </main>

      <footer>
        <div className="wrap">
          <span>AlgoTrading Console · open source · paper execution on live ticks</span>
          <span><a href="#top">Back to top ↑</a></span>
        </div>
      </footer>
    </div>
  )
}
