import { describe, expect, it } from 'vitest'

import {
  AskRefusal,
  agentStatus,
  aiCallsQuery,
  askErrorText,
  attemptLabel,
  callTime,
  catalogGroups,
  chainProblem,
  chatReducer,
  decodeStreamEvent,
  fallbackNote,
  formatSeconds,
  formatTokens,
  historyFor,
  initialChat,
  moveInChain,
  modelName,
  newConversationId,
  outcomeBadge,
  parseAnswer,
  parseInline,
  parseSse,
  readCallsPage,
  readModels,
  readOverview,
  retryAfterSeconds,
  prettyJson,
  readToolStep,
  rowsText,
  clockTime,
  streamAsk,
  toolLabel,
  toolsLine,
  turnReasoning,
  confidenceText,
  directionBadge,
  firstParagraph,
  readNewsEvent,
  readReport,
  readReportsPage,
  readTradeReview,
  reportLink,
  reportStatus,
  reportSubjectLabel,
  reportsQuery,
  urgencyBadge,
  validityTone,
  verdictBadge,
  botLink,
  checkScoreText,
  countdownText,
  docsLink,
  healthBadge,
  readAssistantCheck,
  readDocsSearch,
  readTelegram,
  shortDate,
  sourceLabel,
  telegramState,
} from './ai'
import type { AiModel, AiStreamEvent, AiToolStep, ChatAction, ChatState, SseMessage } from './ai'

/** Feeds chunks one after another, as a stream would, and collects every message. */
function feed(chunks: string[]): { messages: SseMessage[]; carry: string } {
  let carry = ''
  const messages: SseMessage[] = []
  for (const chunk of chunks) {
    const r = parseSse(carry, chunk)
    carry = r.carry
    messages.push(...r.messages)
  }
  return { messages, carry }
}

describe('parseSse', () => {
  it('reads one whole event', () => {
    const r = parseSse('', 'event: delta\ndata: {"text":"Hi"}\n\n')
    expect(r.messages).toEqual([{ event: 'delta', data: '{"text":"Hi"}' }])
    expect(r.carry).toBe('')
  })

  it('joins an event split across chunks, wherever the split falls', () => {
    const whole = 'event: start\ndata: {"callId":41,"chain":["a","b"]}\n\nevent: delta\ndata: {"text":"OK"}\n\n'
    for (let cut = 1; cut < whole.length; cut++) {
      const r = feed([whole.slice(0, cut), whole.slice(cut)])
      expect(r.messages.map((m) => m.event)).toEqual(['start', 'delta'])
      expect(r.messages[1].data).toBe('{"text":"OK"}')
    }
    // Byte by byte.
    expect(feed([...whole]).messages).toHaveLength(2)
  })

  it('reads several events out of one chunk, and keeps the unfinished one for later', () => {
    const r = parseSse('', 'event: delta\ndata: {"text":"a"}\n\nevent: delta\ndata: {"text":"b"}\n\nevent: delta\ndata: {"te')
    expect(r.messages.map((m) => m.data)).toEqual(['{"text":"a"}', '{"text":"b"}'])
    expect(r.carry).toBe('event: delta\ndata: {"te')
  })

  it('skips the keep-alive comments, alone or inside an event', () => {
    const r = feed([': ping\n\n', 'event: delta\n: ping\ndata: {"text":"x"}\n\n', ': ping\n\n'])
    expect(r.messages).toEqual([{ event: 'delta', data: '{"text":"x"}' }])
  })

  it('reads CRLF and CR line ends, even with the CR and LF in different chunks', () => {
    expect(parseSse('', 'event: delta\r\ndata: {"text":"a"}\r\n\r\n').messages).toEqual([{ event: 'delta', data: '{"text":"a"}' }])
    // A CR at the end may be half of a CRLF, so the event waits for the next byte.
    const cr = parseSse('', 'event: delta\rdata: {"text":"b"}\r\r')
    expect(cr.messages).toEqual([])
    expect(parseSse(cr.carry, 'event: delta').messages).toEqual([{ event: 'delta', data: '{"text":"b"}' }])
    const split = feed(['event: delta\r\ndata: {"text":"c"}\r', '\n\r', '\n'])
    expect(split.messages).toEqual([{ event: 'delta', data: '{"text":"c"}' }])
    expect(split.carry).toBe('')
  })

  it('joins data lines with a newline, strips one space, and names an unnamed event "message"', () => {
    expect(parseSse('', 'data:  two spaces\ndata:none\n\n').messages).toEqual([{ event: 'message', data: ' two spaces\nnone' }])
  })

  it('ignores an event with no data, and reads a last event the server left unterminated', () => {
    expect(parseSse('', 'event: delta\n\n').messages).toEqual([])
    const r = parseSse('', 'event: done\ndata: {"callId":1}')
    expect(r.messages).toEqual([])
    expect(parseSse(r.carry, '', true).messages).toEqual([{ event: 'done', data: '{"callId":1}' }])
  })
})

