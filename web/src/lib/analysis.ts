/**
 * Analysis — forecasts with proof: the shapes the Forecasts API returns
 * (docs/modules/analysis.md is the contract) and the words the Analysis page
 * puts on them. Pure, so the wording is tested rather than eyeballed.
 *
 * The rule this file keeps: the page never says more than the evidence does.
 * A model is called proven only on live forecasts — at least 60 scored, the
 * API's status agreeing, and the whole 95% interval above zero — whatever its
 * backtest says. A backtest number is always labelled as history. And "not
 * known" never renders as a fact: a list the API did not answer is an error on
 * screen, not an empty scoreboard.
 */

import { formatDay, istDate } from './factors'

// ---------- contract shapes ----------

/** The three indices of version 1, in the order the page shows them. */
export const UNDERLYINGS = ['NIFTY', 'BANKNIFTY', 'SENSEX'] as const
export type Underlying = (typeof UNDERLYINGS)[number]

export const UNDERLYING_SYMBOL: Record<Underlying, string> = {
  NIFTY: 'NSE:NIFTY50-INDEX',
  BANKNIFTY: 'NSE:NIFTYBANK-INDEX',
  SENSEX: 'BSE:SENSEX-INDEX',
}

export const TARGETS = ['range', 'trend', 'direction'] as const
export type ForecastTarget = (typeof TARGETS)[number]

export type ModelStatus = 'collecting' | 'testing' | 'proven' | 'retired'

export type RangeBucket = 'quiet' | 'normal' | 'wild'

/**
 * One walk-forward period of a model's backtest, as registered. The contract's
 * six keys, plus what the analysis job adds (docs/modules/analysis.md, "Fitting,
 * the backtest…"); the extras are optional and the page reads only the six.
 */
export interface BacktestPeriod {
  from: string
  to: string
  n: number
  loss: number
  baselineLoss: number
  skill: number
  diffCiLow?: number | null
  diffCiHigh?: number | null
  calibration?: CalibrationBin[] | null
  baselineCalibration?: CalibrationBin[] | null
  /** Range only. */
  coverage80?: number | null
  baselineCoverage80?: number | null
  bucketBrier?: number | null
  baselineBucketBrier?: number | null
}

/**
 * `backtest` on a registered model. Every part may be missing: a model may be
 * registered without one, and a period with no forecasts is null.
 */
export interface ModelBacktest {
  design?: BacktestPeriod | null
  validation?: BacktestPeriod | null
  holdout?: BacktestPeriod | null
  byUnderlying?: Record<string, { n: number; skill: number; loss?: number | null; baselineLoss?: number | null }> | null
  configurationsTried?: number | null
  notes?: string | null
}

/** GET /api/Forecasts/models — one per registered model version. */
export interface ForecastModel {
  key: string
  version: string
  target: ForecastTarget
  description: string | null
  backtest: ModelBacktest | null
  /** When the version was first registered, and last re-registered (the API adds these to its views). */
  registeredUtc?: string | null
  updatedUtc?: string | null
}

/** `prediction` / `baseline` of a range forecast; percentages of the previous close. */
export interface RangePrediction {
  median: number
  low80: number
  high80: number
  prevClose: number
  points: { median: number; low80: number; high80: number }
  buckets: Record<RangeBucket, number>
  /** The underlying's terciles in the training data: [quiet|normal, normal|wild]. */
  bucketEdges: [number, number]
}

/** `prediction` / `baseline` of a trend or direction forecast. */
export interface ProbabilityPrediction {
  p: number
}

export type Prediction = RangePrediction | ProbabilityPrediction

/** What the session did — written after its close. */
export interface ForecastOutcome {
  open: number
  high: number
  low: number
  close: number
  range: number
  /** Null on trend and direction forecasts: bucket edges belong to a range forecast. */
  bucket: RangeBucket | null
  trendDay: boolean
  efficiency: number
  up: boolean
}

export interface CalibrationPair {
  p: number
  y: number
}

export interface ForecastScores {
  loss: number
  baselineLoss: number
  metrics?: { covered80?: boolean; brier?: number; baselineBrier?: number } | null
  calibration?: CalibrationPair[] | null
}

/** GET /api/Forecasts — one issued forecast, scored or not. */
export interface Forecast {
  id: number
  modelKey: string
  modelVersion: string
  target: ForecastTarget
  underlying: string
  sessionDate: string
  issuedUtc: string
  prediction: Prediction
  baseline: Prediction
  inputs: Record<string, unknown> | null
  outcome: ForecastOutcome | null
  scores: ForecastScores | null
  scoredUtc: string | null
}

/** A tenth of probability on the scoreboard's reliability diagram. */
export interface CalibrationBin {
  from: number
  to: number
  n: number
  meanP: number
  hitRate: number
}

/**
 * GET /api/Forecasts/scoreboard — one row per model version, target and
 * underlying, plus an "ALL" row per model version. The means, skill and
 * interval are null-tolerant here: a row with nothing scored has none.
 */
export interface ScoreboardRow {
  modelKey: string
  modelVersion: string
  target: ForecastTarget
  underlying: string
  description: string | null
  liveCount: number
  meanLoss: number | null
  meanBaselineLoss: number | null
  skill: number | null
  diffCiLow: number | null
  diffCiHigh: number | null
  status: ModelStatus
  statusReason: string
  coverage80: number | null
  calibration: CalibrationBin[] | null
  firstSession: string | null
  lastSession: string | null
  backtest: ModelBacktest | null
}

// ---------- the rules, as the contract fixes them ----------

/** The pass marks, set before the data (rule 4). */
export const PROOF = {
  /** Under this many scored forecasts a model is still collecting. */
  firstReadingAt: 20,
  /** Proven needs at least this many, with the interval's low end above zero. */
  provenAt: 60,
  /** Retired needs at least this many, with the interval's high end below zero. */
  retiredAt: 120,
  confidencePct: 95,
  resamples: 2000,
} as const

/** When the desk writes and scores forecasts, IST (docs/modules/analysis.md, Schedule). */
export const SCHEDULE = {
  issue: '08:50',
  open: '09:15',
  close: '15:30',
  score: '15:50',
} as const

const ISSUE_MIN = 8 * 60 + 50
const OPEN_MIN = 9 * 60 + 15

export const TARGET_META: Record<
  ForecastTarget,
  { label: string; question: string; what: string; baseline: string; loss: string }
> = {
  range: {
    label: 'Range',
    question: 'How far will the index travel today?',
    what: "The session's high − low, as % of the previous close",
    baseline: 'Mean range of the last 20 sessions',
    loss: '|ln(actual) − ln(predicted median)|',
  },
  trend: {
    label: 'Trend day',
    question: 'Will today be a trend day?',
    what: 'A trend day: |close − open| ≥ 0.6 × (high − low)',
    baseline: 'Trailing 250-session base rate',
    loss: 'Brier score',
  },
  direction: {
    label: 'Direction',
    question: 'Will the index close above its open?',
    what: 'Close above open',
    baseline: 'Trailing 250-session base rate',
    loss: 'Brier score',
  },
}

export function targetLabel(target: string): string {
  return TARGET_META[target as ForecastTarget]?.label ?? target
}

export const BUCKETS: ReadonlyArray<{ key: RangeBucket; label: string }> = [
  { key: 'quiet', label: 'Quiet' },
  { key: 'normal', label: 'Normal' },
  { key: 'wild', label: 'Wild' },
]

export function bucketLabel(bucket: string | null | undefined): string {
  if (!bucket) return '—'
  return BUCKETS.find((b) => b.key === bucket)?.label ?? bucket
}

/** "Quiet under 0.75% · Normal 0.75–1.25% · Wild from 1.25%". */
export function bucketEdgesText(edges: readonly number[] | null | undefined): string {
  if (!edges || edges.length < 2 || !edges.every(Number.isFinite)) return ''
  const [a, b] = edges
  return `Quiet under ${formatPct(a)} · Normal ${num(a, 2)}–${formatPct(b)} · Wild from ${formatPct(b)}`
}

type Tone = 'pos' | 'neg' | 'warn' | 'neutral' | 'accent'

