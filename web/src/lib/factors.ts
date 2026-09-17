/**
 * Market factors: the page's types and the arithmetic behind its readings.
 *
 * Everything here is a pure function over the API's answers (api/MarketFactors
 * and api/OptionChain/view), so each reading the page shows — how far NIFTY
 * is from its call wall, whether FIIs are net short, what gap GIFT Nifty
 * points to — can be tested without a browser.
 */

import type { OptionChain } from './types'
import { formatOi } from './movers'

export interface DatasetStatus {
  dataset: string
  lastAttemptUtc: string | null
  lastSuccessUtc: string | null
  newestDay: string | null
  lastMessage: string | null
  lastFailed: boolean
}

export interface CashSide {
  buy: number
  sell: number
  net: number
}

export interface CashDay {
  date: string
  fii: CashSide | null
  dii: CashSide | null
}

export interface ParticipantPosition {
  clientType: string
  futureIndexLong: number
  futureIndexShort: number
  futureIndexNet: number
  futureIndexLongPercent: number | null
  futureIndexNetChange: number | null
  optionIndexCallLong: number
  optionIndexCallShort: number
  optionIndexPutLong: number
  optionIndexPutShort: number
  futureStockLong: number
  futureStockShort: number
}

export interface ParticipantDay {
  date: string
  groups: ParticipantPosition[]
}

export interface MarketFlows {
  serverUtc: string
  cash: CashDay[]
  participants: ParticipantDay[]
  status: DatasetStatus[]
}

export interface FuturesDay {
  date: string
  expiry: string
  close: number
  previousClose: number
  priceChangePercent: number | null
  openInterest: number
  openInterestChange: number
  openInterestChangePercent: number | null
  buildUp: string
}

export interface FuturesLive {
  symbol: string
  lastPrice: number | null
  openInterest: number | null
  asOfUtc: string | null
  sourceKey: string | null
  isFresh: boolean
  baselineDate: string | null
  priceChangePercent: number | null
  openInterestChange: number | null
  openInterestChangePercent: number | null
  buildUp: string | null
}

export interface IndexFutures {
  underlying: string
  latest: FuturesDay | null
  history: FuturesDay[]
  live: FuturesLive | null
}

export interface StockBuildUp {
  underlying: string
  priceChangePercent: number | null
  openInterestChangePercent: number | null
  close: number
}

export interface MarketFutures {
  serverUtc: string
  latestDay: string | null
  indices: IndexFutures[]
  stocks: Record<string, StockBuildUp[]>
  stockCounts: Record<string, number>
  status: DatasetStatus[]
}

export interface GlobalCue {
  symbol: string
  name: string
  group: string
  currency: string | null
  lastPrice: number | null
  previousClose: number | null
  change: number | null
  changePercent: number | null
  asOfUtc: string | null
  error: string | null
}

export interface GlobalCues {
  serverUtc: string
  fetchedUtc: string
  gift: GlobalCue
  giftExpiry: string | null
  giftContractsTraded: number | null
  nseFutureClose: number | null
  nseFutureCloseDate: string | null
  indicatedGapPoints: number | null
  indicatedGapPercent: number | null
  markets: GlobalCue[]
  sourceNote: string
}

export interface MarketEvent {
  id: number | null
  date: string
  timeIst: string | null
  region: string
  category: string
  title: string
  importance: number
  notes: string | null
  source: string | null
  /** "event" | "holiday" | "expiry" */
  kind: string
}

export interface MarketEvents {
  serverUtc: string
  from: string
  to: string
  events: MarketEvent[]
}

export interface SaveMarketEvent {
  date: string
  timeIst: string | null
  region: string
  category: string
  title: string
  importance: number
  notes: string | null
  source: string | null
}

export const BUILD_UPS = ['Long build-up', 'Short build-up', 'Short covering', 'Long unwinding'] as const

export const EVENT_CATEGORIES = ['RBI policy', 'Fed policy', 'US CPI', 'India CPI', 'Budget', 'Election', 'Results', 'Other'] as const

