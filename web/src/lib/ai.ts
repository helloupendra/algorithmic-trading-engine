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
 *   - the agents the owner can talk with (the Desk Assistant and the four
 *     others, each as itself), one conversation per agent;
 *   - the agents' memory: notes, corrections and approved lessons, the 👍/👎
 *     on answers, and whether the check's score moves with them;
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
  /** How each model a chat tier names has been answering; empty from an API without model health. */
  health: AiModelHealth[]
}

/** healthy; failed (its last call failed); cooling (asked after the healthy ones until coolingUntilUtc); unknown (not asked yet). */
export type ModelHealthState = 'healthy' | 'failed' | 'cooling' | 'unknown'

/**
 * A model's health as the router sees it. A model that failed after a long
 * wait, or twice running, cools for 10, 20, 40, then 60 minutes: it is still
 * asked, but after its chain's healthy models. One answer heals it; a probe
 * asks it one tiny question when its cooling ends.
 */
export interface AiModelHealth {
  model: string
  state: ModelHealthState | string
  coolingUntilUtc: string | null
  consecutiveFailures: number
  lastFailure: string | null
  lastFailureUtc: string | null
  lastOkSeconds: number | null
  lastOkUtc: string | null
  lastProbeUtc: string | null
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
  /** Whether the owner can talk with it on AI → Assistant: the Desk Assistant, or a built agent with a chat persona. */
  chat: boolean
  /** What it reads when the owner talks with it: its own work first. Empty when it cannot be talked with. */
  chatTools: AiAgentTool[]
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
  /** For a model in use; null for one the catalog only lists. */
  health: AiModelHealth | null
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
  /** The owner's 👍 (1) or 👎 (-1) on the answer; null when none was given. */
  feedback: FeedbackScore | null
  /** Memories the answer was given; null from an API without memory. */
  memoryCount: number | null
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

/** The detail's feedback is the whole record; the list row carries only its score. */
export interface AiCallDetail extends Omit<AiCallSummary, 'feedback'> {
  /** Who said 👍 or 👎, when, and the correction written with a 👎; null when none was given. */
  feedback: AiCallFeedback | null
  /** The memories the answer was given, by id; null from an API without memory. */
  memoryIds: number[] | null
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
    health: list<AiModelHealth>(o.health).map(readHealth),
  }
}

function readHealth(h: AiModelHealth): AiModelHealth {
  return {
    model: h.model,
    state: h.state ?? 'unknown',
    coolingUntilUtc: h.coolingUntilUtc ?? null,
    consecutiveFailures: h.consecutiveFailures ?? 0,
    lastFailure: h.lastFailure ?? null,
    lastFailureUtc: h.lastFailureUtc ?? null,
    lastOkSeconds: h.lastOkSeconds ?? null,
    lastOkUtc: h.lastOkUtc ?? null,
    lastProbeUtc: h.lastProbeUtc ?? null,
  }
}

export function readAgents(raw: unknown): AiAgentsResponse {
  const o = need<AiAgentsResponse>(raw, ['agents'], 'agent list')
  return {
    agents: list<AiAgent>(o.agents).map((a) => {
      // An API from before the other agents could be talked with: only the Desk Assistant, with its own tools.
      const desk = a.key === DESK_ASSISTANT
      const chatTools = a.chatTools === undefined ? (desk ? a.tools : []) : a.chatTools
      return { ...a, tools: list(a.tools), chat: a.chat ?? desk, chatTools: list(chatTools) }
    }),
    ruleBased: list(o.ruleBased),
  }
}

export function readModels(raw: unknown): AiModelsResponse {
  const o = need<AiModelsResponse>(raw, ['models'], 'model catalog')
  return {
    ...o,
    models: list<AiModel>(o.models).map((m) => ({
      ...m,
      embedding: m.embedding ?? isEmbeddingModel(m.id),
      listed: m.listed ?? true,
      health: m.health ? readHealth(m.health) : null,
    })),
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
  return {
    calls: list<AiCallSummary>(o.calls).map((c) => ({ ...withRounds(c), feedback: feedbackScore(c.feedback), memoryCount: num(c.memoryCount) })),
    nextBeforeId: o.nextBeforeId ?? null,
  }
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
    memoryCount: num(o.memoryCount),
    memoryIds: memoryIds(o.memoryIds),
    feedback: readCallFeedback(o.feedback),
  }
}

// ---------- queries ---------------------------------------------------------

export interface AiCallFilters {
  agent?: string
  outcome?: string
  model?: string
  /** console, api, schedule, check, health, index or telegram. */
  source?: string
  take?: number
}

