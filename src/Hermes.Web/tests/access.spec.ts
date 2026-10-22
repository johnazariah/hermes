import { expect, test, type Request, type Response } from '@playwright/test';
import { approveDisplayedPairing, unlock } from './owner';
import { accessDiagnostic, type AccessDiagnostic } from './access-diagnostic';

const panelName = 'Hermes access';
function controlURL(baseURL: string | undefined, path: string): string {
  if (!baseURL) throw new Error('An owned synthetic Service is required');
  const url = new URL(path, baseURL);
  url.port = String(Number(url.port) + 1);
  return url.href;
}

test('anonymous settings do not fetch protected configuration or mistake denial for onboarding', async ({ page, baseURL }) => {
  if (!baseURL) throw new Error('An owned synthetic Service is required');
  const target = new URL('/api/access', baseURL).href;
  const started = performance.now();
  const diagnostic: AccessDiagnostic = {
    requests: 0, responses: 0, finished: 0, failed: 0, status: 0,
    requestMs: -1, headersMs: -1, bodyMs: -1,
  };
  let selected: Request | undefined;
  const requested = (request: Request) => {
    if (request.url() !== target || request.method() !== 'GET') return;
    diagnostic.requests++;
    if (selected) return;
    selected = request;
    diagnostic.requestMs = performance.now() - started;
  };
  const responded = (response: Response) => {
    if (response.request() !== selected) return;
    diagnostic.responses++;
    diagnostic.status = response.status();
    diagnostic.headersMs = performance.now() - started;
  };
  const finished = (request: Request) => {
    if (request !== selected) return;
    diagnostic.finished++;
    diagnostic.bodyMs = performance.now() - started;
  };
  const failed = (request: Request) => {
    if (request === selected) diagnostic.failed++;
  };
  page.on('request', requested);
  page.on('response', responded);
  page.on('requestfinished', finished);
  page.on('requestfailed', failed);
  const protectedReads: string[] = [];
  page.on('request', request => {
    if (/\/api\/(?:settings|preferences|sync\/accounts)(?:[/?]|$)/.test(request.url())) protectedReads.push(request.url());
  });
  try {
    await page.goto('/settings');
    await expect(page.getByRole('region', { name: panelName })).toContainText('Access: anonymous');
    await expect(page.getByRole('heading', { name: 'Settings', exact: true })).toBeVisible();
    await expect(page.locator('textarea').first()).toBeDisabled();
    await expect(page.getByRole('button', { name: 'Save', exact: true }).first()).toBeDisabled();
    await expect(page.getByRole('button', { name: 'Sync Now', exact: true })).toBeDisabled();
    await expect(page).toHaveURL(/\/settings$/);
    expect(protectedReads).toEqual([]);
  } finally {
    page.off('request', requested);
    page.off('response', responded);
    page.off('requestfinished', finished);
    page.off('requestfailed', failed);
    console.info(accessDiagnostic(diagnostic));
  }
});

test('real HTTP policy denies anonymous sensitive reads, writes and model calls before provider work', async ({ request, baseURL }) => {
  const before = await (await request.get(controlURL(baseURL, '/fixtures/provider'))).json();
  for (const path of ['/api/settings', '/api/preferences', '/api/sync/accounts']) {
    const response = await request.get(path);
    expect(response.status(), path).toBe(401);
    expect((await response.json()).error).toBe('authentication_required');
    expect(response.headers()['cache-control']).toContain('no-store');
  }
  const mutation = await request.post('/api/sync');
  expect(mutation.status()).toBe(401);
  const chat = await request.post('/api/chat', { data: { query: 'synthetic quartz', aiEnabled: true } });
  expect(chat.status()).toBe(401);
  for (const mode of ['semantic', 'hybrid']) {
    const response = await request.get(`/api/search?q=synthetic&mode=${mode}`);
    expect(response.status()).toBe(401);
  }
  const after = await (await request.get(controlURL(baseURL, '/fixtures/provider'))).json();
  expect(after.providerCalls).toBe(before.providerCalls);
});

