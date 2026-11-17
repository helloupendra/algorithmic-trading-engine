/**
 * The run story: one run's day from top to bottom, so a trader never needs
 * another screen while it trades.
 *
 *   - a strip of the numbers that matter now: the index and its move, the net
 *     P&L, the premium at work, and what the strategy is doing;
 *   - the index chart at the strategy's own timeframe, with the levels its
 *     entries are read from, every trade's BUY and SELL boxed where it
 *     happened, and, for an open position, its stop, where the trail arms and
 *     where the trail now sells (a TrendRider run's index exits);
 *   - the rules of the day in plain words, ticked against the market;
 *   - the positions, with their stop and trail, P&L and an exit button;
 *   - each trade's option on its own chart;
 *   - the timeline of what the strategy said and why.
 *
 * Everything is read from the run's live view and the bars the platform
 * already records; lib/runStory.ts does the arithmetic.
 */

import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import {
  CandlestickSeries,
  createChart,
  LineStyle,
  type IChartApi,
  type IPriceLine,
  type ISeriesApi,
  type UTCTimestamp,
} from 'lightweight-charts'
import { chartOptions, cssVar } from '../../lib/chartTheme'
import { useTheme } from '../../lib/theme'
import { useClosePositions, useRunDayBars, useStoredCandles } from '../../lib/queries'
import { formatInrSigned, formatInrWhole, formatPercent, formatPrice, pnlClass } from '../../lib/format'
import { Badge, InlineError, Panel, StatTile } from '../../components/ui'
import type { StrategyLiveView } from '../../lib/types'
import {
  adrFrom,
  armEvent,
  candleStart,
  chartTime,
  engineStatus,
  exitPlan,
  foldSession,
  istDay,
  istHm,
  ladderLayout,
  mergeLevels,
  previousClose,
  sessionsFrom,
  tradeAt,
  tradeMarks,
  trendRiderEntries,
  v2Check,
  type Bar,
  type ExitPlan,
  type Level,
  type TradeMark,
  type TrendRiderEntry,
} from '../../lib/runStory'
import './story.css'

/* ------------------------------------------------------------------ chart */

/**
 * A boxed mark on a chart: the candle it sits on (UTC ms of its start), its
 * price and what it says. Boxes never sit on candles: entries go in the empty
 * band under them, exits and the trail arming in the band above.
 */
interface Mark {
  time: number
  price: number
  title: string
  sub?: string
  tone: 'buy' | 'win' | 'loss' | 'flat' | 'arm'
  band: 'top' | 'bottom'
}

interface Placed {
  mark: Mark
  /** The mark's own point: its candle and price. */
  x: number
  y: number
  /** The box: its centre and its top. */
  lx: number
  ly: number
}

interface Rung {
  level: Level
  /** The level's own y, and where its label went on the ladder. */
  y: number
  ly: number
}

/** The share of the pane kept free of candles and levels, above and below, for the boxes. */
const BAND = 0.16
const BOX_H = 36
const BOX_GAP = 6
const RUNG_GAP = 22

function lineStyleOf(kind: Level['kind']): { color: string; style: LineStyle; width: 1 | 2 } {
  switch (kind) {
    case 'prev':
      return { color: cssVar('--text-3'), style: LineStyle.Dashed, width: 1 }
    case 'open':
      return { color: cssVar('--text-2'), style: LineStyle.SparseDotted, width: 1 }
    case 'entry':
      return { color: cssVar('--accent'), style: LineStyle.Solid, width: 2 }
    case 'stop':
      return { color: cssVar('--neg'), style: LineStyle.Solid, width: 2 }
    case 'trail':
      return { color: cssVar('--warn'), style: LineStyle.Solid, width: 2 }
    case 'arm':
      return { color: cssVar('--warn'), style: LineStyle.Dotted, width: 1 }
    case 'best':
      return { color: cssVar('--pos'), style: LineStyle.Dotted, width: 1 }
    case 'check':
      return { color: cssVar('--warn'), style: LineStyle.LargeDashed, width: 1 }
  }
}

/**
 * Candles, their levels and the trades, drawn so nothing hides anything:
 *  - the levels' names and prices stand on a ladder beside the price axis,
 *    each as near its line as the others allow (lib/runStory ladderLayout),
 *    so the axis keeps its own numbers;
 *  - BUY boxes sit in an empty band under the candles and SELL boxes (and the
 *    moment the trail armed) in one above, each tied to its candle by a line;
 *  - a strip above the chart says what is on it now and, under the cursor,
 *    that candle's open, high, low and close.
 * The chart is made once per mount (and theme); data and lines are swapped in
 * place, so a 5-second refresh never resets the trader's zoom.
 */
