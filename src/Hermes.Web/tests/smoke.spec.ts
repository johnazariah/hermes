import { test, expect } from '@playwright/test';

const BASE = 'http://localhost:21742';

test.describe('Hermes UI Smoke Tests', () => {
    test('top nav renders', async ({ page }) => {
        await page.goto(BASE);
        await expect(page.getByRole('link', { name: 'Pipeline' })).toBeVisible();
        await expect(page.getByRole('link', { name: 'Documents' })).toBeVisible();
        await expect(page.getByRole('link', { name: 'Search' })).toBeVisible();
        await expect(page.getByRole('link', { name: 'Chat' })).toBeVisible();
        await expect(page.getByRole('link', { name: 'Settings' })).toBeVisible();
    });

    test('Pipeline page shows stages', async ({ page }) => {
        await page.goto(BASE);
        await expect(page.getByText('PIPELINE', { exact: true })).toBeVisible();
        await expect(page.getByText('Received')).toBeVisible();
        await expect(page.getByText('LIVE FEED')).toBeVisible();
    });

    test('Documents page loads categories and docs', async ({ page }) => {
        await page.goto(BASE + '/documents');
        await expect(page.getByText('CATEGORIES', { exact: true })).toBeVisible();
        // Wait for categories
        const catBtn = page.locator('button').filter({ hasText: /📁/ }).first();
        await expect(catBtn).toBeVisible({ timeout: 15000 });
        await catBtn.click();
        // Wait for doc list header to show count
        await page.waitForTimeout(2000); // docs loaded
    });

    test('Documents page click doc shows detail', async ({ page }) => {
        await page.goto(BASE + '/documents');
        const catBtn = page.locator('button').filter({ hasText: /📁/ }).first();
        await expect(catBtn).toBeVisible({ timeout: 15000 });
        await catBtn.click();
        // Wait for docs to load - doc items are in the center panel
        await page.waitForTimeout(2000);
        // Click a doc item - they have truncated text class
        const docItem = page.locator('.truncate').filter({ hasText: /\.(pdf|md)/ }).first();
        await expect(docItem).toBeVisible({ timeout: 10000 });
        await docItem.click();
        // Should show detail - Back button and content
        await expect(page.getByText('← Back')).toBeVisible({ timeout: 10000 });
    });

    test('API file endpoint returns 404 not HTML', async ({ page }) => {
        const r = await page.request.head(BASE + '/api/documents/1/file');
        expect(r.status()).toBe(404);
    });

    test('Search page renders', async ({ page }) => {
        await page.goto(BASE + '/search');
        await expect(page.getByRole('heading', { name: 'Search' })).toBeVisible();
    });

    test('Chat page renders', async ({ page }) => {
        await page.goto(BASE + '/chat');
        await expect(page.getByRole('heading', { name: 'Chat' })).toBeVisible();
    });

    test('Settings page renders', async ({ page }) => {
        await page.goto(BASE + '/settings');
        await expect(page.getByRole('heading', { name: 'Settings' })).toBeVisible();
    });

    test('client-side nav works', async ({ page }) => {
        await page.goto(BASE);
        await page.getByRole('link', { name: 'Documents' }).click();
        await expect(page.getByText('CATEGORIES', { exact: true })).toBeVisible();
        await page.getByRole('link', { name: 'Search' }).click();
        await expect(page.getByRole('heading', { name: 'Search' })).toBeVisible();
        await page.getByRole('link', { name: 'Chat' }).click();
        await expect(page.getByRole('heading', { name: 'Chat' })).toBeVisible();
        await page.getByRole('link', { name: 'Settings' }).click();
        await expect(page.getByRole('heading', { name: 'Settings' })).toBeVisible();
    });
});
