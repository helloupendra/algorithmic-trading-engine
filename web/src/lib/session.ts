/**
 * Signing in, signing out and a session that ends by itself, apart from React
 * so a test can drive them against a real query cache (lib/auth.tsx holds the
 * state they set).
 *
 * The query cache belongs to one session. App.tsx makes one client for the life
 * of the tab, and most query keys are not per user (['positions', …], ['orders'],
 * ['risk', 'exposure']), so until 2 Nov the next user in the same tab was first
 * shown the previous user's rows. When their own refetch answered 403 those rows
 * stayed, under "Refresh failed — showing the last loaded data", which for an admin
 * signing out and a trader signing in meant every account's positions.
 *
 * clear() also cancels what is on its way: an answer that arrives afterwards goes
 * to a query no longer in the cache. Nothing goes on polling for the old session,
 * because everything that polls is a page under RequireAuth (or /ira's own gate)
 * and setUser(null) unmounts it, and that closes the live hub too (useLiveFeed).
 */

import type { QueryClient } from '@tanstack/react-query'
import { authApi, tokenStore, type MeResponse } from './api'
import { assertCurrentSession, captureSession, isCurrentSession, subscribeSessionChanges } from './sessionStore'

export interface SessionActions {
  login: (userNameOrEmail: string, password: string) => Promise<MeResponse>
  logout: () => Promise<void>
  /** The API client found the session over: an unrecoverable 401. */
  expired: () => void
}

export function sessionActions(queryClient: QueryClient, setUser: (user: MeResponse | null) => void): SessionActions {
  return {
    async login(userNameOrEmail, password) {
      const starting = captureSession()
      const result = await authApi.login(userNameOrEmail, password)
      assertCurrentSession(starting)
      tokenStore.set(result.accessToken, result.refreshToken)
      const scope = captureSession()
      queryClient.clear()
      setUser(null)

      // Prefer /me over the login payload: it is the same shape the rest of the
      // app consumes, including capital and status.
      const me = await authApi.me()
      assertCurrentSession(scope)
      // Before the first page of this session can read anything. Nothing worth
      // keeping is in it, whoever this is: no console page is mounted at a sign-in.
      queryClient.clear()
      setUser(me)
      return me
    },

    async logout() {
      queryClient.clear()
      setUser(null)
      await authApi.logout()
    },

    expired() {
      queryClient.clear()
      setUser(null)
    },
  }
}

/** Restores the current login and synchronously discards caches on local or cross-tab changes. */
export function watchSession(
  queryClient: QueryClient,
  setUser: (user: MeResponse | null) => void,
  setLoading: (loading: boolean) => void,
): () => void {
  let disposed = false
  async function restore() {
    const scope = captureSession()
    queryClient.clear()
    setUser(null)
    const signedIn = !!tokenStore.access
    setLoading(signedIn)
    if (!signedIn) return
    try {
      const me = await authApi.me()
      if (!disposed && isCurrentSession(scope)) setUser(me)
    } catch {
      if (!disposed && isCurrentSession(scope)) tokenStore.clear()
    } finally {
      if (!disposed && isCurrentSession(scope)) setLoading(false)
    }
  }
  const unsubscribe = subscribeSessionChanges(() => { void restore() })
  void restore()
  return () => { disposed = true; unsubscribe() }
}
