/**
 * The AI workspace: the desk's hosted language models, the agents that use
 * them, and every call they made (api/Ai, admin-only on the server).
 *
 * The models are NVIDIA-hosted (build.nvidia.com, the free NIM API), grouped
 * into tiers. A tier is an ordered chain: the first model answers, and the
 * next is asked only when it fails or times out. An agent uses its tier's
 * chain unless the owner gave it one of its own. The key never leaves the
 * server; the API says only whether one is configured.
 *
 * This module is the page-free half of the workspace, so it can be tested:
 *   - the API's shapes and a reader that refuses a body it cannot read (a
 *     missing section is an error on screen, never an empty panel that reads
 *     as "nothing happened");
 *   - the query hooks. Kept out of queries.ts on purpose, like specs.ts: only
 *     the AI pages use them;
 *   - the assistant's stream: a server-sent-events parser fed raw chunks, the
 *     runner that POSTs the question (EventSource can neither POST nor send a
 *     bearer token), and the chat reducer the page keeps its conversation in;
 *   - a small, safe reader of the answers' markdown. The page renders the
 *     blocks it returns as React text nodes, never as HTML, so nothing a model
 *     writes can become markup;
 *   - the words and formats every AI page shares: model names, outcomes,
 *     seconds, tokens, IST times.
 */

import { keepPreviousData, useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, api, fetchWithSession } from './api'

// ---------- shapes ---------------------------------------------------------

export type Tone = 'pos' | 'neg' | 'warn' | 'neutral' | 'accent' | 'live'

/** judge, analyst, extract and embed; kept a string, so a tier the server adds later still shows. */
export type TierKey = string
export type AgentStatus = 'on' | 'off' | 'planned'
/** ok, failed, refused (switched off, rate limit, busy, no key), cancelled (the asker stopped it), running. */
export type CallOutcome = 'ok' | 'failed' | 'refused' | 'cancelled' | 'running'

export interface AiProvider {
  key: string
  name: string
  baseUrl: string
  keyConfigured: boolean
  catalogUrl: string
  terms: string
}

export interface AiLimits {
  perUserPer10Min: number
  globalPerMinute: number
  maxConcurrent: number
  usedLastMinute: number
  inFlight: number
  providerNote: string
}

export interface AiTier {
  key: TierKey
  label: string
  purpose: string
  /** Fallback order: the first model answers, the next only when it fails. */
  chain: string[]
  defaultChain: string[]
  overridden: boolean
  /** False for the embed tier: it makes vectors, answers no question, and its chain is not editable. */
  chat: boolean
  updatedUtc: string | null
  updatedBy: string | null
}

export interface AiToday {
  calls: number
  ok: number
  failed: number
  refused: number
  cancelled: number
  /** Calls in flight now. */
  running: number
  promptTokens: number
  completionTokens: number
  totalTokens: number
  /** Null when there was nothing to average. */
  avgSeconds: number | null
  p95Seconds: number | null
  fallbacks: number
}

export interface AiModelUsage {
  model: string
  calls: number
  ok: number
  failed: number
  totalTokens: number
  avgSeconds: number | null
}

export interface AiOverview {
  provider: AiProvider
  limits: AiLimits
  tiers: AiTier[]
  today: AiToday
  byModel: AiModelUsage[]
  lastOk: { utc: string; model: string; callId: number } | null
  lastError: { utc: string; error: string; callId: number } | null
  agents: { total: number; built: number; on: number; off: number; planned: number }
}

export interface AiCallRef {
  id: number
  utc: string
  outcome: CallOutcome | string
  model: string | null
}

/** A read-only tool an agent may call on its own: a name the model uses and what it returns. */
export interface AiAgentTool {
  name: string
  description: string
}

export interface AiAgent {
  key: string
  number: number
  name: string
  job: string
  useCase: string
  schedule: string
  phase: string
  status: AgentStatus
  built: boolean
  enabled: boolean
  tier: TierKey
  tierLabel: string
  chain: string[]
  chainOverridden: boolean
  reads: string
  limits: string
  /** The desk reads it may make itself (Phase 2); empty for a planned agent. */
  tools: AiAgentTool[]
  lastCall: AiCallRef | null
  nextRunUtc: string | null
  today: { calls: number; ok: number; failed: number; totalTokens: number } | null
  updatedUtc: string | null
  updatedBy: string | null
  reason: string | null
}

/** Something on the desk that looks like an agent but is rules in code. */
export interface RuleBasedAgent {
  name: string
  what: string
  where: string
  model: string
}

export interface AiAgentsResponse {
  agents: AiAgent[]
  ruleBased: RuleBasedAgent[]
}

export interface AiModelTest {
  utc: string
  ok: boolean
  seconds: number | null
  error: string | null
  callId: number | null
}

export interface AiModel {
  id: string
  ownedBy: string
  inUse: boolean
  tiers: string[]
  agents: string[]
  note: string | null
  /** A vectors model: it cannot answer, so it cannot be tested with a question (the API answers 400). */
  embedding: boolean
  /** False when the provider's list did not include it: a tier names a model the provider withdrew. */
  listed: boolean
  lastTest: AiModelTest | null
  today: { calls: number; ok: number; failed: number; avgSeconds: number | null } | null
}

export interface LocalModel {
  id: string
  where: string
  usedBy: string
  kind: string
}

/** provider: fresh; cache: the last good list, up to 10 minutes old; unavailable: only the models the tiers name. */
export type CatalogSource = 'provider' | 'cache' | 'unavailable'

export interface AiModelsResponse {
  fetchedUtc: string | null
  source: CatalogSource | string
  error: string | null
  models: AiModel[]
  local: LocalModel[]
}

export interface ModelTestResult {
  ok: boolean
  model: string
  seconds: number | null
  answer: string | null
  error: string | null
  callId: number | null
}

export interface AiCallSummary {
  id: number
  utc: string
  completedUtc: string | null
  agentKey: string
  agentName: string
  tier: string
  source: string
  requestedBy: string
  /** The model that answered; null when none did. */
  model: string | null
  outcome: CallOutcome | string
  attemptCount: number
  fallbacks: number
  seconds: number | null
  promptTokens: number | null
  completionTokens: number | null
  totalTokens: number | null
  summary: string
  error: string
  conversationId: string
  /** Tools the model called while answering. */
  toolCalls: number
  /** Model rounds: one, plus one per round of tool results sent back. */
  rounds: number
}

export interface AiCallsPage {
  calls: AiCallSummary[]
  nextBeforeId: number | null
}

export interface ChatMessage {
  role: 'system' | 'user' | 'assistant' | string
  content: string
}

export interface AiAttempt {
  model: string
  /** ok, or why it moved on: timeout, http 429, empty answer… */
  outcome: string
  seconds: number | null
  httpStatus: number | null
  /** The model round it belongs to, from 1. */
  round: number
}

/**
 * One tool the model called and what came back, as the stream's `tool` event
 * and the call's detail carry it. `result` is exactly what the model was
 * given (the first 16,000 characters of `resultChars`).
 */
export interface AiToolStep {
  round: number
  /** The model's id for the call, unique within the question. */
  id: string
  name: string
  /** The JSON text the model wrote as the arguments. */
  arguments: string
  ok: boolean
  error: string | null
  seconds: number | null
  rows: number | null
  /** When the data it read was true; null when the tool cannot say. */
  asOfUtc: string | null
  /** One line: "3 runs on 30 Sep, net ₹-4,210.00". */
  summary: string
  resultChars: number | null
  result: string
}

