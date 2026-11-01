/**
 * Data → Mutual funds: what the desk holds of AMFI's NAVs (api/MutualFunds/coverage)
 * and, from Phase 2, the fund report built on them (api/MutualFunds/{code}/report,
 * compare and summary). The types mirror AlgoTrading.Contracts.MutualFunds; the
 * helpers are pure, so the pages' wording is tested without a browser.
 *
 * Units, as the API sends them: every field named ...Pct is a percent (7.38 is
 * 7.38%), money is rupees, dates are IST days (yyyy-MM-dd). A null is a figure
 * that is not known, never a zero: it shows "—" with the API's reason beside it.
 */

import { formatInrSigned, formatInrWhole, formatNumber } from './format'

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
  /** What the fund report's jobs have collected: benchmarks, TER and the category ranking. */
  analytics: MutualFundAnalyticsCoverage
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

// ---------------------------------------------------------------------------
// Phase 2: the fund report (api/MutualFunds/{code}/report, compare, summary)
// ---------------------------------------------------------------------------

/** One scheme in a search (GET api/MutualFunds/schemes). */
export interface MutualFundScheme {
  schemeCode: number
  schemeName: string
  amc: string
  category: string
  /** "direct", "regular" or "unknown". */
  plan: string
  /** "growth", "idcw", "other" or "unknown". */
  option: string
  optionLabel: string | null
  isinGrowth: string | null
  isinReinvest: string | null
  firstNavDate: string | null
  lastNavDate: string | null
  lastNav: number | null
  isActive: boolean
}

/** Where one kind of figure comes from and how current it is. */
export interface MutualFundSource {
  /** "nav", "benchmark", "category", "ter", "risk-free". */
  key: string
  what: string
  source: string
  /** The newest day the figures use; null when nothing is collected. */
  asOf: string | null
  note: string | null
}

export interface MutualFundHistory {
  firstDate: string | null
  lastDate: string | null
  navs: number
  /** The stored history reaches the scheme's first NAV in AMFI's history (or April 2006). */
  complete: boolean
  note: string
}

export interface MutualFundCategoryPeriod {
  years: number
  /** 1 for the highest return; null when the scheme has no return for the period. */
  rank: number | null
  /** Peers with a return for the period, this scheme included. */
  peers: number
  returnPct: number | null
  medianPct: number | null
  highestPct: number | null
  lowestPct: number | null
}

export interface MutualFundPeriod {
  years: number
  start: string | null
  end: string
  /** Annualised. */
  cagrPct: number | null
  /** Not annualised. */
  absolutePct: number | null
  missing: string | null
  benchmarkCagrPct: number | null
  /** Fund CAGR minus benchmark CAGR, percentage points; the tracking difference for an index fund. */
  excessPct: number | null
  benchmarkMissing: string | null
}

export interface MutualFundCalendarYear {
  year: number
  start: string | null
  end: string | null
  returnPct: number | null
  benchmarkReturnPct: number | null
  /** The as-of year: from the last NAV of the year before to the as-of day. */
  yearToDate: boolean
  /** Why the fund's return for the year is not known. */
  missing: string | null
  /** Why the benchmark's return for the year is not known, when the fund's is. */
  benchmarkMissing: string | null
}

export interface MutualFundRolling {
  years: number
  windows: number
  firstEnd: string
  lastEnd: string
  lowestPct: number
  lowestEnd: string
  highestPct: number
  highestEnd: string
  medianPct: number
  /** Percent of windows with a return below zero. */
  belowZeroPct: number
  /** Windows with a benchmark return over the same dates. */
  benchmarkCompared: number | null
  /** Percent of those windows in which the fund's return was higher than the benchmark's. */
  aboveBenchmarkPct: number | null
}

export interface MutualFundSip {
  instalment: number
  instalments: number
  firstDate: string
  lastDate: string
  valuedOn: string
  invested: number
  units: number
  value: number
  gain: number
  xirrPct: number | null
}

export interface MutualFundLumpSum {
  amount: number
  start: string
  startNav: number
  valuedOn: string
  endNav: number
  value: number
  gain: number
  cagrPct: number
}

/** The ₹10,000 monthly SIP and the ₹2,00,000 lump sum over one period. */
export interface MutualFundExample {
  years: number
  sip: MutualFundSip | null
  lumpSum: MutualFundLumpSum | null
  missing: string | null
}

