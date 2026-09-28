/**
 * The console's one live connection: the API's /hubs/livefeed, which pushes
 * prices and desk events, and every screen that shows a number moving by the
 * second reads it through here.
 *
 * Until 28 Sep the hub broadcast every tick of every symbol to every
 * browser, and the console folded all of it into one quotes cache. Now it
 * sends a connection only what it asked for, so asking is this module's job:
 *
 * - Symbols are reference-counted. Two panels showing NIFTY are one server
 *   subscription, and it is dropped only when the last of them unmounts.
 * - Asks are batched. A route change unmounts one page and mounts the next
 *   in the same moment; gathering the changes for a tenth of a second turns
 *   that into one Unsubscribe and one Subscribe, and the symbols both pages
 *   show are never dropped at all.
 * - The hub carries at most 400 symbols a connection and throws past that.
 *   The 400 most recently asked for are kept (the page just opened is the
 *   one being read); the rest fall back to their page's poll, and the
 *   console says so once.
 * - A reconnect is a new server connection that remembers nothing: every
 *   symbol, and the everything-feed when a page holds it, is asked for
 *   again, and the queries that desk events keep fresh are re-read, since
 *   the events of the gap were never delivered.
 * - The retry never gives up while someone is signed in. SignalR's default
 *   stops after four tries (about 40 s), and the console then went on
 *   showing its last prices as if they were live until someone reloaded.
 *
 * Polling stays as the safety net: `livePoll` picks a slow interval while
 * the socket is up and today's interval while it is not, so a dropped
 * connection costs freshness, never correctness.
 *
 * The hub's contract (server side):
 *   Subscribe(symbols) → count · Unsubscribe(symbols) → count
 *   SubscribeAll() → true for admins and the market-data grant · UnsubscribeAll()
 *   ReceiveTicks(ticks[]): subscribed symbols only, coalesced every ~250 ms
 *   DeskEvent({ kind, runId, userId, symbol, atUtc, detail }) to the owner and admins
 */

import { useCallback, useEffect, useMemo, useRef, useSyncExternalStore } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import type { QueryClient, QueryKey } from '@tanstack/react-query'
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import { API_BASE_URL, tokenStore } from './api'
import { foldTicks } from './liveMarks'
import type { LiveQuote } from './types'

// ---------------------------------------------------------------- shapes

/** Whether prices are being pushed right now. */
export type LiveConnection = 'connected' | 'reconnecting' | 'disconnected'

/**
 * The everything-feed: nobody holds it (off), held but not asked for yet
 * because the socket is down (waiting), asked (asking), granted (on), or
 * refused, in which case the page that wanted it polls instead.
 */
export type LiveAllState = 'off' | 'waiting' | 'asking' | 'on' | 'refused'

/**
 * One pushed price, as the hub sends it, with the moment this browser got it.
 *
 * Not the stored tick of types.ts (GET /api/LiveData/ticks): this is the
 * newest state of one symbol, merged across pushes, so a push that carries
 * no bid does not blank the last one known.
 */
export interface LiveTick {
  symbol: string
  lastTradedPrice: number | null
  bidPrice: number | null
  askPrice: number | null
  volume: number | null
  openInterest: number | null
  impliedVolatility: number | null
  /** When the exchange stamped the trade. */
  exchangeTimestampUtc: string | null
  /** When this browser received it (ms since the epoch): the age a screen shows. */
  receivedAtMs: number
}

export type DeskEventKind = 'order' | 'fill' | 'run' | 'risk' | 'position' | 'carry'

/** Something happened to a run or a leg; delivered to its owner and to admins. */
export interface LiveDeskEvent {
  kind: DeskEventKind
  runId: number | null
  userId: number | null
  symbol: string | null
  atUtc: string | null
  detail: unknown
}

/** The part of a SignalR HubConnection this uses, so a test can hand it a fake. */
export interface HubLike {
  start(): Promise<void>
  stop(): Promise<void>
  invoke<T = unknown>(method: string, ...args: unknown[]): Promise<T>
  on(method: string, handler: (...args: never[]) => void): void
  onreconnecting(callback: (error?: Error) => void): void
  onreconnected(callback: (connectionId?: string) => void): void
  onclose(callback: (error?: Error) => void): void
}

