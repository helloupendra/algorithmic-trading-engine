/**
 * Data module — Live feeds, v2.1 layout.
 *
 * One control: the Start/Stop button in the page header. One data surface:
 * the live watchlist table, which joins subscriptions with their live quotes
 * (the old page showed the same symbols twice — once as a bare watchlist,
 * once as quotes). Index tickers sit on top and start moving the moment the
 * feed runs. Diagnostics (heartbeats, process logs) fold away at the bottom.
 */

import { useMemo, useState } from 'react'
import {
  useAddEquityGroupToWatchlist,
  useAddWatchlistSymbol,
  useEquityGroups,
  useIngestorLogs,
  useIngestorProcessStatus,
  useIngestorStatuses,
  useInstrumentSearch,
  useLatestQuotes,
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
import { Badge, FlashPrice, InlineError, Panel, QueryBoundary } from '../../components/ui'
import {
  IconDatabase,
  IconPlay,
  IconPlus,
  IconSearch,
  IconStop,
  IconTrash,
  IconWarning,
} from '../../components/icons'
import type { LiveQuote } from '../../lib/types'

/* ---------------------------------------------------------------- helpers */

function changePct(quote: LiveQuote | undefined): number | null {
  if (!quote || quote.lastTradedPrice == null || quote.close == null || quote.close === 0)
    return null
  return ((quote.lastTradedPrice - quote.close) / quote.close) * 100
}

/* ------------------------------------------------------------ header bits */

/**
 * How the console relates to the feed process, from GET /api/Ingestor/status
 * plus the heartbeats:
 *  - managed:  spawned by this API instance — Stop works, output is captured;
 *  - adopted:  a stored pid is alive but was not launched by this API
 *              instance (an earlier instance spawned it, or it was started
 *              from a terminal and reported its pid through its heartbeat) —
 *              Stop works (the API kills that pid), output is not captured;
 *  - external: no pid known but a heartbeat is healthy — a streamer build
 *              that does not report its pid; it can only be stopped from
 *              where it was started;
 *  - stopped:  nothing alive.
 * An API build from before supervision hardening answers with `isRunning`
 * only, which reads as managed (true) or external/stopped (false).
 */
function useFeedProcess() {
  const process = useIngestorProcessStatus()
  const ingestors = useIngestorStatuses()
  const healthyCount = (ingestors.data ?? []).filter((f) => f.isHealthy).length
  const status = process.data
  const pidKnown = status?.isRunning ?? false
  const source = status?.source ?? (pidKnown ? 'managed' : 'none')
  const external = !pidKnown && source === 'none' && healthyCount > 0
  const kind: 'managed' | 'adopted' | 'external' | 'stopped' = pidKnown
    ? source === 'adopted'
      ? 'adopted'
      : 'managed'
    : external
      ? 'external'
      : 'stopped'
  return {
    kind,
    /** Stop is possible: the API holds a handle or a live pid. */
    canStop: pidKnown,
    isRunning: pidKnown || healthyCount > 0,
    processId: status?.processId ?? null,
    healthyCount,
    loaded: process.data !== undefined,
  }
}

function FeedControlButton() {
  const feed = useFeedProcess()
  const session = useMarketSession()
  const start = useStartIngestor()
  const stop = useStopIngestor()

  function confirmStop() {
    const pid = feed.processId != null ? ` (pid ${feed.processId})` : ''
    const adopted =
      feed.kind === 'adopted'
        ? ` This feed was not launched by this API instance${pid} — an earlier instance or a terminal started it; the API will kill that process.`
        : ''
    const warning = session.data?.isMarketOpen
      ? `Market is OPEN. Stopping the ingestor halts tick capture for every running strategy.${adopted} Stop anyway?`
      : `Stop the live ingestor process${pid}?${adopted}`
    if (window.confirm(warning)) stop.mutate()
  }

  const stopTitle =
    feed.kind === 'external'
      ? 'The feed is running outside this console (no pid known) — stop it from the terminal that started it.'
      : feed.kind === 'adopted'
        ? `Not launched by this API instance${feed.processId != null ? ` (pid ${feed.processId})` : ''} — running outside this console, known by its pid; Stop kills that process.`
        : undefined

  return (
    <div className="toolbar">
      {start.isError && <InlineError error={start.error} />}
      {stop.isError && <InlineError error={stop.error} />}
      {feed.isRunning ? (
        <button
          className="btn btn--danger"
          disabled={stop.isPending || !feed.canStop}
          onClick={confirmStop}
          title={stopTitle}
        >
          <IconStop style={{ width: 14, height: 14 }} />
          {stop.isPending ? 'Stopping…' : feed.kind === 'adopted' ? 'Stop live feed (adopted)' : 'Stop live feed'}
        </button>
      ) : (
        <button
          className="btn btn--pos"
          disabled={start.isPending}
          onClick={() => start.mutate()}
        >
          <IconPlay style={{ width: 14, height: 14 }} />
          {start.isPending ? 'Starting…' : 'Start live feed'}
        </button>
      )}
    </div>
  )
}

/* ------------------------------------------------------------ index cards */

const INDEX_CARDS = [
  { symbol: 'NSE:NIFTYBANK-INDEX', label: 'BANKNIFTY' },
  { symbol: 'NSE:NIFTY50-INDEX', label: 'NIFTY 50' },
  { symbol: 'NSE:FINNIFTY-INDEX', label: 'FINNIFTY' },
  { symbol: 'BSE:SENSEX-INDEX', label: 'SENSEX' },
]

function IndexTickerRow() {
  const quotes = useLatestQuotes()
  const watchlist = useWatchlist()
  const add = useAddWatchlistSymbol()

  const bySymbol = useMemo(
    () => new Map((quotes.data ?? []).map((q) => [q.symbol, q])),
    [quotes.data],
  )
  const watched = useMemo(
    () => new Set((watchlist.data ?? []).map((w) => w.symbol)),
    [watchlist.data],
  )

  return (
    <div className="stat-grid" style={{ gridTemplateColumns: 'repeat(auto-fit, minmax(200px, 1fr))' }}>
      {INDEX_CARDS.map(({ symbol, label }) => {
        const quote = bySymbol.get(symbol)
        const chg = changePct(quote)
        return (
          <div className="stat" key={symbol}>
            <div className="stat__value">
              <FlashPrice value={quote?.lastTradedPrice} bold />
            </div>
            <div className="stat__label">{label}</div>
            <div className="stat__sub">
              {quote ? (
                <>
                  <span className={chg == null ? 'muted' : chg >= 0 ? 'pos' : 'neg'}>
                    {chg == null ? '' : `${chg >= 0 ? '+' : ''}${chg.toFixed(2)}%`}
                  </span>{' '}
                  <span className="faint">· {formatAge(quote.updatedUtc)}</span>
                </>
              ) : watched.has(symbol) ? (
                <span className="faint">awaiting first tick…</span>
              ) : (
                <button
                  className="btn btn--ghost btn--sm"
                  style={{ padding: '1px 8px' }}
                  disabled={add.isPending}
                  onClick={() => add.mutate({ symbol, dataType: 'symbolUpdate' })}
                >
                  <IconPlus style={{ width: 11, height: 11 }} /> Watch
                </button>
              )}
            </div>
          </div>
        )
      })}
    </div>
  )
}

/* ------------------------------------------------------- stale warning bar */

function StalePanel() {
  const session = useMarketSession()
  const stale = useStaleQuotes(120)

  if (!(session.data?.isMarketOpen ?? false)) return null
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

/* ------------------------------------------------- add symbol / add group */

function AddSymbolForm() {
  const [query, setQuery] = useState('')
  const [dataType, setDataType] = useState<'symbolUpdate' | 'lite'>('symbolUpdate')
  const search = useInstrumentSearch(query)
  const add = useAddWatchlistSymbol()

  const results = search.data ?? []

  return (
    <div style={{ flex: 1, minWidth: 260 }}>
      <div className="inline-form">
        <input
          className="field__input field__input--sm"
          style={{ flex: 1, minWidth: 180 }}
          placeholder="Search and add symbol to save to database…"
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

      {add.isError && (
        <div style={{ marginTop: 8 }}>
          <InlineError error={add.error} />
        </div>
      )}

      {query.trim().length >= 2 && (
        <div className="tablewrap" style={{ maxHeight: 220, overflowY: 'auto', marginTop: 8 }}>
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
                      onClick={() => {
                        add.mutate({ symbol: inst.symbol, dataType })
                        setQuery('')
                      }}
                      title="Add to database recording list"
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

function AddGroupForm() {
  const groups = useEquityGroups()
  const addGroup = useAddEquityGroupToWatchlist()
  const [selected, setSelected] = useState('')

  return (
    <div className="inline-form">
      <select
        className="field__input field__input--sm"
        value={selected}
        onChange={(e) => setSelected(e.target.value)}
      >
        <option value="">Add an equity group…</option>
        {(groups.data ?? []).map((g) => (
          <option key={g.id} value={g.name}>
            {g.displayName || g.name} ({g.memberCount})
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

/* ---------------------------------------------- merged live watchlist table */

function LiveWatchlistPanel() {
  const watchlist = useWatchlist()
  const quotes = useLatestQuotes()
  const remove = useRemoveWatchlistSymbol()
  const session = useMarketSession()
  const [filter, setFilter] = useState('')

  const quoteBySymbol = useMemo(
    () => new Map((quotes.data ?? []).map((q) => [q.symbol, q])),
    [quotes.data],
  )

  function confirmRemove(id: number, symbol: string) {
    const open = session.data?.isMarketOpen
    const msg = open
      ? `Market is OPEN. Removing ${symbol} stops its live tick capture immediately. Remove anyway?`
      : `Remove ${symbol} from the database recording list?`
    if (window.confirm(msg)) remove.mutate(id)
  }

  return (
    <Panel
      title={
        <>
          <IconDatabase /> Database recording list
        </>
      }
      actions={
        <input
          className="field__input field__input--sm"
          placeholder="Filter…"
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
        />
      }
    >
      <div className="toolbar" style={{ marginBottom: 12, alignItems: 'flex-start' }}>
        <AddSymbolForm />
        <AddGroupForm />
      </div>

      <QueryBoundary
        query={watchlist}
        empty="Watchlist is empty — the feed subscribes to nothing. Add a symbol or a group above."
      >
        {(items) => {
          const needle = filter.trim().toUpperCase()
          const rows = [...items]
            .sort((a, b) => a.symbol.localeCompare(b.symbol))
            .filter((w) => (needle ? w.symbol.toUpperCase().includes(needle) : true))
          return (
            <div className="tablewrap tablewrap--tall">
              <table className="table">
                <thead>
                  <tr>
                    <th>Symbol</th>
                    <th>Type</th>
                    <th className="r">LTP</th>
                    <th className="r">Chg%</th>
                    <th className="r">Open</th>
                    <th className="r">High</th>
                    <th className="r">Low</th>
                    <th className="r">Volume</th>
                    <th>Updated</th>
                    <th>Feed</th>
                    <th className="r"></th>
                  </tr>
                </thead>
                <tbody>
                  {rows.map((item) => {
                    const quote = quoteBySymbol.get(item.symbol)
                    const chg = changePct(quote)
                    const ageMs = quote ? Date.now() - new Date(quote.updatedUtc).getTime() : null
                    return (
                      <tr key={item.id}>
                        <td className="mono">{shortSymbol(item.symbol)}</td>
                        <td>
                          <Badge tone="neutral">{classifySymbol(item.symbol)}</Badge>
                        </td>
                        <td className="r">
                          {quote ? (
                            <FlashPrice value={quote.lastTradedPrice} />
                          ) : (
                            <span className="faint">awaiting tick</span>
                          )}
                        </td>
                        <td className={`r ${chg == null ? 'muted' : chg >= 0 ? 'pos' : 'neg'}`}>
                          {chg == null ? '—' : `${chg >= 0 ? '+' : ''}${chg.toFixed(2)}%`}
                        </td>
                        <td className="r muted">{formatPrice(quote?.open)}</td>
                        <td className="r muted">{formatPrice(quote?.high)}</td>
                        <td className="r muted">{formatPrice(quote?.low)}</td>
                        <td className="r muted">
                          {quote?.volume == null ? '—' : quote.volume.toLocaleString('en-IN')}
                        </td>
                        <td className={ageMs != null && ageMs > 120_000 ? 'warn' : 'muted'}>
                          {quote ? formatAge(quote.updatedUtc) : '—'}
                        </td>
                        <td>
                          <span className="muted">{item.dataType === 'symbolUpdate' ? 'Full' : 'Lite'}</span>{' '}
                          {!item.isActive && <Badge tone="warn">off</Badge>}
                        </td>
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
                    )
                  })}
                </tbody>
              </table>
              {rows.length === 0 && <p className="empty">No symbols match “{filter}”.</p>}
            </div>
          )
        }}
      </QueryBoundary>
    </Panel>
  )
}

/* ------------------------------------------------------------- diagnostics */

/** One line on how the console holds the feed process, for the diagnostics panel. */
function processSummary(feed: ReturnType<typeof useFeedProcess>): { tone: 'pos' | 'warn' | 'neutral' | 'accent'; text: string } {
  const pid = feed.processId != null ? ` · pid ${feed.processId}` : ''
  switch (feed.kind) {
    case 'managed':
      return { tone: 'pos', text: `process managed by this API instance${pid} · output captured below` }
    case 'adopted':
      return {
        tone: 'accent',
        text: `not launched by this API instance — known by its pid${pid}, output not captured · Stop kills that process`,
      }
    case 'external':
      return {
        tone: 'warn',
        text: 'heartbeat healthy but no pid known — the feed was started outside this console',
      }
    default:
      return { tone: 'neutral', text: 'no feed process alive' }
  }
}

function DiagnosticsPanel() {
  const [open, setOpen] = useState(false)
  const statuses = useIngestorStatuses()
  const feed = useFeedProcess()
  const logs = useIngestorLogs(open)

  const feeds = statuses.data ?? []
  // Defensive: an API build without /api/Ingestor/logs answers with the SPA
  // fallback HTML (a string) — never crash on it.
  const logLines = Array.isArray(logs.data) ? logs.data : []
  const summary = processSummary(feed)

  let emptyOutput: string
  switch (feed.kind) {
    case 'adopted':
      emptyOutput =
        'The ingestor was not launched by this API instance (an earlier instance or a terminal started it), so its output is not captured here. ' +
        `It keeps writing to logs/engine/ingestor-${feed.processId ?? '<pid>'}.log on the API host.`
      break
    case 'external':
      emptyOutput =
        'The ingestor is running outside this console — its output goes to the terminal that started it.'
      break
    default:
      emptyOutput =
        'No process output yet — appears after the feed is started from this console (requires the updated API).'
  }

  return (
    <Panel
      title="Feed diagnostics"
      actions={
        <button className="btn btn--ghost btn--sm" onClick={() => setOpen(!open)}>
          {open ? 'Hide' : 'Show'}
        </button>
      }
    >
      {feed.loaded && (
        <p className="inline-form" style={{ margin: '0 0 8px', gap: 8, fontSize: 12.5 }}>
          <Badge tone={summary.tone}>{feed.kind}</Badge>
          <span className="muted">{summary.text}</span>
        </p>
      )}
      <div className="chip-row">
        {feeds.map((s) => (
          <span key={s.sourceName} className="inline-form" style={{ gap: 6 }}>
            <b className="mono" style={{ fontSize: 12 }}>{s.sourceName}</b>
            <Badge tone={s.isHealthy ? 'pos' : 'warn'}>
              {s.isHealthy ? s.status : `${s.status} · stale`}
            </Badge>
            <span className="faint" style={{ fontSize: 12 }}>
              beat {formatAge(s.lastHeartbeatUtc)} · {s.currentSubscribedSymbols.length} symbols
            </span>
          </span>
        ))}
        {feeds.length === 0 && <span className="faint">No heartbeat recorded yet.</span>}
      </div>
      {feeds.some((s) => s.lastError) && (
        <p className="neg" style={{ margin: '8px 0 0', fontSize: 12.5 }}>
          {feeds.find((s) => s.lastError)?.lastError}
        </p>
      )}

      {open && (
        <div style={{ marginTop: 12 }}>
          <div className="console">
            <div className="console__bar">
              <span className="console__dot console__dot--r" />
              <span className="console__dot console__dot--y" />
              <span className="console__dot console__dot--g" />
              <span className="console__title">Ingestor process output</span>
            </div>
            <div className={`console__body ${logLines.length === 0 ? 'faint' : ''}`}>
              {logLines.length === 0
                ? emptyOutput
                : logLines.map((line, i) => <div key={i}>{line}</div>)}
            </div>
          </div>
        </div>
      )}
    </Panel>
  )
}

/* ---------------------------------------------------------------- inspector */

function InspectorPanel() {
  const watchlist = useWatchlist()
  const [symbol, setSymbol] = useState<string | null>(null)

  const symbols = useMemo(
    () => (watchlist.data ?? []).map((w) => w.symbol).sort(),
    [watchlist.data],
  )

  const activeSymbol = symbol && symbols.includes(symbol) ? symbol : null
  const ticks = useRecentTicks(activeSymbol, 30)
  const bars = useLiveBars(activeSymbol, 30)

  return (
    <Panel
      title={
        <>
          <IconSearch /> Inspector — raw ticks & 1m bars
        </>
      }
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

/* --------------------------------------------------------------------- page */

export function LiveFeedsPage() {
  return (
    <div className="page">
      <header className="page__header">
        <div>
          <h1 className="page__title">Live feeds</h1>
          <p className="page__subtitle">
            Broker websocket → watchlist subscriptions → live quotes, ticks and bars.
          </p>
        </div>
        <FeedControlButton />
      </header>

      <IndexTickerRow />
      <StalePanel />
      <LiveWatchlistPanel />
      <DiagnosticsPanel />
      <InspectorPanel />
    </div>
  )
}
