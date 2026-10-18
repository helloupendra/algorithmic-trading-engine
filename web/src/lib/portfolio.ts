/**
 * Portfolio (/portfolio): a signed-in user's own demat and broker accounts,
 * linked read-only, and a report of facts about what they hold.
 *
 * Facts only, never advice: SEBI requires registration to give investment
 * advice, so nothing here, nor on the page, says what to do with a holding.
 * The API answers every route for the caller alone (api/Portfolio): a user
 * sees only their own accounts and snapshots.
 *
 * The page-free half of the section, so it can be tested:
 *   - the API's shapes, and readers that take the JSON as `unknown`. A body
 *     that has no holdings list or no totals is refused (an error on screen,
 *     never an empty table that reads as "you hold nothing"); a section that is
 *     missing is null, so the page can say "not in this report" instead of
 *     "none"; a row with no identity is dropped and counted in a data note;
 *   - the words and formats the page shows: weights as percentages, the HHI's
 *     plain meaning, a connection's state, the sync countdown;
 *   - the query hooks, keyed by the signed-in user so one account's report is
 *     never served from the cache to the next account in the same tab.
 *
 * Broker tokens: the token form posts the fields straight from the form
 * (`submitPortfolioToken`, not a mutation, whose cache would keep the
 * variables), and nothing here stores them anywhere.
 */

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from './api'
import { useAuth } from './auth'

// ---------- shapes ----------------------------------------------------------

/** The three brokers the server links today, in the order a first-run panel offers them. */
export const PORTFOLIO_BROKERS = ['angel', 'dhan', 'fyers'] as const

export const BROKER_NAMES: Readonly<Record<string, string>> = { fyers: 'FYERS', dhan: 'Dhan', angel: 'Angel One' }

export type ConnectionStatus = 'not_connected' | 'connected' | 'expired' | 'error'
export type ConnectMode = 'redirect' | 'token'

export interface TokenField {
  key: string
  label: string
  help: string
}

export interface PortfolioConnection {
  /** "fyers", "dhan", "angel"; kept a string, so a broker the server adds later still shows. */
  broker: string
  name: string
  status: ConnectionStatus
  /** Masked by the server ("XX4521"); shown as it comes. */
  accountLabel: string | null
  connectedUtc: string | null
  expiresUtc: string | null
  lastSyncUtc: string | null
  lastError: string | null
  connectMode: ConnectMode
  tokenFields: TokenField[]
}

export type ConnectStart = { mode: 'redirect'; url: string } | { mode: 'token'; fields: TokenField[] }

export interface SyncOutcome {
  broker: string
  ok: boolean
  error: string | null
}

export interface SyncResult {
  takenUtc: string | null
  results: SyncOutcome[]
}

export interface ReportSource {
  broker: string
  takenUtc: string | null
  ok: boolean
  error: string | null
}

export interface ReportTotals {
  invested: number | null
  current: number | null
  pnl: number | null
  /** Percent units: 12.5 is 12.5 %. */
  pnlPct: number | null
  dayChange: number | null
  dayChangePct: number | null
  holdingsCount: number | null
}

export interface Holding {
  symbol: string
  isin: string | null
  name: string
  brokers: string[]
  qty: number | null
  avgPrice: number | null
  ltp: number | null
  invested: number | null
  value: number | null
  pnl: number | null
  pnlPct: number | null
  dayChange: number | null
  /** A fraction of the portfolio's value, 0 to 1. */
  weight: number | null
  sector: string | null
  marketCapBand: string | null
}

export interface AllocationSlice {
  /** "Unknown" when the server could not tell; never blank. */
  name: string
  value: number | null
  weight: number | null
}

export interface Allocation {
  bySector: AllocationSlice[]
  byBroker: AllocationSlice[]
  byMarketCap: AllocationSlice[]
}

export interface Concentration {
  top1Weight: number | null
  top5Weight: number | null
  /** Sum of squared weights, 0 to 1 (a 0–10,000 answer is scaled down when read). */
  hhi: number | null
  largest: string[]
}

export interface RiskFigures {
  lookbackDays: number | null
  volatilityAnnualPct: number | null
  betaVsNifty: number | null
  maxDrawdownPct: number | null
  /** The share of the portfolio's value the figures are computed over, 0 to 1. */
  coveredWeight: number | null
  note: string | null
}

