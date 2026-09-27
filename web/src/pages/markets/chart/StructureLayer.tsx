/**
 * The chart's structure layer: market structure the way Smart Money Concepts
 * reads a chart. Swing points labelled HH / HL / LH / LL, a line from every
 * broken level to the candle that broke it (BOS or CHoCH), the inducement
 * each leg has to take first, and the order blocks, fair value gaps and
 * order flow around them.
 *
 * The marks are read on the server (`GET /api/Smc/ladder`) and come back with
 * the candles they were read from, so the chart cannot draw a mark against a
 * different series. This was a page of its own (Market structure); it is a
 * layer on Markets → Chart now, sharing the chart's symbol, resolution and
 * range, and these are its controls and readings.
 */

import { useState } from 'react'
import type { ReactNode } from 'react'
import type { SmcLayers } from '../../../components/SmcChart'
import { formatDateTime, formatPrice } from '../../../lib/format'
import type { SmcStructure } from '../../../lib/types'
import './structure.css'

/** What the layer draws and how it reads a break; only `standingZonesOnly` changes the request. */
export interface StructureSettings {
  layers: SmcLayers
  breakOn: 'close' | 'wick'
  inducement: 'last' | 'first'
  /**
   * Not a layer: this one changes what is FETCHED, because a zone the market
   * has spent is history rather than a level, and on a month of 5-minute
   * candles the spent ones are 99% of the gaps and 98% of the blocks. It
   * starts on for that reason; turning it off asks for the whole history.
   */
  standingZonesOnly: boolean
}

export function useStructureSettings() {
  return useState<StructureSettings>({
    // The zone layers start on except the delivery band, which is the reading
    // a chart can be read without.
    layers: {
      swings: true, breaks: true, inducements: true, minorSwings: false, higher: true,
      orderBlocks: true, fvg: true, orderFlow: false,
    },
    breakOn: 'close',
    inducement: 'last',
    standingZonesOnly: true,
  })
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
  children: ReactNode
  title?: string
  /** For a toggle that only means something while another one is on. */
  disabled?: boolean
}) {
  return (
    <button type="button" className={`seg__btn${on ? ' is-active' : ''}`} aria-pressed={on} onClick={onClick} title={title} disabled={disabled}>
      {children}
    </button>
  )
}

/** The layer's switches, one wrapping row of groups; spelt out, not initialled, where the legend unpacks jargon. */
export function StructureControls({ value, onChange }: { value: StructureSettings; onChange: (next: StructureSettings) => void }) {
  const { layers } = value
  const flip = (key: keyof SmcLayers) => onChange({ ...value, layers: { ...layers, [key]: !layers[key] } })
  return (
    <div className="smc__bar" aria-label="Structure layer">
      <div className="seg" role="group" aria-label="Marks">
        <Toggle on={layers.swings} onClick={() => flip('swings')}>Swings</Toggle>
        <Toggle on={layers.minorSwings} onClick={() => flip('minorSwings')} title="Every pullback, not only the swings the structure turned on">
          Minor
        </Toggle>
        <Toggle on={layers.breaks} onClick={() => flip('breaks')}>BOS / CHoCH</Toggle>
        <Toggle on={layers.inducements} onClick={() => flip('inducements')}>IDM</Toggle>
        <Toggle on={layers.higher} onClick={() => flip('higher')} title="The levels the higher timeframes are holding, drawn across the chart">
          Higher TF
        </Toggle>
      </div>
      <div className="seg" role="group" aria-label="Zones">
        <Toggle
          on={layers.orderBlocks}
          onClick={() => flip('orderBlocks')}
          title="The last candle against the move before structure broke — drawn from that candle, but only once the break found it"
        >
          Order blocks
        </Toggle>
        <Toggle on={layers.fvg} onClick={() => flip('fvg')} title="The band of price three candles stepped over without trading back through it">
          Fair value gaps
        </Toggle>
        <Toggle
          on={layers.orderFlow}
          onClick={() => flip('orderFlow')}
          title="Which way the market was being delivered, break to break — read from price, not measured flow"
        >
          Order flow
        </Toggle>
      </div>
      {/* Not a layer: it changes the request, so both zone kinds move together.
          It is dead only while neither zone layer is on. */}
      <div className="seg" role="group" aria-label="Which zones are fetched">
        <Toggle
          on={value.standingZonesOnly}
          onClick={() => onChange({ ...value, standingZonesOnly: !value.standingZonesOnly })}
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
      <div className="seg" role="group" aria-label="A level is broken by">
        <Toggle on={value.breakOn === 'close'} onClick={() => onChange({ ...value, breakOn: 'close' })} title="A candle has to close through the level">
          Close
        </Toggle>
        <Toggle on={value.breakOn === 'wick'} onClick={() => onChange({ ...value, breakOn: 'wick' })} title="A wick through the level is enough">
          Wick
        </Toggle>
      </div>
      <div className="seg" role="group" aria-label="Inducement">
        <Toggle on={value.inducement === 'last'} onClick={() => onChange({ ...value, inducement: 'last' })} title="The pullback the leg is on now">
          Latest pullback
        </Toggle>
        <Toggle
          on={value.inducement === 'first'}
          onClick={() => onChange({ ...value, inducement: 'first' })}
          title="The leg's first pullback, as it is taught — stricter, and it can stall on a strong trend"
        >
          First pullback
        </Toggle>
      </div>
    </div>
  )
}

