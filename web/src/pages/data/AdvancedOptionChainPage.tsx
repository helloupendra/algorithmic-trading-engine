/**
 * The advanced option chain — one expiry of one underlying, read the way a
 * trader reads it: where the price is, what every strike costs, how much is
 * written on it and how that moved today.
 *
 * Two layers, and the page always says which it is showing. The Dhan recorder
 * captures the whole chain once a minute (with IV and greeks); the Dhan feed
 * streams a second-by-second quote for the index, its futures and the strikes
 * around the money. The API overlays every fresh quote on the newest capture,
 * so the ATM rows move with the market while the far strikes wait for the next
 * capture — and a leg carrying a live quote has a teal dot beside its LTP.
 *
 * The spot line is drawn between the two strikes the price lies between, the
 * in-the-money half of each side is shaded, and the ATM row is highlighted:
 * the page is meant to be readable at a glance before it is read number by
 * number.
 */

import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useOptionChainExpiries, useOptionChainTrend, useOptionChainView } from '../../lib/queries'
import type { OptionChain, OptionChainHeader, OptionChainLeg, OptionChainQuote, OptionChainStrike } from '../../lib/types'
import { EmptyState, InlineError, Loading, Panel } from '../../components/ui'
import {
  BUILD_UP,
  UNDERLYINGS,
  arrowPercent,
  columnPeak,
  compactIndian,
  compactSigned,
  describeFreshness,
  expiryLabel,
  isCallItm,
  isPutItm,
  istDate,
  istStamp,
  istTime,
  price,
  sidePeak,
  signedPercent,
  sourceLabel,
  spotMarkerIndex,
  strikeWindow,
  tone,
  type WindowSize,
} from '../../lib/optionChain'
import { OptionChainAnalysis } from './OptionChainAnalysis'

// --- columns ----------------------------------------------------------------------

type ColumnKey = 'analysis' | 'oi' | 'oiChg' | 'volume' | 'iv' | 'delta' | 'gamma' | 'theta' | 'vega' | 'bid' | 'ask' | 'ltp'

interface Toggles {
  greeks: boolean
  bidAsk: boolean
  perLot: boolean
}

/** Call-side columns, outermost first; the put side is the mirror. */
function callColumns(t: Toggles): ColumnKey[] {
  return [
    'analysis',
    'oi',
    'oiChg',
    'volume',
    'iv',
    ...(t.greeks ? (['delta', 'gamma', 'theta', 'vega'] as const) : []),
    ...(t.bidAsk ? (['bid', 'ask'] as const) : []),
    'ltp',
  ]
}

function columnTitle(key: ColumnKey, t: Toggles, lotSize: number | null): { label: string; hint: string } {
  switch (key) {
    case 'analysis': return { label: 'Analysis', hint: 'Build-up: what price and OI did together today (legend below the chain)' }
    case 'oi': return { label: 'OI', hint: 'Open interest — contracts outstanding. Bar scaled to the largest OI on either side' }
    case 'oiChg': return { label: 'OI chg', hint: "Change in OI since the previous day's close, and as a percentage" }
    case 'volume': return { label: 'Volume', hint: 'Contracts traded today' }
    case 'iv': return { label: 'IV', hint: 'Implied volatility, % (from the per-minute capture)' }
    case 'delta': return { label: 'Δ', hint: 'Delta (per-minute capture)' }
    case 'gamma': return { label: 'Γ', hint: 'Gamma (per-minute capture)' }
    case 'theta': return { label: 'Θ', hint: 'Theta, per day (per-minute capture)' }
    case 'vega': return { label: 'V', hint: 'Vega (per-minute capture)' }
    case 'bid': return { label: t.perLot ? 'Bid/lot' : 'Bid', hint: t.perLot ? `Best bid × lot size ${lotSize}` : 'Best bid' }
    case 'ask': return { label: t.perLot ? 'Ask/lot' : 'Ask', hint: t.perLot ? `Best ask × lot size ${lotSize}` : 'Best ask' }
    case 'ltp': return {
      label: t.perLot ? '₹/lot' : 'LTP',
      hint: t.perLot ? `Premium for one lot: LTP × ${lotSize}. Change % is the day's move` : "Last traded price and the day's change %",
    }
  }
}

