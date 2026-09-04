/**
 * Risk rules — the three-level model shared by live runs and backtests, and
 * everything the screens need to edit, validate, describe and display it:
 * a string-valued draft for form fields, the draft → RiskRules parser with
 * the "> 0" rule, chip and sentence renderers, and the RISK_UPDATED activity
 * decoder. Pure functions only; the form itself is components/RiskRulesForm.
 *
 * Each level carries a fixed stop-loss / target pair and a trailing pair (the
 * give-back from the best P&L, plus the profit at which it arms). The engine
 * evaluates them fixed stop-loss → trailing stop → target, leg → group →
 * overall; peaks live in the runner's memory only, so a rule change or an API
 * restart re-arms the trail from the current P&L.
 */

import { formatInrWhole } from './format'
import type { GroupRisk, LegRisk, LiveActivity, OverallRisk, RiskRules } from './types'

/* -------------------------------------------------------------------- draft */

/** Every rule as the text of its input — "" means not set. */
export interface RiskDraft {
  overallStopLoss: string
  overallTarget: string
  overallTrailStopLoss: string
  overallTrailTrigger: string
  groupStopLoss: string
  groupTarget: string
  groupTrailStopLoss: string
  groupTrailTrigger: string
  legStopLossPoints: string
  legTargetPoints: string
  legStopLossPercent: string
  legTargetPercent: string
  legTrailStopLossPoints: string
  legTrailStopLossPercent: string
  legTrailTriggerPoints: string
  legTrailTriggerPercent: string
}

export type RiskDraftField = keyof RiskDraft

export const EMPTY_RISK_DRAFT: RiskDraft = {
  overallStopLoss: '',
  overallTarget: '',
  overallTrailStopLoss: '',
  overallTrailTrigger: '',
  groupStopLoss: '',
  groupTarget: '',
  groupTrailStopLoss: '',
  groupTrailTrigger: '',
  legStopLossPoints: '',
  legTargetPoints: '',
  legStopLossPercent: '',
  legTargetPercent: '',
  legTrailStopLossPoints: '',
  legTrailStopLossPercent: '',
  legTrailTriggerPoints: '',
  legTrailTriggerPercent: '',
}

function text(v: number | null | undefined): string {
  return v == null || !Number.isFinite(v) ? '' : String(v)
}

/** Draft from stored rules (a run's view, a backtest's "Run again"). */
export function riskDraftFrom(rules: RiskRules | null | undefined): RiskDraft {
  return {
    overallStopLoss: text(rules?.overall?.stopLoss),
    overallTarget: text(rules?.overall?.target),
    overallTrailStopLoss: text(rules?.overall?.trailStopLoss),
    overallTrailTrigger: text(rules?.overall?.trailTrigger),
    groupStopLoss: text(rules?.group?.stopLoss),
    groupTarget: text(rules?.group?.target),
    groupTrailStopLoss: text(rules?.group?.trailStopLoss),
    groupTrailTrigger: text(rules?.group?.trailTrigger),
    legStopLossPoints: text(rules?.leg?.stopLossPoints),
    legTargetPoints: text(rules?.leg?.targetPoints),
    legStopLossPercent: text(rules?.leg?.stopLossPercent),
    legTargetPercent: text(rules?.leg?.targetPercent),
    legTrailStopLossPoints: text(rules?.leg?.trailStopLossPoints),
    legTrailStopLossPercent: text(rules?.leg?.trailStopLossPercent),
    legTrailTriggerPoints: text(rules?.leg?.trailTriggerPoints),
    legTrailTriggerPercent: text(rules?.leg?.trailTriggerPercent),
  }
}

/** Rules from the legacy overall-only fields (older API builds, older runs). */
export function riskFromLegacy(
  stopLoss: number | null | undefined,
  target: number | null | undefined,
): RiskRules {
  return normalizeRisk({ overall: { stopLoss: stopLoss ?? null, target: target ?? null } })
}

/**
 * The rules a view is really under: the three-level object when the API sends
 * one, else the overall shorthands an older build (or an older run) carries.
 */
