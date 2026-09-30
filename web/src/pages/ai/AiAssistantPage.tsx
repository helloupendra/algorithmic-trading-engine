/**
 * AI → Assistant (/ai/assistant): the owner asks the desk's AI, and the
 * answer streams in as the model writes it.
 *
 * One conversation, kept in page state (a reload starts a new one; every
 * question and answer is in the Calls tab either way). The unsent draft and
 * the tier choice survive a reload in localStorage, where storage allows.
 *
 * The answer arrives over server-sent events on a POST (lib/ai streamAsk).
 * The Desk Assistant reads the desk itself, through read-only tools, in
 * rounds: in each round the model either answers or asks for tools, and the
 * server runs them and asks again with the results. So a turn shows, round
 * by round, the model's reasoning (dim and folded), anything it wrote before
 * asking (its working, not the answer), and each tool step as a row that
 * opens to the arguments and the exact JSON the model was given; then the
 * answer. When a model fails mid-round the server asks the next one in the
 * chain; what the failed model had written since its attempt began is
 * dropped, earlier rounds stay, and one line says who timed out and who was
 * asked next. Each finished answer names the model that gave it, how long it
 * took, the tokens and tool calls it cost, and links to its call. Stop aborts
 * the request, which also cancels the call on the server.
 *
 * No tool places, changes or cancels an order; the page says so from the
 * agent's own record.
 *
 * Memory: under each finished answer, 👍 and 👎. A 👎 asks what the answer
 * should have said; saved, that becomes a correction the Assistant reads
 * from the next question on. A quiet line names the memories the answer was
 * given ("Read 3 memories: M3 · M7 · M9"), each linked to the Memory tab.
 * Text sent as "/remember …" is not asked: it is saved as a note, read from
 * the next question, and a line in the conversation says under which id.
 */

import { Fragment, useEffect, useLayoutEffect, useMemo, useReducer, useRef, useState } from 'react'
import type { FormEvent, KeyboardEvent } from 'react'
import { Link } from 'react-router-dom'
import { useQueryClient } from '@tanstack/react-query'
import {
  ASK_TIERS,
  AskRefusal,
  MEMORY_MAX_CHARS,
  askFailureText,
  chatReducer,
  formatSeconds,
  formatTokens,
  historyFor,
  initialChat,
  toolLabel,
  toolsLine,
  isAbort,
  memoryErrorText,
  memoryHref,
  memoryLabel,
  memoryTextProblem,
  modelName,
  newConversationId,
  parseAskTier,
  parseRemember,
  streamAsk,
  useAddMemory,
  useAiAgents,
  useAiOverview,
  useCallFeedback,
} from '../../lib/ai'
import type { AskTier, ChatTurn, FeedbackScore, TurnRound } from '../../lib/ai'
import { IconStop } from '../../components/icons'
import { AnswerText } from './AnswerText'
import { CallLink, ChainChips, MemoriesRead, ToolStepRow } from './parts'
import { DocsSearchCard, TelegramCard } from './AssistantSide'
import { useNow } from './common'
import '../system/health/health.css'
import './ai.css'

const AGENT = 'desk-assistant'

