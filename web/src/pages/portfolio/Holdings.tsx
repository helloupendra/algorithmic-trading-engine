/**
 * The holdings: a sortable table with a search box, and on a phone a card a
 * holding (the table's ten columns do not fit a phone's width, and a sideways
 * table hides the value column the eye looks for first).
 */

import { useMemo, useState } from 'react'
import type { ReactNode } from 'react'
import { formatInrSigned, formatInrWhole, formatNumber, formatPrice } from '../../lib/format'
import {
  brokerName,
  filterHoldings,
  firstSortDir,
  formatSignedPct,
  formatWeight,
  signTone,
  sortHoldings,
} from '../../lib/portfolio'
import type { Holding, HoldingSortKey, SortDir } from '../../lib/portfolio'
import { Panel } from '../../components/ui'

interface Column {
  key: HoldingSortKey
  label: string
  num?: boolean
  /** What the header's sort button says to a screen reader. */
  long?: string
}

const COLUMNS: Column[] = [
  { key: 'symbol', label: 'Symbol' },
  { key: 'name', label: 'Name' },
  { key: 'brokers', label: 'Broker' },
  { key: 'qty', label: 'Qty', num: true, long: 'Quantity' },
  { key: 'avgPrice', label: 'Avg', num: true, long: 'Average price' },
  { key: 'ltp', label: 'LTP', num: true, long: 'Last traded price' },
  { key: 'value', label: 'Value', num: true },
  { key: 'pnl', label: 'P&L', num: true },
  { key: 'pnlPct', label: 'P&L %', num: true },
  { key: 'weight', label: 'Weight', num: true, long: 'Weight in the portfolio' },
]

function brokersText(h: Holding): string {
  return h.brokers.length ? h.brokers.map((b) => brokerName(b)).join(' · ') : '—'
}

function Signed({ value, children }: { value: number | null; children: ReactNode }) {
  return <span className={signTone(value)}>{children}</span>
}

function HoldingCard({ h }: { h: Holding }) {
  return (
    <li className="pf-card">
      <div className="pf-card__top">
        <b className="pf-card__sym">{h.symbol}</b>
        <span className="pf-card__value">{formatInrWhole(h.value)}</span>
      </div>
      <div className="pf-card__sub">
        <span className="pf-card__name">{h.name}</span>
        <span className="pf-card__weight">{formatWeight(h.weight)}</span>
      </div>
      <dl className="pf-card__facts">
        <div>
          <dt>Qty · avg</dt>
          <dd>
            {formatNumber(h.qty)} × {formatPrice(h.avgPrice)}
          </dd>
        </div>
        <div>
          <dt>LTP</dt>
          <dd>{formatPrice(h.ltp)}</dd>
        </div>
        <div>
          <dt>P&amp;L</dt>
          <dd>
            <Signed value={h.pnl}>
              {formatInrSigned(h.pnl)} <span className="pf-card__pct">{formatSignedPct(h.pnlPct)}</span>
            </Signed>
          </dd>
        </div>
        <div>
          <dt>Broker</dt>
          <dd>{brokersText(h)}</dd>
        </div>
      </dl>
    </li>
  )
}

