/**
 * The Desk (/desk): the one screen kept open all day, as data.
 *
 * Everything the Desk decides lives here, so it is tested rather than
 * eyeballed: which part of the day it is, which panels show and in what
 * order, the strategy × underlying × account grid and its totals, the open
 * legs, the day's timeline, the news filters. The components only lay it out.
 *
 * Two rules run through all of it, the same as on the rest of the console:
 *
 * - P&L is net of charges, always. A run's net is what its card on the Live
 *   runner shows: realized plus the open book, minus the statutory charges of
 *   its fills (lib/strategyList.ts), so the grid and the card never disagree.
 * - "Not known" is never rendered as a fact. A helper that has nothing to go
 *   on returns null, and the panel says it is waiting; it never invents a
 *   zero, an "off" or an "all clear".
 */

import type { Forecast, ScoreboardRow } from './analysis'
import { asProb, asRange } from './analysis'
import type { CheckupSummary } from './checkup'
import { SLOT_LABEL } from './checkup'
import { deployState, deploySummary } from './deploys'
import { expectedMove, istDate } from './factors'
import type { MarketEvent } from './factors'
import { formatInrSigned } from './format'
import { SEVERITY_LABEL, severityTone } from './incidents'
import type { Access, Requirement } from './modules'
import { allows } from './modules'
import { runUserLabel, shortStopReason } from './runHistory'
import { liveNet, realizedNet } from './strategyList'
import type {
  DeployRecord,
  DeskPlanLine,
  DeskPlanResponse,
  DeskPlanRun,
  Incident,
  IntelAnnouncement,
  IntelBoardMeeting,
  IntelDailyBar,
  IntelHeadline,
  IntelSnapshot,
  LiveBar,
  LiveRunSummary,
  MarketPulseResponse,
  MarketSessionInfo,
  OpenPosition,
  OptionChain,
  OptionChainQuote,
} from './types'

// ---------------------------------------------------------------- time, in IST

const IST_OFFSET_MS = 330 * 60_000

/** Today in IST, yyyy-MM-dd, whatever the browser's zone. */
export function istDay(ms: number): string {
  return istDate(new Date(ms))
}

/** Minutes since midnight IST. */
export function istMinute(ms: number): number {
  const d = new Date(ms + IST_OFFSET_MS)
  return d.getUTCHours() * 60 + d.getUTCMinutes()
}

/** "11:42" in IST; '' for a missing or unreadable stamp. */
export function istHm(iso: string | null | undefined): string {
  const ms = iso ? Date.parse(iso) : NaN
  if (Number.isNaN(ms)) return ''
  const d = new Date(ms + IST_OFFSET_MS)
  return `${String(d.getUTCHours()).padStart(2, '0')}:${String(d.getUTCMinutes()).padStart(2, '0')}`
}

/** "Fri" for a yyyy-MM-dd date. */
export function weekdayOf(date: string): string {
  const [y, m, d] = date.split('-').map(Number)
  return new Date(Date.UTC(y, m - 1, d)).toLocaleDateString('en-US', { weekday: 'short', timeZone: 'UTC' })
}

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']

/** "29 Sep" for a yyyy-MM-dd date. Built by hand: en-GB now spells the month "Sept". */
export function dayMonth(date: string): string {
  const [, m, d] = date.split('-').map(Number)
  return `${String(d).padStart(2, '0')} ${MONTHS[m - 1]}`
}

/** "Tue 29 Sep" for a yyyy-MM-dd date. */
export function dayLabel(date: string): string {
  const [, m, d] = date.split('-').map(Number)
  return `${weekdayOf(date)} ${d} ${MONTHS[m - 1]}`
}

/** yyyy-MM-dd shifted by whole days. */
export function shiftDay(date: string, days: number): string {
  const [y, m, d] = date.split('-').map(Number)
  return new Date(Date.UTC(y, m - 1, d + days)).toISOString().slice(0, 10)
}

function msOf(iso: string | null | undefined): number | null {
  const ms = iso ? Date.parse(iso) : NaN
  return Number.isNaN(ms) ? null : ms
}

/** The IST date an instant falls on, or null. */
export function dayOf(iso: string | null | undefined): string | null {
  const ms = msOf(iso)
  return ms == null ? null : istDay(ms)
}

/**
 * The day the Desk reports on. Today, unless today has no session and no
 * runs (a weekend, a holiday): then the last day in `recent` that had runs,
 * so a Saturday opens on Friday's result rather than on an empty sheet.
 */
export function deskDay(
  today: string,
  isTradingDay: boolean | undefined,
  todayRuns: number,
  recent: ReadonlyArray<Pick<LiveRunSummary, 'startedUtc'>> | undefined,
): string {
  if (todayRuns > 0 || isTradingDay !== false) return today
  const days = (recent ?? []).map((r) => dayOf(r.startedUtc)).filter((d): d is string => d != null && d < today)
  return days.length ? days.sort()[days.length - 1] : today
}

/** "in 16 min", "in 2 h 05 min"; null once it has passed. */
export function untilText(targetMs: number, nowMs: number): string | null {
  const minutes = Math.ceil((targetMs - nowMs) / 60_000)
  if (minutes <= 0) return null
  if (minutes < 60) return `in ${minutes} min`
  return `in ${Math.floor(minutes / 60)} h ${String(minutes % 60).padStart(2, '0')} min`
}

// ---------------------------------------------------------------- the part of the day

/**
 * Before the open, the session, after the close. The Desk keeps the same
 * panels all day and changes their order and emphasis with these.
 */
export type Phase = 'pre' | 'live' | 'post'

export const PHASES: ReadonlyArray<{ key: Phase; label: string; short: string; title: string }> = [
  { key: 'pre', label: 'Before open', short: 'Pre-open', title: 'Before the open' },
  { key: 'live', label: 'Session', short: 'Session', title: 'Session' },
  { key: 'post', label: 'After close', short: 'Closed', title: 'After the close' },
]

const NSE_OPEN_MIN = 9 * 60 + 15
const NSE_CLOSE_MIN = 15 * 60 + 30

/**
 * The phase the clock and the NSE session put the Desk in. The session is the
 * API's answer (holidays and special sessions included); until it arrives,
 * the standard 09:15–15:30 on weekdays stands in, because the phase only
 * orders the panels and claims nothing about the market. A day without a
 * session (a weekend, a holiday) shows the last session's close.
 */
export function clockPhase(
  nowMs: number,
  session?: Pick<MarketSessionInfo, 'isTradingDay' | 'sessionOpenUtc' | 'sessionCloseUtc'> | null,
): Phase {
  if (session) {
    if (!session.isTradingDay) return 'post'
    const open = msOf(session.sessionOpenUtc)
    const close = msOf(session.sessionCloseUtc)
    if (open != null && close != null && istDay(open) === istDay(nowMs)) {
      return nowMs < open ? 'pre' : nowMs < close ? 'live' : 'post'
    }
  }
  const weekday = new Date(nowMs + IST_OFFSET_MS).getUTCDay()
  if (weekday === 0 || weekday === 6) return 'post'
  const minute = istMinute(nowMs)
  return minute < NSE_OPEN_MIN ? 'pre' : minute < NSE_CLOSE_MIN ? 'live' : 'post'
}

/** A phase the viewer clicked to look at, and what the clock said when they did. */
export interface PhasePick {
  phase: Phase
  from: Phase
}

/**
 * A phase held until the clock next moves (09:15, 15:30) or the IST day ends,
 * whichever is first, across reloads and tabs.
 */
export interface PhasePin {
  phase: Phase
  date: string
  /** The phase the clock was in when the pin was set: it holds while the clock stays there. */
  from: Phase
}

/**
 * Whether a pin still holds. Until 28 Sep a pin held for the whole day, so
 * "Before open" pinned at 08:50 kept the Desk on the 08:55 readiness list at
 * 13:15, long after the open, showing a checkup nobody needed any more as if
 * it were the state of the desk. Now it ends where the Desk would reorder
 * itself: a pin set before the open ends at the open, one set in the session
 * ends at the close, one set after the close ends with the day.
 */
export function pinHolds(pin: PhasePin | null, clock: Phase, today: string): boolean {
  return pin != null && pin.date === today && pin.from === clock
}