function StoryChart({
  candles,
  levels,
  marks,
  fitKey,
  height,
  range,
  status,
}: {
  candles: Bar[]
  levels: Level[]
  marks: Mark[]
  fitKey: string
  height: number
  /** The bars (logical indexes) shown when `fitKey` changes; all of them when absent. */
  range?: { from: number; to: number }
  /** What the strip above the chart says (the replay's clock, the strategy's state). */
  status?: ReactNode
}) {
  const boxRef = useRef<HTMLDivElement | null>(null)
  const canvasRef = useRef<HTMLDivElement | null>(null)
  const chartRef = useRef<IChartApi | null>(null)
  const seriesRef = useRef<ISeriesApi<'Candlestick'> | null>(null)
  const linesRef = useRef<IPriceLine[]>([])
  // The levels, for the price scale: a stop below every candle is still drawn.
  const levelPricesRef = useRef<number[]>([])
  levelPricesRef.current = levels.map((l) => l.price).filter((p) => Number.isFinite(p))
  const fittedRef = useRef<string | null>(null)
  // The range to show, read when `fitKey` changes (not on every refresh, which would undo the trader's zoom).
  const rangeRef = useRef(range)
  rangeRef.current = range
  // Chart time → candle, for the strip's open/high/low/close under the cursor.
  const byTimeRef = useRef(new Map<number, Bar>())
  const [placed, setPlaced] = useState<Placed[]>([])
  const [rungs, setRungs] = useState<Rung[]>([])
  const [hover, setHover] = useState<Bar | null>(null)
  const { theme } = useTheme()
  const shown = useMemo(() => mergeLevels(levels, 0.00002), [levels])

  const layout = useCallback(() => {
    const chart = chartRef.current
    const series = seriesRef.current
    const box = boxRef.current
    if (!chart || !series || !box) return
    const pane = chart.paneSize()
    const paneW = pane.width
    const paneH = pane.height
    if (paneW <= 0 || paneH <= 0) return

    // The ladder: every level on screen, as near its own y as the others allow.
    const visible: Array<{ level: Level; y: number }> = []
    for (const level of shown) {
      const yc = series.priceToCoordinate(level.price)
      if (yc == null) continue
      const y: number = yc
      if (y >= 0 && y <= paneH) visible.push({ level, y })
    }
    const ys = ladderLayout(visible.map((r) => r.y), paneH, RUNG_GAP)
    setRungs(visible.map((r, i) => ({ ...r, ly: ys[i] })))

    // The boxes, in the bands the price scale keeps empty.
    const bandH = paneH * BAND
    const rows = Math.max(1, Math.floor((bandH - 4) / (BOX_H + BOX_GAP)))
    const out: Placed[] = []
    const taken: Array<{ l: number; r: number; t: number; b: number }> = []
    for (const mark of [...marks].sort((a, b) => a.time - b.time)) {
      const xc = chart.timeScale().timeToCoordinate(chartTime(mark.time) as UTCTimestamp)
      const yc = series.priceToCoordinate(mark.price)
      if (xc == null || yc == null) continue
      const x: number = xc
      const y: number = yc
      const w = Math.min(230, 6.4 * Math.max(mark.title.length, (mark.sub ?? '').length) + 22)
      const clampX = (cx: number) => Math.min(Math.max(cx, w / 2 + 4), Math.max(w / 2 + 4, paneW - w / 2 - 4))
      const topOf = (row: number) =>
        mark.band === 'top' ? 4 + row * (BOX_H + BOX_GAP) : paneH - 4 - BOX_H - row * (BOX_H + BOX_GAP)
      const hits = (rect: { l: number; r: number; t: number; b: number }) =>
        taken.find((o) => rect.l < o.r + 6 && rect.r > o.l - 6 && rect.t < o.b && rect.b > o.t)
      let lx = clampX(x)
      let ly = topOf(0)
      let rect = { l: lx - w / 2, r: lx + w / 2, t: ly, b: ly + BOX_H }
      let free = false
      for (let row = 0; row < rows && !free; row++) {
        ly = topOf(row)
        rect = { l: lx - w / 2, r: lx + w / 2, t: ly, b: ly + BOX_H }
        free = !hits(rect)
      }
      if (!free) {
        // Every row is taken here: beside the box in the way, on the first row.
        ly = topOf(0)
        for (let k = 0; k < 8; k++) {
          rect = { l: lx - w / 2, r: lx + w / 2, t: ly, b: ly + BOX_H }
          const o = hits(rect)
          if (!o) break
          lx = clampX(o.r + 6 + w / 2)
        }
        rect = { l: lx - w / 2, r: lx + w / 2, t: ly, b: ly + BOX_H }
      }
      taken.push(rect)
      out.push({ mark, x, y, lx, ly })
    }
    setPlaced(out)
  }, [marks, shown])

  useEffect(() => {
    const el = canvasRef.current
    if (!el) return
    const chart = createChart(el, chartOptions(el))
    const series = chart.addSeries(CandlestickSeries, {
      upColor: cssVar('--pos'),
      downColor: cssVar('--neg'),
      wickUpColor: cssVar('--pos'),
      wickDownColor: cssVar('--neg'),
      borderVisible: false,
      // The scale covers the levels as well as the candles, so the stop, the
      // trail and yesterday's range are on screen even when price is far from them.
      autoscaleInfoProvider: (original: () => { priceRange: { minValue: number; maxValue: number } | null } | null) => {
        const res = original()
        const prices = levelPricesRef.current
        if (!res?.priceRange || prices.length === 0) return res
        return {
          ...res,
          priceRange: {
            minValue: Math.min(res.priceRange.minValue, ...prices),
            maxValue: Math.max(res.priceRange.maxValue, ...prices),
          },
        }
      },
    })
    // Empty bands above and below for the boxes; the view holds the whole session's width, so new
    // candles (live, or a replay's) fill it from the left instead of sliding under the trader's eyes.
    chart.applyOptions({
      rightPriceScale: { scaleMargins: { top: BAND, bottom: BAND } },
      timeScale: { shiftVisibleRangeOnNewBar: false },
    })
    chartRef.current = chart
    seriesRef.current = series
    const observer = new ResizeObserver(() => {
      if (el.clientWidth > 0) chart.applyOptions({ width: el.clientWidth, height: el.clientHeight })
      requestAnimationFrame(() => layoutRef.current())
    })
    observer.observe(el)
    const onRange = () => layoutRef.current()
    chart.timeScale().subscribeVisibleLogicalRangeChange(onRange)
    const onMove = (param: { time?: unknown }) => {
      const bar = typeof param.time === 'number' ? byTimeRef.current.get(param.time) ?? null : null
      setHover(bar)
    }
    chart.subscribeCrosshairMove(onMove)
    return () => {
      chart.unsubscribeCrosshairMove(onMove)
      chart.timeScale().unsubscribeVisibleLogicalRangeChange(onRange)
      observer.disconnect()
      chart.remove()
      chartRef.current = null
      seriesRef.current = null
      linesRef.current = []
      fittedRef.current = null
    }
  }, [theme])

  // The latest layout, for the callbacks the chart holds on to.
  const layoutRef = useRef(layout)
  layoutRef.current = layout

  useEffect(() => {
    const chart = chartRef.current
    const series = seriesRef.current
    if (!chart || !series) return
    const byTime = new Map<number, Bar>()
    series.setData(
      candles.map((c) => {
        const time = chartTime(c.t)
        byTime.set(time, c)
        return { time: time as UTCTimestamp, open: c.o, high: c.h, low: c.l, close: c.c }
      }),
    )
    byTimeRef.current = byTime
    if (fittedRef.current !== fitKey && candles.length > 0) {
      fittedRef.current = fitKey
      if (rangeRef.current) chart.timeScale().setVisibleLogicalRange(rangeRef.current)
      else chart.timeScale().fitContent()
    }
    requestAnimationFrame(() => layoutRef.current())
  }, [candles, fitKey, theme])

  useEffect(() => {
    const series = seriesRef.current
    if (!series) return
    for (const line of linesRef.current) series.removePriceLine(line)
    // The lines only: their names and prices are on the ladder, so the axis keeps its own numbers.
    linesRef.current = shown.map((l) => {
      const s = lineStyleOf(l.kind)
      return series.createPriceLine({
        price: l.price,
        color: s.color,
        lineWidth: s.width,
        lineStyle: s.style,
        axisLabelVisible: false,
        title: '',
      })
    })
    requestAnimationFrame(() => layoutRef.current())
  }, [shown, theme])

  useEffect(() => {
    requestAnimationFrame(() => layoutRef.current())
  }, [layout])

  const ohlc = hover ?? candles.at(-1) ?? null
  const move = ohlc ? ohlc.c - ohlc.o : 0

  return (
    <div className="story-chart-wrap">
      <div className="story-chart__status">
        <div className="story-chart__state">{status}</div>
        {ohlc && (
          <div className="story-chart__ohlc mono" aria-live="off">
            <span className="faint">{istHm(ohlc.t)}</span> <b>O</b> {p1(ohlc.o)} <b>H</b> {p1(ohlc.h)} <b>L</b> {p1(ohlc.l)} <b>C</b>{' '}
            <span className={move > 0 ? 'pos' : move < 0 ? 'neg' : undefined}>{p1(ohlc.c)}</span>
          </div>
        )}
      </div>
      <div className="story-chart__body" style={{ ['--h' as string]: `${height}px` }}>
        <div ref={boxRef} className="story-chart">
          <div ref={canvasRef} className="story-chart__canvas" />
          <svg className="story-chart__links" aria-hidden="true">
            {placed.map((p, i) => (
              <g key={i} className={`story-link story-link--${p.mark.tone}`}>
                <line x1={p.x} y1={p.y} x2={p.lx} y2={p.mark.band === 'top' ? p.ly + BOX_H : p.ly} />
                <circle cx={p.x} cy={p.y} r={5} />
              </g>
            ))}
          </svg>
          {placed.map((p, i) => (
            <div
              key={i}
              className={`story-label story-label--${p.mark.tone}`}
              style={{ left: p.lx, top: p.ly, height: BOX_H }}
            >
              <b>{p.mark.title}</b>
              {p.mark.sub && <span>{p.mark.sub}</span>}
            </div>
          ))}
          {candles.length === 0 && <p className="story-chart__empty">No candles recorded for this day yet.</p>}
        </div>
        <div className="story-ladder" aria-label="Levels on the chart">
          <svg className="story-ladder__links" aria-hidden="true">
            {rungs.map((r, i) => (
              <line key={i} className={`story-rung-link story-rung-link--${r.level.kind}`} x1={0} y1={r.y} x2={9} y2={r.ly} />
            ))}
          </svg>
          {rungs.map((r, i) => (
            <div key={i} className={`story-rung story-rung--${r.level.kind}`} style={{ top: r.ly }} title={`${r.level.title}: ${p1(r.level.price)}`}>
              <span className="story-rung__name">{r.level.title}</span>
              <span className="story-rung__short">{r.level.short ?? r.level.title}</span>
              <b>{p1(r.level.price)}</b>
            </div>
          ))}
        </div>
      </div>
    </div>
  )
}

