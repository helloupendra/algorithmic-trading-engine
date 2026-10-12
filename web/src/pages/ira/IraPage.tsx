/**
 * The Ira app (/ira): the desk's assistant on the owner's phone, like Siri or
 * Gemini (owner, 12 Oct 2026). Full screen, outside the console's chrome,
 * installable on Android and iPhone (ira.webmanifest, ira-sw.js, and the
 * shell's head in ira.html), dark by default and following the theme.
 *
 * One big mic: hold it and speak, let go to ask; or tap it, speak, and tap it
 * again to ask. Hands-free ("Ira, …") and spoken replies sit either side of
 * it; replies are spoken by default here. The conversation is the console's
 * own engine (pages/ai/useAssistantSession), drawn as large bubbles: what was
 * heard, marked 🎙, then the answer in the console's renderer (tables and
 * all), and the confirm card when Ira proposes an action. Actions proposed
 * from "Hey Siri, Ira" wait at the top for the PIN.
 *
 * Signed out, it goes to the console's sign-in and comes back here (an
 * iPhone's home-screen app has storage of its own, so its first launch always
 * does). Admin only, as every AI endpoint is.
 */

import { useCallback, useEffect, useId, useRef, useState } from 'react'
import type { FormEvent, ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { Navigate, useLocation } from 'react-router-dom'
import { useAuth } from '../../lib/auth'
import { useTheme, THEME_LABELS, THEME_PREFS } from '../../lib/theme'
import {
  CHAT_AGENTS,
  DESK_ASSISTANT,
  chatAgentMeta,
  conversationNote,
  formatSeconds,
  modelName,
  toolLabel,
  useAiAgents,
  useAiOverview,
} from '../../lib/ai'
import type { ChatTurn } from '../../lib/ai'
import {
  installHint,
  iraActivity,
  iraActivityText,
  iraGate,
  isStandalone,
  loginState,
  readInstallDismissed,
  readIraSpeak,
  usePhoneActions,
  writeInstallDismissed,
  writeIraSpeak,
} from '../../lib/ira'
import type { InstallHint } from '../../lib/ira'
import type { ActionOutcome, ProposedAction } from '../../lib/aiActions'
import { IconChevronDown, IconKeyboard, IconMic, IconMore, IconStop, IconX } from '../../components/icons'
import { AnswerText } from '../ai/AnswerText'
import { ActionCard, ActionPinCard } from '../ai/AssistantActions'
import { TurnActions } from '../ai/AssistantProposals'
import { PhonesCard } from '../ai/AssistantPhones'
import { HandsFreeStrip, HandsFreeToggle, MicButton, PlaybackBar, RecordingStrip, SpeakToggle } from '../ai/AssistantVoice'
import { useActionOutcomes, useAssistantSession, useFollowLatest } from '../ai/useAssistantSession'
import '../ai/ai.css'
import './ira.css'

const AGENT_KEY = 'openfno.ira.agent'

function readAgent(): string | null {
  try {
    return window.localStorage.getItem(AGENT_KEY)
  } catch {
    return null
  }
}

function writeAgent(agent: string) {
  try {
    if (agent === DESK_ASSISTANT) window.localStorage.removeItem(AGENT_KEY)
    else window.localStorage.setItem(AGENT_KEY, agent)
  } catch {
    /* storage refused: the choice lives as long as the page */
  }
}

/** The route: wait for the session, send a signed-out visitor to sign in (and back here), turn a trader away. */
export function IraPage() {
  const auth = useAuth()
  const location = useLocation()
  // The theme follows the console's choice (dark by default) from the first paint.
  useTheme()
  const gate = iraGate(auth)
  if (gate === 'wait') {
    return (
      <div className="ira ira--center" role="status" aria-live="polite">
        <IraGlyph className="ira-glyph ira-glyph--pulse" />
        <p className="ira-faint">Restoring your session…</p>
      </div>
    )
  }
  if (gate === 'login') return <Navigate to="/login" replace state={loginState(location)} />
  if (gate === 'forbidden') {
    return (
      <div className="ira ira--center">
        <IraGlyph className="ira-glyph" />
        <h1 className="ira-hello__title">Ira is for the desk's admin</h1>
        <p className="ira-faint">This account cannot ask the desk's AI. Sign in as an admin to use Ira.</p>
        <a className="btn" href="/desk">
          Open the console
        </a>
      </div>
    )
  }
  return <IraApp />
}

/** Five candles standing as a voice: the app's mark (public/brand/ira-mark.svg is the same drawing). */
function IraGlyph({ className = 'ira-glyph' }: { className?: string }) {
  const bodies = [12, 22, 32, 22, 12]
  return (
    <svg className={className} viewBox="0 0 64 64" aria-hidden="true">
      <defs>
        <linearGradient id="ira-glyph-fg" x1="0" y1="10" x2="0" y2="54" gradientUnits="userSpaceOnUse">
          <stop offset="0" stopColor="var(--brand-hi)" />
          <stop offset="1" stopColor="var(--live)" />
        </linearGradient>
      </defs>
      <g fill="url(#ira-glyph-fg)">
        {bodies.map((h, i) => {
          const x = 13.2 + i * 8
          return (
            <g key={i}>
              <rect x={x + 2} y={32 - h / 2 - 4.5} width={1.6} height={h + 9} rx={0.8} opacity={0.55} />
              <rect className="ira-glyph__body" x={x} y={32 - h / 2} width={5.6} height={h} rx={1.8} style={{ animationDelay: `${i * 0.12}s` }} />
            </g>
          )
        })}
      </g>
    </svg>
  )
}

/** The browser's install prompt (Android: Chrome, Edge, Samsung Internet), held until the viewer asks for it. */
interface InstallPromptEvent extends Event {
  prompt: () => Promise<void>
  userChoice: Promise<{ outcome: 'accepted' | 'dismissed' }>
}

function useInstall(): { hint: InstallHint; install: () => void; dismiss: () => void } {
  const [prompt, setPrompt] = useState<InstallPromptEvent | null>(null)
  const [dismissed, setDismissed] = useState(() => readInstallDismissed())
  const [standalone, setStandalone] = useState(() => isStandalone())
  useEffect(() => {
    const onPrompt = (e: Event) => {
      e.preventDefault()
      setPrompt(e as InstallPromptEvent)
    }
    const onInstalled = () => {
      setPrompt(null)
      setStandalone(true)
    }
    window.addEventListener('beforeinstallprompt', onPrompt)
    window.addEventListener('appinstalled', onInstalled)
    return () => {
      window.removeEventListener('beforeinstallprompt', onPrompt)
      window.removeEventListener('appinstalled', onInstalled)
    }
  }, [])
  const hint = installHint({
    standalone,
    promptReady: prompt != null,
    userAgent: navigator.userAgent,
    maxTouchPoints: navigator.maxTouchPoints ?? 0,
    dismissed,
  })
  return {
    hint,
    install: () => {
      if (!prompt) return
      void prompt.prompt()
      void prompt.userChoice.then(() => setPrompt(null))
    },
    dismiss: () => {
      writeInstallDismissed()
      setDismissed(true)
    },
  }
}

/** The service worker that keeps the app's shell for an offline launch (production builds only: dev serves no shell). */
function useShellWorker() {
  useEffect(() => {
    if (!import.meta.env.PROD || !('serviceWorker' in navigator)) return
    navigator.serviceWorker.register('/ira-sw.js', { scope: '/ira' }).catch(() => undefined)
  }, [])
}

function useOnline(): boolean {
  const [online, setOnline] = useState(() => (typeof navigator === 'undefined' ? true : navigator.onLine !== false))
  useEffect(() => {
    const on = () => setOnline(true)
    const off = () => setOnline(false)
    window.addEventListener('online', on)
    window.addEventListener('offline', off)
    return () => {
      window.removeEventListener('online', on)
      window.removeEventListener('offline', off)
    }
  }, [])
  return online
}

type SheetName = 'agents' | 'settings' | null

function IraApp() {
  const overview = useAiOverview()
  const agents = useAiAgents()
  const online = useOnline()
  const install = useInstall()
  const [sheet, setSheet] = useState<SheetName>(null)
  const [typing, setTyping] = useState(false)
  const [draft, setDraft] = useState('')
  const [outcomes, onOutcome] = useActionOutcomes()
  const typeRef = useRef<HTMLInputElement>(null)
  useShellWorker()

  useEffect(() => {
    const before = document.title
    document.title = 'Ira · OpenFNO'
    return () => {
      document.title = before
    }
  }, [])

  const keyMissing = overview.data?.provider.keyConfigured === false
  const agentInfo = (key: string) => agents.data?.agents.find((a) => a.key === key)
  const blockedFor = (key: string) => {
    const a = agentInfo(key)
    return keyMissing || a?.status === 'off' || (a != null && !a.chat)
  }

  const session = useAssistantSession({
    initialAgent: readAgent(),
    blockedFor,
    tier: 'judge',
    // The agent's own chain, always: the phone has no tier picker.
    sendTierFor: () => null,
    speakInitially: readIraSpeak(),
    saveSpeak: writeIraSpeak,
    onAsked: (heard) => {
      follow()
      if (!heard) setDraft('')
    },
    onHeldBack: (question) => {
      setDraft((d) => (d.trim() ? d : question))
      setTyping(true)
      return 'Heard, but another answer is still coming: the words are in the box. Ask when it finishes.'
    },
  })
  const { agentKey, chat, streaming, busy, busyElsewhere, recorder, recording, hearing, replies, handsFree } = session
  const { endRef, follow } = useFollowLatest([chat.turns], chat.turns.length > 0)
  useEffect(() => writeAgent(agentKey), [agentKey])

  const meta = chatAgentMeta(agentKey)
  const agent = agentInfo(agentKey)
  const blocked = blockedFor(agentKey)
  const last = chat.turns[chat.turns.length - 1]
  const activity = iraActivity({
    online,
    recording,
    hearing,
    playback: replies.state,
    streaming,
    answerStarted: !!last && last.status === 'streaming' && last.answer.length > 0,
    handsFreeListening: handsFree.status === 'listening' || handsFree.status === 'hearing' || handsFree.status === 'checking',
  })

  // Actions proposed from "Hey Siri, Ira" (the API lists only those), waiting for the PIN. One confirmed or cancelled
  // here stays on screen with its outcome after the API stops listing it, until the app is closed.
  const phoneActions = usePhoneActions(true)
  const [waiting, setWaiting] = useState<ProposedAction[]>([])
  const settledIds = useRef(new Set<number>())
  useEffect(() => {
    const listed = phoneActions.data
    if (!listed) return
    setWaiting((before) => {
      const ids = new Set(listed.map((a) => a.id))
      return [...listed, ...before.filter((a) => !ids.has(a.id) && settledIds.current.has(a.id))]
    })
  }, [phoneActions.data])
  const onPhoneOutcome = (id: number, outcome: ActionOutcome) => {
    if (outcome.kind !== 'wrong') settledIds.current.add(id)
    onOutcome(id, outcome)
  }

  const submitTyped = (e: FormEvent) => {
    e.preventDefault()
    if (session.ask(draft.trim())) replies.unlock()
  }

  const blockedText = keyMissing
    ? 'No model can be asked: the server has no NVIDIA key.'
    : agent?.status === 'off'
      ? `The ${meta.label} is switched off on the console's AI page.`
      : agent != null && !agent.chat
        ? `This API build cannot talk with the ${meta.label} yet.`
        : null

  return (
    <div className="ira">
      <header className="ira-top">
        <IraGlyph className={`ira-glyph ira-glyph--sm ${activity === 'speaking' || activity === 'recording' ? 'ira-glyph--pulse' : ''}`} />
        <div className="ira-top__who">
          <span className="ira-top__name">{session.voiceName}</span>
          <span className={`ira-top__state ira-top__state--${activity}`} aria-live="polite">
            {iraActivityText(activity, session.voiceName)}
          </span>
        </div>
        <button type="button" className="ira-chip" aria-haspopup="dialog" onClick={() => setSheet('agents')}>
          <span className="ira-chip__label">{meta.label}</span>
          <IconChevronDown />
        </button>
        <button type="button" className="ira-iconbtn" aria-label="Settings" aria-haspopup="dialog" onClick={() => setSheet('settings')}>
          <IconMore />
        </button>
      </header>

      <main className="ira-main">
        {blockedText && (
          <div className="alert alert--warn ira-banner" role="status">
            {blockedText}
          </div>
        )}
        {install.hint !== 'none' && <InstallBar hint={install.hint} onInstall={install.install} onDismiss={install.dismiss} />}

        {waiting.length > 0 && (
          <section className="ira-waiting" aria-label="Actions waiting for your PIN">
            <p className="ira-waiting__head">From “Hey Siri, {session.voiceName}”</p>
            {waiting.map((a, i) => (
              <ActionCard
                key={a.id}
                action={a}
                replaced={false}
                outcome={outcomes[a.id] ?? null}
                onOutcome={(o: ActionOutcome) => onPhoneOutcome(a.id, o)}
                focus={i === 0}
              />
            ))}
          </section>
        )}

        <section className="ira-chat" aria-label={`Conversation with the ${meta.label}`} aria-live="polite">
          {chat.turns.length === 0 ? (
            <div className="ira-hello">
              <IraGlyph className="ira-glyph ira-glyph--lg" />
              <h1 className="ira-hello__title">Ask {agentKey === DESK_ASSISTANT ? session.voiceName : `the ${meta.label}`}</h1>
              <p className="ira-faint">
                Hold the mic and speak, or tap it, speak, and tap again.
                {handsFree.available ? ` With Hands-free on, just say “${session.voiceName}, …”.` : ''}
              </p>
              <div className="ira-starters" aria-label="Questions to start with">
                {meta.starters.slice(0, 4).map((q) => (
                  <button
                    key={q}
                    type="button"
                    className="ira-starter"
                    disabled={blocked || busy != null}
                    onClick={() => {
                      replies.unlock()
                      session.ask(q)
                    }}
                  >
                    {q}
                  </button>
                ))}
              </div>
            </div>
          ) : (
            chat.turns.map((turn, i) => (
              <IraTurn key={turn.id} turn={turn} newest={i === chat.turns.length - 1} outcomes={outcomes} onOutcome={onOutcome} />
            ))
          )}
          {chat.turns.length > 0 && !streaming && (
            <p className="ira-chat__foot">
              <button type="button" className="ira-link" onClick={session.startOver}>
                New conversation
              </button>
            </p>
          )}
        </section>
      </main>

      <footer className="ira-dock">
        <div className="ira-dock__status">
          <RecordingStrip recorder={recorder} hearing={hearing} onStopHearing={session.stopHearing} keyboard={false} tapSends />
          <HandsFreeStrip handsFree={handsFree} name={session.voiceName} />
          <PlaybackBar replies={replies} />
          {session.note && (
            <p className="small-note warn ai-flush" role="alert">
              {session.note}
            </p>
          )}
          {busyElsewhere && busy && (
            <p className="small-note ai-flush" role="status">
              The {chatAgentMeta(busy).label} is still answering.{' '}
              <button type="button" className="ira-link" onClick={() => session.pick(busy)}>
                Back to it
              </button>
            </p>
          )}
        </div>

        {typing && (
          <form className="ira-type" onSubmit={submitTyped}>
            <input
              ref={typeRef}
              className="field__input ira-type__input"
              placeholder={blocked ? 'It cannot be asked right now' : agentKey === DESK_ASSISTANT ? `Type a question for ${session.voiceName}` : `Type a question for the ${meta.label}`}
              aria-label={`Your question to the ${meta.label}`}
              value={draft}
              maxLength={4000}
              enterKeyHint="send"
              onChange={(e) => {
                setDraft(e.target.value)
                session.setNote(null)
              }}
            />
            <button type="submit" className="btn btn--primary ira-type__go" disabled={!draft.trim() || blocked || busy != null || hearing}>
              Ask
            </button>
          </form>
        )}

        <div className="ira-dock__row">
          <SpeakToggle className="ira-side" on={session.speakOn} onChange={session.setSpeak} />
          {streaming ? (
            <button type="button" className="ira-mic ira-mic--stop" aria-label="Stop the answer" onClick={session.stop}>
              <IconStop />
            </button>
          ) : (
            <MicButton
              className="ira-mic"
              tapSends
              recorder={recorder}
              disabled={session.micBlocked != null}
              why={session.micBlocked}
              onStart={session.onMicPress}
            >
              {recording ? <span className="ira-mic__send" aria-hidden="true" /> : <IconMic />}
            </MicButton>
          )}
          <HandsFreeToggle
            className="ira-side"
            handsFree={handsFree}
            name={session.voiceName}
            why={session.handsFreeWhy}
            onTurnOn={() => {
              replies.unlock()
              session.setNote(null)
            }}
          />
        </div>
        <div className="ira-dock__hint">
          {recording ? (
            // A phone has no Esc: dropping a take is a tap of its own.
            <button type="button" className="ira-link ira-link--icon" onClick={recorder.cancel}>
              <IconX /> Cancel
            </button>
          ) : (
            <button
              type="button"
              className="ira-link ira-link--icon"
              aria-pressed={typing}
              onClick={() => {
                setTyping((t) => !t)
                window.setTimeout(() => typeRef.current?.focus(), 0)
              }}
            >
              <IconKeyboard /> {typing ? 'Hide keyboard' : 'Type'}
            </button>
          )}
          <span className="ira-faint">
            {streaming
              ? 'Tap the square to stop'
              : recording
                ? recorder.held
                  ? 'Let go to ask'
                  : 'Tap again to ask'
                : 'Hold to talk · tap to talk'}
          </span>
        </div>
      </footer>
      <div ref={endRef} className="ira-end" aria-hidden="true" />

      {sheet === 'agents' && (
        <Sheet title="Talk with" onClose={() => setSheet(null)}>
          <div className="ira-agents" role="radiogroup" aria-label="Who to talk with">
            {CHAT_AGENTS.map((a) => {
              const info = agentInfo(a.key)
              const note = conversationNote(session.state.chats[a.key]) || (info?.status === 'off' ? 'switched off' : '')
              return (
                <button
                  key={a.key}
                  type="button"
                  role="radio"
                  aria-checked={agentKey === a.key}
                  className={`ira-agent ${agentKey === a.key ? 'is-active' : ''}`}
                  onClick={() => {
                    session.pick(a.key)
                    follow()
                    setSheet(null)
                  }}
                >
                  <span className="ira-agent__name">{a.label}</span>
                  {note && <span className="ira-agent__note">{note}</span>}
                  {info?.job && <span className="ira-agent__job">{info.job}</span>}
                </button>
              )
            })}
          </div>
        </Sheet>
      )}
      {sheet === 'settings' && <SettingsSheet install={install} onClose={() => setSheet(null)} onNewConversation={session.startOver} />}
    </div>
  )
}

/** "Waiting for the model", what the desk is reading, or the answer, for one bubble. */
function waitText(turn: ChatTurn): string {
  const lastRound = turn.rounds[turn.rounds.length - 1]
  const step = lastRound?.tools[lastRound.tools.length - 1]
  if (step) return `Reading the desk: ${toolLabel(step.name, step.arguments).label}`
  if (turn.model) return `Asking ${modelName(turn.model)}`
  return 'Sending'
}

function IraTurn({
  turn,
  newest,
  outcomes,
  onOutcome,
}: {
  turn: ChatTurn
  newest: boolean
  outcomes: Readonly<Record<number, ActionOutcome>>
  onOutcome: (id: number, outcome: ActionOutcome) => void
}) {
  const streaming = turn.status === 'streaming'
  return (
    <article className="ira-turn">
      <div className="ira-q">
        <p className="ira-q__text">
          {turn.heard && (
            <span className="ira-q__heard" title={turn.heard.handsFree ? 'Heard hands-free' : 'Spoken and written down'}>
              <span aria-hidden="true">🎙</span>
              <span className="sr-only">Spoken: </span>
            </span>
          )}
          {turn.question}
        </p>
      </div>
      <div className={`ira-a ira-a--${turn.status}`}>
        {turn.answer ? (
          <AnswerText text={turn.answer} />
        ) : streaming ? (
          <p className="ira-a__wait">
            <span className="ira-dots" aria-hidden="true">
              <span />
              <span />
              <span />
            </span>
            {waitText(turn)}
          </p>
        ) : null}
        {turn.status === 'done' && turn.finishReason === 'length' && <p className="small-note warn ai-flush">The answer was cut at the token limit.</p>}
        <TurnActions turn={turn} newest={newest} outcomes={outcomes} onOutcome={onOutcome} />
        {turn.error && (
          <div className="alert alert--error ira-a__error" role="alert">
            {turn.error}
          </div>
        )}
        {turn.status === 'stopped' && <p className="small-note ai-flush">Stopped.</p>}
        {turn.status === 'done' && (
          <p className="ira-a__meta">
            {modelName(turn.model)} · {formatSeconds(turn.seconds)}
            {turn.callId != null && ` · call #${turn.callId}`}
          </p>
        )}
      </div>
    </article>
  )
}

/** Android's button, or the iPhone's words: how to put Ira on the home screen. */
function InstallBar({ hint, onInstall, onDismiss }: { hint: InstallHint; onInstall: () => void; onDismiss: () => void }) {
  return (
    <div className="ira-install" role="note">
      <IraGlyph className="ira-glyph ira-glyph--xs" />
      <span className="ira-install__text">
        {hint === 'prompt' ? (
          'Install Ira on this phone: it opens like an app, with “Hey Google, open Ira”.'
        ) : (
          <>
            Install Ira: tap <b>Share</b>, then <b>Add to Home Screen</b>.
          </>
        )}
      </span>
      {hint === 'prompt' && (
        <button type="button" className="btn btn--primary btn--sm" onClick={onInstall}>
          Install Ira
        </button>
      )}
      <button type="button" className="ira-iconbtn ira-iconbtn--sm" aria-label="Hide this hint" onClick={onDismiss}>
        <IconX />
      </button>
    </div>
  )
}

/** A sheet from the bottom of the screen: the agent picker and the settings. Esc or the scrim closes it. */
function Sheet({ title, onClose, children }: { title: string; onClose: () => void; children: ReactNode }) {
  const id = useId()
  const panel = useRef<HTMLDivElement>(null)
  const close = useCallback(() => onClose(), [onClose])
  useEffect(() => {
    const before = document.activeElement as HTMLElement | null
    panel.current?.focus({ preventScroll: true })
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') close()
    }
    document.addEventListener('keydown', onKey)
    document.documentElement.classList.add('ira-sheet-open')
    return () => {
      document.removeEventListener('keydown', onKey)
      document.documentElement.classList.remove('ira-sheet-open')
      before?.focus?.({ preventScroll: true })
    }
  }, [close])
  return createPortal(
    <div className="ira-sheet" onPointerDown={(e) => e.target === e.currentTarget && close()}>
      <div className="ira-sheet__panel" role="dialog" aria-modal="true" aria-labelledby={`${id}-title`} tabIndex={-1} ref={panel}>
        <div className="ira-sheet__grip" aria-hidden="true" />
        <div className="ira-sheet__head">
          <h2 className="ira-sheet__title" id={`${id}-title`}>
            {title}
          </h2>
          <button type="button" className="ira-iconbtn" aria-label="Close" onClick={close}>
            <IconX />
          </button>
        </div>
        <div className="ira-sheet__body">{children}</div>
      </div>
    </div>,
    document.body,
  )
}

