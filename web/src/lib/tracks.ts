/**
 * The Tracks view of Trade → Runs (the proposal's Direction B): every run of
 * the day drawn as a track on one IST time axis, so a stop, the fills that
 * led to it and the price move behind them line up vertically. This file
 * decides what is drawn; RunTracks.tsx only draws it.
 *
 * A track is what a cell of the Desk's grid is: one account's runs of one
 * strategy on one underlying, a restart adding to it rather than starting a
 * new row. Its line is the runs' net after charges from the recorder's
 * minutes (GET /api/Strategy/runs/pnl-series), each run holding its last
 * value between points and after it ends, as the series defines. Its ticks
 * are the fills from the runs' order ledgers; its end says how the last run
 * stopped. The figure beside it is the grid's, from the run list, so the two
 * views never disagree.
 */

import type { DeskAccount, Figures, StopKind } from './desk'
import { byUnderlying, istHm, isTradingRun, stopKind, strategyLabel, sumFigures } from './desk'
import { MANUAL_BOOK } from './orders'
import type { AccountCurve, CurvePoint, DayAxis, Gap } from './pnlSeries'
import { dayAxis, dayStartMs, mcxCloseMinute, minuteOfDay, recorderGaps, seriesPoints, splitAtGaps, valueAt } from './pnlSeries'
import type { LiveBar, LiveRunSummary, RunPnlSeriesResponse } from './types'

// ---------------------------------------------------------------- one underlying

/** The URL's name for the board's underlying: /trade/runs?view=tracks&underlying=BANKNIFTY */
export const UNDERLYING_PARAM = 'underlying'

const NIFTY_SPOT = 'NSE:NIFTY50-INDEX'

/**
 * The underlyings the board can be narrowed to: the ones its runs are on
 * (the day's trading runs in the accounts shown), in the grid's index order.
 * Taken from the runs rather than a list, so a day on NIFTY and crude offers
 * exactly those two. A manual book is on no underlying and is not offered;
 * it stays on the board under All.
 */
export function trackUnderlyings(runs: readonly Pick<LiveRunSummary, 'underlying' | 'role' | 'strategyName'>[]): string[] {
  const found = new Set<string>()
  for (const r of runs) {
    if (!isTradingRun(r) || r.strategyName === MANUAL_BOOK || !r.underlying) continue
    found.add(r.underlying.toUpperCase())
  }
  return [...found].sort(byUnderlying)
}

/** The underlying the URL names, upper-cased; null (every underlying) when it names none or something that is not one. */
export function readTrackUnderlying(params: URLSearchParams): string | null {
  const raw = params.get(UNDERLYING_PARAM)?.trim().toUpperCase()
  return raw && /^[A-Z0-9&_-]{1,30}$/.test(raw) ? raw : null
}

/** The URL's params with the underlying set, or removed for every underlying; every other param is kept. */
export function writeTrackUnderlying(params: URLSearchParams, underlying: string | null): URLSearchParams {
  const next = new URLSearchParams(params)
  if (underlying) next.set(UNDERLYING_PARAM, underlying.toUpperCase())
  else next.delete(UNDERLYING_PARAM)
  return next
}

/** An underlying no run on the board is on falls back to every underlying, as an account no longer on the Desk does. */
export function validUnderlying(underlying: string | null, offered: readonly string[]): string | null {
  return underlying != null && offered.includes(underlying) ? underlying : null
}

/**
 * The price lane's instrument: NIFTY 50 over every underlying, else the one
 * picked, at the spot its newest run of the day names (for crude, the
 * future the run traded against). NIFTY 50 again when no run names one.
 */
export function priceTrace(
  runs: readonly Pick<LiveRunSummary, 'underlying' | 'spotSymbol' | 'startedUtc'>[],
  underlying: string | null,
): { symbol: string; label: string } {
  const nifty = { symbol: NIFTY_SPOT, label: 'NIFTY 50' }
  if (underlying == null || underlying === 'NIFTY') return nifty
  const started = (r: Pick<LiveRunSummary, 'startedUtc'>) => (r.startedUtc ? Date.parse(r.startedUtc) : 0)
  const newest = runs
    .filter((r) => r.underlying.toUpperCase() === underlying && !!r.spotSymbol)
    .sort((a, b) => started(b) - started(a))[0]
  return newest ? { symbol: newest.spotSymbol, label: underlying } : nifty
}

