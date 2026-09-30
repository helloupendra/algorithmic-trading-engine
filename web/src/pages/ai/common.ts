/**
 * The AI pages' shared non-component helpers (kept apart from parts.tsx so
 * that file exports only components, which fast refresh needs).
 */

import { useEffect, useState } from 'react'

/** Re-renders on a clock, so "running for 12 s" and ages move between polls; `active` false stops it. */
export function useNow(intervalMs: number, active = true): number {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    if (!active) return
    setNow(Date.now())
    const id = window.setInterval(() => setNow(Date.now()), intervalMs)
    return () => window.clearInterval(id)
  }, [intervalMs, active])
  return now
}

/** An API failure in words, first line only. */
export function errorText(error: unknown): string {
  const raw = error instanceof Error ? error.message : String(error ?? '')
  return raw.split('\n')[0].trim() || 'Something went wrong.'
}
