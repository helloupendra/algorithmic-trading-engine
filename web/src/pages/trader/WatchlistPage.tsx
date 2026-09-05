/**
 * The trader's own watchlist.
 *
 * Removing a row here removes it from *this trader's* list only. The live feed
 * keeps carrying the symbol — another trader or a running strategy may still
 * need it, and quietly unsubscribing the feed would starve them of data.
 */

import { useState } from 'react'
import {
  useAddToMyWatchlist,
  useMyWatchlist,
  useRemoveFromMyWatchlist,
  useResetMyWatchlist,
} from '../../lib/queries'
import type { MyWatchlistItem } from '../../lib/types'
import { formatAge, formatPrice, pnlClass } from '../../lib/format'
import { Badge, EmptyState, InlineError, Panel, QueryBoundary } from '../../components/ui'

function change(item: MyWatchlistItem): { text: string; cls: string } {
  if (item.lastTradedPrice == null || item.close == null || item.close === 0) {
    return { text: '—', cls: 'muted' }
  }

  const diff = item.lastTradedPrice - item.close
  const pct = (diff / item.close) * 100

  return {
    text: `${diff >= 0 ? '+' : ''}${formatPrice(diff)} (${pct >= 0 ? '+' : ''}${pct.toFixed(2)}%)`,
    cls: pnlClass(diff),
  }
}

export function WatchlistPage() {
  const watchlist = useMyWatchlist()
  const add = useAddToMyWatchlist()
  const remove = useRemoveFromMyWatchlist()
  const reset = useResetMyWatchlist()
  const [symbol, setSymbol] = useState('')

  return (
    <div className="page">
      <header className="page__header">
        <h1 className="page__title">Watchlist</h1>
        <p className="page__subtitle">
          Your own list of symbols, with the last saved quote for each. It starts with the three
          indices and is yours to change.
        </p>
      </header>

      {add.isError && <InlineError error={add.error} />}
      {remove.isError && <InlineError error={remove.error} />}
      {(add.isSuccess || remove.isSuccess || reset.isSuccess) && (
        <div className="alert alert--success" role="status">
          {add.data?.message ?? remove.data?.message ?? reset.data?.message}
        </div>
      )}

      <Panel
        title="Symbols"
        actions={
          <form
            className="chip-row"
            onSubmit={(e) => {
              e.preventDefault()
              add.mutate(symbol.trim(), { onSuccess: () => setSymbol('') })
            }}
          >
            <input
              className="field__input mono"
              placeholder="NSE:SBIN-EQ"
              value={symbol}
              onChange={(e) => setSymbol(e.target.value.toUpperCase())}
              aria-label="Symbol to add"
            />
            <button className="btn btn--primary btn--sm" disabled={add.isPending || !symbol.trim()}>
              {add.isPending ? 'Adding…' : 'Add'}
            </button>
            <button
              type="button"
              className="btn btn--ghost btn--sm"
              disabled={reset.isPending}
              onClick={() => reset.mutate()}
              title="Put the three default indices back"
            >
              Reset
            </button>
          </form>
        }
      >
        <QueryBoundary query={watchlist}>
          {(list) =>
            list.length === 0 ? (
              <EmptyState>Your watchlist is empty. Add a symbol, or reset to the defaults.</EmptyState>
            ) : (
              <div className="tablewrap">
                <table className="table">
                  <thead>
                    <tr>
                      <th>Symbol</th>
                      <th className="r">LTP</th>
                      <th className="r">Change</th>
                      <th className="r">Open</th>
                      <th className="r">High</th>
                      <th className="r">Low</th>
                      <th>Quote age</th>
                      <th />
                    </tr>
                  </thead>
                  <tbody>
                    {list.map((item) => {
                      const ch = change(item)
                      return (
                        <tr key={item.symbol}>
                          <td className="mono">
                            {item.symbol}
                            {!item.isSubscribed && (
                              <div className="small-note">
                                <Badge tone="warn">feed not subscribed</Badge>
                              </div>
                            )}
                          </td>
                          <td className="r">{formatPrice(item.lastTradedPrice)}</td>
                          <td className={`r ${ch.cls}`}>{ch.text}</td>
                          <td className="r">{formatPrice(item.open)}</td>
                          <td className="r">{formatPrice(item.high)}</td>
                          <td className="r">{formatPrice(item.low)}</td>
                          <td>
                            {item.updatedUtc ? (
                              formatAge(item.updatedUtc)
                            ) : (
                              <span className="muted">no quote</span>
                            )}
                          </td>
                          <td>
                            <button
                              type="button"
                              className="btn btn--ghost btn--sm"
                              disabled={remove.isPending}
                              onClick={() => remove.mutate(item.symbol)}
                            >
                              Remove
                            </button>
                          </td>
                        </tr>
                      )
                    })}
                  </tbody>
                </table>
              </div>
            )
          }
        </QueryBoundary>
        <p className="small-note muted">
          Removing takes the symbol off <b>your</b> list only — the live feed keeps carrying it,
          because another trader or a running strategy may depend on it. Adding a new symbol also
          asks the feed to subscribe, so quotes start arriving on its next refresh.
        </p>
      </Panel>
    </div>
  )
}
