import { test } from 'node:test';
import assert from 'node:assert/strict';
import { setImmediate } from 'node:timers/promises';

test('archive action helpers require real acknowledgements and verify tag persistence without replay', async t => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const instanceId = 'AAAAAAAAAAAAAAAAAAAAAA';
  const pairingId = 'AgICAgICAgICAgICAgICAg';
  const origin = 'http://127.0.0.1:21742';
  let lastTagBody: unknown;
  let requests = 0;
  let persisted = true;
  let failMutation = false;
  let batchValid = true;
  let reclassificationError = false;
  let searchRows: unknown[] = [];
  const expiresAt = new Date(Date.now() + 1_800_000).toISOString();
  t.mock.method(globalThis, 'fetch', async (input: RequestInfo | URL, init: RequestInit = {}) => {
    const path = new URL(String(input), origin).pathname;
    const json = (value: unknown, status = 200) => new Response(JSON.stringify(value),
      { status, headers: { 'Content-Type': 'application/json' } });
    if (path === '/api/access') return json({
      version: 1, instanceId, processId: 1, processStartTimeUtc: new Date().toISOString(), origin,
      state: 'anonymous', capabilities: ['archive.read'], expiresAt: null, pairingMethod: 'ownerApproval',
    });
    if (path === '/api/access/pairings') return json({
      version: 1, instanceId, pairingId, userCode: 'ABCD-EFGH-JKMN', browserOrigin: origin, apiOrigin: origin,
      requestedCapabilities: ['config.read', 'model.invoke', 'mutate'],
      expiresAt: new Date(Date.now() + 180_000).toISOString(), status: 'pending',
    }, 201);
    if (path.endsWith('/exchange')) return json({
      version: 1, instanceId, tokenType: 'Bearer', accessToken: `h1.${instanceId}.${'A'.repeat(43)}`,
      capabilities: ['config.read', 'model.invoke', 'mutate'], expiresAt,
    }, 201);
    if (path === '/api/search') return json(searchRows);
    if (path === '/api/documents/batch') {
      requests++;
      if (failMutation) return json({ error: 'Unavailable', message: 'Synthetic unavailable' }, 503);
      if (reclassificationError) return json({ error: 'Archive database is read-only' });
      return json(batchValid ? { action: 'tag', count: 2 } : { status: 'ok' });
    }
    if (path === '/api/documents/1/tags' || path === '/api/documents/2/tags') {
      if (init.method === 'POST') {
        requests++;
        lastTagBody = JSON.parse(String(init.body));
        return json({ added: ['synthetic-tag'] });
      }
      return json(persisted ? [{ tag: 'synthetic-tag', source: 'user', confidence: null }] : []);
    }
    throw new Error('Unexpected unit API operation');
  });
  const { access } = await import('../../src/api/http.ts');
  const api = await import('../../src/api/hermes.ts');
  t.after(access.dispose);
  await access.check(); await access.pair();
  t.mock.timers.tick(2000);
  for (let i = 0; i < 8; i++) await setImmediate();
  assert.equal(access.getSnapshot().phase, 'authenticated');
  await t.test('single tag sends the server tags array and verifies persisted source', async () => {
    await api.addDocumentTag(1, 'synthetic-tag');
    assert.deepEqual(lastTagBody, { tags: ['synthetic-tag'] });
  });
  await t.test('a 200 tag no-op is not displayed as success', async () => {
    persisted = false;
    await assert.rejects(api.addDocumentTag(1, 'synthetic-tag'), /did not persist/);
    persisted = true;
  });
  await t.test('batch tag verifies every document and rejects an unconfirmed response', async () => {
    await api.batchDocuments([1, 2], 'tag', 'synthetic-tag');
    batchValid = false;
    await assert.rejects(api.batchDocuments([1, 2], 'tag', 'synthetic-tag'), /did not confirm/);
    batchValid = true;
  });
  await t.test('failed batch does not fall back to individual mutation retries', async () => {
    const before = requests;
    failMutation = true;
    await assert.rejects(api.batchDocuments([1, 2], 'tag', 'synthetic-tag'), /HTTP 503/);
    assert.equal(requests, before + 1);
    failMutation = false;
  });
  await t.test('HTTP 200 reclassification domain error retains its actual failed outcome', async () => {
    reclassificationError = true;
    await assert.rejects(api.reclassifyDocuments([1], 'synthetic'), /Archive database is read-only/);
    reclassificationError = false;
  });
  await t.test('model search keeps document/email source identities distinct', async () => {
    searchRows = [
      { id: 1, path: '', resultType: 'email', originalName: '', subject: 'Synthetic metadata', category: 'email', sender: null, snippet: null, amount: null },
      { id: 1, path: 'synthetic/file.md', resultType: 'document', originalName: 'Synthetic file', subject: null, category: 'synthetic', sender: null, snippet: null, amount: 42 },
    ];
    const rows = await api.searchWithModel('synthetic', '', 'semantic');
    assert.equal(rows[0].savedPath, '');
    assert.equal(rows[0].resultType, 'email');
    assert.equal(rows[0].originalName, '');
    assert.equal(rows[0].subject, 'Synthetic metadata');
    assert.equal(rows[1].savedPath, 'synthetic/file.md');
    assert.equal(rows[1].documentId, rows[0].documentId);
    searchRows = [{ id: 1, path: null }];
    await assert.rejects(api.searchWithModel('synthetic', '', 'hybrid'), /invalid search source/);
  });
});
