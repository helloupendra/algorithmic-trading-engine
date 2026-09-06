/**
 * What FYERS will actually serve in one history request.
 *
 * The broker refuses an over-long window rather than truncating it, so a picker
 * that lets someone ask for two years of one-minute candles is a picker that
 * produces a failed backfill and no explanation. These are the documented
 * limits (Data API → History → "Limits for History"), kept in one place so the
 * form can grey out what cannot be fetched instead of finding out afterwards.
 */

/** A resolution the backfill form offers. */
export interface ResolutionOption {
  /** The code the API takes: "1", "5", "15", "60", "D". */
  value: string
  label: string
  /** Days of history FYERS returns in one request at this resolution. */
  maxDays: number
}

/**
 * Intraday minute resolutions are capped at 100 days per request; the daily
 * and coarser ones at 366. Both are per request, not per day — there is no
 * limit on how many requests a day.
 */
export const MINUTE_MAX_DAYS = 100
export const DAILY_MAX_DAYS = 366

/** FYERS holds no history before this date at any resolution. */
export const EARLIEST_HISTORY = '2017-07-03'

export const RESOLUTIONS: ResolutionOption[] = [
  { value: '1', label: '1 minute', maxDays: MINUTE_MAX_DAYS },
  { value: '5', label: '5 minutes', maxDays: MINUTE_MAX_DAYS },
  { value: '15', label: '15 minutes', maxDays: MINUTE_MAX_DAYS },
  { value: '60', label: '60 minutes', maxDays: MINUTE_MAX_DAYS },
  { value: 'D', label: '1 day', maxDays: DAILY_MAX_DAYS },
]

export function maxDaysFor(resolution: string): number {
  return RESOLUTIONS.find((r) => r.value === resolution)?.maxDays ?? DAILY_MAX_DAYS
}

/** Whole days from `from` to `to`, inclusive of both ends. */
export function spanDays(from: string, to: string): number {
  const a = Date.parse(`${from}T00:00:00Z`)
  const b = Date.parse(`${to}T00:00:00Z`)
  if (!Number.isFinite(a) || !Number.isFinite(b)) return 0
  return Math.floor((b - a) / 86_400_000) + 1
}

/** The date `days` before `to`, as YYYY-MM-DD. */
export function dateMinusDays(to: string, days: number): string {
  const b = Date.parse(`${to}T00:00:00Z`)
  if (!Number.isFinite(b)) return to
  return new Date(b - (days - 1) * 86_400_000).toISOString().slice(0, 10)
}

/** Why a resolution cannot serve this range, or null when it can. */
export function rejectionFor(resolution: string, from: string, to: string): string | null {
  if (!from || !to) return null
  if (to < from) return 'the end date is before the start date'
  if (from < EARLIEST_HISTORY) return `FYERS holds nothing before ${EARLIEST_HISTORY}`

  const span = spanDays(from, to)
  const max = maxDaysFor(resolution)
  return span > max ? `${span} days asked for; this resolution serves ${max} per request` : null
}

export function isAllowed(resolution: string, from: string, to: string): boolean {
  return rejectionFor(resolution, from, to) === null
}

/**
 * The resolution to fall back to when the chosen one cannot serve the range.
 *
 * The coarsest still-valid option, because that is the one most likely to cover
 * the whole span; null when the range is impossible at every resolution and the
 * dates themselves have to change.
 */
export function fallbackResolution(from: string, to: string): string | null {
  const usable = RESOLUTIONS.filter((r) => isAllowed(r.value, from, to))
  if (usable.length === 0) return null
  return usable.reduce((best, r) => (r.maxDays > best.maxDays ? r : best)).value
}

/**
 * The earliest date this resolution can reach from `to`: its per-request window,
 * or the start of FYERS history, whichever is later.
 *
 * Clamped, because the window alone can name a date the broker has nothing for
 * — 100 days back from August 2017 lands in April, which does not exist here —
 * and a hint pointing at unavailable data is worse than no hint.
 */
export function earliestFor(resolution: string, to: string): string {
  const windowStart = dateMinusDays(to, maxDaysFor(resolution))
  return windowStart < EARLIEST_HISTORY ? EARLIEST_HISTORY : windowStart
}

/**
 * A sentence for the form: what this resolution can currently cover.
 */
export function limitHint(resolution: string, to: string): string {
  return `up to ${maxDaysFor(resolution)} days per request — from ${earliestFor(resolution, to)}`
}
