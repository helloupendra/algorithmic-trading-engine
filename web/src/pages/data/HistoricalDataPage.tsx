/**
 * Data module — Historical. Coverage first: the left rail lists every
 * (symbol, resolution) range that actually exists locally — with its span and
 * bar count — before any picker asks for input. Selecting a row charts it;
 * the backfill panel extends it (or pulls a brand-new symbol) from FYERS.
 */

import { useMemo, useState } from 'react'
import {
  useBackfillHistory,
  useDataCoverage,
  useLiveBars,
  useOptionsBackfill,
  useStoredCandles,
  type CoverageRow,
} from '../../lib/queries'
import { formatDateTime, formatNumber, formatPrice, shortSymbol } from '../../lib/format'
import { Badge, InlineError, Panel, QueryBoundary } from '../../components/ui'
import { CandleChart } from '../../components/CandleChart'
import { SymbolCombobox } from '../../components/SymbolCombobox'
import {
  EARLIEST_HISTORY,
  RESOLUTIONS,
  earliestFor,
  fallbackResolution,
  limitHint,
  maxDaysFor,
  rejectionFor,
} from '../../lib/fyersLimits'
import { IconCandles, IconDownload, IconLayers } from '../../components/icons'
import {
  CATEGORY_ORDER,
  classifySymbol,
  formatResolution,
  resolutionRank,
  type SymbolCategory,
} from '../../lib/symbols'
import type { CandleDto } from '../../lib/types'

type SourceFilter = 'all' | 'backfill' | 'live'

function isoDate(d: Date): string {
  return d.toISOString().slice(0, 10)
}

function CoverageBrowser({
  rows,
  selected,
  onSelect,
}: {
  rows: CoverageRow[]
  selected: CoverageRow | null
  onSelect: (row: CoverageRow) => void
}) {
  const [search, setSearch] = useState('')
  const [source, setSource] = useState<SourceFilter>('all')
  const [category, setCategory] = useState<SymbolCategory | 'all'>('all')

  const categories = useMemo(
    () => CATEGORY_ORDER.filter((c) => rows.some((r) => classifySymbol(r.symbol) === c)),
    [rows],
  )

  const filtered = useMemo(() => {
    const needle = search.trim().toUpperCase()
    return rows
      .filter((r) => (source === 'all' ? true : r.source === source))
      .filter((r) => (category === 'all' ? true : classifySymbol(r.symbol) === category))
      .filter((r) => (needle ? r.symbol.toUpperCase().includes(needle) : true))
      .sort(
        (a, b) =>
          a.symbol.localeCompare(b.symbol) || resolutionRank(a.resolution) - resolutionRank(b.resolution),
      )
  }, [rows, search, source, category])

  const isSame = (a: CoverageRow, b: CoverageRow | null) =>
    !!b && a.symbol === b.symbol && a.resolution === b.resolution && a.source === b.source

  return (
    <div>
      <div className="toolbar" style={{ marginBottom: 10 }}>
        <input
          className="field__input field__input--sm"
          style={{ flex: 1, minWidth: 160 }}
          placeholder="Filter symbols…"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
        />
        <div className="seg" role="group" aria-label="Source">
          {(['all', 'backfill', 'live'] as const).map((s) => (
            <button
              key={s}
              type="button"
              className={`seg__btn ${source === s ? 'is-active' : ''}`}
              onClick={() => setSource(s)}
            >
              {s === 'all' ? 'All' : s === 'backfill' ? 'Backfill' : 'Live 1m'}
            </button>
          ))}
        </div>
        <select
          className="field__input field__input--sm"
          value={category}
          onChange={(e) => setCategory(e.target.value as SymbolCategory | 'all')}
        >
          <option value="all">All categories</option>
          {categories.map((c) => (
            <option key={c} value={c}>
              {c}
            </option>
          ))}
        </select>
      </div>

      <p className="small-note" style={{ margin: '0 0 8px' }}>
        {formatNumber(filtered.length)} of {formatNumber(rows.length)} ranges ·{' '}
        {formatNumber(filtered.reduce((s, r) => s + r.barCount, 0))} bars
      </p>

      <div className="cov-list">
        {filtered.map((row) => (
          <button
            key={`${row.source}|${row.resolution}|${row.symbol}`}
            type="button"
            className={`cov-row ${isSame(row, selected) ? 'is-active' : ''}`}
            onClick={() => onSelect(row)}
          >
            <span className="cov-row__top">
              <span className="cov-row__symbol">{shortSymbol(row.symbol)}</span>
              <Badge tone={row.source === 'live' ? 'live' : 'accent'}>
                {row.source === 'live' ? 'live' : 'backfill'}
              </Badge>
              <Badge tone="neutral">{formatResolution(row.resolution)}</Badge>
            </span>
            <span className="cov-row__meta">
              <span>
                {formatDateTime(row.fromUtc)} → {formatDateTime(row.toUtc)}
              </span>
              <span>{formatNumber(row.barCount)} bars</span>
            </span>
          </button>
        ))}
        {filtered.length === 0 && <p className="empty">Nothing matches these filters.</p>}
      </div>
    </div>
  )
}

