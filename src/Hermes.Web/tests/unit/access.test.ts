import { test, type TestContext } from 'node:test';
import assert from 'node:assert/strict';
import { createHash, webcrypto } from 'node:crypto';
import { setImmediate } from 'node:timers/promises';
import { AccessClient, ApiError, strictJson, requestedCapabilities } from '../../src/api/access.ts';
import { readChat } from '../../src/api/chat.ts';

const instanceId = 'AAAAAAAAAAAAAAAAAAAAAA';
const replacement = 'AQEBAQEBAQEBAQEBAQEBAQ';
const pairingId = 'AgICAgICAgICAgICAgICAg';
const navigationId = 'AwMDAwMDAwMDAwMDAwMDAw';
const nextNavigation = 'BAQEBAQEBAQEBAQEBAQEBA';
const apiOrigin = 'http://127.0.0.1:21843';
const browserOrigin = 'http://localhost:5173';
const epoch = Date.parse('2026-10-20T00:00:00Z');
const bearer = `h1.${instanceId}.${'A'.repeat(43)}`;
const iso = (ms: number) => new Date(epoch + ms).toISOString();
const json = (value: unknown, status = 200) => new Response(JSON.stringify(value),
  { status, headers: { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' } });
const statusBody = (authenticated = false) => ({
  version: 1, instanceId, processId: 123, processStartTimeUtc: iso(-1000), origin: apiOrigin,
  state: authenticated ? 'authenticated' : 'anonymous',
  capabilities: authenticated ? ['archive.read', ...requestedCapabilities] : ['archive.read'],
  expiresAt: authenticated ? iso(1_800_000) : null, pairingMethod: 'ownerApproval',
});
const pairBody = () => ({
  version: 1, instanceId, pairingId, userCode: 'ABCD-EFGH-JKMN',
  browserOrigin, apiOrigin, requestedCapabilities: [...requestedCapabilities],
  expiresAt: iso(180_000), status: 'pending',
});
const exchangeBody = (capabilities: readonly string[] = requestedCapabilities) => ({
  version: 1, instanceId, tokenType: 'Bearer', accessToken: bearer, capabilities, expiresAt: iso(1_800_000),
});
const ready = (id = navigationId, instance = instanceId) => ({
  version: 1, type: 'hermes.access.ready', navigationId: id, instanceId: instance,
});
const error = (status: number, code: string, requiredCapabilities: string[] = []) =>
  json({ error: code, message: 'Fixed safe error', instanceId, requiredCapabilities }, status);

function fixture(t: TestContext, selected = false) {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  let elapsed = 0;
  let utcElapsed = 0;
  let granted = false;
  const calls: { path: string; init: RequestInit }[] = [];
  const native: object[] = [];
  const nativeEvents = new EventTarget();
  const routes = new Map<string, (init: RequestInit) => Response | Promise<Response>>();
  const fetcher = async (input: RequestInfo | URL, init: RequestInit = {}) => {
    const path = new URL(String(input), browserOrigin).pathname;
    calls.push({ path, init });
    if (routes.has(path)) return routes.get(path)!(init);
    if (path === '/api/access') return json(statusBody(granted));
    if (path === '/api/access/pairings') return json(pairBody(), 201);
    if (path.endsWith('/exchange')) { granted = true; return json(exchangeBody(), 201); }
    if (path === '/api/access/session') { granted = false; return new Response(null, { status: 204 }); }
    return json({ saved: true });
  };
  const client = new AccessClient({
    browserOrigin, apiOrigin: selected ? apiOrigin : undefined, apiAudience: apiOrigin,
    fetch: fetcher as typeof fetch, crypto: webcrypto as Crypto,
    now: () => elapsed, utcNow: () => epoch + utcElapsed, postNative: value => {
      native.push(value);
      nativeEvents.dispatchEvent(new Event('message'));
    },
  });
  t.after(() => { client.dispose(); });
  const tick = async (ms: number) => {
    elapsed += ms; utcElapsed += ms; t.mock.timers.tick(ms);
    for (let i = 0; i < 8; i++) await setImmediate();
  };
  const unlock = async () => { await client.check(); await client.pair(); await tick(2000); assert.equal(client.getSnapshot().phase, 'authenticated'); };
  const nextNative = () => new Promise<void>(resolve =>
    nativeEvents.addEventListener('message', () => resolve(), { once: true }));
  return { client, routes, calls, native, nextNative, tick, unlock, rollback: () => { utcElapsed -= 86_400_000; } };
}

test('S256 pair proof is random, canonical and only the verifier exchange sends its verifier', async t => {
  const f = fixture(t);
  await f.client.check(); await f.client.pair();
  const create = JSON.parse(f.calls.find(c => c.path === '/api/access/pairings')!.init.body as string);
  assert.deepEqual(create.requestedCapabilities, requestedCapabilities);
  assert.match(create.challenge, /^[A-Za-z0-9_-]{43}$/);
  assert.equal(f.client.getSnapshot().phase, 'pairing');
  assert.equal(f.calls.filter(c => c.path.endsWith('/exchange')).length, 0);
  await f.tick(1999);
  assert.equal(f.calls.filter(c => c.path.endsWith('/exchange')).length, 0);
  await f.tick(1);
  const proof = JSON.parse(f.calls.find(c => c.path.endsWith('/exchange'))!.init.body as string);
  assert.match(proof.verifier, /^[A-Za-z0-9_-]{43}$/);
  assert.equal(createHash('sha256').update(proof.verifier).digest('base64url'), create.challenge);
  assert.equal(JSON.stringify(f.client.getSnapshot()).includes(proof.verifier), false);
  assert.equal(JSON.stringify(f.client.getSnapshot()).includes(bearer), false);
});

test('preview proxy origin U stays distinct from audience A; transport is relative and credentials omitted', async t => {
  const f = fixture(t);
  await f.unlock();
  await (await f.client.request('/api/settings')).text();
  const call = f.calls.at(-1)!;
  assert.equal(call.path, '/api/settings');
  assert.equal(call.init.credentials, 'omit');
  assert.equal(call.init.redirect, 'error');
  assert.equal(call.init.cache, 'no-store');
  assert.equal(new Headers(call.init.headers).get('Authorization'), `Bearer ${bearer}`);
  assert.equal(f.client.getSnapshot().apiOrigin, apiOrigin);
});

test('explicit selected API transport pins the discovered numeric audience', async t => {
  const f = fixture(t, true);
  f.routes.set('/api/access', () => json({ ...statusBody(), origin: 'http://127.0.0.1:21844' }));
  await f.client.check();
  assert.equal(f.client.getSnapshot().phase, 'unavailable');
});

test('bad destinations and caller authority never reach fetch', async t => {
  const f = fixture(t);
  await f.unlock();
  const count = f.calls.length;
  for (const path of ['https://external.invalid/api/documents', '//foreign/api/x', '/mcp', '/api/mcp',
    '/api/x/../../mcp', '/api/x/../settings', '/api/%2e%2e/mcp', '/api/x%2fy', '/api/x\\y', '/api/documents#token']) {
    await assert.rejects(f.client.request(path));
  }
  await assert.rejects(f.client.request('/api/stats', { headers: { authorization: 'Bearer caller' } }));
  await assert.rejects(f.client.request('/api/access/pairings', { method: 'POST' }));
  assert.equal(f.calls.length, count);
});

for (const [name, patch] of [
  ['missing version', { version: undefined }], ['unknown field', { extra: true }],
  ['malformed ID', { instanceId: 'not-canonical' }], ['wrong method', { pairingMethod: 'cookie' }],
  ['external audience', { origin: 'https://external.invalid' }], ['unordered permissions', { capabilities: ['mutate', 'archive.read'] }],
  ['duplicate permissions', { capabilities: ['archive.read', 'archive.read'] }], ['anonymous expiry', { expiresAt: iso(1000) }],
  ['invalid process', { processId: 0 }], ['invalid start time', { processStartTimeUtc: 'yesterday' }],
] as const) {
  test(`status rejects ${name}`, async t => {
    const f = fixture(t);
    f.routes.set('/api/access', () => json({ ...statusBody(), ...patch }));
    await f.client.check();
    assert.equal(f.client.getSnapshot().phase, 'unavailable');
    assert.equal(f.client.can('mutate'), false);
  });
}

test('strict access JSON rejects duplicates, BOM, trailing data, excessive depth and invalid UTF-8', async t => {
  for (const text of ['{"x":1,"x":2}', '\ufeff{}', '{}{}', '{"a":{"b":{"c":{"d":{"e":{"f":{"g":{"h":{}}}}}}}}', '{"x":1,}']) {
    assert.throws(() => strictJson(text));
  }
  assert.deepEqual(strictJson('{"a":[{"x":"escaped \\"name\\""}],"b":true}'), { a: [{ x: 'escaped "name"' }], b: true });
  const f = fixture(t);
  f.routes.set('/api/access', () => new Response(new Uint8Array([255]), { headers: { 'Content-Type': 'application/json' } }));
  await f.client.check();
  assert.equal(f.client.getSnapshot().phase, 'unavailable');
});

for (const [name, prefix, suffix] of [
  ['valid UTF-8', [], []],
  ['UTF-8 BOM bytes', [239, 187, 191], []],
  ['malformed UTF-8', [], [195, 40]],
  ['oversized body', [], Array<number>(4097).fill(32)],
] as const) {
  test(`actual bounded status decode handles ${name} without publishing response contents`, async t => {
    const f = fixture(t);
    const bytes = new Uint8Array([...prefix, ...new TextEncoder().encode(JSON.stringify(statusBody())), ...suffix]);
    let cancelled = false;
    let offset = 0;
    f.routes.set('/api/access', () => new Response(new ReadableStream<Uint8Array>({
      pull(controller) {
        if (offset < bytes.length) controller.enqueue(bytes.slice(offset, ++offset));
        else controller.close();
      },
      cancel() { cancelled = true; },
    }), { headers: { 'Content-Type': 'application/json' } }));
    await f.client.check();
    assert.equal(f.client.getSnapshot().phase, name === 'valid UTF-8' ? 'anonymous' : 'unavailable');
    if (name === 'oversized body') assert.equal(cancelled, true);
    assert.equal(JSON.stringify(f.client.getSnapshot()).includes(bearer), false);
    assert.doesNotMatch(f.client.getSnapshot().message ?? '', /version|processStartTimeUtc|AAAA/);
  });
}

for (const [name, patch] of [
  ['foreign U', { browserOrigin: 'https://foreign.invalid' }],
  ['changed A', { apiOrigin: 'http://127.0.0.1:21844' }],
  ['changed instance', { instanceId: replacement }],
  ['bad display code', { userCode: 'OOOO-OOOO-OOOO' }],
  ['changed permissions', { requestedCapabilities: ['mutate'] }],
] as const) {
  test(`pairing rejects ${name} without exchanging`, async t => {
    const f = fixture(t);
    await f.client.check();
    f.routes.set('/api/access/pairings', () => json({ ...pairBody(), ...patch }, 201));
    await f.client.pair(); await f.tick(2000);
    assert.equal(f.client.getSnapshot().phase, 'unavailable');
    assert.equal(f.calls.some(c => c.path.endsWith('/exchange')), false);
  });
}

test('anonymous protects sensitive GETs and mutations but search-only chat is explicit and provider-free on the wire', async t => {
  const f = fixture(t);
  await f.client.check();
  const initial = f.calls.length;
  for (const path of ['/api/settings', '/api/preferences', '/api/sync/accounts']) await assert.rejects(f.client.request(path), ApiError);
  await assert.rejects(f.client.request('/api/sync', { method: 'POST' }), ApiError);
  await assert.rejects(f.client.request('/api/chat', { method: 'POST', body: '{"query":"x"}' }));
  await assert.rejects(f.client.request('/api/chat', { method: 'POST', body: '{"query":"x","aiEnabled":true}' }), ApiError);
  await assert.rejects(f.client.request('/api/search?q=x&mode=semantic'), ApiError);
  await assert.rejects(f.client.request('/api/search?q=x&mode=hybrid'), ApiError);
  assert.equal(f.calls.length, initial);
  await (await f.client.request('/api/chat', { method: 'POST', body: '{"query":"x","aiEnabled":false}' }, { searchOnly: true })).text();
  assert.equal(new Headers(f.calls.at(-1)!.init.headers).has('Authorization'), false);
  await (await f.client.request('/api/documents?q=x')).text();
});

test('a subset grant never implies all permissions; 403 preserves authority', async t => {
  const f = fixture(t);
  f.routes.set(`/api/access/pairings/${pairingId}/exchange`, () => json(exchangeBody(['mutate']), 201));
  await f.unlock();
  assert.equal(f.client.can('mutate'), true);
  assert.equal(f.client.can('config.read'), false);
  await assert.rejects(f.client.request('/api/settings'), ApiError);
  f.routes.set('/api/sync', () => error(403, 'insufficient_capability', ['model.invoke']));
  await assert.rejects(f.client.request('/api/sync', { method: 'POST' }), (err: unknown) =>
    err instanceof ApiError && err.status === 403 && err.requiredCapabilities[0] === 'model.invoke');
  assert.equal(f.client.getSnapshot().phase, 'authenticated');
});

for (const code of ['authentication_required', 'invalid_credential', 'credential_expired', 'wrong_instance']) {
  test(`401 ${code} clears authority and generation, without mutation replay`, async t => {
    const f = fixture(t); await f.unlock();
    const generation = f.client.getSnapshot().generation;
    f.routes.set('/api/sync', () => error(401, code));
    await assert.rejects(f.client.request('/api/sync', { method: 'POST' }));
    assert.equal(f.client.can('mutate'), false);
    assert.ok(f.client.getSnapshot().generation > generation);
    assert.equal(f.calls.filter(c => c.path === '/api/sync').length, 1);
  });
}

test('network and 429 preserve authority and expose retry data; no automatic replay', async t => {
  const f = fixture(t); await f.unlock();
  f.routes.set('/api/sync', () => new Response(JSON.stringify({ error: 'access_rate_limited', message: 'Wait', requiredCapabilities: [] }),
    { status: 429, headers: { 'Content-Type': 'application/json', 'Retry-After': '2' } }));
  await assert.rejects(f.client.request('/api/sync', { method: 'POST' }),
    (err: unknown) => err instanceof ApiError && err.retryAfter === '2');
  assert.equal(f.client.getSnapshot().phase, 'authenticated');
  f.routes.set('/api/sync', () => { throw new TypeError('Synthetic network failure'); });
  await assert.rejects(f.client.request('/api/sync', { method: 'POST' }));
  assert.equal(f.client.getSnapshot().phase, 'authenticated');
  assert.equal(f.calls.filter(c => c.path === '/api/sync').length, 2);
});

test('redirects and HTTP 200 error or unconfirmed actions are not successes', async t => {
  const f = fixture(t); await f.unlock();
  for (const response of [() => new Response(null, { status: 302 }), () => json({ error: 'read_only' }),
    () => json({ saved: false }), () => json({ status: 'ok' })]) {
    f.routes.set('/api/settings', response);
    await assert.rejects(f.client.request('/api/settings', { method: 'PUT', body: 'synthetic' }));
  }
});

test('same-authority focus checks preserve generation, authority, pending reads and mutations', async t => {
  const f = fixture(t); await f.unlock();
  const generation = f.client.getSnapshot().generation;
  let releaseRead!: (response: Response) => void;
  let releaseMutation!: (response: Response) => void;
  f.routes.set('/api/settings', init => new Promise<Response>(resolve => {
    if (init.method === 'PUT') releaseMutation = resolve;
    else releaseRead = resolve;
  }));
  const read = f.client.request('/api/settings');
  const mutation = f.client.request('/api/settings', { method: 'PUT', body: 'synthetic' });
  const pendingCalls = f.calls.slice(-2);
  await f.client.check();
  assert.equal(f.client.getSnapshot().phase, 'authenticated');
  assert.equal(f.client.getSnapshot().generation, generation);
  assert.equal(f.client.can('mutate'), true);
  assert.ok(pendingCalls.every(call => !call.init.signal?.aborted));
  assert.equal(new Headers(f.calls.at(-1)!.init.headers).get('Authorization'), `Bearer ${bearer}`);
  releaseRead(json({ sensitive: 'still current' }));
  releaseMutation(json({ saved: true }));
  assert.deepEqual(await (await read).json(), { sensitive: 'still current' });
  assert.deepEqual(await (await mutation).json(), { saved: true });
  assert.equal(f.calls.filter(call => call.path === '/api/settings' && call.init.method === 'PUT').length, 1);
});

test('local lock clears generation, authority and pending protected work before revocation settles', async t => {
  const f = fixture(t); await f.unlock();
  const generation = f.client.getSnapshot().generation;
  let revoke!: (response: Response) => void;
  f.routes.set('/api/access/session', () => new Promise<Response>(resolve => { revoke = resolve; }));
  let release!: (response: Response) => void;
  f.routes.set('/api/settings', () => new Promise<Response>(resolve => { release = resolve; }));
  const pending = f.client.request('/api/settings');
  const rejected = assert.rejects(pending, { name: 'AbortError' });
  const protectedCall = f.calls.at(-1)!;
  let cleaned = false;
  f.client.subscribe(() => {
    if (f.client.getSnapshot().generation !== generation) {
      cleaned = true;
      assert.equal(f.client.getSnapshot().phase, 'anonymous');
      assert.deepEqual(f.client.getSnapshot().capabilities, []);
    }
  });
  const locking = f.client.lock();
  assert.equal(cleaned, true);
  assert.equal(protectedCall.init.signal?.aborted, true);
  assert.equal(f.client.getSnapshot().phase, 'anonymous');
  assert.ok(f.client.getSnapshot().generation > generation);
  assert.equal(f.client.can('config.read'), false);
  await assert.rejects(f.client.request('/api/preferences'), ApiError);
  assert.equal(new Headers(f.calls.at(-1)!.init.headers).get('Authorization'), `Bearer ${bearer}`);
  release(json({ sensitive: 'obsolete' }));
  await rejected;
  revoke(error(401, 'invalid_credential'));
  await locking;
  assert.equal(f.client.getSnapshot().phase, 'anonymous');
  assert.match(f.client.getSnapshot().message!, /Locked locally.*not confirmed/);
  assert.equal(f.calls.filter(call => call.path === '/api/access/session').length, 1);
});

test('null-body responses discard intercepted streams without constructing an invalid response', async t => {
  const f = fixture(t); await f.unlock();
  for (const status of [204, 205]) {
    let cancelled = false;
    f.routes.set('/api/sync', () => {
      const response = new Response(new ReadableStream<Uint8Array>({
        cancel() { cancelled = true; },
      }));
      Object.defineProperty(response, 'status', { value: status });
      return response;
    });
    const response = await f.client.request('/api/sync', { method: 'POST' });
    assert.equal(response.status, status);
    assert.equal(response.body, null);
    assert.equal(cancelled, true);
    assert.equal(f.client.getSnapshot().phase, 'authenticated');
  }
  f.routes.set('/api/access/session', () => {
    const response = new Response(new ReadableStream<Uint8Array>());
    Object.defineProperty(response, 'status', { value: 204 });
    return response;
  });
  await f.client.lock();
  assert.equal(f.client.getSnapshot().phase, 'anonymous');
  assert.equal(f.client.getSnapshot().message, 'Locked.');
});

test('HEAD responses preserve headers without retaining a response stream', async t => {
  const f = fixture(t); await f.unlock();
  let cancelled = false;
  f.routes.set('/api/documents', () => new Response(new ReadableStream<Uint8Array>({
    cancel() { cancelled = true; },
  }), { headers: { 'Content-Length': '123' } }));
  const response = await f.client.request('/api/documents', { method: 'HEAD' });
  assert.equal(response.status, 200);
  assert.equal(response.body, null);
  assert.equal(response.headers.get('Content-Length'), '123');
  assert.equal(cancelled, true);
});

test('locking fences a HEAD response whose intercepted stream cancellation is still pending', async t => {
  const f = fixture(t); await f.unlock();
  let entered!: () => void;
  let release!: () => void;
  const cancelling = new Promise<void>(resolve => { entered = resolve; });
  const pending = new Promise<void>(resolve => { release = resolve; });
  f.routes.set('/api/documents', () => new Response(new ReadableStream<Uint8Array>({
    cancel() { entered(); return pending; },
  })));
  const request = f.client.request('/api/documents', { method: 'HEAD' });
  const rejected = assert.rejects(request, { name: 'AbortError' });
  await cancelling;
  await f.client.lock();
  release();
  await rejected;
  assert.equal(f.client.getSnapshot().phase, 'anonymous');
  assert.equal(f.client.getSnapshot().message, 'Locked.');
});

test('late focus and revoke replies cannot revive or overwrite a newer pairing', async t => {
  const f = fixture(t); await f.unlock();
  let status!: (response: Response) => void;
  let revoke!: (response: Response) => void;
  f.routes.set('/api/access', () => new Promise<Response>(resolve => { status = resolve; }));
  f.routes.set('/api/access/session', () => new Promise<Response>(resolve => { revoke = resolve; }));
  const checking = f.client.check();
  assert.equal(f.client.check(), checking);
  const locking = f.client.lock();
  await f.client.pair();
  const snapshot = f.client.getSnapshot();
  assert.equal(snapshot.phase, 'pairing');
  status(json(statusBody(true)));
  revoke(new Response(null, { status: 204 }));
  await Promise.all([checking, locking]);
  assert.equal(f.client.getSnapshot(), snapshot);
  assert.equal(f.client.can('mutate'), false);
});

test('focus permission loss fences old work while preserving only the actual remaining grant', async t => {
  const f = fixture(t); await f.unlock();
  const generation = f.client.getSnapshot().generation;
  f.routes.set('/api/access', () => json({ ...statusBody(true), capabilities: ['archive.read', 'mutate'] }));
  let release!: (response: Response) => void;
  f.routes.set('/api/preferences', () => new Promise<Response>(resolve => { release = resolve; }));
  const pending = assert.rejects(f.client.request('/api/preferences'), { name: 'AbortError' });
  await f.client.check();
  assert.ok(f.client.getSnapshot().generation > generation);
  assert.equal(f.client.can('config.read'), false);
  assert.equal(f.client.can('mutate'), true);
  release(json({ obsolete: true }));
  await pending;
});

test('focus status reports expiry immediately; transient outage does not erase a valid session', async t => {
  const f = fixture(t); await f.unlock();
  const generation = f.client.getSnapshot().generation;
  f.routes.set('/api/access', () => { throw new TypeError('Synthetic offline status'); });
  await f.client.check();
  assert.equal(f.client.getSnapshot().generation, generation);
  assert.equal(f.client.can('config.read'), true);
  assert.equal(f.client.getSnapshot().phase, 'authenticated');
  f.routes.set('/api/access', () => json({ ...statusBody(true), expiresAt: iso(0) }));
  await f.client.check();
  assert.equal(f.client.getSnapshot().phase, 'expired');
  assert.equal(f.client.can('mutate'), false);
  assert.ok(f.client.getSnapshot().generation > generation);
});

test('service replacement discards session and old native navigation', async t => {
  const f = fixture(t); await f.unlock();
  f.routes.set('/api/access', () => json({ ...statusBody(), instanceId: replacement }));
  await f.client.check();
  assert.equal(f.client.getSnapshot().phase, 'expired');
  assert.equal(f.client.can('mutate'), false);
  f.client.acceptNative(ready());
  assert.equal(f.native.length, 0);
});

test('wrong status audience and malformed authenticated status fence authority instead of treating it as an outage', async t => {
  const f = fixture(t); await f.unlock();
  const generation = f.client.getSnapshot().generation;
  f.routes.set('/api/access', () => json({ ...statusBody(true), origin: 'http://127.0.0.1:21844' }));
  await f.client.check();
  assert.equal(f.client.getSnapshot().phase, 'unavailable');
  assert.equal(f.client.can('mutate'), false);
  assert.ok(f.client.getSnapshot().generation > generation);
});

test('session expiry is absolute despite UTC rollback and polling', async t => {
  const f = fixture(t); await f.unlock();
  f.rollback();
  await f.client.check();
  await f.tick(1_800_000);
  assert.equal(f.client.getSnapshot().phase, 'expired');
  assert.equal(f.client.can('mutate'), false);
});

test('pairing polls once per two seconds, is bounded to 180 seconds and cancellation drops late create', async t => {
  const f = fixture(t); await f.client.check();
  f.routes.set(`/api/access/pairings/${pairingId}/exchange`, () =>
    json({ version: 1, instanceId, status: 'pending', expiresAt: iso(180_000) }, 202));
  await f.client.pair(); await f.client.pair();
  assert.equal(f.calls.filter(c => c.path === '/api/access/pairings').length, 1);
  await f.tick(2000); await f.tick(1999);
  assert.equal(f.calls.filter(c => c.path.endsWith('/exchange')).length, 1);
  f.rollback(); await f.tick(176_001);
  assert.equal(f.client.getSnapshot().phase, 'expired');
  const calls = f.calls.length; await f.tick(10_000);
  assert.equal(f.calls.length, calls);
  const entered = Promise.withResolvers<void>();
  const response = Promise.withResolvers<Response>();
  f.routes.set('/api/access/pairings', () => {
    entered.resolve();
    return response.promise;
  });
  const pending = f.client.pair();
  await entered.promise;
  f.client.cancel(); response.resolve(json(pairBody(), 201)); await pending;
  assert.equal(f.client.getSnapshot().pairing, undefined);
});

test('lost exchange is not retried; reconnect creates a new proof', async t => {
  const f = fixture(t); await f.client.check(); await f.client.pair();
  const challenge = JSON.parse(f.calls.at(-1)!.init.body as string).challenge;
  f.routes.set(`/api/access/pairings/${pairingId}/exchange`, () => { throw new TypeError('Lost reply'); });
  await f.tick(2000); await f.tick(10_000);
  assert.equal(f.client.getSnapshot().phase, 'unavailable');
  assert.equal(f.calls.filter(c => c.path.endsWith('/exchange')).length, 1);
  await f.client.pair();
  assert.notEqual(JSON.parse(f.calls.at(-1)!.init.body as string).challenge, challenge);
});

test('malformed exchange JSON never publishes its credential contents in an error', async t => {
  const f = fixture(t);
  await f.client.check(); await f.client.pair();
  f.routes.set(`/api/access/pairings/${pairingId}/exchange`, () => new Response(
    `{"accessToken":"${bearer}\n"}`, { status: 201, headers: { 'Content-Type': 'application/json' } }));
  await f.tick(2000);
  assert.equal(f.client.getSnapshot().phase, 'unavailable');
  assert.equal(JSON.stringify(f.client.getSnapshot()).includes(bearer), false);
  assert.equal(f.client.getSnapshot().message, 'The Service returned invalid access JSON.');
});

test('native golden wire echoes only current navigation and never crosses bearer or verifier', { timeout: 5000 }, async t => {
  const f = fixture(t); await f.client.check();
  f.client.acceptNative({ ...ready(), instanceId: replacement });
  f.client.acceptNative({ ...ready(), navigationId: 'not-canonical' });
  assert.equal(f.native.length, 0);
  const firstPairing = f.nextNative();
  f.client.acceptNative(ready()); await firstPairing;
  assert.equal(f.native.length, 1);
  const pairing = f.native[0] as Record<string, unknown>;
  assert.deepEqual(Object.keys(pairing).sort(), ['version', 'type', 'navigationId', 'instanceId', 'pairingId', 'userCode', 'challenge'].sort());
  assert.equal(pairing.type, 'hermes.access.pairing');
  assert.equal(pairing.navigationId, navigationId);
  const authenticated = f.nextNative();
  await f.tick(2000); await authenticated;
  assert.deepEqual(f.native[1], { version: 1, type: 'hermes.access.status', navigationId,
    instanceId, state: 'authenticated', capabilities: ['archive.read', ...requestedCapabilities], expiresAt: iso(1_800_000) });
  assert.equal(JSON.stringify(f.native).includes(bearer), false);
  const replacementPairing = f.nextNative();
  f.client.acceptNative(ready(nextNavigation)); await replacementPairing;
  assert.equal(f.client.getSnapshot().phase, 'pairing');
  f.client.acceptNative(ready());
  assert.equal((f.native.at(-1) as Record<string, unknown>).navigationId, nextNavigation);
});

test('early host ready waits for matching public identity; stale exchange cannot revive new navigation', { timeout: 5000 }, async t => {
  const f = fixture(t);
  const firstPairing = f.nextNative();
  f.client.acceptNative(ready());
  await f.client.check(); await firstPairing;
  let release!: (response: Response) => void;
  f.routes.set(`/api/access/pairings/${pairingId}/exchange`, () => new Promise(resolve => { release = resolve; }));
  await f.tick(2000);
  const replacementPairing = f.nextNative();
  f.client.acceptNative(ready(nextNavigation));
  release(json(exchangeBody(), 201));
  await replacementPairing;
  assert.equal(f.client.getSnapshot().phase, 'pairing');
  assert.equal(f.native.some(message => (message as Record<string, unknown>).type === 'hermes.access.status'), false);
});

test('dispose aborts pending work and leaves no pairing callbacks or subscribers', async t => {
  const f = fixture(t); await f.client.check(); await f.client.pair();
  let notices = 0; f.client.subscribe(() => { notices++; });
  f.client.dispose(); const count = notices; const calls = f.calls.length;
  await f.tick(200_000);
  await f.client.check(); await f.client.pair(); f.client.acceptNative(ready());
  assert.equal(notices, count);
  assert.equal(f.calls.length, calls);
});

test('locking cancels an authenticated SSE body and suppresses late answer frames', async t => {
  const f = fixture(t); await f.unlock();
  let cancelled = false;
  let writer!: ReadableStreamDefaultController<Uint8Array>;
  const body = new ReadableStream<Uint8Array>({
    start(controller) {
      writer = controller;
      controller.enqueue(new TextEncoder().encode('event: results\ndata: {"results":[]}\n\n'));
    },
    cancel() { cancelled = true; },
  });
  f.routes.set('/api/chat', () => new Response(body, { headers: { 'Content-Type': 'text/event-stream' } }));
  const response = await f.client.request('/api/chat', { method: 'POST', body: '{"query":"synthetic","aiEnabled":true}' });
  const events: string[] = [];
  const reading = readChat(response.body!, event => { events.push(event.type); });
  const rejected = assert.rejects(reading);
  await setImmediate();
  await f.client.lock();
  assert.equal(cancelled, true);
  assert.throws(() => writer.enqueue(new TextEncoder().encode('event: answer\ndata: {"answer":"late"}\n\n')));
  await rejected;
  assert.deepEqual(events, ['results']);
  assert.equal(response.body!.locked, false);
});
