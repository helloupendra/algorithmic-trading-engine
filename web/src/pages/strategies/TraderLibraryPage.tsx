/**
 * Trade → Library, as a trader sees it: what they have running and how it is
 * doing, their last few runs, and the strategies their package allows, each
 * with Deploy and its "How it works" page. The API scopes every list to the
 * trader asking.
 *
 * An admin's Library is the full catalogue (StrategyLibraryPage). This page
 * was also the admin's Strategies overview once; the Desk's runs grid and
 * Trade → Runs replaced that, and its admin half went with it.
 */

import { useEffect, useMemo, useState } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import {
  useKillSwitch,
  useLiveRunHistory,
  useRiskExposure,
  useRiskLimits,
  useStrategies,
  useStrategyLivesRepriced,
} from '../../lib/queries'
import { formatDateTime, formatDuration, formatNumber } from '../../lib/format'
import { runDurationSeconds, runNetPnl } from '../../lib/runHistory'
import { Panel, QueryBoundary, StatTile } from '../../components/ui'
import { IconClock, IconFlask, IconPlay } from '../../components/icons'
import type { StrategyActiveRun, StrategyListItem, StrategyLiveView } from '../../lib/types'
import { CategoryBadge, LaunchDialog, PnlValue, StrategyCard } from './shared'
import { RunStatusCell } from './RunHistoryPage'
import { liveNet, runningSummary } from '../../lib/strategyList'
import { todayIst } from '../backtesting/shared'

interface RunRow {
  strategy: StrategyListItem
  run: StrategyActiveRun
}

const RECENT_RUNS = 6
const HISTORY = '/trade/history'
const POSITIONS = '/trade/positions'
const runLink = (runId: number) => `/trade/runs/${runId}`