function SettingsSheet({
  install,
  onClose,
  onNewConversation,
}: {
  install: { hint: InstallHint; install: () => void }
  onClose: () => void
  onNewConversation: () => void
}) {
  const { pref, setPref } = useTheme()
  const { user, logout } = useAuth()
  const standalone = isStandalone()
  return (
    <Sheet title="Settings" onClose={onClose}>
      <section className="ira-set">
        <h3 className="ira-set__head">Theme</h3>
        <div className="seg ira-seg" role="radiogroup" aria-label="Theme">
          {THEME_PREFS.map((p) => (
            <button key={p} type="button" role="radio" aria-checked={pref === p} className={`seg__btn ${pref === p ? 'is-active' : ''}`} onClick={() => setPref(p)}>
              {THEME_LABELS[p]}
            </button>
          ))}
        </div>
      </section>

      <section className="ira-set">
        <h3 className="ira-set__head">On this phone</h3>
        {standalone ? (
          <p className="ira-faint">Installed: Ira opens from the home screen. On Android, “Hey Google, open Ira” opens it.</p>
        ) : install.hint === 'prompt' ? (
          <p className="ira-set__row">
            <span className="ira-faint">Put Ira on the home screen, like an app.</span>
            <button type="button" className="btn btn--primary btn--sm" onClick={install.install}>
              Install Ira
            </button>
          </p>
        ) : (
          <p className="ira-faint">
            iPhone: tap <b>Share</b>, then <b>Add to Home Screen</b>. Android: Chrome's menu, then <b>Install app</b> or <b>Add to Home screen</b>.
          </p>
        )}
      </section>

      <PhonesCard className="ira-panel" showAppLink={false} />
      <ActionPinCard />

      <section className="ira-set">
        <h3 className="ira-set__head">Conversation</h3>
        <p className="ira-set__row">
          <button
            type="button"
            className="btn btn--sm"
            onClick={() => {
              onNewConversation()
              onClose()
            }}
          >
            New conversation
          </button>
          <a className="btn btn--ghost btn--sm" href="/ai/assistant">
            Open the console
          </a>
        </p>
        <p className="ira-set__row">
          <span className="ira-faint">Signed in as {user?.userName ?? 'admin'}.</span>
          <button type="button" className="btn btn--ghost btn--sm" onClick={() => void logout()}>
            Sign out
          </button>
        </p>
      </section>
    </Sheet>
  )
}
