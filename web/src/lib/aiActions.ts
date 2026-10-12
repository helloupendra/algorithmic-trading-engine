/**
 * Ira's actions on the console (step 4): the Desk Assistant may propose one of
 * five actions on the paper desk (stop or start a strategy run, square off
 * manual-book positions, close everything open in an account, switch an AI
 * agent), and code carries it out only after the owner types his PIN.
 *
 * A proposal arrives as a tool step of the answer (propose_*): its result is
 * the JSON the model was given, which names the stored action, the sentence
 * code wrote and when it expires. The page shows a confirm card from it; the
 * PIN goes to POST /api/Ai/actions/{id}/confirm and nowhere else (never into
 * the conversation, never to a model). The PIN itself is set on the side
 * panel (PUT /api/Ai/actions/pin); the API keeps only a salted hash.
 */

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, api } from './api'
import type { AiToolStep, TurnRound } from './ai'

/** The proposal tools, in the order the model is offered them. */
export const ACTION_TOOLS: readonly string[] = [
  'propose_stop_run',
  'propose_start_run',
  'propose_square_off',
  'propose_agent_switch',
  'propose_close_account',
]

export type ActionStatus = 'pending' | 'confirmed' | 'cancelled' | 'expired' | 'executed' | 'failed'

/** A live run an account close stops, as code listed it when it proposed. */
export interface CloseRunItem {
  runId: number
  strategy: string
  underlying: string
  openLegs: number
  /** Net after charges when proposed; null when it could not be read. */
  net: number | null
}

/** A manual-book position an account close squares off. */
export interface ClosePositionItem {
  positionId: number
  /** "NIFTY 25100 CE · 27 Oct". */
  contract: string
  side: string
  lots: number
  /** Unrealized when proposed, before charges; null when it could not be marked. */
  unrealized: number | null
}

/** Everything an account close will close: the confirm card lists it in full. */
export interface CloseItems {
  account: string
  runCount: number
  positionCount: number
  /** The runs' open legs, squared off when they stop. */
  openLegs: number
  runs: CloseRunItem[]
  positions: ClosePositionItem[]
  /** Items past what the tool's answer could carry (the summary and the action still hold them all). */
  notListed: number
}

/** An action the Desk Assistant proposed in an answer, as its tool's result names it. */
export interface ProposedAction {
  id: number
  kind: string
  summary: string
  /** When it stops waiting for the PIN; null when the result did not say. */
  expiresUtc: string | null
  /** An account close's full list; null for the other actions. */
  items: CloseItems | null
}

/** An action as the API returns it once it has an answer. */
export interface ActionView {
  id: number
  kind: string
  status: ActionStatus
  summary: string
  channel: string
  createdUtc: string
  expiresUtc: string
  confirmedUtc: string | null
  executedUtc: string | null
  /** "Done: …" or "Not done: …" once it ran. */
  result: string | null
  error: string | null
  callId: number | null
}

/** The signed-in admin's Action PIN: never the PIN, only whether there is one. */
export interface ActionPinStatus {
  enabled: boolean
  set: boolean
  lockedUntilUtc: string | null
  confirmSeconds: number
}

/** What became of a confirm or a cancel, in the card's terms. */
export type ActionOutcome =
  /** It ran: done, or refused by the desk (the message says which). */
  | { kind: 'ran'; done: boolean; message: string; action: ActionView | null }
  | { kind: 'cancelled'; message: string }
  /** A wrong PIN; the action still waits. */
  | { kind: 'wrong'; message: string; attemptsLeft: number }
  /** Three wrong PINs: actions locked until then. */
  | { kind: 'locked'; message: string; lockedUntilUtc: string | null }
  /** It no longer waits (expired, cancelled, done elsewhere), or is not this admin's, or actions are off. */
  | { kind: 'closed'; message: string; status: ActionStatus | null }

function record(value: unknown): Record<string, unknown> | null {
  return value && typeof value === 'object' && !Array.isArray(value) ? (value as Record<string, unknown>) : null
}

const num = (value: unknown): number | null => (typeof value === 'number' && Number.isFinite(value) ? value : null)
const text = (value: unknown): string => (typeof value === 'string' ? value : '')

