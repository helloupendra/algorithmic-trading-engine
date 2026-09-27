/**
 * Sentinel's desk checkups: the types, wording and grouping the Desk checkup
 * page puts on them. Pure, so the rules are tested rather than eyeballed.
 *
 * A checkup is a checklist Sentinel runs over the desk at fixed times (before
 * the open, after the close, at the end of the day, weekly) or when an admin
 * asks, with a plain-English "what to do" on every item that needs a person.
 *
 * The rule this file keeps, as the incidents page does: "not known" never
 * renders as "all good". An answer the page cannot read is an error, a report
 * whose items could not be read says so, a check that was skipped is never
 * counted as passed, and an old or missing checkup is said out loud rather
 * than letting the last good report stand in for today.
 */

// ---------- types (GET/POST /api/Checkups) ----------

export type CheckupSlot = 'morning' | 'close' | 'night' | 'weekly' | 'on-request'

/** requested (asked for, not picked up yet) → running → done or failed. */
export type CheckupStatus = 'requested' | 'running' | 'done' | 'failed'

/** Sentinel's own reading of the items: any fail is action, else any warn is attention, else ok. */
export type CheckupVerdict = 'ok' | 'attention' | 'action'

/** fail: a person must act. warn: worth a look. info: a fact to keep in mind. skip: could not be checked. */
export type CheckupItemState = 'ok' | 'warn' | 'fail' | 'info' | 'skip'

export interface CheckupCounts {
  ok: number
  warn: number
  fail: number
  info: number
  skip: number
}

export interface CheckupItem {
  /** What was checked, e.g. "dhan-token". */
  key: string
  /** The group it belongs to, e.g. "Brokers & data". */
  area: string
  title: string
  /** One of {@link CheckupItemState}; anything else Sentinel wrote is passed through as written. */
  state: string
  /** What was found. */
  detail: string
  /** What to do; empty when nothing is. */
  action: string
  /** A console path that shows more, or null. */
  link: string | null
}

/** A checkup as the history lists it. */
export interface CheckupSummary {
  id: number
  slot: string
  status: string
  /** '' until the checkup is done. */
  verdict: string
  headline: string
  requestedUtc: string | null
  /** '' for a scheduled checkup. */
  requestedBy: string
  startedUtc: string | null
  completedUtc: string | null
  host: string
  counts: CheckupCounts
}

/** A checkup in full. */
export interface CheckupDetail extends CheckupSummary {
  items: CheckupItem[]
  /** Some or all of the stored items could not be read, so the list may be short. */
  itemsUnreadable: boolean
  /** Why it failed; '' otherwise. */
  error: string
  /** When its Telegram message went out, if it did. */
  notifiedUtc: string | null
}

/** GET /api/Checkups/latest. */
export interface CheckupLatest {
  /** The newest done or failed checkup; null when none has finished. */
  latest: CheckupDetail | null
  /** Requested or running within the last 10 minutes; null when none is. */
  pending: CheckupSummary | null
  /** When the newest *done* checkup finished; null when none ever has. */
  lastCompletedUtc: string | null
}

/** POST /api/Checkups/run. */
export interface CheckupRunAnswer {
  /** The checkup to wait for: the new request, or the one already pending. */
  id: number
  status: string
  alreadyPending: boolean
}

// ---------- labels ----------

export const SLOT_LABEL: Record<CheckupSlot, string> = {
  morning: 'Before the open',
  close: 'After the close',
  night: 'End of day',
  weekly: 'Weekly review',
  'on-request': 'On request',
}

/** A slot this page does not know is shown by its own name rather than hidden. */
export function slotLabel(slot: string): string {
  return SLOT_LABEL[slot as CheckupSlot] ?? slot
}

export const VERDICT_LABEL: Record<CheckupVerdict, string> = {
  ok: 'All good',
  attention: 'Worth a look',
  action: 'Needs action',
}

export type Tone = 'pos' | 'warn' | 'neg' | 'accent' | 'neutral'

/**
 * The badge for a checkup: its verdict once done, otherwise where it is. A
 * failed checkup has no verdict, and is never shown as a good one.
 */
