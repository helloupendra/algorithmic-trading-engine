/**
 * Today (/today): the owner's one page, as data.
 *
 * The owner asked on 1 Oct for one place to see everything at a glance, with
 * nothing on it that needs a click to keep the desk going. One endpoint,
 * GET /api/Today (admin-only), answers the whole page: the markets, what
 * deserves a look, the day's live trading, the agents, what they learned,
 * the system and the decisions taken. This module is the page-free half of
 * it, so it can be tested:
 *   - the shapes, and a reader that keeps what it can show. A body that is
 *     not the day at all is an error on screen; a section the body leaves
 *     out is null, and the page says that part did not come back rather than
 *     drawing an empty panel that reads as "nothing happened" (an absent
 *     attention list must never read as "nothing needs you");
 *   - the query hook, polling every 30 s;
 *   - the words the page puts on states: market pills, attention levels,
 *     run and decision statuses, "4 min ago", Sentinel's freshness.
 *
 * Read-only: nothing here posts, and the page asks no question.
 */

import { useQuery } from '@tanstack/react-query'
import { api } from './api'
import { istDay, istHm, weekdayOf } from './desk'
import { runStatusTone } from './runHistory'

// ---------- shapes ---------------------------------------------------------------

/** open, closed, pre-open or holiday; kept a string, so a state the server adds later still shows. */
export type MarketState = 'open' | 'closed' | 'pre-open' | 'holiday'

export interface TodayMarket {
  exchange: string
  state: MarketState | string
  opensUtc: string | null
  closesUtc: string | null
}

export type AttentionLevel = 'critical' | 'high' | 'medium' | 'info'
export type AttentionKind = 'incident' | 'check' | 'agent' | 'learning' | 'decision' | 'trading' | 'system'

/** One thing that deserves a look. Never a yes/no prompt: a title, why, and where to read more. */
export interface AttentionItem {
  level: AttentionLevel | string
  kind: AttentionKind | string
  title: string
  detail: string | null
  /** A console route ("/system/incidents?id=206"); null when there is nowhere to go. */
  link: string | null
  atUtc: string | null
}

/** One account's live trading today. Money is null when the API did not send it: unknown is not ₹0. */
export interface TodayAccount {
  userName: string
  net: number | null
  gross: number | null
  charges: number | null
  runsLive: number
  runsStopped: number
  openLegs: number
}

export type RunStatus = 'Running' | 'Stopped' | 'Failed'

export interface TodayRun {
  runId: number
  userName: string
  strategy: string
  underlying: string
  status: RunStatus | string
  net: number | null
  trades: number
  startedUtc: string | null
  stoppedUtc: string | null
  stopReason: string | null
}

export interface TodayTrading {
  /** Live trading only; a recap is never in these. */
  accounts: TodayAccount[]
  /** Today's runs, worst net first. */
  runs: TodayRun[]
  /** Recap runs started today: tests, in no total. */
  recapsToday: number
}

export interface AgentHighlight {
  text: string
  /** A console route, or null. */
  link: string | null
}

export interface TodayAgent {
  key: string
  name: string
  /** Null when the API did not say. */
  on: boolean | null
  callsToday: number
  failedCallsToday: number
  reportsToday: { ok: number; invalid: number; failed: number }
  lastActivityUtc: string | null
  /** At most four. */
  highlights: AgentHighlight[]
}

export interface LearnedMemory {
  id: number
  agentName: string
  text: string
  /** "verified by the check", "your note", "your correction". */
  how: string
}

export interface DroppedMemory {
  id: number
  agentName: string
  text: string
  why: string
}

export interface CheckScore {
  passed: number
  total: number
}

export interface CheckDay extends CheckScore {
  date: string
}

export interface TodayLearning {
  activeMemories: number | null
  learnedToday: LearnedMemory[]
  droppedToday: DroppedMemory[]
  /** Null on a day without a check (yet). */
  checkToday: CheckScore | null
  /** The last seven days with a check, oldest first. */
  checkDays: CheckDay[]
}

