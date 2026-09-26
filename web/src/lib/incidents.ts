/**
 * Sentinel's incidents: the order, the wording and the query the Incidents page
 * puts on them. Pure, so the ordering and the sentences are tested rather than
 * eyeballed.
 *
 * The rule this file keeps: "not known" never renders as "nothing wrong". A
 * list the API did not answer, or answered in a shape this page cannot read, is
 * an error on screen — never an empty table that reads as a quiet desk. And an
 * empty list is never presented as proof that Sentinel is looking: only a
 * recent check is.
 */

import type { Incident, IncidentSeverity, IncidentStatus, IncidentSummary } from './types'

/** Loudest first — the order of the header tiles. */
export const SEVERITY_ORDER: readonly IncidentSeverity[] = ['critical', 'high', 'medium', 'low']

const SEVERITY_RANK: Record<IncidentSeverity, number> = { low: 0, medium: 1, high: 2, critical: 3 }

/** Higher is louder; an unknown severity sorts below "low" rather than throwing. */
export function severityRank(severity: string): number {
  return SEVERITY_RANK[severity as IncidentSeverity] ?? -1
}

export const SEVERITY_LABEL: Record<IncidentSeverity, string> = {
  critical: 'Critical',
  high: 'High',
  medium: 'Medium',
  low: 'Low',
}

/** Badge tone per severity. Critical and high are both red; critical is also filled. */
export function severityTone(severity: string): 'neg' | 'warn' | 'neutral' {
  if (severity === 'critical' || severity === 'high') return 'neg'
  if (severity === 'medium') return 'warn'
  return 'neutral'
}

/** Tone for a header count: a zero is plain, so a quiet desk is not painted green. */
export function countTone(severity: IncidentSeverity, count: number | null): 'neg' | 'warn' | undefined {
  if (count == null || count <= 0) return undefined
  if (severity === 'critical' || severity === 'high') return 'neg'
  if (severity === 'medium') return 'warn'
  return undefined
}

export const STATUS_TONE: Record<IncidentStatus, 'warn' | 'accent' | 'pos'> = {
  open: 'warn',
  acknowledged: 'accent',
  resolved: 'pos',
}

const LIVE_STATUSES: ReadonlySet<string> = new Set<IncidentStatus>(['open', 'acknowledged'])

function time(iso: string | null | undefined): number {
  if (!iso) return 0
  const t = Date.parse(iso)
  return Number.isNaN(t) ? 0 : t
}

/**
 * Incidents first seen within this long of each other count as one check.
 * Sentinel stamps each finding as it stores it, so one check's incidents
 * differ by milliseconds, never by exactly zero; its cycle runs every 5 s.
 */
export const SAME_CHECK_MS = 5_000

/**
 * Newest first, by when the problem was first seen. Not by last seen: every
 * live incident is re-seen on every check, so that order would reshuffle the
 * table under the reader on each poll. Incidents opened in the same check
 * (first seen within {@link SAME_CHECK_MS} of the one before) put the louder
 * one first.
 */
export function sortNewestFirst(items: readonly Incident[]): Incident[] {
  const byTime = [...items].sort((a, b) => time(b.firstSeenUtc) - time(a.firstSeenUtc) || b.id - a.id)

  // Walk newest to oldest and cut a new group wherever the gap to the previous
  // incident is longer than one check. Grouping by gap, not by clock buckets,
  // so two incidents a millisecond apart are never split by a bucket edge.
  const groups: Incident[][] = []
  let previous = Number.NaN
  for (const item of byTime) {
    const t = time(item.firstSeenUtc)
    if (groups.length === 0 || previous - t > SAME_CHECK_MS) groups.push([])
    groups[groups.length - 1].push(item)
    previous = t
  }

  return groups.flatMap((group) =>
    group.sort(
      (a, b) =>
        severityRank(b.severity) - severityRank(a.severity) ||
        time(b.firstSeenUtc) - time(a.firstSeenUtc) ||
        b.id - a.id,
    ),
  )
}

// ---------- filters ----------

/** What the status filter offers. "Live" is open or acknowledged — what still needs someone. */
export type IncidentView = 'live' | 'resolved' | 'any'

export const INCIDENT_VIEWS: ReadonlyArray<{ key: IncidentView; label: string }> = [
  { key: 'live', label: 'Live' },
  { key: 'resolved', label: 'Resolved' },
  { key: 'any', label: 'All' },
]

/** Sentinel's watchers, by the `name` each declares in sentinel/agents/*.py. */
export const INCIDENT_AGENTS: ReadonlyArray<{ key: string; label: string }> = [
  { key: 'health', label: 'Health' },
  { key: 'trading', label: 'Trading' },
  { key: 'logs', label: 'Logs' },
  { key: 'security', label: 'Security' },
]

export interface IncidentFilters {
  status: IncidentView | 'open' | 'acknowledged'
  severity?: IncidentSeverity | ''
  agent?: string
  take?: number
}

