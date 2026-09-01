/**
 * Option chain from the instrument master: strikes for an underlying+expiry,
 * CE and PE side by side, joined with any saved premiums from the live store.
 * Premium coverage is partial by design — only symbols that were on the
 * watchlist during a live session have stored quotes.
 */

import { useMemo, useState } from 'react'
import { useExpiries, useLatestQuotes, useOptionChain } from '../../lib/queries'
import { formatPrice } from '../../lib/format'
import { Panel, QueryBoundary } from '../../components/ui'
import type { OptionChainItem } from '../../lib/types'

const UNDERLYINGS = ['BANKNIFTY', 'NIFTY', 'FINNIFTY', 'MIDCPNIFTY']

interface StrikeRow {
  strike: number
  ce?: OptionChainItem
  pe?: OptionChainItem
}

export function OptionChainPage() {
  const [underlying, setUnderlying] = useState('BANKNIFTY')
  const [expiry, setExpiry] = useState<string | null>(null)

  const expiries = useExpiries(underlying)
  const chain = useOptionChain(underlying, expiry ?? expiries.data?.[0]?.expiryDate ?? null)
  const quotes = useLatestQuotes()

  const effectiveExpiry = expiry ?? expiries.data?.[0]?.expiryDate ?? null

  const premiums = useMemo(() => {
    // Live symbols look like "NSE:BANKNIFTY26AUG57600CE"; chain symbols may or
    // may not carry the exchange prefix, so index by the suffix.
    const map = new Map<string, number | null>()
    for (const q of quotes.data ?? []) {
      const bare = q.symbol.includes(':') ? q.symbol.split(':')[1] : q.symbol
      map.set(bare, q.lastTradedPrice)
    }
    return map
  }, [quotes.data])

  function premiumFor(item: OptionChainItem | undefined): number | null | undefined {
    if (!item) return undefined
    const bare = item.symbol.includes(':') ? item.symbol.split(':')[1] : item.symbol
    return premiums.has(bare) ? premiums.get(bare) : undefined
  }

  return (
    <div className="page">
      <header className="page__header">
        <h1 className="page__title">Option chain</h1>
        <p className="page__subtitle">
          Strikes from the instrument master ({underlying}); premiums shown where a live
          session saved them.
        </p>
      </header>

      <Panel
        title="Contracts"
        actions={
          <div className="inline-form">
            <select
              className="field__input field__input--sm"
              value={underlying}
              onChange={(e) => {
                setUnderlying(e.target.value)
                setExpiry(null)
              }}
              aria-label="Underlying"
            >
              {UNDERLYINGS.map((u) => (
                <option key={u}>{u}</option>
              ))}
            </select>
            <select
              className="field__input field__input--sm"
              value={effectiveExpiry ?? ''}
              onChange={(e) => setExpiry(e.target.value)}
              aria-label="Expiry"
              disabled={!expiries.data?.length}
            >
              {(expiries.data ?? []).map((e) => (
                <option key={e.expiryDate} value={e.expiryDate}>
                  {e.expiryDate}
                </option>
              ))}
            </select>
          </div>
        }
      >
        <QueryBoundary
          query={chain}
          empty="No contracts found — run the instrument import for this underlying."
        >
          {(items) => {
            const rows = new Map<number, StrikeRow>()
            for (const item of items) {
              if (item.strikePrice == null) continue
              const row = rows.get(item.strikePrice) ?? { strike: item.strikePrice }
              if (item.optionType === 'CE') row.ce = item
              if (item.optionType === 'PE') row.pe = item
              rows.set(item.strikePrice, row)
            }
            const sorted = [...rows.values()].sort((a, b) => a.strike - b.strike)

            return (
              <div className="tablewrap tablewrap--tall">
                <table className="table table--center">
                  <thead>
                    <tr>
                      <th className="r">CE premium</th>
                      <th className="r">CE symbol</th>
                      <th className="c">Strike</th>
                      <th>PE symbol</th>
                      <th>PE premium</th>
                    </tr>
                  </thead>
                  <tbody>
                    {sorted.map((row) => {
                      const ce = premiumFor(row.ce)
                      const pe = premiumFor(row.pe)
                      return (
                        <tr key={row.strike}>
                          <td className="r mono">{ce !== undefined ? formatPrice(ce) : ''}</td>
                          <td className="r mono muted">{row.ce ? row.ce.symbol : ''}</td>
                          <td className="c mono strike">{row.strike.toLocaleString('en-IN')}</td>
                          <td className="mono muted">{row.pe ? row.pe.symbol : ''}</td>
                          <td className="mono">{pe !== undefined ? formatPrice(pe) : ''}</td>
                        </tr>
                      )
                    })}
                  </tbody>
                </table>
              </div>
            )
          }}
        </QueryBoundary>
      </Panel>
    </div>
  )
}
