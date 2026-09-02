/**
 * Data module — Overview. The first screen of the data story: what is flowing
 * live right now, and what history is on disk — grouped by category and
 * resolution so nobody has to guess what a picker will find (coverage before
 * pickers, always).
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
import { formatAge, formatNumber } from '../../lib/format'
import { Badge, Panel, QueryBoundary, StatTile } from '../../components/ui'
import { IconArrowRight, IconCandles, IconDatabase, IconPulse, IconServer } from '../../components/icons'
import {
  CATEGORY_ORDER,
  classifySymbol,
  formatResolution,
  resolutionRank,
  type SymbolCategory,
} from '../../lib/symbols'

interface MatrixCell {
  symbols: Set<string>
  bars: number
}

function buildMatrix(rows: CoverageRow[]) {
  const resolutions = [...new Set(rows.map((r) => r.resolution))].sort(
    (a, b) => resolutionRank(a) - resolutionRank(b),
  )
  const matrix = new Map<SymbolCategory, Map<string, MatrixCell>>()

  for (const row of rows) {
    const cat = classifySymbol(row.symbol)
    if (!matrix.has(cat)) matrix.set(cat, new Map())
    const byRes = matrix.get(cat)!
    if (!byRes.has(row.resolution)) byRes.set(row.resolution, { symbols: new Set(), bars: 0 })
    const cell = byRes.get(row.resolution)!
    cell.symbols.add(row.symbol)
    cell.bars += row.barCount
  }

  const categories = CATEGORY_ORDER.filter((c) => matrix.has(c))
  return { resolutions, matrix, categories }
}

export function DataOverviewPage() {
  const coverage = useDataCoverage()
  const watchlist = useWatchlist()
  const quotes = useLatestQuotes()
  const ingestors = useIngestorStatuses()
  const process = useIngestorProcessStatus()
  const stale = useStaleQuotes(120)
  const session = useMarketSession()
  const broker = useBrokerSession()

  const feeds = ingestors.data ?? []
  const healthy = feeds.filter((f) => f.isHealthy)
  const lastBeat = feeds[0]?.lastHeartbeatUtc
  const marketOpen = session.data?.isMarketOpen ?? false

  const covRows = coverage.data ?? []
  const totalBars = covRows.reduce((sum, r) => sum + r.barCount, 0)
  const coveredSymbols = new Set(covRows.map((r) => r.symbol)).size

  const feedTone = !process.data?.isRunning
    ? undefined
    : healthy.length === feeds.length && feeds.length > 0
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

      <div className="stat-grid">
        <StatTile
          label="Live ingestor"
          value={process.data?.isRunning ? 'Running' : 'Stopped'}
          tone={feedTone as 'pos' | 'warn' | undefined}
          sub={
            feeds.length > 0
              ? `${healthy.length}/${feeds.length} sources healthy · beat ${formatAge(lastBeat)}`
              : 'no heartbeat recorded yet'
          }
          to="/admin/data/live"
        />
        <StatTile
          label="Market session"
          value={marketOpen ? 'NSE open' : 'NSE closed'}
          tone={marketOpen ? 'pos' : undefined}
          sub={
            session.data && !marketOpen
              ? `next open ${new Date(session.data.nextMarketOpenUtc).toLocaleString('en-IN', {
                  day: '2-digit',
                  month: 'short',
                  hour: '2-digit',
                  minute: '2-digit',
                })}`
              : 'equities · cash segment'
          }
        />
        <StatTile
          label="Broker (FYERS)"
          value={broker.data?.isAuthenticated ? 'Linked' : 'Not linked'}
          tone={broker.data?.isAuthenticated ? 'pos' : 'neg'}
          sub={broker.data?.isAuthenticated ? 'history sync + live stream ready' : 'connect before starting the feed'}
          to="/admin/broker"
        />
        <StatTile
          label="Watchlist"
          value={formatNumber(watchlist.data?.length ?? 0)}
          sub={`${formatNumber(quotes.data?.length ?? 0)} symbols with live quotes`}
          to="/admin/data/live"
        />
        <StatTile
          label="Stored history"
          value={formatNumber(totalBars)}
          sub={`bars across ${coveredSymbols} symbols`}
          to="/admin/data/historical"
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
          <Link className="btn btn--sm" to="/admin/data/historical">
            Browse historical <IconArrowRight style={{ width: 13, height: 13 }} />
          </Link>
        }
      >
        <QueryBoundary
          query={coverage}
          empty="No stored candles yet. Use Historical → Backfill to pull data from FYERS."
        >
          {(rows) => {
            const { resolutions, matrix, categories } = buildMatrix(rows)
            return (
              <div className="tablewrap">
                <table className="table">
                  <thead>
                    <tr>
                      <th>Category</th>
                      {resolutions.map((r) => (
                        <th key={r} className="r">
                          {formatResolution(r)}
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
                        {resolutions.map((res) => {
                          const cell = matrix.get(cat)?.get(res)
                          return (
                            <td key={res} className="r">
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
          Counts combine broker backfill (candles) and live-captured 1m bars. Tick-level capture is
          visible per symbol under Live feeds.
        </p>
      </Panel>

      <div className="two-col">
        <Panel
          title={
            <>
              <IconPulse /> Live pipeline
            </>
          }
        >
          <QueryBoundary
            query={ingestors}
            empty="No ingestor has ever reported a heartbeat. Start the live feed from Live feeds."
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
                    </div>
                    <div className="muted" style={{ fontSize: 12 }}>
                      {s.currentSubscribedSymbols.length} symbols subscribed
                    </div>
                    {s.lastError && <p className="neg" style={{ margin: '4px 0 0' }}>{s.lastError}</p>}
                  </div>
                ))}
              </div>
            )}
          </QueryBoundary>
        </Panel>

        <Panel
          title={
            <>
              <IconCandles /> Quick actions
            </>
          }
        >
          <div className="checklist">
            <Link to="/admin/data/live" className="checklist__item">
              <span className="checklist__state">
                <IconPulse style={{ width: 13, height: 13 }} />
              </span>
              <span className="checklist__body">
                <span className="checklist__label">Manage live feeds</span>
                <div className="checklist__hint">Start/stop the ingestor, edit the watchlist</div>
              </span>
              <span className="checklist__go">Open →</span>
            </Link>
            <Link to="/admin/data/historical" className="checklist__item">
              <span className="checklist__state">
                <IconCandles style={{ width: 13, height: 13 }} />
              </span>
              <span className="checklist__body">
                <span className="checklist__label">Browse & backfill history</span>
                <div className="checklist__hint">Coverage-first candle browser with FYERS backfill</div>
              </span>
              <span className="checklist__go">Open →</span>
            </Link>
            <Link to="/admin/data/instruments" className="checklist__item">
              <span className="checklist__state">
                <IconServer style={{ width: 13, height: 13 }} />
              </span>
              <span className="checklist__body">
                <span className="checklist__label">Instruments & F&O</span>
                <div className="checklist__hint">Search the master, explore expiries and option chains</div>
              </span>
              <span className="checklist__go">Open →</span>
            </Link>
          </div>
        </Panel>
      </div>
    </div>
  )
}
