/**
 * The names people read for a strategy. A strategy's id (its class name: what runs, backtests, the morning plan,
 * the AI Trader's grants and docs/strategies key on) never changes; only what the console shows does.
 *
 * 28 Nov 2026, the owner's names for the trend family: TrendRider is Opener (the morning range's break),
 * TrendRiderV2 is Breaker (yesterday's high or low), TrendRiderV2BE is Guard (Breaker with the breakeven step),
 * TrendRiderV2OB is Sniper (Guard with the stop at the order block).
 * Same list in src/AlgoTrading.Api/Services/StrategyNames.cs.
 */
const NAMES: Readonly<Record<string, string>> = {
  TrendRider: 'Opener',
  TrendRiderV2: 'Breaker',
  TrendRiderV2BE: 'Guard',
  TrendRiderV2OB: 'Sniper',
}

/** The name to show for a strategy id: the owner's name when it has one, else the id itself. */
export function strategyName(id: string | null | undefined): string {
  if (!id) return id ?? ''
  return NAMES[id] ?? id
}

/** True when the shown name differs from the id, so a page can keep the id visible beside it. */
export function hasOwnName(id: string | null | undefined): boolean {
  return !!id && id in NAMES
}

/** A family's heading in the library: "Trend family" for TrendRider's variants, else "<root> variants". */
export function familyName(root: string): string {
  return root === 'TrendRider' ? 'Trend family' : `${root} variants`
}

/** "Sniper (TrendRiderV2OB)" for a renamed strategy, the id alone otherwise: for tooltips and confirmations. */
export function strategyNameWithId(id: string | null | undefined): string {
  if (!id) return id ?? ''
  return hasOwnName(id) ? `${NAMES[id]} (${id})` : id
}