export const STATUS_META: Record<ModelStatus, { label: string; tone: Tone; meaning: string }> = {
  collecting: {
    label: 'Collecting',
    tone: 'neutral',
    meaning: `Fewer than ${PROOF.firstReadingAt} live forecasts scored — too few to read anything into.`,
  },
  testing: {
    label: 'Testing',
    tone: 'accent',
    meaning:
      `${PROOF.firstReadingAt} or more scored, not decided. Proven needs at least ${PROOF.provenAt} with the ` +
      `whole ${PROOF.confidencePct}% interval above zero; retired needs ${PROOF.retiredAt} with it wholly below.`,
  },
  proven: {
    label: 'Proven',
    tone: 'pos',
    meaning:
      `At least ${PROOF.provenAt} live forecasts beat the baseline, and even the low end of the ` +
      `${PROOF.confidencePct}% interval is above zero.`,
  },
  retired: {
    label: 'Retired',
    tone: 'neg',
    meaning:
      `After ${PROOF.retiredAt} or more live forecasts, even the high end of the ${PROOF.confidencePct}% ` +
      'interval is worse than the baseline.',
  },
}

/** A status this page does not know is shown as itself, in a plain badge, and claims nothing. */
export function statusMeta(status: string): { label: string; tone: Tone; meaning: string } {
  return (
    STATUS_META[status as ModelStatus] ?? {
      label: status,
      tone: 'neutral',
      meaning: 'A status this page does not know.',
    }
  )
}

// ---------- numbers ----------

function finite(v: unknown): v is number {
  return typeof v === 'number' && Number.isFinite(v)
}

function num(v: number, digits: number): string {
  return v.toLocaleString('en-IN', { minimumFractionDigits: digits, maximumFractionDigits: digits })
}

/** "1.02%" — a range, as % of the previous close. Unsigned. */
export function formatPct(value: number | null | undefined, digits = 2): string {
  if (!finite(value)) return '—'
  return `${num(value, digits)}%`
}

/** "≈251 pts", "≈1,120 pts" — index points, rounded, Indian grouping. */
export function formatPoints(value: number | null | undefined): string {
  if (!finite(value)) return '—'
  return `≈${Math.round(value).toLocaleString('en-IN')} pts`
}

/** "38%" — a probability, whole percent. */
export function formatProb(p: number | null | undefined): string {
  if (!finite(p)) return '—'
  const clamped = Math.min(1, Math.max(0, p))
  return `${Math.round(clamped * 100)}%`
}

/** "+6 pts", "−3 pts", "±0 pts" — a probability's distance from its baseline, in percentage points. */
export function formatProbDelta(p: number | null | undefined, baseline: number | null | undefined): string {
  if (!finite(p) || !finite(baseline)) return ''
  const d = Math.round((p - baseline) * 100)
  if (d === 0) return '±0 pts'
  return `${d > 0 ? '+' : '−'}${Math.abs(d)} pts`
}

/** The range half of a prediction, or null when it is not one. */
export function asRange(prediction: unknown): RangePrediction | null {
  const r = prediction as Partial<RangePrediction> | null | undefined
  return r && finite(r.median) ? (r as RangePrediction) : null
}

/** The probability of a trend or direction prediction, or null. */
export function asProb(prediction: unknown): number | null {
  const p = (prediction as Partial<ProbabilityPrediction> | null | undefined)?.p
  return finite(p) ? p : null
}

/**
 * "NIFTY 1.02% · ≈251 pts, 80% between 0.68% and 1.55%" — a range forecast in
 * one line. Parts the forecast does not carry are left out, never shown as 0.
 */
export function formatRangeForecast(underlying: string, prediction: unknown): string {
  const r = asRange(prediction)
  if (!r) return `${underlying} —`
  const points = finite(r.points?.median) ? ` · ${formatPoints(r.points.median)}` : ''
  const band =
    finite(r.low80) && finite(r.high80) ? `, 80% between ${formatPct(r.low80)} and ${formatPct(r.high80)}` : ''
  return `${underlying} ${formatPct(r.median)}${points}${band}`
}

// ---------- skill and its interval ----------

/**
 * "12% better than the baseline" / "4% worse than the baseline" / "level with
 * the baseline". `short` drops the last three words for a table cell.
 */
export function formatSkill(skill: number | null | undefined, short = false): string {
  if (!finite(skill)) return short ? '—' : 'no live score yet'
  const pct = Math.round(skill * 100)
  if (pct === 0) return short ? 'level' : 'level with the baseline'
  const word = pct > 0 ? 'better' : 'worse'
  return `${Math.abs(pct)}% ${word}${short ? '' : ' than the baseline'}`
}

type CiRow = Pick<ScoreboardRow, 'diffCiLow' | 'diffCiHigh' | 'meanBaselineLoss'>

/**
 * The 95% interval rescaled to skill: the API's interval is of the mean of
 * (baselineLoss − loss), and skill is that mean over the mean baseline loss,
 * so dividing both ends by it puts the interval on the same scale as the skill
 * shown beside it. Approximate — the bootstrap resampled the baseline loss as
 * well — which is why the page prints it with "≈". Null when not computable.
 */
export function ciAsSkill(row: CiRow): { low: number; high: number } | null {
  const { diffCiLow: lo, diffCiHigh: hi, meanBaselineLoss: base } = row
  if (!finite(lo) || !finite(hi) || !finite(base) || base <= 0) return null
  return { low: lo / base, high: hi / base }
}

/** "≈ −2% to +24%" — the rescaled interval, signed. */
export function formatCiSkill(row: CiRow): string {
  const ci = ciAsSkill(row)
  if (!ci) return '—'
  // Near zero the side of zero is the whole point, so an end inside ±1% keeps
  // a decimal: −0.3% must not print as "0%" and read as "no worse".
  const f = (v: number) => {
    const pct = v * 100
    const abs = Math.abs(pct)
    if (abs < 0.05) return '0%'
    const text = abs < 1 ? abs.toFixed(1) : String(Math.round(abs))
    return `${pct > 0 ? '+' : '−'}${text}%`
  }
  return `≈ ${f(ci.low)} to ${f(ci.high)}`
}

/**
 * One scale for every interval bar in a table: the widest end or skill across
 * its rows, with a margin, so a wide interval looks wide next to a narrow one.
 */
export function sharedExtent(rows: ReadonlyArray<CiRow & Pick<ScoreboardRow, 'skill'>>): number | undefined {
  let widest = 0
  for (const row of rows) {
    const ci = ciAsSkill(row)
    if (!ci) continue
    widest = Math.max(widest, Math.abs(ci.low), Math.abs(ci.high), finite(row.skill) ? Math.abs(row.skill) : 0)
  }
  return widest > 0 ? widest * 1.15 : undefined
}

/**
 * What the interval says, in words. Its low end at or below zero means the
 * data cannot rule out "no better than the baseline" — the reading that stops
 * a lucky streak from passing for skill.
 */
export function ciReading(row: Pick<ScoreboardRow, 'diffCiLow' | 'diffCiHigh'>): string {
  const { diffCiLow: lo, diffCiHigh: hi } = row
  if (!finite(lo) || !finite(hi)) return 'No interval yet.'
  if (hi < 0) return 'Worse than the baseline: even the top of the 95% interval is below zero.'
  if (lo > 0) return 'Better than the baseline: even the bottom of the 95% interval is above zero.'
  return 'Could be no better than the baseline: the 95% interval includes zero.'
}

/** "+12%", "−4%", "0%" — a skill, signed, whole percent. */
export function formatSkillSigned(skill: number | null | undefined): string {
  if (!finite(skill)) return '—'
  const pct = Math.round(skill * 100)
  if (pct === 0) return '0%'
  return `${pct > 0 ? '+' : '−'}${Math.abs(pct)}%`
}

/**
 * Where an interval bar's parts sit, 0–100, on a scale centred on zero. Zero
 * is always the middle, so a bar is read by which side of the line it falls.
 * Pass one `extent` for every row of a table so the bars share a scale;
 * without it, the scale fits this bar alone.
 */
export function intervalBar(
  low: number,
  high: number,
  point?: number | null,
  extent?: number,
): { from: number; to: number; zero: number; point: number | null } {
  const own = Math.max(Math.abs(low), Math.abs(high), finite(point) ? Math.abs(point) : 0) * 1.15
  const scale = finite(extent) && extent > 0 ? extent : Math.max(own, 1e-9)
  const at = (v: number) => Math.min(100, Math.max(0, 50 + (v / scale) * 50))
  const a = at(Math.min(low, high))
  const b = at(Math.max(low, high))
  return { from: a, to: b, zero: 50, point: finite(point) ? at(point) : null }
}

