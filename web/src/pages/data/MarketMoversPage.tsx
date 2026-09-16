/**
 * Market movers: what the F&O market is doing right now, from Angel One.
 *
 * Three sections, one at a time — which names are moving (price), where money
 * is entering or leaving (open interest, read as a build-up), and how
 * put-heavy each underlying is (put-call ratio). The chosen section lives in
 * the URL, so a refresh or a shared link opens where it was.
 *
 * Every card scrolls on its own with its column headers held in place: the
 * lists are long (215 underlyings for the PCR) and the page must not turn into
 * one column the reader scrolls through to find a panel.
 *
 * The snapshot is built by the API from six SmartAPI calls and cached for 45
 * seconds, because the vendor refuses a burst; the page polls at that pace and
 * shows when the picture was taken, so a stale number never passes for live.
 */

import { useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useAngelMovers } from '../../lib/queries'
import {
  MOVER_SECTIONS,
  buildUpCounts,
  buildUpTone,
  filterBuildUp,
  formatOi,
  movePercent,
  pcrSummary,
  sectionCount,
  sectionFrom,
  sortPcr,
} from '../../lib/movers'
import type { MoverRow, MoversSnapshot, PcrOrder } from '../../lib/movers'
import { formatAge } from '../../lib/format'
import { Badge, EmptyState, InlineError, Loading, Panel } from '../../components/ui'
import './movers.css'

function Underlying({ row }: { row: { underlying: string; symbol: string } }) {
  return (
    <>
      {row.underlying}
      <span className="mv-sym__code">{row.symbol}</span>
    </>
  )
}

