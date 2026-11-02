/**
 * Polling across a sign-out, in a file of its own: TanStack Query starts no
 * refetchInterval when `window` is undefined, which it reads once as it loads, so
 * a window has to exist before the import. session.test.tsx has the rest.
 */

import { QueryClient, QueryObserver } from '@tanstack/react-query'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { sessionActions } from './session'

vi.hoisted(() => {
  ;(globalThis as { window?: unknown }).window = globalThis
})

describe('a page polling at the sign-out', () => {
  afterEach(() => {
    vi.useRealTimers()
    vi.unstubAllGlobals()
  })

  it('asks nothing more once RequireAuth has unmounted it, and leaves nothing behind', async () => {
    const stored = new Map<string, string>()
    vi.stubGlobal('localStorage', {
      getItem: (key: string) => stored.get(key) ?? null,
      setItem: (key: string, value: string) => void stored.set(key, value),
      removeItem: (key: string) => void stored.delete(key),
    })
    vi.useFakeTimers()
    const client = new QueryClient()
    const poll = vi.fn(async () => ['every account'])
    const observer = new QueryObserver(client, { queryKey: ['risk', 'exposure'], queryFn: poll, refetchInterval: 1_000 })
    const unmount = observer.subscribe(() => {})
    await vi.advanceTimersByTimeAsync(2_500)
    expect(poll.mock.calls.length).toBeGreaterThan(1) // it really is polling

    // Signed out with no token stored, so there is no call to /logout to stub.
    await sessionActions(client, () => {}).logout()
    unmount()
    const asked = poll.mock.calls.length
    await vi.advanceTimersByTimeAsync(60_000)

    expect(poll).toHaveBeenCalledTimes(asked)
    expect(client.getQueryCache().getAll()).toHaveLength(0)
  })
})
