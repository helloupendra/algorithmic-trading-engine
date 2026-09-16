import { describe, expect, it } from 'vitest'

import { angelFailureHint, angelReadiness } from './angel'
import type { AngelStatus } from './angel'

const base: AngelStatus = {
  provider: 'angel',
  displayName: 'Angel One',
  rootUrl: 'https://apiconnect.angelone.in',
  clientCode: '',
  staticIp: '',
  configured: false,
  missing: [],
  hasSession: false,
  sessionStartedUtc: null,
  note: '',
}

describe('angelReadiness', () => {
  it('names the missing settings rather than saying "not configured"', () => {
    const r = angelReadiness({ ...base, missing: ['ANGEL_PIN', 'ANGEL_TOTP_SECRET'] })
    expect(r.tone).toBe('neg')
    expect(r.label).toContain('2 value(s) missing')
    expect(r.detail).toContain('ANGEL_PIN, ANGEL_TOTP_SECRET')
  })

  it('separates configured from signed in', () => {
    expect(angelReadiness({ ...base, configured: true }).tone).toBe('warn')
    expect(angelReadiness({ ...base, configured: true, hasSession: true }).tone).toBe('pos')
  })

  it('says nothing definite before the answer arrives', () => {
    expect(angelReadiness(undefined).label).toBe('Checking…')
  })
})

describe('angelFailureHint', () => {
  it('explains the two refusals that have a cause worth naming', () => {
    expect(angelFailureHint('Client is blocked [AB1004] — the app only answers from the static IP …')).toContain(
      'registered with',
    )
    expect(angelFailureHint('Invalid totp [AB1050] — the TOTP secret or this machine’s clock is wrong.')).toContain(
      'ANGEL_TOTP_SECRET',
    )
  })

  it('stays quiet when it has nothing to add', () => {
    expect(angelFailureHint('Something else entirely')).toBeNull()
  })
})
