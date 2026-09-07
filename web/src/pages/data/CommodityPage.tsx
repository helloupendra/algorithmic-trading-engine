/**
 * Data module — Commodity: the MCX futures desk in one screen.
 *
 * Gold, Silver, Crude and Natural Gas (with the mini contracts alongside the
 * full-size ones), each showing the near-month future's last price, its move
 * from the previous close, the day's range and how old the quote is.
 *
 * The contract is resolved, not hardcoded. MCX expiries roll every month, so a
 * pinned symbol would quietly go stale and then dead: the page asks the
 * instrument master for that root's futures and takes the nearest expiry that
 * has not passed. On 7 Sep 2026 that is GOLD26OCTFUT; in November it will be a
 * different symbol without anyone editing this file.
 *
 * There is no "market open" badge here on purpose. MCX runs to 23:30 IST while
 * NSE closes at 15:30, and the API's session endpoint only models NSE — asking
 * it about MCX returns nulls. A badge built on that would be wrong for eight
 * hours of every trading day, so the page shows quote age instead: "0.3s ago"
 * says the feed is live far more honestly than a rule that does not know this
 * exchange exists.
 */

import { useMemo } from 'react'
import { useQueries } from '@tanstack/react-query'
import { api } from '../../lib/api'
import { useAddWatchlistSymbol, useLatestQuotes, useWatchlist } from '../../lib/queries'
import { formatAge, formatPrice } from '../../lib/format'
import { Badge, FlashPrice, InlineError, Loading, Panel } from '../../components/ui'
import { IconPlus } from '../../components/icons'
import type { Instrument, LiveQuote } from '../../lib/types'

/* --------------------------------------------------------------- contracts */

interface Commodity {
  /** The MCX root as it appears in the symbol, e.g. GOLDM in MCX:GOLDM26OCTFUT. */
  root: string
  label: string
  /** Grouped so the mini sits under its full-size contract. */
  group: string
}