export function TraderLibraryPage() {
  const strategies = useStrategies()
  const list = useMemo(() => strategies.data ?? [], [strategies.data])
  const runningStrategies = useMemo(() => list.filter((s) => s.activeRuns.length > 0), [list])
  const rows = useMemo<RunRow[]>(
    () => list.flatMap((s) => s.activeRuns.map((run) => ({ strategy: s, run }))),
    [list],
  )
  const runIds = useMemo(() => rows.map((r) => r.run.runId), [rows])

  // Re-priced at the pushed prices of each run's legs, as its run card is:
  // with the socket up a view is read every 15 s, and the prices move between.
  const views = useStrategyLivesRepriced(runIds)
  const viewByRun = new Map<number, StrategyLiveView>()
  for (const v of views) if (v?.runId != null) viewByRun.set(v.runId, v)

  const openPositions = rows.reduce(
    (n, r) => n + (viewByRun.get(r.run.runId)?.positions.filter((p) => p.status === 'Open').length ?? 0),
    0,
  )
  const livePnl = rows.reduce((n, r) => {
    const view = viewByRun.get(r.run.runId)
    return n + (view ? liveNet(view.pnl) : 0)
  }, 0)

  // Run history: today's runs for the tile, the newest six for the list.
  const today = todayIst()
  const todayFilters = useMemo(() => ({ fromDate: today, toDate: today, take: 500 }), [today])
  const todayRuns = useLiveRunHistory(todayFilters)
  const recentFilters = useMemo(() => ({ take: RECENT_RUNS }), [])
  const recent = useLiveRunHistory(recentFilters)
  const todayList = todayRuns.data ?? []
  const todayPnl = todayList.reduce((n, r) => n + runNetPnl(r), 0)

  // Only what concerns a trader: a halt they must respect and a ceiling they
  // have hit. The broker link and the feed are the operator's job — a trader is
  // never told "FYERS is not signed in", because there is nothing they can do
  // about it.
  const killSwitch = useKillSwitch()
  const exposure = useRiskExposure()
  const riskLimits = useRiskLimits()
  const blockers: { text: string; to: string }[] = []
  if (killSwitch.data?.isActive)
    blockers.push({ text: 'Trading is halted by the operator (kill switch) — new runs are refused', to: '/desk' })
  if (
    exposure.data &&
    riskLimits.data &&
    riskLimits.data.maxConcurrentRuns > 0 &&
    exposure.data.activeRunsCount >= riskLimits.data.maxConcurrentRuns
  )
    blockers.push({
      text: `Concurrent runs limit reached (${exposure.data.activeRunsCount}/${riskLimits.data.maxConcurrentRuns}) — stop a run first`,
      to: HISTORY,
    })

  const navigate = useNavigate()
  const [launching, setLaunching] = useState<StrategyListItem | null>(null)

  // "Deploy this strategy…" on a How-it-works page lands here with the strategy
  // in the address; the dialog opens on it as soon as the catalogue arrives,
  // and the address is cleaned so a refresh does not reopen it.
  const [search, setSearch] = useSearchParams()
  useEffect(() => {
    const wanted = Number(search.get('strategy'))
    if (!Number.isInteger(wanted) || wanted <= 0 || !strategies.data) return
    const found = strategies.data.find((s) => s.id === wanted)
    if (found) setLaunching(found)
    setSearch({}, { replace: true })
  }, [search, setSearch, strategies.data])

  return (
    <div className="page">
      <header className="page__header">
        <div>
          <h1 className="page__title">Strategies</h1>
          <p className="page__subtitle">What you have running, how it is doing, and the strategies your package allows.</p>
        </div>
      </header>

      {blockers.length > 0 && (
        <div className="alert alert--error" role="alert">
          <b>Not ready to trade:</b>
          {blockers.map((b) => (
            <div key={b.text}>
              • {b.text} — <Link to={b.to}>see →</Link>
            </div>
          ))}
        </div>
      )}

      <div className="stat-grid">
        <StatTile
          label={rows.length === 1 ? 'Running run' : 'Running runs'}
          value={rows.length}
          tone={rows.length > 0 ? 'pos' : undefined}
          sub={
            runningStrategies.length > 0
              ? runningStrategies.map((s, i) => (
                  <span key={s.id}>
                    {i > 0 && <br />}
                    {runningSummary(s)}
                  </span>
                ))
              : 'nothing running'
          }
          to={POSITIONS}
        />
        <StatTile label="Open positions" value={openPositions} sub="across running runs" to={POSITIONS} />
        <StatTile
          label="Live P&L"
          value={<PnlValue value={livePnl} />}
          tone={livePnl > 0 ? 'pos' : livePnl < 0 ? 'neg' : undefined}
          sub="net of charges · open and closed"
          to={POSITIONS}
        />
        <StatTile
          label="Runs today"
          value={todayRuns.data ? formatNumber(todayList.length) : '—'}
          tone={todayPnl > 0 ? 'pos' : todayPnl < 0 ? 'neg' : undefined}
          sub={
            todayRuns.data
              ? todayList.length > 0
                ? <>net <PnlValue value={todayPnl} /> · your runs · incl. stopped</>
                : 'none started yet — all in Run history'
              : todayRuns.isError
                ? 'history unavailable'
                : 'loading…'
          }
          to={HISTORY}
        />
        <StatTile label="In my package" value={list.length} sub="strategies you may deploy" />
      </div>

      <Panel
        title={
          <>
            <IconPlay /> Running now
          </>
        }
        actions={
          <Link className="btn btn--sm" to={HISTORY}>
            My runs
          </Link>
        }
      >
        <QueryBoundary query={strategies}>
          {() =>
            rows.length === 0 ? (
              <p className="empty">Nothing of yours is running. Pick a strategy below and press Deploy.</p>
            ) : (
              <div className="tablewrap">
                <table className="table">
                  <thead>
                    <tr>
                      <th>Strategy</th>
                      <th>Underlying</th>
                      <th className="r">Open</th>
                      <th className="r">P&L</th>
                      <th>Started</th>
                      <th />
                    </tr>
                  </thead>
                  <tbody>
                    {rows.map(({ strategy: s, run }) => {
                      const v = viewByRun.get(run.runId)
                      return (
                        <tr key={run.runId}>
                          <td>
                            <b>{s.name}</b> <CategoryBadge category={s.category} />
                            <span className="faint"> · #{run.runId}</span>
                          </td>
                          <td className="mono">{v?.underlying ?? run.underlying}</td>
                          <td className="r">{v ? v.positions.filter((p) => p.status === 'Open').length : '—'}</td>
                          <td className="r">{v ? <PnlValue value={v.pnl.total} /> : '—'}</td>
                          <td className="muted">
                            {run.startedBy ? `${run.startedBy} · ` : ''}
                            {formatDateTime(run.startedUtc)}
                          </td>
                          <td className="r">
                            <Link to={runLink(run.runId)}>Positions →</Link>
                          </td>
                        </tr>
                      )
                    })}
                  </tbody>
                </table>
              </div>
            )
          }
        </QueryBoundary>
      </Panel>

      <Panel
        title={
          <>
            <IconClock /> Recent runs
          </>
        }
        actions={
          <Link className="btn btn--sm" to={HISTORY}>
            All history →
          </Link>
        }
      >
        <QueryBoundary query={recent} empty="No runs yet — deploy a strategy below and it will appear here.">
          {(runs) => (
            <div className="tablewrap">
              <table className="table">
                <thead>
                  <tr>
                    <th>Run #</th>
                    <th>Strategy</th>
                    <th>Underlying</th>
                    <th>Started</th>
                    <th className="r">Duration</th>
                    <th className="r">Trades</th>
                    <th className="r">Net P&L</th>
                    <th>Status</th>
                    <th />
                  </tr>
                </thead>
                <tbody>
                  {runs.slice(0, RECENT_RUNS).map((run) => (
                    <tr key={run.runId} className={run.isActive ? 'row--live' : ''}>
                      <td className="mono muted">#{run.runId}</td>
                      <td>
                        <b>{run.strategyName}</b> <CategoryBadge category={run.category} />
                      </td>
                      <td className="mono">{run.underlying}</td>
                      <td className="muted">{formatDateTime(run.startedUtc)}</td>
                      <td className="r mono muted">{formatDuration(runDurationSeconds(run))}</td>
                      <td className="r">{formatNumber(run.trades)}</td>
                      <td className="r">
                        <PnlValue value={runNetPnl(run)} />
                      </td>
                      <td>
                        <RunStatusCell run={run} />
                      </td>
                      <td className="r">
                        <Link to={runLink(run.runId)}>Detail →</Link>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </QueryBoundary>
      </Panel>

      <Panel
        title={
          <>
            <IconFlask /> Deploy a strategy
          </>
        }
      >
        <QueryBoundary query={strategies} empty="No strategies in your package yet — ask the operator.">
          {(all) => (
            <div className="strategy-grid">
              {all.map((s) => (
                <StrategyCard
                  key={s.id}
                  strategy={s}
                  actionLabel="Deploy…"
                  specHref={`/trade/library/${s.id}`}
                  onStart={(st) => setLaunching(st)}
                />
              ))}
            </div>
          )}
        </QueryBoundary>
      </Panel>

      {launching && (
        <LaunchDialog strategy={launching} onClose={() => setLaunching(null)} onStarted={(r) => navigate(runLink(r.runId))} />
      )}
    </div>
  )
}
