import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import {
  LiveFeed,
  allEventedKeys,
  deskEventKeys,
  hubUrl,
  invalidationBatcher,
  livePoll,
  normalizeTick,
  retryDelay,
} from './live'
import type { HubLike, LiveDeskEvent } from './live'

type Handler = (...args: never[]) => void

/**
 * A hub that records what it was asked and lets a test drop the connection,
 * bring it back, and push ticks and events, the way SignalR's HubConnection
 * would. `answer` decides what an invoke resolves to (or throws).
 */
class FakeHub implements HubLike {
  calls: Array<{ method: string; args: unknown[] }> = []
  answer: (method: string, args: unknown[]) => unknown = (method, args) =>
    method === 'SubscribeAll' ? true : Array.isArray(args[0]) ? args[0].length : true
  startWith: () => Promise<void> = () => Promise.resolve()
  stopped = false
  private readonly handlers = new Map<string, Handler>()
  private reconnecting: (error?: Error) => void = () => {}
  private reconnected: (id?: string) => void = () => {}
  private closed: (error?: Error) => void = () => {}

  start() {
    return this.startWith()
  }
  stop() {
    this.stopped = true
    return Promise.resolve()
  }
  invoke<T>(method: string, ...args: unknown[]): Promise<T> {
    this.calls.push({ method, args })
    try {
      return Promise.resolve(this.answer(method, args) as T)
    } catch (err) {
      return Promise.reject(err)
    }
  }
  on(method: string, handler: Handler) {
    this.handlers.set(method, handler)
  }
  onreconnecting(callback: (error?: Error) => void) {
    this.reconnecting = callback
  }
  onreconnected(callback: (id?: string) => void) {
    this.reconnected = callback
  }
  onclose(callback: (error?: Error) => void) {
    this.closed = callback
  }

  push(ticks: unknown[]) {
    ;(this.handlers.get('ReceiveTicks') as (ticks: unknown) => void)(ticks)
  }
  event(event: unknown) {
    ;(this.handlers.get('DeskEvent') as (event: unknown) => void)(event)
  }
  drop() {
    this.reconnecting()
  }
  restore() {
    this.reconnected('new-id')
  }
  close() {
    this.closed(new Error('gone'))
  }

  /** The symbol lists sent with one method, in order. */
  sent(method: string): string[][] {
    return this.calls.filter((c) => c.method === method).map((c) => [...(c.args[0] as string[])].sort())
  }
  count(method: string): number {
    return this.calls.filter((c) => c.method === method).length
  }
}

function feedWith(options: { cap?: number; hubs?: FakeHub[] } = {}) {
  const hubs = options.hubs ?? [new FakeHub()]
  const made: FakeHub[] = []
  const warn = vi.fn()
  let clock = 1_000_000
  const feed = new LiveFeed({
    connect: () => {
      const hub = hubs[made.length] ?? new FakeHub()
      made.push(hub)
      return hub
    },
    cap: options.cap,
    warn,
    now: () => clock,
  })
  return { feed, made, warn, tickClock: (ms: number) => (clock += ms) }
}

/** Past the batching window, with every promise it set off settled. */
const settle = () => vi.advanceTimersByTimeAsync(150)

beforeEach(() => {
  vi.useFakeTimers()
})

afterEach(() => {
  vi.useRealTimers()
})

