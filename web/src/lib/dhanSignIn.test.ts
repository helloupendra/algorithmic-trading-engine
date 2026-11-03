import { describe, expect, it } from 'vitest'
import { dhanAutoSignInView, type DhanAutoSignInStatus } from './dhanSignIn'

const base: DhanAutoSignInStatus = {
  configured: true,
  enabled: true,
  missing: [],
  morningFromIst: '08:00',
  morningUntilIst: '08:40',
  stoppedForToday: false,
  codesPausedUntilIst: null,
  codesToday: 0,
  maxCodesPerDay: 9,
  heldByDisconnect: false,
  tokenRefused: false,
  lastAttemptUtc: null,
  lastOk: null,
  lastTrigger: null,
  lastMessage: null,
  lastExpiresUtc: null,
}

describe('dhanAutoSignInView', () => {
  it('says the desk is holding after a Disconnect, ahead of anything it last did', () => {
    const view = dhanAutoSignInView({ ...base, heldByDisconnect: true, lastOk: true, lastMessage: 'Signed in.' })
    expect(view.label).toBe('Held after Disconnect')
    expect(view.tone).toBe('neutral')
    expect(view.detail).toContain('Connect or Sign in now starts it again')
  })

  it('says Dhan refused the token, and what a person can do about it', () => {
    const view = dhanAutoSignInView({ ...base, tokenRefused: true, lastOk: true, lastMessage: 'Signed in.' })
    expect(view.label).toBe('Dhan refused the token')
    expect(view.tone).toBe('neg')
    expect(view.detail).toContain('Press Connect or Sign in now')
  })

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

  it('reads a pause after refused codes as neither stopped nor working, with the next try', () => {
    const view = dhanAutoSignInView({ ...base, codesPausedUntilIst: '08:08', codesToday: 3, lastOk: false, lastMessage: 'Dhan refused the PIN + TOTP sign-in (400: Invalid TOTP).' })
    expect(view.label).toBe('Paused')
    expect(view.tone).toBe('warn')
    expect(view.detail).toContain('Dhan refused 3 TOTP codes today')
    expect(view.detail).toContain('08:08 IST')
    expect(view.detail).toContain('up to 9 a day')
  })

  it('shows a pause after a restart too, when there is no last result to go on', () => {
    expect(dhanAutoSignInView({ ...base, codesPausedUntilIst: '08:08', codesToday: 3 }).label).toBe('Paused')
  })

  it('calls the day stopped once the codes are used up, not paused', () => {
    const view = dhanAutoSignInView({ ...base, stoppedForToday: true, codesToday: 9, lastOk: false, lastMessage: 'Dhan refused the PIN + TOTP sign-in (400: Invalid TOTP).' })
    expect(view.label).toBe('Stopped for today')
  })

  it('says when it runs before it has ever run', () => {
    const view = dhanAutoSignInView(base)
    expect(view.tone).toBe('pos')
    expect(view.detail).toContain('from 08:00 IST')
    expect(view.detail).toContain('after the last token has run out')
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
