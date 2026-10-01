/**
 * AI → Agents (/ai/agents): every agent the desk has or plans.
 *
 * Each agent says what it does and why, when it runs, which tier and chain
 * of models it uses (in fallback order), what it may read and the read-only
 * tools it may call to read it, what it may never do, when it last ran and what that call came to, and today's calls,
 * tokens and errors. A built agent can be switched off (and on) with a
 * reason, and given a chain of its own or put back on its tier's. A planned
 * agent is code still to write: it is shown dimmer with its phase, and has
 * no switch, because there is nothing to switch on.
 *
 * Under them, the AI Trader's record (its mode, its limits, today's looks
 * and every decision with the brief it read), the assistant's check and
 * exam, and the parts of the desk that look like agents but are rules in
 * code (Sentinel, the pager, the supervisor), so the list is the whole story.
 */

import { useEffect, useState } from 'react'
import { Link, useLocation, useSearchParams } from 'react-router-dom'
import {
  AGENT_STATUSES,
  CHECK_PASS_MARK,
  SCHEDULED_AGENTS,
  agentStatus,
  callTime,
  checkScoreText,
  examScoreText,
  readAssistantCheck,
  readAssistantExam,
  shortDate,
  useLatestAgentReport,
  formatTokens,
  modelName,
  toolLabel,
  useAiAgents,
  useAiModels,
  useAiOverview,
  useRunAgent,
  useUpdateAgent,
} from '../../lib/ai'
import type { AgentStatus, AiAgent, AiAgentTool, AiModel } from '../../lib/ai'
import { AI_TRADER_KEY, AI_TRADER_POLL_MS, dayCountsText, limitsParts, modeLabel, netTone } from '../../lib/aiTrader'
import { istDay } from '../../lib/desk'
import { formatDateTime, formatInrSigned } from '../../lib/format'
import { useAiTraderStatus } from '../../lib/queries'
import { shortDay } from '../../lib/replay'
import { DateField } from '../../components/DateField'
import { Badge, EmptyState, InlineError, Loading, Panel } from '../../components/ui'
import { CallLink, ChainChips, ChainEditor, OutcomeBadge, StatusPill } from './parts'
import { AiTraderDecisionList, AiTraderShadowBook } from './AiTraderParts'
import { errorText, useNow } from './common'
import '../system/health/health.css'
import './ai.css'

type Filter = AgentStatus | 'all'

function filterFrom(raw: string | null): Filter {
  return raw === 'on' || raw === 'off' || raw === 'planned' ? raw : 'all'
}

/** The on/off switch, which asks for a reason before it acts. */
function AgentSwitch({ agent }: { agent: AiAgent }) {
  const update = useUpdateAgent()
  const [asking, setAsking] = useState(false)
  const [reason, setReason] = useState('')
  const on = agent.enabled
  const act = () =>
    update.mutate(
      { key: agent.key, enabled: !on, reason: reason.trim() },
      {
        onSuccess: () => {
          setAsking(false)
          setReason('')
        },
      },
    )

  return (
    <div className="ai-switch">
      <button
        type="button"
        className="oc-toggle"
        aria-pressed={on}
        aria-expanded={asking}
        disabled={update.isPending}
        title={on ? 'Switch this agent off' : 'Switch this agent on'}
        onClick={() => setAsking((v) => !v)}
      >
        <span className="oc-toggle__box" aria-hidden="true" />
        {on ? 'On' : 'Off'}
      </button>
      {asking && (
        <div className="ai-switch__ask">
          <span className="ai-switch__q">
            Switch the {agent.name} <b>{on ? 'off' : 'on'}</b>?{' '}
            {on && <span className="muted">Questions to it are refused until it is switched back on.</span>}
          </span>
          <input
            className="field__input field__input--sm ai-switch__reason"
            placeholder="Why (optional, kept with the change)"
            aria-label="Reason"
            value={reason}
            maxLength={200}
            onChange={(e) => setReason(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') act()
            }}
          />
          <span className="ai-switch__tools">
            <button type="button" className={`btn btn--sm ${on ? 'btn--danger' : 'btn--primary'}`} disabled={update.isPending} onClick={act}>
              {update.isPending ? 'Saving…' : on ? 'Switch off' : 'Switch on'}
            </button>
            <button type="button" className="btn btn--ghost btn--sm" disabled={update.isPending} onClick={() => setAsking(false)}>
              Cancel
            </button>
          </span>
          {update.isError && (
            <div className="alert alert--error ai-switch__error" role="alert">
              {errorText(update.error)}
            </div>
          )}
        </div>
      )}
    </div>
  )
}

