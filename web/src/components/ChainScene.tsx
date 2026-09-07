/**
 * The strike ledger: the option-chain poller's ring buffer as one small lit
 * object. Shared by the public homepage hero and the sign-in backdrop.
 *
 * The front layer is the newest poll round — a vertical strike spine, call
 * open-interest bars extending left, put bars right, every bar a solid box
 * with three baked face tones. Behind it the older rounds recede into the
 * page in true perspective and fog to the page colour. One wire threads the
 * stack at each round's spot; the front rung it crosses carries the ATM mark.
 * Every round a new layer grows out of the spine while the stack breathes
 * back one slot, then a brightness pulse walks through the strikes that round
 * polled. It is the product's own data model, not a picture of a chart, and
 * it shows three things the real poller does:
 *
 *   money-first  — each round refreshes one band of strikes by distance from
 *                  ATM, nearest first (the pulse), a full pass every six rounds;
 *   carry-forward — a strike not polled this round keeps its last figure, and a
 *                  contract never seen is null, drawn as an empty socket, never
 *                  a zero-length bar (the wings of the oldest layers);
 *   discrete rounds — OI exists only for rounds that ran, so the object is a
 *                  stack of separate layers, not a surface.
 *
 * Not a single number is printed: the chain is synthetic and the caption says
 * so. Nothing glows, nothing floats, there is no floor grid and no shadow (on
 * a near-black page a shadow cannot register) — hard box edges, exactly three
 * tones, one wire, and four mono labels.
 *
 * Rendering: four draw calls (bars as one instanced mesh with a trivial
 * shader, the spine hairline, the wire as a ribbon, the ATM rung), no lights,
 * everything opaque. The canvas covers only the object's rect, not the page:
 * the camera renders that rect as a view offset of the full-frame projection,
 * so the GPU clears and resolves a fraction of the viewport. The GPU idles
 * between rounds — the frame loop runs only while an ease, a pulse or the
 * pointer parallax is settling — and parks entirely while the tab is hidden
 * or the hero is scrolled away. Device pixel ratio is capped at 2 and steps
 * down once if frames drop. Under prefers-reduced-motion the object is a still.
 *
 * An inline SVG twin drawn from the same model and the same projection maths
 * (lib/chainView) is in the DOM from first paint; the WebGL canvas cross-fades
 * over it after its first frame. Without WebGL, without the vendored three.js,
 * or after a context loss, the twin simply is the design — there is no state
 * in which the hero shows a blank hole.
 */

import { useLayoutEffect, useRef, type RefObject } from 'react'
import { CELLS, STRIKES, createChainModel, slotAtAge, type ChainModel } from '../lib/chainModel'
import {
  COLORS,
  FOV,
  LABEL_H,
  NULL_H,
  NULL_LEN,
  PARALLAX,
  STAGE_PAD,
  accentColour,
  atmAnchor,
  callsLabel,
  captionAnchor,
  cropFor,
  frameFor,
  project,
  putsLabel,
  svgMarkup,
  updateFrame,
  wireWidthFor,
  type Frame,
  type Rects,
  type Variant,
} from '../lib/chainView'
import { prefersReducedMotion } from '../lib/motion'
import { loadThree, webglAvailable } from '../lib/three'

const SEED = 20260907
/**
 * The SVG twin draws every layer the model holds. It once stopped at 12 of 24
 * — past the mass layer, where fog has taken half the contrast — but a layer
 * at half contrast is still a hard, saturated back edge, and the cross-fade to
 * the GL canvas visibly grew the stack by the twelve layers the twin lacked.
 * The full set is ~210 KB of markup (180 paths, integer coordinates), built on
 * layout only and parsed once — a few milliseconds on an integrated GPU
 * laptop, which is cheaper than a first impression that pops.
 */
const SVG_LAYERS = Number.POSITIVE_INFINITY
/** Trailing debounce for layout after a resize. */
const RESIZE_MS = 120
/** How long the canvas cross-fade takes (matches .chain__gl in styles.css). */
const FADE_MS = 400
/** Page around the object's projected bounds the canvas also covers, CSS px. */
const CROP_MARGIN = 24