interface Peaks {
  oi: number
  oiChange: number
  volume: number
  callOi: number
  putOi: number
  callVolume: number
  putVolume: number
}

function Bar({ value, peak, side }: { value: number | null | undefined; peak: number; side: 'call' | 'put' }) {
  const share = peak > 0 && value != null ? Math.min(1, Math.abs(value) / peak) : 0
  if (share === 0) return null
  return (
    <span
      className={`oc-bar oc-bar--${side === 'call' ? 'right' : 'left'} oc-bar--${side} ${value != null && value < 0 ? 'oc-bar--negative' : ''}`}
      style={{ width: `${(share * 100).toFixed(1)}%` }}
      aria-hidden="true"
    />
  )
}

function premium(value: number | null | undefined, t: Toggles, lotSize: number | null): string {
  if (value == null) return '—'
  if (t.perLot && lotSize) return `₹${Math.round(value * lotSize).toLocaleString('en-IN')}`
  return price(value)
}

function LegCell({
  column,
  leg,
  side,
  itm,
  peaks,
  toggles,
  lotSize,
}: {
  column: ColumnKey
  leg: OptionChainLeg | null
  side: 'call' | 'put'
  itm: boolean
  peaks: Peaks
  toggles: Toggles
  lotSize: number | null
}) {
  const align = side === 'call' ? 'oc-cell--right' : 'oc-cell--left'
  const base = `oc-cell ${align} ${itm ? 'oc-itm' : ''}`

  if (!leg) return <td className={base}><span className="faint">—</span></td>

  switch (column) {
    case 'analysis': {
      const b = BUILD_UP[leg.buildUp]
      return <td className={`${base} oc-analysis ${b?.className ?? ''}`}>{b?.label ?? ''}</td>
    }
    case 'oi': {
      const heaviest = leg.openInterest != null && leg.openInterest > 0
        && leg.openInterest === (side === 'call' ? peaks.callOi : peaks.putOi)
      return (
        <td className={`${base} ${heaviest ? 'oc-peak' : ''}`} title={heaviest ? `Heaviest ${side} OI in the chain` : undefined}>
          <Bar value={leg.openInterest} peak={peaks.oi} side={side} />
          <span className="oc-value">{compactIndian(leg.openInterest)}</span>
        </td>
      )
    }
    case 'oiChg':
      return (
        <td className={base}>
          <span className={`oc-value ${tone(leg.openInterestChange)}`}>{compactSigned(leg.openInterestChange)}</span>
          {leg.openInterestChangePercent != null && (
            <span className={`oc-sub ${tone(leg.openInterestChangePercent)}`}>
              ({Math.abs(leg.openInterestChangePercent) >= 1000
                ? `${leg.openInterestChangePercent > 0 ? '>+' : '<−'}999%`
                : signedPercent(leg.openInterestChangePercent, 1)})
            </span>
          )}
        </td>
      )
    case 'volume': {
      const heaviest = leg.volume != null && leg.volume > 0
        && leg.volume === (side === 'call' ? peaks.callVolume : peaks.putVolume)
      return (
        <td className={`${base} ${heaviest ? 'oc-peak' : ''}`} title={heaviest ? `Most traded ${side} in the chain` : undefined}>
          <Bar value={leg.volume} peak={peaks.volume} side={side} />
          <span className="oc-value">{compactIndian(leg.volume)}</span>
        </td>
      )
    }
    case 'iv':
      return <td className={`${base} muted`}>{leg.impliedVolatility != null && leg.impliedVolatility > 0 ? leg.impliedVolatility.toFixed(1) : '—'}</td>
    case 'delta':
      return <td className={`${base} muted`}>{leg.delta != null ? leg.delta.toFixed(2) : '—'}</td>
    case 'gamma':
      return <td className={`${base} muted`}>{leg.gamma != null ? leg.gamma.toFixed(4) : '—'}</td>
    case 'theta':
      return <td className={`${base} muted`}>{leg.theta != null ? leg.theta.toFixed(1) : '—'}</td>
    case 'vega':
      return <td className={`${base} muted`}>{leg.vega != null ? leg.vega.toFixed(1) : '—'}</td>
    case 'bid':
      return <td className={`${base} muted`}>{premium(leg.bidPrice, toggles, lotSize)}</td>
    case 'ask':
      return <td className={`${base} muted`}>{premium(leg.askPrice, toggles, lotSize)}</td>
    case 'ltp': {
      const dot = leg.isLive ? <span className="oc-live-dot" title={`Live quote, ${istTime(leg.quoteUpdatedUtc, true)} IST`} /> : null
      const change = leg.priceChangePercent != null
        ? <span className={`oc-sub ${tone(leg.priceChangePercent)}`}>({signedPercent(leg.priceChangePercent, 1)})</span>
        : null
      return (
        <td className={`${base} oc-ltp`}>
          {side === 'put' && dot}
          {side === 'call' && change}
          <span className="oc-value"> {premium(leg.lastTradedPrice, toggles, lotSize)} </span>
          {side === 'put' && change}
          {side === 'call' && dot}
        </td>
      )
    }
  }
}