describe('decodeStreamEvent', () => {
  it('reads each event the stream sends', () => {
    expect(decodeStreamEvent({ event: 'start', data: '{"callId":41,"chain":["m1","m2"]}' })).toEqual({ type: 'start', callId: 41, chain: ['m1', 'm2'] })
    expect(decodeStreamEvent({ event: 'attempt', data: '{"model":"m1","n":1,"of":3}' })).toEqual({ type: 'attempt', model: 'm1', n: 1, of: 3, round: 1 })
    expect(decodeStreamEvent({ event: 'reasoning', data: '{"text":"hmm"}' })).toEqual({ type: 'reasoning', text: 'hmm' })
    expect(decodeStreamEvent({ event: 'fallback', data: '{"model":"m1","reason":"timeout","next":"m2"}' })).toEqual({
      type: 'fallback',
      model: 'm1',
      reason: 'timeout',
      next: 'm2',
    })
    expect(
      decodeStreamEvent({
        event: 'done',
        data: '{"callId":41,"model":"m2","seconds":9.1,"finishReason":"stop","usage":{"promptTokens":23,"completionTokens":16,"totalTokens":39},"fallbacks":1}',
      }),
    ).toEqual({
      type: 'done',
      callId: 41,
      model: 'm2',
      seconds: 9.1,
      finishReason: 'stop',
      usage: { promptTokens: 23, completionTokens: 16, totalTokens: 39 },
      fallbacks: 1,
      // An API from before tools: none called, one round.
      toolCalls: 0,
      rounds: 1,
    })
    expect(decodeStreamEvent({ event: 'error', data: '{"callId":41,"error":"every model failed"}' })).toEqual({
      type: 'error',
      callId: 41,
      error: 'every model failed',
    })
  })

  it('reads a tool step and the round an attempt is for', () => {
    expect(decodeStreamEvent({ event: 'attempt', data: '{"model":"m1","n":1,"of":2,"round":3}' })).toEqual({
      type: 'attempt',
      model: 'm1',
      n: 1,
      of: 2,
      round: 3,
    })
    const sent = {
      round: 1,
      id: 'call_1',
      name: 'get_runs',
      arguments: '{"date":"today"}',
      ok: true,
      error: null,
      seconds: 0.21,
      rows: 3,
      asOfUtc: '2026-09-30T10:00:00Z',
      summary: '3 runs on 30 Sep, net ₹-4,210.00',
      resultChars: 812,
      result: '{"runs":[]}',
    }
    expect(decodeStreamEvent({ event: 'tool', data: JSON.stringify(sent) })).toEqual({ type: 'tool', step: sent })
    expect(decodeStreamEvent({ event: 'done', data: '{"callId":1,"toolCalls":2,"rounds":2}' })).toMatchObject({ toolCalls: 2, rounds: 2 })
  })

  it('makes a tool step safe to show: arguments as text, missing numbers as not known', () => {
    expect(readToolStep({ name: 'get_run', arguments: { runId: 412 }, ok: false, error: 'Run 412 not found' })).toEqual({
      round: 1,
      id: '',
      name: 'get_run',
      arguments: '{"runId":412}',
      ok: false,
      error: 'Run 412 not found',
      seconds: null,
      rows: null,
      asOfUtc: null,
      summary: '',
      resultChars: null,
      result: '',
    })
  })

  it('skips a malformed data line and an event it does not know, rather than throwing', () => {
    expect(decodeStreamEvent({ event: 'delta', data: '{"text": "cut off' })).toBeNull()
    expect(decodeStreamEvent({ event: 'delta', data: '"just a string"' })).toBeNull()
    expect(decodeStreamEvent({ event: 'delta', data: '{"words":"no text field"}' })).toBeNull()
    expect(decodeStreamEvent({ event: 'surprise', data: '{}' })).toBeNull()
  })
})

describe('modelName', () => {
  it('names the models the desk uses', () => {
    expect(modelName('nvidia/nemotron-3-ultra-550b-a55b')).toBe('Nemotron 3 Ultra')
    expect(modelName('moonshotai/kimi-k3')).toBe('Kimi K3')
    expect(modelName('z-ai/glm-5.3')).toBe('GLM-5.3')
    expect(modelName('z-ai/glm-5.3-flash')).toBe('GLM-5.3 Flash')
    expect(modelName('deepseek-ai/deepseek-v4.1-flash')).toBe('DeepSeek V4.1 Flash')
    expect(modelName('nvidia/nemotron-3.5-lightning-30b-a3b')).toBe('Nemotron 3.5 Lightning')
    expect(modelName('nvidia/nemotron-3-super-120b-a12b')).toBe('Nemotron 3 Super')
  })

  it('falls back to the part after the vendor, and says "—" for none', () => {
    expect(modelName('meta/llama-4-maverick-17b-128e-instruct')).toBe('llama-4-maverick-17b-128e-instruct')
    expect(modelName('plain-id')).toBe('plain-id')
    expect(modelName('odd/')).toBe('odd/')
    expect(modelName(null)).toBe('—')
    expect(modelName('')).toBe('—')
  })
})

describe('labels', () => {
  it('gives every call outcome a word and a tone, and shows an unknown one as sent', () => {
    expect(outcomeBadge('ok')).toMatchObject({ label: 'OK', tone: 'pos' })
    expect(outcomeBadge('failed')).toMatchObject({ label: 'Failed', tone: 'neg' })
    expect(outcomeBadge('refused')).toMatchObject({ label: 'Refused', tone: 'warn' })
    expect(outcomeBadge('cancelled')).toMatchObject({ label: 'Cancelled', tone: 'neutral' })
    expect(outcomeBadge('running')).toMatchObject({ label: 'Running', tone: 'live' })
    expect(outcomeBadge('queued')).toEqual({ label: 'queued', tone: 'neutral', means: '' })
    expect(outcomeBadge(null).label).toBe('unknown')
  })

  it("says what each agent's status means", () => {
    expect(agentStatus('on')).toMatchObject({ label: 'On', tone: 'pos' })
    expect(agentStatus('off')).toMatchObject({ label: 'Off', tone: 'warn' })
    expect(agentStatus('planned')).toMatchObject({ label: 'Planned', tone: 'neutral', means: 'not built yet' })
  })

  it("reads an attempt's outcome", () => {
    expect(attemptLabel('ok')).toBe('answered')
    expect(attemptLabel('timeout')).toBe('timed out')
    expect(attemptLabel('http 429')).toBe('HTTP 429 (rate limit)')
    expect(attemptLabel('http 503')).toBe('HTTP 503')
    expect(attemptLabel('empty answer')).toBe('empty answer')
  })

  it('says why a model handed over, and to whom', () => {
    expect(fallbackNote({ model: 'nvidia/nemotron-3-ultra-550b-a55b', reason: 'timeout', next: 'moonshotai/kimi-k3' })).toBe(
      'Nemotron 3 Ultra timed out, asking Kimi K3',
    )
    expect(fallbackNote({ model: 'moonshotai/kimi-k3', reason: 'http 429', next: 'z-ai/glm-5.3' })).toBe(
      'Kimi K3 was rate-limited (HTTP 429), asking GLM-5.3',
    )
    expect(fallbackNote({ model: 'z-ai/glm-5.3', reason: 'empty answer', next: null })).toBe(
      'GLM-5.3 failed (empty answer), and no model is left to ask',
    )
    // A capacity refusal is asked once more before the chain moves on.
    expect(
      fallbackNote({
        model: 'nvidia/nemotron-3-ultra-550b-a55b',
        reason: 'provider error: Service temporarily overloaded',
        next: 'nvidia/nemotron-3-ultra-550b-a55b',
      }),
    ).toBe('Nemotron 3 Ultra failed (provider error: Service temporarily overloaded), asking it again in a moment')
  })
})