/** GET /api/Ai/calls's query string; empty filters are left out, not sent blank. */
export function aiCallsQuery(filters: AiCallFilters, beforeId: number | null = null): string {
  const p = new URLSearchParams()
  if (filters.agent) p.set('agent', filters.agent)
  if (filters.outcome) p.set('outcome', filters.outcome)
  if (filters.model) p.set('model', filters.model)
  if (filters.source) p.set('source', filters.source)
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

// ---------- reports (the scheduled agents) ----------------------------------------

/** run, news, filing or incident; kept a string so a subject the server adds later still shows. */
export type ReportSubjectType = string
/** ok: written and its check passed; invalid: answered but failed its check; failed: no model answered. */
export type ReportStatus = 'ok' | 'invalid' | 'failed'

export interface AiReportSummary {
  id: number
  agentKey: string
  agentName: string
  subjectType: ReportSubjectType
  subjectId: string
  /** The IST trading day it is about, "yyyy-MM-dd"; null when it is about no one day. */
  sessionDate: string | null
  createdUtc: string
  updatedUtc: string | null
  status: ReportStatus | string
  attempts: number
  callId: number | null
  model: string | null
  title: string
  /** Why it is invalid or failed; null when ok. */
  error: string | null
  /** A console path (a run, an incident) or an https URL (the article, the filing); null when there is none. */
  link: string | null
}

export interface AiReportsPage {
  reports: AiReportSummary[]
  nextBeforeId: number | null
}

export interface AiReportDetail {
  report: AiReportSummary
  /** Markdown; for an invalid report, the model's raw text. */
  body: string
  data: Record<string, unknown> | null
}

export interface AiReportDay {
  date: string
  ok: number
  invalid: number
  failed: number
}

export interface AiReportAgentStats {
  agentKey: string
  agentName: string
  total: number
  ok: number
  invalid: number
  failed: number
  /** ok / (ok + invalid) × 100; null when there were none of either. */
  validPercent: number | null
  days: AiReportDay[]
}

export interface AiReportStats {
  since: string
  days: number
  agents: AiReportAgentStats[]
}

/** The Trade Reviewer's verdict on a run against its written spec. */
export interface TradeReview {
  verdict: string
  followed: string[]
  deviations: string[]
  staleFills: number | null
  marketContext: string | null
  lesson: string | null
}

export interface NewsNumber {
  what: string
  value: string
  unit: string
  /** The words in the item the number was read from; the check requires them to be there. */
  quote: string
}

/** The News Analyst's structured event for one headline or filing. */
export interface NewsEvent {
  event: string
  direction: string
  symbols: string[]
  numbers: NewsNumber[]
  /** 0 to 1; null when not given. */
  confidence: number | null
  summary: string
}

/** The Incident Explainer's judgement of how soon a person is needed. */
export interface IncidentExplanation {
  urgency: string
  confidence: number | null
}

/**
 * The agents that run on a schedule, with what "Run now" may name and what
 * they write: reports (the Reports tab), or for the AI Trader its own
 * decisions (`decisions: true`), listed on its panel on AI → Agents.
 */
export const SCHEDULED_AGENTS: Readonly<Record<string, { subject: 'run' | 'incident' | null; writes: string; decisions?: true }>> = {
  'trade-reviewer': { subject: 'run', writes: 'after 15:45 IST, a review of each run stopped that day' },
  'news-analyst': { subject: null, writes: "every 10 minutes, a structured event for each unread news item or filing of the last 24 hours" },
  'incident-explainer': { subject: 'incident', writes: 'for each live incident of medium severity or worse, what happened, why and what to do' },
  'assistant-check': { subject: null, writes: "on weekdays after 16:40 IST, the Desk Assistant's graded answers to questions the code knows" },
  'assistant-exam': { subject: null, writes: 'on Sundays from 10:30 IST, the Desk Assistant\'s score on a fixed bank of questions about finished days' },
  'ai-trader': {
    subject: null,
    writes: 'every 10 minutes 09:20–15:00 IST on trading days, a decision with the brief it read (shadow: nothing is placed)',
    decisions: true,
  },
}

/** The utilities that call models without being agents, by the key their calls and reports carry. */
export const AI_UTILITIES: Readonly<Record<string, string>> = {
  'assistant-check': 'Assistant check',
  'assistant-exam': 'Assistant exam',
  'doc-index': 'Docs index',
  'model-test': 'Model test',
}

/** What a call's source says, in words: where the question came from. */
const SOURCES: Record<string, string> = {
  console: 'Console',
  api: 'API',
  schedule: 'Schedule',
  check: 'Assistant check',
  exam: 'Assistant exam',
  health: 'Health probe',
  index: 'Docs index',
  telegram: 'Telegram',
  preview: 'AI Trader look now',
}

export const CALL_SOURCES = Object.keys(SOURCES)

export function sourceLabel(source: string | null | undefined): string {
  return SOURCES[source ?? ''] ?? (source || 'unknown')
}

export function readReportsPage(raw: unknown): AiReportsPage {
  const o = need<AiReportsPage>(raw, ['reports'], 'report list')
  return { reports: list<AiReportSummary>(o.reports).map(readReportSummary), nextBeforeId: o.nextBeforeId ?? null }
}

function readReportSummary(r: AiReportSummary): AiReportSummary {
  return {
    ...r,
    subjectId: r.subjectId == null ? '' : String(r.subjectId),
    sessionDate: r.sessionDate ?? null,
    updatedUtc: r.updatedUtc ?? null,
    attempts: r.attempts ?? 1,
    callId: r.callId ?? null,
    model: r.model ?? null,
    title: r.title ?? '',
    error: r.error ?? null,
    link: r.link ?? null,
  }
}

export function readReport(raw: unknown): AiReportDetail {
  const o = need<AiReportDetail>(raw, ['report'], 'report')
  const data = o.data && typeof o.data === 'object' && !Array.isArray(o.data) ? o.data : null
  return { report: readReportSummary(need<AiReportSummary>(o.report, ['id', 'agentKey'], 'report')), body: typeof o.body === 'string' ? o.body : '', data }
}

export function readReportStats(raw: unknown): AiReportStats {
  const o = need<AiReportStats>(raw, ['agents'], 'report statistics')
  return {
    ...o,
    agents: list<AiReportAgentStats>(o.agents).map((a) => ({ ...a, validPercent: a.validPercent ?? null, days: list(a.days) })),
  }
}

const strings = (v: unknown): string[] => list<unknown>(v).filter((x): x is string => typeof x === 'string' && x.trim() !== '')

/** A report's data read as a trade review; missing lists are empty, missing figures null. */
export function readTradeReview(data: Record<string, unknown> | null): TradeReview | null {
  if (!data) return null
  return {
    verdict: str(data.verdict) ?? 'unclear',
    followed: strings(data.followed),
    deviations: strings(data.deviations),
    staleFills: num(data.staleFills),
    marketContext: str(data.marketContext),
    lesson: str(data.lesson),
  }
}

export function readNewsEvent(data: Record<string, unknown> | null): NewsEvent | null {
  if (!data) return null
  const text = (v: unknown) => (typeof v === 'string' ? v : typeof v === 'number' ? String(v) : '')
  return {
    event: str(data.event) ?? 'other',
    direction: str(data.direction) ?? 'unclear',
    symbols: strings(data.symbols),
    numbers: list<Record<string, unknown>>(data.numbers)
      .filter((n) => n && typeof n === 'object')
      .map((n) => ({ what: text(n.what), value: text(n.value), unit: text(n.unit), quote: text(n.quote) })),
    confidence: num(data.confidence),
    summary: str(data.summary) ?? '',
  }
}

export function readIncidentExplanation(data: Record<string, unknown> | null): IncidentExplanation | null {
  if (!data) return null
  return { urgency: str(data.urgency) ?? 'unclear', confidence: num(data.confidence) }
}

export interface AiReportFilters {
  agent?: string
  subjectType?: string
  subjectId?: string
  status?: string
  /** An IST day, "yyyy-MM-dd". */
  date?: string
  take?: number
}

/** GET /api/Ai/reports's query string; empty filters are left out. */
export function reportsQuery(filters: AiReportFilters, beforeId: number | null = null): string {
  const p = new URLSearchParams()
  if (filters.agent) p.set('agent', filters.agent)
  if (filters.subjectType) p.set('subjectType', filters.subjectType)
  if (filters.subjectId) p.set('subjectId', filters.subjectId)
  if (filters.status) p.set('status', filters.status)
  if (filters.date) p.set('date', filters.date)
  p.set('take', String(filters.take ?? 50))
  if (beforeId != null) p.set('beforeId', String(beforeId))
  return p.toString()
}

/** The reports, newest first, a page at a time; re-read every 30 s while the tab is open. */
export function useAiReports(filters: AiReportFilters) {
  return useInfiniteQuery({
    queryKey: ['ai', 'reports', filters],
    queryFn: async ({ pageParam }: { pageParam: number | null }) =>
      readReportsPage(await api.get<unknown>(`/api/Ai/reports?${reportsQuery(filters, pageParam)}`)),
    initialPageParam: null as number | null,
    getNextPageParam: (last: AiReportsPage) => last.nextBeforeId ?? undefined,
    placeholderData: keepPreviousData,
    refetchInterval: 30_000,
  })
}

export function useAiReport(id: number | null) {
  return useQuery({
    queryKey: ['ai', 'report', id],
    queryFn: async () => readReport(await api.get<unknown>(`/api/Ai/reports/${id}`)),
    enabled: id != null,
    // A report is rewritten only by a retry after a failure; a minute is fresh enough.
    staleTime: 60_000,
  })
}

export function useAiReportStats(days = 7) {
  return useQuery({
    queryKey: ['ai', 'report-stats', days],
    queryFn: async () => readReportStats(await api.get<unknown>(`/api/Ai/reports/stats?days=${days}`)),
    refetchInterval: 60_000,
  })
}

/**
 * The newest report an agent wrote about one subject, in full, or null when
 * it has written none. For a panel on the subject's own page (a run's
 * review): the list names it, the detail carries the body. Admin-only on the
 * server, so the caller enables it for an admin only.
 */
export function useLatestReport(agent: string, subjectType: string, subjectId: string | null, enabled: boolean) {
  return useQuery({
    queryKey: ['ai', 'latest-report', agent, subjectType, subjectId],
    queryFn: async () => {
      const page = readReportsPage(
        await api.get<unknown>(`/api/Ai/reports?${reportsQuery({ agent, subjectType, subjectId: subjectId ?? '', take: 1 })}`),
      )
      const first = page.reports[0]
      return first ? readReport(await api.get<unknown>(`/api/Ai/reports/${first.id}`)) : null
    },
    enabled: enabled && subjectId != null,
    staleTime: 60_000,
    retry: 1,
  })
}

/** The Incident Explainer's newest reports by incident id, for the Incidents page (admin-only). */
export function useIncidentExplanations(enabled = true) {
  return useQuery({
    queryKey: ['ai', 'reports', 'incident-explainer', 'by-incident'],
    queryFn: async () => {
      const page = readReportsPage(await api.get<unknown>(`/api/Ai/reports?${reportsQuery({ agent: 'incident-explainer', subjectType: 'incident', take: 200 })}`))
      // Newest first, so the first report seen for an incident is its latest.
      const byIncident = new Map<string, AiReportSummary>()
      for (const r of page.reports) if (!byIncident.has(r.subjectId)) byIncident.set(r.subjectId, r)
      return byIncident
    },
    enabled,
    refetchInterval: 60_000,
    retry: 1,
  })
}

/** POST /api/Ai/agents/{key}/run: start a scheduled agent now; the report arrives later. */
export function useRunAgent() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ key, subjectId }: { key: string; subjectId: string | null }) =>
      api.post<{ started: boolean; agent: string; subjectId: string | null }>(`/api/Ai/agents/${encodeURIComponent(key)}/run`, {
        subjectId: subjectId && subjectId.trim() ? subjectId.trim() : null,
      }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['ai', 'reports'] }),
  })
}

