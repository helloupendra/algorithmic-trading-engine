/**
 * Candle-pattern alerts: the API's shapes and the page's pure rules.
 *
 * Kept out of the page so the decisions that can be wrong in a way nobody
 * notices — "is the scanner actually running", "which rows match this filter",
 * "what does this rule watch" — are testable without rendering anything.
 */

export type PatternDirection = 'bullish' | 'bearish' | 'neutral'

export interface PatternInfo {
  key: string
  name: string
  direction: PatternDirection
  bars: number
  suggests: string
  definition: string
  portedFrom: string | null
}

export interface PatternGroup {
  key: string
  label: string
  description: string
}

export interface PatternCatalog {
  patterns: PatternInfo[]
  groups: PatternGroup[]
  timeframes: number[]
}

export interface PatternRule {
  id: number
  name: string
  symbols: string[]
  groups: string[]
  timeframes: number[]
  patterns: string[]
  isEnabled: boolean
  notify: boolean
  updatedBy: string | null
  updatedUtc: string
  resolvedSymbols: string[]
}

export interface SavePatternRule {
  name: string
  symbols: string[]
  groups: string[]
  timeframes: number[]
  patterns: string[]
  isEnabled: boolean
  notify: boolean
}

export interface PatternHit {
  key: string
  name: string
  direction: PatternDirection
}

export interface PatternAlert {
  id: number
  occurredUtc: string
  symbol: string
  displayName: string
  timeframe: number
  pattern: string
  patternName: string
  direction: PatternDirection
  barStartUtc: string
  barEndUtc: string
  open: number
  high: number
  low: number
  close: number
  minutesInBar: number
  minutesExpected: number
  title: string
  message: string
  deliveredToTelegram: boolean
  notify: boolean
  notifySkippedReason: string | null
  rules: string[]
}

export interface PatternForming {
  symbol: string
  displayName: string
  timeframe: number
  exchange: string
  inSession: boolean
  barStartUtc: string | null
  barEndUtc: string | null
  open: number | null
  high: number | null
  low: number | null
  close: number | null
  minutesElapsed: number
  minutesExpected: number
  minutesWithData: number
  wouldBe: PatternHit[]
  lastClosed: PatternHit[]
  lastClosedStartUtc: string | null
}

export interface PatternFormingResponse {
  asOfUtc: string
  items: PatternForming[]
}

export interface PatternSymbolStatus {
  symbol: string
  displayName: string
  exchange: string
  inSession: boolean
  barsToday: number
  lastBarUtc: string | null
  problem: string | null
}

export interface PatternScannerStatus {
  enabled: boolean
  intervalSeconds: number
  startedUtc: string
  lastScanUtc: string | null
  lastScanMilliseconds: number | null
  lastErrorUtc: string | null
  lastError: string | null
  ruleCount: number
  watchCount: number
  closedCandlesRead: number
  symbols: PatternSymbolStatus[]
  unresolved: string[]
  telegramConfigured: boolean
  telegramMaxMessages: number
  telegramWindowMinutes: number
  lastTelegramUtc: string | null
  telegramMessagesSent: number
  telegramMessagesSuppressed: number
  telegramMessagesFailed: number
  lastTelegramProblem: string | null
  alertsToday: number
  deliveredToday: number
  todayByPattern: { key: string; count: number }[]
  todayByTimeframe: { key: string; count: number }[]
}

export type Tone = 'pos' | 'neg' | 'warn' | 'neutral' | 'accent'

export function directionTone(direction: string): Tone {
  if (direction === 'bullish') return 'pos'
  if (direction === 'bearish') return 'neg'
  return 'neutral'
}

/** "10:30" in IST. */
export function istClock(iso: string | null | undefined): string {
  if (!iso) return '—'
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return '—'
  return d.toLocaleTimeString('en-GB', { timeZone: 'Asia/Kolkata', hour: '2-digit', minute: '2-digit', hour12: false })
}

/** "10:30–10:45" in IST. */
export function candleSpan(startIso: string | null | undefined, endIso: string | null | undefined): string {
  return `${istClock(startIso)}–${istClock(endIso)}`
}