function AgentChain({ agent, catalog, catalogNote, tierChain }: { agent: AiAgent; catalog: AiModel[] | undefined; catalogNote: string | null; tierChain: string[] | null }) {
  const update = useUpdateAgent()
  const [editing, setEditing] = useState(false)
  const done = { onSuccess: () => setEditing(false) }
  return (
    <>
      <div className="ai-agent__chain">
        <ChainChips chain={agent.chain} />
        <span className="faint ai-agent__chainwhose">
          {agent.chainOverridden ? <Badge tone="warn">its own chain</Badge> : `the ${agent.tierLabel} tier's chain`}
        </span>
        {agent.built && !editing && (
          <button type="button" className="btn btn--ghost btn--sm" onClick={() => setEditing(true)}>
            Edit chain
          </button>
        )}
      </div>
      {editing && (
        <ChainEditor
          chain={agent.chain}
          resetTo={tierChain ?? agent.chain}
          resetLabel={`Use the ${agent.tierLabel} tier's chain`}
          catalog={catalog}
          catalogNote={catalogNote}
          saving={update.isPending}
          error={update.isError ? errorText(update.error) : null}
          onSave={(chain, reason) => update.mutate({ key: agent.key, chain, reason }, done)}
          onReset={(reason) => update.mutate({ key: agent.key, resetChain: true, reason }, done)}
          onCancel={() => {
            update.reset()
            setEditing(false)
          }}
        />
      )}
    </>
  )
}

/** Open where there is room for it; on a phone ten tools would be a screen of their own. */
function wideScreen(): boolean {
  try {
    return window.matchMedia('(min-width: 761px)').matches
  } catch {
    return true
  }
}

/** The read-only tools an agent may call, folded behind their count, each with what it returns. */
function AgentTools({ tools }: { tools: AiAgentTool[] }) {
  const [open, setOpen] = useState(wideScreen)
  return (
    <details className="ai-agent-tools" open={open} onToggle={(e) => setOpen(e.currentTarget.open)}>
      <summary className="ai-agent-tools__summary">
        {tools.length} tool{tools.length === 1 ? '' : 's'}, read-only
        {!open && <span className="faint"> · {tools.map((t) => toolLabel(t.name, '').label).join(', ')}</span>}
      </summary>
      <dl className="ai-agent-tools__list">
        {tools.map((t) => (
          <div key={t.name}>
            <dt className="mono">{t.name}</dt>
            <dd>{t.description}</dd>
          </div>
        ))}
      </dl>
    </details>
  )
}

/**
 * Starts a scheduled agent now, on one run or incident when an id is given,
 * or on its next due work. The API only accepts the job (202); the report
 * arrives on the Reports tab when the model answers.
 */