export interface MutualFundVolatility {
  years: number
  start: string
  end: string
  returns: number
  returnsPerYear: number
  annualisedPct: number
  sharpe: number | null
  sortino: number | null
}

export interface MutualFundRiskFree {
  ratePct: number
  source: string
  asOf: string | null
}

export interface MutualFundDrawdown {
  /** The window in years; null for the whole stored history. */
  years: number | null
  start: string
  end: string
  /** The fall, a negative percent. */
  maxDrawdownPct: number
  peak: string
  peakNav: number
  trough: string
  troughNav: number
  /** The first day the NAV was back at the peak; null when it has not been yet. */
  recovery: string | null
}

export interface MutualFundRisk {
  volatility: MutualFundVolatility | null
  /** Why there is no volatility, when there is none. */
  volatilityMissing: string | null
  riskFree: MutualFundRiskFree | null
  sharpeMissing: string | null
  drawdowns: MutualFundDrawdown[]
  /** The 3, 5 or 10-year windows left out, each worded with its window first ("10 years: not enough history …"). */
  drawdownsMissing: string[]
  /** The as-of NAV against the highest NAV stored, a negative percent; zero at a new high. */
  currentDrawdownPct: number | null
  highestNavDate: string | null
  highestNav: number | null
}

export interface MutualFundBeta {
  years: number
  start: string
  end: string
  returns: number
  beta: number
}

export interface MutualFundTrackingError {
  start: string
  end: string
  returns: number
  valuePct: number
}

export interface MutualFundCapture {
  start: string
  end: string
  months: number
  downMonths: number
  upMonths: number
  downsidePct: number | null
  upsidePct: number | null
}

export interface MutualFundBenchmark {
  index: string | null
  name: string | null
  /** "index-name", "category" or "none". */
  basis: string
  note: string
  collected: boolean
  firstDate: string | null
  lastDate: string | null
  lastCommonDate: string | null
  indexFund: boolean
  beta: MutualFundBeta | null
  trackingError: MutualFundTrackingError | null
  capture: MutualFundCapture | null
  missing: string[]
}

export interface MutualFundCategory {
  peerGroup: string | null
  /** "direct" or "regular": peers have the same plan, growth option. */
  plan: string | null
  asOf: string | null
  computedUtc: string | null
  periods: MutualFundCategoryPeriod[]
  /** What the comparison covers: the peers and how they are counted. */
  note: string
  /** Why there is no ranking at all, when there is none. */
  missing: string | null
}

export interface MutualFundTer {
  date: string
  terSchemeName: string
  regularPct: number | null
  directPct: number | null
  thisPlanPct: number | null
}

/** The direct and the regular plan's ₹ outcomes over one period, on the same dates. */
export interface MutualFundPlanGap {
  years: number
  sipDirect: number | null
  sipRegular: number | null
  /** Direct minus regular, rupees. */
  sipGap: number | null
  lumpSumDirect: number | null
  lumpSumRegular: number | null
  lumpSumGap: number | null
  missing: string | null
}

export interface MutualFundCost {
  ter: MutualFundTer | null
  terMissing: string | null
  directCode: number | null
  regularCode: number | null
  planGap: MutualFundPlanGap[]
  planGapMissing: string | null
}

/** GET api/MutualFunds/{code}/report. */
export interface MutualFundReport {
  scheme: MutualFundScheme
  categoryName: string
  schemeType: string
  /** The NAV day every figure ends on. */
  asOf: string
  nav: number
  history: MutualFundHistory
  sources: MutualFundSource[]
  returns: MutualFundPeriod[]
  calendarYears: MutualFundCalendarYear[]
  rolling: MutualFundRolling | null
  rollingMissing: string | null
  examples: MutualFundExample[]
  risk: MutualFundRisk
  benchmark: MutualFundBenchmark
  category: MutualFundCategory
  cost: MutualFundCost
  notes: string[]
  disclaimer: string
  generatedUtc: string
}

/** GET api/MutualFunds/compare: 2 to 4 reports, all ending on the same NAV day. */
export interface MutualFundCompare {
  asOf: string
  funds: MutualFundReport[]
  notes: string[]
  disclaimer: string
}

export interface MutualFundSummary {
  text: string
  model: string
  writtenUtc: string
}

/** GET api/MutualFunds/{code}/report/summary: a summary, or the plain reason there is none. */
export interface MutualFundSummaryResponse {
  schemeCode: number
  asOf: string
  summary: MutualFundSummary | null
  note: string | null
}