const REPORT_STATUSES: Record<ReportStatus, { label: string; tone: Tone; means: string }> = {
  ok: { label: 'OK', tone: 'pos', means: 'written, and its check passed' },
  invalid: { label: 'Invalid', tone: 'warn', means: 'the model answered, but the answer failed its check' },
  failed: { label: 'Failed', tone: 'neg', means: 'no model answered; retried after 15 minutes, up to 3 attempts' },
}

export const REPORT_STATUS_KEYS = Object.keys(REPORT_STATUSES) as ReportStatus[]

export function reportStatus(status: string | null | undefined): { label: string; tone: Tone; means: string } {
  return REPORT_STATUSES[status as ReportStatus] ?? { label: status || 'unknown', tone: 'neutral', means: '' }
}

const VERDICTS: Record<string, { label: string; tone: Tone }> = {
  followed: { label: 'Followed the spec', tone: 'pos' },
  deviated: { label: 'Deviated from the spec', tone: 'neg' },
  unclear: { label: 'Unclear', tone: 'neutral' },
}
const DIRECTIONS: Record<string, { label: string; tone: Tone }> = {
  positive: { label: 'Positive', tone: 'pos' },
  negative: { label: 'Negative', tone: 'neg' },
  neutral: { label: 'Neutral', tone: 'neutral' },
  unclear: { label: 'Unclear', tone: 'neutral' },
}
const URGENCIES: Record<string, { label: string; tone: Tone }> = {
  now: { label: 'Act now', tone: 'neg' },
  today: { label: 'Today', tone: 'warn' },
  later: { label: 'Later', tone: 'neutral' },
  unclear: { label: 'Unclear', tone: 'neutral' },
}
const unknownWord = (v: string | null | undefined) => ({ label: v || 'unknown', tone: 'neutral' as Tone })

export function verdictBadge(v: string | null | undefined): { label: string; tone: Tone } {
  return VERDICTS[v ?? ''] ?? unknownWord(v)
}
export function directionBadge(v: string | null | undefined): { label: string; tone: Tone } {
  return DIRECTIONS[v ?? ''] ?? unknownWord(v)
}
export function urgencyBadge(v: string | null | undefined): { label: string; tone: Tone } {
  return URGENCIES[v ?? ''] ?? unknownWord(v)
}

/** The News Analyst's target share of reports that pass their check. */
export const NEWS_VALID_TARGET = 98

/**
 * How a valid share reads against its target: at or above it is fine, up to
 * eight points under is a warning, further under is a problem; unknown (no
 * report yet) is plain, not green.
 */
export function validityTone(percent: number | null | undefined, target = NEWS_VALID_TARGET): 'pos' | 'warn' | 'neg' | 'neutral' {
  if (percent == null || !Number.isFinite(percent)) return 'neutral'
  if (percent >= target) return 'pos'
  return percent >= target - 8 ? 'warn' : 'neg'
}

/** "Run #412", "Incident #9", "News", "Filing": what a report is about, in a word or two. */
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']

/** "2026-09-30" → "30 Sep"; anything else as it came. */
export function shortDate(isoDay: string): string {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(isoDay)
  if (!m) return isoDay
  const month = MONTHS[Number(m[2]) - 1]
  return month ? `${Number(m[3])} ${month}` : isoDay
}

export function reportSubjectLabel(r: Pick<AiReportSummary, 'subjectType' | 'subjectId'> & { agentKey?: string }): string {
  // The AI Trader's lessons write "check" reports too: a reflection on a day, and a lesson's test.
  if (r.agentKey === 'ai-trader-reflect') {
    const replay = /^replay:(\d+)$/.exec(r.subjectId)
    const day = /^day:(\d{4}-\d{2}-\d{2})$/.exec(r.subjectId)
    return `AI Trader reflection · ${replay ? `replay #${replay[1]}` : day ? shortDate(day[1]) : r.subjectId}`
  }
  if (r.agentKey === 'ai-trader-lesson-check') return `AI Trader lesson test · ${r.subjectId}`
  switch (r.subjectType) {
    case 'check':
      return `Assistant check · ${shortDate(r.subjectId)}`
    case 'exam':
      return `Assistant exam #${r.subjectId}`
    case 'run':
      return `Run #${r.subjectId}`
    case 'incident':
      return `Incident #${r.subjectId}`
    case 'news':
      return 'News'
    case 'filing':
      return 'Filing'
    default:
      return `${r.subjectType || 'subject'} ${r.subjectId}`.trim()
  }
}

/**
 * Where a report's link may go: a console path (one leading slash, never
 * two) or an https page opened in a new tab. Anything else, a javascript:
 * or a plain-http address included, is not a link.
 */
export function reportLink(link: string | null | undefined): { kind: 'internal'; to: string } | { kind: 'external'; href: string } | null {
  if (!link) return null
  const l = link.trim()
  if (/^\/(?!\/)/.test(l)) return { kind: 'internal', to: l }
  if (/^https:\/\/[^\s]+$/i.test(l)) return { kind: 'external', href: l }
  return null
}