function RunNow({ agentKey }: { agentKey: string }) {
  const run = useRunAgent()
  const [subject, setSubject] = useState('')
  const kind = SCHEDULED_AGENTS[agentKey]?.subject ?? null
  const bad = subject.trim() !== '' && !/^\d+$/.test(subject.trim())
  const go = () => {
    if (bad) return
    run.mutate({ key: agentKey, subjectId: kind ? subject : null })
  }
  return (
    <div className="ai-runnow">
      <div className="ai-runnow__form">
        {kind && (
          <input
            className="field__input field__input--sm ai-runnow__id"
            inputMode="numeric"
            placeholder={kind === 'run' ? 'Run id (optional)' : 'Incident id (optional)'}
            aria-label={kind === 'run' ? 'Run id' : 'Incident id'}
            aria-invalid={bad || undefined}
            value={subject}
            onChange={(e) => setSubject(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') go()
            }}
          />
        )}
        <button type="button" className="btn btn--sm" disabled={run.isPending || bad} onClick={go}>
          {run.isPending ? 'Starting…' : 'Run now'}
        </button>
        <span className="faint ai-runnow__hint">
          {kind === 'run'
            ? 'Blank reviews the runs that are due.'
            : kind === 'incident'
              ? 'Blank explains the incidents that are due.'
              : agentKey === 'assistant-check'
                ? 'Asks the questions now; it takes a few minutes.'
                : agentKey === 'assistant-exam'
                  ? 'Starts the exam, or carries on the open one; two questions a minute, a few hours in all.'
                  : agentKey === AI_TRADER_KEY
                    ? 'One look now, outside its schedule; the rules judge it as at this hour.'
                    : 'Reads its next batch of unread items.'}
        </span>
      </div>
      {bad && <p className="small-note warn ai-flush">An id is a whole number.</p>}
      {run.isSuccess && agentKey === AI_TRADER_KEY && (
        <p className="small-note ai-flush ai-runnow__said">
          Started. The decision appears in the list below when the model answers (usually within a minute or two).
        </p>
      )}
      {run.isSuccess && agentKey !== AI_TRADER_KEY && (
        <p className="small-note ai-flush ai-runnow__said">
          Started{run.data?.subjectId ? ` on ${kind === 'incident' ? 'incident' : 'run'} #${run.data.subjectId}` : ''}. The report appears on
          the <Link to={`/ai/reports?agent=${encodeURIComponent(agentKey)}`}>Reports tab</Link>{' '}
          {agentKey === 'assistant-exam' ? 'when the last question is answered (a few hours).' : 'when the model answers (usually 1–3 minutes).'}
        </p>
      )}
      {run.isError && (
        <div className="alert alert--error ai-flush" role="alert">
          {errorText(run.error)}
        </div>
      )}
    </div>
  )
}

/**
 * The daily assistant check: not an agent but a test of one. After 16:40 IST
 * on weekdays the desk asks the Desk Assistant questions the code knows the
 * answers to, grades the answers by code, and writes one report a day.
 */
function AssistantCheckPanel({ now }: { now: number }) {
  const latest = useLatestAgentReport('assistant-check')
  const d = latest.data
  const c = d ? readAssistantCheck(d.data) : null
  return (
    <Panel title={<>Assistant check</>} className="ai-utility">
      <p className="ai-utility__what">
        A test of the Desk Assistant, graded by code: up to 12 questions whose answers the desk knows (today's runs, the worst
        run and its net, open legs, live incidents, the checkup's verdict, NIFTY's PCR and max pain, one sum). It passes a day at{' '}
        {Math.round(CHECK_PASS_MARK * 100)}% right.
      </p>
      <dl className="ai-facts">
        <div>
          <dt>Runs</dt>
          <dd>Weekdays after 16:40 IST; its calls are on the Calls tab as the Desk Assistant's, from the check.</dd>
        </div>
        <div>
          <dt>Last result</dt>
          <dd>
            {d ? (
              <>
                <Link to={`/ai/reports?id=${d.report.id}`}>{d.report.subjectType === 'check' ? shortDate(d.report.subjectId) : `report #${d.report.id}`}</Link>{' '}
                <span className={d.report.status === 'ok' ? 'pos' : 'warn'}>{c ? checkScoreText(c) : d.report.title}</span>
                <span className="faint"> · {callTime(d.report.createdUtc, now)}</span>{' '}
                <Link to="/ai/reports?agent=assistant-check">every check</Link>
              </>
            ) : latest.isPending ? (
              <span className="faint">not read yet</span>
            ) : latest.isError ? (
              <span className="faint">could not be read</span>
            ) : (
              <span className="faint">no check has run yet</span>
            )}
          </dd>
        </div>
        <div>
          <dt>By hand</dt>
          <dd>
            <RunNow agentKey="assistant-check" />
          </dd>
        </div>
      </dl>
    </Panel>
  )
}

