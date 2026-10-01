/**
 * The AI Trader (agent "ai-trader"), as data.
 *
 * The owner asked on 1 Oct for a trader that reads everything the desk
 * records: every ten minutes of the session the code builds a market brief,
 * the model proposes one action as JSON, and code judges it against the
 * owner's limits (private/notes/2026-10-01-ai-trader-design.md). Every look
 * is kept, "do nothing" included, with the brief it read and the rule that
 * judged it. It starts in shadow mode: it decides and places nothing.
 *
 * This module is the page-free half, so it can be tested:
 *   - the shapes of GET /api/AiTrader/status, /decisions and /decisions/{id},
 *     and readers that never trust a body: a field the API left out is null
 *     ("not known"), never a made-up zero, an "off" or an "allowed";
 *   - the words the pages put on a decision: its action ("Buy NIFTY CE"),
 *     the verdict (allowed, refused and by which rule, or no answer), the
 *     model's confidence, the mode, and the limits in one line;
 *   - the shadow book (GET /api/AiTrader/positions): in shadow mode and in a
 *     replay an allowed buy is kept by code as if placed, checked every
 *     minute against its stop and target, and scored after charges. Nothing
 *     in it reaches a broker.
 *
 * The query hooks live with the others in lib/queries.ts. The switch and
 * "Run now" are the agent's own, on AI → Agents (lib/ai.ts).
 */

import { istHm, strategyLabel } from './desk'
import { formatInr, formatInrWhole } from './format'

/** The agent's key in the AI catalog, its calls and its switch. */
export const AI_TRADER_KEY = 'ai-trader'

/** How often today's status and decisions are read again; it looks every ten minutes. */
export const AI_TRADER_POLL_MS = 30_000
/** A replay's decisions while it plays: its clock runs up to ten times faster. */
export const AI_TRADER_REPLAY_POLL_MS = 10_000
/** Decisions a page asks for at a time. */
export const AI_TRADER_PAGE = 50

// ---------- shapes -----------------------------------------------------------------

/** shadow (decides, places nothing), live (places paper orders) or replay; kept a string, so a mode the server adds later still shows. */
export type AiTraderMode = 'shadow' | 'live' | 'replay'
/** What the model proposed; '' when its answer could not be read. */
export type AiTraderAction = 'none' | 'buy' | 'exit' | 'start_strategy' | 'stop_strategy'

/** The owner's limits, as the guard enforces them. A field the API did not send is null. */
export interface AiTraderRules {
  underlyings: string[]
  maxLotsPerTrade: number | null
  maxPremiumPerTrade: number | null
  maxOpenPositions: number | null
  capital: number | null
  /** 0..1: the stop may sit at most this far below the entry premium. */
  maxStopFraction: number | null
  /** Rupees, positive: at or past −this net of charges, nothing new opens that day. */
  dailyLossLimit: number | null
  /** "09:20", IST. */
  openFrom: string | null
  openUntil: string | null
  maxTradesPerDay: number | null
  strategies: string[]
  maxStrategyRuns: number | null
}

/** One day's looks (live and shadow; a replay's are never in these). */
export interface AiTraderDay {
  date: string | null
  /** Every look, "none" included. */
  decisions: number
  /** Looks that proposed something other than "none". */
  actions: number
  /** Proposed actions the rules allowed. */
  allowed: number
  /** Plans a rule refused. */
  refused: number
  /** Looks with no usable answer (the model did not answer, or not as a plan). */
  noAnswer: number
}

export interface AiTraderDecision {
  id: number
  /** The market's time it decided at: now, or a replay's clock. */
  clockUtc: string | null
  /** "11:20", IST. */
  clockIst: string
  /** yyyy-MM-dd, IST. */
  day: string | null
  mode: AiTraderMode | string
  replaySessionId: number | null
  /** Lower-case; '' when the answer could not be read. */
  action: AiTraderAction | string
  /** Upper-case; '' when none was named. */
  underlying: string
  /** CE or PE when the API sends it; the summary does not (yet), the plan does. */
  option: string | null
  reason: string
  /** 0..1, or null when the model gave none (or one out of range). */
  confidence: number | null
  /** True only when the API said so. */
  allowed: boolean
  /** "ok", the rule a plan broke, "no-answer" or "unreadable"; '' when not sent. */
  rule: string
  why: string
  executed: boolean
  error: string
  model: string
  callId: number | null
}

