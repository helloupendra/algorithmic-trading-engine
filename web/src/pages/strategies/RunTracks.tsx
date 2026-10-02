/**
 * Trade → Runs, the Tracks view: every run of the day as a track on one IST
 * time axis, under NIFTY's price and each account's net, so a stop, the
 * fills before it and the move that caused it line up vertically. It
 * answers "when, and why"; the cards answer "what now".
 *
 * A track's line is the net after charges the recorder wrote once a minute,
 * square-root scaled on one shared scale (lib/tracks.ts): green above its
 * line, red below, a big track filling its row rather than flattening the
 * rest. Ticks along the top are fills; the end says how the run stopped.
 * Hovering anywhere on the board reads every row at that minute. The
 * figures on the right are the Desk grid's, from the run list.
 *
 * Drawn in pixels at the measured width of the lane column, with the Desk's
 * tokens; the phone keeps the same rows at about 200 px of axis.
 *
 * The board can be narrowed to one underlying (in the URL, &underlying=),
 * offered from the runs it draws: its tracks, the price of that underlying
 * instead of NIFTY's, and each account's line on it alone.
 */

import { memo, useId, useMemo, useState } from 'react'
import type { MouseEvent, ReactNode } from 'react'
import { Link } from 'react-router-dom'
import type { Scope } from '../../lib/desk'
import {
  accountStroke,
  carriedRunIds,
  compactInr,
  dayLabel,
  deskAccounts,
  isTradingRun,
  plainNumber,
  underlyingShort,
  validScope,
} from '../../lib/desk'
import type { CurvePoint, DayAxis, Gap } from '../../lib/pnlSeries'
import { dayStartMs, gapText, minuteLabel, NSE_CLOSE, NSE_OPEN, stepPath, timeTicks, valueAt, valueDomain } from '../../lib/pnlSeries'
import type { Track, TracksLayout } from '../../lib/tracks'
import {
  barPoints,
  priceTrace,
  trackHeight,
  trackUnderlyings,
  tracksLayout,
  tracksWithLiveRuns,
  underlyingCode,
  validUnderlying,
} from '../../lib/tracks'
import {
  deskLegsPoll,
  useIntradayTrace,
  useLiveRuns,
  useMarketSession,
  useOpenPositions,
  useRunPnlSeries,
  useRunsOrders,
} from '../../lib/queries'
import { useLiveConnection } from '../../lib/live'
import { answerAsOf } from '../../lib/asOf'
import { runLegs } from '../../lib/liveMarks'
import { useNow, useShownDay, useWidth } from '../desk/data'
import { Money, Swatch, Waiting } from '../desk/parts'
import { InlineError } from '../../components/ui'
import '../desk/desk.css'
import './tracks.css'

interface Frame {
  axis: DayAxis
  width: number
  gaps: readonly Gap[]
  now: number | null
}

const xOf = (f: Frame) => (m: number) => ((Math.min(Math.max(m, f.axis.from), f.axis.to) - f.axis.from) / (f.axis.to - f.axis.from)) * f.width

/** What every lane draws behind its data: outside the NSE session shaded, the recorder's gaps, and now. */
function Backdrop({ f, h }: { f: Frame; h: number }) {
  const x = xOf(f)
  return (
    <>
      {f.axis.from < NSE_OPEN && <rect className="tk-off" x={0} y={0} width={x(NSE_OPEN)} height={h} />}
      {f.axis.to > NSE_CLOSE && <rect className="tk-off" x={x(NSE_CLOSE)} y={0} width={f.width - x(NSE_CLOSE)} height={h} />}
      {f.gaps.map((g) => (
        <rect key={`${g.from}-${g.to}`} className="tk-gap" x={x(g.from)} y={0} width={Math.max(1, x(g.to) - x(g.from))} height={h} />
      ))}
      {f.now != null && <line className="tk-now" x1={x(f.now)} x2={x(f.now)} y1={0} y2={h} />}
    </>
  )
}