export interface MutualFundBenchmarkCoverage {
  index: string
  name: string
  rows: number
  firstDate: string | null
  lastDate: string | null
  /** The one-time history fetch reached 2006, or the index's first value. */
  historyComplete: boolean
}

export interface MutualFundJobRun {
  trigger: string
  startedUtc: string
  finishedUtc: string
  ok: boolean
  message: string
  reached: string | null
}

export interface MutualFundAnalyticsCoverage {
  benchmarks: MutualFundBenchmarkCoverage[]
  benchmarkSync: MutualFundJobRun | null
  terLatestDate: string | null
  terSchemes: number
  terSync: MutualFundJobRun | null
  statsAsOf: string | null
  statsSchemes: number
  statsComputedUtc: string | null
  statsRun: MutualFundJobRun | null
  /** Jobs an admin asked for that wait for their turn (outside market hours). */
  requested: string[]
}

export type MutualFundJob = 'benchmarks' | 'ter' | 'stats'

/** POST api/MutualFunds/analytics/{job}. */
export interface MutualFundJobResponse {
  message: string
  startsNow: boolean
}

// ---------- numbers and words ----------

const MINUS = '−'

/** A level, not a change (a volatility, a TER, a share of windows): "15.80%", "−3.20%" below zero; null → "—". */
export function formatPct(value: number | null | undefined, digits = 2): string {
  if (value == null || !Number.isFinite(value)) return '—'
  const text = Math.abs(value).toFixed(digits)
  return `${value < 0 && Number(text) !== 0 ? MINUS : ''}${text}%`
}

/** A return, always signed: "+7.38%", "−10.16%", "0.00%" for a flat one; null → "—". */
export function formatReturn(value: number | null | undefined, digits = 2): string {
  if (value == null || !Number.isFinite(value)) return '—'
  const text = Math.abs(value).toFixed(digits)
  if (Number(text) === 0) return `${text}%`
  return `${value > 0 ? '+' : MINUS}${text}%`
}

/** A difference between two percents, in percentage points: "+1.23 pp", "−0.45 pp". */
export function formatPoints(value: number | null | undefined, digits = 2): string {
  if (value == null || !Number.isFinite(value)) return '—'
  const text = Math.abs(value).toFixed(digits)
  if (Number(text) === 0) return `${text} pp`
  return `${value > 0 ? '+' : MINUS}${text} pp`
}

/** A ratio such as beta or Sharpe: "0.94", "−0.12". */
export function formatRatio(value: number | null | undefined, digits = 2): string {
  if (value == null || !Number.isFinite(value)) return '—'
  const text = Math.abs(value).toFixed(digits)
  return `${value < 0 && Number(text) !== 0 ? MINUS : ''}${text}`
}

/** A NAV in rupees, two to four decimals as AMFI prints it: "₹45.1234", "₹1,234.50". */
export function formatNav(value: number | null | undefined): string {
  if (value == null || !Number.isFinite(value)) return '—'
  return `₹${value.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 4 })}`
}

/** A signed ₹ gain for a table cell: "+₹79,412", "−₹4,100". */
export function formatGain(value: number | null | undefined): string {
  return formatInrSigned(value)
}

/** 1st, 2nd, 3rd, 4th, 11th, 12th, 13th, 21st, 22nd, 101st, 111th. */
export function ordinal(n: number): string {
  const mod100 = Math.abs(n) % 100
  if (mod100 >= 11 && mod100 <= 13) return `${n}th`
  switch (Math.abs(n) % 10) {
    case 1:
      return `${n}st`
    case 2:
      return `${n}nd`
    case 3:
      return `${n}rd`
    default:
      return `${n}th`
  }
}

/** "4th of 31": a place by return among the peers that have one; "—" when there is no place. */
export function rankText(rank: number | null | undefined, peers: number | null | undefined): string {
  if (rank == null || peers == null || peers <= 0) return '—'
  return `${ordinal(rank)} of ${formatNumber(peers)}`
}

/** "1 year", "3 years"; null is the whole stored history. */
export function periodLabel(years: number | null | undefined): string {
  if (years == null) return 'Whole history'
  return years === 1 ? '1 year' : `${years} years`
}

/** "a", "a and b", "a, b and c". */
export function joinAnd(items: readonly string[]): string {
  if (items.length <= 1) return items.join('')
  return `${items.slice(0, -1).join(', ')} and ${items[items.length - 1]}`
}

