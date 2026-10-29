/**
 * Markets → Chart as data: which resolutions and ranges a symbol offers, how
 * a range becomes a window of candles, how live 1-minute bars fold into the
 * stored series, how 30m, 1h and weekly candles are built from the stored
 * ones, which indicators are drawn, and which timeframes the structure layer
 * reads. The page only renders these, so each rule is pinned by
 * chart.test.ts rather than by a screenshot of an empty chart.
 */

import type { CoverageRow } from './queries'
import type { SmcLayers } from '../components/SmcChart'

export type Resolution = '1' | '5' | '15' | '30' | '60' | 'D' | 'W'

export const RESOLUTIONS: ReadonlyArray<{ key: Resolution; label: string; minutes: number | null }> = [
  { key: '1', label: '1m', minutes: 1 },
  { key: '5', label: '5m', minutes: 5 },
  { key: '15', label: '15m', minutes: 15 },
  { key: '30', label: '30m', minutes: 30 },
  { key: '60', label: '1h', minutes: 60 },
  { key: 'D', label: 'Day', minutes: null },
  { key: 'W', label: 'Week', minutes: null },
]

/** Minutes in one candle; null for the day and the week. */
export function minutesOf(resolution: Resolution): number | null {
  return RESOLUTIONS.find((r) => r.key === resolution)?.minutes ?? null
}

export function isIntraday(resolution: Resolution): boolean {
  return minutesOf(resolution) != null
}

export type RangeKey = '1D' | '5D' | '1M' | '3M' | '1Y' | 'ALL'

/**
 * Intraday ranges are SESSIONS, not calendar days: "5D" is the last five days
 * that traded, so a weekend never eats the range. `fetchDays` is the
 * calendar window asked of the server, wide enough to hold those sessions.
 */
export const RANGES: ReadonlyArray<{ key: RangeKey; label: string; sessions: number | null; fetchDays: number | null }> = [
  { key: '1D', label: '1D', sessions: 1, fetchDays: 8 },
  { key: '5D', label: '5D', sessions: 5, fetchDays: 12 },
  { key: '1M', label: '1M', sessions: 22, fetchDays: 34 },
  { key: '3M', label: '3M', sessions: 66, fetchDays: 96 },
  { key: '1Y', label: '1Y', sessions: 250, fetchDays: 370 },
  { key: 'ALL', label: 'All', sessions: null, fetchDays: null },
]

/** The ranges that make sense at a resolution: 1m over a year is a wall of noise. */
export const RANGES_FOR: Record<Resolution, readonly RangeKey[]> = {
  '1': ['1D', '5D'],
  '5': ['1D', '5D', '1M'],
  '15': ['1D', '5D', '1M', '3M'],
  '30': ['1D', '5D', '1M', '3M'],
  // Seven candles is not a day worth charting by the hour.
  '60': ['5D', '1M', '3M', '1Y'],
  D: ['1M', '3M', '1Y', 'ALL'],
  W: ['1Y', 'ALL'],
}

/** One candle as the chart draws it. */
export interface Candle {
  timeUtc: string
  open: number
  high: number
  low: number
  close: number
  volume: number | null
}

/** The chart's layers. Price alone, or with the Smart Money Concepts (SMC) read on it. */
export type ChartLayer = 'price' | 'smc'

/**
 * The layer a URL asks for (?layer=smc), price otherwise. The layer was called
 * "Structure" until 28 Sep, which did not tell a trader that it switched SMC
 * on; links saved with ?layer=structure still open it.
 */
export function chartLayer(value: string | null | undefined): ChartLayer {
  return value === 'smc' || value === 'structure' ? 'smc' : 'price'
}

/**
 * What the installation holds for one symbol, by resolution. The coverage
 * endpoint spells the live 1-minute bars "1m" and the stored candles "1";
 * both are the chart's 1m.
 */
export function coverageByResolution(rows: readonly CoverageRow[] | undefined, symbol: string): Partial<Record<Resolution, CoverageRow[]>> {
  const by: Partial<Record<Resolution, CoverageRow[]>> = {}
  for (const r of rows ?? []) {
    if (r.symbol !== symbol) continue
    const key: Resolution | null =
      r.resolution === '1m' || r.resolution === '1' ? '1' : r.resolution === '5' ? '5' : r.resolution === '15' ? '15' : r.resolution === 'D' ? 'D' : null
    if (key) (by[key] ??= []).push(r)
  }
  return by
}