/** An account close's list from its tool's answer; null when there is none or it is not the shape. */
export function readCloseItems(value: unknown): CloseItems | null {
  const o = record(value)
  if (!o || !Array.isArray(o.runs) || !Array.isArray(o.positions)) return null
  const runs = o.runs.flatMap((r): CloseRunItem[] => {
    const x = record(r)
    const runId = num(x?.runId)
    return x && runId != null
      ? [{ runId, strategy: text(x.strategy), underlying: text(x.underlying), openLegs: num(x.openLegs) ?? 0, net: num(x.net) }]
      : []
  })
  const positions = o.positions.flatMap((p): ClosePositionItem[] => {
    const x = record(p)
    const positionId = num(x?.positionId)
    return x && positionId != null
      ? [{ positionId, contract: text(x.contract), side: text(x.side), lots: num(x.lots) ?? 0, unrealized: num(x.unrealized) }]
      : []
  })
  return {
    account: text(o.account),
    runCount: num(o.runCount) ?? runs.length,
    positionCount: num(o.positionCount) ?? positions.length,
    openLegs: num(o.openLegs) ?? runs.reduce((sum, r) => sum + r.openLegs, 0),
    runs,
    positions,
    notListed: num(o.notListed) ?? 0,
  }
}

/** The action a proposal tool stored, read from the exact JSON the model was given; null for any other step. */
export function readProposal(step: Pick<AiToolStep, 'name' | 'ok' | 'result'>): ProposedAction | null {
  if (!step.ok || !ACTION_TOOLS.includes(step.name)) return null
  let parsed: unknown
  try {
    parsed = JSON.parse(step.result)
  } catch {
    return null
  }
  const data = record(record(parsed)?.data)
  if (!data || data.proposed !== true) return null
  const id = typeof data.actionId === 'number' ? data.actionId : Number.NaN
  const summary = typeof data.summary === 'string' ? data.summary : ''
  if (!Number.isInteger(id) || id <= 0 || !summary) return null
  return {
    id,
    kind: typeof data.kind === 'string' ? data.kind : '',
    summary,
    expiresUtc: typeof data.expiresUtc === 'string' && !Number.isNaN(Date.parse(data.expiresUtc)) ? data.expiresUtc : null,
    items: readCloseItems(data.items),
  }
}

/**
 * The actions an answer proposed, in order, each once. A model that proposed
 * again in the same answer replaced the first on the server, so only the last
 * one can still be confirmed: the earlier ones are marked replaced.
 */
export function proposalsOf(rounds: readonly Pick<TurnRound, 'tools'>[]): { action: ProposedAction; replaced: boolean }[] {
  const seen = new Map<number, ProposedAction>()
  for (const r of rounds) {
    for (const step of r.tools) {
      const p = readProposal(step)
      if (p) seen.set(p.id, p)
    }
  }
  const list = [...seen.values()]
  return list.map((action, i) => ({ action, replaced: i < list.length - 1 }))
}

/** A PIN's shape: 4 to 8 digits, nothing else. */
export function isPinShape(pin: string): boolean {
  return /^[0-9]{4,8}$/.test(pin)
}

/** Why a new PIN cannot be saved yet, or null; the repeat must match when given. */
export function pinProblem(pin: string, repeat?: string): string | null {
  if (!pin) return 'Type 4 to 8 digits.'
  if (!isPinShape(pin)) return 'A PIN is 4 to 8 digits, nothing else.'
  if (repeat !== undefined && repeat !== pin) return 'The two PINs differ.'
  return null
}

/** What an action does, in two or three words, for the card's head. */
export function actionKindLabel(kind: string): string {
  switch (kind) {
    case 'stop_run':
      return 'Stop a run'
    case 'start_run':
      return 'Start a run'
    case 'square_off':
      return 'Square off'
    case 'agent_switch':
      return 'Switch an agent'
    case 'close_account':
      return 'Close an account'
    default:
      return 'Action'
  }
}

/** "7 runs (9 open legs) · 3 manual positions": the count over an account close's list. */
export function closeCountText(items: Pick<CloseItems, 'runCount' | 'positionCount' | 'openLegs'>): string {
  const plural = (n: number, noun: string) => `${n} ${noun}${n === 1 ? '' : 's'}`
  const parts: string[] = []
  if (items.runCount > 0) parts.push(`${plural(items.runCount, 'run')}${items.openLegs > 0 ? ` (${plural(items.openLegs, 'open leg')})` : ''}`)
  if (items.positionCount > 0) parts.push(plural(items.positionCount, 'manual position'))
  return parts.join(' · ') || 'nothing open'
}

/** Whether a reply says only part was done ("Partly done: …"): the card marks it so, not as nothing done. */
export function isPartlyDone(message: string): boolean {
  return message.startsWith('Partly done')
}

/** Seconds until `iso`, never below 0; null when it is not a time. */
export function secondsLeft(iso: string | null | undefined, nowMs: number): number | null {
  const at = iso ? Date.parse(iso) : Number.NaN
  if (Number.isNaN(at)) return null
  return Math.max(0, Math.ceil((at - nowMs) / 1000))
}

