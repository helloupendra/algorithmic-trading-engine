import { describe, expect, it } from 'vitest'
import { formatDayRange, runSpecLabel } from './shared'

describe('backtest date ranges', () => {
  it('distinguishes the years when a later month starts an earlier year', () => {
    expect(formatDayRange('2025-11-01', '2026-10-30')).toBe('01 Nov 2025 → 30 Oct 2026')
  })

  it('keeps both years visible for same-year and single-day runs', () => {
    expect(formatDayRange('2026-01-01', '2026-10-30')).toBe('01 Jan 2026 → 30 Oct 2026')
    expect(formatDayRange('2028-02-29', '2028-02-29')).toBe('29 Feb 2028 → 29 Feb 2028')
  })

  it('keeps missing or unreadable bounds distinct from a real date', () => {
    expect(formatDayRange(null, '2026-10-30')).toBe('— → 30 Oct 2026')
    expect(formatDayRange('2025-11-01', undefined)).toBe('01 Nov 2025 → —')
    expect(formatDayRange('', 'not a date')).toBe('— → —')
  })

  it('uses the IST calendar boundary for timestamps, including a change of year', () => {
    expect(formatDayRange('2025-12-31T18:29:59Z', '2025-12-31T18:30:00Z')).toBe('31 Dec 2025 → 01 Jan 2026')
    expect(formatDayRange('2026-01-01T00:00:00+05:30', '2026-01-01T23:59:59+05:30')).toBe('01 Jan 2026 → 01 Jan 2026')
  })

  it('includes the same full range in the run detail description', () => {
    expect(runSpecLabel({ underlying: 'NIFTY', resolution: '5', fromDate: '2025-11-01', toDate: '2026-10-30', lots: 2, lotSize: 65 }))
      .toBe('NIFTY · 5m · 01 Nov 2025 → 30 Oct 2026 · 2 lots × 65 = 130')
  })
})
