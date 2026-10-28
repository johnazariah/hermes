import { createServer } from 'node:http';
import { spawn } from 'node:child_process';
import { access, mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { once } from 'node:events';

const web = fileURLToPath(new URL('..', import.meta.url));
const port = Number(process.env.HERMES_TEST_PORT);
if (!Number.isInteger(port) || port < 1024 || port > 65534
  || [21741, 21742, 21740].includes(port)) throw new Error('Unsafe synthetic test port');

const fixtures = join(web, 'test-results', 'issue28-fixtures');
const metadata = join(web, 'test-results', 'issue28-harness.json');
await mkdir(fixtures, { recursive: true });
const root = await mkdtemp(join(fixtures, 'hermes-browser-'));
const config = join(root, 'config');
const archive = join(root, 'archive');
await Promise.all([mkdir(config), mkdir(archive)]);
await writeFile(join(root, '.synthetic-smoke'), 'Owned synthetic browser fixture\n');
let ready = false;
let child;
let closing = false;
let completedProducerCycles = 0;
let producerLogBuffer = '';
let providerCalls = 0;
let restartService;
let failService;
const failure = new Promise((_, reject) => { failService = reject; });
const expectedStops = new WeakSet();
const syncFixtures = new Map([
  ['settings', {
    originalName: 'synthetic-manual-sync-settings.txt',
    content: 'Owned synthetic settings Sync Now producer evidence. No personal data.\n',
  }],
  ['palette', {
    originalName: 'synthetic-manual-sync-palette.txt',
    content: 'Owned synthetic command palette producer evidence. No personal data.\n',
  }],
]);
const fake = createServer(async (request, response) => {
  if (['/api/generate', '/api/embed', '/api/embeddings'].includes(request.url) && request.method === 'POST') providerCalls++;
  if (request.url === '/ready') {
    response.writeHead(ready ? 200 : 503).end(ready ? 'synthetic fixture ready' : 'starting');
  } else if (request.url === '/api/generate' && request.method === 'POST') {
    for await (const _chunk of request) { /* Discard synthetic prompt bytes; never log documents. */ }
    response.writeHead(200, { 'Content-Type': 'application/json' }).end(JSON.stringify({
      response: 'Synthetic answer from the isolated smoke provider.',
      done: true,
    }));
  } else if (request.url === '/fixtures/provider' && request.method === 'GET') {
    response.writeHead(200, { 'Content-Type': 'application/json' }).end(JSON.stringify({ providerCalls }));
  } else if (request.url === '/fixtures/restart' && request.method === 'POST') {
    try {
      if (!ready || !restartService) throw new Error('Owned Service is not ready');
      await restartService();
      response.writeHead(201, { 'Content-Type': 'application/json' }).end(JSON.stringify({ restarted: true }));
    } catch {
      response.writeHead(500).end('Owned synthetic restart failed');
    }
  } else if (request.url === '/fixtures/sync' && request.method === 'GET') {
    response.writeHead(ready ? 200 : 503, { 'Content-Type': 'application/json' })
      .end(JSON.stringify({ completedProducerCycles }));
  } else if (request.method === 'POST'
    && (request.url === '/fixtures/sync/settings' || request.url === '/fixtures/sync/palette')) {
    try {
      if (!ready || completedProducerCycles === 0) throw new Error('Initial producer cycle is not complete');
      if (await readFile(join(root, '.synthetic-smoke'), 'utf8') !== 'Owned synthetic browser fixture\n') {
        throw new Error('Synthetic fixture ownership marker is invalid');
      }
      const fixture = syncFixtures.get(request.url.split('/').pop());
      if (!fixture) throw new Error('Unknown synthetic sync fixture');
      await writeFile(join(archive, 'unclassified', fixture.originalName), fixture.content, { flag: 'wx' });
      response.writeHead(201, { 'Content-Type': 'application/json' })
        .end(JSON.stringify({ ...fixture, completedProducerCycles }));
    } catch (error) {
      response.writeHead(500, { 'Content-Type': 'application/json' })
        .end(JSON.stringify({ error: error instanceof Error ? error.message : 'Synthetic fixture staging failed' }));
    }
  } else {
    response.writeHead(404).end('Unsupported synthetic provider operation');
  }
});

async function cleanup() {
  if (closing) return;
  closing = true;
  ready = false;
  if (child && child.exitCode === null && child.signalCode === null) {
    expectedStops.add(child);
    child.kill('SIGTERM');
    const timeout = setTimeout(() => child.kill('SIGKILL'), 10_000);
    try { await once(child, 'exit'); } finally { clearTimeout(timeout); }
  }
  if (fake.listening) {
    await new Promise((fulfil, reject) => fake.close(error => error ? reject(error) : fulfil()));
  }
  await rm(root, { recursive: true, force: true });
  await rm(metadata, { force: true });
}

process.once('SIGTERM', () => { void cleanup().then(() => process.exit(0), error => { console.error(error); process.exit(1); }); });
process.once('SIGINT', () => { void cleanup().then(() => process.exit(0), error => { console.error(error); process.exit(1); }); });

try {
  fake.listen(port + 1, '127.0.0.1');
  await once(fake, 'listening');
  const yaml = `archive_dir: ${JSON.stringify(archive)}\naccounts: []\nwatch_folders: []\n`
    + `preferences: Synthetic browser smoke preferences\nsync_interval_minutes: 60\n`
    + `ollama:\n  enabled: false\n  base_url: http://127.0.0.1:${port + 1}\n`;
  await writeFile(join(config, 'config.yaml'), yaml);
  const dll = process.env.HERMES_TEST_SERVICE_DLL
    ? resolve(process.env.HERMES_TEST_SERVICE_DLL)
    : resolve(web, '../Hermes.Service/bin/Release/net9.0/Hermes.Service.dll');
  await access(dll);
  const env = { ...process.env, HERMES_CONFIG_DIR: config, HERMES_PORT: String(port),
    HERMES_MCP_MUTATIONS: 'false', HERMES_ALLOWED_ORIGINS: '',
    DOTNET_USE_POLLING_FILE_WATCHER: '1', ASPNETCORE_CONTENTROOT: root };
  delete env.HERMES_MCP_TOKEN;
  const startService = () => {
    const service = spawn('dotnet', [dll], { cwd: root, env, stdio: ['ignore', 'pipe', 'pipe'] });
    service.stdout.on('data', chunk => {
      producerLogBuffer += chunk.toString('utf8');
      const lines = producerLogBuffer.split(/\r?\n/);
      producerLogBuffer = lines.pop() ?? '';
      completedProducerCycles += lines.filter(line => line.includes('Producer cycle completed')).length;
    });
    service.stdout.pipe(process.stdout);
    service.stderr.pipe(process.stderr);
    service.once('error', failService);
    service.once('exit', code => {
      if (!closing && !expectedStops.has(service)) failService(new Error(`Synthetic Service exited unexpectedly: ${code}`));
    });
    return service;
  };
  const waitForHealth = async () => {
    const deadline = Date.now() + 30_000;
    while (Date.now() < deadline) {
      try {
        const result = await fetch(`http://127.0.0.1:${port}/health`, { signal: AbortSignal.timeout(1000) });
        if (result.ok && (await result.json()).service === 'hermes') return;
      } catch (error) {
        if (!(error instanceof TypeError) && error.name !== 'TimeoutError') throw error;
      }
      await new Promise(fulfil => setTimeout(fulfil, 100));
    }
    throw new Error('Synthetic Service health deadline expired');
  };
  child = startService();
  await Promise.race([waitForHealth(), failure]);
  await writeFile(metadata, JSON.stringify({ version: 1, root, configDir: config, port, dll }));
  restartService = async () => {
    ready = false;
    if (await readFile(join(root, '.synthetic-smoke'), 'utf8') !== 'Owned synthetic browser fixture\n') throw new Error('Ownership marker changed');
    const old = child;
    expectedStops.add(old);
    old.kill('SIGTERM');
    const deadline = setTimeout(() => old.kill('SIGKILL'), 10_000);
    try { await once(old, 'exit'); } finally { clearTimeout(deadline); }
    producerLogBuffer = '';
    child = startService();
    await Promise.race([waitForHealth(), failure]);
    ready = true;
  };
  const python = process.env.PYTHON ?? (process.platform === 'win32' ? 'python' : 'python3');
  const seed = spawn(python, [resolve(web, 'tests/seed.py'), archive], { stdio: 'inherit' });
  const [code] = await once(seed, 'exit');
  if (code !== 0) throw new Error(`Synthetic seed failed: ${code}`);
  const initialCycle = (async () => {
    const deadline = Date.now() + 30_000;
    while (completedProducerCycles === 0 && Date.now() < deadline) {
      await new Promise(fulfil => setTimeout(fulfil, 100));
    }
    if (completedProducerCycles === 0) throw new Error('Initial synthetic producer cycle did not complete');
  })();
  await Promise.race([initialCycle, failure]);
  ready = true;
  console.log('HERMES_SYNTHETIC_BROWSER_READY');
  await failure;
} finally {
  await cleanup();
}