/** When a pin set now would end, for the pin's own words. */
export function pinUntil(clock: Phase): string {
  return clock === 'pre' ? 'until the open' : clock === 'live' ? 'until the close' : 'for the rest of the day'
}

/**
 * What the Desk shows. A pin holds until the clock next moves or the day
 * ends (pinHolds); a click to look at another phase lasts until the clock
 * next moves, when the Desk reorders itself; otherwise the clock decides.
 * Until the market session has been read (`clockKnown` false) the clock is
 * the standard hours' guess, which is wrong on a holiday, so a pin of today
 * is honoured without asking it.
 */
export function shownPhase(
  clock: Phase,
  pick: PhasePick | null,
  pin: PhasePin | null,
  today: string,
  clockKnown = true,
): { phase: Phase; how: 'clock' | 'picked' | 'pinned' } {
  if (pin && (clockKnown ? pinHolds(pin, clock, today) : pin.date === today)) return { phase: pin.phase, how: 'pinned' }
  if (pick && pick.from === clock) return { phase: pick.phase, how: pick.phase === clock ? 'clock' : 'picked' }
  return { phase: clock, how: 'clock' }
}

/** Where the pin is kept: in this browser only, per viewer. */
export const PIN_KEY = 'openfno.desk.pin'

const isPhase = (v: unknown): v is Phase => v === 'pre' || v === 'live' || v === 'post'

/**
 * The stored pin, or null. Storage can be missing or throw (a private window,
 * blocked site data), and what is stored can be anything an older build or a
 * person wrote; none of that may break the Desk, so it all reads as "no pin".
 * A pin from before 28 Sep has no `from`: it was set to hold the whole day,
 * which pins no longer do, so it reads as none rather than being guessed at.
 */
export function readPin(storage: Pick<Storage, 'getItem'> | null): PhasePin | null {
  try {
    const raw = storage?.getItem(PIN_KEY)
    if (!raw) return null
    const v = JSON.parse(raw) as Partial<PhasePin>
    return isPhase(v.phase) && isPhase(v.from) && typeof v.date === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(v.date)
      ? { phase: v.phase, date: v.date, from: v.from }
      : null
  } catch {
    return null
  }
}

/** Stores or clears the pin; a storage that refuses is ignored (the pin then lasts until reload). */
export function writePin(storage: Pick<Storage, 'setItem' | 'removeItem'> | null, pin: PhasePin | null): void {
  try {
    if (pin) storage?.setItem(PIN_KEY, JSON.stringify(pin))
    else storage?.removeItem(PIN_KEY)
  } catch {
    /* A refused write only means the pin is not remembered. */
  }
}

// ---------------------------------------------------------------- panels and their order

export type PanelKey =
  | 'grid'
  | 'plan'
  | 'pnl'
  | 'indices'
  | 'legs'
  | 'timeline'
  | 'news'
  | 'movers'
  | 'checkup'
  | 'overnight'
  | 'forecast'
  | 'scores'
  | 'flows'
  | 'week'

/**
 * What each panel needs, checked with the same `allows` as the workspaces.
 * The API enforces every one of these itself; this only keeps a panel that
 * would be refused off the sheet. The pulse and the chain view answer any
 * signed-in user, so the index table needs no grant; its forecast column
 * checks `analysis` on its own. The market-intelligence reads (news and
 * filings, board meetings, the overnight snapshots, breadth) answer the
 * market-data grant, like the rest of the market.
 */
export const PANEL_REQUIRES: Readonly<Record<PanelKey, Requirement | undefined>> = {
  grid: 'strategies',
  plan: 'strategies',
  pnl: 'strategies',
  legs: 'strategies',
  indices: undefined,
  movers: 'market-data',
  flows: 'market-data',
  week: 'market-data',
  news: 'market-data',
  overnight: 'market-data',
  forecast: 'analysis',
  scores: 'analysis',
  // Sentinel and the desk timeline (incidents, checkups, deploys) are the operator's.
  checkup: 'admin',
  timeline: 'admin',
}

/** One cell of the sheet: a panel, how many of the 12 columns it spans, and any panel stacked under it. */
export interface Slot {
  key: PanelKey
  span: number
  under?: PanelKey
}

type Row = ReadonlyArray<readonly [PanelKey, number, PanelKey?]>

/**
 * The sheet per phase, in the order of that part of the day's jobs; a phone
 * stacks the same order. Before the open: is the desk ready, what will today
 * be, what is planned. In the session: how much and where, then the market
 * around it. After the close: the result, how the forecasts did, what is held
 * overnight. A trader's sheet has no Sentinel or timeline (they are the
 * operator's), so the rows they would fill hold what a trader can read
 * instead: before the open, the overnight markets and the news lead, where an
 * admin's readiness checklist would be.
 */
const LAYOUTS: Record<Phase, { admin: Row[]; trader: Row[] }> = {
  pre: {
    admin: [
      [['checkup', 5], ['overnight', 3], ['forecast', 4, 'flows']],
      [['news', 4], ['week', 4], ['plan', 4]],
      [['indices', 8], ['legs', 4, 'timeline']],
    ],
    trader: [
      [['overnight', 4], ['news', 4], ['plan', 4]],
      [['indices', 8], ['legs', 4]],
      [['week', 4, 'flows'], ['movers', 4], ['forecast', 4]],
    ],
  },
  live: {
    admin: [
      [['grid', 8], ['pnl', 4]],
      [['indices', 8], ['legs', 4]],
      [['timeline', 4], ['news', 4], ['movers', 4]],
    ],
    trader: [
      [['grid', 8], ['pnl', 4]],
      [['indices', 8], ['legs', 4]],
      [['news', 4], ['week', 4, 'flows'], ['movers', 4]],
    ],
  },
  post: {
    admin: [
      [['grid', 8], ['pnl', 4]],
      [['scores', 4], ['checkup', 4], ['legs', 4]],
      [['timeline', 4], ['week', 4], ['flows', 4]],
      [['indices', 8], ['movers', 4]],
    ],
    trader: [
      [['grid', 8], ['pnl', 4]],
      [['legs', 4], ['week', 4], ['flows', 4]],
      [['indices', 8], ['movers', 4]],
    ],
  },
}

/**
 * The sheet this viewer sees: the phase's rows with every panel they may not
 * use taken out, and each row's remaining panels widened to fill its 12
 * columns in proportion, so the sheet stays one closed rectangle. A panel
 * stacked under another drops out alone; the one above keeps its place.
 */
export function deskLayout(phase: Phase, access: Access): Slot[][] {
  const rows = LAYOUTS[phase][access.isAdmin ? 'admin' : 'trader']
  const can = (key: PanelKey) => allows(access, PANEL_REQUIRES[key])
  return rows.flatMap((row) => {
    const kept = row.filter(([key]) => can(key))
    if (kept.length === 0) return []
    const weight = kept.reduce((sum, [, span]) => sum + span, 0)
    let used = 0
    return [
      kept.map(([key, span, under], i) => {
        const width = i === kept.length - 1 ? 12 - used : Math.round((span / weight) * 12)
        used += width
        return under && can(under) ? { key, span: width, under } : { key, span: width }
      }),
    ]
  })
}

// ---------------------------------------------------------------- accounts

/** A trading account on the Desk, and the series colour it wears (1, 2, or none past the second). */
export interface DeskAccount {
  id: number
  name: string
  tone: 1 | 2 | null
}

/**
 * The accounts that have runs, oldest user first: the admin, then the
 * traders in the order they joined. The first two get the account colours;
 * any more are named, and neutral, rather than sharing a colour.
 */
export function deskAccounts(runs: readonly Pick<LiveRunSummary, 'userId' | 'userName'>[]): DeskAccount[] {
  const byId = new Map<number, string>()
  for (const r of runs) if (!byId.has(r.userId)) byId.set(r.userId, runUserLabel(r.userName, r.userId))
  return [...byId.entries()]
    .sort(([a], [b]) => a - b)
    .map(([id, name], i) => ({ id, name, tone: i === 0 ? 1 : i === 1 ? 2 : null }))
}

