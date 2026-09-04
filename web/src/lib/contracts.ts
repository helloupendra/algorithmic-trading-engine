/**
 * Contract requirements — the strikes a strategy asks for, and the run
 * parameters that move them.
 *
 * A strategy declares what it wants to trade as keys ("atm_ce", "otm_pe",
 * "wing_ce") with a moneyness and a distance from ATM; the engine resolves
 * each key to a real contract on the underlying's strike grid. A requirement
 * may name a run parameter (`otm_offset_steps`, `wing_offset_points`) that
 * overrides that distance, which is what the launch and backtest dialogs let
 * the user edit before a run starts.
 *
 * Everything here is pure: the dialogs own the input state, these functions
 * only seed it, describe it and turn it back into run parameters.
 */

import type { FnoUnderlying, StrategyContractRequirement } from './types'

/** Requirements of a catalogue entry; `[]` for an older API build. */
export function contractRequirementsOf(
  list: StrategyContractRequirement[] | null | undefined,
): StrategyContractRequirement[] {
  return Array.isArray(list) ? list : []
}

/**
 * A parameter whose name ends with `_points` carries absolute points; every
 * other one carries strike steps (multiplied by the underlying's step).
 */
export function isPointsParam(name: string): boolean {
  return name.toLowerCase().endsWith('_points')
}

/** One tunable distance: the parameter, what it is measured in, and who reads it. */
export interface StrikeParam {
  /** Run-parameter name, e.g. "otm_offset_steps". */
  name: string
  /** True when the value is absolute points rather than strike steps. */
  points: boolean
  /** Requirement keys resolved with it, in declaration order. */
  keys: string[]
  /** What the strategy falls back to when the parameter is left out. */
  fallback: number | null
}

/** The distinct parameters of a requirement list, in first-seen order. */
export function strikeParams(reqs: readonly StrategyContractRequirement[]): StrikeParam[] {
  const out: StrikeParam[] = []
  const byName = new Map<string, StrikeParam>()
  for (const req of reqs) {
    const name = req.param?.trim()
    if (!name) continue
    const known = byName.get(name)
    if (known) {
      known.keys.push(req.key)
      continue
    }
    const points = isPointsParam(name)
    const entry: StrikeParam = {
      name,
      points,
      keys: [req.key],
      fallback: points ? (req.points ?? null) : req.steps,
    }
    byName.set(name, entry)
    out.push(entry)
  }
  return out
}

/** The parameter names of a requirement list — the keys the free-form grid must not repeat. */
export function strikeParamKeys(reqs: readonly StrategyContractRequirement[]): Set<string> {
  return new Set(strikeParams(reqs).map((p) => p.name))
}

function parseJsonObject(json: string): Record<string, unknown> {
  try {
    const obj = JSON.parse(json || '{}') as unknown
    return obj && typeof obj === 'object' && !Array.isArray(obj) ? (obj as Record<string, unknown>) : {}
  } catch {
    return {}
  }
}

/**
 * One text value per parameter: the strategy's (or the earlier run's) default
 * when it names the parameter, else the distance the requirement itself
 * declares, else "" (the engine's own default applies).
 */
export function seedStrikeParams(
  params: readonly StrikeParam[],
  parametersJson: string,
): Record<string, string> {
  const defaults = parseJsonObject(parametersJson)
  const out: Record<string, string> = {}
  for (const p of params) {
    const raw = defaults[p.name]
    const fromJson = typeof raw === 'number' || typeof raw === 'string' ? Number(raw) : Number.NaN
    if (Number.isFinite(fromJson) && fromJson > 0) out[p.name] = String(fromJson)
    else out[p.name] = p.fallback != null && p.fallback > 0 ? String(p.fallback) : ''
  }
  return out
}

/** "" → not set; otherwise the number, or NaN when it is not a positive one. */
export function parseStrikeValue(value: string): number | null {
  const t = value.trim()
  if (t === '') return null
  const n = Number(t)
  return Number.isFinite(n) && n > 0 ? n : Number.NaN
}

/** True for a field holding something that is not a positive number. */
export function strikeValueInvalid(value: string): boolean {
  const n = parseStrikeValue(value)
  return n != null && Number.isNaN(n)
}

/** Human label of a parameter: "otm_offset_steps" → "OTM offset steps". */
export function strikeParamLabel(name: string): string {
  const words = name.split('_').map((w) => (/^(atm|otm|itm|ce|pe|sl)$/i.test(w) ? w.toUpperCase() : w))
  const joined = words.join(' ')
  return joined.charAt(0).toUpperCase() + joined.slice(1)
}

