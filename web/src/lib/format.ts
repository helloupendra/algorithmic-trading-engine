/**
 * Shared display formatting. All prices are INR; all API timestamps are UTC
 * ISO strings and are rendered in the viewer's local time zone (IST for us).
 */

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
  return d.toLocaleTimeString('en-IN', { hour12: false })
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
