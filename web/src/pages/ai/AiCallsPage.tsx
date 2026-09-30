/**
 * AI → Calls (/ai/calls): every call the desk made to a model, newest first.
 *
 * A row is one question: when (IST), which agent, who asked, the model that
 * answered, the outcome, how many models failed before it, the seconds and
 * the tokens, and the start of the question. Refusals are calls too (switched
 * off, rate limit, no key), so a question that never reached a model is still
 * on the record. The list re-reads every 10 s while the tab is open, so a
 * running call turns into its outcome in place; older calls come a page at a
 * time.
 *
 * ?id= opens a call in full above the list, one URL per call so the
 * assistant, the agents and the overview can link to it: the request as
 * sent (endpoint, limits, the system prompt, every message), each model tried
 * with its outcome and HTTP status (this is where a fallback shows), the
 * model's reasoning, the answer, the tokens and the finish reason.
 */

import { useMemo } from 'react'
import type { MouseEvent } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import {
  CALL_OUTCOMES,
  attemptLabel,
  callTime,
  formatSeconds,
  formatTokens,
  modelName,
  outcomeBadge,
  useAiAgents,
  useAiCall,
  useAiCalls,
  useAiModels,
} from '../../lib/ai'
import type { AiCallDetail, AiCallFilters, AiCallSummary } from '../../lib/ai'
import { formatAge, formatDateTime } from '../../lib/format'
import { EmptyState, InlineError, Loading, Panel } from '../../components/ui'
import { AnswerText } from './AnswerText'
import { ChainChips, ModelLabel, OutcomeBadge } from './parts'
import { useNow } from './common'
import '../system/health/health.css'
import './ai.css'

const PAGE = '/ai/calls'
const TAKE = 50

function idParam(raw: string | null): number | null {
  if (!raw || !/^\d+$/.test(raw)) return null
  const n = Number(raw)
  return Number.isSafeInteger(n) && n > 0 ? n : null
}

function Tokens({ call }: { call: Pick<AiCallSummary, 'promptTokens' | 'completionTokens' | 'totalTokens'> }) {
  if (call.totalTokens == null) return <span className="faint">—</span>
  return (
    <span title={`${formatTokens(call.promptTokens)} in · ${formatTokens(call.completionTokens)} out`}>
      {formatTokens(call.totalTokens)}
      <span className="ai-only-s faint"> tokens</span>
    </span>
  )
}

/** A block of text as it was sent or received: kept as written, wrapped, and scrollable when long. */
function Sent({ text }: { text: string }) {
  return <pre className="ai-pre">{text}</pre>
}

