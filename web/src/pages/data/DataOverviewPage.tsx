/**
 * Data module — Overview, v2.1.
 *
 * Only what the topbar does NOT already say (market session and broker live
 * there): the state of the pipeline, what data exists, what changed last,
 * and — only when something is actually wrong — what needs attention.
 */

import { Link } from 'react-router-dom'
import {
  useBrokerSession,
  useDataCoverage,
  useIngestorProcessStatus,
  useIngestorStatuses,
  useLatestQuotes,
  useMarketSession,
  useStaleQuotes,
  useWatchlist,
  type CoverageRow,
} from '../../lib/queries'
import { formatAge, formatDateTime, formatNumber, shortSymbol } from '../../lib/format'
import { Badge, Panel, QueryBoundary, StatTile } from '../../components/ui'
import { SymbolMastersPanel } from './SymbolMastersPanel'
import { meaningfulError } from '../../lib/pulse'
import { IconArrowRight, IconDatabase, IconPulse, IconWarning } from '../../components/icons'
import {
  CATEGORY_ORDER,
  classifySymbol,
  coverageColumnKey,
  coverageColumns,
  formatResolution,
  type SymbolCategory,
} from '../../lib/symbols'

interface MatrixCell {
  symbols: Set<string>
  bars: number
}

function buildMatrix(rows: CoverageRow[]) {
  // A column per source and resolution: the stored 1-minute candles and the
  // live 1-minute bars are different data, and used to share a "1M" heading.
  const columns = coverageColumns(rows)
  const matrix = new Map<SymbolCategory, Map<string, MatrixCell>>()

  for (const row of rows) {
    const cat = classifySymbol(row.symbol)
    if (!matrix.has(cat)) matrix.set(cat, new Map())
    const byColumn = matrix.get(cat)!
    const key = coverageColumnKey(row)
    if (!byColumn.has(key)) byColumn.set(key, { symbols: new Set(), bars: 0 })
    const cell = byColumn.get(key)!
    cell.symbols.add(row.symbol)
    cell.bars += row.barCount
  }

  const categories = CATEGORY_ORDER.filter((c) => matrix.has(c))
  return { columns, matrix, categories }
}

/** A heartbeat's last error, in red only when it says something ("None None" does not). */
function HeartbeatError({ text }: { text: string | null }) {
  const error = meaningfulError(text)
  return error ? <p className="neg" style={{ margin: '6px 0 0', fontSize: 12.5 }}>{error}</p> : null
}

/** Renders only when something genuinely needs an operator's eyes. */
function NeedsAttention() {
  const process = useIngestorProcessStatus()
  const ingestors = useIngestorStatuses()
  const broker = useBrokerSession()
  const session = useMarketSession()
  const stale = useStaleQuotes(120)
  const watchlist = useWatchlist()
  const quotes = useLatestQuotes()

  const marketOpen = session.data?.isMarketOpen ?? false
  const items: { text: string; to: string; action: string }[] = []

  if (broker.data && !broker.data.isAuthenticated) {
    items.push({
      text: 'FYERS is not linked — the live stream and history sync cannot work without a broker session.',
      to: '/system/connectors',
      action: 'Connect broker',
    })
  }

  const feeds = ingestors.data ?? []
  const healthy = feeds.filter((f) => f.isHealthy).length
  const unhealthy = feeds.filter((f) => !f.isHealthy)
  const isRunning = process.data?.isRunning || healthy > 0

  if (process.data && !isRunning && marketOpen) {
    items.push({
      text: 'Market is open but the live ingestor is not running — no ticks are being captured.',
      to: '/data/feeds',
      action: 'Start feed',
    })
  }

  if (isRunning && unhealthy.length > 0) {
    items.push({
      text: `${unhealthy.length} feed source${unhealthy.length > 1 ? 's' : ''} unhealthy: ${unhealthy
        .map((s) => `${s.sourceName} (${s.status})`)
        .join(', ')}.`,
      to: '/data/feeds',
      action: 'Diagnostics',
    })
  }

  if (marketOpen && (stale.data?.length ?? 0) > 0) {
    items.push({
      text: `${stale.data!.length} watched symbol${stale.data!.length > 1 ? 's' : ''} stopped ticking over 2 minutes ago during market hours.`,
      to: '/data/feeds',
      action: 'View',
    })
  }

  if (marketOpen && watchlist.data && quotes.data) {
    const quoted = new Set(quotes.data.map((q) => q.symbol))
    const neverTicked = watchlist.data.filter((w) => w.isActive && !quoted.has(w.symbol))
    if (neverTicked.length > 0) {
      items.push({
        text: `${neverTicked.length} watchlist symbol${neverTicked.length > 1 ? 's have' : ' has'} never received a tick.`,
        to: '/data/feeds',
        action: 'View',
      })
    }
  }

  if (items.length === 0) return null

  return (
    <Panel
      title={
        <>
          <IconWarning /> Needs attention
        </>
      }
      className="panel--danger"
    >
      <div className="checklist">
        {items.map((item, i) => (
          <div key={i} className="checklist__item">
            <span className="checklist__state checklist__state--todo">!</span>
            <span className="checklist__body">
              <span className="checklist__hint" style={{ fontSize: 13, color: 'var(--text)' }}>
                {item.text}
              </span>
            </span>
            <Link className="btn btn--sm" to={item.to}>
              {item.action}
            </Link>
          </div>
        ))}
      </div>
    </Panel>
  )
}

