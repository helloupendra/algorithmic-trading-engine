/**
 * Data module — Instruments & F&O. Searches the broker instrument master
 * (equity, futures, options, indices across NSE/BSE/MCX) and explores the
 * derivatives universe availability-first: underlying → the expiries that
 * actually exist → the full CE/PE chain for one expiry.
 */

import { useMemo, useState } from 'react'
import {
  useAddWatchlistSymbol,
  useExpiries,
  useInstrumentSearch,
  useOptionChain,
} from '../../lib/queries'
import { formatNumber } from '../../lib/format'
import { Badge, InlineError, Panel, QueryBoundary } from '../../components/ui'
import { IconLayers, IconPlus, IconSearch } from '../../components/icons'
import type { Instrument, OptionChainItem } from '../../lib/types'

const TYPE_FILTERS = [
  { key: undefined, label: 'All' },
  { key: 'EQ', label: 'Equity' },
  { key: 'FUT', label: 'Futures' },
  { key: 'OPT', label: 'Options' },
  { key: 'INDEX', label: 'Index' },
] as const

const COMMON_UNDERLYINGS = ['NIFTY', 'BANKNIFTY', 'FINNIFTY', 'MIDCPNIFTY', 'SENSEX']

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

/** strike ladder: CE on the left, PE on the right. */
function ChainTable({ chain }: { chain: OptionChainItem[] }) {
  const add = useAddWatchlistSymbol()

  const ladder = useMemo(() => {
    const byStrike = new Map<number, { ce?: OptionChainItem; pe?: OptionChainItem }>()
    for (const item of chain) {
      if (item.strikePrice == null) continue
      if (!byStrike.has(item.strikePrice)) byStrike.set(item.strikePrice, {})
      const slot = byStrike.get(item.strikePrice)!
      if (item.optionType === 'CE') slot.ce = item
      else if (item.optionType === 'PE') slot.pe = item
    }
    return [...byStrike.entries()].sort((a, b) => a[0] - b[0])
  }, [chain])

  return (
    <div className="tablewrap tablewrap--tall">
      <table className="table table--center">
        <thead>
          <tr>
            <th>Call (CE)</th>
            <th></th>
            <th className="c">Strike</th>
            <th></th>
            <th>Put (PE)</th>
          </tr>
        </thead>
        <tbody>
          {ladder.map(([strike, { ce, pe }]) => (
            <tr key={strike}>
              <td className="mono muted">{ce ? ce.symbol.split(':')[1] : '—'}</td>
              <td>
                {ce && (
                  <button
                    className="btn btn--ghost btn--sm"
                    title="Watch CE live"
                    disabled={add.isPending}
                    onClick={() => add.mutate({ symbol: ce.symbol, dataType: 'symbolUpdate' })}
                  >
                    <IconPlus style={{ width: 12, height: 12 }} />
                  </button>
                )}
              </td>
              <td className="strike">{formatNumber(strike)}</td>
              <td>
                {pe && (
                  <button
                    className="btn btn--ghost btn--sm"
                    title="Watch PE live"
                    disabled={add.isPending}
                    onClick={() => add.mutate({ symbol: pe.symbol, dataType: 'symbolUpdate' })}
                  >
                    <IconPlus style={{ width: 12, height: 12 }} />
                  </button>
                )}
              </td>
              <td className="mono muted">{pe ? pe.symbol.split(':')[1] : '—'}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function FnoExplorerPanel() {
  const [underlying, setUnderlying] = useState('NIFTY')
  const [expiry, setExpiry] = useState<string | null>(null)

  const expiries = useExpiries(underlying)
  const chain = useOptionChain(underlying, expiry)

  const expiryList = expiries.data ?? []

  return (
    <Panel
      title={
        <>
          <IconLayers /> F&O explorer
        </>
      }
      actions={
        <div className="inline-form">
          {COMMON_UNDERLYINGS.map((u) => (
            <button
              key={u}
              type="button"
              className={`btn btn--sm ${underlying === u ? 'btn--primary' : 'btn--ghost'}`}
              onClick={() => {
                setUnderlying(u)
                setExpiry(null)
              }}
            >
              {u}
            </button>
          ))}
          <input
            className="field__input field__input--sm"
            style={{ minWidth: 130 }}
            placeholder="Other underlying…"
            value={underlying}
            onChange={(e) => {
              setUnderlying(e.target.value.toUpperCase())
              setExpiry(null)
            }}
          />
        </div>
      }
    >
      <p className="field__label" style={{ marginBottom: 6 }}>
        Available expiries
      </p>
      {underlying.trim().length === 0 ? (
        <p className="empty">Pick or type an underlying to see its expiries.</p>
      ) : (
      <QueryBoundary
        query={expiries}
        empty={`No derivative contracts found for “${underlying}” in the instrument master.`}
      >
        {() => (
          <div className="chip-row" style={{ marginBottom: 14 }}>
            {expiryList.map((e) => (
              <button
                key={e.expiryDate}
                type="button"
                className={`btn btn--sm ${expiry === e.expiryDate ? 'btn--primary' : ''}`}
                onClick={() => setExpiry(e.expiryDate)}
              >
                {new Date(e.expiryDate).toLocaleDateString('en-IN', {
                  day: '2-digit',
                  month: 'short',
                  year: '2-digit',
                })}
              </button>
            ))}
          </div>
        )}
      </QueryBoundary>
      )}

      {expiry &&
        (chain.isPending ? (
          <p className="empty">Loading chain…</p>
        ) : chain.isError ? (
          <InlineError error={chain.error} />
        ) : (
          <>
            <p className="small-note" style={{ margin: '0 0 8px' }}>
              {formatNumber((chain.data ?? []).length)} contracts for {underlying} ·{' '}
              {new Date(expiry).toLocaleDateString('en-IN', {
                day: '2-digit',
                month: 'long',
                year: 'numeric',
              })}
              . Use + to subscribe a leg to the live feed.
            </p>
            <ChainTable chain={chain.data ?? []} />
          </>
        ))}
    </Panel>
  )
}

export function InstrumentsFnoPage() {
  return (
    <div className="page">
      <header className="page__header">
        <div>
          <h1 className="page__title">Instruments & F&O</h1>
          <p className="page__subtitle">
            The tradable universe: search the master, then walk underlying → expiry → chain.
          </p>
        </div>
      </header>

      <MasterSearchPanel />
      <FnoExplorerPanel />
    </div>
  )
}
