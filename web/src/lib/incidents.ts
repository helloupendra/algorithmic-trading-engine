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

import type {
  Incident,
  IncidentEpisode,
  IncidentHistory,
  IncidentHistoryRow,
  IncidentNotes,
  IncidentResolution,
  IncidentSeverity,
  IncidentStatus,
  IncidentSummary,
} from './types'

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
  { key: 'checkup', label: 'Checkup' },
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
 * How long Sentinel may go without finishing a round before its silence is
 * itself news. The health agent looks every 30 s, so a round ends at least that
 * often; five minutes is ten missed rounds.
 */
export const SILENCE_MINUTES = 5

/**
 * How long one agent's live incident may go without being re-seen before the
 * page says that agent may have stopped: at least two of its checks plus a
 * margin, so one slow check (a scan, an API timeout) is not an alarm. Each
 * agent re-sees every live incident it owns on every check, and one it stops
 * reporting is resolved within a few checks, so a live row older than this
 * means the agent is not checking. The cadences are the `interval_seconds` each
 * declares in sentinel/agents/*.py and sentinel/checkup/agent.py: health 30 s,
 * trading 60 s, logs 30 s, security 300 s, checkup 60 s — five minutes is ten,
 * five, ten and five checks; security's twelve is two checks and two minutes.
 * The checkup opens an incident only when it crashes, about itself.
 */
export const AGENT_SILENCE_MINUTES: Readonly<Record<string, number>> = {
  health: 5,
  trading: 5,
  logs: 5,
  security: 12,
  checkup: 5,
}

/** An agent this page does not know is given the longest threshold: better late than a false alarm. */
export function agentSilenceMinutes(agent: string | undefined): number {
  const known = agent ? AGENT_SILENCE_MINUTES[agent] : undefined
  return known ?? Math.max(...Object.values(AGENT_SILENCE_MINUTES))
}

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
 *   and moves its last-seen time, so one not re-seen for its agent's
 *   {@link agentSilenceMinutes} means that agent has stopped — the case the
 *   heartbeat cannot show.
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
  const stale = aged.filter((x) => x.min >= agentSilenceMinutes(x.r.agent))
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

/**
 * What the Resolve form says above its button. Closing a record whose problem
 * is still there buys a new incident and a new alert on the next check; the
 * form says so before, not after.
 */
export const RESOLVE_WARNING =
  'This closes the record; it fixes nothing. If the problem is still there, Sentinel opens a new ' +
  'incident on its next check and sends a fresh alert.'

/** "12 times", "once" — how often the problem has been seen. */
export function occurrencesText(n: number): string {
  if (!Number.isFinite(n) || n <= 0) return '—'
  return n === 1 ? 'once' : `${n.toLocaleString('en-IN')} times`
}

// ---------- the knowledge record: notes, seen before, history ----------

/** The longest root cause or resolution the API keeps (IncidentsController.MaxNoteChars); the form stops there. */
export const NOTE_MAX_CHARS = 2000

/** The longest fix reference the API keeps (IncidentsController.MaxFixRefChars). */
export const FIX_REF_MAX_CHARS = 300

/** How much of a note one line quotes; the History tab has the whole of it. */
export const QUOTE_CHARS = 200

const DAY = new Intl.DateTimeFormat('en-IN', {
  timeZone: 'Asia/Kolkata',
  day: 'numeric',
  month: 'short',
  year: 'numeric',
})

/** "24 Sept 2026" in IST, or "—". */
export function dayText(iso: string | null | undefined): string {
  if (!iso) return '—'
  const t = Date.parse(iso)
  return Number.isNaN(t) ? '—' : DAY.format(new Date(t))
}

function clip(text: string, max: number): string {
  const one = text.split(/\s+/).filter(Boolean).join(' ')
  return one.length > max ? `${one.slice(0, max - 1).trimEnd()}…` : one
}

type Notes = { rootCause?: string | null; resolution?: string | null; fixRef?: string | null }

/** Whether anyone has written anything about it. */
export function hasNotes(x: Notes | null | undefined): boolean {
  return !!(x && (x.rootCause?.trim() || x.resolution?.trim() || x.fixRef?.trim()))
}

/**
 * How an earlier episode ended, in a few words: what was done if someone wrote
 * it, else the cause, else who closed it. Never blank: "nobody wrote anything"
 * is itself worth knowing when the problem is back.
 */
export function lastTimeText(r: IncidentResolution): string {
  const done = r.resolution?.trim()
  if (done) return clip(maskSecrets(done), QUOTE_CHARS)
  const cause = r.rootCause?.trim()
  if (cause) return `cause: ${clip(maskSecrets(cause), QUOTE_CHARS)}`
  if (r.status !== 'resolved') return `still ${r.status}`
  return r.resolvedBy ? `closed by ${r.resolvedBy}, no notes` : 'cleared on its own, no notes'
}