/** How a track ends, drawn at the minute it ended. */
export interface TrackEnd {
  m: number
  kind: StopKind
  /** "SL 11:20", "TGT 13:05", "closed", "exited 10:02", "stopped 12:00". */
  label: string
}

export interface Track {
  key: string
  account: DeskAccount
  strategy: string
  label: string
  underlying: string
  /** Oldest first; a restart is a second run. */
  runs: LiveRunSummary[]
  /** Net after charges through the day, the runs summed. Empty when the recorder has no point for them. */
  points: CurvePoint[]
  segments: CurvePoint[][]
  /** Minutes with at least one fill, from the order ledgers read so far. */
  fills: number[]
  /** Minutes a later run of the track started (a restart). */
  restarts: number[]
  /** How the last run ended; null while it is live. */
  end: TrackEnd | null
  /** A leg of one of its runs was carried to a manual book. */
  carried: boolean
  live: boolean
  /** The grid cell's figures, from the run list. */
  figures: Figures
}

export interface TrackGroup {
  account: DeskAccount
  tracks: Track[]
  figures: Figures
}

export interface TracksLayout {
  axis: DayAxis
  groups: TrackGroup[]
  /** The accounts' own curves, from the series' account totals. */
  accounts: AccountCurve[]
  gaps: Gap[]
  /** "Now" on the axis, on the day that is today; else null. */
  now: number | null
  /** The rupees at which a track reaches its full height (see trackScale). */
  scale: number
  /** Whether the recorder has any point for these runs at all. */
  any: boolean
}

/**
 * One shared height scale for every track: the median track's largest
 * swing, at least ₹1,000. A track bigger than that fills its row and stops
 * there rather than flattening every other track to a line (one runaway
 * strategy would otherwise set the scale for all); the figure beside it says
 * how big it really is. Tracks that never moved do not pull the scale down.
 */
export function trackScale(maxAbs: readonly number[], floor = 1_000): number {
  const sorted = maxAbs.filter((v) => Number.isFinite(v) && v > 0).sort((a, b) => a - b)
  if (sorted.length === 0) return floor
  const mid = sorted.length >> 1
  const median = sorted.length % 2 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2
  return Math.max(floor, median)
}

/** The narrowest name for an underlying, for a phone's track label: "N", "BN", "SX", "CR". */
export function underlyingCode(u: string): string {
  const codes: Record<string, string> = {
    NIFTY: 'N',
    BANKNIFTY: 'BN',
    SENSEX: 'SX',
    FINNIFTY: 'FN',
    MIDCPNIFTY: 'MN',
    BANKEX: 'BX',
    CRUDEOIL: 'CR',
    CRUDEOILM: 'CRM',
    NATURALGAS: 'NG',
  }
  return codes[u.toUpperCase()] ?? u.toUpperCase().slice(0, 3)
}

/**
 * A value's height on a track, from -1 (full height below the line) to +1
 * (full height above): square-root compressed so small moves still show,
 * clipped at the scale.
 */
export function trackHeight(value: number, scale: number): number {
  if (!Number.isFinite(value) || value === 0 || scale <= 0) return 0
  return Math.sign(value) * Math.sqrt(Math.min(1, Math.abs(value) / scale))
}

/**
 * Several runs' points as one curve: at every minute any of them has a
 * point, the sum of each run's value then (its last point at or before it; a
 * run not started yet adds nothing).
 */
export function sumCurves(curves: ReadonlyArray<readonly CurvePoint[]>): CurvePoint[] {
  const live = curves.filter((c) => c.length > 0)
  if (live.length === 1) return [...live[0]]
  const minutes = [...new Set(live.flatMap((c) => c.map((p) => p.m)))].sort((a, b) => a - b)
  return minutes.map((m) => ({ m, v: live.reduce((sum, c) => sum + (valueAt(c, m) ?? 0), 0) }))
}

function endOf(run: LiveRunSummary, dayStart: number): TrackEnd | null {
  const kind = stopKind(run)
  const m = minuteOfDay(run.stoppedUtc, dayStart)
  if (kind == null || m == null) return null
  const at = istHm(run.stoppedUtc)
  const label =
    kind === 'rule' ? `SL ${at}` : kind === 'target' ? `TGT ${at}` : kind === 'close' ? 'closed' : kind === 'fault' ? `exited ${at}` : `stopped ${at}`
  return { m, kind, label }
}

