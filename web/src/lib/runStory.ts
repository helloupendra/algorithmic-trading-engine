/**
 * The run story as data: one run's day — its index candles folded to a
 * timeframe, the levels a TrendRider-family entry was read from, where each
 * trade went in and out, and, for an open position, the index stop and trail
 * as they stand now. RunStory.tsx only draws these, so every rule here is
 * pinned by runStory.test.ts rather than by a screenshot.
 *
 * Times are UTC milliseconds in and out; the chart is handed IST-shifted
 * seconds (chartTime) so its axis reads the exchange's clock.
 */

import type { LiveActivity, LivePosition } from './types'

export const IST_OFFSET_MS = 330 * 60_000
const SESSION_OPEN_MIN = 9 * 60 + 15
const SESSION_CLOSE_MIN = 15 * 60 + 30

/** One candle: `t` is its start in UTC milliseconds. */
export interface Bar {
  t: number
  o: number
  h: number
  l: number
  c: number
}

/** "2026-11-17" — the IST date of an instant. */
export function istDay(ms: number): string {
  return new Date(ms + IST_OFFSET_MS).toISOString().slice(0, 10)
}

/** Minutes after IST midnight. */
export function istMinute(ms: number): number {
  const d = new Date(ms + IST_OFFSET_MS)
  return d.getUTCHours() * 60 + d.getUTCMinutes()
}

/** "09:20" — the IST clock of an instant. */
export function istHm(ms: number): string {
  return new Date(ms + IST_OFFSET_MS).toISOString().slice(11, 16)
}

/** The chart's time for an instant: IST-shifted whole seconds, so the axis shows IST. */
export function chartTime(ms: number): number {
  return Math.floor((ms + IST_OFFSET_MS) / 1000)
}

/**
 * The session's bars of one IST day (09:15–15:30), folded into `minutes`-long
 * candles that start at 09:15 — the strategy's own grid, so a 5-minute candle
 * here is the candle the strategy read. Ascending, one per start.
 */
export function foldSession(bars: Bar[], day: string, minutes: number): Bar[] {
  const out = new Map<number, Bar>()
  const sorted = [...bars].sort((a, b) => a.t - b.t)
  for (const b of sorted) {
    if (istDay(b.t) !== day) continue
    const m = istMinute(b.t)
    if (m < SESSION_OPEN_MIN || m >= SESSION_CLOSE_MIN) continue
    const slot = SESSION_OPEN_MIN + Math.floor((m - SESSION_OPEN_MIN) / minutes) * minutes
    const start = b.t - (m - slot) * 60_000 - (b.t % 60_000)
    const cur = out.get(start)
    if (!cur) out.set(start, { t: start, o: b.o, h: b.h, l: b.l, c: b.c })
    else {
      cur.h = Math.max(cur.h, b.h)
      cur.l = Math.min(cur.l, b.l)
      cur.c = b.c
    }
  }
  return [...out.values()].sort((a, b) => a.t - b.t)
}

/** The start of the `minutes`-long session candle an instant falls in (UTC ms). */
export function candleStart(ms: number, minutes: number): number {
  const m = istMinute(ms)
  const slot = SESSION_OPEN_MIN + Math.floor((Math.max(m, SESSION_OPEN_MIN) - SESSION_OPEN_MIN) / minutes) * minutes
  return ms - (m - slot) * 60_000 - (ms % 60_000)
}

/* ------------------------------------------------------------ TrendRider */

export interface TrendRiderLevels {
  prevHigh: number | null
  prevLow: number | null
  dayOpen: number | null
  adr: number | null
}

/** One TrendRider / TrendRiderV2 entry, read from its OPEN_GROUP signal. */
export interface TrendRiderEntry {
  groupId: string
  atMs: number
  direction: 'BUY' | 'SELL'
  entrySpot: number | null
  levels: TrendRiderLevels
  /** The index exits, in multiples of the ADR (0 = off). */
  stopAdr: number
  armAdr: number
  giveAdr: number
  /** "prev_day" (V2) or "opening_range" (TrendRider). */
  levelsKind: string | null
  entryOfDay: number | null
  reason: string
}

