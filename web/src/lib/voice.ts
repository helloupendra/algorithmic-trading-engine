/**
 * Talking to the desk on AI → Assistant ("Ira", the voice Telegram has too):
 * the page-free half, so it can be tested.
 *
 *   - transcribe: a recording (16 kHz mono WAV, made by wav.ts) to
 *     POST /api/Ai/voice/transcribe, which writes it down with the same audio
 *     model, words and name as Telegram's voice notes. The page then asks the
 *     transcript exactly as a typed question.
 *   - speak: an answer to POST /api/Ai/voice/speak, which returns the short
 *     spoken version as MP3 (Amazon Polly).
 *   - hands-free: checkWake sends one utterance with wake=1, and the API says
 *     whether it was said to the assistant (its name first) and what it asks;
 *     readVoiceInfo reads the name and the budget; handsFreeWait keeps the
 *     page under the budget (Ai:Voice:HandsFreePerMinute).
 *   - the words for every way either can fail, the microphone's included, and
 *     the viewer's "Speak replies" choice, kept in this browser only.
 *
 * Neither call keeps audio anywhere: the API stores the transcript and a line
 * describing the recording, never the recording.
 */

import { ApiError, fetchWithSession } from './api'
import { bodyCallId, bodyRetryAfter, retryAfterSeconds, serverText } from './ai'

/** Ai:Voice:MaxSeconds' default: the recorder stops there and sends. The API holds its own limit. */
export const VOICE_MAX_SECONDS = 120

/** A take held down shorter than this was a click, not push-to-talk: recording carries on. */
export const HOLD_MS = 400

export interface VoiceTranscript {
  text: string
  /** The transcription's own call (agent voice-transcribe) on the Calls tab. */
  callId: number
  /** The recording's length, as the API measured it. */
  seconds: number
}

/** The API said no to a recording or a spoken reply, in words. */
export class VoiceRefusal extends Error {
  readonly status: number
  readonly retryAfterSeconds: number | null
  readonly callId: number | null

  constructor(status: number, message: string, retryAfterSeconds: number | null = null, callId: number | null = null) {
    super(message)
    this.name = 'VoiceRefusal'
    this.status = status
    this.retryAfterSeconds = retryAfterSeconds
    this.callId = callId
  }
}

type Send = (path: string, init: RequestInit) => Promise<Response>

const withStop = (s: string) => (/[.!?]$/.test(s) ? s : `${s}.`)

/**
 * What a refused recording (`hear`) or spoken reply (`speak`) says on screen.
 * The API words its own refusals (too long, not WAV, Polly not permitted), so
 * its words come first; the statuses it cannot word are worded here.
 */
export function voiceErrorText(kind: 'hear' | 'speak', status: number, body: unknown, retryAfter: number | null): string {
  const said = serverText(body)
  switch (status) {
    case 401:
      return 'Your session has expired. Please sign in again.'
    case 403:
      return 'Only an admin may talk to the AI.'
    case 404:
      return said && kind === 'speak' && /^No call/.test(said)
        ? withStop(said)
        : 'The voice endpoints are not on the running API build. Restart the API to pick up the latest code.'
    case 429: {
      const wait = retryAfter != null ? ` Try again in ${retryAfter} s.` : ' Try again in a minute.'
      return `${said ? withStop(said) : 'The AI is rate-limited or busy.'}${wait}`
    }
    default:
      if (said) return withStop(said)
      return kind === 'hear'
        ? `The recording could not be written down (HTTP ${status}). Try again, or type the question.`
        : `The answer could not be spoken (HTTP ${status}). It is above as text.`
  }
}

async function refusal(kind: 'hear' | 'speak', response: Response): Promise<VoiceRefusal> {
  const text = await response.text().catch(() => '')
  let parsed: unknown = text
  try {
    parsed = text ? JSON.parse(text) : null
  } catch {
    /* plain text */
  }
  // A 200 that is not what was asked for is the SPA's index.html: the running API does not know the route.
  const status = response.ok ? 404 : response.status
  const wait = retryAfterSeconds(response.headers.get('Retry-After'), Date.now()) ?? bodyRetryAfter(parsed)
  return new VoiceRefusal(status, voiceErrorText(kind, status, parsed, wait), wait, bodyCallId(parsed))
}