export interface AiTraderStatus {
  /** on or off (the agent's switch); null when the API did not say. */
  status: string | null
  /** shadow or live; null when not said. */
  mode: string | null
  /** Minutes between looks. */
  everyMinutes: number | null
  rules: AiTraderRules | null
  today: AiTraderDay | null
  /** Newest first, at most three. */
  latest: AiTraderDecision[]
  /** Today's shadow book in figures; null when the API did not send it (an older build). */
  shadow: AiTraderShadowDay | null
}

/** A day's shadow book in figures. */
export interface AiTraderShadowDay {
  positions: number
  open: number
  /** Rupees after charges, open positions as if sold at their marks; null when not sent. */
  net: number | null
}

/** stop, target, exit (its own decision), close (the session's close), replay-ended; '' while open. Kept a string. */
export type ShadowExitReason = 'stop' | 'target' | 'exit' | 'close' | 'replay-ended' | ''

/** One position of the shadow book: kept by code as if placed, never sent to a broker. */
export interface AiTraderShadowPosition {
  id: number
  decisionId: number | null
  mode: string
  replaySessionId: number | null
  day: string | null
  symbol: string
  underlying: string
  /** CE or PE; '' when not sent. */
  optionType: string
  strike: number | null
  expiry: string | null
  lots: number | null
  lotSize: number | null
  entryUtc: string | null
  /** "10:00", IST. */
  entryIst: string
  entryPrice: number | null
  stopLoss: number | null
  target: number | null
  /** The last minute check's price; null before the first. */
  markPrice: number | null
  markUtc: string | null
  /** True only when the API said so in as many words. */
  open: boolean
  exitUtc: string | null
  exitIst: string
  exitPrice: number | null
  exitReason: ShadowExitReason | string
  charges: number | null
  /** Rupees after charges: as closed, or for an open one as if sold at its mark now. */
  net: number | null
}

/** GET /api/AiTrader/positions: a day's shadow book or a replay's, oldest first. */
export interface AiTraderShadowBook {
  day: string | null
  replay: number | null
  positions: number
  open: number
  /** After charges; null when not sent. */
  net: number | null
  charges: number | null
  items: AiTraderShadowPosition[]
}

export interface AiTraderDecisionsPage {
  /** Newest first. */
  items: AiTraderDecision[]
  /** The id to ask for the next page with; null on the last page. */
  nextBeforeId: number | null
}

/** The fields of the model's plan the pages name; null for any it did not give. */
export interface AiTraderPlan {
  action: string | null
  underlying: string | null
  option: string | null
  strike: string | null
  lots: number | null
  stopLoss: number | null
  target: number | null
  strategy: string | null
  positionId: number | null
  runId: number | null
}

export interface AiTraderDecisionDetail {
  decision: AiTraderDecision
  /** The brief exactly as the model read it. */
  brief: string
  planJson: string
  resultJson: string
  briefHash: string
  /** The plan, read from planJson; null when it is not a plan (an unreadable answer). */
  plan: AiTraderPlan | null
}

/** Which decisions to list: a day's (IST), or a replay's. */
export interface AiTraderDecisionFilter {
  day?: string | null
  replay?: number | null
}

// ---------- reading the API ---------------------------------------------------------

const record = (v: unknown): v is Record<string, unknown> => v != null && typeof v === 'object' && !Array.isArray(v)
const num = (v: unknown): number | null => (typeof v === 'number' && Number.isFinite(v) ? v : null)
const str = (v: unknown): string | null => (typeof v === 'string' ? v : null)
const words = (v: unknown): string => (str(v) ?? '').trim()
const optional = (v: unknown): string | null => words(v) || null
const rows = (v: unknown): Record<string, unknown>[] => (Array.isArray(v) ? v.filter(record) : [])
const instant = (v: unknown): string | null => {
  const s = str(v)
  return s && !Number.isNaN(Date.parse(s)) ? s : null
}
/** A whole, non-negative count; 0 when missing (the day's counts are counts of rows). */
const count = (v: unknown): number => Math.max(0, Math.round(num(v) ?? 0))
/** A limit: a non-negative number, or null when not sent. */
const limit = (v: unknown): number | null => {
  const n = num(v)
  return n == null || n < 0 ? null : n
}
/** A positive whole id, or null. */
const id = (v: unknown): number | null => {
  const n = num(v)
  return n != null && n > 0 && Number.isInteger(n) ? n : null
}
const names = (v: unknown): string[] =>
  (Array.isArray(v) ? v : []).map(words).filter((s) => s !== '')
