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
 * No tool places, changes or cancels a broker order; the page says so from
 * the agent's own record. The Desk Assistant may propose one of five actions
 * on the paper desk (stop or start a run, square off manual positions, close
 * everything in an account, switch an agent): the answer that proposed it ends
 * in a confirm card (AssistantActions)
 * with the summary code wrote and a PIN field, and only that PIN, sent to the
 * API's confirm endpoint, lets code carry it out. The PIN never joins the
 * conversation. The side panel sets the PIN.
 *
 * Every agent can be talked with, not only the Desk Assistant: a picker above
 * the conversation chooses the AI Trader, the Trade Reviewer, the News Analyst
 * or the Incident Explainer, each answering as itself (its chat persona, on
 * the server) and reading its own work: its decisions, reviews, records or
 * explanations. One conversation per agent, kept while the page is open, so
 * switching back finds it where it was; one question at a time across them.
 * The header says what the agent does and what it can read; each has its own
 * starter questions. The choice survives a reload (?agent= links to one).
 *
 * Memory: under each finished answer, 👍 and 👎. A 👎 asks what the answer
 * should have said; saved, that becomes a correction the agent that answered
 * reads from the next question on (and, for a scheduled agent, in its
 * scheduled work). A quiet line names the memories the answer was
 * given ("Read 3 memories: M3 · M7 · M9"), each linked to the Memory tab.
 * Text sent as "/remember …" is not asked: it is saved as a note for the
 * agent being talked with, read from the next question, and a line in the
 * conversation says under which id.
 *
 * Voice ("Ira", the assistant Telegram's voice notes reach too): the mic
 * beside Ask records the question in the browser as 16 kHz WAV
 * (AssistantVoice, lib/voice), the API writes it down with the same audio
 * model and words, and the transcript is asked exactly as a typed question,
 * marked "🎙 heard". A voice question never runs /remember. With Speak replies
 * on (this browser's choice, off by default), the newest answer is read aloud
 * when it finishes; Stop, a new question or a new recording stops it.
 * Hands-free (also this browser's choice, off by default) keeps the mic open
 * and asks whatever is said to the assistant by name ("Ira, …"), marked
 * "🎙 heard hands-free"; it pauses while Ira speaks, while an answer comes,
 * while the mic button is in use and while the tab is hidden.
 *
 * The conversations, the asking, the mic, spoken replies and hands-free are
 * one engine (useAssistantSession) that the phone app, /ira, shares; this
 * page adds the tiers, /remember, the votes and the side panel. The side
 * panel's Phones box makes and revokes the phone tokens "Hey Siri, Ira" asks
 * with (AssistantPhones).
 */

import { Fragment, useEffect, useMemo, useRef, useState } from 'react'
import type { FormEvent, KeyboardEvent } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import {
  ASK_TIERS,
  CHAT_AGENTS,
  DESK_ASSISTANT,
  MEMORY_MAX_CHARS,
  chatAgentMeta,
  conversationNote,
  formatSeconds,
  formatTokens,
  toolLabel,
  toolsLine,
  memoryErrorText,
  memoryHref,
  memoryLabel,
  memoryTextProblem,
  modelName,
  parseAskTier,
  parseRemember,
  useAddMemory,
  useAiAgents,
  useAiOverview,
  useCallFeedback,
} from '../../lib/ai'
import type { AskTier, ChatTurn, FeedbackScore, TurnRound } from '../../lib/ai'
import { readSpeakReplies, writeSpeakReplies } from '../../lib/voice'
import { IconStop } from '../../components/icons'
import { AnswerText } from './AnswerText'
import { CallLink, ChainChips, CopyButton, MemoriesRead, ToolStepRow } from './parts'
import { DocsSearchCard, TelegramCard } from './AssistantSide'
import { ActionPinCard } from './AssistantActions'
import type { ActionOutcome } from '../../lib/aiActions'
import { TurnActions } from './AssistantProposals'
import { PhonesCard } from './AssistantPhones'
import { HandsFreeStrip, HandsFreeToggle, MicButton, PlaybackBar, RecordingStrip, SpeakToggle } from './AssistantVoice'
import { useActionOutcomes, useAssistantSession, useFollowLatest } from './useAssistantSession'
import { useNow } from './common'
import '../system/health/health.css'
import './ai.css'

const DRAFT_KEY = 'openfno.ai.draft'
const TIER_KEY = 'openfno.ai.tier'
const AGENT_KEY = 'openfno.ai.agent'

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
 * correction the agent that answered reads from the next question on (the
 * API keeps it for the call's agent).
 */
function AnswerVotes({ callId, agentName }: { callId: number; agentName: string }) {
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
            What should it have said? It becomes a correction the {agentName} reads from the next question.
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
  /** The agent it was saved for: it shows in that agent's conversation. */
  agent: string
  /** How many turns the conversation had when it was sent: it shows after them. */
  at: number
  text: string
  state: 'saving' | 'saved' | 'failed'
  id: number | null
  error: string | null
}

function RememberNote({ line, agentName }: { line: RememberLine; agentName: string }) {
  return (
    <div className={`ai-remember ai-remember--${line.state}`} role="status">
      {line.state === 'saving' ? (
        <span className="faint">Saving the note…</span>
      ) : line.state === 'saved' ? (
        <span>
          Saved as note {line.id != null && <Link to={memoryHref(line.id)}>{memoryLabel(line.id)}</Link>}.{' '}
          <span className="faint">The {agentName} reads it from the next question.</span>
        </span>
      ) : (
        <span>The note was not saved: {line.error}</span>
      )}
      <span className="ai-remember__text">{line.text}</span>
    </div>
  )
}

function Turn({
  turn,
  now,
  agentKey,
  agentName,
  newest,
  outcomes,
  onOutcome,
}: {
  turn: ChatTurn
  now: number
  agentKey: string
  agentName: string
  newest: boolean
  outcomes: Readonly<Record<number, ActionOutcome>>
  onOutcome: (id: number, outcome: ActionOutcome) => void
}) {
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
          {turn.heard && (
            <span
              className="ai-q__heard"
              title={
                turn.heard.handsFree
                  ? `Heard hands-free (${formatSeconds(turn.heard.seconds)}), said to the assistant by name and written down by the audio model; the words below were asked as typed`
                  : `Spoken (${formatSeconds(turn.heard.seconds)}) and written down by the audio model; the words below were asked as typed`
              }
            >
              <span aria-hidden="true">🎙</span> heard{turn.heard.handsFree ? ' hands-free' : ''}
              {turn.heard.callId != null && (
                <>
                  {' · '}
                  <CallLink id={turn.heard.callId} />
                </>
              )}
            </span>
          )}
          <span className="faint ai-q__tier">{tierLabel(turn.tier)}</span>
        </div>
        <p className="ai-q__text">{turn.question}</p>
      </div>
      <div className={`ai-a ai-a--${turn.status}`}>
        <div className="ai-turn__head">
          <span className="ai-who">{agentKey === DESK_ASSISTANT ? 'AI' : agentName}</span>
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
        <TurnActions turn={turn} newest={newest} outcomes={outcomes} onOutcome={onOutcome} />
        {turn.status === 'done' && (turn.callId != null || turn.memoryIds != null) && (
          <div className="ai-a__foot">
            {turn.callId != null && <AnswerVotes callId={turn.callId} agentName={agentName} />}
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
              <Link className="btn btn--sm" to={`/ai/agents#agent-${agentKey}`}>
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
  const [params] = useSearchParams()
  const [draft, setDraft] = useState(() => readStored(DRAFT_KEY) ?? '')
  const [tier, setTier] = useState<AskTier>(() => parseAskTier(readStored(TIER_KEY)))
  const [remembered, setRemembered] = useState<RememberLine[]>([])
  const [outcomes, onOutcome] = useActionOutcomes()
  const inputRef = useRef<HTMLTextAreaElement>(null)

  const tiers = overview.data?.tiers
  const keyMissing = overview.data?.provider.keyConfigured === false
  const agentInfo = (key: string) => agents.data?.agents.find((a) => a.key === key)
  // Switched off, or an API from before the other agents could be talked with (it would answer with their scheduled task's JSON).
  const blockedFor = (key: string) => {
    const a = agentInfo(key)
    return keyMissing || a?.status === 'off' || (a != null && !a.chat)
  }

  const session = useAssistantSession({
    initialAgent: params.get('agent') ?? readStored(AGENT_KEY),
    blockedFor,
    tier,
    // The agent's own tier asks with no tier at all, so the API walks the agent's own chain: a chain the owner
    // gave the agent on the Agents tab is the one its tier choice uses. The other choices ask that tier's chain.
    sendTierFor: (key) => (tier === (agentInfo(key)?.tier ?? null) ? null : tier),
    speakInitially: readSpeakReplies(),
    saveSpeak: writeSpeakReplies,
    onAsked: (heard) => {
      follow()
      if (!heard) setDraft('')
    },
    // A recording or a hands-free question that cannot be asked now waits in the box.
    onHeldBack: (question) => {
      setDraft((d) => (d.trim() ? d : question))
      return 'Heard, but it cannot be asked yet: the words are in the box. Ask when the answer above finishes.'
    },
  })
  const { state, agentKey, chat, busy, streaming, busyElsewhere, recorder, recording, hearing, replies, handsFree } = session
  const meta = chatAgentMeta(agentKey)
  const now = useNow(1_000, busy != null)
  const { endRef, follow } = useFollowLatest([chat.turns, remembered], chat.turns.length > 0 || remembered.length > 0)

  useEffect(() => writeStored(DRAFT_KEY, draft), [draft])
  useEffect(() => writeStored(TIER_KEY, tier), [tier])
  useEffect(() => writeStored(AGENT_KEY, agentKey === DESK_ASSISTANT ? '' : agentKey), [agentKey])
  // A link to /ai/assistant?agent=… picks that agent, here or arriving.
  const linked = params.get('agent')
  const pickAgent = session.pick
  useEffect(() => {
    if (linked) pickAgent(linked)
    // Only a new link picks: the session's pick is a new function each render.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [linked])

  const agent = agentInfo(agentKey)
  const agentName = meta.label
  const switchedOff = agent?.status === 'off'
  const notOnThisApi = agent != null && !agent.chat
  const blocked = blockedFor(agentKey)
  const ownTier = agent?.tier ?? null
  const chainFor = (key: AskTier) =>
    key === ownTier && agent ? agent.chain : (tiers?.find((t) => t.key === key)?.chain ?? [])

  // Enter sends where there is a keyboard; on a phone Enter is a new line and the button sends.
  const finePointer = useMemo(() => {
    try {
      return window.matchMedia('(pointer: fine)').matches
    } catch {
      return true
    }
  }, [])

  /**
   * "/remember …" is saved as a note for the agent being talked with, not
   * asked. A note with nothing in it, or too long, stays in the box with the
   * reason under it; one the API refuses comes back to the box (unless
   * something new was typed) with the reason in the conversation.
   */
  const remember = (text: string) => {
    const problem = text === '' ? 'Write the note after /remember, for example: /remember quote P&L net of charges.' : memoryTextProblem(text)
    if (problem) {
      session.setNote(problem)
      return
    }
    const key = `n-${Date.now().toString(36)}-${remembered.length}`
    const typed = draft
    const forAgent = agentKey
    follow()
    setRemembered((lines) => [...lines, { key, agent: forAgent, at: chat.turns.length, text, state: 'saving', id: null, error: null }])
    setDraft('')
    // mutateAsync, not mutate: each note needs its own answer, even when two are in flight.
    addMemory.mutateAsync({ agent: forAgent, text }).then(
      (m) => setRemembered((lines) => lines.map((l) => (l.key === key ? { ...l, state: 'saved', id: m.id, text: m.text || l.text } : l))),
      (error: unknown) => {
        setRemembered((lines) => lines.map((l) => (l.key === key ? { ...l, state: 'failed', error: `${memoryErrorText(error)} It is back in the box.` } : l)))
        setDraft((d) => (d.trim() === '' ? typed : d))
      },
    )
  }

  const submit = () => {
    // Enter or Ask while recording sends the recording.
    if (recorder.state === 'recording') {
      recorder.send()
      return
    }
    const note = parseRemember(draft)
    if (note != null) remember(note)
    else if (session.ask(draft.trim())) replies.unlock()
  }

  const voiceName = session.voiceName
  const startOver = () => {
    session.startOver()
    setRemembered((lines) => lines.filter((l) => l.agent !== agentKey))
    inputRef.current?.focus()
  }

  const pick = (key: string) => {
    session.pick(key)
    follow()
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
  // This agent's notes sent after the first `k` turns, in the order they were sent.
  const notesAt = (k: number) =>
    remembered.filter((l) => l.agent === agentKey && l.at === k).map((l) => <RememberNote key={l.key} line={l} agentName={agentName} />)
  const reads = agent?.chatTools ?? []

  return (
    <div className="page ai ai-assistant">
      <div className="ai-assistant__grid">
      <div className="ai-assistant__main hp">
      <div className="seg seg--wrap ai-agentpick" role="radiogroup" aria-label="Who to talk with">
        {CHAT_AGENTS.map((a) => {
          const info = agents.data?.agents.find((x) => x.key === a.key)
          const note = conversationNote(state.chats[a.key]) || (info?.status === 'off' ? 'switched off' : '')
          return (
            <button
              key={a.key}
              type="button"
              role="radio"
              aria-checked={agentKey === a.key}
              className={`seg__btn seg__btn--meta ${agentKey === a.key ? 'is-active' : ''}`}
              title={info?.job}
              onClick={() => pick(a.key)}
            >
              {a.label}
              <small>{note || ' '}</small>
            </button>
          )
        })}
      </div>

      <div className="hp-bar">
        <p className="hp-bar__lead muted">{meta.lead}</p>
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
            The {agentName} is switched off{agent?.updatedBy ? ` (by ${agent.updatedBy}${agent.reason ? `: ${agent.reason}` : ''})` : ''}
            {agentKey === DESK_ASSISTANT ? '.' : ': it can neither run on its schedule nor be asked.'}
          </span>
          <Link className="btn btn--sm" to={`/ai/agents#agent-${agentKey}`}>
            Agents →
          </Link>
        </div>
      )}
      {notOnThisApi && !switchedOff && (
        <div className="alert alert--warn" role="status">
          This API build cannot talk with the {agentName} yet: it would answer with its scheduled task's JSON. It can once the API is deployed.
        </div>
      )}

      <section className="ai-chat" aria-label={`Conversation with the ${agentName}`} aria-live="polite">
        {questions === 0 ? (
          <div className="ai-chat__empty">
            {agentKey === DESK_ASSISTANT ? (
              <p className="ai-chat__lead">
                It reads the desk's runs, orders, positions, quotes, option chains, incidents, checkups, forecasts, news and
                strategy specs. Asked to, it can also propose to stop or start a strategy run, square off manual positions,
                close everything in an account or switch an agent: nothing happens until you confirm with your Action PIN, and
                then code does it, as your own
                click would. It never places a broker order. Numbers it takes from the desk are cited like{' '}
                <span className="mono">(get_runs, 15:30 IST)</span>.
              </p>
            ) : (
              <p className="ai-chat__lead">
                {agent ? `${agent.job} ` : ''}It answers as itself, from its own records, read-only: it never places or changes an
                order, and says so when the records do not show something. A correction saved under an answer becomes its
                memory, read from the next question on and in its scheduled work.
              </p>
            )}
            <div className="ai-starters" aria-label="Questions to start with">
              {meta.starters.map((q) => (
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
              {agent && agentKey !== DESK_ASSISTANT && (
                <div>
                  <dt>Its job</dt>
                  <dd>
                    <span>{agent.schedule}</span>
                  </dd>
                </div>
              )}
              {ASK_TIERS.map((t) => (
                <div key={t.key}>
                  <dt>{t.label}</dt>
                  <dd>
                    {tiers ? <ChainChips chain={chainFor(t.key)} empty="no chain listed" /> : <span className="faint">chain not known yet</span>}
                    <span className="faint ai-chat__hint">{t.hint}</span>
                  </dd>
                </div>
              ))}
              {agent && (
                <>
                  <div>
                    <dt>Reads</dt>
                    <dd>
                      {reads.length === 0 ? (
                        <span className="faint">nothing: it reads only what you type</span>
                      ) : (
                        <span className="ai-toolchips">
                          {reads.map((t) => (
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
                    <dd>{agent.limits}</dd>
                  </div>
                </>
              )}
            </dl>
          </div>
        ) : (
          chat.turns.map((turn, i) => (
            <Fragment key={turn.id}>
              {notesAt(i)}
              <Turn
                turn={turn}
                now={now}
                agentKey={agentKey}
                agentName={agentName}
                newest={i === chat.turns.length - 1}
                outcomes={outcomes}
                onOutcome={onOutcome}
              />
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
          placeholder={blocked ? `The ${agentName} cannot be asked right now (see above).` : meta.placeholder}
          aria-label={`Your question to the ${agentName}`}
          value={draft}
          maxLength={20_000}
          onChange={(e) => {
            setDraft(e.target.value)
            session.setNote(null)
          }}
          onKeyDown={onKeyDown}
        />
        <RecordingStrip recorder={recorder} hearing={hearing} onStopHearing={session.stopHearing} keyboard={finePointer} />
        <HandsFreeStrip handsFree={handsFree} name={voiceName} />
        {session.note && (
          <p className="small-note warn ai-flush" role="alert">
            {session.note}
          </p>
        )}
        <PlaybackBar replies={replies} />
        {busyElsewhere && busy && !isNote && (
          <p className="small-note ai-flush" role="status">
            The {chatAgentMeta(busy).label} is still answering: one question at a time.{' '}
            <button type="button" className="btn btn--ghost btn--sm" onClick={() => pick(busy)}>
              Back to it
            </button>
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
          <div className="ai-composer__send">
            <SpeakToggle on={session.speakOn} onChange={session.setSpeak} />
            <HandsFreeToggle
              handsFree={handsFree}
              name={voiceName}
              why={session.handsFreeWhy}
              onTurnOn={() => {
                // The click is the gesture a spoken reply may need later, when nothing else is pressed.
                replies.unlock()
                session.setNote(null)
              }}
            />
            <MicButton recorder={recorder} disabled={session.micBlocked != null} why={session.micBlocked} onStart={session.onMicPress} />
            {streaming && !recording && (
              <button type="button" className="btn btn--danger btn--sm ai-composer__go" onClick={session.stop}>
                <IconStop /> Stop
              </button>
            )}
            {recording ? (
              <button
                type="button"
                className="btn btn--primary btn--sm ai-composer__go"
                disabled={recorder.state !== 'recording'}
                onClick={recorder.send}
              >
                Send
              </button>
            ) : (
              (!streaming || isNote) && (
                <button
                  type="submit"
                  className="btn btn--primary btn--sm ai-composer__go"
                  disabled={!draft.trim() || (!isNote && (blocked || busyElsewhere || hearing))}
                >
                  {isNote ? 'Save note' : 'Ask'}
                </button>
              )
            )}
          </div>
        </div>
      </form>
      <div ref={endRef} className="ai-end" aria-hidden="true" />
      </div>
      <aside className="ai-assistant__side" aria-label="Telegram, phones, Action PIN and docs search">
        <TelegramCard />
        <PhonesCard />
        <ActionPinCard />
        <DocsSearchCard />
      </aside>
      </div>
    </div>
  )
}