/** "30 Oct 2025 → 30 Oct 2026" for a window; "—" when either end is not known. */
export function dayRange(from: string | null | undefined, to: string | null | undefined): string {
  if (!from || !to) return '—'
  return `${formatDay(from)} → ${formatDay(to)}`
}

/**
 * What NAV history the report rests on, in one line: the API's own sentence
 * when it sends one (it says why longer periods may be empty while the
 * history is not complete), else the same facts put together here.
 */
export function historyLine(h: MutualFundHistory): string {
  const note = h.note?.trim()
  if (note) return note
  if (!h.firstDate || !h.lastDate) return 'No NAVs stored for this scheme.'
  const base = `NAVs from ${formatDay(h.firstDate)} to ${formatDay(h.lastDate)} (${formatNumber(h.navs)} days).`
  return h.complete ? base : `${base} Older NAVs may still arrive, so the longer periods may be empty for that reason.`
}

/** The line under a report's title: the day every figure ends on and the NAV that day. */
export function reportAsOfLine(asOf: string, nav: number | null | undefined): string {
  const navText = nav == null || !Number.isFinite(nav) ? '' : `, when the NAV was ${formatNav(nav)}`
  return `Every figure ends on ${formatDay(asOf)}${navText}.`
}

/** The line above a comparison: the common NAV day. */
export function compareAsOfLine(asOf: string, funds: number): string {
  const all = funds === 2 ? 'both schemes' : `all ${funds} schemes`
  return `Every figure ends on ${formatDay(asOf)}, the latest NAV day ${all} have.`
}

/**
 * The ₹10,000 SIP as one sentence: "₹10,000 a month from 1 Nov 2021 to 1 Oct
 * 2026, 60 instalments, ₹6,00,000 in all, was worth ₹6,79,412 on 30 Oct 2026:
 * an XIRR of 4.92%."
 */
export function sipSentence(s: MutualFundSip): string {
  const count = s.instalments === 1 ? '1 instalment' : `${formatNumber(s.instalments)} instalments`
  const xirr = s.xirrPct == null ? '; its XIRR is not known' : `: an XIRR of ${formatPct(s.xirrPct)}`
  return (
    `${formatInrWhole(s.instalment)} a month from ${formatDay(s.firstDate)} to ${formatDay(s.lastDate)}, ${count}, ` +
    `${formatInrWhole(s.invested)} in all, was worth ${formatInrWhole(s.value)} on ${formatDay(s.valuedOn)}${xirr}.`
  )
}

/** "a gain of ₹61,240", "a loss of ₹12,000", "no gain or loss". */
export function gainWords(gain: number): string {
  const rounded = Math.round(gain)
  if (rounded === 0) return 'no gain or loss'
  return `${rounded > 0 ? 'a gain' : 'a loss'} of ${formatInrWhole(Math.abs(rounded))}`
}

/**
 * The ₹2,00,000 lump sum as one sentence: "₹2,00,000 invested on 1 Nov 2021 at
 * a NAV of ₹45.1234 was worth ₹2,61,240 on 30 Oct 2026, at a NAV of ₹58.95:
 * a gain of ₹61,240, a CAGR of 5.48%."
 */
export function lumpSumSentence(l: MutualFundLumpSum): string {
  return (
    `${formatInrWhole(l.amount)} invested on ${formatDay(l.start)} at a NAV of ${formatNav(l.startNav)} was worth ` +
    `${formatInrWhole(l.value)} on ${formatDay(l.valuedOn)}, at a NAV of ${formatNav(l.endNav)}: ` +
    `${gainWords(l.gain)}, a CAGR of ${formatPct(l.cagrPct)}.`
  )
}

/** Direct minus regular in words: "₹13,580 more in the direct plan", "the same in both plans". */
export function planGapWords(gap: number | null | undefined): string {
  if (gap == null || !Number.isFinite(gap)) return '—'
  const rounded = Math.round(gap)
  if (rounded === 0) return 'the same in both plans'
  return `${formatInrWhole(Math.abs(rounded))} more in the ${rounded > 0 ? 'direct' : 'regular'} plan`
}

/** The heading for fund CAGR minus benchmark CAGR: an index fund's is its tracking difference. */
export function excessLabel(indexFund: boolean): string {
  return indexFund ? 'Tracking difference' : 'Fund minus benchmark'
}

