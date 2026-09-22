/**
 * Strategies module — one strategy's lifetime record, under its specification.
 * Everything the strategy has actually done live: the rollup
 * (GET /api/Strategy/{id}/track-record) above every run it has ever had
 * (GET /api/Strategy/runs?strategyId=…, no date floor), each one a link to the
 * run that produced it.
 *
 * A trader's record is their own runs and an admin's is the desk's — the API
 * scopes it from the token, and the panel says which it is showing rather than
 * leaving the reader to assume. Backtests are not in here: a backtest is a
 * hypothesis and a live run is a result.
 */

import { Link, useNavigate } from 'react-router-dom'
import { RUN_HISTORY_PAGE, useLiveRunHistoryPages, useStrategyTrackRecord } from '../../lib/queries'
import { formatDateTime, formatDuration, formatInrSigned, formatLots, formatNumber } from '../../lib/format'
import { runDurationSeconds, runNetPnl } from '../../lib/runHistory'
import { InlineError, Loading, StatTile } from '../../components/ui'
import type { StrategyTrackRecord, StrategyTrackRecordRunRef } from '../../lib/types'
import { PnlValue } from './shared'
import { RunStatusCell } from './RunHistoryPage'

/** Runs the table asks for per request — the API's cap, so "Load older" is rare. */
const TAKE = RUN_HISTORY_PAGE

/**
 * "12 Aug 2026" — the record's span is read in days, not minutes. The shared
 * `formatDay` drops the year, which is exactly the digit a lifetime record
 * needs: two runs a year apart must not both read "12 Aug".
 */
function formatRecordDay(iso: string | null | undefined): string {
  if (!iso) return '—'
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return '—'
  return d.toLocaleDateString('en-IN', { day: '2-digit', month: 'short', year: 'numeric', timeZone: 'Asia/Kolkata' })
}

/** "57.1%", or "—" until a run has finished and the rate means something. */
function formatWinRate(rate: number | null): string {
  return rate == null ? '—' : `${rate.toFixed(1)}%`
}

/** A best/worst run as a link: "#412 · BANKNIFTY · 02 Sep". */
function RunRefLink({ run, href }: { run: StrategyTrackRecordRunRef; href: string }) {
  return (
    <Link to={href} className="mono" title={`Run #${run.runId}, started ${formatDateTime(run.startedUtc)}`}>
      #{run.runId}
    </Link>
  )
}

/* ------------------------------------------------------------------ rollup */

function Rollup({ record, runHref }: { record: StrategyTrackRecord; runHref: (runId: number) => string }) {
  const own = record.scope === 'own'
  const live = record.activeRuns > 0

  return (
    <>
      <div className="stat-grid">
        <StatTile
          label="Net P&L"
          value={<PnlValue value={record.netPnl} />}
          tone={record.netPnl > 0 ? 'pos' : record.netPnl < 0 ? 'neg' : undefined}
          sub={
            live && record.openPnl !== 0 ? (
              <>open {formatInrSigned(record.openPnl)}</>
            ) : record.pnlIsGrossOfCharges ? (
              <span className="faint">before charges</span>
            ) : undefined
          }
        />
        <StatTile
          label="Win rate"
          value={formatWinRate(record.winRate)}
          sub={
            record.decidedRuns === 0 ? (
              <span className="faint">no finished run yet</span>
            ) : (
              <>
                {record.wins}W · {record.losses}L
                {record.flat > 0 && <> · {record.flat} flat</>}
              </>
            )
          }
        />
        <StatTile
          label="Runs"
          value={formatNumber(record.runs)}
          sub={
            <>
              {live ? <b className="pos">{record.activeRuns} live</b> : <span className="faint">none live</span>}
              {record.alertRuns > 0 && <> · {record.alertRuns} alerts-only</>}
            </>
          }
        />
        <StatTile
          label="Days out"
          value={formatNumber(record.tradingDays)}
          sub={
            record.firstRunUtc ? (
              <>
                {formatRecordDay(record.firstRunUtc)} → {formatRecordDay(record.lastRunUtc)}
              </>
            ) : undefined
          }
        />
        <StatTile
          label="Per finished run"
          value={<PnlValue value={record.averagePnlPerRun} />}
          sub={
            record.decidedRuns === 0 ? (
              <span className="faint">—</span>
            ) : (
              <>over {formatNumber(record.decidedRuns)} finished</>
            )
          }
        />
        <StatTile
          label="Trades"
          value={formatNumber(record.trades)}
          sub={
            record.averageRuntimeSeconds != null ? (
              <>avg run {formatDuration(record.averageRuntimeSeconds)}</>
            ) : undefined
          }
        />
      </div>

      {(record.bestRun || record.worstRun) && (
        <p className="small-note" style={{ marginTop: 10 }}>
          {record.bestRun && (
            <>
              Best run <RunRefLink run={record.bestRun} href={runHref(record.bestRun.runId)} />{' '}
              <span className="mono pos">{formatInrSigned(record.bestRun.netPnl)}</span> on{' '}
              <span className="mono">{record.bestRun.underlying}</span>, {formatRecordDay(record.bestRun.startedUtc)}
            </>
          )}
          {record.bestRun && record.worstRun && ' · '}
          {record.worstRun && (
            <>
              Worst <RunRefLink run={record.worstRun} href={runHref(record.worstRun.runId)} />{' '}
              <span className="mono neg">{formatInrSigned(record.worstRun.netPnl)}</span> on{' '}
              <span className="mono">{record.worstRun.underlying}</span>, {formatRecordDay(record.worstRun.startedUtc)}
            </>
          )}
        </p>
      )}

      <p className="small-note">
        {own ? 'Your live runs only' : 'Every user’s live runs'} · win rate counts the{' '}
        {formatNumber(record.decidedRuns)} finished{' '}
        {record.decidedRuns === 1 ? 'run' : 'runs'} — a run still going has not won or lost yet
        {record.alertRuns > 0 && ' · alerter runs place no orders and carry no P&L'}
        {record.pnlIsGrossOfCharges && (
          <>
            {' '}
            · <b>P&L is gross of brokerage, STT and slippage</b> — live runs carry no charges today, so these
            figures read better than the same trades would on a real account
          </>
        )}
        .
      </p>
    </>
  )
}

