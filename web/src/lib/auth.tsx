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
  useSyncExternalStore,
  type ReactNode,
} from 'react'
import {
  setSessionExpiredHandler,
  tokenStore,
  type MeResponse,
} from './api'
import { sessionActions, watchSession } from './session'
import { captureSession, subscribeSessionChanges } from './sessionStore'

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
  const scope = useSyncExternalStore(subscribeSessionChanges, captureSession, captureSession)
  const [held, setHeld] = useState<{ user: MeResponse | null; scope: typeof scope }>({ user: null, scope })
  const setUser = useMemo(() => (user: MeResponse | null) => setHeld({ user, scope: captureSession() }), [])
  const user = held.scope === scope ? held.user : null
  const actions = useMemo(() => sessionActions(queryClient, setUser), [queryClient, setUser])
  // Only a stored token needs verifying; with none there is nothing to probe,
  // so anonymous visitors never render a loading state.
  const [isLoading, setIsLoading] = useState(() => !!tokenStore.access)

  useEffect(() => watchSession(queryClient, setUser, setIsLoading), [queryClient, setUser])

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

  return <AuthContext.Provider key={scope.id ?? 'anonymous'} value={value}>{children}</AuthContext.Provider>
}

export function useAuth(): AuthContextValue {
  const context = useContext(AuthContext)
  if (!context) {
    throw new Error('useAuth must be used inside an <AuthProvider>')
  }
  return context
}
