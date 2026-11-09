/** One atomic record per login. Token rotation preserves its identity; a new login never does. */
const SESSION_KEY = 'algotrading.session'
const rotationKey = (id: string) => `algotrading.rotation.${id}`
const LEGACY_ACCESS = 'algotrading.accessToken'
const LEGACY_REFRESH = 'algotrading.refreshToken'

interface StoredSession {
  id: string
  accessToken: string | null
  refreshToken: string | null
}

export class SessionChangedError extends Error {
  constructor() {
    super('The signed-in session changed. Please try again in the current session.')
    this.name = 'AbortError'
  }
}

let current = { id: null as string | null, controller: new AbortController() }
const listeners = new Set<() => void>()
let listeningWindow: Window | undefined

// getRandomValues also works on installations reached over a local HTTP/LAN origin.
function newIdentity(): string {
  return Array.from(crypto.getRandomValues(new Uint8Array(32)), (b) => b.toString(16).padStart(2, '0')).join('')
}

function read(): StoredSession | null {
  const raw = localStorage.getItem(SESSION_KEY)
  if (raw) {
    try {
      const value = JSON.parse(raw) as StoredSession
      if (typeof value.id === 'string' &&
        (value.accessToken === null || typeof value.accessToken === 'string') &&
        (value.refreshToken === null || typeof value.refreshToken === 'string')) {
        const rotated = localStorage.getItem(rotationKey(value.id))
        if (rotated) {
          try {
            const pair = JSON.parse(rotated) as StoredSession
            if (typeof pair.accessToken === 'string' && typeof pair.refreshToken === 'string') return { ...value, accessToken: pair.accessToken, refreshToken: pair.refreshToken }
          } catch { /* An unreadable rotation cannot replace the initial pair. */ }
        }
        return value
      }
    } catch { /* Invalid local data is a signed-out session. */ }
    return null
  }
  const accessToken = localStorage.getItem(LEGACY_ACCESS)
  const refreshToken = localStorage.getItem(LEGACY_REFRESH)
  if (!accessToken && !refreshToken) return null
  const migrated = { id: newIdentity(), accessToken, refreshToken }
  localStorage.setItem(SESSION_KEY, JSON.stringify(migrated))
  localStorage.removeItem(LEGACY_ACCESS)
  localStorage.removeItem(LEGACY_REFRESH)
  return migrated
}

function synchronize(): StoredSession | null {
  if (typeof window !== 'undefined' && window.addEventListener && listeningWindow !== window) {
    listeningWindow = window
    window.addEventListener('storage', (event) => {
      // Read the latest record: queued events may describe an already superseded login.
      if (event.key === SESSION_KEY || event.key === null) synchronize()
    })
  }
  const stored = read()
  const id = stored?.id ?? null
  if (id !== current.id) {
    const previous = current
    current = { id, controller: new AbortController() }
    previous.controller.abort(new SessionChangedError())
    for (const listener of listeners) listener()
  }
  return stored
}

export function captureSession() {
  synchronize()
  return current
}
export type SessionScope = ReturnType<typeof captureSession>

export function isCurrentSession(scope: SessionScope): boolean {
  synchronize()
  return scope === current && !scope.controller.signal.aborted
}

export function assertCurrentSession(scope: SessionScope): void {
  if (!isCurrentSession(scope)) throw new SessionChangedError()
}

export function subscribeSessionChanges(listener: () => void): () => void {
  synchronize()
  listeners.add(listener)
  return () => { listeners.delete(listener) }
}

function write(value: StoredSession): void {
  const previousId = read()?.id
  localStorage.setItem(SESSION_KEY, JSON.stringify(value))
  localStorage.removeItem(LEGACY_ACCESS)
  localStorage.removeItem(LEGACY_REFRESH)
  if (previousId) localStorage.removeItem(rotationKey(previousId))
  synchronize()
}

export const tokenStore = {
  get access() { return synchronize()?.accessToken ?? null },
  get refresh() { return synchronize()?.refreshToken ?? null },
  set(accessToken: string, refreshToken: string) {
    write({ id: newIdentity(), accessToken, refreshToken })
  },
  rotate(scope: SessionScope, accessToken: string, refreshToken: string) {
    assertCurrentSession(scope)
    if (!scope.id) throw new SessionChangedError()
    // A rotation never writes the active-login record. Even if another tab
    // logs in between this check and the write, these tokens stay under the
    // old identity and cannot replace the new login's credentials.
    const key = rotationKey(scope.id)
    localStorage.setItem(key, JSON.stringify({ accessToken, refreshToken }))
    if (!isCurrentSession(scope)) {
      localStorage.removeItem(key)
      throw new SessionChangedError()
    }
  },
  clear() {
    write({ id: newIdentity(), accessToken: null, refreshToken: null })
  },
}
