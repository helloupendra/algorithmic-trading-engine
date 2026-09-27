import { BrowserRouter, Route, Routes, useSearchParams } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { AuthProvider } from './lib/auth'
import { useLiveFeedSignalR } from './lib/queries'
import { ROUTE_MOVES } from './lib/routeMap'
import { AppLayout } from './components/AppLayout'
import { ByRole, MovedTo, RedirectIfAuthenticated, RequireAuth, RequireRole, WorkspaceLanding } from './components/RouteGuards'
import { LoginPage } from './pages/LoginPage'
import { AcceptInvitePage } from './pages/AcceptInvitePage'
import { LandingPage } from './pages/LandingPage'
import { ForbiddenPage, NotFoundPage } from './pages/Placeholders'
import { DeskPage } from './pages/desk/DeskPage'
import { WatchlistPage } from './pages/trader/WatchlistPage'
import { ChartPage } from './pages/markets/chart/ChartPage'
import { PositionsPage } from './pages/trader/PositionsPage'
import { OrdersPage } from './pages/trader/OrdersPage'
import { MarketNewsPage } from './pages/data/MarketNewsPage'
import { AccountPage } from './pages/trader/AccountPage'
import { RunDetailPage } from './pages/trader/RunDetailPage'
import { AdminOverviewPage } from './pages/admin/AdminOverviewPage'
import { UsersPage } from './pages/admin/UsersPage'
import { StrategyPackagesPage } from './pages/admin/StrategyPackagesPage'
import { RiskV2Page } from './pages/admin/RiskV2Page'
import { BrokerPage } from './pages/admin/BrokerPage'
import { ConnectorDetailPage } from './pages/admin/ConnectorDetailPage'
import { LiveAlertsV2Page } from './pages/admin/LiveAlertsV2Page'
import { PatternAlertsPage } from './pages/data/PatternAlertsPage'
import { MoversPage } from './pages/markets/movers/MoversPage'
import MarketFactorsPage from './pages/data/MarketFactorsPage'
import { ActivityLogPage } from './pages/admin/ActivityLogPage'
import { DeploymentsPage } from './pages/admin/DeploymentsPage'
import { IncidentsPage } from './pages/admin/IncidentsPage'
import { CheckupPage } from './pages/admin/CheckupPage'
import { MarketCalendarPage } from './pages/admin/MarketCalendarPage'
import { StrategiesOverviewPage } from './pages/strategies/StrategiesOverviewPage'
import { ManualOrderPage } from './pages/trading/ManualOrderPage'
import { FilterLabPage } from './pages/trading/FilterLabPage'
import { LiveRunnerPage } from './pages/strategies/LiveRunnerPage'
import { StrategyLibraryPage } from './pages/strategies/StrategyLibraryPage'
import { StrategySpecPage } from './pages/strategies/StrategySpecPage'
import { RunHistoryPage } from './pages/strategies/RunHistoryPage'
import { LiveRunDetailPage } from './pages/strategies/LiveRunDetailPage'
import { BacktestOverviewPage } from './pages/backtesting/BacktestOverviewPage'
import { NewBacktestPage } from './pages/backtesting/NewBacktestPage'
import { BacktestRunsPage } from './pages/backtesting/BacktestRunsPage'
import { BacktestRunPage } from './pages/backtesting/BacktestRunPage'
import { AnalysisPage } from './pages/analysis/AnalysisPage'
import { DataOverviewPage } from './pages/data/DataOverviewPage'
import { OptionChainPage } from './pages/markets/chain/OptionChainPage'
import { LiveFeedsPage } from './pages/data/LiveFeedsPage'
import { CommodityPage } from './pages/data/CommodityPage'
import { HistoricalDataPage } from './pages/data/HistoricalDataPage'
import { InstrumentsFnoPage } from './pages/data/InstrumentsFnoPage'
import { NotebookPage } from './pages/notebook/NotebookPage'
import { WhiteboardPage } from './pages/notebook/WhiteboardPage'
import './styles.css'

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

