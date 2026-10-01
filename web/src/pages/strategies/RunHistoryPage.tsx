/**
 * Strategies module — Run history. Every live (paper) run ever started, one
 * row per run, attached to the user who started it. Whatever ended the run —
 * stop-loss, target, market close, a manual stop, a runner exit, an API
 * restart — it stays here with its reason and P&L; the Live runner's
 * "Dismiss" only hides a card from that list. An admin sees everyone's runs
 * with a per-user rollup; a trader sees their own (the API enforces it —
 * the `mode` only decides what the page offers).
 */

import { memo, useEffect, useMemo, useState } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import {
  RUN_HISTORY_PAGE,
  pagedRowsAsOf,
  useFnoUnderlyings,
  useLiveRun,
  useLiveRunHistoryPages,
  useLiveRunUserSummary,
  useLiveRuns,
  useRunLegs,
  useStrategies,
} from '../../lib/queries'
import type { ListAsOf, RunLegs } from '../../lib/liveMarks'
import type { LiveRunHistoryFilters } from '../../lib/queries'
import { formatDateTime, formatDuration, formatInr, formatInrSigned, formatLots, formatNumber } from '../../lib/format'
import { riskChips } from '../../lib/risk'
import { recapBadge, runDurationSeconds, runNetPnl, runSpan, runStatusTone, runUserLabel, shortStopReason } from '../../lib/runHistory'
import { DateField } from '../../components/DateField'
import { isoToDmy } from '../../lib/dates'
import { Badge, InlineError, Loading, Panel, StatTile } from '../../components/ui'
import { IconClock, IconPlay, IconUsers } from '../../components/icons'
import type { LiveRunSummary, LiveRunUserSummary } from '../../lib/types'
import { CategoryBadge, PnlValue } from './shared'
import { addDays, istDate, todayIst } from '../backtesting/shared'

export type RunHistoryMode = 'admin' | 'trader'

const STATUSES = ['Running', 'Stopped', 'Failed', 'Completed'] as const
const DEFAULT_RANGE_DAYS = 30
/** Rows per request; a range with more runs than this is paged with "Load older". */
const TAKE = RUN_HISTORY_PAGE

/** Where a row's detail lives and where "start one" points: the run cards for an admin, the library for a trader. */
function routesFor(mode: RunHistoryMode) {
  const detail = (runId: number) => `/trade/runs/${runId}`
  return mode === 'admin'
    ? { detail, start: '/trade/runs', startLabel: 'Live runner' }
    : { detail, start: '/trade/library', startLabel: 'Library' }
}

/* ------------------------------------------------------------ status cell */

/** "Running" with a live dot, or the status with the short stop reason under it and the full reason in the title. */
export function RunStatusCell({ run }: { run: LiveRunSummary }) {
  const reason = shortStopReason(run.stopReason)
  const title = run.stopReason
    ? `${run.stopReason}${run.stoppedBy ? ` · by ${run.stoppedBy}` : ''}`
    : run.isActive
      ? 'The runner is alive'
      : undefined
  return (
    <span className="status-cell" title={title}>
      <Badge tone={runStatusTone(run.status)}>
        {run.isActive && <span className="live-dot" aria-hidden="true" />}
        {run.status}
      </Badge>
      {!run.isActive && reason && <span className="cell-sub">{reason}</span>}
    </span>
  )
}

/** The run's risk rules as compact chips ("Overall SL ₹5,000 · target —", …); "—" when none. */
function RiskChips({ run }: { run: LiveRunSummary }) {
  const chips = riskChips(run.risk)
  if (chips.length === 0) return <span className="muted">—</span>
  return (
    <span className="chip-row chip-row--compact" title={chips.map((c) => c.label).join(' · ')}>
      {chips.map((c) => (
        <Badge key={c.level} tone={c.level === 'overall' ? 'accent' : 'neutral'}>
          {c.label}
        </Badge>
      ))}
    </span>
  )
}

/* ------------------------------------------------------------ user rollup */

