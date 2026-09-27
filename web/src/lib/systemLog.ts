/**
 * System → Log as data: four streams of what happened on the platform, read
 * as one timeline. Activity (every action that changed something, and every
 * refusal), alerts (everything sent to Telegram), deploys (what the desk did
 * with each push) and risk events (the kill switch and the limits).
 *
 * Each stream keeps its own endpoint; this turns their rows into one shape,
 * merges them newest first and filters them, so the page only renders rows
 * and the rules are pinned by systemLog.test.ts. A server-side timeline
 * (GET /api/System/timeline) would replace the merge, not the page.
 */

import { deployState, deploySummary } from './deploys'
import type { DeployState } from './deploys'
import type { ActivityLogEntry, AlertEvent, DeployRecord, RiskEvent } from './types'

export type LogSource = 'activity' | 'alerts' | 'deploys' | 'risk'

export const LOG_SOURCES: ReadonlyArray<{ key: LogSource; label: string }> = [
  { key: 'activity', label: 'Activity' },
  { key: 'alerts', label: 'Alerts' },
  { key: 'deploys', label: 'Deploys' },
  { key: 'risk', label: 'Risk' },
]

/** The stream a URL asks for (?source=), or all of them. */
export function logSourceFrom(value: string | null | undefined): LogSource | 'all' {
  return LOG_SOURCES.some((s) => s.key === value) ? (value as LogSource) : 'all'
}

export type LogTone = 'pos' | 'neg' | 'warn' | 'accent' | 'neutral'

/** One line of the timeline, whichever stream it came from. */
export interface LogRow {
  /** Unique across every stream: it is the row's React key. */
  key: string
  source: LogSource
  atUtc: string
  /** What happened, in the stream's own words. */
  title: string
  /** The quieter line under it: the request, the message, the commits. */
  detail: string | null
  /** Who did it, or what sent it. */
  who: string | null
  /** How it ended, as a badge. */
  result: { label: string; tone: LogTone } | null
}

const MODULE_LABELS: Record<string, string> = {
  strategies: 'Strategies',
  backtesting: 'Backtesting',
  data: 'Data',
  connectors: 'Connectors',
  risk: 'Risk',
  alerts: 'Alerts',
  users: 'Users',
  auth: 'Sign-in',
  other: 'Other',
}

export function moduleLabel(key: string): string {
  return MODULE_LABELS[key] ?? key
}

/** A refusal is often the more interesting row, so it reads as one rather than as a failure. */
export function activityResult(entry: Pick<ActivityLogEntry, 'succeeded' | 'statusCode'>): { label: string; tone: LogTone } {
  if (entry.succeeded) return { label: String(entry.statusCode), tone: 'pos' }
  if (entry.statusCode === 403) return { label: '403 refused', tone: 'warn' }
  if (entry.statusCode === 401) return { label: '401', tone: 'warn' }
  return { label: String(entry.statusCode), tone: 'neg' }
}

export function activityRows(entries: readonly ActivityLogEntry[] | undefined): LogRow[] {
  return (entries ?? []).map((e) => {
    const request = `${e.method} ${e.path}`
    return {
      key: `activity:${e.id}`,
      source: 'activity',
      atUtc: e.occurredUtc,
      // The endpoint's own sentence when it wrote one; the request when it did not.
      title: e.summary || request,
      detail: [moduleLabel(e.module), e.summary ? request : null, `${e.durationMs} ms`].filter(Boolean).join(' · '),
      who: e.role ? `${e.userName} · ${e.role}` : e.userName,
      result: activityResult(e),
    }
  })
}

function severityTone(severity: string): LogTone {
  switch (severity?.toLowerCase()) {
    case 'error':
    case 'critical':
      return 'neg'
    case 'warning':
      return 'warn'
    case 'success':
      return 'pos'
    case 'info':
      return 'accent'
    default:
      return 'neutral'
  }
}