export interface PortfolioPosition {
  broker: string
  symbol: string
  product: string
  qty: number | null
  avgPrice: number | null
  ltp: number | null
  pnl: number | null
  isDerivative: boolean
}

export interface DerivativesExposure {
  openPositions: number | null
  grossExposure: number | null
  note: string | null
}

export interface BrokerFunds {
  broker: string
  available: number | null
  used: number | null
  total: number | null
}

export interface PortfolioSummary {
  text: string
  model: string | null
  /** The server checked the paragraph's numbers against the report. */
  checked: boolean
  generatedUtc: string | null
}

export interface PortfolioReport {
  asOfUtc: string | null
  sources: ReportSource[]
  totals: ReportTotals
  holdings: Holding[]
  /** Null: the section was not in the answer (not the same as "nothing to show"). */
  allocation: Allocation | null
  concentration: Concentration | null
  risk: RiskFigures | null
  positions: PortfolioPosition[] | null
  derivatives: DerivativesExposure | null
  funds: BrokerFunds[] | null
  summary: PortfolioSummary | null
  /** The server's notes, then the reader's own (rows it could not read). */
  dataNotes: string[]
  disclaimer: string
}

/** GET /report: the report, or the server's word that nothing has been synced yet (its 404). */
export type ReportAnswer = { kind: 'report'; report: PortfolioReport } | { kind: 'none'; message: string }

export interface HistoryPoint {
  /** A calendar day, "2026-10-16". */
  date: string
  invested: number | null
  current: number | null
  pnl: number | null
}

/** Shown when the report did not come back, and whenever the report's own is blank. */
export const DISCLAIMER = 'Information about your own holdings, not investment advice.'

// ---------- readers ----------------------------------------------------------

type Rec = Record<string, unknown>

function rec(value: unknown): Rec | null {
  return value && typeof value === 'object' && !Array.isArray(value) ? (value as Rec) : null
}

/** A finite number, or a numeric string (a decimal the server wrote as text); anything else is unknown. */
export function num(value: unknown): number | null {
  if (typeof value === 'number') return Number.isFinite(value) ? value : null
  if (typeof value === 'string' && value.trim() !== '') {
    const n = Number(value)
    return Number.isFinite(n) ? n : null
  }
  return null
}

function str(value: unknown): string | null {
  return typeof value === 'string' && value.trim() !== '' ? value : null
}

function strings(value: unknown): string[] {
  return Array.isArray(value) ? value.map(str).filter((s): s is string => s !== null) : []
}

/** A broker's display name: the server's, else the known one, else the key itself. */
export function brokerName(key: string, named?: string | null): string {
  return str(named) ?? BROKER_NAMES[key] ?? key
}

/** A broker's name as the linked accounts give it, for a key they may not hold (a source the report names). */
export function nameFor(connections: readonly PortfolioConnection[] | undefined, broker: string): string {
  return connections?.find((c) => c.broker === broker)?.name ?? brokerName(broker)
}

const STATUSES: readonly ConnectionStatus[] = ['not_connected', 'connected', 'expired', 'error']

function readTokenField(raw: unknown): TokenField | null {
  const o = rec(raw)
  const key = str(o?.key)
  if (!o || !key) return null
  return { key, label: str(o.label) ?? key, help: str(o.help) ?? '' }
}

function readTokenFields(raw: unknown): TokenField[] {
  return Array.isArray(raw) ? raw.map(readTokenField).filter((f): f is TokenField => f !== null) : []
}

export function readConnection(raw: unknown): PortfolioConnection | null {
  const o = rec(raw)
  const broker = str(o?.broker)
  if (!o || !broker) return null
  const status = STATUSES.includes(o.status as ConnectionStatus) ? (o.status as ConnectionStatus) : 'error'
  const tokenFields = readTokenFields(o.tokenFields)
  const connectMode: ConnectMode =
    o.connectMode === 'redirect' || o.connectMode === 'token' ? o.connectMode : tokenFields.length > 0 ? 'token' : 'redirect'
  return {
    broker,
    name: brokerName(broker, str(o.name)),
    status,
    accountLabel: str(o.accountLabel),
    connectedUtc: str(o.connectedUtc),
    expiresUtc: str(o.expiresUtc),
    lastSyncUtc: str(o.lastSyncUtc),
    // A status the console does not know is shown as a link error, and says why.
    lastError: str(o.lastError) ?? (status === 'error' && o.status !== 'error' ? `The server reported "${String(o.status)}".` : null),
    connectMode,
    tokenFields,
  }
}

