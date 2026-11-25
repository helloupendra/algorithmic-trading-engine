/**
 * The entry plan of a TrendRider-family run for one day: when it can buy, at
 * which index level, and which of its day rules (if any) keeps it out today.
 *
 * Mirrors strategies/directional/trend_rider.py (`_context`, `_enter`) and
 * trend_rider_v2.py (`_enter_prev_day`), so the page states the strategy's own
 * conditions rather than a summary of them:
 *  - TrendRider (levels "opening_range"): a closed 5-minute candle beyond the
 *    first `or_minutes` range and beyond the day's open, from `first_entry`;
 *  - V2, V2 BE, V2 OB (levels "prev_day"): a closed 5-minute candle beyond
 *    yesterday's high (low) and the day's open, from 09:15;
 *  - "yesterday" is the last session with any candle; the average range (ADR)
 *    is the mean high − low of the last `adr_days` complete sessions (at least
 *    80% of their 5-minute candles), so a Muhurat hour never counts in it;
 *  - the day rules: no trade after a day wider than `skip_after_wide` × ADR;
 *    with `max_prev_range` set, only after a day narrower than that; a gap of
 *    more than `max_gap_against_pct` against a side turns that side off; with
 *    `min_range_frac` set, today's range must have reached that share of the ADR.
 *
 * A candle is named by its start (09:15 is the 09:15–09:20 candle) and the
 * strategy reads it when it closes, so the window's first and last chances are
 * five minutes after `first_entry` and `last_entry`.
 */

import { istDay, istHm, sessionsFrom, type Bar } from './runStory'

export interface TrendRiderRules {
  levels: 'prev_day' | 'opening_range'
  firstEntry: string
  lastEntry: string
  orMinutes: number
  maxEntries: number
  adrDays: number
  skipAfterWide: number
  /** 0 = off. */
  maxPrevRange: number
  /** 0 = off. */
  minRangeFrac: number
  maxGapAgainstPct: number
  /**
   * The four day rules (after a wide day, yesterday not narrow, today's range still small, a gap against).
   * False: every break is traded at its level, and the entry's reason names the rule it passed (day_filters=off,
   * the desk's paper runs from 26 Nov 2026).
   */
  dayFilters: boolean
}

const BAR_MINUTES = 5
/** 60 of the session's 75 five-minute candles (trend_rider.py FULL_SESSION_SHARE). */
const FULL_SESSION_BARS = 60

function num(v: unknown, fallback: number): number {
  const n = typeof v === 'number' ? v : typeof v === 'string' && v.trim() !== '' ? Number(v) : NaN
  return Number.isFinite(n) ? n : fallback
}

/** A yes/no setting as trend_rider.py `_switch` reads it: true/false, on/off, yes/no, 1/0. */
function onOff(v: unknown, fallback: boolean): boolean {
  if (typeof v === 'boolean') return v
  const s = String(v ?? '').trim().toLowerCase()
  if (['true', 'on', 'yes', '1'].includes(s)) return true
  if (['false', 'off', 'no', '0'].includes(s)) return false
  return fallback
}

function hm(v: unknown, auto: string): string | null {
  const s = typeof v === 'string' ? v.trim().toLowerCase() : ''
  if (s === '' || s === 'auto') return auto
  return /^\d{2}:\d{2}$/.test(s) ? s : null
}

/**
 * A TrendRider-family strategy's entry settings from its parameters (the
 * catalog's defaults, or a run's own). Null when the page cannot mirror them:
 * not a TrendRider strategy, a timeframe other than 5 minutes, or an MCX run
 * (its session and entry window are MCX's, which this does not model).
 */
export function trendRiderRules(
  strategyName: string,
  params: Record<string, unknown> | null,
  opts: { mcx?: boolean } = {},
): TrendRiderRules | null {
  if (!/^TrendRider/i.test(strategyName) || !params || opts.mcx) return null
  const tf = String(params.timeframe ?? '5m').trim().toLowerCase()
  if (tf !== '5m' && tf !== '5') return null
  if (String(params.instrument_kind ?? 'options').toLowerCase() === 'equity') return null
  const firstEntry = hm(params.first_entry, '09:15')
  const lastEntry = hm(params.last_entry, '14:30')
  if (!firstEntry || !lastEntry) return null
  return {
    levels: params.levels === 'prev_day' ? 'prev_day' : 'opening_range',
    firstEntry,
    lastEntry,
    orMinutes: num(params.or_minutes, 30),
    maxEntries: num(params.max_entries, 3),
    adrDays: num(params.adr_days, 10),
    skipAfterWide: num(params.skip_after_wide, 1.3),
    maxPrevRange: num(params.max_prev_range, 0),
    minRangeFrac: num(params.min_range_frac, 0),
    maxGapAgainstPct: num(params.max_gap_against_pct, 0.2),
    dayFilters: onOff(params.day_filters, true),
  }
}

/** Parses a catalog row's defaultParametersJson; null when it is not a JSON object. */
export function parseParams(json: string | null | undefined): Record<string, unknown> | null {
  if (!json) return null
  try {
    const v: unknown = JSON.parse(json)
    return v && typeof v === 'object' && !Array.isArray(v) ? (v as Record<string, unknown>) : null
  } catch {
    return null
  }
}