/** Chart + table for the selected coverage row. */
function SelectionDetail({ row }: { row: CoverageRow }) {
  const isLive = row.source === 'live'
  const fromDate = isoDate(new Date(row.fromUtc))
  const toDate = isoDate(new Date(new Date(row.toUtc).getTime() + 24 * 3600 * 1000))

  const stored = useStoredCandles(isLive ? null : row.symbol, row.resolution, fromDate, toDate)
  const live = useLiveBars(isLive ? row.symbol : null, 2000)

  const candles: CandleDto[] = useMemo(() => {
    if (!isLive) return stored.data ?? []
    return (live.data ?? []).map((b) => ({
      symbol: b.symbol,
      resolution: b.resolution,
      timestampUtc: b.barStartUtc,
      open: b.open,
      high: b.high,
      low: b.low,
      close: b.close,
      volume: b.volumeDelta,
    }))
  }, [isLive, stored.data, live.data])

  const query = isLive ? live : stored
  const recent = useMemo(
    () =>
      [...candles]
        .sort((a, b) => new Date(b.timestampUtc).getTime() - new Date(a.timestampUtc).getTime())
        .slice(0, 60),
    [candles],
  )

  return (
    <div className="stack-list">
      <Panel
        title={
          <>
            <IconCandles /> {shortSymbol(row.symbol)}
            <Badge tone={isLive ? 'live' : 'accent'}>{isLive ? 'live 1m' : 'backfill'}</Badge>
            <Badge tone="neutral">{formatResolution(row.resolution)}</Badge>
          </>
        }
        actions={
          <span className="muted" style={{ fontSize: 12 }}>
            {formatNumber(row.barCount)} bars · {formatDateTime(row.fromUtc)} →{' '}
            {formatDateTime(row.toUtc)}
          </span>
        }
      >
        {query.isPending ? (
          <p className="empty">Loading candles…</p>
        ) : query.isError ? (
          <InlineError error={query.error} />
        ) : candles.length === 0 ? (
          <p className="empty">The store returned no candles for this range.</p>
        ) : (
          <CandleChart candles={candles} fitKey={`${row.symbol}|${row.resolution}|${row.source}`} tall />
        )}
      </Panel>

      <Panel title="Latest candles">
        <div className="tablewrap" style={{ maxHeight: 320, overflowY: 'auto' }}>
          <table className="table">
            <thead>
              <tr>
                <th>Timestamp</th>
                <th className="r">Open</th>
                <th className="r">High</th>
                <th className="r">Low</th>
                <th className="r">Close</th>
                <th className="r">Volume</th>
              </tr>
            </thead>
            <tbody>
              {recent.map((c) => (
                <tr key={c.timestampUtc}>
                  <td className="muted">{formatDateTime(c.timestampUtc)}</td>
                  <td className="r mono">{formatPrice(c.open)}</td>
                  <td className="r mono">{formatPrice(c.high)}</td>
                  <td className="r mono">{formatPrice(c.low)}</td>
                  <td className={`r mono ${c.close >= c.open ? 'pos' : 'neg'}`}>
                    {formatPrice(c.close)}
                  </td>
                  <td className="r muted">{formatNumber(c.volume)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Panel>
    </div>
  )
}

/** Pull candles from FYERS — extend the selection or fetch a new symbol. */
function BackfillPanel({ selected }: { selected: CoverageRow | null }) {
  const backfill = useBackfillHistory()

  const [symbol, setSymbol] = useState('')
  const [resolution, setResolution] = useState('D')
  const [fromDate, setFromDate] = useState(isoDate(new Date(Date.now() - 30 * 24 * 3600 * 1000)))
  const [toDate, setToDate] = useState(isoDate(new Date()))
  const [seeded, setSeeded] = useState<string | null>(null)
  /** The resolution a range change moved away from, so the form can say so. */
  const [autoSwitchedFrom, setAutoSwitchedFrom] = useState<string | null>(null)

  // Prefill from the selected coverage row: continue where its data ends.
  // Coverage resolutions come in several spellings ('1m' from live bars,
  // '1'/'D' from backfill) — map into the select's canonical vocabulary.
  const canonicalResolution = (res: string): string => {
    const r = res.trim().toLowerCase()
    if (r === 'd' || r === '1d') return 'D'
    const m = /^(\d+)m?$/.exec(r)
    if (m && ['1', '5', '15', '60'].includes(m[1])) return m[1]
    return 'D'
  }
  const seedKey = selected ? `${selected.symbol}|${selected.resolution}` : null
  if (seedKey && seedKey !== seeded) {
    setSeeded(seedKey)
    setSymbol(selected!.symbol)
    setResolution(canonicalResolution(selected!.resolution))
    setFromDate(isoDate(new Date(selected!.toUtc)))
    setToDate(isoDate(new Date()))
  }

  // What the broker will refuse, worked out before the request rather than
  // after: FYERS rejects an over-long window outright, so an unchecked form
  // produces a failed backfill and nothing on screen to explain it.
  let rejection = rejectionFor(resolution, fromDate, toDate)
  const suggestion = rejection ? fallbackResolution(fromDate, toDate) : null

  // Widening the range past what minute candles can serve moves the resolution
  // rather than leaving a dead button: the dates are the deliberate choice, and
  // the resolution is what has to give. Said out loud, because a control that
  // changes itself in silence is worse than one that refuses.
  //
  // Guarded like the seeding above (this file's own idiom): after the switch
  // the new resolution is valid, so `rejection` is null and this cannot loop.
  if (rejection && suggestion && suggestion !== resolution) {
    setResolution(suggestion)
    setAutoSwitchedFrom(resolution)
    rejection = null
  } else if (autoSwitchedFrom && !rejection && resolution !== suggestion && autoSwitchedFrom === resolution) {
    setAutoSwitchedFrom(null)
  }

  const result = backfill.data

  return (
    <Panel
      title={
        <>
          <IconDownload /> Backfill from FYERS
        </>
      }
    >
      <div className="form-row">
        <div className="field">
          <label className="field__label" htmlFor="backfill-symbol">Symbol</label>
          <SymbolCombobox id="backfill-symbol" value={symbol} onChange={setSymbol} />
        </div>
        <label className="field">
          <span className="field__label">Resolution</span>
          <select
            className="field__input"
            value={resolution}
            onChange={(e) => {
              setResolution(e.target.value)
              setAutoSwitchedFrom(null)
            }}
          >
            {RESOLUTIONS.map((r) => {
              const why = rejectionFor(r.value, fromDate, toDate)
              return (
                <option key={r.value} value={r.value} disabled={why !== null}>
                  {r.label}
                  {why ? ` — ${r.maxDays} days max` : ''}
                </option>
              )
            })}
          </select>
        </label>
        <label className="field">
          <span className="field__label">From</span>
          <input
            className="field__input"
            type="date"
            min={earliestFor(resolution, toDate)}
            max={toDate || undefined}
            value={fromDate}
            onChange={(e) => setFromDate(e.target.value)}
            title={`Earliest FYERS holds is ${EARLIEST_HISTORY}; this resolution reaches back to ${earliestFor(resolution, toDate)}`}
          />
        </label>
        <label className="field">
          <span className="field__label">To</span>
          <input
            className="field__input"
            type="date"
            min={fromDate || EARLIEST_HISTORY}
            value={toDate}
            onChange={(e) => setToDate(e.target.value)}
          />
        </label>
        <button
          className="btn btn--primary"
          disabled={!symbol.trim() || backfill.isPending || rejection !== null}
          title={rejection ?? undefined}
          onClick={() =>
            backfill.mutate({ symbol: symbol.trim(), resolution, fromDate, toDate })
          }
        >
          {backfill.isPending ? 'Fetching…' : 'Backfill'}
        </button>
      </div>

      {backfill.isError && (
        <div style={{ marginTop: 10 }}>
          <InlineError error={backfill.error} />
        </div>
      )}

      {result && (
        <div className={`alert ${result.fullCoverageAfterBackfill ? 'alert--success' : 'alert--error'}`} style={{ marginTop: 10 }}>
          <span>
            {result.message} — fetched {formatNumber(result.candlesFetched)} candles,{' '}
            {formatNumber(result.localCandlesAvailable)} now stored locally
            {result.missingSlicesFetched.length > 0 &&
              ` (${result.missingSlicesFetched.length} missing slice${result.missingSlicesFetched.length > 1 ? 's' : ''} filled)`}
            .
          </span>
        </div>
      )}

      {autoSwitchedFrom && !rejection ? (
        <p className="small-note small-note--warn">
          {RESOLUTIONS.find((r) => r.value === autoSwitchedFrom)?.label} only reaches back{' '}
          {maxDaysFor(autoSwitchedFrom)} days, so this range switched to{' '}
          {RESOLUTIONS.find((r) => r.value === resolution)?.label}. Shorten the range to go finer.
        </p>
      ) : rejection ? (
        <p className="small-note small-note--warn">
          {RESOLUTIONS.find((r) => r.value === resolution)?.label}: {rejection}.{' '}
          {suggestion
            ? `Use ${RESOLUTIONS.find((r) => r.value === suggestion)?.label}, or shorten the range.`
            : `No resolution covers this range in one request — shorten it.`}
        </p>
      ) : (
        <p className="small-note">
          {limitHint(resolution, toDate)} · backfill is gap-aware: it only fetches slices the local
          store is missing. Requires a linked FYERS session.
        </p>
      )}
    </Panel>
  )
}

/** ATM±N option-chain backfill around an index underlying. */
function OptionsBackfillPanel() {
  const mutate = useOptionsBackfill()
  const [underlying, setUnderlying] = useState('BANKNIFTY')
  const [exchange, setExchange] = useState('NSE')
  const [strikes, setStrikes] = useState(2)
  const [step, setStep] = useState(100)
  // Canonical resolution strings ('1'/'5'/'15'/'D') — the store and the
  // read-back path (/history/local) match on these; '1m' would be written
  // verbatim and then never found by the browser again.
  const [resolution, setResolution] = useState('1')
  const [fromDate, setFromDate] = useState(isoDate(new Date(Date.now() - 7 * 24 * 3600 * 1000)))
  const [toDate, setToDate] = useState(isoDate(new Date()))

  // The chain fetch is one history request per contract, so the same
  // per-request window applies here as on the single-symbol panel.
  const chainRejection = rejectionFor(resolution, fromDate, toDate)

  return (
    <Panel
      title={
        <>
          <IconLayers /> Option-chain backfill (ATM ± N)
        </>
      }
    >
      <div className="form-row">
        <label className="field">
          <span className="field__label">Exchange</span>
          <select className="field__input" value={exchange} onChange={(e) => setExchange(e.target.value)}>
            <option value="NSE">NSE</option>
            <option value="BSE">BSE</option>
          </select>
        </label>
        <label className="field">
          <span className="field__label">Underlying</span>
          <input
            className="field__input"
            value={underlying}
            onChange={(e) => setUnderlying(e.target.value.toUpperCase())}
          />
        </label>
        <label className="field">
          <span className="field__label">Strikes each side</span>
          <input
            className="field__input"
            type="number"
            min={0}
            max={10}
            value={strikes}
            onChange={(e) => setStrikes(Number(e.target.value))}
          />
        </label>
        <label className="field">
          <span className="field__label">Strike step</span>
          <input
            className="field__input"
            type="number"
            min={1}
            value={step}
            onChange={(e) => setStep(Number(e.target.value))}
          />
        </label>
        <label className="field">
          <span className="field__label">Resolution</span>
          <select
            className="field__input"
            value={resolution}
            onChange={(e) => setResolution(e.target.value)}
          >
            {RESOLUTIONS.map((r) => {
              const why = rejectionFor(r.value, fromDate, toDate)
              return (
                <option key={r.value} value={r.value} disabled={why !== null}>
                  {r.label}
                  {why ? ` — ${r.maxDays} days max` : ''}
                </option>
              )
            })}
          </select>
        </label>
        <label className="field">
          <span className="field__label">From</span>
          <input
            className="field__input"
            type="date"
            min={earliestFor(resolution, toDate)}
            max={toDate || undefined}
            value={fromDate}
            onChange={(e) => setFromDate(e.target.value)}
            title={`Earliest FYERS holds is ${EARLIEST_HISTORY}; this resolution reaches back to ${earliestFor(resolution, toDate)}`}
          />
        </label>
        <label className="field">
          <span className="field__label">To</span>
          <input
            className="field__input"
            type="date"
            min={fromDate || EARLIEST_HISTORY}
            value={toDate}
            onChange={(e) => setToDate(e.target.value)}
          />
        </label>
        <button
          className="btn"
          disabled={!underlying.trim() || mutate.isPending || chainRejection !== null}
          title={chainRejection ?? undefined}
          onClick={() =>
            mutate.mutate({
              exchange,
              underlying: underlying.trim(),
              strikeCountEachSide: strikes,
              strikeStep: step,
              resolution,
              fromUtc: `${fromDate}T00:00:00Z`,
              toUtc: `${toDate}T23:59:59Z`,
              includeCalls: true,
              includePuts: true,
            })
          }
        >
          {mutate.isPending ? 'Fetching…' : 'Backfill chain'}
        </button>
      </div>

      {mutate.isError && (
        <div style={{ marginTop: 10 }}>
          <InlineError error={mutate.error} />
        </div>
      )}
      {mutate.isSuccess && (
        <div className="alert alert--success" style={{ marginTop: 10 }}>
          <span>Chain backfill finished — check the coverage list for the new option symbols.</span>
        </div>
      )}

      {chainRejection && (
        <p className="small-note small-note--warn">
          {RESOLUTIONS.find((r) => r.value === resolution)?.label}: {chainRejection}.
        </p>
      )}

      <p className="small-note">
        Resolves the expiry and ATM strike automatically (uses the live quote when available), then
        fetches CE/PE candles per contract into the same store.
      </p>
    </Panel>
  )
}

export function HistoricalDataPage() {
  const coverage = useDataCoverage()
  // Only the identity of the selection lives in state; the row itself is
  // re-resolved from the latest coverage data so a backfill that extends the
  // range also extends the chart window and the header counts.
  const [selectedKey, setSelectedKey] = useState<{
    symbol: string
    resolution: string
    source: string
  } | null>(null)

  const rows = coverage.data ?? []
  const selected = selectedKey
    ? (rows.find(
        (r) =>
          r.symbol === selectedKey.symbol &&
          r.resolution === selectedKey.resolution &&
          r.source === selectedKey.source,
      ) ?? null)
    : null

  const setSelected = (row: CoverageRow) =>
    setSelectedKey({ symbol: row.symbol, resolution: row.resolution, source: row.source })

  return (
    <div className="page">
      <header className="page__header">
        <div>
          <h1 className="page__title">Historical data</h1>
          <p className="page__subtitle">
            Every stored range, up front — pick from what exists, then extend it from the broker.
          </p>
        </div>
      </header>

      <QueryBoundary
        query={coverage}
        empty="No historical data stored yet. Use the backfill form below to pull your first candles from FYERS."
      >
        {(rows) => (
          <div className="cov-layout">
            <Panel title="Available ranges" className="panel--well">
              <CoverageBrowser rows={rows} selected={selected} onSelect={setSelected} />
            </Panel>
            {selected ? (
              <SelectionDetail row={selected} />
            ) : (
              <div className="card card--dashed page--centered" style={{ minHeight: 320 }}>
                <IconCandles style={{ width: 28, height: 28, color: 'var(--text-3)' }} />
                <p className="card__muted">Select a range on the left to chart it.</p>
              </div>
            )}
          </div>
        )}
      </QueryBoundary>

      <BackfillPanel selected={selected} />
      <OptionsBackfillPanel />
    </div>
  )
}
