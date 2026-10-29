/**
 * Markets → Strategy builder: put a multi-leg option position together off
 * the live chain and see what it can make and lose before entering it.
 *
 * The legs come from the chain (a strike, a call or a put, bought or sold, in
 * lots) or from a preset built around the money, and each is priced at the
 * middle of its quote, else its last trade, until the trader types a price.
 * The page reads that position back: the payoff at expiry and before it, the
 * breakevens, the most it can make and lose (and "unlimited" where nothing
 * bounds it), the premium in rupees and the net greeks, all in rupees for
 * lots × the contract's lot size. Three sliders move the spot, the IV and the
 * day, and the P&L at that point is read off the curve.
 *
 * Analysis only: nothing here places an order. Margin is not computed: it is
 * the broker's SPAN and exposure figure, and the page says so rather than
 * guess it. The arithmetic is lib/payoff.ts and the chain-facing rules
 * lib/strategyBuilder.ts, both pinned by their tests.
 *
 * The chain is the Option chain page's own query (useOptionChainView), on the
 * expiry its recorder follows; the underlying travels in the URL (?u=) the
 * same way, so a link opens the same chain on either page.
 */

import { useEffect, useMemo, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useFnoUnderlyings, useOptionChainView } from '../../../lib/queries'
import {
  UNDERLYINGS,
  capturedExpiry,
  chainUnderlying,
  describeFreshness,
  expiryLabel,
  istDate,
  istTime,
  price,
  signedPercent,
  strikeWindow,
} from '../../../lib/optionChain'
import type { OptionChain, OptionChainHeader } from '../../../lib/types'
import { breakevens, expiryPnl, extremes, netGreeks, netPremium, pnlBefore } from '../../../lib/payoff'
import type { Extreme, LegSide, OptionKind, PayoffLeg, PricingInputs } from '../../../lib/payoff'
import {
  BUILDER_RATE,
  MAX_LOTS,
  PRESETS,
  addLeg,
  atmStrike,
  chainLeg,
  daysToExpiry,
  expiryCloseMs,
  legIv,
  marketPrice,
  presetLegs,
  timeLeftLabel,
  toPayoffLeg,
} from '../../../lib/strategyBuilder'
import type { BuilderLeg, LegIv, MarketPrice, PresetKey } from '../../../lib/strategyBuilder'
import { formatInrSigned, formatInrWhole, formatNumber } from '../../../lib/format'
import { EmptyState, InlineError, Loading, Panel, StatTile } from '../../../components/ui'
import { PayoffChart } from './PayoffChart'
import type { BeforeCurve } from './PayoffChart'
import './builder.css'

const MINUS = '−'

/** "+37.5", "−0.090": a signed figure with Indian grouping and a true minus. */
function signed(value: number, digits: number): string {
  const body = Math.abs(value).toLocaleString('en-IN', { minimumFractionDigits: digits, maximumFractionDigits: digits })
  if (Number(Math.abs(value).toFixed(digits)) === 0) return body
  return `${value > 0 ? '+' : MINUS}${body}`
}

/** A strike as the chain prints it: "24,300", "5,450.5". */
function strikeText(strike: number): string {
  return strike.toLocaleString('en-IN', { maximumFractionDigits: 2 })
}

/** What the curve before expiry is called: "Today", "T+1.5 days", "Today, IV +2". */
function curveLabel(daysAhead: number, ivShift: number): string {
  const when = daysAhead === 0 ? 'Today' : `T+${daysAhead.toLocaleString('en-IN')} ${daysAhead === 1 ? 'day' : 'days'}`
  return ivShift === 0 ? when : `${when}, IV ${signed(ivShift, ivShift % 1 === 0 ? 0 : 1)}`
}

// --- freshness ---------------------------------------------------------------------

function useNow(intervalMs: number): number {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), intervalMs)
    return () => clearInterval(id)
  }, [intervalMs])
  return now
}

