/**
 * Market structure, the way Smart Money Concepts reads a chart: swing points
 * labelled HH / HL / LH / LL, a line from every broken level to the candle that
 * broke it (BOS or CHoCH), and the inducement each leg has to take first.
 *
 * Coverage first, as everywhere in the Data module: the timeframes this symbol
 * has are the only ones offered, and the range counts back from the last candle
 * stored rather than from the clock.
 *
 * The marks are read on the server (`GET /api/Smc/structure`) and come back
 * with the candles they were read from, so the chart cannot draw a mark against
 * a different series.
 */

import { useEffect, useMemo, useState } from 'react'
import { SmcChart, type SmcLayers } from '../../components/SmcChart'
import { SymbolCombobox } from '../../components/SymbolCombobox'
import { InlineError, Loading, Panel } from '../../components/ui'
import { formatDateTime, formatPrice, shortSymbol } from '../../lib/format'
import { useDataCoverage, useSmcLadder } from '../../lib/queries'
import type { CoverageRow } from '../../lib/queries'
import type { SmcStructure } from '../../lib/types'
import './structure.css'

type Resolution = '1' | '5' | '15' | 'D'

const RESOLUTIONS: { key: Resolution; label: string }[] = [
  { key: '1', label: '1m' },
  { key: '5', label: '5m' },
  { key: '15', label: '15m' },
  { key: 'D', label: 'Day' },
]

/** Calendar days asked of the server per range, wide enough to hold the sessions. */
const RANGES: { key: string; label: string; days: number | null }[] = [
  { key: '5D', label: '5D', days: 12 },
  { key: '1M', label: '1M', days: 34 },
  { key: '3M', label: '3M', days: 96 },
  { key: '1Y', label: '1Y', days: 370 },
  { key: 'ALL', label: 'All', days: null },
]

const DEFAULT_SYMBOL = 'NSE:NIFTYBANK-INDEX'

function dateInput(d: Date): string {
  return d.toISOString().slice(0, 10)
}

function Toggle({
  on,
  onClick,
  children,
  title,
  disabled,
}: {
  on: boolean
  onClick: () => void
  children: React.ReactNode
  title?: string
  /** For a toggle that only means something while another one is on. */
  disabled?: boolean
}) {
  return (
    <button
      type="button"
      className={`seg__btn${on ? ' is-active' : ''}`}
      aria-pressed={on}
      onClick={onClick}
      title={title}
      disabled={disabled}
    >
      {children}
    </button>
  )
}

function LadderRow({ tf, chart }: { tf: SmcStructure; chart: boolean }) {
  const label = tf.trend === 'bullish' ? 'Bullish' : tf.trend === 'bearish' ? 'Bearish' : 'Not set'
  const latest = tf.events.length > 0 ? tf.events[tf.events.length - 1] : null
  return (
    <div className={chart ? 'smc__rung smc__rung--chart' : 'smc__rung'}>
      <span className="smc__rung-tf">{tf.resolution}</span>
      <span className={`smc__rung-trend smc__trend--${tf.trend}`}>{label}</span>
      <span className="smc__rung-level">
        turns on <b>{tf.protectedLevel != null ? formatPrice(tf.protectedLevel) : '—'}</b>
      </span>
      <span className="smc__rung-level">
        {tf.trend === 'bearish' ? 'breaks below' : 'breaks above'}{' '}
        <b>{tf.breakLevel != null ? formatPrice(tf.breakLevel) : '—'}</b>
      </span>
      <span className="smc__rung-idm">{tf.inducementTaken ? 'inducement taken' : 'waiting for the inducement'}</span>
      <span className="smc__rung-last">
        {latest ? `${latest.kind === 'CHOCH' ? 'CHoCH' : 'BOS'} ${formatDateTime(latest.breakTimeUtc)}` : 'nothing broken'}
      </span>
    </div>
  )
}