test('anonymous chat explicitly sends search-only false and makes no model call', async ({ page, request, baseURL }) => {
  const before = await (await request.get(controlURL(baseURL, '/fixtures/provider'))).json();
  await page.goto('/chat');
  await expect(page.getByRole('region', { name: panelName })).toContainText('Access: anonymous');
  await expect(page.getByRole('checkbox', { name: 'Enable AI answer' })).toBeDisabled();
  await page.getByPlaceholder('Ask about your documents...').fill('quartz');
  const sent = page.waitForRequest(value => new URL(value.url()).pathname === '/api/chat');
  await page.getByRole('button', { name: 'Send', exact: true }).click();
  expect((await sent).postDataJSON()).toEqual({ query: 'quartz', aiEnabled: false });
  await expect(page.getByRole('button', { name: /synthetic-payslip.md/ })).toBeVisible();
  const after = await (await request.get(controlURL(baseURL, '/fixtures/provider'))).json();
  expect(after.providerCalls).toBe(before.providerCalls);
});

test('anonymous semantic and hybrid controls are visibly permission-gated without issuing an encoder request', async ({ page }) => {
  const calls: string[] = [];
  page.on('request', request => { if (new URL(request.url()).pathname === '/api/search') calls.push(request.url()); });
  await page.goto('/search');
  await expect(page.getByRole('region', { name: panelName })).toContainText('Access: anonymous');
  await expect(page.locator('option[value="semantic"]')).toHaveJSProperty('disabled', true);
  await expect(page.locator('option[value="hybrid"]')).toHaveJSProperty('disabled', true);
  const mode = page.getByRole('combobox', { name: 'Search mode' });
  await mode.focus();
  await page.keyboard.press('ArrowDown');
  await expect(mode).toHaveValue('keyword');
  await expect(page.getByText('Unlock Hermes with model.invoke (semantic/hybrid search) permission to use these controls.')).toBeVisible();
  expect(calls).toEqual([]);
});

test('owner CLI pairing unlocks protected reads; reload deliberately locks and clears the editor', async ({ page }) => {
  await page.goto('/settings');
  await unlock(page);
  const editor = page.locator('textarea').first();
  await expect(editor).toBeEnabled();
  await editor.fill('Synthetic unsaved sensitive editor text');
  const storage = await page.evaluate(() => ({
    local: { ...localStorage }, session: { ...sessionStorage }, cookie: document.cookie, url: location.href,
  }));
  expect(JSON.stringify(storage)).not.toMatch(/h1\.[A-Za-z0-9_-]{22}\.[A-Za-z0-9_-]{43}/);
  expect(storage.cookie).toBe('');
  await page.reload();
  await expect(page.getByRole('region', { name: panelName })).toContainText('Access: anonymous');
  await expect(editor).toBeDisabled();
  await expect(editor).toHaveValue('');
  await expect(page).toHaveURL(/\/settings$/);
});

test('owner-approved document tag uses tags array and is actually persisted in the real archive', async ({ page, request }) => {
  await page.goto('/documents');
  await unlock(page);
  await page.getByRole('checkbox', { name: 'Select synthetic-payslip.md' }).check();
  await page.getByPlaceholder('tag name…').fill('synthetic-owner-approved');
  const sent = page.waitForRequest(value => new URL(value.url()).pathname === '/api/documents/1/tags' && value.method() === 'POST');
  await page.getByRole('button', { name: 'Apply', exact: true }).click();
  expect((await sent).postDataJSON()).toEqual({ tags: ['synthetic-owner-approved'] });
  await expect.poll(async () => {
    const response = await request.get('/api/documents/1/tags');
    expect(response.status()).toBe(200);
    const rows: { tag: string; source: string }[] = await response.json();
    return rows.some(row => row.tag === 'synthetic-owner-approved' && row.source === 'user');
  }).toBe(true);
});