/** How old the prices under every figure are: the chain page's line, on the server's clock. */
function FreshnessLine({ header, receivedAt, fetchFailed }: { header: OptionChainHeader; receivedAt: number; fetchFailed: boolean }) {
  const now = useNow(1000)
  const f = describeFreshness(header, Date.parse(header.serverUtc) + (now - receivedAt))
  return (
    <p className={`oc-fresh oc-fresh--${f.tone}`} role="status">
      <span className="oc-fresh__dot" aria-hidden="true" />
      <span>{f.text}</span>
      {fetchFailed && <span className="warn"> · refresh failed, showing the last answer</span>}
    </p>
  )
}

// --- inputs ------------------------------------------------------------------------

/**
 * A number typed into a cell. The text is the input's own while it is being
 * typed ("12." is not yet a number), and is replaced only when the value
 * changes from outside it (a reset to the market).
 */
function NumberInput({
  value,
  placeholder,
  label,
  parse,
  onChange,
  className,
}: {
  value: number | null
  placeholder?: string
  label: string
  /** The typed text as a value, or undefined while it is not one. */
  parse: (text: string) => number | null | undefined
  onChange: (value: number | null) => void
  className?: string
}) {
  const [text, setText] = useState(value == null ? '' : String(value))
  const [seen, setSeen] = useState(value)
  if (value !== seen) {
    setSeen(value)
    setText(value == null ? '' : String(value))
  }
  const invalid = parse(text) === undefined
  return (
    <input
      className={`field__input field__input--sm sb-num ${className ?? ''}`}
      inputMode="decimal"
      value={text}
      placeholder={placeholder}
      aria-label={label}
      aria-invalid={invalid || undefined}
      onChange={(e) => {
        setText(e.target.value)
        const next = parse(e.target.value)
        if (next === undefined) return
        setSeen(next)
        onChange(next)
      }}
      onBlur={() => {
        if (invalid) setText(value == null ? '' : String(value))
      }}
    />
  )
}

/** A price: empty follows the market; otherwise a number of zero or more. */
const parseEntry = (text: string) => {
  if (text.trim() === '') return null
  const n = Number(text)
  return Number.isFinite(n) && n >= 0 ? Math.round(n * 100) / 100 : undefined
}

/** Lots: a whole number from 1 to MAX_LOTS. */
const parseLots = (text: string) => {
  const n = Number(text)
  return Number.isInteger(n) && n >= 1 && n <= MAX_LOTS ? n : undefined
}

function Slider({
  label,
  value,
  min,
  max,
  step,
  disabled,
  text,
  onChange,
}: {
  label: string
  value: number
  min: number
  max: number
  step: number
  disabled?: boolean
  text: string
  onChange: (value: number) => void
}) {
  return (
    <label className="sb-slider">
      <span className="sb-slider__head">
        <span className="oc-control__label">{label}</span>
        <span className="sb-slider__value">{text}</span>
      </span>
      <input
        type="range"
        min={min}
        max={max}
        step={step}
        value={value}
        disabled={disabled}
        onChange={(e) => onChange(Number(e.target.value))}
      />
    </label>
  )
}

// --- the readings --------------------------------------------------------------------

function ExtremeTile({ label, extreme, underlying, highest, kind }: { label: string; extreme: Extreme; underlying: string; highest: number; kind: 'profit' | 'loss' }) {
  if (extreme === 'unlimited') {
    return <StatTile label={label} value="Unlimited" tone={kind === 'profit' ? 'pos' : 'neg'} sub={`keeps growing above ${strikeText(highest)}`} />
  }
  const sub =
    extreme.at === 0
      ? `if ${underlying} went to 0`
      : kind === 'profit' && extreme.value < 0
        ? 'the best case still loses'
        : kind === 'loss' && extreme.value > 0
          ? 'the worst case still makes money'
          : 'at expiry'
  return <StatTile label={label} value={formatInrSigned(extreme.value)} tone={extreme.value > 0 ? 'pos' : extreme.value < 0 ? 'neg' : undefined} sub={sub} />
}

function Greek({ symbol, value, unit, title }: { symbol: string; value: string; unit: string; title: string }) {
  return (
    <div className="sb-greek" title={title}>
      <span className="oc-control__label">{symbol}</span>
      <span className="sb-greek__value">{value}</span>
      <span className="faint">{unit}</span>
    </div>
  )
}

// --- one priced leg ---------------------------------------------------------------------

