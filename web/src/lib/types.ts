/**
 * DTO shapes returned by AlgoTrading.Api.
 *
 * These mirror the C# contracts in src/AlgoTrading.Contracts, serialized with
 * ASP.NET's default web JSON options (camelCase). Keep property names in sync
 * with the API — the compiler is the only thing standing between a renamed C#
 * property and a column of `undefined` in a table.
 */

// ---------- Live data ----------

export interface LiveWatchlistItem {
  id: number
  symbol: string
  dataType: string
  isActive: boolean
  priority: number
  createdUtc: string
  updatedUtc: string
}

export interface LiveQuote {
  symbol: string
  dataType: string
  lastTradedPrice: number | null
  open: number | null
  high: number | null
  low: number | null
  close: number | null
  volume: number | null
  updatedUtc: string
  /** Patched in from SignalR ticks; the REST snapshot does not carry them. */
  bidPrice?: number | null
  askPrice?: number | null
}

export interface LiveBar {
  symbol: string
  resolution: string
  barStartUtc: string
  open: number
  high: number
  low: number
  close: number
  volumeDelta: number
  tickCount: number
  updatedUtc: string
}

export interface LiveTick {
  symbol: string
  dataType: string
  receivedUtc: string
  exchangeTimestampUtc: string | null
  lastTradedPrice: number | null
  bidPrice: number | null
  askPrice: number | null
  bidSize: number | null
  askSize: number | null
  open: number | null
  high: number | null
  low: number | null
  prevClose: number | null
  volume: number | null
}

export interface IngestorStatus {
  sourceName: string
  status: string
  lastHeartbeatUtc: string
  lastWatchlistRefreshUtc: string | null
  currentSubscribedSymbols: string[]
  lastError: string | null
  updatedUtc: string
  isHealthy: boolean
}

export interface StaleQuote {
  symbol: string
  dataType: string
  lastTradedPrice: number | null
  updatedUtc: string
  ageSeconds: number
}

// ---------- Instruments & derivatives ----------

export interface Instrument {
  id: number
  symbol: string
  exchange: string
  segment: string
  description: string
  instrumentType: string
  isin: string | null
  lotSize: number | null
  tickSize: number | null
  expiryDate: string | null
  isEnabled: boolean
  underlying: string | null
  strikePrice: number | null
  optionType: string | null
}

export interface DerivativeExpiry {
  underlying: string
  expiryDate: string
}

export interface OptionChainItem {
  symbol: string
  underlying: string
  expiryDate: string | null
  strikePrice: number | null
  optionType: string | null
  instrumentType: string
  description: string
}

// ---------- Simulator ----------

export interface SimulationRun {
  id: number
  userId: number
  mode: string
  symbol: string
  resolution: string
  fromUtc: string | null
  toUtc: string | null
  replaySpeed: string
  status: string
  strategyName: string
  parametersJson: string
  createdUtc: string
  startedUtc: string | null
  completedUtc: string | null
  lastError: string
  initialCapital: number
}

export interface SimulationSignal {
  id: number
  simulationRunId: number
  strategyName: string
  signalType: string
  timestampUtc: string
  symbol: string
  price: number | null
  groupId: string
  metadataJson: string
  createdUtc: string
}

export interface PaperOrder {
  id: number
  simulationRunId: number
  simulationSignalId: number | null
  strategyName: string
  groupId: string
  symbol: string
  side: string
  quantity: number
  orderType: string
  status: string
  requestedPrice: number | null
  fillPrice: number | null
  createdUtc: string
  filledUtc: string | null
}

export interface PaperPosition {
  id: number
  simulationRunId: number
  strategyName: string
  groupId: string
  symbol: string
  direction: string
  quantity: number
  averagePrice: number
  lastMarkPrice: number | null
  realizedPnl: number
  unrealizedPnl: number
  status: string
  openedUtc: string
  closedUtc: string | null
  updatedUtc: string
}

export interface PortfolioGroup {
  groupId: string
  strategyName: string
  openPositionCount: number
  closedPositionCount: number
  usedCapital: number
  realizedPnl: number
  unrealizedPnl: number
  status: string
}

export interface SimulationPortfolio {
  simulationRunId: number
  strategyName: string
  runStatus: string
  initialCapital: number
  usedCapital: number
  availableCapital: number
  realizedPnl: number
  unrealizedPnl: number
  totalPnl: number
  currentEquity: number
  returnPercent: number
  totalOrders: number
  filledOrders: number
  openPositions: number
  closedPositions: number
  groups: PortfolioGroup[]
}

export interface EquitySnapshot {
  snapshotUtc: string
  initialCapital: number
  usedCapital: number
  availableCapital: number
  realizedPnl: number
  unrealizedPnl: number
  totalPnl: number
  currentEquity: number
  openPositions: number
  closedPositions: number
}

export interface PerformanceMetrics {
  simulationRunId: number
  initialCapital: number
  currentEquity: number
  totalReturnPercent: number
  maxDrawdownPercent: number
  totalClosedPositions: number
  winningPositions: number
  losingPositions: number
  winRatePercent: number
  averageWin: number
  averageLoss: number
  grossProfit: number
  grossLoss: number
  profitFactor: number
  expectancy: number
}