export interface TodaySystem {
  /** Sentinel's last completed round; null when it has never reported. */
  sentinelLastUtc: string | null
  checkup: { verdict: string; headline: string; utc: string | null } | null
  deploy: { commit: string; utc: string | null; summary: string } | null
  /** Null when the API did not send the count. */
  openIncidents: number | null
}

export type DecisionStatus = 'decided' | 'default' | 'open'

export interface TodayDecision {
  date: string
  title: string
  decided: string
  /** "owner" or "Claude (default)", as sent. */
  by: string
  status: DecisionStatus | string
}

/** The whole page. A section is null when the body left it out or sent something else in its place. */
export interface Today {
  /** The IST date the page is about. */
  date: string
  nowUtc: string | null
  markets: TodayMarket[] | null
  /** Most serious first. Empty means nothing needs the owner; null means the list did not come back. */
  attention: AttentionItem[] | null
  trading: TodayTrading | null
  agents: TodayAgent[] | null
  learning: TodayLearning | null
  system: TodaySystem | null
  /** Newest first. */
  decisions: TodayDecision[] | null
}

// ---------- reading the API -------------------------------------------------------

const record = (v: unknown): v is Record<string, unknown> => v != null && typeof v === 'object' && !Array.isArray(v)
const num = (v: unknown): number | null => (typeof v === 'number' && Number.isFinite(v) ? v : null)
const str = (v: unknown): string | null => (typeof v === 'string' ? v : null)
/** Text to show: trimmed, '' when missing. */
const words = (v: unknown): string => (str(v) ?? '').trim()
/** Text that may be absent: null rather than ''. */
const optional = (v: unknown): string | null => words(v) || null
/** A count: whole and not negative; 0 when missing. */
const count = (v: unknown): number => Math.max(0, Math.round(num(v) ?? 0))
const rows = (v: unknown): Record<string, unknown>[] => (Array.isArray(v) ? v.filter(record) : [])
/** An instant the browser can read, or null. */
const instant = (v: unknown): string | null => {
  const s = str(v)
  return s && !Number.isNaN(Date.parse(s)) ? s : null
}
const ISO_DAY = /^\d{4}-\d{2}-\d{2}$/

/**
 * A console route the page may link to, or null. Only a path on this site
 * ("/system/incidents?id=206"): a full URL, a protocol-relative "//host" or a
 * "javascript:" link never becomes a link.
 */
export function consoleLink(v: unknown): string | null {
  const s = words(v)
  return s.startsWith('/') && !s.startsWith('//') && !s.startsWith('/\\') ? s : null
}

const SECTIONS = ['markets', 'attention', 'trading', 'agents', 'learning', 'system', 'decisions'] as const

function readMarket(m: Record<string, unknown>): TodayMarket | null {
  const exchange = words(m.exchange)
  if (!exchange) return null
  return { exchange, state: words(m.state).toLowerCase() || 'unknown', opensUtc: instant(m.opensUtc), closesUtc: instant(m.closesUtc) }
}

function readAttention(a: Record<string, unknown>): AttentionItem | null {
  const title = words(a.title)
  if (!title) return null
  return {
    level: words(a.level).toLowerCase() || 'info',
    kind: words(a.kind).toLowerCase(),
    title,
    detail: optional(a.detail),
    link: consoleLink(a.link),
    atUtc: instant(a.atUtc),
  }
}

function readAccount(a: Record<string, unknown>): TodayAccount | null {
  const userName = words(a.userName)
  if (!userName) return null
  return {
    userName,
    net: num(a.net),
    gross: num(a.gross),
    charges: num(a.charges),
    runsLive: count(a.runsLive),
    runsStopped: count(a.runsStopped),
    openLegs: count(a.openLegs),
  }
}

function readRun(r: Record<string, unknown>): TodayRun | null {
  const runId = num(r.runId)
  if (runId == null) return null
  return {
    runId,
    userName: words(r.userName),
    strategy: words(r.strategy) || `Run ${runId}`,
    underlying: words(r.underlying),
    status: words(r.status),
    net: num(r.net),
    trades: count(r.trades),
    startedUtc: instant(r.startedUtc),
    stoppedUtc: instant(r.stoppedUtc),
    stopReason: optional(r.stopReason),
  }
}

