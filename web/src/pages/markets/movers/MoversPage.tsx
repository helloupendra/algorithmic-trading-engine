/**
 * Markets → Movers: who is moving, from every source the desk has, one
 * section per source and each labelled with where its numbers come from.
 *
 *  - F&O movers (Angel One): the market-wide futures screens, as tabs:
 *    gainers and losers, open-interest build-up, put-call ratio. Built by the
 *    API from six SmartAPI calls and cached for 45 seconds, because the vendor
 *    refuses a burst; the section polls at that pace and says when the
 *    picture was taken, so a stale number never passes for live.
 *  - Index movers (public quotes): day gainers and losers inside an index.
 *  - Futures build-up (NSE's evening bhavcopy): index futures today and over
 *    ten sessions, and the day's stock futures by build-up.
 *
 * These were three pages: two both called "movers" side by side in the
 * trader's menu, and the build-up inside Market factors. Each section scrolls
 * its long lists in its own box, so the page reads top to bottom without one
 * list burying the next. A mechanical sort of market data, not advice.
 */

import { useEffect, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import { useLocation, useSearchParams } from 'react-router-dom'
import { useAngelMovers, useEquityGroups, useMarketFutures, useTopMovers } from '../../../lib/queries'
import {
  MOVER_SECTIONS,
  buildUpCounts,
  buildUpTone,
  filterBuildUp,
  formatOi,
  indexLabel,
  movePercent,
  pcrSummary,
  sectionCount,
  sectionFrom,
  sortPcr,
} from '../../../lib/movers'
import type { MoverRow, MoversSnapshot, PcrOrder } from '../../../lib/movers'
import { BUILD_UPS, formatDay, signTone } from '../../../lib/factors'
import { formatAge, formatPrice, pnlClass, shortSymbol } from '../../../lib/format'
import type { Mover } from '../../../lib/types'
import { Badge, EmptyState, InlineError, Loading, Panel } from '../../../components/ui'
import { ResearchNote, StatusLine } from '../factorParts'
import './movers.css'

/**
 * A link to one source (#futures, from an old Market factors URL) opens on
 * it, once it has something to show: scrolled any earlier, the sections
 * above it would still be growing and push it back out of view.
 */
function useHashTarget(id: string, ready: boolean) {
  const { hash } = useLocation()
  const done = useRef(false)
  useEffect(() => {
    if (!ready || done.current || hash !== `#${id}`) return
    done.current = true
    document.getElementById(id)?.scrollIntoView({ block: 'start' })
  }, [id, ready, hash])
}

/** A section's heading: what it ranks, and where the numbers come from. */
function SectionHead({ title, source, children }: { title: string; source: ReactNode; children?: ReactNode }) {
  return (
    <header className="mvp-head">
      <h2 className="mvp-head__title">{title}</h2>
      <span className="mvp-head__source">{source}</span>
      {children && <span className="mvp-head__tools">{children}</span>}
    </header>
  )
}

/* ------------------------------------------------------------ Angel One -- */

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

function PriceTab({ data }: { data: MoversSnapshot }) {
  return (
    <div className="mv-grid">
      <PriceCard title="Top gainers" rows={data.priceGainers ?? []} tone="pos" />
      <PriceCard title="Top losers" rows={data.priceLosers ?? []} tone="neg" />
    </div>
  )
}

function BuildUpTab({ data }: { data: MoversSnapshot }) {
  const [reading, setReading] = useState('all')
  const counts = buildUpCounts(data.buildUp)
  const rows = filterBuildUp(data.buildUp, reading)

  return (
    <Panel className="mv-card">
      <div className="mv-filters" role="group" aria-label="Filter by reading">
        <button type="button" className="mv-filter" aria-pressed={reading === 'all'} onClick={() => setReading('all')}>
          All<span className="mv-filter__count">{data.buildUp?.length ?? 0}</span>
        </button>
        {counts.map((c) => (
          <button key={c.label} type="button" className="mv-filter" aria-pressed={reading === c.label} onClick={() => setReading(c.label)}>
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
      <p className="muted small mv-note">
        Open interest and price read together: money entering on the long side, fresh shorts, shorts closing, or longs
        leaving.
      </p>
    </Panel>
  )
}

function PcrTab({ data }: { data: MoversSnapshot }) {
  const [order, setOrder] = useState<PcrOrder>('high')
  const [query, setQuery] = useState('')
  const summary = pcrSummary(data.pcr)
  const rows = sortPcr(data.pcr, order, query)

  return (
    <Panel className="mv-card">
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
        <span className="muted small">
          {summary.count} underlyings · median {summary.median != null ? summary.median.toFixed(2) : '—'} ·{' '}
          <span className="pos">{summary.putHeavy} put-heavy</span> · <span className="neg">{summary.callHeavy} call-heavy</span>
        </span>
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
      <p className="muted small mv-note">Put-heavy: PCR above 1, more puts written. Call-heavy: PCR below 0.5.</p>
    </Panel>
  )
}

function AngelSection() {
  const movers = useAngelMovers()
  const [params, setParams] = useSearchParams()
  const tab = sectionFrom(params.get('section'))
  const data = movers.data

  const source = data?.configured === false
    ? 'Angel One · not set up'
    : `Angel One · ${data?.expiryType ?? 'NEAR'} expiry futures · ${data?.asOfUtc ? `as of ${formatAge(data.asOfUtc)}` : 'reading'} · every 45 s${movers.isFetching ? ' · updating…' : ''}`

  return (
    <section className="mvp-sec" id="fno" aria-label="F&O movers">
      <SectionHead title="F&O movers" source={source}>
        <button className="btn btn--sm btn--ghost" onClick={() => movers.refetch()} disabled={movers.isFetching}>
          Refresh
        </button>
      </SectionHead>

      {movers.isError && !data ? (
        <InlineError error={movers.error} />
      ) : !data ? (
        <Loading />
      ) : data.configured === false ? (
        <p className="muted small mvp-empty">
          {data.message} These screens come from Angel One's SmartAPI: fill{' '}
          <span className="mono">{(data.missing ?? []).join(', ')}</span> in <code>.env</code> and restart the API.
        </p>
      ) : (
        <>
          <div className="oc-tabs" role="tablist" aria-label="F&O movers">
            {MOVER_SECTIONS.map((s) => (
              <button
                key={s.key}
                type="button"
                role="tab"
                aria-selected={tab === s.key}
                className={`oc-tab ${tab === s.key ? 'oc-tab--on' : ''}`}
                onClick={() => {
                  const next = new URLSearchParams(params)
                  next.set('section', s.key)
                  setParams(next, { replace: true })
                }}
              >
                {s.label} <span className="muted">· {sectionCount(data, s.key)}</span>
              </button>
            ))}
          </div>
          {(data.warnings ?? []).length > 0 && (
            <ul className="mvp-warnings small warn">
              {data.warnings!.map((w) => (
                <li key={w}>{w}</li>
              ))}
            </ul>
          )}
          <div className="mvp-body" role="tabpanel">
            {tab === 'price' && <PriceTab data={data} />}
            {tab === 'buildup' && <BuildUpTab data={data} />}
            {tab === 'pcr' && <PcrTab data={data} />}
          </div>
        </>
      )}
    </section>
  )
}

/* ------------------------------------------------------ index movers -- */

function ConstituentTable({ title, items }: { title: string; items: Mover[] }) {
  return (
    <Panel title={`${title} · ${items.length}`} className="mv-card">
      {items.length === 0 ? (
        <EmptyState>No quotes resolved for this index.</EmptyState>
      ) : (
        <div className="mv-scroll">
          <table className="table">
            <thead>
              <tr>
                <th className="col-rank">#</th>
                <th>Symbol</th>
                <th className="num">Last</th>
                <th className="num">Prev close</th>
                <th className="num">Change</th>
              </tr>
            </thead>
            <tbody>
              {items.map((m, i) => (
                <tr key={m.symbol}>
                  <td className="mono muted">{i + 1}</td>
                  <td className="mono">{shortSymbol(m.symbol)}</td>
                  <td className="num mono">{formatPrice(m.lastPrice)}</td>
                  <td className="num mono">{formatPrice(m.previousClose)}</td>
                  <td className={`num mono ${pnlClass(m.changePercent)}`}>{movePercent(m.changePercent)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Panel>
  )
}

function IndexSection() {
  const groups = useEquityGroups()
  const [group, setGroup] = useState<string | null>(null)
  const firstGroup = groups.data?.[0]?.name
  useEffect(() => {
    if (group == null && firstGroup) setGroup(firstGroup)
  }, [group, firstGroup])
  const movers = useTopMovers(group)
  const data = movers.data && movers.data.group === group ? movers.data : undefined
  useHashTarget('index', data !== undefined)

  return (
    <section className="mvp-sec" id="index" aria-label="Index movers">
      <SectionHead
        title="Index movers"
        source={
          data ? `Public quotes · ${data.symbolsResolved} quoted · fetched ${formatAge(data.fetchedUtc)}` : 'Public quotes, cached on the server'
        }
      >
        {(groups.data ?? []).length > 0 && (
          <span className="oc-seg scroll-x" role="group" aria-label="Index">
            {groups.data!.map((g) => (
              <button key={g.name} type="button" aria-pressed={group === g.name} onClick={() => setGroup(g.name)}>
                {indexLabel(g.displayName || g.name)}
              </button>
            ))}
          </span>
        )}
      </SectionHead>
      {groups.isError ? (
        <InlineError error={groups.error} />
      ) : movers.isError && !data ? (
        <InlineError error={movers.error} />
      ) : !data ? (
        groups.data && groups.data.length === 0 ? <p className="muted small mvp-empty">No index groups are set up.</p> : <Loading />
      ) : (
        <div className="mv-grid">
          <ConstituentTable title={`Gainers · ${indexLabel(data.displayName)}`} items={data.gainers} />
          <ConstituentTable title={`Losers · ${indexLabel(data.displayName)}`} items={data.losers} />
        </div>
      )}
    </section>
  )
}

/* ---------------------------------------------------- futures build-up -- */

function FuturesSection() {
  const futures = useMarketFutures(10)
  const data = futures.data
  useHashTarget('futures', data !== undefined)

  return (
    <section className="mvp-sec" id="futures" aria-label="Futures build-up">
      <SectionHead
        title="Futures build-up"
        source={data?.latestDay ? `NSE F&O bhavcopy · last session ${formatDay(data.latestDay)} · index futures live` : 'NSE F&O bhavcopy, fetched each evening'}
      />
      {futures.isError && !data && <InlineError error={futures.error} />}
      {futures.isLoading && !data && <Loading />}
      {data && !data.latestDay && (
        <p className="muted small mvp-empty">
          No bhavcopy is stored yet. The API fetches NSE's files a few minutes after it starts and every evening after 18:00 IST.
        </p>
      )}

      {data?.latestDay && (
        <>
          <Panel title="Index futures · price and open interest together" className="mv-card">
            <div className="tablewrap">
              <table className="table">
                <thead>
                  <tr>
                    <th>Future</th>
                    <th>Now, against the last close</th>
                    <th className="num">Price</th>
                    <th className="num">OI</th>
                    <th>Last session · {formatDay(data.latestDay)}</th>
                    <th className="num">Price</th>
                    <th className="num">OI</th>
                    <th>Last 10 sessions, newest left</th>
                  </tr>
                </thead>
                <tbody>
                  {data.indices.map((f) => (
                    <tr key={f.underlying}>
                      <td>
                        {f.underlying}
                        {f.latest && <span className="cell-sub muted small">exp {formatDay(f.latest.expiry)}</span>}
                      </td>
                      <td>
                        {f.live?.buildUp ? (
                          <>
                            <Badge tone={buildUpTone(f.live.buildUp)}>{f.live.buildUp}</Badge>
                            <span className="cell-sub muted small">{f.live.isFresh ? 'live' : `quote ${formatAge(f.live.asOfUtc)}`}</span>
                          </>
                        ) : (
                          <span className="muted small">{f.live ? 'no baseline close yet' : 'no live quote'}</span>
                        )}
                      </td>
                      <td className={`num mono ${signTone(f.live?.priceChangePercent)}`}>{movePercent(f.live?.priceChangePercent)}</td>
                      <td className={`num mono ${signTone(f.live?.openInterestChangePercent)}`}>{movePercent(f.live?.openInterestChangePercent)}</td>
                      <td>{f.latest ? <Badge tone={buildUpTone(f.latest.buildUp)}>{f.latest.buildUp}</Badge> : '—'}</td>
                      <td className={`num mono ${signTone(f.latest?.priceChangePercent)}`}>{movePercent(f.latest?.priceChangePercent)}</td>
                      <td className={`num mono ${signTone(f.latest?.openInterestChangePercent)}`}>{movePercent(f.latest?.openInterestChangePercent)}</td>
                      <td>
                        <div className="mf-strip">
                          {f.history.slice(0, 10).map((h) => (
                            <span
                              key={h.date}
                              className={`mf-strip__cell mf-tone--${buildUpTone(h.buildUp)}`}
                              title={`${formatDay(h.date)}: ${h.buildUp} · price ${movePercent(h.priceChangePercent)} · OI ${movePercent(h.openInterestChangePercent)}`}
                            />
                          ))}
                        </div>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </Panel>

          {/* A reading with no stocks is a line, not an empty card. */}
          <div className="mvp-stocks">
            {BUILD_UPS.filter((b) => (data.stocks[b] ?? []).length > 0).map((b) => (
              <Panel key={b} title={`Stock futures · ${b} · ${data.stockCounts[b] ?? 0}`} className="mv-card">
                <div className="mv-scroll">
                  <table className="table">
                    <thead>
                      <tr>
                        <th>Stock</th>
                        <th className="num">Price</th>
                        <th className="num">OI</th>
                      </tr>
                    </thead>
                    <tbody>
                      {data.stocks[b].map((s) => (
                        <tr key={s.underlying}>
                          <td>{s.underlying}</td>
                          <td className={`num mono ${signTone(s.priceChangePercent)}`}>{movePercent(s.priceChangePercent)}</td>
                          <td className={`num mono ${signTone(s.openInterestChangePercent)}`}>{movePercent(s.openInterestChangePercent)}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </Panel>
            ))}
          </div>
          {BUILD_UPS.some((b) => (data.stocks[b] ?? []).length === 0) && (
            <p className="muted small mvp-legend">
              No stock futures in {BUILD_UPS.filter((b) => (data.stocks[b] ?? []).length === 0).map((b) => b.toLowerCase()).join(' or ')} on{' '}
              {formatDay(data.latestDay)}.
            </p>
          )}
          <p className="muted small mvp-legend">
            Long build-up: price and OI both up (new buyers). Short build-up: price down, OI up (new sellers). Short covering:
            price up, OI down (sellers leaving). Long unwinding: both down (buyers leaving).
          </p>
        </>
      )}
      <ResearchNote id="futures" />
      <StatusLine status={data?.status} datasets={['futures-bhavcopy']} />
    </section>
  )
}

/* ----------------------------------------------------------------- page -- */

export function MoversPage() {
  return (
    <div className="page mvp">
      <AngelSection />
      <IndexSection />
      <FuturesSection />
    </div>
  )
}
