/**
 * The option chain's replay clock: the recorded day the Chain and OI history
 * tabs are rewound to, and the minute on it. Both travel in the URL (`d` and
 * `at`), so a link to NIFTY's chain on 14 Oct at 11:00 opens that chain:
 * `/markets/chain?u=NIFTY&d=2026-10-14&at=11:00`.
 *
 * No `d` is the newest recorded session, which the page shows live; a
 * minute on it is still written with its day, so the link names the same
 * minute tomorrow. A past day with no minute is read to its end: its last
 * capture. Pure functions, pinned by chainReplay.test.ts.
 */

import { istDate, istStamp, istTime, replayAsOfUtc } from './optionChain'
import { dayLabel } from './straddle'
import type { OptionChainRecordedExpiries } from './types'

export interface ReplayClock {
  /** An IST day, "2026-10-14"; null for the newest session. */
  day: string | null
  /** An IST minute, "11:00"; null for live, or for a past day's last capture. */
  time: string | null
}

export const LIVE: ReplayClock = { day: null, time: null }

const DAY = /^(\d{4})-(\d{2})-(\d{2})$/
const MINUTE = /^([01]\d|2[0-3]):[0-5]\d$/

/** A calendar day as the URL carries it, or null for anything that is not one ("2026-02-30" included). */
export function replayDay(value: string | null | undefined): string | null {
  const match = DAY.exec(value ?? '')
  if (!match) return null
  const [y, m, d] = [Number(match[1]), Number(match[2]), Number(match[3])]
  const date = new Date(Date.UTC(y, m - 1, d))
  return date.getUTCFullYear() === y && date.getUTCMonth() === m - 1 && date.getUTCDate() === d ? match[0] : null
}

/** The clock a URL asks for. A malformed `d` or `at` reads as absent, never as some other moment. */
export function readReplayClock(search: string | URLSearchParams): ReplayClock {
  const params = new URLSearchParams(search)
  const time = params.get('at')
  return { day: replayDay(params.get('d')), time: time && MINUTE.test(time) ? time : null }
}

/** The later of two "yyyy-mm-dd" days, either of which may be unknown. */
export function laterDay(a: string | null | undefined, b: string | null | undefined): string | null {
  if (!a) return b || null
  if (!b) return a
  return a > b ? a : b
}

/**
 * The URL with the clock moved to `next`. The newest session without a
 * minute is the page's default (live) and writes neither; a minute is
 * written with its day, even today's, so the link does not slide to the
 * next session overnight. Moving to another day drops the expiry the URL
 * named: another day records other expiries, and the picker starts on that
 * day's nearest.
 */
export function withReplayClock(
  search: string | URLSearchParams,
  next: ReplayClock,
  newestDay: string | null | undefined,
): URLSearchParams {
  const params = new URLSearchParams(search)
  const before = readReplayClock(params).day ?? newestDay ?? null
  const day = next.day ?? newestDay ?? null

  if (next.time) params.set('at', next.time)
  else params.delete('at')
  if (day && (next.time || day !== newestDay)) params.set('d', day)
  else params.delete('d')
  if (day !== before) params.delete('expiry')
  return params
}

/**
 * The instant a rewound view asks for, or undefined for live: the minute on
 * the chosen day (on the newest session when only a minute is given), or a
 * past day's end when no minute is. The end of the minute, through
 * replayAsOfUtc, so the Chain and OI history stop at the same capture.
 */
export function replayClockAsOfUtc(clock: ReplayClock, newestDay: string | null | undefined): string | undefined {
  if (clock.day) return replayAsOfUtc(clock.day, clock.time ?? '23:59')
  if (clock.time && newestDay) return replayAsOfUtc(newestDay, clock.time)
  return undefined
}

/** 00:00 IST of a day, as an ISO instant: where one session's OI history starts. */
export function sessionStartUtc(day: string | null | undefined): string | undefined {
  if (!replayDay(day)) return undefined
  return new Date(Date.parse(`${day}T00:00:00+05:30`)).toISOString()
}

export interface ReplayDayOption {
  date: string
  label: string
}

/**
 * The day picker's list, newest first: every recorded day ("Wed 14 Oct",
 * with the year when it is not this one), the newest session marked as
 * today's or, before today's first capture, the latest. The newest session
 * the page is showing and a day a link named are listed even when the list
 * has not got them (yet); the second says "not recorded" once the list has
 * answered without it, rather than the picker showing some other day.
 */
export function replayDayOptions(
  recorded: readonly string[] | undefined,
  newestDay: string | null | undefined,
  picked: string | null,
  todayIso: string,
): ReplayDayOption[] {
  const dates = new Set(recorded ?? [])
  for (const extra of [newestDay, picked]) if (extra) dates.add(extra)
  return [...dates]
    .sort((a, b) => b.localeCompare(a))
    .map((date) => {
      const name = date.slice(0, 4) === todayIso.slice(0, 4) ? dayLabel(date) : `${dayLabel(date)} ${date.slice(0, 4)}`
      let mark = ''
      if (date === newestDay) mark = date === todayIso ? ' · today' : ' · latest'
      else if (recorded && !recorded.includes(date)) mark = ' · not recorded'
      return { date, label: `${name}${mark}` }
    })
}

/**
 * The time input's bounds on the day shown: the first and last capture of
 * the expiry on screen that day (the day's nearest when it was not recorded
 * then), as IST minutes. Empty when the day's expiries have not answered.
 */
export function captureBounds(
  recorded: Pick<OptionChainRecordedExpiries, 'expiries'> | undefined,
  expiry: string | null | undefined,
): { min?: string; max?: string } {
  const list = recorded?.expiries ?? []
  const shown = list.find((e) => e.expiryDate === expiry) ?? list.find((e) => e.isDefault) ?? list[0]
  if (!shown) return {}
  return { min: istTime(shown.firstCapturedUtc), max: istTime(shown.lastCapturedUtc) }
}

/** The moment a rewound page stands at, in words: "Wed 14 Oct 11:00 IST", or "the end of Wed 14 Oct" with no minute. */
export function replayMoment(day: string, time: string | null): string {
  return time ? `${dayLabel(day)} ${time} IST` : `the end of ${dayLabel(day)}`
}

/**
 * What a rewound page says when the capture it found is from before the day
 * it was rewound to: nothing of that expiry was captured on the day by then,
 * so the chain shown is the last one before it. Null when the capture is the
 * day's own, and for one after it: that is the chain of before the rewind,
 * still on screen while the rewound one loads.
 */
export function replayGapNote(day: string, time: string | null, capturedUtc: string | null | undefined): string | null {
  if (!capturedUtc || Number.isNaN(Date.parse(capturedUtc))) return null
  if (istDate(Date.parse(capturedUtc)) >= day) return null
  return `Nothing of this expiry was captured on ${dayLabel(day)}${time ? ` by ${time} IST` : ''}: this is the last capture before it, ${istStamp(capturedUtc)} IST.`
}
