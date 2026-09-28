import { describe, expect, it, vi } from 'vitest'

import { freshAccessToken, tokenExpiresAtMs } from './api'

const NOW = Date.parse('2026-09-28T09:00:00Z')

/** One JWT segment: base64url, unpadded. */
const segment = (value: unknown) => btoa(JSON.stringify(value)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')

/** A signed-in user's access token as the API issues it, expiring at `expMs`. The signature is not checked here. */
function jwt(expMs: number, extra: Record<string, unknown> = {}): string {
  return `${segment({ alg: 'HS256', typ: 'JWT' })}.${segment({ sub: '4', role: 'Trader', exp: Math.floor(expMs / 1000), ...extra })}.c2lnbmF0dXJl`
}

/** The token store as the hub's token factory sees it: just the stored access token. */
const storeWith = (access: string | null) => ({ access })

describe('tokenExpiresAtMs', () => {
  it("reads a JWT's expiry, base64url and all", () => {
    // A name whose payload has base64url's '-' and '_', which plain base64 decoding refuses.
    const token = jwt(NOW + 3_600_000, { name: '~~~???>>>' })
    expect(token.split('.')[1]).toMatch(/-.*_|_.*-/)
    expect(tokenExpiresAtMs(token)).toBe(NOW + 3_600_000)
  })

  it('is null for anything that is not a readable token', () => {
    expect(tokenExpiresAtMs(null)).toBeNull()
    expect(tokenExpiresAtMs('')).toBeNull()
    expect(tokenExpiresAtMs('opaque-token')).toBeNull()
    expect(tokenExpiresAtMs('a.not-json.c')).toBeNull()
    expect(tokenExpiresAtMs(`a.${segment({ sub: '4' })}.c`)).toBeNull()
  })
})

describe('freshAccessToken', () => {
  it('hands over a token with time left as it is', async () => {
    const token = jwt(NOW + 30 * 60_000)
    const refresh = vi.fn(async () => 'refreshed')
    expect(await freshAccessToken(storeWith(token), refresh, () => NOW)).toBe(token)
    expect(refresh).not.toHaveBeenCalled()
  })

  it('refreshes an expired token before a reconnect sends it, and one about to expire', async () => {
    // The API closed the hub connection at the hour; the stored token is the one it closed it for.
    const refresh = vi.fn(async () => 'refreshed')
    expect(await freshAccessToken(storeWith(jwt(NOW - 1_000)), refresh, () => NOW)).toBe('refreshed')
    expect(await freshAccessToken(storeWith(jwt(NOW + 30_000)), refresh, () => NOW)).toBe('refreshed')
    expect(refresh).toHaveBeenCalledTimes(2)
  })

  it('keeps the old token when the refresh fails, leaving the sign-out to the REST calls', async () => {
    const expired = jwt(NOW - 1_000)
    expect(await freshAccessToken(storeWith(expired), async () => null, () => NOW)).toBe(expired)
  })

  it('asks nothing when signed out, and passes a token it cannot read unchanged', async () => {
    const refresh = vi.fn(async () => 'refreshed')
    expect(await freshAccessToken(storeWith(null), refresh, () => NOW)).toBeNull()
    expect(await freshAccessToken(storeWith('opaque-token'), refresh, () => NOW)).toBe('opaque-token')
    expect(refresh).not.toHaveBeenCalled()
  })
})