export function effectiveRisk(view: {
  risk?: RiskRules | null
  stopLoss?: number | null
  target?: number | null
}): RiskRules {
  if (view.risk) return normalizeRisk(view.risk)
  return riskFromLegacy(view.stopLoss, view.target)
}

/* ---------------------------------------------------------------- validate */

function positive(v: number | null | undefined): number | null {
  return v != null && Number.isFinite(v) && v > 0 ? v : null
}

/** Drops unset / non-positive values so "nothing set" is `{}` at every level. */
export function normalizeRisk(rules: RiskRules | null | undefined): RiskRules {
  const out: RiskRules = {}

  const overall: OverallRisk = {}
  const overallSl = positive(rules?.overall?.stopLoss)
  const overallTg = positive(rules?.overall?.target)
  const overallTrail = positive(rules?.overall?.trailStopLoss)
  const overallTrigger = positive(rules?.overall?.trailTrigger)
  if (overallSl != null) overall.stopLoss = overallSl
  if (overallTg != null) overall.target = overallTg
  if (overallTrail != null) overall.trailStopLoss = overallTrail
  if (overallTrigger != null) overall.trailTrigger = overallTrigger
  if (Object.keys(overall).length > 0) out.overall = overall

  const group: GroupRisk = {}
  const groupSl = positive(rules?.group?.stopLoss)
  const groupTg = positive(rules?.group?.target)
  const groupTrail = positive(rules?.group?.trailStopLoss)
  const groupTrigger = positive(rules?.group?.trailTrigger)
  if (groupSl != null) group.stopLoss = groupSl
  if (groupTg != null) group.target = groupTg
  if (groupTrail != null) group.trailStopLoss = groupTrail
  if (groupTrigger != null) group.trailTrigger = groupTrigger
  if (Object.keys(group).length > 0) out.group = group

  const leg: LegRisk = {}
  const slPts = positive(rules?.leg?.stopLossPoints)
  const tgPts = positive(rules?.leg?.targetPoints)
  const slPct = positive(rules?.leg?.stopLossPercent)
  const tgPct = positive(rules?.leg?.targetPercent)
  const trailPts = positive(rules?.leg?.trailStopLossPoints)
  const trailPct = positive(rules?.leg?.trailStopLossPercent)
  const triggerPts = positive(rules?.leg?.trailTriggerPoints)
  const triggerPct = positive(rules?.leg?.trailTriggerPercent)
  if (slPts != null) leg.stopLossPoints = slPts
  if (tgPts != null) leg.targetPoints = tgPts
  if (slPct != null) leg.stopLossPercent = slPct
  if (tgPct != null) leg.targetPercent = tgPct
  if (trailPts != null) leg.trailStopLossPoints = trailPts
  if (trailPct != null) leg.trailStopLossPercent = trailPct
  if (triggerPts != null) leg.trailTriggerPoints = triggerPts
  if (triggerPct != null) leg.trailTriggerPercent = triggerPct
  if (Object.keys(leg).length > 0) out.leg = leg

  return out
}

export function isRiskEmpty(rules: RiskRules | null | undefined): boolean {
  const r = normalizeRisk(rules)
  return !r.overall && !r.group && !r.leg
}

const FIELD_LABELS: Record<RiskDraftField, string> = {
  overallStopLoss: 'Overall stop-loss',
  overallTarget: 'Overall target',
  overallTrailStopLoss: 'Overall trailing stop-loss',
  overallTrailTrigger: 'Overall trail trigger',
  groupStopLoss: 'Group stop-loss',
  groupTarget: 'Group target',
  groupTrailStopLoss: 'Group trailing stop-loss',
  groupTrailTrigger: 'Group trail trigger',
  legStopLossPoints: 'Leg stop-loss (points)',
  legTargetPoints: 'Leg target (points)',
  legStopLossPercent: 'Leg stop-loss (%)',
  legTargetPercent: 'Leg target (%)',
  legTrailStopLossPoints: 'Leg trailing stop-loss (points)',
  legTrailStopLossPercent: 'Leg trailing stop-loss (%)',
  legTrailTriggerPoints: 'Leg trail trigger (points)',
  legTrailTriggerPercent: 'Leg trail trigger (%)',
}

