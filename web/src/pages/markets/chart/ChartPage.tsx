/**
 * Markets → Chart: the chart first, everything else in service of it.
 *
 * A symbol, a resolution, a range and a big candlestick, with market
 * structure as a layer over it. The resolution and range only offer what
 * this installation has for the symbol (the coverage endpoint says), so a
 * reader never lands on an empty chart without knowing why. Intraday
 * resolutions are stitched from the stored candles (the nightly archive and
 * backfills) and today's live 1-minute bars, rolled up in the browser with
 * the server's bucket rule; daily comes from the stored candles alone.
 *
 * Between polls of the bars the forming candle moves with the symbol's pushed
 * price (lib/live.ts): its close, and its high and low when the price passes
 * them, so the last candle is as live as the price over the chart.
 *
 * The structure layer (Smart Money Concepts) was a page of its own with its
 * own symbol, timeframe and range pickers; it reads the same chart now. The
 * symbol and the layer live in the URL, so a chart can be linked to as it
 * was seen. The historical backfill stays under Data → Historical.
 */

import { useEffect, useMemo, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useDataCoverage, useLiveBars, useMarketPulse, useMarketSession, useMyWatchlist, useSmcLadder, useStoredCandles } from '../../../lib/queries'
import {
  RANGES,
  RANGES_FOR,
  RESOLUTIONS,
  STRUCTURE_DEFAULTS,
  chartLayer,
  coverageByResolution,
  hasResolution,
  rangeFromDate,
  stitch,
  structureTimeframes,
  withLiveTick,
} from '../../../lib/chart'
import type { Candle, RangeKey, Resolution, StructureSettings } from '../../../lib/chart'
import { useLivePrices } from '../../../lib/live'
import { formatAge, formatDateTime, formatNumber, formatPrice, shortSymbol } from '../../../lib/format'
import { SymbolCombobox } from '../../../components/SymbolCombobox'
import { InlineError, Loading } from '../../../components/ui'
import { PriceChart } from '../../../components/charts'
import { SmcChart } from '../../../components/SmcChart'
import {
  StructureControls,
  StructureLadder,
  StructureLegend,
  StructureNotes,
  StructureStats,
} from './StructureLayer'
import './chart.css'

const DEFAULT_SYMBOL = 'NSE:NIFTY50-INDEX'