/** What 30m and 1h candles are built from, coarsest first: each divides both, from either open. */
const BUILT_FROM: readonly Resolution[] = ['15', '5', '1']

/**
 * Whether a resolution has anything to draw. Intraday resolutions are built
 * from the live 1-minute bars too, so a symbol recording live has every one
 * of them; 30m and 1h are built from any stored intraday candles; the day and
 * the week need stored daily candles.
 */
export function hasResolution(by: Partial<Record<Resolution, CoverageRow[]>>, key: Resolution): boolean {
  if (key === 'D' || key === 'W') return (by.D?.length ?? 0) > 0
  const live = (by['1'] ?? []).some((r) => r.source === 'live')
  if (key === '30' || key === '60') return live || BUILT_FROM.some((k) => (by[k]?.length ?? 0) > 0)
  return (by[key]?.length ?? 0) > 0 || live
}

/**
 * The stored candles a resolution is drawn from. Nothing stores 30m, 1h or
 * weekly candles (the nightly archive writes 1, 5 and 15 minutes), so those
 * are built in the browser: 30m and 1h from the coarsest stored intraday
 * series the symbol has (15m, else 5m, else 1m), the week from the days.
 */
export function storedResolution(by: Partial<Record<Resolution, CoverageRow[]>>, resolution: Resolution): Resolution {
  if (resolution === 'W') return 'D'
  if (resolution !== '30' && resolution !== '60') return resolution
  return BUILT_FROM.find((k) => (by[k] ?? []).some((r) => r.source !== 'live')) ?? '15'
}

/**
 * The first calendar day to ask for: a range counts back from the last bar
 * the symbol HAS at this resolution, not from the clock, so "1D" on a Sunday
 * or after the close is the last session rather than an empty day. Undefined
 * for "All".
 */
export function rangeFromDate(by: Partial<Record<Resolution, CoverageRow[]>>, resolution: Resolution, range: RangeKey, nowIso: string): string | undefined {
  const fetchDays = RANGES.find((r) => r.key === range)?.fetchDays
  if (fetchDays == null) return undefined
  const stored = storedResolution(by, resolution)
  const rows = [...(by[stored] ?? []), ...(isIntraday(resolution) ? by['1'] ?? [] : [])]
  const anchor = rows.reduce((latest, r) => (r.toUtc > latest ? r.toUtc : latest), '') || nowIso
  const d = new Date(anchor)
  d.setUTCDate(d.getUTCDate() - (fetchDays - 1))
  return d.toISOString().slice(0, 10)
}

/**
 * NSE and BSE print from 09:00 IST in the pre-open auction: a handful of
 * ticks at indicative prices that draw a wick to nowhere on the first
 * candle. The regular session starts 09:15 (03:45Z); nothing before it is a
 * bar. MCX has no pre-open and is left alone.
 */
export function isPreOpen(symbol: string, timeUtc: string): boolean {
  if (!symbol.startsWith('NSE:') && !symbol.startsWith('BSE:')) return false
  return timeUtc.slice(11, 16) < '03:45'
}

/** MCX's evening close while the US is on winter time, 23:55 IST; 23:30 (18:00Z) in its summer. */
const MCX_LATEST_CLOSE = '18:25'

/** A session answer's close as HH:MM UTC when it reads as an MCX evening close; null otherwise. */
function mcxCloseHm(sessionCloseUtc: string | null | undefined): string | null {
  const ms = sessionCloseUtc ? Date.parse(sessionCloseUtc) : NaN
  if (Number.isNaN(ms)) return null
  const hm = new Date(ms).toISOString().slice(11, 16)
  return hm > '10:00' && hm <= '18:30' ? hm : null
}

/**
 * Whether a moment is outside the symbol's regular session: the rule the API
 * keeps live bars to (LiveDataService.InSession), for a price pushed between
 * polls of those bars.
 *
 * - NSE and BSE: 09:15 to 15:30 IST (03:45 to 10:00Z). Before is the pre-open
 *   auction. After, the vendors go on repeating the close with stamps that
 *   move forward until the feeds stop late in the evening, and BSE replays the
 *   previous close stamped 00:00 IST (18:30Z the day before).
 * - MCX: 09:00 IST (03:30Z) to the evening close. `mcxCloseUtc` is the MCX
 *   session answer's close; without it, the later of the two seasonal closes,
 *   so a real candle is never held back.
 *
 * Other symbols are left alone. A special session (Muhurat trading) is not a
 * regular one: its candles come with the bars poll.
 */