/** Questions that show what reading the desk is for; a click puts one in the box, it does not send it. */
const STARTERS = [
  'How did the runs do today?',
  'Why did the worst run today lose money?',
  'What is open right now, and what is it worth?',
  'Anything wrong on the desk: open incidents or checkup items?',
]
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
function Thinking({ text, live, round }: { text: string; live: boolean; round: number | null }) {
  const words = text.trim() ? text.trim().split(/\s+/).length : 0
  const tail = text.length > 180 ? `…${text.slice(-180)}` : text
  return (
    <details className={`ai-think ${live ? 'ai-think--live' : ''}`}>
      <summary className="ai-think__summary">
        <span className="ai-think__text">
          {round != null && <span className="ai-think__round">Round {round} · </span>}
          <span className="ai-think__label">{live ? 'Thinking' : 'Reasoning'}</span>
          <span className="faint"> · {formatTokens(words)} words</span>
        </span>
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
            {turn.round > 1 && <span className="faint"> · round {turn.round}</span>}
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
    const tools = toolsLine(turn.toolCalls, turn.roundCount)
    return (
      <span className="ai-a__meta">
        <b title={turn.model ?? undefined}>{modelName(turn.model)}</b>
        <span className="faint">
          {' '}
          · {formatSeconds(turn.seconds)}
          {tokens != null && ` · ${formatTokens(tokens)} tokens`}
          {tools && ` · ${tools}`}
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

/** One round on the way to the answer: its reasoning, what it wrote before asking, and the tools it called. */
function RoundSteps({ round, numbered, live, now }: { round: TurnRound; numbered: boolean; live: boolean; now: number }) {
  if (!round.reasoning && !round.working && round.tools.length === 0) return null
  return (
    <div className="ai-round">
      {round.reasoning && <Thinking text={round.reasoning} live={live} round={numbered ? round.round : null} />}
      {round.working && (
        <p className="ai-working" title="Written before it asked for data: the model's working, not the answer">
          {round.working}
        </p>
      )}
      {round.tools.length > 0 && (
        <div className="ai-tools-steps">
          {round.tools.map((step, i) => (
            <ToolStepRow key={step.id || i} step={step} now={now} showRound={numbered && !round.reasoning} />
          ))}
        </div>
      )}
    </div>
  )
}

/**
 * 👍 and 👎 under a finished answer. A vote is sent at once, and shows as
 * chosen once the API has it; a second click on the chosen one clears it.
 * 👎 then asks what the answer should have said: saved, that becomes a
 * correction the Assistant reads from the next question on.
 */
function AnswerVotes({ callId }: { callId: number }) {
  const feedback = useCallFeedback()
  const [score, setScore] = useState<FeedbackScore | null>(null)
  const [asking, setAsking] = useState(false)
  const [correction, setCorrection] = useState('')
  const [saved, setSaved] = useState<{ memoryId: number | null } | null>(null)
  const busy = feedback.isPending
  const problem = memoryTextProblem(correction)

  const vote = (next: FeedbackScore) => {
    feedback.mutate(
      { callId, score: score === next ? 0 : next },
      {
        onSuccess: (r) => {
          const chosen = r.score === 0 ? null : r.score
          setScore(chosen)
          setSaved(null)
          setAsking(chosen === -1)
        },
      },
    )
  }
  const save = () =>
    feedback.mutate(
      { callId, score: -1, correction },
      {
        onSuccess: (r) => {
          setScore(-1)
          setAsking(false)
          setCorrection('')
          setSaved({ memoryId: r.memory?.id ?? null })
        },
      },
    )

  const voteButton = (value: FeedbackScore) => {
    const chosen = score === value
    // This button's own vote is on its way: a click on the chosen one sends 0 (clear).
    const pending = busy && !feedback.variables?.correction && feedback.variables?.score === (chosen ? 0 : value)
    return (
      <button
        type="button"
        className="btn btn--ghost btn--sm ai-vote"
        aria-pressed={chosen}
        aria-busy={pending || undefined}
        aria-label={value === 1 ? 'A good answer' : 'A wrong answer'}
        title={chosen ? 'Chosen: click again to clear' : value === 1 ? 'A good answer' : 'A wrong answer: say what it should have said'}
        disabled={busy}
        onClick={() => vote(value)}
      >
        {value === 1 ? '👍' : '👎'}
      </button>
    )
  }

  return (
    <div className="ai-votes">
      <span className="ai-votes__btns" role="group" aria-label="Was this answer right?">
        {voteButton(1)}
        {voteButton(-1)}
      </span>
      {saved && (
        <span className="ai-votes__said" role="status">
          {saved.memoryId != null ? (
            <>
              Saved as correction <Link to={memoryHref(saved.memoryId)}>{memoryLabel(saved.memoryId)}</Link>.
            </>
          ) : (
            'Saved.'
          )}
        </span>
      )}
      {asking && (
        <div className="ai-votes__ask">
          <label className="ai-votes__q" htmlFor={`fix-${callId}`}>
            What should it have said? It becomes a correction the Assistant reads from the next question.
          </label>
          <textarea
            id={`fix-${callId}`}
            className="field__input ai-memtext"
            rows={3}
            maxLength={MEMORY_MAX_CHARS}
            value={correction}
            onChange={(e) => setCorrection(e.target.value)}
          />
          <div className="ai-mem__editfoot">
            <button type="button" className="btn btn--primary btn--sm" disabled={busy || problem != null} onClick={save}>
              {busy && feedback.variables?.correction ? 'Saving…' : 'Save'}
            </button>
            <button type="button" className="btn btn--ghost btn--sm" disabled={busy} onClick={() => setAsking(false)}>
              Skip
            </button>
            <span className="ai-mem__count faint">
              {correction.trim().length} / {MEMORY_MAX_CHARS}
            </span>
          </div>
        </div>
      )}
      {feedback.isError && (
        <div className="alert alert--error ai-flush ai-votes__error" role="alert">
          {memoryErrorText(feedback.error, 'call')}
        </div>
      )}
    </div>
  )
}

/** A note sent as /remember: saving, saved under its id, or not saved and why. */
interface RememberLine {
  key: string
  /** How many turns the conversation had when it was sent: it shows after them. */
  at: number
  text: string
  state: 'saving' | 'saved' | 'failed'
  id: number | null
  error: string | null
}

function RememberNote({ line }: { line: RememberLine }) {
  return (
    <div className={`ai-remember ai-remember--${line.state}`} role="status">
      {line.state === 'saving' ? (
        <span className="faint">Saving the note…</span>
      ) : line.state === 'saved' ? (
        <span>
          Saved as note {line.id != null && <Link to={memoryHref(line.id)}>{memoryLabel(line.id)}</Link>}.{' '}
          <span className="faint">The Assistant reads it from the next question.</span>
        </span>
      ) : (
        <span>The note was not saved: {line.error}</span>
      )}
      <span className="ai-remember__text">{line.text}</span>
    </div>
  )
}

function Turn({ turn, now }: { turn: ChatTurn; now: number }) {
  const streaming = turn.status === 'streaming'
  // Rounds are worth numbering once there is more than one.
  const many = turn.rounds.length > 1 || (turn.roundCount ?? 1) > 1
  const last = turn.rounds[turn.rounds.length - 1]
  const lastHasTools = !!last && last.tools.length > 0
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
        {turn.rounds.map((r, i) => (
          <RoundSteps
            key={r.round}
            round={r}
            numbered={many}
            live={streaming && i === turn.rounds.length - 1 && !turn.answer && r.tools.length === 0}
            now={now}
          />
        ))}
        {turn.answer ? (
          <AnswerText text={turn.answer} />
        ) : streaming && !turn.rounds.some((r) => r.reasoning || r.tools.length > 0) ? (
          <p className="ai-a__pending faint">No words yet. A reasoning model can think for a while before it writes.</p>
        ) : streaming && lastHasTools ? (
          <p className="ai-a__pending faint">Sending what the desk said back to the model…</p>
        ) : null}
        {turn.status === 'done' && turn.finishReason === 'length' && (
          <p className="small-note warn ai-flush">The answer was cut at the token limit.</p>
        )}
        {turn.status === 'done' && (turn.callId != null || turn.memoryIds != null) && (
          <div className="ai-a__foot">
            {turn.callId != null && <AnswerVotes callId={turn.callId} />}
            <MemoriesRead ids={turn.memoryIds} />
          </div>
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
  const addMemory = useAddMemory()
  const qc = useQueryClient()
  const [chat, dispatch] = useReducer(chatReducer, undefined, () => initialChat())
  const [draft, setDraft] = useState(() => readStored(DRAFT_KEY) ?? '')
  const [tier, setTier] = useState<AskTier>(() => parseAskTier(readStored(TIER_KEY)))
  const [remembered, setRemembered] = useState<RememberLine[]>([])
  const [composerNote, setComposerNote] = useState<string | null>(null)
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
    if (follow.current && (chat.turns.length > 0 || remembered.length > 0)) endRef.current?.scrollIntoView({ block: 'end' })
  }, [chat.turns, remembered])

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

  /**
   * "/remember …" is saved as a note, not asked. A note with nothing in it, or
   * too long, stays in the box with the reason under it; one the API refuses
   * comes back to the box (unless something new was typed) with the reason
   * in the conversation.
   */
  const remember = (text: string) => {
    const problem = text === '' ? 'Write the note after /remember, for example: /remember quote P&L net of charges.' : memoryTextProblem(text)
    if (problem) {
      setComposerNote(problem)
      return
    }
    const key = `n-${Date.now().toString(36)}-${remembered.length}`
    const typed = draft
    follow.current = true
    setRemembered((lines) => [...lines, { key, at: chat.turns.length, text, state: 'saving', id: null, error: null }])
    setDraft('')
    // mutateAsync, not mutate: each note needs its own answer, even when two are in flight.
    addMemory.mutateAsync({ agent: AGENT, text }).then(
      (m) => setRemembered((lines) => lines.map((l) => (l.key === key ? { ...l, state: 'saved', id: m.id, text: m.text || l.text } : l))),
      (error: unknown) => {
        setRemembered((lines) => lines.map((l) => (l.key === key ? { ...l, state: 'failed', error: `${memoryErrorText(error)} It is back in the box.` } : l)))
        setDraft((d) => (d.trim() === '' ? typed : d))
      },
    )
  }

  const submit = () => {
    const note = parseRemember(draft)
    if (note != null) remember(note)
    else void ask()
  }

  const stop = () => abortRef.current?.abort()

  const startOver = () => {
    abortRef.current?.abort()
    dispatch({ type: 'reset', conversationId: newConversationId() })
    setRemembered([])
    setComposerNote(null)
    inputRef.current?.focus()
  }

  const onSubmit = (e: FormEvent) => {
    e.preventDefault()
    submit()
  }

  const onKeyDown = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing && finePointer) {
      e.preventDefault()
      submit()
    }
  }

  const questions = chat.turns.length
  const isNote = parseRemember(draft) != null
  // The notes sent after the first `k` turns, in the order they were sent.
  const notesAt = (k: number) => remembered.filter((l) => l.at === k).map((l) => <RememberNote key={l.key} line={l} />)

  return (
    <div className="page ai ai-assistant">
      <div className="ai-assistant__grid">
      <div className="ai-assistant__main hp">
      <div className="hp-bar">
        <p className="hp-bar__lead muted">
          Ask the desk's AI. It reads the desk through read-only tools, answers stream from the NVIDIA-hosted models of the
          tier you pick, and every question is logged in the Calls tab.
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
            <p className="ai-chat__lead">
              It can read the desk's runs, orders, positions, quotes, option chains, incidents, checkups, forecasts, news
              and strategy specs, read-only: it never places or changes an order. Numbers it takes from the desk are cited
              like <span className="mono">(get_runs, 15:30 IST)</span>.
            </p>
            <div className="ai-starters" aria-label="Questions to start with">
              {STARTERS.map((q) => (
                <button
                  key={q}
                  type="button"
                  className="btn btn--sm ai-starter"
                  disabled={blocked}
                  onClick={() => {
                    setDraft(q)
                    inputRef.current?.focus()
                  }}
                >
                  {q}
                </button>
              ))}
            </div>
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
                    <dt>Tools</dt>
                    <dd>
                      {assistant.tools.length === 0 ? (
                        <span className="faint">none: it reads only what you type</span>
                      ) : (
                        <span className="ai-toolchips">
                          {assistant.tools.map((t) => (
                            <span key={t.name} className="ai-toolchip" title={`${t.name}: ${t.description}`}>
                              {toolLabel(t.name, '').label}
                            </span>
                          ))}
                        </span>
                      )}
                    </dd>
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
          chat.turns.map((turn, i) => (
            <Fragment key={turn.id}>
              {notesAt(i)}
              <Turn turn={turn} now={now} />
            </Fragment>
          ))
        )}
        {notesAt(questions)}
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
          onChange={(e) => {
            setDraft(e.target.value)
            setComposerNote(null)
          }}
          onKeyDown={onKeyDown}
        />
        {composerNote && (
          <p className="small-note warn ai-flush" role="alert">
            {composerNote}
          </p>
        )}
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
            {finePointer ? 'Enter to ask · Shift+Enter for a new line · /remember … saves a note' : 'The button asks; Enter is a new line'}
          </span>
          {streaming && (
            <button type="button" className="btn btn--danger btn--sm ai-composer__go" onClick={stop}>
              <IconStop /> Stop
            </button>
          )}
          {(!streaming || isNote) && (
            <button type="submit" className="btn btn--primary btn--sm ai-composer__go" disabled={!draft.trim() || (blocked && !isNote)}>
              {isNote ? 'Save note' : 'Ask'}
            </button>
          )}
        </div>
      </form>
      <div ref={endRef} className="ai-end" aria-hidden="true" />
      </div>
      <aside className="ai-assistant__side" aria-label="Telegram and docs search">
        <TelegramCard />
        <DocsSearchCard />
      </aside>
      </div>
    </div>
  )
}