/** Underlyings whose option chain the levels section can read. */
export const LEVEL_UNDERLYINGS = ['NIFTY', 'BANKNIFTY', 'SENSEX', 'FINNIFTY', 'MIDCPNIFTY'] as const

export const DATASET_LABELS: Record<string, string> = {
  'participant-oi': 'Participant-wise OI (NSE)',
  'futures-bhavcopy': 'F&O bhavcopy futures (NSE)',
  'fii-dii-cash': 'FII/DII cash (NSE)',
}

/**
 * What our own tests on NIFTY history found for each factor, so a number on
 * this page is never read as a proven signal. Updated when a study finishes.
 */
export const RESEARCH_NOTES: Record<string, { tested: boolean; text: string }> = {
  levels: {
    tested: false,
    text:
      'OI walls and max pain as buy/sell levels are not tested yet. PCR shifts and OI change near ATM were tested as NIFTY option-buying signals (2021–2025): no profit after costs.',
  },
  futures: { tested: false, text: 'Futures build-up has not been tested on our data yet.' },
  flows: { tested: false, text: 'FII/DII cash and FII futures positioning have not been tested on our data yet.' },
  global: { tested: false, text: 'GIFT Nifty and overseas markets have not been tested on our data yet.' },
  events: {
    tested: false,
    text: 'Event days are not tested yet. Selling an iron fly on NIFTY expiry days (2021–2025) broke even after costs.',
  },
}

/** Today's date in IST as yyyy-MM-dd, whatever the browser's time zone. */
export function istDate(now: Date = new Date()): string {
  const ist = new Date(now.getTime() + 330 * 60_000)
  return ist.toISOString().slice(0, 10)
}

/** Rupees crore with a sign and Indian grouping: +₹3,618 cr. */
export function formatCrore(value: number | null | undefined): string {
  if (value == null || Number.isNaN(value)) return '—'
  const sign = value > 0 ? '+' : value < 0 ? '−' : ''
  return `${sign}₹${Math.round(Math.abs(value)).toLocaleString('en-IN')} cr`
}

/** Contracts with a sign, in lakhs and crores: −2.88 L. */
export function formatSignedContracts(value: number | null | undefined): string {
  if (value == null || Number.isNaN(value)) return '—'
  const sign = value > 0 ? '+' : value < 0 ? '−' : ''
  return `${sign}${formatOi(Math.abs(value))}`
}

export type Tone = 'pos' | 'neg' | 'warn' | 'neutral'

export function signTone(value: number | null | undefined): Tone {
  if (value == null || Number.isNaN(value) || value === 0) return 'neutral'
  return value > 0 ? 'pos' : 'neg'
}

/** How a participant group sits in index futures, in words a desk uses. */
export function participantStance(p: ParticipantPosition): { label: string; tone: Tone } {
  const total = p.futureIndexLong + p.futureIndexShort
  if (total === 0) return { label: 'No positions', tone: 'neutral' }
  const longShare = p.futureIndexLong / total
  if (longShare >= 0.55) return { label: 'Net long', tone: 'pos' }
  if (longShare <= 0.45) return { label: 'Net short', tone: 'neg' }
  return { label: 'Balanced', tone: 'neutral' }
}

/** Points and percent from the spot to a level; positive when the level is above. */
export function distanceTo(
  spot: number | null | undefined,
  level: number | null | undefined,
): { points: number; percent: number } | null {
  if (spot == null || level == null || !(spot > 0)) return null
  const points = level - spot
  return { points, percent: (points / spot) * 100 }
}

export function formatDistance(d: { points: number; percent: number } | null): string {
  if (!d) return '—'
  const sign = d.points > 0 ? '+' : d.points < 0 ? '−' : ''
  return `${sign}${Math.abs(Math.round(d.points)).toLocaleString('en-IN')} pts (${sign}${Math.abs(d.percent).toFixed(2)}%)`
}

/**
 * The move the ATM straddle is priced for until expiry: call + put at the
 * strike nearest the spot. Null when either leg has no price.
 */