/** The first paragraph of a Markdown body, headings skipped: a review folded to its opening. */
export function firstParagraph(markdown: string): string {
  const blocks = markdown.replace(/\r\n?/g, '\n').split(/\n\s*\n/)
  for (const block of blocks) {
    const lines = block.split('\n').filter((line) => !/^\s{0,3}#{1,6}\s/.test(line))
    const text = lines.join('\n').trim()
    if (text) return text
  }
  return ''
}

/** 0.82 → "82%"; null → "—". */
export function confidenceText(c: number | null | undefined): string {
  if (c == null || !Number.isFinite(c)) return '—'
  return `${Math.round((c <= 1 ? c * 100 : c))}%`
}

// ---------- model health -------------------------------------------------------

/**
 * A model's health in a word and a tone, with the line that says why: "cooling
 * until 16:40" (IST) with its last failure, "failed" with it, "healthy" with
 * its last answer's time taken, or "not asked yet". Cooling is a warning, not
 * an alarm: the model is still asked, after the healthy ones.
 */
export function healthBadge(h: AiModelHealth | null | undefined, nowMs: number): { label: string; tone: Tone; detail: string } {
  if (!h) return { label: 'not known', tone: 'neutral', detail: '' }
  const failure = h.lastFailure ? `${h.lastFailure}${h.lastFailureUtc ? ` (${clockTime(h.lastFailureUtc, nowMs)})` : ''}` : ''
  switch (h.state) {
    case 'cooling':
      return {
        label: h.coolingUntilUtc ? `cooling until ${clockTime(h.coolingUntilUtc, nowMs)}` : 'cooling',
        tone: 'warn',
        detail: failure || 'failed; no reason recorded',
      }
    case 'failed':
      return { label: h.consecutiveFailures > 1 ? `failed ${h.consecutiveFailures}× running` : 'failed last time', tone: 'warn', detail: failure }
    case 'healthy':
      return {
        label: 'healthy',
        tone: 'pos',
        detail: h.lastOkSeconds != null ? `last answer in ${formatSeconds(h.lastOkSeconds)}${h.lastOkUtc ? ` (${clockTime(h.lastOkUtc, nowMs)})` : ''}` : '',
      }
    case 'unknown':
      return { label: 'not asked yet', tone: 'neutral', detail: '' }
    default:
      return { label: h.state || 'not known', tone: 'neutral', detail: failure }
  }
}

/** The health of each model, by id. */
export function healthByModel(list: readonly AiModelHealth[] | null | undefined): Map<string, AiModelHealth> {
  return new Map((list ?? []).map((h) => [h.model, h]))
}

// ---------- the assistant check ---------------------------------------------------

export interface CheckQuestion {
  question: string
  /** number, id or word: how the answer was graded. */
  kind: string
  expected: string
  pass: boolean
  answer: string
  callId: number | null
  model: string | null
  seconds: number | null
  error: string | null
}

export interface AssistantCheck {
  passed: number
  total: number
  /** 0 to 1; worked out from passed and total when not sent. */
  score: number | null
  questions: CheckQuestion[]
}

/** An assistant-check report's data; the counts are taken from the questions when the data leaves them out. */
export function readAssistantCheck(data: Record<string, unknown> | null): AssistantCheck | null {
  if (!data) return null
  const text = (v: unknown) => (typeof v === 'string' ? v : typeof v === 'number' || typeof v === 'boolean' ? String(v) : '')
  const questions = list<Record<string, unknown>>(data.questions)
    .filter((q) => q && typeof q === 'object')
    .map((q) => ({
      question: text(q.question),
      kind: text(q.kind) || 'word',
      expected: text(q.expected),
      pass: q.pass === true,
      answer: text(q.answer),
      callId: num(q.callId),
      model: str(q.model),
      seconds: num(q.seconds),
      error: str(q.error),
    }))
  const total = num(data.total) ?? questions.length
  const passed = num(data.passed) ?? questions.filter((q) => q.pass).length
  const score = num(data.score) ?? (total > 0 ? passed / total : null)
  return { passed, total, score, questions }
}

/** One set's score in an assistant-exam report: pass^k over the questions answered every time, pass@1 over all answered asks. */
export interface ExamScore {
  questions: number
  scored: number
  passK: number | null
  pass1: number | null
}

export interface AssistantExam {
  repeats: number
  all: ExamScore
  practice: ExamScore
  holdout: ExamScore
}

/** An assistant-exam report's data; null when it is not one. */
export function readAssistantExam(data: Record<string, unknown> | null): AssistantExam | null {
  if (!data || typeof data.all !== 'object' || data.all === null) return null
  const part = (v: unknown): ExamScore => {
    const o = (v && typeof v === 'object' ? v : {}) as Record<string, unknown>
    return { questions: num(o.questions) ?? 0, scored: num(o.scored) ?? 0, passK: num(o.passK), pass1: num(o.pass1) }
  }
  return { repeats: num(data.repeats) ?? 3, all: part(data.all), practice: part(data.practice), holdout: part(data.holdout) }
}

/** "pass^3 87% practice, 80% held out (150 questions)"; a dash where nothing could be scored. */
export function examScoreText(e: AssistantExam): string {
  const pct = (v: number | null) => (v === null ? '—' : `${Math.round(100 * v)}%`)
  return `pass^${e.repeats} ${pct(e.practice.passK)} practice, ${pct(e.holdout.passK)} held out (${e.all.questions} questions)`
}

/** "8 of 9, 89%"; "no questions" for an empty check. */
export function checkScoreText(c: Pick<AssistantCheck, 'passed' | 'total' | 'score'>): string {
  if (c.total <= 0) return 'no questions'
  const pct = Math.round(100 * (c.score ?? c.passed / c.total))
  return `${c.passed} of ${c.total}, ${pct}%`
}

/** The pass mark of the daily check, as a share of questions. */
export const CHECK_PASS_MARK = 0.8

/** The newest report one agent or utility wrote, in full; null when there is none. */
export function useLatestAgentReport(agent: string) {
  return useQuery({
    queryKey: ['ai', 'latest-report', agent],
    queryFn: async () => {
      const page = readReportsPage(await api.get<unknown>(`/api/Ai/reports?${reportsQuery({ agent, take: 1 })}`))
      const first = page.reports[0]
      return first ? readReport(await api.get<unknown>(`/api/Ai/reports/${first.id}`)) : null
    },
    refetchInterval: 60_000,
    retry: 1,
  })
}

// ---------- docs search -----------------------------------------------------------

export interface AiDocsIndex {
  files: number
  passages: number
  indexedUtc: string | null
  model: string
}

export interface AiDocsHit {
  file: string
  /** "AI workspace › Desk tools": the headings the passage sits under. */
  section: string
  score: number | null
  text: string
}

export interface AiDocsSearch {
  index: AiDocsIndex
  query: string | null
  hits: AiDocsHit[]
}

export function readDocsSearch(raw: unknown): AiDocsSearch {
  const o = need<AiDocsSearch>(raw, ['index'], 'docs search')
  return {
    index: need<AiDocsIndex>(o.index, ['files', 'passages'], 'docs index'),
    query: o.query ?? null,
    hits: list<AiDocsHit>(o.hits).map((h) => ({ file: h.file ?? '', section: h.section ?? '', score: num(h.score), text: h.text ?? '' })),
  }
}

/**
 * GET /api/Ai/search: the index's state, and with a query the passages that
 * match it best. The query is sent as typed; an empty one asks for the state.
 */
export function useDocsSearch(query: string, limit = 5) {
  const q = query.trim()
  return useQuery({
    queryKey: ['ai', 'search', q, limit],
    queryFn: async () => readDocsSearch(await api.get<unknown>(`/api/Ai/search?${new URLSearchParams(q ? { q, limit: String(limit) } : { limit: String(limit) })}`)),
    staleTime: 60_000,
    placeholderData: keepPreviousData,
    retry: 1,
  })
}

/**
 * The published docs pages by source file, as web/docs-site/build.mjs's NAV
 * names them (keep the two together). A file not published has no page.
 */
const DOCS_PAGES: Readonly<Record<string, string>> = {
  'docs/01_ARCHITECTURE_OVERVIEW.md': 'architecture',
  'docs/RESEARCH_AND_ARCHITECTURE.md': 'research',
  'docs/03_ARCHITECTURE_AND_RISK_MANAGEMENT.md': 'risk-management',
  'docs/PROJECT_STATUS.md': 'status',
  'docs/modules/data_module.md': 'modules/data',
  'docs/modules/strategies_module.md': 'modules/strategies',
  'docs/modules/manual_orders.md': 'modules/manual-orders',
  'docs/modules/backtesting_module.md': 'modules/backtesting',
  'docs/modules/option_chain.md': 'modules/option-chain',
  'docs/modules/pattern_alerts.md': 'modules/pattern-alerts',
  'docs/modules/connectors_module.md': 'modules/connectors',
  'docs/modules/dhan_connector.md': 'modules/dhan',
  'docs/modules/users_module.md': 'modules/users',
  'docs/modules/strategy_packages.md': 'modules/strategy-packages',
  'docs/modules/activity_log.md': 'modules/activity-log',
  'docs/strategies/README.md': 'strategies',
  'docs/02_LOCAL_DEPLOYMENT_GUIDE.md': 'deploy/local',
}

export const DOCS_SITE = 'https://openfno.com/docs/'

/** The docs site's anchor for a heading, as build.mjs's slugify makes it. */
function docsAnchor(heading: string): string {
  return heading
    .toLowerCase()
    .replace(/[^a-z0-9\s-]/g, '')
    .trim()
    .replace(/\s+/g, '-')
    .replace(/-+/g, '-')
    .slice(0, 80)
}

/**
 * Where a passage can be read on openfno.com/docs, or null when its file is
 * not published there. A strategy spec's page is its registry name in
 * kebab case, as the site builds it; the passage's own heading (the last part
 * of its section) becomes the anchor, unless it is the page's title.
 */
export function docsLink(file: string, section = ''): string | null {
  let slug = DOCS_PAGES[file] ?? null
  const spec = /^docs\/strategies\/([A-Za-z0-9]+)\.md$/.exec(file)
  if (!slug && spec && spec[1] !== 'README') {
    slug = `strategies/${spec[1].replace(/([a-z0-9])([A-Z])/g, '$1-$2').replace(/([A-Z]+)([A-Z][a-z])/g, '$1-$2').toLowerCase()}`
  }
  if (slug == null) return null
  const parts = section.split('›').map((p) => p.trim()).filter(Boolean)
  const anchor = parts.length > 1 ? docsAnchor(parts[parts.length - 1]) : ''
  return `${DOCS_SITE}${slug}/${anchor ? `#${anchor}` : ''}`
}

// ---------- telegram ------------------------------------------------------------------

export interface AiTelegramOwner {
  telegramUserId: string
  telegramName: string
  consoleUser: string
  linkedUtc: string | null
}

export interface AiTelegramStatus {
  /** The setting is on, and the bot token and the model key are there. */
  running: boolean
  enabled: boolean
  botUsername: string | null
  owners: AiTelegramOwner[]
}

export interface AiTelegramPairing {
  code: string
  expiresUtc: string
  botUsername: string | null
  instruction: string
}

export function readTelegram(raw: unknown): AiTelegramStatus {
  const o = need<AiTelegramStatus>(raw, ['running', 'enabled'], 'Telegram status')
  return {
    running: o.running === true,
    enabled: o.enabled === true,
    botUsername: o.botUsername ?? null,
    owners: list<AiTelegramOwner>(o.owners).map((w) => ({ ...w, telegramUserId: String(w.telegramUserId), linkedUtc: w.linkedUtc ?? null })),
  }
}

/** Telegram's state in words: running, not running (and what to check), or switched off. */
export function telegramState(t: Pick<AiTelegramStatus, 'running' | 'enabled'>): { label: string; tone: Tone; note: string } {
  if (t.running) return { label: 'running', tone: 'pos', note: 'The Desk Assistant answers linked accounts in private chats.' }
  if (t.enabled) return { label: 'not running', tone: 'warn', note: 'The setting is on but the bot is not answering: check the bot token and the model key.' }
  return { label: 'off', tone: 'neutral', note: 'The Telegram setting is off, so the bot answers nobody.' }
}

/** "9:42 left" until a pairing code expires; "expired" after. */
export function countdownText(expiresUtc: string | null | undefined, nowMs: number): string {
  const at = expiresUtc ? Date.parse(expiresUtc) : Number.NaN
  if (Number.isNaN(at)) return 'expiry not known'
  const left = Math.ceil((at - nowMs) / 1000)
  if (left <= 0) return 'expired'
  return `${Math.floor(left / 60)}:${String(left % 60).padStart(2, '0')} left`
}

/** The bot's Telegram link, from its user name ("codefortrade_bot" or "@codefortrade_bot"); null without one. */
export function botLink(botUsername: string | null | undefined): string | null {
  const name = (botUsername ?? '').trim().replace(/^@/, '')
  return /^[A-Za-z0-9_]{3,64}$/.test(name) ? `https://t.me/${name}` : null
}

/** Telegram status; polled every 5 s while a pairing code is showing, so the new link appears. */
export function useAiTelegram(pairing: boolean) {
  return useQuery({
    queryKey: ['ai', 'telegram'],
    queryFn: async () => readTelegram(await api.get<unknown>('/api/Ai/telegram')),
    refetchInterval: pairing ? 5_000 : 60_000,
    retry: 1,
  })
}

export function usePairTelegram() {
  return useMutation({ mutationFn: () => api.post<AiTelegramPairing>('/api/Ai/telegram/pair') })
}

export function useUnlinkTelegram() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (telegramUserId: string) => api.delete<unknown>(`/api/Ai/telegram/owners/${encodeURIComponent(telegramUserId)}`),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['ai', 'telegram'] }),
  })
}

