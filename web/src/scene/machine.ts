/**
 * The engine in its glass case.
 *
 * One object on a black mirror floor: a two-metre case that arrives as a black
 * box and turns to glass, and inside it the five layers of the platform,
 * stacked like the boards of a machine —
 *
 *   data      a candle tape printing at the front, five rows of stored history
 *             behind it, and ticks streaming in from four feeds
 *   chain     the option chain as a butterfly of bars: calls one way, puts the
 *             other, around the strike nearest the money
 *   strategy  four conditions as gates; a bar passes through them and, if every
 *             gate holds, a signal stands up at the end
 *   risk      three nested limits — leg, group, day — around one position, and
 *             a kill switch at the corner
 *   ledger    176 tiles, one per real session of the run the site shows, tall
 *             by the day's P&L, with the account's equity as a thread in front
 *
 * Scrolling takes the lid off: the case lifts, the layers separate into an
 * exploded stack, the camera climbs it one layer at a time (scene/story.ts),
 * and at the end it all closes again. The floor is a real planar reflection
 * (three's Reflector with its own fading shader), so everything that glows is
 * seen twice.
 *
 * Nothing here is a quote. The tape and the chain come from scene/synth.ts —
 * seeded, unitless, and captioned as synthetic on the page. The only real
 * numbers in the world are the ledger's, from scene/evidence.ts.
 */

import * as THREE from 'three'
import { Reflector } from 'three/addons/objects/Reflector.js'
import { Engine } from './engine'
import { ATM, STRIKES, ladder, rng, tape } from './synth'
import { LAYERS, chapterProgress, layerY, lerpRect, stageFit, stateAt, type ChapterKey, type Rect, type StoryState } from './story'
import { RUN, SESSIONS } from './evidence'
import { follow } from '../lib/timeline'

/* ------------------------------------------------------------------ palette */

const C = {
  page: '#030406',
  up: '#25d69a',
  down: '#ff5f5c',
  call: '#7d97ff',
  put: '#2bd4bd',
  teal: '#2bd4bd',
  ice: '#bfd6ff',
  plate: '#0a0f1b',
  white: '#ffffff',
}

const SEED = 20260920
/** Half the case's edge. The case is 2 m on a side, sitting on the floor. */
const HALF = 1
/** Half a layer plate's edge. */
const PLATE = 0.85
const TAPE_N = 30
const BED_ROWS = 5
const BED_COLS = 40
const LEDGER_COLS = 16
/** The render layer for sparks and dust: the camera sees it, the floor's mirror camera does not. */
const AIR = 1

/* -------------------------------------------------------------------- types */

export type PoseName = ChapterKey | 'login'

export type AnchorName =
  | 'feed0' | 'feed1' | 'feed2' | 'feed3'
  | 'tape' | 'chain'
  | 'gate0' | 'gate1' | 'gate2' | 'gate3'
  | 'ringLeg' | 'ringGroup' | 'ringDay' | 'kill'
  | 'threadEnd'

export interface Anchor {
  x: number
  y: number
  visible: boolean
}

export interface MachineOptions {
  canvas: HTMLCanvasElement
  /**
   * 'full' is the scene on a computer or a tablet. 'phone' keeps the floor
   * reflection — it is most of the look — but renders about a million pixels,
   * with no dust and no anti-aliasing pass. Either way a GPU that cannot keep up
   * is handled at run time by the governor (`adaptive`), not guessed up front.
   */
  quality?: 'full' | 'phone'
  /** Skip the black-box-to-glass arrival (stills, reduced motion, the sign-in page). */
  skipIntro?: boolean
  /** Watch the frame time and step the quality down on a GPU that cannot keep up. */
  adaptive?: boolean
  /** A touch screen: no pointer parallax (it fights the scroll); the engine sways a little on its own instead. */
  touch?: boolean
}

/** Where the engine stands when a page lays it out itself: one box for the closed case, one for an open layer. */
export interface Stage {
  closed: Rect
  open: Rect
}

export interface MachineHandle {
  /** Where the page is on the story, 0…1. The world follows it, damped. */
  setProgress(p: number): void
  /** Hold a named pose (the sign-in page, and the fallback stills). */
  setPose(name: PoseName | null): void
  /** Pointer position as −0.5…0.5 of the viewport, for a little parallax. */
  setPointer(x: number, y: number): void
  /** The sign-in page: how awake the engine is, 0…1 (it wakes as the form is filled in). */
  setEnergy(e: number): void
  /**
   * Let the page place the engine: the world fits it into these boxes of the
   * viewport instead of using the story's own framing. Null hands the framing back.
   */
  setStage(stage: Stage | null): void
  /** The canvas size in CSS px, for pages that turn their own layout into a Stage. */
  size(): { width: number; height: number }
  resize(width: number, height: number): void
  /** Advance the world by `dt` seconds and render. */
  frame(dt: number, now: number): void
  /** A named point in the world, in CSS px of the canvas. */
  project(name: AnchorName): Anchor
  state(): StoryState
  /** The sign-in page reacts: 'open' lifts the lid, 'error' flashes the case and drops it. */
  fire(event: 'open' | 'error'): void
  dispose(): void
}

/* ------------------------------------------------------------------ helpers */

const col = (hex: string) => new THREE.Color(hex)
const rad = (deg: number) => (deg * Math.PI) / 180
const clamp01 = (t: number) => (t < 0 ? 0 : t > 1 ? 1 : t)
const lerp = (a: number, b: number, t: number) => a + (b - a) * t
const ramp = (t: number, a: number, b: number) => {
  const x = clamp01((t - a) / (b - a))
  return x * x * (3 - 2 * x)
}

