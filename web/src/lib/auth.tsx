/**
 * Session state for the whole app.
 *
 * The user object here decides only what the UI *renders*. Every actual
 * permission check happens on the API against the token's role claim — hiding a
 * nav link is a usability choice, never a security control.
 */

import { useQueryClient } from '@tanstack/react-query'
import {
  createContext,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from 'react'
import {
  authApi,
  setSessionExpiredHandler,
  tokenStore,
  type MeResponse,
} from './api'
import { sessionActions } from './session'

interface AuthContextValue {
  user: MeResponse | null
  /** True until the initial session probe finishes, so guards can wait. */
  isLoading: boolean
  isAuthenticated: boolean
  isAdmin: boolean
  login: (userNameOrEmail: string, password: string) => Promise<MeResponse>
  logout: () => Promise<void>
}

const AuthContext = createContext<AuthContextValue | undefined>(undefined)

export function AuthProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient()
  const [user, setUser] = useState<MeResponse | null>(null)
  const actions = useMemo(() => sessionActions(queryClient, setUser), [queryClient])
  // Only a stored token needs verifying; with none there is nothing to probe,
  // so anonymous visitors never render a loading state.
  const [isLoading, setIsLoading] = useState(() => !!tokenStore.access)

  // Restore the session on first load: a stored token may still be valid, and
  // /me is the authority on the current role (it may have changed server-side).
  useEffect(() => {
    let cancelled = false

    async function restore() {
      if (!tokenStore.access) {
        setIsLoading(false)
        return
      }
      try {
        const me = await authApi.me()
        if (!cancelled) setUser(me)
      } catch {
        tokenStore.clear()
        if (!cancelled) setUser(null)
      } finally {
        if (!cancelled) setIsLoading(false)
      }
    }

    void restore()
    return () => {
      cancelled = true
    }
  }, [])

  // The API client signals an unrecoverable 401; drop the user so guards redirect.
  useEffect(() => {
    setSessionExpiredHandler(actions.expired)
    return () => setSessionExpiredHandler(() => {})
  }, [actions])

  const value = useMemo<AuthContextValue>(
    () => ({
      user,
      isLoading,
      isAuthenticated: user !== null,
      isAdmin: user?.role === 'Admin',
      login: actions.login,
      logout: actions.logout,
    }),
    [user, isLoading, actions],
  )

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

export function useAuth(): AuthContextValue {
  const context = useContext(AuthContext)
  if (!context) {
    throw new Error('useAuth must be used inside an <AuthProvider>')
  }
  return context
}