function UserRollup({
  users,
  selected,
  onSelect,
}: {
  users: LiveRunUserSummary[]
  selected: number | null
  onSelect: (userId: number | null) => void
}) {
  const totalRuns = users.reduce((n, u) => n + u.runs, 0)
  const totalActive = users.reduce((n, u) => n + u.active, 0)
  const totalPnl = users.reduce((n, u) => n + u.netPnl, 0)
  return (
    <div className="rollup" role="group" aria-label="Runs per user">
      <button
        type="button"
        className={`rollup__chip ${selected == null ? 'is-active' : ''}`}
        onClick={() => onSelect(null)}
        title="Every user"
      >
        <span className="rollup__name">
          <IconUsers /> All users
        </span>
        <span className="rollup__meta">
          {formatNumber(totalRuns)} {totalRuns === 1 ? 'run' : 'runs'}
          {totalActive > 0 && <span className="rollup__active">· {totalActive} live</span>}
        </span>
        <PnlValue value={totalPnl} className="rollup__pnl" />
      </button>
      {users.map((u) => (
        <button
          key={u.userId}
          type="button"
          className={`rollup__chip ${selected === u.userId ? 'is-active' : ''}`}
          onClick={() => onSelect(selected === u.userId ? null : u.userId)}
          title={u.lastRunUtc ? `Last run ${formatDateTime(u.lastRunUtc)}` : 'No runs yet'}
        >
          <span className="rollup__name">{runUserLabel(u.userName, u.userId)}</span>
          <span className="rollup__meta">
            {formatNumber(u.runs)} {u.runs === 1 ? 'run' : 'runs'}
            {u.active > 0 && <span className="rollup__active">· {u.active} live</span>}
          </span>
          <PnlValue value={u.netPnl} className="rollup__pnl" />
        </button>
      ))}
    </div>
  )
}

/* ------------------------------------------------------------ live figures */

/**
 * The Net P&L tile: every loaded row's net, the live ones' open books at the
 * pushed prices of their legs. Its own component, so a push re-renders the
 * tile and the rows it moved, not the page and its five hundred rows.
 */
function NetPnlTile({ rows, asOf, legs, pending, hasOlder, activeCount, recaps }: {
  rows: readonly LiveRunSummary[]
  asOf: ListAsOf
  legs: RunLegs | null
  pending: boolean
  hasOlder: boolean
  activeCount: number
  /** The list is the recap runs: the sum is a test's, never trading. */
  recaps: boolean
}) {
  const live = useLiveRuns(rows, asOf, legs) ?? rows
  const netPnl = live.reduce((n, r) => n + runNetPnl(r), 0)
  return (
    <StatTile
      label={recaps ? 'Net P&L (tests)' : 'Net P&L'}
      value={pending ? '—' : <PnlValue value={netPnl} />}
      tone={netPnl > 0 ? 'pos' : netPnl < 0 ? 'neg' : undefined}
      sub={
        recaps
          ? `of the ${hasOlder ? `${formatNumber(rows.length)} loaded ` : ''}recap runs · in no live total`
          : hasOlder
            ? `realized of the ${formatNumber(rows.length)} loaded runs${activeCount > 0 ? ' + open book of live ones' : ''}`
            : `realized of every run in range${activeCount > 0 ? ' + open book of live ones' : ''}`
      }
    />
  )
}

/**
 * One run's row. Memoised, and holding only its own legs' prices: a push
 * re-renders the live rows it moves, never the stopped ones, whose figures
 * are final.
 */