const ISO_DAY = /^\d{4}-\d{2}-\d{2}$/
const isoDay = (v: unknown): string | null => {
  const s = words(v).slice(0, 10)
  return ISO_DAY.test(s) ? s : null
}

/** "09:20" from a TimeOnly as System.Text.Json writes it ("09:20:00"), or "09:20", "9:20"; null otherwise. */
export function hm(v: unknown): string | null {
  const m = /^(\d{1,2}):(\d{2})(?::\d{2}(?:\.\d+)?)?$/.exec(words(v))
  if (!m || Number(m[1]) > 23 || Number(m[2]) > 59) return null
  return `${m[1].padStart(2, '0')}:${m[2]}`
}

/** A share 0..1, or null for a missing or out-of-range one. */
function share(v: unknown): number | null {
  const n = num(v)
  return n == null || n < 0 || n > 1 ? null : n
}

export function readAiTraderRules(v: unknown): AiTraderRules | null {
  if (!record(v)) return null
  return {
    underlyings: names(v.underlyings).map((u) => u.toUpperCase()),
    maxLotsPerTrade: limit(v.maxLotsPerTrade),
    maxPremiumPerTrade: limit(v.maxPremiumPerTrade),
    maxOpenPositions: limit(v.maxOpenPositions),
    capital: limit(v.capital),
    maxStopFraction: share(v.maxStopFraction),
    dailyLossLimit: (() => {
      // A limit sent signed (−10000) is the same limit.
      const n = num(v.dailyLossLimit)
      return n == null ? null : Math.abs(n)
    })(),
    openFrom: hm(v.openFrom),
    openUntil: hm(v.openUntil),
    maxTradesPerDay: limit(v.maxTradesPerDay),
    strategies: names(v.strategies),
    maxStrategyRuns: limit(v.maxStrategyRuns),
  }
}

export function readAiTraderDay(v: unknown): AiTraderDay | null {
  if (!record(v)) return null
  return {
    date: isoDay(v.date),
    decisions: count(v.decisions),
    actions: count(v.actions),
    allowed: count(v.allowed),
    refused: count(v.refused),
    noAnswer: count(v.noAnswer),
  }
}

/** One decision summary, or null when it has no id (a row the page could not open). */
export function readAiTraderDecision(v: unknown): AiTraderDecision | null {
  if (!record(v)) return null
  const decisionId = id(v.id)
  if (decisionId == null) return null
  const clockUtc = instant(v.clockUtc)
  const option = words(v.option).toUpperCase()
  return {
    id: decisionId,
    clockUtc,
    clockIst: hm(v.clockIst) ?? istHm(clockUtc),
    day: isoDay(v.day),
    mode: words(v.mode).toLowerCase(),
    replaySessionId: id(v.replaySessionId),
    action: words(v.action).toLowerCase(),
    underlying: words(v.underlying).toUpperCase(),
    option: option === 'CE' || option === 'PE' ? option : null,
    reason: words(v.reason),
    confidence: share(v.confidence),
    allowed: v.allowed === true,
    rule: words(v.rule).toLowerCase(),
    why: words(v.why),
    executed: v.executed === true,
    error: words(v.error),
    model: words(v.model),
    callId: id(v.callId),
  }
}

function decisionList(v: unknown): AiTraderDecision[] {
  return rows(v)
    .map(readAiTraderDecision)
    .filter((d): d is AiTraderDecision => d != null)
}

/**
 * GET /api/AiTrader/status. Throws for a body that is not the status at all
 * (an HTML page, an API without the endpoint), so the page says so instead of
 * drawing an AI Trader that is "off" with no limits.
 */
export function readAiTraderStatus(raw: unknown): AiTraderStatus {
  if (!record(raw) || !['status', 'mode', 'rules', 'today'].some((k) => k in raw)) {
    throw new Error("The AI Trader's status came back in a shape this page cannot read. Is the API build current?")
  }
  const everyMinutes = num(raw.everyMinutes)
  return {
    status: optional(raw.status)?.toLowerCase() ?? null,
    mode: optional(raw.mode)?.toLowerCase() ?? null,
    everyMinutes: everyMinutes != null && everyMinutes > 0 ? everyMinutes : null,
    rules: readAiTraderRules(raw.rules),
    today: readAiTraderDay(raw.today),
    latest: decisionList(raw.latest).slice(0, 3),
    shadow: readAiTraderShadowDay(raw.shadow),
  }
}

