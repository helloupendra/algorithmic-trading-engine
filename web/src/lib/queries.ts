/**
 * TanStack Query hooks for every backend domain the UI shows.
 *
 * Conventions:
 *  - one exported hook per endpoint, named use<Thing>;
 *  - query keys are arrays rooted in a domain word, so invalidation can target
 *    a whole domain (`queryClient.invalidateQueries({ queryKey: ['watchlist'] })`);
 *  - polling intervals live here, not in components — a screen should not need
 *    to know how fresh "fresh" is.
 *
 * The market data in the DB is whatever the ingestor last saved; when the
 *  market is closed these queries simply keep returning the stored snapshot.
 */

import { keepPreviousData, useInfiniteQuery, useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query'
import type { InfiniteData } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import { api, API_BASE_URL, tokenStore } from './api'
import type {
  AlertEvent,
  BackfillHistoryResponse,
  BacktestBackfillRequest,
  BacktestBackfillResponse,
  BacktestCoverageResponse,
  BacktestEquity,
  BacktestRunSummary,
  BacktestRunView,
  BrokerSessionInfo,
  CandleDto,
  ChainPollerStatus,
  DeskPlanResponse,
  FnoUnderlying,
  IngestorProcessStatus,
  IngestorStatus,
  Instrument,
  InstrumentMastersResponse,
  KillSwitchState,
  LiveBar,
  LiveFeed,
  LiveQuote,
  LiveRunSummary,
  LiveRunUserSummary,
  StrategyTrackRecord,
  LiveTick,
  LiveWatchlistItem,
  MarketSessionInfo,
  OptionChain,
  OptionChainSeries,
  OpenPositionsResponse,
  PaperOrderRow,
  PruneWatchlistResponse,
  RiskEvent,
  RiskExposureResponse,
  RiskLimits,
  RunPnlSeriesResponse,
  SmcLadder,
  SmcStructure,
  StaleQuote,
  StaleWatchlistResponse,
  StartBacktestRequest,
  StartBacktestResponse,
  StartStrategyRequest,
  StartStrategyResponse,
  StopStrategyResponse,
  StrategyActiveRun,
  StrategyLastExit,
  StrategyListItem,
  StrategyLiveView,
  UpdateRunRiskRequest,
  UpdateRunRiskResponse,
} from './types'
import type { MeResponse } from './api'
import type { SystemHostReport } from './system'

// Re-exported so pages import user types from one place.
export type { MeResponse }

const POLL_FAST = 5_000
const POLL_SLOW = 15_000

// ---------- Live data ----------

export function useWatchlist() {
  return useQuery({
    queryKey: ['watchlist'],
    queryFn: () => api.get<LiveWatchlistItem[]>('/api/LiveData/watchlist'),
    refetchInterval: POLL_SLOW,
  })
}

export function useLatestQuotes() {
  return useQuery({
    queryKey: ['quotes', 'all'],
    queryFn: () => api.get<LiveQuote[]>('/api/LiveData/latest/all'),
    refetchInterval: POLL_SLOW, // Relies on SignalR for fast updates
  })
}

/**
 * Pages that want every pushed price as it lands (the option chain) listen
 * here, rather than opening a second hub connection of their own.
 */
type LiveTicksListener = (ticks: any[]) => void
const liveTicksListeners = new Set<LiveTicksListener>()

export function onLiveTicks(listener: LiveTicksListener): () => void {
  liveTicksListeners.add(listener)
  return () => {
    liveTicksListeners.delete(listener)
  }
}

export function useLiveFeedSignalR() {
  const qc = useQueryClient()

  useEffect(() => {
    // Nothing to subscribe to while signed out. The component that calls this
    // is mounted above the router, so it also runs on the public landing and
    // login pages — where it would otherwise open a socket, fail to authorize,
    // and retry forever.
    if (!tokenStore.access) return

    const connection = new HubConnectionBuilder()
      // The hub is authorized now — it carries live market data the platform
      // pays a broker for, and used to accept anyone with the URL.
      .withUrl(`${API_BASE_URL}/hubs/livefeed`, {
        accessTokenFactory: () => tokenStore.access ?? '',
      })
      .configureLogging(LogLevel.Warning)
      .withAutomaticReconnect()
      .build()

    // Applies a whole delivery in one cache write.
    //
    // The ingestor posts ticks in batches now, so the hub sends "ReceiveTicks"
    // with an array. Folding the array in here rather than looping the single
    // handler means one render per delivery instead of one per price, which is
    // what keeps a busy open-bell burst from thrashing the tree.
    const applyTicks = (ticks: any[]) => {
      if (!ticks.length) return
      for (const listener of liveTicksListeners) {
        try {
          listener(ticks)
        } catch {
          // One page's handler must not stop the prices reaching the rest.
        }
      }
      qc.setQueryData(['quotes', 'all'], (old: LiveQuote[] | undefined) => {
        if (!old) return old

        const bySymbol = new Map<string, any>()
        // Last one wins: within a batch the newest price for a symbol is the
        // one worth rendering.
        for (const tick of ticks) {
          if (tick?.symbol) bySymbol.set(tick.symbol, tick)
        }
        if (!old.some((q) => bySymbol.has(q.symbol))) return old

        return old.map((q) => {
          const tick = bySymbol.get(q.symbol)
          if (!tick) return q
          return {
            ...q,
            lastTradedPrice: tick.lastTradedPrice ?? q.lastTradedPrice,
            bidPrice: tick.bidPrice ?? q.bidPrice,
            askPrice: tick.askPrice ?? q.askPrice,
            volume: tick.volume ?? q.volume,
            // Arrival time, to match what the REST snapshot puts here. This
            // used to be assigned the tick's EXCHANGE stamp, so the field meant
            // "when we got it" after a poll and "when it last traded" after a
            // push - and a quiet contract's age jumped between 1s and minutes
            // depending on which had landed last.
            updatedUtc: new Date().toISOString(),
            exchangeTimestampUtc: tick.exchangeTimestampUtc ?? q.exchangeTimestampUtc,
          }
        })
      })
    }

    // Both shapes are handled: the batch endpoint sends the plural, and the
    // single-tick endpoint is still there for anything that posts one at a time.
    connection.on('ReceiveTicks', (ticks: any[]) => applyTicks(ticks ?? []))
    connection.on('ReceiveTick', (tick: any) => applyTicks(tick ? [tick] : []))

    let isMounted = true

    connection.start().catch((err) => {
      if (isMounted) console.error(err)
    })

    return () => {
      isMounted = false
      connection.stop()
    }
  }, [qc])
}

export function useLiveBars(symbol: string | null, take = 500) {
  return useQuery({
    queryKey: ['bars', symbol, take],
    queryFn: () =>
      api.get<LiveBar[]>(
        `/api/LiveData/bars?symbol=${encodeURIComponent(symbol!)}&take=${take}`,
      ),
    enabled: !!symbol,
    refetchInterval: POLL_SLOW,
  })
}

export function useRecentTicks(symbol: string | null, take = 50) {
  return useQuery({
    queryKey: ['ticks', symbol, take],
    queryFn: () =>
      api.get<LiveTick[]>(
        `/api/LiveData/ticks?symbol=${encodeURIComponent(symbol!)}&take=${take}`,
      ),
    enabled: !!symbol,
    refetchInterval: POLL_SLOW,
  })
}

export function useIngestorStatuses() {
  return useQuery({
    queryKey: ['ingestor', 'all'],
    queryFn: () => api.get<IngestorStatus[]>('/api/LiveData/status/all'),
    refetchInterval: POLL_SLOW,
  })
}

/**
 * Whether a feed process is alive and how the API knows it (managed by this
 * instance, adopted from a persisted pid after a restart, or no pid known).
 */
export function useIngestorProcessStatus() {
  return useQuery({
    queryKey: ['ingestor', 'process'],
    queryFn: () => api.get<IngestorProcessStatus>('/api/Ingestor/status'),
    refetchInterval: POLL_SLOW,
  })
}

/**
 * Every live feed the API can run, one per connector that declares live ticks,
 * with whether each is running. The list is the server's: a vendor added there
 * appears here with no change to the console.
 *
 * Admin-only on the API: the topbar, mounted for every role, passes `enabled`
 * so a trader's console does not poll a 403.
 */
export function useFeeds({ enabled = true }: { enabled?: boolean } = {}) {
  return useQuery({
    queryKey: ['feeds'],
    queryFn: () => api.get<LiveFeed[]>('/api/Feeds'),
    refetchInterval: POLL_SLOW,
    enabled,
  })
}

/**
 * Settles on success and on failure alike: a start refused as "already
 * running", or a stop that timed out, still changed what the list should say.
 * The ingestor queries are refreshed too, since the FYERS row is that process.
 */
function useFeedMutation(action: 'start' | 'stop') {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (key: string) =>
      api.post<{ message: string }>(`/api/Feeds/${encodeURIComponent(key)}/${action}`),
    onSettled: () => {
      qc.invalidateQueries({ queryKey: ['feeds'] })
      qc.invalidateQueries({ queryKey: ['ingestor'] })
    },
  })
}

export function useStartFeed() {
  return useFeedMutation('start')
}

export function useStopFeed() {
  return useFeedMutation('stop')
}

/** Recent output of one feed's process; only fetched while someone is looking. */
export function useFeedLogs(key: string | null, take = 80) {
  return useQuery({
    queryKey: ['feeds', 'logs', key, take],
    queryFn: () => api.get<string[]>(`/api/Feeds/${encodeURIComponent(key!)}/logs?take=${take}`),
    enabled: key !== null,
    refetchInterval: POLL_FAST,
  })
}

export function useStaleQuotes(staleAfterSeconds = 60) {
  return useQuery({
    queryKey: ['quotes', 'stale', staleAfterSeconds],
    queryFn: () =>
      api.get<StaleQuote[]>(`/api/LiveData/stale?staleAfterSeconds=${staleAfterSeconds}`),
    refetchInterval: POLL_SLOW,
  })
}

export function useAddWatchlistSymbol() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (input: { symbol: string; dataType: string; priority?: number }) =>
      api.post<LiveWatchlistItem>('/api/LiveData/watchlist', {
        symbol: input.symbol,
        dataType: input.dataType,
        isActive: true,
        priority: input.priority ?? 0,
      }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['watchlist'] }),
  })
}

export function useRemoveWatchlistSymbol() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api.delete<{ message: string }>(`/api/LiveData/watchlist/${id}`),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['watchlist'] }),
  })
}

/** Rows the feed can no longer serve, with the reason each — what "Remove stale" would take out. */
export function useStaleWatchlist() {
  return useQuery({
    queryKey: ['watchlist', 'stale'],
    queryFn: () => api.get<StaleWatchlistResponse>('/api/LiveData/watchlist/stale'),
    refetchInterval: 60_000,
    staleTime: 30_000,
  })
}