// ---------- Strategies ----------

export interface StrategyDataRequirement {
  symbolType: string
  resolution: string
}

/** Why and when the last run of a strategy ended (survives API restarts). */
export interface StrategyLastExit {
  runId: number
  reason: string
  atUtc: string
  underlying: string | null
}

/** GET /api/Strategy — catalog entry decorated with its run state. */
export interface StrategyListItem {
  id: number
  name: string
  description: string
  category: string
  supportedUnderlyings: string[]
  instrumentKind: string
  legsSummary: string
  dataRequirements: StrategyDataRequirement[]
  defaultParametersJson: string
  defaultLots: number
  sourceFile: string
  createdUtc: string
  isActive: boolean
  startedBy: string | null
  startedUtc: string | null
  runId: number | null
  underlying: string | null
  spotSymbol: string | null
  lots: number | null
  stopLoss: number | null
  target: number | null
  processId: number | null
  lastExit: StrategyLastExit | null
}

/** GET /api/Instruments/derivatives/underlyings — what can actually be traded. */
export interface FnoUnderlying {
  underlying: string
  exchange: string
  spotSymbol: string
  lotSize: number
  lotSizeSource: 'master' | 'configured' | 'unknown'
  strikeStep: number
  nextExpiry: string
  expiries: string[]
  optionContracts: number
}

export interface StartStrategyRequest {
  underlying: string
  lots?: number
  stopLoss?: number | null
  target?: number | null
  parameters?: Record<string, unknown> | null
  initialCapital?: number
}

export interface StartStrategyResponse {
  message: string
  processId: number
  runId: number
  underlying: string
  spotSymbol: string
  lots: number
  stopLoss: number | null
  target: number | null
  startedBy: string
}

export interface StopStrategyResponse {
  message: string
  flattened: number
}

export interface LiveContract {
  underlying: string
  strike: number | null
  optionType: string | null
  expiryDate: string | null
  label: string
}

export interface LivePosition {
  id: number
  groupId: string
  symbol: string
  contract: LiveContract | null
  side: 'BUY' | 'SELL'
  lots: number
  lotSize: number
  quantity: number
  status: 'Open' | 'Closed'
  entryPrice: number
  ltp: number | null
  ltpUpdatedUtc: string | null
  pnl: number
  openedUtc: string
  closedUtc: string | null
}

export interface LiveActivity {
  atUtc: string
  type: string
  text: string
  groupId: string
}

/** GET /api/Strategy/{id}/live — the position-based view of one run. */
export interface StrategyLiveView {
  strategyId: number
  name: string
  isActive: boolean
  runId: number | null
  underlying: string | null
  spotSymbol: string | null
  spotLtp: number | null
  spotUpdatedUtc: string | null
  lots: number | null
  lotSize: number | null
  lotSizeSource: 'master' | 'configured' | 'unknown' | null
  stopLoss: number | null
  target: number | null
  startedBy: string | null
  startedUtc: string | null
  stoppedUtc: string | null
  stopReason: string | null
  pnl: { realized: number; unrealized: number; total: number }
  positions: LivePosition[]
  activity: LiveActivity[]
  runner: { processId: number; lastLogUtc: string | null } | null
}

// ---------- Risk / session / system ----------

export interface KillSwitchState {
  isActive: boolean
  updatedBy: string | null
  reason: string | null
  updatedUtc: string | null
}

export interface MarketSessionInfo {
  exchange: string
  segment: string
  utcNow: string
  localNow: string
  isTradingDay: boolean
  isMarketOpen: boolean
  sessionOpenUtc: string
  sessionCloseUtc: string
  nextMarketOpenUtc: string
  timeZoneId: string
}

export interface BrokerSessionInfo {
  broker: string
  isAuthenticated: boolean
  createdUtc?: string
  updatedUtc?: string
}

export interface CandleDto {
  symbol: string
  resolution: string
  timestampUtc: string
  open: number
  high: number
  low: number
  close: number
  volume: number
}

export interface BackfillHistoryResponse {
  symbol: string
  resolution: string
  requestedFromDate: string
  requestedToDate: string
  instrumentExists: boolean
  fullCoverageAfterBackfill: boolean
  missingSlicesFetched: string[]
  /** Note: the API property really is spelled this way. */
  candelsFetchedFromFyers: number
  localCandlesAvailable: number
  message: string
}

// ---------- Market intelligence ----------

export interface NewsItem {
  title: string
  link: string
  source: string
  publishedUtc: string | null
  summary: string | null
}

export interface NewsResponse {
  category: string
  fetchedUtc: string
  items: NewsItem[]
}

export interface Mover {
  symbol: string
  yahooSymbol: string
  lastPrice: number | null
  previousClose: number | null
  changePercent: number | null
}

export interface MoversResponse {
  group: string
  displayName: string
  fetchedUtc: string
  gainers: Mover[]
  losers: Mover[]
  symbolsResolved: number
  symbolsFailed: number
}

export interface EquityGroup {
  id: number
  name: string
  exchange: string
  displayName: string
  description: string
  isEnabled: boolean
  memberCount: number
}
