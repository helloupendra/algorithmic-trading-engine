/**
 * Data module — Live feeds. Operates the live pipeline end to end: ingestor
 * process control, per-source heartbeats, the watchlist that drives broker
 * subscriptions, a flashing quote monitor, and a raw tick/bar inspector.
 */

import { useMemo, useState } from 'react'
import {
  useAddEquityGroupToWatchlist,
  useAddWatchlistSymbol,
  useEquityGroups,
  useIngestorProcessStatus,
  useIngestorStatuses,
  useInstrumentSearch,
  useLiveBars,
  useMarketSession,
  useRecentTicks,
  useRemoveWatchlistSymbol,
  useStaleQuotes,
  useStartIngestor,
  useStopIngestor,
  useWatchlist,
} from '../../lib/queries'
import { formatAge, formatDateTime, formatPrice, shortSymbol } from '../../lib/format'
import { classifySymbol } from '../../lib/symbols'
import { Badge, InlineError, Panel, QueryBoundary } from '../../components/ui'
import { LiveQuotesMonitor } from '../../components/LiveQuotesMonitor'
import { IconPlay, IconPlus, IconPulse, IconSearch, IconStop, IconTrash, IconWarning } from '../../components/icons'

/** Start/stop + heartbeat health for the ingestor process. */
function IngestorPanel() {
  const process = useIngestorProcessStatus()
  const statuses = useIngestorStatuses()
  const session = useMarketSession()
  const start = useStartIngestor()
  const stop = useStopIngestor()

  const marketOpen = session.data?.isMarketOpen ?? false

  function confirmStop() {
    const warning = marketOpen
      ? 'Market is OPEN. Stopping the ingestor halts tick capture for every running strategy. Stop anyway?'
      : 'Stop the live ingestor process?'
    if (window.confirm(warning)) stop.mutate()
  }

  return (
    <Panel
      title={
        <>
          <IconPulse /> Ingestor
        </>
      }
      actions={
        process.data?.isRunning ? (
          <button className="btn btn--danger btn--sm" disabled={stop.isPending} onClick={confirmStop}>
            <IconStop style={{ width: 13, height: 13 }} /> Stop
          </button>
        ) : (
          <button
            className="btn btn--pos btn--sm"
            disabled={start.isPending}
            onClick={() => start.mutate()}
          >
            <IconPlay style={{ width: 13, height: 13 }} /> Start live feed
          </button>
        )
      }
    >
      {start.isError && <InlineError error={start.error} />}
      {stop.isError && <InlineError error={stop.error} />}

      <QueryBoundary
        query={statuses}
        empty="No heartbeat recorded yet — once the feed starts, each source reports here every few seconds."
      >
        {(data) => (
          <div className="stack-list">
            {data.map((s) => (
              <div key={s.sourceName}>
                <div className="ingestor__head">
                  <b className="mono">{s.sourceName}</b>
                  <Badge tone={s.isHealthy ? 'pos' : 'warn'}>
                    {s.isHealthy ? s.status : `${s.status} · stale`}
                  </Badge>
                  <span className="muted">beat {formatAge(s.lastHeartbeatUtc)}</span>
                  <span className="faint">
                    watchlist refresh {formatAge(s.lastWatchlistRefreshUtc)}
                  </span>
                </div>
                {s.lastError && <p className="neg" style={{ margin: '2px 0 6px' }}>{s.lastError}</p>}
                <div className="chip-row">
                  {s.currentSubscribedSymbols.slice(0, 24).map((sym) => (
                    <span key={sym} className="badge badge--neutral mono">
                      {shortSymbol(sym)}
                    </span>
                  ))}
                  {s.currentSubscribedSymbols.length > 24 && (
                    <span className="badge badge--neutral">
                      +{s.currentSubscribedSymbols.length - 24} more
                    </span>
                  )}
                </div>
              </div>
            ))}
          </div>
        )}
      </QueryBoundary>
    </Panel>
  )
}

