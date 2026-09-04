/**
 * Shared display formatting. All prices are INR; all API timestamps are UTC
 * ISO strings and are rendered in IST (Asia/Kolkata) whatever the viewer's
 * machine is set to — the market, the session bounds, the daily P&L buckets
 * and the chart axes are all IST, so a browser in another zone must not show
 * a different clock next to them.
 */

const MARKET_TIME_ZONE = 'Asia/Kolkata'

const inr = new Intl.NumberFormat('en-IN', {
  style: 'currency',
  currency: 'INR',
  maximumFractionDigits: 2,
})

const inrWhole = new Intl.NumberFormat('en-IN', {
  style: 'currency',
  currency: 'INR',
  maximumFractionDigits: 0,
})

const num = new Intl.NumberFormat('en-IN')

export function formatInr(value: number | null | undefined): string {
  if (value == null) return '—'
  return inr.format(value)
}

export function formatInrWhole(value: number | null | undefined): string {
  if (value == null) return '—'
  return inrWhole.format(value)
}

export function formatNumber(value: number | null | undefined): string {
  if (value == null) return '—'
  return num.format(value)
}

/**
 * P&L style: always signed, whole rupees, true minus sign — "+₹1,250" /
 * "−₹640". Zero renders as "₹0" so a flat book does not look like a gain.
 */
export function formatInrSigned(value: number | null | undefined): string {
  if (value == null) return '—'
  const rounded = Math.round(value)
  if (rounded === 0) return inrWhole.format(0)
  const sign = rounded > 0 ? '+' : '−'
  return `${sign}${inrWhole.format(Math.abs(rounded))}`
}

/** "2 lots × 30 = 60" — lots, lot size and the resulting unit quantity. */
export function formatLots(lots: number | null | undefined, lotSize: number | null | undefined): string {
  if (lots == null) return '—'
  const unit = lots === 1 ? 'lot' : 'lots'
  if (lotSize == null || lotSize <= 0) return `${num.format(lots)} ${unit}`
  return `${num.format(lots)} ${unit} × ${num.format(lotSize)} = ${num.format(lots * lotSize)}`
}

export function formatPercent(value: number | null | undefined, digits = 2): string {
  if (value == null) return '—'
  return `${value >= 0 ? '+' : ''}${value.toFixed(digits)}%`
}

export function formatPrice(value: number | null | undefined): string {
  if (value == null) return '—'
  return value.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
}

export function formatDateTime(iso: string | null | undefined): string {
  if (!iso) return '—'
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return '—'
  return d.toLocaleString('en-IN', {
    timeZone: MARKET_TIME_ZONE,
    day: '2-digit',
    month: 'short',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    hour12: false,
  })
}

export function formatTime(iso: string | null | undefined): string {
  if (!iso) return '—'
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return '—'
  return d.toLocaleTimeString('en-IN', { timeZone: MARKET_TIME_ZONE, hour12: false })
}

/** "1h 12m", "38m 05s", "12s" — a run's length from its seconds. */
export function formatDuration(seconds: number | null | undefined): string {
  if (seconds == null || !Number.isFinite(seconds) || seconds < 0) return '—'
  const s = Math.floor(seconds)
  const h = Math.floor(s / 3600)
  const m = Math.floor((s % 3600) / 60)
  const sec = s % 60
  if (h > 0) return `${h}h ${String(m).padStart(2, '0')}m`
  if (m > 0) return `${m}m ${String(sec).padStart(2, '0')}s`
  return `${sec}s`
}

/** "3m ago", "11d ago" — for freshness chips. */
export function formatAge(iso: string | null | undefined): string {
  if (!iso) return 'never'
  const ms = Date.now() - new Date(iso).getTime()
  if (Number.isNaN(ms)) return 'never'
  const s = Math.max(0, Math.floor(ms / 1000))
  if (s < 60) return `${s}s ago`
  const m = Math.floor(s / 60)
  if (m < 60) return `${m}m ago`
  const h = Math.floor(m / 60)
  if (h < 48) return `${h}h ago`
  return `${Math.floor(h / 24)}d ago`
}

/** Change vs previous close, when both are present. */
export function quoteChange(ltp: number | null, prevClose: number | null) {
  if (ltp == null || prevClose == null || prevClose === 0) return null
  const abs = ltp - prevClose
  return { abs, pct: (abs / prevClose) * 100 }
}

/** Strips the exchange prefix for tighter tables: "NSE:SBIN-EQ" -> "SBIN-EQ". */
export function shortSymbol(symbol: string): string {
  const i = symbol.indexOf(':')
  return i === -1 ? symbol : symbol.slice(i + 1)
}

export function pnlClass(value: number | null | undefined): string {
  if (value == null || value === 0) return ''
  return value > 0 ? 'pos' : 'neg'
}
