/**
 * Zone boxes painted behind the candles: one primitive that draws every box in
 * a single pass, rather than one primitive — or a pair of line series — per box.
 *
 * It knows nothing about market structure. A zone is a rectangle over a range
 * of candles at a range of prices, with its colours already resolved by the
 * caller, so the reading that decides what a box *means* stays in one place and
 * this file stays testable on its own.
 *
 * Why zOrder 'normal' with everything painted in drawBackground(), which reads
 * backwards at first glance: lightweight-charts paints the main canvas as
 * background, then 'bottom' primitives, then the grid, then series together
 * with 'normal' primitives, then labels — and within each of those passes it
 * runs drawBackground across every source before it runs drawForeground across
 * every source. drawBackground is implemented only by the two primitive
 * wrappers; no built-in series renderer defines it. So a 'normal' primitive
 * that paints in drawBackground lands after the grid and before every candle,
 * which is where a zone belongs. Choosing 'bottom' also puts the fill behind
 * the candles, but behind the grid as well, so grid lines print on top of the
 * fill and dirty its colour.
 *
 * That ordering was read out of the installed build rather than taken on trust
 * (lightweight-charts 5.2.1, dist/lightweight-charts.development.mjs:9891-9899
 * for the pass order, :9961-9970 for background-before-foreground, :3154 and
 * :3222 for the only two drawBackground implementations in the library). It is
 * not stated in the public documentation, which makes it observed behaviour
 * and not a promise: on a lightweight-charts upgrade, re-read those four places
 * before trusting that zones still sit where this file says they do.
 */

import {
  LineStyle,
  type DrawingUtils,
  type IChartApiBase,
  type IPrimitivePaneRenderer,
  type IPrimitivePaneView,
  type ISeriesApi,
  type ISeriesPrimitive,
  type Logical,
  type PrimitivePaneViewZOrder,
  type SeriesAttachedParameter,
  type SeriesType,
  type Time,
} from 'lightweight-charts'

/**
 * One rectangle, in the caller's terms: logical candle indices along the time
 * axis and prices up the price axis.
 */
export type Zone = {
  /** Logical index of the first candle the zone covers. */
  fromIndex: number
  /**
   * Logical index of the last candle it covers, or null for a zone that is
   * still open — the right edge then tracks the last candle on the chart.
   */
  toIndex: number | null
  top: number
  bottom: number
  /** Resolved colours. Never a CSS variable: nothing is looked up while painting. */
  fill: string
  edge: string
  /** A dashed edge, for a zone that is still live rather than settled. */
  dashed?: boolean
  /** A short caption inside the box, drawn only when the box is big enough to hold it. */
  label?: string
}

/**
 * The canvas wrapper's type lives in fancy-canvas, which is lightweight-charts'
 * own dependency and not one of ours. Naming it would mean importing from a
 * package this app never installed, so it is read off the interface instead.
 */
type RenderTarget = Parameters<IPrimitivePaneRenderer['draw']>[0]

/** A span in bitmap pixels: where it starts and how long it is. */
type Position = { position: number; length: number }

/** A zone once it is in screen coordinates, ready to paint. */
type Box = {
  x1: number
  x2: number
  y1: number
  y2: number
  fill: string
  edge: string
  dashed: boolean
  label?: string
}

const LABEL_FONT_PX = 10
const LABEL_PAD_PX = 3

/**
 * Copied from @tradingview/lwc-toolkit (Apache-2.0) rather than depended on: it
 * is eleven lines and the package was first published days ago, which is a poor
 * trade for one function.
 *
 * Rounds a pair of media-space coordinates into bitmap space and gives the span
 * between them. The +1 is not an off-by-one — it covers both edge pixels, so a
 * box that should be one pixel wide is one pixel wide.
 */
function positionsBox(position1Media: number, position2Media: number, pixelRatio: number): Position {
  const scaledPosition1 = Math.round(pixelRatio * position1Media)
  const scaledPosition2 = Math.round(pixelRatio * position2Media)
  return {
    position: Math.min(scaledPosition1, scaledPosition2),
    length: Math.abs(scaledPosition2 - scaledPosition1) + 1,
  }
}

function drawLabel(
  ctx: CanvasRenderingContext2D,
  text: string,
  colour: string,
  horizontal: Position,
  vertical: Position,
  pixelRatio: number,
) {
  const size = Math.round(LABEL_FONT_PX * pixelRatio)
  const pad = Math.round(LABEL_PAD_PX * pixelRatio)
  ctx.font = `${size}px sans-serif`
  ctx.textBaseline = 'top'
  // A caption that does not fit is worse than no caption: it spills out of its
  // own box and reads as if it belonged to the one beside it.
  if (ctx.measureText(text).width + pad * 2 > horizontal.length) return
  if (size + pad * 2 > vertical.length) return
  ctx.fillStyle = colour
  ctx.fillText(text, horizontal.position + pad, vertical.position + pad)
}

class ZoneRenderer implements IPrimitivePaneRenderer {
  private boxes: readonly Box[]

  constructor(boxes: readonly Box[]) {
    this.boxes = boxes
  }

  /**
   * Required by the interface and deliberately empty: everything this primitive
   * draws belongs behind the candles, and that is the background pass.
   */
  draw() {}