interface LegRow {
  leg: BuilderLeg
  market: MarketPrice | null
  /** The price the arithmetic uses: typed, else the market's; null when there is neither. */
  entry: number | null
  iv: LegIv | null
  /** The chain's quote, for the market cell's tooltip. */
  quote: { bid: number | null; ask: number | null; ltp: number | null }
}

function marketTitle(row: LegRow): string {
  const q = row.quote
  const parts = [`Bid ${price(q.bid)} · Ask ${price(q.ask)} · LTP ${price(q.ltp)}`]
  parts.push(row.market?.basis === 'mid' ? 'Priced at the middle of the bid and ask.' : 'No two-sided quote: priced at the last trade.')
  return parts.join('\n')
}

function LegsTable({
  rows,
  strikes,
  atm,
  lotSize,
  onChange,
  onRemove,
}: {
  rows: LegRow[]
  strikes: readonly number[]
  atm: number | null
  lotSize: number | null
  onChange: (id: string, patch: Partial<BuilderLeg>) => void
  onRemove: (id: string) => void
}) {
  return (
    <table className="table sb-legs">
      <thead>
        <tr>
          <th>Side</th>
          <th>Contract</th>
          <th className="r">Lots</th>
          <th className="r" title="The price each unit is entered at. Empty follows the market; type a price to hold it.">Entry</th>
          <th className="r" title="The middle of the bid and ask, else the last trade">Market</th>
          <th className="r" title="The IV this leg is priced with before expiry">IV</th>
          <th className="r" title="Premium for the leg: received on a sale (+), paid on a purchase (−)">Premium</th>
          <th aria-label="Remove" />
        </tr>
      </thead>
      <tbody>
        {rows.map((row) => {
          const { leg } = row
          const units = lotSize ? leg.lots * lotSize : null
          const premium = row.entry != null && units != null ? (leg.side === 'sell' ? 1 : -1) * row.entry * units : null
          return (
            <tr key={leg.id}>
              <td className="sb-legs__side" data-label="Side">
                <button
                  type="button"
                  className={`sb-side sb-side--${leg.side}`}
                  title="Switch between buying and selling this leg"
                  onClick={() => onChange(leg.id, { side: leg.side === 'buy' ? 'sell' : 'buy' })}
                >
                  {leg.side === 'buy' ? 'BUY' : 'SELL'}
                </button>
              </td>
              <td className="sb-legs__contract" data-label="Contract">
                <select
                  className="field__input field__input--sm sb-strike"
                  aria-label="Strike"
                  value={leg.strike}
                  onChange={(e) => onChange(leg.id, { strike: Number(e.target.value) })}
                >
                  {strikes.map((s) => (
                    <option key={s} value={s}>
                      {strikeText(s)}
                      {s === atm ? ' · ATM' : ''}
                    </option>
                  ))}
                </select>
                <div className="oc-seg" role="group" aria-label="Call or put">
                  {(['CE', 'PE'] as const).map((k) => (
                    <button key={k} type="button" aria-pressed={leg.kind === k} onClick={() => onChange(leg.id, { kind: k })}>
                      {k}
                    </button>
                  ))}
                </div>
              </td>
              <td className="r sb-legs__lots" data-label="Lots">
                <NumberInput value={leg.lots} label="Lots" parse={parseLots} onChange={(v) => v != null && onChange(leg.id, { lots: v })} className="sb-num--lots" />
                {units != null && <span className="faint sb-units">×{formatNumber(lotSize)} = {formatNumber(units)}</span>}
              </td>
              <td className="r sb-legs__entry" data-label="Entry">
                <NumberInput
                  value={leg.entry}
                  placeholder={row.market ? price(row.market.price) : 'price'}
                  label="Entry price"
                  parse={parseEntry}
                  onChange={(v) => onChange(leg.id, { entry: v })}
                />
                {leg.entry != null ? (
                  <button type="button" className="sb-link" onClick={() => onChange(leg.id, { entry: null })} disabled={!row.market}>
                    typed · use market
                  </button>
                ) : (
                  <span className={row.market ? 'faint sb-units' : 'warn sb-units'}>{row.market ? 'follows market' : 'type a price'}</span>
                )}
              </td>
              <td className="r mono sb-legs__market" data-label="Market" title={marketTitle(row)}>
                {row.market ? (
                  <>
                    {price(row.market.price)} <span className="faint">{row.market.basis === 'mid' ? 'mid' : 'LTP'}</span>
                  </>
                ) : (
                  <span className="faint">no price</span>
                )}
              </td>
              <td
                className="r mono sb-legs__iv"
                data-label="IV"
                title={row.iv?.source === 'price' ? 'The chain has no IV for this strike: solved here from its market price.' : undefined}
              >
                {row.iv ? (
                  <>
                    {(row.iv.iv * 100).toFixed(1)}% <span className="faint">{row.iv.source === 'chain' ? 'chain' : 'from price'}</span>
                  </>
                ) : (
                  <span className="warn">none</span>
                )}
              </td>
              <td className={`r mono sb-legs__premium ${premium != null && premium > 0 ? 'pos' : premium != null && premium < 0 ? 'neg' : ''}`} data-label="Premium">
                {premium != null ? formatInrSigned(premium) : '—'}
              </td>
              <td className="sb-legs__remove">
                <button type="button" className="btn btn--ghost btn--sm" aria-label={`Remove ${strikeText(leg.strike)} ${leg.kind}`} onClick={() => onRemove(leg.id)}>
                  ×
                </button>
              </td>
            </tr>
          )
        })}
      </tbody>
    </table>
  )
}