describe('LiveFeed subscriptions', () => {
  it('asks the hub once for a symbol two panels show, and gives it back only when the last one leaves', async () => {
    const { feed, made } = feedWith()
    feed.start()
    await settle()
    const hub = made[0]

    const deskNifty = feed.acquire(['NSE:NIFTY50-INDEX'])
    const chartNifty = feed.acquire(['NSE:NIFTY50-INDEX'])
    await settle()
    expect(hub.sent('Subscribe')).toEqual([['NSE:NIFTY50-INDEX']])

    deskNifty()
    await settle()
    expect(hub.count('Unsubscribe')).toBe(0)
    expect(feed.subscribed.has('NSE:NIFTY50-INDEX')).toBe(true)

    chartNifty()
    chartNifty() // a second release of the same hold changes nothing
    await settle()
    expect(hub.sent('Unsubscribe')).toEqual([['NSE:NIFTY50-INDEX']])
    expect(feed.subscribed.size).toBe(0)
  })

  it('turns a route change into one call each way, and never drops what both pages show', async () => {
    const { feed, made } = feedWith()
    feed.start()
    const leaving = feed.acquire(['A', 'SHARED'])
    await settle()
    const hub = made[0]
    expect(hub.sent('Subscribe')).toEqual([['A', 'SHARED']])

    // The next page mounts in the same moment the last one unmounts.
    leaving()
    feed.acquire(['SHARED', 'B'])
    await settle()
    expect(hub.sent('Unsubscribe')).toEqual([['A']])
    expect(hub.sent('Subscribe')).toEqual([['A', 'SHARED'], ['B']])
  })

  it('asks for nothing before the socket is up, then for everything held', async () => {
    const hub = new FakeHub()
    let open!: () => void
    hub.startWith = () => new Promise<void>((resolve) => (open = resolve))
    const { feed } = feedWith({ hubs: [hub] })
    feed.start()
    feed.acquire(['X'])
    await settle()
    expect(feed.connection).toBe('reconnecting')
    expect(hub.count('Subscribe')).toBe(0)

    open()
    await settle()
    expect(feed.connection).toBe('connected')
    expect(hub.sent('Subscribe')).toEqual([['X']])
  })

  it('asks again for every symbol and the everything-feed after a reconnect, and re-reads the evented queries', async () => {
    const { feed, made } = feedWith()
    const reconnects = vi.fn()
    feed.onReconnected(reconnects)
    feed.start()
    feed.acquire(['X', 'Y'])
    feed.holdAll()
    await settle()
    const hub = made[0]
    expect(hub.sent('Subscribe')).toEqual([['X', 'Y']])
    expect(hub.count('SubscribeAll')).toBe(1)
    expect(feed.all).toBe('on')
    expect(reconnects).not.toHaveBeenCalled()

    hub.drop()
    expect(feed.connection).toBe('reconnecting')
    expect(feed.all).toBe('waiting')

    hub.restore()
    await settle()
    expect(feed.connection).toBe('connected')
    // A new server connection holds none of the old subscriptions.
    expect(hub.sent('Subscribe')).toEqual([['X', 'Y'], ['X', 'Y']])
    expect(hub.count('SubscribeAll')).toBe(2)
    expect(feed.all).toBe('on')
    expect(reconnects).toHaveBeenCalledTimes(1)
  })

  it('opens a new connection after a close, and never stops trying while wanted', async () => {
    const failing = new FakeHub()
    failing.startWith = () => Promise.reject(new Error('401'))
    const { feed, made } = feedWith({ hubs: [new FakeHub(), failing, failing, new FakeHub()] })
    feed.start()
    feed.acquire(['X'])
    await settle()
    expect(feed.connection).toBe('connected')

    made[0].close()
    expect(feed.connection).toBe('reconnecting')
    // The first retry is at once; the failures back off 2 s, then 5 s.
    await vi.advanceTimersByTimeAsync(10)
    await vi.advanceTimersByTimeAsync(2_000)
    await vi.advanceTimersByTimeAsync(5_000)
    expect(made).toHaveLength(4)
    await settle()
    expect(feed.connection).toBe('connected')
    expect(made[3].sent('Subscribe')).toEqual([['X']])
  })

  it('keeps the most recently asked-for symbols at the cap, and says so once', async () => {
    const { feed, made, warn } = feedWith({ cap: 3 })
    feed.start()
    for (const s of ['a', 'b', 'c', 'd', 'e']) feed.acquire([s])
    await settle()
    expect(made[0].sent('Subscribe')).toEqual([['c', 'd', 'e']])
    expect(warn).toHaveBeenCalledTimes(1)

    // Asking for "a" again makes it the newest; "c" is the oldest and gives way.
    feed.acquire(['a'])
    feed.acquire(['f'])
    await settle()
    expect([...feed.subscribed].sort()).toEqual(['a', 'e', 'f'])
    expect(warn).toHaveBeenCalledTimes(1)
  })

  it('takes back what the hub refused and leaves those symbols to the polls', async () => {
    const { feed, made, warn } = feedWith()
    feed.start()
    await settle()
    made[0].answer = (method) => {
      if (method === 'Subscribe') throw new Error('Over the 400-symbol limit')
      return true
    }
    feed.acquire(['X'])
    await settle()
    expect(feed.subscribed.has('X')).toBe(false)
    expect(warn).toHaveBeenCalledWith(expect.stringContaining('refused 1 symbol'))
  })

  it('reports a refused everything-feed so the page can poll, and forgets it when released', async () => {
    const { feed, made } = feedWith()
    feed.start()
    await settle()
    made[0].answer = (method) => (method === 'SubscribeAll' ? false : 1)
    const release = feed.holdAll()
    expect(feed.all).toBe('waiting')
    await settle()
    expect(feed.all).toBe('refused')
    release()
    await settle()
    expect(feed.all).toBe('off')
    expect(made[0].count('UnsubscribeAll')).toBe(0)
  })

  it('tells the hub when the last holder of the everything-feed leaves', async () => {
    const { feed, made } = feedWith()
    feed.start()
    const one = feed.holdAll()
    const two = feed.holdAll()
    await settle()
    one()
    await settle()
    expect(made[0].count('UnsubscribeAll')).toBe(0)
    two()
    await settle()
    expect(made[0].count('UnsubscribeAll')).toBe(1)
    expect(feed.all).toBe('off')
  })
})