function num(v: unknown): number | null {
  const n = typeof v === 'number' ? v : typeof v === 'string' && v.trim() !== '' ? Number(v) : NaN
  return Number.isFinite(n) ? n : null
}

/** The signal's metadata, whether the API sent it parsed or as JSON text. */
export function activityMeta(a: LiveActivity): Record<string, unknown> | null {
  if (a.metadata && typeof a.metadata === 'object') return a.metadata
  if (a.metadataJson) {
    try {
      const parsed: unknown = JSON.parse(a.metadataJson)
      return parsed && typeof parsed === 'object' ? (parsed as Record<string, unknown>) : null
    } catch {
      return null
    }
  }
  return null
}

/** Every TrendRider-family entry in a run's activity, oldest first, one per group. */
export function trendRiderEntries(activity: ReadonlyArray<LiveActivity>): TrendRiderEntry[] {
  const byGroup = new Map<string, TrendRiderEntry>()
  for (const a of activity) {
    if (a.type !== 'OPEN_GROUP') continue
    const meta = activityMeta(a)
    const tr = meta?.trend_rider as Record<string, unknown> | undefined
    if (!tr || typeof tr !== 'object') continue
    const exits = (tr.index_exits ?? {}) as Record<string, unknown>
    const atMs = Date.parse(a.atUtc)
    if (Number.isNaN(atMs)) continue
    byGroup.set(a.groupId, {
      groupId: a.groupId,
      atMs,
      direction: String(meta?.direction ?? 'BUY').toUpperCase() === 'SELL' ? 'SELL' : 'BUY',
      entrySpot: num(tr.entry_spot),
      levels: {
        prevHigh: num(tr.prev_high),
        prevLow: num(tr.prev_low),
        dayOpen: num(tr.day_open),
        adr: num(tr.adr),
      },
      stopAdr: num(exits.stop_adr) ?? 0,
      armAdr: num(exits.trail_arm_adr) ?? 0,
      giveAdr: num(exits.trail_giveback_adr) ?? 0,
      levelsKind: typeof tr.levels === 'string' ? tr.levels : null,
      entryOfDay: num(tr.entry_of_day),
      reason: String(meta?.reason ?? a.text ?? ''),
    })
  }
  return [...byGroup.values()].sort((a, b) => a.atMs - b.atMs)
}

/** Where an open TrendRider position's index exits stand now. */
export interface ExitPlan {
  direction: 'BUY' | 'SELL'
  entry: number
  adr: number
  /** Index level of the stop (null when off). */
  stop: number | null
  /** Index level at which the trail arms (null when off). */
  armAt: number | null
  /** Best favourable move so far, in index points (≥ 0). */
  best: number
  bestLevel: number
  armed: boolean
  /** Index level the trail sells at, once armed. */
  trail: number | null
  /** The level that sells the position now, and which rule it is. */
  active: number | null
  activeKind: 'stop' | 'trail' | null
  /** Index points between the spot and the active level (positive: still clear of it). */
  room: number | null
}

/**
 * The stop and trail of an entry, replayed over the index candles since it
 * (their highs and lows are the extreme ticks, which is what the engine's
 * per-tick check sees) and the spot now. Mirrors strategies/index_exits.py
 * `check_tick`: favour = spot − entry for a call (entry − spot for a put);
 * stop at stop_adr × ADR against; the trail arms at trail_arm_adr × ADR in
 * favour and sells trail_giveback_adr × ADR back from the best.
 */