/** A line through a lane's points, each segment on its own so a gap stays open. */
function lanePaths(segments: readonly CurvePoint[][], x: (m: number) => number, y: (v: number) => number): string {
  return segments.map((s) => stepPath(s, x, y)).join('')
}

const PriceLane = memo(function PriceLane({ f, points, h }: { f: Frame; points: CurvePoint[]; h: number }) {
  // The recorder's gaps are the P&L's, not the market's: the price lane is drawn through them.
  const bare: Frame = { ...f, gaps: [] }
  const x = xOf(bare)
  const values = points.map((p) => p.v)
  const lo = Math.min(...values)
  const hi = Math.max(...values)
  const pad = (hi - lo || 1) * 0.12
  const y = (v: number) => 4 + (1 - (v - (lo - pad)) / (hi - lo + 2 * pad)) * (h - 8)
  const inside = points.filter((p) => p.m >= f.axis.from && p.m <= f.axis.to)
  const hiP = inside.reduce<CurvePoint | null>((a, p) => (!a || p.v > a.v ? p : a), null)
  const loP = inside.reduce<CurvePoint | null>((a, p) => (!a || p.v < a.v ? p : a), null)
  return (
    <svg width={f.width} height={h} className="tk-svg" aria-hidden="true">
      <Backdrop f={bare} h={h} />
      {inside.length > 1 && <path d={stepPath(inside, x, y)} fill="none" stroke="var(--text-2)" strokeWidth="1.2" />}
      {hiP && f.width > 260 && (
        <text x={x(hiP.m)} y={y(hiP.v) - 3} textAnchor="middle">
          {plainNumber(hiP.v, 0)}
        </text>
      )}
      {loP && f.width > 260 && (
        <text x={x(loP.m)} y={y(loP.v) + 11} textAnchor="middle">
          {plainNumber(loP.v, 0)}
        </text>
      )}
    </svg>
  )
})

const AccountLane = memo(function AccountLane({ f, layout, h }: { f: Frame; layout: TracksLayout; h: number }) {
  const x = xOf(f)
  const tone = new Map(layout.groups.map((g) => [g.account.id, g.account.tone]))
  const domain = valueDomain(layout.accounts.flatMap((a) => a.points.map((p) => p.v)))
  const y = (v: number) => 5 + (1 - (v - domain.lo) / (domain.hi - domain.lo)) * (h - 10)
  return (
    <svg width={f.width} height={h} className="tk-svg" aria-hidden="true">
      <Backdrop f={f} h={h} />
      <line x1={0} x2={f.width} y1={y(0)} y2={y(0)} stroke="var(--line-strong)" />
      <text x={3} y={y(0) - 3}>
        0
      </text>
      {layout.accounts.map((a) => (
        <g key={a.userId}>
          <path d={lanePaths(a.segments, x, y)} fill="none" stroke={accountStroke(tone.get(a.userId) ?? null)} strokeWidth="1.5" />
          {a.last && <circle cx={x(a.last.m)} cy={y(a.last.v)} r="2.3" fill={accountStroke(tone.get(a.userId) ?? null)} />}
        </g>
      ))}
    </svg>
  )
})