/** "Recovered 3 Mar 2021", or "Not recovered by 30 Oct 2026". */
export function recoveryText(d: MutualFundDrawdown, asOf: string): string {
  return d.recovery ? `Recovered ${formatDay(d.recovery)}` : `Not recovered by ${formatDay(asOf)}`
}

/** The as-of NAV against the highest stored, in words. */
export function currentDrawdownText(risk: MutualFundRisk): string {
  if (risk.currentDrawdownPct == null) return '—'
  const high =
    risk.highestNav != null && risk.highestNavDate ? ` (${formatNav(risk.highestNav)} on ${formatDay(risk.highestNavDate)})` : ''
  if (Math.abs(risk.currentDrawdownPct) < 0.005) return `At its highest NAV stored${high}.`
  return `${formatPct(risk.currentDrawdownPct)} from its highest NAV stored${high}.`
}

/** The rolling windows above the benchmark, in words; null when none were compared. */
export function rollingAboveText(r: MutualFundRolling): string | null {
  if (r.aboveBenchmarkPct == null || !r.benchmarkCompared) return null
  const windows = r.benchmarkCompared === 1 ? 'the 1 window' : `the ${formatNumber(r.benchmarkCompared)} windows`
  return `${formatPct(r.aboveBenchmarkPct, 1)} of ${windows} with a benchmark return over the same dates`
}

/** Reasons a table leaves cells empty, each said once with the rows it applies to. */
export interface MissingNote {
  labels: string[]
  reason: string
}

/**
 * Groups the reasons for the "—" cells of a table: the same reason for the 5
 * and 10 year rows is one line, "5 years and 10 years: …". Order is kept.
 */
export function groupMissing(items: readonly { label: string; reason: string | null | undefined }[]): MissingNote[] {
  const out: MissingNote[] = []
  for (const item of items) {
    const reason = item.reason?.trim()
    if (!reason) continue
    const found = out.find((n) => n.reason === reason)
    if (found) {
      if (!found.labels.includes(item.label)) found.labels.push(item.label)
    } else out.push({ labels: [item.label], reason })
  }
  return out
}

/** One grouped reason as a line: "5 years and 10 years: History starts 3 Jan 2018." */
export function missingLine(n: MissingNote): string {
  return `${joinAnd(n.labels)}: ${n.reason}`
}

/** A scheme's place in its category for one period: the text, the median, and the reason when there is no place. */
export function categoryPosition(
  category: MutualFundCategory,
  period: MutualFundPeriod | undefined,
  years: number,
): { text: string; median: string; missing: string | null } {
  const p = category.periods.find((x) => x.years === years)
  if (p && p.rank != null && p.peers > 0) {
    return { text: rankText(p.rank, p.peers), median: formatReturn(p.medianPct), missing: null }
  }
  const median = p ? formatReturn(p.medianPct) : '—'
  // No place, and why: the scheme has no return for the period (its own reason); there is no ranking at all
  // (category.missing says why); or the ranking has no entry for this period. The note only describes the peers,
  // so it is never given as a reason.
  if (period && period.cagrPct == null && period.missing?.trim()) return { text: '—', median, missing: period.missing.trim() }
  if (category.missing?.trim()) return { text: '—', median, missing: category.missing.trim() }
  if (category.periods.length === 0) return { text: '—', median, missing: 'No category ranking in this report.' }
  if (!p) return { text: '—', median, missing: `No category ranking for ${periodLabel(years)}.` }
  return { text: '—', median, missing: `Not placed in the category ranking for ${periodLabel(years)}.` }
}

/**
 * Why a drawdown window is not in the report: the API's own line for it
 * ("5 years: not enough history …"), as written. The whole history has no
 * line of its own; it is left out only when there are no NAVs.
 */
export function drawdownMissing(risk: MutualFundRisk, years: number | null): string {
  if (years == null) return 'No NAVs to measure a fall on.'
  const prefix = `${periodLabel(years)}:`
  return risk.drawdownsMissing.find((line) => line.trim().startsWith(prefix))?.trim() ?? `${periodLabel(years)}: not in this report.`
}

/** Every period the reports cover, shortest first (1, 3, 5, 10 years). */
export function reportPeriods(reports: readonly MutualFundReport[]): number[] {
  const years = new Set<number>()
  for (const r of reports) for (const p of r.returns) years.add(p.years)
  return [...years].sort((a, b) => a - b)
}