/** The one-minute closes of one IST day as points on the day's axis (a price lane). */
export function barPoints(bars: readonly LiveBar[] | undefined, day: string): CurvePoint[] {
  const start = dayStartMs(day)
  return (bars ?? [])
    .map((b) => ({ m: minuteOfDay(b.barStartUtc, start), v: b.close }))
    .filter((p): p is CurvePoint => p.m != null && p.m >= 0 && p.m < 24 * 60 && Number.isFinite(p.v))
    .sort((a, b) => a.m - b.m)
}

/**
 * The board with its live runs' figures at the pushed prices of their legs
 * (`live`: the layout's runs re-priced, liveMarks.runsWithTicks, each run no
 * price moved the same object). A track or account holding a moved run gets
 * its figures again from the moved runs, and on the day that is today, with
 * now on the axis, its line carried on to that figure at `nowMinute`, as the
 * Desk's curve is (pnlSeries.withLiveTips): the recorder's minutes stay as
 * written, and the line's end agrees with the number beside it. Everything
 * else is the same object, so a lane no price reached is not redrawn.
 */
export function tracksWithLiveRuns(layout: TracksLayout, live: readonly LiveRunSummary[], nowMinute: number): TracksLayout {
  const byId = new Map(live.map((r) => [r.runId, r]))
  const moved = (r: LiveRunSummary) => {
    const next = byId.get(r.runId)
    return next && next !== r ? next : null
  }
  const tipped = (points: CurvePoint[], v: number): CurvePoint[] | null => {
    const last = points[points.length - 1]
    if (layout.now == null || !last || last.m >= nowMinute) return null
    return [...points, { m: nowMinute, v }]
  }
  let changed = false
  const groups = layout.groups.map((g) => {
    let groupMoved = false
    const tracks = g.tracks.map((t) => {
      if (!t.runs.some(moved)) return t
      groupMoved = true
      const runs = t.runs.map((r) => moved(r) ?? r)
      const figures = sumFigures(runs)
      const points = t.live ? tipped(t.points, figures.net) : null
      return points ? { ...t, runs, figures, points, segments: splitAtGaps(points, layout.gaps) } : { ...t, runs, figures }
    })
    if (!groupMoved) return g
    changed = true
    return { ...g, tracks, figures: sumFigures(tracks.flatMap((t) => t.runs)) }
  })
  if (!changed) return layout
  const figuresOf = new Map(groups.map((g) => [g.account.id, g.figures]))
  const movedAccounts = new Set(groups.filter((g, i) => g !== layout.groups[i]).map((g) => g.account.id))
  const accounts = layout.accounts.map((a) => {
    const figures = figuresOf.get(a.userId)
    const points = figures && movedAccounts.has(a.userId) ? tipped(a.points, figures.net) : null
    return points ? { ...a, points, segments: splitAtGaps(points, layout.gaps), last: points[points.length - 1] } : a
  })
  return { ...layout, groups, accounts }
}

/**
 * The board: the day's trading runs in scope as tracks, grouped by account
 * (in the order given) and ordered as the grid orders its rows (strategies
 * in the order they first started, then the index order). `fills` holds each
 * run's filled instants, from its ledger; a run whose ledger has not arrived
 * simply has no ticks yet. With `underlying`, only the runs on it, and each
 * account's line is theirs rather than the account's whole day.
 */