describe('LiveFeed pushes', () => {
  it('keeps the newest price of each symbol asked for, merged, and wakes only its listeners, once a push', async () => {
    const { feed, made, tickClock } = feedWith()
    feed.start()
    feed.acquire(['A'])
    await settle()
    const onA = vi.fn()
    const onB = vi.fn()
    feed.listen(['A'], onA)
    feed.listen(['B'], onB)

    made[0].push([
      { symbol: 'A', lastTradedPrice: 100, bidPrice: 99.5, askPrice: 100.5 },
      { symbol: 'A', lastTradedPrice: 101 },
      { symbol: 'B', lastTradedPrice: 5 },
      { nonsense: true },
    ])
    expect(onA).toHaveBeenCalledTimes(1)
    expect(onB).not.toHaveBeenCalled()
    // B was never asked for, so it is not kept (the everything-feed's other symbols only reach the quotes cache).
    expect(feed.tick('B')).toBeUndefined()
    expect(feed.tick('A')).toMatchObject({ lastTradedPrice: 101, bidPrice: 99.5, askPrice: 100.5, receivedAtMs: 1_000_000 })

    tickClock(250)
    const version = feed.versionOf('A')
    made[0].push([{ symbol: 'A', lastTradedPrice: 102, bidPrice: null }])
    expect(feed.versionOf('A')).toBeGreaterThan(version)
    expect(feed.tick('A')).toMatchObject({ lastTradedPrice: 102, bidPrice: 99.5, receivedAtMs: 1_000_250 })
  })

  it('hands every push to the tick listeners, the symbols nobody asked for included', async () => {
    const { feed, made } = feedWith()
    const heard = vi.fn()
    feed.onTicks(heard)
    feed.start()
    await settle()
    made[0].push([{ symbol: 'Z', lastTradedPrice: 7 }])
    expect(heard).toHaveBeenCalledWith([expect.objectContaining({ symbol: 'Z', lastTradedPrice: 7 })])
  })

  it('forgets a symbol it no longer receives, and everything on stop', async () => {
    const { feed, made } = feedWith()
    feed.start()
    const release = feed.acquire(['A'])
    const keep = feed.acquire(['K'])
    await settle()
    made[0].push([{ symbol: 'A', lastTradedPrice: 1 }, { symbol: 'K', lastTradedPrice: 2 }])
    release()
    await settle()
    expect(feed.tick('A')).toBeUndefined()
    expect(feed.tick('K')).toBeDefined()

    feed.stop()
    expect(feed.connection).toBe('disconnected')
    expect(feed.tick('K')).toBeUndefined()
    expect(made[0].stopped).toBe(true)
    keep()
  })

  it('passes on desk events it can read, whatever the case of their kind', async () => {
    const { feed, made } = feedWith()
    const events: LiveDeskEvent[] = []
    feed.onDeskEvent((e) => events.push(e))
    feed.start()
    await settle()
    made[0].event({ kind: 'Fill', runId: 612, userId: 1, symbol: 'NSE:NIFTY2692923300PE', atUtc: '2026-09-28T06:12:04Z', detail: 'BUY 2 @ 64.80' })
    made[0].event({ kind: 'weather', runId: 1 })
    made[0].event(null)
    expect(events).toEqual([
      { kind: 'fill', runId: 612, userId: 1, symbol: 'NSE:NIFTY2692923300PE', atUtc: '2026-09-28T06:12:04Z', detail: 'BUY 2 @ 64.80' },
    ])
  })
})

