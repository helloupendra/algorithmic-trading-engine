/**
 * Data → Instruments: the broker instrument master (equity, futures, options
 * and indices across NSE, BSE and MCX). Search it, read a contract's lot size,
 * tick size and expiry, and put it on the live feed.
 *
 * It also used to walk underlying → expiry → a CE/PE ladder of contract names.
 * Markets → Option chain reads the same chains with prices, OI and greeks, so
 * the ladder went; the chain is read in one place.
 */

import { useState } from 'react'
import { useAddWatchlistSymbol, useInstrumentSearch } from '../../lib/queries'
import { formatNumber } from '../../lib/format'
import { Badge, InlineError, Panel, QueryBoundary } from '../../components/ui'
import { IconPlus, IconSearch } from '../../components/icons'
import type { Instrument } from '../../lib/types'

const TYPE_FILTERS = [
  { key: undefined, label: 'All' },
  { key: 'EQ', label: 'Equity' },
  { key: 'FUT', label: 'Futures' },
  { key: 'OPT', label: 'Options' },
  { key: 'INDEX', label: 'Index' },
] as const

function InstrumentDetail({ instrument }: { instrument: Instrument }) {
  const add = useAddWatchlistSymbol()

  return (
    <div className="card">
      <div className="panel__head">
        <h3 className="panel__title mono">{instrument.symbol}</h3>
        <button
          className="btn btn--sm"
          disabled={add.isPending}
          onClick={() => add.mutate({ symbol: instrument.symbol, dataType: 'symbolUpdate' })}
        >
          <IconPlus style={{ width: 13, height: 13 }} /> Watch live
        </button>
      </div>
      {add.isError && <InlineError error={add.error} />}
      {add.isSuccess && (
        <div className="alert alert--success" style={{ marginBottom: 10 }}>
          <span>Added to the live watchlist.</span>
        </div>
      )}
      <div className="kv-grid">
        <div>
          <span className="muted">Description</span>
          <span>{instrument.description || '—'}</span>
        </div>
        <div>
          <span className="muted">Exchange / segment</span>
          <span>
            {instrument.exchange} · {instrument.segment}
          </span>
        </div>
        <div>
          <span className="muted">Type</span>
          <span>{instrument.instrumentType}</span>
        </div>
        <div>
          <span className="muted">Lot size</span>
          <span>{instrument.lotSize ?? '—'}</span>
        </div>
        <div>
          <span className="muted">Tick size</span>
          <span>{instrument.tickSize ?? '—'}</span>
        </div>
        <div>
          <span className="muted">ISIN</span>
          <span className="mono">{instrument.isin || '—'}</span>
        </div>
        {instrument.expiryDate && (
          <div>
            <span className="muted">Expiry</span>
            <span>{instrument.expiryDate}</span>
          </div>
        )}
        {instrument.strikePrice != null && (
          <div>
            <span className="muted">Strike</span>
            <span>
              {formatNumber(instrument.strikePrice)} {instrument.optionType}
            </span>
          </div>
        )}
        {instrument.underlying && (
          <div>
            <span className="muted">Underlying</span>
            <span className="mono">{instrument.underlying}</span>
          </div>
        )}
      </div>
    </div>
  )
}

function MasterSearchPanel() {
  const [query, setQuery] = useState('')
  const [type, setType] = useState<string | undefined>(undefined)
  const [selected, setSelected] = useState<Instrument | null>(null)
  const search = useInstrumentSearch(query, type)

  return (
    <Panel
      title={
        <>
          <IconSearch /> Instrument master
        </>
      }
      actions={
        <div className="seg" role="group" aria-label="Instrument type">
          {TYPE_FILTERS.map((f) => (
            <button
              key={f.label}
              type="button"
              className={`seg__btn ${type === f.key ? 'is-active' : ''}`}
              onClick={() => setType(f.key)}
            >
              {f.label}
            </button>
          ))}
        </div>
      }
    >
      <input
        className="field__input"
        style={{ width: '100%', marginBottom: 10 }}
        placeholder="Search 1.7 lakh instruments — symbol or company name, e.g. RELIANCE, BANKNIFTY, GOLD…"
        value={query}
        onChange={(e) => {
          setQuery(e.target.value)
          setSelected(null)
        }}
      />

      {query.trim().length < 2 ? (
        <p className="empty">
          Type at least two characters. The master covers NSE cash + F&O and BSE F&O (top 50
          matches shown).
        </p>
      ) : (
        <div className="two-col" style={{ alignItems: 'start' }}>
          <QueryBoundary query={search} empty={`No instruments match “${query}”.`}>
            {(rows) => (
              <div className="tablewrap tablewrap--tall">
                <table className="table table--hover">
                  <thead>
                    <tr>
                      <th>Symbol</th>
                      <th>Description</th>
                      <th>Type</th>
                    </tr>
                  </thead>
                  <tbody>
                    {rows.map((inst) => (
                      <tr
                        key={inst.id}
                        className={selected?.id === inst.id ? 'row--selected' : ''}
                        onClick={() => setSelected(inst)}
                      >
                        <td className="mono">{inst.symbol}</td>
                        <td className="muted">{inst.description}</td>
                        <td>
                          <Badge tone="neutral">{inst.instrumentType}</Badge>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </QueryBoundary>

          {selected ? (
            /* key remounts the card per instrument so mutation state
               (the "added" alert) never leaks across selections */
            <InstrumentDetail key={selected.id} instrument={selected} />
          ) : (
            <div className="card card--dashed" style={{ textAlign: 'center' }}>
              <p className="card__muted" style={{ margin: 0 }}>
                Click a row to see lot size, tick size, expiry and watch it live.
              </p>
            </div>
          )}
        </div>
      )}
    </Panel>
  )
}

export function InstrumentsFnoPage() {
  return (
    <div className="page">
      <header className="page__header">
        <div>
          <h1 className="page__title">Instruments</h1>
          <p className="page__subtitle">
            The tradable universe: search the master, read a contract, and watch it live. Chains are read on
            Markets → Option chain.
          </p>
        </div>
      </header>

      <MasterSearchPanel />
    </div>
  )
}
