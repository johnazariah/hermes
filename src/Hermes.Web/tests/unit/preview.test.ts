import { test, type TestContext } from 'node:test';
import assert from 'node:assert/strict';
import { AccessClient } from '../../src/api/access.ts';
import { loadDocumentPreview, previewKind, type PreviewSource } from '../../src/api/preview.ts';

const browserOrigin = 'http://localhost:5173';
const apiOrigin = 'http://127.0.0.1:21849';
const instanceId = 'AAAAAAAAAAAAAAAAAAAAAA';
const pdf = new TextEncoder().encode('%PDF-1.7\nsynthetic only');
const png = new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0]);
const file = (bytes = pdf, mime = 'application/pdf') => new Response(bytes, { headers: { 'Content-Type': mime } });

async function fixture(t: TestContext, selected = true) {
  const calls: { url: string; init: RequestInit }[] = [];
  let reply: () => Response | Promise<Response> = () => file();
  const client = new AccessClient({ browserOrigin, apiOrigin: selected ? apiOrigin : undefined, apiAudience: apiOrigin,
    fetch: (async (input, init = {}) => {
      calls.push({ url: String(input), init });
      if (new URL(String(input), browserOrigin).pathname === '/api/access') return Response.json({
        version: 1, instanceId, processId: 123, processStartTimeUtc: '2026-10-20T00:00:00Z', origin: apiOrigin,
        state: 'anonymous', capabilities: ['archive.read'], expiresAt: null, pairingMethod: 'ownerApproval',
      });
      return reply();
    }) as typeof fetch,
  });
  t.after(client.dispose);
  await client.check();
  const source: PreviewSource = { documentId: 7, originalName: 'synthetic.pdf', filePath: 'synthetic/source.pdf',
    instanceId, generation: client.getSnapshot().generation };
  const created: Blob[] = [];
  const revoked: string[] = [];
  const urls = {
    createObjectURL: (blob: Blob) => { created.push(blob); return `blob:owned-preview-${created.length}`; },
    revokeObjectURL: (url: string) => { revoked.push(url); },
  };
  return { client, calls, source, created, revoked, urls, respond: (next: typeof reply) => { reply = next; } };
}

for (const selected of [true, false]) {
  test(`${selected ? 'selected API B' : 'trusted same-origin proxy'} preview uses the shared anonymous transport, not UI A`, async t => {
    const f = await fixture(t, selected);
    const controller = new AbortController();
    const resource = await loadDocumentPreview(f.client, f.source, controller.signal, f.urls);
    assert.equal(f.calls.at(-1)!.url, selected ? `${apiOrigin}/api/documents/7/file` : '/api/documents/7/file');
    const { init } = f.calls.at(-1)!;
    assert.equal(init.credentials, 'omit');
    assert.equal(init.redirect, 'error');
    assert.equal(init.cache, 'no-store');
    assert.equal(new Headers(init.headers).has('Authorization'), false);
    assert.equal(f.created[0].type, 'application/pdf');
    assert.deepEqual(new Uint8Array(await f.created[0].arrayBuffer()), pdf);
    assert.equal(resource.url, 'blob:owned-preview-1');
    controller.abort();
    resource.dispose();
    assert.deepEqual(f.revoked, [resource.url]);
  });
}

for (const [name, mime, bytes] of [
  ['synthetic.PNG', 'image/png', png],
  ['synthetic.jpg', 'image/jpeg', new Uint8Array([255, 216, 255, 0])],
  ['synthetic.jpeg', 'image/jpeg', new Uint8Array([255, 216, 255, 0])],
  ['synthetic.gif', 'image/gif', new TextEncoder().encode('GIF87a')],
  ['synthetic.gif', 'image/gif', new TextEncoder().encode('GIF89a')],
  ['synthetic.webp', 'image/webp', new Uint8Array([82, 73, 70, 70, 195, 128, 0, 0, 87, 69, 66, 80])],
] as const) {
  test(`allowed raster signature ${name} (${Array.from(bytes).join(',')}) produces a typed preview`, async t => {
    const f = await fixture(t);
    f.respond(() => file(bytes, `${mime}; synthetic=fixture`));
    const resource = await loadDocumentPreview(f.client, { ...f.source, originalName: name },
      new AbortController().signal, f.urls);
    assert.equal(f.created[0].type, mime);
    resource.dispose();
    assert.deepEqual(f.revoked, [resource.url]);
  });
}

