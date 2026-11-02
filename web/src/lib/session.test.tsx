import { QueryClient, QueryClientProvider, QueryObserver } from '@tanstack/react-query'
import { renderToStaticMarkup } from 'react-dom/server'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { ApiError, tokenStore, type MeResponse } from './api'
import { AuthProvider, useAuth } from './auth'
import authSource from './auth.tsx?raw'
import { sessionActions } from './session'
import { RequireAuth } from '../components/RouteGuards'

const ADMIN_POSITIONS = [{ account: 'every account', symbol: 'NSE:NIFTY25N0425000CE' }]
const POSITIONS_KEY = ['positions', 'open']

const me = (id: number, userName: string) => ({ id, userName }) as MeResponse

/** An API that signs anyone in as the trader, with a real token store over a stubbed localStorage. */
function stubApi(): void {
  const stored = new Map<string, string>()
  vi.stubGlobal('localStorage', {
    getItem: (key: string) => stored.get(key) ?? null,
    setItem: (key: string, value: string) => void stored.set(key, value),
    removeItem: (key: string) => void stored.delete(key),
  })
  const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } })
  vi.stubGlobal('fetch', vi.fn(async (url: string) => {
    if (url.endsWith('/api/UserAuth/login')) return json({ accessToken: 'trader-access', refreshToken: 'trader-refresh' })
    if (url.endsWith('/api/UserAuth/me')) return json(me(7, 'trader'))
    return json({})
  }))
}

describe('a session and the query cache', () => {
  let client: QueryClient
  const setUser = vi.fn()

  beforeEach(() => {
    stubApi()
    setUser.mockClear()
    client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    tokenStore.set('admin-access', 'admin-refresh')
    client.setQueryData(POSITIONS_KEY, ADMIN_POSITIONS)
  })

  afterEach(() => vi.unstubAllGlobals())

  it('a sign-out leaves nothing in the cache', async () => {
    await sessionActions(client, setUser).logout()

    expect(client.getQueryCache().getAll()).toHaveLength(0)
    expect(tokenStore.access).toBeNull()
    expect(setUser).toHaveBeenCalledWith(null)
  })

  it('a session that ended by itself leaves nothing in the cache', () => {
    sessionActions(client, setUser).expired()

    expect(client.getQueryCache().getAll()).toHaveLength(0)
    expect(setUser).toHaveBeenCalledWith(null)
  })

  it('a sign-in as another user starts from an empty cache, before that user is set', async () => {
    let cachedWhenSet: unknown = 'not set yet'
    setUser.mockImplementation(() => {
      cachedWhenSet = client.getQueryData(POSITIONS_KEY)
    })

    const user = await sessionActions(client, setUser).login('trader', 'secret')

    expect(user.userName).toBe('trader')
    expect(cachedWhenSet).toBeUndefined()
    expect(tokenStore.access).toBe('trader-access')
  })

  it("does not show the next user the previous user's rows when their own read is refused", async () => {
    await sessionActions(client, setUser).logout()

    // A module the trader does not hold: 403 is not retried, and a page would keep whatever was cached.
    const observer = new QueryObserver(client, {
      queryKey: POSITIONS_KEY,
      queryFn: async () => {
        throw new ApiError(403, 'You do not have permission to do that.')
      },
    })
    expect(observer.getCurrentResult().data).toBeUndefined()

    const off = observer.subscribe(() => {})
    await vi.waitFor(() => expect(observer.getCurrentResult().status).toBe('error'))
    expect(observer.getCurrentResult().data).toBeUndefined()
    off()
  })

  it('an answer still on its way at the sign-out does not come back into the cache', async () => {
    let answer: (rows: typeof ADMIN_POSITIONS) => void = () => {}
    const observer = new QueryObserver(client, {
      queryKey: ['orders'],
      queryFn: () => new Promise<typeof ADMIN_POSITIONS>((resolve) => { answer = resolve }),
    })
    const off = observer.subscribe(() => {})

    await sessionActions(client, setUser).logout()
    off() // RequireAuth has unmounted the page
    answer(ADMIN_POSITIONS)
    await Promise.resolve()

    expect(client.getQueryData(['orders'])).toBeUndefined()
    expect(client.getQueryCache().getAll()).toHaveLength(0)
  })

})

describe('AuthProvider', () => {
  let client: QueryClient

  /** The provider as App.tsx mounts it; server rendering hands over its functions, its effects do not run. */
  function renderedAuth(): ReturnType<typeof useAuth> {
    let auth: ReturnType<typeof useAuth> | undefined
    function Probe() {
      auth = useAuth()
      return null
    }
    renderToStaticMarkup(
      <QueryClientProvider client={client}>
        <AuthProvider>
          <Probe />
        </AuthProvider>
      </QueryClientProvider>,
    )
    return auth!
  }

  beforeEach(() => {
    stubApi()
    client = new QueryClient()
    tokenStore.set('admin-access', 'admin-refresh')
    client.setQueryData(POSITIONS_KEY, ADMIN_POSITIONS)
  })

  afterEach(() => vi.unstubAllGlobals())

  it('its sign-out empties the cache of the client above it', async () => {
    await renderedAuth().logout()

    expect(client.getQueryData(POSITIONS_KEY)).toBeUndefined()
  })

  it('its sign-in empties it too', async () => {
    await renderedAuth().login('trader', 'secret')

    expect(client.getQueryData(POSITIONS_KEY)).toBeUndefined()
  })

  it("hands the API client's session-over signal to the same action", () => {
    // In an effect, so not run by a server render: this is the one line that wires it.
    expect(authSource).toContain('setSessionExpiredHandler(actions.expired)')
  })
})

describe('RequireAuth', () => {
  it('renders no console page for a signed-out user, which is what stops its polling', () => {
    stubApi()
    const html = renderToStaticMarkup(
      <QueryClientProvider client={new QueryClient()}>
        <MemoryRouter initialEntries={['/desk']}>
          <AuthProvider>
            <Routes>
              <Route element={<RequireAuth />}>
                <Route path="/desk" element={<p>the desk</p>} />
              </Route>
              <Route path="*" element={null} />
            </Routes>
          </AuthProvider>
        </MemoryRouter>
      </QueryClientProvider>,
    )

    expect(html).not.toContain('the desk')
    vi.unstubAllGlobals()
  })
})
