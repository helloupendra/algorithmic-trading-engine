import { describe, expect, it } from 'vitest'
import { CHAPTERS, LAYERS, LAYER_BASE, chapterProgress, layerY, locate, stateAt } from './story'

describe('the story', () => {
  it('covers 0…1 without gaps or overlaps, each plateau inside its chapter', () => {
    expect(CHAPTERS[0].from).toBe(0)
    expect(CHAPTERS[CHAPTERS.length - 1].to).toBe(1)
    CHAPTERS.forEach((c, i) => {
      expect(c.hold[0]).toBeGreaterThanOrEqual(c.from)
      expect(c.hold[1]).toBeLessThanOrEqual(c.to)
      expect(c.hold[1]).toBeGreaterThan(c.hold[0])
      if (i > 0) expect(c.from).toBe(CHAPTERS[i - 1].to)
    })
  })
  it('has one chapter per layer, in pipeline order, between a closed engine and a closed engine', () => {
    expect(CHAPTERS.map((c) => c.key)).toEqual(['hero', ...LAYERS, 'open'])
    expect(CHAPTERS.filter((c) => c.layer >= 0).map((c) => c.layer)).toEqual([0, 1, 2, 3, 4])
    expect(CHAPTERS[0].explode).toBe(0)
    expect(CHAPTERS[CHAPTERS.length - 1].explode).toBe(0)
  })
})

describe('layerY', () => {
  it('keeps the layers inside the two-metre case when assembled, in order', () => {
    for (let i = 0; i < LAYER_BASE.length; i++) {
      expect(layerY(i, 0)).toBe(LAYER_BASE[i])
      expect(layerY(i, 0)).toBeLessThan(2)
      if (i > 0) expect(layerY(i, 0)).toBeGreaterThan(layerY(i - 1, 0))
    }
  })
  it('spreads them apart when exploded and never moves the bottom one', () => {
    expect(layerY(0, 1)).toBe(layerY(0, 0))
    for (let i = 1; i < LAYER_BASE.length; i++) {
      expect(layerY(i, 1) - layerY(i - 1, 1)).toBeGreaterThan(0.9)
    }
  })
})

describe('locate', () => {
  it('holds a whole chapter index across each plateau', () => {
    CHAPTERS.forEach((c, i) => {
      expect(locate(c.hold[0]).u).toBe(i)
      expect(locate((c.hold[0] + c.hold[1]) / 2).u).toBe(i)
      expect(locate(c.hold[1]).u).toBe(i)
    })
  })
  it('is monotonic and clamps outside the story', () => {
    let last = 0
    for (let p = 0; p <= 1; p += 0.001) {
      const { u } = locate(p)
      expect(u).toBeGreaterThanOrEqual(last)
      last = u
    }
    expect(locate(-1).u).toBe(0)
    expect(locate(2).u).toBe(CHAPTERS.length - 1)
  })
})

describe('stateAt', () => {
  it('starts and ends on the closed engine, centred, turning on its own', () => {
    for (const p of [0, 1]) {
      const s = stateAt(p)
      expect(s.explode).toBe(0)
      expect(s.spin).toBe(1)
      expect(s.shiftX).toBe(0)
      expect(s.focus.every((f) => f === 0)).toBe(true)
    }
  })
  it('lights exactly the layer the camera is on during a plateau', () => {
    LAYERS.forEach((key, k) => {
      const s = stateAt(chapterProgress(key))
      expect(s.explode).toBe(1)
      expect(s.focus[k]).toBe(1)
      s.focus.forEach((f, other) => {
        if (other !== k) expect(f).toBe(0)
      })
      expect(s.targetY).toBeCloseTo(layerY(k, 1) + 0.16)
    })
  })
  it('alternates the side the engine sits on, leaving the other for the copy', () => {
    const sides = LAYERS.map((key) => Math.sign(stateAt(chapterProgress(key)).shiftX))
    expect(sides).toEqual([1, -1, 1, -1, 1])
  })
  it('moves continuously between chapters', () => {
    let prev = stateAt(0)
    for (let p = 0.002; p <= 1; p += 0.002) {
      const s = stateAt(p)
      expect(Math.abs(s.targetY - prev.targetY)).toBeLessThan(0.2)
      expect(Math.abs(s.azim - prev.azim)).toBeLessThan(6)
      prev = s
    }
  })
})