// --- the chain ------------------------------------------------------------------

function ChainTable({
  chain,
  header,
  toggles,
  windowSize,
  scrollKey,
}: {
  chain: OptionChain
  header: OptionChainHeader
  toggles: Toggles
  windowSize: WindowSize
  scrollKey: string
}) {
  const wrapRef = useRef<HTMLDivElement>(null)
  const scrolledFor = useRef<string | null>(null)
  const spot = header.spot?.lastPrice ?? (chain.spotPrice > 0 ? chain.spotPrice : null)
  const lotSize = header.lotSize

  const strikes = chain.strikes
  const { start, end } = useMemo(
    () => strikeWindow(strikes.map((s) => s.strikePrice), chain.atTheMoneyStrike, windowSize),
    [strikes, chain.atTheMoneyStrike, windowSize],
  )
  const shown = strikes.slice(start, end)
  const markerAt = spotMarkerIndex(shown.map((s) => s.strikePrice), spot)

  const peaks = useMemo<Peaks>(
    () => ({
      oi: columnPeak(strikes, 'openInterest'),
      oiChange: columnPeak(strikes, 'openInterestChange'),
      volume: columnPeak(strikes, 'volume'),
      callOi: sidePeak(strikes, 'call', 'openInterest'),
      putOi: sidePeak(strikes, 'put', 'openInterest'),
      callVolume: sidePeak(strikes, 'call', 'volume'),
      putVolume: sidePeak(strikes, 'put', 'volume'),
    }),
    [strikes],
  )

  const calls = callColumns(toggles)
  // The mirror image, except that bid stays before ask on both sides.
  const puts = [...calls].reverse().map((c) => (c === 'bid' ? 'ask' : c === 'ask' ? 'bid' : c))
  const width = calls.length * 2 + 1

  // Bring the ATM row (and the strike column, on a narrow screen) into view
  // once per underlying, expiry and window — never on a poll, which would
  // yank the table away from wherever the reader scrolled to.
  useLayoutEffect(() => {
    const wrap = wrapRef.current
    if (!wrap || scrolledFor.current === scrollKey || shown.length === 0) return
    const row = wrap.querySelector<HTMLElement>('tr[data-atm="true"]') ?? wrap.querySelector<HTMLElement>('tr.oc-spot-row')
    const strikeCell = wrap.querySelector<HTMLElement>('td.oc-strike')
    const box = wrap.getBoundingClientRect()
    if (row) {
      const r = row.getBoundingClientRect()
      wrap.scrollTop += r.top - box.top - (wrap.clientHeight - r.height) / 2
    }
    if (strikeCell && wrap.scrollWidth > wrap.clientWidth) {
      const c = strikeCell.getBoundingClientRect()
      wrap.scrollLeft += c.left - box.left - (wrap.clientWidth - c.width) / 2
    }
    scrolledFor.current = scrollKey
  })

  const spotRow = (
    <tr key="spot" className="oc-spot-row" aria-label="Spot price">
      <td colSpan={calls.length} />
      <td className="oc-spot-cell">
        <span className={`oc-spot-pill ${header.spot?.isLive ? '' : 'oc-spot-pill--static'}`}>
          {chain.underlying} {price(spot)} <span className="oc-spot-chg">{arrowPercent(header.spot?.changePercent)}</span>
        </span>
      </td>
      <td colSpan={puts.length} />
    </tr>
  )

  const rows: ReactNode[] = []
  shown.forEach((strike: OptionChainStrike, i) => {
    if (markerAt === i) rows.push(spotRow)
    const callItm = isCallItm(strike.strikePrice, spot)
    const putItm = isPutItm(strike.strikePrice, spot)
    rows.push(
      <tr
        key={strike.strikePrice}
        className={`oc-row ${strike.isAtTheMoney ? 'oc-row--atm' : ''}`}
        data-atm={strike.isAtTheMoney ? 'true' : undefined}
      >
        {calls.map((c) => (
          <LegCell key={`c-${c}`} column={c} leg={strike.call} side="call" itm={callItm} peaks={peaks} toggles={toggles} lotSize={lotSize} />
        ))}
        <td className="oc-strike" title={strike.isAtTheMoney ? 'At the money' : undefined}>
          <b>{strike.strikePrice.toLocaleString('en-IN')}</b>
          <span className="oc-pcr" title="Put-call ratio at this strike">
            {strike.putCallRatio != null ? `PCR ${strike.putCallRatio.toFixed(2)}` : ' '}
          </span>
        </td>
        {puts.map((c) => (
          <LegCell key={`p-${c}`} column={c} leg={strike.put} side="put" itm={putItm} peaks={peaks} toggles={toggles} lotSize={lotSize} />
        ))}
      </tr>,
    )
  })
  if (markerAt === shown.length) rows.push(spotRow)

  return (
    <div className="tablewrap oc-wrap" ref={wrapRef}>
      <table className="table oc-table">
        <thead>
          <tr>
            <th colSpan={calls.length} className="oc-side oc-side--call">Calls</th>
            <th className="oc-strike-head">{header.daysToExpiry != null ? `${header.daysToExpiry}d to expiry` : ''}</th>
            <th colSpan={puts.length} className="oc-side oc-side--put">Puts</th>
          </tr>
          <tr>
            {calls.map((c) => {
              const t = columnTitle(c, toggles, lotSize)
              return <th key={`hc-${c}`} className={c === 'analysis' ? '' : 'r'} title={t.hint}>{t.label}</th>
            })}
            <th className="oc-strike-head" title="Strike price, and the put-call ratio of OI at it">Strike</th>
            {puts.map((c) => {
              const t = columnTitle(c, toggles, lotSize)
              return <th key={`hp-${c}`} className={c === 'analysis' ? 'r' : ''} title={t.hint}>{t.label}</th>
            })}
          </tr>
        </thead>
        <tbody>
          {rows.length > 0 ? rows : (
            <tr><td colSpan={width} className="empty">No strikes in this capture.</td></tr>
          )}
        </tbody>
      </table>
    </div>
  )
}