describe('formats', () => {
  it('writes seconds and tokens, and "—" for what is not known', () => {
    expect(formatSeconds(0.84)).toBe('0.8 s')
    expect(formatSeconds(8.16)).toBe('8.2 s')
    expect(formatSeconds(42.4)).toBe('42 s')
    expect(formatSeconds(92)).toBe('1m 32s')
    expect(formatSeconds(null)).toBe('—')
    expect(formatTokens(123456)).toBe('1,23,456')
    expect(formatTokens(null)).toBe('—')
  })

  it("gives a call's time in IST: the clock today, the day on an earlier one", () => {
    const now = Date.parse('2026-09-30T10:30:00Z') // 16:00 IST
    expect(callTime('2026-09-30T10:02:11Z', now)).toBe('15:32:11')
    // 18:40 UTC on the 29th is already the 30th in IST; 18:00 is not.
    expect(callTime('2026-09-29T18:40:00Z', now)).toBe('00:10:00')
    // "Sep" or "Sept", as the browser's locale data has it; the console's other dates say the same.
    expect(callTime('2026-09-29T18:00:00Z', now)).toMatch(/^29 Sept? 23:30$/)
    expect(callTime(null, now)).toBe('—')
    expect(callTime('not a date', now)).toBe('—')
  })

  it('builds the call-log query without blank filters', () => {
    expect(aiCallsQuery({})).toBe('take=50')
    expect(aiCallsQuery({ agent: 'desk-assistant', outcome: '', model: 'moonshotai/kimi-k3', take: 20 }, 12)).toBe(
      'agent=desk-assistant&model=moonshotai%2Fkimi-k3&take=20&beforeId=12',
    )
  })

  it('reads Retry-After as seconds or a date', () => {
    const now = Date.parse('2026-09-30T10:00:00Z')
    expect(retryAfterSeconds('20', now)).toBe(20)
    expect(retryAfterSeconds('Wed, 30 Sep 2026 10:00:45 GMT', now)).toBe(45)
    expect(retryAfterSeconds(null, now)).toBeNull()
    expect(retryAfterSeconds('soon', now)).toBeNull()
  })

  it('says each refusal in words, with what to do', () => {
    expect(askErrorText(429, { error: 'Rate limit: 30 questions in 10 minutes' }, 20)).toBe(
      'Rate limit: 30 questions in 10 minutes. Try again in 20 s.',
    )
    expect(askErrorText(429, null, null)).toBe('The AI is rate-limited or busy. Try again in a minute.')
    expect(askErrorText(503, { error: 'No key' }, null)).toMatch(/NVIDIA_API_KEY/)
    expect(askErrorText(409, { error: 'Desk Assistant is switched off' }, null)).toBe(
      'Desk Assistant is switched off. It can be switched on in the Agents tab.',
    )
    expect(askErrorText(400, { error: 'Last message must be user' }, null)).toBe('The API refused the question: Last message must be user.')
    expect(askErrorText(502, '<!doctype html>', null)).toBe('The API failed (HTTP 502).')
  })
})

describe('reading the API', () => {
  it('refuses a body it cannot read, rather than showing an empty page', () => {
    expect(() => readOverview({ tiers: [] })).toThrow(/shape this page cannot read/)
    expect(() => readCallsPage('<!doctype html>')).toThrow()
    expect(readCallsPage({ calls: [] })).toEqual({ calls: [], nextBeforeId: null })
  })

  it('fills the fields an older API build did not send, from what they mean', () => {
    const models = readModels({ models: [{ id: 'nvidia/nemotron-3-embed-1b', ownedBy: 'nvidia' }, { id: 'z-ai/glm-5.3', ownedBy: 'z-ai', listed: false }] })
    expect(models.models.map((m) => [m.embedding, m.listed])).toEqual([
      [true, true],
      [false, false],
    ])
    expect(models.local).toEqual([])
    const overview = readOverview({ provider: {}, limits: {}, today: {}, tiers: [{ key: 'judge' }, { key: 'embed' }] })
    expect(overview.tiers.map((t) => t.chat)).toEqual([true, false])
  })
})

// ---------- the conversation ----------

const ask = (id: string, question = 'Why did NIFTY gap up?'): ChatAction => ({ type: 'ask', id, question, tier: 'judge', now: 1_000 })
const ev = (id: string, event: AiStreamEvent): ChatAction => ({ type: 'event', id, event })
const run = (actions: ChatAction[], start: ChatState = initialChat('c-test')) => actions.reduce(chatReducer, start)

describe('chatReducer', () => {
  it('streams reasoning and the answer into the turn, and labels it when done', () => {
    const s = run([
      ask('t1'),
      ev('t1', { type: 'start', callId: 41, chain: ['nvidia/nemotron-3-ultra-550b-a55b', 'moonshotai/kimi-k3'] }),
      ev('t1', { type: 'attempt', model: 'nvidia/nemotron-3-ultra-550b-a55b', n: 1, of: 2, round: 1 }),
      ev('t1', { type: 'reasoning', text: 'Look at ' }),
      ev('t1', { type: 'reasoning', text: 'GIFT Nifty.' }),
      ev('t1', { type: 'delta', text: 'Because ' }),
      ev('t1', { type: 'delta', text: 'of the US close.' }),
    ])
    const t = s.turns[0]
    expect(t).toMatchObject({ status: 'streaming', callId: 41, model: 'nvidia/nemotron-3-ultra-550b-a55b', attempt: { n: 1, of: 2 } })
    expect(turnReasoning(t)).toBe('Look at GIFT Nifty.')
    expect(t.answer).toBe('Because of the US close.')

    const done = chatReducer(
      s,
      ev('t1', {
        type: 'done',
        callId: 41,
        model: 'nvidia/nemotron-3-ultra-550b-a55b',
        seconds: 8.1,
        finishReason: 'stop',
        usage: { promptTokens: 23, completionTokens: 16, totalTokens: 39 },
        fallbacks: 0,
        toolCalls: 0,
        rounds: 1,
      }),
    ).turns[0]
    expect(done).toMatchObject({ status: 'done', callId: 41, seconds: 8.1, finishReason: 'stop', fallbacks: 0, toolCalls: 0, roundCount: 1 })
    expect(done.usage?.totalTokens).toBe(39)
    expect(done.answer).toBe('Because of the US close.')
  })

  it('discards the partial answer and reasoning on a fallback, and notes why', () => {
    const s = run([
      ask('t1'),
      ev('t1', { type: 'attempt', model: 'nvidia/nemotron-3-ultra-550b-a55b', n: 1, of: 3, round: 1 }),
      ev('t1', { type: 'reasoning', text: 'half a thought' }),
      ev('t1', { type: 'delta', text: 'half an ans' }),
      ev('t1', { type: 'fallback', model: 'nvidia/nemotron-3-ultra-550b-a55b', reason: 'timeout', next: 'moonshotai/kimi-k3' }),
    ])
    expect(s.turns[0]).toMatchObject({ answer: '', fallbacks: 1, status: 'streaming' })
    expect(turnReasoning(s.turns[0])).toBe('')
    expect(s.turns[0].notes).toEqual(['Nemotron 3 Ultra timed out, asking Kimi K3'])

    const after = run(
      [
        ev('t1', { type: 'attempt', model: 'moonshotai/kimi-k3', n: 2, of: 3, round: 1 }),
        ev('t1', { type: 'delta', text: 'The whole answer.' }),
        ev('t1', { type: 'done', callId: 42, model: 'moonshotai/kimi-k3', seconds: 97.3, finishReason: 'stop', usage: null, fallbacks: 1, toolCalls: 0, rounds: 1 }),
      ],
      s,
    ).turns[0]
    expect(after).toMatchObject({ status: 'done', model: 'moonshotai/kimi-k3', answer: 'The whole answer.', fallbacks: 1, toolCalls: 0, roundCount: 1 })
  })

  it('records an error event, a refusal and a dropped connection as errors', () => {
    const errored = run([ask('t1'), ev('t1', { type: 'error', callId: 43, error: 'every model failed' })]).turns[0]
    expect(errored).toMatchObject({ status: 'error', callId: 43, error: 'every model failed' })

    const refused = run([ask('t1'), { type: 'refused', id: 't1', message: 'switched off', status: 409 }]).turns[0]
    expect(refused).toMatchObject({ status: 'error', error: 'switched off', errorStatus: 409 })

    const dropped = run([ask('t1'), ev('t1', { type: 'delta', text: 'par' }), { type: 'closed', id: 't1' }]).turns[0]
    expect(dropped.status).toBe('error')
    expect(dropped.error).toMatch(/connection closed/)
    // A stream that ended after its answer changes nothing.
    const fine = run([
      ask('t1'),
      ev('t1', { type: 'done', callId: 1, model: 'm', seconds: 1, finishReason: 'stop', usage: null, fallbacks: 0, toolCalls: 0, rounds: 1 }),
      { type: 'closed', id: 't1' },
    ]).turns[0]
    expect(fine.status).toBe('done')
  })

  it('keeps a stopped answer as it was, deaf to anything still in flight', () => {
    const s = run([ask('t1'), ev('t1', { type: 'delta', text: 'So far' }), { type: 'stopped', id: 't1' }, ev('t1', { type: 'delta', text: ' more' })])
    expect(s.turns[0]).toMatchObject({ status: 'stopped', answer: 'So far' })
  })

  it('starts a new conversation empty, and ignores events for the old one', () => {
    const s = run([ask('t1'), { type: 'reset', conversationId: 'c-new' }, ev('t1', { type: 'delta', text: 'late' })])
    expect(s).toEqual({ conversationId: 'c-new', turns: [] })
  })
})