// ---------------------------------------------------------------- pure helpers

/** The hub's per-connection symbol limit; Subscribe throws past it. */
export const SYMBOL_CAP = 400

/** How long symbol changes are gathered before they go to the hub. */
const BATCH_MS = 100

/** Waits between tries to (re)connect: quick at first, then every 30 s for as long as it takes. */
const RETRY_MS = [0, 2_000, 5_000, 10_000, 30_000]

export function retryDelay(attempt: number): number {
  return RETRY_MS[Math.min(Math.max(0, attempt), RETRY_MS.length - 1)]
}

/**
 * The refetch interval of a query that pushes keep fresh: `connectedMs` while
 * the socket is up (a safety net for a missed event), `fallbackMs` otherwise,
 * which is the interval the page used before there were pushes.
 */
export function livePoll(connection: LiveConnection, connectedMs: number | false, fallbackMs: number | false): number | false {
  return connection === 'connected' ? connectedMs : fallbackMs
}

function num(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null
}

/**
 * One pushed tick, checked and merged over the symbol's previous state: a
 * field the push leaves out (or sends as null) keeps its last known value.
 * Null for anything without a symbol.
 */
export function normalizeTick(raw: unknown, receivedAtMs: number, previous?: LiveTick): LiveTick | null {
  if (!raw || typeof raw !== 'object') return null
  const r = raw as Record<string, unknown>
  if (typeof r.symbol !== 'string' || !r.symbol) return null
  const pick = (key: 'lastTradedPrice' | 'bidPrice' | 'askPrice' | 'volume' | 'openInterest' | 'impliedVolatility') =>
    num(r[key]) ?? previous?.[key] ?? null
  return {
    symbol: r.symbol,
    lastTradedPrice: pick('lastTradedPrice'),
    bidPrice: pick('bidPrice'),
    askPrice: pick('askPrice'),
    volume: pick('volume'),
    openInterest: pick('openInterest'),
    impliedVolatility: pick('impliedVolatility'),
    exchangeTimestampUtc:
      typeof r.exchangeTimestampUtc === 'string' ? r.exchangeTimestampUtc : (previous?.exchangeTimestampUtc ?? null),
    receivedAtMs,
  }
}

const KINDS: readonly DeskEventKind[] = ['order', 'fill', 'run', 'risk', 'position', 'carry']

function normalizeEvent(raw: unknown): LiveDeskEvent | null {
  if (!raw || typeof raw !== 'object') return null
  const r = raw as Record<string, unknown>
  const kind = typeof r.kind === 'string' ? (r.kind.toLowerCase() as DeskEventKind) : null
  if (!kind || !KINDS.includes(kind)) return null
  return {
    kind,
    runId: num(r.runId),
    userId: num(r.userId),
    symbol: typeof r.symbol === 'string' ? r.symbol : null,
    atUtc: typeof r.atUtc === 'string' ? r.atUtc : null,
    detail: r.detail ?? null,
  }
}

/**
 * The queries a desk event makes out of date, as TanStack Query keys (each a
 * prefix: ['positions'] covers ['positions', 'open']). The run's own views
 * when the event names a run, every run's otherwise.
 *
 * - order, fill: the open legs (Positions, the Desk, the chain's positions
 *   panel), the run's order ledger and live view, the day's orders across
 *   runs (Trade → Orders), the day's P&L series and the run lists, whose
 *   P&L and trade counts moved.
 * - run (started, stopped): the strategy list the Live runner reads its
 *   running runs from, the run lists, the morning plan's live count, the
 *   run's live view and the strategies' track records.
 * - risk (a rule tripped or was changed): the run's live view, the legs it
 *   may have squared off, and the day's orders, which hold those
 *   square-offs and the orders the risk gate refused.
 * - position, carry: the open legs, and the run's view that shows the same
 *   leg with its Carry tick.
 */