const TrackLane = memo(function TrackLane({ f, track, scale, h }: { f: Frame; track: Track; scale: number; h: number }) {
  const id = useId()
  const x = xOf(f)
  const mid = h / 2
  const y = (v: number) => mid - trackHeight(v, scale) * (mid - 2)
  const first = track.points[0]
  const last = track.points[track.points.length - 1]
  const startM = first?.m ?? null
  const endM = track.end?.m ?? last?.m ?? null
  const net = track.figures.net
  const stroke = net >= 0.5 ? 'var(--pos)' : net <= -0.5 ? 'var(--neg)' : 'var(--text-3)'
  const areas = track.segments.map((s) => {
    const d = stepPath(s, x, y)
    return `${d}V${mid.toFixed(1)}H${x(s[0].m).toFixed(1)}Z`
  })
  const endTone = track.end?.kind === 'rule' ? 'var(--neg)' : track.end?.kind === 'target' ? 'var(--pos)' : track.end?.kind === 'fault' ? 'var(--warn)' : null
  return (
    <svg width={f.width} height={h} className="tk-svg" aria-hidden="true">
      <Backdrop f={f} h={h} />
      <clipPath id={`${id}a`}>
        <rect x={0} y={0} width={f.width} height={mid} />
      </clipPath>
      <clipPath id={`${id}b`}>
        <rect x={0} y={mid} width={f.width} height={mid} />
      </clipPath>
      {startM != null && endM != null && <line x1={x(startM)} x2={x(endM)} y1={mid} y2={mid} stroke="var(--line-strong)" />}
      {areas.map((d, i) => (
        <g key={i}>
          <path d={d} fill="var(--pos)" fillOpacity="0.3" clipPath={`url(#${id}a)`} />
          <path d={d} fill="var(--neg)" fillOpacity="0.3" clipPath={`url(#${id}b)`} />
        </g>
      ))}
      <path d={lanePaths(track.segments, x, y)} fill="none" stroke={stroke} strokeOpacity="0.8" strokeWidth="1" />
      {track.fills.map((m) => (
        <line key={m} className="tk-fill" x1={x(m)} x2={x(m)} y1={0.5} y2={3.5} />
      ))}
      {track.restarts.map((m) => (
        <text key={m} x={x(m)} y={mid + 3.5} textAnchor="middle" className="tk-mark tk-mark--warn">
          ↻
        </text>
      ))}
      {track.end &&
        (endTone ? (
          <rect x={x(track.end.m) - 3} y={mid - 3} width={6} height={6} fill={endTone} />
        ) : (
          <line x1={x(track.end.m)} x2={x(track.end.m)} y1={mid - 4} y2={mid + 4} stroke="var(--text-3)" />
        ))}
      {track.carried && track.end && (
        <text x={x(track.end.m) + 5} y={mid + 3.5} className="tk-mark tk-mark--warn">
          C
        </text>
      )}
    </svg>
  )
})

const AxisLane = memo(function AxisLane({ f, narrow }: { f: Frame; narrow: boolean }) {
  const x = xOf(f)
  const ticks = timeTicks(f.axis, narrow ? 4 : 8)
  return (
    <svg width={f.width} height={22} className="tk-svg" aria-hidden="true">
      {ticks.map((m) => (
        <g key={m}>
          <line x1={x(m)} x2={x(m)} y1={0} y2={4} stroke="var(--line-strong)" />
          <text x={Math.min(f.width - 16, Math.max(16, x(m)))} y={15} textAnchor="middle" className={m === NSE_OPEN || m === NSE_CLOSE ? 'tk-strong' : ''}>
            {minuteLabel(m)}
          </text>
        </g>
      ))}
    </svg>
  )
})

/** A row of the board: its label, its lane, its figures. A row with no lane lets its label run across it. */
function Row({ cls, label, lane, num, laneRef }: { cls: string; label: ReactNode; lane?: ReactNode; num?: ReactNode; laneRef?: (el: HTMLDivElement | null) => void }) {
  return (
    <div className={`tk-row ${cls}`}>
      <div className={`tk-lab${lane === undefined ? ' tk-lab--wide' : ''}`}>{label}</div>
      {lane !== undefined && (
        <div className="tk-lane" ref={laneRef}>
          {lane}
        </div>
      )}
      <div className="tk-num">{num}</div>
    </div>
  )
}

function trackState(t: Track): ReactNode {
  if (t.live) return <span className="tk-live">live</span>
  if (!t.end) return <span className="dk-t3">stopped</span>
  const tone = t.end.kind === 'rule' ? 'neg' : t.end.kind === 'target' ? 'pos' : t.end.kind === 'fault' ? 'warn' : 'dk-t3'
  return <span className={tone}>{t.end.label}</span>
}

/** The figure at the hovered minute, else the run list's: hovering reads the board at one time. */
function At({ points, hover, fallback, compact = true }: { points: CurvePoint[]; hover: number | null; fallback: number; compact?: boolean }) {
  const v = hover == null ? fallback : valueAt(points, hover)
  if (v == null) return <span className="dk-t3">—</span>
  return <Money value={v} compact={compact} />
}