const HistoryRow = memo(function HistoryRow({ run: answered, to, isAdmin, asOf, legs, onOpen }: {
  run: LiveRunSummary
  to: string
  isAdmin: boolean
  asOf: number
  legs: RunLegs | null
  onOpen: (to: string) => void
}) {
  const run = useLiveRun(answered, asOf, legs)
  const pnl = runNetPnl(run)
  const recap = recapBadge(run)
  return (
    <tr
      className={run.isActive ? 'row--live' : ''}
      onClick={(e) => {
        // Plain clicks open the detail; modified clicks and clicks on
        // the explicit link keep their browser behaviour.
        if (e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return
        if ((e.target as HTMLElement).closest('a')) return
        onOpen(to)
      }}
    >
      {/* The row opens the run on a click; the number is the same target for a keyboard. */}
      <td className="mono">
        <Link to={to} className="run-link">
          #{run.runId}
        </Link>
      </td>
      {isAdmin && <td>{run.userName || <span className="faint">user {run.userId}</span>}</td>}
      <td>
        <b>{run.strategyName}</b> <CategoryBadge category={run.category} />
        {recap && (
          <>
            {' '}
            <span className="badge badge--warn" title={recap.title}>
              {recap.label}
            </span>
          </>
        )}
      </td>
      <td className="mono" title={run.spotSymbol || undefined}>
        {run.underlying}
      </td>
      <td className="r">{run.role === 'alerts' ? <span className="muted">alerts only</span> : formatLots(run.lots, run.lotSize)}</td>
      <td>
        <RiskChips run={run} />
      </td>
      <td className="muted run-when" title={`${formatDateTime(run.startedUtc)} → ${run.isActive ? 'running' : formatDateTime(run.stoppedUtc)}`}>
        {runSpan(run)}
        <span className="cell-sub">{formatDuration(runDurationSeconds(run))}</span>
      </td>
      <td className="r" title={run.openPositions > 0 ? `${run.openPositions} open` : undefined}>
        {formatNumber(run.trades)}
        {run.openPositions > 0 && <span className="cell-sub">{run.openPositions} open</span>}
      </td>
      <td className="r">
        <PnlValue value={pnl} />
        {run.charges != null && run.charges > 0 && (
          <span className="cell-sub" title={`gross ${formatInrSigned(run.grossPnl ?? run.realizedPnl)}`}>
            after {formatInr(run.charges)} charges
          </span>
        )}
        {run.isActive && run.unrealizedPnl !== 0 && (
          <span className="cell-sub">unrealized {formatInrSigned(run.unrealizedPnl)}</span>
        )}
      </td>
      <td>
        <RunStatusCell run={run} />
      </td>
    </tr>
  )
})

/* -------------------------------------------------------------------- page */

export function RunHistoryPage({ mode }: { mode: RunHistoryMode }) {
  const navigate = useNavigate()
  const routes = routesFor(mode)
  const isAdmin = mode === 'admin'

  // `?strategy=<catalog id>` opens the page on that strategy — where a
  // strategy's track record sends the reader for the filters it does not have.
  // Such a link also drops the default 30-day floor, because arriving on one
  // strategy means its whole history is what was asked for, not its last month.
  // Read once, in the initialisers: after mount the controls own the filters,
  // and a URL left behind in the address bar must not pull them back.
  const [params] = useSearchParams()

  const [userId, setUserId] = useState<number | null>(null)
  const [strategyId, setStrategyId] = useState<number | null>(() => {
    const id = Number(params.get('strategy'))
    return Number.isInteger(id) && id > 0 ? id : null
  })
  const [underlying, setUnderlying] = useState<string>('all')
  const [status, setStatus] = useState<string>('any')
  const [fromDate, setFromDate] = useState<string>(() =>
    params.get('strategy') ? '' : addDays(todayIst(), -DEFAULT_RANGE_DAYS),
  )
  const [toDate, setToDate] = useState<string>(() => todayIst())
  const [search, setSearch] = useState('')
  // Off: live trading only. On: the recap runs alone (recorded days played
  // again as tests). The API never mixes the two, so no total on this page
  // adds a test to trading.
  const [recaps, setRecaps] = useState(false)

  const filters = useMemo<Omit<LiveRunHistoryFilters, 'skip'>>(
    () => ({
      userId: isAdmin ? userId : null,
      strategyId,
      underlying: underlying === 'all' ? null : underlying,
      status: status === 'any' ? null : status,
      fromDate: fromDate || null,
      toDate: toDate || null,
      take: TAKE,
      recap: recaps,
    }),
    [isAdmin, userId, strategyId, underlying, status, fromDate, toDate, recaps],
  )

  // Paged: the API carries at most TAKE rows per request, so a busy range is
  // loaded page by page ("Load older") rather than silently cut at the cap.
  const history = useLiveRunHistoryPages(filters)
  const summary = useLiveRunUserSummary(isAdmin)
  const strategies = useStrategies()
  const fno = useFnoUnderlyings()

  const answered = useMemo(() => history.data?.pages.flat() ?? [], [history.data])
  // Only rows of the kind asked for. Switching the toggle keeps the other list
  // on screen while this one loads (placeholder data), and a live run must not
  // sit under the recap labels, nor a recap under the live ones.
  const rows = useMemo(() => answered.filter((r) => (r.isRecap ?? false) === recaps), [answered, recaps])
  const pending = history.isPending || (history.isPlaceholderData && rows.length < answered.length)
  // The live rows are re-priced from their open legs, each against the page it came in.
  // Never a recap's: the replay prices its legs, and a pushed price is a live one.
  const legs = useRunLegs(rows, !recaps)
  const asOfByRun = useMemo(() => pagedRowsAsOf(history.data, history.dataUpdatedAt), [history.data, history.dataUpdatedAt])
  const hasOlder = history.hasNextPage
  const loadingOlder = history.isFetchingNextPage
  /** "500" or "500+": the loaded count, flagged while older runs are still unloaded. */
  const countLabel = (n: number) => `${formatNumber(n)}${hasOlder ? '+' : ''}`

  // A user who no longer has runs in the rollup (deleted) cannot stay selected.
  useEffect(() => {
    if (userId != null && summary.data && !summary.data.some((u) => u.userId === userId)) setUserId(null)
  }, [userId, summary.data])

  const underlyingOptions = useMemo(() => {
    const set = new Set<string>()
    for (const u of fno.data ?? []) set.add(u.underlying.toUpperCase())
    for (const r of rows) if (r.underlying) set.add(r.underlying.toUpperCase())
    if (underlying !== 'all') set.add(underlying)
    return [...set].sort()
  }, [fno.data, rows, underlying])

  const strategyOptions = useMemo(() => {
    const byId = new Map<number, string>()
    for (const s of strategies.data ?? []) byId.set(s.id, s.name)
    for (const r of rows) if (!byId.has(r.strategyId)) byId.set(r.strategyId, r.strategyName)
    return [...byId.entries()].sort((a, b) => a[1].localeCompare(b[1]))
  }, [strategies.data, rows])

  // Run-id search is client-side over the loaded page: "42" matches #42, #142, #420.
  const query = search.trim().replace(/^#/, '')
  const visible = useMemo(
    () => (query === '' ? rows : rows.filter((r) => String(r.runId).includes(query))),
    [rows, query],
  )

  const activeCount = rows.filter((r) => r.isActive).length
  const trades = rows.reduce((n, r) => n + r.trades, 0)
  const today = todayIst()
  const runsToday = rows.filter((r) => istDate(r.startedUtc) === today).length
  const defaultFromDate = addDays(today, -DEFAULT_RANGE_DAYS)
  const defaultToDate = today

  function resetFilters() {
    setUserId(null)
    setStrategyId(null)
    setUnderlying('all')
    setStatus('any')
    setFromDate(defaultFromDate)
    setToDate(defaultToDate)
    setSearch('')
  }

  // Anything Reset would change counts as a filter — the date range included,
  // so a range with no runs still offers a way back to the default 30 days.
  const filtered =
    userId != null ||
    strategyId != null ||
    underlying !== 'all' ||
    status !== 'any' ||
    query !== '' ||
    fromDate !== defaultFromDate ||
    toDate !== defaultToDate
  const rangeLabel = fromDate && toDate ? `${isoToDmy(fromDate)} → ${isoToDmy(toDate)}` : fromDate ? `from ${isoToDmy(fromDate)}` : toDate ? `to ${isoToDmy(toDate)}` : 'all time'

  return (
    <div className="page">
      <header className="page__header">
        <div>
          <h1 className="page__title">{isAdmin ? 'Run history' : 'My runs'}</h1>
          <p className="page__subtitle">
            {recaps
              ? 'Recap runs — recorded days played again through the strategy runners. They are tests: their P&L is in no live total.'
              : isAdmin
                ? 'Run history — every live run, attached to the user who started it. Nothing here is dismissed.'
                : 'Every live run you started, with its result. Nothing here is dismissed — hiding a card on the runner does not remove it.'}
          </p>
        </div>
        <Link className="btn btn--sm" to={routes.start}>
          <IconPlay style={{ width: 13, height: 13 }} /> Open {routes.startLabel}
        </Link>
      </header>

      {isAdmin && (
        <>
          {summary.isError && summary.data === undefined && <InlineError error={summary.error} />}
          {/* The rollup counts live trading only, so it stands aside while the recaps are listed. */}
          {!recaps && summary.data && summary.data.length > 0 && (
            <UserRollup users={summary.data} selected={userId} onSelect={setUserId} />
          )}
        </>
      )}

      <div className="stat-grid">
        <StatTile
          label={
            recaps
              ? rows.length === 1 && !hasOlder ? 'Recap run in range' : 'Recap runs in range'
              : rows.length === 1 && !hasOlder ? 'Run in range' : 'Runs in range'
          }
          value={pending ? '—' : countLabel(rows.length)}
          sub={hasOlder ? `${rangeLabel} · newest ${formatNumber(rows.length)} loaded` : rangeLabel}
        />
        <StatTile
          label={recaps ? 'Running now' : 'Live now'}
          value={pending ? '—' : activeCount}
          tone={activeCount > 0 ? 'pos' : undefined}
          sub={activeCount > 0 ? 'this list refreshes every 10 s' : 'no runner alive in this range'}
        />
        <NetPnlTile
          rows={rows}
          asOf={asOfByRun}
          legs={legs}
          pending={pending}
          hasOlder={hasOlder}
          activeCount={activeCount}
          recaps={recaps}
        />
        <StatTile
          label="Trades"
          value={pending ? '—' : countLabel(trades)}
          sub={`closed positions${hasOlder ? ' of the loaded runs' : ''} · ${runsToday} ${runsToday === 1 ? 'run' : 'runs'} started today`}
        />
      </div>

      <Panel
        title={
          <>
            <IconClock /> Runs
          </>
        }
        actions={
          <div className="filter-row">
            {isAdmin && summary.data && summary.data.length > 0 && (
              <select
                className="field__input field__input--sm"
                value={userId ?? 'all'}
                onChange={(e) => setUserId(e.target.value === 'all' ? null : Number(e.target.value))}
                aria-label="Filter by user"
              >
                <option value="all">All users</option>
                {summary.data.map((u) => (
                  <option key={u.userId} value={u.userId}>
                    {runUserLabel(u.userName, u.userId)}
                  </option>
                ))}
              </select>
            )}
            <select
              className="field__input field__input--sm"
              value={strategyId ?? 'all'}
              onChange={(e) => setStrategyId(e.target.value === 'all' ? null : Number(e.target.value))}
              aria-label="Filter by strategy"
            >
              <option value="all">All strategies</option>
              {strategyOptions.map(([id, name]) => (
                <option key={id} value={id}>
                  {name}
                </option>
              ))}
            </select>
            <select
              className="field__input field__input--sm"
              value={underlying}
              onChange={(e) => setUnderlying(e.target.value)}
              aria-label="Filter by underlying"
            >
              <option value="all">All underlyings</option>
              {underlyingOptions.map((u) => (
                <option key={u} value={u}>
                  {u}
                </option>
              ))}
            </select>
            <select
              className="field__input field__input--sm"
              value={status}
              onChange={(e) => setStatus(e.target.value)}
              aria-label="Filter by status"
            >
              <option value="any">All statuses</option>
              {STATUSES.map((s) => (
                <option key={s} value={s}>
                  {s}
                </option>
              ))}
            </select>
            <DateField
              className="field__input field__input--sm field__input--date"
              value={fromDate}
              max={toDate || undefined}
              onChange={setFromDate}
              aria-label="Started on or after (IST day)"
              title="Started on or after (IST day)"
            />
            <span className="faint">→</span>
            <DateField
              className="field__input field__input--sm field__input--date"
              value={toDate}
              min={fromDate || undefined}
              onChange={setToDate}
              aria-label="Started on or before (IST day)"
              title="Started on or before (IST day)"
            />
            <button
              type="button"
              className="oc-toggle"
              aria-pressed={recaps}
              onClick={() => setRecaps((on) => !on)}
              title={
                recaps
                  ? 'Listing the recap runs alone. Switch off for live trading.'
                  : 'List the recap runs instead: recorded days played again as tests, in no live total.'
              }
            >
              <span className="oc-toggle__box" aria-hidden="true" />
              Recap runs
            </button>
            <input
              className="field__input field__input--sm"
              type="search"
              inputMode="numeric"
              placeholder="Run #"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              aria-label="Search by run id"
              style={{ minWidth: 90, width: 90 }}
            />
            <span
              className="small-note"
              style={{ margin: 0 }}
              title={hasOlder ? `Only the newest ${formatNumber(rows.length)} runs of this range are loaded — older ones exist` : undefined}
            >
              {formatNumber(visible.length)} of {countLabel(rows.length)}
            </span>
            {filtered && (
              <button type="button" className="btn btn--ghost btn--sm" onClick={resetFilters}>
                Reset
              </button>
            )}
          </div>
        }
      >
        {pending ? (
          <Loading label={recaps ? 'Loading recap runs…' : 'Loading run history…'} />
        ) : history.isError && history.data === undefined ? (
          <InlineError error={history.error} />
        ) : (
          <>
            {history.isError && (
              <p className="small-note warn" role="status" style={{ margin: '0 0 8px' }}>
                Refresh failed — showing the last loaded data.
              </p>
            )}
            {visible.length === 0 ? (
              <div className="empty">
                {recaps ? (
                  <p style={{ margin: 0 }}>
                    No recap runs for this filter
                    {isAdmin ? (
                      <>
                        {' '}
                        — play a recorded day again from <Link to="/data/replay">Data → Replay</Link>.
                      </>
                    ) : (
                      '.'
                    )}
                  </p>
                ) : (
                  <p style={{ margin: 0 }}>
                    No live runs yet for this filter — start one from{' '}
                    <Link to={routes.start}>{routes.startLabel}</Link>.
                  </p>
                )}
                {filtered && (
                  <p style={{ margin: '8px 0 0' }}>
                    <button type="button" className="btn btn--ghost btn--sm" onClick={resetFilters}>
                      Reset filters
                    </button>{' '}
                    <span className="faint">
                      back to every {recaps ? 'recap ' : ''}run of the last {DEFAULT_RANGE_DAYS} days
                    </span>
                  </p>
                )}
              </div>
            ) : (
              <div className="tablewrap tablewrap--tall">
                <table className="table table--hover">
                  <thead>
                    <tr>
                      <th>Run #</th>
                      {isAdmin && <th>User</th>}
                      <th>Strategy</th>
                      <th>Underlying</th>
                      <th className="r">Lots × lot size</th>
                      <th>Risk</th>
                      <th>When (IST)</th>
                      <th className="r">Trades</th>
                      <th className="r">Net P&L</th>
                      <th>Status</th>
                    </tr>
                  </thead>
                  <tbody>
                    {visible.map((run) => (
                      <HistoryRow
                        key={run.runId}
                        run={run}
                        to={routes.detail(run.runId)}
                        isAdmin={isAdmin}
                        asOf={asOfByRun.get(run.runId) ?? history.dataUpdatedAt}
                        legs={legs}
                        onOpen={navigate}
                      />
                    ))}
                  </tbody>
                </table>
              </div>
            )}
            {hasOlder && (
              <p className="small-note warn" role="status" style={{ margin: '8px 0 0' }}>
                Showing the newest {formatNumber(rows.length)} runs of this range — older runs exist.{' '}
                <button
                  type="button"
                  className="btn btn--ghost btn--sm"
                  onClick={() => void history.fetchNextPage()}
                  disabled={loadingOlder}
                >
                  {loadingOlder ? 'Loading older…' : `Load older ${formatNumber(TAKE)}`}
                </button>{' '}
                <span className="faint">or narrow the date range.</span>
              </p>
            )}
          </>
        )}
      </Panel>

      <p className="small-note">
        Dates are IST calendar days on the run's start · Net P&L is the realized P&L of every position of
        the run after brokerage and statutory charges (plus the open book while it is live), and before the
        bid–ask spread — paper fills are at the last price · a run is never removed from this history ·{' '}
        {recaps
          ? 'a recap run plays a recorded day again (Data → Replay). It is a test, and is in no live total: not in the live list, Today or a track record.'
          : 'recap runs, recorded days played again as tests, are listed apart: switch on “Recap runs”.'}
      </p>
    </div>
  )
}
