/**
 * Data → Mutual funds: what the desk holds of AMFI's NAVs, before anything is
 * picked (the data-discoverability rule). On top, the fund report's way in: a
 * scheme search that shows what is stored for each scheme before it links to
 * its report, and picks two to four for a comparison. Below it, schemes by
 * plan, option, category and AMC, the NAV range and how current it is, the
 * last nightly sync, the history backfill, and what the report's own jobs
 * (benchmarks, TER, category ranking) have collected
 * (docs/modules/mutual_funds_module.md).
 */

import { useEffect, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { api } from '../../lib/api'
import { useMutualFundCoverage, useMutualFundSchemes, useRunMutualFundJob } from '../../lib/queries'
import { formatDateTime, formatNumber } from '../../lib/format'
import {
  MF_COMPARE_MAX,
  MF_COMPARE_MIN,
  MF_JOBS,
  backfillSummary,
  compareHref,
  dayRange,
  formatDay,
  formatNav,
  freshness,
  jobRequested,
  jobRun,
  navRange,
  optionLabel,
  planLabel,
  reportHref,
  searchScope,
  splitCategory,
  type MutualFundAnalyticsCoverage,
  type MutualFundCount,
  type MutualFundCoverage,
  type MutualFundJob,
  type MutualFundScheme,
} from '../../lib/mutualFunds'
import { Badge, Panel, QueryBoundary, StatTile } from '../../components/ui'
import { IconSearch, IconX } from '../../components/icons'
import './data.css'

/** The value, once it has stopped changing for `ms`: a search is asked for when the typing pauses. */
function useDebounced<T>(value: T, ms: number): T {
  const [settled, setSettled] = useState(value)
  useEffect(() => {
    const t = setTimeout(() => setSettled(value), ms)
    return () => clearTimeout(t)
  }, [value, ms])
  return settled
}

const PLAN_FILTERS = [
  { key: undefined, label: 'All plans' },
  { key: 'direct', label: 'Direct' },
  { key: 'regular', label: 'Regular' },
] as const

const OPTION_FILTERS = [
  { key: undefined, label: 'All options' },
  { key: 'growth', label: 'Growth' },
  { key: 'idcw', label: 'IDCW' },
] as const

const SEARCH_TAKE = 50

function errorText(error: unknown): string {
  return error instanceof Error ? error.message : String(error)
}

/**
 * Search a scheme by name, AMC, code or ISIN. Each match says what is stored
 * for it (plan, option, category, the NAVs from–to, active) before it links to
 * its report; ticks pick two to four schemes for a comparison, and a tick
 * stays while the search changes, so schemes from different searches can be
 * compared.
 */
function FundSearch({ coverage }: { coverage: MutualFundCoverage | undefined }) {
  const [query, setQuery] = useState('')
  const [plan, setPlan] = useState<string | undefined>(undefined)
  const [option, setOption] = useState<string | undefined>(undefined)
  const [picked, setPicked] = useState<MutualFundScheme[]>([])
  const settled = useDebounced(query, 300)
  const search = useMutualFundSchemes(settled, { plan, option, take: SEARCH_TAKE })
  const navigate = useNavigate()

  const typed = query.trim().length >= 2
  const scope = coverage ? searchScope(coverage) : 'every scheme stored'
  const toggle = (s: MutualFundScheme) =>
    setPicked((list) =>
      list.some((p) => p.schemeCode === s.schemeCode)
        ? list.filter((p) => p.schemeCode !== s.schemeCode)
        : list.length >= MF_COMPARE_MAX
          ? list
          : [...list, s],
    )
  const canCompare = picked.length >= MF_COMPARE_MIN && picked.length <= MF_COMPARE_MAX

  return (
    <Panel
      title={
        <>
          <IconSearch /> Fund report
        </>
      }
      actions={
        <>
          <div className="seg" role="group" aria-label="Plan">
            {PLAN_FILTERS.map((f) => (
              <button key={f.label} type="button" className="seg__btn" aria-pressed={plan === f.key} onClick={() => setPlan(f.key)}>
                {f.label}
              </button>
            ))}
          </div>
          <div className="seg" role="group" aria-label="Option">
            {OPTION_FILTERS.map((f) => (
              <button key={f.label} type="button" className="seg__btn" aria-pressed={option === f.key} onClick={() => setOption(f.key)}>
                {f.label}
              </button>
            ))}
          </div>
        </>
      }
    >
      <input
        className="field__input mf-search"
        type="search"
        placeholder="Scheme name, AMC, scheme code or ISIN…"
        aria-label="Search schemes"
        value={query}
        onChange={(e) => setQuery(e.target.value)}
      />

      {!typed ? (
        <p className="empty">
          Type at least two characters to search {scope}: a name, an AMC, a scheme code or an ISIN. Up to {SEARCH_TAKE}{' '}
          matches, active schemes first; each opens its fund report.
        </p>
      ) : (
        <QueryBoundary query={search} empty={`No scheme matches “${settled.trim()}”${plan || option ? ' with these filters' : ''}.`}>
          {(rows) => (
            <>
              <div className="tablewrap tablewrap--tall">
                <table className="table">
                  <thead>
                    <tr>
                      <th className="c">Pick</th>
                      <th>Scheme</th>
                      <th>Plan</th>
                      <th>Option</th>
                      <th className="col--wide">Category</th>
                      <th>NAVs stored</th>
                      <th>Status</th>
                    </tr>
                  </thead>
                  <tbody>
                    {rows.map((s) => {
                      const hasNavs = Boolean(s.firstNavDate)
                      const isPicked = picked.some((p) => p.schemeCode === s.schemeCode)
                      const full = !isPicked && picked.length >= MF_COMPARE_MAX
                      const category = splitCategory(s.category).name
                      return (
                        <tr key={s.schemeCode} className={isPicked ? 'row--selected' : ''}>
                          <td className="c">
                            <input
                              type="checkbox"
                              checked={isPicked}
                              disabled={!hasNavs || full}
                              onChange={() => toggle(s)}
                              aria-label={`Pick ${s.schemeName} to compare`}
                              title={
                                !hasNavs
                                  ? 'No NAVs stored for this scheme'
                                  : full
                                    ? `${MF_COMPARE_MAX} schemes picked already`
                                    : 'Pick to compare'
                              }
                            />
                          </td>
                          <td className="mf-wrap">
                            {hasNavs ? <Link to={reportHref(s.schemeCode)}>{s.schemeName}</Link> : s.schemeName}
                            <span className="cell-sub">
                              {s.amc || '—'} · {s.schemeCode}
                              {s.isinGrowth ? ` · ${s.isinGrowth}` : ''}
                            </span>
                            <span className="cell-sub col--phone">{category}</span>
                          </td>
                          <td>{planLabel(s.plan)}</td>
                          <td>{s.optionLabel || optionLabel(s.option)}</td>
                          <td className="col--wide muted mf-wrap" title={s.category}>
                            {category}
                          </td>
                          <td>
                            {navRange(s)}
                            {s.lastNav != null && <span className="cell-sub">last {formatNav(s.lastNav)}</span>}
                          </td>
                          <td>
                            <Badge tone={s.isActive ? 'pos' : 'neutral'}>{s.isActive ? 'Active' : 'Inactive'}</Badge>
                          </td>
                        </tr>
                      )
                    })}
                  </tbody>
                </table>
              </div>
              {rows.length >= SEARCH_TAKE && (
                <p className="small-note">The first {SEARCH_TAKE} matches are shown; more text narrows them.</p>
              )}
            </>
          )}
        </QueryBoundary>
      )}

      <div className="mf-picked" aria-live="polite">
        {picked.length === 0 ? (
          <span className="small-note mf-flush">
            Tick {MF_COMPARE_MIN} to {MF_COMPARE_MAX} schemes to compare them side by side; ticks stay while you search again.
          </span>
        ) : (
          <>
            <span className="small-note mf-flush">Picked:</span>
            {picked.map((p) => (
              <button
                key={p.schemeCode}
                type="button"
                className="btn btn--ghost btn--sm"
                onClick={() => toggle(p)}
                aria-label={`Remove ${p.schemeName}`}
                title={p.schemeName}
              >
                <span className="mf-picked__name">{p.schemeName}</span> <IconX style={{ width: 12, height: 12 }} />
              </button>
            ))}
          </>
        )}
        <button
          type="button"
          className="btn btn--primary btn--sm"
          disabled={!canCompare}
          onClick={() => navigate(compareHref(picked.map((p) => p.schemeCode)))}
          title={canCompare ? undefined : `Pick ${MF_COMPARE_MIN} to ${MF_COMPARE_MAX} schemes`}
        >
          Compare{picked.length > 0 ? ` (${picked.length})` : ''}
        </button>
      </div>
    </Panel>
  )
}

/** One fund-report job: its last run and a Run now button (admin; the API decides and says why not). */
function JobRow({ job, label, what, analytics }: { job: MutualFundJob; label: string; what: string; analytics: MutualFundAnalyticsCoverage }) {
  const run = useRunMutualFundJob()
  const last = jobRun(analytics, job)
  const waiting = jobRequested(analytics, job)
  return (
    <tr>
      <td className="mf-wrap">
        {label}
        <span className="cell-sub">{what}</span>
      </td>
      <td>
        {last ? (
          <>
            {formatDateTime(last.startedUtc)} IST
            <span className="cell-sub">{last.trigger}</span>
          </>
        ) : (
          <span className="muted">Never run</span>
        )}
      </td>
      <td className="mf-wrap">
        {last ? (
          <>
            <Badge tone={last.ok ? 'pos' : 'neg'}>{last.ok ? 'Worked' : 'Failed'}</Badge> {last.message}
            {last.reached && <span className="cell-sub">reached {formatDay(last.reached)}</span>}
          </>
        ) : (
          <span className="muted">—</span>
        )}
      </td>
      <td className="mf-wrap">
        <div className="toolbar">
          <button type="button" className="btn btn--sm" disabled={run.isPending} onClick={() => run.mutate(job)}>
            {run.isPending ? 'Asking…' : 'Run now'}
          </button>
          {waiting && <Badge tone="warn">Asked for: waits for its turn</Badge>}
        </div>
        {run.data && <span className="cell-sub">{run.data.message}</span>}
        {run.isError && <span className="cell-sub neg">{errorText(run.error)}</span>}
      </td>
    </tr>
  )
}

/** What the fund report uses besides the NAVs, and the jobs that collect it. */
function ReportData({ analytics }: { analytics: MutualFundAnalyticsCoverage | null | undefined }) {
  // Always sent; the guard only keeps a null from blanking the page.
  if (!analytics) {
    return (
      <Panel title="Report data">
        <p className="empty">The coverage answer carried no report data (benchmarks, TER, category ranking).</p>
      </Panel>
    )
  }
  return (
    <Panel title="Report data">
      <p className="small-note mf-flush">
        What the fund reports use besides the NAVs: benchmark index values, AMFI&rsquo;s total expense ratios and the
        nightly category ranking.
      </p>
      <div className="kv-grid mf-gap">
        <div>
          <span className="muted">Benchmark indices</span>
          <span>{formatNumber(analytics.benchmarks.length)}</span>
        </div>
        <div>
          <span className="muted">TER, latest day</span>
          <span>{analytics.terLatestDate ? formatDay(analytics.terLatestDate) : 'none stored'}</span>
        </div>
        <div>
          <span className="muted">TER, schemes</span>
          <span>{formatNumber(analytics.terSchemes)}</span>
        </div>
        <div>
          <span className="muted">Category ranking as of</span>
          <span>{analytics.statsAsOf ? formatDay(analytics.statsAsOf) : 'not computed yet'}</span>
        </div>
        <div>
          <span className="muted">Schemes ranked</span>
          <span>{formatNumber(analytics.statsSchemes)}</span>
        </div>
        <div>
          <span className="muted">Ranking computed</span>
          <span>{analytics.statsComputedUtc ? `${formatDateTime(analytics.statsComputedUtc)} IST` : '—'}</span>
        </div>
      </div>

      <h3 className="section-title mf-gap">Benchmark indices stored</h3>
      {analytics.benchmarks.length === 0 ? (
        <p className="empty">No benchmark values stored yet.</p>
      ) : (
        <div className="tablewrap tablewrap--rows8">
          <table className="table">
            <thead>
              <tr>
                <th>Index</th>
                <th className="r">Values</th>
                <th>From – to</th>
                <th>History</th>
              </tr>
            </thead>
            <tbody>
              {analytics.benchmarks.map((b) => (
                <tr key={b.index}>
                  <td>
                    {b.name || b.index}
                    {b.name && b.name !== b.index && <span className="cell-sub">{b.index}</span>}
                  </td>
                  <td className="num">{formatNumber(b.rows)}</td>
                  <td>{b.firstDate ? dayRange(b.firstDate, b.lastDate) : 'none stored'}</td>
                  <td>
                    <Badge tone={b.historyComplete ? 'pos' : 'warn'}>{b.historyComplete ? 'Complete' : 'Partial'}</Badge>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <h3 className="section-title mf-gap">Jobs</h3>
      <div className="tablewrap">
        <table className="table">
          <thead>
            <tr>
              <th>Job</th>
              <th>Last run</th>
              <th>Result</th>
              <th>Run</th>
            </tr>
          </thead>
          <tbody>
            {MF_JOBS.map((j) => (
              <JobRow key={j.key} job={j.key} label={j.label} what={j.what} analytics={analytics} />
            ))}
          </tbody>
        </table>
      </div>
      <p className="small-note">
        Run now starts a job at once outside market hours; inside them it waits for the close. Admins only: the API says
        so when it refuses.
      </p>
    </Panel>
  )
}

function CountTable({ rows, label, name }: { rows: MutualFundCount[]; label: string; name: (key: string) => string }) {
  return (
    <div className="tablewrap tablewrap--rows8">
      <table className="table">
        <thead>
          <tr>
            <th>{label}</th>
            <th className="r">Schemes</th>
            <th className="r">Active</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => (
            <tr key={r.key}>
              <td>{name(r.key)}</td>
              <td className="num">{formatNumber(r.schemes)}</td>
              <td className="num">{formatNumber(r.active)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function CategoryTable({ rows }: { rows: MutualFundCount[] }) {
  return (
    <div className="tablewrap tablewrap--rows8">
      <table className="table">
        <thead>
          <tr>
            <th>Category</th>
            <th className="col--wide">Type</th>
            <th className="r">Schemes</th>
            <th className="r">Active</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => {
            const { type, name } = splitCategory(r.key)
            return (
              <tr key={r.key} title={r.key}>
                <td>
                  {name}
                  {/* The type column is hidden on a phone; "Income" is both open and close ended. */}
                  <span className="col--phone muted">{type}</span>
                </td>
                <td className="col--wide muted">{type}</td>
                <td className="num">{formatNumber(r.schemes)}</td>
                <td className="num">{formatNumber(r.active)}</td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}

/** Start or pause the history backfill (admin; the API checks). It resumes where it stopped. */
function BackfillControls({ backfill }: { backfill: MutualFundCoverage['backfill'] }) {
  const queryClient = useQueryClient()
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['mutualFunds', 'coverage'] })
  const start = useMutation({ mutationFn: () => api.post('/api/MutualFunds/backfill'), onSuccess: refresh })
  const pause = useMutation({ mutationFn: () => api.delete('/api/MutualFunds/backfill'), onSuccess: refresh })
  if (!backfill.enabled || backfill.state === 'done') return null
  const active = ['queued', 'running', 'waiting'].includes(backfill.state)
  const failed = start.error ?? pause.error
  return (
    <Panel title="History backfill">
      <p className="small-note">
        Reads AMFI's history from 1 Apr 2006, newest first: one request every few seconds, never in market hours, and it
        picks up where it stopped after a pause or a restart. About 2–4 hours in all.
      </p>
      <div className="toolbar">
        {active ? (
          <button type="button" className="btn btn--ghost btn--sm" disabled={pause.isPending} onClick={() => pause.mutate()}>
            Pause
          </button>
        ) : (
          <button type="button" className="btn btn--primary btn--sm" disabled={start.isPending} onClick={() => start.mutate()}>
            {backfill.state === 'paused' || backfill.state === 'failed' ? 'Resume the backfill' : 'Start the backfill'}
          </button>
        )}
      </div>
      {failed && <p className="neg">{failed instanceof Error ? failed.message : String(failed)}</p>}
    </Panel>
  )
}

function Coverage({ data }: { data: MutualFundCoverage }) {
  const fresh = freshness(data.nav)
  const history = backfillSummary(data.backfill)
  const last = data.lastSync
  const toneOf = (tone: string) => (tone === 'pos' || tone === 'warn' || tone === 'neg' ? tone : undefined)

  return (
    <>
      <div className="stat-grid">
        <StatTile
          label="Schemes"
          value={formatNumber(data.schemes)}
          sub={`${formatNumber(data.activeSchemes)} active: a NAV within 30 days`}
        />
        <StatTile
          label="NAVs stored"
          value={`${data.nav.rowsApproximate ? '≈ ' : ''}${formatNumber(data.nav.rows)}`}
          sub={data.nav.firstDate ? `${formatDay(data.nav.firstDate)} to ${formatDay(data.nav.lastDate)}` : 'none yet'}
        />
        <StatTile label="Freshness" value={fresh.label} tone={toneOf(fresh.tone)} sub={fresh.detail} />
        <StatTile label="History" value={history.label} tone={toneOf(history.tone)} sub={history.detail} />
      </div>

      <BackfillControls backfill={data.backfill} />

      <Panel title="Last sync">
        {last ? (
          <div className="kv-grid">
            <div>
              <span className="muted">When</span>
              <span>
                {formatDateTime(last.startedUtc)} IST · {last.trigger}
              </span>
            </div>
            <div>
              <span className="muted">Result</span>
              <span>
                <Badge tone={last.ok ? 'pos' : 'neg'}>{last.ok ? 'Worked' : 'Failed'}</Badge>
              </span>
            </div>
            <div>
              <span className="muted">AMFI file for</span>
              <span>{formatDay(last.asOfDate)}</span>
            </div>
            <div>
              <span className="muted">NAVs written</span>
              <span>{formatNumber(last.navRowsWritten)}</span>
            </div>
          </div>
        ) : (
          <p className="empty">No sync has run yet.</p>
        )}
        {last && <p className={last.ok ? '' : 'neg'}>{last.message}</p>}
        <p className="small-note">
          {data.syncEnabled
            ? "AMFI's daily file is read at 23:30 IST every day, again at 07:00 IST when the evening fell short of the last business day, and an hour after a failure."
            : 'The daily sync is switched off (MutualFunds:SyncEnabled).'}
          {data.syncRunning ? ' A sync is running now.' : ''}
        </p>
      </Panel>

      <ReportData analytics={data.analytics} />

      <div className="two-col">
        <Panel title="By plan">
          <CountTable rows={data.byPlan} label="Plan" name={planLabel} />
        </Panel>
        <Panel title="By option">
          <CountTable rows={data.byOption} label="Option" name={optionLabel} />
        </Panel>
      </div>

      <Panel title={`By category · ${formatNumber(data.byCategory.length)}`}>
        <CategoryTable rows={data.byCategory} />
      </Panel>

      <Panel title={`By AMC · ${formatNumber(data.byAmc.length)}`}>
        <CountTable rows={data.byAmc} label="AMC" name={(key) => key || '—'} />
      </Panel>

      <p className="small-note">
        Source: AMFI (portal.amfiindia.com), its daily NAV file and NAV history. Plan and option are cleaned from
        AMFI&rsquo;s own columns, then the scheme name, then the ISINs; &ldquo;Not stated&rdquo; is what none of them
        says (ETFs, and plans from before direct plans existed). Categories are AMFI&rsquo;s section lines as written,
        so older names stay apart from SEBI&rsquo;s current ones. Facts only: nothing here rates a fund or says what to
        do with one.
      </p>
    </>
  )
}

export function MutualFundsPage() {
  const coverage = useMutualFundCoverage()

  return (
    <div className="page data-page">
      <header className="page__header">
        <div>
          <h1 className="page__title">Mutual funds</h1>
          <p className="page__subtitle">
            Every scheme AMFI publishes and its daily NAV: what is stored, how current it is and how far back it goes.
            Search a scheme for its fund report, or pick two to four to compare.
          </p>
        </div>
      </header>

      <FundSearch coverage={coverage.data} />

      <QueryBoundary query={coverage}>{(data) => <Coverage data={data} />}</QueryBoundary>
    </div>
  )
}