// ---------- memory ------------------------------------------------------------------------

/*
 * What an agent reads before every answer (the Desk Assistant is the first):
 * the owner's notes (/remember) and corrections (a 👎 with what the answer
 * should have said), active at once, and lessons an agent proposed from a
 * failed check, read only once the owner approves them. Every memory counts
 * the 👍/👎 and the check results of the answers it was part of; one that
 * keeps turning up in bad answers is flagged for review. Nothing is retired
 * on its own.
 */

/** note: the owner's /remember; correction: a 👎 with text; lesson: proposed by an agent. */
export type MemoryKind = 'note' | 'correction' | 'lesson'
export type MemoryStatus = 'active' | 'proposed' | 'rejected' | 'retired'
export type MemorySource = 'owner' | 'feedback' | 'check'
export type MemoryVia = 'console' | 'telegram' | 'check'
export type FeedbackScore = 1 | -1

export interface AiMemory {
  id: number
  agentKey: string
  agentName: string
  kind: MemoryKind | string
  status: MemoryStatus | string
  /** What the agent reads, at most MEMORY_MAX_CHARS. */
  text: string
  /** The question it came from; '' for a plain note. */
  context: string
  source: MemorySource | string
  via: MemoryVia | string
  /** The answer it corrects, or the failed check answer a lesson came from. */
  sourceCallId: number | null
  /** The check report a lesson came from. */
  sourceReportId: number | null
  createdBy: string
  createdUtc: string
  /** Who approved, rejected or retired it last; '' if nobody. */
  decidedBy: string
  decidedUtc: string | null
  activatedUtc: string | null
  retiredUtc: string | null
  updatedUtc: string
  /** Answers it was given to. */
  uses: number
  lastUsedUtc: string | null
  /** 👍 and 👎 on the answers it was part of. */
  ups: number
  downs: number
  /** Check questions answered right, and wrong, while it was part of the answer. */
  checkPasses: number
  checkFails: number
  /** The outcomes say: look at this one. */
  review: boolean
  reviewReason: string | null
}

/** An agent that has a memory, and whether it is on. */
export interface AiMemoryAgent {
  key: string
  name: string
  on: boolean
}

export interface AiMemories {
  enabled: boolean
  agents: AiMemoryAgent[]
  counts: Record<MemoryStatus, number>
  /** How many characters of active memory an answer is given at most; null when not sent. */
  budgetChars: number | null
  activeChars: number | null
  /** Newest first. */
  memories: AiMemory[]
}

/** One IST day with a check or feedback. The check's figures are null on a day without a check. */
export interface MemoryProgressDay {
  date: string
  checkPassed: number | null
  checkTotal: number | null
  /** 0 to 1. */
  score: number | null
  activeMemories: number | null
  ups: number
  downs: number
}

export interface MemoryProgress {
  /** Oldest first. */
  days: MemoryProgressDay[]
}

/** A call's feedback as its detail carries it. */
export interface AiCallFeedback {
  score: FeedbackScore
  /** The correction written with a 👎; '' when none. */
  note: string
  by: string
  utc: string | null
}

/** What POST /api/Ai/calls/{id}/feedback answers: 0 is feedback cleared; `memory` is the correction it made. */
export interface CallFeedbackResult {
  callId: number
  score: FeedbackScore | 0
  note: string
  memory: AiMemory | null
}

/** The longest memory the API takes. */
export const MEMORY_MAX_CHARS = 600

const record = (v: unknown): v is Record<string, unknown> => v != null && typeof v === 'object' && !Array.isArray(v)
const words = (v: unknown): string => str(v) ?? ''
const count = (v: unknown): number => num(v) ?? 0

