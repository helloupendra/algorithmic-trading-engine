/**
 * A day's P&L as the recorder wrote it (GET /api/Strategy/runs/pnl-series),
 * turned into what a chart draws: points on an IST minute axis, the
 * stretches the recorder missed, the axis itself and where "now" sits on it.
 * The Desk's Day P&L and the Tracks view on Trade → Runs both draw from here.
 *
 * Two rules, the same as the rest of the Desk:
 *
 * - Net of charges: a curve is the series' `net`, what the run card showed
 *   at that minute.
 * - A gap is said, never drawn over. A strategy run gets a point every minute
 *   it is live, so a stretch without one means the recorder was not running;
 *   the curve breaks there and the panel names the stretch, instead of a line
 *   across it claiming to know what happened in between.
 */

import type { RunPnlSeries, RunPnlSeriesResponse } from './types'

/** Minutes from 00:00 IST. */
export const NSE_OPEN = 9 * 60 + 15
export const NSE_CLOSE = 15 * 60 + 30
export const MCX_OPEN = 9 * 60
/** The MCX evening close when the session answer is not in (23:55 while the US is on winter time). */
export const MCX_CLOSE = 23 * 60 + 30

/** A run the close stopped writes its last row a minute or two after 15:30: still the NSE day. */
const CLOSE_SLACK = 5

/**
 * Minutes without a point before a stretch counts as a gap. One missed pass
 * (a slow query, a deploy's restart) is not worth a sentence; three in a row
 * are a stretch someone should know the curve does not cover.
 */
export const GAP_MINUTES = 3

export interface CurvePoint {
  /** Minutes from 00:00 IST of the day. */
  m: number
  /** Rupees. */
  v: number
}

/** A stretch with no point: the last minute before it and the first after it (or now, for one still open). */
export interface Gap {
  from: number
  to: number
}

/** "09:15" for a minute of the day; past midnight it keeps counting ("24:10"), which a day axis never reaches. */
export function minuteLabel(m: number): string {
  const whole = Math.round(m)
  return `${String(Math.floor(whole / 60)).padStart(2, '0')}:${String(whole % 60).padStart(2, '0')}`
}

/** The minute of the day an instant falls in, counted from the day's 00:00 IST; null for a missing stamp. */
export function minuteOfDay(iso: string | null | undefined, dayStartMs: number): number | null {
  const ms = iso ? Date.parse(iso) : NaN
  return Number.isNaN(ms) ? null : Math.floor((ms - dayStartMs) / 60_000)
}

/** 00:00 IST of a yyyy-MM-dd day, as epoch ms: the series' own dayStartUtc when it has one. */
export function dayStartMs(day: string, dayStartUtc?: string | null): number {
  const given = dayStartUtc ? Date.parse(dayStartUtc) : NaN
  return Number.isNaN(given) ? Date.parse(`${day}T00:00:00+05:30`) : given
}

/** A series' net as points, oldest first; a point the API sent without a number is left out, not zeroed. */
export function seriesPoints(s: { minutes: readonly number[]; net: readonly number[] }): CurvePoint[] {
  const out: CurvePoint[] = []
  const n = Math.min(s.minutes.length, s.net.length)
  for (let i = 0; i < n; i++) {
    const m = s.minutes[i]
    const v = s.net[i]
    if (Number.isFinite(m) && Number.isFinite(v)) out.push({ m, v })
  }
  return out.sort((a, b) => a.m - b.m)
}

/** Stretches merged where they touch or overlap, oldest first; an empty one is dropped. */
export function mergeGaps(gaps: readonly Gap[]): Gap[] {
  const sorted = [...gaps].filter((g) => g.to > g.from).sort((a, b) => a.from - b.from)
  const out: Gap[] = []
  for (const g of sorted) {
    const last = out[out.length - 1]
    if (last && g.from <= last.to) last.to = Math.max(last.to, g.to)
    else out.push({ ...g })
  }
  return out
}

/**
 * Where the recorder wrote nothing while a strategy run was live: the
 * stretches, inside the time some strategy run was live (from its start to
 * its last point, or to now for one still running on the day that is today),
 * where no strategy run has a point. The recorder is one pass for every run,
 * so a real outage shows in all of them; asking for no point from any run
 * keeps a run that sat Pending for a while from reading as an outage. Manual
 * books are left out: they get a point only when their figures move, so
 * their silences say nothing about the recorder.
 */