/* -------------------------------------------------------------- breakdowns */

function ByUnderlying({ record }: { record: StrategyTrackRecord }) {
  if (record.byUnderlying.length === 0) return null
  return (
    <div className="track__break">
      <h3 className="track__break-title">Per underlying</h3>
      <div className="tablewrap">
        <table className="table">
          <thead>
            <tr>
              <th>Underlying</th>
              <th className="r">Runs</th>
              <th className="r">Won</th>
              <th className="r">Net P&L</th>
            </tr>
          </thead>
          <tbody>
            {record.byUnderlying.map((u) => (
              <tr key={u.underlying}>
                <td className="mono">{u.underlying}</td>
                <td className="r">{formatNumber(u.runs)}</td>
                <td className="r muted">
                  {u.decidedRuns === 0 ? <span className="faint">—</span> : `${u.wins}/${u.decidedRuns}`}
                </td>
                <td className="r">
                  <PnlValue value={u.netPnl} />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}

function StopReasons({ record }: { record: StrategyTrackRecord }) {
  if (record.stopReasons.length === 0) return null
  return (
    <div className="track__break">
      <h3 className="track__break-title">How the runs ended</h3>
      <div className="tablewrap">
        <table className="table">
          <thead>
            <tr>
              <th>Reason</th>
              <th className="r">Runs</th>
              <th className="r">Net P&L</th>
            </tr>
          </thead>
          <tbody>
            {record.stopReasons.map((r) => (
              <tr key={r.reason}>
                <td>{r.reason}</td>
                <td className="r">{formatNumber(r.runs)}</td>
                <td className="r">
                  <PnlValue value={r.netPnl} />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}

/* --------------------------------------------------------------- every run */

function RunTable({
  strategyId,
  runHref,
  isAdmin,
}: {
  strategyId: number
  runHref: (runId: number) => string
  isAdmin: boolean
}) {
  const navigate = useNavigate()
  // No date floor: this is the lifetime list, paged from the newest back.
  const history = useLiveRunHistoryPages({ strategyId, take: TAKE })
  const rows = history.data?.pages.flat() ?? []

  if (history.isPending) return <Loading label="Loading runs…" />
  if (history.isError && history.data === undefined) return <InlineError error={history.error} />
  if (rows.length === 0) return null

  return (
    <div className="track__runs">
      <h3 className="track__break-title">
        Every run <span className="faint">({formatNumber(rows.length)} loaded, newest first)</span>
      </h3>
      {history.isError && (
        <p className="small-note warn" role="status" style={{ margin: '0 0 8px' }}>
          Refresh failed — showing the last loaded runs.
        </p>
      )}
      <div className="tablewrap tablewrap--tall">
        <table className="table table--hover">
          <thead>
            <tr>
              <th>Run #</th>
              {isAdmin && <th>User</th>}
              <th>Underlying</th>
              <th className="r">Lots × lot size</th>
              <th>Started</th>
              <th>Stopped</th>
              <th className="r">Duration</th>
              <th className="r">Trades</th>
              <th className="r">Net P&L</th>
              <th>Status</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {rows.map((run) => {
              const to = runHref(run.runId)
              return (
                <tr
                  key={run.runId}
                  className={run.isActive ? 'row--live' : ''}
                  onClick={(e) => {
                    // Plain clicks open the run; modified clicks and clicks on
                    // the explicit link keep their browser behaviour.
                    if (e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return
                    if ((e.target as HTMLElement).closest('a')) return
                    navigate(to)
                  }}
                >
                  <td className="mono muted">#{run.runId}</td>
                  {isAdmin && <td>{run.userName || <span className="faint">user {run.userId}</span>}</td>}
                  <td className="mono" title={run.spotSymbol || undefined}>
                    {run.underlying}
                  </td>
                  <td className="r">
                    {run.role === 'alerts' ? <span className="muted">alerts only</span> : formatLots(run.lots, run.lotSize)}
                  </td>
                  <td className="muted">{formatDateTime(run.startedUtc)}</td>
                  <td className="muted">{run.isActive ? <span className="faint">—</span> : formatDateTime(run.stoppedUtc)}</td>
                  <td className="r mono muted">{formatDuration(runDurationSeconds(run))}</td>
                  <td className="r" title={run.openPositions > 0 ? `${run.openPositions} open` : undefined}>
                    {formatNumber(run.trades)}
                    {run.openPositions > 0 && <span className="cell-sub">{run.openPositions} open</span>}
                  </td>
                  <td className="r">
                    <PnlValue value={runNetPnl(run)} />
                    {run.isActive && run.unrealizedPnl !== 0 && (
                      <span className="cell-sub">unrealized {formatInrSigned(run.unrealizedPnl)}</span>
                    )}
                  </td>
                  <td>
                    <RunStatusCell run={run} />
                  </td>
                  <td className="r">
                    <Link to={to}>Detail →</Link>
                  </td>
                </tr>
              )
            })}
          </tbody>
        </table>
      </div>
      {history.hasNextPage && (
        <p className="small-note" style={{ margin: '8px 0 0' }}>
          The newest {formatNumber(rows.length)} runs are loaded — older ones exist.{' '}
          <button
            type="button"
            className="btn btn--ghost btn--sm"
            onClick={() => void history.fetchNextPage()}
            disabled={history.isFetchingNextPage}
          >
            {history.isFetchingNextPage ? 'Loading older…' : `Load older ${formatNumber(TAKE)}`}
          </button>{' '}
          <span className="faint">The rollup above already counts every one of them.</span>
        </p>
      )}
    </div>
  )
}

/* -------------------------------------------------------------------- page */

export function TrackRecordPanel({
  strategyId,
  runHref,
  isAdmin,
  historyHref,
}: {
  strategyId: number
  runHref: (runId: number) => string
  /** Admins see whose run each row was; a trader's rows are all their own. */
  isAdmin: boolean
  /** The Run history page, pre-filtered to this strategy. */
  historyHref: string
}) {
  const record = useStrategyTrackRecord(strategyId)

  if (record.isPending) return <Loading label="Loading track record…" />
  if (record.isError) return <InlineError error={record.error} />
  if (!record.data) return null

  const r = record.data

  if (r.runs === 0) {
    return (
      <p className="empty" style={{ margin: 0 }}>
        {r.scope === 'own'
          ? 'You have never run this strategy live. Once you do, every run shows up here — when it ran, on what, and what came of it.'
          : 'This strategy has never run live. Once it does, every run shows up here — when it ran, on what, for whom, and what came of it.'}
      </p>
    )
  }

  return (
    <div className="track">
      <Rollup record={r} runHref={runHref} />
      <div className="track__breaks">
        <ByUnderlying record={r} />
        <StopReasons record={r} />
      </div>
      <RunTable strategyId={strategyId} runHref={runHref} isAdmin={isAdmin} />
      <p className="small-note">
        Live runs only — backtests are a separate history and are never added in here.{' '}
        <Link to={historyHref}>Open in Run history →</Link>
      </p>
    </div>
  )
}