const TIMING = {
  hero: { period: 1600, ease: 600, sweep: 400 },
  login: { period: 4000, ease: 900, sweep: 0 },
}

/* ---------------------------------------------------------------- shaders */

export const BAR_VERT = `
attribute vec3 aCell;
attribute float aValue;
uniform float uHead, uShift, uN, uD, uMaxLen, uBarH, uNullH, uAtm, uSweepLo, uSweepHi, uSweepT, uDim, uTop;
uniform vec3 uCall, uPut, uNull, uBg;
varying vec3 vCol;
void main() {
  // Age of this cell's ring slot: 0 = newest (the head), 1 = the previous
  // round, uN-1 = the oldest. MUST agree with ageOf() in chainModel.ts, which
  // the wire and the SVG twin use — with the operands the other way round the
  // oldest round rendered directly behind the front layer, its never-polled
  // sockets showing as dark dashes over the wings, and every round boundary
  // jolted the whole mass two slots forward.
  float k = mod(uHead - aCell.z + uN, uN);
  float newest = step(k, 0.5);
  float grow = mix(1.0, uShift, newest);              // the new round grows out of the spine
  float age = mix(k - 1.0 + uShift, 0.0, newest);     // the rest breathe back one slot
  float isNull = step(aValue, 0.001);
  float len = mix(aValue * uMaxLen, ${NULL_LEN.toFixed(2)}, isNull) * grow;
  float h = mix(uBarH, uNullH, isNull);
  float depth = 0.6 * uD;
  vec3 p = position * vec3(len, h, depth);
  p.x += aCell.y * len * 0.5;                          // extend away from the spine
  p.y += aCell.x - 20.0;
  p.z += -age * uD - depth * 0.5;                      // box from z = 0 back to -depth
  vec3 n = normalize(mat3(modelMatrix) * normal);
  float tone = n.y > 0.5 ? uTop : (n.x > 0.5 ? 0.72 : (n.z > 0.5 ? 0.55 : 0.5));
  float dist = abs(aCell.x - uAtm);
  float inBand = step(uSweepLo - 0.5, dist) * (1.0 - step(uSweepHi - 0.5, dist));
  float boost = 1.0 + 0.25 * inBand * (1.0 - uSweepT) * newest;
  vec3 side = aCell.y < 0.0 ? uCall : uPut;
  vec3 lit = mix(uBg, mix(side * tone * boost, uNull, isNull), uDim);
  float fog = pow(clamp(age / uN, 0.0, 1.0), 0.9);
  vCol = mix(lit, uBg, fog);
  gl_Position = projectionMatrix * modelViewMatrix * vec4(p, 1.0);
}
`

const FLAT_FRAG = `
varying vec3 vCol;
void main() { gl_FragColor = vec4(vCol, 1.0); }
`

const WIRE_VERT = `
attribute float aAge;
uniform float uN, uDim;
uniform vec3 uWire, uBg;
varying vec3 vCol;
void main() {
  vCol = mix(mix(uBg, uWire, uDim), uBg, pow(clamp(aAge / uN, 0.0, 1.0), 1.1));
  gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
}
`

/* ------------------------------------------------------------------ scene */

interface Objects {
  bars: any
  aValue: any
  u: Record<string, { value: any }>
  spine: any
  wire: any
  wirePos: Float32Array
  wireAge: Float32Array
  wireGeo: any
  rung: any
  disposables: { dispose: () => void }[]
}

function hex(THREE: any, s: string): any {
  return new THREE.Color(s)
}