/** GET /connections. A body with no brokers list is refused: it cannot be told from "none linked". */
export function readConnections(raw: unknown): PortfolioConnection[] {
  const brokers = rec(raw)?.brokers
  if (!Array.isArray(brokers)) throw new Error('The linked accounts could not be read: the answer had no brokers list.')
  return brokers.map(readConnection).filter((c): c is PortfolioConnection => c !== null)
}

/** POST /connections/{broker}/start. */
export function readConnectStart(raw: unknown): ConnectStart {
  const o = rec(raw)
  if (o?.mode === 'redirect') {
    const url = str(o.url)
    if (!url) throw new Error('The broker sign-in address did not come back.')
    return { mode: 'redirect', url }
  }
  if (o?.mode === 'token') return { mode: 'token', fields: readTokenFields(o.fields) }
  throw new Error('The server did not say how to link this broker.')
}

/** POST /sync. */
export function readSyncResult(raw: unknown): SyncResult {
  const o = rec(raw) ?? {}
  const results = Array.isArray(o.results)
    ? o.results
        .map(rec)
        .filter((r): r is Rec => r !== null && str(r.broker) !== null)
        .map((r) => ({ broker: r.broker as string, ok: r.ok === true, error: str(r.error) }))
    : []
  return { takenUtc: str(o.takenUtc), results }
}

function readTotals(o: Rec): ReportTotals {
  return {
    invested: num(o.invested),
    current: num(o.current),
    pnl: num(o.pnl),
    pnlPct: num(o.pnlPct),
    dayChange: num(o.dayChange),
    dayChangePct: num(o.dayChangePct),
    holdingsCount: num(o.holdingsCount),
  }
}

export function readHolding(raw: unknown): Holding | null {
  const o = rec(raw)
  const symbol = str(o?.symbol)
  if (!o || !symbol) return null
  return {
    symbol,
    isin: str(o.isin),
    name: str(o.name) ?? symbol,
    brokers: strings(o.brokers),
    qty: num(o.qty),
    avgPrice: num(o.avgPrice),
    ltp: num(o.ltp),
    invested: num(o.invested),
    value: num(o.value),
    pnl: num(o.pnl),
    pnlPct: num(o.pnlPct),
    dayChange: num(o.dayChange),
    weight: num(o.weight),
    sector: str(o.sector),
    marketCapBand: str(o.marketCapBand),
  }
}

export const UNKNOWN = 'Unknown'

function readSlices(raw: unknown): AllocationSlice[] {
  if (!Array.isArray(raw)) return []
  return raw
    .map(rec)
    .filter((r): r is Rec => r !== null)
    .map((r) => ({ name: str(r.name) ?? UNKNOWN, value: num(r.value), weight: num(r.weight) }))
}

function readAllocation(raw: unknown): Allocation | null {
  const o = rec(raw)
  if (!o) return null
  return { bySector: readSlices(o.bySector), byBroker: readSlices(o.byBroker), byMarketCap: readSlices(o.byMarketCap) }
}

/**
 * The HHI as a fraction. The contract's weights are fractions, so their
 * squares sum to at most 1; a figure above 1 can only be the 0–10,000 scale
 * (percent weights squared), and is scaled to match.
 */
export function hhiFraction(raw: number | null): number | null {
  if (raw == null || raw < 0) return null
  return raw > 1 ? raw / 10_000 : raw
}

function readConcentration(raw: unknown): Concentration | null {
  const o = rec(raw)
  if (!o) return null
  return { top1Weight: num(o.top1Weight), top5Weight: num(o.top5Weight), hhi: hhiFraction(num(o.hhi)), largest: strings(o.largest) }
}

function readRisk(raw: unknown): RiskFigures | null {
  const o = rec(raw)
  if (!o) return null
  return {
    lookbackDays: num(o.lookbackDays),
    volatilityAnnualPct: num(o.volatilityAnnualPct),
    betaVsNifty: num(o.betaVsNifty),
    maxDrawdownPct: num(o.maxDrawdownPct),
    coveredWeight: num(o.coveredWeight),
    note: str(o.note),
  }
}

