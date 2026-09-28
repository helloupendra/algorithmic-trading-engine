/**
 * Markets → Commodity: the MCX futures desk in one screen.
 *
 * One tile per near-month future — the pulse's own tile, so a contract reads
 * the same here as on the Watchlist (name, price, change, the day's range,
 * the quote's age) — then the contracts table with the open, high, low,
 * previous close and volume a tile does not carry.
 *
 * Which commodities: the pulse's commodity group, in its order, each with
 * its mini contract beside it when the instrument master lists one (GOLDM
 * beside GOLD, CRUDEOILM beside CRUDEOIL); and the desk's usual four until
 * the pulse answers, or for a root it does not carry. A commodity added to
 * the pulse on the server appears here without a change in this file.
 *
 * The contract is resolved, not hardcoded. MCX expiries roll every month, so
 * a pinned symbol would quietly go stale and then dead: the page asks the
 * instrument master for a root's futures and takes the nearest expiry that
 * has not passed. On 7 Sep 2026 that is GOLD26OCTFUT; in November it will be
 * a different symbol without anyone editing this file.
 *
 * There is no "market open" badge here on purpose. MCX runs to 23:30 IST while
 * NSE closes at 15:30, and the API's session endpoint only models NSE — asking
 * it about MCX returns nulls. A badge built on that would be wrong for eight
 * hours of every trading day, so the page shows quote age instead: "0.3s ago"
 * says the feed is live far more honestly than a rule that does not know this
 * exchange exists.
 *
 * The contracts' prices are pushed (lib/live.ts) and laid over the latest
 * quotes, which are still polled for the open, the previous close and the
 * volume a tick does not carry.
 */

import { useMemo } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { useQueries } from '@tanstack/react-query'
import { api } from '../../lib/api'
import { useAddWatchlistSymbol, useLatestQuotes, useMarketPulse, useWatchlist } from '../../lib/queries'
import { useLivePrices } from '../../lib/live'
import { answerAsOf } from '../../lib/asOf'
import { quotesWithTicks } from '../../lib/liveMarks'
import { formatAge, formatPrice } from '../../lib/format'
import { Badge, FlashPrice, InlineError, Loading, Panel } from '../../components/ui'
import { BigTile } from '../../components/MarketPulse'
import { IconPlus } from '../../components/icons'
import type { Instrument, LiveQuote, MarketPulseItem } from '../../lib/types'

/* --------------------------------------------------------------- contracts */

interface CommodityRoot {
  /** The MCX root as it appears in the symbol, e.g. GOLD in MCX:GOLD26OCTFUT. */
  root: string
  label: string
  /** Grouped so the mini sits under its full-size contract. */
  group: string
}

/** The desk's usual four: shown until the pulse answers, and for a root the pulse does not carry. */
const FALLBACK: CommodityRoot[] = [
  { root: 'GOLD', label: 'Gold', group: 'Gold' },
  { root: 'SILVER', label: 'Silver', group: 'Silver' },
  { root: 'CRUDEOIL', label: 'Crude Oil', group: 'Energy' },
  { root: 'NATURALGAS', label: 'Natural Gas', group: 'Energy' },
]

/**
 * MCX:GOLDM26OCTFUT → GOLDM.
 *
 * Matters because a search for "MCX:GOLD" also returns GOLDM, GOLDGUINEA,
 * GOLDPETAL and GOLDTEN; without an exact root match the Gold card would show
 * whichever of those happened to sort first.
 */
const SYMBOL_SHAPE = /^MCX:([A-Z]+?)(\d{2})([A-Z]{3})FUT$/

function rootOf(symbol: string): string | null {
  return SYMBOL_SHAPE.exec(symbol)?.[1] ?? null
}

/** The full-size roots to show: the pulse's commodities in its order, then the fallback's it lacks. */
function commodityRoots(pulseItems: MarketPulseItem[] | undefined): CommodityRoot[] {
  const out: CommodityRoot[] = []
  const seen = new Set<string>()
  for (const item of pulseItems ?? []) {
    const root = rootOf(item.symbol)
    if (!root || seen.has(root)) continue
    seen.add(root)
    const known = FALLBACK.find((f) => f.root === root)
    out.push(known ?? { root, label: item.name, group: item.name })
  }
  for (const f of FALLBACK) {
    if (seen.has(f.root)) continue
    seen.add(f.root)
    out.push(f)
  }
  return out
}

/** The nearest future of this root whose expiry has not passed. */
function nearMonth(rows: Instrument[] | undefined, root: string): Instrument | null {
  if (!rows?.length) return null
  const today = new Date().toISOString().slice(0, 10)
  const candidates = rows
    .filter((r) => rootOf(r.symbol) === root)
    .filter((r) => !!r.expiryDate && r.expiryDate.slice(0, 10) >= today)
    .sort((a, b) => (a.expiryDate! < b.expiryDate! ? -1 : 1))
  return candidates[0] ?? null
}

