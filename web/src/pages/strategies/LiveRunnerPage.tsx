/**
 * Strategies module — Live runner, v2.
 *
 * Position-based, not order-based: each running strategy is one RunCard that
 * shows its spot, P&L, risk limits and every position of the run as a row
 * (closed legs stay in the table with quantity 0 instead of appearing as a
 * separate "SELL order"). Starting goes through the LaunchDialog, which makes
 * the underlying mandatory and stop-loss/target optional.
 */

import { useEffect, useMemo, useRef, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import {
  useStopStrategy,
  useStrategies,
  useStrategyLive,
  useStrategyLives,
  useStrategyLogs,
} from '../../lib/queries'
import {
  formatAge,
  formatInrWhole,
  formatLots,
  formatNumber,
  formatPrice,
  formatTime,
} from '../../lib/format'
import { formatContract } from '../../lib/symbols'
import { Badge, FlashPrice, InlineError, Loading, QueryBoundary, StatTile } from '../../components/ui'
import { IconLayers, IconPlay, IconStop } from '../../components/icons'
import type { LivePosition, StrategyListItem, StrategyLiveView } from '../../lib/types'
import {
  ActivityList,
  CategoryBadge,
  ConsoleOutput,
  Disclosure,
  LaunchDialog,
  PnlValue,
  ReadinessStrip,
  StrategyCard,
} from './shared'

/* ------------------------------------------------------------------ helpers */

function exitKey(s: StrategyListItem): string {
  return `${s.id}:${s.lastExit?.runId ?? 0}`
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

function contractLabel(p: LivePosition): string {
  return p.contract?.label || formatContract(p.symbol)
}

/* ------------------------------------------------------------- risk metric */

function RiskMetric({
  label,
  limit,
  used,
  kind,
}: {
  label: string
  limit: number
  used: number
  kind: 'stop' | 'target'
}) {
  const pct = limit > 0 ? Math.min(100, (Math.max(0, used) / limit) * 100) : 0
  const modifier =
    kind === 'target' ? 'progress__bar--pos' : pct >= 100 ? 'progress__bar--neg' : pct >= 70 ? 'progress__bar--warn' : ''
  return (
    <div className="metric">
      <div className="metric__label">{label}</div>
      <div className="metric__value">{formatInrWhole(limit)}</div>
      <div className="metric__sub">{pct.toFixed(0)}% of the way</div>
      <div
        className="progress"
        role="progressbar"
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={Math.round(pct)}
        aria-label={label}
      >
        <div className={`progress__bar ${modifier}`} style={{ width: `${pct}%` }} />
      </div>
    </div>
  )
}

/* ---------------------------------------------------------- positions table */

function PositionsTable({ positions }: { positions: LivePosition[] }) {
  return (
    <div className="tablewrap">
      <table className="table">
        <thead>
          <tr>
            <th>Contract</th>
            <th>Side</th>
            <th className="r">Lots</th>
            <th className="r">Lot size</th>
            <th className="r">Qty</th>
            <th className="r">Entry</th>
            <th className="r">LTP</th>
            <th className="r">P&L</th>
            <th>Status</th>
            <th>Time</th>
          </tr>
        </thead>
        <tbody>
          {positions.map((p) => {
            const open = p.status === 'Open'
            return (
              <tr key={p.id} className={open ? '' : 'pos-row--closed'}>
                <td className="mono" title={`${p.symbol} · group ${p.groupId}`}>
                  {contractLabel(p)}
                </td>
                <td>
                  <Badge tone={p.side === 'BUY' ? 'pos' : 'neg'}>{p.side}</Badge>
                </td>
                <td className="r">{open ? formatNumber(p.lots) : 0}</td>
                <td className="r muted">{formatNumber(p.lotSize)}</td>
                <td className="r">{open ? formatNumber(p.quantity) : 0}</td>
                <td className="r mono">{formatPrice(p.entryPrice)}</td>
                <td className="r">{open ? <FlashPrice value={p.ltp} /> : <span className="muted">—</span>}</td>
                <td className="r">
                  <PnlValue value={p.pnl} />
                </td>
                <td>{open ? <Badge tone="accent">Open</Badge> : <Badge>Closed</Badge>}</td>
                <td className="muted">{open ? formatTime(p.openedUtc) : formatTime(p.closedUtc)}</td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}

/* ------------------------------------------------------------ runner output */

function RunnerOutput({ strategyId, isActive }: { strategyId: number; isActive: boolean }) {
  const logs = useStrategyLogs(strategyId, isActive)
  const lines = isActive && Array.isArray(logs.data) ? logs.data : []
  return (
    <ConsoleOutput
      lines={lines}
      placeholder={
        !isActive
          ? 'The runner is not running — output is only kept while the process is alive.'
          : 'No output yet — the runner prints a [CONFIG] line at startup and a [STATUS] line every 10 s.'
      }
    />
  )
}

/* ---------------------------------------------------------------- run card */

function RunCard({
  strategy,
  onDismiss,
}: {
  strategy: StrategyListItem
  onDismiss: () => void
}) {
  const live = useStrategyLive(strategy.id, true)
  const stop = useStopStrategy()
  const [showActivity, setShowActivity] = useState(false)
  const [showOutput, setShowOutput] = useState(false)

  const view = live.data
  const isActive = view ? view.isActive : strategy.isActive
  const positions = view?.positions ?? []
  const openCount = positions.filter((p) => p.status === 'Open').length
  const underlying = view?.underlying ?? strategy.underlying
  const stopReason = view?.stopReason ?? strategy.lastExit?.reason ?? null
  const startedBy = view?.startedBy ?? strategy.startedBy
  const startedUtc = view?.startedUtc ?? strategy.startedUtc
  const stoppedUtc = view?.stoppedUtc ?? strategy.lastExit?.atUtc ?? null
  const total = view?.pnl.total ?? 0

  function confirmStop() {
    const msg = `Square off ${openCount} open position${openCount === 1 ? '' : 's'} at the last price and stop ${strategy.name}?`
    if (window.confirm(msg)) stop.mutate({ id: strategy.id })
  }

  return (
    <section
      id={`run-${strategy.id}`}
      className={`run-card ${isActive ? 'run-card--live' : 'run-card--stopped'}`}
      aria-label={`${strategy.name} run`}
    >
      <header className="run-card__head">
        <span className="run-card__name">
          {strategy.name}
          <CategoryBadge category={strategy.category} />
        </span>
        {underlying && (
          <span className="badge badge--accent" title={view?.spotSymbol ?? undefined}>
            {underlying} <FlashPrice value={view?.spotLtp} bold />
          </span>
        )}
        {isActive ? (
          <Badge tone="live">running</Badge>
        ) : (
          <Badge tone="warn">Stopped{stopReason ? ` · ${stopReason}` : ''}</Badge>
        )}
        <span className="run-card__meta">
          {startedBy ? `started by ${startedBy}` : 'started'} · {formatTime(startedUtc)}
          {!isActive && stoppedUtc && <> · stopped {formatTime(stoppedUtc)}</>}
          {view?.runId != null && <span className="faint">· run #{view.runId}</span>}
        </span>
        <div className="run-card__actions">
          {isActive ? (
            <button
              type="button"
              className="btn btn--danger btn--sm"
              disabled={stop.isPending}
              onClick={confirmStop}
            >
              <IconStop style={{ width: 13, height: 13 }} />
              {stop.isPending ? 'Stopping…' : 'Stop'}
            </button>
          ) : (
            <button type="button" className="btn btn--ghost btn--sm" onClick={onDismiss}>
              Dismiss
            </button>
          )}
        </div>
      </header>

      {stop.isError && (
        <div style={{ marginBottom: 10 }}>
          <InlineError error={stop.error} />
        </div>
      )}
      {stop.isSuccess && stop.data && (
        <p className="small-note" style={{ margin: '0 0 10px' }} role="status">
          {stop.data.message}
          {stop.data.flattened > 0 ? ` · squared off ${stop.data.flattened}` : ''}
        </p>
      )}

      {!view && live.isPending && <Loading label="Loading run…" />}
      {!view && live.isError && <InlineError error={live.error} />}

      {view && (
        <>
          <div className="metric-strip">
            <div className="metric">
              <div className="metric__label">Total P&L</div>
              <div className="metric__value metric__value--lg">
                <PnlValue value={view.pnl.total} />
              </div>
              <div className="metric__sub">realized + unrealized</div>
            </div>
            <div className="metric">
              <div className="metric__label">Realized</div>
              <div className="metric__value">
                <PnlValue value={view.pnl.realized} />
              </div>
            </div>
            <div className="metric">
              <div className="metric__label">Unrealized</div>
              <div className="metric__value">
                <PnlValue value={view.pnl.unrealized} />
              </div>
              <div className="metric__sub">
                {openCount} open · {positions.length - openCount} closed
              </div>
            </div>
            <div className="metric">
              <div className="metric__label">Lots</div>
              <div className="metric__value">{formatLots(view.lots, view.lotSize)}</div>
              <div className="metric__sub">
                {view.lotSize != null ? `qty ${formatNumber((view.lots ?? 0) * view.lotSize)}` : ''}
                {view.lotSizeSource && view.lotSizeSource !== 'master' ? ` · lot size ${view.lotSizeSource}` : ''}
              </div>
            </div>
            {view.stopLoss != null && view.stopLoss > 0 && (
              <RiskMetric label="Stop-loss" limit={view.stopLoss} used={-Math.min(total, 0)} kind="stop" />
            )}
            {view.target != null && view.target > 0 && (
              <RiskMetric label="Target" limit={view.target} used={Math.max(total, 0)} kind="target" />
            )}
          </div>

          {positions.length > 0 ? (
            <PositionsTable positions={positions} />
          ) : isActive ? (
            <div className="waiting" role="status">
              <span className="pulse-dot" aria-hidden="true" />
              <span>
                Waiting for entry conditions
                {view.underlying ? (
                  <>
                    {' '}
                    — spot {view.underlying} {formatPrice(view.spotLtp)} · last tick{' '}
                    {view.spotUpdatedUtc ? formatAge(view.spotUpdatedUtc) : 'not yet received'}
                  </>
                ) : null}
              </span>
            </div>
          ) : (
            <p className="empty">No positions were opened during this run.</p>
          )}

          <Disclosure
            label={`Activity (${view.activity.length})`}
            open={showActivity}
            onToggle={() => setShowActivity((v) => !v)}
          >
            <ActivityList items={view.activity} />
          </Disclosure>
          <Disclosure label="Runner output" open={showOutput} onToggle={() => setShowOutput((v) => !v)}>
            <RunnerOutput strategyId={strategy.id} isActive={isActive} />
          </Disclosure>
        </>
      )}
    </section>
  )
}

/* -------------------------------------------------------------------- page */

export function LiveRunnerPage() {
  const strategies = useStrategies()
  const qc = useQueryClient()
  const [launch, setLaunch] = useState<StrategyListItem | null>(null)
  const [dismissed, setDismissed] = useState<ReadonlySet<string>>(() => new Set<string>())
  const [scrollTo, setScrollTo] = useState<number | null>(null)

  // The list request failed and there is nothing cached: the page cannot know
  // what is running, so it must say so rather than claim a flat book.
  const listUnknown = strategies.isError && strategies.data === undefined

  const list = useMemo(() => strategies.data ?? [], [strategies.data])
  const running = useMemo(() => list.filter((s) => s.isActive), [list])
  const stopped = useMemo(
    () => list.filter((s) => !s.isActive && s.lastExit && !dismissed.has(exitKey(s))),
    [list, dismissed],
  )
  const visible = useMemo(() => [...running, ...stopped], [running, stopped])
  // Stopped cards stay in this list on purpose: the "Realized today" tile sums
  // them, and their live query stops polling by itself once isActive is false.
  const visibleIds = useMemo(() => visible.map((s) => s.id), [visible])

  // A stopped card's live view does not poll, so when the list reports a
  // strategy running again (restarted here or from another browser) its view
  // must be refetched once to pick the new run up and resume polling.
  const runningKey = useMemo(() => running.map((s) => `${s.id}:${s.runId ?? 0}`).join(','), [running])
  const prevRunningKey = useRef<string | null>(null)
  useEffect(() => {
    if (prevRunningKey.current === runningKey) return
    // First list load: the cards fetch on mount anyway, nothing to invalidate.
    if (prevRunningKey.current !== null) {
      const before = new Set(prevRunningKey.current.split(',').filter(Boolean))
      for (const s of running) {
        if (!before.has(`${s.id}:${s.runId ?? 0}`)) {
          qc.invalidateQueries({ queryKey: ['strategy', 'live', s.id] })
        }
      }
    }
    prevRunningKey.current = runningKey
  }, [runningKey, running, qc])

  // Page totals share the cache with each card's own useStrategyLive.
  const lives = useStrategyLives(visibleIds)
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
  const startedCardVisible = scrollTo != null && running.some((s) => s.id === scrollTo)
  useEffect(() => {
    if (scrollTo == null || !startedCardVisible) return
    const el = document.getElementById(`run-${scrollTo}`)
    if (el) {
      el.scrollIntoView({ behavior: 'smooth', block: 'start' })
      setScrollTo(null)
    }
  }, [scrollTo, startedCardVisible])

  return (
    <div className="page">
      <header className="page__header">
        <div>
          <h1 className="page__title">Live runner</h1>
          <p className="page__subtitle">
            Pick a strategy, choose the underlying, set optional stop-loss/target, start. Paper
            execution on live ticks.
          </p>
        </div>
        <ReadinessStrip />
      </header>

      <div className="stat-grid">
        {listUnknown ? (
          <>
            <StatTile label="Running strategies" value="—" sub="strategy list unavailable" />
            <StatTile label="Open positions" value="—" sub="strategy list unavailable" />
            <StatTile label="Live P&L" value="—" sub="strategy list unavailable" />
            <StatTile label="Realized today" value="—" sub="strategy list unavailable" />
          </>
        ) : (
          <>
            <StatTile
              label="Running strategies"
              value={running.length}
              tone={running.length > 0 ? 'pos' : undefined}
              sub={running.length > 0 ? running.map((s) => s.name).join(', ') : 'nothing running'}
            />
            <StatTile label="Open positions" value={openPositions} sub="across running strategies" />
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
            />
          </>
        )}
      </div>

      <section aria-labelledby="running-now">
        <h2 className="section-title" id="running-now">
          <IconPlay /> Running now
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
              its live positions and P&L.
            </p>
          </div>
        ) : (
          <div className="stack-list">
            {visible.map((s) => (
              <RunCard
                key={s.id}
                strategy={s}
                onDismiss={() =>
                  setDismissed((prev) => {
                    const next = new Set(prev)
                    next.add(exitKey(s))
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
                <StrategyCard key={s.id} strategy={s} onStart={setLaunch} />
              ))}
            </div>
          )}
        </QueryBoundary>
      </section>

      {launch && (
        <LaunchDialog
          strategy={launch}
          onClose={() => setLaunch(null)}
          onStarted={() => setScrollTo(launch.id)}
        />
      )}
    </div>
  )
}