export function outsideSession(symbol: string, timeUtc: string, mcxCloseUtc?: string | null): boolean {
  const exchange = symbol.slice(0, symbol.indexOf(':') + 1).toUpperCase()
  const hm = timeUtc.slice(11, 16)
  if (exchange === 'NSE:' || exchange === 'BSE:') return hm < '03:45' || hm >= '10:00'
  if (exchange === 'MCX:') return hm < '03:30' || hm >= (mcxCloseHm(mcxCloseUtc) ?? MCX_LATEST_CLOSE)
  return false
}

/**
 * The last N sessions present in the data (a session is one UTC date, which
 * is the IST day for every Indian bar). For a single session, a day with only
 * a bar or two, the first minutes of today or a stray print, is skipped in
 * favour of the last day that traded properly.
 */
export function lastSessions<T extends Candle>(candles: readonly T[], n: number): T[] {
  const byDay = new Map<string, T[]>()
  for (const c of candles) {
    const day = c.timeUtc.slice(0, 10)
    const list = byDay.get(day)
    if (list) list.push(c)
    else byDay.set(day, [c])
  }
  const days = [...byDay.keys()].sort()
  if (n === 1) {
    const full = [...days].reverse().find((d) => (byDay.get(d)?.length ?? 0) >= 10) ?? days[days.length - 1]
    return full ? byDay.get(full)! : []
  }
  return days.slice(-n).flatMap((d) => byDay.get(d)!)
}

/** Live 1-minute bars folded into a coarser bucket, by the server's own rollup rule. */
export function rollUp(bars: readonly Candle[], minutes: number): Candle[] {
  if (minutes <= 1) return [...bars]
  const span = minutes * 60_000
  const out = new Map<number, Candle>()
  for (const b of [...bars].sort((a, c) => a.timeUtc.localeCompare(c.timeUtc))) {
    const t = new Date(b.timeUtc).getTime()
    const bucket = t - (t % span)
    const cur = out.get(bucket)
    if (!cur) {
      out.set(bucket, { ...b, timeUtc: new Date(bucket).toISOString() })
    } else {
      cur.high = Math.max(cur.high, b.high)
      cur.low = Math.min(cur.low, b.low)
      cur.close = b.close
      cur.volume = (cur.volume ?? 0) + (b.volume ?? 0)
    }
  }
  return [...out.values()]
}

/** A symbol's session open in minutes after 00:00 UTC: MCX's 09:00 IST, and 09:15 for NSE, BSE and the rest. */
function sessionOpenUtcMinutes(symbol: string): number {
  return symbol.toUpperCase().startsWith('MCX:') ? 210 : 225
}

/**
 * The start of the candle a moment falls in, counted from the session's open
 * rather than from the UTC clock. rollUp floors to the clock, which is the
 * server's rule for live bars and lands on the open for 1, 5 and 15 minutes
 * (09:15 IST is 225 minutes past midnight UTC, 09:00 is 210, and 15 divides
 * both). 30 and 60 do not: an NSE hour floored to the clock would run
 * 09:30-10:30. So candles count from the open, as the alerts'
 * SessionBarAggregator does: NSE's hours start 09:15, 10:15, ... 15:15 (that
 * one cut short by the 15:30 close), its half hours 09:15, 09:45, ...; MCX's
 * hours 09:00, 10:00, ...
 */
export function bucketStart(symbol: string, timeMs: number, minutes: number): number {
  const span = minutes * 60_000
  const open = timeMs - (timeMs % 86_400_000) + sessionOpenUtcMinutes(symbol) * 60_000
  return open + Math.floor((timeMs - open) / span) * span
}

/** Volume summed; null only when neither side reports any (an index). */
function addVolume(a: number | null, b: number | null): number | null {
  return a == null && b == null ? null : (a ?? 0) + (b ?? 0)
}

