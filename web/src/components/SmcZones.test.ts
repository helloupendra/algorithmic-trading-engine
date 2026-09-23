import { describe, expect, it, vi } from 'vitest'
import { LineStyle, type SeriesAttachedParameter, type Time } from 'lightweight-charts'

import { ZonePrimitive, type Zone } from './SmcZones'

/**
 * The zone primitive's coordinate maths, driven through its public surface:
 * attached() → setZones() → updateAllViews() → paneViews()[0].renderer(), which
 * is exactly the order lightweight-charts drives it in.
 *
 * What is mocked is only the four library surfaces the primitive reads — the
 * time scale's logicalToCoordinate and getVisibleLogicalRange, the series'
 * priceToCoordinate, and the canvas target — and each is modelled on what the
 * installed build actually does, not on what would be convenient. The one that
 * matters is logicalToCoordinate: it returns 0 for a non-integer index (see
 * `scale` below), and the tests here are what stop that trap being walked into
 * again.
 *
 * Nothing here asserts pixels look nice. It asserts the arithmetic that turns
 * a price/index pair into a rectangle, and the decisions around it — culling,
 * open right edges, and when a box is not drawn at all.
 */

/** Bar spacing in the fake scale, in media pixels. Round, so the sums read. */
const BAR_PX = 10

/** Where the fake price scale puts a price. Inverted, as a real one is. */
const yOf = (price: number) => 1200 - price * 10

type Range = { from: number; to: number }

/** What one box actually painted, reassembled from the canvas calls. */
type PaintedBox = {
  x: number
  y: number
  w: number
  h: number
  fill: string
  edge: string
  lineWidth: number
  dashed: boolean
  /** The stroke rect, kept apart so a test can check it covers the fill exactly. */
  stroke: { x: number; y: number; w: number; h: number }
  label: { text: string; x: number; y: number; colour: string } | null
}

/**
 * A canvas that records instead of painting. Every character is `charWidth`
 * wide, which is the only thing drawLabel's fit test needs from measureText
 * and is far easier to reason about than a real font.
 */
function recorder(charWidth = 6) {
  const boxes: PaintedBox[] = []
  let dashed = false
  const last = () => {
    const box = boxes.at(-1)
    if (box === undefined) throw new Error('a stroke or a label was painted before any fill')
    return box
  }
  const ctx = {
    fillStyle: '',
    strokeStyle: '',
    lineWidth: 0,
    font: '',
    textBaseline: '',
    // A box starts at its fill: that is the first call the renderer makes for it.
    fillRect(x: number, y: number, w: number, h: number) {
      boxes.push({ x, y, w, h, fill: ctx.fillStyle, edge: '', lineWidth: 0, dashed: false, stroke: { x: 0, y: 0, w: 0, h: 0 }, label: null })
    },
    setLineDash(pattern: readonly number[]) {
      dashed = pattern.length > 0
    },
    strokeRect(x: number, y: number, w: number, h: number) {
      const box = last()
      box.edge = ctx.strokeStyle
      box.lineWidth = ctx.lineWidth
      box.dashed = dashed
      box.stroke = { x, y, w, h }
    },
    measureText(text: string) {
      return { width: text.length * charWidth }
    },
    fillText(text: string, x: number, y: number) {
      last().label = { text, x, y, colour: ctx.fillStyle }
    },
  }
  return { ctx, boxes }
}

/**
 * The drawing utils the library hands a primitive. The real setLineStyle sets a
 * dash pattern on the context, so the mock does too — that way the "did this box
 * end up dashed?" question is answered by the context, exactly as on a real canvas,
 * and a box that forgets to reset the pattern is caught rather than excused.
 */
const utils = {
  setLineStyle(ctx: CanvasRenderingContext2D, style: LineStyle) {
    ctx.setLineDash(style === LineStyle.Dashed ? [2, 2] : [])
  },
}