export interface AiCallDetail extends AiCallSummary {
  system: string
  messages: ChatMessage[]
  answer: string
  reasoning: string
  finishReason: string | null
  /** The chain the call would walk, in order. */
  chain: string[]
  attempts: AiAttempt[]
  tools: AiToolStep[]
  request: { endpoint: string; maxTokens: number | null; temperature: number | null; stream: boolean } | null
}

export interface AiUsage {
  promptTokens: number | null
  completionTokens: number | null
  totalTokens: number | null
}

// ---------- reading the API strictly ------------------------------------------

/** A body this page cannot read is an error on screen, not an empty page that reads as "nothing yet". */
function need<T>(raw: unknown, keys: readonly string[], what: string): T {
  const ok = raw != null && typeof raw === 'object' && !Array.isArray(raw) && keys.every((k) => k in (raw as object))
  if (!ok) throw new Error(`The API's ${what} came back in a shape this page cannot read. Is the API build current?`)
  return raw as T
}

const list = <T,>(value: unknown): T[] => (Array.isArray(value) ? (value as T[]) : [])

export function readOverview(raw: unknown): AiOverview {
  const o = need<AiOverview>(raw, ['provider', 'limits', 'tiers', 'today'], 'overview')
  return {
    ...o,
    // An API from before `chat` existed: every tier but embed answers questions.
    tiers: list<AiTier>(o.tiers).map((t) => ({ ...t, chat: t.chat ?? t.key !== 'embed' })),
    byModel: list(o.byModel),
    lastOk: o.lastOk ?? null,
    lastError: o.lastError ?? null,
  }
}

export function readAgents(raw: unknown): AiAgentsResponse {
  const o = need<AiAgentsResponse>(raw, ['agents'], 'agent list')
  return { agents: list<AiAgent>(o.agents).map((a) => ({ ...a, tools: list(a.tools) })), ruleBased: list(o.ruleBased) }
}

export function readModels(raw: unknown): AiModelsResponse {
  const o = need<AiModelsResponse>(raw, ['models'], 'model catalog')
  return {
    ...o,
    models: list<AiModel>(o.models).map((m) => ({ ...m, embedding: m.embedding ?? isEmbeddingModel(m.id), listed: m.listed ?? true })),
    local: list(o.local),
    error: o.error ?? null,
    fetchedUtc: o.fetchedUtc ?? null,
  }
}

/** A call from an API build before tools: none called, one round. */
function withRounds<T extends { toolCalls?: number; rounds?: number }>(c: T): T & { toolCalls: number; rounds: number } {
  return { ...c, toolCalls: c.toolCalls ?? 0, rounds: c.rounds ?? 1 }
}

export function readCallsPage(raw: unknown): AiCallsPage {
  const o = need<AiCallsPage>(raw, ['calls'], 'call log')
  return { calls: list<AiCallSummary>(o.calls).map(withRounds), nextBeforeId: o.nextBeforeId ?? null }
}

export function readCall(raw: unknown): AiCallDetail {
  const o = need<AiCallDetail>(raw, ['id', 'outcome'], 'call')
  return {
    ...withRounds(o),
    messages: list(o.messages),
    attempts: list<AiAttempt>(o.attempts).map((a) => ({ ...a, round: a.round ?? 1 })),
    tools: list<unknown>(o.tools).map(readToolStep),
    chain: list(o.chain),
    request: o.request ?? null,
  }
}

// ---------- queries ---------------------------------------------------------

export interface AiCallFilters {
  agent?: string
  outcome?: string
  model?: string
  take?: number
}

/** GET /api/Ai/calls's query string; empty filters are left out, not sent blank. */
export function aiCallsQuery(filters: AiCallFilters, beforeId: number | null = null): string {
  const p = new URLSearchParams()
  if (filters.agent) p.set('agent', filters.agent)
  if (filters.outcome) p.set('outcome', filters.outcome)
  if (filters.model) p.set('model', filters.model)
  p.set('take', String(filters.take ?? 50))
  if (beforeId != null) p.set('beforeId', String(beforeId))
  return p.toString()
}

/** The overview: provider, limits, tiers and today's numbers. The rate-limit meter is only honest while it moves. */
export function useAiOverview() {
  return useQuery({
    queryKey: ['ai', 'overview'],
    queryFn: async () => readOverview(await api.get<unknown>('/api/Ai/overview')),
    refetchInterval: 15_000,
  })
}

export function useAiAgents() {
  return useQuery({
    queryKey: ['ai', 'agents'],
    queryFn: async () => readAgents(await api.get<unknown>('/api/Ai/agents')),
    refetchInterval: 30_000,
  })
}

/** The provider's catalog. The server caches it for ten minutes; a minute here is plenty. */
export function useAiModels() {
  return useQuery({
    queryKey: ['ai', 'models'],
    queryFn: async () => readModels(await api.get<unknown>('/api/Ai/models')),
    staleTime: 60_000,
    refetchInterval: 60_000,
  })
}

/**
 * The call log, newest first, a page at a time (nextBeforeId). Re-read every
 * 10 s while the tab is visible (TanStack pauses the interval in a hidden
 * tab), so a running call turns into its outcome without a reload.
 */
export function useAiCalls(filters: AiCallFilters) {
  return useInfiniteQuery({
    queryKey: ['ai', 'calls', filters],
    queryFn: async ({ pageParam }: { pageParam: number | null }) =>
      readCallsPage(await api.get<unknown>(`/api/Ai/calls?${aiCallsQuery(filters, pageParam)}`)),
    initialPageParam: null as number | null,
    getNextPageParam: (last: AiCallsPage) => last.nextBeforeId ?? undefined,
    placeholderData: keepPreviousData,
    refetchInterval: 10_000,
  })
}

/** One call in full; re-read every 2 s while it is still running. */
export function useAiCall(id: number | null) {
  return useQuery({
    queryKey: ['ai', 'call', id],
    queryFn: async () => readCall(await api.get<unknown>(`/api/Ai/calls/${id}`)),
    enabled: id != null,
    refetchInterval: (q: { state: { data?: AiCallDetail } }) => (q.state.data?.outcome === 'running' ? 2_000 : false),
  })
}

export interface AgentUpdate {
  /** Null leaves it as it is. */
  enabled?: boolean | null
  /** Replaces the agent's own chain (1–5 known models). */
  chain?: string[] | null
  /** Back to the tier's chain. */
  resetChain?: boolean
  reason?: string
}

/** PUT /api/Ai/agents/{key}: on or off, its own chain, or back to the tier's. */
export function useUpdateAgent() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ key, ...body }: { key: string } & AgentUpdate) =>
      api.put<AiAgent>(`/api/Ai/agents/${encodeURIComponent(key)}`, {
        enabled: body.enabled ?? null,
        chain: body.chain ?? null,
        resetChain: body.resetChain ?? false,
        reason: body.reason ?? '',
      }),
    onSuccess: (agent) => {
      // The answer is the updated row: show it at once, then re-read the rest (the overview counts it).
      qc.setQueryData<AiAgentsResponse>(['ai', 'agents'], (old) =>
        old && agent?.key ? { ...old, agents: old.agents.map((a) => (a.key === agent.key ? agent : a)) } : old,
      )
      void qc.invalidateQueries({ queryKey: ['ai'] })
    },
  })
}

/** PUT /api/Ai/tiers/{tier}: a new chain, or null to go back to the default. */
export function useUpdateTier() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ tier, chain, reason }: { tier: string; chain: string[] | null; reason?: string }) =>
      api.put<AiTier>(`/api/Ai/tiers/${encodeURIComponent(tier)}`, { chain, reason: reason ?? '' }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['ai'] }),
  })
}