test('locking revokes the actual session, clears protected editors and keeps anonymous document browsing useful', async ({ page }) => {
  await page.goto('/settings');
  await unlock(page);
  await page.locator('textarea').first().fill('Synthetic discarded editor');
  const revoked = page.waitForResponse(value => new URL(value.url()).pathname === '/api/access/session');
  await page.getByRole('button', { name: 'Lock Hermes', exact: true }).click();
  expect((await revoked).status()).toBe(204);
  await expect(page.locator('textarea').first()).toHaveValue('');
  await expect(page.locator('textarea').first()).toBeDisabled();
  await page.getByRole('link', { name: 'Documents', exact: true }).click();
  await expect(page.getByRole('link', { name: 'synthetic-payslip.md', exact: true })).toBeVisible();
});

test('cancelled pairing cannot be revived by a late actual owner approval', async ({ page }) => {
  await page.goto('/settings');
  const panel = page.getByRole('region', { name: panelName });
  await expect(panel).toContainText('Access: anonymous');
  await panel.getByRole('button', { name: 'Unlock Hermes' }).click();
  await expect(page.getByTestId('pairing-code')).toBeVisible();
  const code = (await page.getByTestId('pairing-code').innerText()).trim();
  await panel.getByRole('button', { name: 'Cancel pairing' }).click();
  // The server can still approve the abandoned request; the page no longer has a verifier.
  await approveDisplayedPairing(page, code);
  await expect(panel).toContainText('Access: anonymous');
  await expect(page.locator('textarea').first()).toBeDisabled();
});

test('browser monotonic expiry locks a real owner-approved session without pretending to advance server time', async ({ page }) => {
  await page.clock.install();
  await page.goto('/settings');
  await unlock(page);
  await page.locator('textarea').first().fill('Synthetic expiring editor');
  await page.clock.fastForward(1_800_001);
  await expect(page.getByRole('region', { name: panelName })).toContainText('Access: expired');
  await expect(page.locator('textarea').first()).toHaveValue('');
  await expect(page.getByRole('button', { name: 'Reconnect', exact: true })).toBeEnabled();
});

test('browser pairing expiry is bounded and visibly offers a fresh code', async ({ page }) => {
  await page.clock.install();
  await page.goto('/settings');
  const panel = page.getByRole('region', { name: panelName });
  await expect(panel).toContainText('Access: anonymous');
  await panel.getByRole('button', { name: 'Unlock Hermes' }).click();
  await expect(page.getByTestId('pairing-code')).toBeVisible();
  await page.clock.fastForward(180_001);
  await expect(panel).toContainText('Access: expired');
  await expect(page.getByTestId('pairing-code')).toHaveCount(0);
  await expect(panel.getByRole('button', { name: 'Reconnect', exact: true })).toBeEnabled();
});

test('actual owned Service restart fences old authority and requires a fresh owner pairing', async ({ page, request, baseURL }) => {
  await page.goto('/settings');
  await unlock(page);
  await page.locator('textarea').first().fill('Synthetic pre-restart editor');
  const before = await (await request.get('/api/access')).json();
  const restarted = await request.post(controlURL(baseURL, '/fixtures/restart'));
  expect(restarted.status()).toBe(201);
  const after = await (await request.get('/api/access')).json();
  expect(after.instanceId).not.toBe(before.instanceId);
  await page.evaluate(() => { window.dispatchEvent(new Event('focus')); });
  const panel = page.getByRole('region', { name: panelName });
  await expect(panel).not.toContainText('Access: authenticated');
  await expect(page.locator('textarea').first()).toHaveValue('');
  await expect(page.locator('textarea').first()).toBeDisabled();
  await unlock(page);
  await expect(page.locator('textarea').first()).toBeEnabled();
});

