import { test, expect, type Page } from '@playwright/test';

const browserErrors = new WeakMap<Page, string[]>();
test.beforeEach(async ({ page }) => {
  const errors: string[] = [];
  browserErrors.set(page, errors);
  page.on('pageerror', error => errors.push(error.message));
  test.info().annotations.push({ type: 'fixture', description: 'Disposable synthetic Service, never an existing archive' });
});
test.afterEach(async ({ page }) => { expect(browserErrors.get(page) ?? []).toEqual([]); });

for (const [path, heading] of [
  ['/', 'Home'], ['/documents', 'Documents'], ['/search', 'Search'],
  ['/settings', 'Settings'], ['/pipeline', 'Pipeline'], ['/chat', 'Chat'],
] as const) {
  test(`direct ${path} serves and mounts canonical React`, async ({ page }) => {
    await page.goto(path);
    await expect(page.locator('#root')).toHaveAttribute('data-hermes-app', 'canonical-react');
    await expect(page.getByRole('heading', { level: 1, name: heading, exact: true })).toBeVisible();
    await expect(page.locator('script[src*="_framework"]')).toHaveCount(0);
    await expect(page.getByRole('link', { name: 'Documents', exact: true })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Chat', exact: true })).toBeVisible();
  });
}

test('all primary navigation links work without a page reload', async ({ page }) => {
  await page.goto('/');
  const navigations = ['/documents', '/search', '/chat', '/settings', '/pipeline', '/'];
  const names = ['Documents', 'Search', 'Chat', 'Settings', 'Pipeline', 'Home'];
  for (const [index, name] of names.entries()) {
    await page.getByRole('link', { name, exact: true }).click();
    await expect(page).toHaveURL(new RegExp(`${navigations[index]}$`));
    await expect(page.getByRole('heading', { name, exact: true, level: 1 })).toBeVisible();
  }
});

test('live documents open their real extracted content and return to the list', async ({ page }) => {
  await page.goto('/documents');
  await page.getByRole('link', { name: 'synthetic-payslip.md', exact: true }).click();
  await expect(page).toHaveURL(/\/documents\?id=1$/);
  await expect(page.getByRole('heading', { name: 'synthetic-payslip.md' })).toBeVisible();
  await expect(page.getByText(/Quartz salary evidence for browser smoke only/)).toBeVisible();
  await page.getByRole('button', { name: /Back/ }).click();
  await expect(page.getByRole('heading', { name: 'Documents', exact: true })).toBeVisible();
});

test('real keyword search returns the synthetic financial document', async ({ page }) => {
  await page.goto('/search');
  await page.getByPlaceholder('Search your documents...').fill('quartz');
  await page.getByRole('button', { name: 'Search', exact: true }).click();
  await expect(page.getByRole('link', { name: 'synthetic-payslip.md', exact: true })).toBeVisible();
  await expect(page.getByRole('combobox', { name: 'Date filtering is unavailable in this view' })).toBeDisabled();
});

test('real pipeline stage controls display real Service counts and document rows', async ({ page }) => {
  await page.goto('/pipeline');
  await page.getByRole('button', { name: /Memorised/ }).click();
  await expect(page.getByRole('button', { name: /synthetic-payslip.md/ })).toBeVisible();
  await page.getByRole('button', { name: /synthetic-payslip.md/ }).click();
  await expect(page.getByText('Synthetic Employer', { exact: true })).toBeVisible();
});

test('chat consumes actual Service results optional answer and done', async ({ page }) => {
  await page.goto('/chat');
  await page.getByPlaceholder('Ask about your documents...').fill('quartz');
  const request = page.waitForRequest(value => value.url().endsWith('/api/chat') && value.method() === 'POST');
  await page.getByRole('button', { name: 'Send', exact: true }).click();
  expect((await request).postDataJSON()).toEqual({ query: 'quartz', aiEnabled: true });
  await expect(page.getByText('Synthetic answer from the isolated smoke provider.', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: /synthetic-payslip.md/ })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Send', exact: true })).toBeVisible();
  await page.getByRole('button', { name: /synthetic-payslip.md/ }).click();
  await expect(page.getByRole('heading', { name: 'synthetic-payslip.md', exact: true })).toBeVisible();
});

test('preferences persist through the real settings API', async ({ page, request }) => {
  await page.goto('/settings');
  const preferences = page.locator('textarea').first();
  await expect(preferences).toHaveValue('Synthetic browser smoke preferences');
  await preferences.fill('Synthetic browser smoke preferences updated');
  await page.getByRole('button', { name: 'Save', exact: true }).first().click();
  await expect(page.getByRole('status')).toHaveText('Preferences saved.');
  expect(await (await request.get('/api/preferences')).text()).toBe('Synthetic browser smoke preferences updated');
});

test('theme toggling changes the mounted app and survives reload', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: /Dark mode|Light mode/ }).click();
  const dark = await page.locator('html').evaluate(element => element.classList.contains('dark'));
  await page.reload();
  await expect(page.locator('html')).toHaveClass(dark ? /dark/ : /^(?!.*dark)/);
});

test('unknown APIs and assets remain errors rather than SPA HTML', async ({ request }) => {
  for (const path of ['/api/missing-route', '/api/documents/999999/file', '/assets/missing.js', '/_framework/blazor.web.js']) {
    const result = await request.get(path);
    expect(result.status(), path).toBe(404);
    expect(result.headers()['content-type'] ?? '').not.toContain('text/html');
    expect(await result.text()).not.toContain('<!doctype');
  }
});

test('foreign origins cannot access the canonical root', async ({ request }) => {
  const result = await request.get('/', { headers: { Origin: 'https://foreign.example' } });
  expect(result.status()).toBe(403);
  expect(result.headers()['access-control-allow-origin']).toBeUndefined();
});

test('the served manifest and module assets match the built page', async ({ page, request }) => {
  await page.goto('/');
  const manifest: { assets: string[]; entrypoints: string[]; sha256: Record<string, string> } =
    await (await request.get('/asset-manifest.json')).json();
  const modules = await page.locator('script[type="module"][src]').evaluateAll(elements =>
    elements.map(element => element.getAttribute('src')));
  expect(modules).toEqual(manifest.entrypoints);
  for (const path of ['/index.html', ...manifest.assets.map(value => `/${value}`)]) {
    const response = await request.get(path);
    expect(response.ok()).toBe(true);
    const bytes = await response.body();
    const { createHash } = await import('node:crypto');
    expect(createHash('sha256').update(bytes).digest('hex')).toBe(manifest.sha256[path.slice(1)]);
  }
});

test('the onboarding deep route mounts its real React flow', async ({ page }) => {
  await page.goto('/onboarding');
  await expect(page.locator('#root')).toHaveAttribute('data-hermes-app', 'canonical-react');
  await expect(page.getByRole('heading', { name: 'Welcome to Hermes', exact: true })).toBeVisible();
});

test('document load failures are visible instead of an empty success', async ({ page }) => {
  await page.route('**/api/documents?*', route => route.fulfill({ status: 503, body: 'Synthetic unavailable' }));
  await page.goto('/documents');
  await expect(page.getByRole('alert')).toContainText('HTTP 503');
  await expect(page.getByText('No documents found', { exact: true })).toHaveCount(0);
});

test('a missing document has a useful error and a working return control', async ({ page }) => {
  await page.goto('/documents?id=999999');
  await expect(page.getByRole('alert')).toContainText('HTTP 404');
  await page.getByRole('button', { name: 'Back to documents', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Documents', exact: true })).toBeVisible();
});