type HarnessOptions = {
  /** null (the default) means the scale reports no range, so nothing is culled. */
  visible?: Range | null
  /** Override the price scale, e.g. to return null for a price it cannot place. */
  priceOf?: (price: number) => number | null
}

function harness(options: HarnessOptions = {}) {
  /** Every logical index the primitive asked about, in order. */
  const asked: number[] = []
  const scale = {
    logicalToCoordinate(index: number) {
      asked.push(index)
      // The trap, modelled exactly: lightweight-charts 5.2.1 opens
      // indexToCoordinate with an isInteger guard and returns 0 — not null, not
      // a coordinate — for anything else (development.mjs:6164). A caller that
      // asks for `fromIndex - 0.5` to widen a box by half a bar therefore gets
      // a silent x=0 and a box stretching off the left of the chart.
      return Number.isInteger(index) ? index * BAR_PX + BAR_PX / 2 : 0
    },
    getVisibleLogicalRange: () => options.visible ?? null,
  }
  const series = { priceToCoordinate: options.priceOf ?? yOf }
  const requestUpdate = vi.fn()

  const primitive = new ZonePrimitive()
  primitive.attached({ chart: { timeScale: () => scale }, series, requestUpdate } as unknown as SeriesAttachedParameter<Time>)

  /**
   * Runs one frame: hand over the zones, let the library's update run, then
   * paint. Returns the boxes that reached the canvas, or null when the view
   * declined to give a renderer at all — which is how the layer switches off.
   */
  function paint(
    zones: readonly Zone[],
    lastIndex = 0,
    frame: { ratio?: number; withUtils?: boolean; charWidth?: number } = {},
  ): PaintedBox[] | null {
    primitive.setZones(zones, lastIndex)
    primitive.updateAllViews()
    const renderer = primitive.paneViews()[0].renderer()
    if (renderer === null) return null
    const ratio = frame.ratio ?? 1
    const { ctx, boxes } = recorder(frame.charWidth)
    const target = {
      useBitmapCoordinateSpace(f: (scope: unknown) => void) {
        f({
          context: ctx,
          horizontalPixelRatio: ratio,
          verticalPixelRatio: ratio,
          mediaSize: { width: 800, height: 400 },
          bitmapSize: { width: 800 * ratio, height: 400 * ratio },
        })
      },
    }
    renderer.drawBackground?.(
      target as unknown as Parameters<NonNullable<typeof renderer.drawBackground>>[0],
      frame.withUtils === false ? undefined : (utils as unknown as Parameters<NonNullable<typeof renderer.drawBackground>>[1]),
    )
    return boxes
  }

  return { primitive, requestUpdate, asked, paint }
}

/** A zone with the boring fields filled in, so each test states only its point. */
function zone(over: Partial<Zone> = {}): Zone {
  return { fromIndex: 3, toIndex: 5, top: 100, bottom: 90, fill: '#0f04', edge: '#0f0', ...over }
}