function readTrading(t: Record<string, unknown>): TodayTrading {
  return {
    accounts: rows(t.accounts)
      .map(readAccount)
      .filter((a): a is TodayAccount => a != null),
    runs: worstFirst(
      rows(t.runs)
        .map(readRun)
        .filter((r): r is TodayRun => r != null),
    ),
    recapsToday: count(t.recapsToday),
  }
}

function readAgent(a: Record<string, unknown>): TodayAgent | null {
  const key = words(a.key)
  if (!key) return null
  const reports = record(a.reportsToday) ? a.reportsToday : {}
  return {
    key,
    name: words(a.name) || key,
    on: a.on === true ? true : a.on === false ? false : null,
    callsToday: count(a.callsToday),
    failedCallsToday: count(a.failedCallsToday),
    reportsToday: { ok: count(reports.ok), invalid: count(reports.invalid), failed: count(reports.failed) },
    lastActivityUtc: instant(a.lastActivityUtc),
    highlights: rows(a.highlights)
      .map((h) => ({ text: words(h.text), link: consoleLink(h.link) }))
      .filter((h) => h.text !== '')
      .slice(0, 4),
  }
}

function readScore(v: unknown): CheckScore | null {
  if (!record(v)) return null
  const passed = num(v.passed)
  const total = num(v.total)
  if (passed == null || total == null || total < 0) return null
  return { passed: Math.max(0, Math.round(passed)), total: Math.round(total) }
}

function readLearning(l: Record<string, unknown>): TodayLearning {
  const days = rows(l.checkDays)
    .map((d) => {
      const score = readScore(d)
      const date = words(d.date)
      return score && ISO_DAY.test(date) ? { date, ...score } : null
    })
    .filter((d): d is CheckDay => d != null)
    .sort((a, b) => a.date.localeCompare(b.date))
  return {
    activeMemories: num(l.activeMemories) == null ? null : count(l.activeMemories),
    learnedToday: rows(l.learnedToday)
      .map((m) => ({ id: num(m.id), agentName: words(m.agentName), text: words(m.text), how: words(m.how) }))
      .filter((m): m is LearnedMemory => m.id != null && m.text !== ''),
    droppedToday: rows(l.droppedToday)
      .map((m) => ({ id: num(m.id), agentName: words(m.agentName), text: words(m.text), why: words(m.why) }))
      .filter((m): m is DroppedMemory => m.id != null && m.text !== ''),
    checkToday: readScore(l.checkToday),
    checkDays: days.slice(-7),
  }
}

function readSystem(s: Record<string, unknown>): TodaySystem {
  const c = s.checkup
  const d = s.deploy
  return {
    sentinelLastUtc: instant(s.sentinelLastUtc),
    checkup: record(c) ? { verdict: words(c.verdict).toLowerCase(), headline: words(c.headline), utc: instant(c.utc) } : null,
    deploy:
      record(d) && (words(d.commit) || words(d.summary))
        ? { commit: words(d.commit), utc: instant(d.utc), summary: words(d.summary) }
        : null,
    openIncidents: num(s.openIncidents) == null ? null : count(s.openIncidents),
  }
}

function readDecision(d: Record<string, unknown>): TodayDecision | null {
  const title = words(d.title)
  if (!title) return null
  return { date: words(d.date), title, decided: words(d.decided), by: words(d.by), status: words(d.status).toLowerCase() }
}

/** A list section: its rows read, or null when the body sent no list. */
function listSection<T>(v: unknown, read: (r: Record<string, unknown>) => T | null): T[] | null {
  if (!Array.isArray(v)) return null
  return rows(v)
    .map(read)
    .filter((x): x is T => x != null)
}

/**
 * GET /api/Today's body as the page shows it. Throws for a body that is not
 * the day at all (an HTML page, an old API without the endpoint's shape), so
 * the page says so instead of drawing seven empty sections.
 */