export function verdictBadge(c: Pick<CheckupSummary, 'status' | 'verdict'>): { label: string; tone: Tone } {
  switch (c.status) {
    case 'failed':
      return { label: 'Did not finish', tone: 'neg' }
    case 'requested':
      return { label: 'Waiting', tone: 'neutral' }
    case 'running':
      return { label: 'Running', tone: 'accent' }
  }
  switch (c.verdict) {
    case 'ok':
      return { label: VERDICT_LABEL.ok, tone: 'pos' }
    case 'attention':
      return { label: VERDICT_LABEL.attention, tone: 'warn' }
    case 'action':
      return { label: VERDICT_LABEL.action, tone: 'neg' }
  }
  return { label: c.verdict || 'No verdict', tone: 'neutral' }
}

/** An item's badge: the same words as the verdict it drives, so "Needs action" means one thing on the page. */
export const STATE_LABEL: Record<CheckupItemState, string> = {
  fail: VERDICT_LABEL.action,
  warn: VERDICT_LABEL.attention,
  info: 'Note',
  ok: 'OK',
  skip: 'Not checked',
}

export function stateBadge(state: string): { label: string; tone: Tone } {
  switch (state) {
    case 'fail':
      return { label: STATE_LABEL.fail, tone: 'neg' }
    case 'warn':
      return { label: STATE_LABEL.warn, tone: 'warn' }
    case 'info':
      return { label: STATE_LABEL.info, tone: 'accent' }
    case 'ok':
      return { label: STATE_LABEL.ok, tone: 'pos' }
    case 'skip':
      return { label: STATE_LABEL.skip, tone: 'neutral' }
  }
  return { label: state || 'unknown', tone: 'neutral' }
}

/** "1 to do · 2 to look at · 9 ok · 1 not checked" — what the history row says; zeros left out. */
export function countsText(counts: CheckupCounts | null | undefined): string {
  if (!counts) return '—'
  const parts: string[] = []
  if (counts.fail > 0) parts.push(`${counts.fail} to do`)
  if (counts.warn > 0) parts.push(`${counts.warn} to look at`)
  if (counts.info > 0) parts.push(`${counts.info} to note`)
  if (counts.ok > 0) parts.push(`${counts.ok} ok`)
  if (counts.skip > 0) parts.push(`${counts.skip} not checked`)
  return parts.length > 0 ? parts.join(' · ') : 'no items'
}

// ---------- grouping ----------

export interface ItemGroups {
  /** fail, then warn, each in the order Sentinel checked them. */
  toDo: CheckupItem[]
  /** info, then any state this page does not know: shown, never dropped or counted as good. */
  info: CheckupItem[]
  ok: CheckupItem[]
  skip: CheckupItem[]
}

/** The page's four sections. Filtering keeps Sentinel's order inside each state. */
export function groupItems(items: readonly CheckupItem[]): ItemGroups {
  const known = new Set<string>(['fail', 'warn', 'info', 'ok', 'skip'])
  const of = (state: string) => items.filter((i) => i.state === state)
  return {
    toDo: [...of('fail'), ...of('warn')],
    info: [...of('info'), ...items.filter((i) => !known.has(i.state))],
    ok: of('ok'),
    skip: of('skip'),
  }
}

/**
 * What the To do section says when it has nothing in it. "Nothing to do" only
 * when the report could say so: a checkup that did not finish, or whose items
 * could not all be read, is no evidence that nothing needs doing.
 */
export function emptyToDoText(c: Pick<CheckupDetail, 'status' | 'itemsUnreadable'>): string {
  if (c.status !== 'done') return 'Nothing listed: this checkup did not finish, so it cannot say that all is well.'
  if (c.itemsUnreadable) return 'Nothing in the part that could be read, which is not proof that nothing needs doing.'
  return 'Nothing to do.'
}

/**
 * An item's link, only when it is a path inside this console. Sentinel builds
 * them ("/admin/incidents"), but the column is text: anything else (a full
 * URL, "//host", "javascript:…") is not turned into a link.
 */
export function itemLink(link: string | null | undefined): string | null {
  const path = link?.trim()
  if (!path || !path.startsWith('/') || path.startsWith('//') || path.includes('\\')) return null
  return path
}

// ---------- staleness ----------

/**
 * How old the last done checkup may be before the page says so. The end-of-day
 * checkup runs every night at 00:15 IST, weekends included, so 26 h is one
 * missed night and a two-hour margin.
 */
export const STALE_HOURS = 26

export const SENTINEL_STATUS_COMMAND = 'systemctl status algotrading-sentinel'

