/**
 * Symbol classification for the Data module. FYERS-style symbols encode the
 * category in the suffix ("NSE:SBIN-EQ", "NSE:NIFTY50-INDEX",
 * "NSE:NIFTY2591123500CE", "NSE:NIFTY25SEPFUT", "MCX:CRUDEOIL25SEPFUT") —
 * good enough to bucket coverage and quotes without another API call.
 */

export type SymbolCategory =
  | 'Equity'
  | 'Index'
  | 'Futures'
  | 'Options'
  | 'Commodity'
  | 'Other'

export const CATEGORY_ORDER: SymbolCategory[] = [
  'Index',
  'Equity',
  'Futures',
  'Options',
  'Commodity',
  'Other',
]

export function classifySymbol(symbol: string): SymbolCategory {
  const s = symbol.toUpperCase()
  const isCommodityVenue = s.startsWith('MCX')
  if (s.endsWith('-INDEX')) return 'Index'
  if (s.endsWith('-EQ') || s.endsWith('-BE') || s.endsWith('-SM')) return 'Equity'
  if (isCommodityVenue) return 'Commodity'
  if (s.endsWith('FUT')) return 'Futures'
  if (s.endsWith('CE') || s.endsWith('PE')) return 'Options'
  return 'Other'
}

/** Sensible ordering for resolution columns: intraday minutes first, then D. */
export function resolutionRank(resolution: string): number {
  const r = resolution.toLowerCase()
  if (r === 'd' || r === '1d') return 10_000
  const minutes = parseInt(r, 10)
  return Number.isNaN(minutes) ? 20_000 : minutes
}

export function formatResolution(resolution: string): string {
  const r = resolution.toLowerCase()
  if (r === 'd' || r === '1d') return '1D'
  if (/^\d+$/.test(r)) return `${r}m`
  return resolution
}