// ---------- the scheme search ----------

/** "1 Apr 2006 – 30 Oct 2026" for a scheme's stored NAVs; "none stored" when there are none. */
export function navRange(s: Pick<MutualFundScheme, 'firstNavDate' | 'lastNavDate'>): string {
  if (!s.firstNavDate || !s.lastNavDate) return 'none stored'
  return `${formatDay(s.firstNavDate)} – ${formatDay(s.lastNavDate)}`
}

/** What a search covers before anything is typed, from the coverage. */
export function searchScope(c: Pick<MutualFundCoverage, 'schemes' | 'activeSchemes' | 'nav'>): string {
  const range = c.nav.firstDate && c.nav.lastDate ? ` with NAVs from ${formatDay(c.nav.firstDate)} to ${formatDay(c.nav.lastDate)}` : ''
  return `${formatNumber(c.schemes)} schemes (${formatNumber(c.activeSchemes)} active)${range}`
}

// ---------- compare ----------

export const MF_COMPARE_MIN = 2
export const MF_COMPARE_MAX = 4

/** "120716,100822" from the address: scheme codes, each once, at most four. */
export function parseCompareCodes(raw: string | null | undefined): { codes: number[]; dropped: number } {
  const seen = new Set<number>()
  for (const part of (raw ?? '').split(',')) {
    const text = part.trim()
    if (!/^\d+$/.test(text)) continue
    const code = Number(text)
    if (code > 0 && Number.isSafeInteger(code)) seen.add(code)
  }
  const all = [...seen]
  return { codes: all.slice(0, MF_COMPARE_MAX), dropped: Math.max(0, all.length - MF_COMPARE_MAX) }
}

export function compareHref(codes: readonly number[]): string {
  return `/data/mutual-funds/compare?codes=${codes.join(',')}`
}

export function reportHref(code: number): string {
  return `/data/mutual-funds/${code}`
}

/** One cell of the compare table: the figure, a second line, and the reason when there is no figure. */
export interface CompareCell {
  text: string
  sub?: string
  /** Why the figure is "—", shown under it. */
  missing?: string
  tone?: 'pos' | 'neg'
}

export interface CompareRow {
  key: string
  label: string
  /** What the figure is, for the row heading's title. */
  hint?: string
  /** One per fund, in the funds' order. */
  cells: CompareCell[]
}

export interface CompareSection {
  title: string
  rows: CompareRow[]
}

const NOT_IN_REPORT = 'Not in this report.'

function toneOf(value: number | null | undefined): 'pos' | 'neg' | undefined {
  if (value == null || Math.abs(value) < 0.005) return undefined
  return value > 0 ? 'pos' : 'neg'
}

/** The rupee amounts the worked examples use, read from the first report that has them. */
export function exampleAmounts(reports: readonly MutualFundReport[]): { instalment: number | null; amount: number | null } {
  let instalment: number | null = null
  let amount: number | null = null
  for (const r of reports) {
    for (const e of r.examples) {
      instalment ??= e.sip?.instalment ?? null
      amount ??= e.lumpSum?.amount ?? null
    }
  }
  return { instalment, amount }
}

/**
 * The compare table, section by section, one cell per fund in the order the
 * funds came. Every "—" carries the API's reason; nothing here orders or
 * marks the funds.
 */