/**
 * A warning when the desk has not been checked recently, or null.
 *
 * `null` means the API says no checkup has ever finished; `undefined` that
 * the page does not know yet (nothing loaded), which says nothing either way.
 */
export function staleNote(
  lastCompletedUtc: string | null | undefined,
  nowMs: number,
  formatIst: (iso: string) => string,
): string | null {
  if (lastCompletedUtc === undefined) return null
  if (lastCompletedUtc === null) {
    return `No checkup has finished yet; Sentinel runs them. Check: ${SENTINEL_STATUS_COMMAND}`
  }
  const t = Date.parse(lastCompletedUtc)
  if (Number.isNaN(t)) return null
  if (nowMs - t < STALE_HOURS * 3_600_000) return null
  return `No checkup since ${formatIst(lastCompletedUtc)} IST; Sentinel runs them. Check: ${SENTINEL_STATUS_COMMAND}`
}

// ---------- run a checkup now ----------

/** How often the page asks for the latest checkup while it waits for one. */
export const WAIT_POLL_MS = 5_000

/** How long it waits before saying Sentinel did not answer. */
export const WAIT_TIMEOUT_MS = 3 * 60_000

export type WaitOutcome = 'waiting' | 'done' | 'timeout'

/**
 * Where a "run now" stands. Done once the newest finished checkup is the one
 * asked for or a later one: Sentinel completes a request in place, and "latest"
 * is by number, so anything at or past the id answered by the run is newer
 * than what was on screen when the button was pressed.
 */
export function waitOutcome(input: {
  runId: number
  startedMs: number
  nowMs: number
  latestId: number | null | undefined
}): WaitOutcome {
  if (input.latestId != null && input.latestId >= input.runId) return 'done'
  if (input.nowMs - input.startedMs >= WAIT_TIMEOUT_MS) return 'timeout'
  return 'waiting'
}

/**
 * What the page says when the wait runs out. A request still waiting was not
 * picked up; one that is running was, and saying otherwise would send someone
 * to restart a Sentinel that is working.
 */
export function waitTimeoutMessage(pendingStatus: string | null | undefined): string {
  if (pendingStatus === 'running') {
    return (
      'Sentinel started the checkup but it has not finished after 3 minutes. It will show here when it does; ' +
      `if it never does, check: ${SENTINEL_STATUS_COMMAND}`
    )
  }
  return `Sentinel did not pick it up. On the server: ${SENTINEL_STATUS_COMMAND}`
}

/** What the page says about a checkup in progress it did not start itself. */
export function pendingText(p: Pick<CheckupSummary, 'slot' | 'status' | 'requestedBy'>): string {
  const what =
    p.slot === 'on-request'
      ? `A checkup${p.requestedBy ? ` asked for by ${p.requestedBy}` : ''}`
      : `The scheduled checkup (${slotLabel(p.slot)})`
  return p.status === 'requested' ? `${what} is waiting for Sentinel to pick it up.` : `${what} is running now.`
}

// ---------- reading the API ----------

/** The query string for GET /api/Checkups. */
export function checkupsQuery(take: number): string {
  const n = Number.isFinite(take) ? Math.round(take) : 30
  return new URLSearchParams({ take: String(n) }).toString()
}

/** The `?id=` in the page's URL, when it is a checkup number. */
export function checkupIdParam(raw: string | null): number | null {
  if (!raw || !/^\d+$/.test(raw)) return null
  const n = Number(raw)
  return Number.isSafeInteger(n) && n > 0 ? n : null
}

/**
 * The list endpoint's body, read strictly: an array or it throws, so the
 * history shows an error rather than an empty table that says nothing ever ran.
 */
export function readCheckupList(body: unknown): CheckupSummary[] {
  if (Array.isArray(body)) return body as CheckupSummary[]
  throw new Error('The checkups endpoint answered in a shape this page cannot read.')
}

/**
 * The latest endpoint's body, read strictly. All three keys must be there
 * (null is an answer, missing is not): a body without them would otherwise
 * render as "no checkup ever", "nothing pending" and "never checked", three
 * facts nobody established.
 */
export function readCheckupLatest(body: unknown): CheckupLatest {
  if (body && typeof body === 'object' && 'latest' in body && 'pending' in body && 'lastCompletedUtc' in body) {
    return body as CheckupLatest
  }
  throw new Error('The latest-checkup endpoint answered in a shape this page cannot read.')
}