function Rung({ tf, chart }: { tf: SmcStructure; chart: boolean }) {
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
        {tf.trend === 'bearish' ? 'breaks below' : 'breaks above'} <b>{tf.breakLevel != null ? formatPrice(tf.breakLevel) : '—'}</b>
      </span>
      <span className="smc__rung-idm">{tf.inducementTaken ? 'inducement taken' : 'waiting for the inducement'}</span>
      <span className="smc__rung-last">
        {latest ? `${latest.kind === 'CHOCH' ? 'CHoCH' : 'BOS'} ${formatDateTime(latest.breakTimeUtc)}` : 'nothing broken'}
      </span>
    </div>
  )
}

/**
 * The nested reading, highest timeframe first: what each one is doing and the
 * level it is holding. A rung is the whole story of the one below it.
 */
export function StructureLadder({ data, higher }: { data: SmcStructure | undefined; higher: SmcStructure[] }) {
  if (higher.length === 0) return null
  return (
    <div className="smc__ladder" aria-label="Structure by timeframe">
      {[...higher, ...(data ? [data] : [])].map((tf, i) => (
        <Rung key={tf.resolution} tf={tf} chart={i === higher.length} />
      ))}
    </div>
  )
}

function Stat({ label, value, note, tone }: { label: string; value: ReactNode; note?: ReactNode; tone?: string }) {
  return (
    <div className="charts__stat">
      <span className="charts__stat-k">{label}</span>
      <span className={`charts__stat-v ${tone ?? ''}`}>{value}</span>
      {note && <span className="smc__stat-note">{note}</span>}
    </div>
  )
}

/** What the structure reads on the chart's own timeframe, in the row the price stats use. */
export function StructureStats({ data }: { data: SmcStructure | undefined }) {
  const latest = data && data.events.length > 0 ? data.events[data.events.length - 1] : null
  const trend = data?.trend === 'bullish' ? 'Bullish' : data?.trend === 'bearish' ? 'Bearish' : 'Not set yet'
  return (
    <div className="charts__stats">
      <Stat label="Structure" value={trend} tone={`smc__trend--${data?.trend ?? 'none'}`} />
      <Stat
        label="Latest mark"
        value={latest ? `${latest.kind === 'CHOCH' ? 'CHoCH' : 'BOS'} ${formatPrice(latest.level)}` : '—'}
        note={latest ? formatDateTime(latest.breakTimeUtc) : 'nothing broken yet'}
      />
      <Stat
        label={`Breaks ${data?.trend === 'bearish' ? 'below' : 'above'}`}
        value={data?.breakLevel != null ? formatPrice(data.breakLevel) : '—'}
        note={data?.inducementTaken ? 'inducement taken' : 'waiting for the inducement'}
      />
      <Stat label="Turns on" value={data?.protectedLevel != null ? formatPrice(data.protectedLevel) : '—'} note="the protected level" />
      <Stat
        label="Inducement"
        value={data?.inducementLevel != null ? formatPrice(data.inducementLevel) : '—'}
        note={data?.inducementLevel != null ? 'still standing' : 'none standing'}
      />
      <Stat
        label="Candles read"
        value={data ? data.candles.length : '—'}
        note={data && data.liveCandles > 0 ? `${data.liveCandles} from today's live bars` : 'stored history'}
      />
    </div>
  )
}

