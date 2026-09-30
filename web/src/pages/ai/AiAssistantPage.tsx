/**
 * AI → Assistant (/ai/assistant): the owner asks the desk's AI, and the
 * answer streams in as the model writes it.
 *
 * One conversation, kept in page state (a reload starts a new one; every
 * question and answer is in the Calls tab either way). The unsent draft and
 * the tier choice survive a reload in localStorage, where storage allows.
 *
 * The answer arrives over server-sent events on a POST (lib/ai streamAsk):
 * the model's reasoning first, shown dim and folded, then the answer. When a
 * model fails mid-answer the server asks the next one in the chain; what the
 * failed model had written is dropped and one line says who timed out and
 * who was asked next. Each finished answer names the model that gave it,
 * how long it took, the tokens it cost, and links to its call. Stop aborts
 * the request, which also cancels the call on the server.
 *
 * The Desk Assistant reads only what is typed here (Phase 1) and holds no
 * order tool; the page says so from the agent's own record.
 */

import { useEffect, useLayoutEffect, useMemo, useReducer, useRef, useState } from 'react'
import type { FormEvent, KeyboardEvent } from 'react'
import { Link } from 'react-router-dom'
import { useQueryClient } from '@tanstack/react-query'
import {
  ASK_TIERS,
  AskRefusal,
  askFailureText,
  chatReducer,
  formatSeconds,
  formatTokens,
  historyFor,
  initialChat,
  isAbort,
  modelName,
  newConversationId,
  parseAskTier,
  streamAsk,
  useAiAgents,
  useAiOverview,
} from '../../lib/ai'
import type { AskTier, ChatTurn } from '../../lib/ai'
import { IconStop } from '../../components/icons'
import { AnswerText } from './AnswerText'
import { CallLink, ChainChips } from './parts'
import { useNow } from './common'
import '../system/health/health.css'
import './ai.css'

const AGENT = 'desk-assistant'
const DRAFT_KEY = 'openfno.ai.draft'
const TIER_KEY = 'openfno.ai.tier'

/** localStorage, where the browser allows it; a private window or blocked storage just forgets. */
function readStored(key: string): string | null {
  try {
    return window.localStorage.getItem(key)
  } catch {
    return null
  }
}

function writeStored(key: string, value: string) {
  try {
    if (value) window.localStorage.setItem(key, value)
    else window.localStorage.removeItem(key)
  } catch {
    /* storage refused: the draft lives as long as the page */
  }
}

/** Copies an answer; falls back to a hidden selection where the clipboard API is not allowed (plain http). */
function CopyButton({ text }: { text: string }) {
  const [said, setSaid] = useState<string | null>(null)
  useEffect(() => {
    if (!said) return
    const id = window.setTimeout(() => setSaid(null), 1600)
    return () => window.clearTimeout(id)
  }, [said])
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(text)
      setSaid('Copied')
    } catch {
      const area = document.createElement('textarea')
      area.value = text
      area.setAttribute('readonly', '')
      area.style.position = 'fixed'
      area.style.opacity = '0'
      document.body.appendChild(area)
      area.select()
      const ok = document.execCommand('copy')
      area.remove()
      setSaid(ok ? 'Copied' : 'Copy failed')
    }
  }
  return (
    <button type="button" className="btn btn--ghost btn--sm ai-copy" onClick={() => void copy()}>
      {said ?? 'Copy'}
    </button>
  )
}

/** The model's reasoning: folded and dim; while it is still thinking, its last words show in the fold. */
function Thinking({ text, live }: { text: string; live: boolean }) {
  const words = text.trim() ? text.trim().split(/\s+/).length : 0
  const tail = text.length > 180 ? `…${text.slice(-180)}` : text
  return (
    <details className={`ai-think ${live ? 'ai-think--live' : ''}`}>
      <summary className="ai-think__summary">
        <span className="ai-think__label">{live ? 'Thinking' : 'Reasoning'}</span>
        <span className="faint"> · {formatTokens(words)} words</span>
        {live && <span className="ai-think__tail">{tail}</span>}
      </summary>
      <div className="ai-think__body">{text}</div>
    </details>
  )
}