// --- header strip -----------------------------------------------------------------

function Change({ quote }: { quote: OptionChainQuote | null }) {
  if (!quote || quote.change == null) return null
  return (
    <span
      className={`oc-chg ${tone(quote.change)}`}
      title={`vs previous close ${price(quote.previousClose)} (${quote.previousCloseBasis === 'feed' ? "the feed's previous close" : "Dhan's close field"})`}
    >
      {quote.change > 0 ? '+' : quote.change < 0 ? '−' : ''}{price(Math.abs(quote.change))} ({signedPercent(quote.changePercent)})
    </span>
  )
}

/** "live", "last 15:29", or with the day when it is not today's: "last 11 Sep 05:40". */
function quoteAge(quote: OptionChainQuote | null, serverUtc: string): string {
  if (!quote) return ''
  if (quote.isLive) return 'live'
  const sameDay = quote.asOfUtc != null && istDate(Date.parse(quote.asOfUtc)) === istDate(Date.parse(serverUtc))
  const when = sameDay ? istTime(quote.asOfUtc) : istStamp(quote.asOfUtc)
  if (quote.basis === 'snapshot') return `capture ${when}`
  if (quote.basis === 'bar') return `1-min bar to ${when}`
  return `last ${when}`
}

function HeadItem({ label, children, sub, wide, title }: { label: ReactNode; children: ReactNode; sub?: ReactNode; wide?: boolean; title?: string }) {
  return (
    <div className={`oc-head-item ${wide ? 'oc-head-item--wide' : ''}`} title={title}>
      <span className="oc-head-label">{label}</span>
      <span className="oc-head-value">{children}</span>
      {sub != null && <span className="oc-head-sub">{sub}</span>}
    </div>
  )
}

