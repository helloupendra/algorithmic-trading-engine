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

import { keepPreviousData, useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect } from 'react'
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import { api, API_BASE_URL } from './api'
import type {
  BackfillHistoryResponse,
  BacktestBackfillRequest,
  BacktestBackfillResponse,
  BacktestCoverageResponse,
  BacktestRunSummary,
  BacktestRunView,
  BrokerSessionInfo,
  CandleDto,
  DerivativeExpiry,
  EquitySnapshot,
  FnoUnderlying,
  IngestorStatus,
  Instrument,
  KillSwitchState,
  LiveBar,
  LiveQuote,
  LiveTick,
  LiveWatchlistItem,
  MarketSessionInfo,
  OptionChainItem,
  PaperOrder,
  PaperPosition,
  PerformanceMetrics,
  SimulationPortfolio,
  SimulationRun,
  SimulationSignal,
  StaleQuote,
  StartBacktestRequest,
  StartBacktestResponse,
  StartStrategyRequest,
  StartStrategyResponse,
  StopStrategyResponse,
  StrategyListItem,
  StrategyLiveView,
} from './types'
import type { MeResponse } from './api'

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

export function useLiveFeedSignalR() {
  const qc = useQueryClient()

  useEffect(() => {
    const connection = new HubConnectionBuilder()
      .withUrl(`${API_BASE_URL}/hubs/livefeed`)
      .configureLogging(LogLevel.Warning)
      .withAutomaticReconnect()
      .build()

    connection.on('ReceiveTick', (tick: any) => {
      qc.setQueryData(['quotes', 'all'], (old: LiveQuote[] | undefined) => {
        if (!old) return old
        // Avoid recreating array if tick symbol doesn't exist in quotes
        const exists = old.some(q => q.symbol === tick.symbol);
        if (!exists) return old;
        
        return old.map((q) => {
          if (q.symbol === tick.symbol) {
            return {
              ...q,
              lastTradedPrice: tick.lastTradedPrice ?? q.lastTradedPrice,
              bidPrice: tick.bidPrice ?? q.bidPrice,
              askPrice: tick.askPrice ?? q.askPrice,
              volume: tick.volume ?? q.volume,
              updatedUtc: tick.exchangeTimestampUtc ?? new Date().toISOString(),
            }
          }
          return q
        })
      })
    })

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

export function useIngestorProcessStatus() {
  return useQuery({
    queryKey: ['ingestor', 'process'],
    queryFn: () => api.get<{ isRunning: boolean }>('/api/Ingestor/status'),
    refetchInterval: POLL_SLOW,
  })
}

export function useStartIngestor() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: () => api.post<{ message: string }>('/api/Ingestor/start'),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['ingestor'] })
    },
  })
}

export function useStopIngestor() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: () => api.post<{ message: string }>('/api/Ingestor/stop'),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['ingestor'] })
    },
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

// ---------- Instruments & derivatives ----------

export function useInstrumentSearch(query: string, type?: string) {
  return useQuery({
    queryKey: ['instruments', 'search', query, type],
    queryFn: () => {
      let url = `/api/Instruments/search?query=${encodeURIComponent(query)}`
      if (type) url += `&type=${type}`
      return api.get<Instrument[]>(url)
    },
    enabled: query.trim().length >= 2,
    staleTime: 60_000,
  })
}

export function useExpiries(underlying: string) {
  return useQuery({
    queryKey: ['derivatives', 'expiries', underlying],
    queryFn: () =>
      api.get<DerivativeExpiry[]>(
        `/api/Instruments/derivatives/expiries?underlying=${encodeURIComponent(underlying)}`,
      ),
    // >= 1, not >= 3 — NSE has legitimate short underlyings (e.g. M&M → "MM").
    enabled: underlying.trim().length >= 1,
    staleTime: 5 * 60_000,
  })
}