/** An account's series colour: its own, or a neutral line past the second account. */
export function accountStroke(tone: DeskAccount['tone']): string {
  return tone === 1 ? 'var(--acct-1)' : tone === 2 ? 'var(--acct-2)' : 'var(--text-2)'
}

/** All accounts, or one by user id. */
export type Scope = 'all' | number

/** The runs a scope covers. */
export function scopeRuns<T extends Pick<LiveRunSummary, 'userId'>>(runs: readonly T[], scope: Scope): T[] {
  return scope === 'all' ? [...runs] : runs.filter((r) => r.userId === scope)
}

/** A scope that names an account no longer on the Desk falls back to all of them. */
export function validScope(scope: Scope, accounts: readonly DeskAccount[]): Scope {
  return scope === 'all' || accounts.some((a) => a.id === scope) ? scope : 'all'
}

// ---------------------------------------------------------------- a run's figures

/** Money a run (or a set of runs) made today, net of the charges of its fills. */
export interface Figures {
  net: number
  /** Before charges: realized plus the open book. */
  gross: number
  charges: number
  /** The open book, at the last mark (0 once a run has stopped). */
  open: number
  trades: number
}

export const NO_FIGURES: Figures = { net: 0, gross: 0, charges: 0, open: 0, trades: 0 }

/**
 * One run's figures from its history row, computed as its Live runner card
 * computes them from the live view: realized after charges (`realizedNet`)
 * plus the open book, falling back to the gross where an older API sends no
 * charges (`liveNet`).
 */
export function runFigures(
  run: Pick<LiveRunSummary, 'realizedPnl' | 'grossPnl' | 'charges' | 'unrealizedPnl' | 'isActive' | 'trades'>,
): Figures {
  const realized = run.grossPnl ?? run.realizedPnl
  const open = run.isActive ? (run.unrealizedPnl ?? 0) : 0
  const gross = realized + open
  const charges = run.charges ?? null
  const net = liveNet({ total: gross, net: charges == null ? null : realizedNet({ realized, charges }) + open })
  return { net, gross, charges: charges ?? 0, open, trades: run.trades }
}

export function addFigures(a: Figures, b: Figures): Figures {
  return { net: a.net + b.net, gross: a.gross + b.gross, charges: a.charges + b.charges, open: a.open + b.open, trades: a.trades + b.trades }
}

export function sumFigures(runs: readonly LiveRunSummary[]): Figures {
  return runs.reduce((acc, r) => addFigures(acc, runFigures(r)), NO_FIGURES)
}

/** An alert-only run places no orders and has no P&L; the Desk counts trading runs. */
export function isTradingRun(run: Pick<LiveRunSummary, 'role'>): boolean {
  return run.role !== 'alerts'
}

// ---------------------------------------------------------------- how a run ended

export type StopKind = 'rule' | 'target' | 'close' | 'fault' | 'person'

/**
 * Why a stopped run stopped, from who stopped it (the API's `stoppedBy`):
 * a risk rule (the guard's stop-loss, trailing stop or target), the market
 * close or expiry, a fault (the runner exited, the API restarted), or a
 * person. Null for a run still going.
 */
export function stopKind(run: Pick<LiveRunSummary, 'isActive' | 'stoppedBy' | 'stopReason'>): StopKind | null {
  if (run.isActive) return null
  const by = (run.stoppedBy ?? '').toLowerCase()
  const reason = run.stopReason ?? ''
  if (by === 'risk-guard') return /^target hit/i.test(reason) ? 'target' : 'rule'
  if (by === 'market-hours' || by === 'expiry-settlement' || /^(market|mcx) closed/i.test(reason)) return 'close'
  if (by === 'runner' || by === 'api' || /^runner exited|^api restarted/i.test(reason)) return 'fault'
  return 'person'
}

// ---------------------------------------------------------------- the grid

/** Names a person reads: "SmcStructureBreak" → "SMC Structure Break". */
export function strategyLabel(name: string): string {
  const ACRONYMS: Record<string, string> = { Smc: 'SMC', Atm: 'ATM', Oi: 'OI', Vwap: 'VWAP', Ema: 'EMA', Iv: 'IV', Pcr: 'PCR' }
  return name
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2')
    .split(' ')
    .map((w) => ACRONYMS[w] ?? w)
    .join(' ')
}

/** The index and commodity order a desk reads in; anything else follows alphabetically. */
const UNDERLYING_ORDER = ['NIFTY', 'BANKNIFTY', 'SENSEX', 'FINNIFTY', 'MIDCPNIFTY', 'BANKEX', 'CRUDEOIL', 'CRUDEOILM', 'NATURALGAS', 'GOLD', 'SILVER']

export function underlyingRank(u: string): number {
  const i = UNDERLYING_ORDER.indexOf(u.toUpperCase())
  return i === -1 ? UNDERLYING_ORDER.length : i
}

export function byUnderlying(a: string, b: string): number {
  return underlyingRank(a) - underlyingRank(b) || a.localeCompare(b)
}

/** The narrow column head on a phone. */
export function underlyingShort(u: string): string {
  return ({ BANKNIFTY: 'BANK', CRUDEOIL: 'CRUDE', MIDCPNIFTY: 'MIDCP', FINNIFTY: 'FIN', NATURALGAS: 'NATGAS' } as Record<string, string>)[u] ?? u
}

export type MarkKind = 'rule' | 'target' | 'restarted' | 'stopped' | 'carried'

/** A small mark in a grid cell: what happened to the run beyond its number. */
export interface CellMark {
  kind: MarkKind
  /** "SL", "TGT", "↻", "off", "C": what a phone shows. */
  label: string
  /** When, where it matters ("11:20"); a wide screen shows it beside the label. */
  at: string | null
  title: string
}

export interface GridCell {
  /** The account's runs of this strategy on this underlying today, oldest first (a restart is a second run). */
  runs: LiveRunSummary[]
  figures: Figures
  /** A run in the cell is still going. */
  live: boolean
  /** Deployed and running, but no trade and no open leg yet: the morning plan's tick. */
  waiting: boolean
  marks: CellMark[]
}

export interface GridRow {
  strategy: string
  label: string
  runs: number
  /** The lots every run of the row trades, or null when they differ. */
  lots: number | null
  /** Accounts that ran it, by id. */
  accountIds: number[]
  /** Underlying → one cell per account, in account order; null where that account has no run. */
  cells: Record<string, Array<GridCell | null>>
  figures: Figures
}

export interface AccountTotals {
  account: DeskAccount
  runs: number
  byUnderlying: Record<string, Figures | null>
  figures: Figures
}

export interface DeskGrid {
  underlyings: string[]
  accounts: DeskAccount[]
  rows: GridRow[]
  totals: AccountTotals[]
  figures: Figures
}

function cellMarks(runs: LiveRunSummary[], carried: ReadonlySet<number>): CellMark[] {
  const marks: CellMark[] = []
  const last = runs[runs.length - 1]
  for (const run of runs) {
    const kind = stopKind(run)
    const at = istHm(run.stoppedUtc)
    const reason = run.stopReason ?? ''
    if (kind === 'rule') marks.push({ kind: 'rule', label: 'SL', at, title: reason || `Stopped by a risk rule at ${at}` })
    else if (kind === 'target') marks.push({ kind: 'target', label: 'TGT', at, title: reason || `Target reached at ${at}` })
    else if ((kind === 'fault' || kind === 'person') && run === last)
      marks.push({ kind: 'stopped', label: 'off', at, title: `${shortStopReason(reason) ?? 'Stopped'} at ${at}, and not restarted` })
    if (carried.has(run.runId)) marks.push({ kind: 'carried', label: 'C', at: null, title: 'A leg of this run was carried to the manual book at the close' })
  }
  if (runs.length > 1) {
    const restart = runs[runs.length - 1]
    const before = runs[runs.length - 2]
    marks.unshift({
      kind: 'restarted',
      label: '↻',
      at: null,
      title: `Restarted ${istHm(restart.startedUtc)} as run #${restart.runId}; #${before.runId} ${
        shortStopReason(before.stopReason)?.toLowerCase() ?? 'stopped'
      } at ${istHm(before.stoppedUtc)}`,
    })
  }
  return marks
}

