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

/* --- option symbol parsing ------------------------------------------------- */

export interface ParsedOptionSymbol {
  underlying: string
  strike: number
  optionType: 'CE' | 'PE'
  /** Exact expiry for weekly symbols; null for monthly ones, whose symbol only
   *  carries the month (see `expiryMonth`). */
  expiry: Date | null
  /** "Sep" for monthly symbols, so a label can still say which series. */
  expiryMonth: string | null
}

const MONTHS = ['JAN', 'FEB', 'MAR', 'APR', 'MAY', 'JUN', 'JUL', 'AUG', 'SEP', 'OCT', 'NOV', 'DEC']
const MONTH_LABELS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']

/** Weekly symbols encode the month as one character: 1-9, then O/N/D. */
function weeklyMonthIndex(ch: string): number {
  if (ch >= '1' && ch <= '9') return Number(ch) - 1
  if (ch === 'O') return 9
  if (ch === 'N') return 10
  if (ch === 'D') return 11
  return -1
}

const MONTHLY_RE = /^([A-Z][A-Z0-9&-]*?)(\d{2})(JAN|FEB|MAR|APR|MAY|JUN|JUL|AUG|SEP|OCT|NOV|DEC)(\d+(?:\.\d+)?)$/
const WEEKLY_TAIL_RE = /^(\d{2})([1-9OND])(\d{2})(\d+(?:\.\d+)?)$/
const UNDERLYING_RE = /^[A-Z][A-Z0-9&-]*$/

/**
 * Parses FYERS option symbols — monthly `NSE:BANKNIFTY26SEP57600CE` and
 * weekly `NSE:NIFTY2690129550CE` (yy, m as 1-9/O/N/D, dd). Returns null for
 * anything that is not an option. Underlyings may contain digits
 * (NIFTYNXT50), so the weekly form is resolved by trying each split and
 * keeping the first one whose day and strike are plausible.
 */
export function parseOptionSymbol(symbol: string): ParsedOptionSymbol | null {
  const s = symbol.trim().toUpperCase()
  const body = s.includes(':') ? s.slice(s.indexOf(':') + 1) : s
  if (body.length < 8) return null
  const optionType = body.slice(-2)
  if (optionType !== 'CE' && optionType !== 'PE') return null
  const core = body.slice(0, -2)

  const monthly = MONTHLY_RE.exec(core)
  if (monthly) {
    const [, underlying, , mon, strikeText] = monthly
    const strike = Number(strikeText)
    if (strike > 0) {
      return {
        underlying,
        strike,
        optionType,
        expiry: null,
        expiryMonth: MONTH_LABELS[MONTHS.indexOf(mon)],
      }
    }
  }

  for (let i = 1; i < core.length; i++) {
    const underlying = core.slice(0, i)
    if (!UNDERLYING_RE.test(underlying)) continue
    const tail = WEEKLY_TAIL_RE.exec(core.slice(i))
    if (!tail) continue
    const [, yy, m, dd, strikeText] = tail
    const monthIndex = weeklyMonthIndex(m)
    const day = Number(dd)
    const strike = Number(strikeText)
    if (monthIndex < 0 || day < 1 || day > 31 || strike <= 0) continue
    const expiry = new Date(2000 + Number(yy), monthIndex, day)
    if (expiry.getMonth() !== monthIndex) continue
    return { underlying, strike, optionType, expiry, expiryMonth: MONTH_LABELS[monthIndex] }
  }

  return null
}

/**
 * "BANKNIFTY 57600 CE · 29 Sep" from the raw symbol. Prefer the server's
 * `contract.label` when it exists; this is the fallback for rows the API
 * could not decorate. Non-option symbols fall back to the bare symbol.
 */
export function formatContract(symbol: string): string {
  const parsed = parseOptionSymbol(symbol)
  if (!parsed) return symbol.includes(':') ? symbol.slice(symbol.indexOf(':') + 1) : symbol
  const strike = Number.isInteger(parsed.strike) ? String(parsed.strike) : parsed.strike.toFixed(2)
  const when = parsed.expiry
    ? `${parsed.expiry.getDate()} ${MONTH_LABELS[parsed.expiry.getMonth()]}`
    : parsed.expiryMonth
  return `${parsed.underlying} ${strike} ${parsed.optionType}${when ? ` · ${when}` : ''}`
}
