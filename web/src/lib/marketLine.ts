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

export function marketLine(s: MarketLineSession, now: Date = new Date()): { open: boolean; text: string } {
  if (s.isMarketOpen) {
    const special = s.specialSessionName ? ` · ${s.specialSessionName}` : ''
    return { open: true, text: `NSE open${special} · closes ${time(s.sessionCloseUtc)} IST` }
  }
  const why = s.isHoliday && s.holidayName ? ` for ${s.holidayName}` : ''
  return { open: false, text: `NSE closed${why} · opens ${when(s.nextMarketOpenUtc, now)} IST` }
}

/** "Sun 15:46 IST": the time in India now, for a title on the line. */
export function istNow(now: Date = new Date()): string {
  return `${IST_DAY.format(now)} ${IST_TIME.format(now)} IST`
}
