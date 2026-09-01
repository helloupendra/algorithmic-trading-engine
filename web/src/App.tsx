import { BrowserRouter, Route, Routes } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { AuthProvider } from './lib/auth'
import { AppLayout } from './components/AppLayout'
import { RedirectIfAuthenticated, RequireAuth, RequireRole } from './components/RouteGuards'
import { LoginPage } from './pages/LoginPage'
import { HomePage } from './pages/HomePage'
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
import { AdminOverviewPage } from './pages/admin/AdminOverviewPage'
import { UsersPage } from './pages/admin/UsersPage'
import { RiskPage } from './pages/admin/RiskPage'
import { StrategyControlPage } from './pages/admin/StrategyControlPage'
import { InstrumentsPage } from './pages/admin/InstrumentsPage'
import { IngestionPage } from './pages/admin/IngestionPage'
import { BrokerPage } from './pages/admin/BrokerPage'
import { LiveAlertsPage } from './pages/admin/LiveAlertsPage'
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

export default function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>
        <AuthProvider>
          <Routes>
            {/* Public front door — reachable signed in or out. */}
            <Route path="/" element={<HomePage />} />

            <Route element={<RedirectIfAuthenticated />}>
              <Route path="/login" element={<LoginPage />} />
            </Route>

            <Route element={<RequireAuth />}>
              <Route element={<AppLayout />}>
                {/* Trader area — any signed-in user. */}
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

                {/* Admin area — Admin role only. */}
                <Route element={<RequireRole role="Admin" />}>
                  <Route path="/admin" element={<AdminOverviewPage />} />
                  <Route path="/admin/users" element={<UsersPage />} />
                  <Route path="/admin/risk" element={<RiskPage />} />
                  <Route path="/admin/strategies" element={<StrategyControlPage />} />
                  <Route path="/admin/instruments" element={<InstrumentsPage />} />
                  <Route path="/admin/ingestion" element={<IngestionPage />} />
                  <Route path="/admin/live-alerts" element={<LiveAlertsPage />} />
                  <Route path="/admin/broker" element={<BrokerPage />} />
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
