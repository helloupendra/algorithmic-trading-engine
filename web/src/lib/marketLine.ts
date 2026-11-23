/**
 * One line for the public pages (the homepage header, the sign-in page):
 * whether NSE is open and when it closes or next opens, from the API's own
 * exchange calendar (GET /api/MarketSession/check, open to anyone). A holiday
 * or a special session (Muhurat trading) is named. The pages show the cash
 * hours until an answer arrives, never a guess from the visitor's clock,
 * which cannot know a holiday.
 */

import type { MarketSessionInfo } from './types'

const IST_TIME = new Intl.DateTimeFormat('en-GB', { timeZone: 'Asia/Kolkata', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' })
const IST_DAY = new Intl.DateTimeFormat('en-GB', { timeZone: 'Asia/Kolkata', weekday: 'short' })
const IST_DATE = new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Kolkata' })

/** What the pages say before (or without) an answer: true at any hour. */
export const CASH_HOURS = 'NSE hours · 09:15–15:30 IST'

const time = (iso: string) => IST_TIME.format(new Date(iso))

/** "Mon 09:15", or just "09:15" when it is later the same IST day. */
function when(iso: string, now: Date): string {
  const d = new Date(iso)
  return IST_DATE.format(d) === IST_DATE.format(now) ? time(iso) : `${IST_DAY.format(d)} ${time(iso)}`
}

export type MarketLineSession = Pick<
  MarketSessionInfo,
  'isMarketOpen' | 'sessionCloseUtc' | 'nextMarketOpenUtc' | 'isHoliday' | 'holidayName' | 'specialSessionName'
>

/** The longest special-session name the short line spells out ("Muhurat trading"); a longer one reads "special session". */
const SHORT_NAME = 16

/**
 * `text` names the holiday or the session. `short` is the same line for a narrow place (the homepage's header):
 * a holiday is "holiday" there, because an exchange's name for one can be fifty characters ("Prakash Gurpurb Sri
 * Guru Nanak Dev" ran over the header's button on 24 Nov 2026), and the full line goes in the element's title.
 */
export function marketLine(s: MarketLineSession, now: Date = new Date()): { open: boolean; text: string; short: string } {
  if (s.isMarketOpen) {
    const closes = `closes ${time(s.sessionCloseUtc)} IST`
    const name = s.specialSessionName
    const brief = name ? ` · ${name.length <= SHORT_NAME ? name : 'special session'}` : ''
    return { open: true, text: `NSE open${name ? ` · ${name}` : ''} · ${closes}`, short: `NSE open${brief} · ${closes}` }
  }
  const opens = `opens ${when(s.nextMarketOpenUtc, now)} IST`
  const why = s.isHoliday && s.holidayName ? ` for ${s.holidayName}` : ''
  return { open: false, text: `NSE closed${why} · ${opens}`, short: `NSE closed${s.isHoliday ? ' · holiday' : ''} · ${opens}` }
}

/** "Sun 15:46 IST": the time in India now, for a title on the line. */
export function istNow(now: Date = new Date()): string {
  return `${IST_DAY.format(now)} ${IST_TIME.format(now)} IST`
}