export function deskEventKeys(event: Pick<LiveDeskEvent, 'kind' | 'runId'>): QueryKey[] {
  const run = event.runId
  const live: QueryKey = run != null ? ['strategy', 'live', run] : ['strategy', 'live']
  const orders: QueryKey = run != null ? ['strategy', 'orders', run] : ['strategy', 'orders']
  const legs: QueryKey[] = [['positions'], ['optionChainPositions']]
  switch (event.kind) {
    case 'order':
    case 'fill':
      return [...legs, orders, live, ['orders'], ['strategy', 'pnl-series'], ['strategy', 'history']]
    case 'run':
      return [['strategies'], ['strategy', 'history'], ['desk', 'plan'], live, ['strategy', 'track-record']]
    case 'risk':
      return [live, ...legs, ['orders']]
    case 'position':
    case 'carry':
      return [...legs, live]
    default:
      return []
  }
}

/** Every query a desk event can touch, for a reconnect: the events of the gap were never delivered. */
export function allEventedKeys(): QueryKey[] {
  const seen = new Map<string, QueryKey>()
  for (const kind of KINDS) for (const key of deskEventKeys({ kind, runId: null })) seen.set(JSON.stringify(key), key)
  return [...seen.values()]
}

/**
 * Invalidations gathered for a moment and sent once each. The open brings a
 * burst (twenty runs starting, each with an order and a fill), and one
 * refetch per query answers all of it.
 */
export function invalidationBatcher(qc: Pick<QueryClient, 'invalidateQueries'>, delayMs = 250) {
  const pending = new Map<string, QueryKey>()
  let timer: ReturnType<typeof setTimeout> | null = null
  const flush = () => {
    timer = null
    const keys = [...pending.values()]
    pending.clear()
    for (const queryKey of keys) void qc.invalidateQueries({ queryKey })
  }
  return {
    push(keys: readonly QueryKey[]) {
      for (const key of keys) pending.set(JSON.stringify(key), key)
      if (keys.length && timer == null) timer = setTimeout(flush, delayMs)
    },
    cancel() {
      if (timer != null) clearTimeout(timer)
      timer = null
      pending.clear()
    },
  }
}

// ---------------------------------------------------------------- the manager

export interface LiveFeedOptions {
  connect: () => HubLike
  cap?: number
  batchMs?: number
  now?: () => number
  warn?: (message: string) => void
}

type Listener = () => void

function each<T>(listeners: Iterable<(value: T) => void>, value: T) {
  for (const listener of listeners) {
    try {
      listener(value)
    } catch {
      // One page's handler must not stop the rest hearing about it.
    }
  }
}

/**
 * The connection and everything asked of it. One instance serves the whole
 * console (`liveFeed` below); tests build their own around a fake hub.
 */
export class LiveFeed {
  private readonly connectHub: () => HubLike
  private readonly cap: number
  private readonly batchMs: number
  private readonly now: () => number
  private readonly warn: (message: string) => void

  private hub: HubLike | null = null
  private wanted = false
  private everConnected = false
  /** Bumped on every (re)connect, so a sync started on the old connection stops touching the new one's state. */
  private generation = 0
  private attempt = 0
  private retryTimer: ReturnType<typeof setTimeout> | null = null
  private batchTimer: ReturnType<typeof setTimeout> | null = null
  private queue: Promise<void> = Promise.resolve()
  private warnedCap = false

  private status: LiveConnection = 'disconnected'
  private allState: LiveAllState = 'off'
  private allHolders = 0

  private readonly refs = new Map<string, number>()
  /** When each symbol was last asked for (a counter): the cap keeps the newest. */
  private readonly askedAt = new Map<string, number>()
  private asks = 0
  /** What the hub has been told to send this connection. */
  private readonly onServer = new Set<string>()

  private readonly latest = new Map<string, LiveTick>()
  private readonly versions = new Map<string, number>()
  private version = 0