const RUPEES = 'a positive rupee amount'
const PREMIUM_POINTS = 'a positive number of premium points'
const PREMIUM_PERCENT = 'a positive percentage of the entry premium'

const FIELD_UNITS: Record<RiskDraftField, string> = {
  overallStopLoss: RUPEES,
  overallTarget: RUPEES,
  overallTrailStopLoss: RUPEES,
  overallTrailTrigger: RUPEES,
  groupStopLoss: RUPEES,
  groupTarget: RUPEES,
  groupTrailStopLoss: RUPEES,
  groupTrailTrigger: RUPEES,
  legStopLossPoints: PREMIUM_POINTS,
  legTargetPoints: PREMIUM_POINTS,
  legStopLossPercent: PREMIUM_PERCENT,
  legTargetPercent: PREMIUM_PERCENT,
  legTrailStopLossPoints: PREMIUM_POINTS,
  legTrailStopLossPercent: PREMIUM_PERCENT,
  legTrailTriggerPoints: PREMIUM_POINTS,
  legTrailTriggerPercent: PREMIUM_PERCENT,
}

/**
 * A trigger only says WHEN a trail arms — without the give-back amount next to
 * it nothing would ever trail, so the field is refused rather than silently
 * ignored. `[trigger, its trail, what to say]`.
 */
const TRIGGER_PAIRS: ReadonlyArray<readonly [RiskDraftField, RiskDraftField, string]> = [
  ['overallTrailTrigger', 'overallTrailStopLoss', 'the overall trailing stop-loss'],
  ['groupTrailTrigger', 'groupTrailStopLoss', 'the group trailing stop-loss'],
  ['legTrailTriggerPoints', 'legTrailStopLossPoints', 'the leg trailing stop-loss in points'],
  ['legTrailTriggerPercent', 'legTrailStopLossPercent', 'the leg trailing stop-loss in %'],
]

/** "" → not set (null); otherwise the number, or NaN when not a positive number. */
export function parseRiskField(value: string): number | null {
  const t = value.trim()
  if (t === '') return null
  const n = Number(t)
  return Number.isFinite(n) && n > 0 ? n : Number.NaN
}

/** True for a field the user typed something that is not a positive number into. */
export function riskFieldInvalid(value: string): boolean {
  const n = parseRiskField(value)
  return n != null && Number.isNaN(n)
}

export type RiskDraftResult =
  | { rules: RiskRules; error: null; field: null }
  | { rules: null; error: string; field: RiskDraftField }

/**
 * Every set field must be > 0, and a trail trigger must have its trailing
 * stop-loss beside it; the first offending field names the error.
 */
export function parseRiskDraft(draft: RiskDraft): RiskDraftResult {
  const values: Partial<Record<RiskDraftField, number | null>> = {}
  for (const key of Object.keys(draft) as RiskDraftField[]) {
    const n = parseRiskField(draft[key])
    if (n != null && Number.isNaN(n)) {
      return {
        rules: null,
        error: `${FIELD_LABELS[key]} must be ${FIELD_UNITS[key]}, or left empty.`,
        field: key,
      }
    }
    values[key] = n
  }
  for (const [trigger, trail, what] of TRIGGER_PAIRS) {
    if (values[trigger] != null && values[trail] == null) {
      return {
        rules: null,
        error: `${FIELD_LABELS[trigger]} only says when the trail arms — set ${what} too, or clear the trigger.`,
        field: trigger,
      }
    }
  }
  const rules = normalizeRisk({
    overall: {
      stopLoss: values.overallStopLoss,
      target: values.overallTarget,
      trailStopLoss: values.overallTrailStopLoss,
      trailTrigger: values.overallTrailTrigger,
    },
    group: {
      stopLoss: values.groupStopLoss,
      target: values.groupTarget,
      trailStopLoss: values.groupTrailStopLoss,
      trailTrigger: values.groupTrailTrigger,
    },
    leg: {
      stopLossPoints: values.legStopLossPoints,
      targetPoints: values.legTargetPoints,
      stopLossPercent: values.legStopLossPercent,
      targetPercent: values.legTargetPercent,
      trailStopLossPoints: values.legTrailStopLossPoints,
      trailStopLossPercent: values.legTrailStopLossPercent,
      trailTriggerPoints: values.legTrailTriggerPoints,
      trailTriggerPercent: values.legTrailTriggerPercent,
    },
  })
  return { rules, error: null, field: null }
}

