import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { renderToStaticMarkup } from 'react-dom/server';
import type { ReactElement } from 'react';
import { transpileModule, ModuleKind, JsxEmit, ScriptTarget } from 'typescript';
import { sourceTitle, type ChatSource } from '../../src/api/chat.ts';

const source = await readFile(new URL('../../src/components/chat/ChatPane.tsx', import.meta.url), 'utf8');
const compiled = transpileModule(source, {
  compilerOptions: { module: ModuleKind.CommonJS, jsx: JsxEmit.ReactJSX, target: ScriptTarget.ES2023 },
}).outputText;
const require = createRequire(import.meta.url);
const exports: {
  SourceCard?: (props: { source: ChatSource; onSelectDocument: (id: number) => void }) => ReactElement<{ onClick?: () => void }>;
} = {};
new Function('require', 'exports', compiled)((name: string) => {
  if (name === 'react' || name === 'react/jsx-runtime') return require(name);
  if (name === '../../api/chat') return { sourceTitle };
  if (name === '../../hooks/useAccess' || name === '../AccessPanel') return {};
  throw new Error(`Unexpected source-card dependency: ${name}`);
}, exports);

test('rendered metadata-only email has a nonempty subject and cannot open a colliding document ID', () => {
  const email: ChatSource = { documentId: 7, resultType: 'email', savedPath: '', originalName: '',
    subject: 'Synthetic metadata title', category: 'email', sender: 'synthetic@example.invalid',
    snippet: 'Synthetic source metadata', extractedAmount: null };
  const selected: number[] = [];
  const element = exports.SourceCard!({ source: email, onSelectDocument: id => selected.push(id) });
  const html = renderToStaticMarkup(element);
  assert.match(html, /Synthetic metadata title/);
  assert.match(html, /Email metadata only — no archived document/);
  assert.doesNotMatch(html, /<button|<a /);
  assert.equal(element.props.onClick, undefined);
  assert.deepEqual(selected, []);
});

test('rendered file-backed email keeps its actual name and document navigation identity', () => {
  const email: ChatSource = { documentId: 7, resultType: 'email', savedPath: 'synthetic/email.txt',
    originalName: 'actual-email.txt', subject: 'Synthetic subject', category: 'email',
    sender: null, snippet: null, extractedAmount: null };
  const selected: number[] = [];
  const element = exports.SourceCard!({ source: email, onSelectDocument: id => selected.push(id) });
  assert.match(renderToStaticMarkup(element), /<button.*actual-email.txt/);
  element.props.onClick!();
  assert.deepEqual(selected, [7]);
  assert.equal(email.resultType, 'email');
  assert.equal(email.savedPath, 'synthetic/email.txt');
});