  drawBackground(target: RenderTarget, utils?: DrawingUtils) {
    target.useBitmapCoordinateSpace((scope) => {
      const ctx = scope.context
      for (const box of this.boxes) {
        const horizontal = positionsBox(box.x1, box.x2, scope.horizontalPixelRatio)
        const vertical = positionsBox(box.y1, box.y2, scope.verticalPixelRatio)

        ctx.fillStyle = box.fill
        ctx.fillRect(horizontal.position, vertical.position, horizontal.length, vertical.length)

        ctx.strokeStyle = box.edge
        ctx.lineWidth = Math.max(1, Math.round(scope.verticalPixelRatio))
        // The dash pattern carries over from the previous box, so every box
        // states its own edge rather than inheriting the one before it. The
        // library derives the pattern from lineWidth, which is what keeps a
        // dashed edge looking the same on a 2x display. Its type marks utils
        // optional; if it ever is missing, a solid edge is the honest fallback
        // — the box is still there, it has only stopped saying it is live.
        if (box.dashed && utils) utils.setLineStyle(ctx, LineStyle.Dashed)
        else ctx.setLineDash([])
        ctx.strokeRect(horizontal.position, vertical.position, horizontal.length, vertical.length)

        // The label is painted in the same background pass as the fill, so a
        // candle drawn over it wins. That is the right way round: a mark may
        // never hide the price it is a reading of.
        if (box.label) drawLabel(ctx, box.label, box.edge, horizontal, vertical, scope.verticalPixelRatio)
      }
    })
  }
}

class ZonePaneView implements IPrimitivePaneView {
  private source: ZonePrimitive
  private boxes: Box[] = []
  /** Stable, because update() rewrites the array in place rather than replacing it. */
  private painter = new ZoneRenderer(this.boxes)

  constructor(source: ZonePrimitive) {
    this.source = source
  }

  zOrder(): PrimitivePaneViewZOrder {
    return 'normal'
  }

  /**
   * null draws nothing at all, which is how a layer switches off: the caller
   * hands over an empty zone list and no canvas work happens.
   */
  renderer(): IPrimitivePaneRenderer | null {
    return this.boxes.length > 0 ? this.painter : null
  }

  update() {
    const chart = this.source.chart
    const series = this.source.series
    this.boxes.length = 0
    if (chart === null || series === null || this.source.zones.length === 0) return

    const scale = chart.timeScale()
    const x0 = scale.logicalToCoordinate(0 as Logical)
    const x1 = scale.logicalToCoordinate(1 as Logical)
    if (x0 === null || x1 === null) return
    // A box covers its end candles whole, so each edge moves out by half a bar
    // — and that has to happen in pixels, after the call. logicalToCoordinate
    // returns 0 (not null, not a coordinate) for any index that is not an
    // integer, so asking it for fromIndex - 0.5 silently pins the left edge to
    // x=0 and the box stretches away off the left of the chart. Verified in
    // 5.2.1: indexToCoordinate opens with an isInteger guard, development.mjs
    // :6164. The indices themselves are rounded below for the same reason.
    const half = (x1 - x0) / 2

    const seen = scale.getVisibleLogicalRange()
    for (const zone of this.source.zones) {
      // An open zone resolves its right edge here, from the time scale, on
      // every frame — which is how it follows the last candle without anyone
      // pushing it new data. Its identity and its left edge never move, so
      // this is not a repaint; it is the same open-ended convention the
      // inducement line already draws with.
      const to = zone.toIndex ?? this.source.lastIndex
      // Culled before any coordinate maths. This loop runs on every pan and
      // every zoom frame, and it is where the per-frame cost of the layer is.
      if (seen !== null && (to < seen.from || zone.fromIndex > seen.to)) continue

      const left = scale.logicalToCoordinate(Math.round(zone.fromIndex) as Logical)
      const right = scale.logicalToCoordinate(Math.round(to) as Logical)
      const top = series.priceToCoordinate(zone.top)
      const bottom = series.priceToCoordinate(zone.bottom)
      if (left === null || right === null || top === null || bottom === null) continue

      this.boxes.push({
        x1: left - half,
        x2: right + half,
        y1: top,
        y2: bottom,
        fill: zone.fill,
        edge: zone.edge,
        dashed: zone.dashed === true,
        label: zone.label,
      })
    }
  }
}

export class ZonePrimitive implements ISeriesPrimitive<Time> {
  chart: IChartApiBase<Time> | null = null
  series: ISeriesApi<SeriesType, Time> | null = null
  zones: readonly Zone[] = []
  /** The index an open zone runs to: the last candle the chart holds. */
  lastIndex = 0

  private view = new ZonePaneView(this)
  /**
   * Built once and handed back unchanged. The library caches pane views by
   * array identity, so returning a fresh [this.view] each frame would defeat
   * that cache for nothing.
   */
  private views: readonly IPrimitivePaneView[] = [this.view]
  private requestUpdate: (() => void) | null = null

  attached(param: SeriesAttachedParameter<Time>) {
    this.chart = param.chart
    this.series = param.series
    this.requestUpdate = param.requestUpdate
  }

  detached() {
    this.chart = null
    this.series = null
    this.requestUpdate = null
  }

  /**
   * Replace the whole zone set. Needed only when the set itself changes — a
   * zone appears, a zone closes — and not on every poll, because the open
   * zones re-solve their right edge from the time scale as the chart paints.
   */
  setZones(zones: readonly Zone[], lastIndex: number) {
    this.zones = zones
    this.lastIndex = lastIndex
    this.requestUpdate?.()
  }

  updateAllViews() {
    this.view.update()
  }

  paneViews(): readonly IPrimitivePaneView[] {
    return this.views
  }

  // autoscaleInfo is deliberately not implemented. It would widen the price
  // scale to take in a zone sitting far from the current price — an order block
  // left behind by a long move — and the candles would be squashed to make room
  // for a box. Price sets the scale; the marks live inside whatever it gives.
}