/** POST /api/Ai/models/test: one tiny question, logged as a call like any other. */
export function useTestModel() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (model: string) => api.post<ModelTestResult>('/api/Ai/models/test', { model }),
    onSettled: () => void qc.invalidateQueries({ queryKey: ['ai'] }),
  })
}

// ---------- server-sent events ------------------------------------------------

export interface SseMessage {
  event: string
  data: string
}

/**
 * Reads server-sent events out of raw text as it arrives. Feed it what was
 * carried from the last call and the new chunk; it returns every event the
 * two complete and the unfinished tail to carry into the next call.
 *
 * It follows the EventSource rules the stream is written to: lines end in
 * LF, CRLF or CR (a CR at the very end is held back, since its LF may be the
 * next chunk's first byte); a blank line ends an event; a line starting with
 * ":" is a comment (the server's ": ping" keep-alive); "data" lines join with
 * a newline; an event with no data is not one; the name defaults to
 * "message". `final` reads whatever is left as a last event, so an answer is
 * not lost when the server closes without the closing blank line.
 */
export function parseSse(carry: string, chunk: string, final = false): { messages: SseMessage[]; carry: string } {
  let text = carry + chunk
  let held = ''
  if (!final && text.endsWith('\r')) {
    held = '\r'
    text = text.slice(0, -1)
  }
  text = text.replace(/\r\n?/g, '\n')
  const blocks = text.split('\n\n')
  const tail = final ? '' : (blocks.pop() ?? '')
  const messages: SseMessage[] = []
  for (const block of blocks) {
    const message = readSseBlock(block)
    if (message) messages.push(message)
  }
  return { messages, carry: tail + held }
}

function readSseBlock(block: string): SseMessage | null {
  let event = ''
  const data: string[] = []
  for (const line of block.split('\n')) {
    if (line === '' || line.startsWith(':')) continue
    const colon = line.indexOf(':')
    const field = colon < 0 ? line : line.slice(0, colon)
    let value = colon < 0 ? '' : line.slice(colon + 1)
    if (value.startsWith(' ')) value = value.slice(1)
    if (field === 'event') event = value
    else if (field === 'data') data.push(value)
    // id and retry mean nothing to a stream that is read once.
  }
  if (data.length === 0) return null
  return { event: event || 'message', data: data.join('\n') }
}

export type AiStreamEvent =
  | { type: 'start'; callId: number | null; chain: string[] }
  /** A model asked for `round` (from 1); n of `of` models still available in that round. */
  | { type: 'attempt'; model: string; n: number; of: number; round: number }
  | { type: 'reasoning'; text: string }
  /** A tool the model called has run; `step` is what the model got back. */
  | { type: 'tool'; step: AiToolStep }
  | { type: 'delta'; text: string }
  | { type: 'fallback'; model: string; reason: string; next: string | null }
  | {
      type: 'done'
      callId: number | null
      model: string | null
      seconds: number | null
      finishReason: string | null
      usage: AiUsage | null
      fallbacks: number
      toolCalls: number
      rounds: number
    }
  | { type: 'error'; callId: number | null; error: string }

const num = (v: unknown): number | null => (typeof v === 'number' && Number.isFinite(v) ? v : null)
const str = (v: unknown): string | null => (typeof v === 'string' ? v : null)

/**
 * A tool step from the stream or a call's detail, every field made safe to
 * show: arguments sent as an object become its JSON text, and a missing
 * number is null, never a zero that would read as a fact.
 */
export function readToolStep(raw: unknown): AiToolStep {
  const d = raw && typeof raw === 'object' ? (raw as Record<string, unknown>) : {}
  const args = d.arguments
  return {
    round: num(d.round) ?? 1,
    id: str(d.id) ?? '',
    name: str(d.name) ?? 'unknown tool',
    arguments: typeof args === 'string' ? args : args == null ? '' : JSON.stringify(args),
    ok: d.ok === true,
    error: str(d.error),
    seconds: num(d.seconds),
    rows: num(d.rows),
    asOfUtc: str(d.asOfUtc),
    summary: str(d.summary) ?? '',
    resultChars: num(d.resultChars),
    result: str(d.result) ?? '',
  }
}

/**
 * One SSE message as the stream's event, or null for one this page does not
 * know or cannot read (a data line that is not JSON). Null is skipped, never
 * thrown: one bad line must not end an answer that is otherwise arriving.
 */
export function decodeStreamEvent(message: SseMessage): AiStreamEvent | null {
  let raw: unknown
  try {
    raw = JSON.parse(message.data)
  } catch {
    return null
  }
  if (!raw || typeof raw !== 'object') return null
  const d = raw as Record<string, unknown>
  switch (message.event) {
    case 'start':
      return { type: 'start', callId: num(d.callId), chain: list<unknown>(d.chain).filter((m): m is string => typeof m === 'string') }
    case 'attempt':
      return { type: 'attempt', model: str(d.model) ?? '', n: num(d.n) ?? 1, of: num(d.of) ?? 1, round: num(d.round) ?? 1 }
    case 'reasoning':
    case 'delta': {
      const text = str(d.text)
      return text == null ? null : { type: message.event, text }
    }
    case 'tool':
      return { type: 'tool', step: readToolStep(d) }
    case 'fallback':
      return { type: 'fallback', model: str(d.model) ?? '', reason: str(d.reason) ?? '', next: str(d.next) }
    case 'done': {
      const u = d.usage && typeof d.usage === 'object' ? (d.usage as Record<string, unknown>) : null
      return {
        type: 'done',
        callId: num(d.callId),
        model: str(d.model),
        seconds: num(d.seconds),
        finishReason: str(d.finishReason),
        usage: u ? { promptTokens: num(u.promptTokens), completionTokens: num(u.completionTokens), totalTokens: num(u.totalTokens) } : null,
        fallbacks: num(d.fallbacks) ?? 0,
        toolCalls: num(d.toolCalls) ?? 0,
        rounds: num(d.rounds) ?? 1,
      }
    }
    case 'error':
      return { type: 'error', callId: num(d.callId), error: str(d.error) ?? 'The call failed.' }
    default:
      return null
  }
}

// ---------- asking ------------------------------------------------------------

export interface AskBody {
  messages: ChatMessage[]
  /** Null: the agent's own chain. */
  tier: string | null
  system?: string | null
  maxTokens?: number
  temperature?: number
  conversationId: string
  agent?: string
}

/** The API said no before the stream began: bad input, switched off, rate limit, no key. */
export class AskRefusal extends Error {
  readonly status: number
  /** From Retry-After (or the body's retryAfterSeconds) on a 429. */
  readonly retryAfterSeconds: number | null
  /** The refusal is logged as a call too; its id, when the API sent one. */
  readonly callId: number | null

  constructor(status: number, message: string, retryAfterSeconds: number | null, callId: number | null = null) {
    super(message)
    this.name = 'AskRefusal'
    this.status = status
    this.retryAfterSeconds = retryAfterSeconds
    this.callId = callId
  }
}

const field = (body: unknown, key: string): unknown =>
  body && typeof body === 'object' ? (body as Record<string, unknown>)[key] : undefined

/** A refusal body's { retryAfterSeconds }, rounded up; the header wins when both are sent. */
function bodyRetryAfter(body: unknown): number | null {
  const v = num(field(body, 'retryAfterSeconds'))
  return v == null ? null : Math.max(0, Math.ceil(v))
}

function bodyCallId(body: unknown): number | null {
  return num(field(body, 'callId'))
}