export function compareSections(funds: readonly MutualFundReport[]): CompareSection[] {
  const periods = reportPeriods(funds)
  const period = (f: MutualFundReport, years: number) => f.returns.find((p) => p.years === years)
  const example = (f: MutualFundReport, years: number) => f.examples.find((e) => e.years === years)
  const { instalment, amount } = exampleAmounts(funds)

  const returns: CompareRow[] = periods.map((years) => ({
    key: `cagr-${years}`,
    label: periodLabel(years),
    hint: `Compound annual growth of the NAV over ${periodLabel(years)}`,
    cells: funds.map((f): CompareCell => {
      const p = period(f, years)
      if (!p) return { text: '—', missing: NOT_IN_REPORT }
      if (p.cagrPct == null) return { text: '—', missing: p.missing ?? NOT_IN_REPORT }
      return { text: formatReturn(p.cagrPct), sub: `${formatReturn(p.absolutePct)} in all`, tone: toneOf(p.cagrPct) }
    }),
  }))

  const positions: CompareRow[] = periods.map((years) => ({
    key: `rank-${years}`,
    label: periodLabel(years),
    hint: `Place by ${periodLabel(years)} return among schemes of the same category, plan and option (1st is the highest return)`,
    cells: funds.map((f): CompareCell => {
      const c = categoryPosition(f.category, period(f, years), years)
      if (c.missing) return { text: '—', missing: c.missing }
      return { text: c.text, sub: `median ${c.median}` }
    }),
  }))

  const benchmark: CompareRow[] = [
    {
      key: 'benchmark',
      label: 'Benchmark',
      cells: funds.map((f): CompareCell =>
        f.benchmark.name ? { text: f.benchmark.name } : { text: '—', missing: f.benchmark.note?.trim() || NOT_IN_REPORT },
      ),
    },
    ...periods.map((years) => ({
      key: `excess-${years}`,
      label: periodLabel(years),
      hint: `Fund CAGR minus benchmark CAGR over ${periodLabel(years)}, in percentage points`,
      cells: funds.map((f): CompareCell => {
        const p = period(f, years)
        if (!p) return { text: '—', missing: NOT_IN_REPORT }
        if (p.excessPct == null) return { text: '—', missing: p.benchmarkMissing ?? p.missing ?? NOT_IN_REPORT }
        const sub = `${f.benchmark.indexFund ? 'tracking difference · ' : ''}benchmark ${formatReturn(p.benchmarkCagrPct)}`
        return { text: formatPoints(p.excessPct), sub, tone: toneOf(p.excessPct) }
      }),
    })),
  ]

  const sips: CompareRow[] = periods.map((years) => ({
    key: `sip-${years}`,
    label: periodLabel(years),
    hint: 'What the monthly SIP was worth on the last day, and its XIRR',
    cells: funds.map((f): CompareCell => {
      const e = example(f, years)
      if (!e?.sip) return { text: '—', missing: e?.missing ?? NOT_IN_REPORT }
      const xirr = e.sip.xirrPct == null ? 'XIRR not known' : `XIRR ${formatPct(e.sip.xirrPct)}`
      return { text: formatInrWhole(e.sip.value), sub: `${xirr} · ${formatInrWhole(e.sip.invested)} put in` }
    }),
  }))

  const lumps: CompareRow[] = periods.map((years) => ({
    key: `lump-${years}`,
    label: periodLabel(years),
    hint: 'What the lump sum was worth on the last day, and its CAGR',
    cells: funds.map((f): CompareCell => {
      const e = example(f, years)
      if (!e?.lumpSum) return { text: '—', missing: e?.missing ?? NOT_IN_REPORT }
      return { text: formatInrWhole(e.lumpSum.value), sub: `CAGR ${formatPct(e.lumpSum.cagrPct)} · from ${formatDay(e.lumpSum.start)}` }
    }),
  }))

  const drawdownRow = (years: number | null, label: string): CompareRow => ({
    key: `dd-${years ?? 'all'}`,
    label,
    hint: 'The deepest fall of the NAV from a high within the window, and its peak and trough days',
    cells: funds.map((f): CompareCell => {
      const d = f.risk.drawdowns.find((x) => x.years === years)
      if (!d) return { text: '—', missing: drawdownMissing(f.risk, years) }
      return { text: formatPct(d.maxDrawdownPct), sub: dayRange(d.peak, d.trough), tone: toneOf(d.maxDrawdownPct) }
    }),
  })

  const risk: CompareRow[] = [
    {
      key: 'volatility',
      label: 'Volatility',
      hint: 'Annualised standard deviation of daily NAV returns',
      cells: funds.map((f): CompareCell => {
        const v = f.risk.volatility
        if (!v) return { text: '—', missing: f.risk.volatilityMissing?.trim() || NOT_IN_REPORT }
        return { text: formatPct(v.annualisedPct), sub: `a year, over ${periodLabel(v.years)}` }
      }),
    },
    drawdownRow(5, 'Max drawdown, 5 years'),
    drawdownRow(null, 'Max drawdown, whole history'),
  ]

  const cost: CompareRow[] = [
    {
      key: 'ter',
      label: 'TER, this plan',
      hint: "Total expense ratio from AMFI's TER file, a percent of assets a year",
      cells: funds.map((f): CompareCell => {
        const t = f.cost.ter
        if (!t) return { text: '—', missing: f.cost.terMissing ?? NOT_IN_REPORT }
        if (t.thisPlanPct == null) {
          return {
            text: '—',
            missing: `This scheme's plan is not known; regular ${formatPct(t.regularPct)}, direct ${formatPct(t.directPct)} on ${formatDay(t.date)}.`,
          }
        }
        return { text: formatPct(t.thisPlanPct), sub: `on ${formatDay(t.date)}` }
      }),
    },
    {
      key: 'history',
      label: 'History starts',
      hint: 'The first NAV stored for the scheme',
      cells: funds.map((f): CompareCell => {
        const h = f.history
        if (!h.firstDate) return { text: '—', missing: historyLine(h) }
        return {
          text: formatDay(h.firstDate),
          sub: h.complete ? `${formatNumber(h.navs)} NAVs, complete` : `${formatNumber(h.navs)} NAVs; older ones may still arrive`,
        }
      }),
    },
  ]

  return [
    { title: 'CAGR', rows: returns },
    { title: 'Category position by return', rows: positions },
    { title: 'Against the benchmark (CAGR difference)', rows: benchmark },
    { title: instalment != null ? `${formatInrWhole(instalment)} a month SIP: value` : 'Monthly SIP: value', rows: sips },
    { title: amount != null ? `${formatInrWhole(amount)} lump sum: value` : 'Lump sum: value', rows: lumps },
    { title: 'Risk', rows: risk },
    { title: 'Cost and history', rows: cost },
  ]
}