// ---------- verdicts ----------

type VerdictRow = Pick<
  ScoreboardRow,
  'target' | 'liveCount' | 'status' | 'skill' | 'diffCiLow' | 'diffCiHigh'
>

/**
 * Whether a row may be called proven. All three must hold: the API says so,
 * there are at least 60 live forecasts, and the interval is above zero. A row
 * that fails any of them is not proven, whatever else it has going for it.
 */
export function isProven(row: VerdictRow): boolean {
  return (
    row.status === 'proven' &&
    finite(row.liveCount) &&
    row.liveCount >= PROOF.provenAt &&
    finite(row.diffCiLow) &&
    row.diffCiLow > 0
  )
}

/**
 * The status the page shows: the API's, except that "proven" is shown only
 * when the page's own check agrees ({@link isProven}). A row marked proven on
 * too few forecasts, or with an interval touching zero, is shown as testing.
 */
export function displayStatus(row: VerdictRow): string {
  if (row.status === 'proven' && !isProven(row)) return 'testing'
  return row.status
}

/** Retired by the API, or by the contract's own rule when the API has not caught up. */
export function isRetired(row: VerdictRow): boolean {
  if (row.status === 'retired') return true
  return finite(row.liveCount) && row.liveCount >= PROOF.retiredAt && finite(row.diffCiHigh) && row.diffCiHigh < 0
}

/**
 * Direction is the control: the desk's research found nothing that predicts
 * it, so apparent skill there is first a reason to suspect a leak.
 */
export const LEAK_NOTE = 'Direction is the control, so skill here is first a reason to look for a leak.'

function forecasts(n: number): string {
  return `${n} live forecast${n === 1 ? '' : 's'}`
}

/**
 * One line per model that never overstates. Only live forecasts count; a
 * backtest is never mentioned as support. Under 60 live forecasts the word
 * "proven" is not used, however good the numbers look.
 */
export function verdict(row: VerdictRow): { tone: Tone; text: string } {
  const n = finite(row.liveCount) ? Math.max(0, Math.floor(row.liveCount)) : 0
  const skill = formatSkill(row.skill)
  const lo = row.diffCiLow
  const hi = row.diffCiHigh

  if (isRetired(row)) {
    return {
      tone: 'neg',
      text: `Retired after ${forecasts(n)}: ${skill}, and even the top of the 95% interval is below zero.`,
    }
  }
  if (isProven(row)) {
    return {
      tone: 'pos',
      text:
        `Proven on ${forecasts(n)}: ${skill}, and even the bottom of the 95% interval is above zero.` +
        (row.target === 'direction' ? ` ${LEAK_NOTE}` : ''),
    }
  }
  if (n < PROOF.firstReadingAt) {
    return {
      tone: 'neutral',
      text:
        n === 0
          ? `Collecting: no live forecast scored yet. The first reading comes at ${PROOF.firstReadingAt}.`
          : `Collecting: ${n} of ${PROOF.firstReadingAt} live forecasts before a first reading. Nothing is claimed yet.`,
    }
  }
  if (finite(lo) && lo > 0) {
    return {
      tone: 'accent',
      text:
        (n < PROOF.provenAt
          ? `Promising, not proven: ${skill} on ${forecasts(n)} with the interval above zero so far. ` +
            `It needs ${PROOF.provenAt} before it can be called proven.`
          : `${capitalise(skill)} on ${forecasts(n)} with the interval above zero, but the scoreboard ` +
            'has not marked it proven, so this page does not either.') +
        (row.target === 'direction' ? ` ${LEAK_NOTE}` : ''),
    }
  }
  if (finite(hi) && hi < 0) {
    return {
      tone: 'warn',
      text:
        `Behind: ${skill} on ${forecasts(n)}, with the whole interval below zero so far. ` +
        `It is retired at ${PROOF.retiredAt} if it stays there.`,
    }
  }
  if (row.target === 'direction') {
    return {
      tone: 'neutral',
      text:
        `Control: ${skill} on ${forecasts(n)}, and it could be no better than the baseline — ` +
        'the honest answer for direction.',
    }
  }
  const progress = n < PROOF.provenAt ? `${n} of ${PROOF.provenAt} live forecasts` : forecasts(n)
  return {
    tone: 'neutral',
    text: `Testing: ${progress}. So far ${skill}, but it could be no better than the baseline.`,
  }
}

function capitalise(s: string): string {
  return s.charAt(0).toUpperCase() + s.slice(1)
}

// ---------- scoreboard grouping ----------

/** A model version on the scoreboard: its across-all row first, then one per index. */
export interface ModelGroup {
  id: string
  modelKey: string
  modelVersion: string
  target: ForecastTarget
  description: string
  all: ScoreboardRow
  perUnderlying: ScoreboardRow[]
  backtest: ModelBacktest | null
  /** From the model list; absent when it did not load or the version is not in it. */
  registeredUtc: string | null
}

function underlyingOrder(u: string): number {
  const i = (UNDERLYINGS as readonly string[]).indexOf(u)
  return i === -1 ? UNDERLYINGS.length : i
}

function targetOrder(t: string): number {
  const i = (TARGETS as readonly string[]).indexOf(t)
  return i === -1 ? TARGETS.length : i
}

/** Compare version strings like "2026-09-27.1" so the newest is first. */
function newerFirst(a: string, b: string): number {
  return b.localeCompare(a, undefined, { numeric: true })
}

/**
 * The scoreboard's rows grouped per model version, with every registered model
 * present: a model registered but not yet scored has no scoreboard rows, and
 * would otherwise be missing from the page. It is shown as collecting with
 * nothing scored — which is exactly what the contract's rule makes it.
 * Ordered range, trend, direction; then by key; newest version first.
 */
export function groupScoreboard(
  rows: readonly ScoreboardRow[] | undefined,
  models: readonly ForecastModel[] | undefined,
): ModelGroup[] {
  const groups = new Map<string, { all: ScoreboardRow | null; per: ScoreboardRow[]; model?: ForecastModel }>()
  const idOf = (key: string, version: string) => `${key}@${version}`

  for (const row of rows ?? []) {
    const id = idOf(row.modelKey, row.modelVersion)
    const g = groups.get(id) ?? { all: null, per: [] }
    if (row.underlying === 'ALL') g.all = row
    else g.per.push(row)
    groups.set(id, g)
  }
  for (const model of models ?? []) {
    const id = idOf(model.key, model.version)
    const g = groups.get(id) ?? { all: null, per: [] }
    g.model = model
    groups.set(id, g)
  }

  const out: ModelGroup[] = []
  for (const [id, g] of groups) {
    const first = g.all ?? g.per[0]
    const modelKey = first?.modelKey ?? g.model!.key
    const modelVersion = first?.modelVersion ?? g.model!.version
    const target = (first?.target ?? g.model!.target) as ForecastTarget
    const description = g.model?.description || first?.description || ''
    const backtest = g.model?.backtest ?? first?.backtest ?? null
    const all: ScoreboardRow = g.all ?? {
      modelKey,
      modelVersion,
      target,
      underlying: 'ALL',
      description,
      liveCount: 0,
      meanLoss: null,
      meanBaselineLoss: null,
      skill: null,
      diffCiLow: null,
      diffCiHigh: null,
      status: 'collecting',
      statusReason: 'No live forecast scored yet.',
      coverage80: null,
      calibration: [],
      firstSession: null,
      lastSession: null,
      backtest,
    }
    out.push({
      id,
      modelKey,
      modelVersion,
      target,
      description,
      all,
      perUnderlying: [...g.per].sort((a, b) => underlyingOrder(a.underlying) - underlyingOrder(b.underlying)),
      backtest,
      registeredUtc: g.model?.registeredUtc ?? null,
    })
  }

  return out.sort(
    (a, b) =>
      targetOrder(a.target) - targetOrder(b.target) ||
      a.modelKey.localeCompare(b.modelKey) ||
      newerFirst(a.modelVersion, b.modelVersion),
  )
}

const STATUS_RANK: Record<string, number> = { proven: 3, testing: 2, collecting: 1, retired: 0 }

/**
 * How strongly a model's live record stands, for choosing which of two range
 * forecasts leads a card: proven over testing over collecting, then the better
 * live skill. Only the live record counts, as everywhere else on this page.
 */
