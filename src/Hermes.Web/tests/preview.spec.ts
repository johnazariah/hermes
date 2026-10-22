import { expect, test } from '@playwright/test';

// The default headless shell has no PDF viewer. The existing Chromium install includes this channel.
test.use({ channel: 'chromium' });

// UI response fixtures only. Access discovery and authority remain the real host's.
const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==', 'base64');

function syntheticPdf(): Buffer {
  const stream = 'q 1 0 0 rg 36 36 200 120 re f Q\n'
    + 'BT /F1 20 Tf 36 240 Td (Hermes synthetic PDF preview) Tj ET\n';
  const objects = [
    '<< /Type /Catalog /Pages 2 0 R >>',
    '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 300] '
      + '/Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>',
    '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>',
    `<< /Length ${Buffer.byteLength(stream)} >>\nstream\n${stream}endstream`,
  ];
  let body = '%PDF-1.4\n';
  const offsets = [0];
  for (const [index, object] of objects.entries()) {
    offsets.push(Buffer.byteLength(body));
    body += `${index + 1} 0 obj\n${object}\nendobj\n`;
  }
  const xref = Buffer.byteLength(body);
  body += `xref\n0 ${offsets.length}\n0000000000 65535 f \n`
    + offsets.slice(1).map(offset => `${String(offset).padStart(10, '0')} 00000 n \n`).join('')
    + `trailer\n<< /Size ${offsets.length} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
  return Buffer.from(body);
}

test('original PDF loads a real Chromium viewer and rasterises its synthetic page', async ({ page }) => {
  await page.addInitScript(() => {
    const original = URL.revokeObjectURL.bind(URL);
    const released: string[] = [];
    Object.assign(window, { previewReleases: released });
    URL.revokeObjectURL = value => { released.push(value); original(value); };
  });
  await page.route('**/api/documents/1', async route => {
    const response = await route.fetch();
    const body = await response.json();
    body.summary.originalName = 'synthetic-preview.pdf';
    await route.fulfill({ response, json: body });
  });
  const files: string[] = [];
  page.on('request', request => { if (new URL(request.url()).pathname === '/api/documents/1/file') files.push(request.url()); });
  await page.route('**/api/documents/1/file', route => route.fulfill({ contentType: 'application/pdf', body: syntheticPdf() }));
  await page.goto('/documents?id=1');
  const original = page.locator('object[aria-label="Document preview"]');
  await expect(original).toBeVisible();
  await expect(original).toHaveAttribute('type', 'application/pdf');
  await expect(original).toHaveAttribute('data', /^blob:/);
  const resource = await original.getAttribute('data');
  const download = page.getByRole('link', { name: 'Download original PDF', exact: true });
  await expect(download).toHaveAttribute('href', resource!);
  await expect(download).toHaveAttribute('download', 'synthetic-preview.pdf');
  await expect(page.getByText('If the PDF is blank or unavailable,')).toBeVisible();
  expect(files).toHaveLength(1);
  expect(new URL(files[0]).origin).toBe(new URL(page.url()).origin);

  // Inspect the real built-in viewer, not the outer object or the fetched PDF bytes.
  const viewerURL = 'chrome-extension://mhjfbmdgcfjbbpaeojofohoefgiehjai/index.html';
  await expect.poll(() => page.frames().some(frame => frame.url() === viewerURL)).toBe(true);
  const viewer = page.frames().find(frame => frame.url() === viewerURL)!;
  await expect(viewer.locator('pdf-viewer')).toBeVisible();
  await expect.poll(() => viewer.locator('pdf-viewer').evaluate(node =>
    (node as HTMLElement & { loadState_: string }).loadState_)).toBe('success');
  const plugin = viewer.locator('embed[type="application/x-google-chrome-pdf"]');
  await expect(plugin).toBeVisible();
  await expect(plugin).toHaveAttribute('original-url', resource!);
  const rendered = await viewer.locator('pdf-viewer').evaluate(async node => {
    const pdf = node as HTMLElement & {
      documentDimensions: { pageDimensions: { width: number; height: number }[] };
      pluginController_: {
        selectAll: () => void;
        getSelectedText: () => Promise<{ selectedText: string }>;
        requestThumbnail: (page: number) => Promise<{ width: number; height: number; imageData: ArrayBuffer }>;
      };
    };
    pdf.pluginController_.selectAll();
    const { selectedText } = await pdf.pluginController_.getSelectedText();
    const thumbnail = await pdf.pluginController_.requestThumbnail(0);
    const pixels = new Uint8Array(thumbnail.imageData);
    let redPixels = 0;
    for (let i = 0; i < pixels.length; i += 4) {
      if (pixels[i] > 240 && pixels[i + 1] < 15 && pixels[i + 2] < 15 && pixels[i + 3] === 255) redPixels++;
    }
    return { selectedText, pages: pdf.documentDimensions.pageDimensions, width: thumbnail.width,
      height: thumbnail.height, bytes: pixels.length, redPixels };
  });
  expect(rendered.selectedText).toContain('Hermes synthetic PDF preview');
  expect(rendered.pages).toHaveLength(1);
  expect(rendered.pages[0].width).toBeGreaterThan(0);
  expect(rendered.pages[0].height).toBeGreaterThan(0);
  expect(rendered.width).toBeGreaterThan(0);
  expect(rendered.height).toBeGreaterThan(0);
  expect(rendered.bytes).toBe(rendered.width * rendered.height * 4);
  expect(rendered.redPixels).toBeGreaterThan(rendered.width * rendered.height * 0.1);
  const saving = page.waitForEvent('download');
  await download.click();
  const saved = await saving;
  expect(saved.suggestedFilename()).toBe('synthetic-preview.pdf');
  expect(await saved.failure()).toBeNull();
  expect(files).toHaveLength(1);
  await page.getByRole('button', { name: '← Back', exact: true }).click();
  await expect(original).toHaveCount(0);
  await expect(download).toHaveCount(0);
  await expect.poll(() => page.evaluate(() =>
    (window as Window & { previewReleases?: string[] }).previewReleases ?? [])).toContain(resource);
});

test('original image uses a verified blob and releases it on document navigation', async ({ page }) => {
  await page.addInitScript(() => {
    const original = URL.revokeObjectURL.bind(URL);
    const released: string[] = [];
    Object.assign(window, { previewReleases: released });
    URL.revokeObjectURL = value => { released.push(value); original(value); };
  });
  await page.route('**/api/documents/1', async route => {
    const response = await route.fetch();
    const body = await response.json();
    body.summary.originalName = 'synthetic-preview.png';
    await route.fulfill({ response, json: body });
  });
  const files: string[] = [];
  page.on('request', request => { if (new URL(request.url()).pathname === '/api/documents/1/file') files.push(request.url()); });
  await page.route('**/api/documents/1/file', route => route.fulfill({ contentType: 'image/png', body: png }));
  await page.goto('/documents?id=1');
  const image = page.getByRole('img', { name: 'synthetic-preview.png' });
  await expect(image).toBeVisible();
  await expect(image).toHaveAttribute('src', /^blob:/);
  await expect.poll(() => image.evaluate(node => (node as HTMLImageElement).naturalWidth)).toBe(1);
  const resource = await image.getAttribute('src');
  expect(files).toHaveLength(1);
  expect(new URL(files[0]).origin).toBe(new URL(page.url()).origin);
  await page.getByRole('button', { name: '← Back', exact: true }).click();
  await expect(image).toHaveCount(0);
  await expect.poll(() => page.evaluate(() =>
    (window as Window & { previewReleases?: string[] }).previewReleases ?? [])).toContain(resource);
});

test('untrusted HTML cannot become an original preview and its format error is visible', async ({ page }) => {
  await page.route('**/api/documents/1', async route => {
    const response = await route.fetch();
    const body = await response.json();
    body.summary.originalName = 'synthetic-preview.pdf';
    await route.fulfill({ response, json: body });
  });
  await page.route('**/api/documents/1/file', route => route.fulfill({
    contentType: 'text/html', body: '<script>window.previewScriptExecuted=true</script>',
  }));
  await page.goto('/documents?id=1');
  await expect(page.getByRole('alert')).toContainText('Unable to preview original: The original file has an unsupported or mismatched preview format.');
  await expect(page.locator('iframe')).toHaveCount(0);
  await expect(page.locator('object')).toHaveCount(0);
  await expect(page.getByRole('link', { name: 'Download original PDF', exact: true })).toHaveCount(0);
  await expect(page.getByText(/Quartz salary evidence for browser smoke only/)).toBeVisible();
  expect(await page.evaluate(() => 'previewScriptExecuted' in window)).toBe(false);
});