// --- the chain to add from -----------------------------------------------------------------

function ChainPicker({
  chain,
  strikes,
  atm,
  all,
  onAdd,
}: {
  chain: OptionChain
  strikes: readonly number[]
  atm: number | null
  all: boolean
  onAdd: (kind: OptionKind, side: LegSide, strike: number) => void
}) {
  const { start, end } = strikeWindow(strikes, atm, all ? 'all' : 10)
  const shown = strikes.slice(start, end)
  const actions = (kind: OptionKind, strike: number, available: boolean) => (
    <span className="sb-pick__acts">
      {(['buy', 'sell'] as const).map((side) => (
        <button
          key={side}
          type="button"
          className={`sb-pick__btn sb-pick__btn--${side}`}
          disabled={!available}
          aria-label={`${side === 'buy' ? 'Buy' : 'Sell'} ${strikeText(strike)} ${kind}`}
          title={available ? `${side === 'buy' ? 'Buy' : 'Sell'} one lot of the ${strikeText(strike)} ${kind}` : `The chain lists no ${strikeText(strike)} ${kind}`}
          onClick={() => onAdd(kind, side, strike)}
        >
          {side === 'buy' ? 'B' : 'S'}
        </button>
      ))}
    </span>
  )
  return (
    <div className="tablewrap sb-pick-wrap">
      <table className="table sb-pick">
        <thead>
          <tr>
            <th className="r">CE</th>
            <th />
            <th className="c">Strike</th>
            <th />
            <th>PE</th>
          </tr>
        </thead>
        <tbody>
          {shown.map((strike) => {
            const call = chainLeg(chain, strike, 'CE')
            const put = chainLeg(chain, strike, 'PE')
            return (
              <tr key={strike} className={strike === atm ? 'sb-pick__atm' : undefined}>
                <td className="r mono">{price(marketPrice(call)?.price)}</td>
                <td className="c">{actions('CE', strike, call != null)}</td>
                <td className="c sb-pick__strike" title={strike === atm ? 'At the money' : undefined}>
                  {strikeText(strike)}
                </td>
                <td className="c">{actions('PE', strike, put != null)}</td>
                <td className="mono">{price(marketPrice(put)?.price)}</td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}

// --- the builder ------------------------------------------------------------------------------

const NO_WHAT_IF = { move: 0, iv: 0, days: 0 }

function Builder({ chain, header, receivedAt, fetchFailed }: { chain: OptionChain; header: OptionChainHeader; receivedAt: number; fetchFailed: boolean }) {
  const [legs, setLegs] = useState<BuilderLeg[]>([])
  const [whatIf, setWhatIf] = useState(NO_WHAT_IF)
  const [pickAll, setPickAll] = useState(false)
  const nextId = useRef(1)

  const underlying = chain.underlying
  const spot = header.spot?.lastPrice ?? (chain.spotPrice > 0 ? chain.spotPrice : null)
  const lotSize = header.lotSize != null && header.lotSize > 0 ? header.lotSize : null
  // MCX options are written on the future, which costs nothing to hold: Black-76.
  const carry = header.spotIsFuture ? BUILDER_RATE : 0
  const expiry = capturedExpiry(chain.expiryDate) ?? chain.expiryDate.slice(0, 10)
  const closeMs = expiryCloseMs(expiry, header.exchange)
  // Counted from the moment the chain answered for, on the server's clock.
  const days = daysToExpiry(Date.parse(header.serverUtc), closeMs)
  const years = days / 365
  const strikes = useMemo(() => chain.strikes.map((s) => s.strikePrice).sort((a, b) => a - b), [chain.strikes])
  const atm = atmStrike(strikes, chain.atTheMoneyStrike, spot)

  const rows: LegRow[] = legs.map((leg) => {
    const quote = chainLeg(chain, leg.strike, leg.kind)
    const market = marketPrice(quote)
    return {
      leg,
      market,
      entry: leg.entry ?? market?.price ?? null,
      iv: spot != null ? legIv(quote, market, leg.kind, leg.strike, spot, years, carry) : null,
      quote: { bid: quote?.bidPrice ?? null, ask: quote?.askPrice ?? null, ltp: quote?.lastTradedPrice ?? null },
    }
  })
  const unpriced = rows.filter((r) => r.entry == null)
  const ready = spot != null && lotSize != null && rows.length > 0 && unpriced.length === 0
  const payoffLegs: PayoffLeg[] = ready ? rows.map((r) => toPayoffLeg(r.leg, lotSize, r.entry!, r.iv?.iv ?? null)) : []

  const now: PricingInputs = { years, rate: BUILDER_RATE, carry }
  const dayStep = 0.25
  const maxDays = Math.floor(days / dayStep) * dayStep
  const ahead = Math.min(whatIf.days, maxDays)
  const target: PricingInputs = { years: Math.max(0, years - ahead / 365), rate: BUILDER_RATE, carry, ivShift: whatIf.iv }
  const missingIv = rows.filter((r) => r.iv == null)
  const priceable = ready && missingIv.length === 0
  const label = curveLabel(ahead, whatIf.iv)
  const neutral = whatIf.move === 0 && whatIf.iv === 0 && ahead === 0
  const curve: BeforeCurve | null = priceable && years > 0 ? { pricing: target, label } : null

  const bes = ready ? breakevens(payoffLegs) : []
  const ext = ready ? extremes(payoffLegs) : null
  const premium = ready ? netPremium(payoffLegs) : 0
  const greeks = priceable ? netGreeks(payoffLegs, spot, now) : null
  const whatIfSpot = spot != null ? spot * (1 + whatIf.move / 100) : null
  const whatIfPnl = curve && whatIfSpot != null ? pnlBefore(payoffLegs, whatIfSpot, target) : null
  const whatIfAtExpiry = ready && whatIfSpot != null ? expiryPnl(payoffLegs, whatIfSpot) : null
  // Legs entered at the market should read zero at the spot today; what they read instead is the model's own error.
  const modelGap = curve && rows.every((r) => r.leg.entry == null) ? pnlBefore(payoffLegs, spot!, now) : null
  const highest = Math.max(...legs.map((l) => l.strike))

  const newId = () => `leg-${nextId.current++}`
  const applyPreset = (key: PresetKey) => {
    const made = presetLegs(key, strikes, atm)
    if (made) setLegs(made.map((l) => ({ ...l, id: newId() })))
  }
  const add = (kind: OptionKind, side: LegSide, strike: number) => {
    const id = newId()
    setLegs((current) => addLeg(current, { kind, side, strike }, id))
  }
  const change = (id: string, patch: Partial<BuilderLeg>) => setLegs((current) => current.map((l) => (l.id === id ? { ...l, ...patch } : l)))
  const remove = (id: string) => setLegs((current) => current.filter((l) => l.id !== id))

  const todayIso = istDate(Date.parse(header.serverUtc))
  const closeText = istTime(new Date(closeMs).toISOString())

  let summary: ReactNode = null
  if (ready && ext) {
    const fromSpot = bes.map((b) => signedPercent(((b - spot) / spot) * 100, 1)).join(' · ')
    summary = (
      <div className="stat-grid stat-grid--pair sb-stats">
        <StatTile
          label={premium > 0 ? 'Net credit' : premium < 0 ? 'Net debit' : 'Net premium'}
          value={formatInrWhole(Math.abs(premium))}
          tone={premium > 0 ? 'pos' : premium < 0 ? 'neg' : undefined}
          sub={`${premium > 0 ? 'received' : premium < 0 ? 'paid' : 'nothing changes hands'} · lot size ${formatNumber(lotSize)}`}
        />
        <ExtremeTile label="Max profit" extreme={ext.maxProfit} underlying={underlying} highest={highest} kind="profit" />
        <ExtremeTile label="Max loss" extreme={ext.maxLoss} underlying={underlying} highest={highest} kind="loss" />
        <StatTile
          label={bes.length === 1 ? 'Breakeven' : 'Breakevens'}
          value={<span className={bes.length > 2 ? 'sb-stats__long' : undefined}>{bes.length > 0 ? bes.map((b) => strikeText(Math.round(b * 100) / 100)).join(' · ') : 'None'}</span>}
          sub={
            bes.length > 0
              ? `${fromSpot} from spot`
              : ext.maxLoss !== 'unlimited' && ext.maxLoss.value >= 0
                ? 'never loses at expiry'
                : 'loses at every price at expiry'
          }
        />
        <StatTile label="Margin" value="Not computed" sub="the broker's SPAN + exposure decides it: check there before entering" />
      </div>
    )
  }

  return (
    <>
      <p className="sb-meta">
        <b>{underlying}</b> <span className="mono">{price(spot)}</span>
        {header.spotIsFuture && <span className="faint"> (future)</span>}
        <span className="faint"> · </span>expiry {expiryLabel(expiry, todayIso)}
        <span className="faint"> · </span>
        {timeLeftLabel(days)} to {closeText} IST
        <span className="faint"> · </span>lot {lotSize != null ? formatNumber(lotSize) : '—'}
      </p>
      <FreshnessLine header={header} receivedAt={receivedAt} fetchFailed={fetchFailed} />
      {spot == null && <p className="small-note warn">No price for {underlying} in this answer: nothing can be worked out until one arrives.</p>}
      {lotSize == null && (
        <p className="small-note warn">
          The chain names no lot size for {underlying}, so no rupee figure can be worked out. It comes from the instrument master; the
          Instruments page shows whether it has the contract.
        </p>
      )}

      {summary}

      <div className="sb-main">
        <Panel
          className="sb-chart-panel"
          title="Payoff"
          actions={
            <div className="sb-presets" role="group" aria-label="Presets">
              {PRESETS.map((p) => {
                const available = presetLegs(p.key, strikes, atm) != null
                return (
                  <button
                    key={p.key}
                    type="button"
                    className="btn btn--sm"
                    disabled={!available}
                    title={available ? `${p.hint}. Replaces the legs.` : 'The chain does not list every strike this needs.'}
                    onClick={() => applyPreset(p.key)}
                  >
                    {p.label}
                  </button>
                )
              })}
            </div>
          }
        >
          {!ready ? (
            <EmptyState>
              {legs.length === 0
                ? `Start from a preset, or add legs from the chain: B buys a lot, S sells one. Nothing here places an order.`
                : `Type an entry price for ${unpriced.map((r) => `${strikeText(r.leg.strike)} ${r.leg.kind}`).join(', ')}: the chain has no price for it.`}
            </EmptyState>
          ) : (
            <>
              <div className="chip-row sb-legend">
                <span className="oi-legend">
                  <span className="sb-swatch sb-swatch--expiry" />
                  At expiry
                </span>
                {curve && (
                  <span className="oi-legend">
                    <span className="sb-swatch sb-swatch--before" />
                    {curve.label} (Black-Scholes)
                  </span>
                )}
                {curve && (
                  <span className="oi-legend">
                    <span className="sb-swatch sb-swatch--point" />
                    What-if point
                  </span>
                )}
                <span className="oi-legend faint">P&amp;L in ₹ against {underlying}</span>
              </div>
              <PayoffChart
                legs={payoffLegs}
                spot={spot}
                underlying={underlying}
                breakevens={bes}
                curve={curve}
                whatIf={curve && whatIfSpot != null && whatIfPnl != null ? { price: whatIfSpot, pnl: whatIfPnl } : null}
              />
              {missingIv.length > 0 && (
                <p className="small-note warn">
                  No curve before expiry and no greeks: {missingIv.map((r) => `${strikeText(r.leg.strike)} ${r.leg.kind}`).join(', ')} has no IV in
                  the chain and none can be solved from its price. The expiry line needs no IV and is exact.
                </p>
              )}

              <div className="sb-whatif">
                <Slider
                  label="Spot move"
                  value={whatIf.move}
                  min={-10}
                  max={10}
                  step={0.1}
                  text={`${signedPercent(whatIf.move, 1)} · ${price(whatIfSpot, 0)}`}
                  onChange={(move) => setWhatIf((w) => ({ ...w, move }))}
                />
                <Slider
                  label="IV change"
                  value={whatIf.iv}
                  min={-10}
                  max={10}
                  step={0.5}
                  text={`${signed(whatIf.iv, whatIf.iv % 1 === 0 ? 0 : 1)} pts`}
                  onChange={(iv) => setWhatIf((w) => ({ ...w, iv }))}
                />
                <Slider
                  label="Days forward"
                  value={ahead}
                  min={0}
                  max={maxDays}
                  step={dayStep}
                  disabled={maxDays === 0}
                  text={ahead === 0 ? 'today' : `+${ahead.toLocaleString('en-IN')} d`}
                  onChange={(d) => setWhatIf((w) => ({ ...w, days: d }))}
                />
                <button type="button" className="btn btn--ghost btn--sm sb-whatif__reset" disabled={neutral} onClick={() => setWhatIf(NO_WHAT_IF)}>
                  Reset
                </button>
              </div>
              <p className="sb-whatif__read">
                {curve && whatIfPnl != null ? (
                  <>
                    At {underlying} {price(whatIfSpot, 0)}
                    {whatIf.move !== 0 && ` (${signedPercent(whatIf.move, 1)})`} · {label}:{' '}
                    <b className={whatIfPnl >= 0.5 ? 'pos' : whatIfPnl <= -0.5 ? 'neg' : ''}>{formatInrSigned(whatIfPnl)}</b>
                    <span className="faint"> · at expiry at that price {formatInrSigned(whatIfAtExpiry)}</span>
                  </>
                ) : (
                  <>
                    At expiry at {underlying} {price(whatIfSpot, 0)}: <b className={whatIfAtExpiry! >= 0.5 ? 'pos' : whatIfAtExpiry! <= -0.5 ? 'neg' : ''}>{formatInrSigned(whatIfAtExpiry)}</b>
                  </>
                )}
              </p>
              {modelGap != null && neutral && Math.abs(modelGap) >= 1 && (
                <p className="small-note">
                  Today at the spot reads {formatInrSigned(modelGap)}, not zero: at the chain's IVs the model prices the legs that far from
                  their market prices.
                </p>
              )}

              {greeks && (
                <div className="sb-greeks" aria-label="Net greeks now">
                  <Greek symbol="Δ Delta" value={signed(greeks.delta, 1)} unit="₹ per point" title="Rupees made for a one-point rise in the underlying, now: the units of it the position is long (or short, below zero)." />
                  <Greek symbol="Γ Gamma" value={signed(greeks.gamma, Math.abs(greeks.gamma) < 1 ? 3 : 1)} unit="Δ per point" title="How far delta moves for a one-point rise." />
                  <Greek symbol="Θ Theta" value={formatInrSigned(greeks.thetaPerDay)} unit="per day" title="Rupees a calendar day of time is worth to the position, everything else held." />
                  <Greek symbol="Vega" value={formatInrSigned(greeks.vegaPerPoint)} unit="per IV point" title="Rupees for every leg's IV one point higher." />
                </div>
              )}
            </>
          )}
        </Panel>

        <Panel
          className="sb-pick-panel"
          title="Add from the chain"
          actions={
            <div className="oc-seg" role="group" aria-label="Strikes shown">
              <button type="button" aria-pressed={!pickAll} onClick={() => setPickAll(false)}>±10</button>
              <button type="button" aria-pressed={pickAll} onClick={() => setPickAll(true)}>All</button>
            </div>
          }
        >
          <ChainPicker chain={chain} strikes={strikes} atm={atm} all={pickAll} onAdd={add} />
          <p className="small-note">Prices per unit: the middle of the bid and ask, else the last trade.</p>
        </Panel>

        <Panel
          className="sb-legs-panel"
          title={legs.length > 0 ? `Legs · ${legs.length}` : 'Legs'}
          actions={
            legs.length > 0 ? (
              <button type="button" className="btn btn--ghost btn--sm" onClick={() => setLegs([])}>
                Clear
              </button>
            ) : undefined
          }
        >
          {legs.length === 0 ? (
            <EmptyState>No legs yet.</EmptyState>
          ) : (
            <div className="tablewrap">
              <LegsTable rows={rows} strikes={strikes} atm={atm} lotSize={lotSize} onChange={change} onRemove={remove} />
            </div>
          )}
        </Panel>
      </div>

      <p className="small-note sb-notes">
        Analysis only: nothing on this page places an order. Margin is not computed. The expiry line is exact. Before expiry each leg is
        priced by Black-Scholes at its IV from the chain (solved from its price where the chain has none), at a {BUILDER_RATE * 100}% rate,
        the one the desk's live greeks use, counting calendar days to {closeText} IST on expiry day
        {header.spotIsFuture ? ', as an option on the future (Black-76)' : ''}. Charges, slippage and early exercise are left out.
      </p>
    </>
  )
}

// --- the page -------------------------------------------------------------------------------

export function StrategyBuilderPage() {
  const [params, setParams] = useSearchParams()
  const known = useFnoUnderlyings()
  const names = useMemo(() => known.data?.map((u) => u.underlying), [known.data])
  const underlying = chainUnderlying(params.get('u'), names)
  const row: readonly string[] = (UNDERLYINGS as readonly string[]).includes(underlying) ? UNDERLYINGS : [...UNDERLYINGS, underlying]
  const undsRef = useRef<HTMLDivElement>(null)

  const view = useOptionChainView(underlying)
  // keepPreviousData must never price one underlying's legs off another's chain.
  const chain = view.data && view.data.underlying === underlying ? view.data : undefined
  const header = chain?.header ?? null

  // On a phone the row scrolls: a link to CRUDEOIL must not open with CRUDEOIL out of sight.
  useEffect(() => {
    const el = undsRef.current
    const on = el?.querySelector<HTMLElement>('[aria-pressed="true"]')
    if (!el || !on) return
    if (on.offsetLeft < el.scrollLeft || on.offsetLeft + on.offsetWidth > el.scrollLeft + el.clientWidth) {
      el.scrollLeft = Math.max(0, on.offsetLeft - 24)
    }
  }, [underlying])

  const pick = (name: string) => {
    const next = new URLSearchParams(params)
    next.set('u', name)
    setParams(next, { replace: true })
  }

  return (
    <div className="page sb">
      <div className="sb-head">
        <div className="oc-seg sb-unds scroll-x" role="group" aria-label="Underlying" ref={undsRef}>
          {row.map((name) => (
            <button key={name} type="button" aria-pressed={underlying === name} onClick={() => pick(name)}>
              {name}
            </button>
          ))}
        </div>
      </div>

      {view.isError && !chain && <InlineError error={view.error} />}
      {!chain && !view.isError && <Loading label={`Loading the ${underlying} chain…`} />}
      {chain && (!header || chain.strikes.length === 0) && (
        <EmptyState>
          No chain has been captured for {underlying} yet. The chain recorder captures each underlying once a minute while its exchange is
          open; the builder prices off its newest capture.
        </EmptyState>
      )}
      {chain && header && chain.strikes.length > 0 && (
        // Keyed by the underlying and the expiry: a strike on one chain means nothing on another.
        <Builder key={`${underlying}|${chain.expiryDate}`} chain={chain} header={header} receivedAt={view.dataUpdatedAt} fetchFailed={view.isError} />
      )}
    </div>
  )
}