function PriceCard({ title, rows, tone }: { title: string; rows: MoverRow[]; tone: 'pos' | 'neg' }) {
  return (
    <Panel title={`${title} · ${rows.length}`} className="mv-card">
      {rows.length === 0 ? (
        <EmptyState>Nothing came back for this list.</EmptyState>
      ) : (
        <div className="mv-scroll">
          <table className="table">
            <thead>
              <tr>
                <th>Underlying</th>
                <th className="num">Last</th>
                <th className="num">Change</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => (
                <tr key={row.symbol}>
                  <td>
                    <Underlying row={row} />
                  </td>
                  <td className="num mono">{row.ltp.toLocaleString('en-IN')}</td>
                  <td className="num">
                    <Badge tone={tone}>{movePercent(row.priceChangePercent)}</Badge>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Panel>
  )
}

function PriceSection({ data }: { data: MoversSnapshot }) {
  return (
    <div className="mv-grid">
      <PriceCard title="Top gainers" rows={data.priceGainers ?? []} tone="pos" />
      <PriceCard title="Top losers" rows={data.priceLosers ?? []} tone="neg" />
    </div>
  )
}

function BuildUpSection({ data }: { data: MoversSnapshot }) {
  const [reading, setReading] = useState('all')
  const counts = buildUpCounts(data.buildUp)
  const rows = filterBuildUp(data.buildUp, reading)

  return (
    <Panel title="Open interest build-up" className="mv-card">
      <p className="muted small" style={{ marginTop: 0 }}>
        Open interest and price read together: money entering on the long side, fresh shorts, shorts closing, or
        longs leaving.
      </p>
      <div className="mv-filters" role="group" aria-label="Filter by reading">
        <button
          type="button"
          className="mv-filter"
          aria-pressed={reading === 'all'}
          onClick={() => setReading('all')}
        >
          All<span className="mv-filter__count">{data.buildUp?.length ?? 0}</span>
        </button>
        {counts.map((c) => (
          <button
            key={c.label}
            type="button"
            className="mv-filter"
            aria-pressed={reading === c.label}
            onClick={() => setReading(c.label)}
          >
            {c.label}
            <span className="mv-filter__count">{c.count}</span>
          </button>
        ))}
      </div>
      {rows.length === 0 ? (
        <EmptyState>No open-interest movers for this reading.</EmptyState>
      ) : (
        <div className="mv-scroll">
          <table className="table">
            <thead>
              <tr>
                <th>Underlying</th>
                <th className="num">Last</th>
                <th className="num">Price</th>
                <th className="num">OI</th>
                <th className="num">OI change</th>
                <th>Reading</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => (
                <tr key={row.symbol}>
                  <td>
                    <Underlying row={row} />
                  </td>
                  <td className="num mono">{row.ltp ? row.ltp.toLocaleString('en-IN') : '—'}</td>
                  <td className="num mono">{movePercent(row.priceChangePercent)}</td>
                  <td className="num mono">{formatOi(row.openInterest)}</td>
                  <td className="num mono">{movePercent(row.oiChangePercent)}</td>
                  <td>
                    {row.buildUp ? (
                      <Badge tone={buildUpTone(row.buildUp)}>{row.buildUp}</Badge>
                    ) : (
                      <span className="muted small">no price for this contract</span>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Panel>
  )
}

function PcrSection({ data }: { data: MoversSnapshot }) {
  const [order, setOrder] = useState<PcrOrder>('high')
  const [query, setQuery] = useState('')
  const summary = pcrSummary(data.pcr)
  const rows = sortPcr(data.pcr, order, query)

  return (
    <>
      <div className="mv-tiles">
        <div className="mv-tile">
          <div className="mv-tile__value">{summary.count}</div>
          <div className="mv-tile__label">underlyings</div>
        </div>
        <div className="mv-tile">
          <div className="mv-tile__value">{summary.median != null ? summary.median.toFixed(2) : '—'}</div>
          <div className="mv-tile__label">median PCR</div>
        </div>
        <div className="mv-tile">
          <div className="mv-tile__value pos">{summary.putHeavy}</div>
          <div className="mv-tile__label">put-heavy (PCR above 1)</div>
        </div>
        <div className="mv-tile">
          <div className="mv-tile__value neg">{summary.callHeavy}</div>
          <div className="mv-tile__label">call-heavy (PCR below 0.5)</div>
        </div>
      </div>

      <Panel title="Put-call ratio by underlying" className="mv-card">
        <div className="mv-tools">
          <input
            className="field__input field__input--sm"
            placeholder="Search underlying"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            aria-label="Search underlying"
          />
          <div className="mv-filters" role="group" aria-label="Order" style={{ margin: 0 }}>
            <button type="button" className="mv-filter" aria-pressed={order === 'high'} onClick={() => setOrder('high')}>
              Most put-heavy first
            </button>
            <button type="button" className="mv-filter" aria-pressed={order === 'low'} onClick={() => setOrder('low')}>
              Most call-heavy first
            </button>
          </div>
          <span className="muted small">{rows.length} shown</span>
        </div>
        {rows.length === 0 ? (
          <EmptyState>No underlying matches “{query}”.</EmptyState>
        ) : (
          <div className="mv-scroll">
            <table className="table">
              <thead>
                <tr>
                  <th className="col-rank">#</th>
                  <th>Underlying</th>
                  <th className="num">PCR</th>
                  <th>Reading</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((row, i) => (
                  <tr key={row.symbol}>
                    <td className="muted mono">{i + 1}</td>
                    <td>
                      <Underlying row={row} />
                    </td>
                    <td className="num mono">{row.pcr.toFixed(2)}</td>
                    <td>
                      {row.pcr > 1 ? (
                        <Badge tone="pos">put-heavy</Badge>
                      ) : row.pcr < 0.5 ? (
                        <Badge tone="neg">call-heavy</Badge>
                      ) : (
                        <span className="muted small">balanced</span>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Panel>
    </>
  )
}

export default function MarketMoversPage() {
  const movers = useAngelMovers()
  const [params, setParams] = useSearchParams()
  const section = sectionFrom(params.get('section'))
  const data = movers.data

  if (movers.isLoading && !data) return <Loading />
  if (movers.isError) return <InlineError error={movers.error} />

  if (data && data.configured === false) {
    return (
      <div className="page mv">
        <header className="mv-head">
          <h1>Market movers</h1>
        </header>
        <Panel title="Angel One is not configured">
          <p>{data.message}</p>
          <p className="muted small">
            These screens come from Angel One's SmartAPI. Fill{' '}
            <span className="mono">{(data.missing ?? []).join(', ')}</span> in <code>.env</code> and restart the
            API.
          </p>
        </Panel>
      </div>
    )
  }

  return (
    <div className="page mv">
      <header className="mv-head">
        <div>
          <h1>Market movers</h1>
          <p className="muted small mv-head__meta">
            Angel One · {data?.expiryType ?? 'NEAR'} expiry futures ·{' '}
            {data?.asOfUtc ? `as of ${formatAge(data.asOfUtc)}` : 'loading'} · refreshes every 45 s
            {movers.isFetching && ' · updating…'}
          </p>
        </div>
        <button className="btn" onClick={() => movers.refetch()} disabled={movers.isFetching}>
          {movers.isFetching ? 'Refreshing…' : 'Refresh'}
        </button>
      </header>

      <div className="oc-tabs" role="tablist" aria-label="Section">
        {MOVER_SECTIONS.map((s) => (
          <button
            key={s.key}
            type="button"
            role="tab"
            aria-selected={section === s.key}
            className={`oc-tab ${section === s.key ? 'oc-tab--on' : ''}`}
            onClick={() => setParams({ section: s.key }, { replace: true })}
          >
            {s.label} <span className="muted">· {sectionCount(data, s.key)}</span>
          </button>
        ))}
      </div>

      {(data?.warnings ?? []).length > 0 && (
        <Panel title="Parts of this snapshot are missing">
          <ul style={{ margin: 0 }}>
            {data!.warnings!.map((w) => (
              <li key={w} className="small">
                {w}
              </li>
            ))}
          </ul>
        </Panel>
      )}

      <div className="mv-body" role="tabpanel">
        {data && section === 'price' && <PriceSection data={data} />}
        {data && section === 'buildup' && <BuildUpSection data={data} />}
        {data && section === 'pcr' && <PcrSection data={data} />}
      </div>
    </div>
  )
}