  private readonly symbolListeners = new Map<string, Set<Listener>>()
  private readonly stateListeners = new Set<Listener>()
  private readonly tickListeners = new Set<(ticks: LiveTick[]) => void>()
  private readonly eventListeners = new Set<(event: LiveDeskEvent) => void>()
  private readonly reconnectListeners = new Set<Listener>()

  constructor(options: LiveFeedOptions) {
    this.connectHub = options.connect
    this.cap = options.cap ?? SYMBOL_CAP
    this.batchMs = options.batchMs ?? BATCH_MS
    this.now = options.now ?? Date.now
    this.warn = options.warn ?? ((message) => console.warn(message))
  }

  // ---- reads

  get connection(): LiveConnection {
    return this.status
  }

  get all(): LiveAllState {
    return this.allState
  }

  /** The symbols the hub is sending this connection now. */
  get subscribed(): ReadonlySet<string> {
    return this.onServer
  }

  tick(symbol: string): LiveTick | undefined {
    return this.latest.get(symbol)
  }

  versionOf(symbol: string): number {
    return this.versions.get(symbol) ?? 0
  }

  // ---- listening (arrow properties: handed straight to useSyncExternalStore)

  readonly onState = (listener: Listener): (() => void) => {
    this.stateListeners.add(listener)
    return () => this.stateListeners.delete(listener)
  }

  readonly listen = (symbols: readonly string[], listener: Listener): (() => void) => {
    for (const s of symbols) {
      let set = this.symbolListeners.get(s)
      if (!set) this.symbolListeners.set(s, (set = new Set()))
      set.add(listener)
    }
    return () => {
      for (const s of symbols) {
        const set = this.symbolListeners.get(s)
        set?.delete(listener)
        if (set?.size === 0) this.symbolListeners.delete(s)
      }
    }
  }

  onTicks(listener: (ticks: LiveTick[]) => void): () => void {
    this.tickListeners.add(listener)
    return () => this.tickListeners.delete(listener)
  }

  onDeskEvent(listener: (event: LiveDeskEvent) => void): () => void {
    this.eventListeners.add(listener)
    return () => this.eventListeners.delete(listener)
  }

  onReconnected(listener: Listener): () => void {
    this.reconnectListeners.add(listener)
    return () => this.reconnectListeners.delete(listener)
  }

  // ---- asking

  /** Ask for these symbols' prices; the returned function gives them back. */
  acquire(symbols: readonly string[]): () => void {
    const list = [...new Set(symbols.filter((s) => typeof s === 'string' && s.length > 0))]
    for (const s of list) {
      this.refs.set(s, (this.refs.get(s) ?? 0) + 1)
      this.askedAt.set(s, ++this.asks)
    }
    if (list.length) this.schedule()
    let released = false
    return () => {
      if (released) return
      released = true
      for (const s of list) {
        const n = (this.refs.get(s) ?? 0) - 1
        if (n > 0) {
          this.refs.set(s, n)
        } else {
          this.refs.delete(s)
          this.askedAt.delete(s)
        }
      }
      if (list.length) this.schedule()
    }
  }

  /** Ask for every symbol (admins and the market-data grant); the returned function gives it back. */
  holdAll(): () => void {
    this.allHolders++
    if (this.allState === 'off') this.setAll('waiting')
    this.schedule()
    let released = false
    return () => {
      if (released) return
      released = true
      this.allHolders--
      // Nothing to tell the hub unless it granted it: settle now, so a page
      // left while the socket is down does not leave the quotes polling fast.
      if (this.allHolders === 0 && this.allState !== 'on' && this.allState !== 'asking') this.setAll('off')
      this.schedule()
    }
  }

  // ---- the connection

  start(): void {
    if (this.wanted) return
    this.wanted = true
    this.open()
  }

