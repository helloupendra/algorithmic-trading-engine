import { defineConfig } from '@playwright/test'

export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 45_000,
  // On CI the github reporter writes each failure as an annotation on the run,
  // readable without downloading the HTML report.
  reporter: process.env.CI ? [['github'], ['list'], ['html', { open: 'never' }]] : [['list'], ['html', { open: 'never' }]],
  use: { baseURL: 'http://127.0.0.1:4173', trace: 'retain-on-failure', screenshot: 'only-on-failure' },
  webServer: [
    { command: 'dotnet run --project ../tests/AlgoTrading.BrowserHost --no-launch-profile', url: 'http://127.0.0.1:5025/test/ready', timeout: 120_000, reuseExistingServer: false },
    { command: 'npm run dev -- --host 127.0.0.1 --port 4173 --strictPort', url: 'http://127.0.0.1:4173', env: { VITE_API_BASE_URL: 'http://127.0.0.1:5025' }, reuseExistingServer: false },
  ],
})
