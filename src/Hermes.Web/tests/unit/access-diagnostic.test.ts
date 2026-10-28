import { test } from 'node:test';
import assert from 'node:assert/strict';
import { accessDiagnostic, type AccessDiagnostic } from '../access-diagnostic.ts';

const facts: AccessDiagnostic = {
  requests: 1, responses: 1, finished: 1, failed: 0, status: 200,
  requestMs: 12.1, headersMs: 55.2, bodyMs: 56.3,
};

test('access diagnostics export only the fixed numeric schema and round durations upwards', () => {
  const extra = { ...facts, url: 'private-url', headers: 'private-headers', body: 'private-body' };
  assert.equal(accessDiagnostic(extra), 'HERMES_ACCESS_BOOTSTRAP v=1 requests=1 responses=1 finished=1 failed=0 status=200 requestMs=13 headersMs=56 bodyMs=57');
});

test('access diagnostics distinguish absent observations from invalid samples', () => {
  assert.equal(accessDiagnostic({
    requests: 0, responses: 0, finished: 0, failed: 0, status: 0,
    requestMs: -1, headersMs: Number.NaN, bodyMs: Number.POSITIVE_INFINITY,
  }), 'HERMES_ACCESS_BOOTSTRAP v=1 requests=0 responses=0 finished=0 failed=0 status=0 requestMs=-1 headersMs=-2 bodyMs=-2');
  assert.match(accessDiagnostic({ ...facts, bodyMs: -3 }), / bodyMs=-2$/);
});

test('access diagnostics saturate fixed bounds without exporting arbitrary values', () => {
  const line = accessDiagnostic({
    requests: 1_000, responses: 1_000, finished: 1_000, failed: 1_000, status: 1_000,
    requestMs: 1_000_000, headersMs: 1_000_000, bodyMs: 1_000_000,
  });
  assert.equal(line, 'HERMES_ACCESS_BOOTSTRAP v=1 requests=999 responses=999 finished=999 failed=999 status=-2 requestMs=999999 headersMs=999999 bodyMs=999999');
  assert.ok(Buffer.byteLength(line, 'ascii') < 256);
  assert.match(line, /^HERMES_ACCESS_BOOTSTRAP v=1(?: [A-Za-z]+=-?\d+){8}$/);
});

test('access diagnostics do not turn invalid HTTP statuses into observed responses', () => {
  for (const status of [1, 99, 200.5, 600, Number.NaN]) {
    assert.match(accessDiagnostic({ ...facts, status }), / status=-2 /);
  }
  for (const status of [0, 100, 200, 599]) {
    assert.ok(accessDiagnostic({ ...facts, status }).includes(` status=${status} `));
  }
});
