/**
 * One conversation engine for both faces of the assistant: AI → Assistant on
 * the console, and the Ira app on the phone (/ira). Each page draws its own
 * conversation and controls; this hook holds what they share, so a question
 * asked, heard or spoken behaves the same on both.
 *
 *   - The conversations: one per agent, kept while the page is open, one
 *     question at a time across them; an answer streams on in its own
 *     conversation when the viewer switches agent (lib/ai agentChatsReducer).
 *   - Asking: the question and the conversation's earlier turns to the API's
 *     streaming ask (lib/ai streamAsk); Stop aborts it, which also cancels the
 *     call on the server; leaving the page does too.
 *   - The mic: a recording written down by the API (lib/voice transcribe) and
 *     asked exactly as a typed question, marked as heard. A transcript that
 *     cannot be asked now (another answer is streaming) goes back to the page
 *     (onHeldBack), which keeps its words.
 *   - Spoken replies: the newest answer read aloud when it finishes, while the
 *     viewer has them on (each page keeps its own default and storage).
 *   - Hands-free: quiet while Ira speaks, while an answer comes, while the mic
 *     is in use and while the agent cannot be asked (lib/voice handsFreeHoldFor).
 */

import { useCallback, useEffect, useLayoutEffect, useReducer, useRef, useState } from 'react'
import type { RefObject } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import type { ActionOutcome } from '../../lib/aiActions'
import {
  AskRefusal,
  agentChatsReducer,
  askFailureText,
  chatAgentMeta,
  historyFor,
  initialAgentChats,
  isAbort,
  newConversationId,
  streamAsk,
  streamingAgent,
} from '../../lib/ai'
import type { AgentChats, AskTier, ChatAction, ChatState, HeardMark } from '../../lib/ai'
import { handsFreeHoldFor, handsFreeUnavailableText, micBlockedText, transcribe, voiceFailureText } from '../../lib/voice'
import { useAiVoice, useHandsFree, useSpokenReplies, useVoiceRecorder } from './voiceHooks'
import type { HandsFree, SpokenReplies, VoiceRecorder } from './voiceHooks'

export interface AssistantSessionOptions {
  /** The agent to start with: a link's ?agent=, or the viewer's stored choice; anything unknown is the Desk Assistant. */
  initialAgent: string | null
  /** Whether an agent cannot be asked now (no key, switched off, an API without it): nothing is sent to it. */
  blockedFor: (agent: string) => boolean
  /** The tier a question's turn shows. */
  tier: AskTier
  /** The tier sent for a question to `agent`; null asks the agent's own chain. Read at each question. */
  sendTierFor: (agent: string) => AskTier | null
  /** Spoken replies when the page opens, from the viewer's stored choice; `saveSpeak` keeps a change. */
  speakInitially: boolean
  saveSpeak: (on: boolean) => void
  /** Each question as it goes out; `heard` is null for a typed one (the page clears its box). */
  onAsked?: (heard: HeardMark | null) => void
  /**
   * Words that were heard but cannot be asked yet (another answer is streaming): the page keeps them where the viewer
   * can ask them later, and returns the line to show.
   */
  onHeldBack: (question: string) => string
}

export interface AssistantSession {
  state: AgentChats
  agentKey: string
  agentName: string
  chat: ChatState
  /** The agent whose answer is streaming, if any. */
  busy: string | null
  /** This agent's answer is streaming. */
  streaming: boolean
  /** Another agent's answer is streaming. */
  busyElsewhere: boolean
  /** Asks `question` (typed, or heard with `heard`); false when it cannot be asked now. */
  ask: (question: string, heard?: HeardMark | null) => boolean
  stop: () => void
  /** A new conversation with this agent; the answer streaming in it is stopped. */
  startOver: () => void
  pick: (agent: string) => void
  /** A line for the composer: a mic problem, nothing heard, words held back. */
  note: string | null
  setNote: (text: string | null) => void
  recorder: VoiceRecorder
  /** The mic is starting or recording. */
  recording: boolean
  /** A recording is being written down. */
  hearing: boolean
  stopHearing: () => void
  /** Why the mic cannot start now; null when it can. */
  micBlocked: string | null
  /** For the press that starts a recording: stops what is being read aloud, and unlocks playback for the answer. */
  onMicPress: () => void
  replies: SpokenReplies
  speakOn: boolean
  setSpeak: (on: boolean) => void
  handsFree: HandsFree
  /** Why hands-free cannot be turned on here; null when it can. */
  handsFreeWhy: string | null
  /** What the assistant answers to: "Ira" unless renamed on Telegram. */
  voiceName: string
}

