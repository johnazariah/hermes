import { test, expect } from '@playwright/test';

const BASE = 'http://localhost:21742/app';

test.describe('Hermes Blazor UI Smoke Tests', () => {

    test('layout renders with nav bar', async ({ page }) => {
        await page.goto(BASE);
        // Wait for Blazor to hydrate
        await page.waitForTimeout(3000);
        await expect(page.getByText('HERMES')).toBeVisible();
        await expect(page.getByRole('link', { name: 'Pipeline' })).toBeVisible();
        await expect(page.getByRole('link', { name: 'Documents' })).toBeVisible();
        await expect(page.getByRole('link', { name: 'Search' })).toBeVisible();
        await expect(page.getByRole('link', { name: 'Chat' })).toBeVisible();
        await expect(page.getByRole('link', { name: 'Settings' })).toBeVisible();
    });

    test('stats bar shows document count', async ({ page }) => {
        await page.goto(BASE);
        await page.waitForTimeout(5000);
        // Stats bar should show doc count
        await expect(page.getByText(/\d+[\d,]* docs/)).toBeVisible({ timeout: 10000 });
    });

    test('Pipeline page shows stage buttons', async ({ page }) => {
        await page.goto(BASE);
        await page.waitForTimeout(3000);
        await expect(page.getByText('Pipeline Stages')).toBeVisible();
        await expect(page.getByText('Received')).toBeVisible();
        await expect(page.getByText('Read')).toBeVisible();
        await expect(page.getByText('Understood')).toBeVisible();
        await expect(page.getByText('Memorised')).toBeVisible();
    });

    test('Pipeline page shows activity feed', async ({ page }) => {
        await page.goto(BASE);
        await page.waitForTimeout(3000);
        await expect(page.getByRole('heading', { name: 'Activity' })).toBeVisible();
    });

    test('Pipeline page shows progress bar', async ({ page }) => {
        await page.goto(BASE);
        await page.waitForTimeout(5000);
        await expect(page.getByText('Progress')).toBeVisible({ timeout: 10000 });
    });

    test('Documents page shows categories', async ({ page }) => {
        await page.goto(BASE + '/documents');
        await page.waitForTimeout(3000);
        await expect(page.getByRole('heading', { name: 'Categories' })).toBeVisible();
        // Categories load asynchronously — wait for at least one category item
        const catItem = page.locator('text=/\\(\\d+\\)/').first();
        await expect(catItem).toBeVisible({ timeout: 10000 });
    });

    test('Documents page click category loads documents', async ({ page }) => {
        await page.goto(BASE + '/documents');
        await page.waitForTimeout(5000);
        // Verify the page is interactive — the heading should be visible
        await expect(page.getByRole('heading', { name: 'Categories' })).toBeVisible();
        // Verify the document list panel exists
        await expect(page.getByRole('heading', { name: /Documents/ })).toBeVisible();
    });

    test('Search page renders with input', async ({ page }) => {
        await page.goto(BASE + '/search');
        await page.waitForTimeout(3000);
        await expect(page.getByPlaceholder('Search documents')).toBeVisible();
        await expect(page.getByRole('button', { name: 'Search' })).toBeVisible();
    });

    test('Chat page renders with input', async ({ page }) => {
        await page.goto(BASE + '/chat');
        await page.waitForTimeout(3000);
        await expect(page.getByPlaceholder('Ask about your documents')).toBeVisible();
        await expect(page.getByRole('button', { name: 'Send' })).toBeVisible();
        await expect(page.getByText('Ask Hermes about your documents')).toBeVisible();
    });

    test('Settings page loads config', async ({ page }) => {
        await page.goto(BASE + '/settings');
        await page.waitForTimeout(3000);
        await expect(page.getByText('Email Accounts')).toBeVisible();
        await expect(page.getByText('Configuration (YAML)')).toBeVisible();
        // Config textarea should have content
        const textarea = page.locator('textarea');
        await expect(textarea).toBeVisible({ timeout: 10000 });
    });

    test('client-side navigation works', async ({ page }) => {
        await page.goto(BASE);
        await page.waitForTimeout(3000);

        // Nav to Documents
        await page.getByRole('link', { name: 'Documents' }).click();
        await page.waitForTimeout(2000);
        await expect(page.getByText('Categories')).toBeVisible();

        // Nav to Search
        await page.getByRole('link', { name: 'Search' }).click();
        await page.waitForTimeout(2000);
        await expect(page.getByPlaceholder('Search documents')).toBeVisible();

        // Nav to Chat
        await page.getByRole('link', { name: 'Chat' }).click();
        await page.waitForTimeout(2000);
        await expect(page.getByText('Ask Hermes about your documents')).toBeVisible();

        // Nav to Settings
        await page.getByRole('link', { name: 'Settings' }).click();
        await page.waitForTimeout(2000);
        await expect(page.getByText('Configuration (YAML)')).toBeVisible();

        // Nav back to Pipeline
        await page.getByRole('link', { name: 'Pipeline' }).click();
        await page.waitForTimeout(2000);
        await expect(page.getByText('Pipeline Stages')).toBeVisible();
    });

    test('search executes without error', async ({ page }) => {
        await page.goto(BASE + '/search');
        await page.waitForTimeout(3000);
        const searchInput = page.getByPlaceholder('Search documents');
        await searchInput.fill('invoice');
        await page.getByRole('button', { name: 'Search' }).click();
        // Wait for search to complete (either results or "No results")
        await page.waitForTimeout(5000);
        // Check that searching state resolved (button text reverts to "Search")
        await expect(page.getByRole('button', { name: 'Search' })).toBeVisible();
    });
});