/** Everything that depends on N; rebuilt when the layer count changes. */
function buildObjects(THREE: any, ledger: any, model: ChainModel, frame: Frame): Objects {
  const N = model.n
  const count = CELLS * N
  const disposables: { dispose: () => void }[] = []

  // (1) Bars: one instanced box; per-instance cell coordinates and the OI
  // value. The value attribute IS the model's ring buffer (same layout), so a
  // round is one contiguous 82-float range upload with nothing copied.
  const barGeo = new THREE.BoxGeometry(1, 1, 1)
  const aCell = new Float32Array(count * 3)
  for (let i = 0; i < count; i++) {
    const slot = Math.floor(i / CELLS)
    const c = i - slot * CELLS
    aCell[i * 3] = c >> 1
    aCell[i * 3 + 1] = c & 1 ? 1 : -1
    aCell[i * 3 + 2] = slot
  }
  barGeo.setAttribute('aCell', new THREE.InstancedBufferAttribute(aCell, 3))
  const aValue = new THREE.InstancedBufferAttribute(model.values, 1)
  aValue.setUsage(THREE.DynamicDrawUsage)
  barGeo.setAttribute('aValue', aValue)
  const u: Record<string, { value: any }> = {
    uHead: { value: model.head },
    uShift: { value: 1 },
    uN: { value: N },
    uD: { value: frame.D },
    uMaxLen: { value: frame.maxLen },
    uBarH: { value: frame.barH },
    uNullH: { value: NULL_H },
    uAtm: { value: model.atm() },
    uSweepLo: { value: 0 },
    uSweepHi: { value: 0 },
    uSweepT: { value: 1 },
    uDim: { value: frame.dim },
    uTop: { value: frame.topTone },
    uCall: { value: hex(THREE, COLORS.call) },
    uPut: { value: hex(THREE, COLORS.put) },
    uNull: { value: hex(THREE, COLORS.nullCell) },
    uBg: { value: hex(THREE, COLORS.bg) },
  }
  const barMat = new THREE.ShaderMaterial({ uniforms: u, vertexShader: BAR_VERT, fragmentShader: FLAT_FRAG })
  const bars = new THREE.InstancedMesh(barGeo, barMat, count)
  bars.frustumCulled = false
  ledger.add(bars)
  disposables.push(barGeo, barMat, bars)

  // (2) Spine and rungs: hairlines by intent; they only separate calls from
  // puts. The login keeps the spine alone — rotated onto the bar axis, rungs
  // would poke out of the short wing bars as stray ticks.
  const spinePos: number[] = [0, -20, 0.05, 0, 20, 0.05]
  const spineCol: number[] = []
  const cSpine = hex(THREE, COLORS.spine)
  const cWide = hex(THREE, COLORS.rungWide)
  spineCol.push(cSpine.r, cSpine.g, cSpine.b, cSpine.r, cSpine.g, cSpine.b)
  if (frame.rungs) {
    for (let s = 0; s < STRIKES; s++) {
      const wide = s % 5 === 0
      const hw = wide ? 0.6 : 0.3
      const c = wide ? cWide : cSpine
      spinePos.push(-hw, s - 20, 0.05, hw, s - 20, 0.05)
      spineCol.push(c.r, c.g, c.b, c.r, c.g, c.b)
    }
  }
  const spineGeo = new THREE.BufferGeometry()
  spineGeo.setAttribute('position', new THREE.Float32BufferAttribute(spinePos, 3))
  spineGeo.setAttribute('color', new THREE.Float32BufferAttribute(spineCol, 3))
  const spineMat = new THREE.LineBasicMaterial({ vertexColors: true })
  const spine = new THREE.LineSegments(spineGeo, spineMat)
  spine.frustumCulled = false
  ledger.add(spine)
  disposables.push(spineGeo, spineMat)

  // (3) Spot wire: a ribbon, drawn over the stack. A GL line is one device
  // pixel and vanishes on non-retina screens; a strip has a width.
  const wirePos = new Float32Array(N * 2 * 3)
  const wireAge = new Float32Array(N * 2)
  const wireIdx = new Uint16Array((N - 1) * 6)
  for (let i = 0; i < N - 1; i++) {
    const a = i * 2
    wireIdx.set([a, a + 1, a + 2, a + 1, a + 3, a + 2], i * 6)
  }
  const wireGeo = new THREE.BufferGeometry()
  const wirePosAttr = new THREE.BufferAttribute(wirePos, 3)
  wirePosAttr.setUsage(THREE.DynamicDrawUsage)
  const wireAgeAttr = new THREE.BufferAttribute(wireAge, 1)
  wireAgeAttr.setUsage(THREE.DynamicDrawUsage)
  wireGeo.setAttribute('position', wirePosAttr)
  wireGeo.setAttribute('aAge', wireAgeAttr)
  wireGeo.setIndex(new THREE.BufferAttribute(wireIdx, 1))
  const wireMat = new THREE.ShaderMaterial({
    uniforms: { uN: u.uN, uDim: u.uDim, uWire: { value: hex(THREE, COLORS.wire) }, uBg: u.uBg },
    vertexShader: WIRE_VERT,
    fragmentShader: FLAT_FRAG,
    // Depth-tested, so a bar nearer the camera hides the wire behind it. Drawn
    // over everything it read as a line laid on top of the mass — flattening
    // the very depth it threads the rounds to show. The offset keeps its own
    // ribbon from z-fighting the spine it runs along.
    depthTest: true,
    depthWrite: false,
    polygonOffset: true,
    polygonOffsetFactor: -1,
    polygonOffsetUnits: -2,
    side: THREE.DoubleSide,
  })
  const wire = new THREE.Mesh(wireGeo, wireMat)
  wire.renderOrder = 10
  wire.frustumCulled = false
  ledger.add(wire)
  disposables.push(wireGeo, wireMat)

  // (4) ATM rung: a flat box in front of the front layer, the full call+put width.
  const rungGeo = new THREE.BoxGeometry(1, 1, 1)
  const rungMat = new THREE.MeshBasicMaterial({ color: accentColour('rung', frame.dim) })
  const rung = new THREE.Mesh(rungGeo, rungMat)
  rung.frustumCulled = false
  ledger.add(rung)
  disposables.push(rungGeo, rungMat)

  return { bars, aValue, u, spine, wire, wirePos, wireAge, wireGeo, rung, disposables }
}

