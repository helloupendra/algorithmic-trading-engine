/**
 * AI → Memory (/ai/memory): what the agents read before every answer.
 *
 * Three kinds of memory, each a short text an agent is given with every
 * question: the owner's notes (/remember here, in the Assistant or on
 * Telegram), corrections (a 👎 on an answer with what it should have said),
 * and lessons an agent proposed from a failed daily check. Notes and
 * corrections are read at once; a lesson waits here until the owner approves
 * it, perhaps after editing it, or rejects it.
 *
 * Every memory counts how it has done: the answers it was given to, the 👍
 * and 👎 on them, and the check questions answered right or wrong while it
 * was part of the answer. One that keeps turning up in bad answers is marked
 * for review with the reason; nothing is retired on its own. Retired and
 * rejected memories stay, folded, and can be restored.
 *
 * "Is it learning?" is the days with a check or feedback: the check's score
 * beside the number of active memories that day. #memory-12 opens the page
 * on one memory, as the Assistant's "Read 3 memories" line links to it.
 */

import { useEffect, useRef, useState } from 'react'
import type { FormEvent, ReactNode } from 'react'
import { Link, useLocation, useSearchParams } from 'react-router-dom'
import {
  CHECK_PASS_MARK,
  MEMORY_MAX_CHARS,
  budgetText,
  checkScoreText,
  clockTime,
  istDaysAgo,
  memoryErrorText,
  memoryHref,
  memoryLabel,
  memoryOrigin,
  memorySourceText,
  memoryStatus,
  memoryTextProblem,
  progressSince,
  shortDate,
  splitMemories,
  useAddMemory,
  useAiMemories,
  useMemoryProgress,
  useUpdateMemory,
} from '../../lib/ai'
import type { AiMemory, MemoryProgressDay } from '../../lib/ai'
import { formatDateTime } from '../../lib/format'
import { Badge, EmptyState, InlineError, Loading, Panel, StatTile } from '../../components/ui'
import { CallLink, KindChip } from './parts'
import { errorText, useNow } from './common'
import '../system/health/health.css'
import './ai.css'

/** The Telegram bot that takes /remember. */
const BOT = '@codefortrade_bot'

/** A memory's text being edited: the box, its count, and the buttons the caller gives it. */
function TextEditor({
  value,
  onChange,
  label,
  children,
}: {
  value: string
  onChange: (text: string) => void
  label: string
  children: ReactNode
}) {
  const problem = memoryTextProblem(value)
  return (
    <div className="ai-mem__edit">
      <textarea
        className="field__input ai-memtext"
        rows={3}
        aria-label={label}
        value={value}
        maxLength={MEMORY_MAX_CHARS}
        onChange={(e) => onChange(e.target.value)}
      />
      <div className="ai-mem__editfoot">
        {children}
        <span className={`ai-mem__count ${problem && value.trim() ? 'warn' : 'faint'}`}>
          {value.trim().length} / {MEMORY_MAX_CHARS}
        </span>
      </div>
    </div>
  )
}

/** Where a memory came from, and how it has done since. */
function MemoryFacts({ m, now }: { m: AiMemory; now: number }) {
  return (
    <p className="ai-mem__facts">
      <span>{memorySourceText(m.source, m.via)}</span>
      <span title={m.createdUtc ? `${formatDateTime(m.createdUtc)} IST` : undefined}>
        added {clockTime(m.createdUtc, now)}
        {m.createdBy ? ` by ${m.createdBy}` : ''}
      </span>
      <span>
        {m.uses === 0 ? 'not used yet' : `used ${m.uses} time${m.uses === 1 ? '' : 's'}`}
        {m.lastUsedUtc ? `, last ${clockTime(m.lastUsedUtc, now)}` : ''}
      </span>
      <span title="👍 and 👎 on the answers it was part of">
        👍 {m.ups} · 👎 {m.downs}
      </span>
      <span title="Check questions answered right and wrong while it was part of the answer">
        check ✓ {m.checkPasses} · ✗ {m.checkFails}
      </span>
      {m.sourceCallId != null && (
        <span>
          from <CallLink id={m.sourceCallId} />
        </span>
      )}
    </p>
  )
}