describe('box geometry', () => {
  it('turns indices and prices into a rect that covers its end candles whole', () => {
    // fromIndex 3 sits at x=35 and toIndex 5 at x=55, and each edge moves out by
    // half a bar: 30..60. Prices 100 and 90 sit at y=200 and y=300.
    const boxes = harness().paint([zone()])
    expect(boxes).toEqual([
      expect.objectContaining({ x: 30, y: 200, w: 31, h: 101, fill: '#0f04', edge: '#0f0' }),
    ])
  })

  it('never asks the time scale for a fractional index, because it would get 0 back', () => {
    // This is the whole reason the half-bar widening happens in pixels after the
    // call rather than as `logicalToCoordinate(fromIndex - 0.5)`. If someone
    // "simplifies" it back, the mock returns 0 exactly as the library does, the
    // box above starts at x=0 instead of x=30, and both of these fail.
    const h = harness()
    h.paint([zone({ fromIndex: 3.4, toIndex: 5.6 })])
    expect(h.asked.every(Number.isInteger)).toBe(true)
    // 3.4 and 5.6 are rounded to 3 and 6 before the call, for the same reason.
    expect(h.asked).toEqual([0, 1, 3, 6])
  })

  it('rounds a fractional anchor to a whole candle', () => {
    const boxes = harness().paint([zone({ fromIndex: 3.4, toIndex: 5.6 })])
    // index 3 → x=35, index 6 → x=65, each widened by half a bar: 30..70.
    expect(boxes?.[0]).toMatchObject({ x: 30, w: 41 })
  })

  it('scales into bitmap pixels and covers both edge pixels', () => {
    // positionsBox rounds each edge into bitmap space and adds 1, so the box is
    // inclusive of both its edges. At 2x: 60..120 across and 400..600 down.
    const boxes = harness().paint([zone()], 0, { ratio: 2 })
    expect(boxes?.[0]).toMatchObject({ x: 60, y: 400, w: 61, h: 201, lineWidth: 2 })
  })

  it('strokes exactly the rect it filled', () => {
    const box = harness().paint([zone()])?.[0]
    expect(box?.stroke).toEqual({ x: box?.x, y: box?.y, w: box?.w, h: box?.h })
  })

  it('does not care which way round top and bottom come', () => {
    // The rect is built from min/abs, so a zone handed its prices inverted paints
    // the same box rather than a negative-height one that strokes nothing.
    const right = harness().paint([zone({ top: 100, bottom: 90 })])
    const wrong = harness().paint([zone({ top: 90, bottom: 100 })])
    expect(wrong?.[0]).toMatchObject({ y: right?.[0].y, h: right?.[0].h })
  })
})

describe('an open zone', () => {
  it('resolves its right edge to the last candle on the chart', () => {
    // toIndex null, lastIndex 7: the right edge lands on index 7 (x=75) widened
    // by half a bar, as if the zone had been handed toIndex 7.
    const open = harness().paint([zone({ toIndex: null })], 7)
    const closed = harness().paint([zone({ toIndex: 7 })], 0)
    expect(open?.[0]).toMatchObject({ x: 30, w: 51 })
    expect(open?.[0].w).toBe(closed?.[0].w)
  })

  it('grows with the chart without its left edge moving', () => {
    // A poll that adds candles re-solves the right edge on the next frame. The
    // left edge is the mark's identity and may never move — see the no-repaint
    // rule in docs/smart-money-concepts.md.
    const h = harness()
    const before = h.paint([zone({ toIndex: null })], 7)?.[0]
    const after = h.paint([zone({ toIndex: null })], 9)?.[0]
    expect(after?.x).toBe(before?.x)
    expect(after?.w).toBe((before?.w ?? 0) + 2 * BAR_PX)
  })

  it('falls back to index 0 before any candle is loaded', () => {
    // setZones has not been told a last index yet, so an open zone collapses onto
    // the first candle rather than painting from index 0 to somewhere undefined.
    const boxes = harness().paint([zone({ fromIndex: 0, toIndex: null })], 0)
    expect(boxes?.[0]).toMatchObject({ x: 0, w: 11 })
  })
})