function GlobalSignalR() {
  useLiveFeedSignalR()
  return null
}

/** Until the one timeline: the log's source picks which of the three pages shows. */
function LogRoute() {
  const [params] = useSearchParams()
  const source = params.get('source')
  return source === 'alerts' ? <LiveAlertsV2Page /> : source === 'deploys' ? <DeploymentsPage /> : <ActivityLogPage />
}

export default function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <GlobalSignalR />
      <BrowserRouter>
        <AuthProvider>
          <Routes>
            {/* Public homepage. Signing out lands here; "Open console" goes to
                /login, which bounces authenticated users to the Desk. */}
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
                <Route path="/markets/movers" element={<MoversPage />} />
                <Route path="/markets/factors" element={<MarketFactorsPage />} />
                <Route path="/markets/news" element={<MarketNewsPage />} />

                {/* Trade. A trader deploys from the library and follows runs in History. */}
                <Route
                  path="/trade/library"
                  element={<ByRole admin={<StrategyLibraryPage />} trader={<StrategiesOverviewPage mode="trader" />} />}
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
                <Route path="/trade/positions" element={<PositionsPage />} />
                <Route path="/trade/positions/runs/:id" element={<RunDetailPage />} />
                <Route path="/trade/orders" element={<OrdersPage />} />
                <Route path="/trade/ticket" element={<ManualOrderPage />} />

                {/* Research. */}
                <Route path="/research/lab" element={<FilterLabPage />} />

                {/* Outside the workspaces: the trader's own account, from the avatar menu. */}
                <Route path="/account" element={<AccountPage />} />

                {/* Admin only. */}
                <Route element={<RequireRole role="Admin" />}>
                  <Route path="/markets/mcx" element={<CommodityPage />} />
                  <Route path="/markets/patterns" element={<PatternAlertsPage />} />

                  <Route path="/trade/runs" element={<LiveRunnerPage />} />
                  <Route path="/trade/risk" element={<RiskV2Page />} />

                  <Route path="/research/backtests" element={<BacktestOverviewPage />} />
                  <Route path="/research/backtests/new" element={<NewBacktestPage />} />
                  <Route path="/research/backtests/runs" element={<BacktestRunsPage />} />
                  <Route path="/research/backtests/runs/:id" element={<BacktestRunPage />} />
                  {/* Forecasts with proof; its sections are tabs on the one page (?section=). */}
                  <Route path="/research/forecasts" element={<AnalysisPage />} />
                  {/* The board list; a board itself is the full-window route above. */}
                  <Route path="/research/notebook" element={<NotebookPage />} />

                  <Route path="/data" element={<DataOverviewPage />} />
                  <Route path="/data/feeds" element={<LiveFeedsPage />} />
                  <Route path="/data/historical" element={<HistoricalDataPage />} />
                  <Route path="/data/instruments" element={<InstrumentsFnoPage />} />

                  <Route path="/system" element={<AdminOverviewPage />} />
                  <Route path="/system/checkups" element={<CheckupPage />} />
                  <Route path="/system/incidents" element={<IncidentsPage />} />
                  <Route path="/system/log" element={<LogRoute />} />
                  <Route path="/system/calendar" element={<MarketCalendarPage />} />
                  <Route path="/system/connectors" element={<BrokerPage />} />
                  <Route path="/system/connectors/:providerKey" element={<ConnectorDetailPage />} />
                  <Route path="/system/people" element={<UsersPage />} />
                  <Route path="/system/people/packages" element={<StrategyPackagesPage />} />
                </Route>
              </Route>
            </Route>

            <Route path="/forbidden" element={<ForbiddenPage />} />
            <Route path="*" element={<NotFoundPage />} />
          </Routes>
        </AuthProvider>
      </BrowserRouter>
    </QueryClientProvider>
  )
}