/** Candles folded into one: the first open, the last close, the extremes and the summed volume. */
function fold(candles: readonly Candle[], key: (c: Candle) => string, timeOf: (first: Candle) => string): Candle[] {
  const out: Candle[] = []
  let current = ''
  for (const c of [...candles].sort((a, b) => a.timeUtc.localeCompare(b.timeUtc))) {
    const k = key(c)
    const last = out[out.length - 1]
    if (!last || k !== current) {
      out.push({ ...c, timeUtc: timeOf(c) })
      current = k
    } else {
      last.high = Math.max(last.high, c.high)
      last.low = Math.min(last.low, c.low)
      last.close = c.close
      last.volume = addVolume(last.volume, c.volume)
    }
  }
  return out
}

/**
 * Intraday candles folded into coarser ones that start at the session's
 * open (bucketStart). A candle with missing minutes is built from the ones it
 * has, and one with none is not drawn, so a feed outage shows as the gap it
 * was rather than as a flat candle.
 */
export function aggregate(candles: readonly Candle[], symbol: string, minutes: number): Candle[] {
  const start = (c: Candle) => new Date(bucketStart(symbol, Date.parse(c.timeUtc), minutes)).toISOString()
  return fold(candles, start, start)
}

/**
 * The IST date a candle belongs to, which names its session: NSE and BSE
 * (09:15-15:30) and MCX (09:00 to its evening close) each run inside one IST
 * day, the rule the strategies' session_date uses. A daily candle stamped at
 * 00:00 IST (18:30Z the day before) is that IST day.
 */
export function istDate(timeUtc: string): string {
  return new Date(Date.parse(timeUtc) + 19_800_000).toISOString().slice(0, 10)
}

/** The Monday of a candle's IST week, YYYY-MM-DD. */
export function istWeek(timeUtc: string): string {
  const d = new Date(`${istDate(timeUtc)}T00:00:00Z`)
  d.setUTCDate(d.getUTCDate() - ((d.getUTCDay() + 6) % 7))
  return d.toISOString().slice(0, 10)
}

/**
 * Daily candles folded into weeks: the days an IST week traded, Monday to
 * Friday with a holiday simply missing, the first day's open and the last
 * day's close. A week is drawn at its first trading day. A weekend session
 * (Muhurat trading has fallen on a Sunday) joins the week it closes.
 */
export function weekly(days: readonly Candle[]): Candle[] {
  return fold(days, (c) => istWeek(c.timeUtc), (c) => c.timeUtc)
}

/**
 * The stored series with today's live bars laid after its last candle, cut to
 * the range. Stored candles end where the last archive ran; live bars beyond
 * that fill in the rest, rolled up to the stored series' own resolution
 * (`source`, see storedResolution) and then, for 30m and 1h, the whole series
 * folded into candles from the session's open. Live bars reach the last
 * candle that way, so today's hour keeps forming as the minutes arrive.
 *
 * The day and the week come from stored daily candles alone, as the day chart
 * always has. A daily candle is a date, not a minute (stamped 00:00 IST, or
 * 00:00 UTC by the option history), so the pre-open rule is not put to it:
 * it used to be, and threw away every daily candle stamped at 00:00 UTC.
 */
export function stitch(
  stored: readonly Candle[],
  live: readonly Candle[],
  opts: { symbol: string; resolution: Resolution; range: RangeKey; fromDate?: string; source?: Resolution },
): Candle[] {
  const sessions = RANGES.find((r) => r.key === opts.range)?.sessions ?? null
  const minutes = minutesOf(opts.resolution)
  if (minutes == null) {
    const days = [...stored].sort((a, b) => a.timeUtc.localeCompare(b.timeUtc))
    if (opts.resolution === 'D') return sessions == null ? days : days.slice(-sessions)
    // The window starts at fromDate, so a week that began before it is missing
    // days. Strictly after: a Monday stamped 00:00 IST is 18:30Z on the
    // Sunday, which is before the window too.
    const from = opts.fromDate
    const weeks = weekly(days)
    return from ? weeks.filter((w) => istWeek(w.timeUtc) > from) : weeks
  }
  const clean = stored.filter((c) => !isPreOpen(opts.symbol, c.timeUtc))
  const grain = minutesOf(opts.source ?? storedResolution({}, opts.resolution)) ?? minutes
  const rolled = rollUp(live.filter((c) => !isPreOpen(opts.symbol, c.timeUtc)), grain)
  const storedMax = clean.reduce((m, c) => (c.timeUtc > m ? c.timeUtc : m), '')
  const from = opts.fromDate ? `${opts.fromDate}T00:00:00Z` : ''
  const merged = [...clean, ...rolled.filter((c) => c.timeUtc > storedMax)]
    .filter((c) => !from || c.timeUtc >= from)
    .sort((a, b) => a.timeUtc.localeCompare(b.timeUtc))
  const candles = grain === minutes ? merged : aggregate(merged, opts.symbol, minutes)
  return sessions == null ? candles : lastSessions(candles, sessions)
}

