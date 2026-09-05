/**
 * The sign-in backdrop: one quiet market tape along the foot of the page.
 *
 * Deliberately flat. A perspective floor grid reads as stock sci-fi rather than
 * as this product, and candles scattered in 3D have no shape to them — the eye
 * finds no composition, only debris. A real tape is flat, continuous and reads
 * left to right, so that is what this draws: one series edge to edge, dissolving
 * upward into the page, with the close line carried through it.
 *
 * Restraint is the point. It sits behind a form, so it never competes: low
 * contrast, slow drift, no glare. Motion stops entirely under
 * prefers-reduced-motion, and if the canvas cannot start, the CSS gradient
 * underneath is the whole design rather than a blank hole.
 */

import { useEffect, useRef } from 'react'

type Candle = { open: number; high: number; low: number; close: number }

/**
 * CSS pixels between candle centres, and the body width inside that. Narrower
 * viewports get finer bars: at phone widths a 13px step leaves barely thirty
 * candles across, which reads as a chunky pattern rather than as a tape.
 */
const STEP = 13
const BODY = 6
const NARROW_STEP = 9
const NARROW_BODY = 4
const NARROW_WIDTH = 620

/** One new candle roughly every four seconds — a tape, not a ticker. */
const DRIFT_SECONDS_PER_CANDLE = 4

/** The band the tape occupies, as a fraction of viewport height from the foot. */
const BAND_TOP = 0.31
const BAND_BOTTOM = 0.035

function readToken(name: string, fallback: string): string {
  const value = getComputedStyle(document.documentElement).getPropertyValue(name).trim()
  return value || fallback
}

/**
 * A random walk that is pulled gently back toward its origin, with occasional
 * larger bars. The pull is what keeps the series inside its band over hundreds
 * of candles — an unbiased walk still wanders off, and a drifting one climbs
 * out of frame and turns the backdrop into a diagonal pointing off the page.
 */
function nextCandle(previousClose: number): Candle {
  const shock = Math.random() < 0.09 ? 2.4 : 1
  const move = (Math.random() - 0.5) * 2.2 * shock - previousClose * 0.02

  const open = previousClose
  const close = open + move
  const wick = (0.35 + Math.random() * 1.1) * shock

  return {
    open,
    close,
    high: Math.max(open, close) + wick * Math.random(),
    low: Math.min(open, close) - wick * Math.random(),
  }
}

/**
 * The vertical range to frame the tape in, ignoring the most extreme 4% at each
 * end. Scaling to the outright min and max lets a single spike own the whole
 * band and press every other candle into a flat line along the floor — which is
 * exactly what happens on a narrow viewport, where there are far fewer bars for
 * an outlier to hide among. Clipping the few that fall outside costs nothing
 * here: they are already dissolving into the page.
 */
function trimmedRange(candles: Candle[]): [number, number] {
  const lows = candles.map((c) => c.low).sort((a, b) => a - b)
  const highs = candles.map((c) => c.high).sort((a, b) => a - b)
  const cut = Math.floor(candles.length * 0.04)

  return [lows[cut] ?? lows[0], highs[highs.length - 1 - cut] ?? highs[highs.length - 1]]
}

function seedSeries(count: number): Candle[] {
  const series: Candle[] = []
  let close = 0
  for (let i = 0; i < count; i++) {
    const candle = nextCandle(close)
    series.push(candle)
    close = candle.close
  }
  return series
}

