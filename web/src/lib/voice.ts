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

function safeStorage(): Storage | null {
  try {
    return typeof window === 'undefined' ? null : window.localStorage
  } catch {
    return null
  }
}
