/**
 * The pieces a report is shown with, wherever it is shown: the Reports tab,
 * a run's own page (the Trade Reviewer's review) and the Incidents page (the
 * Incident Explainer's explanation). A report's status, the link to what it
 * is about, and its structured data laid out for the agent that wrote it.
 */

import { useState } from 'react'
import type { ReactNode } from 'react'
import { Link } from 'react-router-dom'
import {
  CHECK_PASS_MARK,
  confidenceText,
  directionBadge,
  formatSeconds,
  modelName,
  readAssistantCheck,
  firstParagraph,
  useAiReport,
  useLatestReport,
  readIncidentExplanation,
  readNewsEvent,
  readTradeReview,
  reportLink,
  reportStatus,
  reportSubjectLabel,
  urgencyBadge,
  verdictBadge,
} from '../../lib/ai'
import type { AiReportDetail, AiReportSummary } from '../../lib/ai'
import { ApiError } from '../../lib/api'
import { formatDateTime } from '../../lib/format'
import { Badge } from '../../components/ui'
import { IconChip } from '../../components/icons'
import { AnswerText } from './AnswerText'
import './ai.css'

export function ReportStatusBadge({ status }: { status: string }) {
  const s = reportStatus(status)
  return (
    <span className={`badge badge--${s.tone}`} title={s.means || undefined}>
      {s.label}
    </span>
  )
}

/**
 * What a report is about, linked when it has a link: a run or an incident in
 * the console, an article or a filing in a new tab (never with the console
 * as its opener). A link that is neither is shown as text.
 */
export function ReportSubject({ report, children }: { report: Pick<AiReportSummary, 'id' | 'subjectType' | 'subjectId' | 'link'>; children?: ReactNode }) {
  const label = children ?? reportSubjectLabel(report)
  // The assistant check is about a day, which has no page of its own: it links to itself.
  const link = reportLink(report.link ?? (report.subjectType === 'check' ? `/ai/reports?id=${report.id}` : null))
  if (!link) return <span>{label}</span>
  if (link.kind === 'internal') {
    return (
      <Link className="ai-subject" to={link.to}>
        {label}
      </Link>
    )
  }
  return (
    <a className="ai-subject" href={link.href} target="_blank" rel="noopener noreferrer" title={link.href}>
      {label} ↗
    </a>
  )
}

/** A bullet list of plain strings, or a quiet line when there are none. */
function Lines({ items, none }: { items: string[]; none: string }) {
  if (items.length === 0) return <span className="faint">{none}</span>
  return (
    <ul className="ai-report-list">
      {items.map((line, i) => (
        <li key={i}>{line}</li>
      ))}
    </ul>
  )
}

/**
 * The report's data, laid out for the agent that wrote it: the review's
 * verdict and what was and was not followed; the news event with each number
 * and the words it was read from; the explanation's urgency. Null when the
 * report has no data (an invalid or failed one) or an agent this page does
 * not know, so the body speaks for itself.
 */
