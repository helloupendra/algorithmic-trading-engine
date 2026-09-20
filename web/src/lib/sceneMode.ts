/**
 * Whether a visitor gets the live 3D world or the stills, decided once before
 * the three.js chunk is fetched. It lives outside the scene modules on
 * purpose: importing anything from scene/ here would drag three.js into the
 * entry bundle and undo the lazy chunk.
 */

import { prefersReducedMotion } from './motion'

/** The poses the world can hold still: the story's chapters, and the sign-in page. */
export const POSES = ['hero', 'data', 'chain', 'strategy', 'risk', 'ledger', 'open', 'login'] as const
export type Pose = (typeof POSES)[number]

/** The smallest viewport that gets the live scene; phones get the stills. */
export const MIN_LIVE_WIDTH = 760

/** `?pose=<name>` holds a named pose with the page's HTML hidden: how the fallback stills are rendered. */
export function stillPose(): Pose | null {
  if (typeof window === 'undefined') return null
  const v = new URLSearchParams(window.location.search).get('pose')
  return (POSES as readonly string[]).includes(v ?? '') ? (v as Pose) : null
}

/** Whether this browser can give us a WebGL2 context at all. */
export function webglAvailable(): boolean {
  try {
    const probe = document.createElement('canvas')
    const gl = probe.getContext('webgl2')
    if (!gl) return false
    gl.getExtension('WEBGL_lose_context')?.loseContext()
    return true
  } catch {
    return false
  }
}

export function liveSceneWanted(): boolean {
  if (typeof window === 'undefined') return false
  if (stillPose()) return webglAvailable()
  if (prefersReducedMotion()) return false
  if (window.innerWidth < MIN_LIVE_WIDTH) return false
  return webglAvailable()
}
