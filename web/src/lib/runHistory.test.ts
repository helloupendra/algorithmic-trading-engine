import { describe, expect, it } from 'vitest'

import { historyListPending, recapBadge, runSpan } from './runHistory'

describe('recapBadge', () => {
  it('marks a recap run as a test of the day it replayed', () => {
    expect(recapBadge({ isRecap: true, recapDate: '2026-09-11' })).toEqual({
      label: 'Recap test · 11 Sept',
      title: 'A replay of 11 Sept, run as a test. Its P&L is in no live total.',
    })
  })

  it('still marks a recap whose day is missing or unreadable', () => {
    expect(recapBadge({ isRecap: true, recapDate: null })?.label).toBe('Recap test')
    expect(recapBadge({ isRecap: true, recapDate: 'soon' })?.label).toBe('Recap test · soon')
  })

  it('leaves a live run unmarked, and an older API that sends no flag', () => {
    expect(recapBadge({ isRecap: false, recapDate: null })).toBeNull()
    expect(recapBadge({})).toBeNull()
  })
})

describe('historyListPending', () => {
  const answer = { isPending: false, isPlaceholderData: false }
  const kept = { isPending: false, isPlaceholderData: true }

  it('is loading until the first answer, and not once this list has answered, empty or not', () => {
    expect(historyListPending({ isPending: true, isPlaceholderData: false }, 0, 0)).toBe(true)
    expect(historyListPending(answer, 0, 0)).toBe(false)
    expect(historyListPending(answer, 12, 12)).toBe(false)
  })

  it('keeps the last list on screen while a filter changes, but never the other kind of run', () => {
    // Same kind, another filter: kept on screen while the new list loads.
    expect(historyListPending(kept, 12, 12)).toBe(false)
    // Recap runs switched on over a live list: those rows are dropped, so it loads.
    expect(historyListPending(kept, 12, 0)).toBe(true)
  })

  it('reads an empty list kept from the last filter as loading, never as "no runs for this filter"', () => {
    // A filter (or the Recap runs switch) that answered no runs, then changed: nothing of this list has been read yet.
    expect(historyListPending(kept, 0, 0)).toBe(true)
  })
})

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