function readPosition(raw: unknown): PortfolioPosition | null {
  const o = rec(raw)
  const symbol = str(o?.symbol)
  if (!o || !symbol) return null
  return {
    broker: str(o.broker) ?? '',
    symbol,
    product: str(o.product) ?? '',
    qty: num(o.qty),
    avgPrice: num(o.avgPrice),
    ltp: num(o.ltp),
    pnl: num(o.pnl),
    isDerivative: o.isDerivative === true,
  }
}

function readFunds(raw: unknown): BrokerFunds | null {
  const o = rec(raw)
  const broker = str(o?.broker)
  if (!o || !broker) return null
  return { broker, available: num(o.available), used: num(o.used), total: num(o.total) }
}

function readSource(raw: unknown): ReportSource | null {
  const o = rec(raw)
  const broker = str(o?.broker)
  if (!o || !broker) return null
  return { broker, takenUtc: str(o.takenUtc), ok: o.ok === true, error: str(o.error) }
}

function readSummary(raw: unknown): PortfolioSummary | null {
  const o = rec(raw)
  const text = str(o?.text)
  if (!o || !text) return null
  return { text: text.trim(), model: str(o.model), checked: o.checked === true, generatedUtc: str(o.generatedUtc) }
}

/** Reads a list, dropping the rows `read` refuses, and says how many it dropped. */
function readRows<T>(raw: unknown, read: (row: unknown) => T | null): { rows: T[]; dropped: number } {
  if (!Array.isArray(raw)) return { rows: [], dropped: 0 }
  const rows = raw.map(read).filter((r): r is T => r !== null)
  return { rows, dropped: raw.length - rows.length }
}

function plural(n: number, one: string, many = `${one}s`): string {
  return `${n} ${n === 1 ? one : many}`
}

/**
 * GET /report. Refused when there is no holdings list or no totals: a page
 * built from such a body would say "no holdings", which is not what the
 * server said.
 */
export function readReport(raw: unknown): PortfolioReport {
  const o = rec(raw)
  if (!o) throw new Error('The portfolio report could not be read.')
  if (!Array.isArray(o.holdings)) throw new Error('The portfolio report could not be read: it had no holdings list.')
  const totals = rec(o.totals)
  if (!totals) throw new Error('The portfolio report could not be read: it had no totals.')

  const holdings = readRows(o.holdings, readHolding)
  const positions = Array.isArray(o.positions) ? readRows(o.positions, readPosition) : null
  const funds = Array.isArray(o.funds) ? readRows(o.funds, readFunds) : null
  const sources = readRows(o.sources, readSource)

  const notes = strings(o.dataNotes)
  if (holdings.dropped > 0) notes.push(`${plural(holdings.dropped, 'holding')} in the answer had no symbol and ${holdings.dropped === 1 ? 'is' : 'are'} not shown.`)
  if (positions && positions.dropped > 0) notes.push(`${plural(positions.dropped, 'position')} in the answer had no symbol and ${positions.dropped === 1 ? 'is' : 'are'} not shown.`)

  const derivatives = rec(o.derivatives)
  return {
    asOfUtc: str(o.asOfUtc),
    sources: sources.rows,
    totals: readTotals(totals),
    holdings: holdings.rows,
    allocation: readAllocation(o.allocation),
    concentration: readConcentration(o.concentration),
    risk: readRisk(o.risk),
    positions: positions?.rows ?? null,
    derivatives: derivatives
      ? { openPositions: num(derivatives.openPositions), grossExposure: num(derivatives.grossExposure), note: str(derivatives.note) }
      : null,
    funds: funds?.rows ?? null,
    summary: readSummary(o.summary),
    dataNotes: notes,
    disclaimer: str(o.disclaimer) ?? DISCLAIMER,
  }
}

/** GET /history: the days in order, a day without a date dropped. */
export function readHistory(raw: unknown): HistoryPoint[] {
  if (!Array.isArray(raw)) throw new Error('The portfolio history could not be read.')
  return raw
    .map(rec)
    .filter((r): r is Rec => r !== null && typeof r.date === 'string' && /^\d{4}-\d{2}-\d{2}/.test(r.date))
    .map((r) => ({ date: (r.date as string).slice(0, 10), invested: num(r.invested), current: num(r.current), pnl: num(r.pnl) }))
    .sort((a, b) => a.date.localeCompare(b.date))
}

