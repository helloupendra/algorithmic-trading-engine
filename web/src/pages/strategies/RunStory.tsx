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
  candleStart,
  chartTime,
  engineStatus,
  exitPlan,
  foldSession,
  istDay,
  istHm,
  mergeLevels,
  previousClose,
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

/** A boxed mark on a chart: the candle it sits on (UTC ms of its start), its price and what it says. */
interface Mark {
  time: number
  price: number
  title: string
  sub?: string
  tone: 'buy' | 'win' | 'loss' | 'flat'
  above: boolean
}

interface Placed {
  mark: Mark
  x: number
  y: number
  lx: number
  ly: number
}

const LABEL_GAP = 16
const LABEL_ROW = 38
const LABEL_H = 34

function lineStyleOf(kind: Level['kind']): { color: string; style: LineStyle; width: 1 | 2 } {
  switch (kind) {
    case 'prev':
      return { color: cssVar('--text-3'), style: LineStyle.Dashed, width: 1 }
    case 'open':
      return { color: cssVar('--line-strong'), style: LineStyle.Dashed, width: 1 }
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
  }
}

/**
 * Candles with price lines and boxed marks. The chart is made once per mount
 * (and per theme); data and lines are swapped in place so a 5-second refresh
 * never resets the trader's zoom. The marks are HTML laid over the canvas and
 * re-placed whenever the view scrolls, zooms or resizes; labels that would
 * collide are stacked, never drawn over each other.
 */