export function readToday(raw: unknown): Today {
  if (!record(raw) || !SECTIONS.some((k) => k in raw)) {
    throw new Error("The API's Today page came back in a shape this page cannot read. Is the API build current?")
  }
  return {
    date: ISO_DAY.test(words(raw.date)) ? words(raw.date) : '',
    nowUtc: instant(raw.nowUtc),
    markets: listSection(raw.markets, readMarket),
    attention: (() => {
      const list = listSection(raw.attention, readAttention)
      return list ? mostSeriousFirst(list) : null
    })(),
    trading: record(raw.trading) ? readTrading(raw.trading) : null,
    agents: listSection(raw.agents, readAgent),
    learning: record(raw.learning) ? readLearning(raw.learning) : null,
    system: record(raw.system) ? readSystem(raw.system) : null,
    decisions: listSection(raw.decisions, readDecision),
  }
}

// ---------- query -------------------------------------------------------------------

/** How often the page reads the day again. */
export const TODAY_POLL_MS = 30_000

export function useToday() {
  return useQuery({
    queryKey: ['today'],
    queryFn: async () => readToday(await api.get<unknown>('/api/Today')),
    refetchInterval: TODAY_POLL_MS,
  })
}

// ---------- words -----------------------------------------------------------------

export type Tone = 'pos' | 'neg' | 'warn' | 'accent' | 'live' | 'neutral'

const LEVELS: Record<AttentionLevel, { label: string; tone: Tone; rank: number }> = {
  critical: { label: 'Critical', tone: 'neg', rank: 0 },
  high: { label: 'High', tone: 'neg', rank: 1 },
  medium: { label: 'Medium', tone: 'warn', rank: 2 },
  info: { label: 'Info', tone: 'neutral', rank: 3 },
}

/** An attention level's word and tone (the same tones as an incident's severity); a level this page does not know is shown by its own name, last. */
export function attentionLevel(level: string): { key: AttentionLevel | null; label: string; tone: Tone; rank: number } {
  const known = LEVELS[level as AttentionLevel]
  if (known) return { key: level as AttentionLevel, ...known }
  return { key: null, label: level ? capitalise(level) : 'Note', tone: 'neutral', rank: 4 }
}

/** Most serious first; the server's order kept within a level. */
export function mostSeriousFirst(items: readonly AttentionItem[]): AttentionItem[] {
  return items
    .map((item, i) => ({ item, i, rank: attentionLevel(item.level).rank }))
    .sort((a, b) => a.rank - b.rank || a.i - b.i)
    .map((x) => x.item)
}

/** Worst net first; a run without a net last; the server's order kept on a tie. */
export function worstFirst(runs: readonly TodayRun[]): TodayRun[] {
  return runs
    .map((run, i) => ({ run, i }))
    .sort((a, b) => {
      const x = a.run.net
      const y = b.run.net
      if (x == null || y == null) return x == null && y == null ? a.i - b.i : x == null ? 1 : -1
      return x - y || a.i - b.i
    })
    .map((x) => x.run)
}

const KINDS: Record<AttentionKind, string> = {
  incident: 'Incident',
  check: 'Check',
  agent: 'Agent',
  learning: 'Learning',
  decision: 'Decision',
  trading: 'Trading',
  system: 'System',
}

export function attentionKind(kind: string): string {
  return KINDS[kind as AttentionKind] ?? (kind ? capitalise(kind) : '')
}

/** A run's status as a word and a tone, in Run history's tones: a stop reads as a warning, so its reason is looked at. */
export function runStatus(status: string): { label: string; tone: Tone } {
  const known = ['Running', 'Stopped', 'Failed'].find((s) => s.toLowerCase() === status.toLowerCase())
  return known ? { label: known, tone: runStatusTone(known) } : { label: status || 'Unknown', tone: 'neutral' }
}

/** A decision's status: decided by the owner, a default taken for him, or still open. */
export function decisionStatus(status: string): { label: string; tone: Tone; means: string } {
  switch (status) {
    case 'decided':
      return { label: 'Decided', tone: 'pos', means: 'Decided, and in force' }
    case 'default':
      return { label: 'Default', tone: 'accent', means: 'A default taken while the owner had not said; in force until he says otherwise' }
    case 'open':
      return { label: 'Open', tone: 'warn', means: 'Not decided yet; nothing waits on it here' }
    default:
      return { label: status ? capitalise(status) : 'Unknown', tone: 'neutral', means: '' }
  }
}

