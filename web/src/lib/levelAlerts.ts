/**
 * Level alerts: the API's shapes (LevelAlertsController) and the page's pure
 * rules.
 *
 * Kept out of the page so the sentences a trader acts on are pinned by tests:
 * "NIFTY is already above 24,800, so this fires only after it comes back
 * down and crosses again", the price a repeating rule re-arms at, and what
 * the top bar says (and when it says nothing).
 */

export type LevelKind = 'price' | 'pcr'
export type LevelCondition = 'above' | 'below' | 'touch'
export type LevelStatus = 'active' | 'paused' | 'fired' | 'expired'
export type ReadingState = 'live' | 'stale' | 'no-quote' | 'no-chain' | 'no-call-oi' | 'closed' | 'unknown-exchange'

export interface LevelReading {
  kind: LevelKind
  target: string
  displayName: string
  exchange: string
  inSession: boolean
  value: number | null
  asOfUtc: string | null
  state: ReadingState
  detail: string | null
  context: string | null
}

export interface LevelAlertRule {
  id: number
  userId: number
  ownerUserName: string | null
  kind: LevelKind
  target: string
  displayName: string
  condition: LevelCondition
  level: number
  description: string
  repeat: boolean
  rearmBy: number | null
  rearmUnit: 'percent' | 'points'
  note: string | null
  expiresOn: string | null
  notify: boolean
  telegramApplies: boolean
  status: LevelStatus
  armed: boolean
  lastFiredUtc: string | null
  fireCount: number
  createdUtc: string
  updatedUtc: string
  now: LevelReading | null
  side: 'above' | 'below' | 'at' | null
}

export interface SaveLevelAlertRule {
  kind: LevelKind
  target: string
  condition: LevelCondition
  level: number
  repeat: boolean
  rearmBy: number | null
  note: string | null
  expiresOn: string | null
  notify: boolean
  active: boolean
}

export interface LevelAlertFire {
  id: number
  ruleId: number
  userId: number
  ownerUserName: string | null
  firedUtc: string
  kind: LevelKind
  target: string
  displayName: string
  condition: LevelCondition
  level: number
  value: number
  valueAsOfUtc: string
  previousValue: number
  previousAsOfUtc: string
  title: string
  message: string
  deliveredToTelegram: boolean
  telegramNote: string | null
  seen: boolean
}

export interface LevelAlertSummary {
  unseen: number
  firedToday: number
  newestUtc: string | null
  newestTitle: string | null
}

export interface LevelAlertStatus {
  enabled: boolean
  intervalSeconds: number
  pcrIntervalSeconds: number
  priceFreshSeconds: number
  pcrFreshSeconds: number
  startedUtc: string
  lastScanUtc: string | null
  lastScanMilliseconds: number | null
  lastErrorUtc: string | null
  lastError: string | null
  activeRules: number
  maxRulesPerUser: number
  defaultPriceRearmPercent: number
  defaultPcrRearmPoints: number
  watching: LevelReading[]
  skipped: string[]
  telegramConfigured: boolean
  telegramSystemChatConfigured: boolean
  telegramMaxMessages: number
  telegramWindowMinutes: number
  telegramMessagesSent: number
  telegramMessagesSuppressed: number
  telegramMessagesFailed: number
  lastTelegramProblem: string | null
}

/** The page's base URL; a link here can carry ?kind=&target=&level= to open the form filled in. */
export const LEVEL_ALERTS_PATH = '/markets/alerts'

/** The conditions a kind offers, in the words the form shows. Touch is for prices: a ratio is not a price that trades at a level. */
export function conditionsFor(kind: LevelKind): { value: LevelCondition; label: string }[] {
  return kind === 'pcr'
    ? [
        { value: 'above', label: 'Goes above' },
        { value: 'below', label: 'Goes below' },
      ]
    : [
        { value: 'above', label: 'Crosses above' },
        { value: 'below', label: 'Crosses below' },
        { value: 'touch', label: 'Touches' },
      ]
}

/** A price in the market's grouping (24,812.35), a PCR to the chain page's two decimals. */
export function formatLevel(kind: LevelKind, value: number | null | undefined): string {
  if (value == null || Number.isNaN(value)) return '—'
  return kind === 'pcr' ? value.toFixed(2) : value.toLocaleString('en-IN', { maximumFractionDigits: 2 })
}

// ------------------------------------------------------------------ form --

export interface LevelForm {
  id: number | null
  kind: LevelKind
  target: string
  condition: LevelCondition
  /** As typed: the form keeps text until it is saved. */
  level: string
  repeat: boolean
  rearmBy: string
  note: string
  /** yyyy-MM-dd, or '' for no last day. */
  expiresOn: string
  notify: boolean
  active: boolean
}