/** What either scanner (patterns or indicators) says about its own loop. */
export type ScannerClock = Pick<PatternScannerStatus, 'enabled' | 'intervalSeconds' | 'lastScanUtc' | 'lastErrorUtc' | 'lastError'>

/**
 * Whether the scanner is doing its job, from its own status. A scan older than
 * three intervals is "stalled", never "running": the page must not show green
 * for a loop that stopped.
 */
export function scannerHealth(
  status: ScannerClock,
  nowMs: number,
  offDetail = 'PatternAlerts:Enabled is false on this API.',
): { label: string; tone: Tone; detail: string } {
  if (!status.enabled) {
    return { label: 'Off', tone: 'warn', detail: offDetail }
  }
  const last = status.lastScanUtc ? new Date(status.lastScanUtc).getTime() : null
  const errorAt = status.lastErrorUtc ? new Date(status.lastErrorUtc).getTime() : null
  if (errorAt !== null && (last === null || errorAt > last)) {
    return { label: 'Failing', tone: 'neg', detail: status.lastError ?? 'The last scan failed.' }
  }
  if (last === null) {
    return { label: 'Starting', tone: 'neutral', detail: 'No scan has finished since the API started.' }
  }
  const age = Math.max(0, Math.round((nowMs - last) / 1000))
  if (age > status.intervalSeconds * 3) {
    return { label: 'Stalled', tone: 'neg', detail: `Last scan ${age}s ago; it runs every ${status.intervalSeconds}s.` }
  }
  return { label: 'Running', tone: 'pos', detail: `Last scan ${age}s ago, every ${status.intervalSeconds}s.` }
}

export interface AlertFilters {
  symbol: string
  timeframe: string
  pattern: string
  direction: string
}

export const NO_FILTERS: AlertFilters = { symbol: '', timeframe: '', pattern: '', direction: '' }

export function filterAlerts(alerts: PatternAlert[], f: AlertFilters): PatternAlert[] {
  return alerts.filter(
    (a) =>
      (!f.symbol || a.symbol === f.symbol) &&
      (!f.timeframe || String(a.timeframe) === f.timeframe) &&
      (!f.pattern || a.pattern === f.pattern) &&
      (!f.direction || a.direction === f.direction),
  )
}

/** The choices each filter offers: only values that occur today, so no filter can select nothing. */
export function filterChoices(alerts: PatternAlert[]) {
  const symbols = new Map<string, string>()
  const patterns = new Map<string, string>()
  const timeframes = new Set<number>()
  for (const a of alerts) {
    symbols.set(a.symbol, a.displayName)
    patterns.set(a.pattern, a.patternName)
    timeframes.add(a.timeframe)
  }
  const byLabel = (x: [string, string], y: [string, string]) => x[1].localeCompare(y[1])
  return {
    symbols: [...symbols.entries()].sort(byLabel).map(([value, label]) => ({ value, label })),
    patterns: [...patterns.entries()].sort(byLabel).map(([value, label]) => ({ value, label })),
    timeframes: [...timeframes].sort((a, b) => a - b),
  }
}

/** "NSE:SBIN-EQ, nse:infy-eq\nMCX:CRUDEOIL26OCTFUT" → upper-cased, de-duplicated, in order. */
export function parseList(text: string): string[] {
  const seen = new Set<string>()
  for (const part of text.split(/[\s,]+/)) {
    const v = part.trim().toUpperCase()
    if (v) seen.add(v)
  }
  return [...seen]
}

export const FUTURE_PREFIX = 'future:'

/** Splits a rule's groups into the fixed ones and the "nearest future of" underlyings. */
export function splitGroups(groups: string[]): { fixed: string[]; futures: string[] } {
  const fixed: string[] = []
  const futures: string[] = []
  for (const g of groups) {
    if (g.toLowerCase().startsWith(FUTURE_PREFIX)) futures.push(g.slice(FUTURE_PREFIX.length).toUpperCase())
    else fixed.push(g)
  }
  return { fixed, futures }
}

export interface RuleForm {
  id: number | null
  name: string
  groups: string[]
  futures: string
  symbols: string
  timeframes: number[]
  patterns: string[]
  isEnabled: boolean
  notify: boolean
}

export const EMPTY_RULE_FORM: RuleForm = {
  id: null,
  name: '',
  groups: [],
  futures: '',
  symbols: '',
  timeframes: [15],
  patterns: [],
  isEnabled: true,
  notify: true,
}