/**
 * The strategy × underlying × account grid from today's runs. Rows follow the
 * order the strategies were first started (the morning plan's order); columns
 * follow the index order. A cell sums every run of one account on one
 * underlying, so a restart adds to the cell rather than splitting it, and is
 * marked. `carried` holds the runs a leg was carried from, read off the
 * manual book.
 */
export function buildGrid(
  runs: readonly LiveRunSummary[],
  accounts: readonly DeskAccount[],
  carried: ReadonlySet<number> = new Set(),
): DeskGrid {
  const trading = runs.filter(isTradingRun).filter((r) => accounts.some((a) => a.id === r.userId))
  const started = (r: LiveRunSummary) => msOf(r.startedUtc) ?? Number.MAX_SAFE_INTEGER
  const sorted = [...trading].sort((a, b) => started(a) - started(b) || a.runId - b.runId)
  const underlyings = [...new Set(sorted.map((r) => r.underlying.toUpperCase()))].sort(byUnderlying)

  const strategies = [...new Set(sorted.map((r) => r.strategyName))]
  const rows: GridRow[] = strategies.map((strategy) => {
    const own = sorted.filter((r) => r.strategyName === strategy)
    const cells: Record<string, Array<GridCell | null>> = {}
    for (const u of underlyings) {
      cells[u] = accounts.map((a) => {
        const inCell = own.filter((r) => r.userId === a.id && r.underlying.toUpperCase() === u)
        if (inCell.length === 0) return null
        const figures = sumFigures(inCell)
        const live = inCell.some((r) => r.isActive)
        return {
          runs: inCell,
          figures,
          live,
          waiting: live && figures.trades === 0 && inCell.every((r) => r.openPositions === 0),
          marks: cellMarks(inCell, carried),
        }
      })
    }
    const lots = [...new Set(own.map((r) => r.lots))]
    return {
      strategy,
      label: strategyLabel(strategy),
      runs: own.length,
      lots: lots.length === 1 ? lots[0] : null,
      accountIds: [...new Set(own.map((r) => r.userId))],
      cells,
      figures: sumFigures(own),
    }
  })

  const totals = accounts.map((account) => {
    const own = sorted.filter((r) => r.userId === account.id)
    const byUnd: Record<string, Figures | null> = {}
    for (const u of underlyings) {
      const inU = own.filter((r) => r.underlying.toUpperCase() === u)
      byUnd[u] = inU.length ? sumFigures(inU) : null
    }
    return { account, runs: own.length, byUnderlying: byUnd, figures: sumFigures(own) }
  })

  return { underlyings, accounts: [...accounts], rows, totals, figures: sumFigures(sorted) }
}

/**
 * Each account's net now, for the accounts with a run still live: where the
 * Day P&L curve's line is carried on to (pnlSeries.withLiveTips). An account
 * whose runs have all stopped has a final figure the recorder already holds.
 */
export function liveAccountNets(grid: Pick<DeskGrid, 'totals'>, runs: ReadonlyArray<Pick<LiveRunSummary, 'userId' | 'isActive'>>): Map<number, number> {
  const live = new Set(runs.filter((r) => r.isActive).map((r) => r.userId))
  return new Map(grid.totals.filter((t) => live.has(t.account.id)).map((t) => [t.account.id, t.figures.net]))
}

// ---------------------------------------------------------------- run counts and the plan

export interface RunCounts {
  total: number
  live: number
  /** Stopped by a stop-loss or trailing stop. */
  rule: number
  target: number
  /** Closed by the market close or expiry. */
  closed: number
  /** Stopped by a fault (runner exit, API restart) and not restarted. */
  fault: number
  person: number
}

/** Runs by what they are doing now. A cell restarted after a fault counts once, as its restart. */
export function runCounts(runs: readonly LiveRunSummary[]): RunCounts {
  const counts: RunCounts = { total: 0, live: 0, rule: 0, target: 0, closed: 0, fault: 0, person: 0 }
  const latest = new Map<string, LiveRunSummary>()
  for (const r of runs.filter(isTradingRun)) {
    const key = `${r.userId}|${r.strategyName}|${r.underlying.toUpperCase()}`
    const seen = latest.get(key)
    if (!seen || (msOf(r.startedUtc) ?? 0) >= (msOf(seen.startedUtc) ?? 0)) latest.set(key, r)
  }
  for (const r of latest.values()) {
    counts.total++
    const kind = stopKind(r)
    if (kind == null) counts.live++
    else if (kind === 'close') counts.closed++
    else counts[kind]++
  }
  return counts
}

/** One account's part of the morning plan: runs asked for, and how many are live. */
export interface PlanAccount {
  name: string
  /** Null when no active account has the name the plan spells. */
  userId: number | null
  planned: number
  live: number
}

/** One account's run of one plan line on one underlying. */
export interface PlanCell {
  account: string
  userId: number | null
  live: boolean
  runId: number | null
}

/** One strategy line of the plan, as a row: an underlying per column, an account per line in each cell. */
export interface PlanRow {
  /** The line number in the file, or null for runs no line could be matched to. */
  line: number | null
  strategy: string
  label: string
  lots: number | null
  /** "target 20 pts", "no target", "default target". */
  target: string | null
  onlyAccounts: string[]
  /** Underlying → one cell per account in the plan's order; null where that account is not asked to run it. */
  cells: Record<string, Array<PlanCell | null>>
}

export interface PlanView {
  underlyings: string[]
  accounts: PlanAccount[]
  rows: PlanRow[]
  planned: number
  live: number
  warnings: string[]
}

/** The line of the plan a run it asks for came from: same strategy and lots, the underlying on the line, the account allowed. */
function lineOf(plan: Pick<DeskPlanResponse, 'lines'>, run: DeskPlanRun): DeskPlanLine | null {
  const same = (a: string, b: string) => a.toUpperCase() === b.toUpperCase()
  return (
    plan.lines.find(
      (l) =>
        same(l.strategy, run.strategy) &&
        l.lots === run.lots &&
        l.underlyings.some((u) => same(u, run.underlying)) &&
        (l.onlyAccounts.length === 0 || l.onlyAccounts.some((a) => same(a, run.account))),
    ) ?? null
  )
}

function targetText(line: DeskPlanLine): string {
  if (line.legTarget === 'none') return 'no leg target'
  if (line.legTarget === 'points' && line.legTargetPoints != null) return `leg target ${line.legTargetPoints} pts`
  return 'default leg target'
}

/**
 * The morning plan (GET /api/Desk/plan) as the Desk lays it out: a row per
 * plan line, an underlying per column, an account per line in each cell,
 * live or not, and the counts per account. `accounts` narrows it to the
 * Desk's scope by user id (null: every account in the plan). The counts are
 * the API's own test of "live": Running with its runner alive.
 */
export function planView(plan: DeskPlanResponse, accounts: readonly number[] | null = null): PlanView {
  const inScope = (r: Pick<DeskPlanRun, 'userId'>) => accounts == null || (r.userId != null && accounts.includes(r.userId))
  const runs = plan.runs.filter(inScope)
  const names = plan.accounts.filter((name) => runs.some((r) => r.account === name))
  // An account the file names but no run of which is in scope (another account's scope) stays out.
  const accountRows: PlanAccount[] = names.map((name) => {
    const own = runs.filter((r) => r.account === name)
    return { name, userId: own[0]?.userId ?? null, planned: own.length, live: own.filter((r) => r.isLive).length }
  })
  const underlyings = [...new Set(runs.map((r) => r.underlying.toUpperCase()))].sort(byUnderlying)

  const rows = new Map<string, PlanRow>()
  for (const run of runs) {
    const line = lineOf(plan, run)
    const key = line ? `L${line.number}` : `S${run.strategy}`
    let row = rows.get(key)
    if (!row) {
      row = {
        line: line?.number ?? null,
        strategy: run.strategy,
        label: strategyLabel(run.strategy),
        lots: line?.lots ?? run.lots,
        target: line ? targetText(line) : null,
        onlyAccounts: line?.onlyAccounts ?? [],
        cells: Object.fromEntries(underlyings.map((u) => [u, names.map(() => null)])),
      }
      rows.set(key, row)
    }
    row.cells[run.underlying.toUpperCase()][names.indexOf(run.account)] = {
      account: run.account,
      userId: run.userId,
      live: run.isLive,
      runId: run.runId,
    }
  }
  const ordered = [...rows.values()].sort((a, b) => (a.line ?? Infinity) - (b.line ?? Infinity))
  return {
    underlyings,
    accounts: accountRows,
    rows: ordered,
    planned: runs.length,
    live: runs.filter((r) => r.isLive).length,
    warnings: plan.warnings,
  }
}

