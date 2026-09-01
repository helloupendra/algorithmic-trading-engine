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

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from './api'
import type {
  BackfillHistoryResponse,
  BrokerSessionInfo,
  CandleDto,
  DerivativeExpiry,
  EquitySnapshot,
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
  StrategyListItem,
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
    refetchInterval: POLL_FAST,
  })
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

export function useInstrumentSearch(query: string) {
  return useQuery({
    queryKey: ['instruments', 'search', query],
    queryFn: () => api.get<Instrument[]>(`/api/Instruments/search?query=${encodeURIComponent(query)}`),
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
    enabled: underlying.trim().length >= 3,
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
    enabled: underlying.trim().length >= 3 && !!expiry,
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

export function useStrategies() {
  return useQuery({
    queryKey: ['strategies'],
    queryFn: () => api.get<StrategyListItem[]>('/api/Strategy'),
    refetchInterval: POLL_SLOW,
  })
}

export function useStartStrategy() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: number) =>
      api.post<{ message: string; processId: number }>(`/api/Strategy/${id}/start`),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['strategies'] }),
  })
}

export function useStopStrategy() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api.post<{ message: string }>(`/api/Strategy/${id}/stop`),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['strategies'] }),
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
    onSuccess: () => qc.invalidateQueries({ queryKey: ['candles'] }),
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

export function useStopAlerter() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: () => api.post<{ message: string }>('/api/alerts/stop'),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['alerts', 'status'] }),
  })
}