  stop(): void {
    this.wanted = false
    if (this.retryTimer != null) clearTimeout(this.retryTimer)
    if (this.batchTimer != null) clearTimeout(this.batchTimer)
    this.retryTimer = null
    this.batchTimer = null
    this.attempt = 0
    this.everConnected = false
    this.generation++
    const hub = this.hub
    this.hub = null
    this.onServer.clear()
    // Signed out: nothing on screen may go on showing these as live.
    this.forget([...this.latest.keys()])
    this.setAll(this.allHolders > 0 ? 'waiting' : 'off')
    this.setStatus('disconnected')
    hub?.stop().catch(() => {})
  }

  private open(): void {
    const hub = this.connectHub()
    this.hub = hub
    hub.on('ReceiveTicks', (ticks: unknown) => {
      if (this.hub === hub) this.receive(ticks)
    })
    hub.on('DeskEvent', (event: unknown) => {
      if (this.hub === hub) this.dispatch(event)
    })
    hub.onreconnecting(() => {
      if (this.hub !== hub) return
      if (this.allState === 'on' || this.allState === 'asking') this.setAll('waiting')
      this.setStatus('reconnecting')
    })
    hub.onreconnected(() => {
      if (this.hub === hub) this.connected()
    })
    hub.onclose(() => {
      if (this.hub !== hub || !this.wanted) return
      this.lost()
    })
    this.setStatus('reconnecting')
    hub.start().then(
      () => {
        if (this.hub !== hub) return
        this.attempt = 0
        this.connected()
      },
      () => {
        if (this.hub === hub) this.lost()
      },
    )
  }

  /** The connection is gone for good (a failed start, or closed): a new one, after a wait. */
  private lost(): void {
    this.hub = null
    this.generation++
    this.onServer.clear()
    if (this.allState === 'on' || this.allState === 'asking') this.setAll('waiting')
    this.setStatus('reconnecting')
    if (this.retryTimer != null) return
    this.retryTimer = setTimeout(() => {
      this.retryTimer = null
      if (this.wanted && !this.hub) this.open()
    }, retryDelay(this.attempt++))
  }

  private connected(): void {
    // A new server connection: none of the old subscriptions came with it.
    this.generation++
    this.onServer.clear()
    this.setAll(this.allHolders > 0 ? 'waiting' : 'off')
    this.setStatus('connected')
    this.flush()
    if (this.everConnected) each(this.reconnectListeners, undefined)
    this.everConnected = true
  }

  private setStatus(next: LiveConnection): void {
    if (this.status === next) return
    this.status = next
    each(this.stateListeners, undefined)
  }

  private setAll(next: LiveAllState): void {
    if (this.allState === next) return
    this.allState = next
    each(this.stateListeners, undefined)
  }

  // ---- telling the hub

  private schedule(): void {
    if (this.batchTimer != null) return
    this.batchTimer = setTimeout(() => {
      this.batchTimer = null
      this.flush()
    }, this.batchMs)
  }

  /** One sync at a time, in order: an Unsubscribe lands before the Subscribe that relies on its room. */
  private flush(): void {
    this.queue = this.queue.then(() => this.sync()).catch(() => {})
  }

  /** The symbols to hold: every one asked for, or the newest `cap` of them. */
  private desired(): Set<string> {
    const asked = [...this.refs.keys()]
    if (asked.length <= this.cap) return new Set(asked)
    if (!this.warnedCap) {
      this.warnedCap = true
      this.warn(
        `Live prices: ${asked.length} symbols are on screen and the hub sends ${this.cap} a connection. ` +
          `The ${asked.length - this.cap} asked for longest ago are left to their page's poll.`,
      )
    }
    asked.sort((a, b) => (this.askedAt.get(b) ?? 0) - (this.askedAt.get(a) ?? 0))
    return new Set(asked.slice(0, this.cap))
  }