/** "1:42 left", "0:05 left", "expired". */
export function waitText(seconds: number | null): string {
  if (seconds == null) return 'waiting for your PIN'
  if (seconds <= 0) return 'expired'
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')} left`
}

function readView(value: unknown): ActionView | null {
  const o = record(value)
  if (!o || typeof o.id !== 'number' || typeof o.status !== 'string') return null
  return {
    id: o.id,
    kind: String(o.kind ?? ''),
    status: o.status as ActionStatus,
    summary: String(o.summary ?? ''),
    channel: String(o.channel ?? ''),
    createdUtc: String(o.createdUtc ?? ''),
    expiresUtc: String(o.expiresUtc ?? ''),
    confirmedUtc: typeof o.confirmedUtc === 'string' ? o.confirmedUtc : null,
    executedUtc: typeof o.executedUtc === 'string' ? o.executedUtc : null,
    result: typeof o.result === 'string' ? o.result : null,
    error: typeof o.error === 'string' ? o.error : null,
    callId: typeof o.callId === 'number' ? o.callId : null,
  }
}

/** A 200 from confirm or cancel, in the card's terms. */
export function readAnswer(body: unknown): ActionOutcome {
  const o = record(body)
  const message = typeof o?.message === 'string' ? o.message : ''
  const action = readView(o?.action)
  if (action?.status === 'cancelled') return { kind: 'cancelled', message: message || 'Cancelled: nothing was done.' }
  const done = action ? action.status === 'executed' : message.startsWith('Done')
  return { kind: 'ran', done, message: message || (done ? 'Done.' : 'Not done.'), action }
}

/** A refused confirm or cancel (its HTTP status and body), in the card's terms; anything unknown is a closed card with the error's words. */
export function readRefusal(error: unknown): ActionOutcome {
  if (!(error instanceof ApiError)) {
    return { kind: 'closed', message: error instanceof Error ? error.message : 'The request failed.', status: null }
  }
  const body = record(error.body)
  const message = typeof body?.error === 'string' ? body.error : error.message
  if (error.status === 403 && typeof body?.attemptsLeft === 'number') {
    return { kind: 'wrong', message, attemptsLeft: body.attemptsLeft }
  }
  if (error.status === 423) {
    return { kind: 'locked', message, lockedUntilUtc: typeof body?.lockedUntilUtc === 'string' ? body.lockedUntilUtc : null }
  }
  if (error.status === 404 && !body) {
    return { kind: 'closed', message: 'This API build cannot confirm actions yet: it is deployed with the next release.', status: null }
  }
  return { kind: 'closed', message, status: readView(body?.action)?.status ?? null }
}

export function readPinStatus(raw: unknown): ActionPinStatus {
  const o = record(raw) ?? {}
  return {
    enabled: o.enabled !== false,
    set: o.set === true,
    lockedUntilUtc: typeof o.lockedUntilUtc === 'string' ? o.lockedUntilUtc : null,
    confirmSeconds: typeof o.confirmSeconds === 'number' ? o.confirmSeconds : 120,
  }
}

/** POST /api/Ai/actions/{id}/confirm with the PIN: the outcome, a refusal included (never thrown). */
export function useConfirmAction() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ id, pin }: { id: number; pin: string }): Promise<ActionOutcome> => {
      try {
        return readAnswer(await api.post<unknown>(`/api/Ai/actions/${id}/confirm`, { pin }))
      } catch (error) {
        return readRefusal(error)
      }
    },
    // A run stopped or started, an agent switched: the pages that show them read again.
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['ai'] }),
  })
}

/** POST /api/Ai/actions/{id}/cancel. */
export function useCancelAction() {
  return useMutation({
    mutationFn: async (id: number): Promise<ActionOutcome> => {
      try {
        return readAnswer(await api.post<unknown>(`/api/Ai/actions/${id}/cancel`))
      } catch (error) {
        return readRefusal(error)
      }
    },
  })
}

/** GET /api/Ai/actions/pin: whether the signed-in admin has a PIN, and whether actions are on. */
export function useActionPin() {
  return useQuery({
    queryKey: ['ai', 'actions', 'pin'],
    queryFn: async () => readPinStatus(await api.get<unknown>('/api/Ai/actions/pin')),
    retry: 1,
  })
}

/** PUT /api/Ai/actions/pin: set or reset the signed-in admin's PIN. */
export function useSetActionPin() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (pin: string) => readPinStatus(await api.put<unknown>('/api/Ai/actions/pin', { pin })),
    onSuccess: (status) => qc.setQueryData(['ai', 'actions', 'pin'], status),
  })
}