/** The API's { text, callId, seconds }, or an error when the body is not that. */
export function readTranscript(raw: unknown): VoiceTranscript {
  const r = (raw ?? {}) as Record<string, unknown>
  if (typeof r.text !== 'string' || typeof r.callId !== 'number' || typeof r.seconds !== 'number') {
    throw new VoiceRefusal(502, 'The API answered the recording in a shape this page cannot read.')
  }
  return { text: r.text, callId: r.callId, seconds: r.seconds }
}

/**
 * A recording written down: POST /api/Ai/voice/transcribe with the WAV as the
 * body. `conversationId` puts the transcription's call in the conversation it
 * was asked in. Rejects with a VoiceRefusal in words, or with the fetch's
 * AbortError when `signal` stops it.
 */
export async function transcribe(
  wav: ArrayBuffer | Blob,
  conversationId: string,
  signal?: AbortSignal,
  send: Send = fetchWithSession,
): Promise<VoiceTranscript> {
  const query = conversationId ? `?conversationId=${encodeURIComponent(conversationId)}` : ''
  const response = await send(`/api/Ai/voice/transcribe${query}`, {
    method: 'POST',
    headers: { 'Content-Type': 'audio/wav', Accept: 'application/json' },
    body: wav,
    signal,
  })
  const type = response.headers.get('Content-Type') ?? ''
  if (!response.ok || !type.includes('json')) throw await refusal('hear', response)
  return readTranscript(await response.json())
}

/** A hands-free utterance written down and checked for the assistant's name. */
export interface WakeTranscript extends VoiceTranscript {
  /** Said to the assistant: its name first, after an optional "hey", "ok", "suno" or "arre". */
  addressed: boolean
  /** The transcript without the greeting and the name; empty when not addressed, or when the name was all that was said. */
  question: string
}

/** Said when the API answers a wake check without saying whether it was addressed: a build from before hands-free. */
export const NO_WAKE_CHECK = 'The running API cannot listen hands-free yet: it does not check for the name. Restart the API to pick up the latest code.'

/** The API's { text, addressed, question, callId, seconds }, or an error when the body is not that. */
export function readWakeTranscript(raw: unknown): WakeTranscript {
  const heard = readTranscript(raw)
  const r = raw as Record<string, unknown>
  if (typeof r.addressed !== 'boolean') throw new VoiceRefusal(404, NO_WAKE_CHECK)
  return { ...heard, addressed: r.addressed, question: typeof r.question === 'string' ? r.question : '' }
}

/**
 * A hands-free utterance written down with the wake check: POST
 * /api/Ai/voice/transcribe?wake=1. The API, not the page, decides whether it
 * was said to the assistant, by the name it has now. A 429 is the hands-free
 * budget (Ai:Voice:HandsFreePerMinute), with Retry-After.
 */
export async function checkWake(
  wav: ArrayBuffer | Blob,
  conversationId: string,
  signal?: AbortSignal,
  send: Send = fetchWithSession,
): Promise<WakeTranscript> {
  const query = `?wake=1${conversationId ? `&conversationId=${encodeURIComponent(conversationId)}` : ''}`
  const response = await send(`/api/Ai/voice/transcribe${query}`, {
    method: 'POST',
    headers: { 'Content-Type': 'audio/wav', Accept: 'application/json' },
    body: wav,
    signal,
  })
  const type = response.headers.get('Content-Type') ?? ''
  if (!response.ok || !type.includes('json')) throw await refusal('hear', response)
  return readWakeTranscript(await response.json())
}

/** The voice as the API describes it (GET /api/Ai/voice). */
export interface VoiceInfo {
  /** What the assistant answers to now: "Ira" unless it was renamed with /name on Telegram. */
  name: string
  enabled: boolean
  speakReplies: boolean
  /** Hands-free utterances the desk writes down a minute for one user; 0: hands-free is off. */
  handsFreePerMinute: number
}

