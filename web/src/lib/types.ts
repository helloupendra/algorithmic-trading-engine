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
  /** When the API wrote the row. Feed health, not price age. */
  updatedUtc: string
  /**
   * When the exchange says the price was made - the last trade.
   *
   * Not the same question as updatedUtc, and on a quiet contract not the same
   * answer: MCX gold futures was written 1s ago and last traded 211s ago while
   * gold mini traded 4s ago. Screens showing "how old is this price" must read
   * this one; "is the feed alive" reads updatedUtc.
   */
  exchangeTimestampUtc?: string | null
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

/**
 * One row of GET /api/Feeds — a live feed per connector that declares live
 * ticks. `key` is the connector key ("fyers", "truedata"); the FYERS row is
 * the same process as /api/Ingestor. `source` means what it does there.
 */
export interface LiveFeed {
  key: string
  displayName: string
  isRunning: boolean
  managed: boolean
  processId: number | null
  source: 'managed' | 'adopted' | 'none'
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
  initialCapital: number
  createdUtc: string
  startedUtc: string | null
  completedUtc: string | null
  lastError: string | null
}

export interface RiskExposureResponse {
  totalUnrealizedPnL: number
  totalRealizedPnL: number
  activeRunsCount: number
  activeRuns: ActiveRunExposure[]
}

export interface ActiveRunExposure {
  runId: number
  strategyName: string
  underlying: string
  unrealizedPnL: number
  realizedPnL: number
  riskRules: RiskRules
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

/** What an overall rule measures over. See {@link OverallRisk.scope}. */
export type RiskScope = 'day' | 'run'

/**
 * ₹ limits on TOTAL P&L (realized + unrealized).
 *
 * `scope` decides what "total" means, and it changes what a backtest tells
 * you. Under `day` (the default) each session is measured from its own opening
 * P&L: a hit squares off and stops trading for that day, and the next morning
 * starts again from zero — the same thing as running the strategy live every
 * day. Under `run` the measure is cumulative and the first hit ends the whole
 * backtest, so a month-long test only ever reports on its first good
 * afternoon. Live runs are one session, so the two agree there.
 *
 * `trailStopLoss` is a give-back from the best P&L seen within that same
 * scope: it arms once profit reaches `trailTrigger` (or as soon as profit is
 * above zero when no trigger is set), then trips at `peak − trailStopLoss`.
 */
export interface OverallRisk {
  stopLoss?: number | null
  target?: number | null
  trailStopLoss?: number | null
  trailTrigger?: number | null
  scope?: RiskScope | null
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
  /** Held while open; for a closed row, the lots that were opened (0 when unknown). */
  lots: number
  lotSize: number
  quantity: number
  status: 'Open' | 'Closed'
  entryPrice: number
  /** Fill price of the closing order; null while open. */
  exitPrice: number | null
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
  /** This position's own stop / target, from the order that opened it. */
  stopLossPrice: number | null
  targetPrice: number | null
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
  /**
   * 'recap' for a run trading an evening replay: its positions, orders and
   * activity are timed by the replayed session, so they read against that
   * day's chart. startedUtc/stoppedUtc stay the real times. Absent on an older
   * API build.
   */
  session?: 'live' | 'recap'
  /** The replayed day (yyyy-MM-dd) of a recap run. */
  recapDate?: string | null
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
  /** 'alerts' for a LogicEngine alerter run (alerts only, no positions); null for a trading run. */
  role: string | null
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

/** One run named by the track record — enough to label it and link to it. */
export interface StrategyTrackRecordRunRef {
  runId: number
  underlying: string
  startedUtc: string
  netPnl: number
}

/** The strategy's record on one underlying. */
export interface StrategyTrackRecordUnderlying {
  underlying: string
  runs: number
  /** Finished trading runs on this underlying — what `wins` is counted out of. */
  decidedRuns: number
  wins: number
  netPnl: number
}

/** How often the runs ended one way, and what those runs came to. */
export interface StrategyTrackRecordStopReason {
  /** The short form: "Stop loss hit", "Market closed", "Runner exited", "Not recorded". */
  reason: string
  runs: number
  netPnl: number
}

/**
 * GET /api/Strategy/{id}/track-record — one strategy's lifetime live record,
 * rolled up over every run it has ever had. Backtests are not in it: a
 * backtest is a hypothesis and a live run is a result.
 */
export interface StrategyTrackRecord {
  strategyId: number
  strategyName: string
  /** "own" when the numbers cover one user's runs (always so for a trader), "all" when everyone's. */
  scope: 'own' | 'all'
  scopeUserId: number | null

