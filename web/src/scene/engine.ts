/**
 * The renderer behind the public pages: one WebGL context, one post chain, one
 * environment, sized to its canvas and rendered only when asked.
 *
 * Everything scene-specific (what is in the world, how scroll moves it) lives
 * in scene/machine.ts; this file is the part that is the same whatever the
 * world contains — colour management, tone mapping, the bloom the emissive
 * objects need to read as light, the image-based lighting that makes glass
 * and metal look like glass and metal, the anti-aliasing the composer has to
 * do itself, a touch of grain against banding on a dark page, a pixel budget
 * that keeps a retina laptop (or a 4K monitor) at 60 fps, and a dispose that
 * frees the GPU.
 */

import * as THREE from 'three'
import { EffectComposer } from 'three/addons/postprocessing/EffectComposer.js'
import { RenderPass } from 'three/addons/postprocessing/RenderPass.js'
import { UnrealBloomPass } from 'three/addons/postprocessing/UnrealBloomPass.js'
import { SMAAPass } from 'three/addons/postprocessing/SMAAPass.js'
import { ShaderPass } from 'three/addons/postprocessing/ShaderPass.js'
import { OutputPass } from 'three/addons/postprocessing/OutputPass.js'
import { RoomEnvironment } from 'three/addons/environments/RoomEnvironment.js'

export interface EngineOptions {
  canvas: HTMLCanvasElement
  /** Device pixels per CSS pixel, at most. Post-processing doubles the cost of every pixel. */
  dprCap?: number
  /** Rendered pixels, at most: a 4K window at DPR 1 is as expensive as a laptop at DPR 2. */
  maxPixels?: number
  /** Bloom on the bright parts of the frame; strength 0 disables the pass. */
  bloom?: { strength: number; radius: number; threshold: number }
  /** Whether to resolve edges with SMAA (the renderer itself has no MSAA under the composer). */
  smaa?: boolean
  /** Film grain amount in linear units and vignette strength 0…1; both 0 skips the pass. */
  grade?: { grain: number; vignette: number }
  /** Field of view in degrees, vertical. */
  fov?: number
  near?: number
  far?: number
  /** The colour the canvas clears to; the page behind it never shows. */
  clear?: string
}

const GRADE = {
  uniforms: {
    tDiffuse: { value: null as THREE.Texture | null },
    uGrain: { value: 0.012 },
    uVignette: { value: 0.18 },
    uTime: { value: 0 },
  },
  vertexShader: `
    varying vec2 vUv;
    void main() { vUv = uv; gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0); }
  `,
  fragmentShader: `
    uniform sampler2D tDiffuse;
    uniform float uGrain, uVignette, uTime;
    varying vec2 vUv;
    float hash(vec2 p) { return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453); }
    void main() {
      vec4 c = texture2D(tDiffuse, vUv);
      float v = 1.0 - smoothstep(0.35, 1.05, length((vUv - 0.5) * vec2(1.0, 0.85)));
      c.rgb *= mix(1.0, v, uVignette);
      c.rgb += (hash(vUv * vec2(1920.0, 1080.0) + uTime) - 0.5) * uGrain;
      gl_FragColor = c;
    }
  `,
}

export class Engine {
  readonly renderer: THREE.WebGLRenderer
  readonly scene: THREE.Scene
  readonly camera: THREE.PerspectiveCamera
  readonly composer: EffectComposer
  readonly bloom: UnrealBloomPass | null
  readonly grade: ShaderPass | null
  private readonly dprCap: number
  private readonly maxPixels: number
  private width = 1
  private height = 1
  private disposed = false