export function alertRows(events: readonly AlertEvent[] | undefined): LogRow[] {
  return (events ?? []).map((ev) => ({
    key: `alert:${ev.id}`,
    source: 'alerts',
    atUtc: ev.occurredUtc,
    title: ev.title,
    detail:
      [
        ev.message?.trim() || null,
        ev.symbol,
        ev.simulationRunId ? `run #${ev.simulationRunId}` : null,
        // Recorded is not the same as delivered: say which it was.
        ev.deliveredToTelegram ? 'sent to Telegram' : 'recorded only',
      ]
        .filter(Boolean)
        .join(' · ') || null,
    who: ev.underlying && ev.underlying !== 'UNKNOWN' ? `${ev.source} · ${ev.underlying}` : ev.source,
    result: { label: ev.severity, tone: severityTone(ev.severity) },
  }))
}

const DEPLOY_RESULT: Record<DeployState, { label: string; tone: LogTone }> = {
  live: { label: 'Live', tone: 'pos' },
  skipped: { label: 'Skipped', tone: 'warn' },
  failed: { label: 'Needs you', tone: 'neg' },
}

export function deployRows(records: readonly DeployRecord[] | undefined): LogRow[] {
  return (records ?? []).map((r, i) => {
    const took = Math.max(0, (Date.parse(r.finishedUtc) - Date.parse(r.startedUtc)) / 1000)
    const commits = r.fromCommit && r.toCommit ? `${r.fromCommit} → ${r.toCommit}` : r.toCommit ? `at ${r.toCommit}` : null
    const state = deployState(r.outcome)
    return {
      key: `deploy:${r.finishedUtc}:${i}`,
      source: 'deploys',
      atUtc: r.finishedUtc,
      title: deploySummary(r.summary) || 'Deploy',
      detail: [commits, r.filesChanged > 0 ? `${r.filesChanged} file(s)` : null, `took ${took < 1 ? '<1' : took.toFixed(0)}s`]
        .filter(Boolean)
        .join(' · '),
      who: r.machine || null,
      // A word neither deploy script writes is shown as written, never guessed at.
      result: state ? DEPLOY_RESULT[state] : { label: r.outcome || 'unknown', tone: 'neutral' },
    }
  })
}

/** "KillSwitchActivated" → "Kill switch activated": the kinds are code names. */
export function riskKindText(kind: string): string {
  const words = kind.replace(/[_-]+/g, ' ').replace(/([a-z])([A-Z])/g, '$1 $2').trim().toLowerCase()
  return words ? words[0].toUpperCase() + words.slice(1) : kind
}

export function riskRows(events: readonly RiskEvent[] | undefined): LogRow[] {
  return (events ?? []).map((ev) => ({
    key: `risk:${ev.id}`,
    source: 'risk',
    atUtc: ev.occurredUtc,
    title: riskKindText(ev.kind),
    detail: [ev.reason, ev.symbol, ev.simulationRunId ? `run #${ev.simulationRunId}` : null].filter(Boolean).join(' · ') || null,
    who: ev.actorName ?? 'system',
    result: null,
  }))
}

/**
 * The timeline: every stream's rows (or one stream's), newest first, keeping
 * only rows whose text holds every word of the query.
 */
export function mergeLog(rows: readonly LogRow[], source: LogSource | 'all', query = ''): LogRow[] {
  const words = query.toLowerCase().split(/\s+/).filter(Boolean)
  return rows
    .filter((r) => source === 'all' || r.source === source)
    .filter((r) => {
      if (words.length === 0) return true
      const text = `${r.title} ${r.detail ?? ''} ${r.who ?? ''} ${r.result?.label ?? ''}`.toLowerCase()
      return words.every((w) => text.includes(w))
    })
    .map((r, i) => ({ r, i, t: Date.parse(r.atUtc) }))
    .sort((a, b) => (b.t || 0) - (a.t || 0) || a.i - b.i)
    .map((x) => x.r)
}

/** How many rows each stream has, for its tab. */
export function logCounts(rows: readonly LogRow[]): Record<LogSource | 'all', number> {
  const counts: Record<LogSource | 'all', number> = { all: rows.length, activity: 0, alerts: 0, deploys: 0, risk: 0 }
  for (const r of rows) counts[r.source]++
  return counts
}