export function exitPlan(entry: TrendRiderEntry, barsSince: ReadonlyArray<Bar>, spot: number | null): ExitPlan | null {
  const e = entry.entrySpot
  const adr = entry.levels.adr
  if (e == null || adr == null || adr <= 0) return null
  const buy = entry.direction === 'BUY'
  const favour = (p: number) => (buy ? p - e : e - p)
  let best = 0
  for (const b of barsSince) best = Math.max(best, favour(buy ? b.h : b.l))
  if (spot != null) best = Math.max(best, favour(spot))
  const stop = entry.stopAdr > 0 ? (buy ? e - entry.stopAdr * adr : e + entry.stopAdr * adr) : null
  const armAt = entry.armAdr > 0 ? (buy ? e + entry.armAdr * adr : e - entry.armAdr * adr) : null
  const armed = entry.armAdr > 0 && entry.giveAdr > 0 && best >= entry.armAdr * adr
  const trail = armed ? (buy ? e + best - entry.giveAdr * adr : e - best + entry.giveAdr * adr) : null
  // The tighter of the two is the one that sells first.
  let active: number | null = stop
  let activeKind: ExitPlan['activeKind'] = stop != null ? 'stop' : null
  if (trail != null && (stop == null || (buy ? trail > stop : trail < stop))) {
    active = trail
    activeKind = 'trail'
  }
  const room = spot != null && active != null ? (buy ? spot - active : active - spot) : null
  return {
    direction: entry.direction,
    entry: e,
    adr,
    stop,
    armAt,
    best,
    bestLevel: buy ? e + best : e - best,
    armed,
    trail,
    active,
    activeKind,
    room,
  }
}

/** Yesterday's high/low and the day's open against the last closed candle: which side V2 would buy. */
export function v2Check(levels: TrendRiderLevels, lastClose: number | null) {
  const { prevHigh, prevLow, dayOpen } = levels
  const aboveHigh = lastClose != null && prevHigh != null ? lastClose > prevHigh : null
  const belowLow = lastClose != null && prevLow != null ? lastClose < prevLow : null
  const aboveOpen = lastClose != null && dayOpen != null ? lastClose > dayOpen : null
  const belowOpen = lastClose != null && dayOpen != null ? lastClose < dayOpen : null
  const side: 'BUY' | 'SELL' | null = aboveHigh && aboveOpen ? 'BUY' : belowLow && belowOpen ? 'SELL' : null
  return { aboveHigh, belowLow, aboveOpen, belowOpen, side }
}

/* ------------------------------------------------------------- the trades */

/** One trade (one group) as the chart and the table show it. */
export interface TradeMark {
  groupId: string
  positionIds: number[]
  symbol: string
  label: string
  optionType: string | null
  /** Bought or sold to open (the owner buys; other strategies may sell). */
  side: 'BUY' | 'SELL'
  lots: number
  qty: number
  open: boolean
  entryMs: number
  entryPrice: number
  exitMs: number | null
  exitPrice: number | null
  ltp: number | null
  pnl: number
  pnlPercent: number | null
  indexAtEntry: number | null
  indexAtExit: number | null
  entryReason: string | null
  exitReason: string | null
  /** Booked by hand (the order's metadata says so), not a live fill. */
  manual: boolean
}

/** The close of the last bar that started at or before an instant. */
export function indexAt(bars: ReadonlyArray<Bar>, ms: number): number | null {
  let found: number | null = null
  for (const b of bars) {
    if (b.t <= ms) found = b.c
    else break
  }
  return found
}

