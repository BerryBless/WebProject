import { defineConfig, devices } from '@playwright/test'

const origin = process.env.E2E_SPA_ORIGIN
if (!origin || !process.env.E2E_ADMIN_PASSWORD) throw new Error('E2E_SPA_ORIGIN과 E2E_ADMIN_PASSWORD가 필요합니다.')

export default defineConfig({
  testDir: 'e2e',
  timeout: 90_000,
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: process.env.CI ? [['github'], ['list']] : 'list',
  use: { baseURL: origin, ignoreHTTPSErrors: true, trace: 'retain-on-failure' },
  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
    { name: 'firefox', use: { ...devices['Desktop Firefox'] } },
  ],
})