describe('chatReducer with tools', () => {
  const step = (round: number, name: string, args: string): AiToolStep => ({
    round,
    id: `${name}-${round}`,
    name,
    arguments: args,
    ok: true,
    error: null,
    seconds: 0.2,
    rows: 3,
    asOfUtc: '2026-09-30T10:00:00Z',
    summary: '',
    resultChars: 11,
    result: '{"runs":[]}',
  })
  const runs = step(1, 'get_runs', '{"date":"today"}')
  const run412 = step(1, 'get_run', '{"runId":412}')
  const roundOne: ChatAction[] = [
    ask('t1', 'Why did the worst run today lose money?'),
    ev('t1', { type: 'attempt', model: 'nvidia/nemotron-3-ultra-550b-a55b', n: 1, of: 3, round: 1 }),
    ev('t1', { type: 'reasoning', text: 'Need the runs first.' }),
    ev('t1', { type: 'delta', text: "Let me read today's runs." }),
    ev('t1', { type: 'tool', step: runs }),
    ev('t1', { type: 'tool', step: run412 }),
  ]

  it("moves what the model wrote before calling a tool into that round's working, keeping its reasoning", () => {
    const t = run(roundOne).turns[0]
    expect(t.answer).toBe('')
    expect(t.toolCalls).toBe(2)
    expect(t.rounds).toEqual([{ round: 1, reasoning: 'Need the runs first.', working: "Let me read today's runs.", tools: [runs, run412] }])
  })

  it('keeps the earlier round and its tools when a model fails in a later one, and labels the answer', () => {
    const t = run([
      ...roundOne,
      ev('t1', { type: 'attempt', model: 'nvidia/nemotron-3-ultra-550b-a55b', n: 1, of: 3, round: 2 }),
      ev('t1', { type: 'reasoning', text: 'half a thought' }),
      ev('t1', { type: 'delta', text: 'Run 41' }),
      ev('t1', { type: 'fallback', model: 'nvidia/nemotron-3-ultra-550b-a55b', reason: 'timeout', next: 'moonshotai/kimi-k3' }),
      ev('t1', { type: 'attempt', model: 'moonshotai/kimi-k3', n: 1, of: 2, round: 2 }),
      ev('t1', { type: 'reasoning', text: 'Run 412 was the worst.' }),
      ev('t1', { type: 'delta', text: 'Run 412 lost ₹4,210 (get_run, 15:30 IST).' }),
      ev('t1', {
        type: 'done',
        callId: 9,
        model: 'moonshotai/kimi-k3',
        seconds: 12,
        finishReason: 'stop',
        usage: null,
        fallbacks: 1,
        toolCalls: 2,
        rounds: 2,
      }),
    ]).turns[0]
    expect(t.rounds.map((r) => [r.round, r.reasoning, r.working, r.tools.length])).toEqual([
      [1, 'Need the runs first.', "Let me read today's runs.", 2],
      [2, 'Run 412 was the worst.', '', 0],
    ])
    expect(t).toMatchObject({ status: 'done', answer: 'Run 412 lost ₹4,210 (get_run, 15:30 IST).', toolCalls: 2, roundCount: 2, fallbacks: 1 })
    expect(t.notes).toEqual(['Nemotron 3 Ultra timed out, asking Kimi K3'])
    expect(toolsLine(t.toolCalls, t.roundCount)).toBe('2 tool calls · 2 rounds')
  })

  it('says nothing about tools for an answer that needed none', () => {
    expect(toolsLine(0, 1)).toBe('')
    expect(toolsLine(null, null)).toBe('')
    expect(toolsLine(1, 2)).toBe('1 tool call · 2 rounds')
  })
})