export function useOptionChain(underlying: string, expiry: string | null) {
  return useQuery({
    queryKey: ['derivatives', 'chain', underlying, expiry],
    queryFn: () =>
      api.get<OptionChainItem[]>(
        `/api/Instruments/derivatives/chain?underlying=${encodeURIComponent(underlying)}&expiry=${expiry}`,
      ),
    enabled: underlying.trim().length >= 1 && !!expiry,
    staleTime: 5 * 60_000,
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

// ---------- Simulator ----------

export function useSimulationRuns() {
  return useQuery({
    queryKey: ['runs'],
    queryFn: () => api.get<SimulationRun[]>('/api/Simulator/runs'),
    refetchInterval: POLL_SLOW,
  })
}

export function useSimulationRun(id: number | null) {
  return useQuery({
    queryKey: ['runs', id],
    queryFn: () => api.get<SimulationRun>(`/api/Simulator/runs/${id}`),
    enabled: id != null,
  })
}

export function useRunPortfolio(id: number | null) {
  return useQuery({
    queryKey: ['runs', id, 'portfolio'],
    queryFn: () => api.get<SimulationPortfolio>(`/api/Simulator/runs/${id}/portfolio`),
    enabled: id != null,
    refetchInterval: POLL_SLOW,
  })
}

export function useRunPositions(id: number | null) {
  return useQuery({
    queryKey: ['runs', id, 'positions'],
    queryFn: () => api.get<PaperPosition[]>(`/api/Simulator/runs/${id}/positions`),
    enabled: id != null,
    refetchInterval: POLL_SLOW,
  })
}

export function useRunOrders(id: number | null) {
  return useQuery({
    queryKey: ['runs', id, 'orders'],
    queryFn: () => api.get<PaperOrder[]>(`/api/Simulator/runs/${id}/orders`),
    enabled: id != null,
    refetchInterval: POLL_SLOW,
  })
}

export function useRunSignals(id: number | null) {
  return useQuery({
    queryKey: ['runs', id, 'signals'],
    queryFn: () => api.get<SimulationSignal[]>(`/api/Simulator/runs/${id}/signals`),
    enabled: id != null,
  })
}

export function useRunEquityCurve(id: number | null) {
  return useQuery({
    queryKey: ['runs', id, 'equity'],
    queryFn: () => api.get<EquitySnapshot[]>(`/api/Simulator/runs/${id}/equity-curve`),
    enabled: id != null,
  })
}

export function useRunPerformance(id: number | null) {
  return useQuery({
    queryKey: ['runs', id, 'performance'],
    queryFn: () => api.get<PerformanceMetrics>(`/api/Simulator/runs/${id}/performance`),
    enabled: id != null,
  })
}

export function useRefreshPortfolio() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: number) =>
      api.post<SimulationPortfolio>(`/api/Simulator/runs/${id}/portfolio/refresh`),
    onSuccess: (_data, id) => qc.invalidateQueries({ queryKey: ['runs', id] }),
  })
}

// ---------- Strategies ----------

const POLL_LIVE_VIEW = 2_000
const POLL_RUNNER_LOGS = 3_000

/**
 * An API build older than the catalog rewrite answers without the array
 * fields; default them so a stale backend degrades to empty chips instead of
 * crashing every strategy page on `.length`.
 */
function normalizeStrategy(s: Partial<StrategyListItem> & { id: number; name: string }): StrategyListItem {
  return {
    ...s,
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

export function useStrategies() {
  return useQuery({
    queryKey: ['strategies'],
    queryFn: async () => {
      const rows = await api.get<Array<Partial<StrategyListItem> & { id: number; name: string }>>('/api/Strategy')
      return (Array.isArray(rows) ? rows : []).map(normalizeStrategy)
    },
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

/** Stop = square off every open position at the last mark and kill the runner. */
export function useStopStrategy() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ id, flatten = true }: { id: number; flatten?: boolean }) =>
      api.post<StopStrategyResponse>(`/api/Strategy/${id}/stop`, { flatten }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['strategies'] })
      qc.invalidateQueries({ queryKey: ['strategy', 'live'] })
    },
  })
}

function liveViewQuery(id: number, enabled: boolean) {
  return {
    queryKey: ['strategy', 'live', id] as const,
    queryFn: () => api.get<StrategyLiveView>(`/api/Strategy/${id}/live`),
    enabled,
    // A finished run's view cannot change, so a stopped card is fetched once and
    // then left alone (the server marks-to-market on every call). Polling
    // resumes when the strategy is started again: the start mutation and the
    // running-list watcher in LiveRunnerPage invalidate the key.
    refetchInterval: (query: { state: { data?: StrategyLiveView } }) =>
      query.state.data && !query.state.data.isActive ? false : POLL_LIVE_VIEW,
  }
}

/** Position-based live view of one strategy's current (or last) run. */
export function useStrategyLive(id: number, enabled: boolean) {
  return useQuery(liveViewQuery(id, enabled))
}

/**
 * Live views for a set of strategies at once (page-level totals). Shares the
 * cache key with useStrategyLive, so a RunCard and the stat row never double
 * fetch the same run.
 */
