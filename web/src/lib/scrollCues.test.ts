import { describe, expect, it } from 'vitest'
import { overflowState } from './scrollCues'

describe('overflowState', () => {
  it('is empty when everything fits', () => {
    expect(overflowState(300, 300, 0)).toBe('')
    expect(overflowState(301, 300, 0)).toBe('')
  })
  it('points right at the start, left at the end, both in between', () => {
    expect(overflowState(900, 300, 0)).toBe('right')
    expect(overflowState(900, 300, 1)).toBe('right')
    expect(overflowState(900, 300, 300)).toBe('both')
    expect(overflowState(900, 300, 599)).toBe('left')
    expect(overflowState(900, 300, 600)).toBe('left')
  })
})
