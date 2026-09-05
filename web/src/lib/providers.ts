/**
 * Shared vocabulary for the Connectors module — kept out of the page files so
 * both the directory and the detail page can use it without either exporting
 * non-components.
 */

import type { Provider } from './types'

/** The capability flags, in the order an operator thinks about them. */
export const CAPABILITY_LABELS: { key: keyof Provider['capabilities']; label: string }[] = [
  { key: 'history', label: 'History' },
  { key: 'liveTicks', label: 'Live ticks' },
  { key: 'quotes', label: 'Quotes' },
  { key: 'optionChain', label: 'Option chain' },
  { key: 'depth', label: 'Bid/ask depth' },
  { key: 'openInterest', label: 'Open interest' },
  { key: 'greeks', label: 'Greeks' },
  { key: 'orders', label: 'Orders' },
]

export function kindLabel(kind: Provider['kind']): string {
  return kind === 'Both' ? 'Data + Broker' : kind === 'Data' ? 'Data vendor' : 'Broker'
}

/** The short capability list a directory card can show without becoming a table. */
export function capabilitySummary(provider: Provider): string {
  const on: string[] = []
  if (provider.capabilities.history) on.push('history')
  if (provider.capabilities.liveTicks) on.push('live ticks')
  if (provider.capabilities.quotes) on.push('quotes')
  if (provider.capabilities.optionChain) on.push('option chain')
  if (provider.capabilities.orders) on.push('orders')
  return on.length > 0 ? on.join(' · ') : 'nothing declared yet'
}

/** True when the connector needs credentials before it can do anything. */
export function needsCredentials(provider: Provider): boolean {
  return provider.isInstalled && provider.auth !== 'None'
}

/**
 * Usable right now: either it needs no login, or its credentials are saved.
 * This is what the directory means by "active" — not merely "installed".
 */
export function isReady(provider: Provider): boolean {
  return provider.isInstalled && (!needsCredentials(provider) || provider.isConfigured)
}