export function leadScore(group: ModelGroup | undefined): number {
  if (!group) return -1
  const status = isProven(group.all) ? 3 : Math.min(STATUS_RANK[group.all.status] ?? 0, 2)
  const skill = finite(group.all.skill) ? Math.max(-0.99, Math.min(0.99, group.all.skill)) : -0.99
  return status * 10 + skill
}

/**
 * The answer to the question the module exists for, in one line: can this
 * desk predict anything better than a baseline, and prove it?
 */
export function deskAnswer(groups: readonly ModelGroup[]): { tone: Tone; text: string } {
  if (groups.length === 0) {
    return { tone: 'neutral', text: 'No model is registered yet, so there is nothing to judge.' }
  }
  const proven = groups.filter((g) => isProven(g.all))
  if (proven.length > 0) {
    const names = proven
      .map((g) => `${g.modelKey} (${targetLabel(g.target).toLowerCase()}, ${formatSkill(g.all.skill)} on ${forecasts(g.all.liveCount)})`)
      .join('; ')
    return {
      tone: 'pos',
      text: `Yes, on live forecasts, by the pass mark fixed before the data: ${names}.`,
    }
  }
  const read = groups.filter((g) => g.all.liveCount >= PROOF.firstReadingAt && finite(g.all.skill) && !isRetired(g.all))
  if (read.length === 0) {
    return {
      tone: 'neutral',
      text: `Not yet. Every model is still collecting its first ${PROOF.firstReadingAt} live forecasts.`,
    }
  }
  const best = [...read].sort((a, b) => (b.all.skill ?? 0) - (a.all.skill ?? 0))[0]
  const tail =
    finite(best.all.diffCiLow) && best.all.diffCiLow > 0
      ? `with the interval above zero so far — it needs ${PROOF.provenAt} to count.`
      : 'but it could be no better than the baseline.'
  return {
    tone: 'neutral',
    text:
      `Not yet — no model is proven on live forecasts. Closest: ${best.modelKey}, ` +
      `${formatSkill(best.all.skill)} on ${forecasts(best.all.liveCount)}, ${tail}`,
  }
}

// ---------- calibration ----------

export interface ReliabilityPoint {
  /** What the model said, on average, in this bin (0–1). */
  x: number
  /** How often it happened (0–1). */
  y: number
  n: number
  from: number
  to: number
}

/**
 * The reliability diagram's points: one per non-empty bin, at (said, happened),
 * in order of probability. A perfectly calibrated model sits on the diagonal.
 * Bins with nothing in them, or numbers that are not numbers, are left out.
 */
export function reliabilityPoints(bins: readonly CalibrationBin[] | null | undefined): ReliabilityPoint[] {
  const clamp = (v: number) => Math.min(1, Math.max(0, v))
  return (bins ?? [])
    .filter((b) => finite(b.n) && b.n > 0 && finite(b.meanP) && finite(b.hitRate))
    .map((b) => ({ x: clamp(b.meanP), y: clamp(b.hitRate), n: b.n, from: b.from, to: b.to }))
    .sort((a, b) => a.x - b.x)
}

/** "30–40%: said 35%, happened 39% of 18" — a reliability point's tooltip. */
export function reliabilityLabel(p: ReliabilityPoint): string {
  return `${Math.round(p.from * 100)}–${Math.round(p.to * 100)}%: said ${formatProb(p.x)}, happened ${formatProb(p.y)} of ${p.n}`
}

/** "81% inside the 80% band" — how often the interval held the actual range. */
export function coverageText(coverage: number | null | undefined): string {
  if (!finite(coverage)) return '—'
  return `${formatProb(coverage)} inside the 80% band`
}

/**
 * A coverage far from 80% is a model whose band is too narrow or too wide.
 * Judged only once there is a first reading; ten points either side is allowed.
 */
export function coverageTone(coverage: number | null | undefined, liveCount: number): 'warn' | undefined {
  if (!finite(coverage) || liveCount < PROOF.firstReadingAt) return undefined
  return Math.abs(coverage - 0.8) > 0.1 ? 'warn' : undefined
}

// ---------- the range ruler ----------

/**
 * The scale of a card's range ruler: from 0 to a round number past everything
 * drawn on it (the band, the baseline, the actual range, the bucket edges), so
 * nothing lands on the edge. `at` turns a percentage into a position, 0–100.
 */
export function rangeScale(values: ReadonlyArray<number | null | undefined>): {
  max: number
  ticks: number[]
  at: (v: number) => number
} {
  const top = Math.max(0.5, ...values.filter(finite)) * 1.12
  const step = top > 4 ? 1 : top > 2 ? 0.5 : 0.25
  const max = Math.ceil(top / step) * step
  const tickStep = max > 4 ? 1 : 0.5
  const ticks: number[] = []
  for (let t = 0; t <= max + 1e-9; t += tickStep) ticks.push(Math.round(t * 100) / 100)
  return { max, ticks, at: (v: number) => Math.min(100, Math.max(0, (v / max) * 100)) }
}

// ---------- today ----------

/** Today's date in IST, yyyy-MM-dd. */
export function istToday(nowMs: number): string {
  return istDate(new Date(nowMs))
}

/** Minutes since IST midnight. */
export function istMinutes(nowMs: number): number {
  const d = new Date(nowMs)
  return (d.getUTCHours() * 60 + d.getUTCMinutes() + 330) % 1440
}

export function addDays(iso: string, days: number): string {
  const [y, m, d] = iso.split('-').map(Number)
  return new Date(Date.UTC(y, m - 1, d + days)).toISOString().slice(0, 10)
}


/**
 * Which session the Today view shows: today's when there is one; else the next
 * one already issued; else the latest past session, so a weekend shows
 * Friday's forecasts and how they did — labelled as the last session.
 */
export function pickSession(
  list: readonly Forecast[] | undefined,
  today: string,
): { sessionDate: string; relation: 'today' | 'next' | 'last' } | null {
  if (!list || list.length === 0) return null
  const dates = [...new Set(list.map((f) => f.sessionDate))].sort()
  if (dates.includes(today)) return { sessionDate: today, relation: 'today' }
  const upcoming = dates.find((d) => d > today)
  if (upcoming) return { sessionDate: upcoming, relation: 'next' }
  return { sessionDate: dates[dates.length - 1], relation: 'last' }
}

/**
 * When today's forecasts come, for a page that has none for today. Knows the
 * API refuses a forecast once the session has opened, so after 09:15 it says
 * there will be none rather than "not yet".
 */
export function issueNote(input: {
  nowMs: number
  hasToday: boolean
  isTradingDay?: boolean
  nextOpenUtc?: string | null
}): { tone: 'muted' | 'warn'; text: string } | null {
  if (input.hasToday) return null
  const minutes = istMinutes(input.nowMs)
  if (input.isTradingDay === true) {
    if (minutes < ISSUE_MIN) {
      const wait = ISSUE_MIN - minutes
      const h = Math.floor(wait / 60)
      const m = wait % 60
      const eta = h > 0 ? `${h}h ${m}m` : `${m}m`
      return {
        tone: 'muted',
        text: `Today's forecasts are issued at ${SCHEDULE.issue} IST, before the ${SCHEDULE.open} open — in ${eta}.`,
      }
    }
    if (minutes < OPEN_MIN) {
      return {
        tone: 'warn',
        text:
          `Due at ${SCHEDULE.issue} IST and not issued yet. The API refuses a forecast for today once ` +
          `the session opens at ${SCHEDULE.open}.`,
      }
    }
    return {
      tone: 'warn',
      text:
        `Nothing was issued for today. Forecasts are written at ${SCHEDULE.issue} IST and cannot be ` +
        `added after the ${SCHEDULE.open} open, so today has none.`,
    }
  }
  if (input.isTradingDay === false) {
    const next = input.nextOpenUtc ? istDate(new Date(input.nextOpenUtc)) : null
    return {
      tone: 'muted',
      text: next
        ? `No session today. The next forecasts are issued at ${SCHEDULE.issue} IST on ${formatDay(next)}.`
        : `No session today. The next forecasts are issued at ${SCHEDULE.issue} IST on the next trading day.`,
    }
  }
  return {
    tone: 'muted',
    text: `Forecasts are issued at ${SCHEDULE.issue} IST on trading days and scored after ${SCHEDULE.score} IST.`,
  }
}