/** Remove every stale row (or only the ids given) in one call; the ingestor resubscribes. */
export function usePruneWatchlist() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (ids?: number[]) => api.post<PruneWatchlistResponse>('/api/LiveData/watchlist/prune', { ids: ids ?? [] }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['watchlist'] })
    },
  })
}

// ---------- Strategy lab ----------

export interface LabIndicators {
  barIst: string | null
  open: number; high: number; low: number; close: number; volume: number
  sessionBars: number
  vwap: number | null
  ema: number | null
  emaPeriod: number
  sma20: number | null
  rsi14: number | null
  atr14: number | null
  atrPercent: number | null
  volumeZScore: number | null
  patterns: string[]
}

export interface LabVerdict {
  allowed: boolean
  blockedBy: string | null
  reason: string
  details: Record<string, unknown>
}

export interface LabSideVerdict {
  allowed: boolean
  blockedBy: string | null
  reason: string
}

export interface LabTimelineRow {
  barIst: string | null
  close: number
  /** The same candle judged for a call and for a put. */
  bullish: LabSideVerdict
  bearish: LabSideVerdict
}

export interface LabSideTally {
  allowed: number
  blocked: number
  blockedBy: Record<string, number>
}

export interface LabResult {
  symbol: string
  resolution: string
  barCount: number
  closedBarCount: number
  filtersConfigured: boolean
  /** Where the bars came from — stored candles for a window, or the live table. */
  source: string
  indicators: LabIndicators
  verdicts: { bullish: LabVerdict; bearish: LabVerdict }
  timeline: LabTimelineRow[]
  summary: {
    evaluated: number
    bullish: LabSideTally
    bearish: LabSideTally
  }
  error?: string
}

/**
 * Runs the real indicators and the real gate over real bars.
 *
 * A mutation, not a query: it is an experiment the operator asks for, and it
 * should run when they press the button rather than on a poll they cannot see.
 */
export function useEvaluateFilters() {
  return useMutation({
    mutationFn: (body: {
      symbol: string
      resolution: string
      fromDate?: string
      toDate?: string
      fromTime?: string
      toTime?: string
      bars?: number
      filters: unknown
    }) =>
      api.post<LabResult>('/api/StrategyLab/evaluate', body),
  })
}

// ---------- Backend process ----------

export interface BackendStatus {
  startedUtc: string
  uptimeSeconds: number
  version: string | null
  environment: string | null
}

/**
 * Whether the backend is up, and whether it is the SAME backend as a moment ago.
 *
 * A rebuild takes the API down for a few seconds and every polling page turns
 * red with "Failed to fetch", which reads as a fault rather than a restart.
 * Watching the process start time turns that into something the console can
 * state: it went away, it came back, and it is a new process.
 */
export function useBackendStatus() {
  const query = useQuery({
    queryKey: ['backend', 'status'],
    queryFn: () => api.get<BackendStatus>('/api/Backend/status'),
    refetchInterval: 5_000,
    // The outage is the thing being measured, so a failed poll must not stop
    // the polling, and the last good answer stays on screen meanwhile.
    retry: false,
    placeholderData: (previous) => previous,
  })

  const firstSeen = useRef<string | null>(null)
  const [restartedAt, setRestartedAt] = useState<string | null>(null)

  useEffect(() => {
    const started = query.data?.startedUtc
    if (!started) return
    if (firstSeen.current === null) {
      firstSeen.current = started
      return
    }
    if (firstSeen.current !== started) {
      firstSeen.current = started
      setRestartedAt(new Date().toISOString())
    }
  }, [query.data?.startedUtc])

  return {
    ...query,
    /** Set the moment a NEW process was first seen; null until one is. */
    restartedAt,
    acknowledgeRestart: () => setRestartedAt(null),
    isDown: query.isError,
  }
}

/**
 * The server the platform runs on: disk, memory, CPU, the database and how
 * fast it grows, the Drive archive. Admin-only. The API caches the report for
 * ~12 s, so polling every 15 s costs it a cached read.
 */
export function useSystemHost() {
  return useQuery({
    queryKey: ['system', 'host'],
    queryFn: () => api.get<SystemHostReport>('/api/System/host'),
    refetchInterval: POLL_SLOW,
  })
}

// ---------- Manual orders ----------

/** One instrument's live two-sided price and the rules its segment imposes. */
export interface ManualInstrument {
  symbol: string
  exchange: string
  segment: string
  segmentLabel: string
  instrumentType: string
  underlying: string | null
  strikePrice: number | null
  expiryDate: string | null
  tickSize: number | null
  lotSize: number
  lotSizeSource: string
  /** "lots" for a derivative, "shares" for equity — the word the form should use. */
  quantityUnit: 'lots' | 'shares'
  ltp: number | null
  bid: number | null
  ask: number | null
  bidSize: number | null
  askSize: number | null
  open: number | null
  high: number | null
  low: number | null
  prevClose: number | null
  volume: number | null
  change: number | null
  changePercent: number | null
  quoteUpdatedUtc: string | null
  /** This lookup just put the symbol on the feed; the first tick is seconds away. */
  subscribing: boolean
  /** The calendar has retired this contract; it will never quote again. */
  expired: boolean
  /** What a market order would pay right now. */
  buyAt: number | null
  sellAt: number | null
  tradable: boolean
}

export interface ManualOrderResponse {
  message: string
  /** Held overnight (true) or intraday (false); absent on an API older than 28 Sep. */
  carryForward?: boolean
  runId: number
  groupId: string
  symbol: string
  side: string
  quantity: number
  lotSize: number
  filledQuantity: number
  price: number
  priceBasis: string
  signalId: number
}

/**
 * The ticket's live half. Polled fast: the operator is reading a bid and an ask
 * they are about to hit, and a price a few seconds stale is the one thing this
 * screen must not show.
 */
export function useManualInstrument(symbol: string) {
  const trimmed = symbol.trim()
  return useQuery({
    queryKey: ['manual', 'instrument', trimmed],
    queryFn: () => api.get<ManualInstrument>(`/api/ManualOrders/instrument?symbol=${encodeURIComponent(trimmed)}`),
    // Only once the box holds something shaped like a broker symbol. Firing on
    // every keystroke asked the API about "M", "MC", "MCX:C"... and each 404
    // painted "not in the instrument master" under a field still being typed in.
    enabled: /^[A-Za-z]+:.{3,}$/.test(trimmed),
    refetchInterval: 2_000,
    // Keep the last good reading on screen while the next one is in flight,
    // so the prices do not blink out between polls.
    placeholderData: (previous) => previous,
    retry: false,
  })
}

/** The caller's manual book, or null before their first order. */
export function useManualBook(enabled = true) {
  return useQuery({
    queryKey: ['manual', 'book'],
    queryFn: () => api.get<{ runId: number | null; status?: string; startedUtc?: string }>('/api/ManualOrders/book'),
    enabled,
    refetchInterval: POLL_SLOW,
  })
}

export function usePlaceManualOrder() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (body: {
      symbol: string
      side: 'BUY' | 'SELL'
      quantity: number
      limitPrice?: number | null
      stopLossPrice?: number | null
      targetPrice?: number | null
      /** Hold overnight; false (the default) is intraday, squared off at the close. */
      carryForward?: boolean
    }) =>
      api.post<ManualOrderResponse>('/api/ManualOrders', body),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['manual', 'book'] })
      qc.invalidateQueries({ queryKey: ['strategy', 'live'] })
      qc.invalidateQueries({ queryKey: ['positions'] })
    },
  })
}

// ---------- Instruments & derivatives ----------

export function useInstrumentSearch(query: string, type?: string, includeExpired = false) {
  return useQuery({
    queryKey: ['instruments', 'search', query, type, includeExpired],
    queryFn: () => {
      let url = `/api/Instruments/search?query=${encodeURIComponent(query)}`
      if (type) url += `&type=${type}`
      // Expired contracts are hidden unless a caller genuinely works with the
      // past (a historical backfill does; a trading ticket never does).
      if (includeExpired) url += '&includeExpired=true'
      return api.get<Instrument[]>(url)
    },
    enabled: query.trim().length >= 2,
    staleTime: 60_000,
  })
}

export function useStoredCandles(
  symbol: string | null,
  resolution = 'D',
  fromDate?: string,
  toDate?: string,
) {
  const range =
    (fromDate ? `&fromDate=${fromDate}` : '') + (toDate ? `&toDate=${toDate}` : '')
  return useQuery({
    queryKey: ['candles', symbol, resolution, fromDate, toDate],
    queryFn: () =>
      api.get<CandleDto[]>(
        `/api/MarketData/history/local?symbol=${encodeURIComponent(symbol!)}&resolution=${resolution}${range}`,
      ),
    enabled: !!symbol,
    staleTime: 60_000,
  })
}

/**
 * Market structure for one symbol and timeframe: the candles and the marks read
 * from them come back together, so the chart never draws a mark against a
 * different series than it was computed on.
 *
 * The chart's structure layer reads the ladder below instead, so nothing calls
 * this one today; it is kept in step with it rather than allowed to drift apart.
 */
export function useSmcStructure(params: {
  symbol: string | null
  resolution: string
  fromDate?: string
  toDate?: string
  method: string
  breakOn: string
  inducement: string
  /** Defaults mirror the endpoint's own, so a caller that does not care can leave them out. */
  zones?: string
  fvgMinSize?: number
  /** Ask for the zones still standing only: unmitigated blocks and unfilled gaps. */
  standingZonesOnly?: boolean
  includeLive: boolean
}) {
  const { symbol, resolution, fromDate, toDate, method, breakOn, inducement, includeLive } = params
  const { zones = 'touch', fvgMinSize = 0, standingZonesOnly = false } = params
  const range = (fromDate ? `&fromDate=${fromDate}` : '') + (toDate ? `&toDate=${toDate}` : '')
  return useQuery({
    // Every setting that changes the reading — or what of it is reported —
    // belongs in the key as well as the URL, or a switch flipped here is
    // answered from a cache computed under the old one. `standingZonesOnly` is
    // in for that second reason: asking for the history back has to go to the
    // server rather than be served the pruned list it fetched a minute ago.
    queryKey: [
      'smc', symbol, resolution, fromDate, toDate, method, breakOn, inducement, zones, fvgMinSize,
      standingZonesOnly, includeLive,
    ],
    queryFn: () =>
      api.get<SmcStructure>(
        `/api/Smc/structure?symbol=${encodeURIComponent(symbol!)}&resolution=${resolution}` +
          `${range}&method=${method}&breakOn=${breakOn}&inducement=${inducement}` +
          `&zones=${zones}&fvgMinSize=${fvgMinSize}&standingZonesOnly=${standingZonesOnly}` +
          `&includeLive=${includeLive}`,
      ),
    enabled: !!symbol,
    staleTime: 30_000,
    refetchInterval: includeLive ? POLL_SLOW : false,
  })
}

/**
 * The same structure on several timeframes at once. The last timeframe is the
 * one drawn; the ones above it come back as state and marks, so a 5-minute
 * chart can show the level the day is protecting without a second request.
 */
