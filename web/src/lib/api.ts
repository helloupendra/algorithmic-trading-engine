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

/**
 * Where the API lives, from the point of view of whoever loaded this bundle.
 *
 * Empty in a production build, which makes every call relative - and relative is
 * the only answer that is right for a build the API itself serves out of
 * wwwroot, whether that is reached on localhost, a LAN address or a Cloudflare
 * tunnel. It used to fall back to the absolute localhost URL in every build, so
 * a phone opening the tunnel asked ITSELF for the API and the browser refused
 * the loopback request outright:
 *
 *   POST http://localhost:5025/api/UserAuth/login
 *   blocked by CORS: Permission was denied for this request to access the
 *   `loopback` address space
 *
 * The absolute default is kept for `vite dev`, where the console runs on :5173
 * and the API really is somewhere else. An explicit VITE_API_BASE_URL still
 * wins over both.
 *
 * Note for anyone tempted to set that variable to "" in a build script: on
 * PowerShell `$env:X = ''` DELETES the variable rather than emptying it, so the
 * fallback silently applied and shipped a broken bundle. Hence a default that
 * needs no variable at all.
 */
export const API_BASE_URL: string =
  import.meta.env.VITE_API_BASE_URL ?? (import.meta.env.DEV ? 'http://localhost:5025' : '')

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
  /**
   * Module keys a trader holds. Not sent by /me yet: until it is, the console
   * cannot tell a missing grant from an unknown one and keeps grant-gated tabs
   * visible (lib/modules.ts, accessFor). Empty for admins, who hold them all.
   */
  moduleGrants?: string[]
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

/** When a JWT's `exp` says it stops working, in ms since the epoch; null when it cannot be read. */
export function tokenExpiresAtMs(token: string | null | undefined): number | null {
  const payload = token?.split('.')[1]
  if (!payload) return null
  try {
    const base64 = payload.replace(/-/g, '+').replace(/_/g, '/')
    const json = atob(base64.padEnd(Math.ceil(base64.length / 4) * 4, '='))
    const exp = (JSON.parse(json) as { exp?: unknown } | null)?.exp
    return typeof exp === 'number' && Number.isFinite(exp) ? exp * 1000 : null
  } catch {
    return null
  }
}

/** How long before its expiry a token is refreshed: a connect takes a moment, and clocks drift. */
const TOKEN_REFRESH_SKEW_MS = 60_000

/**
 * The access token, refreshed first when it has expired or expires within a
 * minute. For a caller that cannot replay a request on a 401 the way apiFetch
 * does: the live hub's websocket reads a token on every (re)connect, and the
 * API closes a hub connection when the token it opened with expires (about an
 * hour), so the reconnect sent that expired token and was refused until some
 * REST call happened to refresh it.
 *
 * A token whose expiry cannot be read is returned as it is, and so is the old
 * one when the refresh fails: whether the session is over is for apiFetch's
 * 401 handling to decide, not for a socket that cannot tell a refused
 * refresh from a network blip.
 */
export async function freshAccessToken(
  store: { readonly access: string | null } = tokenStore,
  refresh: () => Promise<string | null> = refreshAccessToken,
  now: () => number = Date.now,
): Promise<string | null> {
  const token = store.access
  if (!token) return null
  const expiresAt = tokenExpiresAtMs(token)
  if (expiresAt == null || expiresAt - TOKEN_REFRESH_SKEW_MS > now()) return token
  return (await refresh()) ?? token
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
      // Never let the browser answer an API call from its heuristic cache: an
      // older API build once served the SPA's index.html (with Last-Modified)
      // for an unknown /api route, and Chrome kept replaying that cached HTML
      // for the same URL long after the API was upgraded.
      cache: 'no-store',
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

  // The API serves the SPA from wwwroot with an index.html fallback, so a
  // route the running API build does not know answers 200 + HTML instead of
  // 404. Surface that as an error rather than handing a page a string where it
  // expects JSON (which crashes on the first `.filter`/`.length`).
  if (isHtmlDocument(parsed)) {
    throw new ApiError(
      404,
      'This endpoint is not available on the running API build. Restart the API to pick up the latest code.',
      parsed,
    )
  }

  return parsed as T
}

/**
 * A request whose body the caller reads itself: a stream of server-sent
 * events (the AI assistant's answer), which apiFetch cannot hand over because
 * it reads the whole body before returning. The same session rules apply:
 * the token is refreshed first when it has expired or is about to
 * (freshAccessToken), a 401 refreshes once and replays, and a second 401 ends
 * the session. Every other status is returned for the caller to read, so the
 * body must be one that can be sent twice (a string, not a stream).
 */
export async function fetchWithSession(path: string, init: RequestInit = {}): Promise<Response> {
  const send = (token: string | null) => {
    const headers = new Headers(init.headers)
    if (token) headers.set('Authorization', `Bearer ${token}`)
    return fetch(`${API_BASE_URL}${path}`, { ...init, headers, cache: 'no-store' })
  }

  let response = await send(await freshAccessToken())
  if (response.status === 401) {
    const fresh = await refreshAccessToken()
    if (fresh) response = await send(fresh)
    if (response.status === 401) {
      tokenStore.clear()
      onSessionExpired()
      throw new ApiError(401, errorMessage(401, null))
    }
  }
  return response
}

function isHtmlDocument(value: unknown): boolean {
  if (typeof value !== 'string') return false
  const head = value.trimStart().slice(0, 15).toLowerCase()
  return head.startsWith('<!doctype') || head.startsWith('<html')
}

export const api = {
  get: <T,>(path: string) => apiFetch<T>(path),
  post: <T,>(path: string, body?: unknown) => apiFetch<T>(path, { method: 'POST', body }),
  put: <T,>(path: string, body?: unknown) => apiFetch<T>(path, { method: 'PUT', body }),
  patch: <T,>(path: string, body?: unknown) => apiFetch<T>(path, { method: 'PATCH', body }),
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
