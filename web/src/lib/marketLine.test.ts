import { describe, expect, it } from 'vitest'
import { CASH_HOURS, istNow, marketLine } from './marketLine'

const base = { isHoliday: false, holidayName: null, specialSessionName: null, sessionCloseUtc: '2026-11-06T10:00:00Z', nextMarketOpenUtc: '2026-11-09T03:45:00Z' }

describe('the public market line', () => {
  it('says when an open market closes, in IST', () => {
    expect(marketLine({ ...base, isMarketOpen: true })).toEqual({ open: true, text: 'NSE open · closes 15:30 IST', short: 'NSE open · closes 15:30 IST' })
  })

  it('names a special session such as Muhurat trading', () => {
    const s = { ...base, isMarketOpen: true, specialSessionName: 'Muhurat trading', sessionCloseUtc: '2026-11-08T10:10:00Z' }
    expect(marketLine(s).text).toBe('NSE open · Muhurat trading · closes 15:40 IST')
  })

  it('gives the day of the next open when it is not today', () => {
    // Sunday 8 Nov 2026, 15:46 IST; next open Monday 09:15.
    expect(marketLine({ ...base, isMarketOpen: false }, new Date('2026-11-08T10:16:00Z')).text).toBe('NSE closed · opens Mon 09:15 IST')
  })

  it('names a holiday, and drops the day when the next open is later today', () => {
    const s = { ...base, isMarketOpen: false, isHoliday: true, holidayName: 'Diwali Balipratipada', nextMarketOpenUtc: '2026-11-11T03:45:00Z' }
    expect(marketLine(s, new Date('2026-11-10T05:00:00Z')).text).toBe('NSE closed for Diwali Balipratipada · opens Wed 09:15 IST')
    expect(marketLine({ ...base, isMarketOpen: false, nextMarketOpenUtc: '2026-11-09T03:45:00Z' }, new Date('2026-11-09T02:00:00Z')).text).toBe('NSE closed · opens 09:15 IST')
  })

  it('keeps a short line for a narrow header: a holiday is not named there, however long its name', () => {
    const s = { ...base, isMarketOpen: false, isHoliday: true, holidayName: 'Prakash Gurpurb Sri Guru Nanak Dev', nextMarketOpenUtc: '2026-11-25T03:45:00Z' }
    const line = marketLine(s, new Date('2026-11-24T05:00:00Z'))
    expect(line.text).toBe('NSE closed for Prakash Gurpurb Sri Guru Nanak Dev · opens Wed 09:15 IST')
    expect(line.short).toBe('NSE closed · holiday · opens Wed 09:15 IST')
    expect(marketLine({ ...base, isMarketOpen: false }, new Date('2026-11-08T10:16:00Z')).short).toBe('NSE closed · opens Mon 09:15 IST')
    const muhurat = { ...base, isMarketOpen: true, specialSessionName: 'Muhurat trading', sessionCloseUtc: '2026-11-08T10:10:00Z' }
    expect(marketLine(muhurat).short).toBe('NSE open · Muhurat trading · closes 15:40 IST')
    expect(marketLine({ ...muhurat, specialSessionName: 'A special live trading session for the budget' }).short).toBe('NSE open · special session · closes 15:40 IST')
  })

  it('has a line that is true before any answer, and the time in India', () => {
    expect(CASH_HOURS).toBe('NSE hours · 09:15–15:30 IST')
    expect(istNow(new Date('2026-11-08T10:16:00Z'))).toBe('Sun 15:46 IST')
  })
})