function tierLabel(key: AskTier): string {
  return ASK_TIERS.find((t) => t.key === key)?.label ?? key
}

/** The line above an answer: who is being asked, or who answered and what it cost. */
function AnswerMeta({ turn, now }: { turn: ChatTurn; now: number }) {
  const elapsed = formatSeconds(Math.max(0, (now - turn.startedMs) / 1000))
  if (turn.status === 'streaming') {
    return (
      <span className="ai-a__meta ai-a__meta--live">
        {turn.model ? (
          <>
            Asking <b>{modelName(turn.model)}</b>
            {turn.attempt && turn.attempt.of > 1 && (
              <span className="faint">
                {' '}
                · model {turn.attempt.n} of {turn.attempt.of}
              </span>
            )}
          </>
        ) : (
          'Sending'
        )}
        <span className="faint"> · {elapsed}</span>
      </span>
    )
  }
  if (turn.status === 'done') {
    const tokens = turn.usage?.totalTokens
    return (
      <span className="ai-a__meta">
        <b title={turn.model ?? undefined}>{modelName(turn.model)}</b>
        <span className="faint">
          {' '}
          · {formatSeconds(turn.seconds)}
          {tokens != null && ` · ${formatTokens(tokens)} tokens`}
          {turn.fallbacks > 0 && ` · ${turn.fallbacks} fallback${turn.fallbacks === 1 ? '' : 's'}`} ·{' '}
        </span>
        <CallLink id={turn.callId} />
      </span>
    )
  }
  return (
    <span className="ai-a__meta">
      <b className={turn.status === 'error' ? 'neg' : ''}>{turn.status === 'error' ? 'No answer' : 'Stopped'}</b>
      {turn.callId != null && (
        <>
          <span className="faint"> · </span>
          <CallLink id={turn.callId} />
        </>
      )}
    </span>
  )
}

function Turn({ turn, now }: { turn: ChatTurn; now: number }) {
  const streaming = turn.status === 'streaming'
  return (
    <article className="ai-turn">
      <div className="ai-q">
        <div className="ai-turn__head">
          <span className="ai-who">You</span>
          <span className="faint ai-q__tier">{tierLabel(turn.tier)}</span>
        </div>
        <p className="ai-q__text">{turn.question}</p>
      </div>
      <div className={`ai-a ai-a--${turn.status}`}>
        <div className="ai-turn__head">
          <span className="ai-who">AI</span>
          <AnswerMeta turn={turn} now={now} />
          {turn.answer && !streaming && <CopyButton text={turn.answer} />}
        </div>
        {turn.notes.map((note, i) => (
          <p key={i} className="ai-a__note">
            {note}
          </p>
        ))}
        {turn.reasoning && <Thinking text={turn.reasoning} live={streaming && !turn.answer} />}
        {turn.answer ? (
          <AnswerText text={turn.answer} />
        ) : streaming && !turn.reasoning ? (
          <p className="ai-a__pending faint">No words yet. A reasoning model can think for a while before it writes.</p>
        ) : null}
        {turn.status === 'done' && turn.finishReason === 'length' && (
          <p className="small-note warn ai-flush">The answer was cut at the token limit.</p>
        )}
        {turn.status === 'stopped' && (
          <p className="small-note ai-flush">Stopped{turn.answer ? ' here' : ''}. The call is recorded as cancelled.</p>
        )}
        {turn.error && (
          <div className="alert alert--error ai-a__error" role="alert">
            <span>{turn.error}</span>
            {turn.errorStatus === 409 && (
              <Link className="btn btn--sm" to="/ai/agents#agent-desk-assistant">
                Agents →
              </Link>
            )}
          </div>
        )}
      </div>
    </article>
  )
}

