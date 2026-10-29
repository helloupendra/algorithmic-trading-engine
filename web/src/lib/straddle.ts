/**
 * Markets → Option chain → Straddle & IV as data: the API's answers, the
 * days a trader can compare, the lines the charts draw (aligned by time of
 * day, in rupees or as a share of the first capture), what the IV rank is
 * measured over and how old the numbers are. The view only renders these,
 * so each rule is pinned by straddle.test.ts rather than by a screenshot.
 */

import { ageText, istStamp, istTime, sourceLabel } from './optionChain'
import type { Freshness } from './optionChain'

/* ------------------------------------------------------------------ the API -- */

/** One capture minute of the ATM straddle (StraddlePointResponse). */
export interface StraddlePoint {
  capturedUtc: string
  spotPrice: number
  atTheMoneyStrike: number
  callPrice: number | null
  /** "mid" of bid and ask, or "ltp" when the leg had no usable quote. */
  callPriceBasis: 'mid' | 'ltp' | null
  putPrice: number | null
  putPriceBasis: 'mid' | 'ltp' | null
  /** Null when either leg is missing. */
  straddlePrice: number | null
  /** Percent. */
  atTheMoneyIv: number | null
}

/** One IST session of the straddle, read from one expiry (GET /api/Straddle). */
export interface StraddleSeries {
  underlying: string
  date: string
  expiryDate: string | null
  daysToExpiry: number | null
  isToday: boolean
  marketOpen: boolean
  serverUtc: string
  firstCapturedUtc: string | null
  lastCapturedUtc: string | null
  sources: string[]
  midPricedPoints: number
  /** Today's lot size, not necessarily the day's. */
  lotSize: number | null
  points: StraddlePoint[]
}

/** One recorded session's ATM IV near the open and the close. */
export interface IvHistoryDay {
  date: string
  expiryDate: string
  daysToExpiry: number
  openIv: number | null
  openCapturedUtc: string | null
  closeIv: number | null
  closeCapturedUtc: string | null
  source: string | null
}

/** GET /api/Straddle/iv-history. */
export interface IvHistory {
  underlying: string
  serverUtc: string
  days: IvHistoryDay[]
  currentIv: number | null
  currentDate: string | null
  currentCapturedUtc: string | null
  currentBasis: 'latest' | 'close' | null
  ivRank: number | null
  ivPercentile: number | null
  daysUsed: number
  rangeLow: number | null
  rangeHigh: number | null
}

/* ------------------------------------------------------------- comparing -- */

/** How many past days can be laid over the chosen one. */
export const MAX_COMPARE = 3

/**
 * The days laid over the chosen one, by slot. A slot keeps its day (and so
 * its line style) until that day is taken off: removing the first of three
 * must not restyle the other two, or the reader's "the dotted one" changes
 * meaning under them.
 */
export type CompareSlots = readonly (string | null)[]

export const NO_COMPARE: CompareSlots = Array.from({ length: MAX_COMPARE }, () => null)

/** A day on (into the first free slot) or off (its slot freed). Full slots leave the set as it is. */
export function toggleCompare(slots: CompareSlots, day: string): CompareSlots {
  const at = slots.indexOf(day)
  if (at >= 0) return slots.map((d, i) => (i === at ? null : d))
  const free = slots.indexOf(null)
  if (free < 0) return slots
  return slots.map((d, i) => (i === free ? day : d))
}

/** A whole set of days, in order, replacing whatever was laid over. */
export function compareWith(days: readonly string[]): CompareSlots {
  return NO_COMPARE.map((_, i) => days[i] ?? null)
}

/** The recorded sessions before `selected`, newest first. */
export function daysBefore(days: readonly IvHistoryDay[], selected: string, count: number): string[] {
  return days
    .map((d) => d.date)
    .filter((d) => d < selected)
    .sort()
    .reverse()
    .slice(0, count)
}

/**
 * The last `count` expiry days before `selected`, newest first: sessions on
 * which the contract recorded was the one expiring that day. The straddle
 * decays to nearly nothing on those, and comparing today's against them is
 * how a seller reads an expiry morning.
 */
export function expiryDaysBefore(days: readonly IvHistoryDay[], selected: string, count: number): string[] {
  return days
    .filter((d) => d.date < selected && d.date === d.expiryDate)
    .map((d) => d.date)
    .sort()
    .reverse()
    .slice(0, count)
}

/* ----------------------------------------------------------------- lines -- */

const IST_OFFSET_MS = 330 * 60_000
const DAY_MS = 86_400_000
const WEEKDAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']

/** Minutes since IST midnight of an instant: 09:15 IST is 555. */
export function istMinuteOfDay(iso: string): number {
  const ms = Date.parse(iso) + IST_OFFSET_MS
  return Math.floor((((ms % DAY_MS) + DAY_MS) % DAY_MS) / 60_000)
}

export type LineField = 'straddle' | 'iv'
export type PremiumScale = 'rupees' | 'percentOfFirst'

/** One drawn point: the minute of the IST day, and the value (null leaves a gap). */
export interface LinePoint {
  minute: number
  value: number | null
}

