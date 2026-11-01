/**
 * Data → Mutual funds: what the desk holds of AMFI's NAVs (api/MutualFunds/coverage).
 * The types mirror AlgoTrading.Contracts.MutualFunds; the helpers are pure, so
 * the page's wording is tested without a browser.
 */

export interface MutualFundCount {
  key: string
  schemes: number
  /** Schemes with a NAV within 30 days of the newest NAV day. */
  active: number
}

export interface MutualFundNavCoverage {
  rows: number
  rowsApproximate: boolean
  firstDate: string | null
  lastDate: string | null
  /** The newest day with NAVs for most schemes (a Sunday with only liquid funds' NAVs is not one). */
  latestFullDate: string | null
  /** The newest business day whose NAVs should be stored by now (NSE calendar). */
  expectedDate: string
  businessDaysBehind: number | null
}

export interface MutualFundSync {
  trigger: string
  startedUtc: string
  finishedUtc: string
  ok: boolean
  message: string
  asOfDate: string | null
  rows: number
  newSchemes: number
  navRowsWritten: number
}

export interface MutualFundBackfill {
  enabled: boolean
  requested: boolean
  running: boolean
  /** "idle", "queued", "waiting", "running", "paused", "failed" or "done". */
  state: string
  now: string | null
  from: string
  top: string | null
  cursor: string | null
  windowsDone: number
  windowsLeft: number
  rowsWritten: number
  maxWindows: number | null
  lastWindow: string | null
  lastError: string | null
  startedUtc: string | null
  updatedUtc: string | null
}

export interface MutualFundCoverage {
  serverUtc: string
  schemes: number
  activeSchemes: number
  byPlan: MutualFundCount[]
  byOption: MutualFundCount[]
  byCategory: MutualFundCount[]
  byAmc: MutualFundCount[]
  nav: MutualFundNavCoverage
  lastSync: MutualFundSync | null
  syncEnabled: boolean
  syncRunning: boolean
  backfill: MutualFundBackfill
}

export type Tone = 'pos' | 'warn' | 'neg' | 'neutral'

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']

/** "2026-10-30" → "30 Oct 2026"; the API's dates are IST days, so no time zone is involved. */
export function formatDay(iso: string | null | undefined): string {
  const m = iso ? /^(\d{4})-(\d{2})-(\d{2})/.exec(iso) : null
  if (!m) return '—'
  return `${Number(m[3])} ${MONTHS[Number(m[2]) - 1]} ${m[1]}`
}

/**
 * How current the NAVs are, against the business day due by now. Two business
 * days without a new NAV day is what the desk checkup flags too.
 */
export function freshness(nav: MutualFundNavCoverage): { tone: Tone; label: string; detail: string } {
  if (!nav.latestFullDate || nav.businessDaysBehind == null) {
    return { tone: 'warn', label: 'No NAVs', detail: 'Nothing stored yet: the first sync runs a few minutes after the API starts.' }
  }
  const behind = nav.businessDaysBehind
  if (behind <= 0) {
    return { tone: 'pos', label: 'Up to date', detail: `NAVs to ${formatDay(nav.latestFullDate)}, the business day due.` }
  }
  const days = behind === 1 ? '1 business day' : `${behind} business days`
  return {
    tone: behind >= 2 ? 'neg' : 'warn',
    label: `${days} behind`,
    detail: `NAVs to ${formatDay(nav.latestFullDate)}; ${formatDay(nav.expectedDate)}'s were due.`,
  }
}

const PLAN_LABELS: Record<string, string> = { direct: 'Direct', regular: 'Regular', unknown: 'Not stated' }
const OPTION_LABELS: Record<string, string> = { growth: 'Growth', idcw: 'IDCW', other: 'Other (bonus, unclaimed)', unknown: 'Not stated' }

export const planLabel = (key: string) => PLAN_LABELS[key] ?? key
export const optionLabel = (key: string) => OPTION_LABELS[key] ?? key

/**
 * AMFI's section line split for a table: "Open Ended Schemes(Equity Scheme - Large Cap Fund)"
 * → type "Open Ended", category "Equity Scheme - Large Cap Fund".
 */
export function splitCategory(category: string): { type: string; name: string } {
  const m = /^(.*?)\s*Schemes?\s*\((.*)\)$/.exec(category)
  if (!m) return { type: '', name: category || '—' }
  return { type: m[1].trim(), name: m[2].trim() }
}

/** One line on the history backfill: how far it has come and whether it is moving. */
export function backfillSummary(b: MutualFundBackfill): { tone: Tone; label: string; detail: string } {
  const reach = b.cursor && b.top ? `${formatDay(b.cursor)} to ${formatDay(b.top)}` : null
  const progress = `${b.windowsDone} of ${b.windowsDone + b.windowsLeft} windows`
  if (!b.enabled) return { tone: 'neutral', label: 'Switched off', detail: 'MutualFunds:BackfillEnabled is false.' }
  switch (b.state) {
    case 'done':
      return { tone: 'pos', label: 'Complete', detail: reach ? `History from ${reach}.` : 'History complete.' }
    case 'running':
    case 'queued':
      return { tone: 'neutral', label: b.state === 'queued' ? 'Starting' : 'Running', detail: `${progress}${b.now ? `; reading ${b.now}` : ''}.` }
    case 'waiting':
      return { tone: 'neutral', label: 'Waiting for the close', detail: `${progress}; it does not run in market hours.` }
    case 'failed':
      return { tone: 'neg', label: 'Failed', detail: `${progress}; tries again within the hour. ${b.lastError ?? ''}`.trim() }
    case 'paused':
      return { tone: 'warn', label: 'Paused', detail: reach ? `${progress}; history from ${reach}.` : progress }
    default:
      return { tone: 'neutral', label: 'Not started', detail: 'Only today’s NAVs from the daily file. An admin starts the history backfill.' }
  }
}