// ---------------------------------------------------------------- indices and levels

/** One row of the index table. */
export interface IndexRow {
  key: string
  name: string
  contract: string | null
  ltp: number | null
  prevClose: number | null
  change: number | null
  changePct: number | null
  high: number | null
  low: number | null
  updatedUtc: string | null
  /** The symbol whose one-minute bars trace the row. */
  symbol: string
  /** Has an option chain on the Desk (NIFTY, BANKNIFTY, SENSEX). */
  chained: boolean
  digits: number
}

const PULSE_INDEX_KEYS: Record<string, string> = {
  'NSE:NIFTY50-INDEX': 'NIFTY',
  'NSE:NIFTYBANK-INDEX': 'BANKNIFTY',
  'BSE:SENSEX-INDEX': 'SENSEX',
}

export const VIX_SYMBOL = 'NSE:INDIAVIX-INDEX'

/** "MCX:CRUDEOIL26OCTFUT" → "CRUDEOIL". */
export function commodityRoot(symbol: string): string | null {
  return /^MCX:([A-Z]+?)\d{2}[A-Z]{3}FUT$/.exec(symbol)?.[1] ?? null
}

/**
 * The six rows the Desk reads: the three indices and the two commodities from
 * the market pulse, and India VIX from the NIFTY chain's header (the pulse
 * does not carry it). A row missing from its source is left out, not zeroed.
 */
export function indexRows(pulse: MarketPulseResponse | undefined, vix: OptionChainQuote | null | undefined): IndexRow[] {
  const items = (pulse?.groups ?? []).flatMap((g) => g.items.map((item) => ({ group: g.key, item })))
  const rows: IndexRow[] = []
  for (const { group, item } of items) {
    if (group !== 'index') continue
    const key = PULSE_INDEX_KEYS[item.symbol]
    if (!key) continue
    rows.push({
      key,
      name: item.name,
      contract: null,
      ltp: item.lastTradedPrice,
      prevClose: item.previousClose,
      change: item.change,
      changePct: item.changePercent,
      high: item.high,
      low: item.low,
      updatedUtc: item.updatedUtc,
      symbol: item.symbol,
      chained: true,
      digits: 2,
    })
  }
  rows.sort((a, b) => byUnderlying(a.key, b.key))
  if (vix && vix.lastPrice != null) {
    rows.push({
      key: 'VIX',
      name: 'INDIA VIX',
      contract: null,
      ltp: vix.lastPrice,
      prevClose: vix.previousClose,
      change: vix.change,
      changePct: vix.changePercent,
      high: null,
      low: null,
      updatedUtc: vix.asOfUtc,
      symbol: VIX_SYMBOL,
      chained: false,
      digits: 2,
    })
  }
  for (const root of ['CRUDEOIL', 'GOLD']) {
    const hit = items.find(({ group, item }) => group === 'commodity' && commodityRoot(item.symbol) === root)
    if (!hit) continue
    const { item } = hit
    rows.push({
      key: root,
      name: root,
      contract: item.contract ? item.contract.split(' ')[0] : null,
      ltp: item.lastTradedPrice,
      prevClose: item.previousClose,
      change: item.change,
      changePct: item.changePercent,
      high: item.high,
      low: item.low,
      updatedUtc: item.updatedUtc,
      symbol: item.symbol,
      chained: false,
      digits: 0,
    })
  }
  return rows
}

/** The walls, PCR, max pain, IV and the straddle's move, from a chain view's header (as Market factors reads them). */
export interface ChainLevels {
  putWall: number | null
  callWall: number | null
  pcr: number | null
  maxPain: number | null
  iv: number | null
  /** The move the ATM straddle prices to expiry, % of spot. */
  straddlePct: number | null
  expiry: string | null
}

export function chainLevels(chain: OptionChain | undefined): ChainLevels | null {
  const h = chain?.header
  if (!chain || !h) return null
  return {
    putWall: h.supportStrike,
    callWall: h.resistanceStrike,
    pcr: h.putCallRatio,
    maxPain: h.maxPainStrike,
    iv: h.atTheMoneyIv,
    straddlePct: expectedMove(chain)?.percent ?? null,
    expiry: chain.expiryDate ?? null,
  }
}

/** Today's range so far in points (high − low), or null without both. */
export function rangeSoFar(row: Pick<IndexRow, 'high' | 'low'>): number | null {
  return row.high != null && row.low != null && row.high >= row.low ? row.high - row.low : null
}

/** One-minute closes of one IST day, oldest first: the row's small trace. */
export function dayTrace(bars: readonly LiveBar[] | undefined, day: string): number[] {
  return (bars ?? [])
    .filter((b) => istDay(Date.parse(b.barStartUtc)) === day)
    .sort((a, b) => Date.parse(a.barStartUtc) - Date.parse(b.barStartUtc))
    .map((b) => b.close)
}

// ---------------------------------------------------------------- forecasts

/** The models the Desk leads with; the Analysis page ranks the rest. */
export const DESK_MODELS = { range: 'range.har-vix', trend: 'trend.logit', direction: 'direction.logit' } as const

export interface ForecastRow {
  underlying: string
  range: { median: number; low80: number; high80: number; baseMedian: number | null } | null
  trend: { p: number; base: number | null } | null
  up: { p: number; base: number | null } | null
  issuedUtc: string | null
  /** Once scored: the session's range in points, whether it fell inside the 80% band, and the close. */
  scored: { range: number; inside: boolean | null; up: boolean; scoredUtc: string | null } | null
}

function pick(list: readonly Forecast[], target: Forecast['target'], key: string): Forecast | undefined {
  return list.find((f) => f.target === target && f.modelKey === key) ?? list.find((f) => f.target === target)
}

/** One row per index for a session: range (points), trend-day and up-close probabilities, each against its baseline. */
export function forecastRows(list: readonly Forecast[] | undefined, session: string): ForecastRow[] {
  const today = (list ?? []).filter((f) => f.sessionDate === session)
  const unds = [...new Set(today.map((f) => f.underlying))].sort(byUnderlying)
  return unds.map((underlying) => {
    const own = today.filter((f) => f.underlying === underlying)
    const range = pick(own, 'range', DESK_MODELS.range)
    const trend = pick(own, 'trend', DESK_MODELS.trend)
    const direction = pick(own, 'direction', DESK_MODELS.direction)
    const r = range ? asRange(range.prediction) : null
    const rb = range ? asRange(range.baseline) : null
    const tp = trend ? asProb(trend.prediction) : null
    const dp = direction ? asProb(direction.prediction) : null
    const o = range?.outcome ?? direction?.outcome ?? null
    return {
      underlying,
      range: r ? { median: r.points.median, low80: r.points.low80, high80: r.points.high80, baseMedian: rb?.points.median ?? null } : null,
      trend: tp != null ? { p: tp, base: trend ? asProb(trend.baseline) : null } : null,
      up: dp != null ? { p: dp, base: direction ? asProb(direction.baseline) : null } : null,
      issuedUtc: range?.issuedUtc ?? trend?.issuedUtc ?? direction?.issuedUtc ?? null,
      scored: o
        ? {
            range: o.high - o.low,
            inside: range?.scores?.metrics?.covered80 ?? null,
            up: o.up,
            scoredUtc: range?.scoredUtc ?? direction?.scoredUtc ?? null,
          }
        : null,
    }
  })
}

/**
 * How far the Desk's range model is from proof, from the scoreboard's row
 * for all indices: its status and how many live sessions are scored. Null
 * until the scoreboard answers, and the Desk then says "not proven".
 */
export function modelStanding(
  rows: ReadonlyArray<Pick<ScoreboardRow, 'modelKey' | 'underlying' | 'status' | 'liveCount'>> | undefined,
  modelKey: string = DESK_MODELS.range,
): { status: string; liveCount: number } | null {
  const own = (rows ?? []).filter((r) => r.modelKey === modelKey)
  const row = own.find((r) => r.underlying === 'ALL') ?? own[0]
  return row ? { status: row.status, liveCount: row.liveCount } : null
}