const COMMODITIES: Commodity[] = [
  { root: 'GOLD', label: 'Gold', group: 'Gold' },
  { root: 'GOLDM', label: 'Gold Mini', group: 'Gold' },
  { root: 'SILVER', label: 'Silver', group: 'Silver' },
  { root: 'SILVERM', label: 'Silver Mini', group: 'Silver' },
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

function changePct(quote: LiveQuote | undefined): number | null {
  if (!quote || quote.lastTradedPrice == null || quote.close == null || quote.close === 0)
    return null
  return ((quote.lastTradedPrice - quote.close) / quote.close) * 100
}

/** "05 Oct" from an ISO date, so a card can say which contract it is showing. */
function expiryLabel(iso: string | null | undefined): string {
  if (!iso) return '—'
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return iso.slice(0, 10)
  return d.toLocaleDateString('en-IN', { day: '2-digit', month: 'short' })
}

/* ------------------------------------------------------------------- page */

export function CommodityPage() {
  const quotes = useLatestQuotes()
  const watchlist = useWatchlist()
  const add = useAddWatchlistSymbol()

  // One search per root. They are cached for a minute and the roots never
  // change, so this costs a single round each on the first visit.
  const searches = useQueries({
    queries: COMMODITIES.map((c) => ({
      queryKey: ['instruments', 'search', `MCX:${c.root}`, 'FUT'],
      queryFn: () =>
        api.get<Instrument[]>(
          `/api/Instruments/search?query=${encodeURIComponent(`MCX:${c.root}`)}&type=FUT`,
        ),
      staleTime: 60 * 60_000,
    })),
  })

  const bySymbol = useMemo(
    () => new Map((quotes.data ?? []).map((q) => [q.symbol, q])),
    [quotes.data],
  )
  const watched = useMemo(
    () => new Set((watchlist.data ?? []).map((w) => w.symbol)),
    [watchlist.data],
  )

  const rows = COMMODITIES.map((c, i) => {
    const contract = nearMonth(searches[i].data, c.root)
    return {
      ...c,
      contract,
      quote: contract ? bySymbol.get(contract.symbol) : undefined,
      isWatched: contract ? watched.has(contract.symbol) : false,
      loading: searches[i].isLoading,
      error: searches[i].error as Error | undefined,
    }
  })

  const resolving = rows.some((r) => r.loading)
  const unwatched = rows.filter((r) => r.contract && !r.isWatched)
  const firstError = rows.find((r) => r.error)?.error

  return (
    <div className="page">
      <header className="page__header">
        <div>
          <h1 className="page__title">Commodity</h1>
          <p className="page__subtitle">
            MCX near-month futures — gold, silver, crude and natural gas, with the mini
            contracts beside the full-size ones. The contract rolls with the expiry; the age
            beside each price is how long ago the feed last moved it.
          </p>
        </div>
        {unwatched.length > 0 && (
          <button
            className="btn btn--primary"
            disabled={add.isPending}
            onClick={() =>
              unwatched.forEach((r) =>
                add.mutate({ symbol: r.contract!.symbol, dataType: 'symbolUpdate', priority: 40 }),
              )
            }
            title="Subscribe every contract on this page to the live feed"
          >
            <IconPlus /> Watch all {unwatched.length}
          </button>
        )}
      </header>

      {firstError && <InlineError error={firstError} />}

      {resolving ? (
        <Loading label="Resolving near-month contracts…" />
      ) : (
        <>
          <div
            className="stat-grid"
            style={{ gridTemplateColumns: 'repeat(auto-fit, minmax(200px, 1fr))' }}
          >
            {rows.map((r) => {
              const chg = changePct(r.quote)
              return (
                <div className="stat" key={r.root}>
                  <div className="stat__value">
                    <FlashPrice value={r.quote?.lastTradedPrice} bold />
                  </div>
                  <div className="stat__label">{r.label}</div>
                  <div className="stat__sub">
                    {r.quote ? (
                      <>
                        <span className={chg == null ? 'muted' : chg >= 0 ? 'pos' : 'neg'}>
                          {chg == null ? '' : `${chg >= 0 ? '+' : ''}${chg.toFixed(2)}%`}
                        </span>{' '}
                        <span className="faint">· {formatAge(r.quote.updatedUtc)}</span>
                      </>
                    ) : !r.contract ? (
                      <span className="faint">no live contract</span>
                    ) : r.isWatched ? (
                      <span className="faint">awaiting first tick…</span>
                    ) : (
                      <button
                        className="btn btn--ghost btn--sm"
                        style={{ padding: '1px 8px' }}
                        disabled={add.isPending}
                        onClick={() =>
                          add.mutate({
                            symbol: r.contract!.symbol,
                            dataType: 'symbolUpdate',
                            priority: 40,
                          })
                        }
                      >
                        <IconPlus style={{ width: 11, height: 11 }} /> Watch
                      </button>
                    )}
                  </div>
                </div>
              )
            })}
          </div>

          <Panel
            title="Contracts"
            actions={
              <span className="faint" style={{ fontSize: 12 }}>
                Nearest expiry per commodity, resolved from the instrument master
              </span>
            }
          >
            <div className="tablewrap">
              <table className="table">
                <thead>
                  <tr>
                    <th>Commodity</th>
                    <th>Contract</th>
                    <th>Expiry</th>
                    <th className="r">LTP</th>
                    <th className="r">Change</th>
                    <th className="r">Open</th>
                    <th className="r">High</th>
                    <th className="r">Low</th>
                    <th className="r">Prev close</th>
                    <th className="r">Volume</th>
                    <th>Feed</th>
                  </tr>
                </thead>
                <tbody>
                  {rows.map((r) => {
                    const q = r.quote
                    const chg = changePct(q)
                    return (
                      <tr key={r.root}>
                        <td>
                          {r.label}{' '}
                          <span className="faint" style={{ fontSize: 11.5 }}>
                            {r.group}
                          </span>
                        </td>
                        <td className="mono">{r.contract?.symbol ?? '—'}</td>
                        <td className="muted">{expiryLabel(r.contract?.expiryDate)}</td>
                        <td className="r">
                          <FlashPrice value={q?.lastTradedPrice} />
                        </td>
                        <td className={`r ${chg == null ? 'muted' : chg >= 0 ? 'pos' : 'neg'}`}>
                          {chg == null ? '—' : `${chg >= 0 ? '+' : ''}${chg.toFixed(2)}%`}
                        </td>
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
                            <span className="faint">{formatAge(q.updatedUtc)}</span>
                          ) : r.isWatched ? (
                            <Badge tone="accent">subscribed</Badge>
                          ) : (
                            <Badge tone="warn">not watched</Badge>
                          )}
                        </td>
                      </tr>
                    )
                  })}
                </tbody>
              </table>
            </div>
            <p className="faint" style={{ fontSize: 12, marginTop: 10 }}>
              MCX trades to 23:30 IST, well past the equity close. A quote that stops ageing
              is the feed stopping, not the exchange — the ingestor is on{' '}
              <a href="/admin/data/live">Live feeds</a>.
            </p>
          </Panel>
        </>
      )}
    </div>
  )
}