export function recorderGaps(
  runs: ReadonlyArray<Pick<RunPnlSeries, 'isManualBook' | 'minutes' | 'startedUtc' | 'status'>>,
  opts: { dayStartMs: number; nowMinute?: number | null; minGap?: number },
): Gap[] {
  const minGap = opts.minGap ?? GAP_MINUTES
  const live: Gap[] = []
  const seen = new Set<number>()
  for (const run of runs) {
    if (run.isManualBook || run.minutes.length === 0) continue
    let first = Infinity
    let last = -Infinity
    for (const m of run.minutes) {
      seen.add(m)
      first = Math.min(first, m)
      last = Math.max(last, m)
    }
    const started = minuteOfDay(run.startedUtc, opts.dayStartMs)
    // A run started on an earlier day has no start on this axis to measure from.
    const from = started != null && started >= 0 ? Math.min(started, first) : first
    const running = run.status === 'Running' || run.status === 'Stopping'
    const to = running && opts.nowMinute != null ? Math.max(last, opts.nowMinute) : last
    live.push({ from, to })
  }
  const points = [...seen].sort((a, b) => a - b)
  const gaps: Gap[] = []
  for (const span of mergeGaps(live)) {
    let prev = span.from
    for (const m of points) {
      if (m < span.from || m > span.to) continue
      if (m - prev > minGap) gaps.push({ from: prev, to: m })
      prev = m
    }
    if (span.to - prev > minGap) gaps.push({ from: prev, to: span.to })
  }
  return gaps
}

/**
 * A curve cut at the gaps: consecutive points are joined only when no gap
 * lies between them, so each segment is a stretch the recorder covered.
 */
export function splitAtGaps(points: readonly CurvePoint[], gaps: readonly Gap[]): CurvePoint[][] {
  const segments: CurvePoint[][] = []
  let current: CurvePoint[] = []
  for (const p of points) {
    const prev = current[current.length - 1]
    if (prev && gaps.some((g) => prev.m <= g.from && p.m >= g.to)) {
      segments.push(current)
      current = []
    }
    current.push(p)
  }
  if (current.length) segments.push(current)
  return segments
}

/**
 * The value a curve held at a minute: its last point at or before it, as
 * the series means it (a run holds its value between points and keeps its
 * final one after it ends). Null before its first point.
 */
export function valueAt(points: readonly CurvePoint[], m: number): number | null {
  let lo = 0
  let hi = points.length - 1
  let hit: number | null = null
  while (lo <= hi) {
    const mid = (lo + hi) >> 1
    if (points[mid].m <= m) {
      hit = points[mid].v
      lo = mid + 1
    } else hi = mid - 1
  }
  return hit
}

/** The span a day's chart covers, in minutes from 00:00 IST. */
export interface DayAxis {
  from: number
  to: number
  /** It runs on into the MCX evening session. */
  evening: boolean
}

/**
 * The day's axis: the NSE session, 09:15 to 15:30, from 09:00 when an MCX
 * run has points before the NSE open, and on to the MCX close once an MCX run
 * has points past the NSE close. The axis follows the points, not the
 * calendar: a crude run the close stopped at 15:30 keeps it to the NSE day,
 * and a day so far is drawn across the whole session, so "now" shows how
 * much of it is still to come.
 */
export function dayAxis(minutes: readonly number[], mcxClose: number = MCX_CLOSE): DayAxis {
  if (minutes.length === 0) return { from: NSE_OPEN, to: NSE_CLOSE, evening: false }
  let first = Infinity
  let last = -Infinity
  for (const m of minutes) {
    if (m < first) first = m
    if (m > last) last = m
  }
  const from = first < NSE_OPEN ? Math.max(MCX_OPEN, first) : NSE_OPEN
  if (last > NSE_CLOSE + CLOSE_SLACK) return { from, to: Math.max(mcxClose, last), evening: true }
  // The last row a minute past the close is drawn at the close: the axis still reads 15:30.
  return { from, to: NSE_CLOSE, evening: false }
}

/** The MCX close as the session answer states it, else the usual 23:30. */
export function mcxCloseMinute(sessionCloseUtc: string | null | undefined, dayStart: number): number {
  const m = minuteOfDay(sessionCloseUtc, dayStart)
  return m != null && m > NSE_CLOSE && m < 24 * 60 ? m : MCX_CLOSE
}

/**
 * The times the axis names: its two ends, the NSE open and close where they
 * fall inside it, and whole hours between, spaced so that at most
 * `maxLabels` fit and none crowds an anchor.
 */
