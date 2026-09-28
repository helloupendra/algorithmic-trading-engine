/**
 * Pulse & watchlist: the market pulse everyone shares (indices, the large
 * caps that move them, MCX), then the trader's own list on top of it. The
 * pulse lived on the trader overview until the Desk replaced that page.
 *
 * Removing a row here removes it from *this trader's* list only. The live feed
 * keeps carrying the symbol — another trader or a running strategy may still
 * need it, and quietly unsubscribing the feed would starve them of data.
 *
 * Both halves move with the pushed prices, each in its hook: the pulse, and
 * the list's rows (their price, the day's range and the quote's age) between
 * answers of the list, which with the socket up is read twice a minute, and
 * at once when the first prices of a session arrive.
 */

import { useState } from 'react'
import {
  useAddToMyWatchlist,
  useMyWatchlistLive,
  useRemoveFromMyWatchlist,
  useResetMyWatchlist,
} from '../../lib/queries'
import type { MyWatchlistItem } from '../../lib/types'
import { formatAge, formatPrice, pnlClass } from '../../lib/format'
import { Badge, EmptyState, InlineError, Panel, QueryBoundary } from '../../components/ui'
import { SymbolCombobox } from '../../components/SymbolCombobox'
import { MarketPulse } from '../../components/MarketPulse'

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
  // Rows moved by their pushed prices, and asked for again the moment those are a new session's.
  const { query: watchlist, rows } = useMyWatchlistLive()
  const add = useAddToMyWatchlist()
  const remove = useRemoveFromMyWatchlist()
  const reset = useResetMyWatchlist()
  const [symbol, setSymbol] = useState('')

  return (
    <div className="page wl">
      <header className="page__header">
        <h1 className="page__title">Watchlist</h1>
        <p className="page__subtitle">
          Your own list of symbols, with the last saved quote for each. It is stored against your
          account, so it is the same on every device. The indices, large caps and commodities are
          the same for everyone; your list is for what you want on top of them.
        </p>
      </header>

      <MarketPulse />

      {/* On a phone this panel comes before the pulse (market-pulse.css): the
          list the page is named for, first. The add/remove notes live inside
          it so they travel with it. */}
      <Panel
        title="Symbols"
        className="wl__own"
        actions={
          <form
            className="chip-row"
            onSubmit={(e) => {
              e.preventDefault()
              const s = symbol.trim().toUpperCase()
              if (!s) {
                document.getElementById('wl-symbol')?.focus()
                return
              }
              add.mutate(s, { onSuccess: () => setSymbol('') })
            }}
          >
            {/* A search, not a blank for the exact symbol: type "hdfc",
                "crude", "nifty 23500 pe" and pick from the master. */}
            <div className="wl__search">
              <SymbolCombobox id="wl-symbol" value={symbol} onChange={setSymbol} disabled={add.isPending} />
            </div>
            {/* Always at full colour: an empty box sends the focus back to the
                search instead of greying the button out. */}
            <button className="btn btn--primary btn--sm" disabled={add.isPending}>
              {add.isPending ? 'Adding…' : 'Add'}
            </button>
            <button
              type="button"
              className="btn btn--ghost btn--sm"
              disabled={reset.isPending}
              onClick={() => {
                if (window.confirm('Remove every symbol from your watchlist?')) reset.mutate()
              }}
              title="Remove every symbol from your list"
            >
              Clear
            </button>
          </form>
        }
      >
        {add.isError && <InlineError error={add.error} />}
        {remove.isError && <InlineError error={remove.error} />}
        {(add.isSuccess || remove.isSuccess || reset.isSuccess) && (
          <div className="alert alert--success" role="status">
            {add.data?.message ?? remove.data?.message ?? reset.data?.message}
          </div>
        )}
        <QueryBoundary query={watchlist}>
          {(answered) => {
            const list = rows ?? answered
            return list.length === 0 ? (
              <EmptyState>Your watchlist is empty. Add a symbol above and it stays here.</EmptyState>
            ) : (
              <div className={`tablewrap${list.length > 8 ? ' tablewrap--rows8' : ''}`}>
                <table className="table">
                  <thead>
                    <tr>
                      <th>Symbol</th>
                      <th className="r">LTP</th>
                      <th className="r">Change</th>
                      <th className="r wl-ohl">Open</th>
                      <th className="r wl-ohl">High</th>
                      <th className="r wl-ohl">Low</th>
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
                          <td className="r wl-ohl">{formatPrice(item.open)}</td>
                          <td className="r wl-ohl">{formatPrice(item.high)}</td>
                          <td className="r wl-ohl">{formatPrice(item.low)}</td>
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
          }}
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
