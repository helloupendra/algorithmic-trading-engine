/**
 * Runtime loader for the vendored Three.js (r128, UMD).
 *
 * The library is injected on demand from our own origin, so the console bundle
 * never carries it and a self-hosted install with no internet still gets the
 * scene — a CDN would silently drop it to the fallback. The file is vendored at
 * web/public/vendor/three.min.js and exposes a global THREE, typed loosely on
 * purpose: it is not a bundled dependency.
 */

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
export function loadThree(): Promise<boolean> {
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

export function webglAvailable(): boolean {
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