export function ruleToForm(rule: PatternRule): RuleForm {
  const { fixed, futures } = splitGroups(rule.groups)
  return {
    id: rule.id,
    name: rule.name,
    groups: fixed,
    futures: futures.join(', '),
    symbols: rule.symbols.join(', '),
    timeframes: [...rule.timeframes],
    patterns: [...rule.patterns],
    isEnabled: rule.isEnabled,
    notify: rule.notify,
  }
}

export function formToRequest(form: RuleForm): SavePatternRule {
  return {
    name: form.name.trim(),
    symbols: parseList(form.symbols),
    groups: [...form.groups, ...parseList(form.futures).map((u) => `${FUTURE_PREFIX}${u}`)],
    timeframes: [...form.timeframes].sort((a, b) => a - b),
    patterns: [...form.patterns],
    isEnabled: form.isEnabled,
    notify: form.notify,
  }
}

/** "Indices, CRUDEOIL future · 5m, 15m · all 13 patterns". */
export function ruleSummary(rule: PatternRule, catalog: PatternCatalog | undefined): string {
  const { fixed, futures } = splitGroups(rule.groups)
  const label = (key: string) => catalog?.groups.find((g) => g.key === key)?.label ?? key
  const what = [
    ...fixed.map(label),
    ...futures.map((u) => `${u} nearest future`),
    ...(rule.symbols.length ? [`${rule.symbols.length} symbol${rule.symbols.length === 1 ? '' : 's'}`] : []),
  ]
  const total = catalog?.patterns.length
  const patterns =
    total && rule.patterns.length === total ? 'every pattern' : `${rule.patterns.length} pattern${rule.patterns.length === 1 ? '' : 's'}`
  return `${what.join(', ') || 'nothing'} · ${rule.timeframes.map((t) => `${t}m`).join(', ')} · ${patterns}`
}

/** Toggle membership, keeping the catalog's order when one is given. */
export function toggle<T>(list: T[], value: T, order?: T[]): T[] {
  const next = list.includes(value) ? list.filter((x) => x !== value) : [...list, value]
  return order ? order.filter((x) => next.includes(x)) : next
}

// --------------------------------------------------------- indicator alerts --
//
// RSI, EMA crosses, Supertrend and VWAP on the same live candles, with their
// rules in config/indicator-alerts.txt on the server. The page reads that file
// back through the API; it does not edit it.

export interface IndicatorRuleInfo {
  key: string
  name: string
  label: string
  definition: string
  syntax: string
  settleCandles: number
  needsVolume: boolean
}

export interface IndicatorLine {
  number: number
  text: string
  symbols: string[]
  groups: string[]
  timeframes: number[]
  rules: IndicatorRuleInfo[]
  pageOnly: boolean
  resolvedSymbols: string[]
}

export type IndicatorRuleStateName = 'ready' | 'warming up' | 'waiting' | 'skipped'

export interface IndicatorRuleState {
  rule: string
  label: string
  state: IndicatorRuleStateName
  detail: string | null
}

export interface IndicatorWatch {
  symbol: string
  displayName: string
  timeframe: number
  exchange: string
  inSession: boolean
  historyCandles: number
  todayCandles: number
  lastBarUtc: string | null
  rules: IndicatorRuleState[]
  problem: string | null
}

/**
 * What the indicator status line says of Telegram, and which chat the
 * messages reach as the server reports it. The server sends them to its
 * system chat (the Desk System channel) and, when no system chat is set, to
 * the trades chat. The line read "Desk System channel" whatever the server
 * was doing, so a box without a system chat sent every indicator batch among
 * the trades while the page said otherwise. An API from before 28 Sep does
 * not report it, and then no chat is named.
 */
