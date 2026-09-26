import { describe, expect, it } from 'vitest'
import { dhanAutoSignInView, type DhanAutoSignInStatus } from './dhanSignIn'

const base: DhanAutoSignInStatus = {
  configured: true,
  enabled: true,
  missing: [],
  morningFromIst: '08:00',
  morningUntilIst: '08:40',
  stoppedForToday: false,
  lastAttemptUtc: null,
  lastOk: null,
  lastTrigger: null,
  lastMessage: null,
  lastExpiresUtc: null,
}

describe('dhanAutoSignInView', () => {
  it('names what is missing and keeps Connect as the answer until it is set', () => {
    const view = dhanAutoSignInView({ ...base, configured: false, missing: ['DHAN_PIN', 'DHAN_TOTP_SECRET'] })
    expect(view.label).toBe('Not set up')
    expect(view.detail).toContain('DHAN_PIN and DHAN_TOTP_SECRET')
    expect(view.detail).toContain('Connect')
  })

  it('reads a day stopped after a refusal as a problem, not as quiet', () => {
    const view = dhanAutoSignInView({ ...base, stoppedForToday: true, lastOk: false, lastMessage: 'Dhan refused the PIN + TOTP sign-in (400: Invalid TOTP).' })
    expect(view.tone).toBe('neg')
    expect(view.detail).toContain('Invalid TOTP')
  })

  it('says when it runs before it has ever run', () => {
    const view = dhanAutoSignInView(base)
    expect(view.tone).toBe('pos')
    expect(view.detail).toContain('08:00 and 08:40 IST')
  })

  it('leads with what the last sign-in said', () => {
    const view = dhanAutoSignInView({ ...base, lastOk: true, lastMessage: 'Signed in with the PIN and a TOTP code; token valid until Wed 08:00 IST.' })
    expect(view.detail.startsWith('Signed in with the PIN')).toBe(true)
  })

  it('does not call a pending retry a stop', () => {
    const view = dhanAutoSignInView({ ...base, lastOk: false, lastMessage: 'Dhan could not be reached.' })
    expect(view.tone).toBe('warn')
    expect(view.detail).toContain('try again')
  })
})