function StoryChart({
  candles,
  levels,
  marks,
  fitKey,
  height,
}: {
  candles: Bar[]
  levels: Level[]
  marks: Mark[]
  fitKey: string
  height: number
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
  const [placed, setPlaced] = useState<Placed[]>([])
  const { theme } = useTheme()

  const layout = useCallback(() => {
    const chart = chartRef.current
    const series = seriesRef.current
    const box = boxRef.current
    if (!chart || !series || !box) return
    const width = box.clientWidth
    const out: Placed[] = []
    const taken: Array<{ l: number; r: number; t: number; b: number }> = []
    const sorted = [...marks].sort((a, b) => a.time - b.time)
    for (const mark of sorted) {
      const xc = chart.timeScale().timeToCoordinate(chartTime(mark.time) as UTCTimestamp)
      const yc = series.priceToCoordinate(mark.price)
      if (xc == null || yc == null) continue
      const x: number = xc
      const y: number = yc
      const w = Math.min(240, 6.6 * Math.max(mark.title.length, (mark.sub ?? '').length) + 24)
      // Clear of the price axis on the right (about 70 px) and of the left edge.
      const lx = Math.min(Math.max(x, w / 2 + 4), Math.max(w / 2 + 4, width - w / 2 - 74))
      // Move a label one row further out until it touches no label already placed.
      let ly = y
      let rect = { l: 0, r: 0, t: 0, b: 0 }
      for (let row = 0; row < 6; row++) {
        ly = mark.above ? y - LABEL_GAP - row * LABEL_ROW : y + LABEL_GAP + row * LABEL_ROW
        const top = mark.above ? ly - LABEL_H : ly
        rect = { l: lx - w / 2, r: lx + w / 2, t: top, b: top + LABEL_H }
        if (!taken.some((o) => rect.l < o.r + 4 && rect.r > o.l - 4 && rect.t < o.b + 2 && rect.b > o.t - 2)) break
      }
      taken.push(rect)
      out.push({ mark, x, y, lx, ly })
    }
    setPlaced(out)
  }, [marks])

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
    chart.applyOptions({ rightPriceScale: { scaleMargins: { top: 0.14, bottom: 0.1 } } })
    chartRef.current = chart
    seriesRef.current = series
    const observer = new ResizeObserver(() => {
      if (el.clientWidth > 0) chart.applyOptions({ width: el.clientWidth, height: el.clientHeight })
      requestAnimationFrame(() => layoutRef.current())
    })
    observer.observe(el)
    const onRange = () => layoutRef.current()
    chart.timeScale().subscribeVisibleLogicalRangeChange(onRange)
    return () => {
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
    series.setData(
      candles.map((c) => ({ time: chartTime(c.t) as UTCTimestamp, open: c.o, high: c.h, low: c.l, close: c.c })),
    )
    if (fittedRef.current !== fitKey && candles.length > 0) {
      fittedRef.current = fitKey
      chart.timeScale().fitContent()
    }
    requestAnimationFrame(() => layoutRef.current())
  }, [candles, fitKey, theme])

  useEffect(() => {
    const series = seriesRef.current
    if (!series) return
    for (const line of linesRef.current) series.removePriceLine(line)
    linesRef.current = mergeLevels(levels).map((l) => {
      const s = lineStyleOf(l.kind)
      return series.createPriceLine({
        price: l.price,
        color: s.color,
        lineWidth: s.width,
        lineStyle: s.style,
        axisLabelVisible: true,
        title: l.title,
      })
    })
    requestAnimationFrame(() => layoutRef.current())
  }, [levels, theme])

  useEffect(() => {
    requestAnimationFrame(() => layoutRef.current())
  }, [layout])

  return (
    <div ref={boxRef} className="story-chart" style={{ height }}>
      <div ref={canvasRef} className="story-chart__canvas" />
      <svg className="story-chart__links" aria-hidden="true">
        {placed.map((p, i) => (
          <g key={i} className={`story-link story-link--${p.mark.tone}`}>
            <line x1={p.x} y1={p.y} x2={p.lx} y2={p.ly} />
            <circle cx={p.x} cy={p.y} r={6} />
          </g>
        ))}
      </svg>
      {placed.map((p, i) => (
        <div
          key={i}
          className={`story-label story-label--${p.mark.tone}`}
          style={{ left: p.lx, top: p.ly, transform: `translate(-50%, ${p.mark.above ? '-100%' : '0'})` }}
        >
          <b>{p.mark.title}</b>
          {p.mark.sub && <span>{p.mark.sub}</span>}
        </div>
      ))}
      {candles.length === 0 && <p className="story-chart__empty">No candles recorded for this day yet.</p>}
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
  const dailyQ = useStoredCandles(spotSymbol, 'D', shiftDay(day, -15), day)

  const bars1 = useMemo(() => foldSession(indexQ.data ?? [], day, 1), [indexQ.data, day])
  const candles = useMemo(() => foldSession(indexQ.data ?? [], day, minutes), [indexQ.data, day, minutes])
  const daily = useMemo(
    () => (dailyQ.data ?? []).map((c) => ({ t: Date.parse(c.timestampUtc), o: c.open, h: c.high, l: c.low, c: c.close })),
    [dailyQ.data],
  )
  const entries = useMemo(() => trendRiderEntries(view.activity ?? []), [view.activity])
  const trades = useMemo(() => tradeMarks(view.positions ?? [], view.activity ?? [], bars1), [view.positions, view.activity, bars1])
  const spot = view.spotLtp ?? bars1.at(-1)?.c ?? null

  const plans: OpenPlan[] = useMemo(() => {
    const out: OpenPlan[] = []
    for (const trade of trades) {
      if (!trade.open) continue
      const entry = entries.find((e) => e.groupId === trade.groupId)
      if (!entry) continue
      const since = bars1.filter((b) => b.t >= candleStart(entry.atMs, 1))
      const plan = exitPlan(entry, since, spot)
      if (plan) out.push({ trade, entry, plan })
    }
    return out
  }, [trades, entries, bars1, spot])

  // The day's levels: from the strategy's own signal when it has entered, else from the stored candles.
  const latest = entries.at(-1) ?? null
  const prevSession = useMemo(() => {
    const before = daily.filter((b) => istDay(b.t) < day)
    return before.at(-1) ?? null
  }, [daily, day])
  const levels = {
    prevHigh: latest?.levels.prevHigh ?? prevSession?.h ?? null,
    prevLow: latest?.levels.prevLow ?? prevSession?.l ?? null,
    dayOpen: latest?.levels.dayOpen ?? candles[0]?.o ?? null,
    adr: latest?.levels.adr ?? adrFrom(daily, day, 10),
  }
  const fromSignal = latest != null
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
  // The last closed candle at the strategy's 5-minute grid (live hands a forming one too).
  const lastClosed = (() => {
    const five = foldSession(indexQ.data ?? [], day, 5)
    const now = Date.now()
    const done = live ? five.filter((c) => c.t + 5 * 60_000 <= now) : five
    return done.at(-1) ?? null
  })()
  const check = v2Check(levels, lastClosed?.c ?? null)

  const chartLevels: Level[] = useMemo(() => {
    const out: Level[] = []
    if (levels.prevHigh != null) out.push({ price: levels.prevHigh, title: 'Yday high', kind: 'prev' })
    if (levels.prevLow != null) out.push({ price: levels.prevLow, title: 'Yday low', kind: 'prev' })
    if (levels.dayOpen != null) out.push({ price: levels.dayOpen, title: 'Day open', kind: 'open' })
    for (const { plan } of plans) {
      out.push({ price: plan.entry, title: 'Entry', kind: 'entry' })
      if (plan.stop != null) out.push({ price: plan.stop, title: plan.activeKind === 'stop' ? 'Stop (active)' : 'Stop', kind: 'stop' })
      if (plan.armed && plan.trail != null) out.push({ price: plan.trail, title: 'Trail (active)', kind: 'trail' })
      else if (plan.armAt != null) out.push({ price: plan.armAt, title: 'Trail arms', kind: 'arm' })
      if (plan.armed) out.push({ price: plan.bestLevel, title: 'Best', kind: 'best' })
    }
    return out
  }, [levels.prevHigh, levels.prevLow, levels.dayOpen, plans])

  const indexMarks: Mark[] = useMemo(() => {
    const out: Mark[] = []
    for (const t of trades) {
      if (t.indexAtEntry != null) {
        out.push({
          time: candleStart(t.entryMs, minutes),
          price: t.indexAtEntry,
          title: `BUY ${t.label.split(' · ')[0]}${t.manual ? ' (by hand)' : ''}`,
          sub: `${istHm(t.entryMs)} · ₹${formatPrice(t.entryPrice)}`,
          tone: 'buy',
          above: t.optionType !== 'PE',
        })
      }
      if (!t.open && t.exitMs != null && t.indexAtExit != null) {
        out.push({
          time: candleStart(t.exitMs, minutes),
          price: t.indexAtExit,
          title: `SELL ${formatInrSigned(t.pnl)}`,
          sub: `${istHm(t.exitMs)} · ₹${formatPrice(t.exitPrice)}${shortReason(t.exitReason) ? ` · ${shortReason(t.exitReason)}` : ''}`,
          tone: tradeTone(t),
          above: t.optionType === 'PE',
        })
      }
    }
    return out
  }, [trades, minutes])

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

      {/* ------------------------------------------------ chart and rules */}
      <div className="story__grid">
        <Panel
          title={`${view.underlying ?? 'Index'} · ${minutes}-minute · ${day}`}
          actions={
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
          }
        >
          {indexQ.isError && <InlineError error={indexQ.error} />}
          <StoryChart candles={candles} levels={chartLevels} marks={indexMarks} fitKey={`${spotSymbol}|${day}|${minutes}`} height={460} />
          <ul className="story__legend">
            <li><i className="lg lg--prev" /> Yesterday's high / low</li>
            <li><i className="lg lg--open" /> Day open</li>
            <li><i className="lg lg--entry" /> Entry</li>
            <li><i className="lg lg--stop" /> Stop</li>
            <li><i className="lg lg--arm" /> Trail arms here</li>
            <li><i className="lg lg--trail" /> Trail (active): sells here</li>
          </ul>
        </Panel>

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
                <ul className="story__checks">
                  <li className={check.aboveHigh ? 'ok' : ''}>{check.aboveHigh ? '✓' : '·'} Close above yesterday's high</li>
                  <li className={check.aboveOpen ? 'ok' : ''}>{check.aboveOpen ? '✓' : '·'} Close above the day's open</li>
                  <li className={check.belowLow ? 'ok' : ''}>{check.belowLow ? '✓' : '·'} Close below yesterday's low</li>
                  <li className={check.belowOpen ? 'ok' : ''}>{check.belowOpen ? '✓' : '·'} Close below the day's open</li>
                </ul>
                <p className="story__side">
                  {check.side === 'BUY' ? <Badge tone="pos">Call side is live</Badge> : check.side === 'SELL' ? <Badge tone="neg">Put side is live</Badge> : <Badge>No side yet</Badge>}
                </p>
                {plans.map(({ trade, plan }) => (
                  <div key={trade.groupId} className="story__plan">
                    <h3>{trade.label}</h3>
                    <table className="story__kv">
                      <tbody>
                        <tr><th>Entry (index)</th><td className="num">{p1(plan.entry)}</td></tr>
                        <tr className="neg"><th>Stop</th><td className="num">{p1(plan.stop)} {plan.stop != null && spot != null ? <span className="faint">({pts(plan.direction === 'BUY' ? spot - plan.stop : plan.stop - spot)} away)</span> : null}</td></tr>
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
                {plans.length > 0 && (
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

      {/* ------------------------------------------------- each option */}
      {[...trades].sort((a, b) => Number(b.open) - Number(a.open) || b.entryMs - a.entryMs).map((t) => (
        <OptionPanel key={t.groupId} trade={t} day={day} isToday={isToday} live={live} risk={view.risk?.leg ?? null} />
      ))}

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
}: {
  trade: TradeMark
  day: string
  isToday: boolean
  live: boolean
  risk: { targetPoints?: number | null; stopLossPoints?: number | null; targetPercent?: number | null; stopLossPercent?: number | null } | null
}) {
  const barsQ = useRunDayBars(trade.symbol, day, isToday, live && trade.open)
  const candles = useMemo(() => foldSession(barsQ.data ?? [], day, 1), [barsQ.data, day])
  const levels: Level[] = useMemo(() => {
    const out: Level[] = [{ price: trade.entryPrice, title: `Bought ₹${formatPrice(trade.entryPrice)}`, kind: 'entry' }]
    const target =
      risk?.targetPoints != null ? trade.entryPrice + risk.targetPoints : risk?.targetPercent != null ? trade.entryPrice * (1 + risk.targetPercent / 100) : null
    const stop =
      risk?.stopLossPoints != null ? trade.entryPrice - risk.stopLossPoints : risk?.stopLossPercent != null ? trade.entryPrice * (1 - risk.stopLossPercent / 100) : null
    if (target != null) out.push({ price: target, title: 'Target', kind: 'best' })
    if (stop != null) out.push({ price: stop, title: 'Premium SL', kind: 'stop' })
    return out
  }, [trade.entryPrice, risk])
  const marks: Mark[] = useMemo(() => {
    const out: Mark[] = [
      { time: candleStart(trade.entryMs, 1), price: trade.entryPrice, title: 'BUY', sub: `${istHm(trade.entryMs)} · ₹${formatPrice(trade.entryPrice)}`, tone: 'buy', above: false },
    ]
    if (!trade.open && trade.exitMs != null && trade.exitPrice != null) {
      out.push({
        time: candleStart(trade.exitMs, 1),
        price: trade.exitPrice,
        title: `SELL ${formatInrSigned(trade.pnl)}`,
        sub: `${istHm(trade.exitMs)} · ₹${formatPrice(trade.exitPrice)}`,
        tone: tradeTone(trade),
        above: true,
      })
    }
    return out
  }, [trade])

  return (
    <Panel
      title={
        <>
          {trade.label} {trade.open ? <Badge tone="live">open</Badge> : <Badge>closed</Badge>} {trade.manual && <Badge tone="warn">booked by hand</Badge>}
        </>
      }
      actions={
        <span className={`story__optpnl ${pnlClass(trade.pnl)}`}>
          {formatInrSigned(trade.pnl)} <span className="faint">({formatPercent(trade.pnlPercent, 1)})</span>
        </span>
      }
    >
      <StoryChart candles={candles} levels={levels} marks={marks} fitKey={`${trade.symbol}|${day}`} height={280} />
      <p className="small-note">
        {trade.lots} lots × {trade.qty / Math.max(1, trade.lots)} = {trade.qty} · bought {istHm(trade.entryMs)} at ₹{formatPrice(trade.entryPrice)}
        {trade.indexAtEntry != null ? ` (index ${p1(trade.indexAtEntry)})` : ''}
        {trade.open
          ? ` · now ₹${formatPrice(trade.ltp)}`
          : ` · sold ${trade.exitMs ? istHm(trade.exitMs) : ''} at ₹${formatPrice(trade.exitPrice)}${trade.exitReason ? ` — ${trade.exitReason}` : ''}`}
      </p>
    </Panel>
  )
}