/** Retry-After as seconds from now: a number of seconds or an HTTP date; null when absent or unreadable. */
export function retryAfterSeconds(header: string | null | undefined, nowMs: number): number | null {
  if (!header) return null
  const trimmed = header.trim()
  if (/^\d+$/.test(trimmed)) return Number(trimmed)
  const at = Date.parse(trimmed)
  return Number.isNaN(at) ? null : Math.max(0, Math.ceil((at - nowMs) / 1000))
}

function serverText(body: unknown): string {
  if (typeof body === 'string') return body.trim().startsWith('<') ? '' : body.trim()
  if (body && typeof body === 'object') {
    for (const key of ['error', 'message', 'title', 'detail']) {
      const v = (body as Record<string, unknown>)[key]
      if (typeof v === 'string' && v.trim()) return v.trim()
    }
  }
  return ''
}

const withStop = (s: string) => (/[.!?]$/.test(s) ? s : `${s}.`)

/** What a refused question says on screen, in words, with what to do about it. */
export function askErrorText(status: number, body: unknown, retryAfter: number | null): string {
  const said = serverText(body)
  switch (status) {
    case 400:
      return said ? `The API refused the question: ${withStop(said)}` : 'The API refused the question as it was sent.'
    case 401:
      return 'Your session has expired. Please sign in again.'
    case 403:
      return 'Only an admin may ask the AI.'
    case 404:
      return 'The AI endpoints are not on the running API build. Restart the API to pick up the latest code.'
    case 409:
      return `${said ? withStop(said) : 'The Desk Assistant is switched off.'} It can be switched on in the Agents tab.`
    case 429: {
      const wait = retryAfter != null ? ` Try again in ${retryAfter} s.` : ' Try again in a minute.'
      return `${said ? withStop(said) : 'The AI is rate-limited or busy.'}${wait}`
    }
    case 503:
      return "No NVIDIA key is configured on the server, so no model can be asked. It goes in the server's .env as NVIDIA_API_KEY; the desk regenerates its settings on the next deploy."
    default:
      return `The API failed (HTTP ${status})${said ? `: ${withStop(said)}` : '.'}`
  }
}

/**
 * Asks with a streamed answer: POST /api/Ai/ask/stream, then every event to
 * `onEvent` as it arrives. Resolves when the server closes the stream (after
 * `done` or `error`, or early if the connection drops: the caller tells those
 * apart by whether it saw either); rejects with an AskRefusal when the API
 * refused before streaming, and with the fetch's AbortError when `signal`
 * stops it, which also cancels the call on the server.
 *
 * `send` is the session-aware fetch (fresh token, one refresh on a 401);
 * tests pass a stand-in.
 */
export async function streamAsk(
  body: AskBody,
  onEvent: (event: AiStreamEvent) => void,
  signal?: AbortSignal,
  send: (path: string, init: RequestInit) => Promise<Response> = fetchWithSession,
): Promise<void> {
  const response = await send('/api/Ai/ask/stream', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'text/event-stream' },
    body: JSON.stringify(body),
    signal,
  })
  const type = response.headers.get('Content-Type') ?? ''
  if (!response.ok || !type.includes('text/event-stream') || !response.body) {
    const text = await response.text().catch(() => '')
    let parsed: unknown = text
    try {
      parsed = text ? JSON.parse(text) : null
    } catch {
      /* plain text */
    }
    // A 200 that is not a stream is the SPA's index.html: the running API does not know the route.
    const status = response.ok ? 404 : response.status
    const wait = retryAfterSeconds(response.headers.get('Retry-After'), Date.now()) ?? bodyRetryAfter(parsed)
    throw new AskRefusal(status, askErrorText(status, parsed, wait), wait, bodyCallId(parsed))
  }

  const reader = response.body.getReader()
  const decoder = new TextDecoder()
  let carry = ''
  const emit = (messages: SseMessage[]) => {
    for (const m of messages) {
      const event = decodeStreamEvent(m)
      if (event) onEvent(event)
    }
  }
  try {
    for (;;) {
      const { value, done } = await reader.read()
      if (done) break
      const next = parseSse(carry, decoder.decode(value, { stream: true }))
      carry = next.carry
      emit(next.messages)
    }
    emit(parseSse(carry, decoder.decode(), true).messages)
  } finally {
    reader.releaseLock()
  }
}

/** Whether an error is the fetch being stopped on purpose (the Stop button, leaving the page). */
export function isAbort(error: unknown): boolean {
  return (error as { name?: string } | null)?.name === 'AbortError'
}

/** Any failure to ask, in words. */
export function askFailureText(error: unknown): string {
  if (error instanceof AskRefusal) return error.message
  if (error instanceof ApiError) return error.message
  const message = error instanceof Error ? error.message : String(error)
  return `The question did not reach the API: ${message}. Check the connection, and that the API is running.`
}

// ---------- the conversation -----------------------------------------------------

/** The tiers the assistant offers; embed makes vectors, not answers. */
export type AskTier = 'judge' | 'analyst' | 'extract'

export const ASK_TIERS: ReadonlyArray<{ key: AskTier; label: string; hint: string }> = [
  { key: 'judge', label: 'Deep judgement', hint: 'the slowest and most careful' },
  { key: 'analyst', label: 'Analyst', hint: 'reads and explains' },
  { key: 'extract', label: 'Fast', hint: 'short, quick answers' },
]

export function parseAskTier(raw: string | null | undefined): AskTier {
  return raw === 'analyst' || raw === 'extract' ? raw : 'judge'
}

export type TurnStatus = 'streaming' | 'done' | 'error' | 'stopped'

/**
 * What one model round produced on the way to the answer: its reasoning,
 * any text it wrote before asking for tools (its working, not the answer),
 * and the tools it called with what they returned.
 */
export interface TurnRound {
  round: number
  reasoning: string
  working: string
  tools: AiToolStep[]
}

/** Where the last attempt began, so a fallback drops only what came after it. */
interface AttemptMark {
  /** Index into `rounds` of the round the attempt is for. */
  index: number
  reasoning: number
  working: number
  tools: number
}

/** One question and its answer, as the page shows it while it streams and after. */
export interface ChatTurn {
  id: string
  question: string
  tier: AskTier
  status: TurnStatus
  callId: number | null
  chain: string[]
  /** The model being asked now; once done, the one that answered. */
  model: string | null
  attempt: { n: number; of: number } | null
  /** The round being asked now, from 1; 0 before the first attempt. */
  round: number
  /** Every round so far, in order, with its reasoning, working and tool steps. */
  rounds: TurnRound[]
  /** Text streamed in the current round; once done, the answer. */
  answer: string
  mark: AttemptMark | null
  /** Tools called so far; once done, the server's count. */
  toolCalls: number
  /** Rounds in all, from `done`; null until then. */
  roundCount: number | null
  /** One line per model that failed and handed over, in order. */
  notes: string[]
  seconds: number | null
  usage: AiUsage | null
  finishReason: string | null
  fallbacks: number
  error: string | null
  /** A refusal's HTTP status, so the page can point at the fix (409: the Agents tab). */
  errorStatus: number | null
  startedMs: number
}

export interface ChatState {
  conversationId: string
  turns: ChatTurn[]
}

export type ChatAction =
  | { type: 'ask'; id: string; question: string; tier: AskTier; now: number }
  | { type: 'event'; id: string; event: AiStreamEvent }
  | { type: 'refused'; id: string; message: string; status: number | null; callId?: number | null }
  | { type: 'stopped'; id: string }
  /** The stream ended; a turn still streaming lost its connection. */
  | { type: 'closed'; id: string }
  | { type: 'reset'; conversationId: string }

