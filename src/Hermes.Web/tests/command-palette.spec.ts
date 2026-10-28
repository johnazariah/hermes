import { expect, test, type Page } from '@playwright/test';
import { unlock } from './owner';

const browserErrors = new WeakMap<Page, string[]>();
test.beforeEach(({ page }) => {
  const errors: string[] = [];
  browserErrors.set(page, errors);
  page.on('pageerror', error => errors.push(error.message));
});
test.afterEach(({ page }) => { expect(browserErrors.get(page) ?? []).toEqual([]); });

async function openSync(page: Page) {
  await page.goto('/');
  await unlock(page);
  await page.keyboard.press('Control+k');
  const input = page.getByPlaceholder('Type a command or search…');
  await input.fill('Sync email now');
  return input;
}

for (const activation of ['keyboard', 'pointer'] as const) {
  test(`palette ${activation} sync keeps a 503 failure visible without an unhandled rejection`, async ({ page }) => {
    let requests = 0;
    await page.route('**/api/sync', route => {
      requests++;
      expect(route.request().method()).toBe('POST');
      return route.fulfill({ status: 503, json: {
        status: 'unavailable', error: 'manual_sync_unavailable', reason: 'starting',
      } });
    });
    const input = await openSync(page);
    if (activation === 'keyboard') await input.press('Enter');
    else await page.getByRole('option', { name: /Sync email now/ }).click();
    await expect(page.getByRole('alert')).toContainText('Email sync was not queued:');
    await expect(page.getByRole('alert')).toContainText('HTTP 503');
    await expect(input).toBeVisible();
    await expect(page.getByRole('status')).toHaveCount(0);
    expect(requests).toBe(1);
  });
}

for (const status of ['queued', 'coalesced'] as const) {
  test(`palette reports 202 ${status} as queued rather than completed`, async ({ page }) => {
    await page.route('**/api/sync', route => route.fulfill({ status: 202, json: { status } }));
    const input = await openSync(page);
    if (status === 'queued') await page.getByRole('option', { name: /Sync email now/ }).click();
    else await input.press('Enter');
    await expect(page.getByRole('status')).toHaveText('Email sync queued. This does not mean it has completed.');
    await expect(page.getByRole('alert')).toHaveCount(0);
    await expect(input).toBeVisible();
  });
}

test('pending palette sync is visible and duplicate pointer and keyboard activations send one request', async ({ page }) => {
  let requests = 0;
  let releaseSync: () => void = () => { throw new Error('Sync gate was not initialised'); };
  const gate = new Promise<void>(resolve => { releaseSync = resolve; });
  await page.route('**/api/sync', async route => {
    requests++;
    await gate;
    await route.fulfill({ status: 202, json: { status: 'queued' } });
  });
  const input = await openSync(page);
  const option = page.getByRole('option', { name: /Sync email now/ });
  const started = page.waitForRequest('**/api/sync');
  await option.click();
  await started;
  await expect(page.getByRole('status')).toHaveText('Queuing email sync…');
  await expect(option).toHaveAttribute('aria-disabled', 'true');
  await expect(option).toHaveAttribute('aria-busy', 'true');
  await option.click({ force: true });
  await input.press('Enter');
  await expect(page.getByRole('status')).toHaveText('Queuing email sync…');
  expect(requests).toBe(1);
  releaseSync();
  await expect(page.getByRole('status')).toHaveText('Email sync queued. This does not mean it has completed.');
  await expect(option).not.toHaveAttribute('aria-disabled', 'true');
  expect(requests).toBe(1);
});

test('palette rejects a 200 no-op rather than claiming a sync was queued', async ({ page }) => {
  await page.route('**/api/sync', route => route.fulfill({ status: 200, json: { status: 'ok' } }));
  const input = await openSync(page);
  await input.press('Enter');
  await expect(page.getByRole('alert')).toContainText('The service did not accept a sync request.');
  await expect(input).toBeVisible();
  await expect(page.getByRole('status')).toHaveCount(0);
});