describe('normalizeTick', () => {
  it('reads what the hub sends and keeps the last known value of a field a push leaves out', () => {
    const first = normalizeTick({ symbol: 'A', lastTradedPrice: 10, openInterest: 5_000, exchangeTimestampUtc: '2026-09-28T04:00:00Z' }, 1)
    expect(first).toEqual({
      symbol: 'A',
      lastTradedPrice: 10,
      bidPrice: null,
      askPrice: null,
      volume: null,
      openInterest: 5_000,
      impliedVolatility: null,
      exchangeTimestampUtc: '2026-09-28T04:00:00Z',
      receivedAtMs: 1,
    })
    expect(normalizeTick({ symbol: 'A', lastTradedPrice: Number.NaN }, 2, first!)).toMatchObject({ lastTradedPrice: 10, openInterest: 5_000, receivedAtMs: 2 })
    expect(normalizeTick({ lastTradedPrice: 1 }, 3)).toBeNull()
  })
})

describe('deskEventKeys', () => {
  it('re-reads the legs, the run and the day after a fill or an order', () => {
    const keys = deskEventKeys({ kind: 'fill', runId: 612 })
    expect(keys).toEqual([
      ['positions'],
      ['optionChainPositions'],
      ['strategy', 'orders', 612],
      ['strategy', 'live', 612],
      ['orders'],
      ['strategy', 'pnl-series'],
      ['strategy', 'history'],
    ])
    expect(deskEventKeys({ kind: 'order', runId: 612 })).toEqual(keys)
  })

  it("re-reads the day's orders across runs (Trade → Orders) after an order, a fill or a risk trip, whatever the run", () => {
    // Every Orders page's key starts with it, whatever its day and filters.
    const blotter = ['orders', 'day', { date: '2026-09-28', userId: null, runId: 612, status: 'Rejected' }]
    const covers = (keys: readonly (readonly unknown[])[]) =>
      keys.some((k) => k.every((part, i) => JSON.stringify(part) === JSON.stringify(blotter[i])))
    for (const kind of ['order', 'fill', 'risk'] as const) {
      expect(covers(deskEventKeys({ kind, runId: 7 }))).toBe(true)
      expect(covers(deskEventKeys({ kind, runId: null }))).toBe(true)
    }
    expect(covers(deskEventKeys({ kind: 'run', runId: 7 }))).toBe(false)
    expect(allEventedKeys()).toContainEqual(['orders'])
  })

  it('re-reads the run lists, the plan and the run after a run starts or stops', () => {
    expect(deskEventKeys({ kind: 'run', runId: 7 })).toEqual([
      ['strategies'],
      ['strategy', 'history'],
      ['desk', 'plan'],
      ['strategy', 'live', 7],
      ['strategy', 'track-record'],
    ])
  })

  it('re-reads the run and the legs after a risk trip, and the legs after a position or carry change', () => {
    expect(deskEventKeys({ kind: 'risk', runId: 7 })).toEqual([['strategy', 'live', 7], ['positions'], ['optionChainPositions'], ['orders']])
    expect(deskEventKeys({ kind: 'carry', runId: 7 })).toEqual([['positions'], ['optionChainPositions'], ['strategy', 'live', 7]])
    expect(deskEventKeys({ kind: 'position', runId: 7 })).toEqual(deskEventKeys({ kind: 'carry', runId: 7 }))
  })

  it("reads every run's views when the event names none, and nothing for a kind it does not know", () => {
    expect(deskEventKeys({ kind: 'fill', runId: null })).toContainEqual(['strategy', 'live'])
    expect(deskEventKeys({ kind: 'fill', runId: null })).toContainEqual(['strategy', 'orders'])
    expect(deskEventKeys({ kind: 'weather' as never, runId: 1 })).toEqual([])
  })

  it('lists each evented query once for a reconnect', () => {
    const keys = allEventedKeys().map((k) => JSON.stringify(k))
    expect(new Set(keys).size).toBe(keys.length)
    expect(keys).toEqual(expect.arrayContaining(['["positions"]', '["strategy","live"]', '["strategies"]', '["desk","plan"]']))
  })
})

