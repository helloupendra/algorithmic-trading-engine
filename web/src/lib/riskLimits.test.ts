import { describe, expect, it } from 'vitest'

import {
  LIMIT_RANGES,
  dailyLossHelp,
  draftDiffers,
  draftToLimits,
  killSwitchConfirmText,
  killSwitchView,
  limitsToDraft,
} from './riskLimits'
import type { LimitsDraft } from './riskLimits'
import type { KillSwitchState } from './types'

/**
 * The Risk page's controls. The API stores the max daily loss negative (−50,000 by default) and refuses a
 * positive one; every input had min=0, so the stored value failed the browser's own check and "Save limits" did
 * nothing, while a cleared field saved 0. And a failed kill-switch read showed "Trading allowed" in green.
 */

const stored = { maxOrdersPerMinute: 60, maxDailyLoss: -50_000, maxConcurrentRuns: 10, maxRunsPerUser: 0 }

describe('the trading limits form', () => {
  it('shows the stored daily loss as a positive amount', () => {
    expect(limitsToDraft(stored)).toEqual({
      maxOrdersPerMinute: '60',
      maxDailyLossAmount: '50000',
      maxConcurrentRuns: '10',
      maxRunsPerUser: '0',
    })
  })

  it('saves the stored limits back unchanged, the loss negative again', () => {
    expect(draftToLimits(limitsToDraft(stored))).toEqual({ ok: true, limits: stored })
  })

  it('sends a typed loss amount as its negative, with decimals kept', () => {
    const draft = { ...limitsToDraft(stored), maxDailyLossAmount: '25000.5' }
    const checked = draftToLimits(draft)
    expect(checked.ok && checked.limits.maxDailyLoss).toBe(-25_000.5)
  })

  it('sends a typed 0 as a plain 0, not −0', () => {
    const checked = draftToLimits({ ...limitsToDraft(stored), maxDailyLossAmount: '0' })
    expect(checked.ok).toBe(true)
    expect(checked.ok && Object.is(checked.limits.maxDailyLoss, 0)).toBe(true)
  })

  it('refuses a cleared field instead of saving 0', () => {
    const checked = draftToLimits({ ...limitsToDraft(stored), maxDailyLossAmount: '  ' })
    expect(checked).toEqual({ ok: false, errors: { maxDailyLossAmount: 'Required.' } })
  })

  it('refuses a negative loss amount (the sign is the form’s job)', () => {
    const checked = draftToLimits({ ...limitsToDraft(stored), maxDailyLossAmount: '-50000' })
    expect(checked).toEqual({ ok: false, errors: { maxDailyLossAmount: '0 or more.' } })
  })

  it('holds orders per minute to the API’s 1–10,000 and the run caps to whole numbers 0–500', () => {
    const draft: LimitsDraft = {
      maxOrdersPerMinute: '0',
      maxDailyLossAmount: 'abc',
      maxConcurrentRuns: '2.5',
      maxRunsPerUser: '501',
    }
    expect(draftToLimits(draft)).toEqual({
      ok: false,
      errors: {
        maxOrdersPerMinute: 'Between 1 and 10000.',
        maxDailyLossAmount: 'Enter a number.',
        maxConcurrentRuns: 'Enter a whole number.',
        maxRunsPerUser: 'Between 0 and 500.',
      },
    })
    expect(draftToLimits({ ...limitsToDraft(stored), maxOrdersPerMinute: '10001' })).toEqual({
      ok: false,
      errors: { maxOrdersPerMinute: 'Between 1 and 10000.' },
    })
  })

  it('gives the inputs the same ranges the check uses, so neither refuses what the other allows', () => {
    expect(LIMIT_RANGES.maxDailyLossAmount.min).toBe(0)
    expect(LIMIT_RANGES.maxOrdersPerMinute).toEqual({ min: 1, max: 10_000, whole: true })
  })

  it('knows when the form differs from the stored limits', () => {
    expect(draftDiffers(limitsToDraft(stored), stored)).toBe(false)
    expect(draftDiffers({ ...limitsToDraft(stored), maxRunsPerUser: ' 0 ' }, stored)).toBe(false)
    expect(draftDiffers({ ...limitsToDraft(stored), maxDailyLossAmount: '40000' }, stored)).toBe(true)
  })

  it('says what the daily loss measures, with the amount typed', () => {
    const rupees = (n: number) => `₹${n.toLocaleString('en-IN')}`
    expect(dailyLossHelp(50_000, rupees)).toBe(
      'Per run, on its net P&L for the IST day (after charges): a run whose net today falls below −₹50,000 ' +
        'cannot open new positions until the next day; exits stay allowed.',
    )
    expect(dailyLossHelp(0, rupees)).toContain('as soon as its net today is below zero')
    expect(dailyLossHelp(null, rupees)).toContain('below minus this amount')
    expect(dailyLossHelp(Number.NaN, rupees)).toContain('below minus this amount')
  })
})

describe('the kill switch panel', () => {
  const off: KillSwitchState = { isActive: false, updatedBy: null, reason: null, updatedUtc: null }
  const on: KillSwitchState = { isActive: true, updatedBy: 'admin', reason: 'feed stall', updatedUtc: '2026-11-02T05:00:00Z' }

  it('is unknown, not "Trading allowed", when the first read failed', () => {
    expect(killSwitchView({ data: undefined, isError: true })).toEqual({ kind: 'unknown' })
  })

  it('is still reading before the first answer', () => {
    expect(killSwitchView({ data: undefined, isError: false })).toEqual({ kind: 'reading' })
  })

  it('shows the API’s answer, marked stale when the latest refresh failed', () => {
    expect(killSwitchView({ data: off, isError: false })).toEqual({ kind: 'off', state: off, stale: false })
    expect(killSwitchView({ data: on, isError: false })).toEqual({ kind: 'on', state: on, stale: false })
    expect(killSwitchView({ data: off, isError: true })).toEqual({ kind: 'off', state: off, stale: true })
  })

  it('asks before pulling, naming every open position in every account', () => {
    const text = killSwitchConfirmText(true)
    expect(text.startsWith('Pull the kill switch?')).toBe(true)
    expect(text).toContain('every open position in every account')
    expect(text).toContain('squared off')
  })

  it('asks before releasing too', () => {
    const text = killSwitchConfirmText(false)
    expect(text.startsWith('Release the kill switch?')).toBe(true)
    expect(text).toContain('new positions again')
  })
})