export function LoginBackdrop() {
  const canvasRef = useRef<HTMLCanvasElement | null>(null)

  useEffect(() => {
    const canvas = canvasRef.current
    if (!canvas) return

    const context = canvas.getContext('2d')
    if (!context) return

    const still = window.matchMedia('(prefers-reduced-motion: reduce)').matches

    const palette = {
      up: readToken('--pos', '#31c48d'),
      down: readToken('--neg', '#f4635e'),
      line: readToken('--brand', '#4f7dff'),
      rail: readToken('--line', '#1c2735'),
    }

    let width = 0
    let height = 0
    let step = STEP
    let body = BODY
    let candles: Candle[] = []
    let offset = 0

    // The vertical scale eases toward the series range so a new extreme slides
    // the tape rather than snapping it.
    let low = -1
    let high = 1

    function resize() {
      const ratio = Math.min(window.devicePixelRatio || 1, 2)
      width = canvas!.clientWidth
      height = canvas!.clientHeight
      canvas!.width = Math.round(width * ratio)
      canvas!.height = Math.round(height * ratio)
      context!.setTransform(ratio, 0, 0, ratio, 0, 0)

      const narrow = width < NARROW_WIDTH
      step = narrow ? NARROW_STEP : STEP
      body = narrow ? NARROW_BODY : BODY

      const needed = Math.ceil(width / step) + 3
      if (candles.length < needed) {
        const head = seedSeries(needed - candles.length)
        const bridge = candles.length > 0 ? candles[0].open : 0
        candles = [...head.map((c) => shift(c, bridge - head[head.length - 1].close)), ...candles]
      }
    }

    function shift(candle: Candle, by: number): Candle {
      return {
        open: candle.open + by,
        high: candle.high + by,
        low: candle.low + by,
        close: candle.close + by,
      }
    }

    function advance() {
      candles.shift()
      candles.push(nextCandle(candles[candles.length - 1].close))
    }

    function draw() {
      const ctx = context!
      ctx.clearRect(0, 0, width, height)
      if (width === 0 || height === 0) return

      const top = height * (1 - BAND_TOP)
      const bottom = height * (1 - BAND_BOTTOM)

      const [seriesLow, seriesHigh] = trimmedRange(candles)
      // Ease, so the scale never jumps as candles enter and leave.
      low += (seriesLow - low) * 0.04
      high += (seriesHigh - high) * 0.04
      const span = Math.max(high - low, 1)

      const y = (value: number) => bottom - ((value - low) / span) * (bottom - top)

      // Price rails: three low ones, so the tape has something to sit on
      // without a full-width line cutting across the middle of the page.
      ctx.strokeStyle = palette.rail
      ctx.globalAlpha = 0.28
      ctx.lineWidth = 1
      for (let i = 1; i <= 3; i++) {
        const railY = Math.round(bottom - ((bottom - top) / 5) * i) + 0.5
        ctx.beginPath()
        ctx.moveTo(0, railY)
        ctx.lineTo(width, railY)
        ctx.stroke()
      }
      ctx.globalAlpha = 1

      // Candles.
      ctx.lineWidth = 1
      candles.forEach((candle, index) => {
        const x = Math.round(index * step - offset) + 0.5
        if (x < -step || x > width + step) return

        const rising = candle.close >= candle.open
        ctx.strokeStyle = rising ? palette.up : palette.down
        ctx.fillStyle = rising ? palette.up : palette.down
        ctx.globalAlpha = rising ? 0.5 : 0.42

        ctx.beginPath()
        ctx.moveTo(x, y(candle.high))
        ctx.lineTo(x, y(candle.low))
        ctx.stroke()

        const openY = y(candle.open)
        const closeY = y(candle.close)
        const bodyTop = Math.min(openY, closeY)
        const bodyHeight = Math.max(Math.abs(closeY - openY), 1)
        ctx.fillRect(x - body / 2, bodyTop, body, bodyHeight)
      })

      // The close line, carried through the tape and given a soft glow. This is
      // the only part allowed to look lit.
      ctx.globalAlpha = 0.55
      ctx.strokeStyle = palette.line
      ctx.lineWidth = 1.5
      ctx.lineJoin = 'round'
      ctx.shadowColor = palette.line
      ctx.shadowBlur = 14
      ctx.beginPath()
      candles.forEach((candle, index) => {
        const x = index * step - offset
        const pointY = y(candle.close)
        if (index === 0) ctx.moveTo(x, pointY)
        else ctx.lineTo(x, pointY)
      })
      ctx.stroke()
      ctx.shadowBlur = 0
      ctx.globalAlpha = 1

      // Dissolve upward and at both edges, so the tape has no cut-off line
      // anywhere — it belongs to the page instead of sitting on top of it.
      ctx.globalCompositeOperation = 'destination-out'

      const upward = ctx.createLinearGradient(0, bottom, 0, top)
      upward.addColorStop(0, 'rgba(0,0,0,0)')
      upward.addColorStop(0.28, 'rgba(0,0,0,0.4)')
      upward.addColorStop(0.58, 'rgba(0,0,0,0.86)')
      upward.addColorStop(1, 'rgba(0,0,0,1)')
      ctx.fillStyle = upward
      ctx.fillRect(0, 0, width, bottom + 1)

      const edges = ctx.createLinearGradient(0, 0, width, 0)
      edges.addColorStop(0, 'rgba(0,0,0,1)')
      edges.addColorStop(0.16, 'rgba(0,0,0,0)')
      edges.addColorStop(0.84, 'rgba(0,0,0,0)')
      edges.addColorStop(1, 'rgba(0,0,0,1)')
      ctx.fillStyle = edges
      ctx.fillRect(0, 0, width, height)

      ctx.globalCompositeOperation = 'source-over'
    }

    // Settle the eased scale on the seeded series so the first frame is already
    // framed, rather than easing into place while someone is reading the form.
    function reframe() {
      ;[low, high] = trimmedRange(candles)
    }

    resize()
    reframe()
    draw()

    const observer = new ResizeObserver(() => {
      resize()
      reframe()
      draw()
    })
    observer.observe(canvas)

    if (still) {
      return () => observer.disconnect()
    }

    let frame = 0
    let last = performance.now()

    function tick(now: number) {
      const elapsed = Math.min((now - last) / 1000, 0.1)
      last = now

      offset += (step / DRIFT_SECONDS_PER_CANDLE) * elapsed
      while (offset >= step) {
        offset -= step
        advance()
      }

      draw()
      frame = requestAnimationFrame(tick)
    }

    frame = requestAnimationFrame(tick)

    return () => {
      cancelAnimationFrame(frame)
      observer.disconnect()
    }
  }, [])

  return <canvas ref={canvasRef} className="login__tape" aria-hidden="true" />
}