export function useSmcLadder(params: {
  symbol: string | null
  timeframes: string
  fromDate?: string
  toDate?: string
  method: string
  breakOn: string
  inducement: string
  /** Defaults mirror the endpoint's own, so a caller that does not care can leave them out. */
  zones?: string
  fvgMinSize?: number
  /** Ask for the zones still standing only: unmitigated blocks and unfilled gaps. */
  standingZonesOnly?: boolean
  includeLive: boolean
}) {
  const { symbol, timeframes, fromDate, toDate, method, breakOn, inducement, includeLive } = params
  const { zones = 'touch', fvgMinSize = 0, standingZonesOnly = false } = params
  const range = (fromDate ? `&fromDate=${fromDate}` : '') + (toDate ? `&toDate=${toDate}` : '')
  return useQuery({
    // As above: the key carries every setting the reading, or the reporting of
    // it, depends on.
    queryKey: [
      'smc-ladder', symbol, timeframes, fromDate, toDate, method, breakOn, inducement, zones, fvgMinSize,
      standingZonesOnly, includeLive,
    ],
    queryFn: () =>
      api.get<SmcLadder>(
        `/api/Smc/ladder?symbol=${encodeURIComponent(symbol!)}&timeframes=${encodeURIComponent(timeframes)}` +
          `${range}&method=${method}&breakOn=${breakOn}&inducement=${inducement}` +
          `&zones=${zones}&fvgMinSize=${fvgMinSize}&standingZonesOnly=${standingZonesOnly}` +
          `&includeLive=${includeLive}`,
      ),
    enabled: !!symbol,
    staleTime: 30_000,
    refetchInterval: includeLive ? POLL_SLOW : false,
  })
}

// ---------- Strategies ----------

// Halved once the tick write path stopped being the bottleneck (a tick cost
// ~30ms to store and now costs ~2ms), so the run view can be asked for a
// fresh mark twice as often without crowding the run's lock. The LTP column
// itself no longer waits for this — it re-prices off the live quote cache.
const POLL_LIVE_VIEW = 1_000
const POLL_RUNNER_LOGS = 3_000

/**
 * An API build from before multi-instance answers with the flat single-run
 * fields only. The run list is rebuilt from them so the Live runner still
 * shows that one run (and its last exit) against such a backend.
 */
function legacyActiveRuns(s: Partial<StrategyListItem>): StrategyActiveRun[] {
  if (Array.isArray(s.activeRuns)) return s.activeRuns
  if (!s.isActive || s.runId == null || !s.underlying) return []
  return [
    {
      runId: s.runId,
      underlying: s.underlying,
      spotSymbol: s.spotSymbol ?? '',
      lots: s.lots ?? 0,
      stopLoss: s.stopLoss ?? null,
      target: s.target ?? null,
      startedBy: s.startedBy ?? '',
      startedUtc: s.startedUtc ?? '',
      processId: s.processId ?? 0,
    },
  ]
}

function legacyRecentExits(s: Partial<StrategyListItem>): StrategyLastExit[] {
  if (Array.isArray(s.recentExits)) return s.recentExits
  return s.lastExit ? [s.lastExit] : []
}

/**
 * An API build older than the catalog rewrite answers without the array
 * fields; default them so a stale backend degrades to empty chips instead of
 * crashing every strategy page on `.length`.
 */
function normalizeStrategy(s: Partial<StrategyListItem> & { id: number; name: string }): StrategyListItem {
  return {
    ...s,
    activeRuns: legacyActiveRuns(s),
    recentExits: legacyRecentExits(s),
    description: s.description ?? '',
    category: s.category ?? 'Other',
    supportedUnderlyings: s.supportedUnderlyings ?? [],
    instrumentKind: s.instrumentKind ?? 'options',
    legsSummary: s.legsSummary ?? '',
    dataRequirements: s.dataRequirements ?? [],
    defaultParametersJson: s.defaultParametersJson ?? '{}',
    defaultLots: s.defaultLots ?? 1,
    sourceFile: s.sourceFile ?? '',
    createdUtc: s.createdUtc ?? '',
    isActive: s.isActive ?? false,
    startedBy: s.startedBy ?? null,
    startedUtc: s.startedUtc ?? null,
    runId: s.runId ?? null,
    underlying: s.underlying ?? null,
    spotSymbol: s.spotSymbol ?? null,
    lots: s.lots ?? null,
    stopLoss: s.stopLoss ?? null,
    target: s.target ?? null,
    processId: s.processId ?? null,
    lastExit: s.lastExit ?? null,
  } as StrategyListItem
}

export function useStrategies({ enabled = true }: { enabled?: boolean } = {}) {
  return useQuery({
    queryKey: ['strategies'],
    queryFn: async () => {
      const rows = await api.get<Array<Partial<StrategyListItem> & { id: number; name: string }>>('/api/Strategy')
      return (Array.isArray(rows) ? rows : []).map(normalizeStrategy)
    },
    enabled,
    refetchInterval: POLL_FAST,
  })
}

/** Start a strategy on an underlying (paper). Body shape is the API contract. */
export function useStartStrategy() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ id, body }: { id: number; body: StartStrategyRequest }) =>
      api.post<StartStrategyResponse>(`/api/Strategy/${id}/start`, body),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['strategies'] })
      qc.invalidateQueries({ queryKey: ['strategy', 'live'] })
    },
  })
}

/**
 * Stop ONE run: square off every open position at the last mark and kill that
 * runner. Run-scoped on purpose — the same strategy may be live on other
 * underlyings, and those must keep trading.
 */
export function useStopStrategy() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ runId, flatten = true }: { runId: number; flatten?: boolean }) =>
      api.post<StopStrategyResponse>(`/api/Strategy/runs/${runId}/stop`, { flatten }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['strategies'] })
      qc.invalidateQueries({ queryKey: ['strategy', 'live'] })
      // The retained snapshot carries the runner's final lines.
      qc.invalidateQueries({ queryKey: ['strategy', 'logs'] })
    },
  })
}

export interface ClosePositionsResponse {
  message: string
  runId: number
  closed: number
  skipped: number[]
}

/**
 * Square off SOME of a run's open positions and leave it trading.
 *
 * Stopping is all-or-nothing; this is the smaller instrument, for a run holding
 * several positions where only one has gone wrong. The fills are the same ones
 * the risk guard makes when a leg stop trips, so the run's ledger and activity
 * read identically either way.
 */
export function useClosePositions() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ runId, positionIds }: { runId: number; positionIds: number[] }) =>
      api.post<ClosePositionsResponse>(`/api/Strategy/runs/${runId}/positions/close`, {
        positionIds,
      }),
    onSuccess: () => {
      // The run keeps running, so only its position-level views change.
      qc.invalidateQueries({ queryKey: ['strategy', 'live'] })
      qc.invalidateQueries({ queryKey: ['strategies'] })
      qc.invalidateQueries({ queryKey: ['positions'] })
    },
  })
}

export interface SetCarryForwardResponse {
  message: string
  runId: number
  positionId: number
  carryForward: boolean
  /** False when the position already had that tick. */
  changed: boolean
}

/**
 * Tick or untick "carry forward" on one open position (27 Sep: "if I want to
 * carry forward, there should be a tick there and ticking it is enough").
 * Only that run's live view changes; the change is also a CARRY_FORWARD row
 * in its activity, which the same view carries.
 */
export function useSetCarryForward() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ runId, positionId, carryForward }: { runId: number; positionId: number; carryForward: boolean }) =>
      api.put<SetCarryForwardResponse>(`/api/Strategy/runs/${runId}/positions/${positionId}/carry-forward`, {
        carryForward,
      }),
    onSuccess: (_data, { runId }) => {
      qc.invalidateQueries({ queryKey: ['strategy', 'live', runId] })
      qc.invalidateQueries({ queryKey: ['positions'] })
    },
  })
}

/**
 * Replace the risk rules of ONE running run. The guard picks the new rules up
 * on its next sweep and the run's activity gains a RISK_UPDATED row, so the
 * run's live view and the strategy list (which carries the overall
 * shorthand) are both refreshed.
 */
export function useUpdateRunRisk() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ runId, risk }: { runId: number; risk: UpdateRunRiskRequest }) =>
      api.patch<UpdateRunRiskResponse>(`/api/Strategy/runs/${runId}/risk`, risk),
    onSuccess: (_data, { runId }) => {
      qc.invalidateQueries({ queryKey: ['strategy', 'live', runId] })
      qc.invalidateQueries({ queryKey: ['strategies'] })
    },
  })
}

function liveViewQuery(runId: number, enabled: boolean) {
  return {
    queryKey: ['strategy', 'live', runId] as const,
    queryFn: () => api.get<StrategyLiveView>(`/api/Strategy/runs/${runId}/live`),
    enabled,
    // A finished run's view cannot change, so a stopped card is fetched once and
    // then left alone (the server marks-to-market on every call). A run id is
    // never reused, so a restart is simply a new key that polls from scratch.
    refetchInterval: (query: { state: { data?: StrategyLiveView } }) =>
      query.state.data && !query.state.data.isActive ? false : POLL_LIVE_VIEW,
  }
}

/** Position-based live view of one run (works for finished runs too). */
export function useStrategyLive(runId: number, enabled: boolean) {
  return useQuery(liveViewQuery(runId, enabled))
}

/**
 * Live views for a set of runs at once (page-level totals). Shares the cache
 * key with useStrategyLive, so a RunCard and the stat row never double fetch
 * the same run.
 */
export function useStrategyLives(runIds: number[]) {
  return useQueries({
    queries: runIds.map((runId) => liveViewQuery(runId, true)),
  })
}

/**
 * Runner stdout/stderr ring buffer of one run. The API keeps a snapshot of the
 * last lines after the process exits (bounded to the newest finished runs), so
 * a stopped run is fetched once — only a live run keeps polling.
 */
export function useStrategyLogs(runId: number, live: boolean) {
  return useQuery({
    queryKey: ['strategy', 'logs', runId],
    queryFn: () => api.get<string[]>(`/api/Strategy/runs/${runId}/logs?take=200`),
    refetchInterval: live ? POLL_RUNNER_LOGS : false,
  })
}

// ---------- Live run history ----------

const POLL_RUN_HISTORY_ACTIVE = 10_000

/** Query of GET /api/Strategy/runs; every field optional, dates are IST days ("yyyy-MM-dd"). */
export interface LiveRunHistoryFilters {
  /** Admin only — a trader always gets their own runs whatever is passed. */
  userId?: number | null
  strategyId?: number | null
  underlying?: string | null
  /** Running (includes Stopping) | Stopped | Failed | Completed | Pending; omitted or "any" = every status. */
  status?: string | null
  fromDate?: string | null
  toDate?: string | null
  /** Default 100, max 500. */
  take?: number
  skip?: number
}