/** An instant's IST clock: "15:30" on the day of `nowMs`, "Fri 09:15" on another day; '' for none. */
export function istWhen(iso: string | null, nowMs: number): string {
  if (!iso) return ''
  const ms = Date.parse(iso)
  if (Number.isNaN(ms)) return ''
  const day = istDay(ms)
  return day === istDay(nowMs) ? istHm(iso) : `${weekdayOf(day)} ${istHm(iso)}`
}

/** A market as its pill says it: "NSE open · closes 15:30", "MCX closed · opens Fri 09:00". */
export function marketText(m: TodayMarket, nowMs: number): { text: string; tone: Tone } {
  const opens = istWhen(m.opensUtc, nowMs)
  const closes = istWhen(m.closesUtc, nowMs)
  switch (m.state) {
    case 'open':
      return { text: `${m.exchange} open${closes ? ` · closes ${closes}` : ''}`, tone: 'pos' }
    case 'pre-open':
      return { text: `${m.exchange} pre-open${opens ? ` · opens ${opens}` : ''}`, tone: 'neutral' }
    case 'closed':
      return { text: `${m.exchange} closed${opens ? ` · opens ${opens}` : ''}`, tone: 'neutral' }
    case 'holiday':
      return { text: `${m.exchange} holiday${opens ? ` · opens ${opens}` : ''}`, tone: 'neutral' }
    default:
      return { text: `${m.exchange} ${m.state}`, tone: 'neutral' }
  }
}

/** "just now", "4 min ago", "3 h ago", "2 days ago"; '' for no time. A time ahead of the clock reads "just now". */
export function agoText(iso: string | null, nowMs: number): string {
  if (!iso) return ''
  const ms = Date.parse(iso)
  if (Number.isNaN(ms)) return ''
  const s = Math.max(0, Math.floor((nowMs - ms) / 1000))
  if (s < 60) return 'just now'
  const m = Math.floor(s / 60)
  if (m < 60) return `${m} min ago`
  const h = Math.floor(m / 60)
  if (h < 24) return `${h} h ago`
  const d = Math.floor(h / 24)
  return d === 1 ? '1 day ago' : `${d} days ago`
}

/** Sentinel runs a round every few minutes; past this, the page says it may have stopped. */
export const SENTINEL_STALE_MINUTES = 10

/** Sentinel's last round in words, and whether it is late. */
export function sentinelState(lastUtc: string | null, nowMs: number): { text: string; late: boolean } {
  if (!lastUtc) return { text: 'no round reported yet', late: true }
  const minutes = (nowMs - Date.parse(lastUtc)) / 60_000
  const late = minutes > SENTINEL_STALE_MINUTES
  return { text: `last round ${agoText(lastUtc, nowMs)}${late ? '; it may have stopped' : ''}`, late }
}

const WEEKDAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday']
const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December']

/** "Thursday 1 October 2026" for "2026-10-01"; the text as sent when it is not a date. */
export function longDate(isoDay: string): string {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(isoDay)
  if (!m) return isoDay
  const [y, mo, d] = [Number(m[1]), Number(m[2]), Number(m[3])]
  const at = new Date(Date.UTC(y, mo - 1, d))
  if (Number.isNaN(at.getTime()) || at.getUTCDate() !== d) return isoDay
  return `${WEEKDAYS[at.getUTCDay()]} ${d} ${MONTHS[mo - 1]} ${y}`
}

/** "3 recap runs today (tests, not counted)"; '' for none. */
export function recapText(n: number): string {
  if (n <= 0) return ''
  return n === 1 ? '1 recap run today (a test, not counted)' : `${n} recap runs today (tests, not counted)`
}

/** "1 call", "5 calls". */
export function plural(n: number, one: string, many = `${one}s`): string {
  return `${n} ${n === 1 ? one : many}`
}

function capitalise(s: string): string {
  return s.charAt(0).toUpperCase() + s.slice(1)
}