/** The status's shadow figures, or null when the body has none. */
export function readAiTraderShadowDay(v: unknown): AiTraderShadowDay | null {
  if (!record(v)) return null
  return { positions: count(v.positions), open: count(v.open), net: num(v.net) }
}

/** One shadow position, or null when it has no id. Prices and money the API left out stay null, never 0. */
export function readAiTraderShadowPosition(v: unknown): AiTraderShadowPosition | null {
  if (!record(v)) return null
  const positionId = id(v.id)
  if (positionId == null) return null
  const entryUtc = instant(v.entryUtc)
  const exitUtc = instant(v.exitUtc)
  const option = words(v.optionType).toUpperCase()
  return {
    id: positionId,
    decisionId: id(v.decisionId),
    mode: words(v.mode).toLowerCase(),
    replaySessionId: id(v.replaySessionId),
    day: isoDay(v.day),
    symbol: words(v.symbol),
    underlying: words(v.underlying).toUpperCase(),
    optionType: option === 'CE' || option === 'PE' ? option : '',
    strike: limit(v.strike),
    expiry: isoDay(v.expiry),
    lots: limit(v.lots),
    lotSize: limit(v.lotSize),
    entryUtc,
    entryIst: hm(v.entryIst) ?? istHm(entryUtc),
    entryPrice: limit(v.entryPrice),
    stopLoss: limit(v.stopLoss),
    target: limit(v.target),
    markPrice: limit(v.markPrice),
    markUtc: instant(v.markUtc),
    open: v.open === true,
    exitUtc,
    exitIst: hm(v.exitIst) ?? istHm(exitUtc),
    exitPrice: limit(v.exitPrice),
    exitReason: words(v.exitReason).toLowerCase(),
    charges: limit(v.charges),
    net: num(v.net),
  }
}

/**
 * GET /api/AiTrader/positions. Throws for a body that is not a book, so the
 * page says it could not be read instead of drawing an empty book with ₹0.
 * Rows come oldest first; they are kept in that order.
 */
export function readAiTraderShadowBook(raw: unknown): AiTraderShadowBook {
  if (!record(raw) || !Array.isArray(raw.items)) {
    throw new Error("The AI Trader's shadow book came back in a shape this page cannot read. Is the API build current?")
  }
  const items = raw.items.map(readAiTraderShadowPosition).filter((p): p is AiTraderShadowPosition => p != null)
  const positions = num(raw.positions)
  const open = num(raw.open)
  return {
    day: isoDay(raw.day),
    replay: id(raw.replay),
    positions: positions == null ? items.length : count(positions),
    open: open == null ? items.filter((p) => p.open).length : count(open),
    net: num(raw.net),
    charges: limit(raw.charges),
    items,
  }
}

/** GET /api/AiTrader/decisions. A bare array is read as the items; anything else that is not a page throws. */
export function readAiTraderDecisionsPage(raw: unknown): AiTraderDecisionsPage {
  if (Array.isArray(raw)) return { items: decisionList(raw), nextBeforeId: null }
  if (!record(raw) || !Array.isArray(raw.items)) {
    throw new Error("The AI Trader's decisions came back in a shape this page cannot read. Is the API build current?")
  }
  return { items: decisionList(raw.items), nextBeforeId: id(raw.nextBeforeId) }
}

/** The plan the model proposed, read from its JSON; null when it is not a JSON object or holds no plan field. */
export function readAiTraderPlan(json: string | null | undefined): AiTraderPlan | null {
  let v: unknown
  try {
    v = JSON.parse(json ?? '')
  } catch {
    return null
  }
  if (!record(v)) return null
  const strike = typeof v.strike === 'number' && Number.isFinite(v.strike) ? String(v.strike) : optional(v.strike)
  const option = words(v.option).toUpperCase()
  const plan: AiTraderPlan = {
    action: optional(v.action)?.toLowerCase() ?? null,
    underlying: optional(v.underlying)?.toUpperCase() ?? null,
    option: option === 'CE' || option === 'PE' ? option : null,
    strike,
    lots: num(v.lots),
    stopLoss: num(v.stopLoss),
    target: num(v.target),
    strategy: optional(v.strategy),
    positionId: id(v.positionId),
    runId: id(v.runId),
  }
  return plan.action == null && plan.underlying == null && plan.option == null ? null : plan
}

