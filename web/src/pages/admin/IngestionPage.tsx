/**
 * Data-ingestion health: the Python ingestor's heartbeat, what it is
 * subscribed to, which quotes have gone stale, and a manual candle backfill.
 */

import { useState } from 'react'
import {
  useBackfillHistory,
  useIngestorStatuses,
  useLatestQuotes,
  useStaleQuotes,
} from '../../lib/queries'
import { formatAge, formatDateTime, formatPrice, shortSymbol } from '../../lib/format'
import { Badge, InlineError, Panel, QueryBoundary } from '../../components/ui'
import { LiveQuotesMonitor } from '../../components/LiveQuotesMonitor'

export function IngestionPage() {
  const statuses = useIngestorStatuses()
  const stale = useStaleQuotes(120)
  const backfill = useBackfillHistory()

  const [form, setForm] = useState({
    symbol: 'NSE:SBIN-EQ',
    resolution: 'D',
    fromDate: '2026-08-01',
    toDate: '2026-08-28',
  })

  const [showMonitor, setShowMonitor] = useState(false)

  return (
    <div className="page">
      <header className="page__header">
        <h1 className="page__title">Data ingestion</h1>
        <p className="page__subtitle">
          Ingestor heartbeat, subscriptions, stale symbols, and historical backfill.
        </p>
      </header>

      <Panel
        title="Ingestors"
        actions={
          <button
            className="btn btn--secondary"
            onClick={() => setShowMonitor(!showMonitor)}
          >
            {showMonitor ? 'Close Live Prices Monitor' : 'Open Live Prices Monitor'}
          </button>
        }
      >
        <QueryBoundary query={statuses} empty="No ingestor has ever reported a heartbeat.">
          {(data) => (
            <div className="stack-list">
              {data.map((s) => (
                <div key={s.sourceName} className="ingestor">
                  <div className="ingestor__head">
                    <b className="mono">{s.sourceName}</b>
                    <Badge tone={s.isHealthy ? 'pos' : 'warn'}>
                      {s.isHealthy ? s.status : `${s.status} · stale`}
                    </Badge>
                    <span className="muted">heartbeat {formatAge(s.lastHeartbeatUtc)}</span>
                  </div>
                  {s.lastError && <p className="neg">{s.lastError}</p>}
                  <div className="chip-row">
                    {s.currentSubscribedSymbols.map((sym) => (
                      <span key={sym} className="badge badge--neutral mono">
                        {shortSymbol(sym)}
                      </span>
                    ))}
                  </div>
                </div>
              ))}
            </div>
          )}
        </QueryBoundary>
      </Panel>

      {showMonitor && <LiveQuotesMonitor />}

      <Panel title="Stale quotes (older than 2 minutes)">
        <QueryBoundary query={stale} empty="Nothing is stale — or nothing has data.">
          {(data) => (
            <div className="tablewrap">
              <table className="table">
                <thead>
                  <tr>
                    <th>Symbol</th>
                    <th className="r">LTP</th>
                    <th className="r">Updated</th>
                    <th className="r">Age</th>
                  </tr>
                </thead>
                <tbody>
                  {data.map((q) => (
                    <tr key={q.symbol}>
                      <td className="mono">{shortSymbol(q.symbol)}</td>
                      <td className="r mono">{formatPrice(q.lastTradedPrice)}</td>
                      <td className="r muted">{formatDateTime(q.updatedUtc)}</td>
                      <td className="r mono">{formatAge(q.updatedUtc)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </QueryBoundary>
      </Panel>

      <Panel title="Backfill daily candles from FYERS">
        {backfill.isError && <InlineError error={backfill.error} />}
        {backfill.data && (
          <div className="alert">
            {backfill.data.message} — fetched {backfill.data.candelsFetchedFromFyers}, local total{' '}
            {backfill.data.localCandlesAvailable}.
          </div>
        )}
        <form
          className="form-row"
          onSubmit={(e) => {
            e.preventDefault()
            backfill.mutate(form)
          }}
        >
          <div className="field">
            <label className="field__label" htmlFor="bf-symbol">Symbol</label>
            <input
              id="bf-symbol"
              className="field__input"
              value={form.symbol}
              onChange={(e) => setForm({ ...form, symbol: e.target.value })}
            />
          </div>
          <div className="field">
            <label className="field__label" htmlFor="bf-from">From</label>
            <input
              id="bf-from"
              type="date"
              className="field__input"
              value={form.fromDate}
              onChange={(e) => setForm({ ...form, fromDate: e.target.value })}
            />
          </div>
          <div className="field">
            <label className="field__label" htmlFor="bf-to">To</label>
            <input
              id="bf-to"
              type="date"
              className="field__input"
              value={form.toDate}
              onChange={(e) => setForm({ ...form, toDate: e.target.value })}
            />
          </div>
          <button className="btn btn--primary" disabled={backfill.isPending}>
            {backfill.isPending ? 'Backfilling…' : 'Backfill'}
          </button>
        </form>
        <p className="muted small-note">
          Needs an authenticated FYERS session (Admin → broker login). Candles land in the{' '}
          <code>candles</code> table and become visible under stored history.
        </p>
      </Panel>
    </div>
  )
}