test('same-instance focus and visibility preserve unsaved editors, pending save and actual success feedback', async ({ page }) => {
  await page.goto('/settings');
  await unlock(page);
  await page.getByRole('button', { name: /Configuration \(YAML\)/ }).click();
  const preferences = page.locator('textarea').nth(0);
  const yaml = page.locator('textarea').nth(1);
  await expect(preferences).not.toHaveValue('');
  const originalPreferences = await preferences.inputValue();
  await preferences.fill('Synthetic focus-preserved preferences');
  await yaml.fill('# Synthetic unsaved YAML: must survive ordinary app switching');
  let release!: () => void;
  const held = new Promise<void>(resolve => { release = resolve; });
  let saveCount = 0;
  await page.route('**/api/preferences', async route => {
    if (route.request().method() !== 'PUT') { await route.continue(); return; }
    saveCount++;
    const response = await route.fetch();
    expect(response.status()).toBe(200);
    await held;
    await route.fulfill({ response });
  });
  try {
    await page.getByRole('button', { name: 'Save', exact: true }).nth(0).click();
    await expect(preferences).toBeDisabled();
    await expect.poll(() => saveCount).toBe(1);
    const status = page.waitForResponse(value => new URL(value.url()).pathname === '/api/access');
    await page.evaluate(() => {
      window.dispatchEvent(new Event('focus'));
      document.dispatchEvent(new Event('visibilitychange'));
    });
    expect((await status).status()).toBe(200);
    await expect(page.getByRole('region', { name: panelName })).toContainText('Access: authenticated');
    await expect(preferences).toHaveValue('Synthetic focus-preserved preferences');
    await expect(preferences).toBeDisabled();
    await expect(yaml).toHaveValue('# Synthetic unsaved YAML: must survive ordinary app switching');
    release();
    await expect(page.getByRole('status')).toContainText('Preferences saved.');
    await expect(preferences).toBeEnabled();
    const rechecked = page.waitForResponse(value => new URL(value.url()).pathname === '/api/access');
    await page.evaluate(() => { window.dispatchEvent(new Event('focus')); });
    expect((await rechecked).status()).toBe(200);
    await expect(page.getByRole('status')).toContainText('Preferences saved.');
    await expect(yaml).toHaveValue('# Synthetic unsaved YAML: must survive ordinary app switching');
    expect(saveCount).toBe(1);
  } finally {
    release();
    await page.unroute('**/api/preferences');
    await expect(preferences).toBeEnabled();
    await preferences.fill(originalPreferences);
    await page.getByRole('button', { name: 'Save', exact: true }).nth(0).click();
    await expect(page.getByRole('status')).toContainText('Preferences saved.');
  }
});

test('local lock clears editors before the real server revocation response is delivered', async ({ page }) => {
  await page.goto('/settings');
  await unlock(page);
  await page.locator('textarea').first().fill('Synthetic sensitive edit: clear before network completion');
  let release!: () => void;
  const held = new Promise<void>(resolve => { release = resolve; });
  let revocations = 0;
  await page.route('**/api/access/session', async route => {
    revocations++;
    const response = await route.fetch();
    expect(response.status()).toBe(204);
    await held;
    await route.fulfill({ response });
  });
  try {
    await page.getByRole('button', { name: 'Lock Hermes', exact: true }).click();
    await expect.poll(() => revocations).toBe(1);
    await expect(page.getByRole('region', { name: panelName })).toContainText('Access: anonymous');
    await expect(page.locator('textarea').first()).toHaveValue('');
    await expect(page.locator('textarea').first()).toBeDisabled();
    await expect(page.getByRole('region', { name: panelName })).toContainText('Locked locally.');
    const revoked = page.waitForResponse(value => new URL(value.url()).pathname === '/api/access/session');
    release();
    expect((await revoked).status()).toBe(204);
    await expect(page.getByRole('region', { name: panelName })).toContainText('Locked.');
    expect(revocations).toBe(1);
  } finally { release(); }
});
