/**
 * The Risk page's two controls as plain functions: the "Trading limits" form and the kill switch.
 *
 * Trading limits. The API stores the max daily loss as a negative number (−50,000 by default) and refuses a
 * positive one, but the form's inputs all had `min=0`: the stored value failed the browser's own validation, so
 * "Save limits" did nothing, and clearing the field saved 0 (a run refused new entries at its first rupee of
 * loss). The form now shows the loss as a positive amount ("50000"), sends its negative, checks every field
 * against the API's own ranges before sending, and says what is wrong in words instead of a browser bubble.
 *
 * Kill switch. A failed first read used to render "Trading allowed" in green while the switch may have been on;
 * the top bar shows "Kill switch ?" for the same failure. Both now say the state is unknown, and the page's
 * button waits until it is known.
 */

import type { KillSwitchState, RiskLimits } from './types'

/** The four limits the form edits, as the API takes them. */
export type LimitsInput = Pick<RiskLimits, 'maxOrdersPerMinute' | 'maxDailyLoss' | 'maxConcurrentRuns' | 'maxRunsPerUser'>

/** The form's fields as typed. The loss is a positive amount of rupees; the API stores its negative. */
export interface LimitsDraft {
  maxOrdersPerMinute: string
  maxDailyLossAmount: string
  maxConcurrentRuns: string
  maxRunsPerUser: string
}

export type LimitsField = keyof LimitsDraft

/** The API's ranges (RiskLimitsStore.UpdateLimitsAsync): runs 0–500 with 0 meaning no cap. */
export const LIMIT_RANGES: Record<LimitsField, { min: number; max?: number; whole: boolean }> = {
  maxOrdersPerMinute: { min: 1, max: 10_000, whole: true },
  maxDailyLossAmount: { min: 0, whole: false },
  maxConcurrentRuns: { min: 0, max: 500, whole: true },
  maxRunsPerUser: { min: 0, max: 500, whole: true },
}

/** The stored limits as the form shows them. */
export function limitsToDraft(limits: LimitsInput): LimitsDraft {
  return {
    maxOrdersPerMinute: String(limits.maxOrdersPerMinute),
    // −0 and 0 both read "0".
    maxDailyLossAmount: String(Math.abs(limits.maxDailyLoss)),
    maxConcurrentRuns: String(limits.maxConcurrentRuns),
    maxRunsPerUser: String(limits.maxRunsPerUser),
  }
}

export type LimitsCheck =
  | { ok: true; limits: LimitsInput }
  | { ok: false; errors: Partial<Record<LimitsField, string>> }

/** The form's fields checked against the API's ranges; the limits to send when every field is valid. */
export function draftToLimits(draft: LimitsDraft): LimitsCheck {
  const errors: Partial<Record<LimitsField, string>> = {}
  const values = {} as Record<LimitsField, number>
  for (const field of Object.keys(LIMIT_RANGES) as LimitsField[]) {
    const raw = draft[field].trim()
    const range = LIMIT_RANGES[field]
    const value = Number(raw)
    if (raw === '') errors[field] = 'Required.'
    else if (!Number.isFinite(value)) errors[field] = 'Enter a number.'
    else if (range.whole && !Number.isInteger(value)) errors[field] = 'Enter a whole number.'
    else if (value < range.min || (range.max != null && value > range.max))
      errors[field] = range.max != null ? `Between ${range.min} and ${range.max}.` : `${range.min} or more.`
    else values[field] = value
  }
  if (Object.keys(errors).length > 0) return { ok: false, errors }
  return {
    ok: true,
    limits: {
      maxOrdersPerMinute: values.maxOrdersPerMinute,
      // 0 - 0 would be −0, which reads back as "0" anyway; send a plain 0.
      maxDailyLoss: values.maxDailyLossAmount === 0 ? 0 : -values.maxDailyLossAmount,
      maxConcurrentRuns: values.maxConcurrentRuns,
      maxRunsPerUser: values.maxRunsPerUser,
    },
  }
}

/** True when the form holds something other than the stored limits. */
export function draftDiffers(draft: LimitsDraft, stored: LimitsInput): boolean {
  const saved = limitsToDraft(stored)
  return (Object.keys(saved) as LimitsField[]).some((f) => draft[f].trim() !== saved[f])
}

/** What the daily-loss field's help says, for the amount typed (a positive number of rupees), or null when unreadable. */
export function dailyLossHelp(amount: number | null, format: (rupees: number) => string): string {
  const rule = 'Per run, on its net P&L for the IST day (after charges)'
  if (amount == null || !Number.isFinite(amount) || amount < 0) {
    return `${rule}: a run whose net today falls below minus this amount cannot open new positions until the next day; exits stay allowed.`
  }
  if (amount === 0) {
    return `${rule}: 0 refuses a run's new positions as soon as its net today is below zero; exits stay allowed.`
  }
  return `${rule}: a run whose net today falls below −${format(amount)} cannot open new positions until the next day; exits stay allowed.`
}

/** What the kill switch panel can say. */
export type KillSwitchView =
  /** The first read is in flight. */
  | { kind: 'reading' }
  /** The first read failed: on or off is not known. */
  | { kind: 'unknown' }
  /** Known. `stale` when the latest refresh failed and this is the last answer. */
  | { kind: 'on' | 'off'; state: KillSwitchState; stale: boolean }

export function killSwitchView(query: { data?: KillSwitchState; isError: boolean }): KillSwitchView {
  if (!query.data) return query.isError ? { kind: 'unknown' } : { kind: 'reading' }
  return { kind: query.data.isActive ? 'on' : 'off', state: query.data, stale: query.isError }
}

/** The confirmation asked before the kill switch is pulled (activate) or released. */
export function killSwitchConfirmText(activate: boolean): string {
  return activate
    ? 'Pull the kill switch?\n\n' +
        'Every strategy is stopped and every open position in every account, traders’ included, is squared off at the last mark. ' +
        'New strategy starts and positions are blocked until it is released. It is announced to Telegram.'
    : 'Release the kill switch?\n\n' +
        'Every account can open new positions again. Stopped strategies must be started again explicitly. It is announced to Telegram.'
}
