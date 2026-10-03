import { describe, expect, it } from 'vitest'
import { polledDown, polledState } from './queryState'

describe('polledState', () => {
  it('waits for the first answer', () => {
    expect(polledState({ data: undefined, isError: false, errorUpdateCount: 0 })).toBe('waiting')
  })
  it('stays failed while a refetch of a never-answered query is pending again', () => {
    // v5 resets status to pending at each refetch: isError is false, the failure is in errorUpdateCount.
    expect(polledState({ data: undefined, isError: true, errorUpdateCount: 1 })).toBe('failed')
    expect(polledState({ data: undefined, isError: false, errorUpdateCount: 1 })).toBe('failed')
  })
  it('keeps a known answer, marked stale once its refresh fails', () => {
    expect(polledState({ data: { ok: 1 }, isError: false, errorUpdateCount: 3 })).toBe('ok')
    expect(polledState({ data: { ok: 1 }, isError: true, errorUpdateCount: 4 })).toBe('stale')
  })
})

describe('polledDown', () => {
  it('reads the API as down through every poll of an outage, not only between them', () => {
    expect(polledDown({ data: undefined, isError: false, errorUpdateCount: 2 })).toBe(true)
    expect(polledDown({ data: { up: true }, isError: true, errorUpdateCount: 2 })).toBe(true)
    expect(polledDown({ data: { up: true }, isError: false, errorUpdateCount: 2 })).toBe(false)
    expect(polledDown({ data: undefined, isError: false, errorUpdateCount: 0 })).toBe(false)
  })
})
