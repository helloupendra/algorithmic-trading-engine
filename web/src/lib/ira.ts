/**
 * Ira on the phone (12 Oct 2026): the owner asks the desk like Siri or Gemini.
 *
 * Two ways in, both admin only:
 *   - the Ira app, /ira: an installable full-screen page (a web app manifest of
 *     its own, a service worker for the shell) with a big mic, hands-free and
 *     spoken replies on by default; the same conversation engine as AI →
 *     Assistant (pages/ai/useAssistantSession);
 *   - "Hey Siri, Ira": an Apple Shortcut that dictates the question and posts
 *     it to POST /api/Ira/ask with a phone token made here, then speaks the
 *     answer's `spoken` line. A token is shown once; the desk keeps only its
 *     SHA-256 and it opens that one endpoint alone.
 *
 * This module is the logic both share and the tests read: the route's gate,
 * the install hint, the speak-replies choice, the phone tokens and the actions
 * a phone question proposed.
 */

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from './api'
import type { ProposedAction } from './aiActions'

// ---------- the route's gate ----------------------------------------------------

/** What /ira does with this visitor: wait for the session, send them to sign in, turn away a non-admin, or open. */
export type IraGate = 'wait' | 'login' | 'forbidden' | 'open'

export function iraGate(auth: { isLoading: boolean; isAuthenticated: boolean; isAdmin: boolean }): IraGate {
  if (auth.isLoading) return 'wait'
  if (!auth.isAuthenticated) return 'login'
  return auth.isAdmin ? 'open' : 'forbidden'
}

/**
 * What the gate hands the console's sign-in page, as RequireAuth does: where to
 * come back to (lib/modules returnTarget reads it). An iPhone's home-screen app
 * has storage of its own, so its first launch always passes through here.
 */
export function loginState(location: { pathname: string; search?: string; hash?: string }): { from: { pathname: string; search: string; hash: string } } {
  return { from: { pathname: location.pathname, search: location.search ?? '', hash: location.hash ?? '' } }
}

// ---------- what Ira is doing, for the line under her name ----------------------------

export type IraActivity = 'offline' | 'recording' | 'hearing' | 'speaking' | 'thinking' | 'answering' | 'listening' | 'ready'

/** One word for the header, most pressing first: no network, the mic, the transcript, her voice, the answer, hands-free. */
export function iraActivity(s: {
  online: boolean
  recording: boolean
  hearing: boolean
  playback: 'idle' | 'fetching' | 'playing' | 'blocked'
  streaming: boolean
  answerStarted: boolean
  handsFreeListening: boolean
}): IraActivity {
  if (!s.online) return 'offline'
  if (s.recording) return 'recording'
  if (s.hearing) return 'hearing'
  if (s.playback === 'fetching' || s.playback === 'playing') return 'speaking'
  if (s.streaming) return s.answerStarted ? 'answering' : 'thinking'
  if (s.handsFreeListening) return 'listening'
  return 'ready'
}

export function iraActivityText(activity: IraActivity, name: string): string {
  switch (activity) {
    case 'offline':
      return 'Offline: no network'
    case 'recording':
      return 'Recording'
    case 'hearing':
      return 'Writing it down…'
    case 'speaking':
      return 'Speaking'
    case 'thinking':
      return 'Reading the desk…'
    case 'answering':
      return 'Answering…'
    case 'listening':
      return `Listening for “${name}”`
    case 'ready':
      return 'Ready'
  }
}

// ---------- installing it ------------------------------------------------------------

/** What the page offers: nothing, Android's install button (beforeinstallprompt), or the iPhone's Share → Add to Home Screen. */
export type InstallHint = 'none' | 'prompt' | 'ios'

export interface InstallEnv {
  /** Already opened from the home screen (display-mode standalone, or iOS's navigator.standalone). */
  standalone: boolean
  /** The browser handed over its install prompt (Chrome, Edge, Samsung Internet). */
  promptReady: boolean
  userAgent: string
  maxTouchPoints: number
  /** The viewer closed the hint before. */
  dismissed: boolean
}

/** An iPhone, iPod or iPad, an iPad that reports itself as a Mac included (it has touch). */
export function isIos(userAgent: string, maxTouchPoints: number): boolean {
  if (/iPhone|iPad|iPod/i.test(userAgent)) return true
  return /Macintosh/i.test(userAgent) && maxTouchPoints > 1
}

export function installHint(env: InstallEnv): InstallHint {
  if (env.standalone || env.dismissed) return 'none'
  if (env.promptReady) return 'prompt'
  // Every browser on iOS adds to the home screen through the Share sheet; none fires beforeinstallprompt.
  if (isIos(env.userAgent, env.maxTouchPoints)) return 'ios'
  return 'none'
}

/** Whether this page was opened as the installed app. */
export function isStandalone(win: Pick<Window, 'matchMedia' | 'navigator'> | undefined = typeof window === 'undefined' ? undefined : window): boolean {
  if (!win) return false
  try {
    if (win.matchMedia?.('(display-mode: standalone)').matches) return true
  } catch {
    /* no media queries: fall through */
  }
  return (win.navigator as Navigator & { standalone?: boolean }).standalone === true
}

const INSTALL_DISMISSED_KEY = 'openfno.ira.install-hint'
const SPEAK_KEY = 'openfno.ira.speak'

type Store = Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>

function storage(): Store | null {
  try {
    return typeof window === 'undefined' ? null : window.localStorage
  } catch {
    return null
  }
}

export function readInstallDismissed(store: Pick<Storage, 'getItem'> | null = storage()): boolean {
  try {
    return store?.getItem(INSTALL_DISMISSED_KEY) === '1'
  } catch {
    return false
  }
}