/** A position's own trades, one per group, oldest first. */
export function tradeMarks(
  positions: ReadonlyArray<LivePosition>,
  activity: ReadonlyArray<LiveActivity>,
  bars: ReadonlyArray<Bar>,
): TradeMark[] {
  const sorted = [...bars].sort((a, b) => a.t - b.t)
  const reasons = new Map<string, { open?: string; close?: string; manual?: boolean; entrySpot?: number | null }>()
  for (const a of activity) {
    const r = reasons.get(a.groupId) ?? {}
    if (a.type === 'OPEN_GROUP') {
      const meta = activityMeta(a)
      r.open = a.text
      r.manual = meta?.manual_backfill === true
      const tr = meta?.trend_rider as Record<string, unknown> | undefined
      r.entrySpot = num(tr?.entry_spot) ?? num(meta?.entry_spot)
    } else if (a.type === 'CLOSE_GROUP') r.close = a.text
    reasons.set(a.groupId, r)
  }
  const byGroup = new Map<string, LivePosition[]>()
  for (const p of positions) {
    if (p.status === 'Carried') continue
    byGroup.set(p.groupId, [...(byGroup.get(p.groupId) ?? []), p])
  }
  const out: TradeMark[] = []
  for (const [groupId, legs] of byGroup) {
    const first = [...legs].sort((a, b) => Date.parse(a.openedUtc) - Date.parse(b.openedUtc))[0]
    const entryMs = Date.parse(first.openedUtc)
    const open = legs.some((l) => l.status === 'Open')
    const closedAt = open ? null : Math.max(...legs.map((l) => (l.closedUtc ? Date.parse(l.closedUtc) : 0)))
    const r = reasons.get(groupId) ?? {}
    const pnl = legs.reduce((s, l) => s + (l.pnl ?? 0), 0)
    const outlay = legs.reduce((s, l) => s + (l.entryValue ?? l.entryPrice * l.quantity), 0)
    out.push({
      groupId,
      positionIds: legs.filter((l) => l.status === 'Open').map((l) => l.id),
      symbol: first.symbol,
      label: first.contract?.label ?? first.symbol,
      optionType: first.contract?.optionType ?? null,
      side: first.side === 'SELL' ? 'SELL' : 'BUY',
      lots: first.lots,
      qty: first.quantity,
      open,
      entryMs,
      entryPrice: first.entryPrice,
      exitMs: closedAt && closedAt > 0 ? closedAt : null,
      exitPrice: open ? null : first.exitPrice,
      ltp: first.ltp,
      pnl,
      pnlPercent: outlay > 0 ? (pnl / outlay) * 100 : null,
      indexAtEntry: r.entrySpot ?? indexAt(sorted, entryMs),
      indexAtExit: closedAt ? indexAt(sorted, closedAt) : null,
      entryReason: r.open ?? null,
      exitReason: r.close ?? null,
      manual: !!r.manual,
    })
  }
  return out.sort((a, b) => a.entryMs - b.entryMs)
}

/** One trade as it stood at a replay's clock, priced off its option's 1-minute closes. */
export interface TradeAt {
  /** The entry had happened by then. */
  bought: boolean
  /** The exit had happened by then. */
  sold: boolean
  /** The option's price then: its last close before the clock, the exit once sold. */
  price: number | null
  pnl: number
  pnlPercent: number | null
}

/**
 * Where a trade stood at `cursorMs` (null = now). Before the entry nothing is
 * at stake; once sold the P&L is the booked one; in between it is the option's
 * last 1-minute close against the entry, times the quantity (simple P&L).
 */
export function tradeAt(trade: TradeMark, optionBars: ReadonlyArray<Bar>, cursorMs: number | null): TradeAt {
  if (cursorMs == null) {
    return { bought: true, sold: !trade.open, price: trade.open ? trade.ltp : trade.exitPrice, pnl: trade.pnl, pnlPercent: trade.pnlPercent }
  }
  const bought = trade.entryMs < cursorMs
  const sold = !trade.open && trade.exitMs != null && trade.exitMs < cursorMs
  if (!bought) {
    let last: number | null = null
    for (const b of optionBars) if (b.t < cursorMs) last = b.c
    return { bought, sold: false, price: last, pnl: 0, pnlPercent: null }
  }
  if (sold) return { bought, sold, price: trade.exitPrice, pnl: trade.pnl, pnlPercent: trade.pnlPercent }
  let last: number | null = null
  for (const b of optionBars) if (b.t < cursorMs && b.t >= candleStart(trade.entryMs, 1)) last = b.c
  const price = last ?? trade.entryPrice
  const sign = trade.side === 'SELL' ? -1 : 1
  const pnl = sign * (price - trade.entryPrice) * trade.qty
  const outlay = trade.entryPrice * trade.qty
  return { bought, sold, price, pnl, pnlPercent: outlay > 0 ? (pnl / outlay) * 100 : null }
}

