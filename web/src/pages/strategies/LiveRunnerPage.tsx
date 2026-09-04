/**
 * Strategies module — Live runner, v2.
 *
 * Position-based, not order-based: each RUN (a strategy on one underlying) is
 * one RunCard that shows its spot, P&L, risk limits and every position of the
 * run as a row (closed legs stay in the table with quantity 0 instead of
 * appearing as a separate "SELL order"). The same strategy may be live on
 * several underlyings at once, so cards, stops, logs and the live view are all
 * keyed by runId — never by strategy id. Starting goes through the
 * LaunchDialog, which makes the underlying mandatory and greys out the ones
 * the strategy is already running on.
 *
 * A stopped card can be dismissed from THIS list only — every run stays in
 * Run history, attached to the user who started it.
 */

import { useEffect, useMemo, useState } from 'react'
import type { ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { useStrategies, useStrategyLives } from '../../lib/queries'
import { InlineError, Loading, QueryBoundary, StatTile } from '../../components/ui'
import { IconClock, IconLayers, IconPlay } from '../../components/icons'
import type { StrategyActiveRun, StrategyLastExit, StrategyListItem, StrategyLiveView } from '../../lib/types'
import { LaunchDialog, PnlValue, ReadinessStrip, StrategyCard } from './shared'
import { RunCard } from './RunCard'
import { runningSummary } from '../../lib/strategyList'

/* ------------------------------------------------------------------ helpers */

/**
 * One card on the page. A live run carries its registry snapshot (`run`); a
 * finished one carries the exit record (`exit`). Both fall back to the live
 * view once it is loaded — the snapshot only paints the header before then.
 */
interface CardSpec {
  strategy: StrategyListItem
  runId: number
  run: StrategyActiveRun | null
  exit: StrategyLastExit | null
}

function isToday(iso: string | null | undefined): boolean {
  if (!iso) return false
  const d = new Date(iso)
  const now = new Date()
  return (
    d.getFullYear() === now.getFullYear() &&
    d.getMonth() === now.getMonth() &&
    d.getDate() === now.getDate()
  )
}

const DISMISS_TITLE = 'Hide from this list (stays in Run history)'

/* -------------------------------------------------------------------- page */

export function LiveRunnerPage() {
  const strategies = useStrategies()
  const [launchId, setLaunchId] = useState<number | null>(null)
  const [dismissed, setDismissed] = useState<ReadonlySet<number>>(() => new Set<number>())
  const [scrollTo, setScrollTo] = useState<number | null>(null)

  // The list request failed and there is nothing cached: the page cannot know
  // what is running, so it must say so rather than claim a flat book.
  const listUnknown = strategies.isError && strategies.data === undefined

  const list = useMemo(() => strategies.data ?? [], [strategies.data])
  const runningStrategies = useMemo(() => list.filter((s) => s.activeRuns.length > 0), [list])

  // One card per live run, in start order within each strategy.
  const running = useMemo<CardSpec[]>(
    () =>
      list.flatMap((s) =>
        s.activeRuns.map((run) => ({ strategy: s, runId: run.runId, run, exit: null })),
      ),
    [list],
  )
  // One card per recent exit until the operator dismisses it — a stopped
  // BANKNIFTY run stays on screen even while the NIFTY run of the same
  // strategy keeps going, and even after the strategy is started again.
  const stopped = useMemo<CardSpec[]>(() => {
    const live = new Set(running.map((c) => c.runId))
    return list
      .flatMap((s) =>
        s.recentExits
          .filter((e) => !dismissed.has(e.runId) && !live.has(e.runId))
          .map((e) => ({ strategy: s, runId: e.runId, run: null, exit: e })),
      )
      .sort((a, b) => (b.exit?.atUtc ?? '').localeCompare(a.exit?.atUtc ?? ''))
  }, [list, running, dismissed])
  const visible = useMemo(() => [...running, ...stopped], [running, stopped])
  // Stopped cards stay in this list on purpose: the "Realized today" tile sums
  // them, and their live query stops polling by itself once isActive is false.
  const visibleRunIds = useMemo(() => visible.map((c) => c.runId), [visible])

  // Page totals share the cache with each card's own useStrategyLive.
  const lives = useStrategyLives(visibleRunIds)
  const views = lives.map((q) => q.data).filter((v): v is StrategyLiveView => !!v)
  const activeViews = views.filter((v) => v.isActive)
  const openPositions = activeViews.reduce(
    (n, v) => n + v.positions.filter((p) => p.status === 'Open').length,
    0,
  )
  const livePnl = activeViews.reduce((n, v) => n + v.pnl.total, 0)
  const realizedToday = views
    .filter((v) => isToday(v.startedUtc))
    .reduce((n, v) => n + v.pnl.realized, 0)

  // After a start, bring the new card into view once the list has caught up.
  const startedCardVisible = scrollTo != null && running.some((c) => c.runId === scrollTo)
  useEffect(() => {
    if (scrollTo == null || !startedCardVisible) return
    const el = document.getElementById(`run-${scrollTo}`)
    if (el) {
      el.scrollIntoView({ behavior: 'smooth', block: 'start' })
      setScrollTo(null)
    }
  }, [scrollTo, startedCardVisible])

  // The dialog reads the strategy from the polled list, so a run started from
  // another browser greys its underlying out while the dialog is open.
  const launch = launchId != null ? (list.find((s) => s.id === launchId) ?? null) : null

  let runningSub: ReactNode = 'nothing running'
  if (runningStrategies.length > 0) {
    runningSub = runningStrategies.map((s, i) => (
      <span key={s.id}>
        {i > 0 && <br />}
        {runningSummary(s)}
      </span>
    ))
  }

  return (
    <div className="page">
      <header className="page__header">
        <div>
          <h1 className="page__title">Live runner</h1>
          <p className="page__subtitle">
            Pick a strategy, choose the underlying, set optional risk rules (per leg, per group,
            overall — editable while the run is live), start. Paper execution on live ticks — the
            same strategy can run on several underlyings at once.
          </p>
        </div>
        <ReadinessStrip />
      </header>

      <div className="stat-grid">
        {listUnknown ? (
          <>
            <StatTile label="Running" value="—" sub="strategy list unavailable" />
            <StatTile label="Open positions" value="—" sub="strategy list unavailable" />
            <StatTile label="Live P&L" value="—" sub="strategy list unavailable" />
            <StatTile label="Realized today" value="—" sub="strategy list unavailable" />
          </>
        ) : (
          <>
            <StatTile
              label={running.length === 1 ? 'Running run' : 'Running runs'}
              value={running.length}
              tone={running.length > 0 ? 'pos' : undefined}
              sub={runningSub}
            />
            <StatTile label="Open positions" value={openPositions} sub="across running runs" />
            <StatTile
              label="Live P&L"
              value={<PnlValue value={livePnl} />}
              tone={livePnl > 0 ? 'pos' : livePnl < 0 ? 'neg' : undefined}
              sub="realized + unrealized of running runs"
            />
            <StatTile
              label="Realized today"
              value={<PnlValue value={realizedToday} />}
              tone={realizedToday > 0 ? 'pos' : realizedToday < 0 ? 'neg' : undefined}
              sub="runs started today, incl. stopped ones shown below"
              to="/admin/strategies/history"
            />
          </>
        )}
      </div>

      <section aria-labelledby="running-now">
        <h2 className="section-title section-title--row" id="running-now">
          <IconPlay /> Running now
          <Link
            className="section-title__link"
            to="/admin/strategies/history"
            title="Every run ever started, with its result — nothing there is dismissed"
          >
            <IconClock /> View history →
          </Link>
        </h2>
        {strategies.isPending ? (
          <Loading />
        ) : listUnknown ? (
          <div className="card">
            <p className="card__muted" style={{ margin: '0 0 8px' }}>
              Could not load the strategy list, so it is unknown whether anything is running.
              A runner started earlier keeps trading until the API is reachable again.
            </p>
            <InlineError error={strategies.error} />
          </div>
        ) : visible.length === 0 ? (
          <div className="card card--dashed">
            <p className="card__muted" style={{ margin: 0 }}>
              Nothing is running. Start a strategy from the catalogue below — it will appear here with
              its live positions and P&L. Earlier runs are in{' '}
              <Link to="/admin/strategies/history">Run history</Link>.
            </p>
          </div>
        ) : (
          <div className="stack-list">
            {visible.map((c) => (
              <RunCard
                key={c.runId}
                strategy={c.strategy}
                runId={c.runId}
                run={c.run}
                exit={c.exit}
                dismissTitle={DISMISS_TITLE}
                onDismiss={() =>
                  setDismissed((prev) => {
                    const next = new Set(prev)
                    next.add(c.runId)
                    return next
                  })
                }
              />
            ))}
          </div>
        )}
      </section>

      <section aria-labelledby="start-a-strategy">
        <h2 className="section-title" id="start-a-strategy">
          <IconLayers /> Start a strategy
        </h2>
        <QueryBoundary query={strategies} empty="No strategies found in the Python engine.">
          {(items) => (
            <div className="strategy-grid">
              {items.map((s) => (
                <StrategyCard key={s.id} strategy={s} onStart={(st) => setLaunchId(st.id)} />
              ))}
            </div>
          )}
        </QueryBoundary>
      </section>

      {launch && (
        <LaunchDialog
          strategy={launch}
          onClose={() => setLaunchId(null)}
          onStarted={(response) => setScrollTo(response.runId)}
        />
      )}
    </div>
  )
}
