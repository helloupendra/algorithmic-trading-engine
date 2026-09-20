import { describe, expect, it } from 'vitest'
import { readMarketClock } from './marketClock'

/** A UTC instant for an IST wall-clock time (IST is UTC+5:30, no daylight saving). */
const ist = (iso: string) => new Date(`${iso}+05:30`)

describe('readMarketClock', () => {
  it('formats the time in IST whatever the visitor\'s zone', () => {
    expect(readMarketClock(ist('2026-09-21T14:32:07')).time).toBe('14:32:07')
  })
  it('opens at 09:15 and closes at 15:30 on a weekday', () => {
    expect(readMarketClock(ist('2026-09-21T09:14:59')).open).toBe(false)
    expect(readMarketClock(ist('2026-09-21T09:15:00')).open).toBe(true)
    expect(readMarketClock(ist('2026-09-21T15:29:59')).open).toBe(true)
    expect(readMarketClock(ist('2026-09-21T15:30:00')).open).toBe(false)
  })
  it('is closed at the weekend', () => {
    expect(readMarketClock(ist('2026-09-19T11:00:00')).open).toBe(false)
    expect(readMarketClock(ist('2026-09-20T11:00:00')).open).toBe(false)
  })
})