export function ChartPage() {
  const coverage = useDataCoverage()
  const pulse = useMarketPulse()
  const watchlist = useMyWatchlist()
  const [params, setParams] = useSearchParams()

  const symbol = params.get('symbol') ?? DEFAULT_SYMBOL
  const layer = chartLayer(params.get('layer'))
  const structureOn = layer === 'smc'
  const [search, setSearch] = useState('')
  const [resolution, setResolution] = useState<Resolution>('5')
  const [range, setRange] = useState<RangeKey>('1D')
  const [structure, setStructure] = useState<StructureSettings>(STRUCTURE_DEFAULTS)

  // The address carries the symbol and the layer, and nothing else is lost when either changes.
  const setQuery = (key: 'symbol' | 'layer', value: string | null) => {
    const next = new URLSearchParams(params)
    if (value) next.set(key, value)
    else next.delete(key)
    setParams(next, { replace: true })
  }

  const by = useMemo(() => coverageByResolution(coverage.data, symbol), [coverage.data, symbol])
  const available = (key: Resolution) => hasResolution(by, key)
  const hasLive = (by['1'] ?? []).some((r) => r.source === 'live')

  // A resolution the symbol does not have falls back to one it does.
  useEffect(() => {
    if (!coverage.data || hasResolution(by, resolution)) return
    const first = RESOLUTIONS.find((r) => hasResolution(by, r.key))
    if (first) setResolution(first.key)
  }, [coverage.data, by, resolution])
  useEffect(() => {
    if (!RANGES_FOR[resolution].includes(range)) setRange(RANGES_FOR[resolution][0])
  }, [resolution, range])

  const intraday = resolution !== 'D'
  const fromDate = useMemo(() => rangeFromDate(by, resolution, range, new Date().toISOString()), [by, resolution, range])
  const rangeDef = RANGES.find((r) => r.key === range) ?? RANGES[0]

  // Nothing is asked for until the coverage says where the range ends: asked
  // earlier, the range would count back from the clock and be asked for twice.
  const ready = coverage.data !== undefined || coverage.isError
  // Price layer: stored candles plus today's live bars. The structure layer
  // brings its own candles, read with the marks, so these wait while it is on.
  const stored = useStoredCandles(ready && !structureOn ? symbol : null, resolution, fromDate, undefined)
  const live = useLiveBars(ready && !structureOn && intraday && hasLive ? symbol : null, 3_000)
  const smc = useSmcLadder({
    symbol: ready && structureOn ? symbol : null,
    timeframes: structureTimeframes(resolution),
    fromDate,
    method: 'validPullback',
    breakOn: structure.breakOn,
    inducement: structure.inducement,
    standingZonesOnly: structure.standingZonesOnly,
    includeLive: intraday,
  })
  const smcData = smc.data?.chart ?? undefined
  const higher = smc.data?.higher ?? []

  const candles = useMemo<Candle[]>(() => {
    if (structureOn) {
      return (smcData?.candles ?? []).map((c) => ({ timeUtc: c.timestampUtc, open: c.open, high: c.high, low: c.low, close: c.close, volume: c.volume }))
    }
    const storedCandles = (stored.data ?? []).map((c) => ({ timeUtc: c.timestampUtc, open: c.open, high: c.high, low: c.low, close: c.close, volume: c.volume }))
    const liveCandles = (live.data ?? []).map((b) => ({ timeUtc: b.barStartUtc, open: b.open, high: b.high, low: b.low, close: b.close, volume: b.volumeDelta }))
    return stitch(storedCandles, liveCandles, { symbol, resolution, range, fromDate })
  }, [structureOn, smcData, stored.data, live.data, symbol, resolution, range, fromDate])

  // The forming candle between polls. Each push after the bars' answer is
  // folded into the candles as they stand, so a high reached and left between
  // two polls stays on the candle; a new answer starts again from its own bars,
  // which carry every price before it. The structure layer is left to its
  // marks, which were read from the candles as they came.
  const tick = useLivePrices(useMemo(() => [symbol], [symbol])).get(symbol)
  // MCX's evening close moves with US daylight saving; the session answer
  // says which it is today (the top bar already asks, so this is its cache).
  const mcxCloseUtc = useMarketSession('MCX', 'COM').data?.sessionCloseUtc ?? null
  const [forming, setForming] = useState<{ base: readonly Candle[]; candles: readonly Candle[] } | null>(null)
  const barsAnsweredAt = live.dataUpdatedAt
  useEffect(() => {
    if (structureOn || !tick || tick.receivedAtMs <= barsAnsweredAt) return
    setForming((f) => {
      const from = f && f.base === candles ? f.candles : candles
      const next = withLiveTick(from, tick, { symbol, resolution, mcxCloseUtc })
      return next === from && f?.base === candles ? f : { base: candles, candles: next }
    })
  }, [tick, candles, barsAnsweredAt, structureOn, symbol, resolution, mcxCloseUtc])
  const shown = forming && forming.base === candles ? forming.candles : candles

  const summary = useMemo(() => {
    if (!shown.length) return null
    const first = shown[0]
    const last = shown[shown.length - 1]
    const change = last.close - first.open
    return {
      first,
      last,
      high: Math.max(...shown.map((c) => c.high)),
      low: Math.min(...shown.map((c) => c.low)),
      volume: shown.reduce((s, c) => s + (c.volume ?? 0), 0),
      change,
      changePct: first.open ? (change / first.open) * 100 : 0,
      count: shown.length,
    }
  }, [shown])

  // Quote for the header: the pulse carries the big names; otherwise the last candle.
  const pulseItem = useMemo(
    () => (pulse.data?.groups ?? []).flatMap((g) => g.items).find((i) => i.symbol === symbol) ?? null,
    [pulse.data, symbol],
  )
  const lastPrice = pulseItem?.lastTradedPrice ?? summary?.last.close ?? null
  const asOf = pulseItem?.updatedUtc ?? summary?.last.timeUtc ?? null

  // Quick picks: the pulse universe, then the reader's own list.
  const picks = useMemo(() => {
    const seen = new Set<string>()
    const out: { symbol: string; name: string }[] = []
    for (const g of pulse.data?.groups ?? []) for (const i of g.items) if (!seen.has(i.symbol)) { seen.add(i.symbol); out.push({ symbol: i.symbol, name: i.name }) }
    for (const w of watchlist.data ?? []) if (!seen.has(w.symbol)) { seen.add(w.symbol); out.push({ symbol: w.symbol, name: shortSymbol(w.symbol) }) }
    return out
  }, [pulse.data, watchlist.data])

  const loading = structureOn ? smc.isPending : stored.isPending || (intraday && hasLive && live.isPending)
  const error = structureOn ? (smc.isError ? smc.error : null) : stored.isError ? stored.error : live.isError ? live.error : null
  const nothingHere = coverage.data && !RESOLUTIONS.some((r) => available(r.key))

  return (
    <div className="page charts">
      <div className="charts__bar">
        <div className="charts__symbol">
          <SymbolCombobox
            id="charts-symbol"
            value={search}
            onChange={setSearch}
            onSelect={(s) => {
              setQuery('symbol', s.toUpperCase())
              setSearch('')
            }}
            placeholder="Search a symbol…"
          />
        </div>
        <div className="seg" role="group" aria-label="Resolution">
          {RESOLUTIONS.map((r) => (
            <button
              key={r.key}
              type="button"
              className={`seg__btn${resolution === r.key ? ' is-active' : ''}`}
              aria-pressed={resolution === r.key}
              disabled={coverage.data ? !available(r.key) : false}
              title={coverage.data && !available(r.key) ? `No ${r.label} data for this symbol` : undefined}
              onClick={() => setResolution(r.key)}
            >
              {r.label}
            </button>
          ))}
        </div>
        <div className="seg" role="group" aria-label="Range">
          {RANGES.filter((r) => RANGES_FOR[resolution].includes(r.key)).map((r) => (
            <button
              key={r.key}
              type="button"
              className={`seg__btn${range === r.key ? ' is-active' : ''}`}
              aria-pressed={range === r.key}
              onClick={() => setRange(r.key)}
            >
              {r.label}
            </button>
          ))}
        </div>
        <div className="seg" role="group" aria-label="Layers">
          <button
            type="button"
            className={`seg__btn${structureOn ? ' is-active' : ''}`}
            aria-pressed={structureOn}
            title="Smart Money Concepts (SMC): swings, breaks of structure, inducements and zones"
            onClick={() => setQuery('layer', structureOn ? null : 'smc')}
          >
            SMC
          </button>
        </div>
      </div>

      {structureOn && <StructureControls value={structure} onChange={setStructure} />}

      {picks.length > 0 && (
        <div className="charts__picks" aria-label="Quick picks">
          {picks.map((p) => (
            <button
              key={p.symbol}
              type="button"
              className={`charts__pick${p.symbol === symbol ? ' is-active' : ''}`}
              onClick={() => setQuery('symbol', p.symbol)}
              title={p.symbol}
            >
              {p.name}
            </button>
          ))}
        </div>
      )}

      <div className="charts__head">
        <div className="charts__title">
          <span className="charts__name">{pulseItem?.name ?? shortSymbol(symbol)}</span>
          <span className="charts__sym mono">{symbol}</span>
          {hasLive && <span className="charts__live">● live</span>}
        </div>
        <div className="charts__quote">
          <span className="charts__price">{lastPrice != null ? formatPrice(lastPrice) : '—'}</span>
          {summary && (
            <span className={`charts__change ${summary.change > 0 ? 'pos' : summary.change < 0 ? 'neg' : ''}`}>
              {summary.change >= 0 ? '+' : ''}
              {summary.change.toFixed(2)} ({summary.changePct >= 0 ? '+' : ''}
              {summary.changePct.toFixed(2)}%) <span className="charts__over">over {rangeDef.label}</span>
            </span>
          )}
          {asOf && <span className="charts__asof">{formatAge(asOf)}</span>}
        </div>
      </div>

      {structureOn && <StructureLadder data={smcData} higher={higher} />}

      <div className="charts__stage">
        {error && <InlineError error={error} />}
        {nothingHere ? (
          <div className="charts__empty">
            <b>No candles for {shortSymbol(symbol)} on this installation yet.</b>
            <span>Add it to your watchlist and it will start recording on the next session; a backfill brings in history.</span>
          </div>
        ) : loading && candles.length === 0 ? (
          <Loading label="Loading candles…" />
        ) : candles.length === 0 ? (
          <div className="charts__empty">
            <b>Nothing in this range.</b>
            <span>Try a wider range or a coarser resolution.</span>
          </div>
        ) : structureOn ? (
          <SmcChart data={smcData} higher={higher} layers={structure.layers} fitKey={`${symbol}|${resolution}|${range}`} />
        ) : (
          <PriceChart candles={shown} fitKey={`${symbol}|${resolution}|${range}`} />
        )}
      </div>

      {structureOn ? (
        <>
          <StructureStats data={smcData} />
          <StructureNotes data={smcData} />
          <StructureLegend />
        </>
      ) : (
        summary && (
          <div className="charts__stats">
            <Stat k="Open" v={formatPrice(summary.first.open)} />
            <Stat k="High" v={formatPrice(summary.high)} />
            <Stat k="Low" v={formatPrice(summary.low)} />
            <Stat k="Close" v={formatPrice(summary.last.close)} />
            <Stat k="Volume" v={summary.volume > 0 ? formatNumber(summary.volume) : '—'} />
            <Stat k="Bars" v={formatNumber(summary.count)} />
            <Stat k="From" v={formatDateTime(summary.first.timeUtc)} />
            <Stat k="To" v={formatDateTime(summary.last.timeUtc)} />
          </div>
        )
      )}

      {/* What exists for this symbol, one line per resolution and source. */}
      {coverage.data && (
        <details className="charts__data">
          <summary>Data on this symbol</summary>
          <table className="table charts__data-table">
            <tbody>
              {RESOLUTIONS.flatMap((r) =>
                (by[r.key] ?? []).map((row) => (
                  <tr key={`${r.key}-${row.source}`}>
                    <td className="mono">{r.label}</td>
                    <td className="muted">{row.source === 'live' ? 'live session bars' : 'stored candles'}</td>
                    <td className="muted">
                      {formatDateTime(row.fromUtc)} → {formatDateTime(row.toUtc)}
                    </td>
                    <td className="r mono">{formatNumber(row.barCount)} bars</td>
                  </tr>
                )),
              )}
            </tbody>
          </table>
        </details>
      )}
    </div>
  )
}

function Stat({ k, v }: { k: string; v: string }) {
  return (
    <div className="charts__stat">
      <span className="charts__stat-k">{k}</span>
      <span className="charts__stat-v mono">{v}</span>
    </div>
  )
}