  private async sync(): Promise<void> {
    const hub = this.hub
    const generation = this.generation
    if (!hub || this.status !== 'connected') return
    const still = () => this.hub === hub && this.generation === generation

    const desired = this.desired()
    const drop = [...this.onServer].filter((s) => !desired.has(s))
    const add = [...desired].filter((s) => !this.onServer.has(s))

    if (drop.length) {
      for (const s of drop) this.onServer.delete(s)
      // A symbol no longer sent must not go on being shown as the latest price.
      this.forget(drop.filter((s) => !this.refs.has(s) || this.allState !== 'on'))
      try {
        await hub.invoke('Unsubscribe', drop)
      } catch {
        // Nothing to undo: a price it goes on sending is still a real price
        // (and is not kept unless a screen holds it); a reconnect starts from none.
      }
      if (!still()) return
    }

    if (add.length) {
      for (const s of add) this.onServer.add(s)
      try {
        await hub.invoke('Subscribe', add)
      } catch (err) {
        if (!still()) return
        for (const s of add) this.onServer.delete(s)
        this.warn(`Live prices: the hub refused ${add.length} symbol(s); their pages keep polling. ${err instanceof Error ? err.message : ''}`.trim())
      }
      if (!still()) return
    }

    if (this.allHolders > 0 && this.allState === 'waiting') {
      this.setAll('asking')
      let granted = false
      try {
        granted = (await hub.invoke<boolean>('SubscribeAll')) === true
      } catch {
        granted = false
      }
      if (!still()) return
      this.setAll(granted ? 'on' : 'refused')
    }

    if (this.allHolders === 0 && this.allState !== 'off') {
      const was = this.allState
      this.setAll('off')
      // Everything else it was sending goes with it.
      this.forget([...this.latest.keys()].filter((s) => !this.onServer.has(s)))
      if (was === 'on') {
        try {
          await hub.invoke('UnsubscribeAll')
        } catch {
          // As above: the server drops it with the connection.
        }
      }
    }
  }

  // ---- hearing from the hub

  private receive(raw: unknown): void {
    if (!Array.isArray(raw) || raw.length === 0) return
    const at = this.now()
    const changed: LiveTick[] = []
    const notify = new Set<Listener>()
    for (const item of raw) {
      const symbol = (item as { symbol?: unknown } | null)?.symbol
      if (typeof symbol !== 'string') continue
      const tick = normalizeTick(item, at, this.latest.get(symbol))
      if (!tick) continue
      changed.push(tick)
      // Kept only for what a screen asked for; the everything-feed's other
      // symbols reach the quotes cache through the tick listeners alone.
      if (!this.refs.has(symbol)) continue
      this.latest.set(symbol, tick)
      this.versions.set(symbol, ++this.version)
      for (const l of this.symbolListeners.get(symbol) ?? []) notify.add(l)
    }
    if (!changed.length) return
    // Once each, however many of its symbols the push carried: one render a push.
    each(notify, undefined)
    each(this.tickListeners, changed)
  }

  private dispatch(raw: unknown): void {
    const event = normalizeEvent(raw)
    if (event) each(this.eventListeners, event)
  }

  private forget(symbols: readonly string[]): void {
    const notify = new Set<Listener>()
    for (const s of symbols) {
      if (!this.latest.delete(s)) continue
      this.versions.set(s, ++this.version)
      for (const l of this.symbolListeners.get(s) ?? []) notify.add(l)
    }
    each(notify, undefined)
  }
}

// ---------------------------------------------------------------- the console's instance

/**
 * The hub's address, with the protocol this console speaks. `v=2` is the
 * subscribe-for-what-you-show contract above; a connection without it is a
 * console bundle from before 28 Sep, which never calls Subscribe, and the API
 * sends it the old whole-feed broadcast for one release so a tab left open
 * across the deploy does not go quiet.
 */
export function hubUrl(apiBase: string): string {
  return `${apiBase}/hubs/livefeed?v=2`
}

function connectHub(): HubLike {
  return new HubConnectionBuilder()
    // The hub is authorized: it carries market data the platform pays a
    // vendor for. The factory is read on every (re)connect, so a token the
    // API layer refreshed in the meantime is the one sent.
    .withUrl(hubUrl(API_BASE_URL), { accessTokenFactory: () => tokenStore.access ?? '' })
    .configureLogging(LogLevel.Warning)
    .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (ctx) => retryDelay(ctx.previousRetryCount) })
    .build()
}