/** One index's card on the Today view. */
export interface TodayCard {
  underlying: string
  /** Every range forecast for the session, the one to lead with first. */
  range: Forecast[]
  trend: Forecast[]
  direction: Forecast[]
}

/**
 * The session's forecasts as one card per index, in the page's index order.
 * Where two models forecast the same target, the one with the stronger live
 * record leads (see {@link leadScore}); the others are shown under it.
 */
export function todayCards(
  list: readonly Forecast[] | undefined,
  sessionDate: string,
  groups: readonly ModelGroup[] = [],
): TodayCard[] {
  const byId = new Map(groups.map((g) => [g.id, g]))
  const score = (f: Forecast) => leadScore(byId.get(`${f.modelKey}@${f.modelVersion}`))
  const order = (a: Forecast, b: Forecast) =>
    score(b) - score(a) || a.modelKey.localeCompare(b.modelKey) || newerFirst(a.modelVersion, b.modelVersion)

  const cards = new Map<string, TodayCard>()
  for (const f of list ?? []) {
    if (f.sessionDate !== sessionDate) continue
    const card = cards.get(f.underlying) ?? { underlying: f.underlying, range: [], trend: [], direction: [] }
    if (f.target === 'range' || f.target === 'trend' || f.target === 'direction') card[f.target].push(f)
    cards.set(f.underlying, card)
  }
  for (const card of cards.values()) {
    card.range.sort(order)
    card.trend.sort(order)
    card.direction.sort(order)
  }
  return [...cards.values()].sort(
    (a, b) => underlyingOrder(a.underlying) - underlyingOrder(b.underlying) || a.underlying.localeCompare(b.underlying),
  )
}

/** Where an issued forecast stands on scoring. */
export function scoringNote(f: Pick<Forecast, 'outcome' | 'sessionDate'>, today: string): { tone: 'muted' | 'warn'; text: string } | null {
  if (f.outcome) return null
  if (f.sessionDate > today) return { tone: 'muted', text: 'The session has not started.' }
  if (f.sessionDate === today) return { tone: 'muted', text: `Scored after ${SCHEDULE.score} IST.` }
  return {
    tone: 'warn',
    text:
      'Not scored yet. The scorer waits for the session\'s bars to be complete and retries on every run; ' +
      'after four days it reports an error.',
  }
}

// ---------- the models, in words ----------

/**
 * What each model is (analysis/models.py, docs/modules/analysis.md): `short`
 * for a second line under its key, `about` for its tooltip when the API's own
 * registered description is not at hand. The registered one wins when it is.
 */
export const MODEL_META: Record<string, { short: string; about: string }> = {
  'range.har': {
    short: 'recent ranges',
    about:
      "Log-range on the logs of the 1-, 5- and 22-session mean ranges (OLS); a log-normal around it with the training residuals' spread.",
  },
  'range.har-vix': {
    short: 'recent ranges + India VIX',
    about:
      "Log-range on the logs of the 1-, 5- and 22-session mean ranges, plus India VIX's previous close (log), expiry-day and Monday flags.",
  },
  'trend.logit': {
    short: 'logistic, 7 inputs',
    about:
      'Logistic regression (ridge) for a trend day (|close − open| ≥ 0.6 × range) on the previous range against its ' +
      "20-session mean, previous return and efficiency, India VIX's previous close and 5-session change, expiry-day " +
      'and Monday flags.',
  },
  'direction.logit': {
    short: 'logistic, 7 inputs · control',
    about:
      'Logistic regression (ridge) for close > open, on the same seven inputs as trend.logit. A control: nothing in ' +
      "the desk's research predicts intraday direction.",
  },
}

const CUES_SUFFIX = '-cues'
const CONTROL_SUFFIX = ' · control'
const CUES_ABOUT =
  ' plus the pre-open context: overnight US and Asian moves, US VIX, the rupee, oil, FII index-futures ' +
  "positioning, NSE breadth, the heavyweights' previous session and RBI/Fed/Budget/data-release days."

/** Version 2's "-cues" model's version 1 model, or null. */
function cuesBase(key: string): string | null {
  return key.endsWith(CUES_SUFFIX) ? key.slice(0, -CUES_SUFFIX.length) : null
}

/** "recent ranges + India VIX" — a model in a few words; '' for a model this page does not know. */
export function modelShort(key: string): string {
  const own = MODEL_META[key]
  if (own) return own.short
  const base = cuesBase(key)
  const meta = base ? MODEL_META[base] : undefined
  if (!meta) return ''
  const control = meta.short.endsWith(CONTROL_SUFFIX)
  return `${meta.short.replace(CONTROL_SUFFIX, '')} + pre-open cues${control ? CONTROL_SUFFIX : ''}`
}

/** A model's description: the registered one when there is one, else what this page knows; '' when neither. */
export function modelAbout(key: string, registered?: string | null): string {
  if (registered && registered.trim()) return registered.trim()
  const own = MODEL_META[key]
  if (own) return own.about
  const base = cuesBase(key)
  return base && MODEL_META[base] ? `${base}${CUES_ABOUT}` : ''
}

/** A model key's tooltip: "range.har-vix · version 2026-09-27.1 — Log-range on …". */
export function modelTitle(key: string, version?: string | null, registered?: string | null): string {
  const about = modelAbout(key, registered)
  return `${key}${version ? ` · version ${version}` : ''}${about ? ` — ${about}` : ''}`
}

// ---------- times and signed numbers ----------

const IST_OFFSET_MS = 330 * 60_000
const MINUS = '−'

function pad2(n: number): string {
  return String(n).padStart(2, '0')
}

function parseStamp(iso: unknown): number | null {
  if (typeof iso !== 'string' || !iso) return null
  const ms = Date.parse(iso)
  return Number.isFinite(ms) ? ms : null
}

/** "08:43 IST" — a UTC stamp as the IST clock read it; '—' when missing or unreadable. */
export function formatIstClock(iso: string | null | undefined): string {
  const ms = parseStamp(iso)
  if (ms == null) return '—'
  const d = new Date(ms + IST_OFFSET_MS)
  return `${pad2(d.getUTCHours())}:${pad2(d.getUTCMinutes())} IST`
}

/** "28 Sept, 08:43 IST" — a UTC stamp with its IST date. */
export function formatIstStamp(iso: string | null | undefined): string {
  const ms = parseStamp(iso)
  if (ms == null) return '—'
  const day = new Date(ms).toLocaleDateString('en-IN', { day: 'numeric', month: 'short', timeZone: 'Asia/Kolkata' })
  return `${day}, ${formatIstClock(iso)}`
}

/** "25 Sept" — a yyyy-MM-dd date, short, for a table cell. */
export function shortDay(date: string): string {
  const [y, m, d] = date.split('-').map(Number)
  if (!y || !m || !d) return date
  return new Date(Date.UTC(y, m - 1, d)).toLocaleDateString('en-IN', { day: 'numeric', month: 'short', timeZone: 'UTC' })
}

/**
 * "issued 08:50 IST", or "issued 08:50–09:05 IST" when a session's forecasts
 * went out at different minutes (a re-run for one index); '' with none.
 */
export function issuedText(forecasts: ReadonlyArray<Pick<Forecast, 'issuedUtc'>>): string {
  const stamps = forecasts.map((f) => parseStamp(f.issuedUtc)).filter((ms): ms is number => ms != null)
  if (stamps.length === 0) return ''
  const first = formatIstClock(new Date(Math.min(...stamps)).toISOString())
  const last = formatIstClock(new Date(Math.max(...stamps)).toISOString())
  return first === last ? `issued ${first}` : `issued ${first.replace(' IST', '')}–${last}`
}

/** "+0.34", "−0.41", "0.00" — signed, Indian grouping, a true minus; nothing that rounds to zero gets a sign. */
export function formatSigned(value: number | null | undefined, digits = 2): string {
  if (!finite(value)) return '—'
  const rounded = Number(value.toFixed(digits))
  if (rounded === 0) return num(0, digits)
  return `${rounded > 0 ? '+' : MINUS}${num(Math.abs(value), digits)}`
}

/** "+0.34%", "−0.41%", "0.00%". */
export function formatSignedPct(value: number | null | undefined, digits = 2): string {
  const t = formatSigned(value, digits)
  return t === '—' ? t : `${t}%`
}

// ---------- a value this page has no layout for ----------

