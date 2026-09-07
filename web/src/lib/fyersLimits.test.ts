import { describe, expect, it } from 'vitest'

import {
  DAILY_MAX_DAYS,
  EARLIEST_HISTORY,
  MINUTE_MAX_DAYS,
  dateMinusDays,
  earliestFor,
  fallbackResolution,
  isAllowed,
  limitHint,
  maxDaysFor,
  rejectionFor,
  spanDays,
} from './fyersLimits'

/**
 * FYERS refuses an over-long window rather than truncating it, so anything the
 * form allows but the broker will not serve becomes a failed backfill with no
 * explanation on screen.
 */
describe('spanDays', () => {
  it('counts both ends', () => {
    expect(spanDays('2026-09-01', '2026-09-01')).toBe(1)
    expect(spanDays('2026-09-01', '2026-09-04')).toBe(4)
  })

  it('reads an unparseable date as no span rather than NaN', () => {
    expect(spanDays('', '2026-09-04')).toBe(0)
    expect(spanDays('not a date', '2026-09-04')).toBe(0)
  })
})

describe('dateMinusDays', () => {
  it('lands on the first day of an inclusive window', () => {
    // 100 days ending on the 4th starts on the 4th minus 99.
    expect(dateMinusDays('2026-09-04', 100)).toBe('2026-05-28')
    expect(spanDays(dateMinusDays('2026-09-04', 100), '2026-09-04')).toBe(100)
  })

  it('a one-day window is the same day', () => {
    expect(dateMinusDays('2026-09-04', 1)).toBe('2026-09-04')
  })
})

describe('rejectionFor', () => {
  it('allows a minute resolution inside its 100 days', () => {
    expect(rejectionFor('1', '2026-06-01', '2026-09-04')).toBeNull()
    expect(spanDays('2026-06-01', '2026-09-04')).toBeLessThanOrEqual(MINUTE_MAX_DAYS)
  })

  it('refuses a minute resolution past its 100 days', () => {
    const why = rejectionFor('1', '2026-01-01', '2026-09-04')
    expect(why).toContain('100 per request')
  })

  it('allows the same range at daily', () => {
    expect(rejectionFor('D', '2026-01-01', '2026-09-04')).toBeNull()
  })

  it('refuses even daily past its 366 days', () => {
    expect(rejectionFor('D', '2024-01-01', '2026-09-04')).toContain(String(DAILY_MAX_DAYS))
  })

  it('accepts a window exactly at the limit', () => {
    // Off-by-one here is the difference between working and always failing.
    expect(rejectionFor('1', dateMinusDays('2026-09-04', MINUTE_MAX_DAYS), '2026-09-04')).toBeNull()
    expect(rejectionFor('D', dateMinusDays('2026-09-04', DAILY_MAX_DAYS), '2026-09-04')).toBeNull()
  })

  it('refuses one day past the limit', () => {
    expect(rejectionFor('1', dateMinusDays('2026-09-04', MINUTE_MAX_DAYS + 1), '2026-09-04')).not.toBeNull()
  })

  it('refuses history older than the broker holds', () => {
    expect(rejectionFor('D', '2017-07-02', '2017-08-01')).toContain('03/07/2017')
  })

  it('allows the earliest date itself', () => {
    expect(rejectionFor('D', EARLIEST_HISTORY, '2017-08-01')).toBeNull()
  })

  it('catches a reversed range before the broker does', () => {
    expect(rejectionFor('D', '2026-09-04', '2026-09-01')).toContain('before the start date')
  })

  it('says nothing while the form is still half-filled', () => {
    expect(rejectionFor('1', '', '2026-09-04')).toBeNull()
    expect(rejectionFor('1', '2026-09-04', '')).toBeNull()
  })
})

describe('maxDaysFor', () => {
  it('knows the two documented caps', () => {
    expect(maxDaysFor('1')).toBe(MINUTE_MAX_DAYS)
    expect(maxDaysFor('60')).toBe(MINUTE_MAX_DAYS)
    expect(maxDaysFor('D')).toBe(DAILY_MAX_DAYS)
  })

  it('treats an unknown code as the daily cap rather than throwing', () => {
    expect(maxDaysFor('240')).toBe(DAILY_MAX_DAYS)
  })
})

describe('fallbackResolution', () => {
  it('offers daily when the range is too long for minutes', () => {
    expect(fallbackResolution('2026-01-01', '2026-09-04')).toBe('D')
  })

  it('leaves a short range on the finest option available', () => {
    // Everything fits, so the coarsest is still a valid answer; what matters is
    // that it never returns a resolution the range cannot use.
    expect(isAllowed(fallbackResolution('2026-09-01', '2026-09-04')!, '2026-09-01', '2026-09-04')).toBe(true)
  })

  it('returns nothing when the dates are impossible at any resolution', () => {
    expect(fallbackResolution('2020-01-01', '2026-09-04')).toBeNull()
  })
})

describe('limitHint', () => {
  it('names a date someone can actually type, in the order they type it', () => {
    expect(limitHint('1', '2026-09-04')).toBe('up to 100 days per request — from 28/05/2026')
  })

  it('never points before the broker has any history', () => {
    // 100 days back from August 2017 lands in April, where there is nothing.
    expect(earliestFor('1', '2017-08-01')).toBe(EARLIEST_HISTORY)
    expect(limitHint('1', '2017-08-01')).toContain('03/07/2017')
  })

  it('leaves a reachable window alone', () => {
    expect(earliestFor('1', '2026-09-04')).toBe('2026-05-28')
  })
})