/** 'c-' and something random enough that two tabs never share one. */
export function newConversationId(random: () => number = Math.random): string {
  const part = () => Math.floor(random() * 36 ** 6).toString(36).padStart(6, '0')
  return `c-${part()}${part()}`
}

export function initialChat(conversationId: string = newConversationId()): ChatState {
  return { conversationId, turns: [] }
}

/** "Nemotron 3 Ultra timed out, asking Kimi K3". */
export function fallbackNote(e: { model: string; reason: string; next: string | null }): string {
  const who = modelName(e.model)
  const reason = e.reason.trim().toLowerCase()
  const what =
    reason === 'timeout' || reason === 'timed out'
      ? 'timed out'
      : /^http 429\b/.test(reason)
        ? 'was rate-limited (HTTP 429)'
        : /^http \d+/.test(reason)
          ? `answered ${reason.replace(/^http/, 'HTTP')}`
          : reason
            ? `failed (${e.reason.trim()})`
            : 'failed'
  return e.next ? `${who} ${what}, asking ${modelName(e.next)}` : `${who} ${what}, and no model is left to ask`
}

/** The rounds with `round` present (added at the end when new), and its index. */
function withRound(rounds: readonly TurnRound[], round: number): { rounds: TurnRound[]; index: number } {
  const index = rounds.findIndex((r) => r.round === round)
  if (index >= 0) return { rounds: rounds.slice(), index }
  return { rounds: [...rounds, { round, reasoning: '', working: '', tools: [] }], index: rounds.length }
}

/** Text streamed as the answer that turned out not to be one, moved into round `index`'s working. */
function keepAsWorking(rounds: TurnRound[], index: number, text: string): void {
  if (!text) return
  const r = rounds[index]
  rounds[index] = { ...r, working: r.working ? `${r.working}\n\n${text}` : text }
}

function applyEvent(turn: ChatTurn, e: AiStreamEvent): ChatTurn {
  switch (e.type) {
    case 'start':
      return { ...turn, callId: e.callId ?? turn.callId, chain: e.chain }
    case 'attempt': {
      const { rounds, index } = withRound(turn.rounds, e.round)
      // Text left over from the round before is not this attempt's answer: it was that round's working.
      if (turn.answer) {
        const before = rounds.findIndex((r) => r.round === Math.max(1, turn.round))
        keepAsWorking(rounds, before >= 0 ? before : index, turn.answer)
      }
      const r = rounds[index]
      return {
        ...turn,
        model: e.model,
        attempt: { n: e.n, of: e.of },
        round: e.round,
        rounds,
        answer: '',
        mark: { index, reasoning: r.reasoning.length, working: r.working.length, tools: r.tools.length },
      }
    }
    case 'reasoning': {
      const { rounds, index } = withRound(turn.rounds, Math.max(1, turn.round))
      rounds[index] = { ...rounds[index], reasoning: rounds[index].reasoning + e.text }
      return { ...turn, rounds }
    }
    case 'delta':
      return { ...turn, answer: turn.answer + e.text }
    case 'tool': {
      // The model asked for data, so what it wrote this round was working, not the answer.
      const current = withRound(turn.rounds, Math.max(1, turn.round))
      keepAsWorking(current.rounds, current.index, turn.answer)
      const { rounds, index } = withRound(current.rounds, e.step.round)
      rounds[index] = { ...rounds[index], tools: [...rounds[index].tools, e.step] }
      return { ...turn, rounds, answer: '', toolCalls: turn.toolCalls + 1 }
    }
    case 'fallback': {
      // What the failed model said since its attempt began is dropped; earlier rounds and their tools stay.
      const m = turn.mark
      let rounds = turn.rounds
      if (m && m.index < rounds.length) {
        const r = rounds[m.index]
        rounds = [
          ...rounds.slice(0, m.index),
          { ...r, reasoning: r.reasoning.slice(0, m.reasoning), working: r.working.slice(0, m.working), tools: r.tools.slice(0, m.tools) },
        ]
      } else if (!m && rounds.length > 0) {
        const last = rounds[rounds.length - 1]
        rounds = [...rounds.slice(0, -1), { ...last, reasoning: '', working: '' }]
      }
      return { ...turn, rounds, answer: '', fallbacks: turn.fallbacks + 1, notes: [...turn.notes, fallbackNote(e)] }
    }
    case 'done':
      return {
        ...turn,
        status: 'done',
        callId: e.callId ?? turn.callId,
        model: e.model ?? turn.model,
        seconds: e.seconds,
        usage: e.usage,
        finishReason: e.finishReason,
        fallbacks: Math.max(turn.fallbacks, e.fallbacks),
        toolCalls: Math.max(turn.toolCalls, e.toolCalls),
        roundCount: e.rounds,
      }
    case 'error':
      return { ...turn, status: 'error', callId: e.callId ?? turn.callId, error: e.error, errorStatus: null }
  }
}

export function chatReducer(state: ChatState, action: ChatAction): ChatState {
  if (action.type === 'reset') return initialChat(action.conversationId)
  if (action.type === 'ask') {
    const turn: ChatTurn = {
      id: action.id,
      question: action.question,
      tier: action.tier,
      status: 'streaming',
      callId: null,
      chain: [],
      model: null,
      attempt: null,
      round: 0,
      rounds: [],
      answer: '',
      mark: null,
      toolCalls: 0,
      roundCount: null,
      notes: [],
      seconds: null,
      usage: null,
      finishReason: null,
      fallbacks: 0,
      error: null,
      errorStatus: null,
      startedMs: action.now,
    }
    return { ...state, turns: [...state.turns, turn] }
  }
  const index = state.turns.findIndex((t) => t.id === action.id)
  // A turn from a conversation already reset, or one that has finished: nothing more can change it.
  if (index < 0 || state.turns[index].status !== 'streaming') return state
  const turn = state.turns[index]
  let next: ChatTurn
  switch (action.type) {
    case 'event':
      next = applyEvent(turn, action.event)
      break
    case 'refused':
      next = { ...turn, status: 'error', error: action.message, errorStatus: action.status, callId: action.callId ?? turn.callId }
      break
    case 'stopped':
      next = { ...turn, status: 'stopped' }
      break
    case 'closed':
      next = {
        ...turn,
        status: 'error',
        error: 'The connection closed before the answer finished. The call, and whatever the model sent, is in the Calls tab.',
      }
      break
  }
  const turns = state.turns.slice()
  turns[index] = next
  return { ...state, turns }
}

/** Every round's reasoning, in order: what the model thought on the way to the answer. */
export function turnReasoning(turn: Pick<ChatTurn, 'rounds'>): string {
  return turn.rounds.map((r) => r.reasoning).join('')
}

/** "2 tool calls · 2 rounds", or '' for an answer that needed neither. */
export function toolsLine(toolCalls: number | null | undefined, rounds: number | null | undefined): string {
  const t = toolCalls ?? 0
  const r = rounds ?? 1
  if (t <= 0 && r <= 1) return ''
  const parts: string[] = []
  if (t > 0) parts.push(`${t} tool call${t === 1 ? '' : 's'}`)
  if (r > 1) parts.push(`${r} rounds`)
  return parts.join(' · ')
}

/** What one ask may carry (the API's limits). */
export const MAX_MESSAGES = 40
export const MAX_CHARACTERS = 60_000

/**
 * The messages to send with a new question: every earlier question that got
 * an answer, with that answer, then the new question. A turn that failed or
 * was stopped is left out, so the model never sees a question twice in a row
 * or half an answer. Past the API's limits the oldest turns go first.
 */
