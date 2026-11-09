import { QueryClient } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api, authApi, fetchWithSession, freshAccessToken, setSessionExpiredHandler, tokenStore } from './api'
import { captureSession, SessionChangedError } from './sessionStore'
import { sessionActions, watchSession } from './session'

const json = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status })
function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((r) => { resolve = r })
  return { promise, resolve }
}

describe('requests belong to the login that started them', () => {
  let stored: Map<string, string>
  let browser: EventTarget
  const expired = vi.fn()
  beforeEach(() => {
    stored = new Map()
    browser = new EventTarget()
    vi.stubGlobal('window', browser)
    vi.stubGlobal('localStorage', {
      getItem: (key: string) => stored.get(key) ?? null,
      setItem: (key: string, value: string) => { stored.set(key, value) },
      removeItem: (key: string) => { stored.delete(key) },
    })
    expired.mockClear()
    setSessionExpiredHandler(expired)
    tokenStore.set('A', 'RA')
  })
  afterEach(() => { setSessionExpiredHandler(() => {}); vi.unstubAllGlobals() })

  function otherTab(accessToken: string | null, refreshToken: string | null) {
    stored.set('algotrading.session', JSON.stringify({ id: crypto.randomUUID(), accessToken, refreshToken }))
    browser.dispatchEvent(Object.assign(new Event('storage'), { key: 'algotrading.session' }))
  }

  it.each(['GET', 'POST'])('never retries an old %s with the next account token', async (method) => {
    const refresh = deferred<Response>()
    const sends: { path: string; token: string | null; signal?: AbortSignal | null }[] = []
    vi.stubGlobal('fetch', vi.fn((path: string, init?: RequestInit) => {
      sends.push({ path, token: new Headers(init?.headers).get('Authorization'), signal: init?.signal })
      return path.endsWith('/refresh') ? refresh.promise : Promise.resolve(json({}, 401))
    }))
    const pending = (method === 'POST' ? api.post('/orders', { qty: 1 }) : api.get('/orders')).catch((e: unknown) => e)
    await vi.waitFor(() => expect(sends).toHaveLength(2))
    otherTab('B', 'RB')
    expect(sends[0].signal?.aborted).toBe(true)
    expect(sends[1].signal?.aborted).toBe(true)
    refresh.resolve(json({ accessToken: 'A2', refreshToken: 'RA2' }))
    expect(await pending).toBeInstanceOf(SessionChangedError)
    expect(sends.filter((s) => s.path.endsWith('/orders'))).toHaveLength(1)
    expect(tokenStore.access).toBe('B')
    expect(expired).not.toHaveBeenCalled()
  })

  it('does not start refresh when the original 401 arrives after switching accounts', async () => {
    const answer = deferred<Response>()
    const fetch = vi.fn(() => answer.promise)
    vi.stubGlobal('fetch', fetch)
    const pending = api.get('/positions').catch((e: unknown) => e)
    tokenStore.set('B', 'RB')
    answer.resolve(json({}, 401))
    expect(await pending).toBeInstanceOf(SessionChangedError)
    expect(fetch).toHaveBeenCalledTimes(1)
    expect(tokenStore.access).toBe('B')
  })

  it('rejects a successful old response even if its body finishes after the new login', async () => {
    const body = deferred<string>()
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, status: 200, text: () => body.promise })))
    const pending = api.get('/positions').catch((e: unknown) => e)
    await Promise.resolve()
    tokenStore.set('B', 'RB')
    body.resolve('["private A positions"]')
    expect(await pending).toBeInstanceOf(SessionChangedError)
  })

  it('an old failed refresh cannot clear the new login or block its refresh', async () => {
    const old = deferred<Response>()
    vi.stubGlobal('fetch', vi.fn(async (path: string, init?: RequestInit) => {
      if (path.endsWith('/refresh')) return JSON.parse(String(init?.body)).refreshToken === 'RA'
        ? old.promise : json({ accessToken: 'B2', refreshToken: 'RB2' })
      return new Headers(init?.headers).get('Authorization') === 'Bearer B2' ? json('B data') : json({}, 401)
    }))
    const first = api.get('/a').catch((e: unknown) => e)
    await vi.waitFor(() => expect(fetch).toHaveBeenCalledTimes(2))
    tokenStore.set('B', 'RB')
    expect(await api.get('/b')).toBe('B data')
    old.resolve(json({}, 401))
    expect(await first).toBeInstanceOf(SessionChangedError)
    expect(tokenStore.access).toBe('B2')
    expect(expired).not.toHaveBeenCalled()
  })

  it('accepts rotation by another tab within the same login without restoring older tokens', async () => {
    const refresh = deferred<Response>()
    vi.stubGlobal('fetch', vi.fn(async (path: string, init?: RequestInit) => path.endsWith('/refresh') ? refresh.promise
      : new Headers(init?.headers).get('Authorization') === 'Bearer A3' ? json('same login') : json({}, 401)))
    const scope = captureSession()
    const pending = api.get('/orders')
    await vi.waitFor(() => expect(fetch).toHaveBeenCalledTimes(2))
    const record = JSON.parse(stored.get('algotrading.session')!)
    stored.set('algotrading.session', JSON.stringify({ ...record, accessToken: 'A3', refreshToken: 'RA3' }))
    browser.dispatchEvent(Object.assign(new Event('storage'), { key: 'algotrading.session' }))
    refresh.resolve(json({ accessToken: 'A2', refreshToken: 'RA2' }))
    expect(await pending).toBe('same login')
    expect(captureSession()).toBe(scope)
    expect(tokenStore.refresh).toBe('RA3')
  })

  it('does not revive a logged-out session, even after logging back in as the same account', async () => {
    const answer = deferred<Response>()
    vi.stubGlobal('fetch', vi.fn(() => answer.promise))
    const pending = api.post('/orders', {}).catch((e: unknown) => e)
    tokenStore.clear()
    tokenStore.set('A', 'RA')
    answer.resolve(json({}))
    expect(await pending).toBeInstanceOf(SessionChangedError)
  })

  it('guards streaming requests while their proactive refresh is pending', async () => {
    const refresh = deferred<Response>()
    const jwt = `e30.${btoa(JSON.stringify({ exp: 1 }))}.sig`
    tokenStore.set(jwt, 'RA')
    vi.stubGlobal('fetch', vi.fn(() => refresh.promise))
    const pending = fetchWithSession('/chat', { method: 'POST', body: 'hello' }).catch((e: unknown) => e)
    await vi.waitFor(() => expect(fetch).toHaveBeenCalledTimes(1))
    tokenStore.set('B', 'RB')
    refresh.resolve(json({ accessToken: 'A2', refreshToken: 'RA2' }))
    expect(await pending).toBeInstanceOf(SessionChangedError)
    expect(fetch).toHaveBeenCalledTimes(1)
  })

  it('cancels an active response stream on a session change', async () => {
    let signal: AbortSignal | null | undefined
    vi.stubGlobal('fetch', vi.fn(async (_: string, init?: RequestInit) => { signal = init?.signal; return json({}) }))
    await fetchWithSession('/chat')
    otherTab(null, null)
    expect(signal?.aborted).toBe(true)
  })

  it('never returns a new login token to the old live connection', async () => {
    const answer = deferred<Response>()
    tokenStore.set(`e30.${btoa(JSON.stringify({ exp: 1 }))}.sig`, 'RA')
    vi.stubGlobal('fetch', vi.fn(() => answer.promise))
    const pending = freshAccessToken().catch((e: unknown) => e)
    tokenStore.set('B', 'RB')
    answer.resolve(json({}, 401))
    expect(await pending).toBeInstanceOf(SessionChangedError)
  })

  it('clears local state immediately and never clears a later login when logout finishes', async () => {
    const answer = deferred<Response>()
    vi.stubGlobal('fetch', vi.fn(() => answer.promise))
    const client = new QueryClient()
    client.setQueryData(['positions'], ['A'])
    const user = vi.fn()
    const pending = sessionActions(client, user).logout()
    expect(tokenStore.access).toBeNull()
    expect(client.getQueryData(['positions'])).toBeUndefined()
    expect(user).toHaveBeenCalledWith(null)
    tokenStore.set('B', 'RB')
    answer.resolve(json({}, 401))
    await pending
    expect(tokenStore.access).toBe('B')
    expect(fetch).toHaveBeenCalledTimes(1)
  })

  it('clears caches and restores the current account on cross-tab login/logout, ignoring the old /me failure', async () => {
    const oldMe = deferred<Response>()
    vi.stubGlobal('fetch', vi.fn(async (_: string, init?: RequestInit) =>
      new Headers(init?.headers).get('Authorization') === 'Bearer A' ? oldMe.promise : json({ id: 2, userName: 'B' })))
    const client = new QueryClient()
    const user = vi.fn()
    const loading = vi.fn()
    const off = watchSession(client, user, loading)
    client.setQueryData(['positions'], ['A'])
    otherTab('B', 'RB')
    expect(client.getQueryData(['positions'])).toBeUndefined()
    await vi.waitFor(() => expect(user).toHaveBeenLastCalledWith({ id: 2, userName: 'B' }))
    oldMe.resolve(json({}, 401))
    await Promise.resolve()
    expect(tokenStore.access).toBe('B')
    client.setQueryData(['positions'], ['B'])
    otherTab(null, null)
    expect(client.getQueryData(['positions'])).toBeUndefined()
    expect(user).toHaveBeenLastCalledWith(null)
    expect(loading).toHaveBeenLastCalledWith(false)
    off()
  })

  it('invalidates an in-flight login when another login finishes first', async () => {
    const answer = deferred<Response>()
    vi.stubGlobal('fetch', vi.fn(() => answer.promise))
    const pending = authApi.login('A', 'test-password').catch((e: unknown) => e)
    otherTab('B', 'RB')
    answer.resolve(json({ accessToken: 'A', refreshToken: 'RA' }))
    expect(await pending).toBeInstanceOf(SessionChangedError)
  })

  it('migrates old token pairs once into a stable atomic session record', () => {
    stored.clear()
    stored.set('algotrading.accessToken', 'legacy-A')
    stored.set('algotrading.refreshToken', 'legacy-R')
    expect(tokenStore.access).toBe('legacy-A')
    const scope = captureSession()
    expect(tokenStore.refresh).toBe('legacy-R')
    expect(captureSession()).toBe(scope)
    expect(stored.has('algotrading.accessToken')).toBe(false)
  })

  it('cannot overwrite a login that another tab writes between the rotation check and storage write', () => {
    const scope = captureSession()
    const originalSet = localStorage.setItem
    vi.spyOn(localStorage, 'setItem').mockImplementation((key, value) => {
      if (key.startsWith('algotrading.rotation.')) {
        // Simulate the other process changing shared storage at the exact write boundary,
        // before its storage event can reach this tab.
        stored.set('algotrading.session', JSON.stringify({ id: 'new-login', accessToken: 'B', refreshToken: 'RB' }))
      }
      originalSet(key, value)
    })
    expect(() => tokenStore.rotate(scope, 'A2', 'RA2')).toThrow(SessionChangedError)
    expect(tokenStore.access).toBe('B')
    expect(tokenStore.refresh).toBe('RB')
    expect(stored.has(`algotrading.rotation.${scope.id}`)).toBe(false)
    vi.restoreAllMocks()
  })
})
