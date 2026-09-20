import { describe, expect, it } from 'vitest'
import { follow, progressOf, smooth } from './timeline'

describe('smooth', () => {
  it('pins 0 and 1, passes through the middle, and is flat at both ends', () => {
    expect(smooth(0)).toBe(0)
    expect(smooth(1)).toBe(1)
    expect(smooth(0.5)).toBeCloseTo(0.5)
    expect(smooth(0.01)).toBeLessThan(0.001)
    expect(1 - smooth(0.99)).toBeLessThan(0.001)
  })
})

describe('follow', () => {
  it('covers half the distance in one half-life whatever the frame time', () => {
    // One 0.2 s frame, or ten 0.02 s frames: same place.
    const one = follow(0, 1, 0.2, 0.2)
    let x = 0
    for (let i = 0; i < 10; i++) x = follow(x, 1, 0.02, 0.2)
    expect(one).toBeCloseTo(0.5)
    expect(x).toBeCloseTo(0.5)
  })
  it('snaps when there is no half-life or no time', () => {
    expect(follow(0, 1, 0.016, 0)).toBe(1)
    expect(follow(0, 1, 0, 0.2)).toBe(1)
  })
})

describe('progressOf', () => {
  it('is 0 at the top of the track, 1 when its bottom meets the viewport bottom', () => {
    expect(progressOf(0, 5000, 1000)).toBe(0)
    expect(progressOf(-2000, 5000, 1000)).toBeCloseTo(0.5)
    expect(progressOf(-4000, 5000, 1000)).toBe(1)
    expect(progressOf(-9000, 5000, 1000)).toBe(1)
    expect(progressOf(300, 5000, 1000)).toBe(0)
  })
  it('is 0 for a track no taller than the viewport', () => {
    expect(progressOf(-100, 800, 1000)).toBe(0)
  })
})