describe('tool words', () => {
  it('names each tool, with the argument that says what it read', () => {
    expect(toolLabel('get_runs', '{"date":"today"}')).toEqual({ label: 'Runs', detail: 'date: today' })
    expect(toolLabel('get_run', '{"runId":412}')).toEqual({ label: 'Run #412', detail: '' })
    expect(toolLabel('get_option_chain_summary', '{"underlying":"NIFTY","expiry":"2026-10-06"}')).toEqual({
      label: 'Option chain NIFTY',
      detail: 'expiry: 2026-10-06',
    })
    expect(toolLabel('get_news', '{"query":"HDFC Bank","limit":5}')).toEqual({ label: 'News HDFC Bank', detail: 'limit: 5' })
    expect(toolLabel('get_strategy_spec', '{"strategy":"IronCondor"}').label).toBe('Strategy spec IronCondor')
    expect(toolLabel('get_quotes', '{"symbols":["NIFTY","BANKNIFTY"]}')).toEqual({ label: 'Quotes', detail: 'symbols: NIFTY, BANKNIFTY' })
    expect(toolLabel('get_latest_checkup', '')).toEqual({ label: 'Latest checkup', detail: '' })
  })

  it('keeps an unknown tool its own name, and arguments it cannot read as written', () => {
    expect(toolLabel('get_run', '{}')).toEqual({ label: 'Run', detail: '' })
    expect(toolLabel('get_brand_new', '{"x":1}')).toEqual({ label: 'get_brand_new', detail: 'x: 1' })
    expect(toolLabel('get_runs', '{"date": "tod')).toEqual({ label: 'Runs', detail: '{"date": "tod' })
  })

  it('lays out JSON to read, and shows a cut or broken result as it is', () => {
    expect(prettyJson('{"a":1,"b":[1,2]}')).toEqual({ text: '{\n  "a": 1,\n  "b": [\n    1,\n    2\n  ]\n}', json: true })
    expect(prettyJson('{"runs":[{"runId":4')).toEqual({ text: '{"runs":[{"runId":4', json: false })
    expect(prettyJson('')).toEqual({ text: '', json: false })
    expect(prettyJson(null)).toEqual({ text: '', json: false })
  })

  it('counts rows and gives the IST clock without seconds', () => {
    expect(rowsText(1)).toBe('1 row')
    expect(rowsText(1234)).toBe('1,234 rows')
    expect(rowsText(null)).toBe('')
    const now = Date.parse('2026-09-30T10:30:00Z')
    expect(clockTime('2026-09-30T10:00:00Z', now)).toBe('15:30')
    expect(clockTime('2026-09-29T10:00:00Z', now)).toMatch(/^29 Sept? 15:30$/)
    expect(clockTime(null, now)).toBe('—')
  })
})

describe('historyFor', () => {
  const finished = run([
    ask('t1', 'first?'),
    ev('t1', { type: 'delta', text: 'first answer' }),
    ev('t1', { type: 'done', callId: 1, model: 'm', seconds: 1, finishReason: 'stop', usage: null, fallbacks: 0, toolCalls: 0, rounds: 1 }),
    ask('t2', 'failed?'),
    ev('t2', { type: 'error', callId: 2, error: 'x' }),
    ask('t3', 'stopped?'),
    ev('t3', { type: 'delta', text: 'half' }),
    { type: 'stopped', id: 't3' },
  ])

  it('sends answered turns and the new question, never a failed or half answer', () => {
    expect(historyFor(finished.turns, 'next?')).toEqual([
      { role: 'user', content: 'first?' },
      { role: 'assistant', content: 'first answer' },
      { role: 'user', content: 'next?' },
    ])
  })

  it('drops the oldest turns first past the API limits', () => {
    const many = run(
      Array.from({ length: 30 }, (_, i): ChatAction[] => [
        ask(`t${i}`, `q${i}`),
        ev(`t${i}`, { type: 'delta', text: `a${i}` }),
        ev(`t${i}`, { type: 'done', callId: i, model: 'm', seconds: 1, finishReason: 'stop', usage: null, fallbacks: 0, toolCalls: 0, rounds: 1 }),
      ]).flat(),
    )
    const messages = historyFor(many.turns, 'last?')
    expect(messages.length).toBeLessThanOrEqual(40)
    expect(messages.length % 2).toBe(1)
    expect(messages[messages.length - 1]).toEqual({ role: 'user', content: 'last?' })
    expect(messages[messages.length - 2]).toEqual({ role: 'assistant', content: 'a29' })
    const long = historyFor([{ ...many.turns[0], answer: 'x'.repeat(60_000) }], 'q')
    expect(long).toEqual([{ role: 'user', content: 'q' }])
  })

  it('makes conversation ids of the right form', () => {
    expect(newConversationId()).toMatch(/^c-[0-9a-z]{12}$/)
    expect(newConversationId()).not.toBe(newConversationId())
  })
})

describe('streamAsk', () => {
  const sse = (chunks: string[], init: ResponseInit = {}) => {
    const encoder = new TextEncoder()
    const body = new ReadableStream<Uint8Array>({
      start(controller) {
        for (const c of chunks) controller.enqueue(encoder.encode(c))
        controller.close()
      },
    })
    return new Response(body, { status: 200, headers: { 'Content-Type': 'text/event-stream' }, ...init })
  }

  it('turns the stream into events, whatever the chunking', async () => {
    const events: AiStreamEvent[] = []
    const send = async () =>
      sse([
        ': ping\n\nevent: start\ndata: {"callId":7,"chain":["a"]}\n',
        '\nevent: delta\ndata: {"text":"Na',
        'masté"}\n\nevent: done\ndata: {"callId":7,"model":"a","seconds":1.2,"finishReason":"stop","usage":null,"fallbacks":0}\n\n',
      ])
    await streamAsk({ messages: [{ role: 'user', content: 'hi' }], tier: 'judge', conversationId: 'c-1' }, (e) => events.push(e), undefined, send)
    expect(events.map((e) => e.type)).toEqual(['start', 'delta', 'done'])
    expect(events[1]).toEqual({ type: 'delta', text: 'Namasté' })
  })

  it('keeps a character whose bytes are split across chunks', async () => {
    const bytes = new TextEncoder().encode('event: delta\ndata: {"text":"₹"}\n\n')
    const cut = bytes.indexOf(0xe2) + 1
    const body = new ReadableStream<Uint8Array>({
      start(c) {
        c.enqueue(bytes.slice(0, cut))
        c.enqueue(bytes.slice(cut))
        c.close()
      },
    })
    const events: AiStreamEvent[] = []
    await streamAsk(
      { messages: [], tier: null, conversationId: 'c' },
      (e) => events.push(e),
      undefined,
      async () => new Response(body, { headers: { 'Content-Type': 'text/event-stream' } }),
    )
    expect(events).toEqual([{ type: 'delta', text: '₹' }])
  })

  it('throws a refusal in words, with the wait a 429 asks for', async () => {
    const send = async () =>
      new Response(JSON.stringify({ error: 'Busy: 4 questions already in flight' }), {
        status: 429,
        headers: { 'Content-Type': 'application/json', 'Retry-After': '12' },
      })
    const refusal = await streamAsk({ messages: [], tier: 'judge', conversationId: 'c' }, () => {}, undefined, send).catch((e) => e)
    expect(refusal).toBeInstanceOf(AskRefusal)
    expect(refusal.status).toBe(429)
    expect(refusal.retryAfterSeconds).toBe(12)
    expect(refusal.message).toBe('Busy: 4 questions already in flight. Try again in 12 s.')
  })

  it("takes the wait from the refusal's body when there is no header, and keeps its call id", async () => {
    const send = async () =>
      new Response(JSON.stringify({ error: 'Rate limit', callId: 88, retryAfterSeconds: 7.2 }), {
        status: 429,
        headers: { 'Content-Type': 'application/json' },
      })
    const refusal = await streamAsk({ messages: [], tier: 'judge', conversationId: 'c' }, () => {}, undefined, send).catch((e) => e)
    expect(refusal.retryAfterSeconds).toBe(8)
    expect(refusal.callId).toBe(88)
    expect(refusal.message).toBe('Rate limit. Try again in 8 s.')
  })

  it('treats a page of HTML as an API that does not know the route', async () => {
    const send = async () => new Response('<!doctype html><html></html>', { status: 200, headers: { 'Content-Type': 'text/html' } })
    const refusal = await streamAsk({ messages: [], tier: 'judge', conversationId: 'c' }, () => {}, undefined, send).catch((e) => e)
    expect(refusal.status).toBe(404)
    expect(refusal.message).toMatch(/not on the running API build/)
  })
})