function feedbackScore(v: unknown): FeedbackScore | null {
  return v === 1 || v === -1 ? v : null
}

/** Memory ids as sent, whole and positive; null when the API did not send the list. */
function memoryIds(v: unknown): number[] | null {
  if (!Array.isArray(v)) return null
  return v.filter((n): n is number => typeof n === 'number' && Number.isInteger(n) && n > 0)
}

/** One memory, every field made safe to show; null for a row without an id, which nothing can act on. */
function readMemoryRow(raw: unknown): AiMemory | null {
  if (!record(raw)) return null
  const id = num(raw.id)
  if (id == null) return null
  return {
    id,
    agentKey: words(raw.agentKey),
    agentName: words(raw.agentName),
    kind: words(raw.kind) || 'note',
    status: words(raw.status) || 'active',
    text: words(raw.text),
    context: words(raw.context),
    source: words(raw.source),
    via: words(raw.via),
    sourceCallId: num(raw.sourceCallId),
    sourceReportId: num(raw.sourceReportId),
    createdBy: words(raw.createdBy),
    createdUtc: words(raw.createdUtc),
    decidedBy: words(raw.decidedBy),
    decidedUtc: str(raw.decidedUtc),
    activatedUtc: str(raw.activatedUtc),
    retiredUtc: str(raw.retiredUtc),
    updatedUtc: words(raw.updatedUtc),
    uses: count(raw.uses),
    lastUsedUtc: str(raw.lastUsedUtc),
    ups: count(raw.ups),
    downs: count(raw.downs),
    checkPasses: count(raw.checkPasses),
    checkFails: count(raw.checkFails),
    review: raw.review === true,
    reviewReason: str(raw.reviewReason),
  }
}

/** One memory, as POST and PUT answer with it; a body without one is an error, not an empty row. */
export function readMemory(raw: unknown): AiMemory {
  const m = readMemoryRow(need<Record<string, unknown>>(raw, ['id', 'text'], 'memory'))
  if (!m) throw new Error("The API's memory came back in a shape this page cannot read. Is the API build current?")
  return m
}

export function readMemories(raw: unknown): AiMemories {
  const o = need<Record<string, unknown>>(raw, ['memories'], 'memory list')
  const memories = list<unknown>(o.memories)
    .map(readMemoryRow)
    .filter((m): m is AiMemory => m != null)
  const c = record(o.counts) ? o.counts : {}
  // Counted from the rows when the API left a count out: the list holds every status.
  const counted = (s: MemoryStatus) => num(c[s]) ?? memories.filter((m) => m.status === s).length
  return {
    // Only an API that says so switches the page's warning on; a missing flag is not "off".
    enabled: o.enabled !== false,
    agents: list<unknown>(o.agents)
      .filter(record)
      .map((a) => ({ key: words(a.key), name: words(a.name) || words(a.key), on: a.on !== false }))
      .filter((a) => a.key !== ''),
    counts: { active: counted('active'), proposed: counted('proposed'), rejected: counted('rejected'), retired: counted('retired') },
    budgetChars: num(o.budgetChars),
    activeChars: num(o.activeChars),
    memories,
  }
}

export function readMemoryProgress(raw: unknown): MemoryProgress {
  const o = need<Record<string, unknown>>(raw, ['days'], 'memory progress')
  return {
    days: list<unknown>(o.days)
      .filter(record)
      .filter((d) => typeof d.date === 'string' && d.date !== '')
      .map((d) => {
        const checkPassed = num(d.checkPassed)
        const checkTotal = num(d.checkTotal)
        return {
          date: d.date as string,
          checkPassed,
          checkTotal,
          score: num(d.score) ?? (checkPassed != null && checkTotal != null && checkTotal > 0 ? checkPassed / checkTotal : null),
          activeMemories: num(d.activeMemories),
          ups: count(d.ups),
          downs: count(d.downs),
        }
      }),
  }
}

/** A call detail's feedback; null when there is none, or its score is neither 1 nor -1. */
export function readCallFeedback(raw: unknown): AiCallFeedback | null {
  if (!record(raw)) return null
  const score = feedbackScore(raw.score)
  if (score == null) return null
  return { score, note: words(raw.note), by: words(raw.by), utc: str(raw.utc) }
}

export function readFeedbackResult(raw: unknown): CallFeedbackResult {
  const o = need<Record<string, unknown>>(raw, ['callId', 'score'], 'feedback')
  const score = o.score === 0 ? 0 : feedbackScore(o.score)
  if (score == null) throw new Error("The API's feedback came back in a shape this page cannot read. Is the API build current?")
  return { callId: count(o.callId), score, note: words(o.note), memory: record(o.memory) ? readMemoryRow(o.memory) : null }
}

/** "M12": how every page names a memory. */
export function memoryLabel(id: number): string {
  return `M${id}`
}

/** Where a memory is on the Memory tab. */
export function memoryHref(id: number): string {
  return `/ai/memory#memory-${id}`
}

/** The memories waiting for a decision, the active ones, and the retired and rejected, each newest first as sent. */
export function splitMemories(memories: readonly AiMemory[]): { proposed: AiMemory[]; active: AiMemory[]; closed: AiMemory[] } {
  return {
    proposed: memories.filter((m) => m.status === 'proposed'),
    active: memories.filter((m) => m.status === 'active'),
    closed: memories.filter((m) => m.status === 'retired' || m.status === 'rejected'),
  }
}

const MEMORY_KINDS: Record<MemoryKind, { label: string; tone: Tone; means: string }> = {
  note: { label: 'Note', tone: 'neutral', means: 'written by the owner, with /remember or on this page' },
  correction: { label: 'Correction', tone: 'warn', means: 'what an answer should have said, written with a 👎' },
  lesson: { label: 'Lesson', tone: 'accent', means: 'proposed by an agent from a failed check; read only once approved' },
}

export function memoryKind(kind: string | null | undefined): { label: string; tone: Tone; means: string } {
  return MEMORY_KINDS[kind as MemoryKind] ?? { label: kind || 'unknown', tone: 'neutral', means: '' }
}

const MEMORY_STATUSES: Record<MemoryStatus, { label: string; tone: Tone; means: string }> = {
  active: { label: 'Active', tone: 'pos', means: 'read before every answer' },
  proposed: { label: 'Waiting', tone: 'warn', means: 'proposed; not read until approved' },
  rejected: { label: 'Rejected', tone: 'neutral', means: 'turned down; never read' },
  retired: { label: 'Retired', tone: 'neutral', means: 'no longer read' },
}

export function memoryStatus(status: string | null | undefined): { label: string; tone: Tone; means: string } {
  return MEMORY_STATUSES[status as MemoryStatus] ?? { label: status || 'unknown', tone: 'neutral', means: '' }
}

const MEMORY_SOURCES: Record<string, string> = { owner: 'Owner', feedback: 'Feedback', check: 'Daily check' }
const MEMORY_VIAS: Record<string, string> = { console: 'Console', telegram: 'Telegram', check: 'Daily check' }

/** "Owner · Console", "Feedback · Telegram", "Daily check": who made it, and through what. */
export function memorySourceText(source: string | null | undefined, via: string | null | undefined): string {
  const who = MEMORY_SOURCES[source ?? ''] ?? (source || 'unknown')
  const through = MEMORY_VIAS[via ?? ''] ?? (via || '')
  return through && through !== who ? `${who} · ${through}` : who
}

/** "From the check on 1 Oct": where a memory came from and on which IST day. */
export function memoryOrigin(m: Pick<AiMemory, 'source' | 'createdUtc'>): string {
  const ms = Date.parse(m.createdUtc)
  const day = Number.isNaN(ms) ? '' : ` on ${shortDate(istDay(ms))}`
  switch (m.source) {
    case 'check':
      return `From the check${day}`
    case 'feedback':
      return `From a 👎${day}`
    case 'owner':
      return `From a note${day}`
    default:
      return `From ${m.source || 'somewhere not recorded'}${day}`
  }
}