/**
 * Anything the API sends that this page has no particular layout for, as a
 * small tree the page draws as nested label/value lists: never as JSON.
 */
export type ValueNode =
  | { kind: 'text'; text: string }
  | { kind: 'fields'; fields: Array<{ key: string; label: string; node: ValueNode }> }
  | { kind: 'items'; items: ValueNode[] }

const TREE_DEPTH = 4
const TREE_ITEMS = 40
const ISO_STAMP = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:?\d{2})$/
const ISO_DAY = /^\d{4}-\d{2}-\d{2}$/

function textNode(t: string): ValueNode {
  return { kind: 'text', text: t }
}

/** "giftNiftyGapPct" → "Gift nifty gap pct", "trained_through" → "Trained through". */
export function humanizeKey(key: string): string {
  const words = key
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/[_\s-]+/g, ' ')
    .trim()
    .toLowerCase()
  return words ? words.charAt(0).toUpperCase() + words.slice(1) : key
}

/** A string as a reader wants it: a UTC stamp in IST, a date with its weekday, anything else as it is. */
function describeText(value: string): string {
  if (ISO_STAMP.test(value)) return formatIstStamp(value)
  if (ISO_DAY.test(value)) return formatDay(value)
  return value
}

/**
 * Any JSON value as a {@link ValueNode}: numbers with Indian grouping (at most
 * three decimals), true/false as yes/no, null as "—", UTC stamps in IST, a
 * list of plain values on one line, and objects as labelled fields. Past four
 * levels, or forty entries, it says how many more there are instead.
 */
export function describeValue(value: unknown, depth = 0): ValueNode {
  if (value == null) return textNode('—')
  if (typeof value === 'boolean') return textNode(value ? 'yes' : 'no')
  if (typeof value === 'number') {
    return textNode(Number.isFinite(value) ? value.toLocaleString('en-IN', { maximumFractionDigits: 3 }) : '—')
  }
  if (typeof value === 'string') return textNode(value.trim() === '' ? '—' : describeText(value))
  if (Array.isArray(value)) {
    if (value.length === 0) return textNode('none')
    if (value.every((v) => v == null || typeof v !== 'object')) {
      return textNode(value.map((v) => (describeValue(v) as { text: string }).text).join(', '))
    }
    if (depth >= TREE_DEPTH) return textNode(`${value.length} items`)
    const items = value.slice(0, TREE_ITEMS).map((v) => describeValue(v, depth + 1))
    if (value.length > TREE_ITEMS) items.push(textNode(`and ${value.length - TREE_ITEMS} more`))
    return { kind: 'items', items }
  }
  if (typeof value === 'object') {
    const entries = Object.entries(value as Record<string, unknown>)
    if (entries.length === 0) return textNode('none')
    if (depth >= TREE_DEPTH) return textNode(`${entries.length} fields`)
    const fields = entries
      .slice(0, TREE_ITEMS)
      .map(([key, v]) => ({ key, label: humanizeKey(key), node: describeValue(v, depth + 1) }))
    if (entries.length > TREE_ITEMS) {
      fields.push({ key: '…', label: 'More', node: textNode(`and ${entries.length - TREE_ITEMS} more`) })
    }
    return { kind: 'fields', fields }
  }
  return textNode(String(value))
}

// ---------- the models' inputs ----------

type InputUnit =
  | 'pct'
  | 'pctSigned'
  | 'signed'
  | 'ratio'
  | 'times'
  | 'level'
  | 'price'
  | 'count'
  | 'countSigned'
  | 'flag'
  | 'date'
  | 'text'

/**
 * Every input the models record, in the order the page lists them, with its
 * unit (docs/modules/analysis.md: "Inputs", and version 2's "The inputs and
 * their point-in-time rules"). One not listed is shown under its own name.
 */
const INPUTS: ReadonlyArray<readonly [string, string, InputUnit]> = [
  ['prevSession', 'Inputs from session', 'date'],
  ['prevClose', 'Previous close', 'price'],
  ['r1', 'Range, last session', 'pct'],
  ['r5', 'Range, 5-session mean', 'pct'],
  ['r22', 'Range, 22-session mean', 'pct'],
  ['rangeRatio', 'Last range ÷ 20-session mean', 'times'],
  ['prevReturn', 'Previous close-to-close', 'pctSigned'],
  ['prevEfficiency', 'Previous session efficiency', 'ratio'],
  ['vixPrevClose', 'India VIX, previous close', 'level'],
  ['vixChange5', 'India VIX, 5-session change', 'signed'],
  ['expiryDay', 'Expiry day', 'flag'],
  ['monday', 'Monday', 'flag'],
  // Version 2's context. Markets that close after India: since India's previous close.
  ['spxRet', 'S&P 500', 'pctSigned'],
  ['ndxRet', 'Nasdaq 100', 'pctSigned'],
  ['djiRet', 'Dow Jones', 'pctSigned'],
  ['esRet', 'S&P 500 futures', 'pctSigned'],
  ['usVix', 'US VIX', 'level'],
  ['usVixChange', 'US VIX, change', 'signed'],
  ['brentRet', 'Brent crude', 'pctSigned'],
  ['dxyRet', 'Dollar index', 'pctSigned'],
  ['us10yChange', 'US 10-year yield, change', 'signed'],
  ['usdinrRet', 'USD/INR', 'pctSigned'],
  ['n225Ret', 'Nikkei 225, last session', 'pctSigned'],
  ['hsiRet', 'Hang Seng, last session', 'pctSigned'],
  ['ks11Ret', 'KOSPI, last session', 'pctSigned'],
  ['asiaRet', 'Asia, mean of the three', 'pctSigned'],
  ['fiiNetLong', 'FII index futures, net long', 'pctSigned'],
  ['fiiChange1', 'FII net long, 1-day change', 'signed'],
  ['fiiChange5', 'FII net long, 5-day change', 'signed'],
  ['adRatio', 'Advances ÷ declines', 'ratio'],
  ['pctAdvancing', 'Stocks advancing', 'pct'],
  ['netHighsLows', '52-week highs − lows', 'countSigned'],
  ['netHighsLowsPct', '52-week highs − lows, % of traded', 'pctSigned'],
  ['hwRet', 'Heavyweights, mean return', 'pctSigned'],
  ['hwDispersion', 'Heavyweights, dispersion', 'pct'],
  ['hwCount', 'Heavyweights counted', 'count'],
  ['majorEvent', 'RBI, Fed or Budget day', 'flag'],
  ['majorEve', 'RBI, Fed or Budget next session', 'flag'],
  ['majorAfter', 'Day after RBI, Fed or Budget', 'flag'],
  ['dataRelease', 'US CPI, US jobs or India CPI day', 'flag'],
  ['events', 'Events', 'text'],
  ['trainingSessions', 'Training sessions', 'count'],
  ['trainedThrough', 'Trained through', 'date'],
]

const INPUT_SPEC = new Map(INPUTS.map(([key, label, unit], i) => [key, { label, unit, order: i }]))

/** Recorded on every forecast, but shown by the page once, on its own (the session's pre-open context). */
const OWN_LAYOUT_INPUTS = new Set(['liveOnly'])

export function inputLabel(key: string): string {
  return INPUT_SPEC.get(key)?.label ?? humanizeKey(key)
}