/** The case: black and glossy when it arrives, then glass — all edge and highlight, almost no face. */
function caseMaterial(): THREE.ShaderMaterial {
  return new THREE.ShaderMaterial({
    uniforms: { uGlass: { value: 1 }, uAlpha: { value: 1 }, uFlash: { value: new THREE.Color(0, 0, 0) } },
    vertexShader: `
      varying vec3 vN; varying vec3 vV; varying vec2 vUv;
      void main() {
        vec4 w = modelMatrix * vec4(position, 1.0);
        vN = normalize(mat3(modelMatrix) * normal);
        vV = normalize(cameraPosition - w.xyz);
        vUv = uv;
        gl_Position = projectionMatrix * viewMatrix * w;
      }
    `,
    fragmentShader: `
      uniform float uGlass, uAlpha; uniform vec3 uFlash;
      varying vec3 vN; varying vec3 vV; varying vec2 vUv;
      void main() {
        vec3 n = normalize(vN);
        if (!gl_FrontFacing) n = -n;
        vec3 v = normalize(vV);
        float fres = pow(1.0 - abs(dot(n, v)), 3.0);
        // Two soft boxes overhead: what a sheet of glass in a dark room reflects.
        vec3 r = reflect(-v, n);
        float key = smoothstep(0.80, 0.98, dot(r, normalize(vec3(-0.45, 0.78, 0.42))));
        float fill = smoothstep(0.86, 0.99, dot(r, normalize(vec3(0.6, 0.55, -0.5)))) * 0.5;
        // A little more body toward the edges of each face, as real glass has.
        vec2 e = abs(vUv - 0.5) * 2.0;
        float rimUv = pow(max(e.x, e.y), 6.0);
        // A soft band of light that slides across the face as the view changes.
        float band = fract(vUv.x * 0.7 + vUv.y * 0.45 + dot(v, vec3(0.7, 0.15, 0.5)) * 0.8);
        float streak = smoothstep(0.34, 0.5, band) * (1.0 - smoothstep(0.5, 0.66, band));
        vec3 ice = vec3(0.62, 0.76, 1.0);
        vec3 glass = ice * (0.035 + fres * 0.85 + rimUv * 0.22 + streak * 0.10) + vec3(1.0) * (key * 0.55 + fill * 0.3);
        float glassA = 0.03 + fres * 0.5 + rimUv * 0.16 + streak * 0.07 + key * 0.4 + fill * 0.2;
        vec3 black = vec3(0.010, 0.012, 0.018) + ice * fres * 0.22 + vec3(1.0) * (key * 0.32 + fill * 0.14);
        vec3 c = mix(black, glass, uGlass) + uFlash * (0.25 + fres);
        float a = mix(1.0, glassA, uGlass) * uAlpha;
        gl_FragColor = vec4(c, clamp(a, 0.0, 1.0));
      }
    `,
    transparent: true,
    depthWrite: false,
    side: THREE.DoubleSide,
  })
}

/** A lit material whose glow follows its instance colour, scaled by one uniform the layer owns. */
function glowMaterial(level: { value: number }, base = 1): THREE.MeshStandardMaterial {
  const m = new THREE.MeshStandardMaterial({ roughness: 0.32, metalness: 0.1, envMapIntensity: 0.5 })
  m.onBeforeCompile = (shader) => {
    shader.uniforms.uLevel = level
    shader.uniforms.uGlow = { value: base }
    shader.fragmentShader = shader.fragmentShader
      .replace('#include <common>', '#include <common>\nuniform float uLevel, uGlow;')
      .replace('#include <color_fragment>', '#include <color_fragment>\ndiffuseColor.rgb *= 0.25 + 0.75 * uLevel;')
      .replace(
        '#include <emissivemap_fragment>',
        `#include <emissivemap_fragment>
        #ifdef USE_INSTANCING_COLOR
        totalEmissiveRadiance += vColor * uGlow * uLevel;
        #endif`,
      )
  }
  return m
}

/** The floor: a planar reflection that fades and softens away from the engine, over a pool of light. */
const FLOOR_SHADER = {
  name: 'EngineFloor',
  uniforms: {
    color: { value: null },
    tDiffuse: { value: null },
    textureMatrix: { value: null },
    uStrength: { value: 0.42 },
    uPool: { value: 1 },
    uHorizon: { value: null },
  },
  vertexShader: `
    uniform mat4 textureMatrix;
    varying vec4 vUv; varying vec3 vW;
    void main() {
      vUv = textureMatrix * vec4(position, 1.0);
      vW = (modelMatrix * vec4(position, 1.0)).xyz;
      gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
    }
  `,
  fragmentShader: `
    uniform vec3 color, uHorizon; uniform sampler2D tDiffuse; uniform float uStrength, uPool;
    varying vec4 vUv; varying vec3 vW;
    vec3 tap(vec2 uv) { return texture2D(tDiffuse, uv).rgb; }
    void main() {
      vec2 uv = vUv.xy / vUv.w;
      float d = length(vW.xz);
      // The further from the engine, the rougher the floor reads.
      float r = 0.0012 + d * 0.0016;
      vec2 a = vec2(r, 0.0), b = vec2(0.0, r * 1.6), c = vec2(r, r * 1.6) * 0.7071, e = vec2(r, -r * 1.6) * 0.7071;
      vec3 refl = tap(uv) * 0.2
        + (tap(uv + a) + tap(uv - a) + tap(uv + b) + tap(uv - b) + tap(uv + c) + tap(uv - c) + tap(uv + e) + tap(uv - e)) * 0.1;
      float fade = 1.0 - smoothstep(1.0, 26.0, d);
      vec3 pool = vec3(0.030, 0.052, 0.105) * exp(-d * d * 0.16) * uPool;
      vec3 lit = color + pool + refl * uStrength * fade;
      float haze = 1.0 - exp(-pow(d * 0.045, 2.0));
      gl_FragColor = vec4(mix(lit, uHorizon, haze), 1.0);
    }
  `,
}

function radialTexture(stops: [number, string][]): THREE.CanvasTexture {
  const c = document.createElement('canvas')
  c.width = 256
  c.height = 256
  const g = c.getContext('2d')!
  const grad = g.createRadialGradient(128, 128, 0, 128, 128, 128)
  for (const [at, colour] of stops) grad.addColorStop(at, colour)
  g.fillStyle = grad
  g.fillRect(0, 0, 256, 256)
  const t = new THREE.CanvasTexture(c)
  t.colorSpace = THREE.SRGBColorSpace
  return t
}