/** Issued and scored counts of one session, for the strip. */
export function forecastCounts(list: readonly Forecast[] | undefined, session: string) {
  const today = (list ?? []).filter((f) => f.sessionDate === session)
  const ranges = today.filter((f) => f.target === 'range' && f.modelKey === DESK_MODELS.range)
  return {
    issued: today.length,
    scored: today.filter((f) => f.outcome != null).length,
    issuedUtc: today.map((f) => f.issuedUtc).sort()[0] ?? null,
    rangesScored: ranges.filter((f) => f.scores?.metrics?.covered80 != null).length,
    rangesInside: ranges.filter((f) => f.scores?.metrics?.covered80 === true).length,
  }
}

// ---------------------------------------------------------------- open legs

export interface DeskLeg {
  key: string
  userId: number
  userName: string
  strategy: string
  manual: boolean
  underlying: string
  /** "NIFTY 23300 CE", "CRUDEOIL FUT". */
  label: string
  expiryDate: string | null
  lots: number
  short: boolean
  ltp: number | null
  /** At the live mark, before exit charges; null while no mark exists. */
  pnl: number | null
  openedUtc: string
  /** The weekday it was opened on, when that was before today: a carried leg ("Fri"). */
  carriedFrom: string | null
  /** Ticked to be held overnight rather than squared off at the close. */
  carryForward: boolean
  /** Expires today or on the next session. */
  expires: 'today' | 'next' | null
}

/** "NIFTY 23300 CE" for an option, "CRUDEOIL FUT" for a future, the symbol for anything else. */
export function legLabel(p: Pick<OpenPosition, 'underlying' | 'strike' | 'optionType' | 'symbol'>): string {
  if ((p.optionType === 'CE' || p.optionType === 'PE') && p.strike != null) return `${p.underlying} ${p.strike} ${p.optionType}`
  if (/FUT$/i.test(p.symbol)) return `${p.underlying} FUT`
  return p.symbol.includes(':') ? p.symbol.slice(p.symbol.indexOf(':') + 1) : p.symbol
}

/**
 * Every open leg from GET /api/Positions/open, largest open P&L first. The
 * API scopes them (a trader sees their own), so the list is only narrowed
 * further by the Desk's account scope, by user id.
 */
export function deskLegs(
  positions: readonly OpenPosition[],
  opts: { today: string; nextSession: string | null; userId?: number | null },
): DeskLeg[] {
  const legs: DeskLeg[] = []
  for (const p of positions) {
    if (opts.userId != null && p.userId !== opts.userId) continue
    const openedDay = istDay(Date.parse(p.openedUtc))
    legs.push({
      key: String(p.positionId),
      userId: p.userId,
      userName: runUserLabel(p.userName, p.userId),
      strategy: p.strategyName,
      manual: p.isManualBook,
      underlying: p.underlying.toUpperCase(),
      label: legLabel(p),
      expiryDate: p.expiryDate,
      lots: p.lots,
      short: p.direction === 'SHORT',
      ltp: p.markPrice,
      pnl: p.unrealizedPnl,
      openedUtc: p.openedUtc,
      carriedFrom: openedDay < opts.today ? weekdayOf(openedDay) : null,
      carryForward: p.carryForward,
      expires: p.expiryDate === opts.today ? 'today' : p.expiryDate != null && p.expiryDate === opts.nextSession ? 'next' : null,
    })
  }
  return legs.sort((a, b) => Math.abs(b.pnl ?? 0) - Math.abs(a.pnl ?? 0) || a.label.localeCompare(b.label))
}

/**
 * Runs that carried a leg into a manual book, from the open legs of every
 * book in scope: a carried leg names the run it came from. A carried leg
 * closed since is no longer open, so its run loses the mark.
 */
export function carriedRunIds(positions: ReadonlyArray<{ carriedFromRunId?: number | null }> | undefined): Set<number> {
  return new Set((positions ?? []).map((p) => p.carriedFromRunId).filter((id): id is number => id != null))
}

// ---------------------------------------------------------------- the day's timeline

export type EventTone = 'pos' | 'neg' | 'warn' | 'info'

export interface DeskEvent {
  key: string
  atMs: number
  time: string
  tone: EventTone
  text: string
}

const toneOfSeverity = (severity: string): EventTone => {
  const t = severityTone(severity)
  return t === 'neutral' ? 'info' : t
}

/** The incidents still live (open or acknowledged), newest first: they lead the timeline. */
export function liveIncidents(incidents: readonly Incident[] | undefined): Incident[] {
  return (incidents ?? [])
    .filter((i) => i.status !== 'resolved')
    .sort((a, b) => Date.parse(b.firstSeenUtc) - Date.parse(a.firstSeenUtc))
}

function runName(r: LiveRunSummary): string {
  return `${runUserLabel(r.userName, r.userId)} ${strategyLabel(r.strategyName)} ${r.underlying}`
}

/**
 * Today on the desk, newest first: incidents, runs started and stopped,
 * Sentinel's checkups, the forecasts issued and scored, and deploys. Runs
 * started together (the morning plan) are one line, as are runs the close
 * stopped together; every other start and stop is its own line, because each
 * is a thing someone should know about.
 */
