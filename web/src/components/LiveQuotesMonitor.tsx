/**
 * Live quotes table with per-cell price flashes, v2 design.
 *
 * Shares the ['quotes','all'] query with every other quote consumer (one
 * network cadence for the whole app — the v1 monitor polled the same endpoint
 * on its own 1s key, doubling load). Flashes come from comparing against the
 * previous render's prices, not from extra requests.
 */

import { useEffect, useRef, useState } from 'react'
import { useLatestQuotes } from '../lib/queries'
import { formatAge, formatPrice, shortSymbol } from '../lib/format'
import { classifySymbol } from '../lib/symbols'
import { Badge, QueryBoundary } from './ui'
import type { LiveQuote } from '../lib/types'

const STALE_AFTER_MS = 120_000

function QuoteRow({ quote }: { quote: LiveQuote }) {
  const prev = useRef<number | null>(null)
  // seq keys the flashing cell so consecutive same-direction moves restart
  // the CSS animation instead of silently reusing the finished one.
  const [flash, setFlash] = useState<{ dir: 'flash-up' | 'flash-down' | ''; seq: number }>({
    dir: '',
    seq: 0,
  })

  useEffect(() => {
    const ltp = quote.lastTradedPrice
    if (ltp != null && prev.current != null && ltp !== prev.current) {
      setFlash((f) => ({ dir: ltp > prev.current! ? 'flash-up' : 'flash-down', seq: f.seq + 1 }))
      const t = setTimeout(() => setFlash((f) => ({ ...f, dir: '' })), 900)
      return () => clearTimeout(t)
    }
    prev.current = ltp
  }, [quote.lastTradedPrice])

  useEffect(() => {
    prev.current = quote.lastTradedPrice
  })

  const change =
    quote.lastTradedPrice != null && quote.close != null && quote.close !== 0
      ? ((quote.lastTradedPrice - quote.close) / quote.close) * 100
      : null

  const ageMs = Date.now() - new Date(quote.updatedUtc).getTime()

  return (
    <tr>
      <td className="mono">{shortSymbol(quote.symbol)}</td>
      <td>
        <Badge tone="neutral">{classifySymbol(quote.symbol)}</Badge>
      </td>
      <td key={flash.seq} className={`r mono ${flash.dir}`}>{formatPrice(quote.lastTradedPrice)}</td>
      <td className={`r ${change == null ? 'muted' : change >= 0 ? 'pos' : 'neg'}`}>
        {change == null ? '—' : `${change >= 0 ? '+' : ''}${change.toFixed(2)}%`}
      </td>
      <td className="r muted">{formatPrice(quote.open)}</td>
      <td className="r muted">{formatPrice(quote.high)}</td>
      <td className="r muted">{formatPrice(quote.low)}</td>
      <td className="r muted">{quote.volume == null ? '—' : quote.volume.toLocaleString('en-IN')}</td>
      <td className={ageMs > STALE_AFTER_MS ? 'warn' : 'muted'}>{formatAge(quote.updatedUtc)}</td>
    </tr>
  )
}

export function LiveQuotesMonitor({ filter = '' }: { filter?: string }) {
  const quotes = useLatestQuotes()

  return (
    <QueryBoundary query={quotes} empty="No live quotes yet — start the ingestor and add symbols to the watchlist.">
      {(data) => {
        const needle = filter.trim().toUpperCase()
        const rows = needle ? data.filter((q) => q.symbol.toUpperCase().includes(needle)) : data
        return (
          <div className="tablewrap tablewrap--tall">
            <table className="table">
              <thead>
                <tr>
                  <th>Symbol</th>
                  <th>Type</th>
                  <th className="r">LTP</th>
                  <th className="r">Chg%</th>
                  <th className="r">Open</th>
                  <th className="r">High</th>
                  <th className="r">Low</th>
                  <th className="r">Volume</th>
                  <th>Updated</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((q) => (
                  <QuoteRow key={q.symbol} quote={q} />
                ))}
              </tbody>
            </table>
            {rows.length === 0 && <p className="empty">No symbols match “{filter}”.</p>}
          </div>
        )
      }}
    </QueryBoundary>
  )
}
