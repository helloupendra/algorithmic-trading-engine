/**
 * The market-movers screen: Angel One's gainers, losers, open-interest
 * build-up and put-call ratio, turned into what a table needs.
 *
 * Kept out of the page so the two things that are easy to get wrong can be
 * tested: what a build-up label means (and its colour), and which end of the
 * put-call ratio is worth showing — 215 underlyings is a list nobody reads.
 */

export interface MoverRow {
  symbol: string
  underlying: string
  token: number
  ltp: number
  priceChangePercent: number
  openInterest: number | null
  oiChangePercent: number | null
  buildUp: string | null
}

export interface PcrRow {
  symbol: string
  underlying: string
  pcr: number
}

export interface MoversSnapshot {
  configured: boolean
  missing?: string[]
  message?: string
  asOfUtc?: string
  expiryType?: string
  priceGainers?: MoverRow[]
  priceLosers?: MoverRow[]
  buildUp?: MoverRow[]
  pcr?: PcrRow[]
  warnings?: string[]
}

export type Tone = 'pos' | 'neg' | 'warn' | 'neutral'

/**
 * The colour a build-up deserves.
 *
 * Longs coming in is bullish, fresh shorts bearish; the two unwinds are
 * positions leaving, which is news but not a direction — they stay neutral so
 * the table cannot be read as a signal it is not.
 */
export function buildUpTone(label: string | null): Tone {
  switch (label) {
    case 'Long build-up':
      return 'pos'
    case 'Short build-up':
      return 'neg'
    case 'Short covering':
    case 'Long unwinding':
      return 'warn'
    default:
      return 'neutral'
  }
}

/** A price or OI move, with its sign, for a table cell. */
export function movePercent(value: number | null | undefined): string {
  if (value == null || Number.isNaN(value)) return '—'
  return `${value > 0 ? '+' : ''}${value.toFixed(2)}%`
}

/** Open interest reads in lakhs and crores here, like every Indian screen. */
export function formatOi(value: number | null | undefined): string {
  if (value == null || Number.isNaN(value)) return '—'
  if (Math.abs(value) >= 1e7) return `${(value / 1e7).toFixed(2)} cr`
  if (Math.abs(value) >= 1e5) return `${(value / 1e5).toFixed(2)} L`
  return value.toLocaleString('en-IN')
}

/**
 * The ends of the put-call ratio list: the most put-heavy (highest PCR, where
 * writers are betting the floor holds) and the most call-heavy (lowest).
 */
export function pcrEnds(rows: PcrRow[] | undefined, count = 8): { high: PcrRow[]; low: PcrRow[] } {
  const clean = (rows ?? []).filter((r) => Number.isFinite(r.pcr) && r.pcr > 0)
  const sorted = [...clean].sort((a, b) => b.pcr - a.pcr)
  return { high: sorted.slice(0, count), low: sorted.slice(-count).reverse() }
}

/** How many of each build-up the screen is showing, for the summary line. */
export function buildUpCounts(rows: MoverRow[] | undefined): Array<{ label: string; count: number }> {
  const counts = new Map<string, number>()
  for (const row of rows ?? []) {
    if (!row.buildUp) continue
    counts.set(row.buildUp, (counts.get(row.buildUp) ?? 0) + 1)
  }
  return [...counts.entries()]
    .map(([label, count]) => ({ label, count }))
    .sort((a, b) => b.count - a.count)
}
