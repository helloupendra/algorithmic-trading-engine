/**
 * The engine behind the public pages, as a React component.
 *
 * It owns the canvas, lazy-loads the three.js chunk (scene/machine.ts), turns
 * the page's scroll into story progress and runs the one frame loop. The world
 * is alive — ticks stream, the engine turns — so it renders every frame, but
 * only while its track is on screen and the tab is visible. While the story is
 * on screen the canvas is fixed; once it has been read the canvas parks at the
 * story's foot and the rest of the page scrolls past it (a fixed canvas that is
 * switched, not a sticky one: sticky left the canvas hanging a screen into the
 * next section on iOS).
 *
 * The page places its own labels over the world from `onFrame`, which runs
 * after each rendered frame, so projection reads happen once per frame and
 * never in a scroll handler. If the chunk fails to load, WebGL is lost, or the
 * world throws while it is built, `onUnavailable` tells the page to fall back
 * to the stills instead of leaving a hole.
 *
 * `?pose=<name>` holds a named pose with the HTML hidden; that is how the
 * fallback stills are rendered from the real scene. `?harness` is the same
 * without the pose: no arrival animation and no quality governor, for the
 * screenshot harness (lib/sceneMode).
 */

import { useEffect, useRef, type RefObject } from 'react'
import { progressOf } from '../lib/timeline'
import { harnessMode, isPhone, stillPose } from '../lib/sceneMode'
import type { MachineHandle } from '../scene/machine'

export interface MachineSceneProps {
  /** 'story': progress from the track's scroll. 'login': hold the sign-in pose. */
  mode: 'story' | 'login'
  /** The element whose scroll drives the story. */
  trackRef?: RefObject<HTMLElement | null>
  onReady?: (machine: MachineHandle) => void
  /** After every rendered frame. */
  onFrame?: (machine: MachineHandle) => void
  /** The live world could not start or has died; show the stills. */
  onUnavailable: () => void
}

export function MachineScene({ mode, trackRef, onReady, onFrame, onUnavailable }: MachineSceneProps) {
  const wrapRef = useRef<HTMLDivElement | null>(null)
  const canvasRef = useRef<HTMLCanvasElement | null>(null)
  const callbacks = useRef({ onReady, onFrame, onUnavailable })
  callbacks.current = { onReady, onFrame, onUnavailable }

  useEffect(() => {
    const wrap = wrapRef.current
    const canvas = canvasRef.current
    if (!wrap || !canvas) return
    let machine: MachineHandle | null = null
    let cancelled = false
    let raf = 0
    let last = 0
    let hidden = document.hidden
    let offscreen = false
    let parked = false
    const track = trackRef?.current ?? wrap.parentElement
    const still = stillPose()
    if (still) document.documentElement.dataset.still = '1'

    const running = () => !cancelled && !hidden && !(offscreen && !still) && machine !== null
    const schedule = () => {
      if (!raf && running()) raf = requestAnimationFrame(tick)
    }

    /** Fixed while the story is on screen; parked at its foot once it has been read. */
    const readScroll = () => {
      if (mode !== 'story' || !track || !machine) return
      const r = track.getBoundingClientRect()
      machine.setProgress(progressOf(r.top, r.height, window.innerHeight))
      const shouldPark = r.bottom <= window.innerHeight
      if (shouldPark !== parked) {
        parked = shouldPark
        wrap.classList.toggle('is-parked', parked)
      }
    }

    const tick = (now: number) => {
      raf = 0
      if (!machine || !running()) return
      const dt = last ? Math.min(0.1, (now - last) / 1000) : 1 / 60
      last = now
      machine.frame(dt, now)
      callbacks.current.onFrame?.(machine)
      canvas.classList.add('is-on')
      schedule()
    }

    const onScroll = () => readScroll()
    // Mouse only: on a touch screen a pointer move is a scroll, and parallax would fight it.
    const onPointer = (e: PointerEvent) => {
      if (e.pointerType === 'mouse') machine?.setPointer(e.clientX / window.innerWidth - 0.5, e.clientY / window.innerHeight - 0.5)
    }
    const onVisibility = () => {
      hidden = document.hidden
      last = 0
      schedule()
    }
    const onLost = (e: Event) => {
      e.preventDefault()
      callbacks.current.onUnavailable()
    }
    const io = new IntersectionObserver((entries) => {
      offscreen = !entries[entries.length - 1]?.isIntersecting
      last = 0
      schedule()
    })
    let resizeTimer = 0
    const ro = new ResizeObserver(() => {
      if (resizeTimer) clearTimeout(resizeTimer)
      resizeTimer = window.setTimeout(() => {
        resizeTimer = 0
        machine?.resize(wrap.clientWidth, wrap.clientHeight)
        readScroll()
      }, 100)
    })

    void import('../scene/machine')
      .then(({ createMachine }) => {
        if (cancelled) return
        const harness = harnessMode()
        const touch = window.matchMedia('(pointer: coarse)').matches
        machine = createMachine({
          canvas,
          quality: isPhone() ? 'phone' : 'full',
          skipIntro: mode === 'login' || harness,
          adaptive: !harness,
          touch,
        })
        machine.resize(wrap.clientWidth, wrap.clientHeight)
        if (still) machine.setPose(still)
        else if (mode === 'login') machine.setPose('login')
        readScroll()
        window.addEventListener('scroll', onScroll, { passive: true })
        window.addEventListener('pointermove', onPointer, { passive: true })
        document.addEventListener('visibilitychange', onVisibility)
        canvas.addEventListener('webglcontextlost', onLost)
        ro.observe(wrap)
        if (track) io.observe(track)
        callbacks.current.onReady?.(machine)
        schedule()
      })
      .catch((err) => {
        console.warn('the 3D scene is unavailable; showing stills', err)
        if (!cancelled) callbacks.current.onUnavailable()
      })

    return () => {
      cancelled = true
      if (raf) cancelAnimationFrame(raf)
      if (resizeTimer) clearTimeout(resizeTimer)
      window.removeEventListener('scroll', onScroll)
      window.removeEventListener('pointermove', onPointer)
      document.removeEventListener('visibilitychange', onVisibility)
      canvas.removeEventListener('webglcontextlost', onLost)
      ro.disconnect()
      io.disconnect()
      if (still) delete document.documentElement.dataset.still
      machine?.dispose()
      machine = null
    }
    // Built once per mount; later props are read through the callbacks ref.
  }, [mode, trackRef])

  return (
    <div className="stage" ref={wrapRef} aria-hidden="true">
      <canvas className="stage__gl" ref={canvasRef} />
    </div>
  )
}
