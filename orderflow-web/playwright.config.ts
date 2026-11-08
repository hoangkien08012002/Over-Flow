import { defineConfig } from '@playwright/test';

// Run through tests/e2e/run-browser-e2e.ps1, which starts the API and web client on a throwaway database and sets these variables.
// Locally it uses the Edge that Windows already has, so no browser is downloaded. CI sets E2E_BROWSER_CHANNEL=chromium.
export default defineConfig({
  testDir: './e2e',
  outputDir: process.env.E2E_OUTPUT_DIR ?? 'test-results',
  timeout: 180_000,
  expect: { timeout: 20_000 },
  workers: 1,
  retries: 0,
  reporter: [['list']],
  use: {
    actionTimeout: 20_000,
    baseURL: process.env.E2E_BASE_URL ?? 'http://127.0.0.1:4173',
    channel: process.env.E2E_BROWSER_CHANNEL ?? 'msedge',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure'
  }
});
