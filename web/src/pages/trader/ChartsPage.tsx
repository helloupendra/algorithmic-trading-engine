/**
 * Charts — the chart first, everything else in service of it.
 *
 * A symbol, a resolution, a range, and a big candlestick. The resolution and
 * range pills only offer what this installation actually has for the symbol
 * (the coverage endpoint says), so a trader never lands on an empty chart
 * without knowing why. Intraday resolutions are stitched from two sources:
 * candles stored by the nightly archive and backfills for past days, and
 * the live 1-minute bars for today (rolled up to 5m/15m here with the same
 * bucket rule the server uses). Daily comes from the stored candles alone.
 *
 * The old page led with an inventory of every (symbol, resolution) pair on
 * the box — five hundred rows — and put the chart under it. The inventory
 * is still here, one line per resolution, under the chart where it belongs.
 */
import { useEffect, useMemo, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import {
  useDataCoverage,
  useLiveBars,
  useMarketPulse,
  useMyWatchlist,
  useStoredCandles,
  type CoverageRow,
} from '../../lib/queries'
import { formatAge, formatDateTime, formatNumber, formatPrice, shortSymbol } from '../../lib/format'
import { SymbolCombobox } from '../../components/SymbolCombobox'
import { InlineError, Loading } from '../../components/ui'
import { PriceChart, type PriceCandle } from '../../components/charts'
import './charts.css'

/* ------------------------------------------------------------------ model */

type Resolution = '1' | '5' | '15' | 'D'
const RESOLUTIONS: { key: Resolution; label: string; minutes: number | null }[] = [
  { key: '1', label: '1m', minutes: 1 },
  { key: '5', label: '5m', minutes: 5 },
  { key: '15', label: '15m', minutes: 15 },
  { key: 'D', label: 'Day', minutes: null },
]

type RangeKey = '1D' | '5D' | '1M' | '3M' | '1Y' | 'ALL'
/**
 * Intraday ranges are SESSIONS, not calendar days: "5D" is the last five days
 * that traded, so a weekend never eats the range. `fetchDays` is the
 * calendar window asked of the server, wide enough to hold those sessions.
 */
const RANGES: { key: RangeKey; label: string; sessions: number | null; fetchDays: number | null }[] = [
  { key: '1D', label: '1D', sessions: 1, fetchDays: 8 },
  { key: '5D', label: '5D', sessions: 5, fetchDays: 12 },
  { key: '1M', label: '1M', sessions: 22, fetchDays: 34 },
  { key: '3M', label: '3M', sessions: 66, fetchDays: 96 },
  { key: '1Y', label: '1Y', sessions: 250, fetchDays: 370 },
  { key: 'ALL', label: 'All', sessions: null, fetchDays: null },
]

/**
 * NSE and BSE print from 09:00 IST in the pre-open auction — a handful of
 * ticks at indicative prices that draw a wick to nowhere on the first
 * candle. The regular session starts 09:15 (03:45Z); nothing before it is a
 * bar. MCX has no pre-open and is left alone.
 */
function isPreOpen(symbol: string, timeUtc: string): boolean {
  if (!symbol.startsWith('NSE:') && !symbol.startsWith('BSE:')) return false
  const t = timeUtc.slice(11, 16)
  return t < '03:45'
}

/** A session is one calendar day (UTC date is the IST day for every Indian bar). */
function sessionOf(c: PriceCandle): string {
  return c.timeUtc.slice(0, 10)
}

/**
 * The last N sessions present in the data. For a single session, a day with
 * only a bar or two — the first minutes of today, or a stray print — is
 * skipped in favour of the last day that traded properly.
 */
function lastSessions(candles: PriceCandle[], n: number): PriceCandle[] {
  const byDay = new Map<string, PriceCandle[]>()
  for (const c of candles) (byDay.get(sessionOf(c)) ?? byDay.set(sessionOf(c), []).get(sessionOf(c))!).push(c)
  const days = [...byDay.keys()].sort()
  if (n === 1) {
    const full = [...days].reverse().find((d) => (byDay.get(d)?.length ?? 0) >= 10) ?? days[days.length - 1]
    return full ? byDay.get(full)! : []
  }
  return days.slice(-n).flatMap((d) => byDay.get(d)!)
}

/** The ranges that make sense at a resolution: 1m over a year is a wall of noise. */
const RANGES_FOR: Record<Resolution, RangeKey[]> = {
  '1': ['1D', '5D'],
  '5': ['1D', '5D', '1M'],
  '15': ['1D', '5D', '1M', '3M'],
  D: ['1M', '3M', '1Y', 'ALL'],
}

function dateInput(d: Date): string {
  return d.toISOString().slice(0, 10)
}

/** Live 1-minute bars folded into a coarser bucket — the server's rollup rule, in the browser. */
function rollUp(bars: PriceCandle[], minutes: number): PriceCandle[] {
  if (minutes <= 1) return bars
  const span = minutes * 60_000
  const out = new Map<number, PriceCandle>()
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

/* ------------------------------------------------------------------- page */

export function ChartsPage() {
  const coverage = useDataCoverage()
  const pulse = useMarketPulse()
  const watchlist = useMyWatchlist()
  const [params, setParams] = useSearchParams()

  const [symbol, setSymbol] = useState<string>(() => params.get('symbol') ?? 'NSE:NIFTY50-INDEX')
  const [search, setSearch] = useState('')
  const [resolution, setResolution] = useState<Resolution>('5')
  const [range, setRange] = useState<RangeKey>('1D')

  // The address carries the symbol so a chart can be linked to.
  useEffect(() => {
    if (params.get('symbol') !== symbol) setParams({ symbol }, { replace: true })
  }, [symbol, params, setParams])

  // What this installation has for the symbol, by resolution.
  const rowsFor = useMemo(() => {
    const rows = (coverage.data ?? []).filter((r) => r.symbol === symbol)
    const by: Partial<Record<Resolution, CoverageRow[]>> = {}
    for (const r of rows) {
      const key: Resolution | null =
        r.resolution === '1m' || r.resolution === '1' ? '1' : r.resolution === '5' ? '5' : r.resolution === '15' ? '15' : r.resolution === 'D' ? 'D' : null
      if (!key) continue
      ;(by[key] ??= []).push(r)
    }
    return by
  }, [coverage.data, symbol])
  const hasLive = (rowsFor['1'] ?? []).some((r) => r.source === 'live')
  const available = (key: Resolution) =>
    key === 'D' ? (rowsFor.D?.length ?? 0) > 0 : (rowsFor[key]?.length ?? 0) > 0 || hasLive

  // A resolution the symbol does not have falls back to one it does.
  useEffect(() => {
    if (!coverage.data) return
    if (available(resolution)) return
    const first = RESOLUTIONS.find((r) => available(r.key))
    if (first) setResolution(first.key)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [coverage.data, symbol])
  useEffect(() => {
    if (!RANGES_FOR[resolution].includes(range)) setRange(RANGES_FOR[resolution][0])
  }, [resolution, range])

  // ---- data ----
  // Ranges count back from the last bar this symbol HAS at this resolution,
  // not from the clock: "1D" on a Sunday, or after the close, is the last
  // session — not an empty day.
  const intraday = resolution !== 'D'
  const anchorUtc = useMemo(() => {
    const rows = [...(rowsFor[resolution] ?? []), ...(intraday ? rowsFor['1'] ?? [] : [])]
    const latest = rows.reduce((m, r) => (r.toUtc > m ? r.toUtc : m), '')
    return latest || new Date().toISOString()
  }, [rowsFor, resolution, intraday])
  const rangeDef = RANGES.find((r) => r.key === range) ?? RANGES[0]
  const fromDate = useMemo(() => {
    if (rangeDef.fetchDays == null) return undefined
    const d = new Date(anchorUtc)
    d.setUTCDate(d.getUTCDate() - (rangeDef.fetchDays - 1))
    return dateInput(d)
  }, [rangeDef, anchorUtc])
  const stored = useStoredCandles(symbol, resolution, fromDate, undefined)
  const live = useLiveBars(intraday && hasLive ? symbol : null, 3_000)

  const candles = useMemo(() => {
    const storedCandles: PriceCandle[] = (stored.data ?? [])
      .map((c) => ({
        timeUtc: c.timestampUtc,
        open: c.open,
        high: c.high,
        low: c.low,
        close: c.close,
        volume: c.volume,
      }))
      // Candles archived from live bars before the archive learned to skip
      // the pre-open still carry a 09:00 bar; the broker's never do.
      .filter((c) => !isPreOpen(symbol, c.timeUtc))
    if (!intraday) {
      const sorted = storedCandles.sort((a, b) => a.timeUtc.localeCompare(b.timeUtc))
      return rangeDef.sessions == null ? sorted : sorted.slice(-rangeDef.sessions)
    }

    // Today's bars are live; stored ones end where the last archive ran.
    // Live bars beyond the stored tail fill in the rest.
    const liveCandles: PriceCandle[] = (live.data ?? []).map((b) => ({
      timeUtc: b.barStartUtc,
      open: b.open,
      high: b.high,
      low: b.low,
      close: b.close,
      volume: b.volumeDelta,
    }))
    const minutes = RESOLUTIONS.find((r) => r.key === resolution)?.minutes ?? 1
    const rolled = rollUp(liveCandles.filter((c) => !isPreOpen(symbol, c.timeUtc)), minutes)
    const storedMax = storedCandles.reduce((m, c) => (c.timeUtc > m ? c.timeUtc : m), '')
    const from = fromDate ? `${fromDate}T00:00:00Z` : ''
    const merged = [...storedCandles, ...rolled.filter((c) => c.timeUtc > storedMax)]
      .filter((c) => !from || c.timeUtc >= from)
      .sort((a, b) => a.timeUtc.localeCompare(b.timeUtc))
    return rangeDef.sessions == null ? merged : lastSessions(merged, rangeDef.sessions)
  }, [stored.data, live.data, intraday, resolution, fromDate, rangeDef, symbol])

  const summary = useMemo(() => {
    if (!candles.length) return null
    const first = candles[0]
    const last = candles[candles.length - 1]
    const change = last.close - first.open
    return {
      first,
      last,
      high: Math.max(...candles.map((c) => c.high)),
      low: Math.min(...candles.map((c) => c.low)),
      volume: candles.reduce((s, c) => s + (c.volume ?? 0), 0),
      change,
      changePct: first.open ? (change / first.open) * 100 : 0,
      count: candles.length,
    }
  }, [candles])

  // Quote for the header: the pulse carries the big names; otherwise the last candle.
  const pulseItem = useMemo(
    () => (pulse.data?.groups ?? []).flatMap((g) => g.items).find((i) => i.symbol === symbol) ?? null,
    [pulse.data, symbol],
  )
  const lastPrice = pulseItem?.lastTradedPrice ?? summary?.last.close ?? null
  const asOf = pulseItem?.updatedUtc ?? summary?.last.timeUtc ?? null

  // Quick picks: the pulse universe, then the trader's own list.
  const picks = useMemo(() => {
    const seen = new Set<string>()
    const out: { symbol: string; name: string }[] = []
    for (const g of pulse.data?.groups ?? []) for (const i of g.items) if (!seen.has(i.symbol)) { seen.add(i.symbol); out.push({ symbol: i.symbol, name: i.name }) }
    for (const w of watchlist.data ?? []) if (!seen.has(w.symbol)) { seen.add(w.symbol); out.push({ symbol: w.symbol, name: shortSymbol(w.symbol) }) }
    return out
  }, [pulse.data, watchlist.data])

  const loading = stored.isPending || (intraday && hasLive && live.isPending)
  const error = stored.isError ? stored.error : live.isError ? live.error : null
  const nothingHere = coverage.data && !RESOLUTIONS.some((r) => available(r.key))

  return (
    <div className="page charts">
      {/* Toolbar: symbol · resolution · range */}
      <div className="charts__bar">
        <div className="charts__symbol">
          <SymbolCombobox
            id="charts-symbol"
            value={search}
            onChange={setSearch}
            onSelect={(s) => {
              setSymbol(s.toUpperCase())
              setSearch('')
            }}
            placeholder="Search a symbol…"
          />
        </div>
        <div className="seg" role="group" aria-label="Resolution">
          {RESOLUTIONS.map((r) => (
            <button
              key={r.key}
              type="button"
              className={`seg__btn${resolution === r.key ? ' is-active' : ''}`}
              aria-pressed={resolution === r.key}
              disabled={coverage.data ? !available(r.key) : false}
              title={coverage.data && !available(r.key) ? `No ${r.label} data for this symbol` : undefined}
              onClick={() => setResolution(r.key)}
            >
              {r.label}
            </button>
          ))}
        </div>
        <div className="seg" role="group" aria-label="Range">
          {RANGES.filter((r) => RANGES_FOR[resolution].includes(r.key)).map((r) => (
            <button
              key={r.key}
              type="button"
              className={`seg__btn${range === r.key ? ' is-active' : ''}`}
              aria-pressed={range === r.key}
              onClick={() => setRange(r.key)}
            >
              {r.label}
            </button>
          ))}
        </div>
      </div>

      {/* Quick picks */}
      {picks.length > 0 && (
        <div className="charts__picks" aria-label="Quick picks">
          {picks.map((p) => (
            <button
              key={p.symbol}
              type="button"
              className={`charts__pick${p.symbol === symbol ? ' is-active' : ''}`}
              onClick={() => setSymbol(p.symbol)}
              title={p.symbol}
            >
              {p.name}
            </button>
          ))}
        </div>
      )}

      {/* Header: name, price, change over the range */}
      <div className="charts__head">
        <div className="charts__title">
          <span className="charts__name">{pulseItem?.name ?? shortSymbol(symbol)}</span>
          <span className="charts__sym mono">{symbol}</span>
          {hasLive && <span className="charts__live">● live</span>}
        </div>
        <div className="charts__quote">
          <span className="charts__price">{lastPrice != null ? formatPrice(lastPrice) : '—'}</span>
          {summary && (
            <span className={`charts__change ${summary.change > 0 ? 'pos' : summary.change < 0 ? 'neg' : ''}`}>
              {summary.change >= 0 ? '+' : ''}
              {summary.change.toFixed(2)} ({summary.changePct >= 0 ? '+' : ''}
              {summary.changePct.toFixed(2)}%) <span className="charts__over">over {rangeDef.label}</span>
            </span>
          )}
          {asOf && <span className="charts__asof">{formatAge(asOf)}</span>}
        </div>
      </div>

      {/* The chart */}
      <div className="charts__stage">
        {error && <InlineError error={error} />}
        {nothingHere ? (
          <div className="charts__empty">
            <b>No candles for {shortSymbol(symbol)} on this installation yet.</b>
            <span>Add it to your watchlist and it will start recording on the next session; a backfill brings in history.</span>
          </div>
        ) : loading && candles.length === 0 ? (
          <Loading label="Loading candles…" />
        ) : candles.length === 0 ? (
          <div className="charts__empty">
            <b>Nothing in this range.</b>
            <span>Try a wider range or a coarser resolution.</span>
          </div>
        ) : (
          <PriceChart candles={candles} />
        )}
      </div>

      {/* Stats for what is on screen */}
      {summary && (
        <div className="charts__stats">
          <Stat k="Open" v={formatPrice(summary.first.open)} />
          <Stat k="High" v={formatPrice(summary.high)} />
          <Stat k="Low" v={formatPrice(summary.low)} />
          <Stat k="Close" v={formatPrice(summary.last.close)} />
          <Stat k="Volume" v={summary.volume > 0 ? formatNumber(summary.volume) : '—'} />
          <Stat k="Bars" v={formatNumber(summary.count)} />
          <Stat k="From" v={formatDateTime(summary.first.timeUtc)} />
          <Stat k="To" v={formatDateTime(summary.last.timeUtc)} />
        </div>
      )}

      {/* What exists for this symbol, one line per resolution */}
      {coverage.data && (
        <details className="charts__data">
          <summary>Data on this symbol</summary>
          <table className="table charts__data-table">
            <tbody>
              {RESOLUTIONS.map((r) => {
                const rows = rowsFor[r.key] ?? []
                if (rows.length === 0) return null
                return rows.map((row) => (
                  <tr key={`${r.key}-${row.source}`}>
                    <td className="mono">{r.label}</td>
                    <td className="muted">{row.source === 'live' ? 'live session bars' : 'stored candles'}</td>
                    <td className="muted">
                      {formatDateTime(row.fromUtc)} → {formatDateTime(row.toUtc)}
                    </td>
                    <td className="r mono">{formatNumber(row.barCount)} bars</td>
                  </tr>
                ))
              })}
            </tbody>
          </table>
        </details>
      )}
    </div>
  )
}

function Stat({ k, v }: { k: string; v: string }) {
  return (
    <div className="charts__stat">
      <span className="charts__stat-k">{k}</span>
      <span className="charts__stat-v mono">{v}</span>
    </div>
  )
}
