import { describe, expect, it } from 'vitest'
import { dmyToIso, isoToDmy, maskDmy } from './dates'

describe('isoToDmy', () => {
  it('turns an ISO day around', () => expect(isoToDmy('2026-09-07')).toBe('07/09/2026'))
  it('is empty for nothing or junk', () => {
    expect(isoToDmy('')).toBe('')
    expect(isoToDmy(null)).toBe('')
    expect(isoToDmy('07/09/2026')).toBe('')
  })
})

describe('dmyToIso', () => {
  it('reads dd/mm/yyyy in the Indian order, not the US one', () => {
    expect(dmyToIso('07/09/2026')).toBe('2026-09-07')
    expect(dmyToIso('12/01/2026')).toBe('2026-01-12')
  })
  it('accepts unpadded parts and other separators', () => {
    expect(dmyToIso('7/9/2026')).toBe('2026-09-07')
    expect(dmyToIso('07-09-2026')).toBe('2026-09-07')
    expect(dmyToIso('07.09.2026')).toBe('2026-09-07')
    expect(dmyToIso('07092026')).toBe('2026-09-07')
    expect(dmyToIso('2026-09-07')).toBe('2026-09-07')
  })
  it('refuses incomplete or impossible dates', () => {
    expect(dmyToIso('')).toBeNull()
    expect(dmyToIso('07/09')).toBeNull()
    expect(dmyToIso('07/09/26')).toBeNull()
    expect(dmyToIso('31/02/2026')).toBeNull()
    expect(dmyToIso('29/02/2026')).toBeNull()
    expect(dmyToIso('29/02/2028')).toBe('2028-02-29')
    expect(dmyToIso('00/09/2026')).toBeNull()
    expect(dmyToIso('07/13/2026')).toBeNull()
  })
})

describe('maskDmy', () => {
  it('places the slashes as the digits arrive', () => {
    expect(maskDmy('0')).toBe('0')
    expect(maskDmy('07')).toBe('07')
    expect(maskDmy('070')).toBe('07/0')
    expect(maskDmy('0709')).toBe('07/09')
    expect(maskDmy('07092')).toBe('07/09/2')
    expect(maskDmy('07092026')).toBe('07/09/2026')
  })
  it('pads a one-digit day or month closed with a separator', () => {
    expect(maskDmy('7/')).toBe('07/')
    expect(maskDmy('7/9/')).toBe('07/09/')
    expect(maskDmy('7/9/2026')).toBe('07/09/2026')
  })
  it('never grows past dd/mm/yyyy and drops letters', () => {
    expect(maskDmy('07/09/20261')).toBe('07/09/2026')
    expect(maskDmy('0a7b')).toBe('07')
  })
  it('turns a pasted ISO date around', () => expect(maskDmy('2026-09-07')).toBe('07/09/2026'))
})