/** A square frame made of four thin bars, flat in the XZ plane (rings) or upright in YZ (gates). */
function squareFrame(half: number, bar: number, material: THREE.Material, upright: boolean): THREE.Group {
  const g = new THREE.Group()
  const long = new THREE.BoxGeometry(upright ? bar : half * 2 + bar, bar, upright ? half * 2 + bar : bar)
  const short = new THREE.BoxGeometry(bar, upright ? half * 2 + bar : bar, upright ? bar : half * 2 + bar)
  const add = (geo: THREE.BufferGeometry, x: number, y: number, z: number) => {
    const m = new THREE.Mesh(geo, material)
    m.position.set(x, y, z)
    g.add(m)
  }
  if (upright) {
    add(long, 0, half, 0)
    add(long, 0, -half, 0)
    add(short, 0, 0, half)
    add(short, 0, 0, -half)
  } else {
    add(long, 0, 0, half)
    add(long, 0, 0, -half)
    add(short, half, 0, 0)
    add(short, -half, 0, 0)
  }
  return g
}

/* ---------------------------------------------------------------- the world */

export function createMachine(o: MachineOptions): MachineHandle {
  const phone = o.quality === 'phone'
  const engine = new Engine({
    canvas: o.canvas,
    // A phone's screen is dense and its lines are thin: allow DPR 2, but hold the total to about a million pixels.
    dprCap: phone ? 2 : 1.5,
    maxPixels: phone ? 1_100_000 : 2_800_000,
    bloom: { strength: 0.55, radius: 0.7, threshold: 0.62 },
    smaa: !phone,
    grade: { grain: phone ? 0 : 0.012, vignette: phone ? 0.22 : 0.34 },
    fov: 30,
    far: 200,
    clear: C.page,
  })
  const { scene, camera, renderer } = engine
  camera.layers.enable(AIR)
  scene.fog = new THREE.FogExp2(col(C.page), 0.035)
  // The room environment is only here for the gloss on plates and candles.
  scene.environmentIntensity = 0.35

  const key = new THREE.DirectionalLight(col('#dbe6ff'), 1.4)
  key.position.set(-3, 6, 4)
  const rim = new THREE.DirectionalLight(col('#4f7dff'), 0.9)
  rim.position.set(4, 2, -5)
  scene.add(key, rim, new THREE.HemisphereLight(col('#1a2545'), col(C.page), 0.5))

  /* ---- sky ---- */
  // A cleared pixel and a shaded one do not come out of the post chain the same
  // colour, so the background is a dome: a faint glow at the horizon that the
  // floor's far distance fades into, going to the page colour overhead.
  const HORIZON = new THREE.Color(0.0115, 0.0185, 0.036)
  const sky = new THREE.Mesh(
    new THREE.SphereGeometry(80, 32, 16),
    new THREE.ShaderMaterial({
      uniforms: { uHorizon: { value: HORIZON }, uZenith: { value: col(C.page) } },
      vertexShader: `
        varying vec3 vDir;
        void main() { vDir = normalize(position); gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0); }
      `,
      fragmentShader: `
        uniform vec3 uHorizon, uZenith; varying vec3 vDir;
        void main() { gl_FragColor = vec4(mix(uHorizon, uZenith, smoothstep(0.0, 0.42, abs(vDir.y))), 1.0); }
      `,
      side: THREE.BackSide,
      depthWrite: false,
      fog: false,
    }),
  )
  sky.renderOrder = -100
  sky.frustumCulled = false
  scene.add(sky)

  /* ---- floor ---- */
  const pool = new THREE.Mesh(
    new THREE.PlaneGeometry(14, 14),
    new THREE.MeshBasicMaterial({ map: radialTexture([[0, 'rgba(18,30,62,1)'], [0.5, 'rgba(8,14,30,.5)'], [1, 'rgba(3,4,6,0)']]), transparent: true, depthWrite: false }),
  )
  pool.rotation.x = -Math.PI / 2
  pool.position.y = 0.001
  // Only shown if the governor has to switch the reflection off.
  pool.visible = false
  scene.add(pool)
  const floor = new Reflector(new THREE.PlaneGeometry(90, 90), {
    clipBias: 0.002,
    textureWidth: 1024,
    textureHeight: 512,
    color: 0x030406,
    shader: FLOOR_SHADER,
  })
  floor.rotation.x = -Math.PI / 2
  ;(floor.material as THREE.ShaderMaterial).uniforms.uHorizon.value = HORIZON
  scene.add(floor)

  // A soft light behind the engine, always opposite the camera, so the glass has something to be seen against.
  const halo = new THREE.Sprite(
    new THREE.SpriteMaterial({
      map: radialTexture([[0, 'rgba(60,110,255,.55)'], [0.35, 'rgba(30,70,190,.22)'], [1, 'rgba(3,4,6,0)']]),
      blending: THREE.AdditiveBlending,
      depthWrite: false,
      transparent: true,
      opacity: 0.55,
    }),
  )
  halo.scale.set(7.5, 5.5, 1)
  scene.add(halo)

  /* ---- the machine ---- */
  const machine = new THREE.Group()
  scene.add(machine)

  // The footprint stays lit on the floor when the case lifts.
  const EDGE = 0.01
  const edgeGeoH = new THREE.BoxGeometry(HALF * 2 + EDGE, EDGE, EDGE)
  const edgeGeoD = new THREE.BoxGeometry(EDGE, EDGE, HALF * 2 + EDGE)
  const edgeGeoV = new THREE.BoxGeometry(EDGE, HALF * 2, EDGE)
  const baseEdgeMat = new THREE.MeshBasicMaterial({ color: col(C.ice) })
  for (const s of [-1, 1]) {
    const a = new THREE.Mesh(edgeGeoH, baseEdgeMat)
    a.position.set(0, 0.007, s * HALF)
    const b = new THREE.Mesh(edgeGeoD, baseEdgeMat)
    b.position.set(s * HALF, 0.007, 0)
    machine.add(a, b)
  }

  // The case: five faces and eight edges that lift together.
  const shell = new THREE.Group()
  const caseMat = caseMaterial()
  const faceGeo = new THREE.PlaneGeometry(HALF * 2, HALF * 2)
  const faces: [number, number, number, number, number][] = [
    [0, HALF, HALF, 0, 0],
    [0, HALF, -HALF, 0, Math.PI],
    [HALF, HALF, 0, 0, Math.PI / 2],
    [-HALF, HALF, 0, 0, -Math.PI / 2],
  ]
  for (const [x, y, z, rx, ry] of faces) {
    const f = new THREE.Mesh(faceGeo, caseMat)
    f.position.set(x, y, z)
    f.rotation.set(rx, ry, 0)
    f.renderOrder = 5
    shell.add(f)
  }
  const top = new THREE.Mesh(faceGeo, caseMat)
  top.position.set(0, HALF * 2, 0)
  top.rotation.x = -Math.PI / 2
  top.renderOrder = 5
  shell.add(top)
  const edgeMat = new THREE.MeshBasicMaterial({ color: col(C.ice), transparent: true })
  for (const sx of [-1, 1]) {
    for (const sz of [-1, 1]) {
      const v = new THREE.Mesh(edgeGeoV, edgeMat)
      v.position.set(sx * HALF, HALF, sz * HALF)
      shell.add(v)
    }
    const a = new THREE.Mesh(edgeGeoH, edgeMat)
    a.position.set(0, HALF * 2, sx * HALF)
    const b = new THREE.Mesh(edgeGeoD, edgeMat)
    b.position.set(sx * HALF, HALF * 2, 0)
    shell.add(a, b)
  }
  machine.add(shell)

  /* ---- layers ---- */
  interface Layer {
    group: THREE.Group
    level: { value: number }
    plate: THREE.MeshStandardMaterial
    edge: THREE.MeshBasicMaterial
    basics: { material: THREE.MeshBasicMaterial; color: THREE.Color; gain: number }[]
  }
  const plateGeo = new THREE.BoxGeometry(PLATE * 2, 0.012, PLATE * 2)
  const PLATE_OPACITY = 0.78
  const layers: Layer[] = LAYERS.map(() => {
    const group = new THREE.Group()
    const plateMat = new THREE.MeshStandardMaterial({
      color: col(C.plate), roughness: 0.3, metalness: 0.4, transparent: true, opacity: PLATE_OPACITY, envMapIntensity: 0.45,
    })
    const plate = new THREE.Mesh(plateGeo, plateMat)
    plate.position.y = -0.006
    group.add(plate)
    const edge = new THREE.MeshBasicMaterial({ color: col(C.ice) })
    group.add(squareFrame(PLATE, 0.005, edge, false))
    machine.add(group)
    return { group, level: { value: 1 }, plate: plateMat, edge, basics: [] }
  })
  /** An unlit glowing material whose brightness follows its layer. */
  const basic = (layer: Layer, hex: string, gain: number, transparent = false): THREE.MeshBasicMaterial => {
    const material = new THREE.MeshBasicMaterial({ color: col(hex), transparent })
    layer.basics.push({ material, color: col(hex), gain })
    return material
  }

  const m4 = new THREE.Matrix4()
  const tmpColor = new THREE.Color()
  const unitBox = new THREE.BoxGeometry(1, 1, 1)
  unitBox.translate(0, 0.5, 0)

  /* data: the tape in front, five years behind it */
  const TAPE = tape(SEED, TAPE_N)
  const BED = tape(SEED + 7, BED_ROWS * BED_COLS)
  const dataL = layers[0]
  const dataCount = TAPE_N * 2 + BED_ROWS * BED_COLS
  const dataMesh = new THREE.InstancedMesh(unitBox, glowMaterial(dataL.level, 1.5), dataCount)
  dataMesh.frustumCulled = false
  const tapeX = (k: number) => -0.72 + (k / (TAPE_N - 1)) * 1.3
  const tapeBody = (k: number, grow = 1) => {
    const c = TAPE[k]
    const base = 0.02 + c.base * 0.22
    const h = (0.05 + c.body * 2.3) * grow
    return { base, h }
  }
  function writeTapeCandle(k: number, grow: number) {
    const c = TAPE[k]
    const { base, h } = tapeBody(k, grow)
    m4.makeScale(0.03, Math.max(0.004, h), 0.03).setPosition(tapeX(k), base, 0.46)
    dataMesh.setMatrixAt(k, m4)
    const wb = Math.max(0.004, base - c.wickDown * 0.5)
    const wt = base + h + c.wickUp * 0.5 * grow
    m4.makeScale(0.005, Math.max(0.004, wt - wb), 0.005).setPosition(tapeX(k), wb, 0.46)
    dataMesh.setMatrixAt(TAPE_N + k, m4)
  }
  for (let k = 0; k < TAPE_N; k++) {
    writeTapeCandle(k, 1)
    tmpColor.set(TAPE[k].up ? C.up : C.down)
    dataMesh.setColorAt(k, tmpColor)
    dataMesh.setColorAt(TAPE_N + k, tmpColor.clone().multiplyScalar(0.55))
  }
  for (let r = 0; r < BED_ROWS; r++) {
    for (let cI = 0; cI < BED_COLS; cI++) {
      const i = r * BED_COLS + cI
      const c = BED[i]
      const x = -0.76 + (cI / (BED_COLS - 1)) * 1.52
      const z = 0.12 - r * 0.2
      m4.makeScale(0.02, 0.015 + c.body * 0.5, 0.02).setPosition(x, 0.004 + c.base * 0.05, z)
      dataMesh.setMatrixAt(TAPE_N * 2 + i, m4)
      tmpColor.set(c.up ? C.up : C.down).multiplyScalar(0.3 - r * 0.035)
      dataMesh.setColorAt(TAPE_N * 2 + i, tmpColor)
    }
  }
  dataL.group.add(dataMesh)
  const tapeHead = new THREE.Vector3(tapeX(TAPE_N - 1), 0.2, 0.46)

  /* chain: calls one way, puts the other */
  const chainL = layers[1]
  const barGeo = new THREE.BoxGeometry(0.042, 0.03, 1)
  barGeo.translate(0, 0.015, 0.5)
  const chainMesh = new THREE.InstancedMesh(barGeo, glowMaterial(chainL.level, 1.0), STRIKES * 2)
  chainMesh.frustumCulled = false
  const strikeX = (k: number) => -0.7 + (k / (STRIKES - 1)) * 1.4
  for (let k = 0; k < STRIKES; k++) {
    chainMesh.setColorAt(k, tmpColor.set(C.call))
    chainMesh.setColorAt(STRIKES + k, tmpColor.set(C.put))
  }
  let chainMinute = -1
  function writeChain(minute: number) {
    const m = Math.round(minute)
    if (m === chainMinute) return
    chainMinute = m
    const len = ladder(SEED + 1, m)
    for (let k = 0; k < STRIKES; k++) {
      m4.makeScale(1, 1, 0.06 + len[k] * 0.66).setPosition(strikeX(k), 0, 0.035)
      chainMesh.setMatrixAt(k, m4)
      m4.makeScale(1, 1, -(0.06 + len[STRIKES + k] * 0.66)).setPosition(strikeX(k), 0, -0.035)
      chainMesh.setMatrixAt(STRIKES + k, m4)
    }
    chainMesh.instanceMatrix.needsUpdate = true
  }
  writeChain(105)
  const spine = new THREE.Mesh(new THREE.BoxGeometry(1.5, 0.006, 0.012), basic(chainL, '#8fa4d8', 0.7))
  spine.position.y = 0.004
  const atm = new THREE.Mesh(new THREE.BoxGeometry(0.01, 0.05, 1.5), basic(chainL, C.teal, 1.5))
  atm.position.set(strikeX(ATM) + 0.035, 0.026, 0)
  chainL.group.add(chainMesh, spine, atm)

  /* strategy: four gates, one bar passing through */
  const stratL = layers[2]
  const GATE_X = [-0.54, -0.18, 0.18, 0.54]
  const gateMats = GATE_X.map(() => new THREE.MeshBasicMaterial({ color: col(C.ice) }))
  GATE_X.forEach((x, i) => {
    const g = squareFrame(0.15, 0.012, gateMats[i], true)
    g.position.set(x, 0.19, 0)
    stratL.group.add(g)
  })
  const track = new THREE.Mesh(new THREE.BoxGeometry(1.6, 0.004, 0.004), basic(stratL, '#5a6b96', 0.8))
  track.position.y = 0.19
  const pulse = new THREE.Mesh(new THREE.BoxGeometry(0.05, 0.05, 0.05), basic(stratL, C.white, 2.6))
  const signal = new THREE.Mesh(unitBox, basic(stratL, C.up, 2.4))
  signal.position.set(0.76, 0.005, 0)
  stratL.group.add(track, pulse, signal)

  /* risk: leg inside group inside day, around one position */
  const riskL = layers[3]
  const RINGS = [0.2, 0.44, 0.7]
  const ringMats = RINGS.map(() => new THREE.MeshBasicMaterial({ color: col(C.ice) }))
  RINGS.forEach((half, i) => {
    const g = squareFrame(half, 0.012, ringMats[i], false)
    g.position.y = 0.008
    riskL.group.add(g)
  })
  const positionMat = new THREE.MeshBasicMaterial({ color: col(C.up) })
  const position = new THREE.Mesh(unitBox, positionMat)
  position.scale.set(0.07, 0.1, 0.07)
  position.position.y = 0.004
  const kill = new THREE.Mesh(new THREE.BoxGeometry(0.05, 0.035, 0.05), basic(riskL, C.down, 2.0))
  kill.position.set(0.76, 0.02, -0.76)
  riskL.group.add(position, kill)

  /* ledger: one tile per real session, and the account's equity in front of them */
  const ledgerL = layers[4]
  if (SESSIONS.length !== RUN.sessions) throw new Error('evidence drift: SESSIONS does not match RUN')
  const tiles = new THREE.InstancedMesh(unitBox, glowMaterial(ledgerL.level, 1.1), SESSIONS.length)
  tiles.frustumCulled = false
  const maxAbs = Math.max(...SESSIONS.map((s) => Math.abs(s[5])))
  let greens = 0
  SESSIONS.forEach((s, i) => {
    const c = i % LEDGER_COLS
    const r = Math.floor(i / LEDGER_COLS)
    const size = Math.pow(Math.abs(s[5]) / maxAbs, 0.65)
    m4.makeScale(0.082, 0.01 + size * 0.24, 0.082).setPosition(-0.75 + c * 0.1, 0.002, -0.62 + r * 0.1)
    tiles.setMatrixAt(i, m4)
    if (s[5] > 0) greens++
    tiles.setColorAt(i, tmpColor.set(s[5] > 0 ? C.up : C.down).multiplyScalar(0.35 + 0.65 * size))
  })
  if (greens !== RUN.profitableSessions) throw new Error('evidence drift: green tiles do not match RUN')
  const EQ_Y = 0.3
  const eqPoints = SESSIONS.map((s, i) => new THREE.Vector3(-0.78 + (i / (SESSIONS.length - 1)) * 1.56, EQ_Y + ((s[4] - RUN.initialCapital) / 160_000) * 0.3, 0.72))
  const thread = new THREE.Mesh(
    new THREE.TubeGeometry(new THREE.CatmullRomCurve3(eqPoints, false, 'catmullrom', 0.2), 500, 0.006, 6, false),
    basic(ledgerL, '#eaf1ff', 1.9),
  )
  const startLine = new THREE.Mesh(new THREE.BoxGeometry(1.56, 0.003, 0.003), basic(ledgerL, '#7c8cb4', 0.7))
  startLine.position.set(0, EQ_Y, 0.72)
  const endDot = new THREE.Mesh(new THREE.SphereGeometry(0.018, 16, 12), basic(ledgerL, C.down, 2.6))
  endDot.position.copy(eqPoints[eqPoints.length - 1])
  ledgerL.group.add(tiles, thread, startLine, endDot)

  /* ---- ticks: four feeds streaming into the head of the tape ---- */
  const FEEDS: [number, number, number][] = [
    [rad(24), 1.35, 1.8],
    [rad(56), 0.95, 1.7],
    [rad(92), 1.3, 1.75],
    [rad(128), 0.9, 1.65],
  ]
  const feedPoints = FEEDS.map(([a, y, r]) => new THREE.Vector3(Math.sin(a) * r, y, Math.cos(a) * r))
  const PER_FEED = phone ? 44 : 70
  const tickCount = PER_FEED * FEEDS.length
  const tickSource = new Float32Array(tickCount * 3)
  const tickSeed = new Float32Array(tickCount * 2)
  const r01 = rng(SEED + 3)
  for (let f = 0; f < FEEDS.length; f++) {
    for (let i = 0; i < PER_FEED; i++) {
      const k = f * PER_FEED + i
      tickSource.set([feedPoints[f].x, feedPoints[f].y, feedPoints[f].z], k * 3)
      tickSeed.set([r01(), r01()], k * 2)
    }
  }
  const tickGeo = new THREE.BufferGeometry()
  tickGeo.setAttribute('position', new THREE.BufferAttribute(new Float32Array(tickCount * 3), 3))
  tickGeo.setAttribute('aSource', new THREE.BufferAttribute(tickSource, 3))
  tickGeo.setAttribute('aSeed', new THREE.BufferAttribute(tickSeed, 2))
  const tickUniforms = {
    uTime: { value: 0 },
    uTarget: { value: tapeHead.clone() },
    uLevel: { value: 1 },
    uScale: { value: 1 },
    uMax: { value: 9 },
  }
  const ticks = new THREE.Points(
    tickGeo,
    new THREE.ShaderMaterial({
      uniforms: tickUniforms,
      vertexShader: `
        attribute vec3 aSource; attribute vec2 aSeed;
        uniform float uTime, uScale, uMax; uniform vec3 uTarget;
        varying float vA;
        void main() {
          float t = fract(aSeed.x + uTime * (0.11 + aSeed.y * 0.05));
          vec3 ctrl = mix(aSource, uTarget, 0.55) + vec3(0.0, 0.55 + aSeed.y * 0.5, 0.0) + (aSeed.xyx - 0.5) * 0.35;
          vec3 p = mix(mix(aSource, ctrl, t), mix(ctrl, uTarget, t), t);
          vA = sin(t * 3.14159) * (0.35 + 0.65 * aSeed.y);
          vec4 mv = modelViewMatrix * vec4(p, 1.0);
          gl_PointSize = min(uMax, uScale * (0.3 + 0.4 * aSeed.y) / -mv.z);
          gl_Position = projectionMatrix * mv;
        }
      `,
      fragmentShader: `
        uniform float uLevel; varying float vA;
        void main() {
          float d = length(gl_PointCoord - 0.5);
          float a = 1.0 - smoothstep(0.0, 0.5, d);
          gl_FragColor = vec4(vec3(0.45, 1.0, 0.9) * 1.6, a * a * vA * uLevel);
        }
      `,
      transparent: true,
      depthWrite: false,
      blending: THREE.AdditiveBlending,
    }),
  )
  ticks.frustumCulled = false
  ticks.layers.set(AIR)
  machine.add(ticks)

  // Dust in the air: what makes a dark room a room.
  const DUST = phone ? 0 : 320
  let dust: THREE.Points | null = null
  const dustUniforms = { uTime: { value: 0 }, uScale: { value: 1 } }
  if (DUST) {
    const pos = new Float32Array(DUST * 3)
    const rd = rng(SEED + 11)
    for (let i = 0; i < DUST; i++) pos.set([(rd() - 0.5) * 14, rd() * 7, (rd() - 0.5) * 14], i * 3)
    const g = new THREE.BufferGeometry()
    g.setAttribute('position', new THREE.BufferAttribute(pos, 3))
    dust = new THREE.Points(
      g,
      new THREE.ShaderMaterial({
        uniforms: dustUniforms,
        vertexShader: `
          uniform float uTime, uScale; varying float vA;
          void main() {
            vec3 p = position;
            p.x += sin(uTime * 0.07 + position.y * 3.0) * 0.35;
            p.y += sin(uTime * 0.05 + position.x * 2.0) * 0.25;
            vec4 mv = modelViewMatrix * vec4(p, 1.0);
            vA = 1.0 - smoothstep(3.0, 14.0, -mv.z);
            gl_PointSize = uScale * 0.2 / -mv.z;
            gl_Position = projectionMatrix * mv;
          }
        `,
        fragmentShader: `
          varying float vA;
          void main() {
            float a = 1.0 - smoothstep(0.0, 0.5, length(gl_PointCoord - 0.5));
            gl_FragColor = vec4(vec3(0.6, 0.75, 1.0), a * 0.5 * vA);
          }
        `,
        transparent: true,
        depthWrite: false,
        blending: THREE.AdditiveBlending,
      }),
    )
    dust.frustumCulled = false
    dust.layers.set(AIR)
    scene.add(dust)
  }

  /* ------------------------------------------------------------------ state */

  let target = 0
  let current = -1
  let pose: PoseName | null = null
  let pointerX = 0
  let pointerY = 0
  let panX = 0
  let panY = 0
  let spinAngle = 0
  let intro = o.skipIntro ? 99 : 0
  let energyIn = 0
  let energy = 0
  let stage: Stage | null = null
  let stageNow: Rect | null = null
  // Frame-time governor: 0 as built, 1 fewer pixels, 2 no floor reflection, 3 no bloom.
  let governor = 0
  let slowFrames = 0
  let seenFrames = 0
  let openT = -1
  let errorT = -1
  let lift = 0
  let last: StoryState = stateAt(0)
  let disposed = false
  const tmp = new THREE.Vector3()
  const lookAt = new THREE.Vector3()

  const LOGIN: Omit<StoryState, 'focus' | 'chapter' | 'u' | 'phase'> = {
    explode: 0, azim: 32, elev: 12, dist: 9.4, targetY: 1.0, shiftX: 0, shiftY: 0.04, spin: 1,
  }

  function currentState(): StoryState {
    if (pose === 'login') return { ...LOGIN, chapter: 0, u: 0, phase: 0, focus: [0, 0, 0, 0, 0] }
    return stateAt(current < 0 ? target : current)
  }

  const anchorLocal: Record<AnchorName, { p: THREE.Vector3; layer: number }> = {
    feed0: { p: feedPoints[0], layer: -1 },
    feed1: { p: feedPoints[1], layer: -1 },
    feed2: { p: feedPoints[2], layer: -1 },
    feed3: { p: feedPoints[3], layer: -1 },
    tape: { p: new THREE.Vector3(-0.1, -0.02, 0.86), layer: 0 },
    chain: { p: new THREE.Vector3(0, -0.02, 0.86), layer: 1 },
    gate0: { p: new THREE.Vector3(GATE_X[0], 0.4, 0), layer: 2 },
    gate1: { p: new THREE.Vector3(GATE_X[1], 0.4, 0), layer: 2 },
    gate2: { p: new THREE.Vector3(GATE_X[2], 0.4, 0), layer: 2 },
    gate3: { p: new THREE.Vector3(GATE_X[3], 0.4, 0), layer: 2 },
    ringLeg: { p: new THREE.Vector3(-RINGS[0], 0.03, RINGS[0]), layer: 3 },
    ringGroup: { p: new THREE.Vector3(-RINGS[1], 0.03, RINGS[1]), layer: 3 },
    ringDay: { p: new THREE.Vector3(-RINGS[2], 0.03, RINGS[2]), layer: 3 },
    kill: { p: new THREE.Vector3(0.76, 0.08, -0.76), layer: 3 },
    threadEnd: { p: eqPoints[eqPoints.length - 1].clone().add(new THREE.Vector3(0.02, 0.05, 0)), layer: 4 },
  }

  function frame(dt: number, now: number) {
    if (disposed) return
    const t = now / 1000
    const want = pose && pose !== 'login' ? chapterProgress(pose) : target
    if (current < 0 || pose) current = want
    else current = follow(current, want, dt, 0.14)
    panX += (pointerX - panX) * Math.min(1, dt * 4)
    panY += (pointerY - panY) * Math.min(1, dt * 4)
    intro += dt
    energy += (energyIn - energy) * Math.min(1, dt * 5)
    const s = currentState()
    last = s
    const isLogin = pose === 'login'

    // The arrival: edges first, then the black box clears to glass, then the engine wakes.
    const edgesOn = ramp(intro, 0.15, 1.1)
    let glass = ramp(intro, 0.7, 2.7)
    let wake = ramp(intro, 1.1, 3.2)
    if (isLogin) {
      glass = 0.4 + 0.5 * energy
      wake = 0.4 + 0.6 * energy
    }
    if (openT >= 0) {
      openT += dt
      glass = Math.max(glass, ramp(openT, 0, 0.8))
      wake = Math.max(wake, ramp(openT, 0, 0.6))
    }
    let flash = 0
    if (errorT >= 0) {
      errorT += dt
      flash = Math.max(0, 1 - errorT / 0.6)
      if (errorT > 0.6) errorT = -1
    }
    const liftWant = openT >= 0 ? ramp(openT, 0.1, 1.5) * 0.55 : s.explode
    lift = isLogin ? liftWant : s.explode

    // The engine turns on its own only while it is closed.
    spinAngle += dt * (isLogin ? 0.09 : 0.16) * s.spin
    // Wrap only while the turn is applied in full, so easing it out never jumps.
    if (s.spin === 1 && spinAngle > Math.PI) spinAngle -= Math.PI * 2
    machine.rotation.y = spinAngle * ramp(s.spin, 0, 1)

    // Layers, lit by where the camera is.
    layers.forEach((layer, i) => {
      layer.group.position.y = layerY(i, s.explode)
      const lit = lerp(0.9, 0.16 + 0.84 * s.focus[i], s.explode) * wake
      layer.level.value = lit
      // A plate overhead is a grey sheet across the frame; one below is part of the stack.
      const above = clamp01(i + 1 - s.u)
      const presence = lerp(1, Math.max(s.focus[i], 0.3 * (1 - above)), s.explode)
      layer.plate.opacity = PLATE_OPACITY * presence
      layer.group.visible = presence > 0.02
      layer.edge.color.set(C.ice).multiplyScalar((0.22 + 0.7 * lit) * lerp(1, 0.15 + 0.85 * presence, s.explode))
      for (const b of layer.basics) b.material.color.copy(b.color).multiplyScalar(b.gain * lit)
    })

    // data: the newest candle keeps printing.
    const grow = 0.55 + 0.45 * Math.sin(t * 1.7) * Math.sin(t * 0.6 + 1.3)
    writeTapeCandle(TAPE_N - 1, 0.35 + 0.65 * Math.abs(grow))
    dataMesh.instanceMatrix.needsUpdate = true

    // chain: the book breathes as the minutes pass.
    writeChain(105 + 80 * Math.sin(t * 0.22))

    // strategy: a bar passes the four conditions; if all hold, the signal stands up.
    const loop = (t % 4.2) / 4.2
    const px = -0.8 + loop * 1.75
    pulse.position.set(Math.min(0.76, px), 0.19, 0)
    pulse.visible = loop < 0.9
    const sLit = layers[2].level.value
    GATE_X.forEach((gx, i) => {
      const passed = px > gx
      gateMats[i].color.set(passed ? C.up : C.ice).multiplyScalar((passed ? 2.0 : 0.55) * sLit)
    })
    const up = clamp01((loop - 0.78) / 0.08) * (1 - clamp01((loop - 0.95) / 0.05))
    signal.scale.set(0.05, 0.001 + up * 0.26, 0.05)

    // risk: the position drifts; when it breaks the leg's limit the ring says so.
    const pnl = Math.sin(t * 0.9) * 0.6 + Math.sin(t * 2.3 + 1.0) * 0.4
    const breach = pnl < -0.72
    position.scale.y = 0.02 + Math.abs(pnl) * 0.22
    const rLit = layers[3].level.value
    positionMat.color.set(pnl >= 0 ? C.up : C.down).multiplyScalar(1.8 * rLit)
    ringMats.forEach((m, i) => m.color.set(i === 0 && breach ? C.down : C.ice).multiplyScalar((i === 0 && breach ? 2.6 : 0.75) * rLit))

    // The case.
    shell.position.y = lift * (isLogin ? 1.5 : layerY(4, 1) + 0.9)
    caseMat.uniforms.uGlass.value = glass
    const caseShown = isLogin ? 1 : 1 - ramp(lift, 0.15, 0.7)
    shell.visible = caseShown > 0.01
    caseMat.uniforms.uAlpha.value = caseShown
    ;(caseMat.uniforms.uFlash.value as THREE.Color).set(C.down).multiplyScalar(flash * 0.9)
    edgeMat.color.set(flash > 0 ? C.down : C.ice).multiplyScalar((0.25 + 0.8 * edgesOn) * caseShown)
    baseEdgeMat.color.set(C.ice).multiplyScalar((0.25 + 0.6 * edgesOn) * lerp(1, 0.35, s.explode))

    // Ticks flow once the engine is awake, mostly into the data layer.
    tickUniforms.uTime.value = t
    tickUniforms.uTarget.value.copy(tapeHead).setY(layerY(0, s.explode) + tapeHead.y)
    tickUniforms.uLevel.value = wake * lerp(0.9, 0.25 + 0.75 * s.focus[0], s.explode)
    dustUniforms.uTime.value = t

    // Camera: around the engine, looking at the layer it is on.
    const W = engine.cssWidth
    const H = engine.cssHeight
    const portrait = H > W * 1.15
    // On a touch screen the engine sways a little on its own in place of pointer parallax.
    const sway = o.touch ? Math.sin(t * 0.33) * 3.2 * (1 - s.spin) : 0
    const az = rad(s.azim + panX * 9 + sway)
    // A tall, narrow stage is filled by looking at an open layer from higher up.
    const elev = Math.max(4, s.elev - panY * 5) + (stage && portrait ? 20 * s.explode : 0)
    const el = rad(elev)
    // A page that lays the engine out itself hands over the boxes; the world
    // follows them (damped, so a bottom sheet changing height is a glide, not a
    // jump) and fits the engine inside. Otherwise the story's own framing applies.
    let fitted: ReturnType<typeof stageFit> | null = null
    if (stage) {
      const want = lerpRect(stage.closed, stage.open, s.explode)
      const k = stageNow ? 1 - Math.pow(0.5, dt / 0.16) : 1
      stageNow = stageNow ? lerpRect(stageNow, want, k) : want
      fitted = stageFit(stageNow, W, H, camera.fov, s.explode, elev)
    }
    const dist = fitted ? fitted.dist : s.dist * (portrait ? 1.55 : 1)
    lookAt.set(0, s.targetY, 0)
    camera.position.set(Math.sin(az) * Math.cos(el) * dist, s.targetY + Math.sin(el) * dist, Math.cos(az) * Math.cos(el) * dist)
    camera.lookAt(lookAt)
    // Where the engine stands on screen is set by trucking the camera sideways
    // and up, not by an off-axis frustum: the floor's planar reflection mirrors
    // the camera, and a mirrored camera with a skewed frustum looks at the wrong
    // part of the floor (the reflection smears in from the screen's edge).
    const sx = fitted ? fitted.shiftX : portrait ? 0 : s.shiftX
    const sy = fitted ? fitted.shiftY : portrait ? 0.2 : s.shiftY
    const metresPerPx = dist / (H / 2 / Math.tan(rad(camera.fov) / 2))
    camera.updateMatrixWorld()
    camera.position.addScaledVector(tmp.setFromMatrixColumn(camera.matrixWorld, 0), -sx * W * metresPerPx)
    camera.position.addScaledVector(tmp.setFromMatrixColumn(camera.matrixWorld, 1), sy * H * metresPerPx)
    camera.updateMatrixWorld()

    tmp.copy(camera.position).setY(0).normalize()
    halo.position.set(-tmp.x * 4, s.targetY + 0.9, -tmp.z * 4)
    ;(halo.material as THREE.SpriteMaterial).opacity = (0.16 + 0.22 * wake) * (1 - 0.92 * s.explode)

    const pr = renderer.getPixelRatio()
    tickUniforms.uScale.value = H * pr * 0.11
    tickUniforms.uMax.value = 9 * pr
    dustUniforms.uScale.value = H * pr * 0.11
    engine.render(now)
    if (o.adaptive) govern(dt)
  }

  /**
   * A GPU that cannot hold ~30 fps gets a cheaper picture, one step at a time,
   * each step judged on its own two seconds: fewer pixels, then no floor
   * reflection (the second render of the scene), then no bloom.
   */
  function govern(dt: number) {
    if (governor >= 3 || intro < 4) return
    seenFrames++
    if (dt > 1 / 32) slowFrames++
    if (seenFrames < 120) return
    const struggling = slowFrames > seenFrames * 0.5
    seenFrames = 0
    slowFrames = 0
    if (!struggling) return
    governor++
    if (governor === 1) engine.scalePixelBudget(0.6)
    else if (governor === 2) {
      floor.visible = false
      pool.visible = true
    } else if (engine.bloom) engine.bloom.enabled = false
  }

  function project(name: AnchorName): Anchor {
    const a = anchorLocal[name]
    tmp.copy(a.p)
    if (a.layer >= 0) tmp.y += layerY(a.layer, last.explode)
    tmp.applyMatrix4(machine.matrixWorld).project(camera)
    const W = engine.cssWidth
    const H = engine.cssHeight
    return { x: (tmp.x * 0.5 + 0.5) * W, y: (-tmp.y * 0.5 + 0.5) * H, visible: tmp.z > -1 && tmp.z < 1 && Math.abs(tmp.x) < 1.1 && Math.abs(tmp.y) < 1.1 }
  }

  function resize(width: number, height: number) {
    // A portrait stage is narrow: a wider lens keeps the engine close and its perspective alive.
    camera.fov = stage && height > width * 1.15 ? 38 : 30
    engine.size(width, height)
    const pr = renderer.getPixelRatio()
    floor.getRenderTarget().setSize(Math.max(256, Math.round(width * pr * 0.5)), Math.max(128, Math.round(height * pr * 0.5)))
  }

  if (import.meta.env.DEV) {
    ;(window as unknown as { __openfno?: unknown }).__openfno = { scene, camera, renderer, machine, layers, engine }
  }

  return {
    setProgress(p) {
      target = clamp01(p)
    },
    setPose(name) {
      pose = name
      if (name) intro = 99
    },
    setPointer(x, y) {
      pointerX = x
      pointerY = y
    },
    setEnergy(e) {
      energyIn = clamp01(e)
    },
    setStage(next) {
      const had = !!stage
      stage = next
      if (!next) stageNow = null
      if (had !== !!next) resize(engine.cssWidth, engine.cssHeight)
    },
    size: () => ({ width: engine.cssWidth, height: engine.cssHeight }),
    resize,
    frame,
    project,
    state: () => last,
    fire(event) {
      if (event === 'open') {
        openT = 0
        errorT = -1
      } else {
        errorT = 0
        openT = -1
      }
    },
    dispose() {
      disposed = true
      if (import.meta.env.DEV) delete (window as unknown as { __openfno?: unknown }).__openfno
      floor.getRenderTarget().dispose()
      engine.dispose()
    },
  }
}
