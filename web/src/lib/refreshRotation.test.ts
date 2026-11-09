import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api, ApiError, setSessionExpiredHandler, tokenStore } from './api'
import { captureSession } from './sessionStore'

const json = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status })

describe('refresh rotation across tabs', () => {
  const expired = vi.fn()
  beforeEach(() => {
    const stored = new Map<string, string>()
    vi.stubGlobal('localStorage', {
      getItem: (key: string) => stored.get(key) ?? null,
      setItem: (key: string, value: string) => { stored.set(key, value) },
      removeItem: (key: string) => { stored.delete(key) },
    })
    vi.stubGlobal('navigator', {})
    expired.mockClear()
    setSessionExpiredHandler(expired)
    tokenStore.set('A1', 'R1')
  })
  afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); setSessionExpiredHandler(() => {}) })

  it('two independent clients share one browser lock and send only one refresh', async () => {
    let tail = Promise.resolve()
    const request = vi.fn((_name: string, options: { signal: AbortSignal }, action: () => Promise<unknown>) => {
      const result = tail.then(() => { options.signal.throwIfAborted(); return action() })
      tail = result.then(() => {}, () => {})
      return result
    })
    vi.stubGlobal('navigator', { locks: { request } })
    const refresh = vi.fn(async () => json({ accessToken: 'A2', refreshToken: 'R2' }))
    vi.stubGlobal('fetch', vi.fn(async (path: string, init?: RequestInit) => path.endsWith('/refresh') ? refresh()
      : new Headers(init?.headers).get('Authorization') === 'Bearer A2' ? json('positions') : json({}, 401)))
    // A separate module instance has its own in-flight promise, as another tab does.
    vi.resetModules()
    const secondTab = await import('./api')
    await expect(Promise.all([api.get('/positions'), secondTab.api.get('/positions')])).resolves.toEqual(['positions', 'positions'])
    expect(refresh).toHaveBeenCalledTimes(1)
    expect(request).toHaveBeenCalledTimes(2)
    expect(request.mock.calls[0][0]).toBe(request.mock.calls[1][0])
    expect(tokenStore.refresh).toBe('R2')
  })

  it('a browser without Web Locks uses the winning tabs credentials after a conflict', async () => {
    vi.useFakeTimers()
    const scope = captureSession()
    vi.stubGlobal('fetch', vi.fn(async (path: string, init?: RequestInit) => path.endsWith('/refresh')
      ? json({ code: 'refresh_already_used' }, 409)
      : new Headers(init?.headers).get('Authorization') === 'Bearer A2' ? json('positions') : json({}, 401)))
    const pending = api.get('/positions')
    await vi.advanceTimersByTimeAsync(100)
    tokenStore.rotate(scope, 'A2', 'R2')
    await vi.advanceTimersByTimeAsync(50)
    expect(await pending).toBe('positions')
    expect(expired).not.toHaveBeenCalled()
  })

  it('a conflict without a published winner is retryable and keeps the session', async () => {
    vi.useFakeTimers()
    vi.stubGlobal('fetch', vi.fn(async (path: string) => json({}, path.endsWith('/refresh') ? 409 : 401)))
    const pending = api.get('/positions').catch((e: unknown) => e)
    await vi.advanceTimersByTimeAsync(2100)
    expect(await pending).toMatchObject({ status: 409 })
    expect(tokenStore.refresh).toBe('R1')
    expect(expired).not.toHaveBeenCalled()
  })

  it.each([429, 500, 503])('HTTP %s during refresh does not sign a user out', async (status) => {
    vi.stubGlobal('fetch', vi.fn(async (path: string) => json({}, path.endsWith('/refresh') ? status : 401)))
    const error = await api.get('/positions').catch((e: unknown) => e)
    expect(error).toBeInstanceOf(ApiError)
    expect(error).toMatchObject({ status })
    expect(tokenStore.refresh).toBe('R1')
    expect(expired).not.toHaveBeenCalled()
  })

  it('a network interruption during refresh keeps the session available for retry', async () => {
    vi.stubGlobal('fetch', vi.fn(async (path: string) => {
      if (path.endsWith('/refresh')) throw new TypeError('offline')
      return json({}, 401)
    }))
    await expect(api.get('/positions')).rejects.toMatchObject({ status: 503 })
    expect(tokenStore.refresh).toBe('R1')
    expect(expired).not.toHaveBeenCalled()
  })

  it('a revoked token ends the current local session', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json({ code: 'refresh_reused' }, 401)))
    await expect(api.get('/positions')).rejects.toMatchObject({ status: 401 })
    expect(tokenStore.access).toBeNull()
    expect(expired).toHaveBeenCalledTimes(1)
  })
})