function HeaderStrip({ chain, header }: { chain: OptionChain; header: OptionChainHeader }) {
  const spot = header.spot
  const future = header.future
  const vix = header.vix
  const symbol = spot?.symbol ? spot.symbol.split(':')[1] ?? spot.symbol : chain.underlying

  return (
    <div className="oc-header">
      <HeadItem
        wide
        label={<>{symbol} · {header.exchange}{header.spotIsFuture ? ' · nearest future' : ''}</>}
        sub={header.spotIsFuture && spot?.basis === 'snapshot'
          ? <span className="warn">chain-reported price, not the future's own quote</span>
          : <>{quoteAge(spot, header.serverUtc)}{spot?.sourceKey ? ` · ${sourceLabel(spot.sourceKey)}` : ''}{spot && spot.change == null ? ' · previous close not known' : ''}</>}
        title={header.spotIsFuture ? 'MCX has no spot: the chain is read against the future its options are written on.' : undefined}
      >
        <span className={spot?.isLive ? '' : 'oc-dim'}>{price(spot?.lastPrice)}</span> <Change quote={spot} />
      </HeadItem>

      {!header.spotIsFuture && (
        <HeadItem
          label={<>Future{future?.expiryDate ? ` · ${future.expiryDate.slice(8, 10)} ${new Date(`${future.expiryDate}T00:00:00Z`).toLocaleString('en-IN', { month: 'short', timeZone: 'UTC' })}` : ''}</>}
          sub={future?.premiumOverSpot != null
            ? <>prem {future.premiumOverSpot >= 0 ? '+' : '−'}{price(Math.abs(future.premiumOverSpot))} ({signedPercent(future.premiumPercent)}) · {quoteAge(future, header.serverUtc)}</>
            : future ? quoteAge(future, header.serverUtc) : header.mode === 'replay' ? 'not in replay' : 'no quote'}
        >
          <span className={future?.isLive ? '' : 'oc-dim'}>{price(future?.lastPrice)}</span> <Change quote={future} />
        </HeadItem>
      )}

      <HeadItem
        label="India VIX"
        sub={vix ? quoteAge(vix, header.serverUtc) : header.mode === 'replay' ? 'not in replay' : 'not streamed'}
        title={vix ? undefined : 'No quote for NSE:INDIAVIX-INDEX. Add it to the live watchlist and the Dhan feed will stream it.'}
      >
        <span className={vix?.isLive ? '' : 'oc-dim'}>{price(vix?.lastPrice)}</span> <Change quote={vix} />
      </HeadItem>

      <HeadItem label="PCR" sub={<>of OI chg {header.putCallRatioOfChange != null ? header.putCallRatioOfChange.toFixed(2) : '—'}</>}
        title="Total put OI over total call OI. The second figure is the same ratio on today's OI change, given only when both sides added contracts.">
        {header.putCallRatio != null ? header.putCallRatio.toFixed(2) : '—'}
      </HeadItem>

      <HeadItem label="Max pain" title="The expiry price at which option buyers, in total, would be paid the least.">
        {header.maxPainStrike != null ? header.maxPainStrike.toLocaleString('en-IN') : '—'}
      </HeadItem>

      <HeadItem label="ATM" sub={<>IV {header.atTheMoneyIv != null ? `${header.atTheMoneyIv.toFixed(1)}%` : '—'}</>}
        title="The strike nearest the spot, and the mean implied volatility of its call and put.">
        {header.atTheMoneyStrike != null ? header.atTheMoneyStrike.toLocaleString('en-IN') : '—'}
      </HeadItem>

      <HeadItem wide label="Support · Resistance"
        sub={<>PE {compactIndian(header.supportOpenInterest)} · CE {compactIndian(header.resistanceOpenInterest)}</>}
        title="Support: the strike with the most put OI. Resistance: the strike with the most call OI.">
        <span className="pos">{header.supportStrike != null ? header.supportStrike.toLocaleString('en-IN') : '—'}</span>
        {' · '}
        <span className="neg">{header.resistanceStrike != null ? header.resistanceStrike.toLocaleString('en-IN') : '—'}</span>
      </HeadItem>

      <HeadItem wide label="Total OI · CE vs PE"
        sub={<>chg <span className={tone(header.totalCallOpenInterestChange)}>{compactSigned(header.totalCallOpenInterestChange)}</span> vs <span className={tone(header.totalPutOpenInterestChange)}>{compactSigned(header.totalPutOpenInterestChange)}</span></>}>
        <span className="neg">{compactIndian(header.totalCallOpenInterest)}</span>
        <span className="faint"> vs </span>
        <span className="pos">{compactIndian(header.totalPutOpenInterest)}</span>
      </HeadItem>

      <HeadItem label="Lot" sub={header.lotSizeSource === 'configured' ? 'from config' : header.lotSizeSource === 'master' ? 'from master' : undefined}>
        {header.lotSize ?? '—'}
      </HeadItem>
    </div>
  )
}