export const EMPTY_FORM: LevelForm = {
  id: null,
  kind: 'price',
  target: '',
  condition: 'above',
  level: '',
  repeat: false,
  rearmBy: '',
  note: '',
  expiresOn: '',
  notify: true,
  active: true,
}

/**
 * The form a link opens: the chain's Alert button sends ?kind=pcr&target=NIFTY&level=1.32,
 * the watchlist's ?target=NSE:SBIN-EQ&level=801.5. Null when the link asks for nothing.
 */
export function formFromLink(params: URLSearchParams): LevelForm | null {
  const target = (params.get('target') ?? '').trim().toUpperCase()
  if (!target) return null
  const kind: LevelKind = params.get('kind') === 'pcr' ? 'pcr' : 'price'
  const level = Number(params.get('level'))
  return {
    ...EMPTY_FORM,
    kind,
    target,
    level: Number.isFinite(level) && level > 0 ? String(kind === 'pcr' ? Number(level.toFixed(2)) : level) : '',
  }
}

/** A link that opens the form on this target, at this level when one is given. */
export function alertLink(kind: LevelKind, target: string, level?: number | null): string {
  const q = new URLSearchParams({ kind, target })
  if (level != null && Number.isFinite(level) && level > 0) q.set('level', String(kind === 'pcr' ? Number(level.toFixed(2)) : level))
  return `${LEVEL_ALERTS_PATH}?${q.toString()}`
}

export function ruleToForm(rule: LevelAlertRule): LevelForm {
  return {
    id: rule.id,
    kind: rule.kind,
    target: rule.target,
    condition: rule.condition,
    level: String(rule.level),
    repeat: rule.repeat,
    rearmBy: rule.rearmBy == null ? '' : String(rule.rearmBy),
    note: rule.note ?? '',
    expiresOn: rule.expiresOn ?? '',
    notify: rule.notify,
    // Saving a fired one-shot rule as active arms it again; that is what "edit and re-arm" means.
    active: rule.status !== 'paused',
  }
}

/** A positive number, or null: "24,800" and "24800" are the same level; "abc" and "-1" are none. */
export function parseLevel(text: string): number | null {
  const n = Number(text.replace(/,/g, '').trim())
  return text.trim() !== '' && Number.isFinite(n) && n > 0 ? n : null
}

/** What is wrong with the form before it is sent. The API checks again and has the last word. */
export function formProblems(form: LevelForm): string[] {
  const problems: string[] = []
  if (!form.target.trim()) problems.push(form.kind === 'pcr' ? 'Pick an underlying.' : 'Pick a symbol.')
  if (parseLevel(form.level) == null) problems.push('The level must be a number above zero.')
  if (form.kind === 'pcr' && form.condition === 'touch') problems.push('A PCR rule goes above or below a level.')
  if (form.repeat && form.rearmBy.trim() !== '' && parseLevel(form.rearmBy) == null)
    problems.push('The re-arm distance must be a number above zero, or blank for the default.')
  return problems
}

export function formToRequest(form: LevelForm): SaveLevelAlertRule {
  return {
    kind: form.kind,
    target: form.target.trim().toUpperCase(),
    condition: form.kind === 'pcr' && form.condition === 'touch' ? 'above' : form.condition,
    level: parseLevel(form.level) ?? 0,
    repeat: form.repeat,
    rearmBy: form.repeat ? parseLevel(form.rearmBy) : null,
    note: form.note.trim() || null,
    expiresOn: form.expiresOn || null,
    notify: form.notify,
    active: form.active,
  }
}

/**
 * Where a repeating rule arms again, in the value's own units: back to
 * 24,750.40 for "above 24,800" with 0.2%, to 1.25 for "PCR above 1.30" with
 * 0.05. Null when the form has no level yet.
 */
export function rearmAt(kind: LevelKind, condition: LevelCondition, level: number | null, rearmBy: number | null, defaults: { price: number; pcr: number }): string | null {
  if (level == null) return null
  const gap = kind === 'pcr' ? (rearmBy ?? defaults.pcr) : (level * (rearmBy ?? defaults.price)) / 100
  const round = (v: number) => Math.round(v * 100) / 100
  if (condition === 'above') return `it is back at or below ${formatLevel(kind, round(level - gap))}`
  if (condition === 'below') return `it is back at or above ${formatLevel(kind, round(level + gap))}`
  return `it has moved ${formatLevel(kind, round(gap))} away from the level`
}

