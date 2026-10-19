import { readFile } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';

const before = await readFile(new URL('../dist/asset-manifest.json', import.meta.url), 'utf8');
if (!process.env.npm_execpath) throw new Error('Run this check through npm run test:reproducible');
const result = spawnSync(process.execPath, [process.env.npm_execpath, 'run', 'build'], { stdio: 'inherit' });
if (result.status !== 0) throw new Error('Repeated canonical React build failed');
const after = await readFile(new URL('../dist/asset-manifest.json', import.meta.url), 'utf8');
if (before !== after) throw new Error('Repeated builds produced different canonical bundle bytes');
console.log(`Repeated build is byte-identical: ${JSON.parse(after).bundleSha256}`);