/** GET /api/AiTrader/decisions/{id}. Throws when the body holds no decision it can show. */
export function readAiTraderDetail(raw: unknown): AiTraderDecisionDetail {
  const decision = record(raw) ? readAiTraderDecision(raw.decision) : null
  if (!record(raw) || !decision) {
    throw new Error('This decision came back in a shape this page cannot read.')
  }
  const planJson = str(raw.planJson) ?? ''
  return {
    decision,
    brief: str(raw.brief) ?? '',
    planJson,
    resultJson: str(raw.resultJson) ?? '',
    briefHash: words(raw.briefHash),
    plan: readAiTraderPlan(planJson),
  }
}

/** The query string of GET /api/AiTrader/positions: a replay's book, else a day's (today when none is named). */
export function aiTraderPositionsQuery(filter: AiTraderDecisionFilter): string {
  const p = new URLSearchParams()
  if (filter.replay != null) p.set('replay', String(filter.replay))
  else if (filter.day) p.set('day', filter.day)
  return p.toString()
}

/** The query string of GET /api/AiTrader/decisions: a day or a replay, a page size, and where the page starts. */
export function aiTraderDecisionsQuery(filter: AiTraderDecisionFilter, beforeId: number | null, take = AI_TRADER_PAGE): string {
  const p = new URLSearchParams()
  if (filter.day) p.set('day', filter.day)
  if (filter.replay != null) p.set('replay', String(filter.replay))
  p.set('take', String(take))
  if (beforeId != null) p.set('beforeId', String(beforeId))
  return p.toString()
}

// ---------- words -------------------------------------------------------------------

export type Tone = 'pos' | 'neg' | 'warn' | 'accent' | 'live' | 'neutral'

/** The rules a refusal can name: a short word for the badge, and what it means. */
const RULES: Record<string, { short: string; means: string }> = {
  hours: { short: 'hours', means: 'New positions and strategy starts open only within the allowed hours on a trading day.' },
  'daily-loss': { short: 'daily loss', means: "Today's net, after charges, reached the daily loss limit: nothing new opens today." },
  size: { short: 'size', means: 'Too many lots, or more premium than a trade may hold.' },
  stop: { short: 'stop-loss', means: 'Every buy needs a stop-loss premium below the entry, and not too far below it.' },
  target: { short: 'target', means: 'Every buy needs a target premium above the entry.' },
  instrument: { short: 'instrument', means: 'Only NIFTY, BANKNIFTY or SENSEX options, CE or PE.' },
  contract: { short: 'no contract', means: 'No listed contract with a price matched the underlying, option and strike.' },
  'open-positions': { short: 'open positions', means: 'As many positions are open as it may hold at once.' },
  capital: { short: 'capital', means: "The premium in use plus this trade would pass the account's capital." },
  'trades-a-day': { short: 'trades a day', means: 'It has opened as many trades today as a day allows.' },
  'strategy-list': { short: 'strategy list', means: 'Only the strategies on its allow-list may be started.' },
  'strategy-runs': { short: 'strategy runs', means: 'As many of its strategy runs are running as it may have.' },
  'own-book': { short: 'not its position', means: 'It may close only its own open positions.' },
  'own-runs': { short: 'not its run', means: 'It may stop only its own strategy runs.' },
  'kill-switch': { short: 'kill switch', means: "The desk's kill switch is on: nothing new is placed." },
  action: { short: 'action', means: 'The plan named an action it may not take.' },
}

/** A rule as a short word, and what it means; a rule this page does not know shows by its own name. */
export function ruleLabel(rule: string): { short: string; means: string } {
  const known = RULES[rule]
  if (known) return known
  return { short: rule.replace(/[-_]+/g, ' ').trim(), means: '' }
}

export type VerdictKey = 'allowed' | 'refused' | 'no-answer'

/**
 * A decision's verdict for its badge: allowed, refused (and by which rule),
 * or no answer. An allowed "do nothing" is quiet (neutral); an allowed action
 * is the one that stands out. A refusal is the guard doing its job, so it is
 * a warning, never an error. The label always says it in words.
 */
