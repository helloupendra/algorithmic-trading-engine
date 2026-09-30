/**
 * AI → Reports (/ai/reports): what the scheduled agents wrote.
 *
 * Three agents run on their own and write reports: the Trade Reviewer (a
 * review of each run stopped that day, against its written spec), the News
 * Analyst (a structured event for each news item or filing) and the Incident
 * Explainer (what happened, why and what to do, for each serious incident).
 * Each report is checked before it counts. One whose answer fails the check
 * (not the JSON asked for, or a number whose quote is not in the item) is
 * kept as "invalid" with the model's raw text and the reason; one no model
 * answered is "failed" and retried after 15 minutes, three times at most.
 *
 * The strip at the top is the last seven days per agent, with the News
 * Analyst's share of valid reports against its 98% target. ?id= opens a
 * report in full above the list, one URL per report, as the Calls tab does.
 */

import { useMemo } from 'react'
import type { MouseEvent } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import {
  NEWS_VALID_TARGET,
  REPORT_STATUS_KEYS,
  SCHEDULED_AGENTS,
  callTime,
  istDay,
  modelName,
  reportStatus,
  reportSubjectLabel,
  useAiAgents,
  useAiReport,
  useAiReportStats,
  useAiReports,
  validityTone,
} from '../../lib/ai'
import type { AiReportAgentStats, AiReportDetail, AiReportFilters, AiReportSummary } from '../../lib/ai'
import { formatAge, formatDateTime } from '../../lib/format'
import { DateField } from '../../components/DateField'
import { EmptyState, InlineError, Loading, Panel } from '../../components/ui'
import { AnswerText } from './AnswerText'
import { CallLink } from './parts'
import { ReportData, ReportStatusBadge, ReportSubject } from './reportParts'
import { useNow } from './common'
import '../system/health/health.css'
import './ai.css'

const PAGE = '/ai/reports'
const TAKE = 50

function idParam(raw: string | null): number | null {
  if (!raw || !/^\d+$/.test(raw)) return null
  const n = Number(raw)
  return Number.isSafeInteger(n) && n > 0 ? n : null
}

/** Seven small stacked columns, one a day: ok, invalid and failed, scaled to the busiest day. */
function DayBars({ days }: { days: AiReportAgentStats['days'] }) {
  const max = Math.max(1, ...days.map((d) => d.ok + d.invalid + d.failed))
  const said = days.map((d) => `${d.date}: ${d.ok} ok, ${d.invalid} invalid, ${d.failed} failed`).join('; ')
  return (
    <div className="ai-daybars" role="img" aria-label={said}>
      {days.map((d) => (
        <span key={d.date} className="ai-daybars__day" title={`${d.date}: ${d.ok} ok · ${d.invalid} invalid · ${d.failed} failed`}>
          {d.failed > 0 && <span className="ai-daybars__seg ai-daybars__seg--failed" style={{ height: `${(100 * d.failed) / max}%` }} />}
          {d.invalid > 0 && <span className="ai-daybars__seg ai-daybars__seg--invalid" style={{ height: `${(100 * d.invalid) / max}%` }} />}
          {d.ok > 0 && <span className="ai-daybars__seg ai-daybars__seg--ok" style={{ height: `${(100 * d.ok) / max}%` }} />}
        </span>
      ))}
    </div>
  )
}