/** A lesson an agent proposed: read by no one until it is approved. */
function ProposedRow({ m, target, showAgent }: { m: AiMemory; target: boolean; showAgent: boolean }) {
  const update = useUpdateMemory()
  const [editing, setEditing] = useState(false)
  const [draft, setDraft] = useState(m.text)
  const busy = update.isPending
  const decide = (status: 'active' | 'rejected', text?: string) =>
    update.mutate({ id: m.id, status, text: text != null && text.trim() !== m.text ? text : undefined }, { onSuccess: () => setEditing(false) })

  return (
    <li id={`memory-${m.id}`} className={`ai-mem ${target ? 'ai-mem--target' : ''}`}>
      <div className="ai-mem__head">
        <span className="ai-mem__id mono">{memoryLabel(m.id)}</span>
        <KindChip kind={m.kind} />
        {showAgent && <span className="faint">{m.agentName || m.agentKey}</span>}
      </div>
      {editing ? (
        <TextEditor value={draft} onChange={setDraft} label={`Text of ${memoryLabel(m.id)}`}>
          <button type="button" className="btn btn--primary btn--sm" disabled={busy || memoryTextProblem(draft) != null} onClick={() => decide('active', draft)}>
            {busy ? 'Saving…' : 'Approve'}
          </button>
          <button
            type="button"
            className="btn btn--ghost btn--sm"
            disabled={busy}
            onClick={() => {
              update.reset()
              setDraft(m.text)
              setEditing(false)
            }}
          >
            Cancel
          </button>
        </TextEditor>
      ) : (
        <p className="ai-mem__text">{m.text}</p>
      )}
      <p className="ai-mem__origin">
        {m.sourceReportId != null ? <Link to={`/ai/reports?id=${m.sourceReportId}`}>{memoryOrigin(m)}</Link> : memoryOrigin(m)}
        {m.context ? (
          <>
            : <span className="ai-mem__q">{m.context}</span>
          </>
        ) : null}
        {m.sourceCallId != null && (
          <>
            <span className="faint"> · </span>
            <CallLink id={m.sourceCallId} />
          </>
        )}
      </p>
      {!editing && (
        <div className="ai-mem__act ai-mem__act--row">
          <button type="button" className="btn btn--primary btn--sm" disabled={busy} onClick={() => decide('active')}>
            {busy && update.variables?.status === 'active' ? 'Approving…' : 'Approve'}
          </button>
          <button
            type="button"
            className="btn btn--sm"
            disabled={busy}
            onClick={() => {
              update.reset()
              setDraft(m.text)
              setEditing(true)
            }}
          >
            Edit and approve
          </button>
          <button type="button" className="btn btn--ghost btn--sm" disabled={busy} onClick={() => decide('rejected')}>
            {busy && update.variables?.status === 'rejected' ? 'Rejecting…' : 'Reject'}
          </button>
        </div>
      )}
      {update.isError && (
        <div className="alert alert--error ai-flush" role="alert">
          {memoryErrorText(update.error)}
        </div>
      )}
    </li>
  )
}