function CallDetail({ call, now }: { call: AiCallDetail; now: number }) {
  const running = call.outcome === 'running'
  const req = call.request
  return (
    <div className="ai-call">
      <div className="ai-call__head">
        <h3 className="ai-call__title">Call #{call.id}</h3>
        <OutcomeBadge outcome={call.outcome} />
        <span className="ai-call__who muted">
          {call.agentName || call.agentKey} · asked by {call.requestedBy || 'unknown'}
          {call.source ? ` from the ${call.source}` : ''}
        </span>
        <Link className="btn btn--ghost btn--sm ai-call__close" to={PAGE}>
          Close
        </Link>
      </div>
      <p className="ai-call__when faint">
        {formatDateTime(call.utc)} IST ({formatAge(call.utc)})
        {call.completedUtc ? ` · finished ${callTime(call.completedUtc, now)}` : running ? ' · still running' : ''}
      </p>

      {call.error && (
        <div className="alert alert--error ai-flush" role="alert">
          <span className="ai-call__error">{call.error}</span>
        </div>
      )}

      <div className="kv-grid ai-call__kv">
        <div>
          <span className="muted">Answered by</span>
          <span>{call.model ? <ModelLabel id={call.model} bare /> : <span className="faint">{running ? 'not yet' : 'no model'}</span>}</span>
        </div>
        <div>
          <span className="muted">Tier</span>
          <span>{call.tier ? call.tier[0].toUpperCase() + call.tier.slice(1) : <span className="faint">{call.agentKey === 'model-test' ? 'one model' : "the agent's own chain"}</span>}</span>
        </div>
        <div>
          <span className="muted">Took</span>
          <span>{running ? <span className="live">running</span> : call.outcome === 'refused' ? <span className="faint">not asked</span> : formatSeconds(call.seconds)}</span>
        </div>
        <div>
          <span className="muted">Tokens in / out</span>
          <span>
            {formatTokens(call.promptTokens)} / {formatTokens(call.completionTokens)}
          </span>
        </div>
        <div>
          <span className="muted">Tokens in all</span>
          <span>{formatTokens(call.totalTokens)}</span>
        </div>
        <div>
          <span className="muted">Finish reason</span>
          <span className={call.finishReason === 'length' ? 'warn' : ''}>{call.finishReason || <span className="faint">—</span>}</span>
        </div>
        <div>
          <span className="muted">Fallbacks</span>
          <span className={call.fallbacks > 0 ? 'warn' : ''}>{call.fallbacks}</span>
        </div>
        <div>
          <span className="muted">Conversation</span>
          <span className="mono ai-call__conv">{call.conversationId || <span className="faint">—</span>}</span>
        </div>
      </div>

      <section className="ai-call__sec">
        <h4 className="ai-call__h">Request</h4>
        {req ? (
          <p className="ai-call__req">
            <span className="mono">{req.endpoint}</span>
            <span className="muted">
              {' '}
              · max tokens {req.maxTokens ?? '—'} · temperature {req.temperature ?? '—'} · {req.stream ? 'streamed' : 'not streamed'}
            </span>
          </p>
        ) : (
          <p className="faint ai-flush">Not recorded: the call was refused before a request was made.</p>
        )}
        {call.chain.length > 0 && (
          <div className="ai-call__chain">
            <span className="muted">Chain</span> <ChainChips chain={call.chain} />
          </div>
        )}
      </section>

      <section className="ai-call__sec">
        <h4 className="ai-call__h">Attempts {call.attempts.length > 0 && <span className="faint">{call.attempts.length}</span>}</h4>
        {call.attempts.length === 0 ? (
          <p className="faint ai-flush">{running ? 'No model has answered or failed yet.' : 'No model was asked.'}</p>
        ) : (
          <div className="tablewrap">
            <table className="table ai-attempts">
              <thead>
                <tr>
                  <th>#</th>
                  <th>Model</th>
                  <th>Outcome</th>
                  <th className="num">Seconds</th>
                  <th className="num">HTTP</th>
                </tr>
              </thead>
              <tbody>
                {call.attempts.map((a, i) => (
                  <tr key={i}>
                    <td className="faint">{i + 1}</td>
                    <td>
                      <ModelLabel id={a.model} bare />
                    </td>
                    <td className={a.outcome === 'ok' ? 'pos' : 'warn'}>{attemptLabel(a.outcome)}</td>
                    <td className="num">{formatSeconds(a.seconds)}</td>
                    <td className="num">{a.httpStatus ?? <span className="faint">—</span>}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      <details className="ai-fold">
        <summary>
          System prompt <span className="faint">{call.system ? `${formatTokens(call.system.length)} characters` : 'none'}</span>
        </summary>
        {call.system ? <Sent text={call.system} /> : <p className="faint ai-flush">No system prompt was sent.</p>}
      </details>

      <section className="ai-call__sec">
        <h4 className="ai-call__h">
          Messages <span className="faint">{call.messages.length}</span>
        </h4>
        {call.messages.length === 0 ? (
          <p className="faint ai-flush">None recorded.</p>
        ) : (
          <ol className="ai-msgs">
            {call.messages.map((m, i) => (
              <li key={i} className={`ai-msg ai-msg--${m.role}`}>
                <span className="ai-msg__role">{m.role}</span>
                <Sent text={m.content} />
              </li>
            ))}
          </ol>
        )}
      </section>

      {call.reasoning && (
        <details className="ai-fold">
          <summary>
            Reasoning <span className="faint">{formatTokens(call.reasoning.length)} characters</span>
          </summary>
          <Sent text={call.reasoning} />
        </details>
      )}

      <section className="ai-call__sec">
        <h4 className="ai-call__h">Answer</h4>
        {call.answer ? (
          <div className="ai-call__answer">
            <AnswerText text={call.answer} />
          </div>
        ) : (
          <p className="faint ai-flush">{running ? 'Not finished yet.' : 'No answer.'}</p>
        )}
      </section>
    </div>
  )
}

function CallRow({ call, selected, now, onOpen }: { call: AiCallSummary; selected: boolean; now: number; onOpen: (id: number, e: MouseEvent) => void }) {
  const running = call.outcome === 'running'
  return (
    <tr className={`${selected ? 'row--selected' : ''} ${running ? 'row--live' : ''}`} onClick={(e) => onOpen(call.id, e)}>
      <td className="ai-c-when">
        <Link to={`${PAGE}?id=${call.id}`} className="ai-c-link" title={`${formatDateTime(call.utc)} IST · call #${call.id}`}>
          {callTime(call.utc, now)}
        </Link>
      </td>
      <td className="ai-c-agent">{call.agentName || call.agentKey}</td>
      <td className="ai-c-who muted">{call.requestedBy}</td>
      <td className={`ai-c-model ${call.model ? '' : 'ai-c-none'}`}>{call.model ? <span title={call.model}>{modelName(call.model)}</span> : <span className="faint">—</span>}</td>
      <td className="ai-c-outcome">
        <OutcomeBadge outcome={call.outcome} />
      </td>
      <td className={`num ai-c-fb ${call.fallbacks > 0 ? 'warn' : 'faint'}`}>
        {call.fallbacks}
        <span className="ai-only-s"> fallback{call.fallbacks === 1 ? '' : 's'}</span>
      </td>
      <td className={`num ai-c-secs ${call.outcome === 'refused' ? 'ai-c-none' : ''}`}>
        {running ? <span className="live">running</span> : call.outcome === 'refused' ? <span className="faint">—</span> : formatSeconds(call.seconds)}
      </td>
      <td className={`num ai-c-tokens ${call.totalTokens == null ? 'ai-c-none' : ''}`}>
        <Tokens call={call} />
      </td>
      <td className="ai-c-summary">
        <span className="ai-c-summary__text">{call.summary || <span className="faint">—</span>}</span>
        {call.error && call.outcome !== 'ok' && (
          <span className={`ai-c-summary__err ${call.outcome === 'cancelled' ? 'ai-c-summary__err--quiet' : ''}`}>{call.error}</span>
        )}
      </td>
    </tr>
  )
}

export function AiCallsPage() {
  const [params, setParams] = useSearchParams()
  const navigate = useNavigate()
  const selectedId = idParam(params.get('id'))
  const filters: AiCallFilters = {
    agent: params.get('agent') ?? '',
    outcome: params.get('outcome') ?? '',
    model: params.get('model') ?? '',
    take: TAKE,
  }
  const calls = useAiCalls(filters)
  const selected = useAiCall(selectedId)
  const agents = useAiAgents()
  const models = useAiModels()
  const now = useNow(10_000)

  const rows = useMemo(() => calls.data?.pages.flatMap((p) => p.calls) ?? [], [calls.data])

  // The filters offer what exists: the built agents and the model test, the models in use, and anything the rows name.
  const agentOptions = useMemo(() => {
    const m = new Map<string, string>()
    for (const a of agents.data?.agents ?? []) if (a.built) m.set(a.key, a.name)
    m.set('model-test', 'Model test')
    for (const r of rows) if (!m.has(r.agentKey)) m.set(r.agentKey, r.agentName || r.agentKey)
    if (filters.agent && !m.has(filters.agent)) m.set(filters.agent, filters.agent)
    return [...m.entries()]
  }, [agents.data, rows, filters.agent])
  const modelOptions = useMemo(() => {
    const s = new Set<string>()
    for (const m of models.data?.models ?? []) if (m.inUse) s.add(m.id)
    for (const r of rows) if (r.model) s.add(r.model)
    if (filters.model) s.add(filters.model)
    return [...s].sort((a, b) => modelName(a).localeCompare(modelName(b)))
  }, [models.data, rows, filters.model])

  const setFilter = (key: 'agent' | 'outcome' | 'model', value: string) => {
    const p = new URLSearchParams(params)
    if (value) p.set(key, value)
    else p.delete(key)
    setParams(p, { replace: true })
  }
  const filtered = !!(filters.agent || filters.outcome || filters.model)
  const clear = () => {
    const p = new URLSearchParams()
    if (selectedId != null) p.set('id', String(selectedId))
    setParams(p, { replace: true })
  }

  const open = (id: number, e: MouseEvent) => {
    // The time cell is a link of its own; a click on it (or a modified click) is the link's.
    if ((e.target as HTMLElement).closest('a')) return
    const p = new URLSearchParams(params)
    p.set('id', String(id))
    navigate(`${PAGE}?${p.toString()}`)
    window.scrollTo({ top: 0 })
  }

  return (
    <div className="page hp ai">
      <div className="hp-bar">
        <p className="hp-bar__lead muted">
          Every call to a model: who asked, which model answered, how long it took and what it cost. Refusals are calls too. Open
          one for the prompt, each model tried, and the answer.
        </p>
        <span className="sys-asof faint">
          refreshes every 10 s{calls.isError && calls.data && <span className="warn"> · refresh failed: showing the last list</span>}
        </span>
      </div>

      {selectedId != null && (
        <Panel className="ai-call-panel">
          {selected.isPending ? (
            <Loading label={`Reading call #${selectedId}…`} />
          ) : selected.data ? (
            <CallDetail call={selected.data} now={now} />
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
        <select className="field__input field__input--sm ai-select" aria-label="Outcome" value={filters.outcome} onChange={(e) => setFilter('outcome', e.target.value)}>
          <option value="">Every outcome</option>
          {CALL_OUTCOMES.map((o) => (
            <option key={o} value={o}>
              {outcomeBadge(o).label}
            </option>
          ))}
        </select>
        <select className="field__input field__input--sm ai-select" aria-label="Model" value={filters.model} onChange={(e) => setFilter('model', e.target.value)}>
          <option value="">Every model</option>
          {modelOptions.map((m) => (
            <option key={m} value={m}>
              {modelName(m)}
            </option>
          ))}
        </select>
        {filtered && (
          <button type="button" className="btn btn--ghost btn--sm" onClick={clear}>
            Clear
          </button>
        )}
        <span className="ai-tools__count faint">{calls.data ? `${rows.length} shown${calls.hasNextPage ? ', more older' : ''}` : ''}</span>
      </div>

      {calls.isPending ? (
        <Loading label="Reading the calls…" />
      ) : !calls.data ? (
        <>
          <InlineError error={calls.error} />
          <p className="small-note ai-flush">The log comes from GET /api/Ai/calls; until it answers, no call is known.</p>
        </>
      ) : rows.length === 0 ? (
        <EmptyState>{filtered ? 'No call matches those filters.' : 'No call has been made yet. A question on the Assistant tab is the first.'}</EmptyState>
      ) : (
        <>
          <div className="tablewrap">
            <table className="table table--hover ai-calls">
              <thead>
                <tr>
                  <th>Time IST</th>
                  <th>Agent</th>
                  <th>Asked by</th>
                  <th>Model</th>
                  <th>Outcome</th>
                  <th className="num" title="Models that failed before one answered">
                    Fallbacks
                  </th>
                  <th className="num">Seconds</th>
                  <th className="num">Tokens</th>
                  <th>Question</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((c) => (
                  <CallRow key={c.id} call={c} selected={c.id === selectedId} now={now} onOpen={open} />
                ))}
              </tbody>
            </table>
          </div>
          {calls.hasNextPage && (
            <div className="ai-more">
              <button type="button" className="btn btn--sm" disabled={calls.isFetchingNextPage} onClick={() => void calls.fetchNextPage()}>
                {calls.isFetchingNextPage ? 'Reading older calls…' : 'Older'}
              </button>
            </div>
          )}
          {calls.isFetchNextPageError && <InlineError error={calls.error} />}
        </>
      )}
    </div>
  )
}
