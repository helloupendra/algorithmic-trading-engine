/**
 * The width of an element, kept current by a ResizeObserver, for the charts
 * drawn in pixel units (the strike bars and session lines under the chain,
 * the OI history): axis text stays 10px at every width, and a chart is the
 * same height on a phone as on a desk screen.
 */
import { useLayoutEffect, useRef, useState } from 'react'
import type { RefObject } from 'react'

export function useWidth<T extends HTMLElement>(): [RefObject<T | null>, number] {
  const ref = useRef<T>(null)
  const [width, setWidth] = useState(0)
  useLayoutEffect(() => {
    const el = ref.current
    if (!el) return
    setWidth(el.clientWidth)
    const observer = new ResizeObserver((entries) => {
      const w = Math.floor(entries[0]?.contentRect.width ?? 0)
      setWidth((prev) => (prev === w ? prev : w))
    })
    observer.observe(el)
    return () => observer.disconnect()
  }, [])
  return [ref, width]
}