/** The query string for GET /api/Incidents. Empty filters are left out, not sent blank. */
export function incidentsQuery(filters: IncidentFilters): string {
  const params = new URLSearchParams()
  params.set('status', filters.status)
  if (filters.severity) params.set('severity', filters.severity)
  if (filters.agent) params.set('agent', filters.agent)
  params.set('take', String(filters.take ?? 200))
  return params.toString()
}

/**
 * The list endpoint's body, read strictly. A bare array or `{ items: [...] }`
 * is accepted; anything else throws, so the page shows an error instead of an
 * empty table that would say "nothing wrong".
 */
export function readIncidentList(body: unknown): Incident[] {
  if (Array.isArray(body)) return body as Incident[]
  if (body && typeof body === 'object' && Array.isArray((body as { items?: unknown }).items)) {
    return (body as { items: Incident[] }).items
  }
  throw new Error('The incidents endpoint answered in a shape this page cannot read.')
}

/** One severity's live count, or null when the summary did not carry it (rendered "—", not 0). */
export function countFor(summary: IncidentSummary | undefined, severity: IncidentSeverity): number | null {
  const n = summary?.counts?.[severity]
  return typeof n === 'number' && Number.isFinite(n) ? n : null
}

/**
 * What the list area shows. An error wins over an empty list: a refresh that
 * failed after an empty answer is "not known", and must not read as "nothing
 * open". Rows already on screen stay, with the page saying they are old.
 */
export type ListState = 'loading' | 'error' | 'empty' | 'rows'

export function listState(query: {
  isPending: boolean
  isError: boolean
  data: readonly unknown[] | undefined
}): ListState {
  if (query.isPending) return 'loading'
  if (!query.data) return 'error'
  if (query.data.length === 0) return query.isError ? 'error' : 'empty'
  return 'rows'
}

// ---------- wording ----------

function agentEntry(agent: string | undefined) {
  return agent ? INCIDENT_AGENTS.find((a) => a.key === agent) : undefined
}

function agentName(agent: string | undefined): string | null {
  const a = agentEntry(agent)
  return a ? `the ${a.label.toLowerCase()} agent` : agent ? `the ${agent} agent` : null
}

/**
 * What an empty list says, for the filter it was asked with. Only called when
 * the query succeeded. It states what the table holds and nothing more: an
 * empty table is not evidence that Sentinel is looking (see {@link watchmanNote}).
 */
export function emptyMessage(view: IncidentView, agent?: string): string {
  const name = agentName(agent)

  if (view === 'live') {
    return name ? `No open incidents recorded from ${name}.` : 'No open incidents recorded.'
  }
  if (view === 'resolved') {
    return name ? `Nothing from ${name} has been resolved yet.` : 'Nothing resolved yet.'
  }
  return name ? `${capitalise(name)} has not recorded anything yet.` : 'Sentinel has not recorded anything yet.'
}

function capitalise(s: string): string {
  return s.charAt(0).toUpperCase() + s.slice(1)
}

/**
 * How long Sentinel may go without a check before its silence is itself news.
 * Its slowest agents look every 60 s and re-see every live incident on each
 * look, so five minutes is five missed checks: quiet on an ordinary day.
 */
export const SILENCE_MINUTES = 5

/**
 * A warning when Sentinel itself has gone quiet, or null.
 *
 * `undefined` means the API does not report Sentinel's checks at all: the page
 * then cannot tell and says nothing extra. `null` means it does, and there has
 * never been one.
 */
export function silenceNote(lastCheckUtc: string | null | undefined, nowMs: number): string | null {
  if (lastCheckUtc === undefined) return null
  if (lastCheckUtc === null) {
    return 'Sentinel has not reported a check yet, so an empty list here proves nothing.'
  }
  const t = Date.parse(lastCheckUtc)
  if (Number.isNaN(t)) return null
  const minutes = Math.floor((nowMs - t) / 60_000)
  if (minutes < SILENCE_MINUTES) return null
  return `Sentinel's last check finished ${minutes} min ago. It may have stopped, so an empty list is not proof that all is well.`
}

export interface WatchmanNote {
  /** `warn`: Sentinel has evidently stopped. `unknown`: nothing on the page can say either way. */
  tone: 'warn' | 'unknown'
  text: string
}

function listOf(labels: string[]): string {
  if (labels.length <= 1) return labels.join('')
  return `${labels.slice(0, -1).join(', ')} and ${labels[labels.length - 1]}`
}

/**
 * Whether the page can vouch that Sentinel is still looking.
 *
 * Two kinds of evidence, used together:
 *
 * - The heartbeat (`lastCheckUtc` on the summary): when Sentinel last finished
 *   a round. It is the newest round of any agent, so it proves Sentinel as a
 *   whole is alive, not that every agent is. `undefined` means the API does not
 *   send one; `null` means it does and Sentinel has never reported.
 * - The live incidents: Sentinel re-sees every live incident on every check
 *   and moves its last-seen time, so one not re-seen for
 *   {@link SILENCE_MINUTES} means the agent that owns it has stopped — the case
 *   the heartbeat cannot show.
 *
 * With neither heartbeat nor live incident there is no evidence either way,
 * and the page says so instead of letting four zeros read as a quiet desk.
 *
 * `asOfMs` / `liveAsOfMs` are when the summary and the live list arrived, not
 * the wall clock: a tab woken from sleep must not flash "stopped" in the
 * instant before its refetch lands.
 */
