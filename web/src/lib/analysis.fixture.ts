/**
 * A contract-shaped stand-in for the Forecasts API (docs/modules/analysis.md),
 * for development and tests. The numbers are made up; the shapes are not.
 *
 * Production code never reaches it. The queries import it only behind
 * `import.meta.env.DEV && VITE_ANALYSIS_MOCK === '1'`, which a production build
 * folds to `false`, dropping the branch and this file with it. To review the
 * Analysis page before the API has its endpoints:
 *
 *   VITE_ANALYSIS_MOCK=1 npm run dev
 *
 * It simulates the last `sessions` weekdays up to today for NIFTY, BANKNIFTY
 * and SENSEX and the four models of version 1 — direction.logit registered a
 * week after the others, so it is still collecting per index — then scores and
 * ranks them by the contract's rules: skill, a 2,000-resample bootstrap
 * interval over sessions with a fixed seed, the status thresholds, coverage and
 * calibration in tenths. So the scoreboard agrees with the forecasts it is
 * built from, the way the real one must.
 *
 * Imports types only, so Node can run it as-is (the screenshot harness does).
 */

import type {
  CalibrationBin,
  Forecast,
  ForecastModel,
  ForecastOutcome,
  ForecastTarget,
  ModelStatus,
  RangeBucket,
  RangePrediction,
  ScoreboardRow,
} from './analysis'

export interface AnalysisFixture {
  today: string
  models: ForecastModel[]
  forecasts: Forecast[]
  scoreboard: ScoreboardRow[]
}

// ---------- small maths ----------

/** mulberry32: a tiny seeded generator, so a date always builds the same fixture. */
function rng(seed: number): () => number {
  let a = seed >>> 0
  return () => {
    a = (a + 0x6d2b79f5) >>> 0
    let t = a
    t = Math.imul(t ^ (t >>> 15), t | 1)
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61)
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
}

function gauss(r: () => number): number {
  const u = Math.max(r(), 1e-12)
  return Math.sqrt(-2 * Math.log(u)) * Math.cos(2 * Math.PI * r())
}