/* ----------------------------------------------------------------- display */

function pts(v: number): string {
  return `${v.toLocaleString('en-IN', { maximumFractionDigits: 2 })} pts`
}

function pct(v: number): string {
  return `${v.toLocaleString('en-IN', { maximumFractionDigits: 2 })}%`
}

/** "20 pts / 5%" — the points and/or percent form of one leg rule. */
function legPair(points: number | null | undefined, percent: number | null | undefined): string | null {
  const parts: string[] = []
  if (points != null) parts.push(pts(points))
  if (percent != null) parts.push(pct(percent))
  return parts.length > 0 ? parts.join(' / ') : null
}

/**
 * "₹1,500 arms at ₹3,000" — the give-back, and the profit at which it starts
 * watching when a trigger is set (with none it arms as soon as the subject is
 * in profit, which the form's helper text says). Null when no trail is set:
 * a trigger on its own trails nothing.
 *
 * Wording and order match RiskRulesDto.Describe() on the API, so the sentence
 * a RISK_UPDATED row decodes to here is the one the API logs for it.
 */
function trailPhrase(
  trail: number | null | undefined,
  trigger: number | null | undefined,
): string | null {
  if (trail == null) return null
  const amount = formatInrWhole(trail)
  return trigger != null ? `${amount} arms at ${formatInrWhole(trigger)}` : amount
}

/** Same, per leg: points and percent each trail their own peak. */
function legTrailPhrase(leg: LegRisk | null | undefined): string | null {
  const trail = legPair(leg?.trailStopLossPoints, leg?.trailStopLossPercent)
  if (!trail) return null
  const trigger = legPair(leg?.trailTriggerPoints, leg?.trailTriggerPercent)
  return trigger ? `${trail} arms at ${trigger}` : trail
}

export interface RiskChip {
  level: 'overall' | 'group' | 'leg'
  label: string
}

/**
 * One chip per level that has anything set: "Overall SL ₹5,000 · target — ·
 * trailing ₹1,500 (arms at ₹3,000)", "Group SL ₹1,000", "Leg SL 20 pts / 5% ·
 * target 30 pts". Empty when no rule is set at all.
 */
export function riskChips(rules: RiskRules | null | undefined): RiskChip[] {
  const r = normalizeRisk(rules)
  const chips: RiskChip[] = []
  if (r.overall) {
    const sl = r.overall.stopLoss != null ? formatInrWhole(r.overall.stopLoss) : '—'
    const tg = r.overall.target != null ? formatInrWhole(r.overall.target) : '—'
    const trail = trailPhrase(r.overall.trailStopLoss, r.overall.trailTrigger)
    chips.push({
      level: 'overall',
      label: `Overall SL ${sl} · target ${tg}${trail ? ` · trail ${trail}` : ''}`,
    })
  }
  if (r.group) {
    const parts: string[] = []
    if (r.group.stopLoss != null) parts.push(`SL ${formatInrWhole(r.group.stopLoss)}`)
    if (r.group.target != null) parts.push(`target ${formatInrWhole(r.group.target)}`)
    const trail = trailPhrase(r.group.trailStopLoss, r.group.trailTrigger)
    if (trail) parts.push(`trail ${trail}`)
    chips.push({ level: 'group', label: `Group ${parts.join(' · ')}` })
  }
  if (r.leg) {
    const parts: string[] = []
    const sl = legPair(r.leg.stopLossPoints, r.leg.stopLossPercent)
    const tg = legPair(r.leg.targetPoints, r.leg.targetPercent)
    if (sl) parts.push(`SL ${sl}`)
    if (tg) parts.push(`target ${tg}`)
    const trail = legTrailPhrase(r.leg)
    if (trail) parts.push(`trail ${trail}`)
    chips.push({ level: 'leg', label: `Leg ${parts.join(' · ')}` })
  }
  return chips
}