export type EntryStatus =
  /** Fewer than `adr_days` complete sessions before today: it records "no_history" and does not trade. */
  | 'no_history'
  /** A day rule keeps it out for the whole day. */
  | 'day_blocked'
  /** Its entries for the day are used up. */
  | 'max_entries'
  /** Before the first candle it may act on has closed. */
  | 'before_window'
  /** Inside the window: the next closed candle beyond a level buys. */
  | 'watching'
  /** After the last candle it may act on. */
  | 'window_closed'
  /** A day before today: the plan as it stood that day. */
  | 'past_day'

export interface EntrySide {
  /** A 5-minute close beyond this index level buys (above for the call, below for the put). */
  level: number | null
  /** What the level is: "yesterday's high", "the opening range high", "the day's open". */
  from: string
  /** Why this side cannot buy today, when a rule says so. */
  off: string | null
  /** With the day rules off: the gap rule that would have turned this side off. */
  ruledOut: string | null
}

export interface EntryPlan {
  rules: TrendRiderRules
  status: EntryStatus
  /** One sentence for the status line. */
  headline: string
  /** Why no entry can come today (status day_blocked / no_history). */
  dayOff: string | null
  /** With the day rules off: the day rule that would have kept it out today, which it now trades through. */
  ruledOut: string | null
  call: EntrySide
  put: EntrySide
  dayOpen: number | null
  /** Yesterday as the strategy reads it: the last session with any candle. */
  prevDay: string | null
  prev: Bar | null
  adr: number | null
  prevRangeAdr: number | null
  /** Today's open against yesterday's close, in percent (+ = gap up). */
  gapPct: number | null
  orHigh: number | null
  orLow: number | null
  /** Today's range so far and the range it needs first (min_range_frac × ADR), when that rule is on. */
  rangeNow: number | null
  rangeNeeded: number | null
  /** IST clocks of the first and last candle closes it may act on. */
  firstChance: string
  lastChance: string
}

function addMinutes(hhmm: string, minutes: number): string {
  const [h, m] = hhmm.split(':').map(Number)
  const t = h * 60 + m + minutes
  return `${String(Math.floor(t / 60)).padStart(2, '0')}:${String(t % 60).padStart(2, '0')}`
}

/** "23 Nov" for "2026-11-23". */
export function shortDay(day: string): string {
  return new Date(`${day}T00:00:00Z`).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', timeZone: 'UTC' })
}

const f1 = (n: number) => n.toLocaleString('en-IN', { minimumFractionDigits: 1, maximumFractionDigits: 1 })
const f2 = (n: number) => n.toFixed(2)

/**
 * The plan for `day`, from the stored 5-minute candles of earlier sessions and
 * today's closed 5-minute candles, as it stands at `nowMs`.
 * `entriesToday` is the number of entries the run has made today.
 */