/** One input's value in words: its unit, Indian grouping, a true minus, yes/no for the 0/1 facts. */
export function formatInputValue(key: string, value: unknown): string {
  const unit = INPUT_SPEC.get(key)?.unit
  if (value == null) return '—'
  if (typeof value === 'boolean') return value ? 'yes' : 'no'
  if (unit === 'flag' && (value === 0 || value === 1)) return value === 1 ? 'yes' : 'no'
  if (typeof value === 'string') {
    if (unit === 'date' && ISO_DAY.test(value)) return shortDay(value)
    if (value.trim() === '') return unit === 'text' ? 'none' : '—'
    return describeText(value)
  }
  if (!finite(value)) return '—'
  switch (unit) {
    case 'pct':
      return formatPct(value, 2)
    case 'pctSigned':
      return formatSignedPct(value, 2)
    case 'signed':
      return formatSigned(value, 2)
    case 'ratio':
    case 'level':
    case 'price':
      return num(value, 2)
    case 'times':
      return `${num(value, 2)}×`
    case 'count':
      return Math.round(value).toLocaleString('en-IN')
    case 'countSigned':
      return formatSigned(value, 0)
    default:
      return value.toLocaleString('en-IN', { maximumFractionDigits: 3 })
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return value != null && typeof value === 'object' && !Array.isArray(value)
}

/** A value with its object keys sorted, so two equal records compare equal whatever their key order. */
function sortedKeys(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(sortedKeys)
  if (isRecord(value)) {
    return Object.fromEntries(Object.keys(value).sort().map((k) => [k, sortedKeys(value[k])]))
  }
  return value
}

/** Two JSON values' identity, for telling them apart. Serialised only to compare; never shown. */
function sameKey(value: unknown): string {
  return JSON.stringify(sortedKeys(value)) ?? 'undefined'
}

function inputNode(key: string, value: unknown): ValueNode {
  return value != null && typeof value === 'object' ? describeValue(value) : textNode(formatInputValue(key, value))
}

/** An input the same for every model that records it, shown once. */
export interface InputFact {
  key: string
  label: string
  value: ValueNode
}

/** The inputs a set of models share, under those models' names. */
export interface InputGroup {
  /** Model keys, in the card's order; `all` when every model on the card records these. */
  models: string[]
  all: boolean
  facts: InputFact[]
}

/** Inputs whose values differ from model to model (how much history each was fitted on): one row per model. */
export interface PerModelInputs {
  keys: Array<{ key: string; label: string }>
  rows: Array<{ model: string; modelKey: string; modelVersion: string; cells: Array<ValueNode | null> }>
}

export interface InputsView {
  /** The session every model's inputs come from, when they all say the same one (then it is not listed). */
  prevSession: string | null
  groups: InputGroup[]
  perModel: PerModelInputs | null
}

/**
 * What a card's models saw. The morning's facts are the same whichever model
 * reads them, so each is shown once, under the models that record it, in
 * the documented order; what differs by model (training sessions, trained
 * through) is a small table of its own. The pre-open context is left out:
 * it is the same on every forecast, and the page shows it once, on its own.
 */
export function inputsView(forecasts: readonly Forecast[]): InputsView | null {
  const shown = forecasts.filter(
    (f) => isRecord(f.inputs) && Object.keys(f.inputs).some((k) => !OWN_LAYOUT_INPUTS.has(k)),
  )
  if (shown.length === 0) return null
  const inputs = shown.map((f) => f.inputs as Record<string, unknown>)
  const keyCount = new Map<string, number>()
  for (const f of shown) keyCount.set(f.modelKey, (keyCount.get(f.modelKey) ?? 0) + 1)
  const names = shown.map((f) => ((keyCount.get(f.modelKey) ?? 0) > 1 ? `${f.modelKey} ${f.modelVersion}` : f.modelKey))

  const sessions = new Set(inputs.map((i) => i.prevSession))
  const only = sessions.size === 1 ? [...sessions][0] : null
  const prevSession = typeof only === 'string' && ISO_DAY.test(only) ? only : null

  const firstSeen = new Map<string, number>()
  for (const i of inputs) {
    for (const key of Object.keys(i)) {
      if (OWN_LAYOUT_INPUTS.has(key) || (key === 'prevSession' && prevSession)) continue
      if (!firstSeen.has(key)) firstSeen.set(key, firstSeen.size)
    }
  }
  const rank = (key: string) => INPUT_SPEC.get(key)?.order ?? INPUTS.length + (firstSeen.get(key) ?? 0)
  const keys = [...firstSeen.keys()].sort((a, b) => rank(a) - rank(b))

  const groups = new Map<string, InputGroup>()
  const differing: string[] = []
  for (const key of keys) {
    const carriers = inputs.map((i, n) => (key in i ? n : -1)).filter((n) => n >= 0)
    const values = new Set(carriers.map((n) => sameKey(inputs[n][key])))
    if (values.size > 1) {
      differing.push(key)
      continue
    }
    const models = carriers.map((n) => names[n])
    const id = models.join('\u0000')
    const group = groups.get(id) ?? { models, all: carriers.length === shown.length && shown.length > 1, facts: [] }
    group.facts.push({ key, label: inputLabel(key), value: inputNode(key, inputs[carriers[0]][key]) })
    groups.set(id, group)
  }

  return {
    prevSession,
    groups: [...groups.values()],
    perModel:
      differing.length === 0
        ? null
        : {
            keys: differing.map((key) => ({ key, label: inputLabel(key) })),
            rows: shown.map((f, n) => ({
              model: names[n],
              modelKey: f.modelKey,
              modelVersion: f.modelVersion,
              cells: differing.map((key) => (key in inputs[n] ? inputNode(key, inputs[n][key]) : null)),
            })),
          },
  }
}

// ---------- the pre-open context (inputs.liveOnly) ----------

/** The news model's reading: within ±0.15 is level, as on the Desk. */
export const SENTIMENT_LEVEL = 0.15

/** The tone of a sentiment reading, −1 … +1; null when nothing was scored. */
export function sentimentTone(value: number | null | undefined): 'pos' | 'neg' | 'flat' | null {
  if (!finite(value)) return null
  if (value > SENTIMENT_LEVEL) return 'pos'
  if (value < -SENTIMENT_LEVEL) return 'neg'
  return 'flat'
}

/** "+0.71", "−0.33", "0.00"; "—" when nothing was scored. */
export function formatSentiment(value: number | null | undefined): string {
  return formatSigned(value, 2)
}

/** One `news_items` category (or NIFTY-50 companies' filings) in the pre-open context. */
export interface NewsRow {
  key: string
  label: string
  group: 'market' | 'sector' | 'other' | 'filings'
  /** Headlines first seen between the previous close and the forecasts. */
  n: number | null
  scored: number | null
  /** Mean sentiment of the scored ones, −1 … +1. */
  sentiment: number | null
  /** Highest importance among the scored ones, 0 … 3. */
  maxImportance: number | null
}

const NEWS_LABELS: Record<string, string> = {
  india: 'India',
  global: 'Global',
  auto: 'Auto',
  banking: 'Banking',
  commodities: 'Commodities',
  energy: 'Energy',
  fmcg: 'FMCG',
  it: 'IT',
  metals: 'Metals',
  pharma: 'Pharma',
  realty: 'Realty',
  uncategorised: 'Uncategorised',
  'nifty50 announcements': 'NIFTY 50 filings',
}

function newsGroup(key: string): NewsRow['group'] {
  if (key === 'india' || key === 'global') return 'market'
  if (key === 'nifty50 announcements') return 'filings'
  if (key === 'uncategorised') return 'other'
  return 'sector'
}

const NEWS_GROUP_ORDER: Record<NewsRow['group'], number> = { market: 0, sector: 1, other: 2, filings: 3 }

export function newsLabel(key: string): string {
  return NEWS_LABELS[key] ?? humanizeKey(key)
}

/** India and Global first, then the sectors A–Z, then uncategorised, then the NIFTY-50 filings. */
export function sortNewsRows(rows: readonly NewsRow[]): NewsRow[] {
  const marketRank = (key: string) => (key === 'india' ? 0 : key === 'global' ? 1 : 2)
  return [...rows].sort(
    (a, b) =>
      NEWS_GROUP_ORDER[a.group] - NEWS_GROUP_ORDER[b.group] ||
      marketRank(a.key) - marketRank(b.key) ||
      a.label.localeCompare(b.label),
  )
}

export interface LiveContext {
  /** False on every forecast so far: no model reads this. Null when the record does not say. */
  usedByModels: boolean | null
  gift: { gapPct: number | null; changePct: number | null; asOfUtc: string | null; fetchedUtc: string | null } | null
  /** Why there is no GIFT Nifty reading ("no snapshot this morning", "unavailable (…)"). */
  giftNote: string | null
  news: NewsRow[] | null
  newsNote: string | null
  earnings: { today: number | null; sincePrev: number | null } | null
  earningsNote: string | null
  /** Anything else recorded there, for the generic view. */
  other: Array<{ key: string; label: string; node: ValueNode }>
}

const CONTEXT_KEYS = new Set([
  'usedByModels',
  'giftNifty',
  'giftNiftyGapPct',
  'giftNiftyChangePct',
  'giftNiftyAsOf',
  'giftNiftyFetchedUtc',
  'news',
  'earnings',
  'earningsToday',
  'earningsSincePrev',
])

const numOrNull = (v: unknown): number | null => (finite(v) ? v : null)
const strOrNull = (v: unknown): string | null => (typeof v === 'string' && v.trim() ? v.trim() : null)

/**
 * `inputs.liveOnly` read into what the page draws: the GIFT Nifty gap, the
 * news since the previous close by category, and the earnings load. A part
 * the job could not read arrives as a sentence ("unavailable (…)") and is
 * kept as that sentence, never as a zero. Null when it is not an object.
 */
export function readLiveContext(raw: unknown): LiveContext | null {
  if (!isRecord(raw)) return null
  const other: LiveContext['other'] = []

  const giftKeys = ['giftNiftyGapPct', 'giftNiftyChangePct', 'giftNiftyAsOf', 'giftNiftyFetchedUtc']
  const gift = giftKeys.some((k) => k in raw)
    ? {
        gapPct: numOrNull(raw.giftNiftyGapPct),
        changePct: numOrNull(raw.giftNiftyChangePct),
        asOfUtc: strOrNull(raw.giftNiftyAsOf),
        fetchedUtc: strOrNull(raw.giftNiftyFetchedUtc),
      }
    : null

  let news: NewsRow[] | null = null
  let newsNote: string | null = null
  if (isRecord(raw.news)) {
    const rows: NewsRow[] = []
    for (const [key, v] of Object.entries(raw.news)) {
      if (!isRecord(v)) {
        other.push({ key: `news.${key}`, label: `News: ${newsLabel(key)}`, node: describeValue(v) })
        continue
      }
      rows.push({
        key,
        label: newsLabel(key),
        group: newsGroup(key),
        n: numOrNull(v.n),
        scored: numOrNull(v.scored),
        sentiment: numOrNull(v.sentiment),
        maxImportance: numOrNull(v.maxImportance),
      })
    }
    news = sortNewsRows(rows)
  } else if (typeof raw.news === 'string') {
    newsNote = strOrNull(raw.news)
  } else if (raw.news != null) {
    other.push({ key: 'news', label: 'News', node: describeValue(raw.news) })
  }

  const earnings =
    'earningsToday' in raw || 'earningsSincePrev' in raw
      ? { today: numOrNull(raw.earningsToday), sincePrev: numOrNull(raw.earningsSincePrev) }
      : null

  for (const [key, v] of Object.entries(raw)) {
    if (!CONTEXT_KEYS.has(key)) other.push({ key, label: humanizeKey(key), node: describeValue(v) })
  }

  return {
    usedByModels: typeof raw.usedByModels === 'boolean' ? raw.usedByModels : null,
    gift,
    giftNote: strOrNull(raw.giftNifty),
    news,
    newsNote,
    earnings,
    earningsNote: strOrNull(raw.earnings),
    other,
  }
}

/** One distinct pre-open context of a session, and which forecasts carry it. */
export interface SessionContext {
  context: LiveContext
  count: number
  underlyings: string[]
  issuedUtc: string
}

/**
 * The session's pre-open context, once. The morning's job records the same
 * `liveOnly` on every forecast it writes, so a session normally has one; a
 * forecast written by a later run carries its own, and each distinct one is
 * returned, the most widely carried first.
 */
export function sessionContexts(forecasts: readonly Forecast[]): SessionContext[] {
  const groups = new Map<string, { raw: unknown; list: Forecast[] }>()
  for (const f of forecasts) {
    const raw = isRecord(f.inputs) ? f.inputs.liveOnly : undefined
    if (!isRecord(raw)) continue
    const key = sameKey(raw)
    const g = groups.get(key) ?? { raw, list: [] }
    g.list.push(f)
    groups.set(key, g)
  }
  const out: SessionContext[] = []
  for (const g of groups.values()) {
    const context = readLiveContext(g.raw)
    if (!context) continue
    const underlyings = [...new Set(g.list.map((f) => f.underlying))].sort(
      (a, b) => underlyingOrder(a) - underlyingOrder(b) || a.localeCompare(b),
    )
    const issuedUtc = g.list.map((f) => f.issuedUtc).sort()[0]
    out.push({ context, count: g.list.length, underlyings, issuedUtc })
  }
  return out.sort((a, b) => b.count - a.count || a.issuedUtc.localeCompare(b.issuedUtc))
}

// ---------- history rows ----------

/** A past forecast in words, for the History table. */
export interface ForecastCells {
  forecast: string
  baseline: string
  outcome: string
  /** Whether it beat the baseline on the target's loss; null until scored. */
  beat: boolean | null
  lossTitle: string
}

export function forecastCells(f: Forecast): ForecastCells {
  const s = f.scores
  const beat = s && finite(s.loss) && finite(s.baselineLoss) ? s.loss < s.baselineLoss : null
  const lossTitle =
    s && finite(s.loss) && finite(s.baselineLoss)
      ? `loss ${num(s.loss, 3)} vs baseline ${num(s.baselineLoss, 3)} (lower is better)`
      : 'not scored yet'

  if (f.target === 'range') {
    const r = asRange(f.prediction)
    const b = asRange(f.baseline)
    const o = f.outcome
    const covered = s?.metrics?.covered80
    const inside = covered === true ? ' · inside' : covered === false ? ' · outside' : ''
    return {
      forecast: r ? `${formatPct(r.median)} (${num(r.low80, 2)}–${formatPct(r.high80)})` : '—',
      baseline: b ? formatPct(b.median) : '—',
      outcome: o ? `${formatPct(o.range)} · ${bucketLabel(o.bucket)}${inside}` : '—',
      beat,
      lossTitle,
    }
  }
  const p = asProb(f.prediction)
  const b = asProb(f.baseline)
  let outcome = '—'
  if (f.outcome) {
    outcome =
      f.target === 'trend'
        ? f.outcome.trendDay
          ? 'trend day'
          : 'no trend day'
        : f.outcome.up
          ? 'closed up'
          : 'closed down'
  }
  return { forecast: formatProb(p), baseline: formatProb(b), outcome, beat, lossTitle }
}

/** Newest session first; within a session, index order, then target order, then model. */
export function sortForecasts(list: readonly Forecast[]): Forecast[] {
  return [...list].sort(
    (a, b) =>
      b.sessionDate.localeCompare(a.sessionDate) ||
      underlyingOrder(a.underlying) - underlyingOrder(b.underlying) ||
      targetOrder(a.target) - targetOrder(b.target) ||
      a.modelKey.localeCompare(b.modelKey) ||
      newerFirst(a.modelVersion, b.modelVersion),
  )
}

// ---------- requests ----------

export interface ForecastFilters {
  from?: string
  to?: string
  target?: ForecastTarget | ''
  underlying?: string
}

/** The query string for GET /api/Forecasts. Empty filters are left out, not sent blank. */
export function forecastsQuery(filters: ForecastFilters): string {
  const params = new URLSearchParams()
  if (filters.from) params.set('from', filters.from)
  if (filters.to) params.set('to', filters.to)
  if (filters.target) params.set('target', filters.target)
  if (filters.underlying) params.set('underlying', filters.underlying)
  return params.toString()
}

/**
 * A list endpoint's body, read strictly: a bare array or `{ items: [...] }`.
 * Anything else throws, so the page shows an error rather than an empty
 * scoreboard that would read as "nothing to judge".
 */
function readList<T>(body: unknown, what: string): T[] {
  if (Array.isArray(body)) return body as T[]
  if (body && typeof body === 'object' && Array.isArray((body as { items?: unknown }).items)) {
    return (body as { items: T[] }).items
  }
  throw new Error(`The ${what} endpoint answered in a shape this page cannot read.`)
}

export const readForecastList = (body: unknown) => readList<Forecast>(body, 'forecasts')
export const readScoreboard = (body: unknown) => readList<ScoreboardRow>(body, 'scoreboard')
export const readForecastModels = (body: unknown) => readList<ForecastModel>(body, 'models')

/**
 * The two failures worth their own words: the account lacks the module (403),
 * and this server does not have the Forecasts API yet (404). Anything else is
 * shown as the error it is.
 */
export function analysisErrorText(error: unknown): string | null {
  const status = (error as { status?: number } | null)?.status
  if (status === 403) {
    return 'This account does not hold the Analysis module. An admin can grant it under Users & access.'
  }
  if (status === 404) {
    return 'This server does not have the Forecasts API yet. The page fills in once it is deployed.'
  }
  return null
}