function runHistoryQuery(filters: LiveRunHistoryFilters): string {
  const q = new URLSearchParams()
  if (filters.userId != null) q.set('userId', String(filters.userId))
  if (filters.strategyId != null) q.set('strategyId', String(filters.strategyId))
  if (filters.underlying) q.set('underlying', filters.underlying)
  if (filters.status && filters.status !== 'any') q.set('status', filters.status)
  if (filters.fromDate) q.set('fromDate', filters.fromDate)
  if (filters.toDate) q.set('toDate', filters.toDate)
  if (filters.take != null) q.set('take', String(filters.take))
  if (filters.skip != null && filters.skip > 0) q.set('skip', String(filters.skip))
  const s = q.toString()
  return s ? `?${s}` : ''
}

/**
 * Every live run matching the filters, newest first. A finished run never
 * changes, so the list is polled (every 10 s) only while at least one row is
 * still active — its status, P&L and duration are what move.
 */
export function useLiveRunHistory(filters: LiveRunHistoryFilters, enabled = true) {
  const query = runHistoryQuery(filters)
  return useQuery({
    queryKey: ['strategy', 'history', query],
    queryFn: () => api.get<LiveRunSummary[]>(`/api/Strategy/runs${query}`),
    enabled,
    // Changing a filter keeps the last list on screen instead of blanking the
    // table while the new one loads.
    placeholderData: keepPreviousData,
    refetchInterval: (q: { state: { data?: LiveRunSummary[] } }) =>
      q.state.data?.some((r) => r.isActive) ? POLL_RUN_HISTORY_ACTIVE : false,
  })
}

/** Rows one history request carries at most (the API's cap). */
export const RUN_HISTORY_PAGE = 500

/**
 * The same list, paged: page N is `skip = N × take`, newest first. The Run
 * history page uses it so a range holding more runs than one request may
 * carry (the API caps `take` at 500) still reaches its oldest runs through
 * "Load older" — `hasNextPage` stays true while the last page came back full.
 * Polls like `useLiveRunHistory` (every page) while any loaded row is active.
 */
export function useLiveRunHistoryPages(filters: Omit<LiveRunHistoryFilters, 'skip'>, enabled = true) {
  const take = Math.min(Math.max(filters.take ?? RUN_HISTORY_PAGE, 1), RUN_HISTORY_PAGE)
  const base: LiveRunHistoryFilters = { ...filters, take }
  const key = runHistoryQuery(base)
  return useInfiniteQuery({
    queryKey: ['strategy', 'history', 'pages', key],
    queryFn: ({ pageParam }) =>
      api.get<LiveRunSummary[]>(`/api/Strategy/runs${runHistoryQuery({ ...base, skip: pageParam })}`),
    initialPageParam: 0,
    // Every earlier page was full (that is the only way a next page is asked
    // for), so the next offset is simply pages × take.
    getNextPageParam: (lastPage: LiveRunSummary[], pages: LiveRunSummary[][]) =>
      lastPage.length >= take ? pages.length * take : undefined,
    enabled,
    placeholderData: keepPreviousData,
    refetchInterval: (q: { state: { data?: InfiniteData<LiveRunSummary[]> } }) =>
      q.state.data?.pages.some((page) => page.some((r) => r.isActive)) ? POLL_RUN_HISTORY_ACTIVE : false,
  })
}

/** Per-user rollup (runs, active, net P&L, last run) — every user for an admin, own row for a trader. */
export function useLiveRunUserSummary(enabled = true) {
  return useQuery({
    queryKey: ['strategy', 'history', 'summary'],
    queryFn: () => api.get<LiveRunUserSummary[]>('/api/Strategy/runs/summary'),
    enabled,
    refetchInterval: POLL_SLOW,
  })
}

/**
 * One strategy's lifetime live record — every run of it ever, rolled up. A
 * trader's covers their own runs; an admin's covers everyone's. Polled while
 * the strategy has a run going, because those are the numbers still moving;
 * once nothing is running the record only changes when a run is started.
 */
export function useStrategyTrackRecord(strategyId: number | null, enabled = true) {
  return useQuery({
    queryKey: ['strategy', 'track-record', strategyId],
    queryFn: () => api.get<StrategyTrackRecord>(`/api/Strategy/${strategyId}/track-record`),
    enabled: enabled && strategyId != null && Number.isInteger(strategyId) && strategyId > 0,
    refetchInterval: (q: { state: { data?: StrategyTrackRecord } }) =>
      (q.state.data?.activeRuns ?? 0) > 0 ? POLL_RUN_HISTORY_ACTIVE : false,
  })
}

/** Paper orders of one live run, newest first (fetched once; `live` re-polls while the run is active). */
export function useLiveRunOrders(runId: number, live = false, enabled = true) {
  return useQuery({
    queryKey: ['strategy', 'orders', runId],
    queryFn: () => api.get<PaperOrderRow[]>(`/api/Strategy/runs/${runId}/orders`),
    enabled,
    refetchInterval: live ? POLL_LIVE_VIEW : false,
  })
}

/** Underlyings with live option contracts in the instrument master. */
export function useFnoUnderlyings() {
  return useQuery({
    queryKey: ['derivatives', 'underlyings'],
    queryFn: () => api.get<FnoUnderlying[]>('/api/Instruments/derivatives/underlyings'),
    staleTime: 5 * 60_000,
  })
}

// ---------- Backtesting ----------

const POLL_BACKTEST_VIEW = 2_000
const POLL_BACKTEST_LIST_ACTIVE = 5_000
const POLL_BACKTEST_LIST_IDLE = 30_000

function isBacktestActive(status: string | undefined): boolean {
  return status === 'Running' || status === 'Pending'
}

/**
 * What the index has at each resolution for one underlying, plus what the
 * strategy needs — the dialog builds its resolution choices from this. One
 * answer per (underlying, strategy): the chosen driver resolution is not sent,
 * the dialog marks it as required itself, so toggling resolutions never
 * re-runs the coverage aggregate.
 */
/** Stocks with stored candles, for an equity strategy's underlying picker. */
export function useBacktestEquities(enabled: boolean) {
  return useQuery({
    queryKey: ['backtest', 'equities'],
    queryFn: () => api.get<BacktestEquity[]>('/api/Backtest/equities'),
    enabled,
    staleTime: 5 * 60_000,
  })
}

export function useBacktestCoverage(
  underlying: string | null,
  strategyId: number | null,
  instrumentKind?: string,
) {
  return useQuery({
    queryKey: ['backtest', 'coverage', underlying, strategyId, instrumentKind ?? ''],
    queryFn: () =>
      api.get<BacktestCoverageResponse>(
        `/api/Backtest/coverage?underlying=${encodeURIComponent(underlying!)}&strategyId=${strategyId}` +
          (instrumentKind ? `&instrumentKind=${encodeURIComponent(instrumentKind)}` : ''),
      ),
    enabled: !!underlying && strategyId != null,
    // Switching underlying keeps the last answer on screen instead of
    // blanking the picker while the new one loads.
    placeholderData: keepPreviousData,
    staleTime: 30_000,
  })
}

/** Pull index candles from FYERS for a set of resolutions, in 30-day chunks. */
export function useBacktestBackfill() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (input: BacktestBackfillRequest) =>
      api.post<BacktestBackfillResponse>('/api/Backtest/backfill', input),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['backtest', 'coverage'] })
      qc.invalidateQueries({ queryKey: ['coverage'] })
      qc.invalidateQueries({ queryKey: ['candles'] })
    },
  })
}

export function useStartBacktest() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (body: StartBacktestRequest) =>
      api.post<StartBacktestResponse>('/api/Backtest/runs', body),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['backtest', 'runs'] }),
  })
}

export function useStopBacktest() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api.post<{ message: string }>(`/api/Backtest/runs/${id}/stop`),
    onSuccess: (_data, id) => {
      qc.invalidateQueries({ queryKey: ['backtest', 'runs'] })
      qc.invalidateQueries({ queryKey: ['backtest', 'run', id] })
    },
  })
}

export function useDeleteBacktest() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api.delete<null>(`/api/Backtest/runs/${id}`),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['backtest', 'runs'] })
    },
    // The run query is dropped only after the caller's own onSuccess (which
    // navigates away) has run, so a still-mounted run page does not re-create
    // it and poll a deleted id.
    onSettled: (_data, _error, id) => {
      qc.removeQueries({ queryKey: ['backtest', 'run', id] })
    },
  })
}

/** All OfflineReplay runs, newest first; polls fast only while one is running. */
export function useBacktestRuns() {
  return useQuery({
    queryKey: ['backtest', 'runs'],
    queryFn: () => api.get<BacktestRunSummary[]>('/api/Backtest/runs'),
    refetchInterval: (query: { state: { data?: BacktestRunSummary[] } }) =>
      query.state.data?.some((r) => isBacktestActive(r.status))
        ? POLL_BACKTEST_LIST_ACTIVE
        : POLL_BACKTEST_LIST_IDLE,
  })
}

/**
 * The full results view of one run; polls every 2 s until it is finished.
 * A run that cannot be loaded at all (404 for a deleted or mistyped id, an
 * outage before the first answer) is not polled — the page shows the error
 * and a reload retries; a transient error on a run already on screen keeps
 * the poll going while that run is still active.
 */
export function useBacktestRun(id: number | null) {
  return useQuery({
    queryKey: ['backtest', 'run', id],
    queryFn: () => api.get<BacktestRunView>(`/api/Backtest/runs/${id}`),
    enabled: id != null,
    refetchInterval: (query: { state: { data?: BacktestRunView; status: string } }) => {
      const { data, status } = query.state
      if (!data) return status === 'error' ? false : POLL_BACKTEST_VIEW
      return isBacktestActive(data.status) ? POLL_BACKTEST_VIEW : false
    },
  })
}

/**
 * Runner stdout/stderr: the live ring buffer while the process runs, its
 * final snapshot for the most recently finished runs. Fetched while `enabled`;
 * re-polled every 3 s only while `live` (a finished run's snapshot never changes).
 */
export function useBacktestLogs(id: number | null, enabled: boolean, live: boolean = enabled) {
  return useQuery({
    queryKey: ['backtest', 'logs', id],
    queryFn: () => api.get<string[]>(`/api/Backtest/runs/${id}/logs?take=200`),
    enabled: enabled && id != null,
    refetchInterval: live ? POLL_RUNNER_LOGS : false,
  })
}

// ---------- Risk / session / system ----------

export function useKillSwitch() {
  return useQuery({
    queryKey: ['risk', 'killswitch'],
    queryFn: () => api.get<KillSwitchState>('/api/Risk/killswitch/status'),
    refetchInterval: POLL_FAST,
  })
}

export function useSetKillSwitch() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (input: { activate: boolean; reason: string }) =>
      api.post<{ message: string }>(
        `/api/Risk/killswitch/${input.activate ? 'activate' : 'deactivate'}?reason=${encodeURIComponent(input.reason)}`,
      ),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['risk'] }),
  })
}

const riskLimitsQuery = {
  queryKey: ['risk', 'limits'] as const,
  queryFn: () => api.get<RiskLimits>('/api/Risk/limits'),
}

export function useRiskLimits() {
  return useQuery({ ...riskLimitsQuery, refetchInterval: POLL_FAST })
}