/**
 * The line beside the form and on each rule: where the value is against the
 * level, and what that means for when it fires. A cross needs the value on
 * the near side first, which is the thing nobody expects.
 */
export function sideNote(kind: LevelKind, condition: LevelCondition, level: number | null, reading: LevelReading | null | undefined): string | null {
  if (level == null || !reading || reading.value == null) return null
  const now = formatLevel(kind, reading.value)
  const at = formatLevel(kind, level)
  const what = kind === 'pcr' ? 'the PCR' : 'the price'
  if (condition === 'above' && reading.value > level)
    return `Now ${now}, already above ${at}: it fires only after ${what} has been at or below ${at} and goes above again.`
  if (condition === 'below' && reading.value < level)
    return `Now ${now}, already below ${at}: it fires only after ${what} has been at or above ${at} and goes below again.`
  if (condition === 'touch' && reading.value === level) return `Now exactly ${at}: it fires on the next move that comes back to it.`
  const distance = Math.abs(reading.value - level)
  const pct = kind === 'price' && reading.value > 0 ? ` (${((distance / reading.value) * 100).toFixed(2)}%)` : ''
  return `Now ${now}, ${formatLevel(kind, Math.round(distance * 100) / 100)}${pct} from ${at}.`
}

/** "24,812.35 · 4s ago" for a live reading; otherwise why there is no number to compare. */
export function readingLine(reading: LevelReading | null | undefined, nowMs: number): { text: string; tone: 'pos' | 'warn' | 'neutral' } {
  if (!reading) return { text: 'not read yet: the service reads a target while an active rule watches it', tone: 'neutral' }
  const value = formatLevel(reading.kind, reading.value)
  const age = reading.asOfUtc ? ageText(nowMs - new Date(reading.asOfUtc).getTime()) : null
  switch (reading.state) {
    case 'live':
      return { text: `${value}${age ? ` · ${age}` : ''}${reading.context ? ` · ${reading.context}` : ''}`, tone: 'pos' }
    case 'stale':
    case 'closed':
      return {
        text: `${reading.value != null ? `${value} · ` : ''}${reading.detail ?? reading.state}`,
        tone: reading.state === 'closed' ? 'neutral' : 'warn',
      }
    default:
      return { text: reading.detail ?? reading.state, tone: 'warn' }
  }
}

function ageText(ms: number): string {
  if (Number.isNaN(ms)) return ''
  const s = Math.max(0, Math.round(ms / 1000))
  if (s < 60) return `${s}s ago`
  const m = Math.floor(s / 60)
  return m < 60 ? `${m} min ago` : `${Math.floor(m / 60)} h ago`
}

export function statusTone(status: LevelStatus): 'pos' | 'neutral' | 'accent' | 'warn' {
  switch (status) {
    case 'active':
      return 'pos'
    case 'fired':
      return 'accent'
    case 'expired':
      return 'warn'
    default:
      return 'neutral'
  }
}

/** A rule's state in a few words: "watching", "fired 3× · re-arming", "paused". */
export function statusLine(rule: Pick<LevelAlertRule, 'status' | 'repeat' | 'armed' | 'fireCount'>): string {
  const fired = rule.fireCount > 0 ? `fired ${rule.fireCount}×` : null
  if (rule.status === 'active') {
    if (rule.repeat && !rule.armed) return [fired, 'waiting to re-arm'].filter(Boolean).join(' · ')
    return [rule.repeat ? 'watching, repeats' : 'watching', fired].filter(Boolean).join(' · ')
  }
  if (rule.status === 'fired') return 'fired, done'
  if (rule.status === 'expired') return 'past its last day'
  return 'paused'
}

/** Where a fire went: Telegram, or why it stayed in the console. */
export function deliveryLine(fire: Pick<LevelAlertFire, 'deliveredToTelegram' | 'telegramNote'>): string {
  if (fire.deliveredToTelegram) return 'Console and Telegram'
  return fire.telegramNote ? `Console · ${fire.telegramNote}` : 'Console'
}

/**
 * The top bar's badge, or null to show nothing: nothing new is not news, and
 * a summary that has not answered is not a zero.
 */
export function alertsBadge(summary: LevelAlertSummary | undefined): { count: string; title: string } | null {
  if (!summary || summary.unseen <= 0) return null
  const count = summary.unseen > 99 ? '99+' : String(summary.unseen)
  const title = `${summary.unseen} new level alert${summary.unseen === 1 ? '' : 's'}${summary.newestTitle ? `. Newest: ${summary.newestTitle}` : ''}`
  return { count, title }
}
