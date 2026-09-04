/**
 * Risk rules — the three-level model shared by live runs and backtests, and
 * everything the screens need to edit, validate, describe and display it:
 * a string-valued draft for form fields, the draft → RiskRules parser with
 * the "> 0" rule, chip and sentence renderers, and the RISK_UPDATED activity
 * decoder. Pure functions only; the form itself is components/RiskRulesForm.
 */

import { formatInrWhole } from './format'
import type { LiveActivity, RiskRules } from './types'

/* -------------------------------------------------------------------- draft */

/** Every rule as the text of its input — "" means not set. */
export interface RiskDraft {
  overallStopLoss: string
  overallTarget: string
  groupStopLoss: string
  groupTarget: string
  legStopLossPoints: string
  legTargetPoints: string
  legStopLossPercent: string
  legTargetPercent: string
}

export type RiskDraftField = keyof RiskDraft

export const EMPTY_RISK_DRAFT: RiskDraft = {
  overallStopLoss: '',
  overallTarget: '',
  groupStopLoss: '',
  groupTarget: '',
  legStopLossPoints: '',
  legTargetPoints: '',
  legStopLossPercent: '',
  legTargetPercent: '',
}

function text(v: number | null | undefined): string {
  return v == null || !Number.isFinite(v) ? '' : String(v)
}

/** Draft from stored rules (a run's view, a backtest's "Run again"). */
export function riskDraftFrom(rules: RiskRules | null | undefined): RiskDraft {
  return {
    overallStopLoss: text(rules?.overall?.stopLoss),
    overallTarget: text(rules?.overall?.target),
    groupStopLoss: text(rules?.group?.stopLoss),
    groupTarget: text(rules?.group?.target),
    legStopLossPoints: text(rules?.leg?.stopLossPoints),
    legTargetPoints: text(rules?.leg?.targetPoints),
    legStopLossPercent: text(rules?.leg?.stopLossPercent),
    legTargetPercent: text(rules?.leg?.targetPercent),
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
  const overallSl = positive(rules?.overall?.stopLoss)
  const overallTg = positive(rules?.overall?.target)
  if (overallSl != null || overallTg != null) {
    out.overall = {}
    if (overallSl != null) out.overall.stopLoss = overallSl
    if (overallTg != null) out.overall.target = overallTg
  }
  const groupSl = positive(rules?.group?.stopLoss)
  const groupTg = positive(rules?.group?.target)
  if (groupSl != null || groupTg != null) {
    out.group = {}
    if (groupSl != null) out.group.stopLoss = groupSl
    if (groupTg != null) out.group.target = groupTg
  }
  const slPts = positive(rules?.leg?.stopLossPoints)
  const tgPts = positive(rules?.leg?.targetPoints)
  const slPct = positive(rules?.leg?.stopLossPercent)
  const tgPct = positive(rules?.leg?.targetPercent)
  if (slPts != null || tgPts != null || slPct != null || tgPct != null) {
    out.leg = {}
    if (slPts != null) out.leg.stopLossPoints = slPts
    if (tgPts != null) out.leg.targetPoints = tgPts
    if (slPct != null) out.leg.stopLossPercent = slPct
    if (tgPct != null) out.leg.targetPercent = tgPct
  }
  return out
}

export function isRiskEmpty(rules: RiskRules | null | undefined): boolean {
  const r = normalizeRisk(rules)
  return !r.overall && !r.group && !r.leg
}

const FIELD_LABELS: Record<RiskDraftField, string> = {
  overallStopLoss: 'Overall stop-loss',
  overallTarget: 'Overall target',
  groupStopLoss: 'Group stop-loss',
  groupTarget: 'Group target',
  legStopLossPoints: 'Leg stop-loss (points)',
  legTargetPoints: 'Leg target (points)',
  legStopLossPercent: 'Leg stop-loss (%)',
  legTargetPercent: 'Leg target (%)',
}

const FIELD_UNITS: Record<RiskDraftField, string> = {
  overallStopLoss: 'a positive rupee amount',
  overallTarget: 'a positive rupee amount',
  groupStopLoss: 'a positive rupee amount',
  groupTarget: 'a positive rupee amount',
  legStopLossPoints: 'a positive number of premium points',
  legTargetPoints: 'a positive number of premium points',
  legStopLossPercent: 'a positive percentage of the entry premium',
  legTargetPercent: 'a positive percentage of the entry premium',
}

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

/** Every set field must be > 0; the first offending one names the error. */
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
  const rules = normalizeRisk({
    overall: { stopLoss: values.overallStopLoss, target: values.overallTarget },
    group: { stopLoss: values.groupStopLoss, target: values.groupTarget },
    leg: {
      stopLossPoints: values.legStopLossPoints,
      targetPoints: values.legTargetPoints,
      stopLossPercent: values.legStopLossPercent,
      targetPercent: values.legTargetPercent,
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

export interface RiskChip {
  level: 'overall' | 'group' | 'leg'
  label: string
}

/**
 * One chip per level that has anything set: "Overall SL ₹5,000 · target —",
 * "Group SL ₹1,000", "Leg SL 20 pts / 5% · target 30 pts". Empty when no rule
 * is set at all.
 */
export function riskChips(rules: RiskRules | null | undefined): RiskChip[] {
  const r = normalizeRisk(rules)
  const chips: RiskChip[] = []
  if (r.overall) {
    const sl = r.overall.stopLoss != null ? formatInrWhole(r.overall.stopLoss) : '—'
    const tg = r.overall.target != null ? formatInrWhole(r.overall.target) : '—'
    chips.push({ level: 'overall', label: `Overall SL ${sl} · target ${tg}` })
  }
  if (r.group) {
    const parts: string[] = []
    if (r.group.stopLoss != null) parts.push(`SL ${formatInrWhole(r.group.stopLoss)}`)
    if (r.group.target != null) parts.push(`target ${formatInrWhole(r.group.target)}`)
    chips.push({ level: 'group', label: `Group ${parts.join(' · ')}` })
  }
  if (r.leg) {
    const parts: string[] = []
    const sl = legPair(r.leg.stopLossPoints, r.leg.stopLossPercent)
    const tg = legPair(r.leg.targetPoints, r.leg.targetPercent)
    if (sl) parts.push(`SL ${sl}`)
    if (tg) parts.push(`target ${tg}`)
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