export function useStrategyLives(ids: number[]) {
  return useQueries({
    queries: ids.map((id) => liveViewQuery(id, true)),
  })
}

/** Runner stdout/stderr ring buffer; empty when the process is not running. */
export function useStrategyLogs(id: number, enabled: boolean) {
  return useQuery({
    queryKey: ['strategy', 'logs', id],
    queryFn: () => api.get<string[]>(`/api/Strategy/${id}/logs?take=200`),
    enabled,
    refetchInterval: POLL_RUNNER_LOGS,
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
export function useBacktestCoverage(underlying: string | null, strategyId: number | null) {
  return useQuery({
    queryKey: ['backtest', 'coverage', underlying, strategyId],
    queryFn: () =>
      api.get<BacktestCoverageResponse>(
        `/api/Backtest/coverage?underlying=${encodeURIComponent(underlying!)}&strategyId=${strategyId}`,
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

export function useUsers() {
  return useQuery({
    queryKey: ['users'],
    queryFn: () => api.get<MeResponse[]>('/api/UserAuth'),
  })
}

export function useRegisterUser() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (input: { userName: string; email: string; password: string }) =>
      api.post('/api/UserAuth/register', input),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['users'] }),
  })
}

export function useDeleteUser() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (userName: string) => api.delete<{ message: string }>(`/api/UserAuth/${userName}`),
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

import type { EquityGroup, MoversResponse, NewsResponse } from './types'

export function useMarketNews(category: 'india' | 'global' | 'commodities') {
  return useQuery({
    queryKey: ['news', category],
    queryFn: () => api.get<NewsResponse>(`/api/MarketIntel/news?category=${category}`),
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

// ---------- Broker app credentials ----------

export interface BrokerConfigResponse {
  broker: string
  clientId: string
  redirectUri: string
  hasSecret: boolean
  source: 'database' | 'config' | 'none'
  updatedBy: string | null
  updatedUtc: string | null
  suggestedRedirectUri: string
}

export function useBrokerConfig() {
  return useQuery({
    queryKey: ['broker', 'config'],
    queryFn: () => api.get<BrokerConfigResponse>('/api/Auth/broker-config'),
  })
}

export function useSaveBrokerConfig() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (input: { clientId: string; secretKey: string; redirectUri: string }) =>
      api.put<{ message: string }>('/api/Auth/broker-config', input),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['broker'] }),
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

export interface AlerterStatus {
  isRunning: boolean
  startedUtc: string | null
}

export function useAlerterStatus() {
  return useQuery({
    queryKey: ['alerts', 'status'],
    queryFn: () => api.get<AlerterStatus>('/api/alerts/status'),
    refetchInterval: 3000,
  })
}

export function useAlerterLogs() {
  return useQuery({
    queryKey: ['alerts', 'logs'],
    queryFn: () => api.get<string[]>('/api/alerts/logs'),
    refetchInterval: 2000,
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

/** Recent stdout/stderr of the ingestor process (new endpoint; an older API
 *  build answers with the SPA fallback, so callers must tolerate non-arrays). */
export function useIngestorLogs(enabled: boolean) {
  return useQuery({
    queryKey: ['ingestor', 'logs'],
    queryFn: () => api.get<string[]>('/api/Ingestor/logs?take=200'),
    enabled,
    refetchInterval: POLL_FAST,
  })
}

/** Expiry dates that actually exist in the instrument universe. */
export function useAvailableExpiries(exchange: string, underlying: string | null) {
  return useQuery({
    queryKey: ['expiries', 'available', exchange, underlying],
    queryFn: () =>
      api.get<string[]>(
        `/api/Expiry/available?exchange=${encodeURIComponent(exchange)}&underlying=${encodeURIComponent(underlying!)}`,
      ),
    enabled: !!underlying,
    staleTime: 10 * 60_000,
  })
}

/** Pull candles straight from FYERS into the local store (returns them too). */
export function useSyncHistory() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (input: {
      symbol: string
      resolution: string
      fromDate: string
      toDate: string
    }) => api.post<CandleDto[]>('/api/MarketData/history/sync', input),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['candles'] })
      qc.invalidateQueries({ queryKey: ['coverage'] })
    },
  })
}

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

export function useStrategySignals(strategyId: number, isRunning: boolean) {
  return useQuery({
    queryKey: ['strategy', 'signals', strategyId],
    queryFn: () => api.get<any[]>(`/api/Strategy/${strategyId}/signals`),
    enabled: isRunning,
    refetchInterval: 1000,
  })
}