/** The notes a reading comes with, when it has any. */
export function StructureNotes({ data }: { data: SmcStructure | undefined }) {
  return (
    <>
      {data && data.droppedOutsideSession > 0 && (
        <p className="smc__note">
          {data.droppedOutsideSession} stored candle(s) in this range are stamped outside the 09:15–15:30 session (the live
          archive keeps writing a flat candle after the close) and were left out, so they cannot invent a swing.
        </p>
      )}
      {data?.note && <p className="smc__note">{data.note}</p>}
    </>
  )
}

/** What each mark means, folded: read once, then out of the chart's way. */
export function StructureLegend() {
  return (
    <details className="charts__data smc__legend-box">
      <summary>What the structure layer draws</summary>
      <ul className="smc__legend">
        <li>
          <span className="smc__key smc__key--swing" />
          <span>
            <b>HH / HL / LH / LL</b> — a swing point, marked once a later candle took the liquidity of the candle that
            made it, and named against the previous swing of its own kind. Only the swings the structure turned on are
            labelled; "Minor" shows the pullbacks too.
          </span>
        </li>
        <li>
          <span className="smc__key smc__key--bos" />
          <span>
            <b>BOS</b> — a close through the last swing in the trend's direction, drawn from that swing to the candle that
            broke it. It only counts once the leg's inducement has been taken.
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
            <b>IDM</b> — the inducement: the pullback whose stops the market takes before it carries on. The line runs to
            the candle that took it, or to the right edge while it still stands.
          </span>
        </li>
        <li>
          <span className="smc__key smc__key--ob" />
          <span>
            <b>Order block</b> — the last candle that closed against the move before structure broke, drawn wick to wick.
            The box starts at that candle but appears only at the candle that broke structure and found it, which is
            usually several candles later. Dashed while the block still stands, solid from the candle that came back and
            used it, and it runs to that candle or to the right edge, so with "Standing only" on every block on the chart
            is dashed. Green where the break was upward and red where it was down; the same for the two marks below.
          </span>
        </li>
        <li>
          <span className="smc__key smc__key--fvg" />
          <span>
            <b>Fair value gap</b> — three candles where the third never traded back into the first's range, drawn across
            the band they stepped over. It appears at the third candle, the first one that can know it, and runs to the
            candle that filled it or to the right edge while it is still open. "Standing only" is on by default, for both
            this and the blocks: most gaps fill within a few candles, and the spent ones bury the chart. Turn it off to
            load the history as well; the delivery bands below are unaffected either way.
          </span>
        </li>
        <li>
          <span className="smc__key smc__key--flow" />
          <span>
            <b>Order flow</b> — a band under the candles covering the run from one break of structure to the next: which
            way the market was being delivered, and for how long it held. Dashed until that leg's inducement is swept.
          </span>
        </li>
      </ul>
      <p className="smc__fineprint">
        Smart Money Concepts is taught rather than specified, and its teachers differ. These marks follow the inducement
        school: a swing stands when a later candle takes the liquidity of the candle that made it, a break needs a close
        (a wick is a setting), and a break of structure waits for the inducement. Nothing is drawn before the candle that
        confirmed it, so no mark moves once it is on the chart. The rules, with their sources, are in
        docs/smart-money-concepts.md.
      </p>
      <p className="smc__fineprint">
        Two things the boxes are honest about. An order block is drawn from the candle that broke structure, so it
        arrives late and often after price has already left the zone; drawing it at its own candle would show a mark at a
        time the market could not have known it. And "order flow" here is the narrative reading, which way price says it
        is being delivered, not footprint order flow: this feed carries no aggressor side and its ticks are a
        once-a-second snapshot, so bid/ask delta and volume at price are not available at any fidelity worth the name,
        and none of them is guessed at.
      </p>
    </details>
  )
}