/* ------------------------------------------------------------ the engine */

export interface EngineState {
  label: string
  tone: 'live' | 'pos' | 'warn' | 'neutral' | 'accent'
  detail: string | null
}

/**
 * What the strategy is doing now, in one phrase. `openTrades` counts the
 * positions the strategy manages; `handBooked` the open ones entered by hand,
 * which it does not manage and so do not make it "in trade".
 */
export function engineStatus(
  isActive: boolean,
  plans: ReadonlyArray<ExitPlan>,
  openTrades: number,
  nowMs: number,
  lastEntryHm = '14:30',
  handBooked = 0,
): EngineState {
  if (!isActive) return { label: 'Stopped', tone: 'warn', detail: null }
  const byHand = handBooked > 0 ? `${handBooked} booked by hand, not managed` : null
  if (openTrades > 0) {
    const trailing = plans.find((p) => p.armed)
    if (trailing) {
      return {
        label: 'Trailing active',
        tone: 'pos',
        detail: trailing.active != null ? `sells ${trailing.direction === 'BUY' ? 'below' : 'above'} ${trailing.active.toFixed(1)}` : null,
      }
    }
    const p = plans[0]
    return {
      label: 'In trade · stop active',
      tone: 'accent',
      detail: p?.stop != null ? `stop ${p.stop.toFixed(1)}${p.armAt != null ? `, trail arms at ${p.armAt.toFixed(1)}` : ''}` : null,
    }
  }
  return istHm(nowMs) <= lastEntryHm
    ? { label: 'Waiting for signal', tone: 'live', detail: byHand }
    : { label: 'No new entries today', tone: 'neutral', detail: byHand ?? `after ${lastEntryHm} IST` }
}

/** The average daily range (high − low) of the last `sessions` daily candles before `day`. */
export function adrFrom(daily: ReadonlyArray<Bar>, day: string, sessions = 10): number | null {
  const before = daily.filter((b) => istDay(b.t) < day).sort((a, b) => a.t - b.t).slice(-sessions)
  if (before.length === 0) return null
  return before.reduce((s, b) => s + (b.h - b.l), 0) / before.length
}

/* ------------------------------------------------------------- the lines */

export interface Level {
  price: number
  title: string
  /** The name on a phone's narrow ladder ("Y-High" for "Yday high"); the title when absent. */
  short?: string
  /** `check`: a level the strategy did not use, shown because it differs from one it did. */
  kind: 'prev' | 'open' | 'entry' | 'stop' | 'trail' | 'arm' | 'best' | 'check'
}

/**
 * Levels closer than `tolerance` (a share of the price) become one line with
 * both titles, so no two labels sit on top of each other on the price axis.
 * The first level's kind (in input order) colours the merged line.
 */
export function mergeLevels(levels: ReadonlyArray<Level>, tolerance = 0.0004): Level[] {
  const sorted = [...levels].filter((l) => Number.isFinite(l.price)).sort((a, b) => a.price - b.price)
  const out: Array<Level & { order: number }> = []
  for (const l of sorted) {
    const order = levels.indexOf(l)
    const last = out[out.length - 1]
    if (last && Math.abs(l.price - last.price) <= last.price * tolerance) {
      last.title = `${last.title} · ${l.title}`
      if (order < last.order) {
        last.kind = l.kind
        last.order = order
      }
      continue
    }
    out.push({ ...l, order })
  }
  return out.map(({ order: _order, ...l }) => l)
}

