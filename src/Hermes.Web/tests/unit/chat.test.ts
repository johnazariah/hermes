import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readChat, type ChatEvent } from '../../src/api/chat.ts';

const source = {
  documentId: 7, resultType: 'document', savedPath: 'synthetic/payslip.pdf',
  originalName: 'Synthetic payslip', subject: null, category: 'payslips',
  sender: null, snippet: null, extractedAmount: 42,
  emailDate: null, extractedVendor: 'Synthetic Employer', relevanceScore: -1,
};
const results = `event: results\ndata: ${JSON.stringify({ results: [source] })}\n\n`;
const done = 'event: done\ndata: {}\n\n';

function stream(chunks: Uint8Array[]): ReadableStream<Uint8Array> {
  return new ReadableStream({
    start(controller) {
      chunks.forEach(chunk => controller.enqueue(chunk));
      controller.close();
    },
  });
}

async function collect(text: string, bytewise = false): Promise<ChatEvent[]> {
  const bytes = new TextEncoder().encode(text);
  const events: ChatEvent[] = [];
  await readChat(stream(bytewise ? Array.from(bytes, byte => new Uint8Array([byte])) : [bytes]),
    event => events.push(event));
  return events;
}

test('every byte boundary preserves results, Unicode answer and done events', async () => {
  const events = await collect(`${results}event: answer\ndata: {"answer":"Synthetic café £42"}\n\n${done}`, true);
  assert.deepEqual(events, [
    { type: 'results', results: [source] },
    { type: 'answer', answer: 'Synthetic café £42' },
    { type: 'done' },
  ]);
});

test('keyword-only results do not require an answer', async () => {
  assert.equal((await collect(results + done)).length, 2);
});

test('document and metadata-only email identities survive a shared numeric ID', async () => {
  const email = { ...source, resultType: 'email', savedPath: '', originalName: null,
    subject: 'Synthetic message only', category: 'email', sender: 'synthetic@example.invalid',
    snippet: 'Synthetic email metadata', extractedAmount: null, extractedVendor: null };
  const text = `event: results\ndata: ${JSON.stringify({ results: [email, source] })}\n\n${done}`;
  assert.deepEqual(await collect(text, true), [
    { type: 'results', results: [email, source] }, { type: 'done' },
  ]);
});

test('a file-backed email retains both its email type and archived document path', async () => {
  const emailDocument = { ...source, resultType: 'email', savedPath: 'synthetic/email-body.txt' };
  const text = `event: results\ndata: ${JSON.stringify({ results: [emailDocument] })}\n\n${done}`;
  assert.deepEqual(await collect(text), [
    { type: 'results', results: [emailDocument] }, { type: 'done' },
  ]);
});

for (const invalid of [
  { ...source, resultType: undefined },
  { ...source, savedPath: undefined },
  { ...source, resultType: 'unknown' },
  { ...source, savedPath: null },
]) {
  test(`invalid source identity ${JSON.stringify(invalid)} is rejected`, async () => {
    await assert.rejects(collect(`event: results\ndata: ${JSON.stringify({ results: [invalid] })}\n\n${done}`));
  });
}

test('CRLF, comments, extension events and multiline data preserve the contract', async () => {
  const text = ': keepalive\r\n\r\nevent: future\r\ndata: ignored\r\n\r\n'
    + 'event: results\r\ndata: {"results":\r\ndata: []}\r\n\r\n'
    + done.replaceAll('\n', '\r\n');
  assert.deepEqual(await collect(text, true), [{ type: 'results', results: [] }, { type: 'done' }]);
});

for (const [name, text] of [
  ['truncated stream', results],
  ['missing results', done],
  ['malformed JSON', 'event: results\ndata: {\n\n' + done],
  ['invalid source', 'event: results\ndata: {"results":[{"documentId":7}]}\n\n' + done],
  ['invalid answer', results + 'event: answer\ndata: {"answer":4}\n\n' + done],
  ['missing event boundary', results + 'event: done\ndata: {}'],
] as const) {
  test(`${name} is an observable failure`, async () => {
    await assert.rejects(collect(text));
  });
}

test('a done frame cancels and releases a still-open stream', async () => {
  let cancelled = false;
  const body = new ReadableStream<Uint8Array>({
    start(controller) { controller.enqueue(new TextEncoder().encode(results + done)); },
    cancel() { cancelled = true; },
  });
  await readChat(body, () => {});
  assert.equal(cancelled, true);
  assert.equal(body.locked, false);
});

test('invalid UTF-8 and oversized incomplete frames are rejected', async () => {
  await assert.rejects(readChat(stream([new Uint8Array([255])]), () => {}));
  await assert.rejects(collect('x'.repeat(1_048_577)));
});
