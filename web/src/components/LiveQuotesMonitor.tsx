import { useEffect, useRef, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api } from '../lib/api'
import { formatDateTime, formatPrice, shortSymbol } from '../lib/format'
import { Panel, QueryBoundary } from './ui'
import type { LiveQuote } from '../lib/types'

// Polls every 1 second specifically for the live monitor
function useLatestQuotesLive() {
  return useQuery({
    queryKey: ['quotes', 'all', 'live'],
    queryFn: () => api.get<LiveQuote[]>('/api/LiveData/latest/all'),
    refetchInterval: 1000,
  })
}

function FlashingCell({ value, formatFn, className = '' }: { value: number | null, formatFn: (v: number | null) => React.ReactNode, className?: string }) {
  const [flash, setFlash] = useState(0)
  const [direction, setDirection] = useState<'up'|'down'|''>('')
  const prevValue = useRef(value)

  useEffect(() => {
    if (value != null && prevValue.current != null && value !== prevValue.current) {
      setDirection(value > prevValue.current ? 'up' : 'down')
      setFlash(f => f + 1) // increment to force re-render/re-animation
    }
    prevValue.current = value
  }, [value])

  const animClass = direction === 'up' ? 'flash-up' : direction === 'down' ? 'flash-down' : ''

  return (
    <td className={`${className} ${animClass}`} key={flash}>
      {formatFn(value)}
    </td>
  )
}

export function LiveQuotesMonitor() {
  const query = useLatestQuotesLive()

  return (
    <Panel title="Live Prices Monitor">
      <QueryBoundary query={query} empty="No live quotes available yet.">
        {(data) => (
          <div className="tablewrap">
            <table className="table">
              <thead>
                <tr>
                  <th>Symbol</th>
                  <th className="r">LTP</th>
                  <th className="r">Open</th>
                  <th className="r">High</th>
                  <th className="r">Low</th>
                  <th className="r">Updated</th>
                </tr>
              </thead>
              <tbody>
                {data.map((q) => (
                  <tr key={q.symbol}>
                    <td className="mono">{shortSymbol(q.symbol)}</td>
                    <FlashingCell value={q.lastTradedPrice} formatFn={formatPrice} className="r mono" />
                    <td className="r mono">{formatPrice(q.open)}</td>
                    <td className="r mono">{formatPrice(q.high)}</td>
                    <td className="r mono">{formatPrice(q.low)}</td>
                    <td className="r muted">{formatDateTime(q.updatedUtc)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </QueryBoundary>
    </Panel>
  )
}