// --- freshness ------------------------------------------------------------------

function useNow(intervalMs: number): number {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), intervalMs)
    return () => clearInterval(id)
  }, [intervalMs])
  return now
}

function FreshnessLine({ header, receivedAt, fetchFailed }: { header: OptionChainHeader; receivedAt: number; fetchFailed: boolean }) {
  const now = useNow(1000)
  const serverNow = Date.parse(header.serverUtc) + (now - receivedAt)
  const f = describeFreshness(header, serverNow)
  return (
    <p className={`oc-fresh oc-fresh--${f.tone}`} role="status">
      <span className="oc-fresh__dot" aria-hidden="true" />
      <span>{f.text}</span>
      {fetchFailed && <span className="warn"> · refresh failed, showing the last answer</span>}
    </p>
  )
}

// --- controls -------------------------------------------------------------------

function Toggle({ on, onChange, children, disabled, title }: { on: boolean; onChange: (v: boolean) => void; children: ReactNode; disabled?: boolean; title?: string }) {
  return (
    <button type="button" className="oc-toggle" aria-pressed={on} disabled={disabled} title={title} onClick={() => onChange(!on)}>
      <span className="oc-toggle__box" aria-hidden="true" />
      {children}
    </button>
  )
}

function BuildUpLegend() {
  return (
    <div className="oc-legend">
      {Object.values(BUILD_UP).map((b) => (
        <span key={b.label}>
          <b className={b.className}>{b.label}</b> <span className="muted">{b.meaning}</span>
        </span>
      ))}
      <span className="faint">
        Shaded: in the money. Teal dot: LTP from a live quote. Bold bar: heaviest OI or volume on that side.
      </span>
    </div>
  )
}

