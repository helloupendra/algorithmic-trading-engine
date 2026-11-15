import { Suspense, lazy } from 'react'
import { BrowserRouter, Route, Routes } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { AuthProvider } from './lib/auth'
import { ROUTE_MOVES } from './lib/routeMap'
import { AppLayout } from './components/AppLayout'
import { ByRole, MovedTo, RedirectIfAuthenticated, RequireAuth, RequireRole, WorkspaceLanding } from './components/RouteGuards'
import { LoginPage } from './pages/LoginPage'
import { AcceptInvitePage } from './pages/AcceptInvitePage'
import { LandingPage } from './pages/LandingPage'
import { FallbackShell, ForbiddenPage, NotFoundPage } from './pages/Placeholders'
import { DeskPage } from './pages/desk/DeskPage'
import { TodayPage } from './pages/today/TodayPage'
import { Loading } from './components/ui'
import './styles.css'

// Every page past the two home pages loads when its route is first opened: the
// entry bundle carries the shell, the Desk and Today, not the whole console.
const WatchlistPage = lazy(() => import('./pages/trader/WatchlistPage').then((m) => ({ default: m.WatchlistPage })))
const ChartPage = lazy(() => import('./pages/markets/chart/ChartPage').then((m) => ({ default: m.ChartPage })))
const PositionsPage = lazy(() => import('./pages/trade/PositionsPage').then((m) => ({ default: m.PositionsPage })))
const OrdersPage = lazy(() => import('./pages/trade/OrdersPage').then((m) => ({ default: m.OrdersPage })))
const MarketNewsPage = lazy(() => import('./pages/data/MarketNewsPage').then((m) => ({ default: m.MarketNewsPage })))
const AccountPage = lazy(() => import('./pages/trader/AccountPage').then((m) => ({ default: m.AccountPage })))
const HealthPage = lazy(() => import('./pages/system/health/HealthPage').then((m) => ({ default: m.HealthPage })))
const PeoplePage = lazy(() => import('./pages/system/people/PeoplePage').then((m) => ({ default: m.PeoplePage })))
const RiskV2Page = lazy(() => import('./pages/admin/RiskV2Page').then((m) => ({ default: m.RiskV2Page })))
const BrokerPage = lazy(() => import('./pages/admin/BrokerPage').then((m) => ({ default: m.BrokerPage })))
const ConnectorDetailPage = lazy(() => import('./pages/admin/ConnectorDetailPage').then((m) => ({ default: m.ConnectorDetailPage })))
const PatternAlertsPage = lazy(() => import('./pages/data/PatternAlertsPage').then((m) => ({ default: m.PatternAlertsPage })))
const LevelAlertsPage = lazy(() => import('./pages/markets/alerts/LevelAlertsPage').then((m) => ({ default: m.LevelAlertsPage })))
const MoversPage = lazy(() => import('./pages/markets/movers/MoversPage').then((m) => ({ default: m.MoversPage })))
const MarketFactorsPage = lazy(() => import('./pages/data/MarketFactorsPage'))
const SystemLogPage = lazy(() => import('./pages/system/log/SystemLogPage').then((m) => ({ default: m.SystemLogPage })))
const IncidentsPage = lazy(() => import('./pages/admin/IncidentsPage').then((m) => ({ default: m.IncidentsPage })))
const MarketCalendarPage = lazy(() => import('./pages/admin/MarketCalendarPage').then((m) => ({ default: m.MarketCalendarPage })))
const TraderLibraryPage = lazy(() => import('./pages/strategies/TraderLibraryPage').then((m) => ({ default: m.TraderLibraryPage })))
const ManualOrderPage = lazy(() => import('./pages/trading/ManualOrderPage').then((m) => ({ default: m.ManualOrderPage })))
const FilterLabPage = lazy(() => import('./pages/trading/FilterLabPage').then((m) => ({ default: m.FilterLabPage })))
const LiveRunnerPage = lazy(() => import('./pages/strategies/LiveRunnerPage').then((m) => ({ default: m.LiveRunnerPage })))
const StrategyLibraryPage = lazy(() => import('./pages/strategies/StrategyLibraryPage').then((m) => ({ default: m.StrategyLibraryPage })))
const StrategySpecPage = lazy(() => import('./pages/strategies/StrategySpecPage').then((m) => ({ default: m.StrategySpecPage })))
const RunHistoryPage = lazy(() => import('./pages/strategies/RunHistoryPage').then((m) => ({ default: m.RunHistoryPage })))
const LiveRunDetailPage = lazy(() => import('./pages/strategies/LiveRunDetailPage').then((m) => ({ default: m.LiveRunDetailPage })))
const BacktestOverviewPage = lazy(() => import('./pages/backtesting/BacktestOverviewPage').then((m) => ({ default: m.BacktestOverviewPage })))
const NewBacktestPage = lazy(() => import('./pages/backtesting/NewBacktestPage').then((m) => ({ default: m.NewBacktestPage })))
const BacktestRunsPage = lazy(() => import('./pages/backtesting/BacktestRunsPage').then((m) => ({ default: m.BacktestRunsPage })))
const BacktestRunPage = lazy(() => import('./pages/backtesting/BacktestRunPage').then((m) => ({ default: m.BacktestRunPage })))
const BacktestComparePage = lazy(() => import('./pages/backtesting/BacktestComparePage').then((m) => ({ default: m.BacktestComparePage })))
const AnalysisPage = lazy(() => import('./pages/analysis/AnalysisPage').then((m) => ({ default: m.AnalysisPage })))
const DataOverviewPage = lazy(() => import('./pages/data/DataOverviewPage').then((m) => ({ default: m.DataOverviewPage })))
const OptionChainPage = lazy(() => import('./pages/markets/chain/OptionChainPage').then((m) => ({ default: m.OptionChainPage })))
const StrategyBuilderPage = lazy(() => import('./pages/markets/builder/StrategyBuilderPage').then((m) => ({ default: m.StrategyBuilderPage })))
const LiveFeedsPage = lazy(() => import('./pages/data/LiveFeedsPage').then((m) => ({ default: m.LiveFeedsPage })))
const CommodityPage = lazy(() => import('./pages/data/CommodityPage').then((m) => ({ default: m.CommodityPage })))
const HistoricalDataPage = lazy(() => import('./pages/data/HistoricalDataPage').then((m) => ({ default: m.HistoricalDataPage })))
const InstrumentsFnoPage = lazy(() => import('./pages/data/InstrumentsFnoPage').then((m) => ({ default: m.InstrumentsFnoPage })))
const MutualFundsPage = lazy(() => import('./pages/data/MutualFundsPage').then((m) => ({ default: m.MutualFundsPage })))
const MutualFundReportPage = lazy(() => import('./pages/data/MutualFundReportPage').then((m) => ({ default: m.MutualFundReportPage })))
const MutualFundComparePage = lazy(() => import('./pages/data/MutualFundComparePage').then((m) => ({ default: m.MutualFundComparePage })))
const ReplayPage = lazy(() => import('./pages/data/ReplayPage').then((m) => ({ default: m.ReplayPage })))
const NotebookPage = lazy(() => import('./pages/notebook/NotebookPage').then((m) => ({ default: m.NotebookPage })))
const WhiteboardPage = lazy(() => import('./pages/notebook/WhiteboardPage').then((m) => ({ default: m.WhiteboardPage })))
const AiOverviewPage = lazy(() => import('./pages/ai/AiOverviewPage').then((m) => ({ default: m.AiOverviewPage })))
const AiAssistantPage = lazy(() => import('./pages/ai/AiAssistantPage').then((m) => ({ default: m.AiAssistantPage })))
const AiMemoryPage = lazy(() => import('./pages/ai/AiMemoryPage').then((m) => ({ default: m.AiMemoryPage })))
const AiAgentsPage = lazy(() => import('./pages/ai/AiAgentsPage').then((m) => ({ default: m.AiAgentsPage })))
const AiReportsPage = lazy(() => import('./pages/ai/AiReportsPage').then((m) => ({ default: m.AiReportsPage })))
const AiModelsPage = lazy(() => import('./pages/ai/AiModelsPage').then((m) => ({ default: m.AiModelsPage })))
const AiCallsPage = lazy(() => import('./pages/ai/AiCallsPage').then((m) => ({ default: m.AiCallsPage })))
const IraPage = lazy(() => import('./pages/ira/IraPage').then((m) => ({ default: m.IraPage })))
const PortfolioPage = lazy(() => import('./pages/portfolio/PortfolioPage').then((m) => ({ default: m.PortfolioPage })))

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      // Live trading data goes stale immediately; individual hooks set their
      // own refetchInterval for polling.
      staleTime: 0,
      retry: (failureCount, error) => {
        // Never retry an authorization failure — it will not succeed.
        const status = (error as { status?: number })?.status
        if (status === 401 || status === 403) return false
        return failureCount < 2
      },
    },
  },
})

