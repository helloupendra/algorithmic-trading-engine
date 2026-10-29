/**
 * The level-alert queries, shared by the page and the top bar's badge.
 *
 * Every key starts with ['level-alerts'], the prefix a desk event of kind
 * "alert" invalidates (live.ts, deskEventKeys): a fire reaches the badge the
 * moment the server records it, and the polls below are only the fallback
 * for a socket that is down.
 */

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from './api'
import type {
  LevelAlertFire,
  LevelAlertRule,
  LevelAlertStatus,
  LevelAlertSummary,
  LevelKind,
  LevelReading,
  SaveLevelAlertRule,
} from './levelAlerts'

const BASE = '/api/LevelAlerts'

/**
 * The caller's new-fire count. Every console asks, since /me does not yet say
 * which grants a trader holds; one without market-data is answered 403 once
 * and not asked again.
 */
export function useLevelAlertSummary() {
  return useQuery({
    queryKey: ['level-alerts', 'summary'],
    queryFn: () => api.get<LevelAlertSummary>(`${BASE}/summary`),
    refetchInterval: (query) => ((query.state.error as { status?: number } | null)?.status === 403 ? false : 30_000),
  })
}

/** The rules with what their targets read now, which moves every few seconds while they are watched. */
export function useLevelAlertRules(mine: boolean) {
  return useQuery({
    queryKey: ['level-alerts', 'rules', { mine }],
    queryFn: () => api.get<LevelAlertRule[]>(`${BASE}/rules?mine=${mine}`),
    refetchInterval: 10_000,
  })
}

export function useLevelAlertFires(mine: boolean) {
  return useQuery({
    queryKey: ['level-alerts', 'fired', { mine }],
    queryFn: () => api.get<LevelAlertFire[]>(`${BASE}/fired?mine=${mine}&limit=200`),
    refetchInterval: 30_000,
  })
}

export function useLevelAlertStatus() {
  return useQuery({
    queryKey: ['level-alerts', 'status'],
    queryFn: () => api.get<LevelAlertStatus>(`${BASE}/status`),
    refetchInterval: 10_000,
  })
}

/** What a target reads now, read the way the service reads it; for the form, before a rule exists. */
export function useLevelAlertNow(kind: LevelKind, target: string) {
  const t = target.trim().toUpperCase()
  // A price symbol is EXCHANGE:NAME; until it is, there is nothing to ask about.
  const ready = kind === 'pcr' ? t.length > 0 : /^[A-Z]+:.+/.test(t)
  return useQuery({
    queryKey: ['level-alerts', 'now', kind, t],
    queryFn: () => api.get<LevelReading>(`${BASE}/now?kind=${kind}&target=${encodeURIComponent(t)}`),
    enabled: ready,
    refetchInterval: 10_000,
    retry: false,
  })
}

export function useSaveLevelAlert() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ id, request }: { id: number | null; request: SaveLevelAlertRule }) =>
      id === null ? api.post<LevelAlertRule>(`${BASE}/rules`, request) : api.put<LevelAlertRule>(`${BASE}/rules/${id}`, request),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['level-alerts'] }),
  })
}

export function useDeleteLevelAlert() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api.delete<void>(`${BASE}/rules/${id}`),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['level-alerts'] }),
  })
}

/** Marks the caller's own new fires seen: the badge clears, the history keeps them. */
export function useMarkLevelAlertsSeen() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: () => api.post<{ marked: number }>(`${BASE}/fired/seen`),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['level-alerts', 'summary'] }),
  })
}