  constructor(o: EngineOptions) {
    this.dprCap = o.dprCap ?? 1.5
    this.maxPixels = o.maxPixels ?? 2_800_000
    this.renderer = new THREE.WebGLRenderer({
      canvas: o.canvas,
      antialias: false, // the post chain resolves to its own targets; SMAA does the edges
      alpha: false,
      powerPreference: 'high-performance',
      stencil: false,
      depth: true,
    })
    this.renderer.setClearColor(new THREE.Color(o.clear ?? '#04060c'), 1)
    this.renderer.outputColorSpace = THREE.SRGBColorSpace
    this.renderer.toneMapping = THREE.ACESFilmicToneMapping
    this.renderer.toneMappingExposure = 1.0

    this.scene = new THREE.Scene()
    this.camera = new THREE.PerspectiveCamera(o.fov ?? 32, 16 / 9, o.near ?? 0.05, o.far ?? 60)

    // Image-based light: a small neutral room, pre-filtered once. Without it a
    // physical material is a flat grey; with it a glass edge catches a highlight.
    const pmrem = new THREE.PMREMGenerator(this.renderer)
    this.scene.environment = pmrem.fromScene(new RoomEnvironment(), 0.04).texture
    pmrem.dispose()

    // Half-float targets keep the bloom from banding on a dark page.
    const target = new THREE.WebGLRenderTarget(1, 1, { type: THREE.HalfFloatType, samples: 0 })
    this.composer = new EffectComposer(this.renderer, target)
    this.composer.addPass(new RenderPass(this.scene, this.camera))
    const b = o.bloom ?? { strength: 0.35, radius: 0.4, threshold: 0.85 }
    if (b.strength > 0) {
      this.bloom = new UnrealBloomPass(new THREE.Vector2(1, 1), b.strength, b.radius, b.threshold)
      this.composer.addPass(this.bloom)
    } else {
      this.bloom = null
    }
    const g = o.grade ?? { grain: 0.012, vignette: 0.18 }
    if (g.grain > 0 || g.vignette > 0) {
      this.grade = new ShaderPass({
        uniforms: THREE.UniformsUtils.clone(GRADE.uniforms),
        vertexShader: GRADE.vertexShader,
        fragmentShader: GRADE.fragmentShader,
      })
      this.grade.uniforms.uGrain.value = g.grain
      this.grade.uniforms.uVignette.value = g.vignette
      this.composer.addPass(this.grade)
    } else {
      this.grade = null
    }
    if (o.smaa ?? true) this.composer.addPass(new SMAAPass())
    this.composer.addPass(new OutputPass())
  }

  /** Resize to CSS pixels; the pixel ratio is applied here, capped. */
  size(width: number, height: number, dpr = window.devicePixelRatio || 1): void {
    if (this.disposed || !width || !height) return
    this.width = width
    this.height = height
    const ratio = Math.min(dpr, this.dprCap, Math.sqrt(this.maxPixels / (width * height)))
    this.renderer.setPixelRatio(ratio)
    this.renderer.setSize(width, height, false)
    this.composer.setPixelRatio(ratio)
    this.composer.setSize(width, height)
    this.camera.aspect = width / height
    this.camera.updateProjectionMatrix()
  }

  get aspect(): number {
    return this.width / this.height
  }

  get cssWidth(): number {
    return this.width
  }

  get cssHeight(): number {
    return this.height
  }

  render(time = 0): void {
    if (this.disposed) return
    if (this.grade) this.grade.uniforms.uTime.value = time % 1000
    this.composer.render()
  }

  dispose(): void {
    if (this.disposed) return
    this.disposed = true
    this.scene.traverse((obj) => {
      const m = obj as THREE.Mesh
      if (m.geometry) m.geometry.dispose()
      const mats = Array.isArray(m.material) ? m.material : m.material ? [m.material] : []
      for (const mat of mats) {
        const any = mat as unknown as Record<string, unknown>
        for (const k of Object.keys(any)) {
          const v = any[k]
          if (v instanceof THREE.Texture) v.dispose()
        }
        mat.dispose()
      }
    })
    this.scene.environment?.dispose()
    this.composer.dispose()
    this.renderer.dispose()
    this.renderer.forceContextLoss()
  }
}