// ---------- chains and the catalog ----------

describe('chains', () => {
  it('moves a model up or down, and nowhere past the ends', () => {
    expect(moveInChain(['a', 'b', 'c'], 1, -1)).toEqual(['b', 'a', 'c'])
    expect(moveInChain(['a', 'b', 'c'], 1, 1)).toEqual(['a', 'c', 'b'])
    expect(moveInChain(['a', 'b'], 0, -1)).toEqual(['a', 'b'])
    expect(moveInChain(['a', 'b'], 1, 1)).toEqual(['a', 'b'])
  })

  it('says why a chain cannot be saved', () => {
    expect(chainProblem([], false)).toMatch(/at least one/)
    expect(chainProblem(['a', 'b', 'c', 'd', 'e', 'f'], false)).toMatch(/at most 5/)
    expect(chainProblem(['a', 'a'], false)).toMatch(/twice/)
    expect(chainProblem(['nvidia/nemotron-3-embed-1b'], false)).toMatch(/cannot answer/)
    expect(chainProblem(['moonshotai/kimi-k3'], true)).toMatch(/embedding models only/)
    expect(chainProblem(['nvidia/nemotron-3-embed-1b'], true)).toBeNull()
    expect(chainProblem(['moonshotai/kimi-k3', 'z-ai/glm-5.3'], false)).toBeNull()
  })

  it('lists the models in use first, then the rest by vendor, filtered', () => {
    const m = (id: string, inUse = false): AiModel => ({
      id,
      ownedBy: id.split('/')[0],
      inUse,
      tiers: [],
      agents: [],
      note: null,
      embedding: false,
      listed: true,
      lastTest: null,
      today: null,
      health: null,
    })
    const models = [m('nvidia/nemotron-3-ultra-550b-a55b', true), m('moonshotai/kimi-k3', true), m('meta/llama-4'), m('google/gemma-4'), m('meta/llama-3.3')]
    const all = catalogGroups(models, '')
    expect(all.inUse.map((x) => x.id)).toEqual(['nvidia/nemotron-3-ultra-550b-a55b', 'moonshotai/kimi-k3'])
    expect(all.vendors.map((g) => [g.vendor, g.models.map((x) => x.id)])).toEqual([
      ['google', ['google/gemma-4']],
      ['meta', ['meta/llama-3.3', 'meta/llama-4']],
    ])
    const found = catalogGroups(models, 'ULTRA')
    expect(found.inUse.map((x) => x.id)).toEqual(['nvidia/nemotron-3-ultra-550b-a55b'])
    expect(found.vendors).toEqual([])
  })
})

// ---------- the answer's text ----------

describe('parseInline', () => {
  it('reads bold, italic and code, and leaves the rest as text', () => {
    expect(parseInline('Sell **2 lots** of `NIFTY 25000 CE` *today*')).toEqual([
      { kind: 'text', text: 'Sell ' },
      { kind: 'bold', children: [{ kind: 'text', text: '2 lots' }] },
      { kind: 'text', text: ' of ' },
      { kind: 'code', text: 'NIFTY 25000 CE' },
      { kind: 'text', text: ' ' },
      { kind: 'em', children: [{ kind: 'text', text: 'today' }] },
    ])
  })

  it('keeps unclosed markers, snake_case and escapes as text', () => {
    expect(parseInline('a **half-sent')).toEqual([{ kind: 'text', text: 'a **half-sent' }])
    expect(parseInline('max_pain_level and 2 * 3 * 4')).toEqual([{ kind: 'text', text: 'max_pain_level and 2 * 3 * 4' }])
    expect(parseInline('\\*not em\\*')).toEqual([{ kind: 'text', text: '*not em*' }])
    expect(parseInline('`unclosed')).toEqual([{ kind: 'text', text: '`unclosed' }])
  })

  it('never turns markup into anything but text', () => {
    expect(parseInline('<img src=x onerror=alert(1)>')).toEqual([{ kind: 'text', text: '<img src=x onerror=alert(1)>' }])
  })
})

describe('parseAnswer', () => {
  it('reads paragraphs, headings, lists, code and tables', () => {
    const blocks = parseAnswer(
      [
        '## Short answer',
        'Line one',
        'line two',
        '',
        '- first',
        '  - nested',
        '- second',
        '',
        '1. one',
        '2. two',
        '',
        '```python',
        'print("x")',
        '```',
        '',
        '| Strike | OI |',
        '|---|---:|',
        '| 25000 | 1.2 L |',
      ].join('\n'),
    )
    expect(blocks.map((b) => b.kind)).toEqual(['heading', 'para', 'list', 'list', 'code', 'table'])
    expect(blocks[1]).toEqual({ kind: 'para', inlines: [{ kind: 'text', text: 'Line one' }, { kind: 'break' }, { kind: 'text', text: 'line two' }] })
    const bullets = blocks[2] as Extract<(typeof blocks)[number], { kind: 'list' }>
    expect(bullets.ordered).toBe(false)
    expect(bullets.items.map((i) => [i.depth, i.marker])).toEqual([
      [0, '•'],
      [1, '•'],
      [0, '•'],
    ])
    const numbered = blocks[3] as Extract<(typeof blocks)[number], { kind: 'list' }>
    expect(numbered.items.map((i) => i.marker)).toEqual(['1.', '2.'])
    expect(blocks[4]).toEqual({ kind: 'code', lang: 'python', text: 'print("x")' })
    const table = blocks[5] as Extract<(typeof blocks)[number], { kind: 'table' }>
    expect(table.head).toHaveLength(2)
    expect(table.rows).toEqual([[[{ kind: 'text', text: '25000' }], [{ kind: 'text', text: '1.2 L' }]]])
  })

  it('shows a fence still open mid-stream as code so far', () => {
    expect(parseAnswer('Here:\n```\nx = 1\ny =')).toEqual([
      { kind: 'para', inlines: [{ kind: 'text', text: 'Here:' }] },
      { kind: 'code', lang: '', text: 'x = 1\ny =' },
    ])
  })

  it('reads a list with blank lines between items as one list', () => {
    const blocks = parseAnswer('1. a\n\n2. b\n\nAfter.')
    expect(blocks.map((b) => b.kind)).toEqual(['list', 'para'])
  })
})

