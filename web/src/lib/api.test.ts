import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { ApiError, api, freshAccessToken, setSessionExpiredHandler, tokenExpiresAtMs, tokenStore } from './api'

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

describe('apiFetch', () => {
  afterEach(() => vi.unstubAllGlobals())

  it("hands a refusal's own message and body to the screen", async () => {
    // POST /api/Backfill/history before the morning's FYERS sign-in. Until 29 Oct it was a 500,
    // "An unexpected error occurred", and the backfill panel had nothing better to show.
    const body = {
      message: "No valid FYERS session: FYERS serves history only after today's sign-in. Sign in on Connectors → FYERS, then try again.",
      provider: 'fyers',
    }
    vi.stubGlobal('localStorage', { getItem: () => null, setItem: () => {}, removeItem: () => {} })
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify(body), { status: 409, headers: { 'Content-Type': 'application/json' } })))

    const error = await api
      .post('/api/Backfill/history', { symbol: 'NSE:NIFTY50-INDEX', resolution: '5', fromDate: '2026-10-01', toDate: '2026-10-28' })
      .catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ApiError)
    expect((error as ApiError).status).toBe(409)
    expect((error as ApiError).message).toBe(body.message)
    expect((error as ApiError).body).toEqual(body)
  })
})

describe('the shared token refresh', () => {
  const REFRESH_URL = '/api/UserAuth/refresh'
  const sessionEnded = vi.fn()
  let calls: string[]
  let answerRefresh: () => Promise<Response>

  const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } })
  const refreshed = (accessToken: string, refreshToken: string) => async () => json({ accessToken, refreshToken })

  /** Anything but the refresh answers 200 for a live token, and 401 for every other. */
  beforeEach(() => {
    calls = []
    answerRefresh = refreshed('A2', 'R2')
    sessionEnded.mockClear()
    setSessionExpiredHandler(sessionEnded)
    const stored = new Map<string, string>()
    vi.stubGlobal('localStorage', {
      getItem: (key: string) => stored.get(key) ?? null,
      setItem: (key: string, value: string) => void stored.set(key, value),
      removeItem: (key: string) => void stored.delete(key),
    })
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      calls.push(url)
      if (url.endsWith(REFRESH_URL)) return answerRefresh()
      const auth = new Headers(init?.headers).get('Authorization')
      return auth === 'Bearer A2' || auth === 'Bearer next-user-access' ? json(['positions']) : new Response('', { status: 401 })
    }))
  })

  afterEach(() => {
    setSessionExpiredHandler(() => {})
    vi.unstubAllGlobals()
  })

  const refreshCalls = () => calls.filter((url) => url.endsWith(REFRESH_URL)).length

  /** Polls that were out when the session ended: each comes back 401 with no refresh token stored. */
  async function pollsReturnAfterTheSessionEnded(): Promise<void> {
    await Promise.all([api.get('/a'), api.get('/b'), api.get('/c')].map((poll) => poll.catch(() => {})))
    expect(tokenStore.refresh).toBeNull()
    expect(refreshCalls()).toBe(0)
    sessionEnded.mockClear()
  }

  it('asks /refresh after a sign-in, even when a 401 once came with no refresh token stored', async () => {
    await pollsReturnAfterTheSessionEnded()

    // Signed in again in the same tab, and an hour later the access token has expired.
    tokenStore.set('A1-expired', 'R1')
    await expect(api.get('/api/Positions/open')).resolves.toEqual(['positions'])

    expect(refreshCalls()).toBe(1)
    expect(tokenStore.access).toBe('A2')
    expect(tokenStore.refresh).toBe('R2')
    expect(sessionEnded).not.toHaveBeenCalled()
  })

  it('gives the live hub a fresh token after that, not the expired one', async () => {
    await pollsReturnAfterTheSessionEnded()

    tokenStore.set(jwt(Date.now() - 1_000), 'R1')

    expect(await freshAccessToken()).toBe('A2')
    expect(refreshCalls()).toBe(1)
  })

  it('still ends a session the refresh cannot save', async () => {
    tokenStore.set('A1-expired', 'R1')
    answerRefresh = async () => new Response('', { status: 401 })

    const error = await api.get('/api/Positions/open').catch((e: unknown) => e)

    expect((error as ApiError).status).toBe(401)
    expect(tokenStore.refresh).toBeNull()
    expect(sessionEnded).toHaveBeenCalledTimes(1)
  })

  it('shares one refresh between requests that hit a 401 together', async () => {
    tokenStore.set('A1-expired', 'R1')

    await Promise.all([api.get('/a'), api.get('/b'), api.get('/c')])

    expect(refreshCalls()).toBe(1)
  })

  describe('with the answer still on its way', () => {
    /** Starts a poll whose 401 asks for a refresh, and leaves that refresh waiting. */
    async function refreshUnderWay(): Promise<{ arrive: () => void; poll: Promise<unknown> }> {
      tokenStore.set('A1-expired', 'R1')
      let arrive = () => {}
      answerRefresh = () => new Promise((resolve) => {
        arrive = () => resolve(json({ accessToken: 'A2', refreshToken: 'R2' }))
      })
      const poll = api.get('/a').catch((e: unknown) => e)
      await vi.waitFor(() => expect(refreshCalls()).toBe(1))
      return { arrive: () => arrive(), poll }
    }

    it('does not sign a user back in who has signed out meanwhile', async () => {
      const { arrive, poll } = await refreshUnderWay()

      tokenStore.clear()
      arrive()
      await poll

      expect(tokenStore.access).toBeNull()
      expect(tokenStore.refresh).toBeNull()
    })

    it("does not put the last user's tokens in the next user's session", async () => {
      const { arrive, poll } = await refreshUnderWay()

      tokenStore.set('next-user-access', 'next-user-refresh')
      arrive()
      await poll

      expect(tokenStore.access).toBe('next-user-access')
      expect(tokenStore.refresh).toBe('next-user-refresh')
    })
  })
})
