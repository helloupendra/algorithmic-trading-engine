import { defineConfig } from '@playwright/test'

export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 45_000,
  // The console loads a page's code when its route is first opened (App.tsx
  // lazy). Against the dev server that first open also transforms the page's
  // modules, which on a cold CI runner takes longer than the 5 s an
  // expectation waits by default; the suite's first "Manual order" heading
  // used to time out there (15 Nov 2026).
  expect: { timeout: 15_000 },
  reporter: [['list'], ['html', { open: 'never' }]],
  use: { baseURL: 'http://127.0.0.1:4173', trace: 'retain-on-failure', screenshot: 'only-on-failure' },
  webServer: [
    { command: 'dotnet run --project ../tests/AlgoTrading.BrowserHost --no-launch-profile', url: 'http://127.0.0.1:5025/test/ready', timeout: 120_000, reuseExistingServer: false },
    { command: 'npm run dev -- --host 127.0.0.1 --port 4173 --strictPort', url: 'http://127.0.0.1:4173', env: { VITE_API_BASE_URL: 'http://127.0.0.1:5025' }, reuseExistingServer: false },
  ],
})