// ---------- reports ----------

describe('reports', () => {
  it('names what a report is about', () => {
    expect(reportSubjectLabel({ subjectType: 'run', subjectId: '412' })).toBe('Run #412')
    expect(reportSubjectLabel({ subjectType: 'incident', subjectId: '9' })).toBe('Incident #9')
    expect(reportSubjectLabel({ subjectType: 'news', subjectId: 'a1b2' })).toBe('News')
    expect(reportSubjectLabel({ subjectType: 'filing', subjectId: 'bse-77' })).toBe('Filing')
    expect(reportSubjectLabel({ subjectType: 'order', subjectId: '5' })).toBe('order 5')
  })

  it('links only to a console path or an https page', () => {
    expect(reportLink('/trade/runs/412')).toEqual({ kind: 'internal', to: '/trade/runs/412' })
    expect(reportLink('/system/incidents?id=9')).toEqual({ kind: 'internal', to: '/system/incidents?id=9' })
    expect(reportLink('https://www.bseindia.com/xml-data/corpfiling/AttachLive/abc.pdf')).toEqual({
      kind: 'external',
      href: 'https://www.bseindia.com/xml-data/corpfiling/AttachLive/abc.pdf',
    })
    expect(reportLink('//evil.example/x')).toBeNull()
    expect(reportLink('javascript:alert(1)')).toBeNull()
    expect(reportLink('http://plain.example/')).toBeNull()
    expect(reportLink(null)).toBeNull()
  })

  it('says each status, verdict, direction and urgency in a word, with a tone', () => {
    expect(reportStatus('ok')).toMatchObject({ label: 'OK', tone: 'pos' })
    expect(reportStatus('invalid')).toMatchObject({ label: 'Invalid', tone: 'warn' })
    expect(reportStatus('failed')).toMatchObject({ label: 'Failed', tone: 'neg' })
    expect(reportStatus('queued')).toMatchObject({ label: 'queued', tone: 'neutral' })
    expect(verdictBadge('followed')).toEqual({ label: 'Followed the spec', tone: 'pos' })
    expect(verdictBadge('deviated')).toEqual({ label: 'Deviated from the spec', tone: 'neg' })
    expect(verdictBadge(null)).toEqual({ label: 'unknown', tone: 'neutral' })
    expect(directionBadge('negative')).toEqual({ label: 'Negative', tone: 'neg' })
    expect(urgencyBadge('now')).toEqual({ label: 'Act now', tone: 'neg' })
    expect(urgencyBadge('today')).toEqual({ label: 'Today', tone: 'warn' })
  })

  it('reads a valid share against the 98% target, and unknown as plain', () => {
    expect(validityTone(99.1)).toBe('pos')
    expect(validityTone(98)).toBe('pos')
    expect(validityTone(94.5)).toBe('warn')
    expect(validityTone(80)).toBe('neg')
    expect(validityTone(null)).toBe('neutral')
  })

  it('folds a review to its first paragraph, headings skipped', () => {
    expect(firstParagraph('## Verdict\n\nFollowed the spec, **one** stale fill.\nSecond line.\n\n## Lesson\n\nNone.')).toBe(
      'Followed the spec, **one** stale fill.\nSecond line.',
    )
    expect(firstParagraph('')).toBe('')
    expect(confidenceText(0.82)).toBe('82%')
    expect(confidenceText(null)).toBe('—')
  })

  it("reads each agent's data, filling what is missing honestly", () => {
    expect(readTradeReview({ verdict: 'deviated', deviations: ['Third entry after the limit of 2', ''], staleFills: 1 })).toEqual({
      verdict: 'deviated',
      followed: [],
      deviations: ['Third entry after the limit of 2'],
      staleFills: 1,
      marketContext: null,
      lesson: null,
    })
    expect(
      readNewsEvent({ event: 'results', direction: 'positive', symbols: ['TCS'], numbers: [{ what: 'Net profit', value: 12380, unit: 'crore', quote: 'net profit of Rs 12,380 crore' }], confidence: 0.9 }),
    ).toEqual({
      event: 'results',
      direction: 'positive',
      symbols: ['TCS'],
      numbers: [{ what: 'Net profit', value: '12380', unit: 'crore', quote: 'net profit of Rs 12,380 crore' }],
      confidence: 0.9,
      summary: '',
    })
    expect(readTradeReview(null)).toBeNull()
  })

  it('reads the list and a report strictly, and builds the query without blanks', () => {
    const page = readReportsPage({ reports: [{ id: 3, agentKey: 'trade-reviewer', subjectType: 'run', subjectId: 412, title: 'Run 412' }], nextBeforeId: null })
    expect(page.reports[0]).toMatchObject({ subjectId: '412', attempts: 1, link: null, error: null })
    expect(() => readReportsPage({ items: [] })).toThrow(/cannot read/)
    expect(readReport({ report: { id: 3, agentKey: 'news-analyst' }, body: 'x', data: [1] }).data).toBeNull()
    expect(reportsQuery({ agent: 'trade-reviewer', subjectType: 'run', subjectId: '412', status: '', take: 1 })).toBe(
      'agent=trade-reviewer&subjectType=run&subjectId=412&take=1',
    )
  })
})

// ---------- model health, the assistant check, docs search, telegram ----------