/**
 * The "nothing synced yet" answer: a 404 whose body is JSON with a message.
 * A 404 without one (no Portfolio API on this server, or the SPA's HTML
 * fallback) is a failure, not an empty portfolio.
 */
export function nothingSynced(error: unknown): string | null {
  const e = error as { status?: unknown; body?: unknown } | null
  if (e?.status !== 404) return null
  return str(rec(e.body)?.message)
}

/** How long a refused sync says to wait, in seconds: its `retryAfterSeconds`, else the minute the limit is. */
export function retryAfterSeconds(error: unknown, limit = SYNC_LIMIT_SECONDS): number | null {
  const e = error as { status?: unknown; body?: unknown } | null
  if (e?.status !== 429) return null
  const after = num(rec(e.body)?.retryAfterSeconds)
  return after != null && after > 0 ? Math.ceil(after) : limit
}

// ---------- words and formats --------------------------------------------------

const MINUS = '−'

/** A weight (0–1) as a percentage, one decimal: "12.3%"; a sliver under 0.05% reads "<0.1%", not "0.0%". */
export function formatWeight(weight: number | null | undefined): string {
  if (weight == null || !Number.isFinite(weight)) return '—'
  if (weight > 0 && weight < 0.0005) return '<0.1%'
  const pct = weight * 100
  return `${pct < 0 ? MINUS : ''}${Math.abs(pct).toFixed(1)}%`
}

/** A percentage the API gave in percent units, signed with a true minus: "+12.40%", "−3.10%", "0.00%". */
export function formatSignedPct(pct: number | null | undefined, digits = 2): string {
  if (pct == null || !Number.isFinite(pct)) return '—'
  const fixed = Math.abs(pct).toFixed(digits)
  if (Number(fixed) === 0) return `${(0).toFixed(digits)}%`
  return `${pct > 0 ? '+' : MINUS}${fixed}%`
}

/** A percentage in percent units, unsigned: "16.2%". */
export function formatPct(pct: number | null | undefined, digits = 1): string {
  if (pct == null || !Number.isFinite(pct)) return '—'
  return `${pct < 0 ? MINUS : ''}${Math.abs(pct).toFixed(digits)}%`
}

/** Rupees as short as an axis needs them: "85k", "4.2L", "1.35Cr". */
export function compactRupees(value: number): string {
  const a = Math.abs(value)
  const sign = value < 0 ? MINUS : ''
  const trim = (n: number, d: number) => String(Number(n.toFixed(d)))
  if (a >= 10_000_000) return `${sign}₹${trim(a / 10_000_000, 2)}Cr`
  if (a >= 100_000) return `${sign}₹${trim(a / 100_000, 2)}L`
  if (a >= 1_000) return `${sign}₹${trim(a / 1_000, 1)}k`
  return `${sign}₹${Math.round(a)}`
}

export type HhiBand = 'low' | 'moderate' | 'high'

export interface HhiReading {
  /** On the conventional 0–10,000 scale. */
  points: number
  /** 1 / HHI: the number of equal-sized holdings that would be as concentrated. */
  equivalent: number
  band: HhiBand
  /** One plain line for the page. */
  text: string
}

/**
 * What an HHI means in words. The bands are the US Horizontal Merger
 * Guidelines' (2010), the usual reading of the index: under 1,500 points is
 * unconcentrated, 1,500 to 2,500 moderately and over 2,500 highly
 * concentrated. A description of the spread, not a judgement of it.
 */
export function hhiMeaning(hhi: number | null | undefined): HhiReading | null {
  const h = hhiFraction(hhi ?? null)
  if (h == null || h === 0) return null
  const points = Math.round(h * 10_000)
  const equivalent = Math.max(1, Math.round(1 / h))
  const band: HhiBand = points < 1_500 ? 'low' : points <= 2_500 ? 'moderate' : 'high'
  const like = equivalent === 1 ? 'a single holding' : `${equivalent} equal-sized holdings`
  const word = band === 'low' ? 'spread out' : band === 'moderate' ? 'moderately concentrated' : 'highly concentrated'
  return { points, equivalent, band, text: `${word}: about as concentrated as ${like}` }
}