export function historyFor(turns: readonly ChatTurn[], question: string): ChatMessage[] {
  const pairs = turns
    .filter((t) => t.status === 'done' && t.answer.trim() !== '')
    .map((t): ChatMessage[] => [
      { role: 'user', content: t.question },
      { role: 'assistant', content: t.answer },
    ])
  const last: ChatMessage = { role: 'user', content: question }
  let size = question.length
  let count = 1
  const kept: ChatMessage[][] = []
  for (let i = pairs.length - 1; i >= 0; i--) {
    const add = pairs[i][0].content.length + pairs[i][1].content.length
    if (count + 2 > MAX_MESSAGES || size + add > MAX_CHARACTERS) break
    kept.unshift(pairs[i])
    size += add
    count += 2
  }
  return [...kept.flat(), last]
}

// ---------- words and formats ----------------------------------------------------

const MODEL_NAMES: Record<string, string> = {
  'nvidia/nemotron-3-ultra-550b-a55b': 'Nemotron 3 Ultra',
  'nvidia/nemotron-3-super-120b-a12b': 'Nemotron 3 Super',
  'nvidia/nemotron-3.5-lightning-30b-a3b': 'Nemotron 3.5 Lightning',
  'nvidia/nemotron-3-embed-1b': 'Nemotron 3 Embed',
  'moonshotai/kimi-k3': 'Kimi K3',
  'z-ai/glm-5.3': 'GLM-5.3',
  'z-ai/glm-5.3-flash': 'GLM-5.3 Flash',
  'deepseek-ai/deepseek-v4.1-flash': 'DeepSeek V4.1 Flash',
}

/** "Nemotron 3 Ultra" for the models the desk uses; any other id without its vendor. */
export function modelName(id: string | null | undefined): string {
  if (!id) return '—'
  const known = MODEL_NAMES[id]
  if (known) return known
  const slash = id.indexOf('/')
  return slash >= 0 && slash < id.length - 1 ? id.slice(slash + 1) : id
}

/** The vendor part of a model id: "nvidia", "moonshotai"; empty when there is none. */
export function modelVendor(id: string): string {
  const slash = id.indexOf('/')
  return slash > 0 ? id.slice(0, slash) : ''
}

/** A model that makes vectors rather than answers: it cannot sit in a chat chain, and only it can sit in embed's. */
export function isEmbeddingModel(id: string): boolean {
  return /embed|rerank/i.test(id)
}

const OUTCOMES: Record<CallOutcome, { label: string; tone: Tone; means: string }> = {
  ok: { label: 'OK', tone: 'pos', means: 'a model answered' },
  failed: { label: 'Failed', tone: 'neg', means: 'every model in the chain failed' },
  refused: { label: 'Refused', tone: 'warn', means: 'not asked: switched off, rate limit, busy or no key' },
  cancelled: { label: 'Cancelled', tone: 'neutral', means: 'the asker stopped it' },
  running: { label: 'Running', tone: 'live', means: 'still waiting for the model' },
}

export const CALL_OUTCOMES = Object.keys(OUTCOMES) as CallOutcome[]

/** A call's outcome as a badge; an outcome this page does not know is shown as sent, in grey. */
export function outcomeBadge(outcome: string | null | undefined): { label: string; tone: Tone; means: string } {
  return OUTCOMES[outcome as CallOutcome] ?? { label: outcome || 'unknown', tone: 'neutral', means: '' }
}

/** One attempt's outcome: "answered", "timed out", "HTTP 429 (rate limit)". */
export function attemptLabel(outcome: string): string {
  const o = outcome.trim().toLowerCase()
  if (o === 'ok') return 'answered'
  if (o === 'timeout') return 'timed out'
  if (/^http 429\b/.test(o)) return 'HTTP 429 (rate limit)'
  if (/^http \d+/.test(o)) return o.replace(/^http/, 'HTTP')
  return outcome || 'unknown'
}

const STATUSES: Record<AgentStatus, { label: string; tone: 'pos' | 'warn' | 'neutral'; means: string }> = {
  on: { label: 'On', tone: 'pos', means: 'built and switched on' },
  off: { label: 'Off', tone: 'warn', means: 'built, switched off' },
  planned: { label: 'Planned', tone: 'neutral', means: 'not built yet' },
}

export const AGENT_STATUSES = Object.keys(STATUSES) as AgentStatus[]

export function agentStatus(status: string | null | undefined): { label: string; tone: 'pos' | 'warn' | 'neutral'; means: string } {
  return STATUSES[status as AgentStatus] ?? { label: status || 'unknown', tone: 'neutral', means: '' }
}

/** Where the catalog came from, said plainly. */
export function catalogSourceText(source: string): { label: string; tone: Tone } {
  if (source === 'provider') return { label: 'fresh from the provider', tone: 'pos' }
  if (source === 'cache') return { label: 'cached (the last good list, up to 10 minutes old)', tone: 'neutral' }
  if (source === 'unavailable') return { label: 'provider unavailable: only the models the tiers name', tone: 'warn' }
  return { label: source || 'unknown', tone: 'neutral' }
}

const grouped = new Intl.NumberFormat('en-IN')

/** Tokens with Indian grouping; "—" when not known (a refused call has none). */
export function formatTokens(n: number | null | undefined): string {
  return n == null || !Number.isFinite(n) ? '—' : grouped.format(Math.round(n))
}

/** "0.8 s", "8.2 s", "42 s", "1m 32s"; "—" when not known. */
export function formatSeconds(s: number | null | undefined): string {
  if (s == null || !Number.isFinite(s) || s < 0) return '—'
  if (s < 10) return `${s.toFixed(1)} s`
  if (s < 60) return `${Math.round(s)} s`
  const whole = Math.round(s)
  return `${Math.floor(whole / 60)}m ${String(whole % 60).padStart(2, '0')}s`
}

const IST = 'Asia/Kolkata'
const istDayFormat = new Intl.DateTimeFormat('en-CA', { timeZone: IST, year: 'numeric', month: '2-digit', day: '2-digit' })
const istClock = new Intl.DateTimeFormat('en-GB', { timeZone: IST, hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false })
const istDayMonth = new Intl.DateTimeFormat('en-GB', { timeZone: IST, day: 'numeric', month: 'short' })
const istHourMinute = new Intl.DateTimeFormat('en-GB', { timeZone: IST, hour: '2-digit', minute: '2-digit', hour12: false })

/** The IST calendar day of an instant, "2026-09-30". */
export function istDay(ms: number): string {
  return istDayFormat.format(new Date(ms))
}

/**
 * A call's time in IST: "16:02:11" today, "29 Sep 16:02" on an earlier day,
 * "—" when there is none. "Today" is the IST day, as the API counts it.
 */
export function callTime(iso: string | null | undefined, nowMs: number): string {
  if (!iso) return '—'
  const ms = Date.parse(iso)
  if (Number.isNaN(ms)) return '—'
  const d = new Date(ms)
  return istDay(ms) === istDay(nowMs) ? istClock.format(d) : `${istDayMonth.format(d)} ${istHourMinute.format(d)}`
}

/** An instant's IST clock without seconds: "15:30" today, "29 Sep 15:30" on an earlier day, "—" when there is none. */
export function clockTime(iso: string | null | undefined, nowMs: number): string {
  if (!iso) return '—'
  const ms = Date.parse(iso)
  if (Number.isNaN(ms)) return '—'
  const d = new Date(ms)
  return istDay(ms) === istDay(nowMs) ? istHourMinute.format(d) : `${istDayMonth.format(d)} ${istHourMinute.format(d)}`
}

