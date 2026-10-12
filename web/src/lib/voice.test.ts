import { describe, expect, it } from 'vitest'
import {
  HANDS_FREE_WINDOW_MS,
  MicUnavailable,
  NO_WAKE_CHECK,
  VoiceRefusal,
  checkWake,
  clockText,
  handsFreeHoldFor,
  handsFreeUnavailableText,
  handsFreeWait,
  micBlockedText,
  micErrorText,
  readHandsFree,
  readSpeakReplies,
  readTranscript,
  readVoiceInfo,
  recentSends,
  speak,
  stopsHandsFree,
  transcribe,
  voiceErrorText,
  voiceFailureText,
  writeHandsFree,
  writeSpeakReplies,
} from './voice'
import { encodeWav } from './wav'

/** A stand-in for the session-aware fetch: answers `response` and keeps what it was sent. */
function sender(response: () => Response) {
  const sent: { path: string; init: RequestInit }[] = []
  const send = async (path: string, init: RequestInit) => {
    sent.push({ path, init })
    return response()
  }
  return { send, sent }
}

const json = (status: number, body: unknown, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json; charset=utf-8', ...headers } })

describe('transcribe', () => {
  it('posts the WAV as audio/wav in its conversation and reads the transcript', async () => {
    const wav = encodeWav(new Int16Array(16_000))
    const { send, sent } = sender(() => json(200, { text: 'Ira, aaj ka P&L kya hai?', callId: 41, seconds: 1 }))

    const heard = await transcribe(wav, 'c-abc 1', undefined, send)

    expect(heard).toEqual({ text: 'Ira, aaj ka P&L kya hai?', callId: 41, seconds: 1 })
    expect(sent).toHaveLength(1)
    expect(sent[0].path).toBe('/api/Ai/voice/transcribe?conversationId=c-abc%201')
    expect(sent[0].init.method).toBe('POST')
    expect(new Headers(sent[0].init.headers).get('Content-Type')).toBe('audio/wav')
    expect(sent[0].init.body).toBe(wav)
  })

  it('leaves the conversation out when there is none', async () => {
    const { send, sent } = sender(() => json(200, { text: 'x', callId: 1, seconds: 1 }))
    await transcribe(new ArrayBuffer(44), '', undefined, send)
    expect(sent[0].path).toBe('/api/Ai/voice/transcribe')
  })

  it("throws the API's own words for a recording it refused", async () => {
    const { send } = sender(() => json(413, { error: 'That recording is 130 s long; the desk listens to 120 s at most. Record a shorter question, or type it.' }))

    const error = await transcribe(new ArrayBuffer(44), 'c-1', undefined, send).catch((e: unknown) => e)

    expect(error).toBeInstanceOf(VoiceRefusal)
    expect((error as VoiceRefusal).status).toBe(413)
    expect((error as VoiceRefusal).message).toBe('That recording is 130 s long; the desk listens to 120 s at most. Record a shorter question, or type it.')
  })

  it('says how long to wait when the rate limit refuses it, from Retry-After', async () => {
    const { send } = sender(() =>
      json(429, { error: 'You have asked 20 questions in the last ten minutes (the cap is 20)', retryAfterSeconds: 99 }, { 'Retry-After': '42' }),
    )

    const error = (await transcribe(new ArrayBuffer(44), 'c-1', undefined, send).catch((e: unknown) => e)) as VoiceRefusal

    expect(error.retryAfterSeconds).toBe(42)
    expect(error.message).toBe('You have asked 20 questions in the last ten minutes (the cap is 20). Try again in 42 s.')
  })

  it('reads a 502 with its call, and a 200 HTML page as an API without the route', async () => {
    const failed = (await transcribe(
      new ArrayBuffer(44),
      'c-1',
      undefined,
      sender(() => json(502, { error: 'The audio model could not make out that recording: timeout (call #9). Try again, or type the question.', callId: 9 })).send,
    ).catch((e: unknown) => e)) as VoiceRefusal
    const old = (await transcribe(
      new ArrayBuffer(44),
      'c-1',
      undefined,
      sender(() => new Response('<!doctype html><html></html>', { status: 200, headers: { 'Content-Type': 'text/html' } })).send,
    ).catch((e: unknown) => e)) as VoiceRefusal

    expect([failed.status, failed.callId]).toEqual([502, 9])
    expect(old.status).toBe(404)
    expect(old.message).toMatch(/not on the running API build/)
  })

  it('refuses a body that is not a transcript', () => {
    expect(() => readTranscript({ text: 'x' })).toThrow(VoiceRefusal)
    expect(() => readTranscript(null)).toThrow(/cannot read/)
  })
})

describe('speak', () => {
  it('posts the answer with its call and returns the MP3', async () => {
    const mp3 = new Uint8Array([0xff, 0xfb, 0x90, 0x64])
    const { send, sent } = sender(() => new Response(mp3, { status: 200, headers: { 'Content-Type': 'audio/mpeg' } }))

    const audio = await speak('Aaj desk ne **₹2,22,414** kamaye.', 77, undefined, send)

    expect(new Uint8Array(await audio.arrayBuffer())).toEqual(mp3)
    expect(sent[0].path).toBe('/api/Ai/voice/speak')
    expect(JSON.parse(sent[0].init.body as string)).toEqual({ text: 'Aaj desk ne **₹2,22,414** kamaye.', callId: 77 })
    expect(new Headers(sent[0].init.headers).get('Accept')).toBe('audio/mpeg')
  })

  it("says Polly's missing permission in the API's words, with the wait", async () => {
    const said =
      "Spoken replies are off for now: the server's AWS role may not call Amazon Polly (it needs polly:SynthesizeSpeech; docs/modules/ai.md, Voice). The desk asks Polly again in 60 min; the answer is above as text."
    const { send } = sender(() => json(503, { error: said, callId: 12, retryAfterSeconds: 3600 }, { 'Retry-After': '3600' }))

    const error = (await speak('₹42,000.', null, undefined, send).catch((e: unknown) => e)) as VoiceRefusal

    expect(error.status).toBe(503)
    expect(error.message).toBe(said)
    expect(error.retryAfterSeconds).toBe(3600)
    expect(error.callId).toBe(12)
  })

  it('treats a 200 that is not audio as an API without the route', async () => {
    const { send } = sender(() => json(200, { ok: true }))
    const error = (await speak('x', null, undefined, send).catch((e: unknown) => e)) as VoiceRefusal
    expect(error.status).toBe(404)
  })
})

describe('the words', () => {
  it('words the statuses the API cannot', () => {
    expect(voiceErrorText('hear', 401, null, null)).toBe('Your session has expired. Please sign in again.')
    expect(voiceErrorText('speak', 403, '', null)).toBe('Only an admin may talk to the AI.')
    expect(voiceErrorText('speak', 404, { error: 'No call #5.' }, null)).toBe('No call #5.')
    expect(voiceErrorText('hear', 500, '<html>oops</html>', null)).toBe('The recording could not be written down (HTTP 500). Try again, or type the question.')
    expect(voiceErrorText('speak', 500, null, null)).toBe('The answer could not be spoken (HTTP 500). It is above as text.')
    expect(voiceErrorText('hear', 429, null, null)).toBe('The AI is rate-limited or busy. Try again in a minute.')
  })

  it('says why the mic did not start, and what to do', () => {
    const named = (name: string) => Object.assign(new Error('x'), { name })
    expect(micErrorText(named('NotAllowedError'))).toMatch(/^The microphone is blocked for this site\. Allow it/)
    expect(micErrorText(named('NotFoundError'))).toMatch(/^No microphone was found\./)
    expect(micErrorText(named('NotReadableError'))).toMatch(/another app may be using it/)
    expect(micErrorText(new MicUnavailable())).toMatch(/only offered to pages on https/)
    expect(micErrorText(new Error('weird'))).toBe('The microphone could not start: weird. Type the question instead.')
  })

  it('says a network failure in words', () => {
    expect(voiceFailureText('hear', new TypeError('Failed to fetch'))).toMatch(/^The recording did not reach the API: Failed to fetch\./)
    expect(voiceFailureText('speak', new VoiceRefusal(503, 'Off.'))).toBe('Off.')
  })

  it('shows a recording clock as minutes and seconds', () => {
    expect([0, 7.9, 65, 120, -3, Number.NaN].map(clockText)).toEqual(['0:00', '0:07', '1:05', '2:00', '0:00', '0:00'])
  })
})

describe('the Speak replies choice', () => {
  it('is off by default and remembered when turned on', () => {
    const kept = new Map<string, string>()
    const storage = {
      getItem: (k: string) => kept.get(k) ?? null,
      setItem: (k: string, v: string) => void kept.set(k, v),
      removeItem: (k: string) => void kept.delete(k),
    }
    expect(readSpeakReplies(storage)).toBe(false)
    writeSpeakReplies(true, storage)
    expect(readSpeakReplies(storage)).toBe(true)
    writeSpeakReplies(false, storage)
    expect(readSpeakReplies(storage)).toBe(false)
  })

  it('survives storage that throws (a private window, blocked site data)', () => {
    const broken = {
      getItem: () => {
        throw new Error('SecurityError')
      },
      setItem: () => {
        throw new Error('QuotaExceededError')
      },
      removeItem: () => {
        throw new Error('SecurityError')
      },
    }
    expect(readSpeakReplies(broken)).toBe(false)
    expect(() => writeSpeakReplies(true, broken)).not.toThrow()
    expect(readSpeakReplies(null)).toBe(false)
  })
})

describe('hands-free: the wake check', () => {
  it('posts the utterance with wake=1 in its conversation and reads whether it was addressed', async () => {
    const wav = encodeWav(new Int16Array(16_000))
    const { send, sent } = sender(() =>
      json(200, { text: 'Hey Ira, aaj ka P&L kya hai?', addressed: true, question: 'Aaj ka P&L kya hai?', callId: 42, seconds: 1.4 }),
    )

    const heard = await checkWake(wav, 'c-abc 1', undefined, send)

    expect(heard).toEqual({ text: 'Hey Ira, aaj ka P&L kya hai?', addressed: true, question: 'Aaj ka P&L kya hai?', callId: 42, seconds: 1.4 })
    expect(sent[0].path).toBe('/api/Ai/voice/transcribe?wake=1&conversationId=c-abc%201')
    expect(new Headers(sent[0].init.headers).get('Content-Type')).toBe('audio/wav')
    expect(sent[0].init.body).toBe(wav)
  })

  it('reads an utterance that was not addressed', async () => {
    const { send, sent } = sender(() => json(200, { text: 'Haan bhai.', addressed: false, question: '', callId: 43, seconds: 1 }))
    const heard = await checkWake(new ArrayBuffer(44), '', undefined, send)
    expect(heard.addressed).toBe(false)
    expect(heard.question).toBe('')
    expect(sent[0].path).toBe('/api/Ai/voice/transcribe?wake=1')
  })

  it('says so when the API answers without a wake check (a build from before hands-free)', async () => {
    const { send } = sender(() => json(200, { text: 'Ira, P&L?', callId: 44, seconds: 1 }))
    const error = await checkWake(new ArrayBuffer(44), 'c-1', undefined, send).catch((e: unknown) => e)
    expect(error).toBeInstanceOf(VoiceRefusal)
    expect((error as VoiceRefusal).status).toBe(404)
    expect((error as VoiceRefusal).message).toBe(NO_WAKE_CHECK)
    expect(stopsHandsFree(error)).toBe(true)
  })

  it('carries the budget refusal with its wait, which pauses hands-free rather than stopping it', async () => {
    const { send } = sender(() =>
      json(429, { error: 'Hands-free has sent 6 utterances to be written down in the last minute.', retryAfterSeconds: 40 }, { 'Retry-After': '40' }),
    )
    const error = (await checkWake(new ArrayBuffer(44), 'c-1', undefined, send).catch((e: unknown) => e)) as VoiceRefusal
    expect(error.status).toBe(429)
    expect(error.retryAfterSeconds).toBe(40)
    expect(stopsHandsFree(error)).toBe(false)
  })

  it('stops for voice switched off, a signed-out session or a missing endpoint, and not for a model that could not hear', () => {
    expect(stopsHandsFree(new VoiceRefusal(503, 'Voice is switched off on this desk.'))).toBe(true)
    expect(stopsHandsFree(new VoiceRefusal(401, 'x'))).toBe(true)
    expect(stopsHandsFree(new VoiceRefusal(403, 'x'))).toBe(true)
    expect(stopsHandsFree(new VoiceRefusal(502, 'The audio model could not make out what was said.'))).toBe(false)
    expect(stopsHandsFree(new TypeError('Failed to fetch'))).toBe(false)
  })
})

describe('hands-free: the voice and the budget', () => {
  it("reads the API's description of the voice", () => {
    expect(readVoiceInfo({ name: 'Kavya', enabled: true, speakReplies: false, handsFreePerMinute: 4 })).toEqual({
      name: 'Kavya',
      enabled: true,
      speakReplies: false,
      handsFreePerMinute: 4,
    })
    expect(readVoiceInfo({ name: ' ', enabled: false, speakReplies: true, handsFreePerMinute: -2 })).toEqual({
      name: 'Ira',
      enabled: false,
      speakReplies: true,
      handsFreePerMinute: 0,
    })
    expect(() => readVoiceInfo('<!doctype html>')).toThrow(VoiceRefusal)
  })

  it('waits only once the last minute holds the budget, and then until the oldest that counts ages out', () => {
    const now = 1_000_000
    expect(handsFreeWait([], now, 6)).toBe(0)
    expect(handsFreeWait([now - 50_000, now - 10_000], now, 3)).toBe(0)
    expect(handsFreeWait([now - 50_000, now - 20_000, now - 10_000], now, 3)).toBe(10_000)
    expect(handsFreeWait([now - 61_000, now - 20_000, now - 10_000], now, 3)).toBe(0)
    expect(handsFreeWait([now - 40_000, now - 30_000, now - 20_000, now - 5_000], now, 3)).toBe(30_000)
    expect(handsFreeWait([], now, 0)).toBe(HANDS_FREE_WINDOW_MS)
    expect(recentSends([now - 70_000, now - 30_000, now + 5], now)).toEqual([now - 30_000])
  })

  it('remembers hands-free per viewer, off by default, and survives storage that throws', () => {
    const kept = new Map<string, string>()
    const storage = {
      getItem: (k: string) => kept.get(k) ?? null,
      setItem: (k: string, v: string) => void kept.set(k, v),
      removeItem: (k: string) => void kept.delete(k),
    }
    expect(readHandsFree(storage)).toBe(false)
    writeHandsFree(true, storage)
    expect(readHandsFree(storage)).toBe(true)
    expect(readSpeakReplies(storage)).toBe(false)
    writeHandsFree(false, storage)
    expect(readHandsFree(storage)).toBe(false)
    const broken = {
      getItem: () => {
        throw new Error('SecurityError')
      },
      setItem: () => {
        throw new Error('QuotaExceededError')
      },
      removeItem: () => {
        throw new Error('SecurityError')
      },
    }
    expect(readHandsFree(broken)).toBe(false)
    expect(() => writeHandsFree(true, broken)).not.toThrow()
  })
})

// The conversation engine both the Assistant page and the Ira app share (pages/ai/useAssistantSession) decides with these.
describe('the shared session’s rules', () => {
  const quiet = { playback: 'idle' as const, answering: false, recording: false, blocked: false }

  it('holds hands-free while Ira speaks, an answer comes, the mic is in use, or nobody can be asked; most pressing first', () => {
    expect(handsFreeHoldFor(quiet)).toBeNull()
    expect(handsFreeHoldFor({ ...quiet, playback: 'fetching' })).toBe('speaking')
    expect(handsFreeHoldFor({ ...quiet, playback: 'playing', answering: true })).toBe('speaking')
    expect(handsFreeHoldFor({ ...quiet, answering: true, recording: true })).toBe('answering')
    expect(handsFreeHoldFor({ ...quiet, recording: true, blocked: true })).toBe('recording')
    expect(handsFreeHoldFor({ ...quiet, blocked: true })).toBe('blocked')
    // A reply the browser held back is not being spoken: it may listen.
    expect(handsFreeHoldFor({ ...quiet, playback: 'blocked' })).toBeNull()
  })

  it('says why the mic cannot start', () => {
    const free = { blocked: false, answering: false, hearing: false, agentName: 'Desk Assistant' }
    expect(micBlockedText(free)).toBeNull()
    expect(micBlockedText({ ...free, blocked: true, answering: true })).toBe('The Desk Assistant cannot be asked right now (see above)')
    expect(micBlockedText({ ...free, answering: true })).toBe('One question at a time: the mic is back when the answer finishes')
    expect(micBlockedText({ ...free, hearing: true })).toBe('Writing the last recording down…')
  })

  it('says why hands-free cannot be turned on', () => {
    const info = { name: 'Ira', enabled: true, speakReplies: true, handsFreePerMinute: 6 }
    expect(handsFreeUnavailableText({ failed: false, info })).toBeNull()
    expect(handsFreeUnavailableText({ failed: true, info: null })).toContain('latest API build')
    expect(handsFreeUnavailableText({ failed: false, info: null })).toBe("Checking the desk's voice…")
    expect(handsFreeUnavailableText({ failed: false, info: { ...info, enabled: false } })).toContain('Ai:Voice:Enabled')
    expect(handsFreeUnavailableText({ failed: false, info: { ...info, handsFreePerMinute: 0 } })).toContain('HandsFreePerMinute is 0')
  })
})