/**
 * The weekly assistant exam: a fixed bank of questions about finished days,
 * each asked three times, held-out days scored apart, so the score compares
 * week to week.
 */
function AssistantExamPanel({ now }: { now: number }) {
  const latest = useLatestAgentReport('assistant-exam')
  const d = latest.data
  const e = d ? readAssistantExam(d.data) : null
  return (
    <Panel title={<>Assistant exam</>} className="ai-utility">
      <p className="ai-utility__what">
        The Desk Assistant's score that compares week to week: up to 150 questions about finished trading days (runs, nets,
        charges, the worst and best run, each account) and a little arithmetic, answers read by code and frozen. Each is asked
        three times; pass^3 counts a question only when all three were right. One day in four is held out and never learnt
        from.
      </p>
      <dl className="ai-facts">
        <div>
          <dt>Runs</dt>
          <dd>Sundays from 10:30 IST, two questions a minute, never while NSE trades; its calls are on the Calls tab, from the exam.</dd>
        </div>
        <div>
          <dt>Last result</dt>
          <dd>
            {d ? (
              <>
                <Link to={`/ai/reports?id=${d.report.id}`}>{shortDate(d.report.sessionDate ?? '') || `report #${d.report.id}`}</Link>{' '}
                <span className={d.report.status === 'ok' ? 'pos' : 'warn'}>{e ? examScoreText(e) : d.report.title}</span>
                <span className="faint"> · {callTime(d.report.createdUtc, now)}</span>{' '}
                <Link to="/ai/reports?agent=assistant-exam">every exam</Link>
              </>
            ) : latest.isPending ? (
              <span className="faint">not read yet</span>
            ) : latest.isError ? (
              <span className="faint">could not be read</span>
            ) : (
              <span className="faint">no exam has finished yet</span>
            )}
          </dd>
        </div>
        <div>
          <dt>By hand</dt>
          <dd>
            <RunNow agentKey="assistant-exam" />
          </dd>
        </div>
      </dl>
    </Panel>
  )
}

/**
 * The AI Trader (owner, 1 Oct): every ten minutes of the session it reads a
 * market brief built by code, the model proposes one action, and code judges
 * it against the owner's limits. Its switch and "Run now" are its card's,
 * like every scheduled agent's; this is its record: the mode, the limits,
 * today's looks, and every decision with the brief it read.
 */