export function useUpdateRiskLimits() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (limits: Omit<RiskLimits, 'source' | 'updatedBy' | 'updatedUtc'>) =>
      api.post<RiskLimits>('/api/Risk/limits', limits),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['risk', 'limits'] }),
  })
}

export function useRiskEvents(limit = 100) {
  return useQuery({
    queryKey: ['risk', 'events', limit],
    queryFn: () => api.get<RiskEvent[]>(`/api/Risk/events?limit=${limit}`),
    refetchInterval: POLL_FAST,
  })
}

export function useAlertEvents(limit = 100) {
  return useQuery({
    queryKey: ['alerts', 'events', limit],
    queryFn: () => api.get<AlertEvent[]>(`/api/Alerts/events?limit=${limit}`),
    refetchInterval: POLL_FAST,
  })
}

export function useMarketSession(exchange = 'NSE', segment = 'CM') {
  return useQuery({
    queryKey: ['session', exchange, segment],
    queryFn: () =>
      api.get<MarketSessionInfo>(`/api/MarketSession/check?exchange=${exchange}&segment=${segment}`),
    refetchInterval: 30_000,
  })
}

export function useBrokerSession() {
  return useQuery({
    queryKey: ['broker', 'session'],
    queryFn: () => api.get<BrokerSessionInfo>('/api/Auth/session'),
    refetchInterval: 60_000,
  })
}

// ---------- Users (admin) ----------

export function useRegisterUser() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (input: { userName: string; email: string; password: string }) =>
      api.post('/api/UserAuth/register', input),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['users'] }),
  })
}

// ---------- Backfill (admin) ----------

export function useBackfillHistory() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (input: {
      symbol: string
      resolution: string
      fromDate: string
      toDate: string
    }) => api.post<BackfillHistoryResponse>('/api/Backfill/history', input),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['candles'] })
      qc.invalidateQueries({ queryKey: ['coverage'] })
    },
  })
}

// ---------- Market intelligence ----------

import type { EquityGroup, MoversResponse, NewsCategory, NewsResponse } from './types'

/**
 * The news tabs the server offers. It changes only when a category is added,
 * so it is fetched once and kept — the headlines behind each tab are what
 * refresh, not the list of tabs.
 */
export function useMarketNewsCategories() {
  return useQuery({
    queryKey: ['news', 'categories'],
    queryFn: () => api.get<NewsCategory[]>('/api/MarketIntel/news/categories'),
    staleTime: 60 * 60_000,
  })
}

export function useMarketNews(category: string | null) {
  return useQuery({
    queryKey: ['news', category],
    queryFn: () => api.get<NewsResponse>(`/api/MarketIntel/news?category=${encodeURIComponent(category!)}`),
    enabled: category != null,
    refetchInterval: 5 * 60_000,
    staleTime: 4 * 60_000,
  })
}

export function useTopMovers(group: string | null, top = 10) {
  return useQuery({
    queryKey: ['movers', group, top],
    queryFn: () =>
      api.get<MoversResponse>(
        `/api/MarketIntel/movers?group=${encodeURIComponent(group!)}&top=${top}`,
      ),
    enabled: !!group,
    refetchInterval: 5 * 60_000,
    staleTime: 4 * 60_000,
  })
}

export function useEquityGroups() {
  return useQuery({
    queryKey: ['equity-groups'],
    queryFn: () => api.get<EquityGroup[]>('/api/Equities/groups'),
    staleTime: 10 * 60_000,
  })
}

// ---------- Data coverage (the chartable-data inventory) ----------

export interface CoverageRow {
  symbol: string
  resolution: string
  fromUtc: string
  toUtc: string
  barCount: number
  source: 'backfill' | 'live'
}

export function useDataCoverage() {
  return useQuery({
    queryKey: ['coverage'],
    queryFn: () => api.get<CoverageRow[]>('/api/MarketData/coverage'),
    refetchInterval: 60_000,
  })
}

// ---------- Telegram Alerter ----------

/** GET /api/Alerts/status: the signal alerter's processes, and whether Telegram delivery is set up. */
export interface AlerterStatus {
  isRunning: boolean
  startedUtc: string | null
  managed: boolean
  processes: { underlying: string; processId: number | null; source: string; startedUtc: string | null }[]
  telegramConfigured: boolean
}

export function useAlerterStatus() {
  return useQuery({
    queryKey: ['alerts', 'status'],
    queryFn: () => api.get<AlerterStatus>('/api/alerts/status'),
    refetchInterval: 3000,
  })
}

export function useStartAlerter() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: () => api.post<{ message: string }>('/api/alerts/start'),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['alerts', 'status'] }),
  })
}

// ---------- Data module v2 ----------

export interface OptionsBackfillRequest {
  exchange: string
  underlying: string
  expiryDate?: string
  strikeCountEachSide: number
  strikeStep: number
  resolution: string
  fromUtc: string
  toUtc: string
  includeCalls: boolean
  includePuts: boolean
}

export interface OptionsBackfillResponse {
  message?: string
  [key: string]: unknown
}

/** Backfill candles for an ATM±N option-chain window around an underlying. */
export function useOptionsBackfill() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (input: OptionsBackfillRequest) =>
      api.post<OptionsBackfillResponse>('/api/Options/history/backfill', input),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['candles'] })
      qc.invalidateQueries({ queryKey: ['coverage'] })
    },
  })
}

/** Bulk-add every member of an equity group to the live watchlist. */
export function useAddEquityGroupToWatchlist() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (input: { groupName: string; dataType: string }) =>
      api.post<{ groupName: string; totalMemberResolved: number; upserted: number; skipped: number }>(
        '/api/Equities/live/watchlist/group',
        { ...input, onlyEnabledMembers: true },
      ),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['watchlist'] }),
  })
}

export function useStopAlerter() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: () => api.post<{ message: string }>('/api/alerts/stop'),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['alerts', 'status'] }),
  })
}

export function useRiskExposure() {
  return useQuery({
    queryKey: ['risk', 'exposure'],
    queryFn: () => api.get<RiskExposureResponse>('/api/Risk/exposure'),
    refetchInterval: POLL_FAST,
  })
}

// ---------- Connectors (data vendors and brokers) ----------

export function useProviders() {
  return useQuery({
    queryKey: ['providers', 'list'],
    queryFn: () => api.get<import('./types').Provider[]>('/api/Providers'),
    refetchInterval: 60_000,
  })
}

export function useProviderBindings() {
  return useQuery({
    queryKey: ['providers', 'bindings'],
    queryFn: () => api.get<import('./types').ProviderBinding[]>('/api/Providers/bindings'),
  })
}

export function useSaveProviderCredentials() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({
      providerKey,
      ...body
    }: {
      providerKey: string
      clientId: string
      secretKey: string
      redirectUri: string
    }) => api.put<{ message: string }>(`/api/Providers/${providerKey}/credentials`, body),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['providers'] })
      qc.invalidateQueries({ queryKey: ['broker'] })
    },
  })
}

export function useDisconnectProvider() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (providerKey: string) =>
      api.post<{ message: string }>(`/api/Providers/${providerKey}/disconnect`),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['providers'] })
      qc.invalidateQueries({ queryKey: ['broker'] })
    },
  })
}

/** What the platform is taking from one connector, item by item. Polled: it is the live picture. */
export function useProviderUsage(providerKey: string, enabled = true) {
  return useQuery({
    queryKey: ['providers', 'usage', providerKey],
    queryFn: () => api.get<import('./types').ProviderUsage>(`/api/Providers/${providerKey}/usage`),
    enabled: enabled && providerKey.length > 0,
    refetchInterval: 10_000,
  })
}

/**
 * The market-movers snapshot: gainers, losers, OI build-up and PCR in one
 * answer. The API caches it for 45 s (six SmartAPI calls go into it), so this
 * polls at the same pace rather than faster.
 */
export function useAngelMovers(expiry = 'NEAR') {
  return useQuery({
    queryKey: ['angel', 'movers', expiry],
    queryFn: () => api.get<import('./movers').MoversSnapshot>(`/api/Angel/movers?expiry=${expiry}`),
    refetchInterval: 45_000,
  })
}

/** Angel One's own status: what is configured, and whether a session is held. */
export function useAngelStatus(enabled = true) {
  return useQuery({
    queryKey: ['angel', 'status'],
    queryFn: () => api.get<import('./angel').AngelStatus>('/api/Angel/status'),
    enabled,
    refetchInterval: 30_000,
  })
}

/** Sign in and price one instrument — the proof that the credentials and the IP are right. */
export function useAngelTest() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (body?: { forceLogin?: boolean }) =>
      api.post<import('./angel').AngelTestResult>('/api/Angel/test', body ?? {}),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['angel', 'status'] }),
  })
}

/** Dhan's automatic PIN + TOTP sign-in: set up or not, and what it last did. */
export function useDhanAutoSignIn(enabled = true) {
  return useQuery({
    queryKey: ['dhan', 'auto-sign-in'],
    queryFn: () => api.get<import('./dhanSignIn').DhanAutoSignInStatus>('/api/Dhan/auto-sign-in'),
    enabled,
    refetchInterval: 30_000,
  })
}

/** Sign Dhan in now with the PIN and a TOTP code; the session and usage panels follow. */
export function useDhanSignInNow() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: () => api.post<import('./dhanSignIn').DhanSignInNowResult>('/api/Dhan/auto-sign-in?trigger=console', {}),
    onSettled: () => {
      qc.invalidateQueries({ queryKey: ['dhan', 'auto-sign-in'] })
      qc.invalidateQueries({ queryKey: ['providers'] })
    },
  })
}

export function useTestProvider() {
  return useMutation({
    mutationFn: (providerKey: string) =>
      api.post<import('./types').ProviderTestResult>(`/api/Providers/${providerKey}/test`),
  })
}

// ---------- Data vendors added from the console ----------

export function useDataVendors() {
  return useQuery({
    queryKey: ['providers', 'vendors'],
    queryFn: () => api.get<import('./types').DataVendor[]>('/api/Providers/vendors'),
  })
}

export function useCreateDataVendor() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (input: import('./types').SaveDataVendorInput) =>
      api.post<{ message: string }>('/api/Providers/vendors', input),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['providers'] }),
  })
}

export function useDeleteDataVendor() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api.delete<{ message: string }>(`/api/Providers/vendors/${id}`),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['providers'] }),
  })
}

// ---------- Users v2 (admin) ----------

export function useUserAccounts() {
  return useQuery({
    queryKey: ['users', 'accounts'],
    queryFn: () => api.get<import('./types').UserAdmin[]>('/api/Users'),
  })
}

export function usePlatformModules() {
  return useQuery({
    queryKey: ['users', 'modules'],
    queryFn: () => api.get<import('./types').PlatformModuleInfo[]>('/api/Users/modules'),
    staleTime: Infinity,
  })
}

export function useUserRoles() {
  return useQuery({
    queryKey: ['users', 'roles'],
    queryFn: () => api.get<string[]>('/api/Users/roles'),
    staleTime: Infinity,
  })
}