export function verdictOf(d: { action: string; allowed: boolean; rule: string }): { key: VerdictKey; label: string; tone: Tone; means: string } {
  if (d.rule === 'no-answer') {
    return { key: 'no-answer', label: 'No answer', tone: 'neutral', means: 'The model did not answer (a timeout, a refusal or a provider error): this look decided nothing.' }
  }
  if (d.rule === 'unreadable') {
    return { key: 'no-answer', label: 'Unreadable', tone: 'neutral', means: 'The model answered, but not as one JSON plan: this look decided nothing.' }
  }
  if (d.allowed) {
    const acted = d.action !== '' && d.action !== 'none'
    return {
      key: 'allowed',
      label: 'Allowed',
      tone: acted ? 'pos' : 'neutral',
      means: acted ? 'The rules allowed the plan.' : 'Nothing to do: no rule needed to judge it.',
    }
  }
  if (!d.rule || d.rule === 'ok') return { key: 'refused', label: 'Refused', tone: 'warn', means: 'The rules refused the plan; the API did not say which rule.' }
  const r = ruleLabel(d.rule)
  return { key: 'refused', label: `Refused · ${r.short}`, tone: 'warn', means: r.means }
}

/** The fields actionText reads; a plan's fields, when known, fill in what the summary leaves out. */
export type ActionFacts = Pick<AiTraderDecision, 'action' | 'underlying' | 'rule'> &
  Partial<Pick<AiTraderDecision, 'option'>> & { plan?: AiTraderPlan | null }

/**
 * What it proposed, in a few words: "Buy NIFTY CE", "Exit position #12",
 * "Start Chain Flow Buy on BANKNIFTY", "Do nothing". The option, the strategy
 * and the ids come from the plan when the page has it; the list's summary
 * names only the action and the underlying.
 */
export function actionText(d: ActionFacts): string {
  const plan = d.plan ?? null
  const underlying = d.underlying || plan?.underlying || ''
  const option = d.option ?? plan?.option ?? null
  switch (d.action) {
    case 'none':
      return 'Do nothing'
    case 'buy':
      return ['Buy', underlying, option].filter(Boolean).join(' ')
    case 'exit':
      return plan?.positionId != null ? `Exit position #${plan.positionId}` : ['Exit', underlying].filter(Boolean).join(' ')
    case 'start_strategy': {
      const what = plan?.strategy ? strategyLabel(plan.strategy) : 'a strategy'
      return `Start ${what}${underlying ? ` on ${underlying}` : ''}`
    }
    case 'stop_strategy':
      return plan?.runId != null ? `Stop run #${plan.runId}` : `Stop a strategy${underlying ? ` on ${underlying}` : ''}`
    case '':
      // The verdict says why (no answer, or one it could not read).
      return 'No decision'
    default:
      // An action the guard refuses ("sell"): shown as the model wrote it.
      return `“${d.action}”${underlying ? ` ${underlying}` : ''}`
  }
}

/** The plan's numbers in a line: "strike ATM+1 · 2 lots · stop ₹82.50 · target ₹140.00"; '' when it gave none. */
export function planFacts(plan: AiTraderPlan | null | undefined): string {
  if (!plan) return ''
  const parts: string[] = []
  if (plan.strike) parts.push(`strike ${plan.strike}`)
  if (plan.lots != null) parts.push(`${plan.lots} ${plan.lots === 1 ? 'lot' : 'lots'}`)
  if (plan.stopLoss != null) parts.push(`stop ${formatInr(plan.stopLoss)}`)
  if (plan.target != null) parts.push(`target ${formatInr(plan.target)}`)
  return parts.join(' · ')
}

/** "72%" for 0.72; '' when the model gave none. */
export function confidenceText(confidence: number | null | undefined): string {
  if (confidence == null || !Number.isFinite(confidence) || confidence < 0 || confidence > 1) return ''
  return `${Math.round(confidence * 100)}%`
}

/** "₹5 lakh", "₹2.5 lakh", "₹1 crore", "₹50,000"; '' for none. */
export function lakhText(rupees: number | null | undefined): string {
  if (rupees == null || !Number.isFinite(rupees) || rupees < 0) return ''
  const trim = (n: number) => String(Math.round(n * 100) / 100)
  if (rupees >= 1_00_00_000) return `₹${trim(rupees / 1_00_00_000)} crore`
  if (rupees >= 1_00_000) return `₹${trim(rupees / 1_00_000)} lakh`
  return formatInrWhole(rupees)
}

