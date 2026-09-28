/**
 * The market at a glance: the index levels, the large caps that move them,
 * and the commodities — the first thing a trader sees.
 *
 * The groups are whatever the API sends, in its order and under its titles,
 * so a fourth index or a new group appears without a change here. A group
 * whose layout is `compact` (the large caps, until the API says so itself)
 * is the small chip grid; every other group is a row of big tiles, and the
 * tile groups share one row across a desk screen.
 *
 * Every number here is the last saved quote from the API. A symbol the feed
 * has not carried yet says so ("no data yet") instead of showing anything.
 * The big tile is also the Commodity page's card, so the same contract reads
 * the same on both pages.
 */
import { useEffect, useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { useMarketPulse } from '../lib/queries'
import { formatAge, formatPrice } from '../lib/format'
import type { MarketPulseGroup, MarketPulseItem } from '../lib/types'
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

/** The chip grid, or the tiles: the API's say, else the large caps are the chips. */
function isCompact(group: MarketPulseGroup): boolean {
  return group.layout ? group.layout === 'compact' : group.key === 'equity'
}

function Arrow({ t }: { t: 'pos' | 'neg' | 'flat' }) {
  if (t === 'flat') return null
  return (
    <svg viewBox="0 0 10 10" width="9" height="9" aria-hidden="true" className="pulse__arrow">
      {t === 'pos' ? <path d="M5 1.5 9 8H1z" fill="currentColor" /> : <path d="M5 8.5 1 2h8z" fill="currentColor" />}
    </svg>
  )
}

/**
 * The console's price flash (flash-up / flash-down for 900 ms) on a moved
 * price, keyed so two moves the same way inside a second both show. The
 * shared FlashPrice sets the mono face, which a tile's price does not use.
 */
function useFlash(value: number | null): { cls: string; seq: number } {
  const prev = useRef<number | null>(null)
  const [flash, setFlash] = useState({ cls: '', seq: 0 })
  useEffect(() => {
    const before = prev.current
    if (value != null) prev.current = value
    if (value == null || before == null || value === before) return
    setFlash((f) => ({ cls: value > before ? 'flash-up' : 'flash-down', seq: f.seq + 1 }))
    const t = setTimeout(() => setFlash((f) => ({ ...f, cls: '' })), 900)
    return () => clearTimeout(t)
  }, [value])
  return flash
}

/** A large tile: name, price, change, and the day's range with today's position on it. */
export function BigTile({ item, onOpen }: { item: MarketPulseItem; onOpen: () => void }) {
  const t = tone(item)
  const pos = rangePosition(item)
  const noData = item.lastTradedPrice == null
  const flash = useFlash(item.lastTradedPrice)
  return (
    <button type="button" className={`pulse-tile pulse-tile--${t}${isStale(item) ? ' is-stale' : ''}`} onClick={onOpen} title={item.symbol}>
      <div className="pulse-tile__head">
        <span className="pulse-tile__name">{item.name}</span>
        {item.contract && <span className="pulse-tile__contract">{item.contract}</span>}
      </div>
      <div key={flash.seq} className={`pulse-tile__price ${flash.cls}`}>{noData ? '—' : formatPrice(item.lastTradedPrice)}</div>
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

function GroupTitle({ group }: { group: MarketPulseGroup }) {
  return (
    <h2 className="pulse__title">
      {group.title}
      {group.hint && <span className="pulse__hint">{group.hint}</span>}
    </h2>
  )
}

export function MarketPulse() {
  const pulse = useMarketPulse()
  const navigate = useNavigate()
  const open = (symbol: string) => navigate(`/markets/chart?symbol=${encodeURIComponent(symbol)}`)

  if (pulse.isError) return <InlineError error={pulse.error} />
  // The tile groups share a row, in the API's order; the chip grids follow, in theirs.
  const groups = pulse.data?.groups ?? SKELETON
  const tiles = groups.filter((g) => !isCompact(g))
  const chips = groups.filter(isCompact)

  return (
    <section className={`pulse${pulse.data ? '' : ' pulse--loading'}`} aria-label="Market pulse">
      {tiles.length > 0 && (
        <div className="pulse__row">
          {tiles.map((g) => (
            <div key={g.key} className="pulse__group">
              <GroupTitle group={g} />
              {g.items.length === 0 ? (
                <p className="pulse__empty">Nothing in this group yet.</p>
              ) : (
                <div className="pulse__big">
                  {g.items.map((it) => (
                    <BigTile key={it.symbol} item={it} onOpen={() => open(it.symbol)} />
                  ))}
                </div>
              )}
            </div>
          ))}
        </div>
      )}
      {chips.map((g) => (
        <div key={g.key} className="pulse__group">
          <GroupTitle group={g} />
          {g.items.length === 0 ? (
            <p className="pulse__empty">Nothing in this group yet.</p>
          ) : (
            <div className="pulse__grid">
              {g.items.map((it) => (
                <SmallTile key={it.symbol} item={it} onOpen={() => open(it.symbol)} />
              ))}
            </div>
          )}
        </div>
      ))}
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

/** The shape of the usual answer, drawn faintly until the real one arrives. */
const SKELETON: MarketPulseGroup[] = [
  { key: 'index', title: 'Indices', items: placeholders(3) },
  { key: 'commodity', title: 'Commodities', items: placeholders(3) },
  { key: 'equity', title: 'Large caps', items: placeholders(12), layout: 'compact' },
]