/**
 * A session as a line against the time of day, so different days sit over
 * each other minute for minute. The API sends at most one point a minute.
 *
 * `percentOfFirst` reads each straddle as a share of the session's first
 * priced one: a Monday straddle and an expiry-day one differ several times
 * over in rupees, and only as shares do their decays have the same shape to
 * compare. IV is always in percent points; the scale does not apply to it.
 */
export function sessionLine(points: readonly StraddlePoint[], field: LineField, scale: PremiumScale = 'rupees'): LinePoint[] {
  const raw = points.map((p) => ({ minute: istMinuteOfDay(p.capturedUtc), value: field === 'iv' ? p.atTheMoneyIv : p.straddlePrice }))
  if (field === 'iv' || scale === 'rupees') return raw
  const first = raw.find((p) => p.value != null && p.value > 0)?.value
  if (first == null) return raw.map((p) => ({ minute: p.minute, value: null }))
  return raw.map((p) => ({ minute: p.minute, value: p.value == null ? null : (p.value / first) * 100 }))
}

/** The session's first and last priced straddle, and the change between them. */
export function straddleChange(points: readonly StraddlePoint[]): { first: StraddlePoint; last: StraddlePoint; percent: number } | null {
  const priced = points.filter((p) => p.straddlePrice != null && p.straddlePrice > 0)
  if (priced.length === 0) return null
  const first = priced[0]
  const last = priced[priced.length - 1]
  return { first, last, percent: (last.straddlePrice! / first.straddlePrice! - 1) * 100 }
}

/** The newest point with an ATM IV. */
export function lastIv(points: readonly StraddlePoint[]): StraddlePoint | null {
  for (let i = points.length - 1; i >= 0; i--) if (points[i].atTheMoneyIv != null) return points[i]
  return null
}

/* ----------------------------------------------------------------- words -- */

/** "Tue 27 Oct" for a "yyyy-mm-dd" session. */
export function dayLabel(dateIso: string): string {
  const [y, m, d] = dateIso.split('-').map(Number)
  return `${WEEKDAYS[new Date(Date.UTC(y, m - 1, d)).getUTCDay()]} ${d} ${MONTHS[m - 1]}`
}

/** A year of sessions: an IV rank over fewer is a fact about fewer. */
export const YEAR_OF_SESSIONS = 250

/**
 * What the IV rank and percentile are measured over, in words the panel can
 * print beside them: "over 31 days" while the history is short, "over the
 * last year" once it is not, and why there is none when there is none.
 */
export function standingBasis(history: Pick<IvHistory, 'daysUsed' | 'ivRank' | 'currentIv'>): string {
  if (history.currentIv == null) return 'no ATM IV recorded yet'
  if (history.daysUsed === 0) return 'no earlier session to rank against yet'
  if (history.daysUsed >= YEAR_OF_SESSIONS) return `over the last year (${history.daysUsed} sessions)`
  return `over ${history.daysUsed} ${history.daysUsed === 1 ? 'day' : 'days'} only — a year is about ${YEAR_OF_SESSIONS}`
}

/**
 * How each leg was read, for the line under the chart: the mid of bid and
 * ask is what the straddle could be dealt at; a last trade can be minutes old.
 */
export function pricingNote(series: Pick<StraddleSeries, 'points' | 'midPricedPoints'>): string | null {
  const priced = series.points.filter((p) => p.straddlePrice != null).length
  if (priced === 0) return null
  if (series.midPricedPoints >= priced) return `Both legs at the mid of bid and ask in every minute (${priced}).`
  const rest = priced - series.midPricedPoints
  return `Both legs at the mid of bid and ask in ${series.midPricedPoints} of ${priced} minutes; in ${rest}, at least one leg at its last trade (no two-sided quote).`
}

/** A capture this old while the market is open means the recorder is behind (the chain page's limit). */
export const CAPTURE_BEHIND_SECONDS = 180

/**
 * The line under the head that says how old the straddle is, on the
 * server's clock as the page believes it now (`nowMs`), the way the chain's
 * own freshness line works.
 */
export function straddleFreshness(series: StraddleSeries, nowMs: number): Freshness {
  const last = series.lastCapturedUtc
  const source = series.sources.length > 0 ? series.sources.map(sourceLabel).join(' + ') : null
  const from = source ? ` · ${source}` : ''

  if (!last) {
    return {
      tone: 'empty',
      text: series.isToday ? `no capture recorded for ${series.underlying} today yet` : `nothing recorded for ${series.underlying} on ${dayLabel(series.date)}`,
    }
  }

  if (!series.isToday) {
    return { tone: 'closed', text: `${dayLabel(series.date)} · a closed session · ${series.points.length} captures, last ${istTime(last)} IST${from}` }
  }

  if (!series.marketOpen) {
    return { tone: 'closed', text: `market closed — last capture ${istStamp(last)} IST${from}` }
  }

  const age = (nowMs - Date.parse(last)) / 1000
  if (age > CAPTURE_BEHIND_SECONDS) {
    return { tone: 'stale', text: `stale — last capture ${istTime(last)} IST (${ageText(age)} old)${from} — the recorder may be behind` }
  }
  return { tone: 'snapshot', text: `recording · last capture ${istTime(last)} IST (${ageText(age)} old)${from} · one capture a minute` }
}