/**
 * The forming candle moved by a pushed price between two polls of the bars:
 * its close becomes the price and its high and low stretch to take it in.
 * A price past the forming candle's bucket opens the next candle (open, high,
 * low and close all at that price), counted from the session's open
 * (bucketStart, which is rollUp's rule for 1, 5 and 15 minutes), but only on
 * the same session: a new day's first candle waits for the bars, which bring
 * its real open. Nothing changes for a day or week chart, a price outside
 * the regular session (outsideSession: the pre-open, the quotes after the
 * close, BSE's midnight replay), or a price older than the forming candle.
 *
 * Until 28 Sep only the pre-open was kept out, and every quote after 15:30
 * opened a flat candle at the close (15:30, 15:35, ...) all evening: the ones
 * the API stopped storing on 20 Sep, drawn again in the browser.
 */
export function withLiveTick(
  candles: readonly Candle[],
  tick: { lastTradedPrice: number | null; exchangeTimestampUtc: string | null; receivedAtMs: number } | undefined,
  opts: { symbol: string; resolution: Resolution; mcxCloseUtc?: string | null },
): readonly Candle[] {
  const price = tick?.lastTradedPrice
  const minutes = minutesOf(opts.resolution)
  const last = candles[candles.length - 1]
  if (!tick || price == null || price <= 0 || minutes == null || !last) return candles
  const stamped = tick.exchangeTimestampUtc ? Date.parse(tick.exchangeTimestampUtc) : NaN
  const t = Number.isNaN(stamped) ? tick.receivedAtMs : stamped
  const timeUtc = new Date(t).toISOString()
  if (outsideSession(opts.symbol, timeUtc, opts.mcxCloseUtc)) return candles

  const span = minutes * 60_000
  const start = Date.parse(last.timeUtc)
  if (Number.isNaN(start) || t < start) return candles
  if (t < start + span) {
    if (price === last.close && price <= last.high && price >= last.low) return candles
    return [...candles.slice(0, -1), { ...last, close: price, high: Math.max(last.high, price), low: Math.min(last.low, price) }]
  }
  const next = new Date(bucketStart(opts.symbol, t, minutes)).toISOString()
  if (next.slice(0, 10) !== last.timeUtc.slice(0, 10)) return candles
  return [...candles, { timeUtc: next, open: price, high: price, low: price, close: price, volume: null }]
}

/** What the structure layer draws and how it reads a break; only `standingZonesOnly` changes the request. */
export interface StructureSettings {
  layers: SmcLayers
  breakOn: 'close' | 'wick'
  inducement: 'last' | 'first'
  /**
   * Not a layer: this one changes what is FETCHED, because a zone the market
   * has spent is history rather than a level, and on a month of 5-minute
   * candles the spent ones are 99% of the gaps and 98% of the blocks. It
   * starts on for that reason; turning it off asks for the whole history.
   */
  standingZonesOnly: boolean
}

export const STRUCTURE_DEFAULTS: StructureSettings = {
  // The zone layers start on except the delivery band, which is the reading
  // a chart can be read without.
  layers: {
    swings: true,
    breaks: true,
    inducements: true,
    minorSwings: false,
    higher: true,
    orderBlocks: true,
    fvg: true,
    orderFlow: false,
  },
  breakOn: 'close',
  inducement: 'last',
  standingZonesOnly: true,
}

/**
 * The resolutions the structure layer reads. It reads stored candles on the
 * server (GET /api/Smc/ladder), and nothing stores 30m, 1h or weekly ones,
 * so on those the layer is not offered rather than drawn on nothing.
 */
export type StructureResolution = '1' | '5' | '15' | 'D'

export function hasStructure(resolution: Resolution): resolution is StructureResolution {
  return resolution === '1' || resolution === '5' || resolution === '15' || resolution === 'D'
}

/**
 * The timeframes the structure layer reads, highest first and the chart's
 * own last: a day's pullback is an hour's whole trend, so a minute chart
 * carries the 15m and the day above it, each read from its own closed candles.
 */
