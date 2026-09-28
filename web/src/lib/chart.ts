/**
 * Markets → Chart as data: which resolutions and ranges a symbol offers, how
 * a range becomes a window of candles, how live 1-minute bars fold into the
 * stored series, and which timeframes the structure layer reads. The page
 * only renders these, so each rule is pinned by chart.test.ts rather than by
 * a screenshot of an empty chart.
 */

import type { CoverageRow } from './queries'
import type { SmcLayers } from '../components/SmcChart'

export type Resolution = '1' | '5' | '15' | 'D'

export const RESOLUTIONS: ReadonlyArray<{ key: Resolution; label: string; minutes: number | null }> = [
  { key: '1', label: '1m', minutes: 1 },
  { key: '5', label: '5m', minutes: 5 },
  { key: '15', label: '15m', minutes: 15 },
  { key: 'D', label: 'Day', minutes: null },
]

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
  D: ['1M', '3M', '1Y', 'ALL'],
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

/**
 * Whether a resolution has anything to draw. Intraday resolutions are built
 * from the live 1-minute bars too, so a symbol recording live has every one
 * of them; a day chart needs stored daily candles.
 */
export function hasResolution(by: Partial<Record<Resolution, CoverageRow[]>>, key: Resolution): boolean {
  if (key === 'D') return (by.D?.length ?? 0) > 0
  const live = (by['1'] ?? []).some((r) => r.source === 'live')
  return (by[key]?.length ?? 0) > 0 || live
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
  const rows = [...(by[resolution] ?? []), ...(resolution !== 'D' ? by['1'] ?? [] : [])]
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

/**
 * The stored series with today's live bars laid after its last candle, cut to
 * the range. Stored candles end where the last archive ran; live bars beyond
 * that fill in the rest, rolled up to the chart's resolution.
 */
export function stitch(
  stored: readonly Candle[],
  live: readonly Candle[],
  opts: { symbol: string; resolution: Resolution; range: RangeKey; fromDate?: string },
): Candle[] {
  const clean = stored.filter((c) => !isPreOpen(opts.symbol, c.timeUtc))
  const sessions = RANGES.find((r) => r.key === opts.range)?.sessions ?? null
  if (opts.resolution === 'D') {
    const sorted = [...clean].sort((a, b) => a.timeUtc.localeCompare(b.timeUtc))
    return sessions == null ? sorted : sorted.slice(-sessions)
  }
  const minutes = RESOLUTIONS.find((r) => r.key === opts.resolution)?.minutes ?? 1
  const rolled = rollUp(live.filter((c) => !isPreOpen(opts.symbol, c.timeUtc)), minutes)
  const storedMax = clean.reduce((m, c) => (c.timeUtc > m ? c.timeUtc : m), '')
  const from = opts.fromDate ? `${opts.fromDate}T00:00:00Z` : ''
  const merged = [...clean, ...rolled.filter((c) => c.timeUtc > storedMax)]
    .filter((c) => !from || c.timeUtc >= from)
    .sort((a, b) => a.timeUtc.localeCompare(b.timeUtc))
  return sessions == null ? merged : lastSessions(merged, sessions)
}

/**
 * The forming candle moved by a pushed price between two polls of the bars:
 * its close becomes the price and its high and low stretch to take it in.
 * A price past the forming candle's bucket opens the next candle (open, high,
 * low and close all at that price), by the same bucket rule as rollUp, but
 * only on the same session: a new day's first candle waits for the bars,
 * which bring its real open. Nothing changes for a day chart, a pre-open
 * print, or a price older than the forming candle.
 */
export function withLiveTick(
  candles: readonly Candle[],
  tick: { lastTradedPrice: number | null; exchangeTimestampUtc: string | null; receivedAtMs: number } | undefined,
  opts: { symbol: string; resolution: Resolution },
): readonly Candle[] {
  const price = tick?.lastTradedPrice
  const minutes = RESOLUTIONS.find((r) => r.key === opts.resolution)?.minutes ?? null
  const last = candles[candles.length - 1]
  if (!tick || price == null || price <= 0 || minutes == null || !last) return candles
  const stamped = tick.exchangeTimestampUtc ? Date.parse(tick.exchangeTimestampUtc) : NaN
  const t = Number.isNaN(stamped) ? tick.receivedAtMs : stamped
  const timeUtc = new Date(t).toISOString()
  if (isPreOpen(opts.symbol, timeUtc)) return candles

  const span = minutes * 60_000
  const start = Date.parse(last.timeUtc)
  if (Number.isNaN(start) || t < start) return candles
  if (t < start + span) {
    if (price === last.close && price <= last.high && price >= last.low) return candles
    return [...candles.slice(0, -1), { ...last, close: price, high: Math.max(last.high, price), low: Math.min(last.low, price) }]
  }
  const bucket = t - (t % span)
  const next = new Date(bucket).toISOString()
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
 * The timeframes the structure layer reads, highest first and the chart's
 * own last: a day's pullback is an hour's whole trend, so a minute chart
 * carries the 15m and the day above it, each read from its own closed candles.
 */
export function structureTimeframes(resolution: Resolution): string {
  const above: Record<Resolution, Resolution[]> = { '1': ['D', '15'], '5': ['D', '15'], '15': ['D'], D: [] }
  return [...above[resolution], resolution].map((r) => (r === 'D' ? '1D' : `${r}m`)).join(',')
}
