import { defineConfig, devices } from '@playwright/test';

const port = Number(process.env.HERMES_TEST_PORT ?? '21843');
if (!Number.isInteger(port) || port < 1024 || port > 65534 || [21741, 21742].includes(port)
  || [21741, 21742].includes(port + 1)) {
  throw new Error('HERMES_TEST_PORT must select two non-production loopback ports');
}
const baseURL = `http://127.0.0.1:${port}`;
process.env.HERMES_WEB_BASE_URL = baseURL;
process.env.HERMES_TEST_PORT = String(port);

export default defineConfig({
  testDir: './tests',
  testMatch: '**/*.spec.ts',
  testIgnore: '**/excluded/**',
  outputDir: './test-results/canonical',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  forbidOnly: Boolean(process.env.CI),
  timeout: 30_000,
  expect: { timeout: 10_000 },
  reporter: [['list'], ['json', { outputFile: 'test-results/canonical/results.json' }]],
  use: { baseURL, trace: 'retain-on-failure', screenshot: 'only-on-failure' },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: {
    command: 'node tests/serve.mjs',
    url: `http://127.0.0.1:${port + 1}/ready`,
    timeout: 60_000,
    reuseExistingServer: false,
    gracefulShutdown: { signal: 'SIGTERM', timeout: 15_000 },
    stdout: 'pipe',
    stderr: 'pipe',
  },
});
