import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { Children, createElement, isValidElement, type ReactElement, type ReactNode } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { transpileModule, ModuleKind, JsxEmit, ScriptTarget } from 'typescript';

// This retained panel is not mounted by SettingsPage. Exercise its render/actions
// in isolation without adding a production route or another DOM dependency.
const source = await readFile(new URL('../../src/components/settings/SyncConfigPanel.tsx', import.meta.url), 'utf8');
const compiled = transpileModule(source, {
  compilerOptions: { module: ModuleKind.CommonJS, jsx: JsxEmit.ReactJSX, target: ScriptTarget.ES2023 },
}).outputText;
const require = createRequire(import.meta.url);
type ButtonProps = { children?: ReactNode; onClick?: () => Promise<void>; disabled?: boolean };

function fixture() {
  const states: unknown[] = [];
  let cursor = 0;
  let generation = 1;
  let mutate = true;
  let readable = true;
  let resetCalls = 0;
  let syncCalls = 0;
  let invalidations = 0;
  let query: { data?: object[]; error?: Error; isPending: boolean } = {
    data: [{ account: 'synthetic@example.invalid', messageCount: 1, lastSyncAt: null }], isPending: false,
  };
  let reset: () => Promise<void> = async () => {};
  let sync: () => Promise<void> = async () => {};
  const imports: Record<string, unknown> = {
    react: { useState: (initial: unknown) => {
      const index = cursor++;
      if (index >= states.length) states[index] = initial;
      return [states[index], (value: unknown) => { states[index] = value; }];
    } },
    '@tanstack/react-query': {
      useQuery: () => query,
      useQueryClient: () => ({ invalidateQueries: () => { invalidations++; } }),
    },
    '../../api/http': {
      access: { isCurrent: (current: number) => current === generation },
      request: async () => { resetCalls++; await reset(); },
    },
    '../../api/hermes': { triggerSync: async () => { syncCalls++; await sync(); } },
    '../../hooks/useAccess': { useAccess: () => ({
      generation, can: (capability: string) => capability === 'config.read' ? readable : mutate,
    }) },
    '../AccessPanel': { UnlockReason: ({ capability = 'mutate' }: { capability?: string }) =>
      createElement('p', null, `Unlock Hermes with ${capability} permission`) },
  };
  const exports: { SyncConfigPanel?: () => ReactElement } = {};
  const load = (name: string) => {
    if (Object.hasOwn(imports, name)) return imports[name];
    if (name === 'react/jsx-runtime') return require(name);
    throw new Error(`Unexpected retained-panel dependency: ${name}`);
  };
  new Function('require', 'exports', compiled)(load, exports);
  const render = () => { cursor = 0; return exports.SyncConfigPanel!(); };
  const html = () => renderToStaticMarkup(render());
  const buttons = (node: ReactNode): ReactElement<ButtonProps>[] =>
    Children.toArray(node).flatMap(child => !isValidElement<ButtonProps>(child) ? []
      : child.type === 'button' ? [child] : buttons(child.props.children));
  const resetButton = () => buttons(render()).find(button => renderToStaticMarkup(button).includes('Reset'))!;
  return {
    html, resetButton, counts: () => ({ resetCalls, syncCalls, invalidations }),
    setReset: (next: typeof reset) => { reset = next; },
    setSync: (next: typeof sync) => { sync = next; },
    setQuery: (next: typeof query) => { query = next; },
    readOnly: () => { mutate = false; },
    lock: () => { generation++; readable = false; mutate = false; },
  };
}

test('retained sync panel renders actual-account success feedback and disables controls while pending', async () => {
  const f = fixture();
  let release!: () => void;
  f.setReset(() => new Promise(resolve => { release = resolve; }));
  const pending = f.resetButton().props.onClick!();
  assert.match(f.html(), /synthetic@example.invalid/);
  assert.match(f.html(), /\.\.\./);
  assert.match(f.html(), /disabled=""/);
  assert.equal(f.counts().syncCalls, 0);
  release(); await pending;
  assert.match(f.html(), /role="status">Reset saved; sync request accepted, not completed\./);
  assert.equal(f.resetButton().props.disabled, false);
  assert.deepEqual(f.counts(), { resetCalls: 1, syncCalls: 1, invalidations: 1 });
});

test('retained sync panel exposes failed/no-op resets without issuing a sync or showing success', async () => {
  const f = fixture();
  f.setReset(async () => { throw new Error('The Service did not confirm the action.'); });
  await f.resetButton().props.onClick!();
  assert.match(f.html(), /role="alert">The Service did not confirm the action\./);
  assert.doesNotMatch(f.html(), /Reset saved|No accounts configured/);
  assert.equal(f.resetButton().props.disabled, false);
  assert.deepEqual(f.counts(), { resetCalls: 1, syncCalls: 0, invalidations: 0 });
});

test('retained sync panel shows sync failure after a confirmed reset, not accepted/completed feedback', async () => {
  const f = fixture();
  f.setSync(async () => { throw new Error('Synthetic sync unavailable (HTTP 503)'); });
  await f.resetButton().props.onClick!();
  assert.match(f.html(), /role="alert">Synthetic sync unavailable \(HTTP 503\)/);
  assert.doesNotMatch(f.html(), /request accepted|completed/);
  assert.deepEqual(f.counts(), { resetCalls: 1, syncCalls: 1, invalidations: 1 });
});

test('retained sync panel distinguishes account errors/loading from empty accounts and exposes permission gating', () => {
  const f = fixture();
  f.readOnly();
  assert.equal(f.resetButton().props.disabled, true);
  assert.match(f.html(), /Unlock Hermes with mutate permission/);
  f.setQuery({ error: new Error('Synthetic configuration unavailable'), isPending: false });
  assert.match(f.html(), /role="alert">Unable to load sync accounts/);
  assert.doesNotMatch(f.html(), /No accounts configured/);
  f.setQuery({ isPending: true });
  assert.match(f.html(), /Loading sync accounts/);
  f.setQuery({ data: [], isPending: false });
  assert.match(f.html(), /No accounts configured/);
});

test('retained sync panel cannot chain a late reset result into a new sync after lock', async () => {
  const f = fixture();
  let release!: () => void;
  f.setReset(() => new Promise(resolve => { release = resolve; }));
  const pending = f.resetButton().props.onClick!();
  f.lock(); release(); await pending;
  assert.deepEqual(f.counts(), { resetCalls: 1, syncCalls: 0, invalidations: 0 });
  assert.match(f.html(), /Unlock Hermes with config.read permission/);
  assert.doesNotMatch(f.html(), /Reset saved/);
});
