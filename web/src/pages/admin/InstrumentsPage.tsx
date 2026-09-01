/**
 * Instrument universe: search the ~1 lakh imported NSE instruments, and
 * resolve derivative expiries via the rule engine.
 */

import { useState } from 'react'
import { useExpiries, useInstrumentSearch } from '../../lib/queries'
import { formatNumber } from '../../lib/format'
import { Badge, Panel, QueryBoundary } from '../../components/ui'

export function InstrumentsPage() {
  const [query, setQuery] = useState('BANKNIFTY')
  const [submitted, setSubmitted] = useState('BANKNIFTY')
  const search = useInstrumentSearch(submitted)

  const [underlying, setUnderlying] = useState('BANKNIFTY')
  const expiries = useExpiries(underlying)

  return (
    <div className="page">
      <header className="page__header">
        <h1 className="page__title">Instruments</h1>
        <p className="page__subtitle">
          The imported instrument master (NSE cash + F&amp;O) and the expiry rule engine.
        </p>
      </header>

      <Panel
        title="Search"
        actions={
          <form
            className="inline-form"
            onSubmit={(e) => {
              e.preventDefault()
              setSubmitted(query.trim())
            }}
          >
            <input
              className="field__input field__input--sm"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              placeholder="symbol / name / ISIN"
              aria-label="Search instruments"
            />
            <button className="btn btn--primary btn--sm">Search</button>
          </form>
        }
      >
        <QueryBoundary query={search} empty={`No instruments match "${submitted}".`}>
          {(data) => (
            <div className="tablewrap tablewrap--tall">
              <table className="table">
                <thead>
                  <tr>
                    <th>Symbol</th>
                    <th>Type</th>
                    <th>Description</th>
                    <th className="r">Lot</th>
                    <th className="r">Tick</th>
                    <th className="r">Strike</th>
                    <th>Expiry</th>
                  </tr>
                </thead>
                <tbody>
                  {data.map((i) => (
                    <tr key={i.id}>
                      <td className="mono">{i.symbol}</td>
                      <td>
                        <Badge tone={i.instrumentType?.includes('OPT') ? 'accent' : 'neutral'}>
                          {i.instrumentType || i.segment}
                        </Badge>{' '}
                        {i.optionType && <Badge tone={i.optionType === 'CE' ? 'pos' : 'neg'}>{i.optionType}</Badge>}
                      </td>
                      <td className="muted">{i.description}</td>
                      <td className="r mono">{formatNumber(i.lotSize)}</td>
                      <td className="r mono">{i.tickSize ?? '—'}</td>
                      <td className="r mono">{i.strikePrice != null ? formatNumber(i.strikePrice) : '—'}</td>
                      <td className="muted">{i.expiryDate ? i.expiryDate.slice(0, 10) : '—'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </QueryBoundary>
        <p className="muted small-note">Showing at most 50 matches — refine the query for more specific results.</p>
      </Panel>

      <Panel
        title="Derivative expiries"
        actions={
          <input
            className="field__input field__input--sm"
            value={underlying}
            onChange={(e) => setUnderlying(e.target.value.toUpperCase())}
            aria-label="Underlying"
          />
        }
      >
        <QueryBoundary query={expiries} empty={`No expiries in the master for ${underlying}.`}>
          {(data) => (
            <div className="chip-row">
              {data.map((e) => (
                <span key={e.expiryDate} className="badge badge--neutral">
                  {e.expiryDate}
                </span>
              ))}
            </div>
          )}
        </QueryBoundary>
      </Panel>
    </div>
  )
}