/** A mode as a word, a tone and what it does. */
export function modeLabel(mode: string | null | undefined, capital?: number | null): { label: string; tone: Tone; means: string } {
  switch (mode) {
    case 'shadow':
      return { label: 'Shadow', tone: 'neutral', means: 'decides, places nothing' }
    case 'live': {
      const money = lakhText(capital)
      return { label: 'Live', tone: 'live', means: `places paper orders in its own ${money ? `${money} ` : ''}account` }
    }
    case 'replay':
      return { label: 'Replay', tone: 'accent', means: "decides on a replay's clock, places nothing" }
    default:
      return { label: mode ? mode.charAt(0).toUpperCase() + mode.slice(1) : 'Not known', tone: 'neutral', means: '' }
  }
}

/**
 * The limits in one line, each part only when the API sent it:
 * "NIFTY, BANKNIFTY, SENSEX options, buying only · 2 lots a trade · ₹50,000 of
 * premium a trade · 3 positions at once · stop at most 40% below entry ·
 * daily loss limit ₹10,000 · new positions 09:20–14:45 IST · 10 trades a day
 * · strategies: Ghost Tangent Crossings, Chain Flow Buy (3 runs at most)".
 */
export function limitsParts(r: AiTraderRules): string[] {
  const parts: string[] = []
  const many = (n: number, one: string, more = `${one}s`) => `${n} ${n === 1 ? one : more}`
  if (r.underlyings.length > 0) parts.push(`${r.underlyings.join(', ')} options, buying only`)
  if (r.maxLotsPerTrade != null) parts.push(`${many(r.maxLotsPerTrade, 'lot')} a trade`)
  if (r.maxPremiumPerTrade != null) parts.push(`${formatInrWhole(r.maxPremiumPerTrade)} of premium a trade`)
  if (r.maxOpenPositions != null) parts.push(`${many(r.maxOpenPositions, 'position')} at once`)
  if (r.maxStopFraction != null) parts.push(`stop at most ${Math.round(r.maxStopFraction * 100)}% below entry`)
  if (r.dailyLossLimit != null) parts.push(`daily loss limit ${formatInrWhole(r.dailyLossLimit)}`)
  if (r.openFrom && r.openUntil) parts.push(`new positions ${r.openFrom}–${r.openUntil} IST`)
  if (r.maxTradesPerDay != null) parts.push(`${many(r.maxTradesPerDay, 'trade')} a day`)
  if (r.strategies.length > 0) {
    const runs = r.maxStrategyRuns != null ? ` (${many(r.maxStrategyRuns, 'run')} at most)` : ''
    parts.push(`strategies: ${r.strategies.map(strategyLabel).join(', ')}${runs}`)
  }
  return parts
}

/** "34 looks · 3 actions · 2 allowed · 1 refused · 0 no answer". */
export function dayCountsText(d: Pick<AiTraderDay, 'decisions' | 'actions' | 'allowed' | 'refused' | 'noAnswer'>): string {
  const n = (v: number, one: string, more = `${one}s`) => `${v} ${v === 1 ? one : more}`
  return [n(d.decisions, 'look'), n(d.actions, 'action'), `${d.allowed} allowed`, `${d.refused} refused`, `${d.noAnswer} no answer`].join(' · ')
}

/**
 * Whether an allowed action was placed, in words; null when there was nothing
 * to place (a "do nothing", a refusal, no answer).
 */
export function placedText(d: Pick<AiTraderDecision, 'action' | 'allowed' | 'executed' | 'mode' | 'error'>): { text: string; tone: Tone } | null {
  if (!d.allowed || d.action === '' || d.action === 'none') return null
  if (d.executed) return { text: 'Placed in its own account', tone: 'pos' }
  if (d.mode === 'shadow') return { text: 'Not placed: shadow mode', tone: 'neutral' }
  if (d.mode === 'replay') return { text: 'Not placed: a replay places nothing', tone: 'neutral' }
  if (d.error) return { text: `Not placed: ${d.error}`, tone: 'warn' }
  return { text: 'Not placed', tone: 'warn' }
}

/** The JSON the page shows: pretty when it parses, as written when not; '' for an empty object. */
export function jsonBlock(text: string | null | undefined): string {
  const t = (text ?? '').trim()
  if (!t || t === '{}' || t === 'null') return ''
  try {
    return JSON.stringify(JSON.parse(t), null, 2)
  } catch {
    return t
  }
}

// ---------- the shadow book ------------------------------------------------------------