describe('model health', () => {
  const now = Date.parse('2026-09-30T10:40:00Z') // 16:10 IST
  const h = (state: string, extra = {}) => ({
    model: 'moonshotai/kimi-k3',
    state,
    coolingUntilUtc: null,
    consecutiveFailures: 0,
    lastFailure: null,
    lastFailureUtc: null,
    lastOkSeconds: null,
    lastOkUtc: null,
    lastProbeUtc: null,
    ...extra,
  })

  it('says until when a model cools, in IST, with its last failure', () => {
    expect(
      healthBadge(h('cooling', { coolingUntilUtc: '2026-09-30T11:10:00Z', lastFailure: 'timeout: no answer within 90 s', lastFailureUtc: '2026-09-30T10:30:00Z', consecutiveFailures: 2 }), now),
    ).toEqual({ label: 'cooling until 16:40', tone: 'warn', detail: 'timeout: no answer within 90 s (16:00)' })
    expect(healthBadge(h('cooling'), now)).toMatchObject({ label: 'cooling', detail: 'failed; no reason recorded' })
  })

  it('names the other states plainly, and unknown as not asked yet', () => {
    expect(healthBadge(h('healthy', { lastOkSeconds: 4.2, lastOkUtc: '2026-09-30T10:32:00Z' }), now)).toEqual({
      label: 'healthy',
      tone: 'pos',
      detail: 'last answer in 4.2 s (16:02)',
    })
    expect(healthBadge(h('failed', { consecutiveFailures: 1, lastFailure: 'http 503' }), now)).toMatchObject({ label: 'failed last time', tone: 'warn', detail: 'http 503' })
    expect(healthBadge(h('unknown'), now)).toMatchObject({ label: 'not asked yet', tone: 'neutral' })
    expect(healthBadge(null, now).label).toBe('not known')
  })
})

describe('the assistant check', () => {
  it('reads its questions and counts, and says the score', () => {
    const c = readAssistantCheck({
      questions: [
        { question: 'How many runs today?', kind: 'number', expected: '3', pass: true, answer: 'Three runs.', callId: 301, model: 'nvidia/nemotron-3-ultra-550b-a55b', seconds: 12.4 },
        { question: 'Worst run id?', kind: 'id', expected: 412, pass: false, answer: 'Run 411', callId: 302, model: null, seconds: 9, error: null },
      ],
    })!
    expect(c).toMatchObject({ passed: 1, total: 2, score: 0.5 })
    expect(c.questions[1]).toMatchObject({ expected: '412', pass: false, model: null })
    expect(checkScoreText({ passed: 8, total: 9, score: 8 / 9 })).toBe('8 of 9, 89%')
    expect(checkScoreText({ passed: 0, total: 0, score: null })).toBe('no questions')
    expect(readAssistantCheck(null)).toBeNull()
  })

  it('names a check report by its day', () => {
    expect(reportSubjectLabel({ subjectType: 'check', subjectId: '2026-09-30' })).toBe('Assistant check · 30 Sep')
    expect(shortDate('2026-01-05')).toBe('5 Jan')
    expect(shortDate('not a day')).toBe('not a day')
  })
})

describe('docs search', () => {
  it('links a passage to its page on openfno.com/docs, with its heading as the anchor', () => {
    expect(docsLink('docs/modules/option_chain.md', 'Option chain › Max pain and walls')).toBe(
      'https://openfno.com/docs/modules/option-chain/#max-pain-and-walls',
    )
    expect(docsLink('docs/modules/data_module.md', 'Data')).toBe('https://openfno.com/docs/modules/data/')
    expect(docsLink('docs/strategies/IronCondor.md', 'IronCondor › Exit')).toBe('https://openfno.com/docs/strategies/iron-condor/#exit')
  })

  it('does not invent a page for a file the site does not publish', () => {
    expect(docsLink('docs/modules/ai.md', 'AI workspace › Desk tools')).toBeNull()
    expect(docsLink('docs/roadmap/private-notes.md')).toBeNull()
  })

  it('reads the index state and the hits', () => {
    const r = readDocsSearch({ index: { files: 56, passages: 1297, indexedUtc: null, model: 'nvidia/nemotron-3-embed-1b' }, hits: [{ file: 'docs/x.md', text: 'a' }] })
    expect(r.query).toBeNull()
    expect(r.hits).toEqual([{ file: 'docs/x.md', section: '', score: null, text: 'a' }])
    expect(() => readDocsSearch({ hits: [] })).toThrow(/cannot read/)
  })

  it('labels a strategy history read with what it names and its period', () => {
    expect(toolLabel('get_strategy_history', '{"strategy":"IronCondor","underlying":"BANKNIFTY","period":"this_month"}')).toEqual({
      label: 'Strategy history · IronCondor · BANKNIFTY · this month',
      detail: '',
    })
    expect(toolLabel('get_strategy_history', '{"account":"admin","from":"2026-09-01","to":"2026-09-15"}').label).toBe('Strategy history · admin · 1 Sep–15 Sep')
    expect(toolLabel('get_strategy_history', '{}').label).toBe('Strategy history')
  })

  it('labels the tool call with its query', () => {
    expect(toolLabel('search_docs', '{"query":"re-entry limit","limit":5}')).toEqual({ label: 'Docs search “re-entry limit”', detail: 'limit: 5' })
  })
})

describe('telegram and sources', () => {
  it('counts a pairing code down to its expiry', () => {
    const now = Date.parse('2026-09-30T10:40:00Z')
    expect(countdownText('2026-09-30T10:49:42Z', now)).toBe('9:42 left')
    expect(countdownText('2026-09-30T10:40:05Z', now)).toBe('0:05 left')
    expect(countdownText('2026-09-30T10:39:59Z', now)).toBe('expired')
    expect(countdownText(null, now)).toBe('expiry not known')
  })

  it("says why the bot is not running, from what the API can tell", () => {
    expect(telegramState({ running: true, enabled: true }).label).toBe('running')
    expect(telegramState({ running: false, enabled: true })).toMatchObject({ label: 'not running', tone: 'warn' })
    expect(telegramState({ running: false, enabled: true }).note).toMatch(/bot token and the model key/)
    expect(telegramState({ running: false, enabled: false }).label).toBe('off')
  })

  it("links the bot only when its name is one", () => {
    expect(botLink('codefortrade_bot')).toBe('https://t.me/codefortrade_bot')
    expect(botLink('@codefortrade_bot')).toBe('https://t.me/codefortrade_bot')
    expect(botLink('javascript:alert(1)')).toBeNull()
    expect(botLink(null)).toBeNull()
    expect(readTelegram({ running: true, enabled: true, owners: [{ telegramUserId: 123, telegramName: '@owner', consoleUser: 'admin' }] }).owners[0]).toMatchObject({
      telegramUserId: '123',
      linkedUtc: null,
    })
  })

  it('names every call source', () => {
    expect(['console', 'api', 'schedule', 'check', 'health', 'index', 'telegram'].map(sourceLabel)).toEqual([
      'Console',
      'API',
      'Schedule',
      'Assistant check',
      'Health probe',
      'Docs index',
      'Telegram',
    ])
    expect(sourceLabel('carrier-pigeon')).toBe('carrier-pigeon')
  })
})