  runs: number
  activeRuns: number
  /** Runs that could place orders — everything that is not an alerter. */
  tradingRuns: number
  /** Alerter runs: no orders, no P&L, kept out of every figure below. */
  alertRuns: number

  /** Finished trading runs — the only ones a win or a loss is counted over. */
  decidedRuns: number
  wins: number
  losses: number
  /** Finished flat, usually because the run never opened a position. */
  flat: number
  /** Percent, one decimal; null until a run has finished. */
  winRate: number | null

  /** Σ realized over every trading run, finished or not. */
  netPnl: number
  /** Σ unrealized of the runs still going, at the latest mark. */
  openPnl: number
  grossProfit: number
  /** Negative. */
  grossLoss: number
  averagePnlPerRun: number
  trades: number

  bestRun: StrategyTrackRecordRunRef | null
  worstRun: StrategyTrackRecordRunRef | null

  /** True while live runs carry no charges — the P&L above is then gross of brokerage, STT and slippage. */
  pnlIsGrossOfCharges: boolean

  firstRunUtc: string | null
  lastRunUtc: string | null
  /** Distinct IST days a run started on. */
  tradingDays: number
  totalRuntimeSeconds: number
  averageRuntimeSeconds: number | null

  byUnderlying: StrategyTrackRecordUnderlying[]
  stopReasons: StrategyTrackRecordStopReason[]
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

/** A stock a replay can run on: what is stored for it, from GET /api/Backtest/equities. */
export interface BacktestEquity {
  underlying: string
  symbol: string
  firstUtc: string
  lastUtc: string
  resolutions: Array<{ resolution: string; barCount: number }>
}

export interface BacktestOptionHistoryCoverage {
  firstUtc: string
  lastUtc: string
  calendarExpiries: number
  firstExpiry: string | null
  lastExpiry: string | null
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
  /** Stored expired-options history (option_history_bars + exchange expiry calendar); null when none. */
  optionHistory: BacktestOptionHistoryCoverage | null
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
  /** On the exchange's holiday list today: closed all day, or (MCX) for one session. Absent on older API builds. */
  isHoliday?: boolean
  /** The occasion, e.g. "Ganesh Chaturthi". */
  holidayName?: string | null
  /** 'FullDay' | 'MorningSession' | 'EveningSession' */
  holidayClosure?: string | null
  /** Set when today runs special hours (Muhurat trading, a special Saturday). */
  specialSessionName?: string | null
  /** Set when today's holiday calendar is not loaded, so the answer knows weekends only. */
  calendarWarning?: string | null
}

export interface BrokerSessionInfo {
  broker: string
  isAuthenticated: boolean
  createdUtc?: string
  updatedUtc?: string
  /** When the access token stops working (FYERS: 06:00 IST after issue). Absent when the broker's rule is unknown. */
  expiresAtUtc?: string | null
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
  candlesFetched: number
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

/**
 * GET /api/MarketIntel/news/categories — one tab of the news section. The page
 * builds its tabs from this list rather than holding its own, so a sector added
 * on the server shows up without a change here.
 */
export interface NewsCategory {
  key: string
  label: string
  /** "markets" for the broad feeds, "sectors" for the per-industry ones. */
  group: 'markets' | 'sectors'
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

// ---------- Connectors (data vendors and brokers) ----------

export interface ProviderCapabilities {
  history: boolean
  liveTicks: boolean
  quotes: boolean
  optionChain: boolean
  orders: boolean
  depth: boolean
  openInterest: boolean
  greeks: boolean
  maxStreamSymbols: number | null
  historyMaxDaysPerCall: number | null
  requestsPerMinute: number | null
  resolutions: string[]
  segments: string[]
}

export interface ProviderCredentials {
  /** "database" | "config" | "none" — the secret itself is never returned. */
  source: string
  clientId: string
  redirectUri: string
  hasSecret: boolean
  updatedBy: string | null
  updatedUtc: string | null
  /** The connector's own name for the first field, e.g. "Client ID"; absent means the default for its auth kind. */
  clientIdLabel?: string | null
  /** The connector's own name for the secret field. */
  secretLabel?: string | null
}

export interface ProviderSession {
  isConnected: boolean
  connectedUtc: string | null
  ageSeconds: number | null
  needsReconnect: boolean
  /** When the token stops working, where the broker's rule is known. */
  expiresUtc?: string | null
}

export interface Provider {
  key: string
  displayName: string
  kind: 'Data' | 'Execution' | 'Both'
  auth: 'None' | 'ApiKey' | 'OAuthDaily'
  isDataProvider: boolean
  isBroker: boolean
  /** False for a roadmap vendor with no adapter in this build. */
  isInstalled: boolean
  /** Why an uninstalled connector is listed. Empty for installed ones. */
  plannedNote: string
  /** True once credentials are saved — what "I added this broker" means. */
  isConfigured: boolean
  capabilities: ProviderCapabilities
  credentials: ProviderCredentials
  session: ProviderSession
  suggestedRedirectUri: string
  servingCapabilities: string[]
}

export interface ProviderBinding {
  capability: string
  providerKeys: string[]
  /** True when nothing is configured and the platform is falling back. */
  isFallback: boolean
}

export interface ProviderTestResult {
  providerKey: string
  ok: boolean
  probe: string
  message: string
  barsReturned: number | null
  elapsedMs: number
}

/** "unknown" is a read that failed — never shown as off. */
export type ProviderUsageState = 'on' | 'idle' | 'off' | 'unknown' | 'not-offered'

export interface ProviderUsageItem {
  /** session, liveTicks, quotes, depth, openInterest, greeks, optionChain, history, instruments, orders. */
  id: string
  label: string
  /** Whether the connector declares this kind of data. */
  offered: boolean
  state: ProviderUsageState
  summary: string
  lastUtc: string | null
}

export interface ProviderUsageMarket {
  /** Null when the session could not be read. */
  open: boolean | null
  holiday: string | null
}

/** GET /api/Providers/{key}/usage — what the platform is taking from one connector right now. */
export interface ProviderUsage {
  providerKey: string
  checkedUtc: string
  markets: { nse: ProviderUsageMarket; mcx: ProviderUsageMarket }
  items: ProviderUsageItem[]
}

/** A file-based data vendor an operator added from the console. */
export interface DataVendor {
  id: number
  key: string
  displayName: string
  kind: string
  directory: string
  /** The folder as the server resolves it — the API host's working directory is not the repo root. */
  resolvedDirectory: string
  directoryExists: boolean
  fileCount: number
  isEnabled: boolean
  notes: string
  createdBy: string
  createdUtc: string
  updatedUtc: string
}

export interface SaveDataVendorInput {
  key: string
  displayName: string
  directory: string
  notes: string
  isEnabled: boolean
}

// ---------- Users v2 ----------

export interface PlatformModuleInfo {
  key: string
  name: string
  description: string
  /** Only the admin console has screens for it yet; not offered as a trader grant. */
  adminOnly: boolean
}

export interface UserAdmin {
  id: number
  userName: string
  email: string
  /** "Admin" | "Trader" | "Service" */
  role: string
  isActive: boolean
  totalCapital: number
  /** Null means the platform-wide limit applies. */
  maxConcurrentRuns: number | null
  /** Module keys held. Empty for admins, who have all of them. */
  moduleGrants: string[]
  /** The strategy package this trader is on. Null means none — they can run nothing. */
  strategyPackageId: number | null
  strategyPackageName: string | null
  /** Extra strategies granted on top of the package. */
  strategyGrants: string[]
  activeSessions: number
  createdUtc: string
  lastLoginUtc: string | null
}

export interface UpdateUserInput {
  role?: string
  isActive?: boolean
  totalCapital?: number
  /** -1 clears the override. */
  maxConcurrentRuns?: number
  /** -1 removes the package. */
  strategyPackageId?: number
}

/** A row on the signed-in trader's own watchlist. */
export interface MyWatchlistItem {
  symbol: string
  sortOrder: number
  /** False when the live feed is not subscribed, so the quote will never refresh. */
  isSubscribed: boolean
  lastTradedPrice: number | null
  open: number | null
  high: number | null
  low: number | null
  close: number | null
  volume: number | null
  updatedUtc: string | null
}

// ---------- Strategy packages ----------

export interface StrategyCatalogName {
  name: string
  category: string
  description: string
  supportedUnderlyings: string[]
}

export interface StrategyPackage {
  id: number
  key: string
  name: string
  description: string
  isEnabled: boolean
  /** Covers the whole catalog, including strategies written later. */
  includesAllStrategies: boolean
  maxLotsPerRun: number | null
  maxConcurrentRuns: number | null
  allowedUnderlyings: string[]
  allowLiveMode: boolean
  strategies: string[]
  holderCount: number
  createdBy: string
  updatedUtc: string
}

export interface SaveStrategyPackageInput {
  key: string
  name: string
  description: string
  isEnabled: boolean
  includesAllStrategies: boolean
  maxLotsPerRun: number | null
  maxConcurrentRuns: number | null
  allowedUnderlyings: string[]
  allowLiveMode: boolean
}

// ---------- Invites ----------

export interface UserInvite {
  id: number
  email: string
  suggestedUserName: string
  moduleKeys: string[]
  strategyPackageId: number | null
  createdBy: string
  createdUtc: string
  expiresUtc: string
  acceptedUtc: string | null
  revokedUtc: string | null
  /** "pending" | "accepted" | "revoked" | "expired" */
  status: string
}

export interface CreatedInvite {
  id: number
  email: string
  expiresUtc: string
  /** Shown once. The token is not recoverable afterwards. */
  link: string
  message: string
}

export interface InvitePreview {
  email: string
  suggestedUserName: string
  expiresUtc: string
}

// ---------- Activity log ----------

export interface ActivityLogEntry {
  id: number
  occurredUtc: string
  userId: number | null
  userName: string
  role: string
  module: string
  action: string
  method: string
  path: string
  statusCode: number
  durationMs: number
  succeeded: boolean
  targetType: string | null
  targetId: string | null
  /** A sentence written by the endpoint itself, when the path alone says too little. */
  summary: string | null
  ipAddress: string | null
}

export interface ActivityLogPage {
  total: number
  rows: ActivityLogEntry[]
}

export interface ActivityLogFacets {
  modules: { module: string; count: number }[]
  actions: { action: string; count: number }[]
  users: {
    userId: number | null
    userName: string
    count: number
    failures: number
    lastUtc: string
  }[]
}

export interface ActivityUserSummary {
  total: number
  failures: number
  firstUtc: string | null
  lastUtc: string | null
  byModule: { module: string; count: number; failures: number; lastUtc: string }[]
}


// --- option chain ------------------------------------------------------------

/** One side of one strike, with everything derived already applied. */
export interface OptionChainLeg {
  symbol: string
  lastTradedPrice: number | null
  priceChange: number | null
  priceChangePercent: number | null
  bidPrice: number | null
  askPrice: number | null
  volume: number | null
  openInterest: number | null
  /** Change against the session's first snapshot, not the previous poll. */
  openInterestChange: number | null
  openInterestChangePercent: number | null
  impliedVolatility: number | null
  delta: number | null
  gamma?: number | null
  theta?: number | null
  vega?: number | null
  /** "LongBuildUp" | "ShortBuildUp" | "ShortCovering" | "LongUnwinding" | "Neutral" */
  buildUp: string
  /** The previous day's OI the change is measured from. */
  openInterestBaseline?: number | null
  /** LTP, bid/ask, volume and OI from a live quote no older than the freshness limit. */
  isLive?: boolean
  quoteUpdatedUtc?: string | null
}

export interface OptionChainStrike {
  strikePrice: number
  isAtTheMoney: boolean
  call: OptionChainLeg | null
  put: OptionChainLeg | null
  putCallRatio: number | null
  putCallRatioOfChange: number | null
}

export interface OptionChain {
  underlying: string
  expiryDate: string
  /** The moment this chain describes — now, or the replay clock. */
  asOfUtc: string
  spotPrice: number
  atTheMoneyStrike: number | null
  maxPainStrike: number | null
  putCallRatio: number | null
  totalCallOpenInterest: number
  totalPutOpenInterest: number
  heaviestCallStrike: number | null
  heaviestPutStrike: number | null
  strikes: OptionChainStrike[]
  /**
   * No open interest exists for the period asked for. Open interest is only
   * recorded while the chain poller runs, and it cannot be backfilled — so a
   * replay of an earlier session has prices and volume and nothing else.
   */
  openInterestUnavailable: boolean
  totalCallOpenInterestChange?: number
  totalPutOpenInterestChange?: number
  /** Filled only by /api/OptionChain/view. */
  header?: OptionChainHeader | null
}

/** One price in the chain's header strip, and how much to trust it. */
export interface OptionChainQuote {
  symbol: string
  lastPrice: number | null
  change: number | null
  changePercent: number | null
  previousClose: number | null
  /** "feed" | "dhan-close-field" | null (no previous close known) */
  previousCloseBasis: string | null
  asOfUtc: string | null
  sourceKey: string | null
  isLive: boolean
  /** "live-quote" | "last-quote" | "snapshot" */
  basis: string
}

export interface OptionChainFuture extends OptionChainQuote {
  expiryDate: string | null
  premiumOverSpot: number | null
  premiumPercent: number | null
}

export interface OptionChainHeader {
  /** "live" | "snapshot" | "replay" */
  mode: string
  serverUtc: string
  exchange: string
  marketOpen: boolean
  spot: OptionChainQuote | null
  /** The spot is a futures contract (MCX commodities). */
  spotIsFuture: boolean
  future: OptionChainFuture | null
  vix: OptionChainQuote | null
  atTheMoneyStrike: number | null
  maxPainStrike: number | null
  putCallRatio: number | null
  putCallRatioOfChange: number | null
  supportStrike: number | null
  supportOpenInterest: number | null
  resistanceStrike: number | null
  resistanceOpenInterest: number | null
  totalCallOpenInterest: number
  totalPutOpenInterest: number
  totalCallOpenInterestChange: number
  totalPutOpenInterestChange: number
  atTheMoneyIv: number | null
  daysToExpiry: number | null
  lotSize: number | null
  lotSizeSource: string | null
  spotBetweenLower: number | null
  spotBetweenUpper: number | null
  snapshotCapturedUtc: string | null
  snapshotSourceKey: string | null
  liveOverlayUtc: string | null
  liveSourceKey: string | null
  liveLegs: number
  totalLegs: number
  freshSeconds: number
}

export interface OptionChainTrendPoint {
  capturedUtc: string
  spotPrice: number
  callOpenInterest: number
  putOpenInterest: number
  callOpenInterestChange: number
  putOpenInterestChange: number
  putCallRatio: number | null
}

export interface OptionChainTrend {
  underlying: string
  expiryDate: string | null
  sessionDate: string | null
  captures: number
  points: OptionChainTrendPoint[]
}

export interface OptionChainSeriesPoint {
  capturedUtc: string
  spotPrice: number
  callLastTradedPrice: number | null
  putLastTradedPrice: number | null
  callOpenInterest: number | null
  putOpenInterest: number | null
  callOpenInterestChange: number | null
  putOpenInterestChange: number | null
  /** Put minus call OI change. */
  openInterestChangeDifference: number | null
  callVolume: number | null
  putVolume: number | null
}

export interface OptionChainSeries {
  underlying: string
  expiryDate: string
  strikePrice: number
  points: OptionChainSeriesPoint[]
  openInterestUnavailable: boolean
}


/** The chain poller's process, and whether it is actually recording. */
export interface ChainPollerStatus {
  isRunning: boolean
  managed: boolean
  processId: number | null
  /** "managed" | "adopted" | "none" */
  source: string
  /**
   * When the chain was last written. Reported next to the process status
   * because they are not the same thing: a poller that started but cannot reach
   * the broker is up, healthy-looking, and recording nothing.
   */
  lastCapturedUtc: string | null
}

/** One FYERS symbol master (NSE_CM, NSE_FO, BSE_CM, BSE_FO, MCX_COM) as GET /api/Instruments/masters reports it. */
export interface InstrumentMasterStatus {
  name: string
  exchange: string
  segment: string
  label: string
  url: string
  filePresent: boolean
  fileBytes: number | null
  fileModifiedUtc: string | null
  /** Instruments in the database for this exchange + segment, expired included. */
  rowsInDb: number
  /** Of those, contracts that have not expired (cash rows never expire). */
  activeRowsInDb: number
  lastRefresh: InstrumentMasterRefreshResult | null
}

export interface InstrumentMasterRefreshResult {
  name: string
  startedUtc: string
  finishedUtc: string | null
  ok: boolean
  error: string | null
  downloadedBytes: number | null
  totalRowsRead: number
  inserted: number
  updated: number
  skipped: number
  message: string | null
  by: string | null
}

export interface InstrumentMasterJob {
  isRunning: boolean
  startedUtc: string | null
  finishedUtc: string | null
  /** Master being downloaded or imported right now. */
  current: string | null
  startedBy: string | null
  results: InstrumentMasterRefreshResult[]
}

export interface InstrumentMastersResponse {
  directory: string
  masters: InstrumentMasterStatus[]
  job: InstrumentMasterJob
}

/** A recording-list row the feed can no longer serve (GET /api/LiveData/watchlist/stale). */
export interface StaleWatchlistItem {
  id: number
  symbol: string
  /** 'expired' (contract expiry behind us) or 'silent' (no tick this session while the feed flows). */
  reason: 'expired' | 'silent'
  detail: string
  expiryDate: string | null
  lastTickUtc: string | null
}

export interface StaleWatchlistResponse {
  feedAlive: boolean
  sessionStartUtc: string | null
  items: StaleWatchlistItem[]
}

export interface PruneWatchlistResponse {
  removed: StaleWatchlistItem[]
  message: string
}

/* --- market pulse ------------------------------------------------------- */
export interface MarketPulseItem {
  symbol: string
  name: string
  /** For a future, the contract month shown ("Sep 2026"). */
  contract: string | null
  lastTradedPrice: number | null
  previousClose: number | null
  open: number | null
  high: number | null
  low: number | null
  volume: number | null
  change: number | null
  changePercent: number | null
  updatedUtc: string | null
  isSubscribed: boolean
}

export interface MarketPulseGroup {
  key: 'index' | 'equity' | 'commodity' | string
  title: string
  items: MarketPulseItem[]
}

export interface MarketPulseResponse {
  groups: MarketPulseGroup[]
  latestQuoteUtc: string | null
}

/* --- trader's own broker --------------------------------------------- */
/** A trader's account at the simulated broker, as the platform records it. */
export interface SimBrokerAccountLink {
  userId: number
  clientId: string
  appId: string
  staticIps: string[]
  isEnabled: boolean
  createdBy: string
  createdUtc: string
}

export interface SimBrokerFunds {
  netDeposits: number
  ledgerBalance: number
  realisedToday: number
  chargesToday: number
  cash: number
  orderMargin: number
  positionMargin: number
  unrealised: number
  available: number
}

export interface SimBrokerPosition {
  symbol: string
  product: string
  quantity: number
  averagePrice: number
  lastPrice: number | null
  unrealised: number
  realisedToday: number
  chargesToday: number
  netToday: number
  margin: number
}

export interface SimBrokerOrder {
  orderId: string
  symbol: string
  side: string
  quantity: number
  filledQuantity: number
  pendingQuantity: number
  type: string
  product: string
  limitPrice: number | null
  triggerPrice: number | null
  averagePrice: number | null
  status: string
  rejectionCode: string | null
  message: string | null
  tag: string | null
  blockedMargin: number
  placedAt: string
}

export interface SimBrokerKillSwitch {
  active: boolean
  since: string | null
  until: string | null
  setBy: string | null
}

/** What one account is doing, read from the broker on every request. */
export interface SimBrokerAccountSnapshot {
  link: SimBrokerAccountLink
  funds: SimBrokerFunds | null
  positions: SimBrokerPosition[]
  orders: SimBrokerOrder[]
  killSwitch: SimBrokerKillSwitch | null
  /** What could not be read, in the broker's own words. Never shown as zero. */
  warnings: string[]
}

/** The credentials an account signs in with. Asked for explicitly, never carried by a page. */
export interface SimBrokerCredentials {
  clientId: string
  appId: string
  appSecret: string
  totpSecret: string
  totpUri: string
}

/** What the admin's user row gets: the account, or plainly that there is none yet. */
export interface SimBrokerAccountResponse {
  linked: boolean
  account: SimBrokerAccountSnapshot | null
}

/** What the trader's own Account page gets. */
export interface TraderSimBrokerResponse {
  linked: boolean
  account?: SimBrokerAccountSnapshot | null
  message?: string
  code?: string
}
/** An open paper position on a contract of the chain's underlying (GET /api/OptionChain/positions). */
export interface OptionChainPosition {
  runId: number
  strategyName: string
  isManual: boolean
  userName: string
  groupId: string
  symbol: string
  instrumentType: string
  strikePrice: number | null
  expiryDate: string | null
  direction: 'LONG' | 'SHORT' | string
  quantity: number
  lotSize: number
  averagePrice: number
  markPrice: number | null
  markUtc: string | null
  unrealizedPnl: number | null
  stopLossPrice: number | null
  targetPrice: number | null
  openedUtc: string
}

/* --- market structure (Smart Money Concepts) -------------------------- */

export interface SmcCandle {
  timestampUtc: string
  open: number
  high: number
  low: number
  close: number
  volume: number
  live: boolean
}

export interface SmcSwing {
  kind: 'high' | 'low'
  /** "HH", "HL", "LH", "LL", or null for the first swing of its kind. */
  label: string | null
  price: number
  timeUtc: string
  /** The candle that made it a structural point: nothing is drawn before this. */
  confirmedTimeUtc: string
  /** True for a swing the structure turned on, as against a pullback inside a leg. */
  major: boolean
}

export interface SmcEvent {
  kind: 'BOS' | 'CHOCH'
  direction: 'bullish' | 'bearish'
  level: number
  levelTimeUtc: string
  breakTimeUtc: string
  breakPrice: number
}

export interface SmcInducement {
  kind: 'high' | 'low'
  level: number
  timeUtc: string
  sweptTimeUtc: string | null
  /** Where the leg ended for one that was never taken; null while it still stands. */
  endedTimeUtc: string | null
}

/**
 * The candle behind the impulse that broke a level. It is confirmed by the
 * break, so it appears well after its own candle and often after price has left
 * the zone — draw it from confirmedTimeUtc, never from timeUtc.
 */
export interface SmcOrderBlock {
  direction: 'bullish' | 'bearish'
  /** The higher edge, whichever way the block faces; bottom the lower. */
  top: number
  bottom: number
  /** The block's own candle: where the box starts. */
  timeUtc: string
  /** The candle that broke structure: nothing is drawn before this. */
  confirmedTimeUtc: string
  /** When price came back and used it up; null while it still stands. */
  mitigatedTimeUtc: string | null
}

/** A band of price the candles either side of a fast move left untouched. */
export interface SmcFairValueGap {
  direction: 'bullish' | 'bearish'
  top: number
  bottom: number
  /** The first of the three candles: where the band starts. */
  timeUtc: string
  /** The third candle, the first that could know the gap: nothing is drawn before this. */
  confirmedTimeUtc: string
  /** When price traded back into it; null while it stands open. */
  filledTimeUtc: string | null
}

/**
 * The stretch of candles one reading held for, break to break. Order flow in
 * the SMC sense — inferred from price — not footprint delta, which this
 * platform's feed cannot give.
 */
export interface SmcOrderFlowRun {
  direction: 'bullish' | 'bearish'
  /** The break that started the run: nothing is drawn before this. */
  fromTimeUtc: string
  /** The break that ended it; null while it still runs, so it draws to the right edge. */
  toTimeUtc: string | null
  /** When this leg's inducement was swept; null until then. */
  inducedTimeUtc: string | null
}

/** One chart's candles plus the structure of the timeframes above it. */
export interface SmcLadder {
  symbol: string
  chart: SmcStructure | null
  /** Highest first; these carry no candles, and so no zones — only their state and line marks. */
  higher: SmcStructure[]
}

export interface SmcStructure {
  symbol: string
  resolution: string
  method: string
  strength: number
  breakOn: string
  inducement: string
  /** "touch", "midpoint" or "close": when price back inside a zone counts as having used it. */
  zones: string
  /** Gaps narrower than this were not reported; zero, the default, applies no threshold. */
  fvgMinSize: number
  /**
   * True when only the zones still standing at the last candle came back —
   * unmitigated blocks and unfilled gaps. The runs are whole either way.
   */
  standingZonesOnly: boolean
  candles: SmcCandle[]
  swings: SmcSwing[]
  events: SmcEvent[]
  inducements: SmcInducement[]
  orderBlocks: SmcOrderBlock[]
  gaps: SmcFairValueGap[]
  orderFlowRuns: SmcOrderFlowRun[]
  trend: 'none' | 'bullish' | 'bearish'
  protectedLevel: number | null
  breakLevel: number | null
  inducementTaken: boolean
  inducementLevel: number | null
  liveCandles: number
  /** Stored candles stamped outside the trading session, which were left out. */
  droppedOutsideSession: number
  note: string | null
}