/**
 * "Seen before: 3 times, first on 2 Sept 2026; last time: restarted the Dhan
 * feed (24 Sept 2026)" — or "First time …" — or null.
 *
 * Null when the API did not say (an older API, or a field missing): "not
 * known" must never read as "first time", which would tell the reader there
 * is no history to look for.
 */
export function seenBeforeText(
  i: Pick<Incident, 'previousEpisodes' | 'firstEverUtc' | 'lastResolution'>,
): string | null {
  const n = i.previousEpisodes
  if (typeof n !== 'number' || !Number.isFinite(n) || n < 0) return null
  if (n === 0) return 'First time Sentinel has seen this problem.'
  let text = `Seen before: ${occurrencesText(n)}, first on ${dayText(i.firstEverUtc)}`
  const last = i.lastResolution
  if (last) {
    text += `; last time: ${lastTimeText(last)}`
    if (last.resolvedUtc) text += ` (${dayText(last.resolvedUtc)})`
  }
  return text
}

/** How an episode ended, telling "it cleared" from "someone closed it" — the difference the history keeps. */
export function endedText(e: Pick<IncidentEpisode, 'status' | 'resolvedBy'>): string {
  if (e.status === 'open') return 'Open now'
  if (e.status === 'acknowledged') return 'Acknowledged, still live'
  return e.resolvedBy ? `Resolved by ${e.resolvedBy}` : "Cleared: Sentinel's checks came back clean"
}

/** "< 1 min", "12 min", "2 h 5 min", "3 d 4 h"; "—" when not known. */
export function durationText(seconds: number | null | undefined): string {
  if (seconds == null || !Number.isFinite(seconds) || seconds < 0) return '—'
  const m = Math.floor(seconds / 60)
  if (m < 1) return '< 1 min'
  if (m < 60) return `${m} min`
  const h = Math.floor(m / 60)
  if (h < 24) return m % 60 ? `${h} h ${m % 60} min` : `${h} h`
  const d = Math.floor(h / 24)
  return h % 24 ? `${d} d ${h % 24} h` : `${d} d`
}

/** How long one episode lasted, first sighting to resolve; "still live" while it is. */
export function lastedText(e: Pick<IncidentEpisode, 'status' | 'firstSeenUtc' | 'resolvedUtc'>): string {
  if (e.status !== 'resolved' || !e.resolvedUtc) return 'still live'
  const ms = Date.parse(e.resolvedUtc) - Date.parse(e.firstSeenUtc)
  return Number.isNaN(ms) ? '—' : `lasted ${durationText(ms / 1000)}`
}

/**
 * The mean time to resolve with what it is a mean of: a mean of one episode
 * is an anecdote and says so, and none ended is not a zero.
 */
export function mttrText(row: Pick<IncidentHistoryRow, 'meanTimeToResolveSeconds' | 'resolvedEpisodes'>): string {
  if (row.meanTimeToResolveSeconds == null || row.resolvedEpisodes <= 0) return 'none ended yet'
  const value = durationText(row.meanTimeToResolveSeconds)
  return row.resolvedEpisodes === 1 ? `${value} (one episode)` : `${value} (mean of ${row.resolvedEpisodes})`
}

/**
 * The fix reference as a link, only when it is an http(s) URL. Anything else
 * — a sha, a path, "javascript:…" — is shown as text: this string is typed by
 * a person and must not become a script the next admin clicks.
 */
export function fixRefLink(ref: string | null | undefined): string | null {
  const text = ref?.trim()
  if (!text) return null
  try {
    const url = new URL(text)
    return url.protocol === 'https:' || url.protocol === 'http:' ? url.href : null
  } catch {
    return null
  }
}

/** What the notes form edits: plain strings, never null. */
export interface NotesForm {
  rootCause: string
  resolution: string
  fixRef: string
}

/** The form's starting values: what is written already, so a resolve never wipes notes made earlier. */
export function notesFormFrom(x: Notes | null | undefined): NotesForm {
  return { rootCause: x?.rootCause ?? '', resolution: x?.resolution ?? '', fixRef: x?.fixRef ?? '' }
}

/**
 * The body the Resolve form and "Edit notes" send: all three fields, trimmed,
 * within the API's limits. All three always, so a field emptied in the form
 * is cleared (the API reads "" as clear, and a missing field as leave alone).
 */
export function notesBody(form: NotesForm): IncidentNotes {
  return {
    rootCause: form.rootCause.trim().slice(0, NOTE_MAX_CHARS),
    resolution: form.resolution.trim().slice(0, NOTE_MAX_CHARS),
    fixRef: form.fixRef.trim().slice(0, FIX_REF_MAX_CHARS),
  }
}

/** The windows the History tab offers. */
export const HISTORY_WINDOWS: ReadonlyArray<{ days: number; label: string }> = [
  { days: 30, label: '30 days' },
  { days: 90, label: '90 days' },
  { days: 365, label: '1 year' },
]

