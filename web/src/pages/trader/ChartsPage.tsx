/**
 * Charts — built around one principle: SHOW WHAT EXISTS FIRST.
 *
 * The "Available data" inventory lists every (symbol, resolution) this
 * installation actually has candles for, with the exact from→to range and bar
 * count. Click a row and that exact data opens in the chart, with the date
 * range pre-set to the full span and adjustable from there. Nobody has to
 * guess which symbol or which dates have data.
 */

import { useEffect, useMemo, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import {
  useDataCoverage,
  useLiveBars,
  useStoredCandles,
  type CoverageRow,
} from '../../lib/queries'
import { useAuth } from '../../lib/auth'
import { formatDateTime, formatNumber, formatPrice, pnlClass } from '../../lib/format'
import { Badge, InlineError, Loading, Panel, StatTile } from '../../components/ui'
import { PriceChart, type PriceCandle } from '../../components/charts'

/** yyyy-MM-dd in UTC, for <input type="date"> bounds and API params. */
function toDateInput(iso: string): string {
  return iso.slice(0, 10)
}

export function ChartsPage() {
  const { isAdmin } = useAuth()
  const coverage = useDataCoverage()
  const [params, setParams] = useSearchParams()

  const [selected, setSelected] = useState<CoverageRow | null>(null)
  const [fromDate, setFromDate] = useState('')
  const [toDate, setToDate] = useState('')

  // Deep link (?symbol=...) and default selection: first row once loaded.
  const rows = coverage.data
  useEffect(() => {
    if (!rows?.length || selected) return
    const wanted = params.get('symbol')
    const row = (wanted && rows.find((r) => r.symbol === wanted)) || rows[0]
    selectRow(row)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [rows])

  function selectRow(row: CoverageRow) {
    setSelected(row)
    setFromDate(toDateInput(row.fromUtc))
    setToDate(toDateInput(row.toUtc))
    setParams({ symbol: row.symbol }, { replace: true })
  }

  // ---- data for the selected row ----
  const isLive = selected?.source === 'live'
  const liveBars = useLiveBars(isLive ? selected!.symbol : null, 10_000)
  const stored = useStoredCandles(
    !isLive && selected ? selected.symbol : null,
    selected?.resolution ?? 'D',
    fromDate || undefined,
    toDate || undefined,
  )

  const candles: PriceCandle[] = useMemo(() => {
    let mapped: PriceCandle[]
    if (isLive) {
      mapped = (liveBars.data ?? []).map((b) => ({
        timeUtc: b.barStartUtc,
        open: b.open,
        high: b.high,
        low: b.low,
        close: b.close,
        volume: b.volumeDelta,
      }))
    } else {
      mapped = (stored.data ?? []).map((c) => ({
        timeUtc: c.timestampUtc,
        open: c.open,
        high: c.high,
        low: c.low,
        close: c.close,
        volume: c.volume,
      }))
    }
    // The date range applies to both sources; live bars are filtered here
    // because the live-bars endpoint has no range parameters yet.
    const from = fromDate ? `${fromDate}T00:00:00Z` : ''
    const to = toDate ? `${toDate}T23:59:59Z` : ''
    return mapped.filter(
      (c) => (!from || c.timeUtc >= from) && (!to || c.timeUtc <= to),
    )
  }, [isLive, liveBars.data, stored.data, fromDate, toDate])

  const activeQuery = isLive ? liveBars : stored

  const summary = useMemo(() => {
    if (!candles.length) return null
    const sorted = [...candles].sort((a, b) => a.timeUtc.localeCompare(b.timeUtc))
    const first = sorted[0]
    const last = sorted[sorted.length - 1]
    const change = last.close - first.open
    return {
      last,
      first,
      high: Math.max(...sorted.map((c) => c.high)),
      low: Math.min(...sorted.map((c) => c.low)),
      change,
      changePct: first.open ? (change / first.open) * 100 : 0,
      count: sorted.length,
    }
  }, [candles])

  return (
    <div className="page">
      <header className="page__header">
        <h1 className="page__title">Charts</h1>
        <p className="page__subtitle">
          Everything chartable on this installation, with its exact date range — click a
          row to open it.
        </p>
      </header>

      <Panel title={`Available data ${rows ? `(${rows.length})` : ''}`}>
        {coverage.isPending && <Loading />}
        {coverage.isError && <InlineError error={coverage.error} />}
        {rows && rows.length === 0 && (
          <div>
            <p className="empty">
              No candles stored yet — nothing has been recorded or backfilled.
            </p>
            <div className="chip-row">
              {isAdmin && (
                <>
                  <Link className="btn btn--sm" to="/admin/ingestion">
                    Backfill daily history →
                  </Link>
                  <Link className="btn btn--sm" to="/admin/broker">
                    Connect FYERS &amp; start the ingestor →
                  </Link>
                </>
              )}
            </div>
          </div>
        )}
        {rows && rows.length > 0 && (
          <div className="tablewrap tablewrap--tall">
            <table className="table table--hover">
              <thead>
                <tr>
                  <th>Symbol</th>
                  <th>Resolution</th>
                  <th>Source</th>
                  <th className="r">From</th>
                  <th className="r">To</th>
                  <th className="r">Bars</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((row) => {
                  const isSel =
                    selected?.symbol === row.symbol &&
                    selected?.resolution === row.resolution &&
                    selected?.source === row.source
                  return (
                    <tr
                      key={`${row.symbol}|${row.resolution}|${row.source}`}
                      className={isSel ? 'row--selected' : ''}
                      onClick={() => selectRow(row)}
                    >
                      <td className="mono">{row.symbol.replace('NSE:', '')}</td>
                      <td className="mono">{row.resolution}</td>
                      <td>
                        <Badge tone={row.source === 'live' ? 'accent' : 'neutral'}>
                          {row.source === 'live' ? 'live session' : 'backfill'}
                        </Badge>
                      </td>
                      <td className="r muted">{formatDateTime(row.fromUtc)}</td>
                      <td className="r muted">{formatDateTime(row.toUtc)}</td>
                      <td className="r mono">{formatNumber(row.barCount)}</td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
        )}
        <p className="muted small-note">
          "Live session" = 1-minute bars saved while the ingestor ran. "Backfill" = daily
          candles fetched from FYERS history{isAdmin ? ' (Admin → Data ingestion)' : ''}.
        </p>
      </Panel>

      {selected && (
        <Panel
          title={`${selected.symbol.replace('NSE:', '')} · ${selected.resolution}`}
          actions={
            <div className="inline-form">
              <label className="muted small-note" style={{ marginTop: 0 }} htmlFor="ch-from">
                From
              </label>
              <input
                id="ch-from"
                type="date"
                className="field__input field__input--sm"
                value={fromDate}
                min={toDateInput(selected.fromUtc)}
                max={toDateInput(selected.toUtc)}
                onChange={(e) => setFromDate(e.target.value)}
              />
              <label className="muted small-note" style={{ marginTop: 0 }} htmlFor="ch-to">
                To
              </label>
              <input
                id="ch-to"
                type="date"
                className="field__input field__input--sm"
                value={toDate}
                min={toDateInput(selected.fromUtc)}
                max={toDateInput(selected.toUtc)}
                onChange={(e) => setToDate(e.target.value)}
              />
              <button
                type="button"
                className="btn btn--ghost btn--sm"
                onClick={() => {
                  setFromDate(toDateInput(selected.fromUtc))
                  setToDate(toDateInput(selected.toUtc))
                }}
              >
                Full range
              </button>
            </div>
          }
        >
          {summary && (
            <div className="stat-grid" style={{ marginBottom: 14 }}>
              <StatTile label="Last close" value={formatPrice(summary.last.close)} />
              <StatTile
                label="Change (range)"
                value={`${summary.change >= 0 ? '+' : ''}${summary.change.toFixed(2)}`}
                tone={summary.change >= 0 ? 'pos' : 'neg'}
                sub={
                  <span className={pnlClass(summary.change)}>
                    {summary.changePct >= 0 ? '+' : ''}
                    {summary.changePct.toFixed(2)}%
                  </span>
                }
              />
              <StatTile label="High" value={formatPrice(summary.high)} />
              <StatTile label="Low" value={formatPrice(summary.low)} />
              <StatTile label="Bars shown" value={formatNumber(summary.count)} />
            </div>
          )}

          {activeQuery.isPending && <Loading label="Loading candles…" />}
          {activeQuery.isError && <InlineError error={activeQuery.error} />}
          {!activeQuery.isPending && !activeQuery.isError && candles.length === 0 && (
            <p className="empty">
              No candles in this date range — widen it or press "Full range".
            </p>
          )}
          {candles.length > 0 && <PriceChart candles={candles} />}
        </Panel>
      )}
    </div>
  )
}