function AiTraderPanel({ now }: { now: number }) {
  const status = useAiTraderStatus()
  const today = istDay(now)
  const [picked, setPicked] = useState<string | null>(null)
  const day = picked ?? today
  const isToday = day === today
  const s = status.data
  const mode = modeLabel(s?.mode, s?.rules?.capital)
  const limits = s?.rules ? limitsParts(s.rules) : []
  const every = s?.everyMinutes ?? 10

  return (
    <div id="ai-trader" className="atr-anchor">
      <Panel
        className="ai-utility atr-panel"
        title={<>AI Trader</>}
        actions={
          s ? (
            <>
              <StatusPill status={s.status ?? 'not known'} />
              <Badge tone={mode.tone}>{mode.label}</Badge>
            </>
          ) : undefined
        }
      >
        <p className="ai-utility__what">
          Trades its own paper account in index options. Every {every} minutes of the session, code builds a market brief, the
          model proposes one action as JSON, and code judges it against the limits below; the model holds no order tool. Every
          look is kept, "do nothing" included, with the brief it read and the rule that judged it.
        </p>
        {status.isPending ? (
          <Loading label="Reading the AI Trader…" />
        ) : !s ? (
          <InlineError error={status.error} />
        ) : (
          <dl className="ai-facts">
            <div>
              <dt>Switch</dt>
              <dd>
                {s.status === 'on' ? 'On: it looks on its schedule.' : s.status === 'off' ? 'Off: it does not look.' : 'Not known.'} Its switch
                is on <a href={`#agent-${AI_TRADER_KEY}`}>its card</a>.
              </dd>
            </div>
            <div>
              <dt>Mode</dt>
              <dd>
                <b>{mode.label}</b>
                {mode.means ? `: ${mode.means}.` : ''}
              </dd>
            </div>
            <div>
              <dt>Limits</dt>
              <dd className="atr-limits">
                {limits.length > 0 ? `${limits.join(' · ')}.` : <span className="faint">not sent by the API</span>}
              </dd>
            </div>
            <div>
              <dt>Today</dt>
              <dd>
                {s.today ? dayCountsText(s.today) : <span className="faint">not known</span>}
                {s.shadow && s.shadow.positions > 0 && (
                  <>
                    {' · shadow net '}
                    <b className={netTone(s.shadow.net)} title="Today's shadow book, after charges; open positions as if sold at their marks">
                      {formatInrSigned(s.shadow.net)}
                    </b>
                    {s.shadow.open > 0 ? ` (${s.shadow.open} open)` : ''}
                  </>
                )}
              </dd>
            </div>
            <div>
              <dt>By hand</dt>
              <dd>
                <RunNow agentKey={AI_TRADER_KEY} />
              </dd>
            </div>
          </dl>
        )}
        {status.isError && s && <p className="small-note warn ai-flush">The last read failed: showing what was read before.</p>}

        <div className="atr-tools">
          <h3 className="atr-tools__h">Day</h3>
          <DateField
            className="field__input field__input--sm field__input--date"
            aria-label="Day (IST)"
            title="The IST day of the decisions"
            max={today}
            value={day}
            onChange={(iso) => setPicked(iso && iso !== today ? iso : null)}
          />
          {!isToday && (
            <button type="button" className="btn btn--ghost btn--sm" onClick={() => setPicked(null)}>
              Today
            </button>
          )}
          <span className="faint atr-tools__note">its shadow book and every look it took that day</span>
        </div>
        <AiTraderShadowBook
          key={`book-${day}`}
          filter={{ day }}
          pollMs={isToday ? AI_TRADER_POLL_MS : false}
          empty={isToday ? 'No shadow position yet today.' : `No shadow position on ${shortDay(day)}.`}
        />
        <h4 className="atr-dec-h">
          Decisions <span className="faint">newest first · open one for the brief it read</span>
        </h4>
        <AiTraderDecisionList
          key={day}
          filter={{ day }}
          pollMs={isToday ? AI_TRADER_POLL_MS : false}
          label={`AI Trader decisions on ${shortDay(day)}`}
          empty={
            !isToday
              ? `No decision on ${shortDay(day)}.`
              : s?.status === 'on'
                ? `No look yet today. It looks every ${every} minutes, 09:20–15:00 IST, on trading days.`
                : 'No look today. It is switched off: switch it on on its card, or press Run now for one look.'
          }
        />
      </Panel>
    </div>
  )
}

function LastCall({ agent, now }: { agent: AiAgent; now: number }) {
  const c = agent.lastCall
  if (!c) return <span className="faint">{agent.built ? 'never called' : 'never: not built yet'}</span>
  return (
    <span className="ai-agent__last">
      <span title={`${formatDateTime(c.utc)} IST`}>{callTime(c.utc, now)}</span> <OutcomeBadge outcome={c.outcome} />{' '}
      {c.model ? (
        <span title={c.model}>{modelName(c.model)}</span>
      ) : (
        <span className="faint">{c.outcome === 'running' ? 'no model has answered yet' : 'no model answered'}</span>
      )}{' '}
      · <CallLink id={c.id} />
    </span>
  )
}

