/**
 * A still of the world, for visitors who do not get the live scene: reduced
 * motion, no WebGL, a phone. The stills are rendered from the real scene by
 * private/homepage-v3/stills.mjs into src/shots/stills/<pose>-<w>x<h>.webp,
 * one per chapter pose and per viewport class, and imported here by glob so a
 * missing still degrades to the page colour rather than a broken image.
 */

import { useMemo } from 'react'

const STILLS = import.meta.glob('../shots/stills/*.webp', { eager: true, import: 'default' }) as Record<string, string>

/** The viewport class a still was rendered for. */
function sizeClass(): string {
  if (typeof window === 'undefined') return '1440x900'
  const w = window.innerWidth
  if (w <= 700) return '390x844'
  if (w <= 1100 && window.innerHeight > w * 1.15) return '820x1180'
  if (w < 1400) return '1280x733'
  return '1440x900'
}

function stillFor(pose: string, size = sizeClass()): string | null {
  const exact = Object.keys(STILLS).find((k) => k.endsWith(`/${pose}-${size}.webp`))
  if (exact) return STILLS[exact]
  const any = Object.keys(STILLS).find((k) => k.includes(`/${pose}-`))
  return any ? STILLS[any] : null
}

export function Still({ pose, className = '' }: { pose: string; className?: string }) {
  const src = useMemo(() => stillFor(pose), [pose])
  if (!src) return <div className={`still still--empty ${className}`.trim()} aria-hidden="true" />
  return <img className={`still ${className}`.trim()} src={src} alt="" decoding="async" loading={pose === 'hero' || pose === 'login' ? 'eager' : 'lazy'} />
}