export function writeInstallDismissed(store: Pick<Storage, 'setItem'> | null = storage()): void {
  try {
    store?.setItem(INSTALL_DISMISSED_KEY, '1')
  } catch {
    /* storage refused: the hint comes back next time */
  }
}

/** Spoken replies in the Ira app: on unless the viewer turned them off (the console's Assistant keeps its own choice, off by default). */
export function readIraSpeak(store: Pick<Storage, 'getItem'> | null = storage()): boolean {
  try {
    return store?.getItem(SPEAK_KEY) !== '0'
  } catch {
    return true
  }
}

export function writeIraSpeak(on: boolean, store: Pick<Storage, 'setItem' | 'removeItem'> | null = storage()): void {
  try {
    if (on) store?.removeItem(SPEAK_KEY)
    else store?.setItem(SPEAK_KEY, '0')
  } catch {
    /* storage refused: the choice lives as long as the page */
  }
}

// ---------- phone tokens ("Hey Siri, Ira") -----------------------------------------

export interface PhoneToken {
  id: number
  label: string
  createdUtc: string
  lastUsedUtc: string | null
}

export interface PhonesStatus {
  enabled: boolean
  maxTokens: number
  tokens: PhoneToken[]
}

/** A token just made: the only time the token itself is ever on screen. */
export interface PhoneTokenMade extends PhoneToken {
  token: string
}

function record(value: unknown): Record<string, unknown> | null {
  return value && typeof value === 'object' && !Array.isArray(value) ? (value as Record<string, unknown>) : null
}

function readToken(raw: unknown): PhoneToken | null {
  const o = record(raw)
  if (!o || typeof o.id !== 'number') return null
  return {
    id: o.id,
    label: typeof o.label === 'string' ? o.label : '',
    createdUtc: typeof o.createdUtc === 'string' ? o.createdUtc : '',
    lastUsedUtc: typeof o.lastUsedUtc === 'string' ? o.lastUsedUtc : null,
  }
}

export function readPhones(raw: unknown): PhonesStatus {
  const o = record(raw) ?? {}
  return {
    enabled: o.enabled !== false,
    maxTokens: typeof o.maxTokens === 'number' ? o.maxTokens : 5,
    tokens: Array.isArray(o.tokens) ? o.tokens.map(readToken).filter((t): t is PhoneToken => t != null) : [],
  }
}

export function readPhoneMade(raw: unknown): PhoneTokenMade {
  const t = readToken(raw)
  const token = record(raw)?.token
  if (!t || typeof token !== 'string' || !token) throw new Error('The API did not return the token.')
  return { ...t, token }
}

/** A phone's name: up to 60 characters, not blank. */
export function phoneLabelProblem(label: string): string | null {
  const t = label.trim()
  if (!t) return 'Name the phone, for example "Upendra\'s iPhone".'
  if (t.length > 60) return 'A phone\'s name is 60 characters at most.'
  return null
}

/** The address the Shortcut posts to, on the console's own origin (https://www.openfno.com on the desk). */
export function iraAskUrl(origin: string): string {
  return `${origin.replace(/\/+$/, '')}/api/Ira/ask`
}

/** One step of the Apple Shortcut, as the Phones box lists it: what to add, and how to set it. */
export interface ShortcutStep {
  action: string
  detail: string
}

/** The Shortcut, exactly: dictate, post, read `spoken`, say it. "Hey Siri, Ira" then runs it. */
export function shortcutSteps(askUrl: string): ShortcutStep[] {
  return [
    { action: 'New Shortcut', detail: 'Name it "Ira". Siri runs a shortcut by its name.' },
    { action: 'Dictate Text', detail: 'Language: English (India), or Hindi. Stop listening: After Pause.' },
    {
      action: 'Get Contents of URL',
      detail: `${askUrl} · Method POST · Headers: Authorization = Bearer <your token> · Request Body JSON: text = Dictated Text`,
    },
    { action: 'Get Dictionary Value', detail: 'Get Value for spoken in Contents of URL.' },
    { action: 'Speak Text', detail: 'Speak Dictionary Value.' },
  ]
}

export function usePhones(enabled = true) {
  return useQuery({
    queryKey: ['ira', 'phones'],
    queryFn: async () => readPhones(await api.get<unknown>('/api/Ira/phones')),
    enabled,
    retry: 1,
  })
}

export function useCreatePhone() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (label: string) => readPhoneMade(await api.post<unknown>('/api/Ira/phones', { label: label.trim() })),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['ira', 'phones'] }),
  })
}

export function useRevokePhone() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (id: number) => {
      await api.delete<unknown>(`/api/Ira/phones/${id}`)
    },
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['ira', 'phones'] }),
  })
}

// ---------- actions a phone question proposed ---------------------------------------

/** The actions waiting for the PIN that a phone question proposed, in the confirm card's shape. */
export function readPhoneActions(raw: unknown): ProposedAction[] {
  if (!Array.isArray(raw)) return []
  return raw
    .map(record)
    .filter((o): o is Record<string, unknown> => o != null && typeof o.id === 'number' && typeof o.summary === 'string')
    .map((o) => ({
      id: o.id as number,
      kind: typeof o.kind === 'string' ? o.kind : '',
      summary: o.summary as string,
      expiresUtc: typeof o.expiresUtc === 'string' ? o.expiresUtc : null,
    }))
}

/** GET /api/Ira/actions, every 15 s while the app is open: "Hey Siri, Ira, stop the Ghost run" waits here for the PIN. */
export function usePhoneActions(enabled: boolean) {
  return useQuery({
    queryKey: ['ira', 'actions'],
    queryFn: async () => readPhoneActions(await api.get<unknown>('/api/Ira/actions')),
    enabled,
    refetchInterval: 15_000,
    refetchOnWindowFocus: true,
    retry: 1,
  })
}
