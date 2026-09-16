/**
 * Market movers: what the F&O market is doing right now, from Angel One.
 *
 * Three questions on one screen — which names are moving (price), where money
 * is entering or leaving (open interest, classified as a build-up), and how
 * put-heavy each underlying is (put-call ratio).
 *
 * The snapshot is built by the API from six SmartAPI calls and cached for 45
 * seconds, because the vendor refuses a burst; this page therefore polls at
 * that pace and shows when the picture was taken, so a stale number never
 * passes for a live one.
 */

import { useAngelMovers } from '../../lib/queries'
import { buildUpCounts, buildUpTone, formatOi, movePercent, pcrEnds } from '../../lib/movers'
import type { MoverRow } from '../../lib/movers'
import { formatAge } from '../../lib/format'
import { Badge, EmptyState, InlineError, Loading, Panel } from '../../components/ui'

function PriceTable({ rows, tone }: { rows: MoverRow[]; tone: 'pos' | 'neg' }) {
  if (rows.length === 0) return <EmptyState>Nothing came back for this list.</EmptyState>
  return (
    <div className="tablewrap">
      <table className="table">
        <thead>
          <tr>
            <th>Underlying</th>
            <th style={{ textAlign: 'right' }}>Last</th>
            <th style={{ textAlign: 'right' }}>Change</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => (
            <tr key={row.symbol}>
              <td>
                {row.underlying}
                <div className="muted small mono">{row.symbol}</div>
              </td>
              <td className="mono" style={{ textAlign: 'right' }}>
                {row.ltp.toLocaleString('en-IN')}
              </td>
              <td className="mono" style={{ textAlign: 'right' }}>
                <Badge tone={tone}>{movePercent(row.priceChangePercent)}</Badge>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

export default function MarketMoversPage() {
  const movers = useAngelMovers()
  const data = movers.data

  if (movers.isLoading && !data) return <Loading />
  if (movers.isError) return <InlineError error={movers.error} />

  if (data && data.configured === false) {
    return (
      <div className="page">
        <header className="page__head">
          <h1>Market movers</h1>
        </header>
        <Panel title="Angel One is not configured">
          <p>{data.message}</p>
          <p className="muted small">
            These screens come from Angel One's SmartAPI. Fill{' '}
            <span className="mono">{(data.missing ?? []).join(', ')}</span> in <code>.env</code> and restart
            the API.
          </p>
        </Panel>
      </div>
    )
  }

  const counts = buildUpCounts(data?.buildUp)
  const { high, low } = pcrEnds(data?.pcr)

  return (
    <div className="page">
      <header className="page__head">
        <div>
          <h1>Market movers</h1>
          <p className="muted small">
            Angel One · {data?.expiryType ?? 'NEAR'} expiry futures ·{' '}
            {data?.asOfUtc ? `as of ${formatAge(data.asOfUtc)}` : 'loading'} · refreshes every 45 s
            {movers.isFetching && ' · updating…'}
          </p>
        </div>
        <button className="btn" onClick={() => movers.refetch()} disabled={movers.isFetching}>
          {movers.isFetching ? 'Refreshing…' : 'Refresh'}
        </button>
      </header>

      {(data?.warnings ?? []).length > 0 && (
        <Panel title="Parts of this snapshot are missing">
          <ul>
            {data!.warnings!.map((w) => (
              <li key={w} className="small">
                {w}
              </li>
            ))}
          </ul>
        </Panel>
      )}

      <div className="grid grid--2">
        <Panel title="Top gainers">
          <PriceTable rows={data?.priceGainers ?? []} tone="pos" />
        </Panel>
        <Panel title="Top losers">
          <PriceTable rows={data?.priceLosers ?? []} tone="neg" />
        </Panel>
      </div>

      <Panel title="Open interest build-up">
        <p className="muted small">
          Open interest and price read together: money entering on the long side, fresh shorts, shorts
          closing, or longs leaving. {counts.map((c) => `${c.label} ${c.count}`).join(' · ')}
        </p>
        {(data?.buildUp ?? []).length === 0 ? (
          <EmptyState>No open-interest movers came back.</EmptyState>
        ) : (
          <div className="tablewrap">
            <table className="table">
              <thead>
                <tr>
                  <th>Underlying</th>
                  <th style={{ textAlign: 'right' }}>Last</th>
                  <th style={{ textAlign: 'right' }}>Price</th>
                  <th style={{ textAlign: 'right' }}>OI</th>
                  <th style={{ textAlign: 'right' }}>OI change</th>
                  <th>Reading</th>
                </tr>
              </thead>
              <tbody>
                {data!.buildUp!.map((row) => (
                  <tr key={row.symbol}>
                    <td>
                      {row.underlying}
                      <div className="muted small mono">{row.symbol}</div>
                    </td>
                    <td className="mono" style={{ textAlign: 'right' }}>
                      {row.ltp ? row.ltp.toLocaleString('en-IN') : '—'}
                    </td>
                    <td className="mono" style={{ textAlign: 'right' }}>
                      {movePercent(row.priceChangePercent)}
                    </td>
                    <td className="mono" style={{ textAlign: 'right' }}>
                      {formatOi(row.openInterest)}
                    </td>
                    <td className="mono" style={{ textAlign: 'right' }}>
                      {movePercent(row.oiChangePercent)}
                    </td>
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

      <div className="grid grid--2">
        <Panel title="Most put-heavy (highest PCR)">
          <PcrTable rows={high} />
        </Panel>
        <Panel title="Most call-heavy (lowest PCR)">
          <PcrTable rows={low} />
        </Panel>
      </div>
    </div>
  )
}

function PcrTable({ rows }: { rows: Array<{ symbol: string; underlying: string; pcr: number }> }) {
  if (rows.length === 0) return <EmptyState>No put-call ratios came back.</EmptyState>
  return (
    <div className="tablewrap">
      <table className="table">
        <thead>
          <tr>
            <th>Underlying</th>
            <th style={{ textAlign: 'right' }}>PCR</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => (
            <tr key={row.symbol}>
              <td>{row.underlying}</td>
              <td className="mono" style={{ textAlign: 'right' }}>
                {row.pcr.toFixed(2)}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}