function StatsCard({ a }: { a: AiReportAgentStats }) {
  const news = a.agentKey === 'news-analyst'
  const tone = validityTone(a.validPercent)
  return (
    <div className="ai-rstat">
      <div className="ai-rstat__head">
        <Link className="ai-rstat__name" to={`${PAGE}?agent=${encodeURIComponent(a.agentKey)}`}>
          {a.agentName}
        </Link>
        <span className="faint">{a.total} in 7 days</span>
      </div>
      <div className="ai-rstat__counts">
        <span className={a.ok > 0 ? 'pos' : 'faint'}>{a.ok} ok</span>
        <span className={a.invalid > 0 ? 'warn' : 'faint'}>{a.invalid} invalid</span>
        <span className={a.failed > 0 ? 'neg' : 'faint'}>{a.failed} failed</span>
      </div>
      {news && (
        <div className={`ai-rstat__valid ${tone === 'neutral' ? 'faint' : tone}`}>
          {a.validPercent == null ? (
            `valid: not known yet · target ${NEWS_VALID_TARGET}%`
          ) : (
            <>
              valid {a.validPercent.toFixed(1)}% · target {NEWS_VALID_TARGET}%
              {a.validPercent < NEWS_VALID_TARGET && <b> · below target</b>}
            </>
          )}
        </div>
      )}
      {a.days.length > 0 && <DayBars days={a.days} />}
    </div>
  )
}

function ReportDetailView({ detail, now }: { detail: AiReportDetail; now: number }) {
  const r = detail.report
  const status = reportStatus(r.status)
  return (
    <div className="ai-call ai-report">
      <div className="ai-call__head">
        <h3 className="ai-call__title">Report #{r.id}</h3>
        <ReportStatusBadge status={r.status} />
        <span className="ai-call__who muted">{r.agentName || r.agentKey}</span>
        <Link className="btn btn--ghost btn--sm ai-call__close" to={PAGE}>
          Close
        </Link>
      </div>
      <p className="ai-report__title">{r.title || reportSubjectLabel(r)}</p>
      <p className="ai-call__when faint">
        About <ReportSubject report={r} />
        {r.sessionDate && ` · session ${r.sessionDate}`} · written {formatDateTime(r.createdUtc)} IST ({formatAge(r.createdUtc)})
        {r.updatedUtc && r.updatedUtc !== r.createdUtc && ` · updated ${callTime(r.updatedUtc, now)}`}
        {r.attempts > 1 && ` · ${r.attempts} attempts`}
        {r.model && (
          <>
            {' · '}
            <span title={r.model}>{modelName(r.model)}</span>
          </>
        )}
        {r.callId != null && (
          <>
            {' · '}
            <CallLink id={r.callId} />
          </>
        )}
      </p>

      {r.status === 'invalid' && (
        <div className="alert alert--warn ai-flush" role="status">
          <span>
            The answer failed its check, so it does not count: {r.error || 'no reason given'}. The model's text is kept below as
            it was written.
          </span>
        </div>
      )}
      {r.status === 'failed' && (
        <div className="alert alert--error ai-flush" role="alert">
          <span>
            No model answered{r.attempts > 1 ? ` in ${r.attempts} attempts` : ''}: {r.error || 'no reason given'}
          </span>
        </div>
      )}
      {r.status !== 'ok' && r.status !== 'invalid' && r.status !== 'failed' && (
        <p className="small-note ai-flush">Status: {status.label}</p>
      )}

      <ReportData detail={detail} />

      <section className="ai-call__sec">
        <h4 className="ai-call__h">{r.status === 'invalid' ? "The model's answer, as written" : 'Report'}</h4>
        {detail.body.trim() === '' ? (
          <p className="faint ai-flush">{r.status === 'failed' ? 'Nothing was written.' : 'Empty.'}</p>
        ) : r.status === 'invalid' ? (
          <pre className="ai-pre">{detail.body}</pre>
        ) : (
          <div className="ai-call__answer">
            <AnswerText text={detail.body} />
          </div>
        )}
      </section>
    </div>
  )
}

