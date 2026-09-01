/**
 * Watchlist: the symbols the ingestor subscribes to, joined with the last
 * quote saved for each. Selecting a row opens the stored 1-minute bars and
 * recent ticks for that symbol — the data captured in the last live session.
 */

import { useMemo, useState } from 'react'
import {
  useAddWatchlistSymbol,
  useLatestQuotes,
  useLiveBars,
  useRecentTicks,
  useRemoveWatchlistSymbol,
  useWatchlist,
} from '../../lib/queries'
import {
  formatAge,
  formatPrice,
  formatTime,
  pnlClass,
  quoteChange,
  shortSymbol,
} from '../../lib/format'
import { Badge, InlineError, Panel, QueryBoundary } from '../../components/ui'
import { BarChart } from '../../components/charts'

export function WatchlistPage() {
  const watchlist = useWatchlist()
  const quotes = useLatestQuotes()
  const addSymbol = useAddWatchlistSymbol()
  const removeSymbol = useRemoveWatchlistSymbol()

  const [selected, setSelected] = useState<string | null>(null)
  const [newSymbol, setNewSymbol] = useState('')

  const bars = useLiveBars(selected)
  const ticks = useRecentTicks(selected, 30)

  const quoteBySymbol = useMemo(() => {
    const map = new Map(quotes.data?.map((q) => [q.symbol, q]))
    return map
  }, [quotes.data])

  function handleAdd(event: React.FormEvent) {
    event.preventDefault()
    const symbol = newSymbol.trim().toUpperCase()
    if (!symbol) return
    addSymbol.mutate(
      { symbol, dataType: 'symbolUpdate' },
      { onSuccess: () => setNewSymbol('') },
    )
  }

  return (
    <div className="page">
      <header className="page__header">
        <h1 className="page__title">Watchlist</h1>
        <p className="page__subtitle">
          Symbols the live ingestor subscribes to, with the last saved quote for each.
        </p>
      </header>

      <Panel
        title="Symbols"
        actions={
          <form className="inline-form" onSubmit={handleAdd}>
            <input
              className="field__input field__input--sm"
              placeholder="NSE:SBIN-EQ"
              value={newSymbol}
              onChange={(e) => setNewSymbol(e.target.value)}
              aria-label="Symbol to add"
            />
            <button className="btn btn--primary btn--sm" disabled={addSymbol.isPending}>
              Add
            </button>
          </form>
        }
      >
        {addSymbol.isError && <InlineError error={addSymbol.error} />}
        <QueryBoundary query={watchlist} empty="Watchlist is empty — add a symbol above.">
          {(items) => (
            <div className="tablewrap">
              <table className="table table--hover">
                <thead>
                  <tr>
                    <th>Symbol</th>
                    <th>Type</th>
                    <th className="r">Priority</th>
                    <th className="r">LTP</th>
                    <th className="r">Change</th>
                    <th className="r">Quote age</th>
                    <th />
                  </tr>
                </thead>
                <tbody>
                  {[...items]
                    .sort((a, b) => b.priority - a.priority || a.symbol.localeCompare(b.symbol))
                    .map((item) => {
                      const quote = quoteBySymbol.get(item.symbol)
                      const chg = quote ? quoteChange(quote.lastTradedPrice, quote.close) : null
                      const isSelected = selected === item.symbol
                      return (
                        <tr
                          key={item.id}
                          className={isSelected ? 'row--selected' : ''}
                          onClick={() => setSelected(item.symbol)}
                        >
                          <td className="mono">
                            {shortSymbol(item.symbol)}{' '}
                            {!item.isActive && <Badge tone="warn">paused</Badge>}
                          </td>
                          <td className="muted">{item.dataType}</td>
                          <td className="r mono">{item.priority}</td>
                          <td className="r mono">{formatPrice(quote?.lastTradedPrice)}</td>
                          <td className={`r mono ${pnlClass(chg?.abs)}`}>
                            {chg ? `${chg.pct.toFixed(2)}%` : '—'}
                          </td>
                          <td className="r muted">{quote ? formatAge(quote.updatedUtc) : 'no quote'}</td>
                          <td className="r">
                            <button
                              type="button"
                              className="btn btn--ghost btn--sm"
                              onClick={(e) => {
                                e.stopPropagation()
                                removeSymbol.mutate(item.id)
                              }}
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
          )}
        </QueryBoundary>
      </Panel>

      {selected && (
        <>
          <Panel title={`${shortSymbol(selected)} · stored 1-minute bars`}>
            <QueryBoundary
              query={bars}
              empty="No bars stored for this symbol — it was never subscribed during a live session."
            >
              {(data) => <BarChart bars={data} />}
            </QueryBoundary>
          </Panel>

          <Panel title="Recent ticks">
            <QueryBoundary query={ticks} empty="No ticks stored for this symbol.">
              {(data) => (
                <div className="tablewrap">
                  <table className="table">
                    <thead>
                      <tr>
                        <th>Received</th>
                        <th className="r">LTP</th>
                        <th className="r">Bid</th>
                        <th className="r">Ask</th>
                        <th className="r">Bid size</th>
                        <th className="r">Ask size</th>
                        <th className="r">Volume</th>
                      </tr>
                    </thead>
                    <tbody>
                      {data.map((t, i) => (
                        <tr key={`${t.receivedUtc}-${i}`}>
                          <td className="mono muted">{formatTime(t.receivedUtc)}</td>
                          <td className="r mono">{formatPrice(t.lastTradedPrice)}</td>
                          <td className="r mono">{formatPrice(t.bidPrice)}</td>
                          <td className="r mono">{formatPrice(t.askPrice)}</td>
                          <td className="r mono">{t.bidSize ?? '—'}</td>
                          <td className="r mono">{t.askSize ?? '—'}</td>
                          <td className="r mono">{t.volume?.toLocaleString('en-IN') ?? '—'}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </QueryBoundary>
          </Panel>
        </>
      )}
    </div>
  )
}