/** "10:41" IST on the session day → ISO UTC at the end of that minute. */
function asOfFor(sessionDate: string, time: string): string | undefined {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(sessionDate) || !/^\d{2}:\d{2}$/.test(time)) return undefined
  const ms = Date.parse(`${sessionDate}T${time}:59+05:30`)
  return Number.isNaN(ms) ? undefined : new Date(ms).toISOString()
}

// --- page -----------------------------------------------------------------------

export function AdvancedOptionChainPage({ asOfUtc: asOfProp }: { asOfUtc?: string } = {}) {
  const [params, setParams] = useSearchParams()
  const requested = (params.get('u') ?? 'NIFTY').toUpperCase()
  const underlying = (UNDERLYINGS as readonly string[]).includes(requested) ? requested : 'NIFTY'
  const expiry = params.get('expiry') ?? undefined
  const replayTime = params.get('at') ?? ''
  const [windowSize, setWindowSize] = useState<WindowSize>(20)
  const [toggles, setToggles] = useState<Toggles>({ greeks: false, bidAsk: false, perLot: false })
  const [sessionDate, setSessionDate] = useState<string>('')

  const asOfUtc = asOfProp ?? (replayTime && sessionDate ? asOfFor(sessionDate, replayTime) : undefined)

  const expiries = useOptionChainExpiries(underlying)
  const view = useOptionChainView(underlying, expiry, asOfUtc)
  // keepPreviousData must never show one underlying's chain under another's tab.
  const data = view.data && view.data.underlying === underlying ? view.data : undefined
  const header = data?.header ?? null
  const trend = useOptionChainTrend(underlying, expiry ?? data?.expiryDate, asOfUtc, header?.marketOpen)
  const trendData = trend.data && trend.data.underlying === underlying ? trend.data : undefined

  // The replay clock is a time on the session the chain belongs to.
  const knownSession = trendData?.sessionDate ?? (header?.snapshotCapturedUtc ? istDate(Date.parse(header.snapshotCapturedUtc)) : '')
  useEffect(() => {
    if (knownSession && (!sessionDate || (!replayTime && knownSession !== sessionDate))) setSessionDate(knownSession)
  }, [knownSession, replayTime, sessionDate])

  const setParam = (key: string, value: string | undefined) => {
    const next = new URLSearchParams(params)
    if (value) next.set(key, value)
    else next.delete(key)
    if (key === 'u') {
      next.delete('expiry')
      next.delete('at')
    }
    setParams(next, { replace: true })
  }

  const todayIso = istDate(Date.now())
  const expiryOptions = useMemo(() => {
    const list = (expiries.data ?? []).filter((d) => d >= todayIso)
    const current = data?.expiryDate
    if (current && !list.includes(current)) list.unshift(current)
    return list.sort()
  }, [expiries.data, todayIso, data?.expiryDate])

  const firstCapture = trendData?.points[0]?.capturedUtc
  const lastCapture = trendData?.points[trendData.points.length - 1]?.capturedUtc

  return (
    <div className="page oc-page">
      <header className="page__header">
        <h1 className="page__title">Option chain</h1>
        <p className="page__subtitle">
          Calls left, puts right, strike in the middle. The spot line sits between the two strikes the price is
          between; the in-the-money half of each side is shaded.
        </p>
      </header>

      <div className="oc-tabs" role="tablist" aria-label="Underlying">
        {UNDERLYINGS.map((name) => (
          <button
            key={name}
            type="button"
            role="tab"
            aria-selected={underlying === name}
            className={`oc-tab ${underlying === name ? 'oc-tab--on' : ''}`}
            onClick={() => setParam('u', name)}
          >
            {name}
          </button>
        ))}
      </div>

      <div className="oc-toolbar">
        <label className="oc-control">
          <span className="oc-control__label">Expiry</span>
          <select
            className="field__input field__input--sm"
            value={data?.expiryDate ?? expiry ?? ''}
            onChange={(e) => setParam('expiry', e.target.value)}
            aria-label="Expiry"
            disabled={expiryOptions.length === 0}
          >
            {expiryOptions.length === 0 && <option value="">no expiry captured</option>}
            {expiryOptions.map((d) => (
              <option key={d} value={d}>{expiryLabel(d, todayIso)}</option>
            ))}
          </select>
        </label>

        <div className="oc-seg" role="group" aria-label="Strikes shown">
          {([10, 20, 'all'] as const).map((w) => (
            <button key={w} type="button" aria-pressed={windowSize === w} onClick={() => setWindowSize(w)}>
              {w === 'all' ? 'All' : `±${w}`}
            </button>
          ))}
        </div>

        <Toggle on={toggles.greeks} onChange={(v) => setToggles((t) => ({ ...t, greeks: v }))}>Greeks</Toggle>
        <Toggle on={toggles.bidAsk} onChange={(v) => setToggles((t) => ({ ...t, bidAsk: v }))}>Bid/Ask</Toggle>
        <Toggle
          on={toggles.perLot}
          onChange={(v) => setToggles((t) => ({ ...t, perLot: v }))}
          disabled={!header?.lotSize}
          title="Show premiums for one lot (price × lot size). OI and volume are unchanged."
        >
          Per lot{header?.lotSize ? <span className="faint"> ×{header.lotSize}</span> : null}
        </Toggle>

        <span className="oc-toolbar__spacer" />

        {!asOfProp && (
          <label className="oc-control" title="Replay a capture from this session (IST)">
            <span className="oc-control__label">As of</span>
            <input
              type="time"
              className="field__input field__input--sm oc-time"
              value={replayTime}
              min={firstCapture ? istTime(firstCapture) : undefined}
              max={lastCapture ? istTime(lastCapture) : undefined}
              onChange={(e) => setParam('at', e.target.value || undefined)}
              aria-label="Replay time (IST)"
            />
            {replayTime ? (
              <button type="button" className="btn btn--sm btn--primary" onClick={() => setParam('at', undefined)}>
                Back to live
              </button>
            ) : (
              <span className="faint oc-control__hint">{sessionDate ? `${sessionDate.slice(8, 10)}/${sessionDate.slice(5, 7)}` : ''}</span>
            )}
          </label>
        )}
      </div>

      {view.isError && !data && <InlineError error={view.error} />}
      {!data && view.isPending && <Loading label={`Loading the ${underlying} chain…`} />}
      {!data && !view.isPending && !view.isError && <Loading label={`Loading the ${underlying} chain…`} />}

      {data && header && (
        <>
          <HeaderStrip chain={data} header={header} />
          <FreshnessLine header={header} receivedAt={view.dataUpdatedAt} fetchFailed={view.isError} />

          {data.strikes.length === 0 ? (
            <EmptyState>
              No chain has been captured for {underlying}{expiry ? ` (${expiry})` : ''} yet. The Dhan chain recorder captures each
              underlying once a minute while its exchange is open; this page fills in with the first capture.
            </EmptyState>
          ) : (
            <>
              {data.openInterestUnavailable && (
                <p className="small-note warn">
                  No open interest in this capture — only price and volume were recorded. The OI columns are blank rather than
                  zero.
                </p>
              )}
              <ChainTable
                chain={data}
                header={header}
                toggles={toggles}
                windowSize={windowSize}
                scrollKey={`${underlying}|${data.expiryDate}|${windowSize}|${asOfUtc ? 'r' : 'l'}`}
              />
              <BuildUpLegend />
              <Panel title="OI analysis" className="oc-analysis-panel">
                <OptionChainAnalysis chain={data} header={header} trend={trendData ?? null} trendLoading={trend.isPending} />
              </Panel>
            </>
          )}
        </>
      )}
    </div>
  )
}
