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
  analystBadge,
  confidenceText,
  directionBadge,
  formatSeconds,
  modelName,
  readAssistantCheck,
  firstParagraph,
  useAiReport,
  useLatestReport,
  readIncidentExplanation,
  readMacroReport,
  readNewsEvent,
  readOptionsReport,
  readResultsReport,
  readTechnicalReport,
  readTradeReview,
  appliesBadge,
  readCircularTriage,
  readDataCheck,
  readResearchNote,
  readingBadge,
  reportLink,
  reportStatus,
  reportSubjectLabel,
  severityBadge,
  urgencyBadge,
  useMarkComplianceHandled,
  verdictBadge,
  yoyText,
  HANDLED_NOTE_MAX_CHARS,
} from '../../lib/ai'
import type { AiReportDetail, AiReportSummary, ComplianceFollowUp, ResearchCheck } from '../../lib/ai'
import { ApiError } from '../../lib/api'
import { formatDateTime, formatNumber } from '../../lib/format'
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

  if (report.agentKey === 'technical-analyst') {
    const t = readTechnicalReport(data)!
    if (t.indices.length === 0) return null
    return (
      <>
        <div className="tablewrap ai-report-numbers">
          <table className="table">
            <thead>
              <tr>
                <th>Index</th>
                <th>Bias</th>
                <th>Grade</th>
                <th>Key levels</th>
                <th>Reason</th>
              </tr>
            </thead>
            <tbody>
              {t.indices.map((i) => {
                const b = analystBadge(i.bias)
                return (
                  <tr key={i.underlying}>
                    <td className="mono">{i.underlying}</td>
                    <td>
                      <Badge tone={b.tone}>{b.label}</Badge>
                    </td>
                    <td className="mono">{i.grade}</td>
                    <td>
                      {i.keyLevels.length === 0 ? (
                        <span className="faint">none</span>
                      ) : (
                        <ul className="ai-report-list">
                          {i.keyLevels.map((l) => (
                            <li key={l.id || String(l.price)}>
                              <span className="mono">{formatNumber(l.price)}</span> <span className="muted">{l.what}</span>
                            </li>
                          ))}
                        </ul>
                      )}
                    </td>
                    <td className="ai-report-quote">{i.reason}</td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
        {t.summary && (
          <dl className="ai-report-data">
            <div>
              <dt>Summary</dt>
              <dd>{t.summary}</dd>
            </div>
          </dl>
        )}
      </>
    )
  }

  if (report.agentKey === 'options-analyst') {
    const o = readOptionsReport(data)!
    if (o.indices.length === 0) return null
    return (
      <dl className="ai-report-data">
        {o.indices.map((i) => {
          const b = analystBadge(i.stance)
          return (
            <div key={i.underlying}>
              <dt>{i.underlying}</dt>
              <dd>
                <Badge tone={b.tone}>{b.label}</Badge>
                <Lines items={i.lines} none="no lines" />
              </dd>
            </div>
          )
        })}
        {o.summary && (
          <div>
            <dt>Summary</dt>
            <dd>{o.summary}</dd>
          </div>
        )}
      </dl>
    )
  }

  if (report.agentKey === 'macro-analyst') {
    const m = readMacroReport(data)!
    if (!m.reason && m.restsOn.length === 0) return null
    const b = analystBadge(m.bias)
    return (
      <dl className="ai-report-data">
        <div>
          <dt>Bias</dt>
          <dd>
            <Badge tone={b.tone}>{b.label}</Badge>
          </dd>
        </div>
        <div>
          <dt>Size</dt>
          <dd>
            <span className="mono">×{m.sizeModifier == null ? '?' : m.sizeModifier.toFixed(2)}</span>
            {m.sizeModifierAsked != null && <span className="muted"> (it asked {m.sizeModifierAsked}; code keeps it within 0.5–1.0)</span>}
          </dd>
        </div>
        <div>
          <dt>Rests on</dt>
          <dd>
            <Lines items={m.restsOn.map((f) => `${f.id}: ${f.fact}`)} none="no fact named" />
          </dd>
        </div>
        {m.risks && (
          <div>
            <dt>Would change it</dt>
            <dd>{m.risks}</dd>
          </div>
        )}
      </dl>
    )
  }

  if (report.agentKey === 'fundamental-analyst') {
    const r = readResultsReport(data)!
    const s = analystBadge(r.results ? r.surprise : 'unclear')
    return (
      <>
        <dl className="ai-report-data">
          <div>
            <dt>Stock</dt>
            <dd className="mono">{r.symbol || '—'}</dd>
          </div>
          <div>
            <dt>Surprise</dt>
            <dd>{r.results ? <Badge tone={s.tone}>{s.label}</Badge> : <span className="faint">not the results themselves</span>}</dd>
          </div>
          {r.period && (
            <div>
              <dt>Period</dt>
              <dd>{r.period}</dd>
            </div>
          )}
          {r.summary && (
            <div>
              <dt>Summary</dt>
              <dd>{r.summary}</dd>
            </div>
          )}
        </dl>
        {r.metrics.length > 0 && (
          <div className="tablewrap ai-report-numbers">
            <table className="table">
              <thead>
                <tr>
                  <th>Metric</th>
                  <th className="num">Value</th>
                  <th>Unit</th>
                  <th className="num">Year earlier</th>
                  <th className="num">YoY</th>
                  <th>From the filing</th>
                </tr>
              </thead>
              <tbody>
                {r.metrics.map((x, i) => (
                  <tr key={i}>
                    <td>{x.metric}</td>
                    <td className="num mono">{formatNumber(x.value)}</td>
                    <td className="muted">{x.unit}</td>
                    <td className="num mono">{formatNumber(x.previous)}</td>
                    <td className="num mono" title={x.yoyComputed != null ? `From the two figures: ${yoyText({ yoyPct: null, yoyComputed: x.yoyComputed })}` : undefined}>
                      {yoyText(x)}
                    </td>
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

  if (report.agentKey === 'data-curator') {
    const c = readDataCheck(data)!
    return (
      <>
        <dl className="ai-report-data ai-report-data--inline">
          <div>
            <dt>Pass</dt>
            <dd>
              {c.pass || '—'}
              {c.checkedDay && <span className="muted"> · data of {c.checkedDay}</span>}
            </dd>
          </div>
          <div>
            <dt>Findings</dt>
            <dd>{c.findings.length === 0 ? 'none' : `${c.high} high · ${c.medium} medium · ${c.low} low`}</dd>
          </div>
        </dl>
        {c.findings.length > 0 && (
          <div className="tablewrap ai-report-numbers">
            <table className="table">
              <thead>
                <tr>
                  <th>Severity</th>
                  <th>What the check found</th>
                  <th>Breaks if ignored</th>
                  <th>Do</th>
                </tr>
              </thead>
              <tbody>
                {c.findings.map((f) => {
                  const s = severityBadge(f.severity)
                  return (
                    <tr key={f.id}>
                      <td>
                        <Badge tone={s.tone}>{s.label}</Badge>
                      </td>
                      <td>
                        <b>{f.subject}.</b> {f.facts}
                        <span className="cell-sub mono">{f.id}</span>
                      </td>
                      <td>{f.impact ?? <span className="faint">read by {f.readBy.join(', ') || 'nothing listed'}</span>}</td>
                      <td>{f.actionMeans ?? <span className="faint">—</span>}</td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
        )}
      </>
    )
  }

  if (report.agentKey === 'compliance-triage') {
    const t = readCircularTriage(data)!
    const a = appliesBadge(t.applies)
    return (
      <dl className="ai-report-data">
        <div>
          <dt>To this desk</dt>
          <dd>
            <Badge tone={a.tone}>{a.label}</Badge>
            {t.area && <span className="muted"> · {t.area}</span>}
          </dd>
        </div>
        {t.why && (
          <div>
            <dt>Why</dt>
            <dd>{t.why}</dd>
          </div>
        )}
        {t.change && (
          <div>
            <dt>Would change</dt>
            <dd>{t.change}</dd>
          </div>
        )}
        {t.effectiveDates.length > 0 && (
          <div>
            <dt>Takes effect</dt>
            <dd>
              <ul className="ai-report-list">
                {t.effectiveDates.map((d) => (
                  <li key={`${d.date}|${d.what}`}>
                    <b className="mono">{d.date}</b> {d.what}
                  </li>
                ))}
              </ul>
            </dd>
          </div>
        )}
        {t.deskChanges.length > 0 && (
          <div>
            <dt>On the desk</dt>
            <dd>
              <ul className="ai-report-list">
                {t.deskChanges.map((c, i) => (
                  <li key={i}>{c}</li>
                ))}
              </ul>
            </dd>
          </div>
        )}
        {t.quote && (
          <div>
            <dt>Rests on</dt>
            <dd className="ai-report-quote">“{t.quote}”</dd>
          </div>
        )}
        {(t.applies === 'yes' || detail.followUp) && report.status === 'ok' && (
          <div>
            <dt>Follow-up</dt>
            <dd>
              <CircularFollowUp reportId={report.id} followUp={detail.followUp} />
            </dd>
          </div>
        )}
      </dl>
    )
  }

  if (report.agentKey === 'strategy-researcher') {
    const n = readResearchNote(data)!
    return (
      <>
        {n.ideas.length > 0 && (
          <section className="ai-call__sec">
            <h4 className="ai-call__h">Worth testing (a backtest each, never a live change)</h4>
            <ol className="ai-report-list">
              {n.ideas.map((i, k) => (
                <li key={k}>
                  <b>{i.strategy}</b>: {i.idea}
                  <span className="cell-sub">Test: {i.test}</span>
                  {i.why && <span className="cell-sub">Why: {i.why}</span>}
                </li>
              ))}
            </ol>
          </section>
        )}
        {n.checks.length > 0 && <ResearchChecks checks={n.checks} />}
      </>
    )
  }

  return null
}

/**
 * A circular that applies: whether its one alert went, and whether the desk is ready for it. "Mark handled" takes a
 * note of what was done; the circular then leaves Sentinel's "Rule changes ahead" and Today, and no alert goes for
 * it if none went yet.
 */
function CircularFollowUp({ reportId, followUp }: { reportId: number; followUp: ComplianceFollowUp | null }) {
  const mark = useMarkComplianceHandled(reportId)
  const [open, setOpen] = useState(false)
  const [note, setNote] = useState('')
  const alerted = followUp?.alertedUtc ? (
    <span className="faint">
      Alert sent {formatDateTime(followUp.alertedUtc)} IST{followUp.alertDelivered ? '' : ' (Telegram did not take it)'}.{' '}
    </span>
  ) : null

  if (followUp?.handledUtc) {
    return (
      <p className="ai-flush">
        {alerted}
        <Badge tone="pos">Handled</Badge> by {followUp.handledBy ?? 'someone'} on {formatDateTime(followUp.handledUtc)} IST
        {followUp.note && <span className="cell-sub">{followUp.note}</span>}
      </p>
    )
  }

  if (!open) {
    return (
      <p className="ai-flush">
        {alerted}
        <button type="button" className="btn btn--sm" onClick={() => setOpen(true)}>
          Mark handled
        </button>
      </p>
    )
  }

  return (
    <form
      className="ai-mem__edit"
      onSubmit={(e) => {
        e.preventDefault()
        if (note.trim()) mark.mutate(note, { onSuccess: () => setOpen(false) })
      }}
    >
      <textarea
        className="field__input"
        rows={2}
        aria-label="What was done"
        placeholder="What was done, e.g. index runs stop at 15:45 from 7 Dec"
        maxLength={HANDLED_NOTE_MAX_CHARS}
        value={note}
        onChange={(e) => setNote(e.target.value)}
      />
      <div className="ai-mem__editfoot">
        <button type="submit" className="btn btn--sm btn--primary" disabled={mark.isPending || !note.trim()}>
          {mark.isPending ? 'Saving…' : 'Mark handled'}
        </button>
        <button type="button" className="btn btn--sm" disabled={mark.isPending} onClick={() => setOpen(false)}>
          Cancel
        </button>
        {mark.isError && <span className="small-note warn">{mark.error instanceof Error ? mark.error.message : 'Not saved.'}</span>}
      </div>
    </form>
  )
}

/** The code's overfitting checks, strategy by strategy, beside what the researcher made of each. */
function ResearchChecks({ checks }: { checks: ResearchCheck[] }) {
  return (
    <div className="tablewrap ai-report-numbers">
      <table className="table">
        <thead>
          <tr>
            <th>Strategy</th>
            <th>Check</th>
            <th>Code</th>
            <th>Researcher</th>
            <th>What the code measured</th>
          </tr>
        </thead>
        <tbody>
          {checks.map((c) => {
            const code = readingBadge(c.reading)
            const said = c.verdict ? readingBadge(c.verdict) : null
            return (
              <tr key={`${c.strategy}|${c.check}`}>
                <td>{c.strategy}</td>
                <td className="mono">{c.check}</td>
                <td>
                  <Badge tone={code.tone}>{code.label}</Badge>
                </td>
                <td>
                  {said ? <Badge tone={said.tone}>{said.label}</Badge> : <span className="faint">—</span>}
                  {c.note && <span className="cell-sub">{c.note}</span>}
                </td>
                <td>{c.facts}</td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
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