export const liveFeed = new LiveFeed({ connect: connectHub })

// ---------------------------------------------------------------- hooks

/**
 * Opens the connection for the signed-in console and wires what it pushes
 * into the query cache: desk events invalidate the queries they make stale,
 * a reconnect re-reads all of them, and with the everything-feed held each
 * price is folded into ['quotes', 'all'] for the pages that read that list.
 *
 * Mounted once, in the signed-in shell: signing in opens it, signing out
 * (the shell unmounting) closes it.
 */
export function useLiveFeed(): void {
  const qc = useQueryClient()
  useEffect(() => {
    if (!tokenStore.access) return
    const batch = invalidationBatcher(qc)
    const offTicks = liveFeed.onTicks((ticks) => {
      if (liveFeed.all !== 'on') return
      qc.setQueryData<LiveQuote[]>(['quotes', 'all'], (old) => (old ? foldTicks(old, ticks) : old))
    })
    const offEvents = liveFeed.onDeskEvent((event) => batch.push(deskEventKeys(event)))
    const offReconnect = liveFeed.onReconnected(() => batch.push(allEventedKeys()))
    liveFeed.start()
    return () => {
      offTicks()
      offEvents()
      offReconnect()
      batch.cancel()
      liveFeed.stop()
    }
  }, [qc])
}

export function useLiveConnection(): LiveConnection {
  return useSyncExternalStore(liveFeed.onState, () => liveFeed.connection, () => liveFeed.connection)
}

/** `livePoll` against the connection now: the interval a pushed query polls at. */
export function useLivePoll(connectedMs: number | false, fallbackMs: number | false): number | false {
  return livePoll(useLiveConnection(), connectedMs, fallbackMs)
}

/** The everything-feed's state without asking for it. */
export function useLiveAllState(): LiveAllState {
  return useSyncExternalStore(liveFeed.onState, () => liveFeed.all, () => liveFeed.all)
}

/**
 * Every symbol's prices, for the pages that list the whole feed (Data →
 * Overview and Live feeds). Folded into ['quotes', 'all']; when the hub
 * refuses (no market-data grant) the answer says so and that query polls
 * faster instead.
 */
export function useLiveAll(): LiveAllState {
  useEffect(() => liveFeed.holdAll(), [])
  return useLiveAllState()
}

/** Calls `listener` with each desk event while mounted. */
export function onDeskEvent(listener: (event: LiveDeskEvent) => void): () => void {
  return liveFeed.onDeskEvent(listener)
}

const SEP = '\n'
const NO_TICKS: ReadonlyMap<string, LiveTick> = new Map()

/**
 * The newest pushed price of each of `symbols` that has one. The map is the
 * same object from render to render until one of THESE symbols moves, so it
 * can sit in a dependency list; a push for anything else does not re-render
 * the caller. The symbols are asked of the hub while the caller is mounted.
 */
export function useLivePrices(symbols: readonly string[]): ReadonlyMap<string, LiveTick> {
  const key = [...new Set(symbols.filter(Boolean))].sort().join(SEP)
  const list = useMemo(() => (key ? key.split(SEP) : []), [key])
  useEffect(() => (list.length ? liveFeed.acquire(list) : undefined), [list])

  const cache = useRef<{ list: readonly string[]; versions: number[]; map: ReadonlyMap<string, LiveTick> } | null>(null)
  const subscribe = useCallback((notify: () => void) => liveFeed.listen(list, notify), [list])
  const snapshot = useCallback(() => {
    if (!list.length) return NO_TICKS
    const versions = list.map((s) => liveFeed.versionOf(s))
    const held = cache.current
    if (held && held.list === list && held.versions.every((v, i) => v === versions[i])) return held.map
    const map = new Map<string, LiveTick>()
    for (const s of list) {
      const tick = liveFeed.tick(s)
      if (tick) map.set(s, tick)
    }
    cache.current = { list, versions, map }
    return map
  }, [list])
  return useSyncExternalStore(subscribe, snapshot, snapshot)
}