export function expectedMove(chain: OptionChain | undefined): { points: number; percent: number; strike: number } | null {
  if (!chain || !(chain.spotPrice > 0)) return null
  const atm = chain.strikes.find((s) => s.isAtTheMoney) ??
    chain.strikes.reduce<OptionChain['strikes'][number] | undefined>(
      (best, s) => (!best || Math.abs(s.strikePrice - chain.spotPrice) < Math.abs(best.strikePrice - chain.spotPrice) ? s : best),
      undefined,
    )
  const call = atm?.call?.lastTradedPrice
  const put = atm?.put?.lastTradedPrice
  if (!atm || call == null || put == null) return null
  const points = call + put
  return { points, percent: (points / chain.spotPrice) * 100, strike: atm.strikePrice }
}

/** What GIFT Nifty's price against NSE's last close of the same future points to. */
export function gapReading(percent: number | null | undefined): { label: string; tone: Tone } {
  if (percent == null || Number.isNaN(percent)) return { label: 'No reading', tone: 'neutral' }
  if (percent >= 0.1) return { label: `Points to a gap-up of ${percent.toFixed(2)}%`, tone: 'pos' }
  if (percent <= -0.1) return { label: `Points to a gap-down of ${Math.abs(percent).toFixed(2)}%`, tone: 'neg' }
  return { label: 'Points to a flat open', tone: 'neutral' }
}

/** Events split around today (IST), each part grouped by date in order. */
export function splitEvents(
  events: MarketEvent[] | undefined,
  today: string,
): { today: MarketEvent[]; upcoming: Array<[string, MarketEvent[]]>; past: Array<[string, MarketEvent[]]> } {
  const group = (list: MarketEvent[]) => {
    const map = new Map<string, MarketEvent[]>()
    for (const e of list) map.set(e.date, [...(map.get(e.date) ?? []), e])
    return [...map.entries()]
  }
  const all = [...(events ?? [])].sort((a, b) => a.date.localeCompare(b.date) || (a.timeIst ?? '99').localeCompare(b.timeIst ?? '99'))
  return {
    today: all.filter((e) => e.date === today),
    upcoming: group(all.filter((e) => e.date > today)),
    past: group(all.filter((e) => e.date < today)).reverse(),
  }
}

/** "Thu 17 Sep" for a yyyy-MM-dd date, read as a calendar day (no time zone shift). */
export function formatDay(date: string): string {
  const [y, m, d] = date.split('-').map(Number)
  const dt = new Date(Date.UTC(y, m - 1, d))
  return dt.toLocaleDateString('en-IN', { weekday: 'short', day: 'numeric', month: 'short', timeZone: 'UTC' })
}

/** A market's net cash flow over the days shown, and how many of them were buying days. */
export function cashStreak(days: CashDay[], side: 'fii' | 'dii'): { total: number; buyingDays: number; days: number } {
  const values = days.map((d) => d[side]?.net).filter((v): v is number => v != null)
  return { total: values.reduce((a, b) => a + b, 0), buyingDays: values.filter((v) => v > 0).length, days: values.length }
}

export interface WallRow {
  strike: number
  openInterest: number
  openInterestChange: number | null
  distance: { points: number; percent: number } | null
}

/**
 * The strikes carrying the most open interest on one side, largest first: the
 * call walls above (resistance) and put walls below (support) desks watch.
 */
export function topWalls(chain: OptionChain | undefined, side: 'call' | 'put', count = 5): WallRow[] {
  if (!chain) return []
  return chain.strikes
    .map((s) => ({ strike: s.strikePrice, leg: side === 'call' ? s.call : s.put }))
    .filter((x): x is { strike: number; leg: NonNullable<typeof x.leg> } => x.leg != null && (x.leg.openInterest ?? 0) > 0)
    .sort((a, b) => (b.leg.openInterest ?? 0) - (a.leg.openInterest ?? 0) || a.strike - b.strike)
    .slice(0, count)
    .map((x) => ({
      strike: x.strike,
      openInterest: x.leg.openInterest ?? 0,
      openInterestChange: x.leg.openInterestChange ?? null,
      distance: distanceTo(chain.spotPrice, x.strike),
    }))
}
