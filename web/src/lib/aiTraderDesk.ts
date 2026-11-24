/**
 * The AI Trader's own page (AI → AI Trader): GET /api/AiTrader/desk read into
 * shapes, and the plain words the page shows.
 *
 * The owner, 24 Nov 2026: "mujhe ek pura section chaiye jismai ai trader trade
 * karta hua dikhey aur apna sb manage krey". One page shows:
 * - what it is doing now;
 * - its trades and the scripts it runs;
 * - every look it took.
 *
 * A replay it was asked into (Data → Replay) shows as its practice.
 */

import type { AiTraderAction, AiTraderDecision, AiTraderRules, Tone } from './aiTrader'
import { readAiTraderDecision, readAiTraderRules } from './aiTrader'

/** One position of its own (manual) book: open now, or closed today. */
export interface AiTraderDeskPosition {
  id: number
  symbol: string
  lots: number | null
  lotSize: number | null
  openedUtc: string | null
  /** The average buy price. */
  entry: number | null
  /** The last price it was marked at; a closed one's is its exit fill. */
  mark: number | null
  closedUtc: string | null
  stopLoss: number | null
  target: number | null
  /** The whole trade's P&L before charges, as closed or at its mark. */
  pnl: number | null
  open: boolean
}

/** A strategy run it started: running, or started today. */
export interface AiTraderDeskRun {
  runId: number
  strategy: string
  underlying: string
  status: string
  startedUtc: string | null
  endedUtc: string | null
  /** After charges, the run's whole life. */
  net: number | null
  running: boolean
}

export interface AiTraderDesk {
  /** on or off. */
  status: string
  /** live (it places paper orders) or shadow (it only records). */
  mode: string
  everyMinutes: number | null
  /** "10:35", IST, when the API answered. */
  clockIst: string
  tradingDay: boolean
  /** Inside its opening hours on a trading day. */
  openNow: boolean
  /** "09:20–14:45". */
  hours: string
  rules: AiTraderRules | null
  /** A line per script it may start: what the tests found. */
  strategyNotes: Record<string, string>
  accountId: number | null
  accountName: string
  netToday: number | null
  chargesToday: number | null
  openedToday: number
  /** How much more it may lose today before the daily limit stops new trades. */
  lossLeft: number | null
  positions: AiTraderDeskPosition[]
  runs: AiTraderDeskRun[]
  lastLook: AiTraderDecision | null
}

const record = (v: unknown): v is Record<string, unknown> => v != null && typeof v === 'object' && !Array.isArray(v)
const num = (v: unknown): number | null => (typeof v === 'number' && Number.isFinite(v) ? v : null)
const str = (v: unknown): string => (typeof v === 'string' ? v.trim() : '')
const instant = (v: unknown): string | null => {
  const s = str(v)
  return s && !Number.isNaN(Date.parse(s)) ? s : null
}
const rows = (v: unknown): Record<string, unknown>[] => (Array.isArray(v) ? v.filter(record) : [])

export function readAiTraderDeskPosition(v: Record<string, unknown>): AiTraderDeskPosition | null {
  const id = num(v.id)
  const symbol = str(v.symbol)
  if (id == null || !symbol) return null
  const closedUtc = instant(v.closedUtc)
  return {
    id,
    symbol,
    lots: num(v.lots),
    lotSize: num(v.lotSize),
    openedUtc: instant(v.openedUtc),
    entry: num(v.entry),
    mark: num(v.mark),
    closedUtc,
    stopLoss: num(v.stopLoss),
    target: num(v.target),
    pnl: num(v.pnl),
    open: closedUtc == null,
  }
}

export function readAiTraderDeskRun(v: Record<string, unknown>): AiTraderDeskRun | null {
  const runId = num(v.runId)
  const strategy = str(v.strategy)
  if (runId == null || !strategy) return null
  return {
    runId,
    strategy,
    underlying: str(v.underlying),
    status: str(v.status),
    startedUtc: instant(v.startedUtc),
    endedUtc: instant(v.endedUtc),
    net: num(v.net),
    running: v.running === true,
  }
}