/** Standard normal CDF (Abramowitz & Stegun 7.1.26). */
function phi(x: number): number {
  const t = 1 / (1 + 0.3275911 * Math.abs(x / Math.SQRT2))
  const y =
    1 - ((((1.061405429 * t - 1.453152027) * t + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t *
      Math.exp(-(x * x) / 2)
  return x >= 0 ? (1 + y) / 2 : (1 - y) / 2
}

const logistic = (x: number) => 1 / (1 + Math.exp(-x))
const round = (v: number, d: number) => Math.round(v * 10 ** d) / 10 ** d
const mean = (xs: number[]) => xs.reduce((a, b) => a + b, 0) / xs.length


// ---------- calendar ----------

function istParts(nowMs: number): { date: string; minutes: number } {
  const ist = new Date(nowMs + 330 * 60_000)
  return { date: ist.toISOString().slice(0, 10), minutes: ist.getUTCHours() * 60 + ist.getUTCMinutes() }
}

function shift(iso: string, days: number): string {
  const [y, m, d] = iso.split('-').map(Number)
  return new Date(Date.UTC(y, m - 1, d + days)).toISOString().slice(0, 10)
}

function weekday(iso: string): number {
  const [y, m, d] = iso.split('-').map(Number)
  return new Date(Date.UTC(y, m - 1, d)).getUTCDay()
}

/** The last `count` weekdays up to and including `today`, oldest first. */
function weekdays(today: string, count: number): string[] {
  const out: string[] = []
  for (let d = today; out.length < count; d = shift(d, -1)) {
    const w = weekday(d)
    if (w !== 0 && w !== 6) out.push(d)
  }
  return out.reverse()
}

// ---------- the models ----------

const period = (from: string, to: string, n: number, loss: number, baselineLoss: number) => ({
  from,
  to,
  n,
  loss,
  baselineLoss,
  skill: round(1 - loss / baselineLoss, 3),
})

const MODELS: Array<Omit<ForecastModel, 'version'> & { startsAt: number }> = [
  {
    key: 'range.har',
    target: 'range',
    description: 'Log-range on 1-, 5- and 22-day mean log ranges.',
    startsAt: 0,
    backtest: {
      design: period('2021-08-04', '2024-12-31', 850, 0.239, 0.262),
      validation: period('2025-01-01', '2025-12-31', 247, 0.232, 0.251),
      holdout: period('2026-01-01', '2026-09-25', 180, 0.246, 0.259),
      byUnderlying: { NIFTY: { n: 1277, skill: 0.07 }, BANKNIFTY: { n: 1277, skill: 0.06 }, SENSEX: { n: 1277, skill: 0.08 } },
      configurationsTried: 3,
      notes: 'The plain HAR baseline for range.har-vix: same data, no VIX.',
    },
  },
  {
    key: 'range.har-vix',
    target: 'range',
    description:
      "Log-range on 1-, 5- and 22-day mean log ranges, plus India VIX's previous close, expiry-day and Monday flags.",
    startsAt: 0,
    backtest: {
      design: period('2021-08-04', '2024-12-31', 850, 0.231, 0.262),
      validation: period('2025-01-01', '2025-12-31', 247, 0.225, 0.251),
      holdout: period('2026-01-01', '2026-09-25', 180, 0.24, 0.259),
      byUnderlying: { NIFTY: { n: 1277, skill: 0.1 }, BANKNIFTY: { n: 1277, skill: 0.09 }, SENSEX: { n: 1277, skill: 0.11 } },
      configurationsTried: 4,
      notes: 'VIX adds most on expiry days; the Monday flag is small.',
    },
  },
  {
    key: 'trend.logit',
    target: 'trend',
    description:
      "Logistic regression for a trend day on recent ranges, the previous session's efficiency, India VIX and an expiry-day flag.",
    startsAt: 0,
    backtest: {
      design: period('2021-08-04', '2024-12-31', 850, 0.2, 0.207),
      validation: period('2025-01-01', '2025-12-31', 247, 0.204, 0.208),
      holdout: period('2026-01-01', '2026-09-25', 180, 0.207, 0.209),
      configurationsTried: 6,
      notes: null,
    },
  },
  {
    key: 'direction.logit',
    target: 'direction',
    description:
      "Logistic regression for an up close on the previous session's return, India VIX and a Monday flag. Kept as a control.",
    startsAt: 7,
    backtest: {
      design: period('2021-08-04', '2024-12-31', 850, 0.2489, 0.2493),
      validation: period('2025-01-01', '2025-12-31', 247, 0.2501, 0.2494),
      holdout: period('2026-01-01', '2026-09-25', 180, 0.2512, 0.2497),
      configurationsTried: 5,
      notes: 'Expected to stay at the baseline: 150 technical configurations found no intraday direction edge.',
    },
  },
]

/** The simulation's seed. Chosen so the scoreboard shows an honest early state; see the tests. */
export const FIXTURE_SEED = 8

const INDICES = {
  NIFTY: { close: 24650.3, typical: 0.95, edges: [0.75, 1.25] as [number, number], expiry: 2 },
  BANKNIFTY: { close: 54120.6, typical: 1.1, edges: [0.85, 1.4] as [number, number], expiry: -1 },
  SENSEX: { close: 80540.2, typical: 0.9, edges: [0.72, 1.2] as [number, number], expiry: 4 },
} as const

type Index = keyof typeof INDICES

/** NIFTY expires on Tuesdays, SENSEX on Thursdays, BANKNIFTY on the month's last Tuesday. */
function isExpiry(index: Index, date: string): boolean {
  const w = weekday(date)
  const spec = INDICES[index]
  if (spec.expiry >= 0) return w === spec.expiry
  return w === 2 && shift(date, 7).slice(5, 7) !== date.slice(5, 7)
}

function rangePrediction(median: number, sigma: number, prevClose: number, edges: [number, number]): RangePrediction {
  const low80 = median * Math.exp(-1.2816 * sigma)
  const high80 = median * Math.exp(1.2816 * sigma)
  const quiet = phi((Math.log(edges[0]) - Math.log(median)) / sigma)
  const wild = 1 - phi((Math.log(edges[1]) - Math.log(median)) / sigma)
  const pts = (pct: number) => round((pct / 100) * prevClose, 1)
  return {
    median: round(median, 2),
    low80: round(low80, 2),
    high80: round(high80, 2),
    prevClose: round(prevClose, 2),
    points: { median: pts(median), low80: pts(low80), high80: pts(high80) },
    buckets: { quiet: round(quiet, 2), normal: round(1 - round(quiet, 2) - round(wild, 2), 2), wild: round(wild, 2) },
    bucketEdges: edges,
  }
}

function bucketOf(range: number, edges: [number, number]): RangeBucket {
  return range < edges[0] ? 'quiet' : range >= edges[1] ? 'wild' : 'normal'
}

// ---------- the scoreboard, by the contract's rules ----------

function calibrate(pairs: Array<{ p: number; y: number }>): CalibrationBin[] {
  const bins = new Map<number, Array<{ p: number; y: number }>>()
  for (const pair of pairs) {
    const k = Math.min(9, Math.floor(pair.p * 10 + 1e-9))
    bins.set(k, [...(bins.get(k) ?? []), pair])
  }
  return [...bins.entries()]
    .sort((a, b) => a[0] - b[0])
    .map(([k, list]) => ({
      from: k / 10,
      to: (k + 1) / 10,
      n: list.length,
      meanP: round(mean(list.map((x) => x.p)), 3),
      hitRate: round(mean(list.map((x) => x.y)), 3),
    }))
}

/** 95% percentile interval of mean(baselineLoss − loss), resampling whole sessions. */
function bootstrap(scored: Forecast[]): { low: number; high: number } | null {
  const bySession = new Map<string, number[]>()
  for (const f of scored) {
    bySession.set(f.sessionDate, [...(bySession.get(f.sessionDate) ?? []), f.scores!.baselineLoss - f.scores!.loss])
  }
  const sessions = [...bySession.entries()].sort().map(([, d]) => ({ sum: d.reduce((a, b) => a + b, 0), n: d.length }))
  if (sessions.length < 2) return null
  const r = rng(20260927)
  const means: number[] = []
  for (let b = 0; b < 2000; b++) {
    let sum = 0
    let n = 0
    for (let i = 0; i < sessions.length; i++) {
      const s = sessions[Math.floor(r() * sessions.length)]
      sum += s.sum
      n += s.n
    }
    means.push(sum / n)
  }
  means.sort((a, b) => a - b)
  const pct = (q: number) => {
    const at = q * (means.length - 1)
    const lo = Math.floor(at)
    return means[lo] + (means[Math.min(lo + 1, means.length - 1)] - means[lo]) * (at - lo)
  }
  return { low: round(pct(0.025), 4), high: round(pct(0.975), 4) }
}

function judge(n: number, lo: number | null, hi: number | null): { status: ModelStatus; reason: string } {
  if (n < 20) {
    return {
      status: 'collecting',
      reason: n === 0 ? 'No scored forecasts yet; 20 are needed before it is judged' : `${n} of 20 scored forecasts; too few to judge yet`,
    }
  }
  if (n >= 60 && lo != null && lo > 0) {
    return { status: 'proven', reason: `${n} scored forecasts; the whole 95% confidence interval is above zero, so it beats the baseline` }
  }
  if (n >= 120 && hi != null && hi < 0) {
    return { status: 'retired', reason: `${n} scored forecasts; the whole 95% confidence interval is below zero, so the baseline is better` }
  }
  if (lo != null && lo > 0) {
    return { status: 'testing', reason: `${n} of 60 scored forecasts; ahead of the baseline so far, but not yet enough to call it proven` }
  }
  if (hi != null && hi < 0) {
    return { status: 'testing', reason: `${n} of 120 scored forecasts; behind the baseline so far, but not yet enough to retire it` }
  }
  return {
    status: 'testing',
    reason:
      n < 60
        ? `${n} of 60 scored forecasts; the confidence interval still includes no improvement`
        : `${n} scored forecasts; the confidence interval still includes no improvement`,
  }
}

function summarize(model: ForecastModel, underlying: string, list: Forecast[]): ScoreboardRow {
  const scored = list
    .filter((f) => f.scores)
    .sort((a, b) => a.sessionDate.localeCompare(b.sessionDate) || a.underlying.localeCompare(b.underlying))
  const n = scored.length
  const meanLoss = n ? mean(scored.map((f) => f.scores!.loss)) : null
  const meanBase = n ? mean(scored.map((f) => f.scores!.baselineLoss)) : null
  const ci = bootstrap(scored)
  const { status, reason } = judge(n, ci?.low ?? null, ci?.high ?? null)
  const covered = scored.map((f) => f.scores!.metrics?.covered80).filter((c): c is boolean => typeof c === 'boolean')
  return {
    modelKey: model.key,
    modelVersion: model.version,
    target: model.target,
    underlying,
    description: model.description,
    liveCount: n,
    meanLoss: meanLoss == null ? null : round(meanLoss, 4),
    meanBaselineLoss: meanBase == null ? null : round(meanBase, 4),
    skill: meanLoss != null && meanBase ? round(1 - meanLoss / meanBase, 4) : null,
    diffCiLow: ci?.low ?? null,
    diffCiHigh: ci?.high ?? null,
    status,
    statusReason: reason,
    coverage80: covered.length ? round(covered.filter(Boolean).length / covered.length, 3) : null,
    calibration: calibrate(scored.flatMap((f) => f.scores!.calibration ?? [])),
    firstSession: n ? scored[0].sessionDate : null,
    lastSession: n ? scored[n - 1].sessionDate : null,
    backtest: model.backtest,
  }
}

// ---------- the build ----------

/**
 * Builds the fixture as the API would answer at `nowMs`: today's forecasts are
 * there from 08:50 IST and scored from 15:50 IST; every earlier session is
 * scored. The same `nowMs` date always gives the same numbers.
 */
export function buildAnalysisFixture(opts: { nowMs: number; sessions?: number; seed?: number }): AnalysisFixture {
  const { date: today, minutes } = istParts(opts.nowMs)
  const count = opts.sessions ?? 25
  const warmup = 22
  const days = weekdays(today, count + warmup)
  const live = days.slice(warmup)
  // A fixed seed, not the date: the page looks the same every day in dev,
  // only its dates move.
  const r = rng(opts.seed ?? FIXTURE_SEED)

  const models: Array<ForecastModel & { startsAt: number }> = MODELS.map((m) => ({
    ...m,
    version: `${shift(live[m.startsAt], -1)}.${m.startsAt === 0 ? 1 : 2}`,
  }))

  const indices = Object.keys(INDICES) as Index[]
  const state = new Map(
    indices.map((index) => {
      const spec = INDICES[index]
      return [
        index,
        {
          ln0: Math.log(spec.typical),
          lv: Math.log(spec.typical),
          prevClose: spec.close * (0.98 + 0.04 * r()),
          prevEfficiency: 0.4,
          prevReturn: 0,
          history: [] as number[],
        },
      ]
    }),
  )
  let vix = 11.5 + 2 * r()

  const forecasts: Forecast[] = []
  let id = 1

  for (let i = 0; i < days.length; i++) {
    const date = days[i]
    const liveIndex = i - warmup
    const isToday = date === today
    const issued = liveIndex >= 0 && (!isToday || minutes >= 8 * 60 + 50)
    const scored = !isToday || minutes >= 15 * 60 + 50
    const monday = weekday(date) === 1

    // What the day shares across the three indices: they move together, so
    // one session's three forecasts are close to one piece of evidence.
    const dayVol = gauss(r)
    const dayTrend = gauss(r)
    const dayUp = r() < 0.53

    for (const index of indices) {
      const spec = INDICES[index]
      const s = state.get(index)!
      const expiry = isExpiry(index, date)

      // The session's hidden volatility, and the range it produced.
      s.lv = s.ln0 + 0.55 * (s.lv - s.ln0) + 0.2 * (0.8 * dayVol + 0.6 * gauss(r)) + (expiry ? 0.08 : 0)
      const actual = Math.exp(s.lv + 0.26 * gauss(r))
      const trendZ = 0.8 * dayTrend + 0.6 * gauss(r)
      const trendDay = r() < logistic(-0.85 + 0.5 * trendZ)
      const up = r() < 0.85 ? dayUp : r() < 0.53

      const open = s.prevClose * (1 + 0.003 * gauss(r))
      const points = (actual / 100) * s.prevClose
      const eff = trendDay ? 0.6 + 0.35 * r() : 0.58 * r()
      const body = eff * points
      const close = up ? open + body : open - body
      const w = r()
      const outcome: ForecastOutcome = {
        open: round(open, 2),
        high: round(Math.max(open, close) + (points - body) * w, 2),
        low: round(Math.min(open, close) - (points - body) * (1 - w), 2),
        close: round(close, 2),
        range: round(actual, 3),
        bucket: bucketOf(actual, spec.edges),
        trendDay,
        efficiency: round(eff, 2),
        up,
      }

      if (issued) {
        const h = s.history
        const r1 = h[h.length - 1]
        const r5 = mean(h.slice(-5))
        const r22 = mean(h.slice(-22))
        const baseRange = rangePrediction(mean(h.slice(-20)), 0.36, s.prevClose, spec.edges)
        baseRange.buckets = { quiet: 0.33, normal: 0.34, wild: 0.33 }
        const trendBase = round(0.29 + 0.02 * r(), 3)
        const upBase = round(0.525 + 0.01 * r(), 3)
        const vixIn = round(vix, 2)

        const made: Record<string, { prediction: RangePrediction | { p: number }; inputs: Record<string, unknown> }> = {
          'range.har': {
            prediction: rangePrediction(
              Math.exp(0.5 * s.lv + 0.5 * Math.log(r5) + 0.14 * gauss(r)),
              0.32,
              s.prevClose,
              spec.edges,
            ),
            inputs: { r1: round(r1, 2), r5: round(r5, 2), r22: round(r22, 2) },
          },
          'range.har-vix': {
            prediction: rangePrediction(Math.exp(0.7 * s.lv + 0.3 * Math.log(r5) - 0.02 + 0.12 * gauss(r)), 0.3, s.prevClose, spec.edges),
            inputs: { vixPrevClose: vixIn, r1: round(r1, 2), r5: round(r5, 2), r22: round(r22, 2), expiryDay: expiry, monday },
          },
          'trend.logit': {
            prediction: { p: round(logistic(-0.85 + 0.25 * trendZ + 0.35 * gauss(r)), 3) },
            inputs: { r1: round(r1, 2), r5: round(r5, 2), prevEfficiency: round(s.prevEfficiency, 2), vixPrevClose: vixIn, expiryDay: expiry },
          },
          'direction.logit': {
            prediction: { p: round(0.53 + 0.035 * gauss(r), 3) },
            inputs: { prevReturn: round(s.prevReturn, 3), vixPrevClose: vixIn, monday },
          },
        }

        for (const model of models) {
          if (liveIndex < model.startsAt) continue
          const m = made[model.key]
          const baseline = model.target === 'range' ? baseRange : { p: model.target === 'trend' ? trendBase : upBase }
          const n = id++
          forecasts.push({
            id: n,
            modelKey: model.key,
            modelVersion: model.version,
            target: model.target as ForecastTarget,
            underlying: index,
            sessionDate: date,
            issuedUtc: `${date}T03:20:0${(n % 7) + 1}Z`,
            prediction: m.prediction,
            baseline,
            inputs: m.inputs,
            // Trend and direction state no bucket edges, so their outcome has no bucket.
            outcome: scored ? (model.target === 'range' ? outcome : { ...outcome, bucket: null }) : null,
            scores: scored ? score(model.target, m.prediction, baseline, outcome) : null,
            scoredUtc: scored ? `${date}T10:20:1${n % 7}Z` : null,
          })
        }
      }

      s.history.push(actual)
      s.prevClose = close
      s.prevEfficiency = eff
      s.prevReturn = ((close - open) / open) * 100
    }
    vix = Math.min(22, Math.max(9.5, vix + 0.35 * dayVol + 0.2 * gauss(r)))
  }

  const scoreboard: ScoreboardRow[] = []
  for (const model of models) {
    const mine = forecasts.filter((f) => f.modelKey === model.key && f.modelVersion === model.version)
    scoreboard.push(summarize(model, 'ALL', mine))
    for (const index of indices) {
      const rows = mine.filter((f) => f.underlying === index)
      if (rows.length) scoreboard.push(summarize(model, index, rows))
    }
  }

  return {
    today,
    models: models.map((m) => {
      const registered = `${m.version.slice(0, 10)}T13:10:00Z`
      return {
        key: m.key,
        version: m.version,
        target: m.target,
        description: m.description,
        backtest: m.backtest,
        registeredUtc: registered,
        updatedUtc: registered,
      }
    }),
    // Newest session first, as GET /api/Forecasts answers.
    forecasts: forecasts.sort((a, b) => b.sessionDate.localeCompare(a.sessionDate) || a.id - b.id),
    scoreboard,
  }
}

function score(
  target: string,
  prediction: RangePrediction | { p: number },
  baseline: RangePrediction | { p: number },
  o: ForecastOutcome,
): NonNullable<Forecast['scores']> {
  if (target === 'range') {
    const p = prediction as RangePrediction
    const b = baseline as RangePrediction
    const buckets: RangeBucket[] = ['quiet', 'normal', 'wild']
    const brier = (x: RangePrediction) =>
      round(buckets.reduce((s, k) => s + (x.buckets[k] - (o.bucket === k ? 1 : 0)) ** 2, 0), 4)
    return {
      loss: round(Math.abs(Math.log(o.range) - Math.log(p.median)), 4),
      baselineLoss: round(Math.abs(Math.log(o.range) - Math.log(b.median)), 4),
      metrics: { covered80: o.range >= p.low80 && o.range <= p.high80, brier: brier(p), baselineBrier: brier(b) },
      calibration: buckets.map((k) => ({ p: p.buckets[k], y: o.bucket === k ? 1 : 0 })),
    }
  }
  const y = target === 'trend' ? (o.trendDay ? 1 : 0) : o.up ? 1 : 0
  const p = (prediction as { p: number }).p
  const pb = (baseline as { p: number }).p
  const loss = round((p - y) ** 2, 4)
  const baselineLoss = round((pb - y) ** 2, 4)
  return { loss, baselineLoss, metrics: { brier: loss, baselineBrier: baselineLoss }, calibration: [{ p, y }] }
}

// ---------- the API, answered from the fixture ----------

let cached: { key: string; fixture: AnalysisFixture } | null = null

function current(nowMs: number): AnalysisFixture {
  // Rebuilt when the IST minute crosses 08:50 or 15:50, so a page left open
  // in dev sees the day's forecasts arrive and get scored.
  const { date, minutes } = istParts(nowMs)
  const key = `${date}:${minutes >= 8 * 60 + 50}:${minutes >= 15 * 60 + 50}`
  if (!cached || cached.key !== key) cached = { key, fixture: buildAnalysisFixture({ nowMs }) }
  return cached.fixture
}

/**
 * What GET `path` (with its query string) would answer. An unknown path
 * throws an error carrying status 404, like the API client does.
 */
export function fixtureResponse(path: string, fixture: AnalysisFixture = current(Date.now())): unknown {
  const url = new URL(path, 'http://fixture.local')
  const route = url.pathname.replace(/\/+$/, '').toLowerCase()
  if (route === '/api/forecasts/models') return fixture.models
  if (route === '/api/forecasts/scoreboard') return fixture.scoreboard
  if (route === '/api/forecasts') {
    const q = url.searchParams
    const from = q.get('from')
    const to = q.get('to')
    const target = q.get('target')
    const underlying = q.get('underlying')
    return fixture.forecasts.filter(
      (f) =>
        (!from || f.sessionDate >= from) &&
        (!to || f.sessionDate <= to) &&
        (!target || f.target === target) &&
        (!underlying || f.underlying === underlying),
    )
  }
  throw Object.assign(new Error(`Not found: ${url.pathname}`), { status: 404 })
}