/**
 * The note in "/remember use net after charges"; '' for a bare "/remember";
 * null when the text is not a /remember at all (it is a question). The bot's
 * "/remember@name" form counts too, as Telegram sends it in a group.
 */
export function parseRemember(input: string): string | null {
  const m = /^\s*\/remember(?:@\w+)?(?:\s+([\s\S]*))?$/i.exec(input)
  return m ? (m[1] ?? '').trim() : null
}

/** Why a memory's text cannot be saved, or null when it can. The API checks again. */
export function memoryTextProblem(text: string): string | null {
  const t = text.trim()
  if (!t) return 'Write the text first.'
  if (t.length > MEMORY_MAX_CHARS) return `A memory holds at most ${MEMORY_MAX_CHARS} characters; this one has ${formatTokens(t.length)}.`
  return null
}

/** "812 of 2,400 characters"; what is known when the budget is not. */
export function budgetText(activeChars: number | null, budgetChars: number | null): string {
  if (activeChars == null) return 'size not known'
  if (budgetChars == null || budgetChars <= 0) return `${formatTokens(activeChars)} characters`
  return `${formatTokens(activeChars)} of ${formatTokens(budgetChars)} characters`
}

/** The IST day `days` days back from today's, so a window of `days` + 1 days ends today. */
export function istDaysAgo(nowMs: number, days: number): string {
  // IST has no daylight saving: a day is always 24 hours.
  return istDay(nowMs - days * 86_400_000)
}

export interface ProgressWindow {
  /** Days in the window that had a check. */
  checks: number
  checkPassed: number
  checkTotal: number
  /** 0 to 1; null when there was no check. */
  score: number | null
  ups: number
  downs: number
}

/** The checks and the 👍/👎 of every day from `fromDay` on, summed. */
export function progressSince(days: readonly MemoryProgressDay[], fromDay: string): ProgressWindow {
  const w: ProgressWindow = { checks: 0, checkPassed: 0, checkTotal: 0, score: null, ups: 0, downs: 0 }
  for (const d of days) {
    if (d.date < fromDay) continue
    w.ups += d.ups
    w.downs += d.downs
    if (d.checkTotal != null && d.checkTotal > 0) {
      w.checks++
      w.checkTotal += d.checkTotal
      w.checkPassed += d.checkPassed ?? 0
    }
  }
  w.score = w.checkTotal > 0 ? w.checkPassed / w.checkTotal : null
  return w
}

/**
 * A memory or feedback request's failure in words: the API's own { error }
 * when it sent one, and what the status means when it did not.
 */
export function memoryErrorText(error: unknown, subject: 'memory' | 'call' = 'memory'): string {
  if (error instanceof ApiError) {
    const said = error.message.split('\n')[0].trim()
    if (said && !/^Request failed with status \d+$/.test(said)) return withStop(said)
    switch (error.status) {
      case 400:
        return 'The API refused it as sent.'
      case 404:
        return subject === 'memory' ? 'That memory is not on the server any more.' : 'That call is not on the server.'
      case 409:
        return 'That call was not answered, so it cannot take feedback.'
      default:
        return `The API failed (HTTP ${error.status}).`
    }
  }
  const message = error instanceof Error ? error.message.split('\n')[0] : String(error ?? '')
  return `The request did not reach the API${message ? `: ${withStop(message)}` : '.'} Check the connection, and that the API is running.`
}

/** Every memory of one agent (or of every agent), all statuses. Re-read every 30 s while the tab is open. */
export function useAiMemories(agent?: string) {
  return useQuery({
    queryKey: ['ai', 'memories', agent ?? ''],
    queryFn: async () => readMemories(await api.get<unknown>(`/api/Ai/memories${agent ? `?${new URLSearchParams({ agent })}` : ''}`)),
    refetchInterval: 30_000,
  })
}

/** The days with a check or feedback, oldest first. */
export function useMemoryProgress(days = 30) {
  return useQuery({
    queryKey: ['ai', 'memory-progress', days],
    queryFn: async () => readMemoryProgress(await api.get<unknown>(`/api/Ai/memories/progress?days=${days}`)),
    refetchInterval: 60_000,
  })
}

/** POST /api/Ai/memories: a note, active at once. */
export function useAddMemory() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ agent, text }: { agent?: string | null; text: string }) =>
      readMemory(await api.post<unknown>('/api/Ai/memories', agent ? { agent, text: text.trim() } : { text: text.trim() })),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['ai', 'memories'] }),
  })
}

export interface MemoryUpdate {
  id: number
  text?: string
  /** Approve or restore (active), reject (rejected), retire (retired). */
  status?: 'active' | 'rejected' | 'retired'
}

/** PUT /api/Ai/memories/{id}: new text, a decision, or both (edit and approve). */
export function useUpdateMemory() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ id, text, status }: MemoryUpdate) => {
      const body: { text?: string; status?: string } = {}
      if (text != null) body.text = text.trim()
      if (status) body.status = status
      return readMemory(await api.put<unknown>(`/api/Ai/memories/${id}`, body))
    },
    onSuccess: (memory) => {
      // The answer is the updated row: show it at once, then re-read the counts and the budget.
      qc.setQueriesData<AiMemories>({ queryKey: ['ai', 'memories'] }, (old) =>
        old ? { ...old, memories: old.memories.map((m) => (m.id === memory.id ? memory : m)) } : old,
      )
      void qc.invalidateQueries({ queryKey: ['ai', 'memories'] })
    },
  })
}

/** POST /api/Ai/calls/{id}/feedback: 👍 (1), 👎 (-1, with an optional correction) or cleared (0). */
export function useCallFeedback() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ callId, score, correction }: { callId: number; score: FeedbackScore | 0; correction?: string }) => {
      const text = correction?.trim()
      return readFeedbackResult(await api.post<unknown>(`/api/Ai/calls/${callId}/feedback`, score === -1 && text ? { score, correction: text } : { score }))
    },
    onSuccess: (_, { callId }) => {
      void qc.invalidateQueries({ queryKey: ['ai', 'calls'] })
      void qc.invalidateQueries({ queryKey: ['ai', 'call', callId] })
      void qc.invalidateQueries({ queryKey: ['ai', 'memories'] })
      void qc.invalidateQueries({ queryKey: ['ai', 'memory-progress'] })
    },
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
      /** The memories the answer was given, by id; null from an API without memory. */
      memoryIds: number[] | null
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
        memoryIds: memoryIds(d.memoryIds),
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
  /** The memories the answer was given, from `done`; null until then, or from an API without memory. */
  memoryIds: number[] | null
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

/**
 * "Nemotron 3 Ultra timed out, asking Kimi K3". When the next model is the
 * same one, the API is asking it once more after a capacity refusal
 * ("Service temporarily overloaded"): "…, asking it again in a moment".
 */
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
  if (e.next && e.next === e.model) return `${who} ${what}, asking it again in a moment`
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
        memoryIds: e.memoryIds,
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
      memoryIds: null,
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

// ---------- talking with the agents ----------------------------------------------

export const DESK_ASSISTANT = 'desk-assistant'

/**
 * An agent the owner can talk with on AI → Assistant: what the picker calls it,
 * one line on what to ask it, and questions to start with (a click puts one in
 * the box; it is not sent). What it does and can read comes from the API.
 */
export interface ChatAgentMeta {
  key: string
  label: string
  lead: string
  starters: readonly string[]
  placeholder: string
}