test('leaving an old document aborts its file fetch; a late reply cannot allocate an old-source URL', async t => {
  const f = await fixture(t);
  let release!: (response: Response) => void;
  f.respond(() => new Promise(resolve => { release = resolve; }));
  const old = new AbortController();
  const pending = assert.rejects(loadDocumentPreview(f.client, f.source, old.signal, f.urls), { name: 'AbortError' });
  const oldCall = f.calls.at(-1)!;
  old.abort();
  assert.equal(oldCall.init.signal?.aborted, true);
  f.respond(() => file(png, 'image/png'));
  const current = await loadDocumentPreview(f.client,
    { ...f.source, documentId: 8, originalName: 'new-source.png', filePath: 'synthetic/new-source.png' },
    new AbortController().signal, f.urls);
  assert.match(f.calls.at(-1)!.url, /\/8\/file$/);
  release(file());
  await pending;
  assert.equal(f.created.length, 1);
  assert.equal(f.created[0].type, 'image/png');
  current.dispose();
  assert.deepEqual(f.revoked, [current.url]);
});

test('lock fences pending preview body reads and revokes already-owned URLs immediately', async t => {
  const f = await fixture(t);
  const resource = await loadDocumentPreview(f.client, f.source, new AbortController().signal, f.urls);
  let writer!: ReadableStreamDefaultController<Uint8Array>;
  let cancelled = false;
  f.respond(() => new Response(new ReadableStream<Uint8Array>({
    start(controller) { writer = controller; controller.enqueue(pdf); },
    cancel() { cancelled = true; },
  }), { headers: { 'Content-Type': 'application/pdf' } }));
  const pending = assert.rejects(loadDocumentPreview(f.client, f.source, new AbortController().signal, f.urls), { name: 'AbortError' });
  await Promise.resolve();
  const locking = f.client.lock();
  assert.deepEqual(f.revoked, [resource.url]);
  await locking;
  await pending;
  assert.equal(cancelled, true);
  assert.throws(() => writer.enqueue(pdf));
  assert.equal(f.created.length, 1);
});

for (const [name, bytes, mime] of [
  ['HTML labelled HTML', new TextEncoder().encode('<html><script>unsafe</script>'), 'text/html'],
  ['HTML labelled PDF', new TextEncoder().encode('<html>not a PDF'), 'application/pdf'],
  ['wrong extension', png, 'image/png'],
  ['missing MIME', pdf, ''],
  ['SVG masquerading as raster', new TextEncoder().encode('<svg onload="unsafe"/>'), 'image/svg+xml'],
] as const) {
  test(`preview rejects ${name} before creating a same-origin resource`, async t => {
    const f = await fixture(t);
    f.respond(() => file(bytes, mime));
    await assert.rejects(loadDocumentPreview(f.client, f.source, new AbortController().signal, f.urls), /preview format/);
    assert.deepEqual(f.created, []);
  });
}

test('preview failures and redirects remain errors rather than resources; stale instance never fetches', async t => {
  const f = await fixture(t);
  f.respond(() => new Response(null, { status: 302 }));
  await assert.rejects(loadDocumentPreview(f.client, f.source, new AbortController().signal, f.urls), /redirect/);
  f.respond(() => Response.json({ error: 'not_found', message: 'Synthetic missing file' }, { status: 404 }));
  await assert.rejects(loadDocumentPreview(f.client, f.source, new AbortController().signal, f.urls), /HTTP 404/);
  const count = f.calls.length;
  await assert.rejects(loadDocumentPreview(f.client, { ...f.source, instanceId: 'AQEBAQEBAQEBAQEBAQEBAQ' },
    new AbortController().signal, f.urls), { name: 'AbortError' });
  assert.equal(f.calls.length, count);
  assert.equal(f.created.length, 0);
  assert.equal(previewKind('A.PDF'), 'pdf');
  assert.equal(previewKind('A.JPEG'), 'image');
  assert.equal(previewKind('A.svg'), undefined);
});