/** GET /api/Ai/voice's body, or an error when it is not that. */
export function readVoiceInfo(raw: unknown): VoiceInfo {
  const r = (raw ?? {}) as Record<string, unknown>
  if (typeof r.name !== 'string' || typeof r.enabled !== 'boolean' || typeof r.handsFreePerMinute !== 'number') {
    throw new VoiceRefusal(404, 'The running API does not describe its voice yet. Restart the API to pick up the latest code.')
  }
  return {
    name: r.name.trim() || 'Ira',
    enabled: r.enabled,
    speakReplies: r.speakReplies !== false,
    handsFreePerMinute: Math.max(0, Math.floor(r.handsFreePerMinute)),
  }
}

/** The window the hands-free budget counts in. */
export const HANDS_FREE_WINDOW_MS = 60_000

/** The sends still inside the budget's window at `now`. */
export function recentSends(sentAt: readonly number[], now: number): number[] {
  return sentAt.filter((t) => t > now - HANDS_FREE_WINDOW_MS && t <= now)
}

/**
 * How long until one more utterance may be sent under `perMinute` a minute,
 * given when the last ones were: 0 when it may go now. The page holds itself
 * to the API's budget so it pauses ("too much speech") before the API has to
 * refuse; the API still enforces it.
 */
export function handsFreeWait(sentAt: readonly number[], now: number, perMinute: number): number {
  if (perMinute <= 0) return HANDS_FREE_WINDOW_MS
  const recent = recentSends(sentAt, now).sort((a, b) => a - b)
  if (recent.length < perMinute) return 0
  return recent[recent.length - perMinute] + HANDS_FREE_WINDOW_MS - now
}

/** Why the page wants hands-free quiet for now: Ira speaking, an answer coming, the mic in use, or nobody to ask. */
export type HandsFreeHold = 'speaking' | 'answering' | 'recording' | 'blocked'

/**
 * The hold a conversation puts on hands-free, in order of what matters most: Ira
 * speaking (it must not hear itself), an answer streaming (in any agent's
 * conversation), the mic button in use or its recording being written down,
 * and an agent that cannot be asked. Null: it may listen.
 */
export function handsFreeHoldFor(s: {
  playback: 'idle' | 'fetching' | 'playing' | 'blocked'
  answering: boolean
  recording: boolean
  blocked: boolean
}): HandsFreeHold | null {
  if (s.playback === 'fetching' || s.playback === 'playing') return 'speaking'
  if (s.answering) return 'answering'
  if (s.recording) return 'recording'
  if (s.blocked) return 'blocked'
  return null
}

/** Why the mic cannot start now, in words for its title; null when it can. */
export function micBlockedText(s: { blocked: boolean; answering: boolean; hearing: boolean; agentName: string }): string | null {
  if (s.blocked) return `The ${s.agentName} cannot be asked right now (see above)`
  if (s.answering) return 'One question at a time: the mic is back when the answer finishes'
  if (s.hearing) return 'Writing the last recording down…'
  return null
}

/** Why hands-free cannot be turned on here, for its title; null when it can. */
export function handsFreeUnavailableText(voice: { failed: boolean; info: VoiceInfo | null }): string | null {
  if (voice.failed) return 'Hands-free needs the latest API build: restart the API to pick up the code'
  if (!voice.info) return "Checking the desk's voice…"
  if (!voice.info.enabled) return 'Voice is switched off on this desk (Ai:Voice:Enabled)'
  if (voice.info.handsFreePerMinute <= 0) return 'Hands-free is switched off on this desk (Ai:Voice:HandsFreePerMinute is 0)'
  return null
}

/** A refusal that hands-free cannot listen past: signed out, not allowed, not on this API, or voice switched off. */
export function stopsHandsFree(error: unknown): boolean {
  return (error instanceof VoiceRefusal || error instanceof ApiError) && [401, 403, 404, 503].includes(error.status)
}

/**
 * An answer spoken: POST /api/Ai/voice/speak with its text (and its call, so
 * the spoken reply's row sits in the same conversation). Resolves with the
 * MP3; rejects with a VoiceRefusal in words (503: Polly not permitted, or
 * spoken replies switched off), or with the fetch's AbortError.
 */