function ReportRow({ r, selected, now, onOpen }: { r: AiReportSummary; selected: boolean; now: number; onOpen: (id: number, e: MouseEvent) => void }) {
  return (
    <tr className={selected ? 'row--selected' : undefined} onClick={(e) => onOpen(r.id, e)}>
      <td className="ai-r-when">
        <Link to={`${PAGE}?id=${r.id}`} className="ai-c-link" title={`${formatDateTime(r.createdUtc)} IST · report #${r.id}`}>
          {callTime(r.createdUtc, now)}
        </Link>
      </td>
      <td className="ai-r-agent">{r.agentName || r.agentKey}</td>
      <td className="ai-r-subject">
        <ReportSubject report={r} />
      </td>
      <td className="ai-r-title">
        <span className="ai-c-summary__text">{r.title || <span className="faint">—</span>}</span>
        {r.error && r.status !== 'ok' && <span className={`ai-c-summary__err ${r.status === 'invalid' ? 'ai-c-summary__err--warn' : ''}`}>{r.error}</span>}
      </td>
      <td className="ai-r-status">
        <ReportStatusBadge status={r.status} />
        {r.attempts > 1 && <span className="cell-sub">{r.attempts} attempts</span>}
      </td>
      <td className={`ai-r-model ${r.model ? '' : 'ai-c-none'}`}>{r.model ? <span title={r.model}>{modelName(r.model)}</span> : <span className="faint">—</span>}</td>
      <td className={`ai-r-call ${r.callId != null ? '' : 'ai-c-none'}`}>{r.callId != null ? <CallLink id={r.callId} /> : <span className="faint">—</span>}</td>
    </tr>
  )
}