export function tracksLayout(input: {
  runs: readonly LiveRunSummary[]
  accounts: readonly DeskAccount[]
  series: Pick<RunPnlSeriesResponse, 'date' | 'dayStartUtc' | 'runs' | 'accounts'> | undefined
  fills: ReadonlyMap<number, readonly string[]>
  carried: ReadonlySet<number>
  day: string
  nowMs: number
  isToday: boolean
  mcxCloseUtc?: string | null
  underlying?: string | null
}): TracksLayout {
  const start = dayStartMs(input.day, input.series?.dayStartUtc)
  const nowMinute = input.isToday ? Math.floor((input.nowMs - start) / 60_000) : null
  const inScope = new Set(input.accounts.map((a) => a.id))
  const underlying = input.underlying?.toUpperCase() ?? null
  const runs = input.runs
    .filter(isTradingRun)
    .filter((r) => inScope.has(r.userId))
    .filter((r) => underlying == null || r.underlying.toUpperCase() === underlying)
  const ids = new Set(runs.map((r) => r.runId))
  const seriesRuns = (input.series?.runs ?? []).filter((s) => ids.has(s.runId))
  const pointsOf = new Map(seriesRuns.map((s) => [s.runId, seriesPoints(s)]))
  const gaps = recorderGaps(seriesRuns, { dayStartMs: start, nowMinute })

  const started = (r: LiveRunSummary) => (r.startedUtc ? Date.parse(r.startedUtc) : Number.MAX_SAFE_INTEGER)
  const sorted = [...runs].sort((a, b) => started(a) - started(b) || a.runId - b.runId)
  const strategyOrder = [...new Set(sorted.map((r) => r.strategyName))]

  const groups: TrackGroup[] = input.accounts.flatMap((account) => {
    const own = sorted.filter((r) => r.userId === account.id)
    if (own.length === 0) return []
    const cells = new Map<string, LiveRunSummary[]>()
    for (const r of own) {
      const key = `${r.strategyName}|${r.underlying.toUpperCase()}`
      cells.set(key, [...(cells.get(key) ?? []), r])
    }
    const tracks: Track[] = [...cells.values()].map((cellRuns) => {
      const first = cellRuns[0]
      const last = cellRuns[cellRuns.length - 1]
      const points = sumCurves(cellRuns.map((r) => pointsOf.get(r.runId) ?? []))
      const fillMinutes = new Set<number>()
      for (const r of cellRuns) {
        for (const at of input.fills.get(r.runId) ?? []) {
          const m = minuteOfDay(at, start)
          if (m != null) fillMinutes.add(m)
        }
      }
      return {
        key: `${account.id}|${first.strategyName}|${first.underlying.toUpperCase()}`,
        account,
        strategy: first.strategyName,
        label: strategyLabel(first.strategyName),
        underlying: first.underlying.toUpperCase(),
        runs: cellRuns,
        points,
        segments: splitAtGaps(points, gaps),
        fills: [...fillMinutes].sort((a, b) => a - b),
        restarts: cellRuns.slice(1).flatMap((r) => {
          const m = minuteOfDay(r.startedUtc, start)
          return m == null ? [] : [m]
        }),
        end: endOf(last, start),
        carried: cellRuns.some((r) => input.carried.has(r.runId)),
        live: cellRuns.some((r) => r.isActive),
        figures: sumFigures(cellRuns),
      }
    })
    tracks.sort(
      (a, b) => strategyOrder.indexOf(a.strategy) - strategyOrder.indexOf(b.strategy) || byUnderlying(a.underlying, b.underlying),
    )
    return [{ account, tracks, figures: sumFigures(own) }]
  })

  const accountCurves: AccountCurve[] = input.accounts.flatMap((a) => {
    let points: CurvePoint[]
    if (underlying != null) {
      // The server's account totals are every underlying's: on one, the line
      // is the account's runs on it, summed the way the server sums them.
      points = sumCurves(runs.filter((r) => r.userId === a.id).map((r) => pointsOf.get(r.runId) ?? []))
      if (points.length === 0) return []
    } else {
      const s = input.series?.accounts.find((x) => x.userId === a.id)
      if (!s) return []
      points = seriesPoints(s)
    }
    return [{ userId: a.id, points, segments: splitAtGaps(points, gaps), last: points[points.length - 1] ?? null }]
  })

  const tracks = groups.flatMap((g) => g.tracks)
  const minutes = [
    ...tracks.flatMap((t) => t.points.map((p) => p.m)),
    ...tracks.flatMap((t) => t.fills),
    ...accountCurves.flatMap((c) => c.points.map((p) => p.m)),
  ]
  const axis = dayAxis(minutes, mcxCloseMinute(input.mcxCloseUtc, start))
  const now = nowMinute != null && nowMinute >= axis.from && nowMinute <= axis.to ? nowMinute : null
  const scale = trackScale(tracks.map((t) => Math.max(0, ...t.points.map((p) => Math.abs(p.v)))))
  return { axis, groups, accounts: accountCurves, gaps, now, scale, any: tracks.some((t) => t.points.length > 0) }
}