/**
 * How old the PRICE is, not how old our copy of it is.
 *
 * These differ on anything that trades in bursts: measured on 2026-09-07, MCX
 * gold futures had been written 1s earlier and last traded 211s earlier, while
 * gold mini had traded 4s earlier. Reading the write time would say every
 * contract is a second old, which is true and useless; reading the exchange
 * stamp says a quiet contract has not printed in three minutes, which is the
 * thing a desk needs to know before trusting the number.
 *
 * Falls back to the write time when the vendor sends no stamp.
 */
function priceAgeSource(quote: LiveQuote | undefined): string | undefined {
  if (!quote) return undefined
  return quote.exchangeTimestampUtc ?? quote.updatedUtc
}

function changePct(quote: LiveQuote | undefined): number | null {
  if (!quote || quote.lastTradedPrice == null || quote.close == null || quote.close === 0)
    return null
  return ((quote.lastTradedPrice - quote.close) / quote.close) * 100
}

/** "05 Oct" from an ISO date, for the table's expiry column. */
function expiryLabel(iso: string | null | undefined): string {
  if (!iso) return '—'
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return iso.slice(0, 10)
  return d.toLocaleDateString('en-IN', { day: '2-digit', month: 'short' })
}

/** "Oct 2026" from an ISO date: the contract month, as the pulse prints it on its tiles. */
function contractMonth(iso: string | null | undefined): string | null {
  if (!iso) return null
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return null
  return d.toLocaleDateString('en-IN', { month: 'short', year: 'numeric' })
}

interface Row {
  root: string
  label: string
  group: string
  contract: Instrument | null
  quote: LiveQuote | undefined
  isWatched: boolean
}

/** A row as the pulse's tile reads it. */
function tileItem(r: Row): MarketPulseItem {
  const q = r.quote
  const ltp = q?.lastTradedPrice ?? null
  const close = q?.close ?? null
  const change = ltp != null && close != null ? ltp - close : null
  return {
    symbol: r.contract?.symbol ?? `MCX:${r.root}`,
    name: r.label,
    contract: contractMonth(r.contract?.expiryDate),
    lastTradedPrice: ltp,
    previousClose: close,
    open: q?.open ?? null,
    high: q?.high ?? null,
    low: q?.low ?? null,
    volume: q?.volume ?? null,
    change,
    changePercent: change != null && close ? (change / close) * 100 : null,
    updatedUtc: priceAgeSource(q) ?? null,
    isSubscribed: r.isWatched,
  }
}

/* ------------------------------------------------------------------- page */

