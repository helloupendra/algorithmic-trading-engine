/**
 * Which public page a visitor gets, and whether it is the live 3D world or the
 * stills — decided before the three.js chunk is fetched. It lives outside the
 * scene modules on purpose: importing anything from scene/ here would drag
 * three.js into the entry bundle and undo the lazy chunk.
 */

import { useEffect, useState } from 'react'
import { prefersReducedMotion } from './motion'

/** The poses the world can hold still: the story's chapters, and the sign-in page. */
export const POSES = ['hero', 'data', 'chain', 'strategy', 'risk', 'ledger', 'open', 'login'] as const
export type Pose = (typeof POSES)[number]

/**
 * A phone: a narrow screen, or a short touch screen (a phone on its side).
 * Tablets and computers are neither — an iPad mini is 744 px wide in portrait —
 * and keep the desktop page.
 */
export const PHONE_QUERY = '(max-width: 700px), (max-height: 500px) and (pointer: coarse)'

export function isPhone(): boolean {
  return typeof window !== 'undefined' && !!window.matchMedia && window.matchMedia(PHONE_QUERY).matches
}

/** `isPhone()` as state, so turning or resizing the screen swaps the page rather than stretching the wrong one. */
export function usePhone(): boolean {
  const [phone, setPhone] = useState(isPhone)
  useEffect(() => {
    const mq = window.matchMedia(PHONE_QUERY)
    const onChange = () => setPhone(mq.matches)
    onChange()
    mq.addEventListener('change', onChange)
    return () => mq.removeEventListener('change', onChange)
  }, [])
  return phone
}

const param = (name: string) => (typeof window === 'undefined' ? null : new URLSearchParams(window.location.search).get(name))

/** `?pose=<name>` holds a named pose with the page's HTML hidden: how the fallback stills are rendered. */
export function stillPose(): Pose | null {
  const v = param('pose')
  return (POSES as readonly string[]).includes(v ?? '') ? (v as Pose) : null
}

/**
 * `?harness` is for the screenshot harness, which renders in software at about
 * a frame a second: skip the three-second arrival and pin the quality, so a
 * capture shows what a real GPU shows.
 */
export function harnessMode(): boolean {
  return param('harness') !== null || stillPose() !== null
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
  return webglAvailable()
}