export async function speak(text: string, callId: number | null, signal?: AbortSignal, send: Send = fetchWithSession): Promise<Blob> {
  const response = await send('/api/Ai/voice/speak', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'audio/mpeg' },
    body: JSON.stringify({ text, callId }),
    signal,
  })
  const type = response.headers.get('Content-Type') ?? ''
  if (!response.ok || !type.startsWith('audio/')) throw await refusal('speak', response)
  return response.blob()
}

/** Any failure to hear or speak, in words. */
export function voiceFailureText(kind: 'hear' | 'speak', error: unknown): string {
  if (error instanceof VoiceRefusal || error instanceof ApiError) return error.message
  const message = error instanceof Error ? error.message : String(error)
  return kind === 'hear'
    ? `The recording did not reach the API: ${message}. Check the connection, then try again or type the question.`
    : `The spoken reply did not arrive: ${message}. The answer is above as text.`
}

/** Thrown where the browser offers no microphone at all (no navigator.mediaDevices: plain http, or an old browser). */
export class MicUnavailable extends Error {
  constructor() {
    super('navigator.mediaDevices is not available')
    this.name = 'MicUnavailable'
  }
}

/** Why the microphone did not start, in words, with what to do about it. */
export function micErrorText(error: unknown): string {
  const name = (error as { name?: string } | null)?.name ?? ''
  switch (name) {
    case 'MicUnavailable':
      return 'This browser cannot record here: the microphone is only offered to pages on https (or localhost). Type the question instead.'
    case 'NotAllowedError':
    case 'PermissionDeniedError':
    case 'SecurityError':
      return 'The microphone is blocked for this site. Allow it in the browser (the icon at the left of the address bar), then press the mic again.'
    case 'NotFoundError':
    case 'DevicesNotFoundError':
    case 'OverconstrainedError':
      return 'No microphone was found. Connect one, or choose one in the system sound settings, then press the mic again.'
    case 'NotReadableError':
    case 'TrackStartError':
    case 'AbortError':
      return 'The microphone could not start: another app may be using it. Close that app, then press the mic again.'
    default: {
      const message = error instanceof Error ? error.message : String(error)
      return `The microphone could not start${message ? `: ${withStop(message)}` : '.'} Type the question instead.`
    }
  }
}

/** Seconds as a recording's clock: 0:07, 1:05, 2:00. */
export function clockText(seconds: number): string {
  const s = Math.max(0, Math.floor(Number.isFinite(seconds) ? seconds : 0))
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`
}

const SPEAK_KEY = 'openfno.ai.speak'

/** Whether this viewer wants answers spoken: off unless they turned it on, in this browser. */
export function readSpeakReplies(storage: Pick<Storage, 'getItem'> | null = safeStorage()): boolean {
  try {
    return storage?.getItem(SPEAK_KEY) === '1'
  } catch {
    return false
  }
}

/** Remembers the viewer's choice where storage allows; a private window just forgets it. */
export function writeSpeakReplies(on: boolean, storage: Pick<Storage, 'setItem' | 'removeItem'> | null = safeStorage()): void {
  try {
    if (on) storage?.setItem(SPEAK_KEY, '1')
    else storage?.removeItem(SPEAK_KEY)
  } catch {
    /* storage refused: the choice lives as long as the page */
  }
}

const HANDS_FREE_KEY = 'openfno.ai.handsfree'

/** Whether this viewer left hands-free on: off unless they turned it on, in this browser. */
export function readHandsFree(storage: Pick<Storage, 'getItem'> | null = safeStorage()): boolean {
  try {
    return storage?.getItem(HANDS_FREE_KEY) === '1'
  } catch {
    return false
  }
}

/** Remembers hands-free on or off where storage allows; a private window just forgets it. */
export function writeHandsFree(on: boolean, storage: Pick<Storage, 'setItem' | 'removeItem'> | null = safeStorage()): void {
  try {
    if (on) storage?.setItem(HANDS_FREE_KEY, '1')
    else storage?.removeItem(HANDS_FREE_KEY)
  } catch {
    /* storage refused: the choice lives as long as the page */
  }
}

function safeStorage(): Storage | null {
  try {
    return typeof window === 'undefined' ? null : window.localStorage
  } catch {
    return null
  }
}