export function timeTicks(axis: Pick<DayAxis, 'from' | 'to'>, maxLabels = 5): number[] {
  const { from, to } = axis
  const span = Math.max(1, to - from)
  const step = [30, 60, 120, 180, 240, 360].find((s) => span / s <= maxLabels - 1) ?? 360
  // The NSE open and close are named when they are inside and clear of the ends (09:00 and 09:15 would collide).
  const anchors = [from, to, ...[NSE_OPEN, NSE_CLOSE].filter((m) => m - from >= step / 2 && to - m >= step / 2)]
  const ticks = [...anchors]
  for (let m = Math.ceil(from / step) * step; m <= to; m += step) {
    if (anchors.every((a) => Math.abs(a - m) >= step / 2)) ticks.push(m)
  }
  return ticks.sort((a, b) => a - b)
}

/** The round step (1, 2 or 5 × a power of ten) nearest to `count` gridlines over a span. */
export function niceStep(span: number, count = 3): number {
  const raw = Math.abs(span) / Math.max(1, count)
  if (raw === 0 || !Number.isFinite(raw)) return 1
  const p = 10 ** Math.floor(Math.log10(raw))
  const n = raw / p
  return (n < 1.5 ? 1 : n < 3.5 ? 2 : n < 7.5 ? 5 : 10) * p
}

/**
 * The rupee range a chart shows: zero always inside it, so a line's side of
 * zero is never lost, and at least ₹1,000 tall, so a flat morning is a flat
 * line rather than noise blown up to fill the panel.
 */
export function valueDomain(values: readonly number[], minSpan = 1_000): { lo: number; hi: number } {
  let lo = 0
  let hi = 0
  for (const v of values) {
    if (v < lo) lo = v
    if (v > hi) hi = v
  }
  const span = Math.max(hi - lo, minSpan)
  const pad = span * 0.08
  if (hi - lo < minSpan) {
    const extra = (minSpan - (hi - lo)) / 2
    return { lo: lo - extra - pad, hi: hi + extra + pad }
  }
  return { lo: lo - pad, hi: hi + pad }
}

/** The gridline values inside a domain, on a round step; zero is one of them whenever it is inside. */
export function valueTicks(domain: { lo: number; hi: number }, count = 3): number[] {
  const step = niceStep(domain.hi - domain.lo, count)
  const out: number[] = []
  for (let v = Math.ceil(domain.lo / step) * step; v <= domain.hi + 1e-9; v += step) out.push(Math.abs(v) < step / 1e6 ? 0 : v)
  return out
}

/** A gridline's rupees, as short as they read: "0", "500", "−50k", "−1L", "1.5L" (the step is round, so is the label). */
export function axisInr(v: number): string {
  const a = Math.abs(v)
  const sign = v < 0 ? '−' : ''
  const trim = (n: number) => String(Number(n.toFixed(2)))
  if (a >= 100_000) return `${sign}${trim(a / 100_000)}L`
  if (a >= 1_000) return `${sign}${trim(a / 1_000)}k`
  return `${sign}${Math.round(a)}`
}

/**
 * Label positions at the end of several lines, nudged apart so no two sit
 * closer than `gap` pixels, keeping their order and staying inside
 * [min, max]. Input and output are in the same order.
 */
export function spreadLabels(ys: readonly number[], gap: number, min = -Infinity, max = Infinity): number[] {
  const order = ys.map((y, i) => ({ y, i })).sort((a, b) => a.y - b.y || a.i - b.i)
  const placed = order.map((o) => o.y)
  for (let k = 1; k < placed.length; k++) placed[k] = Math.max(placed[k], placed[k - 1] + gap)
  // Pushed past the bottom: pull the last one back in and let it push the others up.
  if (placed.length) placed[placed.length - 1] = Math.min(placed[placed.length - 1], max)
  for (let k = placed.length - 2; k >= 0; k--) placed[k] = Math.min(placed[k], placed[k + 1] - gap)
  // Too many to fit at all: the top one stays inside, and the rest keep their spacing below it.
  if (placed.length && placed[0] < min) {
    placed[0] = min
    for (let k = 1; k < placed.length; k++) placed[k] = Math.max(placed[k], placed[k - 1] + gap)
  }
  const out = new Array<number>(ys.length)
  order.forEach((o, k) => (out[o.i] = placed[k]))
  return out
}