/** The Desk Assistant first: it is the default, and the one the page was built for. */
export const CHAT_AGENTS: readonly ChatAgentMeta[] = [
  {
    key: DESK_ASSISTANT,
    label: 'Desk Assistant',
    lead: "Ask the desk's AI. It reads the desk through read-only tools, answers stream from the NVIDIA-hosted models of the tier you pick, and every question is logged in the Calls tab.",
    starters: [
      'How did the runs do today?',
      'Why did the worst run today lose money?',
      'What is open right now, and what is it worth?',
      'Anything wrong on the desk: open incidents or checkup items?',
    ],
    placeholder: 'Ask about a backtest, a market move, a concept or the code…',
  },
  {
    key: 'ai-trader',
    label: 'AI Trader',
    lead: 'Question the AI Trader about its own decisions, its shadow book, its scoreboard and its lessons, or ask what it would do now: a look built and judged as now that saves nothing.',
    starters: ['Why did you buy on 16 Sep?', 'What would you do now?', 'Which lessons are you using?', 'How do you stand against the baseline rule?'],
    placeholder: 'Ask the AI Trader about a decision, its book or its lessons…',
  },
  {
    key: 'trade-reviewer',
    label: 'Trade Reviewer',
    lead: 'Question the Trade Reviewer about its run reviews: the verdicts, the deviations it found, and whether a review still stands against the run and its spec.',
    starters: ['Which runs broke their spec this week?', "Show me yesterday's reviews.", 'Does your last "deviated" verdict still stand?'],
    placeholder: 'Ask the Trade Reviewer about a review or a run…',
  },
  {
    key: 'news-analyst',
    label: 'News Analyst',
    lead: 'Question the News Analyst about the events it extracted from headlines and exchange filings, each with the text it read.',
    starters: ['What moved in the last hour?', 'Which filings today did you read as negative?', 'Which of your records were invalid today, and why?'],
    placeholder: 'Ask the News Analyst about the news it read…',
  },
  {
    key: 'incident-explainer',
    label: 'Incident Explainer',
    lead: "Question the Incident Explainer about Sentinel's incidents and its explanations of them.",
    starters: ['Explain the latest incident', 'Which incidents are open, and how urgent are they?', 'What did you explain this week?'],
    placeholder: 'Ask the Incident Explainer about an incident…',
  },
]

/** A stored or linked agent key the picker knows; anything else is the Desk Assistant. */
export function parseChatAgent(raw: string | null | undefined): string {
  return CHAT_AGENTS.find((a) => a.key === raw)?.key ?? DESK_ASSISTANT
}

export function chatAgentMeta(key: string): ChatAgentMeta {
  return CHAT_AGENTS.find((a) => a.key === key) ?? CHAT_AGENTS[0]
}

/** One conversation per agent, and the one the page shows. Switching keeps every conversation. */
export interface AgentChats {
  agent: string
  chats: Readonly<Record<string, ChatState>>
}

export type AgentChatsAction =
  | { type: 'pick'; agent: string }
  /** A change to one agent's conversation, wherever the page is: an answer streams on after the owner switches away. */
  | { type: 'chat'; agent: string; action: ChatAction }

export function initialAgentChats(agent: string | null | undefined = DESK_ASSISTANT, newId: () => string = newConversationId): AgentChats {
  const chats: Record<string, ChatState> = {}
  for (const a of CHAT_AGENTS) chats[a.key] = initialChat(newId())
  return { agent: parseChatAgent(agent), chats }
}

export function agentChatsReducer(state: AgentChats, action: AgentChatsAction): AgentChats {
  if (action.type === 'pick') {
    const agent = parseChatAgent(action.agent)
    return agent === state.agent ? state : { ...state, agent }
  }
  const key = parseChatAgent(action.agent)
  const before = state.chats[key] ?? initialChat()
  const after = chatReducer(before, action.action)
  return after === before ? state : { ...state, chats: { ...state.chats, [key]: after } }
}

/** The agent whose answer is streaming, if any: one question at a time across the conversations. */
export function streamingAgent(state: AgentChats): string | null {
  for (const a of CHAT_AGENTS) {
    if (state.chats[a.key]?.turns.some((t) => t.status === 'streaming')) return a.key
  }
  return null
}

/** "3 questions", "answering…", or "" for a conversation not started: under each agent in the picker. */
export function conversationNote(chat: ChatState | undefined): string {
  if (!chat || chat.turns.length === 0) return ''
  if (chat.turns.some((t) => t.status === 'streaming')) return 'answering…'
  return `${chat.turns.length} question${chat.turns.length === 1 ? '' : 's'}`
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

/** The agents' tools as the console names them; the other agents' read their own work. */
const TOOL_NAMES: Record<string, string> = {
  get_runs: 'Runs',
  get_open_positions: 'Open positions',
  get_quotes: 'Quotes',
  get_incidents: 'Incidents',
  get_latest_checkup: 'Latest checkup',
  get_forecasts: 'Forecasts',
  get_ai_trader_decisions: 'Its decisions',
  get_ai_trader_book: 'Its shadow book',
  get_ai_trader_scoreboard: 'Its scoreboard',
  get_ai_trader_lessons: 'Its lessons',
  ai_trader_look_now: 'A look now (saves nothing)',
  get_trade_reviews: 'Its reviews',
  get_news_events: 'Its news records',
  get_incident_explanations: 'Its explanations',
}

/** Tools whose main argument names the thing read, so it joins the label: "Run #412", "Option chain NIFTY". */
const TOOL_SUBJECTS: Record<string, { keys: string[]; label: (value: string) => string; bare: string }> = {
  get_run: { keys: ['runId', 'run_id', 'id'], label: (v) => `Run #${v}`, bare: 'Run' },
  get_option_chain_summary: { keys: ['underlying', 'symbol'], label: (v) => `Option chain ${v}`, bare: 'Option chain' },
  get_news: { keys: ['query', 'q', 'symbol'], label: (v) => `News ${v}`, bare: 'News' },
  get_strategy_spec: { keys: ['strategy', 'name'], label: (v) => `Strategy spec ${v}`, bare: 'Strategy spec' },
  search_docs: { keys: ['query', 'q'], label: (v) => `Docs search “${v}”`, bare: 'Docs search' },
  get_ai_trader_decision: { keys: ['decisionId', 'id'], label: (v) => `Decision #${v}`, bare: 'One decision' },
  get_trade_review: { keys: ['runId'], label: (v) => `Review of run #${v}`, bare: 'One review' },
  get_news_event: { keys: ['item', 'reportId'], label: (v) => `News record ${v}`, bare: 'One news record' },
  get_incident: { keys: ['incidentId', 'id'], label: (v) => `Incident #${v}`, bare: 'One incident' },
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
  if (name === 'get_strategy_history' && args) return strategyHistoryLabel(args)
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

const PERIODS: Record<string, string> = {
  this_month: 'this month',
  last_month: 'last month',
  last_7_days: 'last 7 days',
  last_30_days: 'last 30 days',
}

/**
 * get_strategy_history in one line: "Strategy history · IronCondor ·
 * BANKNIFTY · this month", or the dates for a from–to range. Arguments it
 * does not know stay in the detail.
 */
function strategyHistoryLabel(args: Record<string, unknown>): { label: string; detail: string } {
  const rest = { ...args }
  const take = (key: string): string => {
    const v = rest[key]
    delete rest[key]
    return v == null ? '' : argText(v).trim()
  }
  const parts = ['Strategy history', take('strategy'), take('underlying'), take('account')]
  const period = take('period')
  const from = take('from')
  const to = take('to')
  if (period) parts.push(PERIODS[period] ?? period.replace(/_/g, ' '))
  else if (from || to) parts.push(`${from ? shortDate(from) : '…'}–${to ? shortDate(to) : 'today'}`)
  const detail = Object.entries(rest)
    .filter(([, v]) => v != null && v !== '')
    .map(([k, v]) => `${k}: ${argText(v)}`)
    .join(', ')
  return { label: parts.filter(Boolean).join(' · '), detail }
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
