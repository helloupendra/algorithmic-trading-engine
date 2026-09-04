import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { AuthProvider } from './lib/auth'
import { useLiveFeedSignalR } from './lib/queries'
import { AppLayout } from './components/AppLayout'
import { RedirectIfAuthenticated, RequireAuth, RequireRole } from './components/RouteGuards'
import { LoginPage } from './pages/LoginPage'
import { LandingPage } from './pages/LandingPage'
import { ForbiddenPage, NotFoundPage } from './pages/Placeholders'
import { OverviewPage } from './pages/trader/OverviewPage'
import { WatchlistPage } from './pages/trader/WatchlistPage'
import { ChartsPage } from './pages/trader/ChartsPage'
import { OptionChainPage } from './pages/trader/OptionChainPage'
import { PositionsPage } from './pages/trader/PositionsPage'
import { OrdersPage } from './pages/trader/OrdersPage'
import { StrategiesPage } from './pages/trader/StrategiesPage'
import { MarketNewsPage } from './pages/trader/MarketNewsPage'
import { TopMoversPage } from './pages/trader/TopMoversPage'
import { DeployPage } from './pages/trader/DeployPage'
import { RunDetailPage } from './pages/trader/RunDetailPage'
import { AdminHomePage } from './pages/admin/AdminHomePage'
import { AdminOverviewPage } from './pages/admin/AdminOverviewPage'
import { UsersPage } from './pages/admin/UsersPage'
import { RiskV2Page } from './pages/admin/RiskV2Page'
import { BrokerPage } from './pages/admin/BrokerPage'
import { LiveAlertsV2Page } from './pages/admin/LiveAlertsV2Page'
import { StrategiesOverviewPage } from './pages/strategies/StrategiesOverviewPage'
import { LiveRunnerPage } from './pages/strategies/LiveRunnerPage'
import { StrategyLibraryPage } from './pages/strategies/StrategyLibraryPage'
import { RunHistoryPage } from './pages/strategies/RunHistoryPage'
import { LiveRunDetailPage } from './pages/strategies/LiveRunDetailPage'
import { BacktestOverviewPage } from './pages/backtesting/BacktestOverviewPage'
import { NewBacktestPage } from './pages/backtesting/NewBacktestPage'
import { BacktestRunsPage } from './pages/backtesting/BacktestRunsPage'
import { BacktestRunPage } from './pages/backtesting/BacktestRunPage'
import { DataOverviewPage } from './pages/data/DataOverviewPage'
import { LiveFeedsPage } from './pages/data/LiveFeedsPage'
import { HistoricalDataPage } from './pages/data/HistoricalDataPage'
import { InstrumentsFnoPage } from './pages/data/InstrumentsFnoPage'
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

export default function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <GlobalSignalR />
      <BrowserRouter>
        <AuthProvider>
          <Routes>
            {/* Public homepage. Signing out lands here; "Open console" goes to
                /login, which bounces authenticated users to their role's home. */}
            <Route path="/" element={<LandingPage />} />

            <Route element={<RedirectIfAuthenticated />}>
              <Route path="/login" element={<LoginPage />} />
            </Route>

            <Route element={<RequireAuth />}>
              <Route element={<AppLayout />}>
                {/* Trader area — any signed-in user. v1 pages, rebuild queued. */}
                <Route path="/trader" element={<OverviewPage />} />
                <Route path="/trader/watchlist" element={<WatchlistPage />} />
                <Route path="/trader/charts" element={<ChartsPage />} />
                <Route path="/trader/news" element={<MarketNewsPage />} />
                <Route path="/trader/movers" element={<TopMoversPage />} />
                <Route path="/trader/option-chain" element={<OptionChainPage />} />
                <Route path="/trader/positions" element={<PositionsPage />} />
                <Route path="/trader/orders" element={<OrdersPage />} />
                <Route path="/trader/strategies" element={<StrategiesPage />} />
                <Route path="/trader/deploy" element={<DeployPage />} />
                <Route path="/trader/runs/:id" element={<RunDetailPage />} />
                {/* Live run history — own runs only (the API enforces it). */}
                <Route path="/trader/strategies/history" element={<RunHistoryPage mode="trader" />} />
                <Route
                  path="/trader/strategies/runs/:runId"
                  element={<LiveRunDetailPage basePath="/trader/strategies" />}
                />

                {/* Admin area — Admin role only. */}
                <Route element={<RequireRole role="Admin" />}>
                  <Route path="/admin" element={<AdminHomePage />} />

                  {/* Data module (v2). */}
                  <Route path="/admin/data" element={<DataOverviewPage />} />
                  <Route path="/admin/data/live" element={<LiveFeedsPage />} />
                  <Route path="/admin/data/historical" element={<HistoricalDataPage />} />
                  <Route path="/admin/data/instruments" element={<InstrumentsFnoPage />} />

                  {/* v1 modules, awaiting their rebuild. */}
                  <Route path="/admin/system" element={<AdminOverviewPage />} />
                  <Route path="/admin/users" element={<UsersPage />} />
                  <Route path="/admin/risk" element={<RiskV2Page />} />
                  <Route path="/admin/strategies" element={<StrategiesOverviewPage />} />
                  <Route path="/admin/strategies/live" element={<LiveRunnerPage />} />
                  <Route path="/admin/strategies/history" element={<RunHistoryPage mode="admin" />} />
                  <Route
                    path="/admin/strategies/runs/:runId"
                    element={<LiveRunDetailPage basePath="/admin/strategies" />}
                  />
                  <Route path="/admin/strategies/library" element={<StrategyLibraryPage />} />

                  {/* Backtesting module (v2). */}
                  <Route path="/admin/backtesting" element={<BacktestOverviewPage />} />
                  <Route path="/admin/backtesting/new" element={<NewBacktestPage />} />
                  <Route path="/admin/backtesting/runs" element={<BacktestRunsPage />} />
                  <Route path="/admin/backtesting/runs/:id" element={<BacktestRunPage />} />

                  <Route path="/admin/live-alerts" element={<LiveAlertsV2Page />} />
                  <Route path="/admin/broker" element={<BrokerPage />} />

                  {/* Old bookmarks from the v1 layout. */}
                  <Route path="/admin/ingestion" element={<Navigate to="/admin/data/live" replace />} />
                  <Route
                    path="/admin/instruments"
                    element={<Navigate to="/admin/data/instruments" replace />}
                  />
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
