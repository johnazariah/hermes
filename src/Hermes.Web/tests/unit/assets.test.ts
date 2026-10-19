import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, writeFile, readFile, rm } from 'node:fs/promises';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';
import process from 'node:process';

const script = fileURLToPath(new URL('../../scripts/assets.mjs', import.meta.url));
const html = '<!doctype html><div id="root"></div><script type="module" src="/assets/unit.js"></script>';

async function bundle(verify: (root: string) => Promise<void>): Promise<void> {
  const fixtures = fileURLToPath(new URL('../../test-results/unit-assets/', import.meta.url));
  await mkdir(fixtures, { recursive: true });
  const root = await mkdtemp(join(fixtures, 'hermes-assets-'));
  try {
    await mkdir(join(root, 'assets'));
    await writeFile(join(root, 'index.html'), html);
    await writeFile(join(root, 'assets/unit.js'), 'export const synthetic = true;');
    await verify(root);
  } finally { await rm(root, { recursive: true, force: true }); }
}

function run(action: string, root: string): void {
  execFileSync(process.execPath, [script, action, root], { stdio: 'pipe' });
}

test('manifest is reproducible and identifies every exact served byte', async () => bundle(async root => {
  run('create', root);
  const first = await readFile(join(root, 'asset-manifest.json'), 'utf8');
  run('create', root);
  assert.equal(await readFile(join(root, 'asset-manifest.json'), 'utf8'), first);
  const manifest = JSON.parse(first);
  assert.deepEqual(manifest.assets, ['assets/unit.js']);
  assert.deepEqual(manifest.entrypoints, ['/assets/unit.js']);
  assert.deepEqual(Object.keys(manifest.sha256), ['assets/unit.js', 'index.html']);
  assert.match(manifest.bundleSha256, /^[a-f0-9]{64}$/);
  run('verify', root);
}));

test('verification rejects changed module bytes', async () => bundle(async root => {
  run('create', root);
  await writeFile(join(root, 'assets/unit.js'), 'export const synthetic = false;');
  assert.throws(() => run('verify', root));
}));

test('verification rejects stale extra files and missing declared files', async () => bundle(async root => {
  run('create', root);
  await writeFile(join(root, 'assets/stale.js'), 'stale synthetic asset');
  assert.throws(() => run('verify', root));
  await rm(join(root, 'assets/stale.js'));
  await rm(join(root, 'assets/unit.js'));
  assert.throws(() => run('verify', root));
}));

test('creation refuses missing index or an undeclared module entry', async () => bundle(async root => {
  await rm(join(root, 'index.html'));
  assert.throws(() => run('create', root));
  await writeFile(join(root, 'index.html'), html.replace('/assets/unit.js', '/assets/missing.js'));
  assert.throws(() => run('create', root));
}));