/** "NIFTY 22650 CE": the underlying, the strike and the option; the symbol when those are not known. */
export function contractText(p: Pick<AiTraderShadowPosition, 'underlying' | 'strike' | 'optionType' | 'symbol'>): string {
  if (!p.underlying || p.strike == null) return p.symbol || '—'
  const strike = Number.isInteger(p.strike) ? String(p.strike) : String(Math.round(p.strike * 100) / 100)
  return [p.underlying, strike, p.optionType].filter(Boolean).join(' ')
}

/** "2 lots × 75", "1 lot"; '' when not known. */
export function lotsText(lots: number | null, lotSize: number | null): string {
  if (lots == null) return ''
  const unit = lots === 1 ? 'lot' : 'lots'
  return lotSize != null && lotSize > 0 ? `${lots} ${unit} × ${lotSize}` : `${lots} ${unit}`
}

const ENDINGS: Record<string, { label: string; tone: Tone; means: string }> = {
  stop: { label: 'Stop', tone: 'neg', means: 'Its bid reached the stop at a minute check: closed at that price, which can be under the stop.' },
  target: { label: 'Target', tone: 'pos', means: 'Its bid reached the target at a minute check: closed at that price.' },
  exit: { label: 'Its exit', tone: 'neutral', means: 'Closed by its own allowed exit decision, at the bid.' },
  close: { label: 'Close', tone: 'neutral', means: "Squared off at the session's close, 15:30 IST." },
  'replay-ended': { label: 'Replay ended', tone: 'neutral', means: 'Still open when the replay ended: closed at its last mark.' },
}

/** How a position ended, as a word and a tone; an open one is "Open". A reason this page does not know shows by its own name. */
export function shadowEnding(p: Pick<AiTraderShadowPosition, 'open' | 'exitReason'>): { label: string; tone: Tone; means: string } {
  if (p.open) return { label: 'Open', tone: 'live', means: 'Still open: its net is as if sold at its last mark.' }
  const known = ENDINGS[p.exitReason]
  if (known) return known
  const r = p.exitReason.replace(/[-_]+/g, ' ').trim()
  return { label: r ? r.charAt(0).toUpperCase() + r.slice(1) : 'Closed', tone: 'neutral', means: '' }
}

/** The price a row ends on: the exit's when closed, else the last mark (null before the first check). */
export function shadowLastPrice(p: Pick<AiTraderShadowPosition, 'open' | 'exitPrice' | 'markPrice'>): { price: number | null; kind: 'exit' | 'mark' } {
  return p.open ? { price: p.markPrice, kind: 'mark' } : { price: p.exitPrice ?? p.markPrice, kind: 'exit' }
}

/** "3 positions · 1 open": the book's counts in words. */
export function shadowCountsText(b: Pick<AiTraderShadowBook, 'positions' | 'open'>): string {
  return `${b.positions} ${b.positions === 1 ? 'position' : 'positions'} · ${b.open} open`
}

/**
 * The shadow position a decision's result names, and what happened to it in
 * a line: "Shadow position #12" for a buy; with the exit's price and net for
 * an exit. Null when the result names none (a refusal, a strategy start).
 */
export function shadowResultText(resultJson: string | null | undefined): { positionId: number; text: string } | null {
  let v: unknown
  try {
    v = JSON.parse(resultJson ?? '')
  } catch {
    return null
  }
  if (!record(v)) return null
  const positionId = id(v.shadowPositionId)
  if (positionId == null) return null
  const parts = [`Shadow position #${positionId}`]
  const exitPrice = limit(v.exitPrice)
  const net = num(v.netPnl)
  if (exitPrice != null) parts.push(`closed at ${formatInr(exitPrice)}`)
  if (net != null) {
    const r = Math.round(net)
    parts.push(`net ${r === 0 ? '₹0' : `${r > 0 ? '+' : '−'}${formatInrWhole(Math.abs(r))}`} after charges`)
  }
  const note = words(v.note)
  return { positionId, text: parts.join(', ') + (note ? `: ${note}` : '') }
}

/** A rupee figure's tone as it is printed (rounded): a ₹0 book is not green. */
export function netTone(v: number | null | undefined): 'pos' | 'neg' | '' {
  if (v == null || !Number.isFinite(v)) return ''
  const r = Math.round(v)
  return r > 0 ? 'pos' : r < 0 ? 'neg' : ''
}