export function HoldingsPanel({ holdings }: { holdings: Holding[] }) {
  const [query, setQuery] = useState('')
  const [sort, setSort] = useState<{ key: HoldingSortKey; dir: SortDir }>({ key: 'value', dir: 'desc' })

  const shown = useMemo(() => sortHoldings(filterHoldings(holdings, query), sort.key, sort.dir), [holdings, query, sort])

  function sortBy(key: HoldingSortKey) {
    setSort((s) => (s.key === key ? { key, dir: s.dir === 'asc' ? 'desc' : 'asc' } : { key, dir: firstSortDir(key) }))
  }

  const sortedLabel = COLUMNS.find((c) => c.key === sort.key)?.long ?? COLUMNS.find((c) => c.key === sort.key)?.label ?? ''
  const count =
    query.trim() && shown.length !== holdings.length ? `${shown.length} of ${holdings.length}` : `${holdings.length}`

  return (
    <Panel
      className="pf-holdings"
      title={
        <>
          Holdings <span className="faint pf-title-note">{count}</span>
        </>
      }
      actions={
        holdings.length > 0 && (
          <div className="pf-holdings__tools">
            <label className="pf-sr" htmlFor="pf-search">
              Search holdings
            </label>
            <input
              id="pf-search"
              className="field__input field__input--sm pf-search"
              type="search"
              placeholder="Search holdings"
              title="Symbol, name, ISIN, sector or broker"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              autoComplete="off"
              spellCheck={false}
            />
            {/* The cards have no header to click: a phone sorts here. */}
            <label className="pf-sr" htmlFor="pf-sort">
              Sort holdings by
            </label>
            <select
              id="pf-sort"
              className="field__input field__input--sm pf-sort-select"
              value={`${sort.key}:${sort.dir}`}
              onChange={(e) => {
                const [key, dir] = e.target.value.split(':') as [HoldingSortKey, SortDir]
                setSort({ key, dir })
              }}
            >
              <option value="value:desc">Largest value</option>
              <option value="pnl:desc">Largest P&amp;L</option>
              <option value="pnl:asc">Smallest P&amp;L</option>
              <option value="pnlPct:desc">Largest P&amp;L %</option>
              <option value="pnlPct:asc">Smallest P&amp;L %</option>
              <option value="symbol:asc">Symbol A–Z</option>
              {/* A header click can pick an order the list does not offer; it is shown, not lost. */}
              {![
                'value:desc',
                'pnl:desc',
                'pnl:asc',
                'pnlPct:desc',
                'pnlPct:asc',
                'symbol:asc',
              ].includes(`${sort.key}:${sort.dir}`) && (
                <option value={`${sort.key}:${sort.dir}`}>
                  {sortedLabel}, {sort.dir === 'asc' ? 'ascending' : 'descending'}
                </option>
              )}
            </select>
          </div>
        )
      }
    >
      {holdings.length === 0 ? (
        <p className="empty">No holdings in the linked accounts at the last sync.</p>
      ) : shown.length === 0 ? (
        <p className="empty">No holding matches “{query.trim()}”.</p>
      ) : (
        <>
          <div className="tablewrap pf-table">
            <table className="table">
              <caption className="pf-sr">
                Holdings, sorted by {sortedLabel.toLowerCase()}, {sort.dir === 'asc' ? 'ascending' : 'descending'}
              </caption>
              <thead>
                <tr>
                  {COLUMNS.map((c) => {
                    const active = sort.key === c.key
                    return (
                      <th
                        key={c.key}
                        scope="col"
                        className={c.num ? 'num' : undefined}
                        aria-sort={active ? (sort.dir === 'asc' ? 'ascending' : 'descending') : undefined}
                      >
                        <button type="button" className={`pf-sort${active ? ' is-active' : ''}`} onClick={() => sortBy(c.key)}>
                          {c.label}
                          <span className="pf-sort__arrow" aria-hidden="true">
                            {active ? (sort.dir === 'asc' ? '↑' : '↓') : '↕'}
                          </span>
                        </button>
                      </th>
                    )
                  })}
                </tr>
              </thead>
              <tbody>
                {shown.map((h) => (
                  <tr key={`${h.symbol}:${h.isin ?? ''}`}>
                    <th scope="row" className="pf-sym">
                      {h.symbol}
                    </th>
                    <td className="pf-name" title={h.isin ? `${h.name} · ${h.isin}` : h.name}>
                      {h.name}
                    </td>
                    <td className="pf-brokers">{brokersText(h)}</td>
                    <td className="num">{formatNumber(h.qty)}</td>
                    <td className="num">{formatPrice(h.avgPrice)}</td>
                    <td className="num">{formatPrice(h.ltp)}</td>
                    <td className="num">{formatInrWhole(h.value)}</td>
                    <td className={`num ${signTone(h.pnl)}`}>{formatInrSigned(h.pnl)}</td>
                    <td className={`num ${signTone(h.pnl)}`}>{formatSignedPct(h.pnlPct)}</td>
                    <td className="num">{formatWeight(h.weight)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <ul className="pf-cards" aria-label="Holdings">
            {shown.map((h) => (
              <HoldingCard key={`${h.symbol}:${h.isin ?? ''}`} h={h} />
            ))}
          </ul>
        </>
      )}
    </Panel>
  )
}
