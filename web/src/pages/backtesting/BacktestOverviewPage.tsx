/**
 * Backtesting module — Overview. The most recent runs come first, then the
 * data there is to replay: one line per index (how many sessions, at which
 * resolutions, over which days), each opening onto its stored ranges. It
 * opened with every range of every index, 37 rows with ditto marks, and the
 * results under them. "New backtest" leads to the strategy grid.
 */

import { useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import {
  useBacktestRuns,
  useBrokerSession,
  useDataCoverage,
  useFnoUnderlyings,
  type CoverageRow,
} from '../../lib/queries'
import { formatNumber, formatPercent } from '../../lib/format'
import { classifySymbol, instrumentName, resolutionLabel, resolutionRank } from '../../lib/symbols'
import { Badge, Panel, QueryBoundary, StatTile } from '../../components/ui'
import { IconArrowRight, IconChevronDown, IconChevronRight, IconDatabase, IconFlask, IconPlus } from '../../components/icons'
import type { BacktestRunSummary, FnoUnderlying } from '../../lib/types'
import { PnlValue } from '../strategies/shared'
import { BackfillDialog } from './BackfillDialog'
import { estimateSessions, foldIndexCoverage, sessionsOnHand } from './readings'
import {
  BacktestStatusBadge,
  formatDayRange,
  isActiveStatus,
  runSpecLabel,
} from './shared'

interface IndexLine {
  underlying: string | null
  spotSymbol: string
  /** Its stored ranges, finest first; none when nothing is stored. */
  rows: CoverageRow[]
}

/**
 * One line per index that has data, plus one per F&O index underlying that
 * has none — so the backfill button exists for it.
 */
function buildLines(rows: CoverageRow[], fno: FnoUnderlying[]): IndexLine[] {
  const bySpot = new Map<string, FnoUnderlying>()
  for (const u of fno) bySpot.set(u.spotSymbol.toUpperCase(), u)

  const byIndex = new Map<string, IndexLine>()
  const indexRows = rows
    .filter((r) => classifySymbol(r.symbol) === 'Index')
    .sort(
      (a, b) =>
        a.symbol.localeCompare(b.symbol) ||
        resolutionRank(a.resolution) - resolutionRank(b.resolution) ||
        a.source.localeCompare(b.source),
    )
  for (const r of indexRows) {
    const key = r.symbol.toUpperCase()
    const line = byIndex.get(key) ?? { underlying: bySpot.get(key)?.underlying ?? null, spotSymbol: r.symbol, rows: [] }
    line.rows.push(r)
    byIndex.set(key, line)
  }

  const lines = [...byIndex.values()]
  for (const u of fno) {
    const key = u.spotSymbol.toUpperCase()
    if (!key.endsWith('-INDEX') || byIndex.has(key)) continue
    lines.push({ underlying: u.underlying, spotSymbol: u.spotSymbol, rows: [] })
  }
  return lines
}

/** "03 Aug 2020": a stored range spans years, so its days carry theirs. */
function dayWithYear(iso: string | null): string {
  if (!iso) return '—'
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? '—' : d.toLocaleDateString('en-IN', { day: '2-digit', month: 'short', year: 'numeric', timeZone: 'Asia/Kolkata' })
}

const yearRange = (from: string | null, to: string | null) => `${dayWithYear(from)} → ${dayWithYear(to)}`

/**
 * An index in one line — "NIFTY ≈ 1,528 days · 1m to daily" and the days it
 * reaches — that opens onto a row per stored range: the resolution, its
 * days, sessions and bars, and whether it was backfilled or recorded live.
 */
function IndexCoverageRows({
  line,
  open,
  onToggle,
  onBackfill,
}: {
  line: IndexLine
  open: boolean
  onToggle: () => void
  onBackfill: () => void
}) {
  // An index that is no F&O underlying (India VIX) by its name, not its symbol.
  const name = line.underlying ?? instrumentName({ symbol: line.spotSymbol })
  const fold = foldIndexCoverage(line.rows)
  const backfill = (
    <td className="r">
      <button
        type="button"
        className="btn btn--sm"
        disabled={!line.underlying}
        title={line.underlying ? `Fetch ${name} index candles from FYERS` : 'Not an F&O underlying in the instrument master'}
        onClick={onBackfill}
      >
        Backfill…
      </button>
    </td>
  )

  if (line.rows.length === 0) {
    return (
      <tr>
        <td className="mono">
          <span className="coverage-table__underlying" title={line.spotSymbol}>
            {name}
          </span>
        </td>
        <td colSpan={3} className="coverage-table__none">
          No index candles stored — backfill to make {name} backtestable.
        </td>
        {backfill}
      </tr>
    )
  }

  return (
    <>
      <tr>
        <td className="mono">
          <button
            type="button"
            className="coverage-table__toggle"
            aria-expanded={open}
            title={`${line.spotSymbol}: ${open ? 'hide' : 'show'} its ${line.rows.length} stored ranges`}
            onClick={onToggle}
          >
            {open ? <IconChevronDown /> : <IconChevronRight />}
            <span className="coverage-table__underlying">{name}</span>
          </button>
        </td>
        <td className="r">≈ {formatNumber(fold.sessions)} days</td>
        <td>{fold.resolutions}</td>
        <td className="muted">{yearRange(fold.fromUtc, fold.toUtc)}</td>
        {backfill}
      </tr>
      {open && (
        <tr className="coverage-table__detail">
          <td colSpan={5}>
            <table className="table">
              <thead>
                <tr>
                  <th>Resolution</th>
                  <th>From → To</th>
                  <th className="r">Sessions</th>
                  <th className="r">Bars</th>
                  <th>Source</th>
                </tr>
              </thead>
              <tbody>
                {line.rows.map((r) => (
                  <tr key={`${r.resolution}|${r.source}`}>
                    <td>
                      <Badge tone="neutral">{resolutionLabel(r.resolution)}</Badge>
                    </td>
                    <td className="muted">{yearRange(r.fromUtc, r.toUtc)}</td>
                    <td className="r">≈ {formatNumber(estimateSessions(r.resolution, r.barCount))}</td>
                    <td className="r">{formatNumber(r.barCount)}</td>
                    <td>
                      <Badge tone={r.source === 'live' ? 'live' : 'accent'}>{r.source}</Badge>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </td>
        </tr>
      )}
    </>
  )
}

function RecentRunRow({ run }: { run: BacktestRunSummary }) {
  return (
    <tr>
      <td>
        <Link to={`/research/backtests/runs/${run.runId}`}>
          <b>{run.strategyName}</b>
        </Link>
        <span className="faint"> · #{run.runId}</span>
      </td>
      <td className="mono">{run.underlying}</td>
      <td className="muted">
        {resolutionLabel(run.resolution)} · {formatDayRange(run.fromDate, run.toDate)}
      </td>
      <td className="r">{formatNumber(run.lots)}</td>
      <td className="r">
        <PnlValue value={run.netPnl} />
      </td>
      <td className="r">{formatNumber(run.trades)}</td>
      <td className="r muted">{run.trades > 0 ? formatPercent(run.winRatePercent, 0).replace('+', '') : '—'}</td>
      <td>
        <BacktestStatusBadge status={run.status} progressPercent={run.progressPercent} stopReason={run.stopReason} />
      </td>
    </tr>
  )
}

export function BacktestOverviewPage() {
  const runs = useBacktestRuns()
  const coverage = useDataCoverage()
  const fno = useFnoUnderlyings()
  const broker = useBrokerSession()
  const [backfillFor, setBackfillFor] = useState<{ underlying: string; spotSymbol: string } | null>(null)
  const [opened, setOpened] = useState<ReadonlySet<string>>(() => new Set())
  const toggle = (spotSymbol: string) =>
    setOpened((was) => {
      const next = new Set(was)
      if (!next.delete(spotSymbol)) next.add(spotSymbol)
      return next
    })

  const runList = useMemo(() => runs.data ?? [], [runs.data])
  const running = runList.filter((r) => isActiveStatus(r.status))
  const best = runList
    .filter((r) => (r.status === 'Completed' || r.status === 'Stopped') && r.trades > 0)
    .reduce<BacktestRunSummary | null>((acc, r) => (acc == null || r.netPnl > acc.netPnl ? r : acc), null)

  const lines = useMemo(() => buildLines(coverage.data ?? [], fno.data ?? []), [coverage.data, fno.data])
  // Each index once, at its best-covered resolution: the same days are stored
  // at 1m, 5m, 15m and 1D, and adding every range up counted them four times.
  // The total adds the indices, so it is index-days; the sub line says so and
  // names the longest single history, which is what one replay can reach.
  const onHand = useMemo(() => sessionsOnHand(lines.flatMap((l) => l.rows)), [lines])
  const brokerLinked = broker.data?.isAuthenticated ?? false
  const recent = runList.slice(0, 8)

  return (
    <div className="page">
      <header className="page__header">
        <div>
          <h1 className="page__title">Backtesting</h1>
          <p className="page__subtitle">
            Replay a catalogue strategy over stored index history — the same on_bar contract as the
            live runner, position-based results.
          </p>
        </div>
        <Link className="btn btn--primary" to="/research/backtests/new">
          <IconPlus style={{ width: 14, height: 14 }} /> New backtest
        </Link>
      </header>

      <div className="stat-grid">
        <StatTile
          label="Backtests run"
          value={runs.data ? formatNumber(runList.length) : '—'}
          sub={runs.data ? `${formatNumber(runList.filter((r) => r.status === 'Completed').length)} completed` : 'loading runs…'}
          to="/research/backtests/runs"
          stale={runs.isRefetchError}
        />
        <StatTile
          label="Running now"
          value={runs.data ? formatNumber(running.length) : '—'}
          tone={running.length > 0 ? 'accent' : undefined}
          sub={
            running.length > 0
              ? running.map((r) => `${r.strategyName} ${Math.round(r.progressPercent)}%`).join(', ')
              : 'nothing replaying'
          }
          to="/research/backtests/runs"
          stale={runs.isRefetchError}
        />
        <StatTile
          label="Best net P&L"
          value={best ? <PnlValue value={best.netPnl} /> : '—'}
          tone={best ? (best.netPnl > 0 ? 'pos' : best.netPnl < 0 ? 'neg' : undefined) : undefined}
          sub={best ? `${best.strategyName} · ${best.underlying} · ${formatDayRange(best.fromDate, best.toDate)}` : 'no finished run with trades yet'}
          to={best ? `/research/backtests/runs/${best.runId}` : undefined}
          stale={runs.isRefetchError}
        />
        <StatTile
          label="Data sessions available"
          value={!coverage.data ? '—' : onHand.indices === 0 ? '0' : `≈ ${formatNumber(onHand.total)}`}
          sub={
            !coverage.data
              ? coverage.isError
                ? 'could not read the stored ranges'
                : 'reading the stored ranges…'
              : onHand.indices === 0
                ? 'no index candles stored yet'
                : onHand.indices === 1
                  ? 'sessions of one index'
                  : `index-days across ${formatNumber(onHand.indices)} indices · longest ≈ ${formatNumber(onHand.longest)}`
          }
          to="/data/historical"
          stale={coverage.isRefetchError || fno.isRefetchError}
        />
      </div>

      <Panel
        title={
          <>
            <IconFlask /> Recent backtests
          </>
        }
        actions={
          <Link className="btn btn--ghost btn--sm" to="/research/backtests/runs">
            All runs <IconArrowRight style={{ width: 12, height: 12 }} />
          </Link>
        }
      >
        <QueryBoundary query={runs}>
          {() =>
            recent.length === 0 ? (
              <p className="empty">
                No backtests yet. <Link to="/research/backtests/new">Start one</Link> from the strategy
                catalogue.
              </p>
            ) : (
              <div className="tablewrap">
                <table className="table">
                  <thead>
                    <tr>
                      <th>Strategy</th>
                      <th>Underlying</th>
                      <th>Range</th>
                      <th className="r">Lots</th>
                      <th className="r">Net P&L</th>
                      <th className="r">Trades</th>
                      <th className="r">Win rate</th>
                      <th>Status</th>
                    </tr>
                  </thead>
                  <tbody>
                    {recent.map((run) => (
                      <RecentRunRow key={run.runId} run={run} />
                    ))}
                  </tbody>
                </table>
              </div>
            )
          }
        </QueryBoundary>
        {recent.length > 0 && (
          <p className="small-note">
            Latest: {runSpecLabel(recent[0])} · {recent[0].strategyName}.
          </p>
        )}
      </Panel>

      <Panel
        title={
          <>
            <IconDatabase /> Historical data on hand
          </>
        }
        actions={
          <Link className="btn btn--ghost btn--sm" to="/data/historical">
            All ranges <IconArrowRight style={{ width: 12, height: 12 }} />
          </Link>
        }
      >
        <QueryBoundary query={coverage}>
          {() =>
            lines.length === 0 ? (
              <p className="empty">
                No index candles are stored yet, and no F&O index underlying is loaded to backfill
                for. Import the instrument master on{' '}
                <Link to="/data/instruments">Data › Instruments</Link> first.
              </p>
            ) : (
              <div className="tablewrap">
                <table className="table coverage-table">
                  <thead>
                    <tr>
                      <th>Index</th>
                      <th className="r">On hand</th>
                      <th>Resolutions</th>
                      <th>From → To</th>
                      <th />
                    </tr>
                  </thead>
                  <tbody>
                    {lines.map((l) => (
                      <IndexCoverageRows
                        key={l.spotSymbol}
                        line={l}
                        open={opened.has(l.spotSymbol)}
                        onToggle={() => toggle(l.spotSymbol)}
                        onBackfill={() => l.underlying && setBackfillFor({ underlying: l.underlying, spotSymbol: l.spotSymbol })}
                      />
                    ))}
                  </tbody>
                </table>
              </div>
            )
          }
        </QueryBoundary>
        <p className="small-note">
          Sessions are estimated from bar counts (75 × 5m or 375 × 1m bars per full NSE day); the
          launch dialog shows the exact count per underlying. Backtests read backfilled candles; live
          1m bars only cover the hours the ingestor ran.
        </p>
      </Panel>

      {backfillFor && (
        <BackfillDialog
          underlying={backfillFor.underlying}
          spotSymbol={backfillFor.spotSymbol}
          brokerLinked={brokerLinked}
          onClose={() => setBackfillFor(null)}
        />
      )}
    </div>
  )
}
