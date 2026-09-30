/**
 * Indices & levels: NIFTY, BANKNIFTY, SENSEX, India VIX, crude and gold, with
 * today's trace, the range so far against the day's range forecast, and the
 * levels option sellers have written into each index's nearest expiry (the
 * walls, PCR, max pain, ATM IV and the move the straddle prices), read the way
 * Market factors reads them.
 *
 * Prices come from the market pulse, VIX from the NIFTY chain's header (the
 * pulse does not carry it), both moved by their pushed prices between
 * answers; the levels from three chain views polled every 30 s in the
 * session, the traces from one-minute bars once a minute.
 */

import { useMemo } from 'react'
import type { ChainLevels, ForecastRow, IndexRow } from '../../lib/desk'
import { VIX_SYMBOL, chainLevels, dayLabel, dayOf, dayTrace, forecastRows, indexRows, plainNumber, rangeSoFar, signedNumber, weekdayOf } from '../../lib/desk'
import { useLivePrices } from '../../lib/live'
import { answerAsOf } from '../../lib/asOf'
import { freshPrice } from '../../lib/liveMarks'
import { applyTickToQuote } from '../../lib/optionChain'
import { allows } from '../../lib/modules'
import { formatCrore } from '../../lib/factors'
import { useDeskChainViews, useIntradayTrace, useMarketEvents, useMarketFlows, useMarketPulse } from '../../lib/queries'
import type { DeskLinks, DeskView } from './data'
import { toneClass, useDayForecasts } from './data'
import { Failed, PanelHead, RangeMeter, Spark, Waiting } from './parts'

const CHAINED = ['NIFTY', 'BANKNIFTY', 'SENSEX'] as const
const VIX = [VIX_SYMBOL]

function Trace({ row, view }: { row: IndexRow; view: DeskView }) {
  const bars = useIntradayTrace(row.symbol, view.clock === 'live')
  return <Spark values={dayTrace(bars.data, view.day)} base={row.prevClose} />
}

function Levels({ levels }: { levels: ChainLevels | null }) {
  const cell = (cls: string, lab: string, value: string | null) => (
    <span className={`dk-ix__lv dk-n ${cls}`}>
      <span className="dk-lab">{lab} </span>
      {value ?? <span className="dk-t3">—</span>}
    </span>
  )
  const n = (v: number | null, d = 0) => (v == null ? null : plainNumber(v, d))
  // On a wide screen the wrapper vanishes (display: contents) and each level takes its column;
  // on a phone it is one wrapping line under the price.
  return (
    <span className="dk-ix__levels">
      {/* Each wall is read on its own side of spot since 30 Sep, so one can be
          unknown (no open interest on that side) while the other is not. */}
      {cell('dk-ix__w1', 'walls', levels && (levels.putWall != null || levels.callWall != null)
        ? `${n(levels.putWall) ?? '—'}·${n(levels.callWall) ?? '—'}`
        : null)}
      {cell('dk-ix__w2', 'PCR', levels?.pcr != null ? levels.pcr.toFixed(2) : null)}
      {cell('dk-ix__w3', 'pain', n(levels?.maxPain ?? null))}
      {cell('dk-ix__w4', 'IV', levels?.iv != null ? levels.iv.toFixed(1) : null)}
      {cell('dk-ix__w5', '±', levels?.straddlePct != null ? `${levels.straddlePct.toFixed(2)}%` : null)}
    </span>
  )
}

function Forecast({ row, forecast, view }: { row: IndexRow; forecast: ForecastRow | undefined; view: DeskView }) {
  const r = forecast?.range
  if (!r) return <span className="dk-ix__fc" />
  if (view.phase === 'pre') {
    return (
      <span className="dk-ix__fc" title={`Range forecast (80%): ${Math.round(r.low80)}–${Math.round(r.high80)} points; baseline ${r.baseMedian != null ? Math.round(r.baseMedian) : '—'}`}>
        <span className="dk-n dk-t2">{Math.round(r.median)}</span>
        <span className="dk-n">
          pts · {Math.round(r.low80)}–{Math.round(r.high80)}
        </span>
      </span>
    )
  }
  const soFar = view.phase === 'post' && forecast?.scored ? forecast.scored.range : rangeSoFar(row)
  return (
    <span className="dk-ix__fc" title={`${view.phase === 'post' ? 'The day’s range' : 'Range so far'} ${soFar != null ? Math.round(soFar) : '—'} points against the forecast ${Math.round(r.median)} (80%: ${Math.round(r.low80)}–${Math.round(r.high80)})`}>
      <RangeMeter soFar={soFar} median={r.median} low80={r.low80} high80={r.high80} />
      <span className="dk-n">
        <span className="dk-t2">{soFar != null ? Math.round(soFar) : '—'}</span> / {Math.round(r.median)}
      </span>
    </span>
  )
}

function Row({ row, levels, forecast, view }: { row: IndexRow; levels: ChainLevels | null; forecast: ForecastRow | undefined; view: DeskView }) {
  const name = row.contract ? `${row.name} ${row.contract}` : row.name
  return (
    <div className={`dk-ix${row.chained ? '' : ' dk-ix--minor'}`}>
      <span className="dk-ix__nm" title={row.symbol}>
        {name}
      </span>
      <span className="dk-ix__ltp dk-n">{row.ltp != null ? plainNumber(row.ltp, row.digits) : '—'}</span>
      <span className={`dk-ix__chg dk-n ${toneClass(row.change)}`}>
        {row.change != null && row.changePct != null ? `${signedNumber(row.change, row.digits)} · ${signedNumber(row.changePct)}%` : '—'}
      </span>
      <span className="dk-ix__tr">{view.phase !== 'pre' && <Trace row={row} view={view} />}</span>
      {row.chained ? <Forecast row={row} forecast={forecast} view={view} /> : <span className="dk-ix__fc" />}
      {row.chained ? <Levels levels={levels} /> : <span className="dk-ix__levels" />}
    </div>
  )
}