export function structureTimeframes(resolution: StructureResolution): string {
  const above: Record<StructureResolution, StructureResolution[]> = { '1': ['D', '15'], '5': ['D', '15'], '15': ['D'], D: [] }
  return [...above[resolution], resolution].map((r) => (r === 'D' ? '1D' : `${r}m`)).join(',')
}

// ------------------------------------------------------------- indicators

/** What the chart can draw over the price, and in panes under it (RSI, MACD). */
export type IndicatorKey = 'sma' | 'ema' | 'vwap' | 'bollinger' | 'supertrend' | 'rsi' | 'macd'

export interface IndicatorSettings {
  on: Record<IndicatorKey, boolean>
  /** The two periods a reader sets; the others are drawn at their standard numbers. */
  smaPeriod: number
  emaPeriod: number
}

/** Shortest and longest SMA or EMA a reader can ask for; the alerts take EMAs to 500 too. */
export const PERIOD_MIN = 2
export const PERIOD_MAX = 500

/** A first visit is the plain chart: nothing on until the reader turns it on. */
export const INDICATOR_DEFAULTS: IndicatorSettings = {
  on: { sma: false, ema: false, vwap: false, bollinger: false, supertrend: false, rsi: false, macd: false },
  smaPeriod: 50,
  emaPeriod: 20,
}

/** A whole period in range, or null. */
export function parsePeriod(value: unknown): number | null {
  const n = typeof value === 'string' && value.trim() !== '' ? Number(value) : value
  return typeof n === 'number' && Number.isInteger(n) && n >= PERIOD_MIN && n <= PERIOD_MAX ? n : null
}

/**
 * The choice as stored, field by field: whatever is missing or not a valid
 * value takes its default, so a key written by an older console, or edited
 * by hand, never leaves the chart without a setting.
 */
export function parseIndicatorSettings(raw: string | null | undefined): IndicatorSettings {
  let stored: unknown = null
  try {
    stored = raw ? JSON.parse(raw) : null
  } catch {
    stored = null
  }
  const s = stored && typeof stored === 'object' ? (stored as Partial<Record<keyof IndicatorSettings, unknown>>) : {}
  const on = s.on && typeof s.on === 'object' ? (s.on as Partial<Record<IndicatorKey, unknown>>) : {}
  const keys = Object.keys(INDICATOR_DEFAULTS.on) as IndicatorKey[]
  return {
    on: Object.fromEntries(keys.map((k) => [k, on[k] === true])) as Record<IndicatorKey, boolean>,
    smaPeriod: parsePeriod(s.smaPeriod) ?? INDICATOR_DEFAULTS.smaPeriod,
    emaPeriod: parsePeriod(s.emaPeriod) ?? INDICATOR_DEFAULTS.emaPeriod,
  }
}

/**
 * Remembered per browser (localStorage), which is why every read and write is
 * guarded: a private window or blocked site data must not take the chart down.
 */
const INDICATORS_KEY = 'openfno.chart.indicators'

function storage(): Storage | null {
  try {
    return window.localStorage
  } catch {
    return null
  }
}

export function readIndicatorSettings(store: Pick<Storage, 'getItem'> | null = storage()): IndicatorSettings {
  try {
    return parseIndicatorSettings(store?.getItem(INDICATORS_KEY))
  } catch {
    return INDICATOR_DEFAULTS
  }
}

export function writeIndicatorSettings(settings: IndicatorSettings, store: Pick<Storage, 'setItem'> | null = storage()): void {
  try {
    store?.setItem(INDICATORS_KEY, JSON.stringify(settings))
  } catch {
    // Storage can be full or blocked; the choice still holds while the page is open.
  }
}

/**
 * What is drawn of the choice at a resolution. VWAP is a session's average,
 * so a day or a week chart has none; the choice itself is kept, and comes
 * back on the next intraday chart.
 */
export function drawnIndicators(settings: IndicatorSettings, resolution: Resolution): IndicatorSettings {
  if (isIntraday(resolution) || !settings.on.vwap) return settings
  return { ...settings, on: { ...settings.on, vwap: false } }
}

/** Whether a choice draws anything under the price, which gives the chart more height. */
export function hasLowerPanes(settings: IndicatorSettings | undefined): boolean {
  return !!settings && (settings.on.rsi || settings.on.macd)
}
