/**
 * Typed access to AlgoTrading.Api.
 *
 * Every endpoint requires a bearer token, so this module owns the whole token
 * lifecycle: it attaches the access token, and when the API answers 401 it
 * refreshes once and replays the original request. Concurrent 401s share a
 * single refresh so a dashboard polling six endpoints cannot fire six refreshes
 * and invalidate its own rotated token.
 *
 * Token storage: tokens live in localStorage so a page reload keeps you signed
 * in. That trades some XSS exposure for usability — acceptable while the app is
 * first-party and served from its own origin. Moving to httpOnly cookies would
 * need matching cookie auth on the API.
 */

export const API_BASE_URL: string =
  import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:5025'

const ACCESS_TOKEN_KEY = 'algotrading.accessToken'
const REFRESH_TOKEN_KEY = 'algotrading.refreshToken'

export type UserRole = 'Admin' | 'Trader'

export interface AuthUser {
  id: number
  userName: string
  email: string
  role: UserRole
}

export interface AuthResponse {
  accessToken: string
  refreshToken: string
  expiresInSeconds: number
  user: AuthUser
}

export interface MeResponse {
  id: number
  userName: string
  email: string
  role: UserRole
  totalCapital: number
  isActive: boolean
  createdUtc: string
  lastLoginUtc: string | null
}

/** Thrown for any non-2xx response, carrying the status for callers to branch on. */
export class ApiError extends Error {
  // Declared as fields rather than constructor parameter properties: the app's
  // tsconfig enables erasableSyntaxOnly, which disallows the shorthand.
  readonly status: number
  readonly body?: unknown

  constructor(status: number, message: string, body?: unknown) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.body = body
  }

  get isUnauthorized() {
    return this.status === 401
  }

  /** Authenticated but not permitted — the caller lacks the required role. */
  get isForbidden() {
    return this.status === 403
  }
}

export const tokenStore = {
  get access() {
    return localStorage.getItem(ACCESS_TOKEN_KEY)
  },
  get refresh() {
    return localStorage.getItem(REFRESH_TOKEN_KEY)
  },
  set(accessToken: string, refreshToken: string) {
    localStorage.setItem(ACCESS_TOKEN_KEY, accessToken)
    localStorage.setItem(REFRESH_TOKEN_KEY, refreshToken)
  },
  clear() {
    localStorage.removeItem(ACCESS_TOKEN_KEY)
    localStorage.removeItem(REFRESH_TOKEN_KEY)
  },
}

/** Notified when the session ends, so the app can route back to /login. */
type SessionExpiredHandler = () => void
let onSessionExpired: SessionExpiredHandler = () => {}
export function setSessionExpiredHandler(handler: SessionExpiredHandler) {
  onSessionExpired = handler
}

/** In-flight refresh, shared by every request that hits a 401 at once. */
let refreshInFlight: Promise<string | null> | null = null

async function refreshAccessToken(): Promise<string | null> {
  if (refreshInFlight) return refreshInFlight

  refreshInFlight = (async () => {
    const refreshToken = tokenStore.refresh
    if (!refreshToken) return null

    try {
      const response = await fetch(`${API_BASE_URL}/api/UserAuth/refresh`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ refreshToken }),
      })

      if (!response.ok) return null

      const data = (await response.json()) as AuthResponse
      tokenStore.set(data.accessToken, data.refreshToken)
      return data.accessToken
    } catch {
      return null
    } finally {
      // Cleared in a microtask so callers awaiting this promise still see it.
      queueMicrotask(() => {
        refreshInFlight = null
      })
    }
  })()

  return refreshInFlight
}

interface RequestOptions extends Omit<RequestInit, 'body'> {
  body?: unknown
  /** Skip auth entirely — used by login, which has no token yet. */
  anonymous?: boolean
}

async function parseBody(response: Response): Promise<unknown> {
  const text = await response.text()
  if (!text) return null
  try {
    return JSON.parse(text)
  } catch {
    return text
  }
}

function errorMessage(status: number, body: unknown): string {
  if (typeof body === 'string' && body.trim()) return body
  if (body && typeof body === 'object') {
    const record = body as Record<string, unknown>
    for (const key of ['message', 'title', 'detail', 'error']) {
      if (typeof record[key] === 'string') return record[key] as string
    }
  }
  if (status === 401) return 'Your session has expired. Please sign in again.'
  if (status === 403) return 'You do not have permission to do that.'
  return `Request failed with status ${status}`
}

export async function apiFetch<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const { body, anonymous, headers, ...rest } = options

  const send = async (token: string | null): Promise<Response> => {
    const finalHeaders = new Headers(headers)
    if (body !== undefined) finalHeaders.set('Content-Type', 'application/json')
    if (token) finalHeaders.set('Authorization', `Bearer ${token}`)

    return fetch(`${API_BASE_URL}${path}`, {
      ...rest,
      headers: finalHeaders,
      body: body === undefined ? undefined : JSON.stringify(body),
    })
  }

  let response = await send(anonymous ? null : tokenStore.access)

  if (response.status === 401 && !anonymous) {
    const fresh = await refreshAccessToken()
    if (fresh) {
      response = await send(fresh)
    }
    // Still unauthorized after a refresh: the session is genuinely over.
    if (response.status === 401) {
      tokenStore.clear()
      onSessionExpired()
      throw new ApiError(401, errorMessage(401, null))
    }
  }

  const parsed = await parseBody(response)

  if (!response.ok) {
    throw new ApiError(response.status, errorMessage(response.status, parsed), parsed)
  }

  return parsed as T
}

export const api = {
  get: <T,>(path: string) => apiFetch<T>(path),
  post: <T,>(path: string, body?: unknown) => apiFetch<T>(path, { method: 'POST', body }),
  put: <T,>(path: string, body?: unknown) => apiFetch<T>(path, { method: 'PUT', body }),
  delete: <T,>(path: string) => apiFetch<T>(path, { method: 'DELETE' }),
}

export const authApi = {
  login: (userNameOrEmail: string, password: string) =>
    apiFetch<AuthResponse>('/api/UserAuth/login', {
      method: 'POST',
      body: { userNameOrEmail, password },
      anonymous: true,
    }),

  me: () => apiFetch<MeResponse>('/api/UserAuth/me'),

  logout: async () => {
    const refreshToken = tokenStore.refresh
    if (refreshToken) {
      // Best effort: a failure here must not block signing out locally.
      try {
        await apiFetch('/api/UserAuth/logout', { method: 'POST', body: { refreshToken } })
      } catch {
        /* ignore */
      }
    }
    tokenStore.clear()
  },
}