describe('the visible-range cull', () => {
  const seen = { from: 10, to: 20 }
  const at = (label: string, fromIndex: number, toIndex: number | null) => zone({ label, fromIndex, toIndex })
  const labels = (boxes: PaintedBox[] | null) => (boxes ?? []).map((b) => b.label?.text ?? null)

  it('keeps a zone that touches the range and drops one that does not', () => {
    // Labels are read back off the canvas, so this also proves the surviving
    // boxes were actually painted and not merely built.
    const boxes = harness({ visible: seen }).paint(
      [at('early', 0, 5), at('left', 5, 12), at('in', 12, 15), at('right', 18, 30), at('over', 0, 100), at('late', 40, 50)],
      0,
      { charWidth: 1 },
    )
    expect(labels(boxes)).toEqual(['left', 'in', 'right', 'over'])
  })

  it('draws everything when the scale reports no range', () => {
    // An empty or not-yet-laid-out chart returns null, and null is not "nothing
    // is visible" — culling on it would blank the layer on the first frame.
    const boxes = harness({ visible: null }).paint([at('a', 0, 5), at('b', 40, 50)], 0, { charWidth: 1 })
    expect(labels(boxes)).toEqual(['a', 'b'])
  })

  it('keeps a zone that ends exactly on the edge of the range', () => {
    const boxes = harness({ visible: seen }).paint([at('left', 0, 10), at('right', 20, 30)], 0, { charWidth: 1 })
    expect(labels(boxes)).toEqual(['left', 'right'])
  })

  it('culls an open zone by the last candle, not by its anchor', () => {
    // An order block anchored long before the viewport is still on screen if it
    // is still open, because its right edge tracks the last candle.
    const boxes = harness({ visible: seen }).paint([at('open', 0, null)], 15, { charWidth: 1 })
    expect(labels(boxes)).toEqual(['open'])
  })

  it('culls in logical units and so can clip a sliver at the very edge', () => {
    // The comparison is against the raw indices, before the half-bar widening.
    // A zone ending at index 10 would have reached x=110 while the range starts
    // at 10.4 (x=109), so a pixel of it was on screen and is not drawn. The cost
    // is up to half a bar of a zone edge missing at the extreme edge of the
    // viewport while panning; the gain is that the per-frame cull costs two
    // comparisons and no coordinate maths. Pinned here so a change is deliberate.
    const boxes = harness({ visible: { from: 10.4, to: 20.6 } }).paint([at('sliver', 0, 10)], 0, { charWidth: 1 })
    expect(boxes).toBeNull()
  })
})

describe('degenerate input', () => {
  it('draws nothing at all for an empty zone list', () => {
    // renderer() returning null is how a switched-off layer costs no canvas work.
    expect(harness().paint([])).toBeNull()
  })

  it('draws nothing when every zone is culled', () => {
    expect(harness({ visible: { from: 100, to: 200 } }).paint([zone()])).toBeNull()
  })

  it('still paints a zone with no height', () => {
    // top === bottom is a legitimate mark — a gap closed to nothing. The +1 in
    // positionsBox is what keeps it a one-pixel line rather than an invisible one.
    const boxes = harness().paint([zone({ top: 100, bottom: 100 })])
    expect(boxes?.[0]).toMatchObject({ y: 200, h: 1 })
  })

  it('still paints a zone covering a single candle', () => {
    // One bar wide plus half a bar either side, plus the inclusive edge pixel.
    const boxes = harness().paint([zone({ fromIndex: 4, toIndex: 4 })])
    expect(boxes?.[0]).toMatchObject({ x: 40, w: 11 })
  })

  it('lets a zone anchored before the first loaded bar run off the left', () => {
    // A negative index is a real coordinate to the time scale, so the box is
    // clipped by the canvas rather than dropped or clamped to x=0. Clamping would
    // move a mark that was already drawn, which the module is not allowed to do.
    const boxes = harness().paint([zone({ fromIndex: -4, toIndex: 2 })])
    expect(boxes?.[0]).toMatchObject({ x: -40, w: 71 })
  })

  it('skips a zone the price scale cannot place, and keeps the rest', () => {
    // priceToCoordinate returns null for a price it has no coordinate for. Drawing
    // that box anyway would put it at the top of the chart, which is a lie.
    const priceOf = (price: number) => (price === 999 ? null : yOf(price))
    const boxes = harness({ priceOf }).paint([zone({ label: 'bad', top: 999 }), zone({ label: 'good' })], 0, { charWidth: 1 })
    expect(boxes?.map((b) => b.label?.text)).toEqual(['good'])
  })
})