function removeObjects(ledger: any, o: Objects) {
  ledger.remove(o.bars, o.spine, o.wire, o.rung)
  o.disposables.forEach((d) => d.dispose())
}

/* ---------------------------------------------------------------- runtime */

interface Els {
  wrap: HTMLDivElement
  svg: SVGSVGElement
  canvas: HTMLCanvasElement
  labels: HTMLDivElement
  anchor: HTMLElement
}

const LABEL_KEYS = ['calls', 'puts', 'atm', 'caption'] as const
type LabelKey = (typeof LABEL_KEYS)[number]

/** Mounts the ledger into the wrapper; returns the teardown. */
function mount(els: Els, variant: Variant): () => void {
  const { wrap, svg, canvas, labels, anchor } = els
  const still = prefersReducedMotion()
  const timing = TIMING[variant]
  const parallaxOk =
    variant === 'hero' && !still && !!window.matchMedia && window.matchMedia('(hover: hover)').matches

  const labelEl: Partial<Record<LabelKey, HTMLElement>> = {}
  for (const k of LABEL_KEYS) {
    const el = labels.querySelector<HTMLElement>(`[data-k="${k}"]`)
    if (el) labelEl[k] = el
  }

  let model = createChainModel(SEED, variant === 'login' ? 8 : 24)
  let frame: Frame | null = null
  let rects: Rects | null = null
  /** The hero caption's width, measured on layout so parallax frames never read layout. */
  let captionW = 0
  let disposed = false
  let cancelled = false

  // WebGL, once three has loaded and a context exists.
  let THREE: any = null
  // Set once three.js has loaded; the scene starts on the first layout with a size.
  let glWanted = false
  let renderer: any = null
  let scene: any = null
  let camera: any = null
  let ledger: any = null
  let objects: Objects | null = null
  let svgShown = true

  // Motion state.
  let rafId = 0
  let timerId = 0
  let shiftStart = -1e9
  let hidden = document.hidden
  let offscreen = false
  let yawCur = 0
  let yawTarget = 0
  let pitchCur = 0
  let pitchTarget = 0
  let hw = 0.08
  /** The wrapper rect the canvas covers: [left, top, width, height]. */
  const crop: [number, number, number, number] = [0, 0, 1, 1]
  let hideTimer = 0

  // Adaptive DPR: one-way, measured on consecutive rendered frames.
  let dprCap = 2
  let lastFrameAt = 0
  let samples = 0
  let slow = 0
  let measuring = true

  const P: [number, number] = [0, 0]
  const A: [number, number, number] = [0, 0, 0]

  if (still) wrap.classList.add('chain--still')

  /* ----------------------------------------------------------- helpers */

  function parked() {
    return disposed || hidden || offscreen
  }

  function measure(): Rects | null {
    const W = wrap.clientWidth
    const H = wrap.clientHeight
    if (!W || !H) return null
    const wr = wrap.getBoundingClientRect()
    const ar = anchor.getBoundingClientRect()
    return {
      W,
      H,
      anchor: { left: ar.left - wr.left, top: ar.top - wr.top, width: ar.width, height: ar.height, bottom: ar.bottom - wr.top },
    }
  }

  function placeLabel(key: LabelKey, x: number, y: number, z: number, origin: string) {
    const el = labelEl[key]
    if (!el || !frame) return
    project(frame, x, y, z, P)
    el.style.transform = `translate(${Math.round(P[0])}px, ${Math.round(P[1])}px) ${origin}`
  }

  function placeAtmLabel() {
    if (!frame) return
    atmAnchor(frame, model.atm(), A)
    placeLabel('atm', A[0], A[1], A[2], variant === 'hero' ? 'translate(-100%, -50%)' : 'translate(-50%, 0)')
  }

  /** Sets a side label from a projected [x, bottom y]; its top never enters the row above the stage. */
  function placeSide(key: LabelKey, origin: string) {
    const el = labelEl[key]
    if (!el || !rects) return
    const floor = rects.anchor.top + STAGE_PAD + LABEL_H
    const y = P[1] < floor ? floor : P[1]
    el.style.transform = `translate(${Math.round(P[0])}px, ${Math.round(y)}px) ${origin}`
  }

  function placeLabels() {
    if (!frame) return
    if (variant === 'hero') {
      // CALLS over the front layer's call reach; PUTS over the green mass,
      // which the stack has stepped away from the front tips by then.
      callsLabel(frame, P)
      placeSide('calls', 'translate(0, -100%)')
      putsLabel(frame, P)
      placeSide('puts', 'translate(-100%, -100%)')
      // Right-aligned to the put tips, but never starting left of the stage
      // column: at narrow widths the caption is wider than the object.
      const cap = labelEl.caption
      if (cap && rects) {
        captionAnchor(frame, P)
        const minRight = rects.anchor.left + STAGE_PAD + captionW
        const x = Math.max(P[0], minRight)
        cap.style.transform = `translate(${Math.round(x)}px, ${Math.round(P[1])}px) translate(-100%, 0)`
      }
    }
    placeAtmLabel()
  }

  function drawSvg() {
    if (!frame) return
    svg.setAttribute('viewBox', `0 0 ${frame.W} ${frame.H}`)
    svg.innerHTML = svgMarkup(model, frame, Math.min(SVG_LAYERS, frame.N))
  }

  function showSvg() {
    svgShown = true
    svg.classList.remove('is-off')
    drawSvg()
  }

  /** After the cross-fade: stop painting the twin underneath the canvas. */
  function hideSvg() {
    if (hideTimer) clearTimeout(hideTimer)
    hideTimer = 0
    canvas.removeEventListener('transitionend', hideSvg)
    if (cancelled || !renderer) return
    svgShown = false
    svg.classList.add('is-off')
  }

  function applyCamera() {
    if (!frame || !camera) return
    camera.position.set(frame.camPos[0], frame.camPos[1], frame.camPos[2])
    camera.lookAt(frame.target[0], frame.target[1], frame.target[2])
  }

  /**
   * Sizes the drawing buffer to the crop rect at the current DPR cap and
   * positions the canvas over it. The camera keeps the full-frame projection
   * and renders this rect as a view offset, so the picture is a crop of what
   * the SVG twin and the labels are projected with.
   */
  function applySize() {
    if (!frame || !renderer || !camera) return
    const cap = Math.min(dprCap, frame.W > 1800 || frame.W < 900 ? 1.5 : 2)
    renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, cap))
    renderer.setSize(crop[2], crop[3], false)
    canvas.style.left = `${crop[0]}px`
    canvas.style.top = `${crop[1]}px`
    canvas.style.width = `${crop[2]}px`
    canvas.style.height = `${crop[3]}px`
    camera.setViewOffset(frame.W, frame.H, crop[0], crop[1], crop[2], crop[3])
  }

  function writeWire(shift: number) {
    if (!objects || !frame) return
    const { wirePos, wireAge } = objects
    const N = model.n
    for (let i = 0; i < N; i++) {
      const slot = slotAtAge(model.head, i, N)
      const age = i === 0 ? 0 : i - 1 + shift
      const y = model.spot[slot] - 20
      const z = -age * frame.D
      const v = i * 6
      if (variant === 'hero') {
        wirePos[v] = 0
        wirePos[v + 1] = y - hw
        wirePos[v + 2] = z
        wirePos[v + 3] = 0
        wirePos[v + 4] = y + hw
        wirePos[v + 5] = z
      } else {
        wirePos[v] = -hw
        wirePos[v + 1] = y
        wirePos[v + 2] = z
        wirePos[v + 3] = hw
        wirePos[v + 4] = y
        wirePos[v + 5] = z
      }
      wireAge[i * 2] = age
      wireAge[i * 2 + 1] = age
    }
    objects.wireGeo.attributes.position.needsUpdate = true
    objects.wireGeo.attributes.aAge.needsUpdate = true
  }

  function uploadSlot(slot: number) {
    if (!objects) return
    objects.aValue.updateRange.offset = slot * CELLS
    objects.aValue.updateRange.count = CELLS
    objects.aValue.needsUpdate = true
  }

  function uploadAll() {
    if (!objects) return
    objects.aValue.updateRange.offset = 0
    objects.aValue.updateRange.count = -1
    objects.aValue.needsUpdate = true
  }

  function syncRound() {
    if (!objects) return
    const { u, rung } = objects
    u.uHead.value = model.head
    u.uAtm.value = model.atm()
    const band = model.band()
    u.uSweepLo.value = band[0]
    u.uSweepHi.value = band[1]
    rung.position.set(0, model.atm() - 20, 0.3)
  }

  function render() {
    if (!renderer || !scene || !camera) return
    renderer.render(scene, camera)
  }

  /* ------------------------------------------------------------ layout */

  function layout() {
    rects = measure()
    if (!rects) return
    frame = frameFor(variant, rects, yawCur, pitchCur)
    // A frame that cannot fit hides the whole object; it comes back on the next
    // resize that gives it room. Both layers go, so the SVG twin never shows a
    // clipped shelf where the GL canvas would not.
    wrap.classList.toggle('chain--unfit', !frame.fits)
    // Three loaded while the wrapper had no size (display:none on a short
    // viewport): start the scene now that there is a frame to build it for.
    if (glWanted && !renderer && startGl()) return
    if (frame.N !== model.n) {
      // A model parameter changed: rebuild from a fresh poller with the new depth.
      model = createChainModel(SEED, frame.N)
      if (objects && ledger) {
        removeObjects(ledger, objects)
        objects = buildObjects(THREE, ledger, model, frame)
        uploadAll()
        syncRound()
      }
      shiftStart = -1e9
    }
    hw = wireWidthFor(frame) / 2 / frame.pxPerUnit
    cropFor(frame, yawCur, pitchCur, CROP_MARGIN, crop)
    captionW = labelEl.caption?.offsetWidth ?? 0
    // A resize lands the object settled: whatever ease was mid-flight is over.
    shiftStart = -1e9
    if (svgShown) drawSvg()
    if (renderer && objects) {
      camera.fov = FOV
      camera.aspect = frame.aspect
      // The camera distance scales with the wrapper height over the strike
      // pitch (a tall phone page puts it hundreds of units out), so the clip
      // planes follow it: the object spans at most ~80 units around the pivot.
      camera.near = Math.max(1, frame.d * 0.25)
      camera.far = frame.d + 160
      applySize()
      camera.updateProjectionMatrix()
      applyCamera()
      objects.rung.scale.set(2 * (frame.maxLen + 0.15), 1.25 / frame.pxPerUnit, 0.05)
      objects.u.uShift.value = 1
      objects.u.uSweepT.value = 1
      writeWire(1)
      render()
    }
    placeLabels()
  }

  /* -------------------------------------------------------------- loop */

  function schedule() {
    if (parked() || still || rafId || !renderer) return
    rafId = requestAnimationFrame(tick)
  }

  function tick(now: number) {
    rafId = 0
    if (parked() || !objects || !frame) return
    const { u } = objects
    let busy = false

    // The ease: the new layer grows, the stack breathes back one slot.
    const t = Math.min(Math.max((now - shiftStart) / timing.ease, 0), 1)
    const shift = 1 - (1 - t) * (1 - t) * (1 - t)
    u.uShift.value = shift
    if (t < 1) busy = true

    // The pulse through the strikes this round polled, after the ease.
    if (timing.sweep > 0) {
      const st = (now - (shiftStart + timing.ease)) / timing.sweep
      if (st < 0) {
        u.uSweepT.value = 1
        busy = true
      } else if (st < 1) {
        u.uSweepT.value = st
        busy = true
      } else {
        u.uSweepT.value = 1
      }
    }

    // Pointer parallax: the camera re-derives around the pinned pivot, so only
    // the older layers swing; the labels follow the same maths.
    if (parallaxOk) {
      const dy = yawTarget - yawCur
      const dp = pitchTarget - pitchCur
      if (Math.abs(dy) + Math.abs(dp) > 0.002) {
        yawCur += dy * 0.06
        pitchCur += dp * 0.06
        updateFrame(frame, yawCur, pitchCur)
        applyCamera()
        placeLabels()
        busy = true
      }
    }

    writeWire(shift)
    render()

    // Adaptive DPR: count dropped frames across a continuous run; one step
    // down at a time, never back up.
    if (measuring) {
      if (lastFrameAt && now - lastFrameAt < 100) {
        samples++
        if (now - lastFrameAt > 25) slow++
        if (samples >= 90) {
          if (slow / samples > 0.2 && dprCap > 1) {
            dprCap = dprCap > 1.5 ? 1.5 : 1
            applySize()
            camera.updateProjectionMatrix()
            render() // the resized buffer starts blank; this may be the last busy frame
            if (dprCap <= 1) measuring = false
          }
          samples = 0
          slow = 0
        }
      }
      lastFrameAt = now
    }

    if (busy) schedule()
  }

  function armTimer() {
    if (parked() || still || !renderer) return
    if (timerId) clearTimeout(timerId)
    timerId = window.setTimeout(onRound, timing.period)
  }

  function onRound() {
    timerId = 0
    if (parked() || !objects) return
    model.advance()
    uploadSlot(model.head)
    syncRound()
    placeAtmLabel()
    shiftStart = performance.now()
    objects.u.uShift.value = 0
    objects.u.uSweepT.value = 1
    schedule()
    armTimer()
  }

  function park() {
    if (rafId) cancelAnimationFrame(rafId)
    rafId = 0
    if (timerId) clearTimeout(timerId)
    timerId = 0
    lastFrameAt = 0
  }

  function resume() {
    if (parked() || !objects) return
    // No catch-up burst: settle whatever was mid-flight and start a fresh period.
    objects.u.uShift.value = 1
    objects.u.uSweepT.value = 1
    shiftStart = -1e9
    writeWire(1)
    render()
    armTimer()
  }

  /* ------------------------------------------------------------ events */

  const onPointer = (e: PointerEvent) => {
    yawTarget = -(e.clientX / window.innerWidth - 0.5) * 2 * PARALLAX.yaw
    pitchTarget = (e.clientY / window.innerHeight - 0.5) * 2 * PARALLAX.pitch
    schedule()
  }
  const onVisibility = () => {
    hidden = document.hidden
    if (hidden) park()
    else resume()
  }
  const io =
    'IntersectionObserver' in window
      ? new IntersectionObserver((entries) => {
          const entry = entries[entries.length - 1]
          if (!entry) return
          offscreen = !entry.isIntersecting
          if (offscreen) park()
          else resume()
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

  function teardownGl() {
    park()
    if (hideTimer) clearTimeout(hideTimer)
    hideTimer = 0
    canvas.removeEventListener('transitionend', hideSvg)
    canvas.removeEventListener('webglcontextlost', onContextLost)
    window.removeEventListener('pointermove', onPointer)
    if (objects && ledger) removeObjects(ledger, objects)
    objects = null
    renderer?.dispose()
    renderer = null
    scene = null
    camera = null
    ledger = null
  }

  // The context is not restored (no preventDefault): tear down and let the
  // SVG twin, redrawn from the model's current state, be the page.
  const onContextLost = () => {
    glWanted = false
    teardownGl()
    canvas.classList.remove('is-on')
    showSvg()
  }

  /* ------------------------------------------------------------- start */

  document.addEventListener('visibilitychange', onVisibility)
  ro.observe(wrap)
  ro.observe(anchor)
  io?.observe(wrap)
  layout()

  /** Builds the scene and renders its first frame; false when it cannot. */
  function startGl(): boolean {
    THREE = window.THREE
    if (!THREE || !frame) return false
    try {
      renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: true, powerPreference: 'high-performance' })
    } catch {
      renderer = null
      glWanted = false
      return false
    }
    renderer.setClearColor(0x000000, 0)
    scene = new THREE.Scene()
    camera = new THREE.PerspectiveCamera(FOV, frame.aspect, 1, frame.d + 160)
    ledger = new THREE.Group()
    ledger.rotation.z = (frame.rotZ * Math.PI) / 180
    scene.add(ledger)
    objects = buildObjects(THREE, ledger, model, frame)
    uploadAll()
    syncRound()
    canvas.addEventListener('webglcontextlost', onContextLost)
    layout()

    // First frame is rendered; fade the canvas over the twin, then hide the
    // twin once the fade has finished so it is not painted underneath forever.
    canvas.classList.add('is-on')
    if (still) hideSvg()
    else {
      canvas.addEventListener('transitionend', hideSvg)
      hideTimer = window.setTimeout(hideSvg, FADE_MS + 50)
    }
    if (parallaxOk) window.addEventListener('pointermove', onPointer, { passive: true })
    armTimer()
    return true
  }

  if (webglAvailable()) {
    void loadThree().then((ok) => {
      if (cancelled || !ok) return
      glWanted = true
      startGl()
    })
  }

  return () => {
    disposed = true
    cancelled = true
    if (resizeTimer) clearTimeout(resizeTimer)
    ro.disconnect()
    io?.disconnect()
    document.removeEventListener('visibilitychange', onVisibility)
    teardownGl()
  }
}

