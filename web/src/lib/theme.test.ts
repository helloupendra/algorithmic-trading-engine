import { describe, expect, it } from 'vitest'
import { THEME_KEY, parseThemePref, readThemePref, resolveTheme, writeThemePref } from './theme'

function fakeStorage(initial: Record<string, string> = {}) {
  const map = new Map(Object.entries(initial))
  return {
    getItem: (k: string) => map.get(k) ?? null,
    setItem: (k: string, v: string) => void map.set(k, v),
    removeItem: (k: string) => void map.delete(k),
    map,
  }
}

describe('parseThemePref', () => {
  it('reads the three words and nothing else', () => {
    expect(parseThemePref('light')).toBe('light')
    expect(parseThemePref('system')).toBe('system')
    expect(parseThemePref('dark')).toBe('dark')
  })
  it('falls back to dark for a missing or corrupted value', () => {
    expect(parseThemePref(null)).toBe('dark')
    expect(parseThemePref(undefined)).toBe('dark')
    expect(parseThemePref('LIGHT')).toBe('dark')
    expect(parseThemePref('{"x":1}')).toBe('dark')
  })
})

describe('resolveTheme', () => {
  it('follows the device only for "system"', () => {
    expect(resolveTheme('system', true)).toBe('dark')
    expect(resolveTheme('system', false)).toBe('light')
    expect(resolveTheme('light', true)).toBe('light')
    expect(resolveTheme('dark', false)).toBe('dark')
  })
})

describe('the stored preference', () => {
  it('reads what was written', () => {
    const s = fakeStorage()
    writeThemePref('light', s)
    expect(readThemePref(s)).toBe('light')
    writeThemePref('system', s)
    expect(readThemePref(s)).toBe('system')
  })
  it('stores the default as no key, so a reset browser reads the same', () => {
    const s = fakeStorage({ [THEME_KEY]: 'light' })
    writeThemePref('dark', s)
    expect(s.map.has(THEME_KEY)).toBe(false)
    expect(readThemePref(s)).toBe('dark')
  })
  it('is dark where storage is unavailable or throws', () => {
    expect(readThemePref(null)).toBe('dark')
    const throwing = {
      getItem: () => {
        throw new Error('blocked')
      },
    }
    expect(readThemePref(throwing)).toBe('dark')
    expect(() => writeThemePref('light', { setItem: throwing.getItem, removeItem: throwing.getItem })).not.toThrow()
  })
})