export function ReportData({ detail }: { detail: AiReportDetail }) {
  const { report, data } = detail
  if (!data) return null

  if (report.agentKey === 'trade-reviewer') {
    const r = readTradeReview(data)!
    const v = verdictBadge(r.verdict)
    return (
      <dl className="ai-report-data">
        <div>
          <dt>Verdict</dt>
          <dd>
            <Badge tone={v.tone}>{v.label}</Badge>
          </dd>
        </div>
        <div>
          <dt>Followed</dt>
          <dd>
            <Lines items={r.followed} none="nothing listed" />
          </dd>
        </div>
        <div>
          <dt>Did not follow</dt>
          <dd>
            <Lines items={r.deviations} none="nothing: every step followed the spec" />
          </dd>
        </div>
        <div>
          <dt>Stale fills</dt>
          <dd className={r.staleFills ? 'warn' : ''}>{r.staleFills == null ? <span className="faint">not known</span> : r.staleFills}</dd>
        </div>
        {r.marketContext && (
          <div>
            <dt>Market</dt>
            <dd>{r.marketContext}</dd>
          </div>
        )}
        {r.lesson && (
          <div>
            <dt>Lesson</dt>
            <dd>{r.lesson}</dd>
          </div>
        )}
      </dl>
    )
  }

  if (report.agentKey === 'news-analyst') {
    const n = readNewsEvent(data)!
    const d = directionBadge(n.direction)
    return (
      <>
        <dl className="ai-report-data">
          <div>
            <dt>Event</dt>
            <dd>{n.event}</dd>
          </div>
          <div>
            <dt>Direction</dt>
            <dd>
              <Badge tone={d.tone}>{d.label}</Badge>
            </dd>
          </div>
          <div>
            <dt>Symbols</dt>
            <dd>
              {n.symbols.length === 0 ? (
                <span className="faint">none named</span>
              ) : (
                <span className="ai-toolchips">
                  {n.symbols.map((s) => (
                    <span key={s} className="ai-toolchip mono">
                      {s}
                    </span>
                  ))}
                </span>
              )}
            </dd>
          </div>
          <div>
            <dt>Confidence</dt>
            <dd>{confidenceText(n.confidence)}</dd>
          </div>
          {n.summary && (
            <div>
              <dt>Summary</dt>
              <dd>{n.summary}</dd>
            </div>
          )}
        </dl>
        {n.numbers.length > 0 && (
          <div className="tablewrap ai-report-numbers">
            <table className="table">
              <thead>
                <tr>
                  <th>Number</th>
                  <th className="num">Value</th>
                  <th>Unit</th>
                  <th>From the text</th>
                </tr>
              </thead>
              <tbody>
                {n.numbers.map((x, i) => (
                  <tr key={i}>
                    <td>{x.what}</td>
                    <td className="num mono">{x.value}</td>
                    <td className="muted">{x.unit}</td>
                    <td className="ai-report-quote">“{x.quote}”</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </>
    )
  }

  if (report.agentKey === 'assistant-check') {
    const c = readAssistantCheck(data)!
    const good = c.total > 0 && c.passed / c.total >= CHECK_PASS_MARK
    return (
      <>
        <div className="ai-check-score">
          <span className={`ai-check-score__big ${good ? 'pos' : 'warn'}`}>
            {c.passed} of {c.total}
          </span>
          <span className="muted">
            {c.total > 0 ? `${Math.round(100 * (c.score ?? c.passed / c.total))}% right` : 'no questions'} · the pass mark is{' '}
            {Math.round(CHECK_PASS_MARK * 100)}%
          </span>
        </div>
        {c.questions.length > 0 && (
          <div className="tablewrap ai-check">
            <table className="table">
              <thead>
                <tr>
                  <th className="c" aria-label="Right or wrong" />
                  <th>Question</th>
                  <th>Expected</th>
                  <th>The answer</th>
                  <th>Model</th>
                  <th className="num">Seconds</th>
                  <th>Call</th>
                </tr>
              </thead>
              <tbody>
                {c.questions.map((q, i) => (
                  <tr key={i} className={q.pass ? undefined : 'ai-check__miss'}>
                    <td className={`c ai-check__mark ${q.pass ? 'pos' : 'neg'}`} aria-label={q.pass ? 'right' : 'wrong'}>
                      {q.pass ? '✓' : '✗'}
                    </td>
                    <td className="ai-check__q">
                      {q.question}
                      <span className="cell-sub">graded as {q.kind}</span>
                    </td>
                    <td className="mono ai-check__expected">{q.expected || <span className="faint">—</span>}</td>
                    <td className="ai-check__answer">
                      {q.error ? <span className="neg">{q.error}</span> : q.answer ? <span>{q.answer}</span> : <span className="faint">no answer</span>}
                    </td>
                    <td className="ai-check__model">{q.model ? <span title={q.model}>{modelName(q.model)}</span> : <span className="faint">—</span>}</td>
                    <td className="num ai-check__secs">{formatSeconds(q.seconds)}</td>
                    <td className="ai-check__call">{q.callId != null ? <Link to={`/ai/calls?id=${q.callId}`}>call #{q.callId}</Link> : <span className="faint">—</span>}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </>
    )
  }

  if (report.agentKey === 'incident-explainer') {
    const e = readIncidentExplanation(data)!
    const u = urgencyBadge(e.urgency)
    return (
      <dl className="ai-report-data ai-report-data--inline">
        <div>
          <dt>Urgency</dt>
          <dd>
            <Badge tone={u.tone}>{u.label}</Badge>
          </dd>
        </div>
        <div>
          <dt>Confidence</dt>
          <dd>{confidenceText(e.confidence)}</dd>
        </div>
      </dl>
    )
  }

  return null
}

/** Why a report that is not ok says nothing useful, in one line. */
function notOkLine(r: AiReportSummary): string | null {
  if (r.status === 'invalid') return `The answer failed its check: ${r.error || 'no reason given'}.`
  if (r.status === 'failed') return `No model answered${r.attempts > 1 ? ` in ${r.attempts} attempts` : ''}: ${r.error || 'no reason given'}.`
  return null
}

/**
 * The Trade Reviewer's review on a run's own page (admins only: the reports
 * are). The verdict, the title and the review folded to its first paragraph,
 * with the rest a click away and the full report on the Reports tab. With no
 * review there is nothing to show, except one quiet line on a stopped run,
 * which is the kind the reviewer reads. An API without reports (404) shows
 * nothing at all.
 */
export function RunAiReview({ runId, stopped }: { runId: number; stopped: boolean }) {
  const review = useLatestReport('trade-reviewer', 'run', String(runId), true)
  const [open, setOpen] = useState(false)
  if (review.isPending) return null
  if (review.isError) {
    if (review.error instanceof ApiError && review.error.status === 404) return null
    return <p className="small-note muted ai-flush">The AI review could not be read.</p>
  }
  const d = review.data
  if (!d) return stopped ? <p className="small-note muted ai-flush">No AI review yet.</p> : null
  const r = d.report
  const verdict = readTradeReview(d.data)
  const v = verdictBadge(verdict?.verdict)
  const first = firstParagraph(d.body)
  const more = first !== d.body.trim()
  const problem = notOkLine(r)
  return (
    <section className="panel ai-review">
      <header className="panel__head">
        <h2 className="panel__title">
          <IconChip /> AI review
        </h2>
        <div className="panel__actions">
          <Link className="btn btn--sm btn--ghost" to={`/ai/reports?id=${r.id}`}>
            Report #{r.id} →
          </Link>
        </div>
      </header>
      <p className="ai-review__line">
        {verdict ? <Badge tone={v.tone}>{v.label}</Badge> : <ReportStatusBadge status={r.status} />}
        <b className="ai-review__title">{r.title}</b>
        <span className="faint">
          {r.agentName} · {formatDateTime(r.createdUtc)} IST
        </span>
      </p>
      {problem && <p className="small-note warn ai-flush">{problem}</p>}
      {d.body.trim() && (r.status === 'ok' ? <AnswerText text={open || !more ? d.body : first} /> : <pre className="ai-pre">{open ? d.body : first}</pre>)}
      {more && (
        <button type="button" className="btn btn--ghost btn--sm ai-review__more" aria-expanded={open} onClick={() => setOpen((o) => !o)}>
          {open ? 'Show less' : 'Read the review'}
        </button>
      )}
    </section>
  )
}

/**
 * The Incident Explainer's explanation inside an opened incident: how soon
 * someone is needed, and what happened, why and what to do. Read when the
 * incident is opened, from the report the list already named.
 */
export function IncidentAiExplanation({ summary }: { summary: AiReportSummary }) {
  const report = useAiReport(summary.id)
  const d = report.data
  const e = d ? readIncidentExplanation(d.data) : null
  const u = urgencyBadge(e?.urgency)
  const problem = notOkLine(summary)
  return (
    <div className="ai-explain">
      <h3 className="section-title incident-detail__label">
        AI explanation{' '}
        <Link className="section-title__link" to={`/ai/reports?id=${summary.id}`}>
          report #{summary.id}
        </Link>
      </h3>
      {report.isPending ? (
        <p className="faint ai-flush">Reading the explanation…</p>
      ) : !d ? (
        <p className="small-note warn ai-flush">The explanation could not be read.</p>
      ) : (
        <>
          <p className="ai-explain__line">
            {e ? <Badge tone={u.tone}>{u.label}</Badge> : <ReportStatusBadge status={summary.status} />}
            {e && e.confidence != null && <span className="faint">confidence {confidenceText(e.confidence)}</span>}
            <span className="faint">
              {summary.agentName} · {formatDateTime(summary.createdUtc)} IST
            </span>
          </p>
          {problem && <p className="small-note warn ai-flush">{problem}</p>}
          {d.body.trim() &&
            (summary.status === 'ok' ? (
              <div className="ai-explain__body">
                <AnswerText text={d.body} />
              </div>
            ) : (
              <pre className="ai-pre">{d.body}</pre>
            ))}
        </>
      )}
    </div>
  )
}
