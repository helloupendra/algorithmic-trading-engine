/**
 * What a connector page's "What we take from …" panel may show for the answer
 * it has so far.
 *
 * The API decides each item's state (ProviderUsageRules on the server). What
 * is decided here is how an answer that is missing or out of date is shown,
 * because that is where this console has gone wrong before: a "not known yet"
 * rendered as "off" or "stopped". So:
 *
 * - Before the first answer every row reads "Checking…", never Off.
 * - If the first request fails, every row reads Unknown.
 * - If a later refresh fails, the last answer stays on screen but is marked as
 *   last known, since the feed may have started or stopped since.
 */

import type { ProviderUsage, ProviderUsageMarket, ProviderUsageState } from './types'

export type UsageTone = 'pos' | 'neutral' | 'warn' | 'muted'

export interface UsageBadge {
  label: string
  tone: UsageTone
}

export interface UsageRow {
  id: string
  label: string
  badge: UsageBadge
  summary: string
}

export type UsageView =
  /** No answer yet: the rows are placeholders and claim nothing. */
  | { kind: 'checking'; rows: UsageRow[] }
  /** The first request failed, so nothing is known. */
  | { kind: 'failed'; rows: UsageRow[]; error: unknown }
  /** `stale`: an earlier answer, kept because the latest refresh failed. */
  | { kind: 'ready'; rows: UsageRow[]; stale: boolean; checkedUtc: string; markets: string }

/** The items the API returns, in its order, for the rows shown before it answers. */
export const USAGE_ITEMS: { id: string; label: string }[] = [
  { id: 'session', label: 'Session' },
  { id: 'liveTicks', label: 'Live ticks' },
  { id: 'quotes', label: 'Quotes' },
  { id: 'depth', label: 'Bid/ask depth' },
  { id: 'openInterest', label: 'Open interest' },
  { id: 'greeks', label: 'Greeks' },
  { id: 'optionChain', label: 'Option chain' },
  { id: 'history', label: 'History' },
  { id: 'instruments', label: 'Instruments' },
  { id: 'orders', label: 'Orders' },
]

const BADGES: Record<ProviderUsageState | 'checking', UsageBadge> = {
  on: { label: 'On', tone: 'pos' },
  idle: { label: 'Idle', tone: 'neutral' },
  off: { label: 'Off', tone: 'warn' },
  unknown: { label: 'Unknown', tone: 'warn' },
  'not-offered': { label: 'Not offered', tone: 'muted' },
  checking: { label: 'Checking…', tone: 'neutral' },
}

export function usageBadge(state: ProviderUsageState | 'checking'): UsageBadge {
  // A state this build does not know is not a state it may guess at.
  return BADGES[state] ?? BADGES.unknown
}

/** "NSE open · MCX closed for Diwali". A market whose session could not be read says so. */
export function marketsLine(markets: { nse: ProviderUsageMarket; mcx: ProviderUsageMarket }): string {
  const describe = (name: string, m: ProviderUsageMarket) =>
    m.open == null
      ? `${name} hours unknown`
      : m.open
        ? `${name} open`
        : m.holiday
          ? `${name} closed for ${m.holiday}`
          : `${name} closed`
  return `${describe('NSE', markets.nse)} · ${describe('MCX', markets.mcx)}`
}

/** The parts of a TanStack query result this reads. */
export interface UsageQueryState {
  isPending: boolean
  isError: boolean
  error: unknown
  data: ProviderUsage | undefined
}

export function usageView(query: UsageQueryState): UsageView {
  if (query.data === undefined) {
    if (query.isPending && !query.isError) {
      return {
        kind: 'checking',
        rows: USAGE_ITEMS.map((i) => ({ ...i, badge: usageBadge('checking'), summary: '' })),
      }
    }
    return {
      kind: 'failed',
      error: query.error,
      rows: USAGE_ITEMS.map((i) => ({ ...i, badge: usageBadge('unknown'), summary: 'could not be checked' })),
    }
  }

  const stale = query.isError
  return {
    kind: 'ready',
    stale,
    checkedUtc: query.data.checkedUtc,
    markets: marketsLine(query.data.markets),
    rows: query.data.items.map((item) => {
      const badge = usageBadge(item.state)
      // What a connector declares does not go out of date; what it is doing does.
      const lastKnown = stale && item.state !== 'not-offered'
      return {
        id: item.id,
        label: item.label,
        badge: lastKnown ? { label: `${badge.label} (last known)`, tone: 'warn' } : badge,
        summary: item.summary,
      }
    }),
  }
}