/** What became of each proposed action, by its id: the confirm cards keep it across a switch of conversation. */
export function useActionOutcomes(): [Readonly<Record<number, ActionOutcome>>, (id: number, outcome: ActionOutcome) => void] {
  const [outcomes, setOutcomes] = useState<Record<number, ActionOutcome>>({})
  const onOutcome = useCallback((id: number, outcome: ActionOutcome) => setOutcomes((o) => ({ ...o, [id]: outcome })), [])
  return [outcomes, onOutcome]
}

/**
 * Keeps the newest words in view while the reader is at the bottom of the window, and leaves them be once they
 * scroll up to read. `follow()` brings it back (a new question, a switch of agent). Nothing moves while `active` is
 * false: an empty conversation's page opens at its top.
 */
export function useFollowLatest(changes: readonly unknown[], active: boolean): { endRef: RefObject<HTMLDivElement | null>; follow: () => void } {
  const endRef = useRef<HTMLDivElement>(null)
  const following = useRef(true)
  useEffect(() => {
    const onScroll = () => {
      following.current = window.innerHeight + window.scrollY >= document.documentElement.scrollHeight - 140
    }
    window.addEventListener('scroll', onScroll, { passive: true })
    return () => window.removeEventListener('scroll', onScroll)
  }, [])
  useLayoutEffect(() => {
    if (active && following.current) endRef.current?.scrollIntoView({ block: 'end' })
    // The caller names what moves the end; the ref and the flag are stable.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [...changes, active])
  return { endRef, follow: useCallback(() => (following.current = true), []) }
}

export function useAssistantSession(options: AssistantSessionOptions): AssistantSession {
  const { onHeldBack } = options
  const qc = useQueryClient()
  const [state, dispatch] = useReducer(agentChatsReducer, undefined, () => initialAgentChats(options.initialAgent))
  const [note, setNote] = useState<string | null>(null)
  const [hearing, setHearing] = useState(false)
  const [speakOn, setSpeakOn] = useState(options.speakInitially)
  const hearAbort = useRef<AbortController | null>(null)
  const abortRef = useRef<AbortController | null>(null)
  // The newest question's turn: its answer is read aloud when it finishes, if spoken replies are on by then.
  const speakNext = useRef<string | null>(null)
  const replies = useSpokenReplies()

  const agentKey = state.agent
  const chat = state.chats[agentKey]
  const agentName = chatAgentMeta(agentKey).label
  // One question at a time across the conversations.
  const busy = streamingAgent(state)
  const streaming = busy === agentKey
  const blocked = options.blockedFor(agentKey)

  const save = useRef(options.saveSpeak)
  useEffect(() => {
    save.current = options.saveSpeak
  })
  useEffect(() => save.current(speakOn), [speakOn])

  // Leaving the page stops the answer, which cancels the call on the server, and any recording still being written down.
  useEffect(
    () => () => {
      abortRef.current?.abort()
      hearAbort.current?.abort()
    },
    [],
  )

  const run = async (question: string, heard: HeardMark | null) => {
    // The answer belongs to the agent asked, wherever the page is when it arrives.
    const asking = agentKey
    const send = (action: ChatAction) => dispatch({ type: 'chat', agent: asking, action })
    const id = `t-${Date.now().toString(36)}-${chat.turns.length}`
    const messages = historyFor(chat.turns, question)
    // A new question stops the last answer's voice; this one is read aloud when it finishes, if replies are on then.
    replies.stop()
    speakNext.current = id
    send({ type: 'ask', id, question, tier: options.tier, now: Date.now(), heard })
    options.onAsked?.(heard)
    const controller = new AbortController()
    abortRef.current = controller
    try {
      await streamAsk(
        { messages, tier: options.sendTierFor(asking), conversationId: chat.conversationId, agent: asking },
        (event) => send({ type: 'event', id, event }),
        controller.signal,
      )
      send({ type: 'closed', id })
    } catch (error) {
      if (isAbort(error)) send({ type: 'stopped', id })
      else if (error instanceof AskRefusal) send({ type: 'refused', id, message: error.message, status: error.status, callId: error.callId })
      else send({ type: 'refused', id, message: askFailureText(error), status: null })
    } finally {
      if (abortRef.current === controller) abortRef.current = null
      // The call is in the log and the day's numbers now.
      void qc.invalidateQueries({ queryKey: ['ai'] })
    }
  }

  const ask = (question: string, heard: HeardMark | null = null): boolean => {
    if (!question || busy != null || blocked) return false
    void run(question, heard)
    return true
  }

  // The newest render's ask, for a transcript that arrives after the page has moved on.
  const askNow = useRef(ask)
  useEffect(() => {
    askNow.current = ask
  })
  const heldBack = useRef(onHeldBack)
  useEffect(() => {
    heldBack.current = onHeldBack
  })

  /**
   * A recording, written down and asked as typed. A voice question never runs
   * a command: "/remember …" heard is asked, not saved. When it cannot be asked
   * now, the words go back to the page.
   */
  const hear = async (wav: ArrayBuffer) => {
    const controller = new AbortController()
    hearAbort.current = controller
    setHearing(true)
    setNote(null)
    try {
      const heard = await transcribe(wav, chat.conversationId, controller.signal)
      const text = heard.text.trim()
      if (!text) setNote('Nothing was heard in that recording. Try again a little closer to the mic, or type the question.')
      else if (!askNow.current(text, { callId: heard.callId, seconds: heard.seconds })) {
        setNote(heldBack.current(text))
      }
    } catch (error) {
      if (!isAbort(error)) setNote(voiceFailureText('hear', error))
    } finally {
      if (hearAbort.current === controller) hearAbort.current = null
      setHearing(false)
    }
  }

  const recorder = useVoiceRecorder(
    (wav) => void hear(wav),
    (problem) => setNote(problem),
  )
  const recording = recorder.state !== 'idle'

  const voice = useAiVoice()
  const voiceName = voice.data?.name ?? 'Ira'
  const handsFree = useHandsFree({
    info: voice.data ?? null,
    hold: handsFreeHoldFor({ playback: replies.state, answering: busy != null, recording: recording || hearing, blocked }),
    conversationId: chat.conversationId,
    // A question said to the assistant is asked as the mic's are; when it cannot be asked now, its words go back to the page.
    onQuestion: (question, mark) => {
      if (askNow.current(question, mark)) return
      setNote(heldBack.current(question))
    },
  })

  // The newest answer is read aloud once it has finished, when spoken replies are on.
  const say = replies.say
  useEffect(() => {
    const id = speakNext.current
    if (!id) return
    for (const c of Object.values(state.chats)) {
      const t = c.turns.find((x) => x.id === id)
      if (!t) continue
      if (t.status === 'streaming') return
      speakNext.current = null
      if (t.status === 'done' && t.answer.trim() && speakOn) say(t.answer, t.callId)
      return
    }
  }, [state, speakOn, say])

  const setSpeak = (on: boolean) => {
    setSpeakOn(on)
    if (on) {
      replies.allow()
      replies.unlock()
    } else replies.stop()
  }

  return {
    state,
    agentKey,
    agentName,
    chat,
    busy,
    streaming,
    busyElsewhere: busy != null && !streaming,
    ask,
    stop: () => abortRef.current?.abort(),
    startOver: () => {
      if (streaming) abortRef.current?.abort()
      dispatch({ type: 'chat', agent: agentKey, action: { type: 'reset', conversationId: newConversationId() } })
      setNote(null)
    },
    pick: (agent: string) => {
      dispatch({ type: 'pick', agent })
      setNote(null)
    },
    note,
    setNote,
    recorder,
    recording,
    hearing,
    stopHearing: () => hearAbort.current?.abort(),
    micBlocked: micBlockedText({ blocked, answering: busy != null, hearing, agentName }),
    onMicPress: () => {
      replies.stop()
      replies.unlock()
      setNote(null)
    },
    replies,
    speakOn,
    setSpeak,
    handsFree,
    handsFreeWhy: handsFreeUnavailableText({ failed: voice.isError, info: voice.data ?? null }),
    voiceName,
  }
}