export function watchmanNote(input: {
  lastCheckUtc: string | null | undefined
  liveRows: readonly Incident[] | undefined
  asOfMs: number
  liveAsOfMs?: number
}): WatchmanNote | null {
  const { lastCheckUtc, liveRows, asOfMs } = input
  const liveAsOfMs = input.liveAsOfMs ?? asOfMs
  const hasHeartbeat = lastCheckUtc !== undefined

  const silence = silenceNote(lastCheckUtc, asOfMs)
  if (silence) return { tone: 'warn', text: silence }

  if (!liveRows) return null // not loaded or failed: the list shows its own error

  const live = liveRows.filter((r) => LIVE_STATUSES.has(r.status))
  if (live.length === 0) {
    if (hasHeartbeat) return null // a recent round, and it found nothing: a real zero
    return {
      tone: 'unknown',
      text:
        'Sentinel sends this page no heartbeat yet, so zero open incidents cannot tell a quiet desk ' +
        'from a Sentinel that has stopped.',
    }
  }

  const aged = live.flatMap((r) => {
    const t = Date.parse(r.lastSeenUtc)
    return Number.isNaN(t) ? [] : [{ r, min: Math.floor((liveAsOfMs - t) / 60_000) }]
  })
  const stale = aged.filter((x) => x.min >= SILENCE_MINUTES)
  if (stale.length === 0) return null

  // Without a heartbeat, every live incident gone quiet is the only sign that
  // all of Sentinel has stopped. With a fresh one, it is some agents only.
  if (!hasHeartbeat && stale.length === aged.length) {
    const freshest = Math.min(...aged.map((x) => x.min))
    return {
      tone: 'warn',
      text:
        `Sentinel has not re-checked any open incident for ${freshest} min. It has probably stopped, ` +
        'so nothing new is being found and these counts are not the desk as it is now.',
    }
  }

  const agents = [...new Set(stale.map((x) => x.r.agent))]
  const labels = agents.map((a) => agentEntry(a)?.label.toLowerCase() ?? a)
  const oldest = Math.max(...stale.map((x) => x.min))
  const plural = agents.length > 1
  return {
    tone: 'warn',
    text:
      `The ${listOf(labels)} agent${plural ? 's have' : ' has'} not re-checked ${stale.length} open ` +
      `incident${stale.length === 1 ? '' : 's'} for ${oldest} min while the rest of Sentinel runs. ` +
      `${plural ? 'They' : 'It'} may have crashed or been left out of Sentinel's --only list.`,
  }
}

/** What the Resolve button asks before it closes a live incident. */
export function resolveConfirmText(incident: Pick<Incident, 'id' | 'title'>): string {
  return (
    `Resolve #${incident.id} "${incident.title}"?\n\n` +
    'This closes the record; it fixes nothing. If the problem is still there, Sentinel opens a new ' +
    'incident on its next check and sends a fresh alert.'
  )
}

/** "12 times", "once" — how often the problem has been seen. */
export function occurrencesText(n: number): string {
  if (!Number.isFinite(n) || n <= 0) return '—'
  return n === 1 ? 'once' : `${n.toLocaleString('en-IN')} times`
}

// ---------- secrets ----------

/**
 * Patterns for secrets that must not be shown even if one reaches an
 * incident's text — a crash traceback can carry a request header or a
 * connection string. Sentinel is meant to redact before it stores (as its
 * notifier does for Telegram); this is the page's own second line, so a slip
 * there does not put a token on a screen or in a screen recording.
 *
 * Each pattern keeps its first group (the label) and replaces the value.
 */
const SECRET_PATTERNS: readonly RegExp[] = [
  /(bearer\s+)[A-Za-z0-9._~+/=-]{12,}/gi,
  // JWTs anywhere, labelled or not.
  /()eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}/g,
  // Telegram bot tokens: 123456789:AA...
  /()(?<![0-9])[0-9]{8,10}:[A-Za-z0-9_-]{30,}/g,
  // Credentials in a URL: scheme://user:password@host
  /(\b[a-z][a-z0-9+.-]*:\/\/[^\s:/@]+:)[^\s@/]+(?=@)/gi,
  // key=value, key: value, "key": "value", Password=...; in a connection string.
  /(\b(?:password|passwd|pwd|secret|client[_-]?secret|app[_-]?secret|token|access[_-]?token|refresh[_-]?token|api[_-]?key|apikey|totp|pin)["']?\s*[:=]\s*["']?)[^\s"',;&}]{4,}/gi,
]

/** The text with anything that looks like a secret replaced by "…". */
export function maskSecrets(text: string): string {
  let out = text
  for (const pattern of SECRET_PATTERNS) {
    out = out.replace(pattern, (_match, label: string) => `${label}…`)
  }
  return out
}