/** The query string for GET /api/Incidents/history. */
export function historyQuery(days: number): string {
  const n = Number.isFinite(days) ? Math.round(days) : 90
  return new URLSearchParams({ days: String(n) }).toString()
}

/**
 * The history endpoint's body, read strictly, like the list: anything but
 * `{ items: [...] }` throws, so the tab shows an error rather than an empty
 * table that would say nothing has ever gone wrong.
 */
export function readIncidentHistory(body: unknown): IncidentHistory {
  if (body && typeof body === 'object' && Array.isArray((body as { items?: unknown }).items)) {
    return body as IncidentHistory
  }
  throw new Error('The incident history endpoint answered in a shape this page cannot read.')
}

/** "3 episodes · 84 sightings" — how much of a problem there has been. */
export function episodesText(row: Pick<IncidentHistoryRow, 'episodes' | 'occurrences'>): string {
  const e = row.episodes === 1 ? '1 episode' : `${row.episodes.toLocaleString('en-IN')} episodes`
  const s = row.occurrences === 1 ? '1 sighting' : `${row.occurrences.toLocaleString('en-IN')} sightings`
  return `${e} · ${s}`
}

// ---------- secrets ----------

/**
 * Anything that looks like a secret is masked even if one reaches an
 * incident's text — a crash traceback can carry a request header or a
 * connection string. Sentinel is meant to redact before it stores (as its
 * notifier does for Telegram); this is the page's own second line, so a slip
 * there does not put a token on a screen or in a screen recording. A mask that
 * garbles the desk's own prose ("FYERS token expired at 08:45") is a failure
 * too, so a key counts only when ':' or '=' follows it on the same line.
 *
 * One spec, identical in every layer that sends or shows incident text:
 * Sentinel's notify.py (Telegram), IncidentRedaction in IncidentsController.cs
 * (the API), this (the console), and the logs agent's filter for lines that may
 * hold a secret. Change one, change all; each has a table-driven test with the
 * same cases.
 *
 * 1. `Authorization: Bearer|Basic <value>` → the value.
 * 2. `Bearer` and 12+ token characters anywhere.
 * 3. `scheme://user:password@` and `scheme://:password@` → the password.
 * 4. `key=value`, `key: value`, `"key": "value"`, where the key is an identifier
 *    with a whole part (underscore separated, or the end of a camelCase key)
 *    that is secret, password, passwd, pwd, token, api_key, private_key, totp
 *    or pin: DHAN_PIN, JWT_SECRET_KEY, access_token, accessToken, X-Api-Key —
 *    not "tokens" or "Skipping". The separator is an optional quote, spaces or
 *    tabs (never a newline), then ':' or '=' but not '=='. The value runs to
 *    whitespace, a quote, '&', ',' or ';' — or, quoted, to its closing quote.
 * 5. Telegram bot tokens anywhere, `/bot<token>/` in a URL included.
 * 6. JWTs: `eyJ….eyJ….<signature>`.
 *
 * Lengths are bounded so a long line cannot make a pattern backtrack.
 */
const HIDDEN = '…'
const AUTH_HEADER = /(authorization["']?[ \t]*[:=][ \t]*["']?(?:bearer|basic)[ \t]+)[^\s"'&,;]+/gi
const BEARER = /(\bbearer[ \t]+)[A-Za-z0-9._~+/=-]{12,}/gi
const URL_PASSWORD = /\b([a-z][a-z0-9+.-]*:\/\/[^\s:/@]*:)[^\s@/]+(?=@)/gi
const KEY_VALUE =
  /(?<![A-Za-z0-9_])([A-Za-z0-9_]{0,64}?(?:secret|password|passwd|pwd|token|api[_-]?key|private[_-]?key|totp|pin)(?:_[A-Za-z0-9]{1,32}){0,8}["']?[ \t]*[:=](?!=)[ \t]*)("[^"\r\n]+"|'[^'\r\n]+'|["']?[^\s"'&,;]+)/gi
const JWT = /eyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}/g
const TELEGRAM_BOT_TOKEN = /(?<![0-9])[0-9]{8,10}:[A-Za-z0-9_-]{30,}/g

/** The key and separator kept, the value hidden; a quoted value keeps its quotes. */
function maskValue(_match: string, label: string, value: string): string {
  const lead = value[0] === '"' || value[0] === "'" ? value[0] : ''
  const tail = lead && value.length > 1 && value.endsWith(lead) ? lead : ''
  return `${label}${lead}${HIDDEN}${tail}`
}

/** The text with anything that looks like a secret replaced by "…". */
export function maskSecrets(text: string): string {
  return text
    .replace(AUTH_HEADER, (_m, label: string) => `${label}${HIDDEN}`)
    .replace(BEARER, (_m, label: string) => `${label}${HIDDEN}`)
    .replace(URL_PASSWORD, (_m, label: string) => `${label}${HIDDEN}`)
    .replace(KEY_VALUE, maskValue)
    .replace(JWT, HIDDEN)
    .replace(TELEGRAM_BOT_TOKEN, HIDDEN)
}
