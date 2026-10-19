import { expect, test, type Page } from '@playwright/test';
import type { DocumentDetail, DocumentSummary } from '../src/types/hermes';

function summary(id: number): DocumentSummary {
  return {
    id, originalName: `synthetic-pipeline-${id}.md`, category: 'payslips',
    extractedDate: null, extractedAmount: null, sender: null, vendor: null,
    sourceType: 'folder', account: null, sourcePath: null,
    classificationTier: null, classificationConfidence: null,
  };
}

function detail(id: number): DocumentDetail {
  return {
    summary: summary(id), extractedText: null, comprehension: null,
    filePath: `synthetic/pipeline-${id}.md`, vendor: null,
    ingestedAt: '2026-01-01T00:00:00Z', extractedAt: '2026-01-01T00:01:00Z',
    embeddedAt: '2026-01-01T00:02:00Z',
    pipelineStatus: { extracted: true, understood: true, embedded: true },
  };
}

async function mockStages(page: Page) {
  await page.route('**/api/pipeline', route => route.fulfill({
    json: { received: 0, extracted: 0, understood: 0, embedded: 1, failed: 2 },
  }));
  await page.route('**/api/activity?*', route => route.fulfill({ json: [] }));
  await page.route('**/api/documents?stage=*', route => {
    const stage = new URL(route.request().url()).searchParams.get('stage');
    const ids = stage === 'failed' ? [41, 43] : stage === 'embedded' ? [42] : [];
    return route.fulfill({ json: ids.map(id => ({
      ...summary(id), stage, extractionMethod: 'text',
    })) });
  });
  await page.route(/\/api\/documents\/\d+$/, route => {
    const id = Number(new URL(route.request().url()).pathname.split('/').pop());
    return route.fulfill({ json: detail(id) });
  });
}

test('failed stage rows override completed detail booleans without inventing an error', async ({ page }) => {
  await mockStages(page);
  await page.goto('/pipeline');
  await page.getByRole('button', { name: /Failed.*2/ }).click();
  await page.getByRole('button', { name: /synthetic-pipeline-41.md/ }).click();
  const journey = page.getByRole('region', { name: 'Document journey' });
  await expect(journey.getByText('❌ Failed', { exact: true })).toBeVisible();
  await expect(journey).not.toContainText('Unknown error');
  await expect(journey).toContainText('method: text');

  await page.getByRole('button', { name: /Memorised.*1/ }).click();
  await expect(journey).toHaveCount(0);
  await page.getByRole('button', { name: /synthetic-pipeline-42.md/ }).click();
  await expect(journey.getByText('🧠 Memorised', { exact: true })).toBeVisible();
  await expect(journey).toContainText('✅');
  await expect(journey).not.toContainText('❌ Failed');
  await expect(journey).not.toContainText('🔄');
});

test('a late detail cannot attach a failed row stage to a newly selected document or filter', async ({ page }) => {
  await mockStages(page);
  let releaseDetail: () => void = () => { throw new Error('Detail gate was not initialised'); };
  const gate = new Promise<void>(resolve => { releaseDetail = resolve; });
  await page.route('**/api/documents/41', async route => {
    await gate;
    await route.fulfill({ json: detail(41) });
  });
  await page.goto('/pipeline');
  await page.getByRole('button', { name: /Failed.*2/ }).click();
  const started = page.waitForRequest('**/api/documents/41');
  await page.getByRole('button', { name: /synthetic-pipeline-41.md/ }).click();
  await started;
  await page.getByRole('button', { name: /synthetic-pipeline-43.md/ }).click();
  const journey = page.getByRole('region', { name: 'Document journey' });
  await expect(page.getByText('synthetic-pipeline-43.md', { exact: true })).toHaveCount(2);
  await expect(journey.getByText('❌ Failed', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: /Memorised.*1/ }).click();
  await expect(journey).toHaveCount(0);
  await page.getByRole('button', { name: /synthetic-pipeline-42.md/ }).click();
  await expect(journey.getByText('🧠 Memorised', { exact: true })).toBeVisible();
  const lateResponse = page.waitForResponse('**/api/documents/41');
  releaseDetail();
  await (await lateResponse).finished();
  await expect(page.getByText('synthetic-pipeline-42.md', { exact: true })).toHaveCount(2);
  await expect(page.getByText('synthetic-pipeline-41.md', { exact: true })).toHaveCount(0);
  await expect(journey).not.toContainText('❌ Failed');
  await page.getByRole('button', { name: /Failed.*2/ }).click();
  await expect(journey).toHaveCount(0);
  await page.getByRole('button', { name: /synthetic-pipeline-41.md/ }).click();
  await expect(journey.getByText('❌ Failed', { exact: true })).toBeVisible();
});