function AgentCard({
  agent,
  now,
  catalog,
  catalogNote,
  tierChain,
  highlighted,
}: {
  agent: AiAgent
  now: number
  catalog: AiModel[] | undefined
  catalogNote: string | null
  tierChain: string[] | null
  highlighted: boolean
}) {
  const planned = agent.status === 'planned'
  const t = agent.today
  return (
    <article id={`agent-${agent.key}`} className={`ai-agent ai-agent--${agent.status} ${highlighted ? 'ai-agent--target' : ''}`}>
      <header className="ai-agent__head">
        <span className="ai-agent__num">#{agent.number}</span>
        <h3 className="ai-agent__name">{agent.name}</h3>
        <StatusPill status={agent.status} phase={agent.phase} />
        <span className="ai-agent__tag faint">
          {!planned && `phase ${agent.phase} · `}
          {agent.tierLabel} tier
        </span>
        {agent.built && <AgentSwitch agent={agent} />}
      </header>
      <p className="ai-agent__job">{agent.job}</p>
      {agent.useCase && <p className="ai-agent__use muted">{agent.useCase}</p>}
      <dl className="ai-facts">
        <div>
          <dt>Runs</dt>
          <dd>
            {agent.schedule}
            {agent.nextRunUtc && <span className="faint"> · next {callTime(agent.nextRunUtc, now)}</span>}
          </dd>
        </div>
        <div>
          <dt>Models</dt>
          <dd>
            <AgentChain agent={agent} catalog={catalog} catalogNote={catalogNote} tierChain={tierChain} />
          </dd>
        </div>
        <div>
          <dt>Reads</dt>
          <dd>{agent.reads}</dd>
        </div>
        {agent.built && SCHEDULED_AGENTS[agent.key] && (
          <>
            <div>
              <dt>Writes</dt>
              <dd>
                {SCHEDULED_AGENTS[agent.key].decisions ? (
                  <>
                    Decisions: {SCHEDULED_AGENTS[agent.key].writes}. <a href="#ai-trader">Its decisions</a>
                  </>
                ) : (
                  <>
                    Reports: {SCHEDULED_AGENTS[agent.key].writes}.{' '}
                    <Link to={`/ai/reports?agent=${encodeURIComponent(agent.key)}`}>Its reports</Link>
                  </>
                )}
              </dd>
            </div>
            <div>
              <dt>By hand</dt>
              <dd>
                <RunNow agentKey={agent.key} />
              </dd>
            </div>
          </>
        )}
        {agent.tools.length > 0 && (
          <div>
            <dt>Tools</dt>
            <dd>
              <AgentTools tools={agent.tools} />
            </dd>
          </div>
        )}
        <div>
          <dt>Never</dt>
          <dd>{agent.limits}</dd>
        </div>
        {!planned && (
          <>
            <div>
              <dt>Last call</dt>
              <dd>
                <LastCall agent={agent} now={now} />
              </dd>
            </div>
            <div>
              <dt>Today</dt>
              <dd>
                {t ? (
                  <>
                    {t.calls} call{t.calls === 1 ? '' : 's'} · {formatTokens(t.totalTokens)} tokens ·{' '}
                    <span className={t.failed > 0 ? 'neg' : ''}>
                      {t.failed} error{t.failed === 1 ? '' : 's'}
                    </span>
                    {t.calls > 0 && (
                      <>
                        {' · '}
                        <Link to={`/ai/calls?agent=${encodeURIComponent(agent.key)}`}>its calls</Link>
                      </>
                    )}
                  </>
                ) : (
                  <span className="faint">not known yet</span>
                )}
              </dd>
            </div>
          </>
        )}
        {agent.updatedUtc && (
          <div>
            <dt>Changed</dt>
            <dd>
              {formatDateTime(agent.updatedUtc)} IST{agent.updatedBy ? ` by ${agent.updatedBy}` : ''}
              {agent.reason ? <span className="muted">: {agent.reason}</span> : ''}
            </dd>
          </div>
        )}
      </dl>
    </article>
  )
}

