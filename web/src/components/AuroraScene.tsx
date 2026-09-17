/**
 * The light behind the page: a slow field of colour that drifts under the hero
 * and under the sign-in form. Shared by "/" and "/login" so the two pages are
 * lit the same way.
 *
 * It is one full-screen triangle running one fragment shader — no geometry, no
 * lights, no meshes, nothing that can be framed badly at one window size and
 * well at another. Four soft sources drift on their own periods; fractal noise
 * bends them so the edges never read as circles; a vignette and a fine grain sit
 * over the top so the gradient has no banding on a dark screen. The result is
 * depth without a single object in it, which is what a page wants behind type.
 *
 * It renders at a third of the device's pixels and is upscaled by the browser —
 * a blurred field has nothing to lose by it, and the fragment cost drops by an
 * order of magnitude. It runs at ~20fps (the motion is slower than that), only
 * while its page is visible, and not at all under prefers-reduced-motion: that
 * gets one frame, which is a finished picture on its own.
 *
 * Without WebGL, or without the vendored three.js, the CSS gradients painted on
 * the wrapper are the design; the canvas only ever fades in over them.
 */

import { useLayoutEffect, useRef } from 'react'
import { prefersReducedMotion } from '../lib/motion'
import { loadThree, webglAvailable } from '../lib/three'

/** Fraction of the real pixels the field is drawn at. */
const RESOLUTION = 0.34
/** Frame budget in ms: the field moves slowly, 20fps is invisible from 24. */
const FRAME_MS = 50
const RESIZE_MS = 150

const FRAG = `
precision highp float;
uniform vec2 uSize;
uniform float uTime, uWarm;
varying vec2 vUv;

// --- value noise, three octaves. Enough to bend a gradient, cheap enough to
// --- run over a third of the screen every 50ms on an integrated GPU.
float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float noise(vec2 p) {
  vec2 i = floor(p), f = fract(p);
  vec2 u = f * f * (3.0 - 2.0 * f);
  return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), u.x),
             mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), u.x), u.y);
}
float fbm(vec2 p) {
  float v = 0.0, a = 0.5;
  for (int i = 0; i < 3; i++) { v += a * noise(p); p *= 2.02; a *= 0.5; }
  return v;
}

// One soft source: a falloff around a point, with no visible edge.
float glow(vec2 uv, vec2 at, float r) {
  float d = length((uv - at) * vec2(1.0, 0.72));
  return exp(-d * d / (r * r));
}

void main() {
  vec2 uv = vUv;
  float t = uTime * 0.045;

  // Bend the whole field, so the sources are never round.
  vec2 w = vec2(fbm(uv * 2.2 + t), fbm(uv * 2.2 - t + 4.7)) - 0.5;
  vec2 p = uv + w * 0.26;

  vec3 indigo = vec3(0.220, 0.330, 0.960);
  vec3 teal   = vec3(0.110, 0.780, 0.700);
  vec3 violet = vec3(0.520, 0.260, 0.880);
  vec3 bg     = vec3(0.016, 0.023, 0.040);

  float a = glow(p, vec2(0.18 + 0.06 * sin(t * 1.7), 0.22 + 0.05 * cos(t * 1.3)), 0.44);
  float b = glow(p, vec2(0.84 + 0.05 * cos(t * 1.1), 0.34 + 0.06 * sin(t * 0.9)), 0.38);
  float c = glow(p, vec2(0.52 + 0.10 * sin(t * 0.8), 0.86 + 0.04 * cos(t * 1.5)), 0.52);
  float d = glow(p, vec2(0.70 + 0.08 * sin(t * 0.6 + 2.0), 0.08), 0.30);

  // Restrained on purpose: this is the light a dark page is lit by, not a
  // wallpaper. Anything brighter and the type stops being the loudest thing.
  vec3 col = bg;
  col += indigo * a * 0.26;
  col += teal   * b * 0.15;
  col += violet * c * 0.12;
  col += teal   * d * 0.10 * uWarm;

  // Vignette: the corners belong to the page, not to the light.
  float v = smoothstep(0.98, 0.18, length((uv - 0.5) * vec2(1.22, 1.0)));
  col = mix(bg, col, v);

  // Grain, to keep a wide dark gradient from banding on an 8-bit screen.
  col += (hash(uv * uSize + fract(uTime)) - 0.5) * 0.016;

  gl_FragColor = vec4(col, 1.0);
}
`

const VERT = `
varying vec2 vUv;
void main() {
  vUv = uv;
  gl_Position = vec4(position.xy, 0.0, 1.0);
}
`

