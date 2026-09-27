import { describe, expect, it } from 'vitest'

import { runSpan } from './runHistory'

describe('runSpan', () => {
  it('reads a run inside one IST day as the day, then from and to', () => {
    expect(runSpan({ startedUtc: '2026-09-25T03:48:48Z', stoppedUtc: '2026-09-25T10:01:10Z', isActive: false })).toBe('25 Sept 09:18 → 15:31')
  })

  it('names the day on both ends when a run crossed midnight in India', () => {
    expect(runSpan({ startedUtc: '2026-09-25T12:30:00Z', stoppedUtc: '2026-09-25T18:40:00Z', isActive: false })).toBe(
      '25 Sept 18:00 → 26 Sept 00:10',
    )
  })

  it('says a live run is running rather than giving it an end', () => {
    expect(runSpan({ startedUtc: '2026-09-28T03:46:00Z', stoppedUtc: null, isActive: true })).toBe('28 Sept 09:16 → running')
  })

  it('shows what it knows of a run with no stop time, and a dash for none', () => {
    expect(runSpan({ startedUtc: '2026-09-28T03:46:00Z', stoppedUtc: null, isActive: false })).toBe('28 Sept 09:16')
    expect(runSpan({ startedUtc: null, stoppedUtc: null, isActive: false })).toBe('—')
  })
})