export function AiAgentsPage() {
  const agents = useAiAgents()
  const models = useAiModels()
  const overview = useAiOverview()
  const [params, setParams] = useSearchParams()
  const { hash } = useLocation()
  const filter = filterFrom(params.get('status'))
  const now = useNow(30_000)
  const data = agents.data

  // A link names an agent (#agent-key from the Overview, ?agent=key from Today): bring it into view once
  // the list is in. The AI Trader's link means its record, the panel under the cards.
  const agentParam = params.get('agent')
  const toTrader = hash === '#ai-trader' || agentParam === AI_TRADER_KEY
  const target = hash.startsWith('#agent-') ? hash.slice('#agent-'.length) : !toTrader && agentParam ? agentParam : null
  const scrollTo = toTrader ? 'ai-trader' : target ? `agent-${target}` : null
  useEffect(() => {
    if (!scrollTo || !data) return
    document.getElementById(scrollTo)?.scrollIntoView({ block: 'start' })
  }, [scrollTo, data])

  const counts: Record<Filter, number> = { all: data?.agents.length ?? 0, on: 0, off: 0, planned: 0 }
  for (const a of data?.agents ?? []) if (a.status in counts) counts[a.status]++
  const shown = (data?.agents ?? []).filter((a) => filter === 'all' || a.status === filter).sort((a, b) => a.number - b.number)

  const catalogNote = models.isError
    ? `The catalog could not be read (${errorText(models.error)}), so only the models already in chains can be kept.`
    : models.data?.source === 'unavailable'
      ? `The provider's list is unavailable${models.data.error ? ` (${models.data.error})` : ''}: only the models the tiers name can be picked.`
      : null
  // What "use the tier's chain" goes back to, from the tiers themselves.
  const tierChains = new Map((overview.data?.tiers ?? []).map((t) => [t.key, t.chain]))

  const pick = (next: Filter) => {
    const p = new URLSearchParams(params)
    if (next === 'all') p.delete('status')
    else p.set('status', next)
    setParams(p, { replace: true })
  }

  return (
    <div className="page hp ai">
      <div className="hp-bar">
        <p className="hp-bar__lead muted">
          Every agent the desk has or plans: what it does, what it may read, what it may never do, and when it last ran.
          Only a built agent can be switched; a planned one is code still to write.
        </p>
      </div>

      <div className="ai-tools">
        <div className="seg" role="radiogroup" aria-label="Show">
          {(['all', ...AGENT_STATUSES] as Filter[]).map((f) => (
            <button key={f} type="button" role="radio" aria-checked={filter === f} className={`seg__btn ${filter === f ? 'is-active' : ''}`} onClick={() => pick(f)}>
              {f === 'all' ? 'All' : agentStatus(f).label} <span className="faint">{data ? counts[f] : '…'}</span>
            </button>
          ))}
        </div>
        {agents.isError && data && <span className="small warn">Refresh failed: showing the last list.</span>}
      </div>

      {agents.isPending ? (
        <Loading label="Reading the agents…" />
      ) : !data ? (
        <InlineError error={agents.error} />
      ) : shown.length === 0 ? (
        <EmptyState>{filter === 'all' ? 'The API listed no agents.' : `No agent is ${agentStatus(filter).label.toLowerCase()}.`}</EmptyState>
      ) : (
        <div className="ai-agents">
          {shown.map((a) => (
            <AgentCard
              key={a.key}
              agent={a}
              now={now}
              catalog={models.data?.models}
              catalogNote={catalogNote}
              tierChain={tierChains.get(a.tier) ?? null}
              highlighted={target === a.key}
            />
          ))}
        </div>
      )}

      {data && filter === 'all' && <AiTraderPanel now={now} />}
      {data && filter === 'all' && <AssistantCheckPanel now={now} />}
      {data && filter === 'all' && <AssistantExamPanel now={now} />}

      {data && (
        <Panel title={<>Also on the desk, not AI</>} className="ai-rules">
          {data.ruleBased.length === 0 ? (
            <EmptyState>None listed.</EmptyState>
          ) : (
            <div className="ai-rules__list">
              {data.ruleBased.map((r) => (
                <div key={r.name} className="ai-rule">
                  <span className="ai-rule__name">{r.name}</span>
                  <span className="ai-rule__what">{r.what}</span>
                  <span className="ai-rule__where mono faint">{r.where}</span>
                  <span className="ai-rule__model muted">{r.model}</span>
                </div>
              ))}
            </div>
          )}
          <p className="small-note ai-flush">
            These watch, page and forecast with rules and statistics written in code. None of them calls a language model, and
            none is switched from here.
          </p>
        </Panel>
      )}
    </div>
  )
}