export function indicatorTelegramNote(
  s: Pick<IndicatorAlertsStatus, 'telegram' | 'telegramConfigured' | 'telegramSystemChatConfigured'>,
): { text: string; title: string | null } {
  if (!s.telegram) return { text: 'off in the file', title: null }
  if (!s.telegramConfigured) return { text: 'on, but no bot is configured on this server', title: null }
  if (s.telegramSystemChatConfigured === true) return { text: 'on (Desk System channel)', title: null }
  if (s.telegramSystemChatConfigured === false) {
    return {
      text: 'on (trades channel: no Desk System chat is set on this server)',
      title: 'Telegram:SystemChatId is not set, so the server sends its system messages, these included, to the trades chat.',
    }
  }
  return {
    text: 'on',
    title: "Sent to the server's system Telegram chat (Telegram:SystemChatId), or to its trades chat when no system chat is set.",
  }
}

export interface IndicatorAlertsStatus {
  enabled: boolean
  intervalSeconds: number
  file: string | null
  fileModifiedUtc: string | null
  searched: string[]
  fileError: string | null
  cooldownMinutes: number
  telegram: boolean
  warmupCandles: number
  warnings: string[]
  lines: IndicatorLine[]
  catalog: IndicatorRuleInfo[]
  startedUtc: string
  lastScanUtc: string | null
  lastScanMilliseconds: number | null
  lastErrorUtc: string | null
  lastError: string | null
  watches: IndicatorWatch[]
  unresolved: string[]
  telegramConfigured: boolean
  /**
   * Whether the server has a system chat (Telegram:SystemChatId): indicator
   * alerts go there, else to the trades chat. Absent on API builds from
   * before 28 Sep.
   */
  telegramSystemChatConfigured?: boolean
  telegramMaxMessages: number
  telegramWindowMinutes: number
  lastTelegramUtc: string | null
  telegramMessagesSent: number
  telegramMessagesSuppressed: number
  telegramMessagesFailed: number
  lastTelegramProblem: string | null
  alertsToday: number
  deliveredToday: number
}

export interface IndicatorAlert {
  id: number
  occurredUtc: string
  symbol: string
  displayName: string
  timeframe: number
  rule: string
  ruleName: string
  what: string
  direction: 'up' | 'down'
  barStartUtc: string
  barEndUtc: string
  close: number
  minutesInBar: number
  minutesExpected: number
  values: Record<string, number>
  title: string
  message: string
  deliveredToTelegram: boolean
  notify: boolean
  notifySkippedReason: string | null
  cooledDown: boolean
}

/**
 * Only a rule that is not yet able to alert is coloured: a table of every rule
 * in green says nothing, and hides the one still warming up.
 */
export function ruleStateTone(state: string): Tone {
  return state === 'warming up' ? 'warn' : 'neutral'
}

/** Up/down is which way a line was crossed, not advice: coloured like a price change. */
export function crossTone(direction: string): Tone {
  return direction === 'up' ? 'pos' : direction === 'down' ? 'neg' : 'neutral'
}

const level = (v: number | undefined) =>
  v === undefined ? '—' : v.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
const oscillator = (v: number | undefined) => (v === undefined ? '—' : v.toFixed(1))

/** The numbers behind an alert, short: "RSI 68.4 → 71.2", "EMA 25,061.30 / 25,058.90". */
export function indicatorNumbers(alert: Pick<IndicatorAlert, 'ruleName' | 'values'>): string {
  const v = alert.values
  switch (alert.ruleName) {
    case 'rsi-above':
    case 'rsi-below':
      return `RSI ${oscillator(v.rsiBefore)} → ${oscillator(v.rsi)}`
    case 'ema-cross':
      return `EMA ${level(v.emaFast)} / ${level(v.emaSlow)}`
    case 'supertrend-flip':
      // The band the close went through, then where the new line starts.
      return `band ${level(v.through)} · line ${level(v.line)}`
    case 'vwap-cross':
      return `VWAP ${level(v.vwap)}`
    default:
      return Object.entries(v)
        .map(([k, x]) => `${k} ${x}`)
        .join(', ')
  }
}

/**
 * The watches with something to say first — a feed problem, then a rule still
 * warming up — so the table answers "will the next alert be right?" at a glance.
 */
export function sortWatches(watches: IndicatorWatch[]): IndicatorWatch[] {
  const rank = (w: IndicatorWatch) => (w.problem ? 0 : w.rules.some((r) => r.state === 'warming up') ? 1 : 2)
  return [...watches].sort(
    (a, b) => rank(a) - rank(b) || a.displayName.localeCompare(b.displayName) || a.timeframe - b.timeframe,
  )
}
