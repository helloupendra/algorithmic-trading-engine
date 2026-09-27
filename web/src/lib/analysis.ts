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

// ---------- the model's inputs ----------

/** The inputs docs/modules/analysis.md defines ("How the models work → Inputs"). */
const INPUT_LABELS: Record<string, string> = {
  vixPrevClose: 'India VIX, previous close',
  vixChange5: 'India VIX, 5-session change',
  r1: 'Range, last session %',
  r5: 'Range, 5-session mean %',
  r22: 'Range, 22-session mean %',
  rangeRatio: 'Last range ÷ 20-session mean',
  prevReturn: 'Previous close-to-close %',
  prevEfficiency: 'Previous session efficiency',
  expiryDay: 'Expiry day',
  monday: 'Monday',
  prevSession: 'Inputs from session',
  trainingSessions: 'Training sessions',
  trainedThrough: 'Trained through',
}

/** Facts the models send as 0/1; shown as yes/no. */
const FLAG_INPUTS = new Set(['expiryDay', 'monday'])

/** `inputs` as label/value pairs, in the order the model sent them. */
export function formatInputs(inputs: Record<string, unknown> | null | undefined): Array<[string, string]> {
  if (!inputs || typeof inputs !== 'object') return []
  return Object.entries(inputs).map(([key, value]) => {
    const label = INPUT_LABELS[key] ?? key
    let text: string
    if (typeof value === 'boolean') text = value ? 'yes' : 'no'
    else if (FLAG_INPUTS.has(key) && (value === 0 || value === 1)) text = value === 1 ? 'yes' : 'no'
    else if (finite(value)) text = value.toLocaleString('en-IN', { maximumFractionDigits: 3 })
    else if (value == null) text = '—'
    else if (typeof value === 'string') text = value
    else text = JSON.stringify(value)
    return [label, text]
  })
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