/** The most recently written data ranges — what actually changed last. */
function RecentlyUpdated({ rows }: { rows: CoverageRow[] }) {
  const recent = [...rows]
    .sort((a, b) => new Date(b.toUtc).getTime() - new Date(a.toUtc).getTime())
    .slice(0, 6)

  return (
    <Panel
      title={
        <>
          <IconDatabase /> Recently updated data
        </>
      }
      actions={
        <Link className="btn btn--ghost btn--sm" to="/data/historical">
          All ranges <IconArrowRight style={{ width: 12, height: 12 }} />
        </Link>
      }
    >
      {recent.length === 0 ? (
        <p className="empty">Nothing stored yet — backfill from Historical, or start the live feed.</p>
      ) : (
        <div className="tablewrap">
          <table className="table">
            <thead>
              <tr>
                <th>Symbol</th>
                <th>Res</th>
                <th>Source</th>
                <th className="r">Bars</th>
                <th>Last bar</th>
              </tr>
            </thead>
            <tbody>
              {recent.map((r) => (
                <tr key={`${r.source}|${r.resolution}|${r.symbol}`}>
                  <td className="mono">{shortSymbol(r.symbol)}</td>
                  <td>
                    <Badge tone="neutral">{formatResolution(r.resolution)}</Badge>
                  </td>
                  <td>
                    <Badge tone={r.source === 'live' ? 'live' : 'accent'}>{r.source}</Badge>
                  </td>
                  <td className="r">{formatNumber(r.barCount)}</td>
                  <td className="muted" title={formatDateTime(r.toUtc)}>
                    {formatAge(r.toUtc)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Panel>
  )
}

function LivePipelinePanel() {
  const ingestors = useIngestorStatuses()
  const process = useIngestorProcessStatus()

  const healthy = (ingestors.data ?? []).filter((f) => f.isHealthy).length
  const isRunning = process.data?.isRunning || healthy > 0

  return (
    <Panel
      title={
        <>
          <IconPulse /> Live pipeline
        </>
      }
      actions={
        <Link className="btn btn--ghost btn--sm" to="/data/feeds">
          Manage <IconArrowRight style={{ width: 12, height: 12 }} />
        </Link>
      }
    >
      <div className="kv-grid" style={{ marginBottom: 12 }}>
        <div>
          <span className="muted">Ingestor</span>
          <span className={isRunning ? 'pos' : 'muted'}>
            {process.isPending ? 'Checking…' : isRunning ? 'Running' : 'Stopped'}
          </span>
        </div>
      </div>
      <QueryBoundary
        query={ingestors}
        empty="No source has ever reported a heartbeat — start the feed once to see its health here."
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
                </div>
                <div className="kv-grid">
                  <div>
                    <span className="muted">Last heartbeat</span>
                    <span>{formatAge(s.lastHeartbeatUtc)}</span>
                  </div>
                  <div>
                    <span className="muted">Watchlist refresh</span>
                    <span>{formatAge(s.lastWatchlistRefreshUtc)}</span>
                  </div>
                  <div>
                    <span className="muted">Subscribed symbols</span>
                    <span>{s.currentSubscribedSymbols.length}</span>
                  </div>
                </div>
                <HeartbeatError text={s.lastError} />
              </div>
            ))}
          </div>
        )}
      </QueryBoundary>
    </Panel>
  )
}

export function DataOverviewPage() {
  const coverage = useDataCoverage()
  const watchlist = useWatchlist()
  const quotes = useLatestQuotes()
  const ingestors = useIngestorStatuses()
  const process = useIngestorProcessStatus()
  const stale = useStaleQuotes(120)
  const session = useMarketSession()

  const feeds = ingestors.data ?? []
  const healthy = feeds.filter((f) => f.isHealthy).length
  const isRunning = process.data?.isRunning || healthy > 0
  const marketOpen = session.data?.isMarketOpen ?? false

  const covRows = coverage.data ?? []
  const totalBars = covRows.reduce((sum, r) => sum + r.barCount, 0)
  const coveredSymbols = new Set(covRows.map((r) => r.symbol)).size

  const feedTone = !isRunning
    ? undefined
    : healthy === feeds.length && feeds.length > 0
      ? 'pos'
      : 'warn'

  return (
    <div className="page">
      <header className="page__header">
        <div>
          <h1 className="page__title">Data overview</h1>
          <p className="page__subtitle">
            Everything the strategies run on — live feeds on the left of the pipeline, stored
            history on the right.
          </p>
        </div>
      </header>

      <NeedsAttention />

      <div className="stat-grid">
        <StatTile
          label="Live feeds"
          value={process.isPending ? '…' : isRunning ? 'Running' : 'Stopped'}
          tone={feedTone as 'pos' | 'warn' | undefined}
          sub={
            feeds.length > 0
              ? `${healthy}/${feeds.length} sources healthy · beat ${formatAge(feeds[0]?.lastHeartbeatUtc)}`
              : 'no heartbeat recorded yet'
          }
          to="/data/feeds"
        />
        <StatTile
          label="Database saving"
          value={formatNumber(watchlist.data?.length ?? 0)}
          sub={`${formatNumber(quotes.data?.length ?? 0)} symbols actively recording`}
          to="/data/feeds"
        />
        <StatTile
          label="Stored history"
          value={formatNumber(totalBars)}
          sub={`bars across ${coveredSymbols} symbols`}
          to="/data/historical"
        />
        <StatTile
          label="Stale quotes"
          value={formatNumber(stale.data?.length ?? 0)}
          tone={marketOpen && (stale.data?.length ?? 0) > 0 ? 'warn' : undefined}
          sub={marketOpen ? 'older than 2 minutes' : 'market closed — staleness expected'}
        />
      </div>

      <Panel
        title={
          <>
            <IconDatabase /> Coverage by category & resolution
          </>
        }
        actions={
          <Link className="btn btn--sm" to="/data/historical">
            Browse historical <IconArrowRight style={{ width: 13, height: 13 }} />
          </Link>
        }
      >
        <QueryBoundary
          query={coverage}
          empty="No stored candles yet. Use Historical → Backfill to pull data from FYERS."
        >
          {(rows) => {
            const { columns, matrix, categories } = buildMatrix(rows)
            return (
              <div className="tablewrap">
                <table className="table">
                  <thead>
                    <tr>
                      <th>Category</th>
                      {columns.map((c) => (
                        <th key={c.key} className="r">
                          {c.label}
                        </th>
                      ))}
                    </tr>
                  </thead>
                  <tbody>
                    {categories.map((cat) => (
                      <tr key={cat}>
                        <td>
                          <Badge tone={cat === 'Options' ? 'accent' : 'neutral'}>{cat}</Badge>
                        </td>
                        {columns.map((c) => {
                          const cell = matrix.get(cat)?.get(c.key)
                          return (
                            <td key={c.key} className="r">
                              {cell ? (
                                <>
                                  <b>{formatNumber(cell.symbols.size)}</b>{' '}
                                  <span className="muted">sym</span>
                                  <span className="faint"> · {formatNumber(cell.bars)} bars</span>
                                </>
                              ) : (
                                <span className="faint">—</span>
                              )}
                            </td>
                          )
                        })}
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )
          }}
        </QueryBoundary>
        <p className="small-note">
          Stored candles from broker backfills and the nightly archive, with the live-captured 1-minute bars
          in a column of their own. Tick-level capture is per symbol under Data → Feeds.
        </p>
      </Panel>

      <div className="two-col">
        <LivePipelinePanel />
        <RecentlyUpdated rows={covRows} />
      </div>

      <SymbolMastersPanel />
    </div>
  )
}
