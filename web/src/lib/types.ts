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

/**
 * GET /api/Ingestor/status — whether a feed process is alive and how the API
 * knows it. `managed`: spawned by this API instance (pipes attached);
 * `adopted`: a pid persisted by an earlier API instance that is still alive
 * (output not captured); `none`: no pid known — a healthy heartbeat with
 * `none` means the feed was started outside the console. An API build from
 * before supervision hardening answers with `isRunning` only.
 */
export interface IngestorProcessStatus {
  isRunning: boolean
  managed?: boolean
  processId?: number | null
  source?: 'managed' | 'adopted' | 'none'
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

// ---------- Risk rules (live runs and backtests) ----------

/**
 * ₹ limits on the run's TOTAL P&L (realized + unrealized); a hit ends the run.
 *
 * `trailStopLoss` is a give-back from the best total P&L the run ever showed:
 * it arms once profit reaches `trailTrigger` (or as soon as profit is above
 * zero when no trigger is set), then trips at `peak − trailStopLoss`.
 */
export interface OverallRisk {
  stopLoss?: number | null
  target?: number | null
  trailStopLoss?: number | null
  trailTrigger?: number | null
}

/**
 * ₹ limits per group (one OPEN_GROUP, e.g. a straddle pair); a hit closes that
 * group only. Trailing works exactly as at the overall level, per group.
 */
export interface GroupRisk {
  stopLoss?: number | null
  target?: number | null
  trailStopLoss?: number | null
  trailTrigger?: number | null
}

/**
 * Per-leg limits: premium points vs entry and/or % of entry premium; a hit
 * closes that leg only. When both points and percent are set, whichever
 * trips first wins.
 *
 * The trailing pair works the same way per leg, points and percent tracked
 * separately, each against its own peak favourable premium move.
 */
export interface LegRisk {
  stopLossPoints?: number | null
  targetPoints?: number | null
  stopLossPercent?: number | null
  targetPercent?: number | null
  trailStopLossPoints?: number | null
  trailStopLossPercent?: number | null
  trailTriggerPoints?: number | null
  trailTriggerPercent?: number | null
}

/**
 * Risk rules at three levels, evaluated leg → group → overall on every guard
 * sweep (live) or bar (backtest). Every field optional (null = not set); set
 * values must be > 0. Persisted as `parametersJson.risk`; the legacy
 * `stop_loss` / `target` keys mirror the overall values for older readers.
 */
export interface RiskRules {
  overall?: OverallRisk | null
  group?: GroupRisk | null
  leg?: LegRisk | null
}

/** PATCH /api/Strategy/runs/{runId}/risk body — replaces the run's rules. */
export type UpdateRunRiskRequest = RiskRules

export interface UpdateRunRiskResponse {
  runId: number
  risk: RiskRules
}

/** One group of a run: its P&L and how many of its legs are still open. */
export interface LiveRunGroup {
  groupId: string
  pnl: number
  openLegs: number
  closedLegs: number
}

// ---------- Strategies ----------

export interface StrategyDataRequirement {
  /**
   * "index" or any contract-requirement key the strategy declares
   * ("atm_ce", "otm_pe", "wing_ce", …).
   */
  symbolType: string
  resolution: string
}

/**
 * One option contract a strategy asks the runner to resolve for it, from
 * `get_contract_requirements()`. `key` is what the strategy reads out of
 * `inp.contracts`; the strike is `steps` strikes (or `points` points, which
 * wins when set) away from ATM on the underlying's grid, in the direction
 * `moneyness` implies for `optionType`. `param` names a run parameter that
 * overrides that distance — as points when the name ends with `_points`,
 * otherwise as strike steps.
 */
export interface StrategyContractRequirement {
  key: string
  optionType: 'CE' | 'PE' | string
  moneyness: 'atm' | 'otm' | 'itm' | string
  steps: number
  points: number | null
  param: string | null
  /** A missing contract is not an error — the strategy copes with its absence. */
  optional: boolean
}

/** Why and when the last run of a strategy ended (survives API restarts). */
export interface StrategyLastExit {
  runId: number
  reason: string
  atUtc: string
  underlying: string | null
}

/**
 * One live instance of a strategy. A strategy may run on several underlyings
 * at once; every run-scoped route (stop, live, logs, signals) is keyed by runId.
 */
export interface StrategyActiveRun {
  runId: number
  underlying: string
  spotSymbol: string
  lots: number
  stopLoss: number | null
  target: number | null
  startedBy: string
  startedUtc: string
  processId: number
}

/**
 * GET /api/Strategy — catalog entry decorated with its run state.
 *
 * `activeRuns` (oldest first) and `recentExits` (newest first, ≤ 5) are the
 * source of truth; the flat single-run fields mirror the FIRST active run
 * (and `lastExit` the newest exit) for callers that predate multi-instance.
 */
export interface StrategyListItem {
  id: number
  name: string
  description: string
  category: string
  supportedUnderlyings: string[]
  instrumentKind: string
  legsSummary: string
  dataRequirements: StrategyDataRequirement[]
  /**
   * The contracts the strategy trades. Absent on an API build from before
   * contract requirements shipped, and empty for a strategy the catalog could
   * only read with its regex fallback — treat both as "ATM CE + ATM PE".
   */
  contractRequirements?: StrategyContractRequirement[] | null
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
  activeRuns: StrategyActiveRun[]
  recentExits: StrategyLastExit[]
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
  /** Legacy overall stop-loss; mirrors `risk.overall.stopLoss` for older API builds. */
  stopLoss?: number | null
  /** Legacy overall target; mirrors `risk.overall.target` for older API builds. */
  target?: number | null
  risk?: RiskRules | null
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
  /** entry × qty. Absent on an API build from before the risk-rules change. */
  entryValue?: number | null
  /** ltp × qty, open rows only. */
  currentValue?: number | null
  /** Signed premium points from entry (sign = profit). */
  pnlPoints?: number | null
  /** pnlPoints / entry × 100. */
  pnlPercent?: number | null
}

export interface LiveActivity {
  atUtc: string
  type: string
  text: string
  groupId: string
  /**
   * The signal's metadata, when the API forwards it (RISK_UPDATED rows carry
   * `{ risk, by }` and are rendered from it). Older builds send `text` only.
   */
  metadata?: Record<string, unknown> | null
  metadataJson?: string | null
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
  /** Overall stop-loss shorthand (= risk.overall.stopLoss). */
  stopLoss: number | null
  /** Overall target shorthand (= risk.overall.target). */
  target: number | null
  /** The three-level rules; absent on an older API build (use the shorthands). */
  risk?: RiskRules | null
  startedBy: string | null
  startedUtc: string | null
  stoppedUtc: string | null
  stopReason: string | null
  pnl: {
    realized: number
    unrealized: number
    total: number
    /** Portfolio UsedCapital. */
    capitalUsed?: number | null
    /** Σ entryValue of open BUY legs. */
    premiumOutlay?: number | null
    /** Σ entryValue of open SELL legs. */
    premiumReceived?: number | null
  }
  groups?: LiveRunGroup[] | null
  positions: LivePosition[]
  activity: LiveActivity[]
  runner: {
    processId: number
    lastLogUtc: string | null
    /** The runner was adopted after an API restart: alive and controllable, output not captured. */
    adopted?: boolean
  } | null
}

// ---------- Live run history ----------

/**
 * SimulationRun.Status of a live run, copied verbatim by the API. "Stopping"
 * is the window between a stop request and the row being closed (the API's
 * Running filter includes it); "Pending" is a row created but not yet started.
 */
export type LiveRunStatus = 'Pending' | 'Running' | 'Stopping' | 'Stopped' | 'Failed' | 'Completed'

/**
 * GET /api/Strategy/runs — one live (paper) run per row, newest first. Every
 * run a user ever started stays here whatever ended it: a stop-loss, the
 * market close, a manual stop, a runner exit or an API restart. A trader gets
 * their own rows only; an admin gets everyone's (filterable by `userId`).
 */
export interface LiveRunSummary {
  runId: number
  userId: number
  /** Null when the user row no longer exists (deleted user; the run stays). */
  userName: string | null
  strategyId: number
  strategyName: string
  category: string | null
  underlying: string
  spotSymbol: string
  lots: number
  lotSize: number | null
  risk: RiskRules | null
  status: LiveRunStatus
  /** The runner is alive in the registry right now. */
  isActive: boolean
  startedUtc: string | null
  stoppedUtc: string | null
  /** The RUN_STOPPED reason, e.g. "Stop loss hit: P&L −₹5,120 ≤ −₹5,000". */
  stopReason: string | null
  /** Who ended it: a user name, "runner", "api", "system" or "risk-guard". */
  stoppedBy: string | null
  durationSeconds: number | null
  /** Σ realized P&L of every position of the run. */
  netPnl: number
  realizedPnl: number
  /** Open positions at the last mark — only while the run is active, else 0. */
  unrealizedPnl: number
  /** Closed positions. */
  trades: number
  openPositions: number
  groups: number
  chargesPerLot?: number | null
  /** Portfolio UsedCapital; null when unknown. */
  capitalUsed?: number | null
}

/** GET /api/Strategy/runs/summary — per-user rollup for the history header. */
export interface LiveRunUserSummary {
  userId: number
  /** Null when the user row no longer exists (deleted user; the runs stay). */
  userName: string | null
  runs: number
  active: number
  netPnl: number
  lastRunUtc: string | null
}

/** GET /api/Strategy/runs/{runId}/orders — the run's paper order ledger, newest first. */
export type PaperOrderRow = PaperOrder

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
  /** Legacy overall stop-loss; mirrors `risk.overall.stopLoss` for older API builds. */
  stopLoss?: number | null
  /** Legacy overall target; mirrors `risk.overall.target` for older API builds. */
  target?: number | null
  risk?: RiskRules | null
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
  /** Max UsedCapital over the run's equity snapshots (0 when none); absent on older builds. */
  capitalUsed?: number | null
  premiumOutlay?: number | null
  premiumReceived?: number | null
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
  /** entry × qty. Absent on an API build from before the risk-rules change. */
  entryValue?: number | null
  /** mark × qty, open rows only. */
  currentValue?: number | null
  /** Signed premium points from entry (sign = profit). */
  pnlPoints?: number | null
  /** pnlPoints / entry × 100. */
  pnlPercent?: number | null
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
  /** Overall stop-loss shorthand (= risk.overall.stopLoss). */
  stopLoss: number | null
  /** Overall target shorthand (= risk.overall.target). */
  target: number | null
  /** The three-level rules; absent on an older API build (use the shorthands). */
  risk?: RiskRules | null
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

export interface RiskLimits {
  maxOrdersPerMinute: number
  maxDailyLoss: number
  maxConcurrentRuns: number
  maxRunsPerUser: number
  source: string
  updatedBy: string | null
  updatedUtc: string | null
}

export interface RiskEvent {
  id: number
  occurredUtc: string
  kind: string
  actorUserId: number | null
  actorName: string | null
  reason: string | null
  detailsJson: string | null
  simulationRunId: number | null
  symbol: string | null
}

export interface AlertEvent {
  id: number
  occurredUtc: string
  source: string
  underlying: string
  symbol: string | null
  severity: string
  title: string
  message: string
  metadataJson: string | null
  deliveredToTelegram: boolean
  simulationRunId: number | null
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
