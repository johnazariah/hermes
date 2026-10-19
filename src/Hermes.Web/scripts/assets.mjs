import { createHash } from 'node:crypto';
import { readdir, readFile, writeFile } from 'node:fs/promises';
import { resolve, relative, sep } from 'node:path';

const manifestName = 'asset-manifest.json';
const digest = bytes => createHash('sha256').update(bytes).digest('hex');

async function files(root, directory = root) {
  const entries = await readdir(directory, { withFileTypes: true });
  const nested = await Promise.all(entries.map(async entry => {
    const path = resolve(directory, entry.name);
    if (entry.isSymbolicLink()) throw new Error(`Bundle contains a symbolic link: ${entry.name}`);
    return entry.isDirectory() ? files(root, path) : [relative(root, path).split(sep).join('/')];
  }));
  return nested.flat().filter(name => name !== manifestName).sort();
}

async function describe(root) {
  const names = await files(root);
  if (!names.includes('index.html')) throw new Error('Bundle is missing index.html');
  const sha256 = Object.fromEntries(await Promise.all(names.map(async name =>
    [name, digest(await readFile(resolve(root, name)))],
  )));
  const html = await readFile(resolve(root, 'index.html'), 'utf8');
  const entrypoints = [...html.matchAll(/<script\b[^>]*\bsrc="([^"]+)"/g)]
    .map(match => match[1]);
  if (!entrypoints.length || entrypoints.some(path => !names.includes(path.replace(/^\//, '')))) {
    throw new Error('Index module entrypoints are missing from the bundle');
  }
  return {
    schemaVersion: 1,
    assets: names.filter(name => name !== 'index.html'),
    entrypoints,
    sha256,
    bundleSha256: digest(JSON.stringify(sha256)),
  };
}

const [action, directory] = process.argv.slice(2);
if (!directory || !['create', 'verify'].includes(action)) {
  throw new Error('Usage: node scripts/assets.mjs create|verify <bundle-directory>');
}
const root = resolve(directory);
const actual = await describe(root);
if (action === 'create') {
  await writeFile(resolve(root, manifestName), `${JSON.stringify(actual, null, 2)}\n`);
} else {
  const expected = JSON.parse(await readFile(resolve(root, manifestName), 'utf8'));
  if (JSON.stringify(actual) !== JSON.stringify(expected)) {
    throw new Error(`Bundle provenance mismatch: ${root}`);
  }
}
console.log(`Canonical React bundle: ${actual.bundleSha256} (${actual.assets.length + 1} files)`);