export function deskTimeline(input: {
  today: string
  runs?: readonly LiveRunSummary[]
  incidents?: readonly Incident[]
  checkups?: readonly CheckupSummary[]
  forecasts?: readonly Forecast[]
  deploys?: readonly DeployRecord[]
}): DeskEvent[] {
  const { today } = input
  const isToday = (ms: number | null): ms is number => ms != null && istDay(ms) === today
  const events: DeskEvent[] = []
  const push = (key: string, ms: number, tone: EventTone, text: string) => events.push({ key, atMs: ms, time: istHm(new Date(ms).toISOString()), tone, text })

  // Runs: the first start and the ones within two minutes of it are the plan.
  const runs = (input.runs ?? []).filter(isTradingRun)
  const starts = runs.map((r) => ({ r, ms: msOf(r.startedUtc) })).filter((x): x is { r: LiveRunSummary; ms: number } => isToday(x.ms))
  starts.sort((a, b) => a.ms - b.ms)
  if (starts.length) {
    const first = starts[0].ms
    const batch = starts.filter((s) => s.ms - first <= 2 * 60_000)
    push('runs-start', first, 'info', batch.length === 1 ? `Started ${runName(batch[0].r)}` : `${batch.length} runs started`)
    for (const s of starts.slice(batch.length)) {
      const restart = runs.some(
        (o) => o !== s.r && o.userId === s.r.userId && o.strategyName === s.r.strategyName && o.underlying === s.r.underlying && (msOf(o.stoppedUtc) ?? Infinity) <= s.ms,
      )
      push(`run-start-${s.r.runId}`, s.ms, 'info', `${restart ? 'Restarted' : 'Started'} ${runName(s.r)}`)
    }
  }
  const closes = new Map<string, number[]>()
  for (const r of runs) {
    const ms = msOf(r.stoppedUtc)
    if (!isToday(ms)) continue
    const kind = stopKind(r)
    if (kind === 'close') {
      const minute = istHm(r.stoppedUtc)
      closes.set(minute, [...(closes.get(minute) ?? []), ms])
      continue
    }
    const net = formatInrSigned(runFigures(r).net)
    if (kind === 'rule') push(`run-stop-${r.runId}`, ms, 'neg', `${shortStopReason(r.stopReason) ?? 'Stop loss'} · ${runName(r)} · net ${net}`)
    else if (kind === 'target') push(`run-stop-${r.runId}`, ms, 'pos', `Target hit · ${runName(r)} · net ${net}`)
    else if (kind === 'fault') push(`run-stop-${r.runId}`, ms, 'neg', `${shortStopReason(r.stopReason) ?? 'Stopped'} · ${runName(r)}`)
    else if (kind === 'person') push(`run-stop-${r.runId}`, ms, 'info', `${shortStopReason(r.stopReason) ?? 'Stopped'} · ${runName(r)}`)
  }
  for (const [minute, stamps] of closes) {
    push(`close-${minute}`, Math.min(...stamps), 'info', `Market close · ${stamps.length} run${stamps.length === 1 ? '' : 's'} stopped`)
  }

  for (const i of input.incidents ?? []) {
    const seen = msOf(i.firstSeenUtc)
    const resolved = msOf(i.resolvedUtc)
    const label = SEVERITY_LABEL[i.severity] ?? i.severity
    if (isToday(seen)) {
      const end = i.status === 'resolved' && resolved != null ? ` · resolved ${istHm(i.resolvedUtc)}` : i.status === 'acknowledged' ? ' · acknowledged' : ''
      push(`inc-${i.id}`, seen, toneOfSeverity(i.severity), `${label} · ${i.title}${end}`)
    } else if (isToday(resolved)) {
      push(`inc-res-${i.id}`, resolved, 'pos', `Resolved · ${i.title}`)
    }
  }

  for (const c of input.checkups ?? []) {
    const ms = msOf(c.completedUtc)
    if (!isToday(ms) || c.status !== 'done') continue
    const verdict = c.verdict === 'ok' ? 'all clear' : c.verdict === 'attention' ? 'attention' : c.verdict === 'action' ? 'action needed' : c.verdict
    const tone: EventTone = c.verdict === 'ok' ? 'pos' : c.verdict === 'action' ? 'neg' : 'warn'
    const slot = (SLOT_LABEL as Record<string, string>)[c.slot]?.toLowerCase() ?? c.slot
    push(`chk-${c.id}`, ms, tone, `Checkup ${slot}: ${verdict}${c.verdict !== 'ok' && c.headline ? ` · ${c.headline}` : ''}`)
  }

  const fc = (input.forecasts ?? []).filter((f) => f.sessionDate === today)
  const issued = fc.map((f) => msOf(f.issuedUtc)).filter(isToday)
  if (issued.length) push('fc-issued', Math.min(...issued), 'info', `Forecasts issued · ${issued.length}`)
  const scored = fc.map((f) => msOf(f.scoredUtc)).filter(isToday)
  if (scored.length) {
    const c = forecastCounts(fc, today)
    const band = c.rangesScored ? ` · ${c.rangesInside} of ${c.rangesScored} ranges inside the 80% band` : ''
    push('fc-scored', Math.max(...scored), c.rangesInside === c.rangesScored ? 'pos' : 'info', `Forecasts scored${band}`)
  }

  for (const d of input.deploys ?? []) {
    const ms = msOf(d.finishedUtc)
    const state = deployState(d.outcome)
    if (!isToday(ms) || state === 'skipped') continue
    if (state === 'live') push(`dep-${d.finishedUtc}`, ms, 'info', `Deployed ${d.toCommit.slice(0, 7)}${d.commits.length ? ` · ${d.commits.length} commit${d.commits.length === 1 ? '' : 's'}` : ''}`)
    else push(`dep-${d.finishedUtc}`, ms, 'neg', `Deploy failed · ${deploySummary(d.summary)}`)
  }

  return events.sort((a, b) => b.atMs - a.atMs || a.key.localeCompare(b.key))
}

// ---------------------------------------------------------------- news and filings

/**
 * NIFTY 50 as the news scorer knows it (analysis/context.py, NIFTY50). The two
 * lists change together, at NSE's semi-annual rebalance.
 */
export const NIFTY50: readonly string[] = [
  'ADANIENT', 'ADANIPORTS', 'APOLLOHOSP', 'ASIANPAINT', 'AXISBANK', 'BAJAJ-AUTO', 'BAJFINANCE', 'BAJAJFINSV',
  'BEL', 'BHARTIARTL', 'CIPLA', 'COALINDIA', 'DRREDDY', 'EICHERMOT', 'ETERNAL', 'GRASIM', 'HCLTECH', 'HDFCBANK',
  'HDFCLIFE', 'HINDALCO', 'HINDUNILVR', 'ICICIBANK', 'ITC', 'INFY', 'INDIGO', 'JSWSTEEL', 'JIOFIN', 'KOTAKBANK',
  'LT', 'M&M', 'MARUTI', 'MAXHEALTH', 'NTPC', 'NESTLEIND', 'ONGC', 'POWERGRID', 'RELIANCE', 'SBILIFE',
  'SHRIRAMFIN', 'SBIN', 'SUNPHARMA', 'TCS', 'TATACONSUM', 'TMPV', 'TATASTEEL', 'TECHM', 'TITAN', 'TRENT',
  'ULTRACEMCO', 'WIPRO',
]

/** "NSE:RELIANCE-EQ" → "RELIANCE"; an index or F&O symbol keeps its root ("NIFTY"). */
export function tickerOf(symbol: string): string {
  const body = symbol.includes(':') ? symbol.slice(symbol.indexOf(':') + 1) : symbol
  return body.replace(/-(EQ|BE|INDEX)$/, '').toUpperCase()
}

const listOf = (csv: string | null | undefined): string[] =>
  (csv ?? '')
    .split(',')
    .map((s) => s.trim().toUpperCase())
    .filter(Boolean)

export type NewsTab = 'all' | 'held' | 'filings' | 'results'

export const NEWS_TABS: ReadonlyArray<{ key: NewsTab; label: string }> = [
  { key: 'all', label: 'All' },
  { key: 'held', label: 'Held & watched' },
  { key: 'filings', label: 'Filings' },
  { key: 'results', label: 'Results' },
]

export interface NewsLine {
  key: string
  atMs: number
  kind: 'news' | 'filing'
  title: string
  source: string
  symbols: string[]
  topics: string[]
  /** −1 … +1, the local model's reading; null until scored. */
  sentiment: number | null
  link: string | null
}

/**
 * The news panel's list for a tab, newest first. Filings are only those of
 * names the desk cares about (held, watched, NIFTY 50): NSE broadcasts
 * thousands a day. "Held & watched" narrows both to held and watched names;
 * "Results" keeps what the scorer tagged as results.
 */
export function newsLines(
  news: readonly IntelHeadline[] | undefined,
  filings: readonly IntelAnnouncement[] | undefined,
  tab: NewsTab,
  names: { held: ReadonlySet<string>; watched: ReadonlySet<string> },
): NewsLine[] {
  const mine = (symbols: string[]) => symbols.some((s) => names.held.has(s) || names.watched.has(s))
  const desk = new Set([...names.held, ...names.watched, ...NIFTY50])
  const lines: NewsLine[] = []
  if (tab !== 'filings') {
    for (const n of news ?? []) {
      const symbols = listOf(n.symbols)
      const topics = listOf(n.topics).map((t) => t.toLowerCase())
      if (tab === 'held' && !mine(symbols)) continue
      if (tab === 'results' && !topics.includes('results')) continue
      lines.push({
        key: `n${n.id}`,
        atMs: Date.parse(n.firstSeenUtc),
        kind: 'news',
        title: n.title,
        source: n.source,
        symbols,
        topics,
        sentiment: n.sentiment,
        link: n.link || null,
      })
    }
  }
  for (const a of filings ?? []) {
    const symbols = [...new Set([a.symbol.toUpperCase(), ...listOf(a.symbols)])].filter(Boolean)
    const topics = listOf(a.topics).map((t) => t.toLowerCase())
    if (!symbols.some((s) => desk.has(s))) continue
    if (tab === 'held' && !mine(symbols)) continue
    if (tab === 'results' && !topics.includes('results') && !/result/i.test(a.subject)) continue
    lines.push({
      key: `a${a.id}`,
      atMs: Date.parse(a.announcedUtc ?? a.firstSeenUtc),
      kind: 'filing',
      title: `${a.symbol}: ${a.subject}`,
      source: 'NSE filing',
      symbols,
      topics,
      sentiment: a.sentiment,
      link: a.attachmentUrl || null,
    })
  }
  return lines.sort((a, b) => b.atMs - a.atMs)
}

// ---------------------------------------------------------------- the week ahead

export interface WeekRow {
  key: string
  date: string
  time: string | null
  title: string
  kind: 'expiry' | 'holiday' | 'event' | 'results' | 'meeting'
  importance: number
}

/**
 * The days ahead: scheduled events, NSE holidays and index expiries (the
 * market-factors events answer holds all three), and, for an admin, the
 * board meetings of the desk's names. Expiries on one day read as one line.
 */
