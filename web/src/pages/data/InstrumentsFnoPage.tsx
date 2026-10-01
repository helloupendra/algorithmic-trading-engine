/**
 * Data → Instruments: the broker instrument master (equity, futures, options
 * and indices across NSE, BSE and MCX). Search it, read a contract's lot size,
 * tick size and expiry, and put it on the live feed.
 *
 * It also used to walk underlying → expiry → a CE/PE ladder of contract names.
 * Markets → Option chain reads the same chains with prices, OI and greeks, so
 * the ladder went; the chain is read in one place.
 */

import { useEffect, useRef, useState } from 'react'
import { useAddWatchlistSymbol, useInstrumentMasters, useInstrumentSearch } from '../../lib/queries'
import { formatNumber } from '../../lib/format'
import { Badge, InlineError, Panel, QueryBoundary } from '../../components/ui'
import { IconPlus, IconSearch } from '../../components/icons'
import type { Instrument } from '../../lib/types'
import './data.css'

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
  const masters = useInstrumentMasters()
  const leadRef = useRef<HTMLDivElement>(null)

  // On a phone the detail card sits above the results (data.css), so a tap
  // on a row further down is answered by bringing the card back into view.
  useEffect(() => {
    if (!selected || !window.matchMedia('(max-width: 760px)').matches) return
    leadRef.current?.scrollIntoView({ block: 'nearest' })
  }, [selected])

  // What the search covers, from the masters on this host: the copy used to
  // say "1.7 lakh" and "NSE + BSE" by hand while an MCX master sat below it.
  const masterList = masters.data?.masters ?? []
  const instrumentCount = masterList.reduce((sum, m) => sum + m.activeRowsInDb, 0)
  const exchanges = [...new Set(masterList.map((m) => m.exchange))]
  const scope =
    instrumentCount > 0
      ? `${formatNumber(instrumentCount)} instruments across ${exchanges.join(', ')}`
      : 'the instrument master'

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
              aria-pressed={type === f.key}
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
        placeholder="Symbol or company name…"
        value={query}
        onChange={(e) => {
          setQuery(e.target.value)
          setSelected(null)
        }}
      />

      {query.trim().length < 2 ? (
        <p className="empty">
          Type at least two characters to search {scope} — a symbol or a company name, e.g. RELIANCE,
          BANKNIFTY, GOLD. Top 50 matches shown.
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
                        <td className="mono">
                          {/* A real button, so the keyboard can pick a row too (the
                              row's onClick keeps the whole row a pointer target). */}
                          <button
                            type="button"
                            className="row-pick"
                            aria-pressed={selected?.id === inst.id}
                            onClick={(e) => {
                              e.stopPropagation()
                              setSelected(inst)
                            }}
                          >
                            {inst.symbol}
                          </button>
                        </td>
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

          <div className="two-col__lead" ref={leadRef}>
            {selected ? (
              /* key remounts the card per instrument so mutation state
                 (the "added" alert) never leaks across selections */
              <InstrumentDetail key={selected.id} instrument={selected} />
            ) : (
              <div className="card card--dashed" style={{ textAlign: 'center' }}>
                <p className="card__muted" style={{ margin: 0 }}>
                  Select a row to see lot size, tick size, expiry and watch it live.
                </p>
              </div>
            )}
          </div>
        </div>
      )}
    </Panel>
  )
}

export function InstrumentsFnoPage() {
  return (
    <div className="page data-page">
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