/** "18 Oct, 15:31" in IST: the time a snapshot or a link is stamped with. */
export function formatStamp(iso: string | null | undefined): string {
  if (!iso) return '—'
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return '—'
  return d.toLocaleString('en-IN', { timeZone: 'Asia/Kolkata', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit', hour12: false })
}

/** "12 Sep" for a calendar day ("2026-09-12"), read as the day itself, not shifted by a time zone. */
export function formatDay(date: string): string {
  const [y, m, d] = date.split('-').map(Number)
  if (!y || !m || !d) return date
  return new Date(Date.UTC(y, m - 1, d)).toLocaleDateString('en-IN', { timeZone: 'UTC', day: 'numeric', month: 'short' })
}

export type Tone = 'pos' | 'neg' | 'warn' | 'neutral'

/** The colour of a signed amount: sign only. Under half a rupee is flat. */
export function signTone(value: number | null | undefined): 'pos' | 'neg' | '' {
  if (value == null || Math.abs(value) < 0.5) return ''
  return value > 0 ? 'pos' : 'neg'
}

export interface ConnectionView {
  status: ConnectionStatus
  label: string
  tone: Tone
  /** The button that links it: none while it is linked and current. */
  action: 'Connect' | 'Reconnect' | null
  canDisconnect: boolean
}

/**
 * A connection as the page shows it now. A link the server still calls
 * connected whose expiry has passed is expired: the list is read once a
 * minute, a FYERS link ends at a fixed time, and the page must not say
 * "linked" past it.
 */
export function connectionView(c: PortfolioConnection, nowMs: number): ConnectionView {
  let status = c.status
  if (status === 'connected' && c.expiresUtc) {
    const ends = Date.parse(c.expiresUtc)
    if (Number.isFinite(ends) && ends <= nowMs) status = 'expired'
  }
  switch (status) {
    case 'connected':
      return { status, label: 'Linked', tone: 'pos', action: null, canDisconnect: true }
    case 'expired':
      return { status, label: 'Link expired', tone: 'warn', action: 'Reconnect', canDisconnect: true }
    case 'error':
      return { status, label: 'Link error', tone: 'neg', action: 'Reconnect', canDisconnect: true }
    default:
      return { status, label: 'Not linked', tone: 'neutral', action: 'Connect', canDisconnect: false }
  }
}

/** The server's rate limit on POST /sync. */
export const SYNC_LIMIT_SECONDS = 60

/** Seconds until the next sync is allowed, from the last one; zero when it is. */
export function syncWaitSeconds(nowMs: number, lastSyncMs: number | null, limitSeconds = SYNC_LIMIT_SECONDS): number {
  if (lastSyncMs == null || !Number.isFinite(lastSyncMs)) return 0
  return Math.max(0, Math.ceil((lastSyncMs + limitSeconds * 1000 - nowMs) / 1000))
}

/** The latest sync any connection records, in ms; null when none has synced. */
export function latestSyncMs(connections: readonly PortfolioConnection[]): number | null {
  let best: number | null = null
  for (const c of connections) {
    const t = c.lastSyncUtc ? Date.parse(c.lastSyncUtc) : NaN
    if (Number.isFinite(t) && (best == null || t > best)) best = t
  }
  return best
}

/**
 * Where a broker's sign-in may send the browser: an https address, or http on
 * this machine (the dev mock). Anything else (javascript:, data:, a typo) is
 * refused rather than followed.
 */
export function safeRedirectUrl(url: string, origin: string): string | null {
  let parsed: URL
  try {
    parsed = new URL(url, origin)
  } catch {
    return null
  }
  if (parsed.protocol === 'https:') return parsed.href
  const local = parsed.hostname === 'localhost' || parsed.hostname === '127.0.0.1'
  if (parsed.protocol === 'http:' && (local || parsed.origin === origin)) return parsed.href
  return null
}

/** A server's message as the end of a sentence: one full stop, never two ("ended.." read as a typo). */
export function sentence(text: string): string {
  const t = text.trim()
  return /[.!?…]$/.test(t) ? t : `${t}.`
}

/** What a broker's redirect came back with: `?connected={broker}` or `?error={message}`. */
export type RedirectResult = { kind: 'connected'; broker: string } | { kind: 'error'; message: string }

export function redirectResult(params: URLSearchParams): RedirectResult | null {
  const broker = params.get('connected')?.trim().toLowerCase()
  if (broker && /^[a-z0-9_-]{1,32}$/.test(broker)) return { kind: 'connected', broker }
  const error = params.get('error')?.trim()
  if (error) return { kind: 'error', message: error.length > 300 ? `${error.slice(0, 300)}…` : error }
  return null
}

/**
 * Whether a token form's field is typed hidden. Everything is, but an
 * identifier the broker shows on its profile page (a client ID), which the
 * user needs to see to type correctly.
 */
export function isSecretField(key: string): boolean {
  return !/^(client|user|account)[-_]?(id|code)$/i.test(key)
}

/** Beta in a line: how the value moved next to NIFTY 50 over the lookback. */
export function betaMeaning(beta: number | null | undefined): string | null {
  if (beta == null || !Number.isFinite(beta)) return null
  if (beta < 0) return 'on average the value moved against NIFTY 50, day to day'
  return `the value moved about ${beta.toFixed(2)}× as much as NIFTY 50, day to day`
}

// ---------- the holdings table ---------------------------------------------------

export type HoldingSortKey = 'symbol' | 'name' | 'brokers' | 'qty' | 'avgPrice' | 'ltp' | 'value' | 'pnl' | 'pnlPct' | 'weight'
export type SortDir = 'asc' | 'desc'

/** The direction a column sorts in on its first click: text A to Z, figures largest first. */
export function firstSortDir(key: HoldingSortKey): SortDir {
  return key === 'symbol' || key === 'name' || key === 'brokers' ? 'asc' : 'desc'
}

function sortValue(h: Holding, key: HoldingSortKey): string | number | null {
  if (key === 'symbol' || key === 'name') return h[key].toLowerCase()
  if (key === 'brokers') return h.brokers.map((b) => brokerName(b)).join(', ').toLowerCase() || null
  return h[key]
}

/** Sorted by one column; an unknown value goes last whichever the direction, and ties keep the symbol order. */
export function sortHoldings(rows: readonly Holding[], key: HoldingSortKey, dir: SortDir): Holding[] {
  const sign = dir === 'asc' ? 1 : -1
  return [...rows].sort((a, b) => {
    const x = sortValue(a, key)
    const y = sortValue(b, key)
    if (x == null || y == null) {
      if (x == null && y == null) return a.symbol.localeCompare(b.symbol)
      return x == null ? 1 : -1
    }
    const c = typeof x === 'number' && typeof y === 'number' ? x - y : String(x).localeCompare(String(y))
    return c !== 0 ? c * sign : a.symbol.localeCompare(b.symbol)
  })
}

/** The rows whose symbol, name, ISIN, sector or broker contains every word typed. */
export function filterHoldings(rows: readonly Holding[], query: string): Holding[] {
  const words = query.toLowerCase().split(/\s+/).filter(Boolean)
  if (words.length === 0) return [...rows]
  return rows.filter((h) => {
    const hay = [h.symbol, h.name, h.isin ?? '', h.sector ?? '', h.marketCapBand ?? '', ...h.brokers.map((b) => `${b} ${brokerName(b)}`)]
      .join(' ')
      .toLowerCase()
    return words.every((w) => hay.includes(w))
  })
}

// ---------- advice words -----------------------------------------------------------

/**
 * Words that tell someone what to do with a holding. The page's own text
 * never uses them (portfolio.test.ts scans it), and a written summary that
 * does is not shown: the server checks it first, and this is the second
 * lock. "Holding" and "holdings" are nouns of fact and are not on the list;
 * "hold" is.
 */
// advice-words:start
const ADVICE_PATTERN =
  /\b(should|buy|buys|buying|sell|sells|selling|hold|holds|exit|exits|exiting|targets?|stop[\s-]?loss(es)?|rebalanc(e|es|ed|ing)|consider(s|ed|ing)?|recommend(s|ed|ing|ation|ations)?|accumulate|kharid\w*|bech\w*)\b/gi
// advice-words:end

/** The advice words in a text, lower-cased, each once. */
export function adviceWords(text: string): string[] {
  return [...new Set([...text.matchAll(ADVICE_PATTERN)].map((m) => m[0].toLowerCase()))]
}

/** The summary the page may show: checked by the server, and free of advice words. */
export function shownSummary(summary: PortfolioSummary | null): PortfolioSummary | null {
  if (!summary || !summary.checked) return null
  return adviceWords(summary.text).length === 0 ? summary : null
}

// ---------- the API ------------------------------------------------------------------

/**
 * `VITE_PORTFOLIO_MOCK=1 npm run dev` answers every Portfolio route from
 * lib/portfolio.fixture.ts (made-up numbers), so the page can be reviewed
 * without the API. `import.meta.env.DEV` is the literal `false` in a
 * production build, so the branch and the fixture's chunk are dropped there.
 */
const PORTFOLIO_MOCK = import.meta.env.DEV && import.meta.env.VITE_PORTFOLIO_MOCK === '1'

async function call(method: 'GET' | 'POST' | 'DELETE', path: string, body?: unknown): Promise<unknown> {
  if (PORTFOLIO_MOCK) {
    const { portfolioMock } = await import('./portfolio.fixture')
    return portfolioMock().handle(method, path, body)
  }
  if (method === 'GET') return api.get<unknown>(path)
  if (method === 'DELETE') return api.delete<unknown>(path)
  return api.post<unknown>(path, body)
}

const BASE = '/api/Portfolio'

/**
 * Sends a token form's fields and forgets them: no mutation (its cache keeps
 * the variables for minutes), no state. The caller reads the inputs on submit
 * and clears the form.
 */
export async function submitPortfolioToken(broker: string, fields: Record<string, string>): Promise<PortfolioConnection | null> {
  const answer = await call('POST', `${BASE}/connections/${encodeURIComponent(broker)}/token`, { fields })
  return readConnection(answer)
}

/** 404 never retries (it is an answer here); 401/403 never do, as everywhere. */
function retry(failureCount: number, error: unknown): boolean {
  const status = (error as { status?: number } | null)?.status
  if (status === 401 || status === 403 || status === 404 || status === 429) return false
  return failureCount < 2
}

/** Every Portfolio query key starts here, with the user it belongs to. */
export function portfolioKey(userId: number | null | undefined, ...rest: (string | number)[]): (string | number)[] {
  return ['portfolio', userId ?? 'anon', ...rest]
}

function useUserId(): number | null {
  return useAuth().user?.id ?? null
}

/** The linked accounts, once a minute: a link's expiry passes while the page is open. */
export function usePortfolioConnections() {
  const userId = useUserId()
  return useQuery({
    queryKey: portfolioKey(userId, 'connections'),
    queryFn: async () => readConnections(await call('GET', `${BASE}/connections`)),
    enabled: userId != null,
    refetchInterval: 60_000,
    retry,
  })
}

/** The report from the latest snapshots; its 404 is "nothing synced yet", not a failure. */
export function usePortfolioReport(enabled = true) {
  const userId = useUserId()
  return useQuery({
    queryKey: portfolioKey(userId, 'report'),
    queryFn: async (): Promise<ReportAnswer> => {
      try {
        return { kind: 'report', report: readReport(await call('GET', `${BASE}/report`)) }
      } catch (error) {
        const message = nothingSynced(error)
        if (message != null) return { kind: 'none', message }
        throw error
      }
    },
    enabled: enabled && userId != null,
    retry,
  })
}

export function usePortfolioHistory(days = 90, enabled = true) {
  const userId = useUserId()
  return useQuery({
    queryKey: portfolioKey(userId, 'history', days),
    queryFn: async () => readHistory(await call('GET', `${BASE}/history?days=${days}`)),
    enabled: enabled && userId != null,
    retry,
  })
}

/** Everything the section shows, read again after a link, an unlink or a sync. */
export function useInvalidatePortfolio() {
  const qc = useQueryClient()
  const userId = useUserId()
  return () => qc.invalidateQueries({ queryKey: portfolioKey(userId) })
}

export function useStartConnect() {
  return useMutation({
    mutationFn: async (broker: string) => readConnectStart(await call('POST', `${BASE}/connections/${encodeURIComponent(broker)}/start`)),
  })
}

export function useDisconnect() {
  const invalidate = useInvalidatePortfolio()
  return useMutation({
    mutationFn: async ({ broker, forget }: { broker: string; forget: boolean }) => {
      await call('DELETE', `${BASE}/connections/${encodeURIComponent(broker)}?forget=${forget}`)
    },
    onSettled: () => void invalidate(),
  })
}

export function useSyncPortfolio() {
  const invalidate = useInvalidatePortfolio()
  return useMutation({
    mutationFn: async () => readSyncResult(await call('POST', `${BASE}/sync`)),
    onSettled: () => void invalidate(),
  })
}
