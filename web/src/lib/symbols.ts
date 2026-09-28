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

/**
 * The index ticker tiles: the well-known indices first, in the order given,
 * then every other index on the recording list — a new index on the list
 * gets a tile without a code change.
 */
export function indexTileSymbols(preferred: readonly string[], watched: readonly string[]): string[] {
  const out = [...preferred]
  const seen = new Set(preferred)
  for (const symbol of [...watched].sort()) {
    if (seen.has(symbol) || classifySymbol(symbol) !== 'Index') continue
    seen.add(symbol)
    out.push(symbol)
  }
  return out
}

/** "BANKEX" from "BSE:BANKEX-INDEX", for a tile with no hand-written label. */
export function indexLabel(symbol: string): string {
  const body = symbol.includes(':') ? symbol.slice(symbol.indexOf(':') + 1) : symbol
  return body.replace(/-INDEX$/i, '')
}

/** Sensible ordering for resolution columns: intraday minutes first, then D. */
export function resolutionRank(resolution: string): number {
  const r = resolution.toLowerCase()
  if (r === 'd' || r === '1d') return 10_000
  const minutes = parseInt(r, 10)
  return Number.isNaN(minutes) ? 20_000 : minutes
}

/** One column of the coverage table: one resolution from one source. */
export interface CoverageColumn {
  key: string
  label: string
  /** The heading a phone shows: "1m", "1m live", "D" — the matrix has six columns in 320px. */
  short: string
}

/** A range's bar count, "1 bar" when there is one — the list printed "1 bars". */
export function formatBars(n: number): string {
  return `${n.toLocaleString('en-IN')} ${n === 1 ? 'bar' : 'bars'}`
}

/** A coverage row's column: its source and resolution, for the matrix to group by. */
export function coverageColumnKey(row: { resolution: string; source: string }): string {
  return `${row.source}|${row.resolution}`
}

/**
 * The coverage table's columns, finest first and stored before live at one
 * resolution, each named so it cannot be misread. The table's headings are
 * uppercased, so "1m" (a minute) read as "1M" (a month); and the stored
 * 1-minute candles (resolution "1") and the live 1-minute bars ("1m") were
 * two columns under that one heading.
 */
export function coverageColumns(rows: readonly { resolution: string; source: string }[]): CoverageColumn[] {
  const seen = new Map<string, { resolution: string; source: string }>()
  for (const r of rows) seen.set(coverageColumnKey(r), { resolution: r.resolution, source: r.source })
  return [...seen.entries()]
    .sort(
      ([, a], [, b]) =>
        resolutionRank(a.resolution) - resolutionRank(b.resolution) || Number(a.source === 'live') - Number(b.source === 'live'),
    )
    .map(([key, c]) => {
      const r = c.resolution.toLowerCase()
      const minutes = /^(\d+)m?$/.exec(r)?.[1]
      const day = r === 'd' || r === '1d'
      const base = day ? 'Day' : minutes ? `${minutes} min` : c.resolution
      const shortBase = day ? 'D' : minutes ? `${minutes}m` : c.resolution
      const live = c.source === 'live'
      return { key, label: live ? `${base} · live` : base, short: live ? `${shortBase} live` : shortBase }
    })
}

export function formatResolution(resolution: string): string {
  const r = resolution.toLowerCase()
  if (r === 'd' || r === '1d') return '1D'
  if (/^\d+$/.test(r)) return `${r}m`
  return resolution
}

/* --- resolution codes ------------------------------------------------------ */

/**
 * Two spellings exist: the candle-table / API form ("1", "5", "15", "D") and
 * the strategy-facing form ("1m", "5m", "15m", "1D"). Never compare resolution
 * strings without going through one of these.
 */
export function toCandleResolution(resolution: string): string {
  const r = resolution.trim().toLowerCase()
  if (r === 'd' || r === '1d') return 'D'
  const m = /^(\d+)m?$/.exec(r)
  return m ? m[1] : resolution.trim()
}

export function toStrategyResolution(resolution: string): string {
  const r = resolution.trim().toLowerCase()
  if (r === 'd' || r === '1d') return '1D'
  const m = /^(\d+)m?$/.exec(r)
  return m ? `${m[1]}m` : resolution.trim()
}

/** Human label for either spelling: "5" -> "5m", "D" -> "1D". */
export function resolutionLabel(resolution: string): string {
  return toStrategyResolution(resolution)
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