export function StructurePage() {
  const [symbol, setSymbol] = useState(DEFAULT_SYMBOL)
  const [search, setSearch] = useState('')
  const [resolution, setResolution] = useState<Resolution>('15')
  const [range, setRange] = useState('5D')
  // The zone layers start on except the delivery band, which is the reading a
  // chart can be read without.
  const [layers, setLayers] = useState<SmcLayers>({
    swings: true, breaks: true, inducements: true, minorSwings: false, higher: true,
    orderBlocks: true, fvg: true, orderFlow: false,
  })
  // Not a layer: this one changes what is FETCHED, because a zone the market has
  // spent is history rather than a level, and on a month of 5-minute candles the
  // spent ones are 99% of the gaps and 98% of the blocks. It starts on for that
  // reason. Turning it off asks the server for the whole history and pays for it
  // — which is the point of the switch.
  const [standingZonesOnly, setStandingZonesOnly] = useState(true)
  const [breakOn, setBreakOn] = useState<'close' | 'wick'>('close')
  const [inducement, setInducement] = useState<'last' | 'first'>('last')

  const coverage = useDataCoverage()

  // What this installation holds for the symbol, by timeframe.
  const rowsFor = useMemo(() => {
    const rows = (coverage.data ?? []).filter((r) => r.symbol === symbol)
    const by: Partial<Record<Resolution, CoverageRow[]>> = {}
    for (const r of rows) {
      const key: Resolution | null =
        r.resolution === '1m' || r.resolution === '1'
          ? '1'
          : r.resolution === '5'
            ? '5'
            : r.resolution === '15'
              ? '15'
              : r.resolution === 'D'
                ? 'D'
                : null
      if (!key) continue
      ;(by[key] ??= []).push(r)
    }
    return by
  }, [coverage.data, symbol])

  const available = (key: Resolution) => (rowsFor[key]?.length ?? 0) > 0
  const nothingHere = coverage.data && !RESOLUTIONS.some((r) => available(r.key))

  useEffect(() => {
    if (!coverage.data || available(resolution)) return
    const first = RESOLUTIONS.find((r) => available(r.key))
    if (first) setResolution(first.key)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [coverage.data, symbol])

  // A day chart wants months; a minute chart wants days. Switching timeframe
  // moves the range with it rather than leaving a wall of candles.
  useEffect(() => {
    setRange(resolution === 'D' ? '1Y' : resolution === '1' ? '5D' : '1M')
  }, [resolution])

  // The range counts back from the last candle stored, so "1M" after a holiday
  // week is still a month of trading rather than an empty chart.
  const anchorUtc = useMemo(() => {
    const rows = rowsFor[resolution] ?? []
    return rows.reduce((latest, r) => (r.toUtc > latest ? r.toUtc : latest), '') || new Date().toISOString()
  }, [rowsFor, resolution])

  const days = RANGES.find((r) => r.key === range)?.days ?? null
  const fromDate = useMemo(() => {
    if (days == null) return undefined
    const d = new Date(anchorUtc)
    d.setUTCDate(d.getUTCDate() - (days - 1))
    return dateInput(d)
  }, [days, anchorUtc])

  // A day's pullback is an hour's whole trend, so the chart carries the
  // timeframes above it: each is read from its own closed candles only.
  const ABOVE: Record<Resolution, Resolution[]> = { '1': ['D', '15'], '5': ['D', '15'], '15': ['D'], D: [] }
  const timeframes = [...ABOVE[resolution], resolution]
    .map((r) => (r === 'D' ? '1D' : `${r}m`))
    .join(',')

  const structure = useSmcLadder({
    symbol,
    timeframes,
    fromDate,
    method: 'validPullback',
    breakOn,
    inducement,
    standingZonesOnly,
    includeLive: resolution !== 'D',
  })

  const data = structure.data?.chart ?? undefined
  const higher = structure.data?.higher ?? []
  const events = data?.events ?? []
  const latest = events.length > 0 ? events[events.length - 1] : null
  const trendLabel = data?.trend === 'bullish' ? 'Bullish' : data?.trend === 'bearish' ? 'Bearish' : 'Not set yet'
  const lastCandle = data && data.candles.length > 0 ? data.candles[data.candles.length - 1] : null
  const previousCandle = data && data.candles.length > 1 ? data.candles[data.candles.length - 2] : null
  const change = lastCandle && previousCandle ? lastCandle.close - previousCandle.close : null

  return (
    <div className="page smc">
      <div className="smc__bar">
        <div className="smc__symbol">
          <SymbolCombobox
            id="smc-symbol"
            value={search}
            onChange={setSearch}
            onSelect={(s) => {
              setSymbol(s.toUpperCase())
              setSearch('')
            }}
            placeholder="Search a symbol…"
          />
        </div>
        <div className="seg" role="group" aria-label="Timeframe">
          {RESOLUTIONS.map((r) => (
            <button
              key={r.key}
              type="button"
              className={`seg__btn${resolution === r.key ? ' is-active' : ''}`}
              aria-pressed={resolution === r.key}
              disabled={coverage.data ? !available(r.key) : false}
              title={coverage.data && !available(r.key) ? `No ${r.label} candles stored for this symbol` : undefined}
              onClick={() => setResolution(r.key)}
            >
              {r.label}
            </button>
          ))}
        </div>
        <div className="seg" role="group" aria-label="Range">
          {RANGES.map((r) => (
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
      </div>

      <div className="smc__bar smc__bar--marks">
        <div className="seg" role="group" aria-label="Marks">
          <Toggle on={layers.swings} onClick={() => setLayers((l) => ({ ...l, swings: !l.swings }))}>
            Swings
          </Toggle>
          <Toggle
            on={layers.minorSwings}
            onClick={() => setLayers((l) => ({ ...l, minorSwings: !l.minorSwings }))}
            title="Every pullback, not only the swings the structure turned on"
          >
            Minor
          </Toggle>
          <Toggle on={layers.breaks} onClick={() => setLayers((l) => ({ ...l, breaks: !l.breaks }))}>
            BOS / CHoCH
          </Toggle>
          <Toggle on={layers.inducements} onClick={() => setLayers((l) => ({ ...l, inducements: !l.inducements }))}>
            IDM
          </Toggle>
          <Toggle
            on={layers.higher}
            onClick={() => setLayers((l) => ({ ...l, higher: !l.higher }))}
            title="The levels the higher timeframes are holding, drawn across the chart"
          >
            Higher TF
          </Toggle>
        </div>
        <div className="seg" role="group" aria-label="A level is broken by">
          <Toggle on={breakOn === 'close'} onClick={() => setBreakOn('close')} title="A candle has to close through the level">
            Close
          </Toggle>
          <Toggle on={breakOn === 'wick'} onClick={() => setBreakOn('wick')} title="A wick through the level is enough">
            Wick
          </Toggle>
        </div>
        <div className="seg" role="group" aria-label="Inducement">
          <Toggle on={inducement === 'last'} onClick={() => setInducement('last')} title="The pullback the leg is on now">
            Latest pullback
          </Toggle>
          <Toggle
            on={inducement === 'first'}
            onClick={() => setInducement('first')}
            title="The leg's first pullback, as it is taught — stricter, and it can stall on a strong trend"
          >
            First pullback
          </Toggle>
        </div>
      </div>

      {/* The zones get a row of their own rather than three more buttons in the
          Marks row: a .seg does not wrap, so the row would run off the side of a
          laptop instead of on to a second line. Spelt out, not initialled — OB
          and FVG are the jargon the legend below exists to unpack. */}
      <div className="smc__bar smc__bar--marks smc__bar--zones">
        <div className="seg" role="group" aria-label="Zones">
          <Toggle
            on={layers.orderBlocks}
            onClick={() => setLayers((l) => ({ ...l, orderBlocks: !l.orderBlocks }))}
            title="The last candle against the move before structure broke — drawn from that candle, but only once the break found it"
          >
            Order blocks
          </Toggle>
          <Toggle
            on={layers.fvg}
            onClick={() => setLayers((l) => ({ ...l, fvg: !l.fvg }))}
            title="The band of price three candles stepped over without trading back through it"
          >
            Fair value gaps
          </Toggle>
          <Toggle
            on={layers.orderFlow}
            onClick={() => setLayers((l) => ({ ...l, orderFlow: !l.orderFlow }))}
            title="Which way the market was being delivered, break to break — read from price, not measured flow"
          >
            Order flow
          </Toggle>
        </div>
        {/* This one is not a layer — it changes the request, so both zone kinds
            move together and there is nothing to draw for what was not asked
            for. It is dead only while neither zone layer is on. */}
        <div className="seg" role="group" aria-label="Which zones are fetched">
          <Toggle
            on={standingZonesOnly}
            onClick={() => setStandingZonesOnly((on) => !on)}
            disabled={!layers.fvg && !layers.orderBlocks}
            title={
              layers.fvg || layers.orderBlocks
                ? 'Fetch only the blocks price has not come back for and the gaps it has not filled. Most are spent within a few candles, and on a month of 5-minute candles the spent ones are the bulk of the answer. Turn it off to load the history too'
                : 'Only does anything while order blocks or fair value gaps are drawn'
            }
          >
            Standing only
          </Toggle>
        </div>
      </div>

      {/* Which symbol is on the chart, in words: the search box empties on
          select, so the chart would otherwise be unlabelled. */}
      <div className="smc__head">
        <div>
          <h2 className="smc__title">{shortSymbol(symbol)}</h2>
          <p className="smc__subtitle">
            <span className="mono">{symbol}</span> · {data?.resolution ?? RESOLUTIONS.find((r) => r.key === resolution)?.label} candles
          </p>
        </div>
        {lastCandle && (
          <div className="smc__last">
            <span className="smc__last-price">{formatPrice(lastCandle.close)}</span>
            {change != null && (
              <span className={change >= 0 ? 'smc__last-change smc__trend--bullish' : 'smc__last-change smc__trend--bearish'}>
                {change >= 0 ? '+' : ''}
                {formatPrice(change)}
              </span>
            )}
            <span className="smc__last-time">last candle {formatDateTime(lastCandle.timestampUtc)}</span>
          </div>
        )}
      </div>

      {(structure.isError || coverage.isError) && (
        <InlineError error={structure.isError ? structure.error : coverage.error} />
      )}

      {nothingHere ? (
        <Panel title={shortSymbol(symbol)}>
          <p className="dim">
            No candles are stored for this symbol. Back-fill it from Data → Historical, then it can be read here.
          </p>
        </Panel>
      ) : (
        <>
          {/* The nested reading, highest timeframe first: what each one is doing,
              and the level it is holding. A row is the whole story of the one below. */}
          {higher.length > 0 && (
            <div className="smc__ladder" aria-label="Structure by timeframe">
              {[...higher, ...(data ? [data] : [])].map((tf, i) => (
                <LadderRow key={tf.resolution} tf={tf} chart={i === higher.length} />
              ))}
            </div>
          )}

          <div className="smc__stage">
            {structure.isLoading && !data ? (
              <Loading />
            ) : (
              <SmcChart data={data} higher={higher} layers={layers} fitKey={`${symbol}|${resolution}|${range}`} />
            )}
          </div>

          <div className="smc__stats">
            <div className="smc__stat">
              <span className="smc__stat-label">Structure</span>
              <span className={`smc__stat-value smc__trend--${data?.trend ?? 'none'}`}>{trendLabel}</span>
            </div>
            <div className="smc__stat">
              <span className="smc__stat-label">Latest mark</span>
              <span className="smc__stat-value">
                {latest ? `${latest.kind === 'CHOCH' ? 'CHoCH' : 'BOS'} ${formatPrice(latest.level)}` : '—'}
              </span>
              <span className="smc__stat-note">{latest ? formatDateTime(latest.breakTimeUtc) : 'nothing broken yet'}</span>
            </div>
            <div className="smc__stat">
              <span className="smc__stat-label">
                Breaks structure {data?.trend === 'bearish' ? 'below' : 'above'}
              </span>
              <span className="smc__stat-value">{data?.breakLevel != null ? formatPrice(data.breakLevel) : '—'}</span>
              <span className="smc__stat-note">
                {data?.inducementTaken ? 'inducement taken' : 'waiting for the inducement'}
              </span>
            </div>
            <div className="smc__stat">
              <span className="smc__stat-label">Turns on</span>
              <span className="smc__stat-value">{data?.protectedLevel != null ? formatPrice(data.protectedLevel) : '—'}</span>
              <span className="smc__stat-note">the protected level</span>
            </div>
            <div className="smc__stat">
              <span className="smc__stat-label">Inducement</span>
              <span className="smc__stat-value">
                {data?.inducementLevel != null ? formatPrice(data.inducementLevel) : '—'}
              </span>
              <span className="smc__stat-note">{data?.inducementLevel != null ? 'still standing' : 'none standing'}</span>
            </div>
            <div className="smc__stat">
              <span className="smc__stat-label">Candles read</span>
              <span className="smc__stat-value">{data ? data.candles.length : '—'}</span>
              <span className="smc__stat-note">
                {data && data.liveCandles > 0 ? `${data.liveCandles} from today's live bars` : 'stored history'}
              </span>
            </div>
          </div>

          {data && data.droppedOutsideSession > 0 && (
            <p className="smc__note">
              {data.droppedOutsideSession} stored candle(s) in this range are stamped outside the 09:15–15:30 session —
              the live archive keeps writing a flat candle after the close — and were left out, so they cannot invent a
              swing.
            </p>
          )}
          {data?.note && <p className="smc__note">{data.note}</p>}

          <Panel title="What is on the chart">
            <ul className="smc__legend">
              <li>
                <span className="smc__key smc__key--swing" />
                <span>
                  <b>HH / HL / LH / LL</b> — a swing point, marked once a later candle took the liquidity of the candle
                  that made it, and named against the previous swing of its own kind. Only the swings the structure
                  turned on are labelled; "Minor" shows the pullbacks too.
                </span>
              </li>
              <li>
                <span className="smc__key smc__key--bos" />
                <span>
                  <b>BOS</b> — a close through the last swing in the trend's direction, drawn from that swing to the
                  candle that broke it. It only counts once the leg's inducement has been taken.
                </span>
              </li>
              <li>
                <span className="smc__key smc__key--choch" />
                <span>
                  <b>CHoCH</b> — a close through the level that protected the trend. The structure turns here.
                </span>
              </li>
              <li>
                <span className="smc__key smc__key--idm" />
                <span>
                  <b>IDM</b> — the inducement: the pullback whose stops the market takes before it carries on. The line
                  runs to the candle that took it, or to the right edge while it still stands.
                </span>
              </li>
              <li>
                <span className="smc__key smc__key--ob" />
                <span>
                  <b>Order block</b> — the last candle that closed against the move before structure broke, drawn wick
                  to wick. The box starts at that candle but appears only at the candle that broke structure and found
                  it, which is usually several candles later. Dashed while the block still stands, solid from the
                  candle that came back and used it, and it runs to that candle or to the right edge — so with
                  "Standing only" on, every block on the chart is dashed. Green where the break was upward and red
                  where it was down — the same for the two marks below.
                </span>
              </li>
              <li>
                <span className="smc__key smc__key--fvg" />
                <span>
                  <b>Fair value gap</b> — three candles where the third never traded back into the first's range, drawn
                  across the band they stepped over. It appears at the third candle, which is the first one that can
                  know it, and runs to the candle that filled it or to the right edge while it is still open.
                  "Standing only" is on by default, for both this and the blocks above: most gaps fill within a few
                  candles, and the spent ones bury the chart and are the bulk of what a refresh fetches. Turn it off
                  to load the history as well — the delivery bands below are unaffected either way, since a finished
                  run is context rather than a level that has been used up.
                </span>
              </li>
              <li>
                <span className="smc__key smc__key--flow" />
                <span>
                  <b>Order flow</b> — a band under the candles covering the run from one break of structure to the
                  next: which way the market was being delivered, and for how long it held. Dashed until that leg's
                  inducement is swept, which is the point at which the break behind the run becomes something to act
                  on.
                </span>
              </li>
            </ul>
            <p className="smc__fineprint">
              Smart Money Concepts is taught rather than specified, and its teachers differ. These marks follow the
              inducement school: a swing stands when a later candle takes the liquidity of the candle that made it, a
              break needs a close (a wick is a setting), and a break of structure waits for the inducement. Nothing is
              drawn before the candle that confirmed it, so no mark here moves once it is on the chart. The rules, with
              their sources, are in docs/smart-money-concepts.md.
            </p>
            <p className="smc__fineprint">
              Two things the boxes are honest about, and the popular scripts are not. An order block is drawn from the
              candle that broke structure, so it arrives late and often after price has already left the zone; drawing
              it at its own candle instead would show a mark at a time the market could not have known it, which is
              the one thing this page will not do. And "order flow" here is the narrative reading — which way price
              says it is being delivered — not footprint order flow. This feed carries no aggressor side and its ticks
              are a once-a-second snapshot, so bid/ask delta, cumulative delta and volume at price are not available at
              any fidelity worth the name, and none of them is guessed at. A narrower ICT usage gives the name "order
              flow" to the corrective candles around a break, which are the very candles the order block is cut from;
              here the box marks that candle and the band marks the run it sits in, so the two layers describe the same
              stretch of chart from different distances rather than disagreeing.
            </p>
          </Panel>
        </>
      )}
    </div>
  )
}
