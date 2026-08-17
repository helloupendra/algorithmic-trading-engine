import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { AuthProvider } from './lib/auth'
import { AppLayout } from './components/AppLayout'
import { RedirectIfAuthenticated, RequireAuth, RequireRole } from './components/RouteGuards'
import { LoginPage } from './pages/LoginPage'
import {
  AdminHome,
  ForbiddenPage,
  NotFoundPage,
  PagePlaceholder,
  TraderHome,
} from './pages/Placeholders'
import './styles.css'

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      // Live trading data goes stale immediately; individual screens set their
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
            <Route element={<RedirectIfAuthenticated />}>
              <Route path="/login" element={<LoginPage />} />
            </Route>

            <Route element={<RequireAuth />}>
              <Route element={<AppLayout />}>
                {/* Trader area — any signed-in user. */}
                <Route path="/trader" element={<TraderHome />} />
                <Route
                  path="/trader/watchlist"
                  element={
                    <PagePlaceholder
                      title="Watchlist"
                      description="Symbols streaming into the live tick pipeline."
                      endpoints={['GET /api/LiveData/watchlist', 'POST /api/LiveData/watchlist']}
                    />
                  }
                />
                <Route
                  path="/trader/option-chain"
                  element={
                    <PagePlaceholder
                      title="Option chain"
                      description="ATM-centred strikes with live premiums."
                      endpoints={[
                        'GET /api/Instruments/derivatives/expiries',
                        'GET /api/Instruments/derivatives/chain',
                      ]}
                    />
                  }
                />
                <Route
                  path="/trader/positions"
                  element={
                    <PagePlaceholder
                      title="Positions"
                      description="Open and closed positions with realised and unrealised P&L."
                      endpoints={[
                        'GET /api/Simulator/runs/{id}/positions',
                        'GET /api/Simulator/runs/{id}/portfolio',
                      ]}
                    />
                  }
                />
                <Route
                  path="/trader/orders"
                  element={
                    <PagePlaceholder
                      title="Orders"
                      description="Paper order history for your simulation runs."
                      endpoints={['GET /api/Simulator/runs/{id}/orders']}
                    />
                  }
                />
                <Route
                  path="/trader/strategies"
                  element={
                    <PagePlaceholder
                      title="Strategies"
                      description="Available strategies and their current run state."
                      endpoints={['GET /api/Strategy', 'GET /api/Simulator/runs']}
                    />
                  }
                />

                {/* Admin area — Admin role only. */}
                <Route element={<RequireRole role="Admin" />}>
                  <Route path="/admin" element={<AdminHome />} />
                  <Route
                    path="/admin/users"
                    element={
                      <PagePlaceholder
                        title="Users"
                        description="Create accounts, assign roles, deactivate access."
                        endpoints={[
                          'GET /api/UserAuth',
                          'POST /api/UserAuth/register',
                          'DELETE /api/UserAuth/{username}',
                        ]}
                      />
                    }
                  />
                  <Route
                    path="/admin/risk"
                    element={
                      <PagePlaceholder
                        title="Risk & kill switch"
                        description="Global trading halt, rate limits and loss caps."
                        endpoints={[
                          'GET /api/Risk/killswitch/status',
                          'POST /api/Risk/killswitch/activate',
                          'POST /api/Risk/killswitch/deactivate',
                        ]}
                      />
                    }
                  />
                  <Route
                    path="/admin/strategies"
                    element={
                      <PagePlaceholder
                        title="Strategy control"
                        description="Start and stop strategy processes."
                        endpoints={[
                          'POST /api/Strategy/{id}/start',
                          'POST /api/Strategy/{id}/stop',
                        ]}
                      />
                    }
                  />
                  <Route
                    path="/admin/instruments"
                    element={
                      <PagePlaceholder
                        title="Instruments"
                        description="Instrument universe and master import."
                        endpoints={[
                          'GET /api/Instruments/search',
                          'POST /api/Instruments/import-local',
                        ]}
                      />
                    }
                  />
                  <Route
                    path="/admin/ingestion"
                    element={
                      <PagePlaceholder
                        title="Data ingestion"
                        description="Ingestor heartbeat, stale symbols and backfill."
                        endpoints={[
                          'GET /api/LiveData/status/all',
                          'GET /api/LiveData/stale',
                          'POST /api/Backfill/history',
                        ]}
                      />
                    }
                  />
                </Route>
              </Route>
            </Route>

            <Route path="/forbidden" element={<ForbiddenPage />} />
            <Route path="/" element={<Navigate to="/trader" replace />} />
            <Route path="*" element={<NotFoundPage />} />
          </Routes>
        </AuthProvider>
      </BrowserRouter>
    </QueryClientProvider>
  )
}