export function useUpdateUser() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ id, ...input }: { id: number } & import('./types').UpdateUserInput) =>
      api.patch<import('./types').UserAdmin>(`/api/Users/${id}`, input),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['users'] }),
  })
}

export function useSetUserGrants() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ id, moduleKeys }: { id: number; moduleKeys: string[] }) =>
      api.put<import('./types').UserAdmin>(`/api/Users/${id}/grants`, { moduleKeys }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['users'] }),
  })
}

export function useResetUserPassword() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ id, newPassword }: { id: number; newPassword: string }) =>
      api.post<{ message: string }>(`/api/Users/${id}/password`, { newPassword }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['users'] }),
  })
}

export function useRevokeUserSessions() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api.post<{ message: string }>(`/api/Users/${id}/revoke-sessions`),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['users'] }),
  })
}

// ---------- The trader's account at the simulated broker ----------
//
// The platform issues each trader an account at the simulated broker and reads
// it back through the back office, so these are reads of the broker's own
// numbers — never a balance the console kept for itself.

export function useTraderSimBroker() {
  return useQuery({
    queryKey: ['trader', 'simbroker'],
    queryFn: () => api.get<import('./types').TraderSimBrokerResponse>('/api/Trader/simbroker'),
    refetchInterval: 15_000,
  })
}

/** The trader's own credentials. A mutation, because asking is a deliberate act. */
export function useTraderSimBrokerCredentials() {
  return useMutation({
    mutationFn: () => api.post<import('./types').SimBrokerCredentials>('/api/Trader/simbroker/credentials', {}),
  })
}

// ---------- Traders' broker accounts, from the admin side ----------

export function useSimBrokerAccount(userId: number, enabled: boolean) {
  return useQuery({
    queryKey: ['simbroker', 'accounts', userId],
    queryFn: () => api.get<import('./types').SimBrokerAccountResponse>(`/api/SimBroker/accounts/${userId}`),
    enabled,
    refetchInterval: 20_000,
    // A broker that cannot be reached is worth saying once, not four times.
    retry: false,
  })
}

export function useIssueSimBrokerAccount() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ userId, openingFunds, name }: { userId: number; openingFunds: number; name?: string }) =>
      api.post<import('./types').SimBrokerAccountLink>(`/api/SimBroker/accounts/${userId}`, { openingFunds, name }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['simbroker'] }),
  })
}

/** Money in, or out with a negative amount. */
export function useSimBrokerFunds() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ userId, amount, reference }: { userId: number; amount: number; reference?: string }) =>
      api.post<import('./types').SimBrokerFunds>(`/api/SimBroker/accounts/${userId}/funds`, { amount, reference }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['simbroker'] }),
  })
}

export function useSimBrokerKillSwitch() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ userId, active, squareOff }: { userId: number; active: boolean; squareOff: boolean }) =>
      api.post<import('./types').SimBrokerKillSwitch>(`/api/SimBroker/accounts/${userId}/kill-switch`, { active, squareOff }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['simbroker'] }),
  })
}

export function useRevealSimBrokerCredentials() {
  return useMutation({
    mutationFn: (userId: number) =>
      api.post<import('./types').SimBrokerCredentials>(`/api/SimBroker/accounts/${userId}/credentials`, {}),
  })
}

/** The market at a glance — indices, large caps, commodities — the same for everyone. */
export function useMarketPulse() {
  return useQuery({
    queryKey: ['market', 'pulse'],
    queryFn: () => api.get<import('./types').MarketPulseResponse>('/api/MarketPulse'),
    refetchInterval: POLL_FAST,
  })
}

const myWatchlistQuery = {
  queryKey: ['watchlist', 'me'] as const,
  queryFn: () => api.get<import('./types').MyWatchlistItem[]>('/api/Watchlist/me'),
}

export function useMyWatchlist() {
  return useQuery({ ...myWatchlistQuery, refetchInterval: POLL_FAST })
}

export function useAddToMyWatchlist() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (symbol: string) => api.post<{ message: string }>('/api/Watchlist/me', { symbol }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['watchlist', 'me'] }),
  })
}

export function useRemoveFromMyWatchlist() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (symbol: string) =>
      api.delete<{ message: string }>(`/api/Watchlist/me/${encodeURIComponent(symbol)}`),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['watchlist', 'me'] }),
  })
}

export function useResetMyWatchlist() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: () => api.post<{ message: string }>('/api/Watchlist/me/reset'),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['watchlist', 'me'] }),
  })
}

// ---------- Strategy packages (admin) ----------

export function useStrategyPackages() {
  return useQuery({
    queryKey: ['packages', 'list'],
    queryFn: () => api.get<import('./types').StrategyPackage[]>('/api/StrategyPackages'),
  })
}

export function useStrategyCatalogNames() {
  return useQuery({
    queryKey: ['packages', 'catalog'],
    queryFn: () =>
      api.get<import('./types').StrategyCatalogName[]>('/api/StrategyPackages/catalog'),
    staleTime: 60_000,
  })
}

export function useCreateStrategyPackage() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (input: import('./types').SaveStrategyPackageInput) =>
      api.post<import('./types').StrategyPackage>('/api/StrategyPackages', input),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['packages'] }),
  })
}

export function useUpdateStrategyPackage() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ id, ...input }: { id: number } & import('./types').SaveStrategyPackageInput) =>
      api.put<import('./types').StrategyPackage>(`/api/StrategyPackages/${id}`, input),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['packages'] }),
  })
}

export function useSetPackageStrategies() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ id, strategyNames }: { id: number; strategyNames: string[] }) =>
      api.put<import('./types').StrategyPackage>(`/api/StrategyPackages/${id}/strategies`, {
        strategyNames,
      }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['packages'] }),
  })
}

export function useDeleteStrategyPackage() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api.delete<{ message: string }>(`/api/StrategyPackages/${id}`),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['packages'] })
      qc.invalidateQueries({ queryKey: ['users'] })
    },
  })
}

export function useSetUserStrategyGrants() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ id, strategyNames }: { id: number; strategyNames: string[] }) =>
      api.put<{ message: string }>(`/api/Users/${id}/strategy-grants`, { strategyNames }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['users'] }),
  })
}

// ---------- Invites ----------

export function useInvites() {
  return useQuery({
    queryKey: ['invites'],
    queryFn: () => api.get<import('./types').UserInvite[]>('/api/Invites'),
  })
}

export function useCreateInvite() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (input: {
      email: string
      suggestedUserName?: string
      moduleKeys: string[]
      strategyPackageId: number | null
      validDays: number
    }) => api.post<import('./types').CreatedInvite>('/api/Invites', input),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['invites'] }),
  })
}

export function useRevokeInvite() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api.post<{ message: string }>(`/api/Invites/${id}/revoke`),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['invites'] }),
  })
}

// ---------- Activity log (admin) ----------

export interface ActivityLogFilters {
  userId?: number | null
  module?: string
  action?: string
  succeeded?: boolean
  search?: string
  limit?: number
}

export function useActivityLog(filters: ActivityLogFilters) {
  const params = new URLSearchParams()
  if (filters.userId != null) params.set('userId', String(filters.userId))
  if (filters.module) params.set('module', filters.module)
  if (filters.action) params.set('action', filters.action)
  if (filters.succeeded != null) params.set('succeeded', String(filters.succeeded))
  if (filters.search) params.set('search', filters.search)
  params.set('limit', String(filters.limit ?? 200))

  return useQuery({
    queryKey: ['activity', 'log', params.toString()],
    queryFn: () => api.get<import('./types').ActivityLogPage>(`/api/ActivityLog?${params}`),
    refetchInterval: 15_000,
  })
}

export function useActivityFacets() {
  return useQuery({
    queryKey: ['activity', 'facets'],
    queryFn: () => api.get<import('./types').ActivityLogFacets>('/api/ActivityLog/facets'),
    refetchInterval: 30_000,
  })
}

export function useActivityUserSummary(userId: number | null) {
  return useQuery({
    queryKey: ['activity', 'user', userId],
    queryFn: () =>
      api.get<import('./types').ActivityUserSummary>(`/api/ActivityLog/users/${userId}/summary`),
    enabled: userId != null,
  })
}


// --- option chain ------------------------------------------------------------

/** One strike through the session — the OI-change curves. */
export function useOptionChainSeries(
  underlying: string,
  strike: number | null,
  expiry?: string,
  toUtc?: string,
) {
  return useQuery({
    queryKey: ['optionChainSeries', underlying, strike, expiry ?? null, toUtc ?? null],
    queryFn: () => {
      const params = new URLSearchParams({ underlying, strike: String(strike) })
      if (expiry) params.set('expiry', expiry)
      if (toUtc) params.set('toUtc', toUtc)
      return api.get<OptionChainSeries>(`/api/OptionChain/series?${params}`)
    },
    enabled: Boolean(underlying) && strike != null && strike > 0,
    refetchInterval: toUtc ? false : POLL_SLOW,
    placeholderData: keepPreviousData,
  })
}

export function useOptionChainExpiries(underlying: string) {
  return useQuery({
    queryKey: ['optionChainExpiries', underlying],
    queryFn: () => api.get<string[]>(`/api/OptionChain/expiries?underlying=${encodeURIComponent(underlying)}`),
    enabled: Boolean(underlying),
  })
}

/**
 * The live chain view: the newest capture with fresh live quotes overlaid and
 * the header strip. Polls every 3 s while the underlying's market is open and
 * every 60 s otherwise; a replay (`asOfUtc`) never changes, so it never polls.
 */
/**
 * Open paper positions on the underlying's contracts — live strategy legs and
 * manual trades, never a backtest's — polled with the chain so a new fill
 * shows within seconds.
 */
function chainPositionsQuery(underlying: string) {
  return {
    queryKey: ['optionChainPositions', underlying] as const,
    queryFn: () =>
      api.get<import('./types').OptionChainPosition[]>(
        `/api/OptionChain/positions?${new URLSearchParams({ underlying, mode: 'LivePaper' })}`,
      ),
    enabled: Boolean(underlying),
  }
}

export function useOptionChainPositions(underlying: string, live: boolean) {
  return useQuery({
    ...chainPositionsQuery(underlying),
    refetchInterval: live ? 3_000 : 30_000,
    placeholderData: keepPreviousData,
  })
}

function chainViewQuery(underlying: string, expiry?: string, asOfUtc?: string) {
  return {
    queryKey: ['optionChainView', underlying, expiry ?? null, asOfUtc ?? null] as const,
    queryFn: () => {
      const params = new URLSearchParams({ underlying })
      if (expiry) params.set('expiry', expiry)
      if (asOfUtc) params.set('asOfUtc', asOfUtc)
      return api.get<OptionChain>(`/api/OptionChain/view?${params}`)
    },
    enabled: Boolean(underlying),
  }
}