/** Search-driven add: shows instrument matches before anything is committed. */
function AddSymbolForm() {
  const [query, setQuery] = useState('')
  const [dataType, setDataType] = useState<'symbolUpdate' | 'lite'>('symbolUpdate')
  const search = useInstrumentSearch(query)
  const add = useAddWatchlistSymbol()

  const results = search.data ?? []

  return (
    <div>
      <div className="inline-form" style={{ marginBottom: 8 }}>
        <input
          className="field__input field__input--sm"
          style={{ flex: 1, minWidth: 200 }}
          placeholder="Search instruments — e.g. SBIN, NIFTY, CRUDEOIL…"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
        />
        <div className="seg" role="group" aria-label="Feed detail">
          {(['symbolUpdate', 'lite'] as const).map((t) => (
            <button
              key={t}
              type="button"
              className={`seg__btn ${dataType === t ? 'is-active' : ''}`}
              onClick={() => setDataType(t)}
              title={t === 'symbolUpdate' ? 'Full tick detail (bid/ask/depth)' : 'LTP-only, lighter'}
            >
              {t === 'symbolUpdate' ? 'Full' : 'Lite'}
            </button>
          ))}
        </div>
      </div>

      {add.isError && <InlineError error={add.error} />}

      {query.trim().length >= 2 && (
        <div className="tablewrap" style={{ maxHeight: 220, overflowY: 'auto' }}>
          <table className="table">
            <tbody>
              {results.map((inst) => (
                <tr key={inst.id}>
                  <td className="mono">{inst.symbol}</td>
                  <td className="muted">{inst.description}</td>
                  <td>
                    <Badge tone="neutral">{inst.instrumentType}</Badge>
                  </td>
                  <td className="r">
                    <button
                      className="btn btn--ghost btn--sm"
                      disabled={add.isPending}
                      onClick={() => add.mutate({ symbol: inst.symbol, dataType })}
                      title="Add to live watchlist"
                    >
                      <IconPlus style={{ width: 13, height: 13 }} /> Watch
                    </button>
                  </td>
                </tr>
              ))}
              {search.isSuccess && results.length === 0 && (
                <tr>
                  <td className="muted">No instruments match “{query}”.</td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}

/** Bulk add a whole equity group (e.g. NIFTY50 constituents). */
function AddGroupForm() {
  const groups = useEquityGroups()
  const addGroup = useAddEquityGroupToWatchlist()
  const [selected, setSelected] = useState('')

  const list = groups.data ?? []

  return (
    <div className="inline-form">
      <select
        className="field__input field__input--sm"
        value={selected}
        onChange={(e) => setSelected(e.target.value)}
      >
        <option value="">Add an equity group…</option>
        {list.map((g) => (
          <option key={g.id} value={g.name}>
            {g.displayName || g.name} ({g.memberCount} members)
          </option>
        ))}
      </select>
      <button
        className="btn btn--sm"
        disabled={!selected || addGroup.isPending}
        onClick={() => addGroup.mutate({ groupName: selected, dataType: 'lite' })}
      >
        <IconPlus style={{ width: 13, height: 13 }} /> Add group
      </button>
      {addGroup.isSuccess && (
        <span className="muted" style={{ fontSize: 12 }}>
          Added {addGroup.data.upserted} · skipped {addGroup.data.skipped}
        </span>
      )}
    </div>
  )
}

function WatchlistPanel() {
  const watchlist = useWatchlist()
  const remove = useRemoveWatchlistSymbol()
  const session = useMarketSession()

  function confirmRemove(id: number, symbol: string) {
    const open = session.data?.isMarketOpen
    const msg = open
      ? `Market is OPEN. Removing ${symbol} stops its live tick capture immediately. Remove anyway?`
      : `Remove ${symbol} from the live watchlist?`
    if (window.confirm(msg)) remove.mutate(id)
  }

  return (
    <Panel
      title={
        <>
          <IconSearch /> Watchlist — live subscriptions
        </>
      }
    >
      <AddSymbolForm />
      <div style={{ margin: '10px 0' }}>
        <AddGroupForm />
      </div>

      <QueryBoundary query={watchlist} empty="Watchlist is empty — the feed subscribes to nothing.">
        {(items) => (
          <div className="tablewrap tablewrap--tall">
            <table className="table">
              <thead>
                <tr>
                  <th>Symbol</th>
                  <th>Category</th>
                  <th>Detail</th>
                  <th>Active</th>
                  <th>Added</th>
                  <th className="r"></th>
                </tr>
              </thead>
              <tbody>
                {items.map((item) => (
                  <tr key={item.id}>
                    <td className="mono">{shortSymbol(item.symbol)}</td>
                    <td>
                      <Badge tone="neutral">{classifySymbol(item.symbol)}</Badge>
                    </td>
                    <td className="muted">{item.dataType === 'symbolUpdate' ? 'Full' : 'Lite'}</td>
                    <td>
                      {item.isActive ? <Badge tone="pos">active</Badge> : <Badge tone="warn">off</Badge>}
                    </td>
                    <td className="muted">{formatDateTime(item.createdUtc)}</td>
                    <td className="r">
                      <button
                        className="btn btn--ghost btn--sm"
                        onClick={() => confirmRemove(item.id, item.symbol)}
                        disabled={remove.isPending}
                        title="Remove from watchlist"
                        aria-label={`Remove ${item.symbol}`}
                      >
                        <IconTrash style={{ width: 13, height: 13 }} />
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </QueryBoundary>
    </Panel>
  )
}

function StalePanel() {
  const session = useMarketSession()
  const stale = useStaleQuotes(120)
  const marketOpen = session.data?.isMarketOpen ?? false

  if (!marketOpen) return null
  const rows = stale.data ?? []
  if (rows.length === 0) return null

  return (
    <Panel
      title={
        <>
          <IconWarning /> Stale during market hours
        </>
      }
      className="panel--danger"
    >
      <p className="card__muted">
        These symbols stopped ticking more than 2 minutes ago while the market is open — a dropped
        subscription or a wedged feed.
      </p>
      <div className="chip-row">
        {rows.map((s) => (
          <span key={s.symbol} className="badge badge--warn mono" title={`last ${formatAge(s.updatedUtc)}`}>
            {shortSymbol(s.symbol)} · {formatAge(s.updatedUtc)}
          </span>
        ))}
      </div>
    </Panel>
  )
}

/** Raw ticks + live 1m bars for one symbol off the watchlist. */
function InspectorPanel() {
  const watchlist = useWatchlist()
  const [symbol, setSymbol] = useState<string | null>(null)

  const symbols = useMemo(
    () => (watchlist.data ?? []).map((w) => w.symbol).sort(),
    [watchlist.data],
  )

  // A symbol removed from the watchlist stops being inspected too — otherwise
  // the panel would keep polling it forever behind a blank select.
  const activeSymbol = symbol && symbols.includes(symbol) ? symbol : null
  const ticks = useRecentTicks(activeSymbol, 30)
  const bars = useLiveBars(activeSymbol, 30)

  return (
    <Panel
      title="Inspector — raw ticks & 1m bars"
      actions={
        <select
          className="field__input field__input--sm"
          value={activeSymbol ?? ''}
          onChange={(e) => setSymbol(e.target.value || null)}
        >
          <option value="">Pick a watched symbol…</option>
          {symbols.map((s) => (
            <option key={s} value={s}>
              {shortSymbol(s)}
            </option>
          ))}
        </select>
      }
    >
      {!activeSymbol ? (
        <p className="empty">Pick a symbol to inspect what the feed is actually writing.</p>
      ) : (
        <div className="two-col">
          <div>
            <p className="field__label" style={{ marginBottom: 6 }}>
              Last ticks
            </p>
            <QueryBoundary query={ticks} empty="No ticks stored for this symbol yet.">
              {(rows) => (
                <div className="tablewrap" style={{ maxHeight: 340, overflowY: 'auto' }}>
                  <table className="table">
                    <thead>
                      <tr>
                        <th>Time</th>
                        <th className="r">LTP</th>
                        <th className="r">Bid</th>
                        <th className="r">Ask</th>
                        <th className="r">Volume</th>
                      </tr>
                    </thead>
                    <tbody>
                      {rows.map((t, i) => (
                        <tr key={i}>
                          <td className="muted">
                            {formatDateTime(t.exchangeTimestampUtc ?? t.receivedUtc)}
                          </td>
                          <td className="r mono">{formatPrice(t.lastTradedPrice)}</td>
                          <td className="r muted">{formatPrice(t.bidPrice)}</td>
                          <td className="r muted">{formatPrice(t.askPrice)}</td>
                          <td className="r muted">
                            {t.volume == null ? '—' : t.volume.toLocaleString('en-IN')}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </QueryBoundary>
          </div>
          <div>
            <p className="field__label" style={{ marginBottom: 6 }}>
              Live 1m bars
            </p>
            <QueryBoundary query={bars} empty="No live bars aggregated for this symbol yet.">
              {(rows) => (
                <div className="tablewrap" style={{ maxHeight: 340, overflowY: 'auto' }}>
                  <table className="table">
                    <thead>
                      <tr>
                        <th>Bar start</th>
                        <th className="r">O</th>
                        <th className="r">H</th>
                        <th className="r">L</th>
                        <th className="r">C</th>
                        <th className="r">Ticks</th>
                      </tr>
                    </thead>
                    <tbody>
                      {rows.map((b) => (
                        <tr key={b.barStartUtc}>
                          <td className="muted">{formatDateTime(b.barStartUtc)}</td>
                          <td className="r mono">{formatPrice(b.open)}</td>
                          <td className="r mono">{formatPrice(b.high)}</td>
                          <td className="r mono">{formatPrice(b.low)}</td>
                          <td className="r mono">{formatPrice(b.close)}</td>
                          <td className="r muted">{b.tickCount}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </QueryBoundary>
          </div>
        </div>
      )}
    </Panel>
  )
}

export function LiveFeedsPage() {
  const [filter, setFilter] = useState('')

  return (
    <div className="page">
      <header className="page__header">
        <div>
          <h1 className="page__title">Live feeds</h1>
          <p className="page__subtitle">
            The tick pipeline: broker websocket → watchlist subscriptions → quotes, ticks and 1m
            bars.
          </p>
        </div>
      </header>

      <StalePanel />

      <div className="two-col">
        <IngestorPanel />
        <WatchlistPanel />
      </div>

      <Panel
        title="Live quotes"
        actions={
          <input
            className="field__input field__input--sm"
            placeholder="Filter symbols…"
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
          />
        }
      >
        <LiveQuotesMonitor filter={filter} />
      </Panel>

      <InspectorPanel />
    </div>
  )
}