describe('invalidationBatcher', () => {
  it('sends each key once for a burst of events', async () => {
    const invalidateQueries = vi.fn()
    const batch = invalidationBatcher({ invalidateQueries } as never, 250)
    batch.push(deskEventKeys({ kind: 'order', runId: 1 }))
    batch.push(deskEventKeys({ kind: 'fill', runId: 1 }))
    batch.push(deskEventKeys({ kind: 'fill', runId: 2 }))
    expect(invalidateQueries).not.toHaveBeenCalled()
    await vi.advanceTimersByTimeAsync(250)
    const sent = invalidateQueries.mock.calls.map(([arg]) => JSON.stringify(arg.queryKey))
    expect(new Set(sent).size).toBe(sent.length)
    expect(sent).toEqual(expect.arrayContaining(['["strategy","live",1]', '["strategy","live",2]', '["positions"]']))
    expect(sent).toHaveLength(9)
  })

  it('drops what is pending when cancelled', async () => {
    const invalidateQueries = vi.fn()
    const batch = invalidationBatcher({ invalidateQueries } as never, 250)
    batch.push([['positions']])
    batch.cancel()
    await vi.advanceTimersByTimeAsync(500)
    expect(invalidateQueries).not.toHaveBeenCalled()
  })
})

describe('livePoll', () => {
  it('polls slowly while prices are pushed and at the old pace otherwise', () => {
    expect(livePoll('connected', 60_000, 5_000)).toBe(60_000)
    expect(livePoll('reconnecting', 60_000, 5_000)).toBe(5_000)
    expect(livePoll('disconnected', 60_000, 5_000)).toBe(5_000)
    expect(livePoll('connected', false, 1_000)).toBe(false)
  })
})

describe('hubUrl', () => {
  it('names the protocol, so the API can tell this console from a bundle that never subscribes', () => {
    expect(hubUrl('')).toBe('/hubs/livefeed?v=2')
    expect(hubUrl('https://openfno.com')).toBe('https://openfno.com/hubs/livefeed?v=2')
  })
})

describe('retryDelay', () => {
  it('retries at once, then backs off to every 30 s, and never gives up', () => {
    expect([0, 1, 2, 3, 4, 5, 50].map(retryDelay)).toEqual([0, 2_000, 5_000, 10_000, 30_000, 30_000, 30_000])
  })
})