/* ------------------------------------------------------------------ helpers */

function shiftDay(day: string, days: number): string {
  const d = new Date(`${day}T00:00:00Z`)
  d.setUTCDate(d.getUTCDate() + days)
  return d.toISOString().slice(0, 10)
}

const p1 = (v: number | null | undefined) => (v == null ? '—' : v.toLocaleString('en-IN', { maximumFractionDigits: 1, minimumFractionDigits: 1 }))
const pts = (v: number | null | undefined) => (v == null ? '—' : `${v >= 0 ? '+' : '−'}${Math.abs(v).toFixed(0)} pts`)

/** "index trail: NIFTY … gave back …" → "index trail"; the first clause of a close reason. */
function shortReason(text: string | null): string | null {
  if (!text) return null
  const t = text.replace(/^Close [A-Z]{2}: /, '').replace(/^TrendRiderV2 /, '').replace(/^TrendRider /, '')
  const cut = t.search(/[:(]/)
  return (cut > 0 ? t.slice(0, cut) : t).trim().slice(0, 48)
}

function tradeTone(t: TradeMark): Mark['tone'] {
  return t.pnl > 0 ? 'win' : t.pnl < 0 ? 'loss' : 'flat'
}

/* --------------------------------------------------------------- the panel */

interface OpenPlan {
  trade: TradeMark
  entry: TrendRiderEntry
  plan: ExitPlan
}

export function RunStory({ view, runId, canControl }: { view: StrategyLiveView; runId: number; canControl: boolean }) {
  const startedMs = view.startedUtc ? Date.parse(view.startedUtc) : Date.now()
  const day = view.session === 'recap' && view.recapDate ? view.recapDate : istDay(startedMs)
  const isToday = day === istDay(Date.now())
  const live = view.isActive && isToday
  const [minutes, setMinutes] = useState<1 | 5>(5)
  const spotSymbol = view.spotSymbol
  const indexQ = useRunDayBars(spotSymbol, day, isToday, live)
  // Earlier sessions from the stored 5-minute candles: the stored daily candles can lag days behind
  // (on 17 Nov their latest was the 8 Nov Muhurat hour, which then showed as "yesterday").
  const histQ = useStoredCandles(spotSymbol, '5', shiftDay(day, -21), shiftDay(day, -1))

  const bars1 = useMemo(() => foldSession(indexQ.data ?? [], day, 1), [indexQ.data, day])
  const candles = useMemo(() => foldSession(indexQ.data ?? [], day, minutes), [indexQ.data, day, minutes])
  const hist5 = useMemo(
    () => (histQ.data ?? []).map((c) => ({ t: Date.parse(c.timestampUtc), o: c.open, h: c.high, l: c.low, c: c.close })),
    [histQ.data],
  )
  // One bar per FULL session: a Muhurat hour or a day the recorder missed never counts as yesterday.
  const daily = useMemo(() => sessionsFrom(hist5, 5, 60), [hist5])
  const prevSession = useMemo(() => daily.filter((b) => istDay(b.t) < day).at(-1) ?? null, [daily, day])

  // The previous session's candles go before today's: V2 enters off yesterday's high and low.
  // At 5 minutes they are already loaded; at 1 minute that one day is fetched.
  const prevDay = prevSession ? istDay(prevSession.t) : null
  const prev1Q = useStoredCandles(prevDay && minutes === 1 ? spotSymbol : null, '1', prevDay ?? undefined, prevDay ?? undefined)
  const prevCandles = useMemo(() => {
    if (!prevDay) return []
    if (minutes === 5) return foldSession(hist5, prevDay, 5)
    return foldSession(
      (prev1Q.data ?? []).map((c) => ({ t: Date.parse(c.timestampUtc), o: c.open, h: c.high, l: c.low, c: c.close })),
      prevDay,
      1,
    )
  }, [prevDay, minutes, hist5, prev1Q.data])

  const entries = useMemo(() => trendRiderEntries(view.activity ?? []), [view.activity])
  const trades = useMemo(() => tradeMarks(view.positions ?? [], view.activity ?? [], bars1), [view.positions, view.activity, bars1])
  const spot = view.spotLtp ?? bars1.at(-1)?.c ?? null

  /* ---- the replay: the day again, candle by candle, after the market or any time */
  const [replay, setReplay] = useState<{ idx: number; playing: boolean; speed: number } | null>(null)
  // A new timeframe is a new grid of candles: the replay starts over.
  useEffect(() => setReplay(null), [minutes])
  useEffect(() => {
    if (!replay?.playing) return
    const id = window.setInterval(() => {
      setReplay((r) => {
        if (!r) return r
        if (r.idx + 1 >= candles.length) return { ...r, idx: Math.max(0, candles.length - 1), playing: false }
        return { ...r, idx: r.idx + 1 }
      })
    }, Math.max(60, 900 / replay.speed))
    return () => window.clearInterval(id)
  }, [replay?.playing, replay?.speed, candles.length])
  // The replay's clock: the close of the candle it has reached.
  const cursorMs = replay && candles.length > 0 ? candles[Math.min(replay.idx, candles.length - 1)].t + minutes * 60_000 : null

  // The day as it stood at the cursor (or now, out of a replay).
  const bars1At = useMemo(() => (cursorMs == null ? bars1 : bars1.filter((b) => b.t < cursorMs)), [bars1, cursorMs])
  const spotAt = cursorMs == null ? spot : (bars1At.at(-1)?.c ?? null)
  const tradesAt = useMemo(
    () =>
      cursorMs == null
        ? trades
        : trades
            .filter((t) => t.entryMs < cursorMs)
            .map((t) => (t.exitMs != null && t.exitMs < cursorMs ? t : { ...t, open: true, exitMs: null, exitPrice: null })),
    [trades, cursorMs],
  )
  const shownToday = useMemo(() => (cursorMs == null ? candles : candles.filter((c) => c.t < cursorMs)), [candles, cursorMs])
  const chartCandles = useMemo(() => [...prevCandles, ...shownToday], [prevCandles, shownToday])

  const plansOf = useCallback(
    (list: TradeMark[], bars: Bar[], spotNow: number | null): OpenPlan[] => {
      const out: OpenPlan[] = []
      for (const trade of list) {
        if (!trade.open) continue
        const entry = entries.find((e) => e.groupId === trade.groupId)
        if (!entry) continue
        const plan = exitPlan(entry, bars.filter((b) => b.t >= candleStart(entry.atMs, 1)), spotNow)
        if (plan) out.push({ trade, entry, plan })
      }
      return out
    },
    [entries],
  )
  // Now: the positions table and the numbers on top. At the cursor: the chart and the rules panel.
  const plans = useMemo(() => plansOf(trades, bars1, spot), [plansOf, trades, bars1, spot])
  const plansAt = useMemo(() => (cursorMs == null ? plans : plansOf(tradesAt, bars1At, spotAt)), [cursorMs, plans, plansOf, tradesAt, bars1At, spotAt])

  // The day's levels: from the strategy's own signal when it has entered, else from the stored candles.
  const latest = entries.at(-1) ?? null
  const levels = {
    prevHigh: latest?.levels.prevHigh ?? prevSession?.h ?? null,
    prevLow: latest?.levels.prevLow ?? prevSession?.l ?? null,
    dayOpen: latest?.levels.dayOpen ?? candles[0]?.o ?? null,
    adr: latest?.levels.adr ?? adrFrom(daily, day, 10),
  }
  const fromSignal = latest != null
  // The strategy's yesterday against the full day's stored candles. They differ when the strategy's own
  // record of that day was incomplete (16 Nov: the live bars began at 13:49, so V2 read the high as
  // 25,291.1 while the day's high was 25,337.6 at 09:58).
  const fullHigh = prevSession?.h ?? null
  const fullLow = prevSession?.l ?? null
  const highOff = fromSignal && levels.prevHigh != null && fullHigh != null && Math.abs(fullHigh - levels.prevHigh) >= 0.5
  const lowOff = fromSignal && levels.prevLow != null && fullLow != null && Math.abs(fullLow - levels.prevLow) >= 0.5
  const isTrendRider = /^TrendRider/i.test(view.name) || entries.length > 0
  const prevClose = previousClose(daily, day)
  const change = spot != null && prevClose ? (spot / prevClose - 1) * 100 : null
  const openTrades = trades.filter((t) => t.open)
  // The strategy is "in a trade" only with a position it manages; one booked by hand is not its own.
  const engine = engineStatus(
    view.isActive,
    plans.map((p) => p.plan),
    openTrades.filter((t) => !t.manual).length,
    Date.now(),
    '14:30',
    openTrades.filter((t) => t.manual).length,
  )
  // The last closed candle at the strategy's 5-minute grid (live hands a forming one too), at the cursor in a replay.
  const lastClosed = (() => {
    const five = foldSession(indexQ.data ?? [], day, 5)
    const now = cursorMs ?? Date.now()
    const done = live || cursorMs != null ? five.filter((c) => c.t + 5 * 60_000 <= now) : five
    return done.at(-1) ?? null
  })()
  const check = v2Check(levels, lastClosed?.c ?? null)
  // What the strategy was doing at the replay's clock.
  const engineAt =
    cursorMs == null
      ? null
      : engineStatus(
          true,
          plansAt.map((p) => p.plan),
          tradesAt.filter((t) => t.open && !t.manual).length,
          cursorMs,
          '14:30',
          tradesAt.filter((t) => t.open && t.manual).length,
        )

  const chartLevels: Level[] = useMemo(() => {
    const out: Level[] = []
    if (levels.prevHigh != null) out.push({ price: levels.prevHigh, title: 'Yday high', short: 'Y-high', kind: 'prev' })
    if (levels.prevLow != null) out.push({ price: levels.prevLow, title: 'Yday low', short: 'Y-low', kind: 'prev' })
    if (highOff && fullHigh != null) out.push({ price: fullHigh, title: 'Full-day high', short: 'Full hi', kind: 'check' })
    if (lowOff && fullLow != null) out.push({ price: fullLow, title: 'Full-day low', short: 'Full lo', kind: 'check' })
    if (levels.dayOpen != null) out.push({ price: levels.dayOpen, title: 'Day open', short: 'Open', kind: 'open' })
    for (const { plan } of plansAt) {
      out.push({ price: plan.entry, title: 'Entry', kind: 'entry' })
      if (plan.stop != null) out.push({ price: plan.stop, title: plan.activeKind === 'stop' ? 'Stop (active)' : 'Stop', short: 'Stop', kind: 'stop' })
      // The arm level stays once crossed, so the trader still sees where the trail began.
      if (plan.armAt != null) out.push({ price: plan.armAt, title: plan.armed ? 'Trail armed' : 'Trail arms', short: plan.armed ? 'Armed' : 'Arms', kind: 'arm' })
      if (plan.armed && plan.trail != null) out.push({ price: plan.trail, title: 'Trail sells', short: 'Trail', kind: 'trail' })
      if (plan.armed) out.push({ price: plan.bestLevel, title: 'Best', kind: 'best' })
    }
    return out
  }, [levels.prevHigh, levels.prevLow, levels.dayOpen, plansAt, highOff, lowOff, fullHigh, fullLow])

  const indexMarks: Mark[] = useMemo(() => {
    const out: Mark[] = []
    for (const t of tradesAt) {
      if (t.indexAtEntry != null) {
        out.push({
          time: candleStart(t.entryMs, minutes),
          price: t.indexAtEntry,
          title: `BUY ${t.label.split(' · ')[0]}${t.manual ? ' (by hand)' : ''}`,
          sub: `${istHm(t.entryMs)} · ₹${formatPrice(t.entryPrice)}`,
          tone: 'buy',
          band: 'bottom',
        })
      }
      if (!t.open && t.exitMs != null && t.indexAtExit != null) {
        out.push({
          time: candleStart(t.exitMs, minutes),
          price: t.indexAtExit,
          title: `SELL ${formatInrSigned(t.pnl)}`,
          sub: `${istHm(t.exitMs)} · ₹${formatPrice(t.exitPrice)}${shortReason(t.exitReason) ? ` · ${shortReason(t.exitReason)}` : ''}`,
          tone: tradeTone(t),
          band: 'top',
        })
      }
      // The moment the trail armed: from then on the trade could only end in the trail or better.
      const entry = t.manual ? null : entries.find((e) => e.groupId === t.groupId)
      const arm = entry ? armEvent(entry, bars1At) : null
      if (arm && (t.exitMs == null || arm.ms < t.exitMs)) {
        out.push({
          time: candleStart(arm.ms, minutes),
          price: arm.level,
          title: 'Trail armed',
          sub: `${istHm(arm.ms)} · ${p1(arm.level)}`,
          tone: 'arm',
          band: 'top',
        })
      }
    }
    return out
  }, [tradesAt, minutes, entries, bars1At])

  // The chart shows the whole session's width (so a replay fills it left to right), with yesterday before it:
  // all of it at 5 minutes, its last hour and a half at 1 minute.
  const sessionBars = minutes === 5 ? 75 : 375
  // A phone shows yesterday's last two hours (scroll left for the rest), so today's candles keep their width.
  const narrow = typeof window !== 'undefined' && window.innerWidth < 640
  const keepPrev = narrow ? Math.min(minutes === 5 ? 24 : 120, prevCandles.length) : minutes === 5 ? prevCandles.length : Math.min(90, prevCandles.length)
  const chartRange = {
    from: Math.max(0, prevCandles.length - keepPrev),
    to: prevCandles.length + Math.max(candles.length, sessionBars) + 2,
  }

  const close = useClosePositions()
  const exitTrade = (t: TradeMark) => {
    if (t.positionIds.length === 0) return
    if (!window.confirm(`Sell ${t.label} now at the market (paper)?`)) return
    close.mutate({ runId, positionIds: t.positionIds })
  }

  const entriesToday = trades.length
  const exitsShown = isTrendRider && latest

  return (
    <section className="story" aria-label="Run story">
      {/* ---------------------------------------------------- the numbers */}
      <div className="stat-grid story__kpis">
        <StatTile
          label={`${view.underlying ?? 'Index'} ${live ? 'live' : 'last'}`}
          value={formatPrice(spot)}
          sub={change != null ? <span className={pnlClass(change)}>{formatPercent(change)} vs yesterday</span> : undefined}
        />
        <StatTile
          label="Net P&L (realized + open)"
          value={formatInrSigned(view.pnl.total)}
          tone={view.pnl.total > 0 ? 'pos' : view.pnl.total < 0 ? 'neg' : undefined}
          sub={`realized ${formatInrSigned(view.pnl.realized)} · open ${formatInrSigned(view.pnl.unrealized)}`}
        />
        <StatTile
          label="Premium at work"
          value={formatInrWhole(view.pnl.premiumOutlay ?? view.pnl.capitalUsed ?? 0)}
          sub={`${openTrades.length} open · ${view.lots ?? '—'} lots a trade`}
        />
        <StatTile label="Strategy now" value={<span className="story__engine">{engine.label}</span>} sub={engine.detail ?? undefined} tone={engine.tone === 'pos' ? 'pos' : engine.tone === 'warn' ? 'warn' : engine.tone === 'accent' ? 'accent' : undefined} />
        <StatTile label="Trades today" value={entriesToday} sub={`${openTrades.length} open · ${entriesToday - openTrades.length} closed`} />
      </div>

      {/* ------------------------------------------- the index, full width */}
        <Panel
          title={`${view.underlying ?? 'Index'} · ${minutes}-minute · ${prevDay ? 'yesterday + ' : ''}${day}`}
          actions={
            <div className="story__controls">
              {replay ? (
                <>
                  <button
                    type="button"
                    className="btn btn--sm"
                    onClick={() =>
                      setReplay((r) => r && { ...r, playing: !r.playing, idx: !r.playing && r.idx >= candles.length - 1 ? 0 : r.idx })
                    }
                  >
                    {replay.playing ? '❚❚ Pause' : '▶ Play'}
                  </button>
                  <div className="seg" role="group" aria-label="Replay speed">
                    {[1, 2, 5, 10].map((s) => (
                      <button
                        key={s}
                        type="button"
                        className={`seg__btn${replay.speed === s ? ' is-active' : ''}`}
                        aria-pressed={replay.speed === s}
                        onClick={() => setReplay((r) => r && { ...r, speed: s })}
                      >
                        {s}×
                      </button>
                    ))}
                  </div>
                  <input
                    className="story__scrub"
                    type="range"
                    min={0}
                    max={Math.max(0, candles.length - 1)}
                    value={Math.min(replay.idx, Math.max(0, candles.length - 1))}
                    onChange={(e) => setReplay((r) => r && { ...r, idx: Number(e.target.value), playing: false })}
                    aria-label="Replay time"
                  />
                  <button type="button" className="btn btn--sm btn--ghost" onClick={() => setReplay(null)}>
                    Live
                  </button>
                </>
              ) : (
                <button
                  type="button"
                  className="btn btn--sm"
                  disabled={candles.length === 0}
                  onClick={() => setReplay({ idx: 0, playing: true, speed: 2 })}
                  title="Play the day again, candle by candle: the trades, the stop and the trail as they happened"
                >
                  ▶ Replay the day
                </button>
              )}
              <div className="seg" role="group" aria-label="Timeframe">
                {([1, 5] as const).map((m) => (
                  <button
                    key={m}
                    type="button"
                    className={`seg__btn${minutes === m ? ' is-active' : ''}`}
                    aria-pressed={minutes === m}
                    onClick={() => setMinutes(m)}
                  >
                    {m}m
                  </button>
                ))}
              </div>
            </div>
          }
        >
          {indexQ.isError && <InlineError error={indexQ.error} />}
          <StoryChart
            candles={chartCandles}
            levels={chartLevels}
            marks={indexMarks}
            fitKey={`${spotSymbol}|${day}|${minutes}|${replay ? 'replay' : 'live'}|${prevCandles.length > 0}`}
            range={chartRange}
            height={560}
            status={
              cursorMs != null ? (
                <>
                  <Badge tone="accent">Replay · {istHm(cursorMs)} IST</Badge>
                  {engineAt && <span className="story__state">{engineAt.label}{engineAt.detail ? ` · ${engineAt.detail}` : ''}</span>}
                </>
              ) : (
                <>
                  {live ? <Badge tone="live">Live</Badge> : <Badge>{day}</Badge>}
                  <span className="story__state">{engine.label}{engine.detail ? ` · ${engine.detail}` : ''}</span>
                </>
              )
            }
          />
          <ul className="story__legend">
            <li><i className="lg lg--prev" /> Yesterday's high / low (as the strategy read them)</li>
            {(highOff || lowOff) && <li><i className="lg lg--check" /> Yesterday from the full day's candles</li>}
            <li><i className="lg lg--open" /> Day open</li>
            <li><i className="lg lg--entry" /> Entry</li>
            <li><i className="lg lg--stop" /> Stop</li>
            <li><i className="lg lg--arm" /> Trail arms here (stays once armed)</li>
            <li><i className="lg lg--trail" /> Trail: sells here</li>
            <li><i className="lg lg--best" /> Best since entry</li>
          </ul>
        </Panel>

      {/* ------------------------------------------------- each option */}
      {/* Right under the index: the same replay button plays both, so a trade's option is watched with its index. */}
      {[...trades].sort((a, b) => Number(b.open) - Number(a.open) || b.entryMs - a.entryMs).map((t) => (
        <OptionPanel
          key={t.groupId}
          trade={t}
          day={day}
          isToday={isToday}
          live={live}
          risk={view.risk?.leg ?? null}
          cursorMs={cursorMs}
        />
      ))}

      {/* ----------------------------------- the rules and what it said */}
      <div className="story__grid">
        <Panel title={isTrendRider ? `How ${view.name} trades today` : 'Today'}>
          <div className="story__rules">
            {isTrendRider ? (
              <>
                <p className="small-note">
                  From {latest?.levelsKind === 'opening_range' ? '09:45, a 5-minute close beyond the first 30 minutes\' range' : "09:15, a 5-minute close above yesterday's high and today's open"} buys a call one strike in the money; below
                  yesterday's low and the open, the put.{' '}
                  {latest
                    ? `It sells when the index goes ${latest.stopAdr} × ADR against the entry, or — once it is ${latest.armAdr} × ADR in favour — gives back ${latest.giveAdr} × ADR from its best, or when a candle closes back across the day's open.`
                    : 'It sells on its index stop, its trail, or a candle closing back across the day\'s open.'}{' '}
                  No fixed target. Up to 3 entries a day, the last at 14:30.
                </p>
                <table className="story__kv">
                  <tbody>
                    <tr><th>Yesterday's high</th><td className="num">{p1(levels.prevHigh)}</td></tr>
                    <tr><th>Yesterday's low</th><td className="num">{p1(levels.prevLow)}</td></tr>
                    <tr><th>Day open</th><td className="num">{p1(levels.dayOpen)}</td></tr>
                    <tr><th>ADR (10 days)</th><td className="num">{levels.adr != null ? `${levels.adr.toFixed(1)} pts` : '—'}</td></tr>
                    <tr>
                      <th>Last 5m close{lastClosed ? ` (${istHm(lastClosed.t + 5 * 60_000)})` : ''}</th>
                      <td className="num">{p1(lastClosed?.c)}</td>
                    </tr>
                  </tbody>
                </table>
                {!fromSignal && <p className="small-note faint">Levels from the stored candles until the strategy's first signal states its own.</p>}
                {(highOff || lowOff) && (
                  <p className="small-note warn" role="note">
                    The strategy read yesterday ({prevDay}) as {highOff ? <>high <b>{p1(levels.prevHigh)}</b></> : null}
                    {highOff && lowOff ? ' and ' : ''}
                    {lowOff ? <>low <b>{p1(levels.prevLow)}</b></> : null}; the full day's stored candles say{' '}
                    {highOff ? <>high <b>{p1(fullHigh)}</b></> : null}
                    {highOff && lowOff ? ' and ' : ''}
                    {lowOff ? <>low <b>{p1(fullLow)}</b></> : null}. Its own record of that day was incomplete.
                  </p>
                )}
                <ul className="story__checks">
                  <li className={check.aboveHigh ? 'ok' : ''}>{check.aboveHigh ? '✓' : '·'} Close above yesterday's high</li>
                  <li className={check.aboveOpen ? 'ok' : ''}>{check.aboveOpen ? '✓' : '·'} Close above the day's open</li>
                  <li className={check.belowLow ? 'ok' : ''}>{check.belowLow ? '✓' : '·'} Close below yesterday's low</li>
                  <li className={check.belowOpen ? 'ok' : ''}>{check.belowOpen ? '✓' : '·'} Close below the day's open</li>
                </ul>
                <p className="story__side">
                  {check.side === 'BUY' ? <Badge tone="pos">Call side is live</Badge> : check.side === 'SELL' ? <Badge tone="neg">Put side is live</Badge> : <Badge>No side yet</Badge>}
                </p>
                {plansAt.map(({ trade, plan }) => (
                  <div key={trade.groupId} className="story__plan">
                    <h3>{trade.label}</h3>
                    <table className="story__kv">
                      <tbody>
                        <tr><th>Entry (index)</th><td className="num">{p1(plan.entry)}</td></tr>
                        <tr className="neg"><th>Stop</th><td className="num">{p1(plan.stop)} {plan.stop != null && spotAt != null ? <span className="faint">({pts(plan.direction === 'BUY' ? spotAt - plan.stop : plan.stop - spotAt)} away)</span> : null}</td></tr>
                        <tr><th>Trail arms at</th><td className="num">{p1(plan.armAt)} {plan.armed ? <Badge tone="pos">armed</Badge> : null}</td></tr>
                        <tr><th>Best so far</th><td className="num">{p1(plan.bestLevel)} <span className="faint">({pts(plan.best)})</span></td></tr>
                        <tr className="warn"><th>Trail sells at</th><td className="num">{plan.trail != null ? p1(plan.trail) : 'not armed yet'}</td></tr>
                        <tr>
                          <th>Room before it sells</th>
                          <td className={`num ${plan.room != null && plan.room < 25 ? 'neg' : ''}`}>{plan.room != null ? `${plan.room.toFixed(1)} pts` : '—'}</td>
                        </tr>
                      </tbody>
                    </table>
                  </div>
                ))}
                {plansAt.length > 0 && (
                  <p className="small-note faint">
                    Best, trail and room are worked out here from 1-minute highs and lows; the strategy checks every tick, so its own figures can differ by a few points.
                  </p>
                )}
              </>
            ) : (
              <p className="small-note">
                This strategy does not publish its levels yet; its trades are marked on the chart and listed below.
              </p>
            )}
          </div>
        </Panel>

        {/* ------------------------------------------------------- timeline */}
        <Panel title="What the strategy said">
          {(view.activity ?? []).length === 0 ? (
            <p className="empty">Nothing yet.</p>
          ) : (
            <ol className="story__timeline">
              {[...(view.activity ?? [])]
                .sort((a, b) => Date.parse(a.atUtc) - Date.parse(b.atUtc))
                .map((a, i) => (
                  <li key={i}>
                    <span className="story__time mono">{istHm(Date.parse(a.atUtc))}</span>
                    <Badge tone={a.type === 'OPEN_GROUP' ? 'accent' : a.type === 'CLOSE_GROUP' ? 'warn' : 'neutral'}>
                      {a.type === 'OPEN_GROUP' ? 'BUY' : a.type === 'CLOSE_GROUP' ? 'SELL' : a.type.replace(/_/g, ' ').toLowerCase()}
                    </Badge>
                    <span className="story__text">{a.text}</span>
                  </li>
                ))}
            </ol>
          )}
        </Panel>
      </div>

      {/* ------------------------------------------------------ positions */}
      <Panel title="Positions today">
        {trades.length === 0 ? (
          <p className="empty">No trades yet{live ? ' — the strategy is watching the market.' : '.'}</p>
        ) : (
          <div className="tablewrap">
            <table className="table">
              <thead>
                <tr>
                  <th>Instrument</th>
                  <th className="r">Lots · qty</th>
                  <th>Status</th>
                  <th>Entry time</th>
                  <th className="r">Entry ₹</th>
                  <th className="r">LTP / exit ₹</th>
                  {exitsShown && <th className="r">Stop (index)</th>}
                  {exitsShown && <th className="r">Trail (index)</th>}
                  <th className="r">P&amp;L ₹</th>
                  <th className="r">P&amp;L %</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {trades.map((t) => {
                  const plan = plans.find((p) => p.trade.groupId === t.groupId)?.plan ?? null
                  return (
                    <tr key={t.groupId}>
                      <td>
                        <b>{t.label}</b>
                        {t.manual && <> <Badge tone="warn">booked by hand</Badge></>}
                      </td>
                      <td className="num">{t.lots} · {t.qty}</td>
                      <td>{t.open ? <Badge tone="live">open</Badge> : <Badge>closed {t.exitMs ? istHm(t.exitMs) : ''}</Badge>}</td>
                      <td>{istHm(t.entryMs)}</td>
                      <td className="num">{formatPrice(t.entryPrice)}</td>
                      <td className="num">{formatPrice(t.open ? t.ltp : t.exitPrice)}</td>
                      {exitsShown && <td className="num">{plan ? p1(plan.stop) : t.manual ? 'none' : '—'}</td>}
                      {exitsShown && (
                        <td className="num">
                          {plan ? (plan.trail != null ? <b className="warn">{p1(plan.trail)}</b> : <span className="faint">arms {p1(plan.armAt)}</span>) : t.manual ? 'none' : '—'}
                        </td>
                      )}
                      <td className={`num ${pnlClass(t.pnl)}`}><b>{formatInrSigned(t.pnl)}</b></td>
                      <td className={`num ${pnlClass(t.pnlPercent)}`}>{formatPercent(t.pnlPercent, 1)}</td>
                      <td className="r">
                        {t.open && canControl && view.isActive && (
                          <button type="button" className="btn btn--sm btn--danger" disabled={close.isPending} onClick={() => exitTrade(t)}>
                            Exit
                          </button>
                        )}
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
        )}
        {close.isError && <InlineError error={close.error} />}
        {trades.some((t) => t.manual) && (
          <p className="small-note">"Booked by hand": entered in the ledger on the owner's request, not a live fill. The strategy does not manage it; it closes at the run's square-off or by Exit.</p>
        )}
      </Panel>
    </section>
  )
}

/* ------------------------------------------------------- one option's chart */

function OptionPanel({
  trade,
  day,
  isToday,
  live,
  risk,
  cursorMs,
}: {
  trade: TradeMark
  day: string
  isToday: boolean
  live: boolean
  risk: { targetPoints?: number | null; stopLossPoints?: number | null; targetPercent?: number | null; stopLossPercent?: number | null } | null
  /** The replay's clock (the index chart's), or null: then the option shows the whole day so far. */
  cursorMs: number | null
}) {
  const barsQ = useRunDayBars(trade.symbol, day, isToday, live && trade.open)
  const all = useMemo(() => foldSession(barsQ.data ?? [], day, 1), [barsQ.data, day])
  const candles = useMemo(() => (cursorMs == null ? all : all.filter((c) => c.t < cursorMs)), [all, cursorMs])
  const at = useMemo(() => tradeAt(trade, all, cursorMs), [trade, all, cursorMs])
  const replaying = cursorMs != null

  const levels: Level[] = useMemo(() => {
    if (!at.bought) return []
    const out: Level[] = [{ price: trade.entryPrice, title: 'Bought', kind: 'entry' }]
    const target =
      risk?.targetPoints != null ? trade.entryPrice + risk.targetPoints : risk?.targetPercent != null ? trade.entryPrice * (1 + risk.targetPercent / 100) : null
    const stop =
      risk?.stopLossPoints != null ? trade.entryPrice - risk.stopLossPoints : risk?.stopLossPercent != null ? trade.entryPrice * (1 - risk.stopLossPercent / 100) : null
    if (target != null) out.push({ price: target, title: 'Target', kind: 'best' })
    if (stop != null) out.push({ price: stop, title: 'Premium SL', short: 'SL', kind: 'stop' })
    return out
  }, [at.bought, trade.entryPrice, risk])
  const marks: Mark[] = useMemo(() => {
    const out: Mark[] = []
    if (at.bought) {
      out.push({ time: candleStart(trade.entryMs, 1), price: trade.entryPrice, title: 'BUY', sub: `${istHm(trade.entryMs)} · ₹${formatPrice(trade.entryPrice)}`, tone: 'buy', band: 'bottom' })
    }
    if (at.sold && trade.exitMs != null && trade.exitPrice != null) {
      out.push({
        time: candleStart(trade.exitMs, 1),
        price: trade.exitPrice,
        title: `SELL ${formatInrSigned(trade.pnl)}`,
        sub: `${istHm(trade.exitMs)} · ₹${formatPrice(trade.exitPrice)}`,
        tone: tradeTone(trade),
        band: 'top',
      })
    }
    return out
  }, [at.bought, at.sold, trade])
  // In a replay the option fills the session's width left to right, like the index above it.
  const range = replaying ? { from: 0, to: Math.max(all.length, 375) + 2 } : undefined
  const state = !at.bought ? <Badge>not bought yet</Badge> : at.sold ? <Badge>closed</Badge> : <Badge tone="live">open</Badge>

  return (
    <Panel
      title={
        <>
          {trade.label} {state} {trade.manual && <Badge tone="warn">booked by hand</Badge>}
        </>
      }
      actions={
        at.bought ? (
          <span className={`story__optpnl ${pnlClass(at.pnl)}`}>
            {formatInrSigned(at.pnl)} <span className="faint">({formatPercent(at.pnlPercent, 1)})</span>
          </span>
        ) : undefined
      }
    >
      <StoryChart
        candles={candles}
        levels={levels}
        marks={marks}
        fitKey={`${trade.symbol}|${day}|${replaying ? 'replay' : 'live'}`}
        range={range}
        height={380}
        status={
          replaying ? (
            <>
              <Badge tone="accent">Replay · {istHm(cursorMs)} IST</Badge>
              {at.price != null && <span className="story__state">₹{formatPrice(at.price)}</span>}
            </>
          ) : undefined
        }
      />
      <p className="small-note">
        {!at.bought
          ? `Bought at ${istHm(trade.entryMs)} — the replay has not reached it yet.`
          : (
            <>
              {trade.lots} lots × {trade.qty / Math.max(1, trade.lots)} = {trade.qty} · bought {istHm(trade.entryMs)} at ₹{formatPrice(trade.entryPrice)}
              {trade.indexAtEntry != null ? ` (index ${p1(trade.indexAtEntry)})` : ''}
              {at.sold
                ? ` · sold ${trade.exitMs ? istHm(trade.exitMs) : ''} at ₹${formatPrice(trade.exitPrice)}${trade.exitReason ? ` — ${trade.exitReason}` : ''}`
                : ` · ${replaying ? 'then' : 'now'} ₹${formatPrice(at.price)}`}
            </>
          )}
      </p>
    </Panel>
  )
}