/* -------------------------------------------------------------- component */

/**
 * The ledger as a positioned layer. `anchorRef` is the element the object is
 * laid out against (the hero's stage column; the login's risk note); the
 * caller's `className` positions the wrapper.
 */
export function ChainCanvas({
  variant,
  anchorRef,
  className,
}: {
  variant: Variant
  anchorRef: RefObject<HTMLElement | null>
  className: string
}) {
  const wrapRef = useRef<HTMLDivElement | null>(null)
  const svgRef = useRef<SVGSVGElement | null>(null)
  const canvasRef = useRef<HTMLCanvasElement | null>(null)
  const labelsRef = useRef<HTMLDivElement | null>(null)

  // Layout effect so the SVG twin is in the DOM before the first paint.
  useLayoutEffect(() => {
    const wrap = wrapRef.current
    const svg = svgRef.current
    const canvas = canvasRef.current
    const labels = labelsRef.current
    const anchor = anchorRef.current
    if (!wrap || !svg || !canvas || !labels || !anchor) return
    return mount({ wrap, svg, canvas, labels, anchor }, variant)
  }, [anchorRef, variant])

  return (
    <div className={`${className} chain chain--${variant}`} ref={wrapRef} aria-hidden="true">
      <svg className="chain__svg" ref={svgRef} xmlns="http://www.w3.org/2000/svg" />
      <canvas className="chain__gl" ref={canvasRef} />
      <div className="chain__labels" ref={labelsRef}>
        {variant === 'hero' && (
          <>
            <span className="chain__label" data-k="calls">CALLS</span>
            <span className="chain__label" data-k="puts">PUTS</span>
          </>
        )}
        <span className="chain__label chain__label--atm" data-k="atm">ATM</span>
        {variant === 'hero' ? (
          <span className="chain__caption" data-k="caption">
            Synthetic chain, for illustration
            <br />
            strike × open interest × poll round
          </span>
        ) : (
          <span className="chain__caption">Synthetic chain, for illustration</span>
        )}
      </div>
    </div>
  )
}