export function weekRows(
  events: readonly MarketEvent[] | undefined,
  meetings: readonly IntelBoardMeeting[] | undefined,
  names: ReadonlySet<string>,
  from: string,
  to: string,
): WeekRow[] {
  const inRange = (d: string) => d >= from && d <= to
  const rows: WeekRow[] = []
  const expiries = new Map<string, string[]>()
  for (const e of events ?? []) {
    if (!inRange(e.date)) continue
    if (e.kind === 'expiry') {
      const und = e.title.replace(/\s+options expiry$/i, '')
      expiries.set(e.date, [...(expiries.get(e.date) ?? []), und])
      continue
    }
    rows.push({
      key: `e${e.id ?? `${e.date}-${e.title}`}`,
      date: e.date,
      time: e.timeIst,
      title: e.title,
      kind: e.kind === 'holiday' ? 'holiday' : 'event',
      importance: e.importance,
    })
  }
  for (const [date, unds] of expiries) {
    const sorted = [...new Set(unds)].sort(byUnderlying)
    rows.push({ key: `x${date}`, date, time: '15:30', title: `${sorted.join(' & ')} expiry`, kind: 'expiry', importance: 2 })
  }
  for (const m of meetings ?? []) {
    if (!inRange(m.eventDate) || !names.has(m.symbol.toUpperCase())) continue
    const results = /result/i.test(m.purpose)
    rows.push({
      key: `m${m.id}`,
      date: m.eventDate,
      time: null,
      title: `${m.symbol} ${results ? 'results' : `board meeting · ${m.purpose.toLowerCase()}`}`,
      kind: results ? 'results' : 'meeting',
      importance: results ? 2 : 1,
    })
  }
  const order = (t: string | null) => t ?? '99:99'
  return rows.sort((a, b) => a.date.localeCompare(b.date) || order(a.time).localeCompare(order(b.time)) || b.importance - a.importance)
}

// ---------------------------------------------------------------- overnight

/** The overseas rows of the pre-open table, by the recorder's keys (GlobalMarketKeys on the server). */
export const OVERNIGHT: ReadonlyArray<{ key: string; name: string; digits: number; yield?: boolean }> = [
  { key: 'DJI', name: 'Dow Jones', digits: 1 },
  { key: 'SPX', name: 'S&P 500', digits: 1 },
  { key: 'NDX', name: 'Nasdaq 100', digits: 1 },
  { key: 'N225', name: 'Nikkei 225', digits: 1 },
  { key: 'HSI', name: 'Hang Seng', digits: 1 },
  { key: 'BRENT', name: 'Brent', digits: 2 },
  { key: 'USDINR', name: 'USD/INR', digits: 2 },
  { key: 'US10Y', name: 'US 10Y', digits: 2, yield: true },
]

export const GIFT_KEY = 'GIFTNIFTY'

export interface OvernightRow {
  key: string
  name: string
  digits: number
  value: number
  /** % change, or for a yield the change in basis points. */
  change: number | null
  isYield: boolean
  /** "08:58" for a price taken this morning, "last close" for one a shut market carried, "Fri close" for a daily bar. */
  asOf: string
}

/** The newest snapshot of each key. */
export function latestSnapshots(snaps: readonly IntelSnapshot[] | undefined): Map<string, IntelSnapshot> {
  const out = new Map<string, IntelSnapshot>()
  for (const s of snaps ?? []) {
    const seen = out.get(s.key)
    if (!seen || Date.parse(s.fetchedUtc) >= Date.parse(seen.fetchedUtc)) out.set(s.key, s)
  }
  return out
}

/**
 * The overnight table: this morning's snapshot where the recorder has taken
 * one, else the market's last daily close. A row with neither is left out.
 */
export function overnightRows(snaps: readonly IntelSnapshot[] | undefined, daily: readonly IntelDailyBar[] | undefined): OvernightRow[] {
  const latest = latestSnapshots(snaps)
  const rows: OvernightRow[] = []
  for (const m of OVERNIGHT) {
    const s = latest.get(m.key)
    if (s) {
      const change = m.yield
        ? s.previousClose != null ? (s.price - s.previousClose) * 100 : null
        : s.changePct
      // A market shut overnight is priced at its last close, hours before the snapshot took it.
      const stale = s.asOfUtc != null && Date.parse(s.fetchedUtc) - Date.parse(s.asOfUtc) > 3 * 3600_000
      rows.push({ key: m.key, name: m.name, digits: m.digits, value: s.price, change, isYield: !!m.yield, asOf: stale ? 'last close' : istHm(s.asOfUtc ?? s.fetchedUtc) })
      continue
    }
    const bars = (daily ?? []).filter((b) => b.symbol === m.key).sort((a, b) => a.date.localeCompare(b.date))
    const last = bars[bars.length - 1]
    if (!last) continue
    const prev = bars[bars.length - 2]
    const change = prev ? (m.yield ? (last.close - prev.close) * 100 : ((last.close - prev.close) / prev.close) * 100) : null
    rows.push({ key: m.key, name: m.name, digits: m.digits, value: last.close, change, isYield: !!m.yield, asOf: `${weekdayOf(last.date)} close` })
  }
  return rows
}

// ---------------------------------------------------------------- the status strip on a phone

/**
 * The strip packs into a 6-column grid on a phone: the lead cell across the
 * whole width, account cells in halves, the rest in thirds, with the last row
 * stretched so no cell is left beside a gap. Spans per cell, in order.
 */
export function stripSpans(kinds: ReadonlyArray<'lead' | 'account' | 'other'>): number[] {
  const spans: number[] = kinds.map((k) => (k === 'lead' ? 6 : k === 'account' ? 3 : 2))
  // Close every row: a row that ends short hands its gap to its last cell.
  let used = 0
  let rowStart = 0
  for (let i = 0; i < spans.length; i++) {
    if (used + spans[i] > 6) {
      spans[i - 1] += 6 - used
      used = 0
      rowStart = i
    }
    used += spans[i]
    if (used === 6) {
      used = 0
      rowStart = i + 1
    }
  }
  if (used > 0 && rowStart < spans.length) {
    const inRow = spans.length - rowStart
    const each = Math.floor(6 / inRow)
    for (let i = rowStart; i < spans.length; i++) spans[i] = each
    spans[spans.length - 1] += 6 - each * inRow
  }
  return spans
}

// ---------------------------------------------------------------- figures in words

export type FigureTone = 'pos' | 'neg' | 'flat'

/** The colour of a rupee figure: flat for anything that rounds to ₹0, so a flat book never reads as a gain. */
export function figureTone(value: number | null | undefined): FigureTone | null {
  if (value == null || Number.isNaN(value)) return null
  const r = Math.round(value)
  return r > 0 ? 'pos' : r < 0 ? 'neg' : 'flat'
}

const MINUS = '−'

/**
 * A rupee figure in a dense cell: whole below a thousand, then thousands and
 * lakhs as the desk writes them ("+482", "−8.2k", "+1.36L"); the exact figure
 * goes in the cell's title. `signed` false prints a charge as a plain amount.
 */
export function compactInr(value: number, signed = true): string {
  const r = Math.round(value)
  const a = Math.abs(r)
  const sign = signed ? (r > 0 ? '+' : r < 0 ? MINUS : '') : r < 0 ? MINUS : ''
  if (a >= 100_000) return `${sign}${(a / 100_000).toFixed(2)}L`
  if (a >= 1_000) return `${sign}${(a / 1_000).toFixed(1)}k`
  return `${sign}${a}`
}

/** A signed number with Indian grouping and a true minus: "+57.45", "−2,184". */
export function signedNumber(value: number, digits = 2): string {
  const s = Math.abs(value).toLocaleString('en-IN', { minimumFractionDigits: digits, maximumFractionDigits: digits })
  return `${value > 0 ? '+' : value < 0 ? MINUS : ''}${s}`
}

/** A plain number with Indian grouping: "23,341.55". */
export function plainNumber(value: number, digits = 2): string {
  return `${value < 0 ? MINUS : ''}${Math.abs(value).toLocaleString('en-IN', { minimumFractionDigits: digits, maximumFractionDigits: digits })}`
}
