import { useState } from 'react'
import {
  useExpiries,
  useInstrumentSearch,
  useAddWatchlistSymbol,
  useWatchlist,
  useRemoveWatchlistSymbol,
} from '../../lib/queries'
import { QueryBoundary } from '../../components/ui'

const CATEGORIES = ['All types', 'Stocks', 'Futures', 'Options', 'Indices']

export function InstrumentsPage() {
  const [query, setQuery] = useState('')
  const [submitted, setSubmitted] = useState('')
  const [activeCategory, setActiveCategory] = useState('All types')

  let apiType = ''
  if (activeCategory === 'Stocks') apiType = 'EQ'
  if (activeCategory === 'Futures') apiType = 'FUT'
  if (activeCategory === 'Options') apiType = 'OPT'
  if (activeCategory === 'Indices') apiType = 'INDEX'

  const search = useInstrumentSearch(submitted, apiType)
  const addWatchlist = useAddWatchlistSymbol()
  const removeWatchlist = useRemoveWatchlistSymbol()
  const watchlist = useWatchlist()

  const [underlying, setUnderlying] = useState('BANKNIFTY')
  const expiries = useExpiries(underlying)

  const getWatchlistItem = (symbol: string) => {
    return watchlist.data?.find((item) => item.symbol === symbol)
  }

  // Helper to split exchange prefix
  const parseSymbol = (fullSymbol: string) => {
    if (fullSymbol.includes(':')) {
      const [exchange, name] = fullSymbol.split(':')
      return { exchange, name }
    }
    // Handle MCX_ prefix or NSE_ prefix if they don't use colon
    if (fullSymbol.startsWith('MCX_')) return { exchange: 'MCX', name: fullSymbol.replace('MCX_', '') }
    if (fullSymbol.startsWith('NSE_')) return { exchange: 'NSE', name: fullSymbol.replace('NSE_', '') }
    
    // Default fallback
    return { exchange: '', name: fullSymbol }
  }

  // Highlight matched query text
  const HighlightText = ({ text }: { text: string }) => {
    if (!submitted) return <span>{text}</span>
    const parts = text.split(new RegExp(`(${submitted})`, 'gi'))
    return (
      <span>
        {parts.map((part, i) =>
          part.toLowerCase() === submitted.toLowerCase() ? (
            <span key={i} className="tv-highlight">{part}</span>
          ) : (
            part
          )
        )}
      </span>
    )
  }

  return (
    <div className="page" style={{ maxWidth: '1000px', margin: '0 auto' }}>
      <header className="page__header" style={{ marginBottom: '24px' }}>
        <h1 className="page__title">Symbol Search</h1>
      </header>

      <div className="tv-search-container" style={{ boxShadow: '0 4px 12px rgba(0,0,0,0.1)', border: '1px solid var(--border-soft)' }}>
        <div className="tv-search-header">
          <form
            className="tv-search-input-wrapper"
            onSubmit={(e) => {
              e.preventDefault()
              setSubmitted(query.trim().toUpperCase())
            }}
          >
            <svg className="tv-search-input-icon" viewBox="0 0 24 24">
              <circle cx="11" cy="11" r="8"></circle>
              <line x1="21" y1="21" x2="16.65" y2="16.65"></line>
            </svg>
            <input
              className="tv-search-input"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              placeholder="Search symbols..."
              autoFocus
            />
          </form>

          <div className="tv-chips-container">
            {CATEGORIES.map((cat) => (
              <button
                key={cat}
                type="button"
                className={`tv-chip ${activeCategory === cat ? 'active' : ''}`}
                onClick={() => setActiveCategory(cat)}
              >
                {cat}
              </button>
            ))}
          </div>
        </div>

        <QueryBoundary query={search} empty={
          <div style={{ padding: '32px', textAlign: 'center', color: 'var(--fg-muted)' }}>
            No symbols found.
          </div>
        }>
          {(data) => {
            if (data.length === 0) {
              return (
                <div style={{ padding: '32px', textAlign: 'center', color: 'var(--fg-muted)' }}>
                  No {activeCategory.toLowerCase()} match your search.
                </div>
              )
            }

            return (
              <div className="tv-list-body">
                <div className="tv-list-header">
                  <div>SYMBOL</div>
                  <div>DESCRIPTION</div>
                  <div style={{ textAlign: 'right' }}>SOURCE</div>
                </div>
                
                {data.map((i) => {
                  const watchItem = getWatchlistItem(i.symbol)
                  const isSubscribed = !!watchItem
                  const { exchange, name } = parseSymbol(i.symbol)
                  
                  // Determine display type
                  let displayType = 'Stock'
                  if (!!i.optionType || i.instrumentType?.includes('OPT') || i.instrumentType === 'CE' || i.instrumentType === 'PE') displayType = 'Options'
                  else if (i.instrumentType?.includes('FUT')) displayType = 'Futures'
                  else if (i.instrumentType?.includes('INDEX') || i.segment?.includes('INDEX')) displayType = 'Index'

                  return (
                    <div className="tv-list-row" key={i.id}>
                      <div className="tv-symbol-col">
                        <div style={{ display: 'flex', alignItems: 'center' }}>
                          {exchange && <span className="tv-symbol-exchange">{exchange}:</span>}
                          <span className="tv-symbol-name"><HighlightText text={name} /></span>
                        </div>
                      </div>
                      
                      <div className="tv-desc-col">
                        <HighlightText text={i.description || i.symbol} />
                      </div>
                      
                      <div className="tv-action-col">
                        <span className="tv-type-label">{displayType}</span>
                        {exchange && <span className="tv-exchange-label">{exchange}</span>}
                        
                        {isSubscribed ? (
                          <button
                            className="tv-add-btn subscribed"
                            title="Unsubscribe"
                            disabled={removeWatchlist.isPending}
                            onClick={(e) => {
                              e.stopPropagation()
                              removeWatchlist.mutate(watchItem.id)
                            }}
                          >
                            <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                              <polyline points="20 6 9 17 4 12"></polyline>
                            </svg>
                          </button>
                        ) : (
                          <button
                            className="tv-add-btn"
                            title="Subscribe"
                            disabled={addWatchlist.isPending}
                            onClick={(e) => {
                              e.stopPropagation()
                              addWatchlist.mutate({ symbol: i.symbol, dataType: 'symbolUpdate' })
                            }}
                          >
                            <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                              <line x1="12" y1="5" x2="12" y2="19"></line>
                              <line x1="5" y1="12" x2="19" y2="12"></line>
                            </svg>
                          </button>
                        )}
                      </div>
                    </div>
                  )
                })}
              </div>
            )
          }}
        </QueryBoundary>
      </div>
      
      <div style={{ marginTop: '24px' }}>
        <p className="muted small-note">Showing at most 50 matches — refine the query for more specific results.</p>
      </div>
    </div>
  )
}