export function useOptionChainView(underlying: string, expiry?: string, asOfUtc?: string) {
  return useQuery({
    ...chainViewQuery(underlying, expiry, asOfUtc),
    refetchInterval: (query) => (asOfUtc ? false : query.state.data?.header?.marketOpen ? 3_000 : 60_000),
    placeholderData: keepPreviousData,
  })
}

/** The session's trend — spot, total OI and PCR per capture. */
export function useOptionChainTrend(underlying: string, expiry?: string, toUtc?: string, marketOpen?: boolean) {
  return useQuery({
    queryKey: ['optionChainTrend', underlying, expiry ?? null, toUtc ?? null],
    queryFn: () => {
      const params = new URLSearchParams({ underlying })
      if (expiry) params.set('expiry', expiry)
      if (toUtc) params.set('toUtc', toUtc)
      return api.get<import('./types').OptionChainTrend>(`/api/OptionChain/trend?${params}`)
    },
    enabled: Boolean(underlying),
    // Captures arrive once a minute; asking more often only repeats the answer.
    refetchInterval: toUtc ? false : marketOpen ? 30_000 : 120_000,
    placeholderData: keepPreviousData,
  })
}


// --- option chain poller -----------------------------------------------------
//
// Open interest enters the platform through this process and nowhere else, and
// it cannot be backfilled — so it needs to be as easy to start as the ingestor,
// and as obvious when it is not running.

export function useChainPollerStatus() {
  return useQuery({
    queryKey: ['chainPoller', 'status'],
    queryFn: () => api.get<ChainPollerStatus>('/api/OptionChain/poller/status'),
    refetchInterval: POLL_SLOW,
  })
}

export function useStartChainPoller() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: () => api.post<{ message: string }>('/api/OptionChain/poller/start'),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['chainPoller'] }),
  })
}

export function useStopChainPoller() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: () => api.post<{ message: string }>('/api/OptionChain/poller/stop'),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['chainPoller'] }),
  })
}

export function useChainPollerLogs(take = 200) {
  return useQuery({
    queryKey: ['chainPoller', 'logs', take],
    queryFn: () => api.get<string[]>(`/api/OptionChain/poller/logs?take=${take}`),
    refetchInterval: POLL_SLOW,
  })
}

// ---------------------------------------------------------------------------
// Symbol masters (Data module)
// ---------------------------------------------------------------------------

/**
 * The FYERS symbol masters and the refresh job. Polls every 3 s while a
 * refresh runs so the page follows it master by master; the job is
 * server-side and survives the tab being closed.
 */
export function useInstrumentMasters() {
  return useQuery({
    queryKey: ['instruments', 'masters'],
    queryFn: () => api.get<InstrumentMastersResponse>('/api/Instruments/masters'),
    refetchInterval: (query) => (query.state.data?.job.isRunning ? 3_000 : false),
    staleTime: 10_000,
  })
}

/** Download the latest masters from FYERS and import them (admin). 409 when one is already running. */
export function useRefreshInstrumentMasters() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: () => api.post<{ message: string }>('/api/Instruments/masters/refresh', {}),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['instruments'] })
    },
  })
}

// ---------------------------------------------------------------------------
// Market factors: participant positioning, futures build-up, global cues, events.
// ---------------------------------------------------------------------------

/** FII/DII cash and participant-wise index positions; NSE posts them once each evening. */
export function useMarketFlows(days = 20) {
  return useQuery({
    queryKey: ['marketFactors', 'flows', days],
    queryFn: () => api.get<import('./factors').MarketFlows>(`/api/MarketFactors/flows?days=${days}`),
    refetchInterval: 300_000,
  })
}

/** Index futures build-up, daily and live against the last close; the live part moves with the feed. */
export function useMarketFutures(days = 10) {
  return useQuery({
    queryKey: ['marketFactors', 'futures', days],
    queryFn: () => api.get<import('./factors').MarketFutures>(`/api/MarketFactors/futures?days=${days}`),
    refetchInterval: 15_000,
  })
}

/** GIFT Nifty and overseas markets; the API shares one snapshot and refreshes it every two minutes. */
export function useGlobalCues() {
  return useQuery({
    queryKey: ['marketFactors', 'global'],
    queryFn: () => api.get<import('./factors').GlobalCues>('/api/MarketFactors/global'),
    refetchInterval: 120_000,
  })
}

export function useMarketEvents(from?: string, to?: string) {
  return useQuery({
    queryKey: ['marketFactors', 'events', from ?? null, to ?? null],
    queryFn: () => {
      const params = new URLSearchParams()
      if (from) params.set('from', from)
      if (to) params.set('to', to)
      const query = params.toString()
      return api.get<import('./factors').MarketEvents>(`/api/MarketFactors/events${query ? `?${query}` : ''}`)
    },
    refetchInterval: 600_000,
  })
}

export function useAddMarketEvent() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (body: import('./factors').SaveMarketEvent) =>
      api.post<import('./factors').MarketEvent>('/api/MarketFactors/events', body),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['marketFactors', 'events'] }),
  })
}

export function useDeleteMarketEvent() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api.delete<void>(`/api/MarketFactors/events/${id}`),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['marketFactors', 'events'] }),
  })
}

/** Fetch NSE's missing evening files now (admin). */
export function useSyncMarketFactors() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: () => api.post<{ lines: string[] }>('/api/MarketFactors/sync?sessions=20'),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['marketFactors', 'flows'] })
      qc.invalidateQueries({ queryKey: ['marketFactors', 'futures'] })
    },
  })
}

// ---------- Sentinel incidents (admin) ----------

import { historyQuery, incidentsQuery, readIncidentHistory, readIncidentList } from './incidents'
import type { IncidentFilters } from './incidents'
import type { IncidentNotes, IncidentSummary } from './types'

/**
 * What Sentinel found. Every 15 s: its health agent checks every 30 s and the
 * others every 60 s, so a new incident shows here within one of their checks.
 */
export function useIncidents(filters: IncidentFilters) {
  const qs = incidentsQuery(filters)
  return useQuery({
    queryKey: ['incidents', 'list', qs],
    queryFn: async () => readIncidentList(await api.get<unknown>(`/api/Incidents?${qs}`)),
    refetchInterval: 15_000,
  })
}

/** Live incidents counted by severity — the page header. */
export function useIncidentSummary() {
  return useQuery({
    queryKey: ['incidents', 'summary'],
    queryFn: () => api.get<IncidentSummary>('/api/Incidents/summary'),
    refetchInterval: 15_000,
  })
}

/**
 * "Someone is on it": the incident stays live, with a name and a time against it.
 *
 * The list is re-read on failure as well as success: the usual failure is a
 * 409 because Sentinel resolved the row first, and the row on screen should
 * show that now rather than keep offering buttons until the next poll.
 */
export function useAcknowledgeIncident() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api.post<unknown>(`/api/Incidents/${id}/acknowledge`),
    onSettled: () => qc.invalidateQueries({ queryKey: ['incidents'] }),
  })
}

/**
 * Closes it now, with what caused it and what was done when the form has them
 * (sent in the same request, so a refused resolve stores no notes either). If
 * the condition is still there, Sentinel opens a new incident on its next
 * check and alerts again. Re-reads on failure too (see
 * {@link useAcknowledgeIncident}).
 */
export function useResolveIncident() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ id, notes }: { id: number; notes?: IncidentNotes }) =>
      api.post<unknown>(`/api/Incidents/${id}/resolve`, notes),
    onSettled: () => qc.invalidateQueries({ queryKey: ['incidents'] }),
  })
}

/**
 * Writes or corrects an incident's root cause, what was done, and the fix
 * reference — open or resolved. Re-reads the list and the history either way:
 * a 409 means Sentinel changed the row while the form was open.
 */
export function useIncidentNotes() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ id, notes }: { id: number; notes: IncidentNotes }) =>
      api.post<unknown>(`/api/Incidents/${id}/notes`, notes),
    onSettled: () => qc.invalidateQueries({ queryKey: ['incidents'] }),
  })
}

/**
 * Every problem of the last `days` days, one row per fingerprint with its
 * episodes. Once a minute: it is for looking back, and a new episode is on the
 * live list first.
 */
export function useIncidentHistory(days: number) {
  const qs = historyQuery(days)
  return useQuery({
    queryKey: ['incidents', 'history', qs],
    queryFn: async () => readIncidentHistory(await api.get<unknown>(`/api/Incidents/history?${qs}`)),
    refetchInterval: 60_000,
  })
}

// ---------- Sentinel desk checkups (admin) ----------

import { WAIT_POLL_MS, checkupsQuery, readCheckupLatest, readCheckupList } from './checkup'
import type { CheckupDetail, CheckupRunAnswer } from './checkup'

/**
 * The newest finished checkup, the one in progress, and when the desk was last
 * checked. Once a minute; every 5 s while the page waits for a checkup it
 * asked for (`waiting`) or one is in progress, so the report shows as soon as
 * Sentinel writes it. Pending lasts ten minutes at most, so neither can poll
 * fast for long.
 */
export function useCheckupLatest(waiting = false) {
  return useQuery({
    queryKey: ['checkups', 'latest'],
    queryFn: async () => readCheckupLatest(await api.get<unknown>('/api/Checkups/latest')),
    refetchInterval: (query) => (waiting || query.state.data?.pending ? WAIT_POLL_MS : 60_000),
  })
}

/** The last `take` checkups, newest first, each with its items counted; at the latest's pace while one is on its way. */
export function useCheckups(take: number, fast = false) {
  const qs = checkupsQuery(take)
  return useQuery({
    queryKey: ['checkups', 'list', qs],
    queryFn: async () => readCheckupList(await api.get<unknown>(`/api/Checkups?${qs}`)),
    refetchInterval: fast ? WAIT_POLL_MS : 60_000,
  })
}

/**
 * One checkup in full, for the history's `?id=`; not fetched without an id. A
 * finished report never changes, so only one still in progress is re-read.
 */
export function useCheckup(id: number | null) {
  return useQuery({
    queryKey: ['checkups', 'detail', id],
    queryFn: () => api.get<CheckupDetail>(`/api/Checkups/${id}`),
    enabled: id != null,
    refetchInterval: (query) => {
      const status = query.state.data?.status
      return status === 'requested' || status === 'running' ? WAIT_POLL_MS : false
    },
  })
}

/**
 * Asks Sentinel for a checkup now. The answer is the checkup to wait for: a new
 * request, or the one already pending. Re-reads either way, so the page shows
 * the pending checkup at once.
 */
export function useRunCheckup() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: () => api.post<CheckupRunAnswer>('/api/Checkups/run'),
    onSettled: () => qc.invalidateQueries({ queryKey: ['checkups'] }),
  })
}

// ---------- Analysis: forecasts with proof ----------

import { forecastsQuery, readForecastList, readForecastModels, readScoreboard } from './analysis'
import type { ForecastFilters } from './analysis'

/**
 * `VITE_ANALYSIS_MOCK=1 npm run dev` answers the Forecasts endpoints from
 * lib/analysis.fixture.ts, so the Analysis page can be reviewed before the API
 * has them. `import.meta.env.DEV` is the literal `false` in a production
 * build, so this branch — and the fixture's chunk with it — is dropped there.
 */