export type StrikeParamResult =
  | { values: Record<string, number>; error: null; param: null }
  | { values: null; error: string; param: string }

/**
 * The parameters to send: every set value, keyed by name. The first field
 * holding a non-positive number names the error instead (same "> 0 or empty"
 * rule as the risk draft).
 */
export function parseStrikeParams(
  params: readonly StrikeParam[],
  values: Record<string, string>,
): StrikeParamResult {
  const out: Record<string, number> = {}
  for (const p of params) {
    const n = parseStrikeValue(values[p.name] ?? '')
    if (n != null && Number.isNaN(n)) {
      return {
        values: null,
        error: `${strikeParamLabel(p.name)} must be a positive number of ${p.points ? 'points' : 'strikes'}, or left empty to use the strategy default.`,
        param: p.name,
      }
    }
    if (n != null) out[p.name] = n
  }
  return { values: out, error: null, param: null }
}

/* ------------------------------------------------------------- description */

function fmt(value: number): string {
  return value.toLocaleString('en-IN', { maximumFractionDigits: 2 })
}

/** "OTM CE", "ATM PE" — what the requirement asks for, without the distance. */
export function requirementLabel(req: StrategyContractRequirement): string {
  const moneyness = (req.moneyness || 'atm').toUpperCase()
  const type = (req.optionType || '').toUpperCase()
  return type ? `${moneyness} ${type}` : moneyness
}

/**
 * `+1` when the strike sits above ATM, `−1` when below: a CE is OTM above and
 * ITM below, a PE the other way round.
 */
function direction(req: StrategyContractRequirement): number {
  const otm = (req.moneyness || 'atm').toLowerCase() === 'otm'
  const ce = (req.optionType || '').toUpperCase() === 'CE'
  return ce === otm ? 1 : -1
}

/**
 * The distance the run will really use: the parameter the user typed when it
 * is set, else the requirement's own points, else its steps.
 */
function effectiveDistance(
  req: StrategyContractRequirement,
  values: Record<string, string>,
): { steps: number | null; points: number | null } {
  if (req.param) {
    const typed = parseStrikeValue(values[req.param] ?? '')
    if (typed != null && !Number.isNaN(typed)) {
      return isPointsParam(req.param) ? { steps: null, points: typed } : { steps: typed, points: null }
    }
  }
  if (req.points != null && req.points > 0) return { steps: null, points: req.points }
  return { steps: req.steps > 0 ? req.steps : null, points: null }
}

/**
 * "+2 strikes (+200 pts on BANKNIFTY)" — the offset from ATM, in strikes and,
 * when an underlying is chosen, in points on its grid. "at the money" for an
 * ATM requirement or a zero distance.
 */
export function describeRequirementDistance(
  req: StrategyContractRequirement,
  values: Record<string, string>,
  underlying: FnoUnderlying | null,
): string {
  if ((req.moneyness || 'atm').toLowerCase() === 'atm') return 'at the money'
  const step = underlying && underlying.strikeStep > 0 ? underlying.strikeStep : null
  let { steps, points } = effectiveDistance(req, values)
  if (steps == null && points == null) return 'at the money'
  if (step != null) {
    if (points == null && steps != null) points = steps * step
    else if (steps == null && points != null) steps = points / step
  }
  const sign = direction(req) > 0 ? '+' : '−'
  const stepsText = steps != null ? `${sign}${fmt(steps)} ${steps === 1 ? 'strike' : 'strikes'}` : null
  const pointsText =
    points != null && underlying ? `${sign}${fmt(points)} pts on ${underlying.underlying}` : points != null ? `${sign}${fmt(points)} pts` : null
  if (stepsText && pointsText) return `${stepsText} (${pointsText})`
  return stepsText ?? pointsText ?? 'at the money'
}

/** "OTM CE · +2 strikes" — the whole requirement on one line, no underlying needed. */
export function describeRequirement(req: StrategyContractRequirement): string {
  return `${requirementLabel(req)} · ${describeRequirementDistance(req, {}, null)}${req.optional ? ' · optional' : ''}`
}

/**
 * "atm_ce, otm_ce +2 strikes" — the compact form the library table and the
 * strategy cards carry. Empty string when the strategy declares none.
 */
export function contractRequirementSummary(reqs: readonly StrategyContractRequirement[]): string {
  return reqs
    .map((r) => {
      const d = describeRequirementDistance(r, {}, null)
      return d === 'at the money' ? r.key : `${r.key} ${d}`
    })
    .join(' · ')
}
