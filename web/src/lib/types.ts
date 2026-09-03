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

// ---------- Backtesting ----------

export type BacktestStatus = 'Pending' | 'Running' | 'Completed' | 'Failed' | 'Stopped'

/** One row of GET /api/Backtest/coverage — what the index has at a resolution. */
export interface BacktestCoverageResolution {
  /** Canonical candle-table code: "1" | "5" | "15" | "D". */
  resolution: string
  /** Strategy-facing label: "1m" | "5m" | "15m" | "1D". */
  label: string
  /** Declared by the strategy's data requirements (or the driver resolution). */
  required: boolean
  barCount: number
  firstUtc: string | null
  lastUtc: string | null
  /** Distinct IST trading days with at least one bar. */
  sessions: number
  source: 'backfill' | 'live' | 'none'
  backfillable: boolean
}

export interface BacktestOptionCoverage {
  symbols: number
  firstUtc: string | null
  lastUtc: string | null
  expiries: string[]
}

export interface BacktestCoverageResponse {
  underlying: string
  spotSymbol: string
  lotSize: number
  lotSizeSource: 'master' | 'configured' | 'unknown'
  resolutions: BacktestCoverageResolution[]
  /** Strategy-facing codes ("5m") the catalog entry declares, plus the driver. */
  requiredResolutions: string[]
  optionCandles: BacktestOptionCoverage
  /** FYERS session valid — a backfill can be attempted. */
  brokerLinked: boolean
  notes: string[]
}

export interface BacktestBackfillRequest {
  underlying: string
  /** Canonical codes, e.g. ["5", "1"]. */
  resolutions: string[]
  fromDate: string
  toDate: string
}

export interface BacktestBackfillResolutionResult {
  resolution: string
  candlesFetched: number
  chunks: number
  skippedChunks: number
}

export interface BacktestBackfillResponse {
  perResolution: BacktestBackfillResolutionResult[]
  message: string
}

/** POST /api/Backtest/runs body. Dates are IST calendar days ("yyyy-MM-dd"). */
export interface StartBacktestRequest {
  strategyId: number
  underlying: string
  resolution: string
  fromDate: string
  toDate: string
  lots?: number
  stopLoss?: number | null
  target?: number | null
  /** "HH:MM" IST; empty string = no end-of-day square-off. */
  eodSquareOffIst?: string
  chargesPerLot?: number
  parameters?: Record<string, unknown> | null
  initialCapital?: number
}

export interface StartBacktestResponse {
  runId: number
  message: string
}

/** GET /api/Backtest/runs — one OfflineReplay run per row, newest first. */
export interface BacktestRunSummary {
  runId: number
  strategyName: string
  strategyId: number
  underlying: string
  spotSymbol: string
  resolution: string
  fromDate: string
  toDate: string
  lots: number
  stopLoss: number | null
  target: number | null
  status: BacktestStatus
  progressPercent: number
  /** Realized P&L net of charges — the detail view's pnl.total for a finished run. */
  netPnl: number
  trades: number
  winRatePercent: number
  /** Why the replay ended early (SL/target trip, user stop, runner exit); null when it ran the whole range. */
  stopReason: string | null
  createdUtc: string
  startedUtc: string | null
  completedUtc: string | null
  startedBy: string | null
  lastError: string | null
}

export interface BacktestProgress {
  percent: number
  barsProcessed: number
  totalBars: number
  currentUtc: string | null
  trades: number
  message: string | null
}

export interface BacktestPnl {
  realized: number
  unrealized: number
  total: number
  charges: number
  returnPercent: number
}

export interface BacktestMetrics {
  closedPositions: number
  winning: number
  losing: number
  winRatePercent: number
  grossProfit: number
  grossLoss: number
  profitFactor: number
  averageWin: number
  averageLoss: number
  expectancy: number
  maxDrawdownPercent: number
  maxDrawdownAmount: number
  largestWin: number
  largestLoss: number
  tradingDays: number
  profitableDays: number
}

export interface BacktestDailyPnl {
  /** IST calendar day, "yyyy-MM-dd". */
  date: string
  pnl: number
  trades: number
}

/** Same row as the live position, plus how and at what price it was closed. */
export interface BacktestPosition {
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
  exitPrice: number | null
  pnl: number
  openedUtc: string
  closedUtc: string | null
  exitReason: string | null
}

export interface BacktestEquityPoint {
  atUtc: string
  equity: number
  realized: number
  unrealized: number
}

/** GET /api/Backtest/runs/{id} — everything the results page shows. */
export interface BacktestRunView {
  runId: number
  strategyId: number
  strategyName: string
  underlying: string
  spotSymbol: string
  resolution: string
  fromDate: string
  toDate: string
  lots: number
  lotSize: number
  lotSizeSource: 'master' | 'configured' | 'unknown'
  stopLoss: number | null
  target: number | null
  eodSquareOffIst: string | null
  chargesPerLot: number
  initialCapital: number
  parametersJson: string
  status: BacktestStatus
  lastError: string | null
  startedUtc: string | null
  completedUtc: string | null
  stopReason: string | null
  progress: BacktestProgress | null
  pnl: BacktestPnl
  metrics: BacktestMetrics
  daily: BacktestDailyPnl[]
  positions: BacktestPosition[]
  activity: LiveActivity[]
  dataNotes: string[]
  equityCurve: BacktestEquityPoint[]
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