// ---------- tools ---------------------------------------------------------------

/** The Desk Assistant's tools as the console names them. */
const TOOL_NAMES: Record<string, string> = {
  get_runs: 'Runs',
  get_open_positions: 'Open positions',
  get_quotes: 'Quotes',
  get_incidents: 'Incidents',
  get_latest_checkup: 'Latest checkup',
  get_forecasts: 'Forecasts',
}

/** Tools whose main argument names the thing read, so it joins the label: "Run #412", "Option chain NIFTY". */
const TOOL_SUBJECTS: Record<string, { keys: string[]; label: (value: string) => string; bare: string }> = {
  get_run: { keys: ['runId', 'run_id', 'id'], label: (v) => `Run #${v}`, bare: 'Run' },
  get_option_chain_summary: { keys: ['underlying', 'symbol'], label: (v) => `Option chain ${v}`, bare: 'Option chain' },
  get_news: { keys: ['query', 'q', 'symbol'], label: (v) => `News ${v}`, bare: 'News' },
  get_strategy_spec: { keys: ['strategy', 'name'], label: (v) => `Strategy spec ${v}`, bare: 'Strategy spec' },
}

/** The arguments the model wrote, when they are a JSON object; null otherwise. */
export function parseToolArgs(text: string | null | undefined): Record<string, unknown> | null {
  if (!text || !text.trim()) return {}
  try {
    const v = JSON.parse(text) as unknown
    return v && typeof v === 'object' && !Array.isArray(v) ? (v as Record<string, unknown>) : null
  } catch {
    return null
  }
}

function argText(value: unknown): string {
  if (value == null) return 'none'
  if (typeof value === 'string') return value
  if (typeof value === 'number' || typeof value === 'boolean') return String(value)
  if (Array.isArray(value)) return value.map(argText).join(', ')
  return JSON.stringify(value)
}

/**
 * A tool call in words: its label ("Runs", "Run #412", "News HDFC Bank") and
 * the other arguments ("date: today"). An unknown tool keeps its own name;
 * arguments that are not a JSON object are shown as written, cut short.
 */
export function toolLabel(name: string, argumentsJson: string | null | undefined): { label: string; detail: string } {
  const args = parseToolArgs(argumentsJson)
  const subject = TOOL_SUBJECTS[name]
  const base = TOOL_NAMES[name] ?? subject?.bare ?? name
  if (args == null) {
    const raw = (argumentsJson ?? '').trim()
    return { label: base, detail: raw.length > 80 ? `${raw.slice(0, 79)}…` : raw }
  }
  let label = base
  const rest = { ...args }
  if (subject) {
    const key = subject.keys.find((k) => rest[k] != null && argText(rest[k]).trim() !== '')
    if (key) {
      label = subject.label(argText(rest[key]))
      delete rest[key]
    }
  }
  const detail = Object.entries(rest)
    .filter(([, v]) => v != null && v !== '')
    .map(([k, v]) => `${k}: ${argText(v)}`)
    .join(', ')
  return { label, detail }
}

/**
 * A JSON text laid out to read, or the text as it is when it is not JSON (a
 * result cut at 16,000 characters, say): never an error, and never a guess.
 */
export function prettyJson(text: string | null | undefined): { text: string; json: boolean } {
  const t = (text ?? '').trim()
  if (!t) return { text: '', json: false }
  try {
    return { text: JSON.stringify(JSON.parse(t), null, 2), json: true }
  } catch {
    return { text: text ?? '', json: false }
  }
}

/** "3 rows", "1 row"; '' when the tool did not say. */
export function rowsText(rows: number | null | undefined): string {
  return rows == null ? '' : `${grouped.format(rows)} row${rows === 1 ? '' : 's'}`
}

// ---------- chains ------------------------------------------------------------

export const MAX_CHAIN = 5

/** Moves the model at `index` one place up (-1) or down (+1); out of range leaves the chain as it is. */
export function moveInChain(chain: readonly string[], index: number, delta: -1 | 1): string[] {
  const to = index + delta
  if (index < 0 || index >= chain.length || to < 0 || to >= chain.length) return [...chain]
  const next = [...chain]
  ;[next[index], next[to]] = [next[to], next[index]]
  return next
}

/** Why a chain cannot be saved, or null when it can. The server checks again; this only saves a round trip. */
export function chainProblem(chain: readonly string[], embedTier: boolean): string | null {
  if (chain.length === 0) return 'A chain needs at least one model.'
  if (chain.length > MAX_CHAIN) return `A chain holds at most ${MAX_CHAIN} models.`
  if (new Set(chain).size !== chain.length) return 'A model appears twice.'
  if (embedTier && chain.some((m) => !isEmbeddingModel(m))) return 'The embed tier takes embedding models only.'
  if (!embedTier && chain.some(isEmbeddingModel)) return 'An embedding model cannot answer a question.'
  return null
}

export function sameChain(a: readonly string[], b: readonly string[]): boolean {
  return a.length === b.length && a.every((m, i) => m === b[i])
}

/**
 * The catalog as the Models tab lists it: the models in use first (as the
 * API sorts them), then the rest grouped by vendor, each filtered by `query`
 * (id, vendor or short name). Vendors in alphabetical order, models by id.
 */
export function catalogGroups(models: readonly AiModel[], query: string): { inUse: AiModel[]; vendors: { vendor: string; models: AiModel[] }[] } {
  const q = query.trim().toLowerCase()
  const match = (m: AiModel) =>
    !q || m.id.toLowerCase().includes(q) || (m.ownedBy ?? '').toLowerCase().includes(q) || modelName(m.id).toLowerCase().includes(q)
  const inUse = models.filter((m) => m.inUse && match(m))
  const byVendor = new Map<string, AiModel[]>()
  for (const m of models) {
    if (m.inUse || !match(m)) continue
    const vendor = m.ownedBy || modelVendor(m.id) || 'other'
    byVendor.set(vendor, [...(byVendor.get(vendor) ?? []), m])
  }
  const vendors = [...byVendor.entries()]
    .sort(([a], [b]) => a.localeCompare(b))
    .map(([vendor, ms]) => ({ vendor, models: ms.sort((a, b) => a.id.localeCompare(b.id)) }))
  return { inUse, vendors }
}

// ---------- the answer's text -----------------------------------------------------

export type Inline =
  | { kind: 'text'; text: string }
  | { kind: 'bold'; children: Inline[] }
  | { kind: 'em'; children: Inline[] }
  | { kind: 'code'; text: string }
  | { kind: 'break' }

export interface ListItem {
  /** 0 for a top-level item, more for an indented one. */
  depth: number
  /** "•", or the number as the model wrote it: "1.", "2)". */
  marker: string
  inlines: Inline[]
}

export type Block =
  | { kind: 'para'; inlines: Inline[] }
  | { kind: 'heading'; level: number; inlines: Inline[] }
  | { kind: 'list'; ordered: boolean; items: ListItem[] }
  | { kind: 'code'; lang: string; text: string }
  | { kind: 'table'; head: Inline[][]; rows: Inline[][][] }
  | { kind: 'quote'; inlines: Inline[] }
  | { kind: 'rule' }

