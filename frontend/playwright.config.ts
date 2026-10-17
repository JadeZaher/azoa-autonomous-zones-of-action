import { defineConfig, devices } from '@playwright/test'

/**
 * Playwright E2E against the dev-up stack in E2E mode (`node e2e/scripts/e2e-stack.mjs up`).
 * Journeys are `*.journey.ts` (the repo .gitignore swallows `*.spec.ts`). See `e2e/AGENTS.md`.
 */
// Dedicated E2E port (3200): AZOA_FRONTEND_HOST_PORT belongs to the dev stack and is never read here.
const frontendPort = process.env.AZOA_E2E_FRONTEND_PORT ?? '3200'
const appUrl = process.env.AZOA_E2E_APP_URL ?? `http://localhost:${frontendPort}`

export default defineConfig({
  testDir: './e2e',
  outputDir: './e2e/.output/test-results',
  globalSetup: './e2e/global-setup.ts',
  fullyParallel: false,
  // Journeys share one stack and one operator; serial keeps rate limits and seeds predictable.
  workers: 1,
  forbidOnly: Boolean(process.env.CI),
  retries: process.env.CI ? 1 : 0,
  timeout: 90_000,
  expect: { timeout: 10_000 },
  reporter: process.env.CI
    ? [['list'], ['html', { outputFolder: './e2e/.output/report', open: 'never' }]]
    : [['list']],
  use: {
    baseURL: appUrl,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [
    {
      // Needs no API or vendors: proves the browser, the config and the app shell.
      name: 'shell',
      testMatch: /.*\.shell\.ts$/,
      use: { ...devices['Desktop Chrome'] },
    },
    {
      name: 'journeys',
      testMatch: /.*\.journey\.ts$/,
      use: { ...devices['Desktop Chrome'] },
    },
  ],
  // Optional local dev server for shell checks; the journeys use the compose frontend instead.
  webServer:
    process.env.AZOA_E2E_DEV_SERVER === '1'
      ? {
          command: `npx next dev -p ${frontendPort}`,
          url: `${appUrl}/login`,
          reuseExistingServer: true,
          timeout: 180_000,
        }
      : undefined,
})