describe('edges and labels', () => {
  it('states its own dash pattern per box rather than inheriting the last one', () => {
    // The pattern is context state and carries over, so a solid box drawn after a
    // dashed one must clear it. Without the else branch this second box is dashed.
    const boxes = harness().paint([zone({ dashed: true }), zone({ fromIndex: 8, toIndex: 9 })])
    expect(boxes?.map((b) => b.dashed)).toEqual([true, false])
  })

  it('falls back to a solid edge when the library gives no drawing utils', () => {
    // The utils argument is typed optional. A solid edge is the honest fallback:
    // the box is still there, it has only stopped saying it is live.
    const boxes = harness().paint([zone({ dashed: true })], 0, { withUtils: false })
    expect(boxes?.[0].dashed).toBe(false)
  })

  it('never lets the edge thin below one pixel', () => {
    // The ratio has to round DOWN to nothing for this to say anything: JS rounds
    // a half up, so Math.round(0.5) is already 1 and a 0.5 ratio would pass with
    // the Math.max floor deleted. 0.4 rounds to 0, which is the case the floor
    // exists for — an edge asked for at zero width paints nothing at all.
    const boxes = harness().paint([zone()], 0, { ratio: 0.4 })
    expect(boxes?.[0].lineWidth).toBe(1)
  })

  it('writes a caption inside the box, in the edge colour', () => {
    // Padded 3px in from the top-left corner of the box it belongs to.
    const boxes = harness().paint([zone({ label: 'OB' })])
    expect(boxes?.[0].label).toEqual({ text: 'OB', x: 33, y: 203, colour: '#0f0' })
  })

  it('drops a caption too wide for its box', () => {
    // A caption that does not fit spills out and reads as if it belonged to the
    // box beside it, which is worse than no caption. 'OB' needs 12px plus 6px of
    // padding; a single-candle box is 11px wide.
    const boxes = harness().paint([zone({ fromIndex: 4, toIndex: 4, label: 'OB' })])
    expect(boxes?.[0].label).toBeNull()
  })

  it('drops a caption too tall for its box', () => {
    const boxes = harness().paint([zone({ top: 100, bottom: 100, label: 'OB' })])
    expect(boxes?.[0].label).toBeNull()
  })
})

describe('the primitive itself', () => {
  it('hands back the same pane view array every frame', () => {
    // The library caches pane views by array identity; a fresh array each frame
    // would defeat that cache for nothing.
    const { primitive } = harness()
    expect(primitive.paneViews()).toBe(primitive.paneViews())
    expect(primitive.paneViews()[0].zOrder?.()).toBe('normal')
  })

  it('paints nothing in the foreground pass', () => {
    // Everything belongs behind the candles, so draw() is deliberately empty.
    const { primitive, paint } = harness()
    paint([zone()])
    const renderer = primitive.paneViews()[0].renderer()
    const { ctx, boxes } = recorder()
    renderer?.draw(ctx as unknown as Parameters<typeof renderer.draw>[0])
    expect(boxes).toEqual([])
  })

  it('asks for a repaint when the zone set changes', () => {
    const { requestUpdate, paint } = harness()
    paint([zone()])
    expect(requestUpdate).toHaveBeenCalledTimes(1)
  })

  it('lets go of the chart, the series and the repaint hook when detached', () => {
    const { primitive, requestUpdate, paint } = harness()
    paint([zone()])
    primitive.detached()

    expect(primitive.chart).toBeNull()
    expect(primitive.series).toBeNull()
    // Nothing is pushed at a chart that is gone — a stale requestUpdate here is
    // how a detached primitive keeps a destroyed chart alive.
    primitive.setZones([zone()], 9)
    expect(requestUpdate).toHaveBeenCalledTimes(1)
    // The boxes are cleared on the next update rather than inside detached(),
    // which is safe because the library stops driving a detached primitive; this
    // pins that the update does clear them rather than reading the null chart.
    primitive.updateAllViews()
    expect(primitive.paneViews()[0].renderer()).toBeNull()
  })

  it('draws nothing before it is attached', () => {
    const primitive = new ZonePrimitive()
    primitive.setZones([zone()], 5)
    primitive.updateAllViews()
    expect(primitive.paneViews()[0].renderer()).toBeNull()
  })
})