export function CommodityPage() {
  const quotes = useLatestQuotes()
  const watchlist = useWatchlist()
  const pulse = useMarketPulse()
  const add = useAddWatchlistSymbol()
  const navigate = useNavigate()

  const roots = useMemo(
    () => commodityRoots(pulse.data?.groups.find((g) => g.key === 'commodity')?.items),
    [pulse.data],
  )

  // One search per full-size root, nearest expiry first, which also lists the
  // root's mini (GOLDM with GOLD). Cached for an hour: the roots rarely change.
  const searches = useQueries({
    queries: roots.map((c) => ({
      queryKey: ['instruments', 'search', `MCX:${c.root}`, 'FUT'],
      queryFn: () =>
        api.get<Instrument[]>(
          `/api/Instruments/search?query=${encodeURIComponent(`MCX:${c.root}`)}&type=FUT`,
        ),
      staleTime: 60 * 60_000,
    })),
  })

  // The full-size contract, then the mini beside it when the master has one.
  const contracts = roots.flatMap((c, i) => {
    const found = searches[i].data
    const full = { ...c, contract: nearMonth(found, c.root) }
    const mini = nearMonth(found, `${c.root}M`)
    return mini ? [full, { root: `${c.root}M`, label: `${c.label} Mini`, group: c.group, contract: mini }] : [full]
  })
  const prices = useLivePrices(contracts.flatMap((c) => (c.contract ? [c.contract.symbol] : [])))
  const quotesAsOf = answerAsOf(quotes)
  const bySymbol = useMemo(
    () => quotesWithTicks(quotes.data, prices, quotesAsOf),
    [quotes.data, prices, quotesAsOf],
  )
  const watched = useMemo(
    () => new Set((watchlist.data ?? []).map((w) => w.symbol)),
    [watchlist.data],
  )

  const rows: Row[] = contracts.map((c) => ({
    ...c,
    quote: c.contract ? bySymbol.get(c.contract.symbol) : undefined,
    isWatched: c.contract ? watched.has(c.contract.symbol) : false,
  }))

  const resolving = searches.some((s) => s.isLoading)
  const unwatched = rows.filter((r) => r.contract && !r.isWatched)
  const firstError = searches.find((s) => s.error)?.error as Error | undefined

  const watch = (symbol: string) => add.mutate({ symbol, dataType: 'symbolUpdate', priority: 40 })

  return (
    <div className="page">
      <header className="page__header">
        <div>
          <h1 className="page__title">Commodity</h1>
          <p className="page__subtitle">
            MCX near-month futures, with the mini contracts beside the full-size ones. The
            contract rolls with the expiry; the age under each price is how long ago that
            contract last traded.
          </p>
        </div>
        {unwatched.length > 0 && (
          <button
            className="btn btn--primary"
            disabled={add.isPending}
            onClick={() => unwatched.forEach((r) => watch(r.contract!.symbol))}
            title="Subscribe every contract on this page to the live feed"
          >
            <IconPlus /> Watch all {unwatched.length}
          </button>
        )}
      </header>

      {firstError && <InlineError error={firstError} />}
      {add.isError && <InlineError error={add.error} />}

      {resolving ? (
        <Loading label="Resolving near-month contracts…" />
      ) : (
        <>
          {/* The pulse's tiles (components/MarketPulse.tsx): as many across as fit, two on a phone. */}
          <div className="pulse__big">
            {rows.map((r) => (
              <BigTile
                key={r.root}
                item={tileItem(r)}
                onOpen={() => r.contract && navigate(`/markets/chart?symbol=${encodeURIComponent(r.contract.symbol)}`)}
              />
            ))}
          </div>

          <Panel
            title="Contracts"
            actions={
              <span className="faint mcx-hint">
                Nearest expiry per commodity, resolved from the instrument master
              </span>
            }
          >
            {/* Price and change beside the name; the contract code and expiry
                (on the tile already) are the desk's columns, hidden on a phone. */}
            <div className="tablewrap">
              <table className="table">
                <thead>
                  <tr>
                    <th>Commodity</th>
                    <th className="r">LTP</th>
                    <th className="r">Change</th>
                    <th className="mcx-desk">Contract</th>
                    <th className="mcx-desk">Expiry</th>
                    <th className="r">Open</th>
                    <th className="r">High</th>
                    <th className="r">Low</th>
                    <th className="r">Prev close</th>
                    <th className="r">Volume</th>
                    <th title="Time since this contract last traded">Last trade</th>
                  </tr>
                </thead>
                <tbody>
                  {rows.map((r) => {
                    const q = r.quote
                    const chg = changePct(q)
                    return (
                      <tr key={r.root}>
                        <td>
                          {r.label} <span className="faint mcx-group">{r.group}</span>
                        </td>
                        <td className="r">
                          <FlashPrice value={q?.lastTradedPrice} />
                        </td>
                        <td className={`r ${chg == null ? 'muted' : chg >= 0 ? 'pos' : 'neg'}`}>
                          {chg == null ? '—' : `${chg >= 0 ? '+' : ''}${chg.toFixed(2)}%`}
                        </td>
                        <td className="mono mcx-desk">{r.contract?.symbol ?? '—'}</td>
                        <td className="muted mcx-desk">{expiryLabel(r.contract?.expiryDate)}</td>
                        <td className="r mono">{q?.open == null ? '—' : formatPrice(q.open)}</td>
                        <td className="r mono">{q?.high == null ? '—' : formatPrice(q.high)}</td>
                        <td className="r mono">{q?.low == null ? '—' : formatPrice(q.low)}</td>
                        <td className="r mono">{q?.close == null ? '—' : formatPrice(q.close)}</td>
                        <td className="r mono">
                          {q?.volume == null ? '—' : q.volume.toLocaleString('en-IN')}
                        </td>
                        <td>
                          {!r.contract ? (
                            <Badge>none</Badge>
                          ) : q ? (
                            <span className="faint">{formatAge(priceAgeSource(q))}</span>
                          ) : r.isWatched ? (
                            <Badge tone="accent">subscribed</Badge>
                          ) : (
                            <button
                              type="button"
                              className="btn btn--ghost btn--sm"
                              disabled={add.isPending}
                              onClick={() => watch(r.contract!.symbol)}
                              title="Subscribe this contract to the live feed"
                            >
                              Watch
                            </button>
                          )}
                        </td>
                      </tr>
                    )
                  })}
                </tbody>
              </table>
            </div>
            <p className="faint mcx-note">
              MCX trades late into the evening, well past the equity close. "Last trade" is
              the exchange's own clock, so a quiet contract can read minutes old while the
              feed is perfectly healthy — the full-size gold and silver futures print far
              less often than their minis. Feed health is the chip in the topbar and{' '}
              <Link to="/data/feeds">Data › Feeds</Link>.
            </p>
          </Panel>
        </>
      )}
    </div>
  )
}