// ---------- the report jobs ----------

export const MF_JOBS: readonly { key: MutualFundJob; label: string; what: string }[] = [
  { key: 'benchmarks', label: 'Benchmark indices', what: 'Total-return index values the schemes are compared with.' },
  { key: 'ter', label: 'TER', what: "AMFI's total expense ratios, regular and direct." },
  { key: 'stats', label: 'Category ranking', what: "Every scheme's returns placed among its category's." },
]

/** A job's last run, from the analytics coverage. */
export function jobRun(a: MutualFundAnalyticsCoverage, job: MutualFundJob): MutualFundJobRun | null {
  if (job === 'benchmarks') return a.benchmarkSync
  if (job === 'ter') return a.terSync
  return a.statsRun
}

/** Whether an admin asked for the job and it waits for its turn. */
export function jobRequested(a: MutualFundAnalyticsCoverage, job: MutualFundJob): boolean {
  return a.requested.some((r) => r.trim().toLowerCase() === job)
}

// ---------- the written summary ----------

/**
 * Words the fund report never uses: they rank, rate or tell someone what to
 * do. The pages' own text is scanned for them (mutualFunds.test.ts), and a
 * written summary that carries one is not shown: the server checks it first,
 * and this is the second lock. "Highest", "lowest", "above" and "below" are
 * the words of fact the report uses instead.
 */
// report-words:start
const REPORT_WORD_PATTERN =
  /\b(best|worst|top|top[\s-]picks?|stars?|ratings?|recommend(s|ed|ing|ation|ations)?|buy(s|ing)?|sell(s|ing)?|hold|avoid(s|ed|ing)?|should|consider(s|ed|ing)?|outperform(s|ed|ing|ance)?|underperform(s|ed|ing|ance)?)\b/gi
// report-words:end

/** The ranking or advice words in a text, lower-cased, each once. */
export function reportWords(text: string): string[] {
  return [...new Set([...text.matchAll(REPORT_WORD_PATTERN)].map((m) => m[0].toLowerCase()))]
}

/** What the summary panel shows: the summary, or the plain reason there is none. */
export type SummaryView = { kind: 'summary'; summary: MutualFundSummary } | { kind: 'note'; text: string }

export function summaryView(r: MutualFundSummaryResponse, reportAsOf: string): SummaryView {
  if (!r.summary) return { kind: 'note', text: r.note?.trim() || 'No written summary for this scheme.' }
  if (r.asOf !== reportAsOf) {
    return {
      kind: 'note',
      text: `The written summary covers the figures to ${formatDay(r.asOf)}, not to ${formatDay(reportAsOf)}, so it is not shown here.`,
    }
  }
  const words = reportWords(r.summary.text)
  if (words.length > 0) {
    return {
      kind: 'note',
      text: `A written summary came back but is not shown: it uses ${joinAnd(words.map((w) => `“${w}”`))}, which this report does not.`,
    }
  }
  return { kind: 'summary', summary: r.summary }
}
