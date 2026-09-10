/**
 * The market at a glance: the three index levels, the large caps that move
 * them, and the three commodities — the first thing a trader sees.
 *
 * Every number here is the last saved quote from the API. A symbol the feed
 * has not carried yet says so ("no data yet") instead of showing anything.
 */
import { useNavigate } from 'react-router-dom'
import { useMarketPulse } from '../lib/queries'
import { formatAge, formatPrice } from '../lib/format'
import type { MarketPulseItem } from '../lib/types'
import { InlineError } from './ui'
import './market-pulse.css'

const STALE_AFTER_MS = 5 * 60 * 1000

function tone(item: MarketPulseItem): 'pos' | 'neg' | 'flat' {
  if (item.change == null) return 'flat'
  if (item.change > 0) return 'pos'
  if (item.change < 0) return 'neg'
  return 'flat'
}

function changeText(item: MarketPulseItem): string {
  if (item.change == null || item.changePercent == null) return '—'
  const sign = item.change > 0 ? '+' : ''
  return `${sign}${item.change.toFixed(2)} (${sign}${item.changePercent.toFixed(2)}%)`
}

/** Where today's last price sits between the day's low and high, 0–100. */
function rangePosition(item: MarketPulseItem): number | null {
  if (item.lastTradedPrice == null || item.low == null || item.high == null || item.high <= item.low) return null
  const p = ((item.lastTradedPrice - item.low) / (item.high - item.low)) * 100
  return Math.max(0, Math.min(100, p))
}

function isStale(item: MarketPulseItem): boolean {
  return item.updatedUtc != null && Date.now() - new Date(item.updatedUtc).getTime() > STALE_AFTER_MS
}

function Arrow({ t }: { t: 'pos' | 'neg' | 'flat' }) {
  if (t === 'flat') return null
  return (
    <svg viewBox="0 0 10 10" width="9" height="9" aria-hidden="true" className="pulse__arrow">
      {t === 'pos' ? <path d="M5 1.5 9 8H1z" fill="currentColor" /> : <path d="M5 8.5 1 2h8z" fill="currentColor" />}
    </svg>
  )
}

/** A large tile: name, price, change, and the day's range with today's position on it. */
function BigTile({ item, onOpen }: { item: MarketPulseItem; onOpen: () => void }) {
  const t = tone(item)
  const pos = rangePosition(item)
  const noData = item.lastTradedPrice == null
  return (
    <button type="button" className={`pulse-tile pulse-tile--${t}${isStale(item) ? ' is-stale' : ''}`} onClick={onOpen}>
      <div className="pulse-tile__head">
        <span className="pulse-tile__name">{item.name}</span>
        {item.contract && <span className="pulse-tile__contract">{item.contract}</span>}
      </div>
      <div className="pulse-tile__price">{noData ? '—' : formatPrice(item.lastTradedPrice)}</div>
      <div className={`pulse-tile__change pulse-tile__change--${t}`}>
        <Arrow t={t} />
        {changeText(item)}
      </div>
      {pos != null ? (
        <div className="pulse-range" title={`Day range ${formatPrice(item.low)} – ${formatPrice(item.high)}`}>
          <span className="pulse-range__lo">{formatPrice(item.low)}</span>
          <span className="pulse-range__bar">
            <span className={`pulse-range__dot pulse-range__dot--${t}`} style={{ left: `${pos}%` }} />
          </span>
          <span className="pulse-range__hi">{formatPrice(item.high)}</span>
        </div>
      ) : (
        <div className="pulse-range pulse-range--empty">{noData ? (item.isSubscribed ? 'no data yet' : 'not on the feed') : 'day range —'}</div>
      )}
      <div className="pulse-tile__age">{item.updatedUtc ? formatAge(item.updatedUtc) : ''}</div>
    </button>
  )
}

/** A compact tile for the large-cap grid: name, price, percent. */
function SmallTile({ item, onOpen }: { item: MarketPulseItem; onOpen: () => void }) {
  const t = tone(item)
  const noData = item.lastTradedPrice == null
  return (
    <button type="button" className={`pulse-chip pulse-chip--${t}${isStale(item) ? ' is-stale' : ''}`} onClick={onOpen} title={item.symbol}>
      <span className="pulse-chip__name">{item.name}</span>
      <span className="pulse-chip__price">{noData ? '—' : formatPrice(item.lastTradedPrice)}</span>
      <span className={`pulse-chip__pct pulse-chip__pct--${t}`}>
        <Arrow t={t} />
        {item.changePercent == null ? (noData ? (item.isSubscribed ? 'no data' : 'not on feed') : '—') : `${item.changePercent > 0 ? '+' : ''}${item.changePercent.toFixed(2)}%`}
      </span>
    </button>
  )
}

export function MarketPulse() {
  const pulse = useMarketPulse()
  const navigate = useNavigate()
  const open = (symbol: string) => navigate(`/trader/charts?symbol=${encodeURIComponent(symbol)}`)

  if (pulse.isError) return <InlineError error={pulse.error} />
  const groups = pulse.data?.groups ?? []
  const index = groups.find((g) => g.key === 'index')
  const equity = groups.find((g) => g.key === 'equity')
  const commodity = groups.find((g) => g.key === 'commodity')

  return (
    <section className={`pulse${pulse.data ? '' : ' pulse--loading'}`} aria-label="Market pulse">
      <div className="pulse__row">
        <div className="pulse__group pulse__group--index">
          <h2 className="pulse__title">Indices</h2>
          <div className="pulse__big">
            {(index?.items ?? placeholders(3)).map((it) => (
              <BigTile key={it.symbol} item={it} onOpen={() => open(it.symbol)} />
            ))}
          </div>
        </div>
        <div className="pulse__group pulse__group--commodity">
          <h2 className="pulse__title">Commodities <span className="pulse__hint">MCX, nearest contract</span></h2>
          <div className="pulse__big">
            {(commodity?.items ?? placeholders(3)).map((it) => (
              <BigTile key={it.symbol} item={it} onOpen={() => open(it.symbol)} />
            ))}
          </div>
        </div>
      </div>
      <div className="pulse__group">
        <h2 className="pulse__title">Large caps <span className="pulse__hint">the weight of NIFTY 50, BANK NIFTY and SENSEX</span></h2>
        <div className="pulse__grid">
          {(equity?.items ?? placeholders(12)).map((it) => (
            <SmallTile key={it.symbol} item={it} onOpen={() => open(it.symbol)} />
          ))}
        </div>
      </div>
    </section>
  )
}

/** Empty tiles while the first response is on its way, so the page does not jump. */
function placeholders(n: number): MarketPulseItem[] {
  return Array.from({ length: n }, (_, i) => ({
    symbol: `placeholder-${i}`,
    name: '',
    contract: null,
    lastTradedPrice: null,
    previousClose: null,
    open: null,
    high: null,
    low: null,
    volume: null,
    change: null,
    changePercent: null,
    updatedUtc: null,
    isSubscribed: true,
  }))
}