export function AiReportsPage() {
  const [params, setParams] = useSearchParams()
  const navigate = useNavigate()
  const selectedId = idParam(params.get('id'))
  const filters: AiReportFilters = {
    agent: params.get('agent') ?? '',
    status: params.get('status') ?? '',
    date: params.get('date') ?? '',
    take: TAKE,
  }
  const reports = useAiReports(filters)
  const selected = useAiReport(selectedId)
  const stats = useAiReportStats(7)
  const agents = useAiAgents()
  const now = useNow(30_000)

  const rows = useMemo(() => reports.data?.pages.flatMap((p) => p.reports) ?? [], [reports.data])
  const agentOptions = useMemo(() => {
    const m = new Map<string, string>()
    for (const key of Object.keys(SCHEDULED_AGENTS)) m.set(key, agents.data?.agents.find((a) => a.key === key)?.name ?? key)
    for (const r of rows) if (!m.has(r.agentKey)) m.set(r.agentKey, r.agentName || r.agentKey)
    if (filters.agent && !m.has(filters.agent)) m.set(filters.agent, filters.agent)
    return [...m.entries()]
  }, [agents.data, rows, filters.agent])

  const setFilter = (key: 'agent' | 'status' | 'date', value: string) => {
    const p = new URLSearchParams(params)
    if (value) p.set(key, value)
    else p.delete(key)
    setParams(p, { replace: true })
  }
  const filtered = !!(filters.agent || filters.status || filters.date)
  const clear = () => {
    const p = new URLSearchParams()
    if (selectedId != null) p.set('id', String(selectedId))
    setParams(p, { replace: true })
  }
  const open = (id: number, e: MouseEvent) => {
    if ((e.target as HTMLElement).closest('a')) return
    const p = new URLSearchParams(params)
    p.set('id', String(id))
    navigate(`${PAGE}?${p.toString()}`)
    window.scrollTo({ top: 0 })
  }
  const nameOf = (key: string) => agents.data?.agents.find((a) => a.key === key)?.name ?? key

  return (
    <div className="page hp ai">
      <div className="hp-bar">
        <p className="hp-bar__lead muted">
          What the scheduled agents wrote. Each report is checked before it counts; one that fails its check is kept and marked
          invalid, and one no model answered is retried after 15 minutes, three times at most.
        </p>
        <span className="sys-asof faint">refreshes every 30 s</span>
      </div>

      <section className="ai-rstats" aria-label="The last 7 days">
        {stats.isPending ? (
          <Loading label="Counting the last 7 days…" />
        ) : !stats.data ? (
          <p className="small-note warn ai-flush">The 7-day counts could not be read: {(stats.error as Error)?.message}</p>
        ) : stats.data.agents.length === 0 ? (
          <p className="small-note ai-flush">No scheduled agent has written anything in the last 7 days.</p>
        ) : (
          stats.data.agents.map((a) => <StatsCard key={a.agentKey} a={a} />)
        )}
      </section>

      {selectedId != null && (
        <Panel className="ai-call-panel">
          {selected.isPending ? (
            <Loading label={`Reading report #${selectedId}…`} />
          ) : selected.data ? (
            <ReportDetailView detail={selected.data} now={now} />
          ) : (
            <>
              <InlineError error={selected.error} />
              <p className="small-note ai-flush">
                <Link to={PAGE}>Back to the list</Link>
              </p>
            </>
          )}
        </Panel>
      )}

      <div className="ai-tools">
        <select className="field__input field__input--sm ai-select" aria-label="Agent" value={filters.agent} onChange={(e) => setFilter('agent', e.target.value)}>
          <option value="">Every agent</option>
          {agentOptions.map(([key, name]) => (
            <option key={key} value={key}>
              {name}
            </option>
          ))}
        </select>
        <select className="field__input field__input--sm ai-select" aria-label="Status" value={filters.status} onChange={(e) => setFilter('status', e.target.value)}>
          <option value="">Every status</option>
          {REPORT_STATUS_KEYS.map((s) => (
            <option key={s} value={s}>
              {reportStatus(s).label}
            </option>
          ))}
        </select>
        <DateField
          className="field__input field__input--sm field__input--date"
          aria-label="Session day (IST)"
          title="The IST trading day a report is about"
          max={istDay(now)}
          value={filters.date ?? ''}
          onChange={(iso) => setFilter('date', iso)}
        />
        {filtered && (
          <button type="button" className="btn btn--ghost btn--sm" onClick={clear}>
            Clear
          </button>
        )}
        <span className="ai-tools__count faint">{reports.data ? `${rows.length} shown${reports.hasNextPage ? ', more older' : ''}` : ''}</span>
      </div>

      {reports.isPending ? (
        <Loading label="Reading the reports…" />
      ) : !reports.data ? (
        <>
          <InlineError error={reports.error} />
          <p className="small-note ai-flush">The reports come from GET /api/Ai/reports; until it answers, none is known.</p>
        </>
      ) : rows.length === 0 ? (
        filtered ? (
          <EmptyState>No report matches those filters.</EmptyState>
        ) : (
          <div className="ai-chat__empty ai-reports-empty">
            <p className="ai-chat__lead">Nothing has been written yet. Each agent writes on its own schedule:</p>
            <dl className="ai-chat__facts">
              {Object.entries(SCHEDULED_AGENTS).map(([key, a]) => (
                <div key={key}>
                  <dt>{nameOf(key)}</dt>
                  <dd>{a.writes}</dd>
                </div>
              ))}
            </dl>
            <p className="small-note ai-flush">
              An agent that is switched off writes nothing; one can be started by hand from the <Link to="/ai/agents">Agents</Link> tab.
            </p>
          </div>
        )
      ) : (
        <>
          <div className="tablewrap">
            <table className="table table--hover ai-calls ai-reports">
              <thead>
                <tr>
                  <th>Time IST</th>
                  <th>Agent</th>
                  <th>About</th>
                  <th>Title</th>
                  <th>Status</th>
                  <th>Model</th>
                  <th>Call</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((r) => (
                  <ReportRow key={r.id} r={r} selected={r.id === selectedId} now={now} onOpen={open} />
                ))}
              </tbody>
            </table>
          </div>
          {reports.hasNextPage && (
            <div className="ai-more">
              <button type="button" className="btn btn--sm" disabled={reports.isFetchingNextPage} onClick={() => void reports.fetchNextPage()}>
                {reports.isFetchingNextPage ? 'Reading older reports…' : 'Older'}
              </button>
            </div>
          )}
          {reports.isFetchNextPageError && <InlineError error={reports.error} />}
        </>
      )}
    </div>
  )
}