/** Mounts the field into the wrapper; returns the teardown. */
function mount(wrap: HTMLDivElement, canvas: HTMLCanvasElement, warm: number): () => void {
  const still = prefersReducedMotion()
  let disposed = false
  let cancelled = false
  let renderer: any = null
  let scene: any = null
  let camera: any = null
  let uniforms: Record<string, { value: any }> | null = null
  let disposables: { dispose: () => void }[] = []
  let rafId = 0
  let lastFrame = 0
  let started = 0
  let hidden = document.hidden
  let offscreen = false

  const parked = () => disposed || hidden || offscreen

  function layout() {
    if (!renderer || !uniforms) return
    const w = wrap.clientWidth
    const h = wrap.clientHeight
    if (!w || !h) return
    const dpr = Math.min(window.devicePixelRatio || 1, 2) * RESOLUTION
    renderer.setPixelRatio(dpr)
    renderer.setSize(w, h, false)
    uniforms.uSize.value.set(w * dpr, h * dpr)
    render()
  }

  function render() {
    if (renderer && scene && camera) renderer.render(scene, camera)
  }

  function schedule() {
    if (parked() || still || rafId || !renderer) return
    rafId = requestAnimationFrame(tick)
  }

  function tick(now: number) {
    rafId = 0
    if (parked() || !uniforms) return
    if (now - lastFrame >= FRAME_MS) {
      lastFrame = now
      uniforms.uTime.value = (now - started) / 1000
      render()
    }
    schedule()
  }

  const onVisibility = () => {
    hidden = document.hidden
    if (hidden) {
      if (rafId) cancelAnimationFrame(rafId)
      rafId = 0
    } else schedule()
  }
  const io =
    'IntersectionObserver' in window
      ? new IntersectionObserver((entries) => {
          const entry = entries[entries.length - 1]
          if (!entry) return
          offscreen = !entry.isIntersecting
          if (offscreen) {
            if (rafId) cancelAnimationFrame(rafId)
            rafId = 0
          } else schedule()
        })
      : null

  let resizeTimer = 0
  const ro = new ResizeObserver(() => {
    if (resizeTimer) clearTimeout(resizeTimer)
    resizeTimer = window.setTimeout(() => {
      resizeTimer = 0
      if (!disposed) layout()
    }, RESIZE_MS)
  })

  function start(): boolean {
    const THREE = window.THREE
    if (!THREE) return false
    try {
      renderer = new THREE.WebGLRenderer({ canvas, antialias: false, alpha: false, powerPreference: 'low-power' })
    } catch {
      renderer = null
      return false
    }
    scene = new THREE.Scene()
    // A single clip-space triangle: the vertex shader ignores the camera, so the
    // camera only has to exist.
    camera = new THREE.Camera()
    const geo = new THREE.PlaneGeometry(2, 2)
    uniforms = {
      uTime: { value: 0 },
      uSize: { value: new THREE.Vector2(1, 1) },
      uWarm: { value: warm },
    }
    const mat = new THREE.ShaderMaterial({ uniforms, vertexShader: VERT, fragmentShader: FRAG, depthTest: false, depthWrite: false })
    scene.add(new THREE.Mesh(geo, mat))
    disposables = [geo, mat]
    canvas.addEventListener('webglcontextlost', onContextLost)
    started = performance.now()
    layout()
    canvas.classList.add('is-on')
    if (!still) schedule()
    return true
  }

  // Not restored: the CSS gradients under the canvas are a finished picture.
  const onContextLost = () => {
    teardown()
    canvas.classList.remove('is-on')
  }

  function teardown() {
    if (rafId) cancelAnimationFrame(rafId)
    rafId = 0
    canvas.removeEventListener('webglcontextlost', onContextLost)
    disposables.forEach((d) => d.dispose?.())
    disposables = []
    renderer?.dispose()
    renderer = null
    scene = null
    camera = null
    uniforms = null
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
 * The field as a positioned layer. `className` places the wrapper; `warm` lifts
 * the fourth source, which is how the sign-in page gets a slightly warmer floor
 * than the homepage without a second shader.
 */
export function AuroraCanvas({ className, warm = 1 }: { className: string; warm?: number }) {
  const wrapRef = useRef<HTMLDivElement | null>(null)
  const canvasRef = useRef<HTMLCanvasElement | null>(null)

  useLayoutEffect(() => {
    const wrap = wrapRef.current
    const canvas = canvasRef.current
    if (!wrap || !canvas) return
    return mount(wrap, canvas, warm)
  }, [warm])

  return (
    <div className={`${className} aurora`} ref={wrapRef} aria-hidden="true">
      <canvas className="aurora__gl" ref={canvasRef} />
      <div className="aurora__grain" />
    </div>
  )
}
