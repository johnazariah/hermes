import { expect, test } from '@playwright/test';
import type { DocumentSummary } from '../src/types/hermes';
import { unlock } from './owner';

interface StagedFixture {
  originalName: string;
  content: string;
  completedProducerCycles: number;
}

for (const surface of ['settings', 'palette'] as const) {
  test(`${surface} Sync Now wakes the real producer and displays its newly ingested synthetic document`, async ({ page, request, baseURL }) => {
    const errors: string[] = [];
    const syncRequests: string[] = [];
    page.on('pageerror', error => errors.push(error.message));
    page.on('request', value => {
      if (new URL(value.url()).pathname === '/api/sync') syncRequests.push(value.method());
    });
    if (!baseURL) throw new Error('The owned synthetic Service base URL is required');
    const fixtureURL = new URL(baseURL);
    fixtureURL.port = String(Number(fixtureURL.port) + 1);
    const controlURL = new URL('/fixtures/sync', fixtureURL).href;
    const originalName = `synthetic-manual-sync-${surface}.txt`;

    await page.goto('/documents');
    await unlock(page);
    await expect(page.getByRole('heading', { name: 'Documents', exact: true })).toBeVisible();
    await expect(page.getByRole('link', { name: originalName, exact: true })).toHaveCount(0);
    const beforeResponse = await request.get('/api/documents?limit=200');
    expect(beforeResponse.status()).toBe(200);
    const before: DocumentSummary[] = await beforeResponse.json();
    expect(before.some(document => document.originalName === originalName)).toBe(false);

    const stagedResponse = await request.post(`${controlURL}/${surface}`);
    expect(stagedResponse.status()).toBe(201);
    const staged: StagedFixture = await stagedResponse.json();
    expect(staged.originalName).toBe(originalName);
    expect(staged.completedProducerCycles).toBeGreaterThan(0);
    const stagedDocumentsResponse = await request.get('/api/documents?limit=200');
    expect(stagedDocumentsResponse.status()).toBe(200);
    const stagedDocuments: DocumentSummary[] = await stagedDocumentsResponse.json();
    expect(stagedDocuments.some(document => document.originalName === originalName)).toBe(false);

    if (surface === 'settings') {
      await page.getByRole('link', { name: 'Settings', exact: true }).click();
      await expect(page.getByRole('button', { name: 'Sync Now', exact: true })).toBeEnabled();
    } else {
      await page.getByRole('link', { name: 'Home', exact: true }).click();
      await page.keyboard.press('Control+k');
      await page.getByPlaceholder('Type a command or search…').fill('Sync email now');
    }
    const accepted = page.waitForResponse(value =>
      new URL(value.url()).pathname === '/api/sync' && value.request().method() === 'POST');
    if (surface === 'settings') await page.getByRole('button', { name: 'Sync Now', exact: true }).click();
    else await page.getByPlaceholder('Type a command or search…').press('Enter');
    const acknowledgement = await accepted;
    expect(acknowledgement.status()).toBe(202);
    const body: { status: string } = await acknowledgement.json();
    expect(['queued', 'coalesced']).toContain(body.status);
    await expect(page.getByRole('status')).toContainText(surface === 'settings'
      ? 'Sync request accepted.' : 'Email sync queued.');

    await expect.poll(async () => {
      const response = await request.get(controlURL);
      expect(response.status()).toBe(200);
      const progress: { completedProducerCycles: number } = await response.json();
      return progress.completedProducerCycles;
    }, { message: 'The real producer must complete a new cycle after the UI request',
      timeout: 10_000, intervals: [100, 250, 500] }).toBe(staged.completedProducerCycles + 1);

    const afterResponse = await request.get('/api/documents?limit=200');
    expect(afterResponse.status()).toBe(200);
    const after: DocumentSummary[] = await afterResponse.json();
    const ingested = after.find(document => document.originalName === originalName);
    if (!ingested) throw new Error('The real producer did not insert the staged synthetic file');
    expect(after.length).toBe(before.length + 1);
    expect(ingested.sourceType).toBe('watched_folder');
    const original = await request.get(`/api/documents/${ingested.id}/file`);
    expect(original.status()).toBe(200);
    expect(await original.text()).toBe(staged.content);

    if (surface === 'palette') await page.getByPlaceholder('Type a command or search…').press('Escape');
    await page.getByRole('link', { name: 'Documents', exact: true }).click();
    await page.getByRole('link', { name: originalName, exact: true }).click();
    await expect(page).toHaveURL(new RegExp(`/documents\\?id=${ingested.id}$`));
    await expect(page.getByRole('heading', { name: originalName, exact: true })).toBeVisible();
    expect(syncRequests).toEqual(['POST']);
    expect(errors).toEqual([]);
  });
}