const ANALYSIS_MOCK = import.meta.env.DEV && import.meta.env.VITE_ANALYSIS_MOCK === '1'

/**
 * A 404 means this server has no Forecasts API yet: retrying it only keeps
 * the page on "Reading…" for a few seconds before it can say so. Other
 * failures keep the app's usual two retries (401/403 never retry, as there).
 */
function forecastsRetry(failureCount: number, error: unknown): boolean {
  const status = (error as { status?: number } | null)?.status
  if (status === 401 || status === 403 || status === 404) return false
  return failureCount < 2
}

async function forecastsGet(path: string): Promise<unknown> {
  if (ANALYSIS_MOCK) {
    const { fixtureResponse } = await import('./analysis.fixture')
    return fixtureResponse(path)
  }
  return api.get<unknown>(path)
}

/**
 * Issued forecasts, newest session first. Every 60 s: forecasts arrive once
 * at 08:50 IST and are scored once after 15:50, so a minute is plenty.
 */
export function useForecasts(filters: ForecastFilters) {
  const qs = forecastsQuery(filters)
  return useQuery({
    queryKey: ['forecasts', 'list', qs],
    queryFn: async () => readForecastList(await forecastsGet(`/api/Forecasts${qs ? `?${qs}` : ''}`)),
    refetchInterval: 60_000,
    retry: forecastsRetry,
  })
}

/** Each model version's live record against its baseline, per index and across all. */
export function useForecastScoreboard() {
  return useQuery({
    queryKey: ['forecasts', 'scoreboard'],
    queryFn: async () => readScoreboard(await forecastsGet('/api/Forecasts/scoreboard')),
    refetchInterval: 60_000,
    retry: forecastsRetry,
  })
}

/** The registered model versions, with the backtest each was registered with. */
export function useForecastModels() {
  return useQuery({
    queryKey: ['forecasts', 'models'],
    queryFn: async () => readForecastModels(await forecastsGet('/api/Forecasts/models')),
    refetchInterval: 60_000,
    retry: forecastsRetry,
  })
}

// ---------- Deploys (admin) ----------

import type {
  DeployHistory,
  IntelAnnouncement,
  IntelBoardMeeting,
  IntelBreadthDay,
  IntelDailyBar,
  IntelHeadline,
  IntelPage,
  IntelSnapshot,
} from './types'

/**
 * What this machine did with the last pushes. Every 10 s on the Deployments
 * page, often enough that watching a deploy land feels live; the Desk, open
 * all day, asks once a minute.
 */
export function useDeployHistory(pollMs = 10_000, enabled = true) {
  return useQuery({
    queryKey: ['deploy', 'history'],
    queryFn: () => api.get<DeployHistory>('/api/Deploy/history?limit=20'),
    enabled,
    refetchInterval: pollMs,
  })
}

// ---------- Market intelligence (market-data grant) ----------

/**
 * What the market-intelligence recorders stored: headlines and filings (with
 * the local model's reading once scored), board meetings, the morning
 * snapshots of GIFT Nifty and overseas markets, their daily bars, and NSE
 * breadth. The API serves these reads to admins and to traders holding the
 * market-data grant; every hook takes `enabled`, so a console without the
 * grant never asks.
 */
const INTEL_TAKE = 60

/** Headlines first seen since `from` (an IST date), newest first. The recorder polls its feeds every few minutes. */
export function useIntelNews(from: string, enabled: boolean) {
  return useQuery({
    queryKey: ['intel', 'news', from],
    queryFn: () => api.get<IntelPage<IntelHeadline>>(`/api/MarketIntelligence/news?${new URLSearchParams({ from, take: String(INTEL_TAKE) })}`),
    enabled,
    refetchInterval: 120_000,
  })
}

/** NSE filings broadcast since `from`, newest first. */
export function useIntelAnnouncements(from: string, enabled: boolean) {
  return useQuery({
    queryKey: ['intel', 'announcements', from],
    queryFn: () =>
      api.get<IntelPage<IntelAnnouncement>>(`/api/MarketIntelligence/announcements?${new URLSearchParams({ from, take: String(INTEL_TAKE) })}`),
    enabled,
    refetchInterval: 120_000,
  })
}

/** Board meetings between two IST dates; announced days ahead, so read rarely. */
export function useIntelCalendar(from: string, to: string, enabled: boolean) {
  return useQuery({
    queryKey: ['intel', 'calendar', from, to],
    queryFn: () => api.get<IntelBoardMeeting[]>(`/api/MarketIntelligence/calendar?${new URLSearchParams({ from, to })}`),
    enabled,
    refetchInterval: 30 * 60_000,
  })
}

/** Every snapshot of one IST date. The recorder takes one every 15 minutes from 06:00, plus one at 08:45. */
export function useIntelSnapshots(date: string, enabled: boolean) {
  return useQuery({
    queryKey: ['intel', 'snapshots', date],
    queryFn: () => api.get<IntelSnapshot[]>(`/api/MarketIntelligence/global/snapshots?${new URLSearchParams({ date })}`),
    enabled,
    refetchInterval: 5 * 60_000,
  })
}

/** Overseas daily bars since `from`: the last close of a market no morning snapshot has priced yet. */
export function useIntelGlobalDaily(from: string, enabled: boolean) {
  return useQuery({
    queryKey: ['intel', 'global-daily', from],
    queryFn: () => api.get<IntelDailyBar[]>(`/api/MarketIntelligence/global/daily?${new URLSearchParams({ from })}`),
    enabled,
    refetchInterval: 30 * 60_000,
  })
}

/** NSE breadth per session since `from`; written once each evening from NSE's files. */
export function useIntelBreadth(from: string, enabled: boolean) {
  return useQuery({
    queryKey: ['intel', 'breadth', from],
    queryFn: () => api.get<IntelBreadthDay[]>(`/api/MarketIntelligence/breadth?${new URLSearchParams({ from })}`),
    enabled,
    refetchInterval: 30 * 60_000,
  })
}

// ---------- The Desk: the same questions, at the pace of a screen open all day ----------

/**
 * The Desk asks what the owning pages ask, under the same query keys, so a
 * visit to either finds the answer already there. It does not ask as often:
 * it stays open from before the open to after the close, and a level on the
 * chain or a leg's mark does not need the 1–3 s those pages use while someone
 * trades from them.
 */
const DESK_CHAIN_LIVE = 30_000
const DESK_LEGS_LIVE = 15_000
const DESK_IDLE = 5 * 60_000

/** The chain view's header (walls, PCR, max pain, IV, straddle) for each underlying. */
export function useDeskChainViews(underlyings: readonly string[], live: boolean) {
  return useQueries({
    queries: underlyings.map((u) => ({
      ...chainViewQuery(u),
      refetchInterval: live ? DESK_CHAIN_LIVE : DESK_IDLE,
      placeholderData: keepPreviousData,
    })),
  })
}

/** The platform limits (admin-only on the API). They change when an admin saves them, not with the market. */
export function useDeskRiskLimits(enabled: boolean) {
  return useQuery({ ...riskLimitsQuery, enabled, refetchInterval: DESK_IDLE })
}

/** The viewer's own watchlist, for "held and watched"; its symbols change by hand, not by the minute. */
export function useDeskWatchlist(enabled: boolean) {
  return useQuery({ ...myWatchlistQuery, enabled, refetchInterval: DESK_IDLE })
}

/** Today's one-minute bars for a small trace; the latest 375 cover a whole NSE session. */
export function useIntradayTrace(symbol: string, live: boolean) {
  return useQuery({
    queryKey: ['bars', symbol, 375],
    queryFn: () => api.get<LiveBar[]>(`/api/LiveData/bars?${new URLSearchParams({ symbol, take: '375' })}`),
    refetchInterval: live ? 60_000 : false,
  })
}

// ---------- The day's P&L series, open positions across books, the morning plan ----------

/**
 * One IST day of live runs' P&L, minute by minute (strategies grant; a
 * trader gets their own runs). The recorder writes once a minute, so a day
 * still being traded is read once a minute; a finished day never changes.
 */
export function useRunPnlSeries(date: string | null, live: boolean, enabled = true) {
  return useQuery({
    queryKey: ['strategy', 'pnl-series', date],
    queryFn: () => api.get<RunPnlSeriesResponse>(`/api/Strategy/runs/pnl-series?${new URLSearchParams({ date: date ?? '' })}`),
    enabled: enabled && date != null,
    refetchInterval: live ? 60_000 : false,
  })
}

/**
 * Every open leg the viewer may see, across runs and manual books, marked at
 * the live price (strategies grant; a trader gets their own). The Desk reads
 * it every 15 s in the session; the Positions page, where legs are closed
 * and ticked, every 5 s.
 */
export function useOpenPositions(pollMs: number | false, enabled = true) {
  return useQuery({
    queryKey: ['positions', 'open'],
    queryFn: () => api.get<OpenPositionsResponse>('/api/Positions/open'),
    enabled,
    refetchInterval: pollMs,
  })
}

/** How often the Desk reads the open legs: every 15 s in the session, every minute otherwise. */
export const deskLegsPoll = (live: boolean) => (live ? DESK_LEGS_LIVE : 60_000)

/**
 * The morning plan against what is live (admin). A 404 means there is no
 * plan file on the server, which asking again will not change.
 */
export function useDeskPlan(enabled: boolean) {
  return useQuery({
    queryKey: ['desk', 'plan'],
    queryFn: () => api.get<DeskPlanResponse>('/api/Desk/plan'),
    enabled,
    refetchInterval: 60_000,
    retry: (failures: number, error: unknown) => {
      const status = (error as { status?: number } | null)?.status
      return status !== 404 && status !== 401 && status !== 403 && failures < 2
    },
  })
}

/**
 * The order ledgers of several runs at once, under the same keys as one
 * run's (useLiveRunOrders), so a run's page and the Orders page share them.
 * A finished run's ledger is read once; a live one every 30 s, plenty for a
 * list a person reads, and cheap across two dozen runs.
 */
export function useRunsOrders(runs: ReadonlyArray<{ runId: number; live: boolean }>, enabled = true) {
  return useQueries({
    queries: runs.map(({ runId, live }) => ({
      queryKey: ['strategy', 'orders', runId],
      queryFn: () => api.get<PaperOrderRow[]>(`/api/Strategy/runs/${runId}/orders`),
      enabled,
      refetchInterval: live ? 30_000 : (false as const),
    })),
    combine: combineLedgers,
  })
}

/**
 * The ledgers as one value, rebuilt only when one of them changes, so a page
 * merging a few thousand orders does it once per answer, not once per render.
 */
function combineLedgers(results: ReadonlyArray<{ data?: PaperOrderRow[]; isError: boolean }>) {
  return {
    ledgers: results.map((r) => r.data),
    failed: results.map((r) => r.isError),
    loaded: results.filter((r) => r.data !== undefined).length,
  }
}
