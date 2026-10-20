import { expect, test, type Page } from '@playwright/test';

const source = { documentId: 1, originalName: 'Synthetic stream source',
  resultType: 'document', savedPath: 'synthetic/stream.pdf', subject: null,
  category: 'payslips', sender: null, snippet: null, extractedAmount: 42,
  emailDate: null, extractedVendor: null, relevanceScore: -1 };
const results = `event: results\ndata: ${JSON.stringify({ results: [source] })}\n\n`;
const done = 'event: done\ndata: {}\n\n';

const browserErrors = new WeakMap<Page, string[]>();
test.beforeEach(({ page }) => {
  const errors: string[] = [];
  browserErrors.set(page, errors);
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => {
    if (message.type() === 'error' && /same key|unique.*key/i.test(message.text())) errors.push(message.text());
  });
});
test.afterEach(({ page }) => { expect(browserErrors.get(page) ?? []).toEqual([]); });

for (const [name, body, expected] of [
  ['keyword-only', results + done, 'Synthetic stream source'],
  ['optional answer', results + 'event: answer\ndata: {"answer":"Synthetic optional answer"}\n\n' + done, 'Synthetic optional answer'],
  ['truncated', results, 'Chat stream ended before its done event'],
  ['malformed JSON', 'event: results\ndata: broken\n\n' + done, 'Unexpected token'],
] as const) {
  test(`ChatPage and its shared ChatPane handle ${name}`, async ({ page }) => {
    await page.route('**/api/chat', route => route.fulfill({
      contentType: 'text/event-stream', body,
    }));
    await page.goto('/chat');
    await page.getByPlaceholder('Ask about your documents...').fill('synthetic');
    await page.getByRole('button', { name: 'Send', exact: true }).click();
    await expect(page.getByText(expected, { exact: name !== 'malformed JSON' })).toBeVisible();
    await expect(page.getByPlaceholder('Ask about your documents...')).toBeEnabled();
  });
}

test('HTTP chat failures remain visible rather than an empty success', async ({ page }) => {
  await page.route('**/api/chat', route => route.fulfill({ status: 503, body: 'Synthetic unavailable' }));
  await page.goto('/chat');
  await page.getByPlaceholder('Ask about your documents...').fill('synthetic');
  await page.getByRole('button', { name: 'Send', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('HTTP 503');
});

for (const resultType of ['document', 'email'] as const) {
test(`a legacy email cannot open a colliding ${resultType} document but its file-backed source can`, async ({ page }) => {
  const email = { ...source, resultType: 'email', savedPath: '', originalName: '',
    subject: 'Synthetic legacy message', category: 'email', extractedAmount: null,
    sender: 'synthetic@example.invalid', snippet: 'Synthetic metadata, not a document' };
  const document = { ...source, resultType, originalName: 'synthetic-payslip.md',
    savedPath: 'synthetic/payslip.md' };
  const lookups: string[] = [];
  page.on('request', request => {
    if (/\/api\/documents\/1(?:[/?]|$)/.test(request.url())) lookups.push(request.url());
  });
  await page.route('**/api/chat', route => route.fulfill({
    contentType: 'text/event-stream',
    body: `event: results\ndata: ${JSON.stringify({ results: [email, document] })}\n\n${done}`,
  }));
  await page.goto('/chat');
  await page.getByPlaceholder('Ask about your documents...').fill('synthetic mixed identities');
  await page.getByRole('button', { name: 'Send', exact: true }).click();
  const emailCard = page.getByRole('listitem').filter({ hasText: 'Synthetic legacy message' });
  await expect(emailCard).toContainText('Email metadata only — no archived document');
  await expect(emailCard).toContainText('synthetic@example.invalid');
  await expect(emailCard.getByRole('button')).toHaveCount(0);
  await expect(emailCard.getByRole('link')).toHaveCount(0);
  await emailCard.click();
  await expect(page).toHaveURL(/\/chat$/);
  expect(lookups).toEqual([]);
  await page.getByRole('button', { name: /synthetic-payslip.md/ }).click();
  await expect(page).toHaveURL(/\/documents\?id=1$/);
  await expect(page.getByRole('heading', { name: 'synthetic-payslip.md', exact: true })).toBeVisible();
  await expect(page.getByText(/Quartz salary evidence for browser smoke only/)).toBeVisible();
  expect(lookups.some(url => new URL(url).pathname === '/api/documents/1')).toBe(true);
});
}