/**
 * Sentence form for activity rows and header lines: "overall SL ₹5,000, leg
 * SL 20 pts" — only what is set; "no risk rules" when nothing is.
 */
export function describeRiskRules(rules: RiskRules | null | undefined): string {
  const r = normalizeRisk(rules)
  const parts: string[] = []
  if (r.overall?.stopLoss != null) parts.push(`overall SL ${formatInrWhole(r.overall.stopLoss)}`)
  if (r.overall?.target != null) parts.push(`overall target ${formatInrWhole(r.overall.target)}`)
  if (r.group?.stopLoss != null) parts.push(`group SL ${formatInrWhole(r.group.stopLoss)}`)
  if (r.group?.target != null) parts.push(`group target ${formatInrWhole(r.group.target)}`)
  const legSl = legPair(r.leg?.stopLossPoints, r.leg?.stopLossPercent)
  if (legSl) parts.push(`leg SL ${legSl}`)
  const legTg = legPair(r.leg?.targetPoints, r.leg?.targetPercent)
  if (legTg) parts.push(`leg target ${legTg}`)
  // The trails come after every fixed rule, as RiskRulesDto.Describe() writes them.
  const overallTrail = trailPhrase(r.overall?.trailStopLoss, r.overall?.trailTrigger)
  if (overallTrail) parts.push(`overall trail ${overallTrail}`)
  const groupTrail = trailPhrase(r.group?.trailStopLoss, r.group?.trailTrigger)
  if (groupTrail) parts.push(`group trail ${groupTrail}`)
  const legTrail = legTrailPhrase(r.leg)
  if (legTrail) parts.push(`leg trail ${legTrail}`)
  return parts.length > 0 ? parts.join(', ') : 'no risk rules'
}

/* ------------------------------------------------------- activity decoding */

export const RISK_UPDATED_TYPE = 'RISK_UPDATED'

function asRecord(value: unknown): Record<string, unknown> | null {
  return value && typeof value === 'object' && !Array.isArray(value) ? (value as Record<string, unknown>) : null
}

function parseJsonRecord(text: string | null | undefined): Record<string, unknown> | null {
  if (!text) return null
  const t = text.trim()
  if (!t.startsWith('{')) return null
  try {
    return asRecord(JSON.parse(t))
  } catch {
    return null
  }
}

/**
 * The `{ risk, by }` metadata of a RISK_UPDATED signal, wherever the API put
 * it: a decoded `metadata` object, a raw `metadataJson` string, or the row's
 * `text` when an older build forwards the JSON verbatim.
 */
export function riskUpdatedMetadata(a: LiveActivity): { risk: RiskRules; by: string | null } | null {
  const meta = asRecord(a.metadata) ?? parseJsonRecord(a.metadataJson) ?? parseJsonRecord(a.text)
  if (!meta) return null
  const risk = asRecord(meta.risk)
  if (!risk) return null
  const by = typeof meta.by === 'string' && meta.by.trim() !== '' ? meta.by : null
  return { risk: normalizeRisk(risk as RiskRules), by }
}

/**
 * Display text of an activity row. RISK_UPDATED renders from its metadata
 * ("Risk rules updated by admin: overall SL ₹5,000, leg SL 20 pts"); every
 * other row (including the guard's leg/group CLOSE_GROUP reasons) shows the
 * API's text as it came.
 */
export function activityText(a: LiveActivity): string {
  if (a.type.toUpperCase() !== RISK_UPDATED_TYPE) return a.text
  const meta = riskUpdatedMetadata(a)
  if (!meta) {
    // Nothing decodable: the API's own sentence, or a plain label rather than
    // the bare signal type.
    return a.text && a.text.toUpperCase() !== RISK_UPDATED_TYPE ? a.text : 'Risk rules updated'
  }
  const who = meta.by ? ` by ${meta.by}` : ''
  return `Risk rules updated${who}: ${describeRiskRules(meta.risk)}`
}