const FENCE = /^\s{0,3}(`{3,}|~{3,})\s*([\w+#.-]*)\s*$/
const HEADING = /^\s{0,3}(#{1,6})\s+(.*?)\s*#*\s*$/
const RULE = /^\s{0,3}([-*_])(\s*\1){2,}\s*$/
const ITEM = /^(\s*)([-*+•]|\d{1,3}[.)])\s+(.*)$/
const QUOTE = /^\s{0,3}>\s?(.*)$/
const TABLE_RULE = /^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$/

const startsBlock = (line: string, next: string | undefined) =>
  FENCE.test(line) || HEADING.test(line) || RULE.test(line) || ITEM.test(line) || QUOTE.test(line) || isTableStart(line, next)

function isTableStart(line: string, next: string | undefined): boolean {
  return line.includes('|') && next !== undefined && TABLE_RULE.test(next) && next.includes('-')
}

function tableCells(line: string): string[] {
  let s = line.trim()
  if (s.startsWith('|')) s = s.slice(1)
  if (s.endsWith('|')) s = s.slice(0, -1)
  return s.split('|').map((c) => c.trim())
}

/** Lines of one paragraph, their line breaks kept: a model's single newlines are usually meant. */
function joinLines(lines: string[]): Inline[] {
  return lines.flatMap((line, i) => (i === 0 ? parseInline(line.trim()) : [{ kind: 'break' } as Inline, ...parseInline(line.trim())]))
}

/**
 * An answer's markdown as blocks: paragraphs, headings, bullet and numbered
 * lists (indented items keep their depth), fenced code, simple tables,
 * quotes and rules. Anything else is text. A fence still open at the end (an
 * answer mid-stream) is code up to where the text stops.
 */
export function parseAnswer(text: string): Block[] {
  const lines = text.replace(/\r\n?/g, '\n').split('\n')
  const blocks: Block[] = []
  let i = 0
  while (i < lines.length) {
    const line = lines[i]
    if (line.trim() === '') {
      i++
      continue
    }

    const fence = FENCE.exec(line)
    if (fence) {
      const mark = fence[1]
      const body: string[] = []
      i++
      while (i < lines.length && !new RegExp(`^\\s{0,3}${mark[0] === '`' ? '`' : '~'}{${mark.length},}\\s*$`).test(lines[i])) {
        body.push(lines[i])
        i++
      }
      i++ // the closing fence, if there was one
      blocks.push({ kind: 'code', lang: fence[2] ?? '', text: body.join('\n') })
      continue
    }

    const heading = HEADING.exec(line)
    if (heading) {
      blocks.push({ kind: 'heading', level: heading[1].length, inlines: parseInline(heading[2]) })
      i++
      continue
    }

    if (RULE.test(line)) {
      blocks.push({ kind: 'rule' })
      i++
      continue
    }

    if (isTableStart(line, lines[i + 1])) {
      const head = tableCells(line).map(parseInline)
      const rows: Inline[][][] = []
      i += 2
      while (i < lines.length && lines[i].trim() !== '' && lines[i].includes('|')) {
        rows.push(tableCells(lines[i]).map(parseInline))
        i++
      }
      blocks.push({ kind: 'table', head, rows })
      continue
    }

    if (QUOTE.test(line)) {
      const body: string[] = []
      while (i < lines.length && QUOTE.test(lines[i])) {
        body.push(QUOTE.exec(lines[i])![1])
        i++
      }
      blocks.push({ kind: 'quote', inlines: joinLines(body) })
      continue
    }

    const first = ITEM.exec(line)
    if (first) {
      const ordered = /\d/.test(first[2])
      const items: ListItem[] = []
      const base = first[1].length
      while (i < lines.length) {
        const current = lines[i]
        const m = ITEM.exec(current)
        // A top-level item of the other kind (bullets after numbers) starts a list of its own.
        if (m && m[1].length <= base && /\d/.test(m[2]) !== ordered) break
        if (m) {
          const depth = Math.min(3, Math.max(0, Math.floor((m[1].length - base) / 2)))
          const marker = /\d/.test(m[2]) ? m[2] : '•'
          items.push({ depth, marker, inlines: parseInline(m[3].trim()) })
          i++
          continue
        }
        if (current.trim() === '') {
          // A blank line inside a list: the list goes on only if another item follows.
          let j = i + 1
          while (j < lines.length && lines[j].trim() === '') j++
          const following = j < lines.length ? ITEM.exec(lines[j]) : null
          if (following && (following[1].length > base || /\d/.test(following[2]) === ordered)) {
            i = j
            continue
          }
          break
        }
        // An indented line carries on the item above it.
        if (/^\s+/.test(current) && items.length > 0 && !startsBlock(current, lines[i + 1])) {
          const last = items[items.length - 1]
          last.inlines = [...last.inlines, { kind: 'break' }, ...parseInline(current.trim())]
          i++
          continue
        }
        break
      }
      blocks.push({ kind: 'list', ordered, items })
      continue
    }

    const body: string[] = [line]
    i++
    while (i < lines.length && lines[i].trim() !== '' && !startsBlock(lines[i], lines[i + 1])) {
      body.push(lines[i])
      i++
    }
    blocks.push({ kind: 'para', inlines: joinLines(body) })
  }
  return blocks
}

const PUNCTUATION = /[!"#$%&'()*+,\-./:;<=>?@[\\\]^_`{|}~]/

/**
 * One line's inline markdown: `code`, **bold** or __bold__, *italic* or
 * _italic_ (an underscore inside a word, as in snake_case, stays text), and
 * backslash escapes. An opener with no closer is text, which is also how an
 * answer reads while its closing marker is still on the way.
 */
export function parseInline(s: string): Inline[] {
  const out: Inline[] = []
  let buf = ''
  const flush = () => {
    if (buf) out.push({ kind: 'text', text: buf })
    buf = ''
  }
  let i = 0
  while (i < s.length) {
    const c = s[i]
    if (c === '\\' && i + 1 < s.length && PUNCTUATION.test(s[i + 1])) {
      buf += s[i + 1]
      i += 2
      continue
    }
    if (c === '`') {
      let ticks = 1
      while (s[i + ticks] === '`') ticks++
      const fence = '`'.repeat(ticks)
      const end = s.indexOf(fence, i + ticks)
      if (end !== -1) {
        flush()
        const inner = s.slice(i + ticks, end)
        out.push({ kind: 'code', text: inner.length > 2 && inner.startsWith(' ') && inner.endsWith(' ') ? inner.slice(1, -1) : inner })
        i = end + ticks
        continue
      }
      buf += fence
      i += ticks
      continue
    }
    if ((c === '*' || c === '_') && s[i + 1] === c) {
      const mark = c + c
      const end = s.indexOf(mark, i + 2)
      const inner = end > i + 2 ? s.slice(i + 2, end) : ''
      if (inner && !/^\s/.test(inner) && !/\s$/.test(inner)) {
        flush()
        out.push({ kind: 'bold', children: parseInline(inner) })
        i = end + 2
        continue
      }
      buf += mark
      i += 2
      continue
    }
    if (c === '*' || c === '_') {
      const wordBefore = i > 0 && /[\p{L}\p{N}]/u.test(s[i - 1])
      if (!(c === '_' && wordBefore)) {
        let end = s.indexOf(c, i + 1)
        // A closing underscore inside a word is not one either.
        while (c === '_' && end !== -1 && end + 1 < s.length && /[\p{L}\p{N}]/u.test(s[end + 1])) end = s.indexOf(c, end + 1)
        const inner = end > i + 1 ? s.slice(i + 1, end) : ''
        if (inner && !/^\s/.test(inner) && !/\s$/.test(inner)) {
          flush()
          out.push({ kind: 'em', children: parseInline(inner) })
          i = end + 1
          continue
        }
      }
    }
    buf += c
    i++
  }
  flush()
  return out
}