/** An active memory: what it says, where it came from, how it has done, and Edit and Retire. */
function ActiveRow({ m, now, target, showAgent }: { m: AiMemory; now: number; target: boolean; showAgent: boolean }) {
  const update = useUpdateMemory()
  const [editing, setEditing] = useState(false)
  const [confirm, setConfirm] = useState(false)
  const [draft, setDraft] = useState(m.text)
  const busy = update.isPending

  return (
    <li id={`memory-${m.id}`} className={`ai-mem ${target ? 'ai-mem--target' : ''}`}>
      <div className="ai-mem__head">
        <span className="ai-mem__id mono">{memoryLabel(m.id)}</span>
        <KindChip kind={m.kind} />
        {m.review && (
          <span title={m.reviewReason ?? undefined}>
            <Badge tone="warn">review</Badge>
          </span>
        )}
        {showAgent && <span className="faint">{m.agentName || m.agentKey}</span>}
        {!editing && !confirm && (
          <span className="ai-mem__act">
            <button
              type="button"
              className="btn btn--ghost btn--sm"
              onClick={() => {
                update.reset()
                setDraft(m.text)
                setEditing(true)
              }}
            >
              Edit
            </button>
            <button type="button" className="btn btn--ghost btn--sm" onClick={() => setConfirm(true)}>
              Retire
            </button>
          </span>
        )}
        {confirm && (
          <span className="ai-mem__act ai-mem__confirm">
            <span className="small">It stops being read from the next question.</span>
            <button
              type="button"
              className="btn btn--danger btn--sm"
              disabled={busy}
              onClick={() => update.mutate({ id: m.id, status: 'retired' }, { onSuccess: () => setConfirm(false) })}
            >
              {busy ? 'Retiring…' : 'Retire'}
            </button>
            <button type="button" className="btn btn--ghost btn--sm" disabled={busy} onClick={() => setConfirm(false)}>
              Cancel
            </button>
          </span>
        )}
      </div>
      {editing ? (
        <TextEditor value={draft} onChange={setDraft} label={`Text of ${memoryLabel(m.id)}`}>
          <button
            type="button"
            className="btn btn--primary btn--sm"
            disabled={busy || memoryTextProblem(draft) != null || draft.trim() === m.text}
            onClick={() => update.mutate({ id: m.id, text: draft }, { onSuccess: () => setEditing(false) })}
          >
            {busy ? 'Saving…' : 'Save'}
          </button>
          <button type="button" className="btn btn--ghost btn--sm" disabled={busy} onClick={() => setEditing(false)}>
            Cancel
          </button>
        </TextEditor>
      ) : (
        <p className="ai-mem__text">{m.text}</p>
      )}
      {m.context && (
        <p className="ai-mem__origin">
          <span className="faint">Asked: </span>
          {m.context}
        </p>
      )}
      {m.review && <p className="ai-mem__review">Review: {m.reviewReason || 'the outcomes of the answers it was part of are poor.'}</p>}
      <MemoryFacts m={m} now={now} />
      {update.isError && (
        <div className="alert alert--error ai-flush" role="alert">
          {memoryErrorText(update.error)}
        </div>
      )}
    </li>
  )
}

/** A retired or rejected memory: read by no one, restorable. */
function ClosedRow({ m, now, target, showAgent }: { m: AiMemory; now: number; target: boolean; showAgent: boolean }) {
  const update = useUpdateMemory()
  const status = memoryStatus(m.status)
  const when = m.status === 'retired' ? (m.retiredUtc ?? m.decidedUtc) : m.decidedUtc
  return (
    <li id={`memory-${m.id}`} className={`ai-mem ai-mem--closed ${target ? 'ai-mem--target' : ''}`}>
      <div className="ai-mem__head">
        <span className="ai-mem__id mono">{memoryLabel(m.id)}</span>
        <KindChip kind={m.kind} />
        <Badge tone="neutral">{status.label}</Badge>
        {showAgent && <span className="faint">{m.agentName || m.agentKey}</span>}
        <span className="ai-mem__act">
          <button type="button" className="btn btn--sm" disabled={update.isPending} onClick={() => update.mutate({ id: m.id, status: 'active' })}>
            {update.isPending ? 'Restoring…' : 'Restore'}
          </button>
        </span>
      </div>
      <p className="ai-mem__text">{m.text}</p>
      <p className="ai-mem__facts">
        <span>
          {status.label}
          {when ? ` ${clockTime(when, now)}` : ''}
          {m.decidedBy ? ` by ${m.decidedBy}` : ''}
        </span>
        <span>{memorySourceText(m.source, m.via)}</span>
        <span>
          {m.uses === 0 ? 'never used' : `used ${m.uses} time${m.uses === 1 ? '' : 's'}`} · 👍 {m.ups} · 👎 {m.downs} · check ✓ {m.checkPasses} · ✗ {m.checkFails}
        </span>
      </p>
      {update.isError && (
        <div className="alert alert--error ai-flush" role="alert">
          {memoryErrorText(update.error)}
        </div>
      )}
    </li>
  )
}