export function RunTracks({
  scope,
  onScope,
  underlying,
  onUnderlying,
}: {
  scope: Scope
  onScope: (scope: Scope) => void
  /** From the URL; null for every underlying. */
  underlying: string | null
  onUnderlying: (underlying: string | null) => void
}) {
  const nowMs = useNow(15_000)
  const shownDay = useShownDay(nowMs, true)
  const { day, today } = shownDay
  const isToday = day === today
  const mcx = useMarketSession('MCX', 'COM')
  const trading = useMemo(() => (shownDay.runs ?? []).filter(isTradingRun), [shownDay.runs])
  const allAccounts = useMemo(() => deskAccounts(trading), [trading])
  const shownScope = validScope(scope, allAccounts)
  const accounts = useMemo(
    () => (shownScope === 'all' ? allAccounts : allAccounts.filter((a) => a.id === shownScope)),
    [shownScope, allAccounts],
  )
  // Offered from the runs the board draws for the accounts shown, so every choice has tracks.
  const underlyings = useMemo(
    () => trackUnderlyings(trading.filter((r) => accounts.some((a) => a.id === r.userId))),
    [trading, accounts],
  )
  const shownUnderlying = validUnderlying(underlying, underlyings)
  const trace = priceTrace(trading, shownUnderlying)

  const series = useRunPnlSeries(day, isToday, shownDay.runs !== undefined)
  const read = useRunsOrders(
    trading.map((r) => ({ runId: r.runId, live: r.isActive && isToday })),
    shownDay.runs !== undefined,
  )
  const fills = useMemo(
    () =>
      new Map(
        trading.map((r, i) => [
          r.runId,
          (read.ledgers[i] ?? []).filter((o) => o.status.toLowerCase() === 'filled' && o.filledUtc).map((o) => o.filledUtc!),
        ]),
      ),
    [trading, read.ledgers],
  )
  const open = useOpenPositions(deskLegsPoll(isToday, useLiveConnection()))
  const positions = open.data?.positions
  const legsAsOf = answerAsOf(open)
  const carried = useMemo(() => carriedRunIds(positions), [positions])
  // The same open legs re-price the live runs' figures between the run list's answers.
  const legs = useMemo(() => runLegs(positions, legsAsOf), [positions, legsAsOf])
  const liveRuns = useLiveRuns(trading, shownDay.asOf, legs)
  const bars = useIntradayTrace(trace.symbol, isToday)
  const price = useMemo(() => barPoints(bars.data, day), [bars.data, day])

  const layout = useMemo(
    () =>
      tracksLayout({
        runs: trading,
        accounts,
        series: series.data,
        fills,
        carried,
        day,
        nowMs,
        isToday,
        mcxCloseUtc: isToday ? mcx.data?.sessionCloseUtc : null,
        underlying: shownUnderlying,
      }),
    [trading, accounts, series.data, fills, carried, day, nowMs, isToday, mcx.data?.sessionCloseUtc, shownUnderlying],
  )

  // The live tracks' figures, and their lines' ends, at the pushed prices. Every lane a push
  // did not reach keeps its track object, and the frame is the answered layout's, so only
  // the lanes a price moved are redrawn.
  const seriesStart = series.data?.dayStartUtc
  const shown = useMemo(() => {
    if (!liveRuns || liveRuns === trading) return layout
    return tracksWithLiveRuns(layout, liveRuns, (Date.now() - dayStartMs(day, seriesStart)) / 60_000)
  }, [layout, liveRuns, trading, day, seriesStart])

  const [laneRef, width] = useWidth<HTMLDivElement>()
  const [hover, setHover] = useState<number | null>(null)
  const frame: Frame = useMemo(() => ({ axis: layout.axis, width, gaps: layout.gaps, now: layout.now }), [layout, width])
  const narrow = width > 0 && width < 320

  function onMove(e: MouseEvent<HTMLDivElement>) {
    const lane = (e.currentTarget.querySelector('.tk-lane') as HTMLElement | null)?.getBoundingClientRect()
    if (!lane || width === 0) return
    const px = e.clientX - lane.left
    if (px < 0 || px > lane.width) return setHover(null)
    setHover(Math.round(layout.axis.from + (px / lane.width) * (layout.axis.to - layout.axis.from)))
  }

  if (shownDay.error != null && shownDay.runs === undefined) return <InlineError error={shownDay.error} />
  if (shownDay.runs === undefined) return <Waiting>Reading the day’s runs…</Waiting>
  if (trading.length === 0) return <p className="tr-empty tk-empty">No strategy run traded on {isToday ? 'today' : dayLabel(day)}.</p>

  const multi = allAccounts.length > 1
  const priceLast = price.filter((p) => p.m >= layout.axis.from && p.m <= layout.axis.to).at(-1)
  const range = price.length ? Math.max(...price.map((p) => p.v)) - Math.min(...price.map((p) => p.v)) : null
  const toneOf = new Map(shown.groups.map((g) => [g.account.id, g.account]))
  const inGap = hover != null && layout.gaps.some((g) => hover > g.from && hover < g.to)

  return (
    <div className="tk">
      <div className="tk-bar">
        {multi && (
          <span className="dk-seg scroll-x" role="group" aria-label="Accounts">
            <button type="button" aria-pressed={shownScope === 'all'} onClick={() => onScope('all')}>
              All accounts
            </button>
            {allAccounts.map((a) => (
              <button key={a.id} type="button" aria-pressed={shownScope === a.id} onClick={() => onScope(a.id)}>
                {a.name}
              </button>
            ))}
          </span>
        )}
        {underlyings.length > 1 && (
          <span className="dk-seg scroll-x" role="group" aria-label="Underlying">
            <button type="button" aria-pressed={shownUnderlying == null} onClick={() => onUnderlying(null)}>
              All
            </button>
            {underlyings.map((u) => (
              <button key={u} type="button" aria-pressed={shownUnderlying === u} onClick={() => onUnderlying(u)} title={u}>
                <span className="dk-long">{u}</span>
                <span className="dk-short">{underlyingShort(u)}</span>
              </button>
            ))}
          </span>
        )}
        <span className="dk-t2">
          {isToday ? 'Today' : dayLabel(day)} · {minuteLabel(layout.axis.from)}–{minuteLabel(layout.axis.to)} IST
          {layout.axis.evening && <span className="dk-t3"> · on to the MCX close</span>}
        </span>
        <span className="tr-grow" />
        <span className="dk-t3 dk-xs tk-hover">
          {hover != null ? `at ${minuteLabel(hover)}${inGap ? ' · no points: the recorder was not writing' : ''}` : 'hover to read every row at one minute'}
        </span>
      </div>
      {!isToday && <p className="dk-note">No session today: the tracks are {dayLabel(day)}’s, the last day with runs.</p>}
      {series.isError && <InlineError error={series.error} />}

      <div className="tk-board" onMouseMove={onMove} onMouseLeave={() => setHover(null)}>
        <Row
          cls="tk-price"
          laneRef={laneRef}
          label={
            <>
              <span className="dk-t3 dk-xs">
                {trace.label}
                <span className="tk-hide-s"> · 1 min</span>
              </span>
              <span className="tk-big dk-n">{priceLast ? plainNumber(priceLast.v) : '—'}</span>
            </>
          }
          lane={width > 0 && price.length > 1 ? <PriceLane f={frame} points={price} h={narrow ? 58 : 70} /> : <span className="dk-t3 dk-xs tk-lane-note">no one-minute bars for this day</span>}
          num={
            <>
              <span className="dk-t3 dk-xs">{hover != null ? `at ${minuteLabel(hover)}` : 'range'}</span>
              <span className="dk-n">
                {hover != null ? (valueAt(price, hover) != null ? plainNumber(valueAt(price, hover)!) : '—') : range != null ? `${Math.round(range)} pts` : '—'}
              </span>
            </>
          }
        />
        <Row
          cls="tk-pnl"
          label={
            <>
              <span className="dk-t3 dk-xs">
                Net P&L<span className="tk-hide-s">{shownUnderlying ? ` on ${underlyingShort(shownUnderlying)}` : ''} by account</span>
              </span>
              {shown.groups.map((g) => (
                <span key={g.account.id} className="tk-legend tk-hide-s">
                  {multi && <Swatch tone={g.account.tone} />}
                  {g.account.name}
                </span>
              ))}
            </>
          }
          lane={
            width > 0 && layout.any ? (
              <AccountLane f={frame} layout={shown} h={62} />
            ) : (
              <span className="dk-t3 dk-xs tk-lane-note">{series.data ? 'the recorder has no minutes for these runs' : 'reading the day’s minutes…'}</span>
            )
          }
          num={shown.groups.map((g) => (
            <span key={g.account.id} className="tk-kv">
              <span className="dk-t2 tk-hide-s">{g.account.name}</span>
              {multi && <span className="tk-only-s"><Swatch tone={g.account.tone} /></span>}
              <At points={shown.accounts.find((a) => a.userId === g.account.id)?.points ?? []} hover={hover} fallback={g.figures.net} compact={false} />
            </span>
          ))}
        />
        {shown.groups.map((g) => (
          <div key={g.account.id} role="group" aria-label={`${g.account.name}: ${g.tracks.length} tracks`}>
            <Row
              cls="tk-grp"
              label={
                <>
                  {multi && <Swatch tone={toneOf.get(g.account.id)?.tone ?? null} />}
                  <span>{g.account.name}</span>
                  <span className="dk-t3 dk-xs">{g.figures.trades.toLocaleString('en-IN')} trades</span>
                </>
              }
              num={<Money value={g.figures.net} />}
            />
            {g.tracks.map((t) => (
              <Row
                key={t.key}
                cls="tk-run"
                label={
                  <Link to={`/trade/runs/${t.runs[t.runs.length - 1].runId}`} title={`${t.label} · ${t.underlying} · run #${t.runs.map((r) => r.runId).join(', #')}`}>
                    <span className="tk-long">
                      {t.label} <span className="tk-u">{underlyingShort(t.underlying)}</span>
                    </span>
                    <span className="tk-short">
                      {t.label.split(' ')[0]} <span className="tk-u">{underlyingCode(t.underlying)}</span>
                    </span>
                  </Link>
                }
                lane={width > 0 ? <TrackLane f={frame} track={t} scale={layout.scale} h={22} /> : null}
                num={
                  <>
                    <span className="tk-tn dk-n">{t.figures.trades} tr</span>
                    <b className="tk-net">
                      <At points={t.points} hover={hover} fallback={t.figures.net} />
                    </b>
                    <span className="tk-st">{trackState(t)}</span>
                  </>
                }
              />
            ))}
          </div>
        ))}
        <Row cls="tk-axis" label={null} lane={width > 0 ? <AxisLane f={frame} narrow={narrow} /> : null} num={<span className="dk-t3 dk-xs">IST</span>} />
        {hover != null && width > 0 && (
          <div className="tk-cursor" style={{ left: `calc(var(--tk-lab) + ${((hover - layout.axis.from) / (layout.axis.to - layout.axis.from)) * width}px)` }} aria-hidden="true" />
        )}
      </div>

      <p className="dk-note tk-read">
        Net after charges, one shared square-root scale: a track fills its row at {compactInr(layout.scale, false)} and stops there; the figure beside it is
        the run list’s, a live run’s open book at the pushed prices, where its line ends too. Green above the line, red below; ticks along the top are fills; ■ a stop by a risk rule (green: target, amber: the runner
        exited), | the close or a person; ↻ a restart; C a leg carried to the manual book. Shaded: outside the NSE session.
        {layout.gaps.length > 0 && ` No points ${gapText(layout.gaps)}: the recorder was not writing, so no line is drawn across it.`}
        {!layout.any && series.data && ' The recorder has no minutes for these runs, so the tracks show fills and stops only.'}
      </p>
    </div>
  )
}