/**
 * A step line through a segment's points (a value holds until the next
 * point), as SVG path data; `x` and `y` map minutes and rupees to pixels.
 */
export function stepPath(points: readonly CurvePoint[], x: (m: number) => number, y: (v: number) => number): string {
  if (points.length === 0) return ''
  const f = (n: number) => n.toFixed(1)
  let d = `M${f(x(points[0].m))} ${f(y(points[0].v))}`
  for (let i = 1; i < points.length; i++) d += `H${f(x(points[i].m))}V${f(y(points[i].v))}`
  return d
}

/** "11:02–11:40", the stretches in words. */
export function gapText(gaps: readonly Gap[]): string {
  return gaps.map((g) => `${minuteLabel(g.from)}–${minuteLabel(g.to)}`).join(', ')
}

/** One account's line through the day, cut where the recorder missed. */
export interface AccountCurve {
  userId: number
  points: CurvePoint[]
  segments: CurvePoint[][]
  /** Where the line ends: the figure it closed the day (or reached now) on. */
  last: CurvePoint | null
}

export interface DayCurves {
  axis: DayAxis
  accounts: AccountCurve[]
  gaps: Gap[]
  /** "Now" on the day that is today, while it is inside the axis; else null. */
  now: number | null
  /** Whether any account has a point at all. */
  any: boolean
}

/**
 * The day's curves with each live account's line carried on to its figure
 * now, at `nowMinute` (fractional: the instant, not the minute it falls in).
 * The recorder writes a point a minute; between two, the Desk's own figure,
 * re-priced at every push, is newer, and a line that stopped at the last
 * minute sat beside a number that had moved on. So the line steps to that
 * figure now and its end, dot and label follow it, while every recorded
 * point stays what the recorder wrote. An account not in `tips` (nothing of
 * it live) ends at its last point, which is its figure. Only on the day that
 * is today, with now on the axis (`curves.now`), and only past the last
 * point: a point the recorder wrote at or after now is as new.
 *
 * Across an open gap at the end (the recorder stopped writing), the figure
 * sits on its own past the gap, not joined to a line across what nobody saw.
 */
export function withLiveTips(curves: DayCurves, tips: ReadonlyMap<number, number>, nowMinute: number): DayCurves {
  if (tips.size === 0 || curves.now == null || !Number.isFinite(nowMinute)) return curves
  let changed = false
  const accounts = curves.accounts.map((a) => {
    const v = tips.get(a.userId)
    const last = a.points[a.points.length - 1]
    if (v == null || !Number.isFinite(v) || (last && last.m >= nowMinute)) return a
    changed = true
    const tip = { m: nowMinute, v }
    const points = [...a.points, tip]
    return { ...a, points, segments: splitAtGaps(points, curves.gaps), last: tip }
  })
  return changed ? { ...curves, accounts, any: true } : curves
}

/**
 * The Day P&L chart's content from one answer: a curve per account in
 * scope (in the order asked for), the recorder's gaps among those accounts'
 * strategy runs, the axis the points need, and "now".
 */
export function dayCurves(
  res: Pick<RunPnlSeriesResponse, 'date' | 'dayStartUtc' | 'runs' | 'accounts'>,
  opts: { userIds: readonly number[]; nowMs: number; isToday: boolean; mcxCloseUtc?: string | null },
): DayCurves {
  const start = dayStartMs(res.date, res.dayStartUtc)
  const nowMinute = opts.isToday ? Math.floor((opts.nowMs - start) / 60_000) : null
  const inScope = new Set(opts.userIds)
  const runs = res.runs.filter((r) => inScope.has(r.userId))
  const gaps = recorderGaps(runs, { dayStartMs: start, nowMinute })
  const accounts = opts.userIds.flatMap((id) => {
    const s = res.accounts.find((a) => a.userId === id)
    if (!s) return []
    const points = seriesPoints(s)
    return [{ userId: id, points, segments: splitAtGaps(points, gaps), last: points[points.length - 1] ?? null }]
  })
  const minutes = [...accounts.flatMap((a) => a.points.map((p) => p.m)), ...runs.flatMap((r) => r.minutes)]
  const axis = dayAxis(minutes, mcxCloseMinute(opts.mcxCloseUtc, start))
  const now = nowMinute != null && nowMinute >= axis.from && nowMinute <= axis.to ? nowMinute : null
  return { axis, accounts, gaps, now, any: accounts.some((a) => a.points.length > 0) }
}