/** A note, active at once. */
function AddNote({ agent }: { agent: string | undefined }) {
  const add = useAddMemory()
  const [text, setText] = useState('')
  const [saved, setSaved] = useState<number | null>(null)
  const problem = memoryTextProblem(text)

  const save = (e: FormEvent) => {
    e.preventDefault()
    if (problem || add.isPending) return
    add.mutate(
      { agent, text },
      {
        onSuccess: (m) => {
          setSaved(m.id)
          setText('')
        },
      },
    )
  }

  return (
    <form className="ai-note" onSubmit={save}>
      <textarea
        className="field__input ai-memtext"
        rows={3}
        aria-label="New note"
        placeholder="Something the Assistant should always keep in mind, e.g. quote P&L net of charges."
        value={text}
        maxLength={MEMORY_MAX_CHARS}
        onChange={(e) => {
          setText(e.target.value)
          setSaved(null)
          if (add.isError) add.reset()
        }}
      />
      <div className="ai-note__foot">
        <button type="submit" className="btn btn--primary btn--sm" disabled={add.isPending || problem != null}>
          {add.isPending ? 'Saving…' : 'Save'}
        </button>
        {saved != null && (
          <span role="status">
            Saved as note <Link to={memoryHref(saved)}>{memoryLabel(saved)}</Link>. It is read from the next question.
          </span>
        )}
        <span className="ai-mem__count faint">
          {text.trim().length} / {MEMORY_MAX_CHARS}
        </span>
      </div>
      <p className="small-note ai-flush">
        Or send /remember … to {BOT}, or type /remember … in the <Link to="/ai/assistant">Assistant</Link>.
      </p>
      {add.isError && (
        <div className="alert alert--error ai-flush" role="alert">
          {memoryErrorText(add.error)}
        </div>
      )}
    </form>
  )
}

/** The check's score in a day's row, in the pass mark's colour; a day without a check says so. */
function DayScore({ d }: { d: MemoryProgressDay }) {
  if (d.checkTotal == null || d.checkPassed == null) return <span className="faint">no check</span>
  const tone = d.score == null ? '' : d.score >= CHECK_PASS_MARK ? 'pos' : 'warn'
  return <span className={tone}>{checkScoreText({ passed: d.checkPassed, total: d.checkTotal, score: d.score })}</span>
}

