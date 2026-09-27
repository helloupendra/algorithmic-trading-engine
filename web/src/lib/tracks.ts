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
import type { AccountCurve, CurvePoint, DayAxis, Gap } from './pnlSeries'
import { dayAxis, dayStartMs, mcxCloseMinute, minuteOfDay, recorderGaps, seriesPoints, splitAtGaps, valueAt } from './pnlSeries'
import type { LiveBar, LiveRunSummary, RunPnlSeriesResponse } from './types'

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
 * The board: the day's trading runs in scope as tracks, grouped by account
 * (in the order given) and ordered as the grid orders its rows (strategies
 * in the order they first started, then the index order). `fills` holds each
 * run's filled instants, from its ledger; a run whose ledger has not arrived
 * simply has no ticks yet.
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
}): TracksLayout {
  const start = dayStartMs(input.day, input.series?.dayStartUtc)
  const nowMinute = input.isToday ? Math.floor((input.nowMs - start) / 60_000) : null
  const inScope = new Set(input.accounts.map((a) => a.id))
  const runs = input.runs.filter(isTradingRun).filter((r) => inScope.has(r.userId))
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
    const s = input.series?.accounts.find((x) => x.userId === a.id)
    if (!s) return []
    const points = seriesPoints(s)
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