/** Flows and the week's markers in one line under the table (market-data grant). */
function ContextLine({ view }: { view: DeskView }) {
  const flows = useMarketFlows(20)
  const events = useMarketEvents()
  const cash = [...(flows.data?.cash ?? [])].sort((a, b) => a.date.localeCompare(b.date)).at(-1)
  const fiiLong = flows.data?.participants
    .slice()
    .sort((a, b) => a.date.localeCompare(b.date))
    .at(-1)
    ?.groups.find((g) => g.clientType.toUpperCase() === 'FII')?.futureIndexLongPercent
  const ahead = (events.data?.events ?? [])
    .filter((e) => e.date >= view.today && e.importance >= 2 && e.kind !== 'expiry')
    .slice(0, 3)
  if (!cash && !ahead.length) return null
  return (
    <div className="dk-ctx">
      {cash?.fii && (
        <span>
          FII cash <b className={toneClass(cash.fii.net)}>{formatCrore(cash.fii.net)}</b>
        </span>
      )}
      {cash?.dii && (
        <span>
          DII <b className={toneClass(cash.dii.net)}>{formatCrore(cash.dii.net)}</b> ({dayLabel(cash.date)})
        </span>
      )}
      {fiiLong != null && (
        <span>
          FII index futures long <b>{fiiLong.toFixed(1)}%</b>
        </span>
      )}
      {ahead.map((e) => (
        <span key={`${e.date}${e.title}`}>
          {weekdayOf(e.date)} <b>{e.title}</b>
          {e.timeIst ? ` ${e.timeIst}` : ''}
        </span>
      ))}
    </div>
  )
}

/** The forecast column needs the analysis grant; without it the table is asked for nothing it would be refused. */
export function Indices({ view, links }: { view: DeskView; links: DeskLinks }) {
  return allows(view.access, 'analysis') ? <WithForecasts view={view} links={links} /> : <IndexTable view={view} links={links} forecasts={null} />
}

function WithForecasts({ view, links }: { view: DeskView; links: DeskLinks }) {
  const forecasts = useDayForecasts(view)
  return <IndexTable view={view} links={links} forecasts={forecastRows(forecasts.data, view.day)} />
}

function IndexTable({ view, links, forecasts }: { view: DeskView; links: DeskLinks; forecasts: ForecastRow[] | null }) {
  const pulse = useMarketPulse()
  const chains = useDeskChainViews(CHAINED, view.clock === 'live')
  const byUnd = new Map((forecasts ?? []).map((f) => [f.underlying, f]))
  const vixAnswer = chains[0].data?.header?.vix ?? null
  const vixAnsweredAt = answerAsOf(chains[0])
  const vixTick = useLivePrices(VIX).get(VIX_SYMBOL)
  const vix = useMemo(
    () =>
      vixTick && freshPrice(vixTick, vixAnsweredAt) != null
        ? applyTickToQuote(vixAnswer, vixTick, new Date(vixTick.receivedAtMs).toISOString())
        : vixAnswer,
    [vixAnswer, vixTick, vixAnsweredAt],
  )
  const rows = indexRows(pulse.data, vix)
  const closeDay = view.phase === 'pre' ? dayOf(rows[0]?.updatedUtc) : null
  const lastLabel = closeDay && closeDay < view.today ? `${weekdayOf(closeDay)} close` : 'LTP'
  const fcHead = view.phase === 'pre' ? 'Range forecast' : view.phase === 'post' ? 'Range vs forecast' : 'Range so far / fc'
  const meta = view.phase === 'pre' ? 'last close · levels from the latest chain' : view.phase === 'post' ? 'the close · nearest expiry' : 'live · chain every 30 s · nearest expiry'
  return (
    <>
      <PanelHead title="Indices & levels" meta={meta} more={links.chain ? { to: links.chain, label: 'Chain' } : null} />
      {pulse.isError && !pulse.data ? (
        <Failed what="The market pulse" error={pulse.error} />
      ) : rows.length === 0 ? (
        <Waiting>Reading the market…</Waiting>
      ) : (
        <>
          <div className="dk-ix dk-ix--head">
            <span>Index</span>
            <span className="dk-ix__ltp">{lastLabel}</span>
            <span className="dk-ix__chg">{closeDay && closeDay < view.today ? `Change (${weekdayOf(closeDay)})` : 'Change'}</span>
            <span>{view.phase === 'pre' ? '' : 'Today'}</span>
            <span>{forecasts ? fcHead : ''}</span>
            <span className="dk-ix__lv">Put · call wall</span>
            <span className="dk-ix__lv">PCR</span>
            <span className="dk-ix__lv">Pain</span>
            <span className="dk-ix__lv">IV</span>
            <span className="dk-ix__lv">Straddle</span>
          </div>
          {rows.map((row) => {
            const i = CHAINED.indexOf(row.key as (typeof CHAINED)[number])
            return <Row key={row.key} row={row} view={view} levels={i >= 0 ? chainLevels(chains[i].data) : null} forecast={byUnd.get(row.key)} />
          })}
          {chains.some((c) => c.isError && !c.data) && <p className="dk-note">Some chain levels could not be read; they fill in on the next try.</p>}
        </>
      )}
      {allows(view.access, 'market-data') && <ContextLine view={view} />}
    </>
  )
}