function LearningTable({ days }: { days: MemoryProgressDay[] }) {
  // Newest first, as every list on these tabs reads.
  const rows = [...days].reverse()
  return (
    <div className="tablewrap ai-progress-wrap">
      <table className="table ai-progress">
        <thead>
          <tr>
            <th>Day</th>
            <th>Check</th>
            <th className="num" title="Memories active that day">
              Memories
            </th>
            <th className="num">👍</th>
            <th className="num">👎</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((d) => (
            <tr key={d.date}>
              <td title={d.date}>{shortDate(d.date)}</td>
              <td>
                <DayScore d={d} />
              </td>
              <td className="num">{d.activeMemories ?? <span className="faint">—</span>}</td>
              <td className={`num ${d.ups > 0 ? '' : 'faint'}`}>{d.ups}</td>
              <td className={`num ${d.downs > 0 ? '' : 'faint'}`}>{d.downs}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

export function AiMemoryPage() {
  const [params, setParams] = useSearchParams()
  const { hash } = useLocation()
  const agentParam = params.get('agent') ?? ''
  const memories = useAiMemories(agentParam || undefined)
  const progress = useMemoryProgress(30)
  const now = useNow(30_000)
  const data = memories.data
  const [closedOpen, setClosedOpen] = useState(false)

  const { proposed, active, closed } = splitMemories(data?.memories ?? [])
  const agents = data?.agents ?? []
  const showAgent = agents.length > 1 && !agentParam
  // A note goes to the agent picked, or to the only agent there is; with neither, the API's default.
  const noteAgent = agentParam || (agents.length === 1 ? agents[0].key : undefined)
  const offAgents = agents.filter((a) => !a.on && (!agentParam || a.key === agentParam))

  // A link from an answer names a memory (#memory-12): open the fold it is in, then bring it into view.
  const target = /^#memory-(\d+)$/.exec(hash)?.[1] ?? null
  const targetId = target ? Number(target) : null
  const targetClosed = targetId != null && closed.some((m) => m.id === targetId)
  useEffect(() => {
    if (targetClosed) setClosedOpen(true)
  }, [targetClosed])
  // Once per link: the list re-reads every 30 s, and the reader may have scrolled on since.
  const scrolledTo = useRef<number | null>(null)
  useEffect(() => {
    if (targetId == null || !data || scrolledTo.current === targetId || (targetClosed && !closedOpen)) return
    const row = document.getElementById(`memory-${targetId}`)
    if (!row) return
    row.scrollIntoView({ block: 'start' })
    scrolledTo.current = targetId
  }, [targetId, data, targetClosed, closedOpen])

  const week = progress.data ? progressSince(progress.data.days, istDaysAgo(now, 6)) : null
  const counts = data?.counts
  const overBudget = data?.activeChars != null && data.budgetChars != null && data.budgetChars > 0 && data.activeChars > data.budgetChars

  const pickAgent = (key: string) => {
    const p = new URLSearchParams(params)
    if (key) p.set('agent', key)
    else p.delete('agent')
    setParams(p, { replace: true })
  }

  return (
    <div className="page hp ai">
      <div className="hp-bar">
        <p className="hp-bar__lead muted">
          What the agents read before every answer: your notes and corrections, and lessons you approved. Nothing an agent
          proposes is used until you approve it.
        </p>
        <span className="sys-asof faint">
          refreshes every 30 s{memories.isError && data && <span className="warn"> · refresh failed: showing the last list</span>}
        </span>
      </div>

      {data && !data.enabled && (
        <div className="alert alert--warn" role="status">
          Memory is switched off on the server, so no agent reads any of this until it is switched on.
        </div>
      )}
      {data &&
        data.enabled &&
        offAgents.map((a) => (
          <div key={a.key} className="alert alert--warn" role="status">
            <span>The {a.name} is switched off, so nothing here is read until it is switched on.</span>
            <Link className="btn btn--sm" to={`/ai/agents#agent-${a.key}`}>
              Agents →
            </Link>
          </div>
        ))}

      <div className="stat-grid stat-grid--pair ai-tiles">
        <StatTile
          label="Active"
          value={counts ? counts.active : '—'}
          tone={overBudget ? 'warn' : undefined}
          sub={data ? `${budgetText(data.activeChars, data.budgetChars)}${overBudget ? ', over the budget' : ''}` : memories.isPending ? 'reading…' : 'not known'}
        />
        <StatTile
          label="Waiting for you"
          value={counts ? counts.proposed : '—'}
          tone={counts && counts.proposed > 0 ? 'warn' : undefined}
          sub={!counts ? (memories.isPending ? 'reading…' : 'not known') : counts.proposed > 0 ? 'lessons to approve or reject' : 'nothing to decide'}
        />
        <StatTile
          label="Check, last 7 days"
          value={week?.score != null ? `${Math.round(week.score * 100)}%` : '—'}
          tone={week?.score == null ? undefined : week.score >= CHECK_PASS_MARK ? 'pos' : 'warn'}
          sub={
            progress.isPending
              ? 'reading…'
              : !week
                ? 'could not be read'
                : week.checks === 0
                  ? 'no check in 7 days'
                  : `${week.checkPassed} of ${week.checkTotal} right in ${week.checks} check${week.checks === 1 ? '' : 's'}`
          }
          to="/ai/reports?agent=assistant-check"
        />
        <StatTile
          label="Feedback, last 7 days"
          value={week ? `👍 ${week.ups} · 👎 ${week.downs}` : '—'}
          tone={week && week.downs > week.ups ? 'warn' : undefined}
          sub={progress.isPending ? 'reading…' : !week ? 'could not be read' : week.ups + week.downs === 0 ? 'no answer rated' : 'on the answers rated'}
        />
      </div>

      {(agents.length > 1 || agentParam) && (
        <div className="ai-tools">
          <select className="field__input field__input--sm ai-select" aria-label="Agent" value={agentParam} onChange={(e) => pickAgent(e.target.value)}>
            <option value="">Every agent</option>
            {agents.map((a) => (
              <option key={a.key} value={a.key}>
                {a.name}
                {a.on ? '' : ' (off)'}
              </option>
            ))}
          </select>
        </div>
      )}

      {memories.isPending ? (
        <Loading label="Reading the memories…" />
      ) : !data ? (
        <>
          <InlineError error={memories.error} />
          <p className="small-note ai-flush">The memories come from GET /api/Ai/memories; until it answers, none is known.</p>
        </>
      ) : (
        <>
          <Panel
            title={
              <>
                Waiting for approval <span className="faint ai-title-note">{proposed.length}</span>
              </>
            }
          >
            {proposed.length === 0 ? (
              <p className="small-note ai-flush">Nothing is waiting. Lessons the daily check proposes appear here, and none is read until you approve it.</p>
            ) : (
              <ul className="ai-mems">
                {proposed.map((m) => (
                  <ProposedRow key={m.id} m={m} target={m.id === targetId} showAgent={showAgent} />
                ))}
              </ul>
            )}
          </Panel>

          <Panel title={<>Add a note</>}>
            <AddNote agent={noteAgent} />
          </Panel>

          <Panel
            title={
              <>
                Active{' '}
                <span className="faint ai-title-note">
                  {active.length} · {budgetText(data.activeChars, data.budgetChars)}
                </span>
              </>
            }
          >
            {active.length === 0 ? (
              <EmptyState>No memory is active. A note above, a 👎 with a correction in the Assistant, or an approved lesson is the first.</EmptyState>
            ) : (
              <ul className="ai-mems">
                {active.map((m) => (
                  <ActiveRow key={m.id} m={m} now={now} target={m.id === targetId} showAgent={showAgent} />
                ))}
              </ul>
            )}
          </Panel>

          {closed.length > 0 && (
            <details className="ai-fold ai-mem-closed" open={closedOpen} onToggle={(e) => setClosedOpen(e.currentTarget.open)}>
              <summary>
                Retired and rejected <span className="faint">{closed.length}</span>
              </summary>
              <ul className="ai-mems">
                {closed.map((m) => (
                  <ClosedRow key={m.id} m={m} now={now} target={m.id === targetId} showAgent={showAgent} />
                ))}
              </ul>
            </details>
          )}
        </>
      )}

      <Panel title={<>Is it learning?</>}>
        {progress.isPending ? (
          <Loading label="Reading the last 30 days…" />
        ) : !progress.data ? (
          <p className="small-note warn ai-flush">The days could not be read: {errorText(progress.error)}</p>
        ) : progress.data.days.length === 0 ? (
          <p className="small-note ai-flush">No day in the last 30 had a check or feedback yet.</p>
        ) : (
          <>
            <LearningTable days={progress.data.days} />
            <p className="small-note ai-flush">
              The last 30 days that had a check or feedback, newest first. The check passes a day at{' '}
              {Math.round(CHECK_PASS_MARK * 100)}% right.
            </p>
          </>
        )}
      </Panel>
    </div>
  )
}