/** The previous session's close from daily candles ending on or before `day`. */
export function previousClose(daily: ReadonlyArray<Bar>, day: string): number | null {
  const before = daily.filter((b) => istDay(b.t) < day).sort((a, b) => a.t - b.t)
  return before.length ? before[before.length - 1].c : null
}

/* ------------------------------------------------------- sessions and events */

/**
 * One bar per full session (09:15–15:30 IST) from intraday candles: the
 * day's first open, highest high, lowest low and last close. A day with fewer
 * than `minBars` candles in the session (a Muhurat hour, a day the recorder
 * missed) is left out, so it never stands in for "yesterday". Built from the
 * stored intraday candles because the stored daily candles can lag days behind.
 */
export function sessionsFrom(bars: ReadonlyArray<Bar>, minutes = 5, minBars = 60): Bar[] {
  const byDay = new Map<string, Bar[]>()
  for (const b of bars) {
    const m = istMinute(b.t)
    if (m < SESSION_OPEN_MIN || m >= SESSION_CLOSE_MIN) continue
    const d = istDay(b.t)
    byDay.set(d, [...(byDay.get(d) ?? []), b])
  }
  const out: Bar[] = []
  for (const [, list] of byDay) {
    const day = [...list].sort((a, b) => a.t - b.t)
    const slots = new Set(day.map((b) => Math.floor((istMinute(b.t) - SESSION_OPEN_MIN) / minutes)))
    if (slots.size < minBars) continue
    out.push({
      t: day[0].t,
      o: day[0].o,
      h: Math.max(...day.map((b) => b.h)),
      l: Math.min(...day.map((b) => b.l)),
      c: day[day.length - 1].c,
    })
  }
  return out.sort((a, b) => a.t - b.t)
}

/**
 * When an entry's trail armed: the first 1-minute bar from the entry on whose
 * high (a call) or low (a put) the index stood trail_arm_adr × ADR in favour.
 * Null when it has not armed, or the entry has no trail.
 */
export function armEvent(entry: TrendRiderEntry, bars: ReadonlyArray<Bar>): { ms: number; level: number } | null {
  const e = entry.entrySpot
  const adr = entry.levels.adr
  if (e == null || adr == null || adr <= 0 || !(entry.armAdr > 0) || !(entry.giveAdr > 0)) return null
  const buy = entry.direction === 'BUY'
  const level = buy ? e + entry.armAdr * adr : e - entry.armAdr * adr
  const from = candleStart(entry.atMs, 1)
  for (const b of [...bars].sort((x, y) => x.t - y.t)) {
    if (b.t < from) continue
    if (buy ? b.h >= level : b.l <= level) return { ms: b.t, level }
  }
  return null
}

/**
 * Where each label of a price ladder goes: as close as it can to its own
 * price's y, never closer than `gap` to the next, inside [gap/2, height − gap/2].
 * `wanted` is in any order; the answer is in the same order.
 */
export function ladderLayout(wanted: ReadonlyArray<number>, height: number, gap: number): number[] {
  const order = wanted.map((y, i) => ({ y, i })).sort((a, b) => a.y - b.y)
  const lo = gap / 2
  const hi = Math.max(lo, height - gap / 2)
  const ys = order.map((o) => Math.min(Math.max(o.y, lo), hi))
  // Down: each label at least `gap` below the one above it.
  for (let k = 1; k < ys.length; k++) ys[k] = Math.max(ys[k], ys[k - 1] + gap)
  // Up from the bottom, if that pushed the last ones past the edge.
  if (ys.length > 0 && ys[ys.length - 1] > hi) {
    ys[ys.length - 1] = hi
    for (let k = ys.length - 2; k >= 0; k--) ys[k] = Math.min(ys[k], ys[k + 1] - gap)
  }
  const out = new Array<number>(wanted.length)
  order.forEach((o, k) => (out[o.i] = ys[k]))
  return out
}