export function readAiTraderDesk(raw: unknown): AiTraderDesk {
  const v = record(raw) ? raw : {}
  const rulesRaw = record(v.rules) ? v.rules : null
  const notes: Record<string, string> = {}
  if (rulesRaw && record(rulesRaw.strategyNotes)) {
    for (const [k, note] of Object.entries(rulesRaw.strategyNotes)) {
      if (typeof note === 'string' && note.trim()) notes[k] = note.trim()
    }
  }
  return {
    status: str(v.status) || 'off',
    mode: str(v.mode),
    everyMinutes: num(v.everyMinutes),
    clockIst: str(v.clockIst),
    tradingDay: v.tradingDay === true,
    openNow: v.openNow === true,
    hours: str(v.hours),
    rules: readAiTraderRules(v.rules),
    strategyNotes: notes,
    accountId: num(v.accountId),
    accountName: str(v.accountName) || 'ai-trader',
    netToday: num(v.netToday),
    chargesToday: num(v.chargesToday),
    openedToday: Math.max(0, Math.round(num(v.openedToday) ?? 0)),
    lossLeft: num(v.lossLeft),
    positions: rows(v.positions).map(readAiTraderDeskPosition).filter((p): p is AiTraderDeskPosition => p != null),
    runs: rows(v.runs).map(readAiTraderDeskRun).filter((r): r is AiTraderDeskRun => r != null),
    lastLook: v.lastLook == null ? null : readAiTraderDecision(v.lastLook),
  }
}

/** "NIFTY 25600 CE" from "NSE:NIFTY25N2525600CE"-like symbols when the strike and side can be read; else the symbol without its exchange. */
export function contractName(symbol: string): string {
  const bare = symbol.replace(/^[A-Z]+:/, '')
  const m = /^([A-Z]+?)(\d{2}[A-Z0-9]{3,5}?)(\d{3,6})(CE|PE)$/.exec(bare)
  return m ? `${m[1]} ${m[3]} ${m[4]}` : bare
}

/** One sentence on whether it is working now, for the top of its page. */
export function nowText(d: Pick<AiTraderDesk, 'status' | 'mode' | 'tradingDay' | 'openNow' | 'hours' | 'everyMinutes' | 'clockIst'>): {
  text: string
  tone: Tone
} {
  const every = d.everyMinutes ? `every ${d.everyMinutes} minutes` : 'every few minutes'
  if (d.status !== 'on') return { text: 'Switched off: it neither looks nor trades. It is switched on on AI → Agents.', tone: 'neutral' }
  const placing = d.mode === 'live' ? 'it places paper orders in its own account' : 'it only records what it would do (shadow)'
  if (d.openNow) return { text: `On and trading: it looks ${every} during ${d.hours} IST, and ${placing}.`, tone: 'live' }
  if (!d.tradingDay) return { text: `On, but there is no market today. It starts again on the next trading day, ${d.hours} IST.`, tone: 'neutral' }
  const [from] = d.hours.split('–')
  if (from && d.clockIst && d.clockIst < from) return { text: `On: it starts looking at ${from} IST today.`, tone: 'neutral' }
  return { text: 'On, done for today: no new trades after its hours; an open trade is sold at the close.', tone: 'neutral' }
}

/** What a look did, in two or three words, for its badge. */
export function lookBadge(d: Pick<AiTraderDecision, 'action' | 'allowed' | 'rule' | 'executed' | 'mode'>): { label: string; tone: Tone } {
  if (d.rule === 'no-answer' || d.rule === 'unreadable') return { label: 'No answer', tone: 'neutral' }
  const action = d.action as AiTraderAction | string
  if (action === 'none' || action === '') return { label: 'Waited', tone: 'neutral' }
  const did: Record<string, string> = {
    buy: 'Bought',
    exit: 'Sold',
    start_strategy: 'Started a script',
    stop_strategy: 'Stopped a script',
  }
  const wanted: Record<string, string> = {
    buy: 'Wanted to buy',
    exit: 'Wanted to sell',
    start_strategy: 'Wanted to start a script',
    stop_strategy: 'Wanted to stop a script',
  }
  if (!d.allowed) return { label: `${wanted[action] ?? 'Wanted to act'}: refused`, tone: 'warn' }
  return { label: did[action] ?? action, tone: action === 'buy' || action === 'start_strategy' ? 'pos' : 'accent' }
}
