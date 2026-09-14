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

/**
 * Whether the scanner is doing its job, from its own status. A scan older than
 * three intervals is "stalled", never "running": the page must not show green
 * for a loop that stopped.
 */
export function scannerHealth(status: PatternScannerStatus, nowMs: number): { label: string; tone: Tone; detail: string } {
  if (!status.enabled) {
    return { label: 'Off', tone: 'warn', detail: 'PatternAlerts:Enabled is false on this API.' }
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