export function AiAssistantPage() {
  const overview = useAiOverview()
  const agents = useAiAgents()
  const qc = useQueryClient()
  const [chat, dispatch] = useReducer(chatReducer, undefined, () => initialChat())
  const [draft, setDraft] = useState(() => readStored(DRAFT_KEY) ?? '')
  const [tier, setTier] = useState<AskTier>(() => parseAskTier(readStored(TIER_KEY)))
  const abortRef = useRef<AbortController | null>(null)
  const inputRef = useRef<HTMLTextAreaElement>(null)
  const endRef = useRef<HTMLDivElement>(null)
  const follow = useRef(true)

  const streaming = chat.turns.some((t) => t.status === 'streaming')
  const now = useNow(1_000, streaming)

  useEffect(() => writeStored(DRAFT_KEY, draft), [draft])
  useEffect(() => writeStored(TIER_KEY, tier), [tier])
  // Leaving the page stops the answer, which cancels the call on the server.
  useEffect(() => () => abortRef.current?.abort(), [])

  // Keep the newest words in view while the reader is at the bottom; leave them be once they scroll up to read.
  useEffect(() => {
    const onScroll = () => {
      follow.current = window.innerHeight + window.scrollY >= document.documentElement.scrollHeight - 140
    }
    window.addEventListener('scroll', onScroll, { passive: true })
    return () => window.removeEventListener('scroll', onScroll)
  }, [])
  useLayoutEffect(() => {
    if (follow.current && chat.turns.length > 0) endRef.current?.scrollIntoView({ block: 'end' })
  }, [chat.turns])

  const tiers = overview.data?.tiers
  const assistant = agents.data?.agents.find((a) => a.key === AGENT)
  const keyMissing = overview.data?.provider.keyConfigured === false
  const switchedOff = assistant?.status === 'off'
  const blocked = keyMissing || switchedOff
  // The assistant's own tier asks with no tier at all, so the API walks the agent's own chain: a chain
  // the owner gave the Desk Assistant on the Agents tab is the one its tier choice uses. The other two
  // choices ask that tier's chain.
  const ownTier = assistant?.tier ?? null
  const chainFor = (key: AskTier) =>
    key === ownTier && assistant ? assistant.chain : (tiers?.find((t) => t.key === key)?.chain ?? [])

  // Enter sends where there is a keyboard; on a phone Enter is a new line and the button sends.
  const finePointer = useMemo(() => {
    try {
      return window.matchMedia('(pointer: fine)').matches
    } catch {
      return true
    }
  }, [])

  const ask = async () => {
    const question = draft.trim()
    if (!question || streaming || blocked) return
    const id = `t-${Date.now().toString(36)}-${chat.turns.length}`
    const messages = historyFor(chat.turns, question)
    follow.current = true
    dispatch({ type: 'ask', id, question, tier, now: Date.now() })
    setDraft('')
    const controller = new AbortController()
    abortRef.current = controller
    try {
      await streamAsk(
        { messages, tier: tier === ownTier ? null : tier, conversationId: chat.conversationId, agent: AGENT },
        (event) => dispatch({ type: 'event', id, event }),
        controller.signal,
      )
      dispatch({ type: 'closed', id })
    } catch (error) {
      if (isAbort(error)) dispatch({ type: 'stopped', id })
      else if (error instanceof AskRefusal) dispatch({ type: 'refused', id, message: error.message, status: error.status, callId: error.callId })
      else dispatch({ type: 'refused', id, message: askFailureText(error), status: null })
    } finally {
      if (abortRef.current === controller) abortRef.current = null
      // The call is in the log and the day's numbers now.
      void qc.invalidateQueries({ queryKey: ['ai'] })
    }
  }

  const stop = () => abortRef.current?.abort()

  const startOver = () => {
    abortRef.current?.abort()
    dispatch({ type: 'reset', conversationId: newConversationId() })
    inputRef.current?.focus()
  }

  const onSubmit = (e: FormEvent) => {
    e.preventDefault()
    void ask()
  }

  const onKeyDown = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing && finePointer) {
      e.preventDefault()
      void ask()
    }
  }

  const questions = chat.turns.length

  return (
    <div className="page hp ai ai-assistant">
      <div className="hp-bar">
        <p className="hp-bar__lead muted">
          Ask the desk's AI. Answers stream from the NVIDIA-hosted models of the tier you pick, and every question is logged in
          the Calls tab.
        </p>
        {questions > 0 && (
          <span className="ai-assistant__conv">
            <span className="faint">
              {questions} question{questions === 1 ? '' : 's'} in this conversation
            </span>
            <button type="button" className="btn btn--sm" onClick={startOver}>
              New conversation
            </button>
          </span>
        )}
      </div>

      {keyMissing && (
        <div className="alert alert--warn" role="status">
          No NVIDIA key on the server, so no model can be asked. It goes in the server's .env as NVIDIA_API_KEY; the desk
          regenerates its settings on the next deploy.
        </div>
      )}
      {switchedOff && (
        <div className="alert alert--warn" role="status">
          <span>
            The Desk Assistant is switched off{assistant?.updatedBy ? ` (by ${assistant.updatedBy}${assistant.reason ? `: ${assistant.reason}` : ''})` : ''}.
          </span>
          <Link className="btn btn--sm" to="/ai/agents#agent-desk-assistant">
            Agents →
          </Link>
        </div>
      )}

      <section className="ai-chat" aria-label="Conversation" aria-live="polite">
        {questions === 0 ? (
          <div className="ai-chat__empty">
            <p className="ai-chat__lead">A new conversation. Pick how hard the AI should think, then ask.</p>
            <dl className="ai-chat__facts">
              {ASK_TIERS.map((t) => (
                <div key={t.key}>
                  <dt>{t.label}</dt>
                  <dd>
                    {tiers ? <ChainChips chain={chainFor(t.key)} empty="no chain listed" /> : <span className="faint">chain not known yet</span>}
                    <span className="faint ai-chat__hint">{t.hint}</span>
                  </dd>
                </div>
              ))}
              {assistant && (
                <>
                  <div>
                    <dt>Reads</dt>
                    <dd>{assistant.reads}</dd>
                  </div>
                  <div>
                    <dt>Never</dt>
                    <dd>{assistant.limits}</dd>
                  </div>
                </>
              )}
            </dl>
          </div>
        ) : (
          chat.turns.map((turn) => <Turn key={turn.id} turn={turn} now={now} />)
        )}
      </section>

      <form className="ai-composer" onSubmit={onSubmit}>
        <textarea
          ref={inputRef}
          className="field__input ai-composer__input"
          rows={2}
          placeholder={blocked ? 'The assistant cannot be asked right now (see above).' : 'Ask about a backtest, a market move, a concept or the code…'}
          aria-label="Your question"
          value={draft}
          maxLength={20_000}
          onChange={(e) => setDraft(e.target.value)}
          onKeyDown={onKeyDown}
        />
        <div className="ai-composer__foot">
          <div className="seg ai-tierpick" role="radiogroup" aria-label="Which models answer">
            {ASK_TIERS.map((t) => {
              const first = chainFor(t.key)[0]
              return (
                <button
                  key={t.key}
                  type="button"
                  role="radio"
                  aria-checked={tier === t.key}
                  className={`seg__btn seg__btn--meta ${tier === t.key ? 'is-active' : ''}`}
                  title={chainFor(t.key).map(modelName).join(' → ') || undefined}
                  onClick={() => setTier(t.key)}
                >
                  {t.label}
                  <small>{first ? modelName(first) : '…'}</small>
                </button>
              )
            })}
          </div>
          <span className="faint ai-composer__hint">
            {finePointer ? 'Enter to ask · Shift+Enter for a new line' : 'The button asks; Enter is a new line'}
          </span>
          {streaming ? (
            <button type="button" className="btn btn--danger btn--sm ai-composer__go" onClick={stop}>
              <IconStop /> Stop
            </button>
          ) : (
            <button type="submit" className="btn btn--primary btn--sm ai-composer__go" disabled={!draft.trim() || blocked}>
              Ask
            </button>
          )}
        </div>
      </form>
      <div ref={endRef} className="ai-end" aria-hidden="true" />
    </div>
  )
}