export default function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>
        <AuthProvider>
          {/* For the lazy pages outside the shell (/ira); the shell has its own
              boundary so it stays on screen while a page loads. */}
          <Suspense fallback={<Loading label="Opening the page…" />}>
            <Routes>
              {/* Public homepage. Signing out lands here; "Open console" goes to
                  /login, which bounces authenticated users to their home
                  (lib/modules.ts homeFor: Today for an admin, the Desk for a trader). */}
              <Route path="/" element={<LandingPage />} />

              <Route element={<RedirectIfAuthenticated />}>
                <Route path="/login" element={<LoginPage />} />
                {/* Someone already signed in is bounced to their own home rather
                    than being allowed to accept an invite over their session. */}
                <Route path="/invite/:token" element={<AcceptInvitePage />} />
              </Route>

              {/* Every URL from before the workspaces, sent on to its new home. */}
              {Object.keys(ROUTE_MOVES).map((from) => (
                <Route key={from} path={from} element={<MovedTo />} />
              ))}

              {/* Ira, the phone app: full screen, outside the console's chrome, installable. Its own gate sends a
                  signed-out visitor to /login and back (an iPhone's home-screen app has storage of its own). */}
              <Route path="/ira" element={<IraPage />} />

              <Route element={<RequireAuth />}>
                {/* Full-window pages: no top bar. A whiteboard inside the console
                    shell was a canvas the size of a postcard; it opens in its own
                    tab and takes the whole viewport. */}
                <Route element={<RequireRole role="Admin" />}>
                  <Route path="/notebook/:id" element={<WhiteboardPage />} />
                </Route>

                {/* A workspace with no page at its own address opens its first tab. */}
                <Route path="/trade" element={<WorkspaceLanding />} />
                <Route path="/research" element={<WorkspaceLanding />} />

                {/* The console. The URLs are the workspace registry's
                    (lib/modules.ts); a page it hides from traders sits in the
                    Admin block below, and routeMap.test.ts checks both. */}
                <Route element={<AppLayout />}>
                  {/* Everyone's home: one sheet for the day; each panel checks its own grant. */}
                  <Route path="/desk" element={<DeskPage />} />

                  {/* Markets. */}
                  <Route path="/markets" element={<WatchlistPage />} />
                  <Route path="/markets/chart" element={<ChartPage />} />
                  {/* One page, a URL per tab. */}
                  <Route path="/markets/chain" element={<OptionChainPage view="chain" />} />
                  <Route path="/markets/chain/oi" element={<OptionChainPage view="oi" />} />
                  <Route path="/markets/chain/levels" element={<OptionChainPage view="levels" />} />
                  <Route path="/markets/chain/straddle" element={<OptionChainPage view="straddle" />} />
                  {/* A position checked against the chain before it is entered: analysis only, no orders. */}
                  <Route path="/markets/builder" element={<StrategyBuilderPage />} />
                  <Route path="/markets/movers" element={<MoversPage />} />
                  <Route path="/markets/factors" element={<MarketFactorsPage />} />
                  <Route path="/markets/news" element={<MarketNewsPage />} />
                  {/* Each user's own price and PCR level alerts; the API answers for the caller alone. */}
                  <Route path="/markets/alerts" element={<LevelAlertsPage />} />

                  {/* Trade. A trader deploys from the library and follows runs in History. */}
                  <Route
                    path="/trade/library"
                    element={<ByRole admin={<StrategyLibraryPage />} trader={<TraderLibraryPage />} />}
                  />
                  <Route
                    path="/trade/library/:id"
                    element={<ByRole admin={<StrategySpecPage />} trader={<StrategySpecPage mode="trader" />} />}
                  />
                  <Route
                    path="/trade/history"
                    element={<ByRole admin={<RunHistoryPage mode="admin" />} trader={<RunHistoryPage mode="trader" />} />}
                  />
                  {/* Any run the API lets the viewer read: it answers 403 for someone else's. */}
                  <Route path="/trade/runs/:runId" element={<LiveRunDetailPage />} />
                  {/* Every open leg and the day's orders, across runs and manual books. */}
                  <Route path="/trade/positions" element={<PositionsPage />} />
                  <Route path="/trade/orders" element={<OrdersPage />} />
                  <Route path="/trade/ticket" element={<ManualOrderPage />} />

                  {/* Every user's own demat and broker accounts, read-only; the API answers for the caller alone. */}
                  <Route path="/portfolio" element={<PortfolioPage />} />

                  {/* Research. */}
                  <Route path="/research/lab" element={<FilterLabPage />} />

                  {/* Outside the workspaces: the trader's own account, from the avatar menu. */}
                  <Route path="/account" element={<AccountPage />} />
                  {/* Where RequireRole sends a user who may not open a page: still their console, one line short. */}
                  <Route path="/forbidden" element={<ForbiddenPage />} />

                  {/* Admin only. */}
                  <Route element={<RequireRole role="Admin" />}>
                    {/* The owner's one page, and an admin's home: the day at a glance, read-only. */}
                    <Route path="/today" element={<TodayPage />} />

                    <Route path="/markets/mcx" element={<CommodityPage />} />
                    <Route path="/markets/patterns" element={<PatternAlertsPage />} />

                    <Route path="/trade/runs" element={<LiveRunnerPage />} />
                    <Route path="/trade/risk" element={<RiskV2Page />} />

                    <Route path="/research/backtests" element={<BacktestOverviewPage />} />
                    <Route path="/research/backtests/new" element={<NewBacktestPage />} />
                    <Route path="/research/backtests/runs" element={<BacktestRunsPage />} />
                    <Route path="/research/backtests/runs/:id" element={<BacktestRunPage />} />
                    <Route path="/research/backtests/compare" element={<BacktestComparePage />} />
                    {/* Forecasts with proof; its sections are tabs on the one page (?section=). */}
                    <Route path="/research/forecasts" element={<AnalysisPage />} />
                    {/* The board list; a board itself is the full-window route above. */}
                    <Route path="/research/notebook" element={<NotebookPage />} />

                    {/* AI: the models, the agents, every call, and the assistant. A call opens in place (?id=). */}
                    <Route path="/ai" element={<AiOverviewPage />} />
                    <Route path="/ai/assistant" element={<AiAssistantPage />} />
                    {/* What the agents read before every answer; a memory is named by its anchor (#memory-12). */}
                    <Route path="/ai/memory" element={<AiMemoryPage />} />
                    <Route path="/ai/agents" element={<AiAgentsPage />} />
                    {/* What the scheduled agents wrote; a report opens in place (?id=). */}
                    <Route path="/ai/reports" element={<AiReportsPage />} />
                    <Route path="/ai/models" element={<AiModelsPage />} />
                    <Route path="/ai/calls" element={<AiCallsPage />} />

                    <Route path="/data" element={<DataOverviewPage />} />
                    <Route path="/data/feeds" element={<LiveFeedsPage />} />
                    <Route path="/data/historical" element={<HistoricalDataPage />} />
                    <Route path="/data/instruments" element={<InstrumentsFnoPage />} />
                    {/* AMFI's schemes and NAVs: coverage first (docs/modules/mutual_funds_module.md). */}
                    <Route path="/data/mutual-funds" element={<MutualFundsPage />} />
                    {/* A fund report and a comparison of 2 to 4; compare comes first so it is never read as a scheme code. */}
                    <Route path="/data/mutual-funds/compare" element={<MutualFundComparePage />} />
                    <Route path="/data/mutual-funds/:code" element={<MutualFundReportPage />} />
                    {/* A recorded day played back through the runners, as recap runs. */}
                    <Route path="/data/replay" element={<ReplayPage />} />

                    <Route path="/system" element={<HealthPage view="overview" />} />
                    <Route path="/system/checkups" element={<HealthPage view="checkups" />} />
                    <Route path="/system/incidents" element={<IncidentsPage />} />
                    <Route path="/system/log" element={<SystemLogPage />} />
                    <Route path="/system/calendar" element={<MarketCalendarPage />} />
                    <Route path="/system/connectors" element={<BrokerPage />} />
                    <Route path="/system/connectors/:providerKey" element={<ConnectorDetailPage />} />
                    <Route path="/system/people" element={<PeoplePage view="users" />} />
                    <Route path="/system/people/packages" element={<PeoplePage view="packages" />} />
                    <Route path="/system/people/invites" element={<PeoplePage view="invites" />} />
                  </Route>
                </Route>
              </Route>

              {/* A route nothing claims: inside the console for a signed-in user, on its own for a visitor. */}
              <Route element={<FallbackShell />}>
                <Route element={<AppLayout />}>
                  <Route path="*" element={<NotFoundPage />} />
                </Route>
              </Route>
            </Routes>
          </Suspense>
        </AuthProvider>
      </BrowserRouter>
    </QueryClientProvider>
  )
}