export function entryPlan(
  rules: TrendRiderRules,
  history5: ReadonlyArray<Bar>,
  today5: ReadonlyArray<Bar>,
  day: string,
  nowMs: number,
  entriesToday = 0,
): EntryPlan {
  const before = history5.filter((b) => istDay(b.t) < day)
  const any = sessionsFrom(before, BAR_MINUTES, 1)
  const full = sessionsFrom(before, BAR_MINUTES, FULL_SESSION_BARS).slice(-rules.adrDays)
  const prev = any.at(-1) ?? null
  const adrRaw = full.length >= rules.adrDays ? full.reduce((s, b) => s + (b.h - b.l), 0) / full.length : null
  const adr = adrRaw != null && adrRaw > 0 ? adrRaw : null
  const prevRangeAdr = prev && adr ? (prev.h - prev.l) / adr : null

  const todays = today5.filter((b) => istDay(b.t) === day)
  const dayOpen = todays[0]?.o ?? null
  const gapPct = dayOpen != null && prev && prev.c ? ((dayOpen - prev.c) / prev.c) * 100 : null
  const orBars = Math.max(1, Math.round(rules.orMinutes / BAR_MINUTES))
  const orDone = rules.levels === 'opening_range' && todays.length >= orBars
  const orHigh = orDone ? Math.max(...todays.slice(0, orBars).map((b) => b.h)) : null
  const orLow = orDone ? Math.min(...todays.slice(0, orBars).map((b) => b.l)) : null
  const rangeNow = todays.length ? Math.max(...todays.map((b) => b.h)) - Math.min(...todays.map((b) => b.l)) : null
  const rangeNeeded = rules.dayFilters && rules.minRangeFrac > 0 && adr ? rules.minRangeFrac * adr : null

  // The first chance: the close of the first candle at or after first_entry; the opening range
  // must have closed first (len(day) > or_bars: the candle after the range's last one).
  const orEnd = addMinutes('09:15', orBars * BAR_MINUTES)
  const firstStart = rules.levels === 'opening_range' && orEnd > rules.firstEntry ? orEnd : rules.firstEntry
  const firstChance = addMinutes(firstStart, BAR_MINUTES)
  const lastChance = addMinutes(rules.lastEntry, BAR_MINUTES)

  // The day rules, in the strategy's order. Too little history stops it whatever the rules say.
  let dayRule: string | null = null
  const noHistory = !adr || !prev
  if (noHistory) {
    dayRule = `fewer than ${rules.adrDays} complete sessions are stored before today, so it has no average range to read`
  } else if (prevRangeAdr != null && prevRangeAdr > rules.skipAfterWide) {
    dayRule =
      `yesterday (${shortDay(istDay(prev.t))}) moved ${f1(prev.h - prev.l)} points, ${f2(prevRangeAdr)} × the average range of ` +
      `${f1(adr)}; it does not trade the day after one wider than ${rules.skipAfterWide} ×`
  } else if (rules.maxPrevRange > 0 && prevRangeAdr != null && prevRangeAdr >= rules.maxPrevRange) {
    dayRule =
      `yesterday (${shortDay(istDay(prev.t))}) moved ${f1(prev.h - prev.l)} points, ${f2(prevRangeAdr)} × the average range of ` +
      `${f1(adr)}; it trades only after a day narrower than ${rules.maxPrevRange} ×`
  }
  const dayOff = noHistory || rules.dayFilters ? dayRule : null
  const ruledOut = !noHistory && !rules.dayFilters ? dayRule : null

  const gapOff = (side: 'call' | 'put'): string | null => {
    if (gapPct == null) return null
    const against = side === 'call' ? -gapPct : gapPct
    if (against <= rules.maxGapAgainstPct) return null
    return `opened ${Math.abs(gapPct).toFixed(2)}% ${gapPct < 0 ? 'down' : 'up'}, more than ${rules.maxGapAgainstPct}% against this side`
  }

  const levelHigh = rules.levels === 'prev_day' ? (prev?.h ?? null) : orHigh
  const levelLow = rules.levels === 'prev_day' ? (prev?.l ?? null) : orLow
  const highName = rules.levels === 'prev_day' ? "yesterday's high" : 'the opening range high'
  const lowName = rules.levels === 'prev_day' ? "yesterday's low" : 'the opening range low'
  // The close must clear both the level and the day's open: whichever is further.
  const gate = (side: 'call' | 'put') =>
    rules.dayFilters ? { off: gapOff(side), ruledOut: null } : { off: null, ruledOut: gapOff(side) }
  const call: EntrySide =
    levelHigh != null && dayOpen != null
      ? { level: Math.max(levelHigh, dayOpen), from: dayOpen > levelHigh ? "the day's open" : highName, ...gate('call') }
      : { level: null, from: highName, ...gate('call') }
  const put: EntrySide =
    levelLow != null && dayOpen != null
      ? { level: Math.min(levelLow, dayOpen), from: dayOpen < levelLow ? "the day's open" : lowName, ...gate('put') }
      : { level: null, from: lowName, ...gate('put') }

  const now = istHm(nowMs)
  const isToday = istDay(nowMs) === day
  let status: EntryStatus
  if (!adr || !prev) status = 'no_history'
  else if (dayOff) status = 'day_blocked'
  else if (!isToday) status = 'past_day'
  else if (entriesToday >= rules.maxEntries) status = 'max_entries'
  else if (now < firstChance) status = 'before_window'
  else if (now > lastChance) status = 'window_closed'
  else status = 'watching'

  const sides = (): string => {
    const parts: string[] = []
    if (!call.off) parts.push(call.level != null ? `call above ${f1(call.level)}` : `call above ${highName}`)
    if (!put.off) parts.push(put.level != null ? `put below ${f1(put.level)}` : `put below ${lowName}`)
    return parts.length ? parts.join(' or ') : 'no side is open today'
  }
  const headline = ((): string => {
    switch (status) {
      case 'no_history':
        return `No entry today: ${dayOff}.`
      case 'day_blocked':
        return `No entry today: ${dayOff}.`
      case 'max_entries':
        return `No more entries today: ${rules.maxEntries} made.`
      case 'before_window':
        return rules.levels === 'opening_range' && !orDone
          ? `Opening range forming until ${orEnd}; from ${firstChance}, a 5-minute close beyond it and the day's open buys.`
          : `From ${firstChance}: a 5-minute close, ${sides()}${rules.dayFilters ? '' : '; day rules off'}.`
      case 'watching':
        return `Buys on a 5-minute close: ${sides()} (until ${lastChance})${rules.dayFilters ? '' : '; day rules off'}.`
      case 'window_closed':
        return `No new entries after ${lastChance}.`
      case 'past_day':
        return `That day it bought on a 5-minute close between ${firstChance} and ${lastChance}: ${sides()}.`
    }
  })()

  return {
    rules,
    status,
    headline,
    dayOff,
    ruledOut,
    call,
    put,
    dayOpen,
    prevDay: prev ? istDay(prev.t) : null,
    prev,
    adr,
    prevRangeAdr,
    gapPct,
    orHigh,
    orLow,
    rangeNow,
    rangeNeeded,
    firstChance,
    lastChance,
  }
}
